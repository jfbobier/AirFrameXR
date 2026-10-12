// The simrig's viewer: a stand-in for the headset (see docs/DEVELOPMENT.md, "simrig"). Compiled only with VISIONALVR_SIMRIG (cargo feature `simrig`).
//
// It receives the shim's composed side-by-side frames (an uncompressed copy of a ring slot, taken where the encoder would read it), "decoded" at a time the
// host's simulated transport chooses, and displays them like the Vision Pro client does: a display tick every `period` (with jitter), a queue of decoded
// frames capped at `queue_max` (oldest dropped), at each tick the oldest queued frame is shown (waiting up to `poll_ms` for one to arrive), else the last
// one again. Each shown frame is corrected for the head rotation between the pose it was rendered with (the client's recorded sample for its timestamp) and
// the head pose at the display tick (the truth trajectory, plus a gaussian time error), by a rotation-only reprojection pass; `reproject = 0` shows the
// frames as they come. The result is rendered offscreen, shown in a window (optional), and the dots of the probe's "dots" scene on the equator row are
// measured every tick and written to dots.csv for harness/simrig/judge.py.
#ifdef VISIONALVR_SIMRIG
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <random>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

extern "C" {
void* nvh_sim_device(void* nvh);
void* nvh_sim_context(void* nvh);
void* nvh_sim_slot(void* nvh, void* ipc, uint32_t slot);
double nvh_qpc_seconds(void);
}

struct SvCfg {
    const char* out_dir;
    int show;               // 1: a visible window
    int reproject;          // 1: rotation correction, 0: frames as they come
    int out_eye_w, out_eye_h; // the offscreen eye image the judge measures
    double period_ns;       // the client's display period
    double tick_jitter_ns;  // sd of the display tick's own jitter
    double photon_err_ms;   // sd of the error of the time the client corrects to
    double dot_deg;         // the probe's dot spacing
    int poll_ms;            // how long a tick waits for a decoded frame (the visionOS client: 5)
    int queue_max;          // the decoded-frame queue (the visionOS client: 2)
    int dump_every;         // a BMP of the window every N ticks (0: none)
    int seconds;            // stop after
    uint64_t seed;
    double t0_qpc_s;        // QPC time of tick 0
    uint64_t ts_base_ns;    // the client timestamp of tick 0
    float fov8[8];          // per eye tangents l r u d
    float gamma;            // the stream's encoding gamma (frames hold pow(v, 1/gamma))
    double stamp_error;     // injection (self-test of the judge): fraction of frames whose pose lookup is replaced by a neighbouring sample
};

namespace {
struct Q4 { double x, y, z, w; };
Q4 qnorm(Q4 q) { double n = std::sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w); return n > 0 ? Q4{ q.x / n, q.y / n, q.z / n, q.w / n } : Q4{ 0, 0, 0, 1 }; }
void qmat(Q4 q, double m[9]) {   // row-major rotation matrix
    q = qnorm(q);
    const double x = q.x, y = q.y, z = q.z, w = q.w;
    m[0] = 1 - 2 * (y * y + z * z); m[1] = 2 * (x * y - z * w);     m[2] = 2 * (x * z + y * w);
    m[3] = 2 * (x * y + z * w);     m[4] = 1 - 2 * (x * x + z * z); m[5] = 2 * (y * z - x * w);
    m[6] = 2 * (x * z - y * w);     m[7] = 2 * (y * z + x * w);     m[8] = 1 - 2 * (x * x + y * y);
}
void mulTA(const double a[9], const double b[9], double o[9]) {   // o = a^T * b
    for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) { double s = 0; for (int k = 0; k < 3; k++) s += a[k * 3 + i] * b[k * 3 + j]; o[i * 3 + j] = s; }
}

