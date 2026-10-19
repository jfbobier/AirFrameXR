// Desktop mirror probe (harness/mirror_check.py): renders the mirror's shader on the real GPU and reports what comes out.
//   desktop_probe.exe synthetic sdr|hdr <white> <out.json> <W> <H> <xl> <xr> <yt> <yb> [zoom]
//        (zoom: ignore the extents; 1 desktop pixel = 1 eye pixel, centred on the cursor, so the dot can be measured)
//        a W x H "desktop" of 4 x 2 known patches, drawn as a screen quad covering NDC x in [xl, xr], y in [yb, yt] of the left eye (the right eye does not see it);
//        reports the value at each patch centre, the cursor dot's pixels at known distances, and the pixels just outside the quad
//   desktop_probe.exe capture <out.json>
//        the real desktop: size, whether HDR, the mean colour of the left eye image
// Includes desktop.cpp directly (one source, no project).
#define NOMINMAX
#include "desktop.cpp"
#include <wtsapi32.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

static uint16_t half(float f) {   // IEEE half (the patch values are simple)
    uint32_t x; memcpy(&x, &f, 4);
    uint32_t sign = (x >> 16) & 0x8000, e = ((x >> 23) & 0xFF), m = x & 0x7FFFFF;
    if (e == 0) return (uint16_t)sign;
    int ne = (int)e - 127 + 15;
    if (ne >= 31) return (uint16_t)(sign | 0x7C00);
    if (ne <= 0) return (uint16_t)sign;
    uint32_t r = (m + 0x1000) >> 13;
    return (uint16_t)(sign | ((uint32_t)ne << 10) + r);
}

