#include "desktop.h"
#include <dxgi1_5.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <windows.h>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {
const char* kShader = R"(
cbuffer CB : register(b0) {
    float4 c01;      // top-left, top-right of the screen in this eye's NDC
    float4 c23;      // bottom-left, bottom-right
    float4 cursor;   // xy: uv over the desktop (negative: none), zw: desktop size in pixels
    float4 mode;     // x: 1 = HDR (scRGB), y: SDR white level (scRGB units of the desktop's white), z: 1 = draw the laser, w: eye image height in pixels
    float4 laser;    // the pointing beam in this eye's NDC: x0 y0 x1 y1
};
Texture2D desk : register(t0);
SamplerState smp : register(s0);
struct VSOut { float4 pos : SV_Position; float2 ndc : TEXCOORD0; };
// the whole eye: the screen is found per pixel (a perspective-correct mapping), so a screen seen at an angle is not warped
VSOut VS(uint id : SV_VertexID) {
    float2 p[4] = { float2(-1, 1), float2(1, 1), float2(-1, -1), float2(1, -1) };
    VSOut o; o.pos = float4(p[id], 0, 1); o.ndc = p[id]; return o;
}
float3 srgb(float3 c) { return c <= 0.0031308 ? 12.92 * c : 1.055 * pow(c, 1.0 / 2.4) - 0.055; }
// the point of the unit square that the eye sees at NDC position q, from the four corners (the projective map of the square onto them, inverted)
float2 ScreenUV(float2 q) {
    float2 A = c01.xy, B = c01.zw, D = c23.xy, C = c23.zw;
    float2 s = A - B + C - D, d1 = B - C, d2 = D - C;
    float den = d1.x * d2.y - d2.x * d1.y;
    float g = (s.x * d2.y - d2.x * s.y) / den, h = (d1.x * s.y - s.x * d1.y) / den;
    float a = B.x - A.x + g * B.x, b = D.x - A.x + h * D.x, d = B.y - A.y + g * B.y, e = D.y - A.y + h * D.y;
    float m00 = a - g * q.x, m01 = b - h * q.x, m10 = d - g * q.y, m11 = e - h * q.y;
    float r0 = q.x - A.x, r1 = q.y - A.y, det = m00 * m11 - m01 * m10;
    return float2((r0 * m11 - m01 * r1) / det, (m00 * r1 - r0 * m10) / det);
}
float4 PS(VSOut i) : SV_Target {
    // the beam first (it is drawn over the screen and outside it): 3 px wide in the eye image, a dark edge around it
    if (mode.z > 0.5) {
        float2 k = float2(mode.w * 0.5, mode.w * 0.5);   // NDC to pixels of one eye (an eye image is as tall as it is half-wide at most: the scale is only used for the width)
        float2 pa = laser.xy * k, pb = laser.zw * k, pp = i.ndc * k;
        float2 ab = pb - pa; float t = saturate(dot(pp - pa, ab) / max(dot(ab, ab), 1e-6));
        float dist = length(pp - (pa + ab * t));
        float w = max(1.5, mode.w / 1080.0 * 1.5);
        if (dist < w) return float4(1.0, 0.35, 0.1, 1);
        if (dist < w + 1.2) return float4(0, 0, 0, 1);
    }
    float2 uv = ScreenUV(i.ndc);
    if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1) discard;
    float3 c = desk.Sample(smp, uv).rgb;
    if (mode.x > 0.5) {
        c = max(c, 0) / mode.y;                                        // the desktop's SDR white becomes 1
        c = c < 0.9 ? c : 0.9 + 0.1 * tanh((c - 0.9) / 0.1);          // HDR highlights roll off instead of clipping
        c = srgb(c);
    }
    if (cursor.x >= 0) {
        // the dot is the same size to the eye whatever the desktop's resolution: 4 px core and 3 px ring on a 1080-line desktop, more on a bigger one
        float sc = max(1.0, cursor.w / 1080.0);
        float r = length((uv - cursor.xy) * cursor.zw);
        if (r < 4.0 * sc) c = float3(1, 1, 1); else if (r < 7.0 * sc) c = float3(0, 0, 0);
    }
    return float4(c, 1);
}
)";

