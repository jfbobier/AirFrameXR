// Desktop mirror probe (harness/mirror_check.py): renders the mirror's shader on the real GPU and reports what comes out.
//   desktop_probe.exe synthetic sdr|hdr <white> <out.json>   a 4x2 grid of known patches as the "desktop" -> the value at each patch centre
//   desktop_probe.exe capture <out.json>                      the real desktop: size, whether HDR, the mean colour of the left eye image
// Both draw the screen over the whole left eye (corners at the NDC square), the right eye not at all (NaN), and, in the synthetic mode, the cursor
// dot at the middle of patch 5 (so its pixels are known). Includes desktop.cpp directly (one source, no project).
#define NOMINMAX
#include "desktop.cpp"
#include <wtsapi32.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

static uint16_t half(float f) {   // IEEE half, round to nearest (patch values are simple)
    uint32_t x; memcpy(&x, &f, 4);
    uint32_t sign = (x >> 16) & 0x8000, e = ((x >> 23) & 0xFF), m = x & 0x7FFFFF;
    if (e == 0) return (uint16_t)sign;
    int ne = (int)e - 127 + 15;
    if (ne >= 31) return (uint16_t)(sign | 0x7C00);
    if (ne <= 0) return (uint16_t)sign;
    uint32_t r = (m + 0x1000) >> 13;   // round
    return (uint16_t)(sign | ((uint32_t)ne << 10) + r);
}

