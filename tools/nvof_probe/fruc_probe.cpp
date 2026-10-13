// fruc_probe: standalone measurement of NVIDIA's NvOFFRUC frame interpolation (Optical Flow SDK 5.x) for the frame-generation design.
//
// Synthetic content with an analytic ground truth: a smooth textured background panning sideways (a head turn), a textured disc and a
// bar moving across it (in-scene motion with occlusion). Frames k = 0..N-1 at integer times go into NvOFFRUCProcess; the library returns
// the frame at k - 0.5, which is compared with the truth rendered at k - 0.5. Baselines at the same instants: repeating frame k - 1 (what
// the headset does today) and a 50/50 blend of k - 1 and k. Reports the Process time (CPU call to GPU fence completion) per resolution.
//
//   fruc_probe.exe <dll dir> <out dir> [frames=40] [WxH ...]      default sizes: 1760x1600 4224x1664 7104x3200
//
// One JSON line per size on stdout: {"w","h","frames","process_ms":{"p50","p95","max"},"psnr_db":{"fruc","repeat","blend"},
// "repetitions" (times the library gave up and returned the previous frame), "create_ms"}. BMPs of prev / interpolated / truth / next for
// the middle frame of each size go to <out dir>.
#define NOMINMAX
#include <Windows.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <thread>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>
#include "NvOFFRUC.h"

using Microsoft::WRL::ComPtr;

static double g_time0 = 0;
static double NowMs() {
    static LARGE_INTEGER f = [] { LARGE_INTEGER q; QueryPerformanceFrequency(&q); return q; }();
    LARGE_INTEGER c; QueryPerformanceCounter(&c);
    return c.QuadPart * 1000.0 / f.QuadPart;
}

// ---- synthetic scene (continuous time t in frames) --------------------------------------------------------------------------------
struct Scene {
    int w, h;
    double pan = 24.0;       // background pixels per frame (a brisk head turn at the display rate)
    double discV = 14.0;     // disc pixels per frame
    double barV = 8.0;
    double R;                // disc radius
    Scene(int W, int H) : w(W), h(H) { R = std::min(W, H) * 0.09; }
    static inline float clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
    // smooth value noise (random gradient-free lattice, bilinear, two octaves): non-periodic like game content, continuous in (u, v) so
    // the truth exists at fractional positions
    static inline float hash(int x, int y) {
        uint32_t h = (uint32_t)x * 374761393u + (uint32_t)y * 668265263u; h = (h ^ (h >> 13)) * 1274126177u; return (float)(h ^ (h >> 16)) / 4294967296.0f;
    }
    static inline float noise(double u, double v) {
        const int x0 = (int)std::floor(u), y0 = (int)std::floor(v);
        const float fx = (float)(u - x0), fy = (float)(v - y0);
        const float sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        const float a = hash(x0, y0), b = hash(x0 + 1, y0), c = hash(x0, y0 + 1), d = hash(x0 + 1, y0 + 1);
        return (a + (b - a) * sx) + ((c + (d - c) * sx) - (a + (b - a) * sx)) * sy;
    }
    inline void bg(double u, double v, float& r, float& g, float& b) const {
        const float n1 = noise(u / 90.0, v / 90.0), n2 = noise(u / 23.0 + 7.3, v / 23.0 + 2.1), n3 = noise(u / 7.0 + 3.0, v / 7.0 + 9.0);
        const float l = 0.25f + 0.5f * n1 + 0.2f * n2 + 0.08f * n3;
        r = clamp01(l + 0.1f * n2); g = clamp01(l * 0.95f); b = clamp01(l * 0.85f + 0.1f * n1);
    }
    inline void disc(double lx, double ly, float& r, float& g, float& b) const {
        float t = 0.55f + 0.3f * (float)(std::sin(lx * 0.25) * std::cos(ly * 0.25));
        r = clamp01(t + 0.3f); g = clamp01(t * 0.5f); b = clamp01(0.2f + 0.2f * (float)std::sin(lx * 0.09));
    }
    void render(double t, std::vector<uint8_t>& out) const {
        out.resize((size_t)w * h * 4);
        const double cx = w * 0.3 + discV * t, cy = h * 0.5 + 0.3 * R * std::sin(t * 0.2);
        const double bx = w * 0.7 + 0.5 * barV * t, by0 = h * 0.15 + barV * t;
        const double bw = R * 0.35, bh = R * 2.0;
#pragma omp parallel for
        for (int y = 0; y < h; y++) {
            uint8_t* row = out.data() + (size_t)y * w * 4;
            for (int x = 0; x < w; x++) {
                float r, g, b;
                bg(x + pan * t, y, r, g, b);
                const double dx = x - cx, dy = y - cy, d2 = dx * dx + dy * dy;
                if (d2 < R * R) {
                    if (d2 > (R - 3) * (R - 3)) { r = g = b = 0.05f; } else disc(dx, dy, r, g, b);
                }
                if (x >= bx && x < bx + bw && y >= by0 && y < by0 + bh) {
                    const float s = 0.3f + 0.5f * (float)std::sin((y - by0) * 0.2);
                    r = 0.1f; g = clamp01(s); b = clamp01(1.0f - s);
                }
                row[x * 4 + 0] = (uint8_t)(r * 255.0f + 0.5f);
                row[x * 4 + 1] = (uint8_t)(g * 255.0f + 0.5f);
                row[x * 4 + 2] = (uint8_t)(b * 255.0f + 0.5f);
                row[x * 4 + 3] = 255;
            }
        }
    }
};