int main(int argc, char** argv) {
    if (argc < 3) { fprintf(stderr, "usage: see the header comment\n"); return 2; }
    const bool synthetic = !strcmp(argv[1], "synthetic");
    const bool hdr = synthetic && !strcmp(argv[2], "hdr");
    const float white = synthetic && argc > 3 ? (float)atof(argv[3]) : 1.f;
    const char* outPath = synthetic ? (argc > 4 ? argv[4] : "probe.json") : argv[2];
    const int DW = synthetic && argc > 5 ? atoi(argv[5]) : 256, DH = synthetic && argc > 6 ? atoi(argv[6]) : 128;
    const bool zoom = synthetic && argc > 11 && !strcmp(argv[11], "zoom");
    const float cu = (1 + 0.5f) / 4.f, cv = (1 + 0.5f) / 2.f;   // the cursor sits on the middle of patch 5 (column 1, row 1)
    float xl = synthetic && argc > 7 ? (float)atof(argv[7]) : -1.f, xr = synthetic && argc > 8 ? (float)atof(argv[8]) : 1.f;
    float yt = synthetic && argc > 9 ? (float)atof(argv[9]) : 1.f, yb = synthetic && argc > 10 ? (float)atof(argv[10]) : -1.f;
    if (zoom) { const float wn = DW * 2.f / 512.f, hn = DH * 2.f / 512.f; xl = -cu * wn; xr = xl + wn; yt = cv * hn; yb = yt - hn; }
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
    const float vals[8] = { 0.f, 0.25f, 0.5f, 1.f, 1.8f, 2.f, 4.f, 8.f };   // HDR: scRGB values
    if (synthetic) {
        if (!m.InitForTest(DW, DH, hdr, white, err, 256)) { fprintf(stderr, "init: %s\n", err); return 4; }
        std::vector<uint8_t> img((size_t)DW * DH * (hdr ? 8 : 4));
        for (int y = 0; y < DH; y++) for (int x = 0; x < DW; x++) {
            const int p = (y * 2 / DH) * 4 + (x * 4 / DW);
            if (hdr) { uint16_t* px = (uint16_t*)&img[((size_t)y * DW + x) * 8]; px[0] = px[1] = px[2] = half(vals[p]); px[3] = half(1.f); }
            else { uint8_t* px = &img[((size_t)y * DW + x) * 4]; px[0] = px[1] = px[2] = (uint8_t)(p * 36); px[3] = 255; }   // BGRA
        }
        m.SetTestImage(img.data());
    } else if (!m.Init(err, 256)) { fprintf(stderr, "init: %s\n", err); return 4; }
    float ndc[16]; for (int i = 0; i < 16; i++) ndc[i] = NAN;
    const float quad[8] = { xl, yt, xr, yt, xl, yb, xr, yb };   // TL TR BL BR
    memcpy(ndc, quad, sizeof quad);
    const float cursor[2] = { synthetic ? cu : -1.f, synthetic ? cv : -1.f };
    ID3D11Texture2D* tex = nullptr;
    for (int i = 0; i < (synthetic ? 1 : 30); i++) { tex = m.Render(ndc, cursor, 0x112233, err, 256); if (!tex) { fprintf(stderr, "render: %s\n", err); return 5; } if (!synthetic) Sleep(100); }
    D3D11_TEXTURE2D_DESC td; tex->GetDesc(&td); td.Usage = D3D11_USAGE_STAGING; td.BindFlags = 0; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> st; dev->CreateTexture2D(&td, nullptr, &st);
    ctx->CopyResource(st.Get(), tex);
    D3D11_MAPPED_SUBRESOURCE mp; if (FAILED(ctx->Map(st.Get(), 0, D3D11_MAP_READ, 0, &mp))) { fprintf(stderr, "map\n"); return 6; }
    auto px = [&](int x, int y) { x = std::max(0, std::min(TW - 1, x)); y = std::max(0, std::min(TH - 1, y)); const uint8_t* p = (const uint8_t*)mp.pData + y * mp.RowPitch + x * 4; return std::string("[") + std::to_string(p[0]) + "," + std::to_string(p[1]) + "," + std::to_string(p[2]) + "]"; };
    // desktop uv -> pixel of the left eye image (512 x 512): the quad spans [xl, xr] x [yb, yt] in NDC
    auto ex = [&](float u) { return (int)(((xl + u * (xr - xl)) + 1.f) / 2.f * 512.f); };
    auto ey = [&](float v) { return (int)((1.f - (yt + v * (yb - yt))) / 2.f * 512.f); };
    FILE* f = fopen(outPath, "w");
    fprintf(f, "{\"mode\":\"%s\",\"hdr\":%d,\"desktop\":[%d,%d],\"white\":%.3f", synthetic ? (hdr ? "synthetic_hdr" : "synthetic_sdr") : "capture", hdr || m.Hdr() ? 1 : 0, m.DesktopW(), m.DesktopH(), white);
    if (synthetic) {
        fprintf(f, ",\"patches\":[");
        for (int p = 0; p < 8; p++) fprintf(f, "%s%s", p ? "," : "", px(ex(((p % 4) + 0.5f) / 4.f), ey(((p / 4) + 0.5f) / 2.f)).c_str());
        // the cursor dot: pixels at known distances (desktop pixels) to the right of its centre; 1 desktop px = (xr - xl) / DW * 256 eye px
        const float sc = std::max(1.f, DH / 1080.f), eyePerDesk = (xr - xl) / DW * 256.f;   // zoom: exactly 1
        auto at = [&](float deskPx) { return px((int)(ex(cu) + deskPx * eyePerDesk), ey(cv)); };
        fprintf(f, "],\"cursor_scale\":%.4f", sc);
        fprintf(f, ",\"cursor_core\":%s,\"cursor_ring\":%s,\"cursor_outside\":%s", at(2.f * sc).c_str(), at(5.5f * sc).c_str(), at(9.f * sc).c_str());
        fprintf(f, ",\"outside_quad\":%s", (xl > -0.97f ? px((int)((xl + 1.f) / 2.f * 512.f) - 6, 256) : std::string("null")).c_str());
        fprintf(f, ",\"right_eye\":%s,\"vals\":[", px(768, 256).c_str());
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