// SDR white level of the primary monitor in units of scRGB 1.0 = 80 nits (Windows' "SDR content brightness"); 1 when unknown.
float SdrWhite() {
    UINT32 np = 0, nm = 0;
    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &np, &nm) != ERROR_SUCCESS || np == 0) return 1.f;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(np); std::vector<DISPLAYCONFIG_MODE_INFO> modes(nm);
    if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &np, paths.data(), &nm, modes.data(), nullptr) != ERROR_SUCCESS) return 1.f;
    DISPLAYCONFIG_SDR_WHITE_LEVEL w{};
    w.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL; w.header.size = sizeof w;
    w.header.adapterId = paths[0].targetInfo.adapterId; w.header.id = paths[0].targetInfo.id;
    if (DisplayConfigGetDeviceInfo(&w.header) != ERROR_SUCCESS || w.SDRWhiteLevel == 0) return 1.f;
    return w.SDRWhiteLevel / 1000.f;   // 1000 = 80 nits
}

void seterr(char* err, int len, const char* m) { if (err && len > 0) snprintf(err, len, "%s", m); }
}

struct DesktopMirror::Impl {
    ID3D11Device* dev; ID3D11DeviceContext* ctx;
    int tw, th; DXGI_FORMAT tfmt;
    // capture (its own device, on the adapter that owns the output)
    ComPtr<ID3D11Device> capDev; ComPtr<ID3D11DeviceContext> capCtx; ComPtr<IDXGIOutputDuplication> dup; ComPtr<IDXGIOutput> output; ComPtr<IDXGIAdapter1> adapter;
    ComPtr<ID3D11Texture2D> staging;
    DXGI_FORMAT dfmt = DXGI_FORMAT_B8G8R8A8_UNORM; int dw = 0, dh = 0; bool hdr = false; float white = 1.f; bool have = false;
    // encoder side
    ComPtr<ID3D11Texture2D> src; ComPtr<ID3D11ShaderResourceView> srcSrv;
    ComPtr<ID3D11Texture2D> target; ComPtr<ID3D11RenderTargetView> rtv;
    ComPtr<ID3D11VertexShader> vs; ComPtr<ID3D11PixelShader> ps; ComPtr<ID3D11Buffer> cb; ComPtr<ID3D11SamplerState> smp; ComPtr<ID3D11RasterizerState> rs;

    bool OpenDuplication(char* err, int errlen) {
        dup.Reset();
        HRESULT hr1 = E_FAIL, hr2 = E_FAIL;
        // the thread has to be on the input desktop (the one the user is looking at); E_ACCESSDENIED otherwise
        if (HDESK d = OpenInputDesktop(0, FALSE, GENERIC_ALL)) { SetThreadDesktop(d); CloseDesktop(d); }
        ComPtr<IDXGIOutput5> o5;
        if (SUCCEEDED(output.As(&o5))) {
            const DXGI_FORMAT fmts[2] = { DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_B8G8R8A8_UNORM };
            hr1 = o5->DuplicateOutput1(capDev.Get(), 0, 2, fmts, &dup);
            if (SUCCEEDED(hr1)) return true;
        }
        ComPtr<IDXGIOutput1> o1;
        if (SUCCEEDED(output.As(&o1))) { hr2 = o1->DuplicateOutput(capDev.Get(), &dup); if (SUCCEEDED(hr2)) return true; }
        char b[200];
        snprintf(b, sizeof b, "desktop duplication is not available (0x%08x / 0x%08x: a locked or remote session, a protected screen, or a display on a GPU that cannot be duplicated)", (unsigned)hr1, (unsigned)hr2);
        seterr(err, errlen, b);
        return false;
    }