static double Psnr(const uint8_t* a, const uint8_t* b, size_t px) {
    double se = 0;
    for (size_t i = 0; i < px; i++) {
        for (int c = 0; c < 3; c++) { const double d = (double)a[i * 4 + c] - b[i * 4 + c]; se += d * d; }
    }
    const double mse = se / (px * 3.0);
    return mse <= 0 ? 99.0 : 10.0 * std::log10(255.0 * 255.0 / mse);
}

static void WriteBmp(const std::string& path, const uint8_t* rgba, int w, int h) {
    FILE* f = nullptr;
    if (fopen_s(&f, path.c_str(), "wb") || !f) return;
    const int stride = (w * 3 + 3) & ~3;
    const uint32_t size = 54 + stride * h;
    uint8_t hdr[54] = { 'B', 'M' };
    auto put32 = [&](int o, uint32_t v) { memcpy(hdr + o, &v, 4); };
    auto put16 = [&](int o, uint16_t v) { memcpy(hdr + o, &v, 2); };
    put32(2, size); put32(10, 54); put32(14, 40); put32(18, (uint32_t)w); put32(22, (uint32_t)h); put16(26, 1); put16(28, 24); put32(34, stride * h);
    fwrite(hdr, 1, 54, f);
    std::vector<uint8_t> row(stride);
    for (int y = h - 1; y >= 0; y--) {
        for (int x = 0; x < w; x++) { const uint8_t* p = rgba + ((size_t)y * w + x) * 4; row[x * 3] = p[2]; row[x * 3 + 1] = p[1]; row[x * 3 + 2] = p[0]; }
        fwrite(row.data(), 1, stride, f);
    }
    fclose(f);
}

struct Fruc {
    HMODULE dll = nullptr;
    PtrToFuncNvOFFRUCCreate create = nullptr;
    PtrToFuncNvOFFRUCRegisterResource reg = nullptr;
    PtrToFuncNvOFFRUCUnregisterResource unreg = nullptr;
    PtrToFuncNvOFFRUCProcess process = nullptr;
    PtrToFuncNvOFFRUCDestroy destroy = nullptr;
    bool load(const std::wstring& dir) {
        SetDllDirectoryW(dir.c_str());
        dll = LoadLibraryW((dir + L"\\NvOFFRUC.dll").c_str());
        if (!dll) { fprintf(stderr, "LoadLibrary NvOFFRUC.dll failed (%lu)\n", GetLastError()); return false; }
        create = (PtrToFuncNvOFFRUCCreate)GetProcAddress(dll, CreateProcName);
        reg = (PtrToFuncNvOFFRUCRegisterResource)GetProcAddress(dll, RegisterResourceProcName);
        unreg = (PtrToFuncNvOFFRUCUnregisterResource)GetProcAddress(dll, UnregisterResourceProcName);
        process = (PtrToFuncNvOFFRUCProcess)GetProcAddress(dll, ProcessProcName);
        destroy = (PtrToFuncNvOFFRUCDestroy)GetProcAddress(dll, DestroyProcName);
        if (!create || !reg || !unreg || !process || !destroy) { fprintf(stderr, "NvOFFRUC exports missing\n"); return false; }
        return true;
    }
};