const char* kShader = R"(
cbuffer CB : register(b0) {
    float4 rowA; float4 rowB; float4 rowC;      // M: display-view direction -> frame-view direction (rows)
    float4 tanOut;                              // l r u d of the displayed view
    float4 tanIn;                               // l r u d of the frame's view
    float4 uvRect;                              // x0 width of this eye in the source (u), unused y z
    float4 misc;                                // x: gamma, y: reproject, z: 1 = draw the offscreen image to the window
};
Texture2D src : register(t0);
SamplerState smp : register(s0);
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSOut VS(uint id : SV_VertexID) {
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = p;
    return o;
}
float4 PS(VSOut i) : SV_Target {
    if (misc.z > 0.5) return src.SampleLevel(smp, i.uv, 0);
    float tx = lerp(-tanOut.x, tanOut.y, i.uv.x);
    float ty = lerp(tanOut.z, -tanOut.w, i.uv.y);
    float3 d = float3(tx, ty, -1);
    if (misc.y > 0.5) d = float3(dot(rowA.xyz, d), dot(rowB.xyz, d), dot(rowC.xyz, d));
    if (d.z > -1e-4) return float4(0, 0, 0, 1);
    float2 t = d.xy / -d.z;
    float u = (t.x + tanIn.x) / (tanIn.x + tanIn.y);
    float v = 1 - (t.y + tanIn.w) / (tanIn.z + tanIn.w);
    if (u < 0 || u > 1 || v < 0 || v > 1) return float4(0, 0, 0, 1);
    float3 c = src.SampleLevel(smp, float2(uvRect.x + u * uvRect.y, v), 0).rgb;
    c = pow(max(c, 0), misc.x);
    return float4(c, 1);
}
)";

struct Frame { int ring; uint64_t ts; double ready; };

struct Viewer {
    SvCfg cfg{};
    std::string outDir;
    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr;
    ID3D11VertexShader* vs = nullptr; ID3D11PixelShader* ps = nullptr; ID3D11Buffer* cb = nullptr; ID3D11SamplerState* smp = nullptr;
    std::vector<ID3D11Texture2D*> ring; std::vector<ID3D11ShaderResourceView*> ringSrv;
    DXGI_FORMAT ringFmt = DXGI_FORMAT_UNKNOWN; UINT ringW = 0, ringH = 0;
    ID3D11Texture2D* rt = nullptr; ID3D11RenderTargetView* rtv = nullptr; ID3D11ShaderResourceView* rtSrv = nullptr;
    ID3D11Texture2D* stage = nullptr;
    IDXGISwapChain1* sc = nullptr; ID3D11RenderTargetView* scRtv = nullptr; HWND hwnd = nullptr; UINT winW = 0, winH = 0;
    ID3D11Texture2D* winStage = nullptr;
    std::mutex m;                       // posted frames, the sample table, the truth table
    std::vector<Frame> posted;
    std::unordered_map<uint64_t, Q4> samples;
    std::vector<uint64_t> sampleOrder;
    std::vector<Q4> truth; double truthT0 = 0, truthDt = 1e6;
    std::thread th; bool stop = false;
    FILE* dots = nullptr;
    int ticks = 0, shown = 0, repeats = 0, missingPose = 0;
    double yHorizon = 0.5;

    Q4 truthAt(double ts_ns) {
        std::lock_guard<std::mutex> l(m);
        if (truth.size() < 2) return Q4{ 0, 0, 0, 1 };
        double f = (ts_ns - truthT0) / truthDt;
        if (f < 0) f = 0;
        size_t i = (size_t)f;
        if (i + 1 >= truth.size()) return truth.back();
        double a = f - (double)i;
        Q4 p = truth[i], q = truth[i + 1];
        if (p.x * q.x + p.y * q.y + p.z * q.z + p.w * q.w < 0) { q = Q4{ -q.x, -q.y, -q.z, -q.w }; }
        return qnorm(Q4{ p.x * (1 - a) + q.x * a, p.y * (1 - a) + q.y * a, p.z * (1 - a) + q.z * a, p.w * (1 - a) + q.w * a });
    }