int main(int argc, char** argv) {
    if (argc < 3) { fprintf(stderr, "usage: see the header comment\n"); return 2; }
    const bool synthetic = !strcmp(argv[1], "synthetic");
    const bool hdr = synthetic && argc > 2 && !strcmp(argv[2], "hdr");
    const float white = synthetic && argc > 3 ? (float)atof(argv[3]) : 1.f;
    const char* outPath = synthetic ? (argc > 4 ? argv[4] : "probe.json") : argv[2];
    if (!synthetic) {   // where this runs: the session and whether the input desktop can be opened (desktop duplication needs both)
        DWORD sid = 0; ProcessIdToSessionId(GetCurrentProcessId(), &sid);
        HDESK d = OpenInputDesktop(0, FALSE, GENERIC_ALL); const DWORD e = d ? 0 : GetLastError();
        fprintf(stderr, "session %lu, console session %lu, input desktop %s (error %lu)\n", (unsigned long)sid, (unsigned long)WTSGetActiveConsoleSessionId(), d ? "opened" : "NOT opened", (unsigned long)e);
        if (d) CloseDesktop(d);
    }
    ComPtr<ID3D11Device> dev; ComPtr<ID3D11DeviceContext> ctx;
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, nullptr, &ctx))) { fprintf(stderr, "no device\n"); return 3; }
    const int TW = 1024, TH = 512;   // two eyes of 512 x 512
    DesktopMirror m(dev.Get(), ctx.Get(), TW, TH, DXGI_FORMAT_R8G8B8A8_UNORM);
    char err[256] = {};
    const int DW = 256, DH = 128;    // synthetic desktop: 4 x 2 patches of 64 x 64
    const float vals[8] = { 0.f, 0.25f, 0.5f, 1.f, 1.8f, 2.f, 4.f, 8.f };   // HDR: scRGB values (white = the SDR white level); SDR: scaled to 0..255 below
    if (synthetic) {
        if (!m.InitForTest(DW, DH, hdr, white, err, 256)) { fprintf(stderr, "init: %s\n", err); return 4; }
        std::vector<uint8_t> img(DW * DH * (hdr ? 8 : 4));
        for (int y = 0; y < DH; y++) for (int x = 0; x < DW; x++) {
            const int p = (y / 64) * 4 + (x / 64);
            if (hdr) { uint16_t* px = (uint16_t*)&img[(y * DW + x) * 8]; px[0] = px[1] = px[2] = half(vals[p]); px[3] = half(1.f); }
            else { uint8_t* px = &img[(y * DW + x) * 4]; px[0] = px[1] = px[2] = (uint8_t)(p * 36); px[3] = 255; }   // BGRA
        }
        m.SetTestImage(img.data());
    } else if (!m.Init(err, 256)) { fprintf(stderr, "init: %s\n", err); return 4; }
    float ndc[16]; for (int i = 0; i < 16; i++) ndc[i] = NAN;
    const float full[8] = { -1, 1, 1, 1, -1, -1, 1, -1 };
    memcpy(ndc, full, sizeof full);
    const float cursor[2] = { synthetic ? (5 % 4 + 0.5f) / 4.f : -1.f, synthetic ? (5 / 4 + 0.5f) / 2.f : -1.f };
    // capture mode: give the duplication a moment to deliver a first image
    ID3D11Texture2D* tex = nullptr;
    for (int i = 0; i < (synthetic ? 1 : 30); i++) { tex = m.Render(ndc, cursor, 0x112233, err, 256); if (!tex) { fprintf(stderr, "render: %s\n", err); return 5; } if (!synthetic) Sleep(100); }
    D3D11_TEXTURE2D_DESC td; tex->GetDesc(&td); td.Usage = D3D11_USAGE_STAGING; td.BindFlags = 0; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> st; dev->CreateTexture2D(&td, nullptr, &st);
    ctx->CopyResource(st.Get(), tex);
    D3D11_MAPPED_SUBRESOURCE mp; if (FAILED(ctx->Map(st.Get(), 0, D3D11_MAP_READ, 0, &mp))) { fprintf(stderr, "map\n"); return 6; }
    auto px = [&](int x, int y) { const uint8_t* p = (const uint8_t*)mp.pData + y * mp.RowPitch + x * 4; return std::string("[") + std::to_string(p[0]) + "," + std::to_string(p[1]) + "," + std::to_string(p[2]) + "]"; };
    FILE* f = fopen(outPath, "w");
    fprintf(f, "{\"mode\":\"%s\",\"hdr\":%d,\"desktop\":[%d,%d],\"white\":%.3f", synthetic ? (hdr ? "synthetic_hdr" : "synthetic_sdr") : "capture", hdr || m.Hdr() ? 1 : 0, m.DesktopW(), m.DesktopH(), white);
    if (synthetic) {
        // the left eye image is 512 x 512 over the 256 x 128 desktop: patch (c, r) is centred at ((c + 0.5) / 4 * 512, (r + 0.5) / 2 * 512); the cursor sits on patch 5
        fprintf(f, ",\"patches\":[");
        for (int p = 0; p < 8; p++) fprintf(f, "%s%s", p ? "," : "", px((int)((p % 4 + 0.5) / 4 * 512), (int)((p / 4 + 0.5) / 2 * 512)).c_str());
        fprintf(f, "],\"cursor_centre\":%s,\"cursor_ring\":%s,\"cursor_outside\":%s,\"right_eye\":%s,\"vals\":[", px(((5 % 4) * 2 + 1) * 64, ((5 / 4) * 2 + 1) * 128).c_str(),
                px(((5 % 4) * 2 + 1) * 64 + (int)(5.5f * 2), ((5 / 4) * 2 + 1) * 128).c_str(), px(((5 % 4) * 2 + 1) * 64 + 40, ((5 / 4) * 2 + 1) * 128).c_str(), px(768, 256).c_str());
        for (int p = 0; p < 8; p++) fprintf(f, "%s%.3f", p ? "," : "", vals[p]);
        fprintf(f, "]");
    } else {
        double r = 0, g = 0, b = 0; int n = 0;
        for (int y = 0; y < TH; y += 8) for (int x = 0; x < TW / 2; x += 8) { const uint8_t* p = (const uint8_t*)mp.pData + y * mp.RowPitch + x * 4; r += p[0]; g += p[1]; b += p[2]; n++; }
        fprintf(f, ",\"mean_left_eye\":[%.1f,%.1f,%.1f],\"right_eye_is_clear_colour\":%s", r / n, g / n, b / n, px(768, 256).c_str());
    }
    fprintf(f, "}\n"); fclose(f);
    ctx->Unmap(st.Get(), 0);
    return 0;
}