    bool Init(char* err, int errlen) {
        ComPtr<IDXGIFactory1> fac;
        if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&fac))) { seterr(err, errlen, "DXGI factory"); return false; }
        // the primary output: the one whose desktop rectangle starts at (0, 0)
        for (UINT a = 0; !output; a++) {
            ComPtr<IDXGIAdapter1> ad; if (fac->EnumAdapters1(a, &ad) == DXGI_ERROR_NOT_FOUND) break;
            for (UINT o = 0;; o++) {
                ComPtr<IDXGIOutput> out; if (ad->EnumOutputs(o, &out) == DXGI_ERROR_NOT_FOUND) break;
                DXGI_OUTPUT_DESC d; out->GetDesc(&d);
                if (d.AttachedToDesktop && d.DesktopCoordinates.left == 0 && d.DesktopCoordinates.top == 0) { output = out; adapter = ad; break; }
            }
        }
        if (!output) { seterr(err, errlen, "no desktop output found"); return false; }
        if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &capDev, nullptr, &capCtx))) { seterr(err, errlen, "capture device"); return false; }
        if (!OpenDuplication(err, errlen)) return false;
        DXGI_OUTDUPL_DESC dd; dup->GetDesc(&dd);
        dw = (int)dd.ModeDesc.Width; dh = (int)dd.ModeDesc.Height;
        hdr = dd.ModeDesc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
        dfmt = hdr ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_B8G8R8A8_UNORM;
        white = hdr ? SdrWhite() : 1.f;
        D3D11_TEXTURE2D_DESC td{}; td.Width = dw; td.Height = dh; td.MipLevels = 1; td.ArraySize = 1; td.Format = dfmt; td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_STAGING; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(capDev->CreateTexture2D(&td, nullptr, &staging))) { seterr(err, errlen, "staging texture"); return false; }
        return InitGpu(err, errlen);
    }

    bool testMode = false;
    bool InitGpu(char* err, int errlen) {
        D3D11_TEXTURE2D_DESC td{}; td.Width = dw; td.Height = dh; td.MipLevels = 1; td.ArraySize = 1; td.Format = dfmt; td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT; td.CPUAccessFlags = 0; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(dev->CreateTexture2D(&td, nullptr, &src)) || FAILED(dev->CreateShaderResourceView(src.Get(), nullptr, &srcSrv))) { seterr(err, errlen, "desktop texture"); return false; }
        D3D11_TEXTURE2D_DESC tt{}; tt.Width = tw; tt.Height = th; tt.MipLevels = 1; tt.ArraySize = 1; tt.Format = tfmt; tt.SampleDesc.Count = 1;
        tt.Usage = D3D11_USAGE_DEFAULT; tt.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(dev->CreateTexture2D(&tt, nullptr, &target)) || FAILED(dev->CreateRenderTargetView(target.Get(), nullptr, &rtv))) { seterr(err, errlen, "target texture"); return false; }
        ComPtr<ID3DBlob> vb, pb, e;
        if (FAILED(D3DCompile(kShader, strlen(kShader), "desktop", nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vb, &e)) ||
            FAILED(D3DCompile(kShader, strlen(kShader), "desktop", nullptr, nullptr, "PS", "ps_5_0", 0, 0, &pb, &e))) {
            seterr(err, errlen, e ? (const char*)e->GetBufferPointer() : "shader compile"); return false;
        }
        dev->CreateVertexShader(vb->GetBufferPointer(), vb->GetBufferSize(), nullptr, &vs);
        dev->CreatePixelShader(pb->GetBufferPointer(), pb->GetBufferSize(), nullptr, &ps);
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth = 80; bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        dev->CreateBuffer(&bd, nullptr, &cb);
        D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR; sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        dev->CreateSamplerState(&sd, &smp);
        D3D11_RASTERIZER_DESC rd{}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE;
        dev->CreateRasterizerState(&rd, &rs);
        return true;
    }

    // the newest desktop image into `src` (nothing to do when the screen did not change)
    void Capture() {
        if (testMode) return;
        if (!dup) { char e[8]; if (!OpenDuplication(e, 8)) return; }
        DXGI_OUTDUPL_FRAME_INFO info; ComPtr<IDXGIResource> res;
        HRESULT hr = dup->AcquireNextFrame(0, &info, &res);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return;
        if (hr == DXGI_ERROR_ACCESS_LOST) { dup.Reset(); return; }   // a mode change, the secure desktop, a display switch: reopened on the next call
        if (FAILED(hr)) return;
        if (info.LastPresentTime.QuadPart != 0) {
            ComPtr<ID3D11Texture2D> tex;
            if (SUCCEEDED(res.As(&tex))) {
                D3D11_TEXTURE2D_DESC d; tex->GetDesc(&d);
                if (d.Format != dfmt || (int)d.Width != dw || (int)d.Height != dh) {   // the surface is not what the duplication description said (HDR switched on, a mode change)
                    dfmt = d.Format; dw = (int)d.Width; dh = (int)d.Height; hdr = dfmt == DXGI_FORMAT_R16G16B16A16_FLOAT; white = hdr ? SdrWhite() : 1.f; have = false;
                    D3D11_TEXTURE2D_DESC sd{}; sd.Width = dw; sd.Height = dh; sd.MipLevels = 1; sd.ArraySize = 1; sd.Format = dfmt; sd.SampleDesc.Count = 1; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                    staging.Reset(); char e[8];
                    if (FAILED(capDev->CreateTexture2D(&sd, nullptr, &staging)) || !InitGpu(e, 8)) { dup->ReleaseFrame(); dup.Reset(); return; }
                }
                capCtx->CopyResource(staging.Get(), tex.Get());
                D3D11_MAPPED_SUBRESOURCE m;
                if (SUCCEEDED(capCtx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &m))) {
                    ctx->UpdateSubresource(src.Get(), 0, nullptr, m.pData, m.RowPitch, 0);
                    capCtx->Unmap(staging.Get(), 0);
                    have = true;
                }
            }
        }
        dup->ReleaseFrame();
    }

    ID3D11Texture2D* Render(const float ndc[16], const float cursor[10], uint32_t clearRgb, char* err, int errlen) {
        Capture();
        const float clear[4] = { ((clearRgb >> 16) & 0xFF) / 255.f, ((clearRgb >> 8) & 0xFF) / 255.f, (clearRgb & 0xFF) / 255.f, 1.f };
        ctx->ClearRenderTargetView(rtv.Get(), clear);
        if (!have) return target.Get();   // nothing captured yet: the plain colour
        ctx->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
        ctx->RSSetState(rs.Get());
        ctx->VSSetShader(vs.Get(), nullptr, 0); ctx->PSSetShader(ps.Get(), nullptr, 0);
        ctx->PSSetShaderResources(0, 1, srcSrv.GetAddressOf()); ctx->PSSetSamplers(0, 1, smp.GetAddressOf());
        ctx->VSSetConstantBuffers(0, 1, cb.GetAddressOf()); ctx->PSSetConstantBuffers(0, 1, cb.GetAddressOf());
        ctx->IASetInputLayout(nullptr); ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        for (int eye = 0; eye < 2; eye++) {
            const float* c = ndc + eye * 8;
            if (std::isnan(c[0])) continue;
            D3D11_MAPPED_SUBRESOURCE m;
            if (FAILED(ctx->Map(cb.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) { seterr(err, errlen, "constant buffer"); return nullptr; }
            float* f = (float*)m.pData;
            memcpy(f, c, 8 * sizeof(float));
            f[8] = cursor[0]; f[9] = cursor[1]; f[10] = (float)dw; f[11] = (float)dh;
            f[12] = hdr ? 1.f : 0.f; f[13] = white;
            const float* lz = cursor + 2 + eye * 4;   // the beam of this eye (NaN: none)
            f[14] = std::isnan(lz[0]) ? 0.f : 1.f; f[15] = (float)th;
            memcpy(f + 16, lz, 4 * sizeof(float)); if (f[14] == 0.f) f[16] = f[17] = f[18] = f[19] = 0.f;
            ctx->Unmap(cb.Get(), 0);
            D3D11_VIEWPORT vp{ eye * tw / 2.f, 0.f, tw / 2.f, (float)th, 0.f, 1.f };
            ctx->RSSetViewports(1, &vp);
            ctx->Draw(4, 0);
        }
        ID3D11ShaderResourceView* none = nullptr; ctx->PSSetShaderResources(0, 1, &none);
        return target.Get();
    }
};

DesktopMirror::DesktopMirror(ID3D11Device* dev, ID3D11DeviceContext* ctx, int w, int h, DXGI_FORMAT fmt) : I(new Impl) { I->dev = dev; I->ctx = ctx; I->tw = w; I->th = h; I->tfmt = fmt; }
DesktopMirror::~DesktopMirror() = default;
bool DesktopMirror::Init(char* err, int errlen) { return I->Init(err, errlen); }
bool DesktopMirror::InitForTest(int w, int h, bool hdr, float sdrWhite, char* err, int errlen) {
    I->testMode = true; I->dw = w; I->dh = h; I->hdr = hdr; I->white = sdrWhite;
    I->dfmt = hdr ? DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_B8G8R8A8_UNORM;
    return I->InitGpu(err, errlen);
}
void DesktopMirror::SetTestImage(const void* pixels) {
    I->ctx->UpdateSubresource(I->src.Get(), 0, nullptr, pixels, I->dw * (I->hdr ? 8 : 4), 0);
    I->have = true;
}
int DesktopMirror::DesktopW() const { return I->dw; }
int DesktopMirror::DesktopH() const { return I->dh; }
bool DesktopMirror::Hdr() const { return I->hdr; }
ID3D11Texture2D* DesktopMirror::Render(const float ndc[16], const float cursor[10], uint32_t clearRgb, char* err, int errlen) { return I->Render(ndc, cursor, clearRgb, err, errlen); }