    bool init(void* nvh) {
        dev = (ID3D11Device*)nvh_sim_device(nvh); ctx = (ID3D11DeviceContext*)nvh_sim_context(nvh);
        if (!dev || !ctx) return false;
        ID3D11Multithread* mt = nullptr;
        if (SUCCEEDED(ctx->QueryInterface(__uuidof(ID3D11Multithread), (void**)&mt))) { mt->SetMultithreadProtected(TRUE); mt->Release(); }
        ID3DBlob *vb = nullptr, *pb = nullptr, *err = nullptr;
        if (FAILED(D3DCompile(kShader, strlen(kShader), "simviewer", nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vb, &err)) ||
            FAILED(D3DCompile(kShader, strlen(kShader), "simviewer", nullptr, nullptr, "PS", "ps_5_0", 0, 0, &pb, &err))) {
            if (err) fprintf(stderr, "simviewer shader: %s\n", (const char*)err->GetBufferPointer());
            return false;
        }
        dev->CreateVertexShader(vb->GetBufferPointer(), vb->GetBufferSize(), nullptr, &vs);
        dev->CreatePixelShader(pb->GetBufferPointer(), pb->GetBufferSize(), nullptr, &ps);
        vb->Release(); pb->Release();
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth = 16 * 7; bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        dev->CreateBuffer(&bd, nullptr, &cb);
        D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR; sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD = D3D11_FLOAT32_MAX;
        dev->CreateSamplerState(&sd, &smp);
        const int ow = cfg.out_eye_w, oh = cfg.out_eye_h;
        D3D11_TEXTURE2D_DESC td{}; td.Width = ow * 2; td.Height = oh; td.MipLevels = 1; td.ArraySize = 1; td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(dev->CreateTexture2D(&td, nullptr, &rt))) return false;
        dev->CreateRenderTargetView(rt, nullptr, &rtv);
        dev->CreateShaderResourceView(rt, nullptr, &rtSrv);
        D3D11_TEXTURE2D_DESC st = td; st.Width = ow; st.Height = 13; st.Usage = D3D11_USAGE_STAGING; st.BindFlags = 0; st.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(dev->CreateTexture2D(&st, nullptr, &stage))) return false;
        yHorizon = cfg.fov8[2] / (cfg.fov8[2] + cfg.fov8[3]);
        if (cfg.show) makeWindow();
        char path[600];
        snprintf(path, sizeof path, "%s\\dots.csv", cfg.out_dir);
        dots = fopen(path, "w");
        if (dots) fprintf(dots, "tick,t_s,shown_ts,reproj,yaw_deg,eye,n,x...\n");
        snprintf(path, sizeof path, "%s\\meta.json", cfg.out_dir);
        if (FILE* f = fopen(path, "w")) {
            const float* fv = cfg.fov8;
            fprintf(f, "{\"eye_w\": %d, \"eye_h\": %d, \"tl\": %.7f, \"tr\": %.7f, \"tu\": %.7f, \"td\": %.7f, \"tl_eye\": [%.7f, %.7f], \"ppt_eye\": [%.5f, %.5f], \"dot_deg\": %.4f, \"period_ns\": %.1f, "
                       "\"reproject\": %d, \"queue_max\": %d, \"poll_ms\": %d, \"photon_err_ms\": %.3f, \"tick_jitter_ns\": %.1f, \"stamp_error\": %.4f}\n",
                    ow, oh, fv[0], fv[1], fv[2], fv[3], fv[0], fv[4], ow / (fv[0] + fv[1]), ow / (fv[4] + fv[5]), cfg.dot_deg, cfg.period_ns, cfg.reproject, cfg.queue_max, cfg.poll_ms,
                    cfg.photon_err_ms, cfg.tick_jitter_ns, cfg.stamp_error);
            fclose(f);
        }
        return true;
    }

    static LRESULT CALLBACK wndProc(HWND h, UINT msg, WPARAM w, LPARAM l) { return DefWindowProcW(h, msg, w, l); }

    void makeWindow() {
        WNDCLASSW wc{}; wc.lpfnWndProc = wndProc; wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"visionalvr_simviewer"; wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        RegisterClassW(&wc);
        winW = 1920; winH = (UINT)(1920.0 * cfg.out_eye_h / (2.0 * cfg.out_eye_w));
        RECT r{ 0, 0, (LONG)winW, (LONG)winH }; AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW, FALSE);
        hwnd = CreateWindowW(wc.lpszClassName, L"VisionALVR simrig viewer (SBS, what the headset would show)", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, r.right - r.left, r.bottom - r.top, nullptr, nullptr, wc.hInstance, nullptr);
        IDXGIDevice* dd = nullptr; IDXGIAdapter* ad = nullptr; IDXGIFactory2* fac = nullptr;
        dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dd); dd->GetAdapter(&ad); ad->GetParent(__uuidof(IDXGIFactory2), (void**)&fac);
        DXGI_SWAP_CHAIN_DESC1 sdsc{}; sdsc.Width = winW; sdsc.Height = winH; sdsc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; sdsc.SampleDesc.Count = 1; sdsc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sdsc.BufferCount = 2; sdsc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        if (SUCCEEDED(fac->CreateSwapChainForHwnd(dev, hwnd, &sdsc, nullptr, nullptr, &sc))) {
            ID3D11Texture2D* bb = nullptr; sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb);
            dev->CreateRenderTargetView(bb, nullptr, &scRtv); bb->Release();
        }
        fac->Release(); ad->Release(); dd->Release();
    }

    void ensureRing(UINT w, UINT h, DXGI_FORMAT f) {
        if (!ring.empty() && ringW == w && ringH == h && ringFmt == f) return;
        for (auto* t : ring) t->Release();
        for (auto* s : ringSrv) s->Release();
        ring.clear(); ringSrv.clear();
        ringW = w; ringH = h; ringFmt = f;
        D3D11_TEXTURE2D_DESC td{}; td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1; td.Format = f; td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        for (int i = 0; i < 12; i++) {
            ID3D11Texture2D* t = nullptr; ID3D11ShaderResourceView* s = nullptr;
            dev->CreateTexture2D(&td, nullptr, &t);
            if (t) dev->CreateShaderResourceView(t, nullptr, &s);
            ring.push_back(t); ringSrv.push_back(s);
        }
    }

    // draws one eye of `frame` (or black) for the pose pair into the offscreen image
    void drawEye(int eye, int ringIdx, const double M[9], bool reproj) {
        const float* f = cfg.fov8 + eye * 4;
        float cbv[28] = {};
        cbv[0] = (float)M[0]; cbv[1] = (float)M[1]; cbv[2] = (float)M[2];
        cbv[4] = (float)M[3]; cbv[5] = (float)M[4]; cbv[6] = (float)M[5];
        cbv[8] = (float)M[6]; cbv[9] = (float)M[7]; cbv[10] = (float)M[8];
        cbv[12] = f[0]; cbv[13] = f[1]; cbv[14] = f[2]; cbv[15] = f[3];
        cbv[16] = f[0]; cbv[17] = f[1]; cbv[18] = f[2]; cbv[19] = f[3];
        cbv[20] = eye ? 0.5f : 0.f; cbv[21] = 0.5f;
        cbv[24] = cfg.gamma > 0 ? cfg.gamma : 1.f; cbv[25] = reproj ? 1.f : 0.f; cbv[26] = 0.f;
        ctx->UpdateSubresource(cb, 0, nullptr, cbv, 0, 0);
        D3D11_VIEWPORT vp{ (float)(eye * cfg.out_eye_w), 0, (float)cfg.out_eye_w, (float)cfg.out_eye_h, 0, 1 };
        ctx->RSSetViewports(1, &vp);
        ID3D11ShaderResourceView* srv = ringIdx >= 0 ? ringSrv[ringIdx] : nullptr;
        ctx->PSSetShaderResources(0, 1, &srv);
        ctx->Draw(3, 0);
    }

    // the dots of one eye: centres (pixels, in the eye image) of the white runs on the equator row
    void measure(int eye, std::vector<double>& xs) {
        const int ow = cfg.out_eye_w, oh = cfg.out_eye_h;
        int y0 = std::max(0, (int)(yHorizon * oh) - 6);
        D3D11_BOX box{ (UINT)(eye * ow), (UINT)y0, 0, (UINT)(eye * ow + ow), (UINT)std::min(oh, y0 + 13), 1 };
        ctx->CopySubresourceRegion(stage, 0, 0, 0, 0, rt, 0, &box);
        D3D11_MAPPED_SUBRESOURCE mp{};
        if (FAILED(ctx->Map(stage, 0, D3D11_MAP_READ, 0, &mp))) return;
        std::vector<float> col(ow, 0.f);
        const int rows = (int)(box.bottom - box.top);
        for (int r = 0; r < rows; r++) {
            const uint8_t* p = (const uint8_t*)mp.pData + (size_t)r * mp.RowPitch;
            for (int x = 0; x < ow; x++) col[x] = std::max(col[x], p[x * 4] / 255.f);
        }
        ctx->Unmap(stage, 0);
        int x = 0;
        while (x < ow) {
            if (col[x] > 0.5f) {
                int s = x; double sw = 0, sx = 0;
                while (x < ow && col[x] > 0.5f) { sw += col[x]; sx += col[x] * (x + 0.5); x++; }
                if (x - s >= 2) xs.push_back(sx / sw);
            } else x++;
        }
    }

    void dumpBmp(int tick) {
        if (!sc || !scRtv) return;
        ID3D11Texture2D* bb = nullptr; sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb);
        if (!bb) return;
        if (!winStage) {
            D3D11_TEXTURE2D_DESC td{}; bb->GetDesc(&td); td.Usage = D3D11_USAGE_STAGING; td.BindFlags = 0; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ; td.MiscFlags = 0;
            dev->CreateTexture2D(&td, nullptr, &winStage);
        }
        ctx->CopyResource(winStage, bb); bb->Release();
        D3D11_MAPPED_SUBRESOURCE mp{};
        if (FAILED(ctx->Map(winStage, 0, D3D11_MAP_READ, 0, &mp))) return;
        char path[640]; snprintf(path, sizeof path, "%s\\view_%05d.bmp", cfg.out_dir, tick);
        if (FILE* f = fopen(path, "wb")) {
            const uint32_t sz = winW * winH * 3, hdr = 54;
            uint8_t h[54] = { 'B', 'M' }; *(uint32_t*)(h + 2) = hdr + sz; *(uint32_t*)(h + 10) = hdr; *(uint32_t*)(h + 14) = 40; *(int32_t*)(h + 18) = (int32_t)winW; *(int32_t*)(h + 22) = -(int32_t)winH;
            *(uint16_t*)(h + 26) = 1; *(uint16_t*)(h + 28) = 24; *(uint32_t*)(h + 34) = sz;
            fwrite(h, 1, 54, f);
            std::vector<uint8_t> line(winW * 3);
            for (UINT y = 0; y < winH; y++) {
                const uint8_t* p = (const uint8_t*)mp.pData + (size_t)y * mp.RowPitch;
                for (UINT x = 0; x < winW; x++) { line[x * 3] = p[x * 4 + 2]; line[x * 3 + 1] = p[x * 4 + 1]; line[x * 3 + 2] = p[x * 4]; }
                fwrite(line.data(), 1, line.size(), f);
            }
            fclose(f);
        }
        ctx->Unmap(winStage, 0);
    }

    void loop() {
        LARGE_INTEGER fq; QueryPerformanceFrequency(&fq);
        std::mt19937_64 rng(cfg.seed ? cfg.seed : 1);
        std::normal_distribution<double> nj(0.0, 1.0);
        std::deque<Frame> queue;
        Frame last{ -1, 0, 0 };
        uint64_t lastShownTs = 0;
        const double T0 = cfg.t0_qpc_s;
        for (int k = 0;; k++) {
            const double tk = T0 + k * cfg.period_ns * 1e-9 + nj(rng) * cfg.tick_jitter_ns * 1e-9;
            if (stop || tk - T0 > cfg.seconds) break;
            while (nvh_qpc_seconds() < tk) { if (tk - nvh_qpc_seconds() > 0.0015) Sleep(1); else YieldProcessor(); }
            if (hwnd) { MSG msg; while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); } }
            auto ingest = [&](double now) {
                std::lock_guard<std::mutex> l(m);
                for (size_t i = 0; i < posted.size();) {
                    if (posted[i].ready <= now) { queue.push_back(posted[i]); posted.erase(posted.begin() + i); while ((int)queue.size() > cfg.queue_max) queue.pop_front(); }
                    else i++;
                }
            };
            ingest(nvh_qpc_seconds());
            const double pollEnd = tk + cfg.poll_ms * 1e-3;
            while (queue.empty() && nvh_qpc_seconds() < pollEnd) { Sleep(0); ingest(nvh_qpc_seconds()); }
            Frame cur = last;
            bool fresh = false;
            if (!queue.empty()) { cur = queue.front(); queue.pop_front(); fresh = true; }
            ticks++;
            if (fresh) { shown++; last = cur; } else repeats++;
            // poses: the head at display (the truth, with the client's time error) and the one the shown frame was rendered with
            const double tsDisp = (double)cfg.ts_base_ns + k * cfg.period_ns + nj(rng) * cfg.photon_err_ms * 1e6;
            const Q4 qd = truthAt(tsDisp);
            Q4 qf = qd;
            bool haveF = false;
            if (cur.ring >= 0) {
                std::lock_guard<std::mutex> l(m);
                uint64_t key = cur.ts;
                if (cfg.stamp_error > 0 && std::uniform_real_distribution<double>(0, 1)(rng) < cfg.stamp_error && !sampleOrder.empty()) {
                    // injection: the pose looked up for this frame is a neighbouring sample's (a stamp error of one to three samples)
                    for (size_t i = 0; i < sampleOrder.size(); i++) if (sampleOrder[i] == cur.ts) { size_t j = std::min(sampleOrder.size() - 1, i + 1 + (size_t)(std::uniform_int_distribution<int>(0, 2)(rng))); key = sampleOrder[j]; break; }
                }
                auto it = samples.find(key);
                if (it != samples.end()) { qf = it->second; haveF = true; } else missingPose++;
            }
            double Rf[9], Rd[9], M[9];
            qmat(qf, Rf); qmat(qd, Rd); mulTA(Rf, Rd, M);
            ctx->OMSetRenderTargets(1, &rtv, nullptr);
            ctx->VSSetShader(vs, nullptr, 0); ctx->PSSetShader(ps, nullptr, 0); ctx->PSSetSamplers(0, 1, &smp); ctx->VSSetConstantBuffers(0, 1, &cb); ctx->PSSetConstantBuffers(0, 1, &cb);
            ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST); ctx->IASetInputLayout(nullptr);
            const float black[4] = { 0, 0, 0, 1 }; ctx->ClearRenderTargetView(rtv, black);
            for (int eye = 0; eye < 2; eye++) drawEye(eye, cur.ring, M, cfg.reproject != 0 && haveF);
            // yaw of the displayed pose (the judge's truth): about +Y, from the rotation matrix
            const double yawDisp = std::atan2(-Rd[6], Rd[8]) * 180.0 / 3.14159265358979;
            if (dots) {
                for (int eye = 0; eye < 2; eye++) {
                    std::vector<double> xs; measure(eye, xs);
                    fprintf(dots, "%d,%.6f,%llu,%d,%.6f,%d,%d", k, tk, (unsigned long long)cur.ts, (cfg.reproject != 0 && haveF) ? 1 : 0, yawDisp, eye, (int)xs.size());
                    for (double x : xs) fprintf(dots, ",%.3f", x);
                    fprintf(dots, "\n");
                }
            }
            if (sc && scRtv) {
                ID3D11RenderTargetView* r = scRtv; ctx->OMSetRenderTargets(1, &r, nullptr);
                D3D11_VIEWPORT vp{ 0, 0, (float)winW, (float)winH, 0, 1 }; ctx->RSSetViewports(1, &vp);
                float cbv[28] = {}; cbv[26] = 1.f; ctx->UpdateSubresource(cb, 0, nullptr, cbv, 0, 0);
                ctx->PSSetShaderResources(0, 1, &rtSrv); ctx->Draw(3, 0);
                ID3D11ShaderResourceView* none = nullptr; ctx->PSSetShaderResources(0, 1, &none);
                sc->Present(0, 0);
                if (cfg.dump_every > 0 && k % cfg.dump_every == 0) dumpBmp(k);
            }
            ID3D11ShaderResourceView* none = nullptr; ctx->PSSetShaderResources(0, 1, &none);
            if (cur.ts) lastShownTs = cur.ts;
        }
        if (dots) { fflush(dots); fclose(dots); dots = nullptr; }
        char path[600]; snprintf(path, sizeof path, "%s\\viewer_summary.json", cfg.out_dir);
        if (FILE* f = fopen(path, "w")) { fprintf(f, "{\"ticks\": %d, \"fresh\": %d, \"repeats\": %d, \"missing_pose\": %d}\n", ticks, shown, repeats, missingPose); fclose(f); }
    }
};
} // namespace