static int RunSize(Fruc& fr, ID3D11Device5* dev, ID3D11DeviceContext4* ctx, int w, int h, int frames, const std::string& outDir, int every) {
    // textures: two inputs (ring), one output, one staging for readback; shared NT handles + a shared fence (the sample's recipe)
    D3D11_TEXTURE2D_DESC td{};
    td.Width = w; td.Height = h; td.MipLevels = td.ArraySize = 1; td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT; td.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    const int kIn = NvOFFRUC_MAX_RESOURCE - 1;
    if (frames > kIn) frames = kIn;
    ComPtr<ID3D11Texture2D> in[NvOFFRUC_MAX_RESOURCE], out, staging;
    for (int i = 0; i < kIn; i++) if (FAILED(dev->CreateTexture2D(&td, nullptr, &in[i]))) { fprintf(stderr, "CreateTexture2D in failed\n"); return 2; }
    if (FAILED(dev->CreateTexture2D(&td, nullptr, &out))) { fprintf(stderr, "CreateTexture2D out failed\n"); return 2; }
    D3D11_TEXTURE2D_DESC sd = td; sd.MiscFlags = 0; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    if (FAILED(dev->CreateTexture2D(&sd, nullptr, &staging))) { fprintf(stderr, "CreateTexture2D staging failed\n"); return 2; }
    ComPtr<ID3D11Fence> fence;
    if (FAILED(dev->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence)))) { fprintf(stderr, "CreateFence failed\n"); return 2; }
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);

    NvOFFRUC_CREATE_PARAM cp{};
    cp.uiWidth = w; cp.uiHeight = h; cp.pDevice = dev; cp.eResourceType = DirectX11Resource; cp.eSurfaceFormat = ARGBSurface;
    cp.eCUDAResourceType = CudaResourceTypeUndefined;
    NvOFFRUCHandle hf = nullptr;
    const double t0 = NowMs();
    NvOFFRUC_STATUS st = fr.create(&cp, &hf);
    const double createMs = NowMs() - t0;
    if (st != NvOFFRUC_SUCCESS) { printf("{\"w\":%d,\"h\":%d,\"error\":\"NvOFFRUCCreate %d\"}\n", w, h, (int)st); return 3; }
    NvOFFRUC_REGISTER_RESOURCE_PARAM rp{};
    for (int i = 0; i < kIn; i++) rp.pArrResource[i] = in[i].Get();
    rp.pArrResource[kIn] = out.Get(); rp.uiCount = kIn + 1; rp.pD3D11FenceObj = fence.Get();
    st = fr.reg(hf, &rp);
    if (st != NvOFFRUC_SUCCESS) { printf("{\"w\":%d,\"h\":%d,\"error\":\"NvOFFRUCRegisterResource %d\"}\n", w, h, (int)st); fr.destroy(hf); return 3; }

    Scene scene(w, h);
    std::vector<std::vector<uint8_t>> rendered(frames);   // all frames first: the GPU must not idle (and downclock) between Process calls
    for (int k = 0; k < frames; k++) { scene.render((double)k, rendered[k]); ctx->UpdateSubresource(in[k].Get(), 0, nullptr, rendered[k].data(), w * 4, 0); }
    ctx->Flush();
    std::vector<uint8_t> truth, got, blend;
    std::vector<double> ms; ms.reserve(frames);
    double psnrF = 0, psnrR = 0, psnrB = 0; int checks = 0, reps = 0;
    uint64_t fv = 0;
    for (int k = 0; k < frames; k++) {
        const std::vector<uint8_t>& cur = rendered[k];
        const std::vector<uint8_t>& prev = rendered[k > 0 ? k - 1 : 0];
        ID3D11Texture2D* tex = in[k].Get();
        fv++;
        ctx->Signal(fence.Get(), fv);              // the library waits for the upload
        NvOFFRUC_PROCESS_IN_PARAMS ip{};
        NvOFFRUC_PROCESS_OUT_PARAMS op{};
        bool repeated = false;
        ip.stFrameDataInput.pFrame = tex; ip.stFrameDataInput.nTimeStamp = (double)k; ip.uSyncWait.FenceWaitValue.uiFenceValueToWaitOn = fv;
        op.stFrameDataOutput.pFrame = out.Get(); op.stFrameDataOutput.nTimeStamp = k - 0.5; op.stFrameDataOutput.bHasFrameRepetitionOccurred = &repeated;
        op.uSyncSignal.FenceSignalValue.uiFenceValueToSignalOn = ++fv;   // the library signals when the output is written
        const double a = NowMs();
        st = fr.process(hf, &ip, &op);
        if (st != NvOFFRUC_SUCCESS) { printf("{\"w\":%d,\"h\":%d,\"error\":\"NvOFFRUCProcess %d at frame %d\"}\n", w, h, (int)st, k); break; }
        fence->SetEventOnCompletion(fv, ev);
        WaitForSingleObject(ev, 5000);
        const double b = NowMs() - a;
        if (k > 0) { ms.push_back(b); if (repeated) reps++; }
        if (k > 0 && (k % every) == 0) {
            ctx->Wait(fence.Get(), fv);
            ctx->CopyResource(staging.Get(), out.Get());
            D3D11_MAPPED_SUBRESOURCE m{};
            if (SUCCEEDED(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &m))) {
                got.resize((size_t)w * h * 4);
                for (int y = 0; y < h; y++) memcpy(got.data() + (size_t)y * w * 4, (uint8_t*)m.pData + (size_t)y * m.RowPitch, (size_t)w * 4);
                ctx->Unmap(staging.Get(), 0);
                scene.render(k - 0.5, truth);
                blend.resize(got.size());
                for (size_t i = 0; i < blend.size(); i++) blend[i] = (uint8_t)(((int)prev[i] + cur[i] + 1) >> 1);
                const size_t px = (size_t)w * h;
                psnrF += Psnr(got.data(), truth.data(), px); psnrR += Psnr(prev.data(), truth.data(), px); psnrB += Psnr(blend.data(), truth.data(), px);
                checks++;
                if (k == (frames / 2 / every) * every) {
                    const std::string p = outDir + "\\fruc_" + std::to_string(w) + "x" + std::to_string(h) + "_";
                    WriteBmp(p + "prev.bmp", prev.data(), w, h); WriteBmp(p + "interp.bmp", got.data(), w, h);
                    WriteBmp(p + "truth.bmp", truth.data(), w, h); WriteBmp(p + "next.bmp", cur.data(), w, h);
                }
            }
        }
    }
    std::sort(ms.begin(), ms.end());
    auto q = [&](double p) -> double { if (ms.empty()) return 0.0; return ms[std::min(ms.size() - 1, (size_t)(p * ms.size()))]; };
    printf("{\"w\":%d,\"h\":%d,\"frames\":%d,\"create_ms\":%.1f,\"process_ms\":{\"p50\":%.2f,\"p95\":%.2f,\"max\":%.2f},"
           "\"psnr_db\":{\"fruc\":%.2f,\"repeat\":%.2f,\"blend\":%.2f},\"checks\":%d,\"repetitions\":%d}\n",
           w, h, frames, createMs, q(0.5), q(0.95), ms.empty() ? 0.0 : ms.back(),
           checks ? psnrF / checks : 0.0, checks ? psnrR / checks : 0.0, checks ? psnrB / checks : 0.0, checks, reps, GetEnvironmentVariableW(L"FRUC_PROBE_NO_KEEPALIVE", nullptr, 0) ? 0 : 1);
    fflush(stdout);
    NvOFFRUC_UNREGISTER_RESOURCE_PARAM up{};
    memcpy(up.pArrResource, rp.pArrResource, sizeof(void*) * (kIn + 1)); up.uiCount = kIn + 1;
    fr.unreg(hf, &up);
    fr.destroy(hf);
    CloseHandle(ev);
    return 0;
}

