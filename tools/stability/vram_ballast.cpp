// Stability harness: takes VRAM and RAM away from the chain so a machine with less memory than the dev PCs can be imitated (docs/STABILITY_PLAN.md).
//
//   vram_ballast.exe [--vram "0:0,30:6000,90:0"] [--ram "0:0,30:8000"] [--seconds 120] [--stop-file <file>] [--log <file>]
//
// Schedules are "t_s:MB" pairs: from t_s on, hold that many MB (a level is reached by allocating or releasing 128 MB chunks; a failed allocation is
// logged and retried every second, so the ballast takes what the system still gives). VRAM chunks are default-usage RGBA8 render targets cleared once a
// second so they stay resident; RAM is committed and touched. Prints one line per change: t, wanted, held, free VRAM budget. Exit: at --seconds or when
// the stop file exists. Windows only (D3D11 on the adapter with the most dedicated memory).
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_4.h>
#include <cstdio>
#include <cstdarg>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <chrono>
#include <thread>
#include <algorithm>

static std::vector<std::pair<double, int>> parse(const char* s) {
    std::vector<std::pair<double, int>> v;
    std::string t(s ? s : "");
    size_t p = 0;
    while (p < t.size()) {
        size_t e = t.find(',', p); if (e == std::string::npos) e = t.size();
        std::string item = t.substr(p, e - p); size_t c = item.find(':');
        if (c != std::string::npos) v.push_back({atof(item.substr(0, c).c_str()), atoi(item.substr(c + 1).c_str())});
        p = e + 1;
    }
    std::sort(v.begin(), v.end());
    return v;
}
static int level(const std::vector<std::pair<double, int>>& sch, double t) {
    int mb = 0;
    for (auto& s : sch) if (s.first <= t) mb = s.second;
    return mb;
}

int main(int argc, char** argv) {
    const char *vs = "", *rs = "", *stopFile = nullptr, *logFile = nullptr; double seconds = 120;
    for (int i = 1; i + 1 < argc; i += 2) {
        std::string k = argv[i];
        if (k == "--vram") vs = argv[i + 1]; else if (k == "--ram") rs = argv[i + 1]; else if (k == "--seconds") seconds = atof(argv[i + 1]);
        else if (k == "--stop-file") stopFile = argv[i + 1]; else if (k == "--log") logFile = argv[i + 1];
    }
    FILE* lf = logFile ? fopen(logFile, "w") : nullptr;
    auto out = [&](const char* fmt, ...) {
        char b[512]; va_list a; va_start(a, fmt); vsnprintf(b, sizeof b, fmt, a); va_end(a);
        printf("%s\n", b); fflush(stdout); if (lf) { fprintf(lf, "%s\n", b); fflush(lf); }
    };
    auto vram = parse(vs); auto ram = parse(rs);

    IDXGIFactory1* fac = nullptr; CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&fac);
    IDXGIAdapter1* best = nullptr; SIZE_T bigMem = 0;
    for (UINT i = 0; fac; i++) {
        IDXGIAdapter1* ad = nullptr; if (fac->EnumAdapters1(i, &ad) != S_OK) break;
        DXGI_ADAPTER_DESC1 d; ad->GetDesc1(&d);
        if (!(d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) && d.DedicatedVideoMemory > bigMem) { bigMem = d.DedicatedVideoMemory; if (best) best->Release(); best = ad; } else ad->Release();
    }
    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr;
    if (!vram.empty() && best) {
        if (FAILED(D3D11CreateDevice(best, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, nullptr, &ctx))) { out("no D3D11 device"); return 2; }
    }
    IDXGIAdapter3* ad3 = nullptr; if (best) best->QueryInterface(__uuidof(IDXGIAdapter3), (void**)&ad3);
    struct Chunk { ID3D11Texture2D* t; ID3D11RenderTargetView* rtv; };
    std::vector<Chunk> chunks; std::vector<void*> rams;
    const int CH = 128;   // MB per chunk: 4096 x 8192 RGBA8
    auto t0 = std::chrono::steady_clock::now();
    int lastV = -1, lastR = -1; int lastHeldV = -1, lastHeldR = -1;
    out("ballast start: vram schedule '%s', ram schedule '%s', %.0f s", vs, rs, seconds);
    for (;;) {
        double t = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
        if (t >= seconds || (stopFile && GetFileAttributesA(stopFile) != INVALID_FILE_ATTRIBUTES)) break;
        int wantV = level(vram, t), wantR = level(ram, t);
        while (dev && (int)chunks.size() * CH < wantV) {
            D3D11_TEXTURE2D_DESC d{}; d.Width = 4096; d.Height = 8192; d.MipLevels = 1; d.ArraySize = 1; d.Format = DXGI_FORMAT_R8G8B8A8_UNORM; d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_RENDER_TARGET;
            ID3D11Texture2D* tex = nullptr; ID3D11RenderTargetView* rtv = nullptr;
            if (FAILED(dev->CreateTexture2D(&d, nullptr, &tex)) || FAILED(dev->CreateRenderTargetView(tex, nullptr, &rtv))) { if (tex) tex->Release(); break; }
            chunks.push_back({tex, rtv});
        }
        while (dev && (int)chunks.size() * CH > wantV && !chunks.empty()) { chunks.back().rtv->Release(); chunks.back().t->Release(); chunks.pop_back(); }
        while ((int)rams.size() * CH < wantR) {
            void* p = VirtualAlloc(nullptr, (SIZE_T)CH << 20, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (!p) break;
            memset(p, 0x5A, (SIZE_T)CH << 20); rams.push_back(p);
        }
        while ((int)rams.size() * CH > wantR && !rams.empty()) { VirtualFree(rams.back(), 0, MEM_RELEASE); rams.pop_back(); }
        if (ctx) { const float c[4] = {0.1f, 0.2f, 0.3f, 1}; for (auto& ch : chunks) ctx->ClearRenderTargetView(ch.rtv, c); ctx->Flush(); }
        for (void* p : rams) { volatile char* q = (volatile char*)p; for (size_t o = 0; o < ((size_t)CH << 20); o += 1 << 20) q[o] = (char)o; }   // keep the pages resident
        int hv = (int)chunks.size() * CH, hr = (int)rams.size() * CH;
        if (wantV != lastV || wantR != lastR || hv != lastHeldV || hr != lastHeldR) {
            DXGI_QUERY_VIDEO_MEMORY_INFO vi{}; if (ad3) ad3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &vi);
            out("t=%.0f vram want %d MB held %d MB%s | ram want %d MB held %d MB%s | this process %llu MB of a %llu MB budget", t, wantV, hv, hv < wantV * 0.9 ? " (SHORT)" : "", wantR, hr, hr < wantR * 0.9 ? " (SHORT)" : "",
                (unsigned long long)(vi.CurrentUsage >> 20), (unsigned long long)(vi.Budget >> 20));
            lastV = wantV; lastR = wantR; lastHeldV = hv; lastHeldR = hr;
        }
        std::this_thread::sleep_for(std::chrono::seconds(1));
    }
    out("ballast end");
    return 0;
}