extern "C" {
void* sv_create(void* nvh, const SvCfg* cfg) {
    auto* v = new Viewer();
    v->cfg = *cfg;
    v->outDir = cfg->out_dir;
    v->cfg.out_dir = v->outDir.c_str();
    if (!v->init(nvh)) { delete v; return nullptr; }
    v->th = std::thread([v]() { v->loop(); });
    return v;
}

// copies the shim's slot (an uncompressed frame) into the viewer's ring; returns the ring index
int sv_capture(void* h, void* nvh, void* ipc, uint32_t slot, uint32_t counter) {
    auto* v = (Viewer*)h;
    ID3D11Texture2D* src = (ID3D11Texture2D*)nvh_sim_slot(nvh, ipc, slot);
    if (!v || !src) return -1;
    D3D11_TEXTURE2D_DESC td{}; src->GetDesc(&td);
    {
        std::lock_guard<std::mutex> l(v->m);
        v->ensureRing(td.Width, td.Height, td.Format);
    }
    const int idx = (int)(counter % v->ring.size());
    v->ctx->CopyResource(v->ring[idx], src);
    return idx;
}

void sv_post(void* h, int ring, uint64_t ts_ns, double ready_qpc_s) {
    auto* v = (Viewer*)h;
    if (!v || ring < 0) return;
    std::lock_guard<std::mutex> l(v->m);
    v->posted.push_back(Frame{ ring, ts_ns, ready_qpc_s });
}

void sv_sample(void* h, uint64_t ts_ns, const float* q) {
    auto* v = (Viewer*)h;
    if (!v) return;
    std::lock_guard<std::mutex> l(v->m);
    v->samples[ts_ns] = Q4{ q[0], q[1], q[2], q[3] };
    v->sampleOrder.push_back(ts_ns);
}

void sv_truth(void* h, double t0_ns, double dt_ns, const float* q4, int n) {
    auto* v = (Viewer*)h;
    if (!v) return;
    std::lock_guard<std::mutex> l(v->m);
    v->truthT0 = t0_ns; v->truthDt = dt_ns;
    v->truth.resize(n);
    for (int i = 0; i < n; i++) v->truth[i] = Q4{ q4[i * 4], q4[i * 4 + 1], q4[i * 4 + 2], q4[i * 4 + 3] };
}

int sv_running(void* h) { auto* v = (Viewer*)h; return v && !v->stop && v->th.joinable() ? 1 : 0; }

void sv_stop(void* h) {
    auto* v = (Viewer*)h;
    if (!v) return;
    v->stop = true;
    if (v->th.joinable()) v->th.join();
    delete v;
}
}
#endif // VISIONALVR_SIMRIG