// Keep-alive: a laptop GPU idles into P8 (video clock ~550 MHz) when its only work is a few short calls per frame, and every
// measurement is then 3x slower than in a running game. A second device on the same adapter copies two 32 MB textures back and
// forth at a high duty cycle, as alvr_host --gpu-keepalive does, so the clocks stay where a game keeps them.
static void KeepAlive(IDXGIAdapter1* adapter, std::atomic<bool>* stop) {
    ComPtr<ID3D11Device> dev; ComPtr<ID3D11DeviceContext> ctx;
    D3D_FEATURE_LEVEL lv[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, lv, 2, D3D11_SDK_VERSION, &dev, nullptr, &ctx))) return;
    D3D11_TEXTURE2D_DESC td{};
    td.Width = 4096; td.Height = 2048; td.MipLevels = td.ArraySize = 1; td.Format = DXGI_FORMAT_R8G8B8A8_UNORM; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> a, b;
    if (FAILED(dev->CreateTexture2D(&td, nullptr, &a)) || FAILED(dev->CreateTexture2D(&td, nullptr, &b))) return;
    D3D11_QUERY_DESC qd{ D3D11_QUERY_EVENT, 0 };
    ComPtr<ID3D11Query> q; dev->CreateQuery(&qd, &q);
    while (!stop->load()) {
        for (int i = 0; i < 8; i++) { ctx->CopyResource(b.Get(), a.Get()); ctx->CopyResource(a.Get(), b.Get()); }
        ctx->End(q.Get()); ctx->Flush();
        BOOL done = FALSE;
        while (!stop->load() && ctx->GetData(q.Get(), &done, sizeof(done), 0) != S_OK) Sleep(0);
        Sleep(1);
    }
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 3) { fprintf(stderr, "usage: fruc_probe <dll dir> <out dir> [frames] [WxH ...]\n"); return 1; }
    std::wstring dllDir = argv[1];
    std::string outDir; for (wchar_t c : std::wstring(argv[2])) outDir.push_back((char)c);
    int frames = argc > 3 ? _wtoi(argv[3]) : 40;
    std::vector<std::pair<int, int>> sizes;
    for (int i = 4; i < argc; i++) { int w = 0, h = 0; if (swscanf_s(argv[i], L"%dx%d", &w, &h) == 2) sizes.push_back({ w, h }); }
    if (sizes.empty()) sizes = { {1760, 1600}, {4224, 1664}, {7104, 3200} };
    CreateDirectoryA(outDir.c_str(), nullptr);

    // the NVIDIA adapter (a laptop's default adapter is often the integrated GPU)
    ComPtr<IDXGIFactory1> factory; ComPtr<IDXGIAdapter1> adapter, pick;
    CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    for (UINT i = 0; factory && factory->EnumAdapters1(i, &adapter) == S_OK; i++) {
        DXGI_ADAPTER_DESC1 d{}; adapter->GetDesc1(&d);
        if (d.VendorId == 0x10DE && !pick) pick = adapter;
        adapter.Reset();
    }
    if (!pick) { fprintf(stderr, "no NVIDIA adapter\n"); return 2; }
    ComPtr<ID3D11Device> dev0; ComPtr<ID3D11DeviceContext> ctx0;
    D3D_FEATURE_LEVEL lv[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(pick.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, lv, 2, D3D11_SDK_VERSION, &dev0, nullptr, &ctx0))) {
        fprintf(stderr, "D3D11CreateDevice failed\n"); return 2;
    }
    ComPtr<ID3D11Device5> dev; ComPtr<ID3D11DeviceContext4> ctx;
    if (FAILED(dev0.As(&dev)) || FAILED(ctx0.As(&ctx))) { fprintf(stderr, "ID3D11Device5 / ID3D11DeviceContext4 not available\n"); return 2; }
    {
        ComPtr<IDXGIDevice> dx; ComPtr<IDXGIAdapter> ad; DXGI_ADAPTER_DESC d{};
        if (SUCCEEDED(dev0.As(&dx)) && SUCCEEDED(dx->GetAdapter(&ad)) && SUCCEEDED(ad->GetDesc(&d))) {
            std::string name; for (wchar_t c : std::wstring(d.Description)) name.push_back((char)c);
            printf("{\"gpu\":\"%s\"}\n", name.c_str());
        }
    }
    Fruc fr;
    if (!fr.load(dllDir)) return 2;
    std::atomic<bool> stop{ false };
    const bool keep = !GetEnvironmentVariableW(L"FRUC_PROBE_NO_KEEPALIVE", nullptr, 0);
    std::thread ka;
    if (keep) { ka = std::thread(KeepAlive, pick.Get(), &stop); Sleep(400); }
    int rc = 0;
    for (auto& s : sizes) rc |= RunSize(fr, dev.Get(), ctx.Get(), s.first, s.second, frames, outDir, 2);
    stop = true;
    if (ka.joinable()) ka.join();
    return rc;
}
