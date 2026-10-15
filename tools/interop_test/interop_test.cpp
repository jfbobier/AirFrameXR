// End-to-end check of the fixed shared-copy path: D3D12 source (UE-like flags, NON_PIXEL_SHADER_RESOURCE) -> shared RT|SIMULTANEOUS copy -> D3D11 readback.
#include <windows.h>
#include <d3d12.h>
#include <d3d11_1.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <cstdio>
#include <string>
#include <vector>
using Microsoft::WRL::ComPtr;
#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
static ComPtr<ID3D12InfoQueue> g_iq;
static void Drain(const char* tag) {
    if (!g_iq) return;
    UINT64 n = g_iq->GetNumStoredMessages();
    for (UINT64 i = 0; i < n; i++) {
        SIZE_T len = 0; g_iq->GetMessage(i, nullptr, &len);
        std::string buf(len, 0); D3D12_MESSAGE* m = (D3D12_MESSAGE*)buf.data();
        if (SUCCEEDED(g_iq->GetMessage(i, m, &len)) && m->Severity <= D3D12_MESSAGE_SEVERITY_WARNING) printf("    [%s] d3d12: %s\n", tag, m->pDescription);
    }
    g_iq->ClearStoredMessages();
}
struct Ctx { ComPtr<ID3D12Device> dev; ComPtr<ID3D12CommandQueue> q; ComPtr<ID3D12CommandAllocator> alloc; ComPtr<ID3D12GraphicsCommandList> cmd; ComPtr<ID3D12Fence> fence; HANDLE ev; UINT64 fv = 0;
    ComPtr<ID3D11Device> d11; ComPtr<ID3D11Device1> d11_1; ComPtr<ID3D11DeviceContext> ctx; };
static void Exec(Ctx& c) { c.cmd->Close(); ID3D12CommandList* l[] = { c.cmd.Get() }; c.q->ExecuteCommandLists(1, l); c.fv++; c.q->Signal(c.fence.Get(), c.fv); c.fence->SetEventOnCompletion(c.fv, c.ev); WaitForSingleObject(c.ev, 5000); c.alloc->Reset(); c.cmd->Reset(c.alloc.Get(), nullptr); }
// source: a D3D12 texture filled by upload, left in `state` (what the game leaves the DLSS inputs in)
static ComPtr<ID3D12Resource> MakeSource(Ctx& c, DXGI_FORMAT fmt, UINT w, UINT h, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state, const std::vector<uint8_t>& bytes, UINT bpp, DXGI_FORMAT uploadFmt) {
    D3D12_RESOURCE_DESC d{}; d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; d.Width = w; d.Height = h; d.DepthOrArraySize = 1; d.MipLevels = 1; d.Format = fmt; d.SampleDesc.Count = 1; d.Flags = flags;
    D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    ComPtr<ID3D12Resource> r;
    D3D12_CLEAR_VALUE cv{}; cv.Format = (flags & D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL) ? uploadFmt : fmt; cv.DepthStencil.Depth = 1.0f;
    HRESULT hr = c.dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &d, D3D12_RESOURCE_STATE_COPY_DEST, (flags & D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL) ? &cv : nullptr, IID_PPV_ARGS(&r));
    printf("  source create 0x%08x\n", hr); Drain("src"); if (FAILED(hr)) return nullptr;
    // upload plane 0
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp{}; UINT rows; UINT64 rowBytes, total;
    c.dev->GetCopyableFootprints(&d, 0, 1, 0, &fp, &rows, &rowBytes, &total);
    D3D12_RESOURCE_DESC bd{}; bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; bd.Width = total; bd.Height = 1; bd.DepthOrArraySize = 1; bd.MipLevels = 1; bd.SampleDesc.Count = 1; bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    D3D12_HEAP_PROPERTIES up{}; up.Type = D3D12_HEAP_TYPE_UPLOAD;
    ComPtr<ID3D12Resource> ub; c.dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&ub));
    uint8_t* p = nullptr; ub->Map(0, nullptr, (void**)&p);
    for (UINT y = 0; y < h; y++) memcpy(p + fp.Offset + y * fp.Footprint.RowPitch, bytes.data() + (size_t)y * w * bpp, (size_t)w * bpp);
    ub->Unmap(0, nullptr);
    D3D12_TEXTURE_COPY_LOCATION dst{}; dst.pResource = r.Get(); dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dst.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION src{}; src.pResource = ub.Get(); src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; src.PlacedFootprint = fp;
    c.cmd->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
    D3D12_RESOURCE_BARRIER b{}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; b.Transition.pResource = r.Get(); b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES; b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST; b.Transition.StateAfter = state;
    c.cmd->ResourceBarrier(1, &b);
    Exec(c); Drain("upload");
    return r;
}
// the shim's fixed path
static bool SharedCopy(Ctx& c, ID3D12Resource* src, D3D12_RESOURCE_STATES srcState, DXGI_FORMAT dstFmt, bool planar, const std::vector<uint8_t>& expect, UINT bpp, const char* name) {
    printf("== %s\n", name);
    D3D12_RESOURCE_DESC d = src->GetDesc();
    D3D12_RESOURCE_DESC nd = d; nd.Alignment = 0; nd.MipLevels = 1; nd.DepthOrArraySize = 1; nd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN; nd.Format = dstFmt;
    nd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
    D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    ComPtr<ID3D12Resource> r; HRESULT hr = c.dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_SHARED, &nd, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&r));
    printf("  CreateCommittedResource 0x%08x\n", hr); Drain("create"); if (FAILED(hr)) return false;
    HANDLE h = nullptr; hr = c.dev->CreateSharedHandle(r.Get(), nullptr, GENERIC_ALL, nullptr, &h); printf("  CreateSharedHandle 0x%08x\n", hr); if (FAILED(hr)) return false;
    ComPtr<ID3D11Texture2D> view; hr = c.d11_1->OpenSharedResource1(h, IID_PPV_ARGS(&view)); CloseHandle(h); printf("  OpenSharedResource1 0x%08x\n", hr); if (FAILED(hr)) return false;
    // the copy on the game's list, as the tap records it
    D3D12_RESOURCE_BARRIER b{}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; b.Transition.pResource = src; b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES; b.Transition.StateBefore = srcState; b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    c.cmd->ResourceBarrier(1, &b);
    if (planar) {
        D3D12_TEXTURE_COPY_LOCATION dst{}; dst.pResource = r.Get(); dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; dst.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION s{}; s.pResource = src; s.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; s.SubresourceIndex = 0;   // plane 0 = depth
        c.cmd->CopyTextureRegion(&dst, 0, 0, 0, &s, nullptr);
    } else {
        c.cmd->CopyResource(r.Get(), src);
    }
    std::swap(b.Transition.StateBefore, b.Transition.StateAfter); c.cmd->ResourceBarrier(1, &b);
    Exec(c); Drain("copy");
    // D3D11 side: staging readback (the host does the same through its own device)
    D3D11_TEXTURE2D_DESC vd; view->GetDesc(&vd); printf("  d3d11 view %ux%u fmt %u bind 0x%x misc 0x%x\n", vd.Width, vd.Height, vd.Format, vd.BindFlags, vd.MiscFlags);
    D3D11_TEXTURE2D_DESC sd = vd; sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.MiscFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> st; hr = c.d11->CreateTexture2D(&sd, nullptr, &st); if (FAILED(hr)) { printf("  staging 0x%08x\n", hr); return false; }
    c.ctx->CopyResource(st.Get(), view.Get()); c.ctx->Flush();
    D3D11_MAPPED_SUBRESOURCE m{}; hr = c.ctx->Map(st.Get(), 0, D3D11_MAP_READ, 0, &m); if (FAILED(hr)) { printf("  map 0x%08x\n", hr); return false; }
    size_t bad = 0; for (UINT y = 0; y < vd.Height; y++) if (memcmp((uint8_t*)m.pData + y * m.RowPitch, expect.data() + (size_t)y * vd.Width * bpp, (size_t)vd.Width * bpp)) bad++;
    c.ctx->Unmap(st.Get(), 0);
    printf("  readback: %zu of %u rows differ -> %s\n", bad, vd.Height, bad ? "MISMATCH" : "BIT-EXACT");
    return bad == 0;
}
int main(int argc, char** argv) {
    bool debug = argc > 1 && std::string(argv[1]) == "debug";
    ComPtr<ID3D12Debug> dbg; if (debug && SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&dbg)))) { dbg->EnableDebugLayer(); printf("d3d12 debug layer on\n"); }
    Ctx c; ComPtr<IDXGIFactory4> f; CreateDXGIFactory1(IID_PPV_ARGS(&f)); ComPtr<IDXGIAdapter1> ad; f->EnumAdapters1(0, &ad);
    D3D12CreateDevice(ad.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&c.dev)); c.dev.As(&g_iq);
    D3D12_COMMAND_QUEUE_DESC qd{}; c.dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&c.q)); c.dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&c.alloc));
    c.dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, c.alloc.Get(), nullptr, IID_PPV_ARGS(&c.cmd)); c.dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&c.fence)); c.ev = CreateEvent(nullptr, FALSE, FALSE, nullptr);
    D3D_FEATURE_LEVEL fl; D3D11CreateDevice(ad.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &c.d11, &fl, &c.ctx); c.d11.As(&c.d11_1);
    const UINT w = 2560, h = 1440; std::vector<uint8_t> mv((size_t)w * h * 4); for (size_t i = 0; i < mv.size(); i++) mv[i] = (uint8_t)(i * 2654435761u >> 13);
    // 1. vectors: UE-style R16G16_FLOAT RT|UAV, left in NON_PIXEL_SHADER_RESOURCE
    auto s1 = MakeSource(c, DXGI_FORMAT_R16G16_FLOAT, w, h, D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, mv, 4, DXGI_FORMAT_R16G16_FLOAT);
    if (s1) SharedCopy(c, s1.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, DXGI_FORMAT_R16G16_FLOAT, false, mv, 4, "vectors R16G16_FLOAT -> shared RT|SIMULTANEOUS R16G16_FLOAT, CopyResource");
    // 2. depth D32_FLOAT (DS flag) -> R32_FLOAT shared, CopyResource
    const UINT dw = 1708, dh = 960; std::vector<uint8_t> dp((size_t)dw * dh * 4); for (size_t i = 0; i < dp.size(); i += 4) { float v = (float)(i % 977) / 977.f; memcpy(&dp[i], &v, 4); }
    auto s2 = MakeSource(c, DXGI_FORMAT_R32_TYPELESS, dw, dh, D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, dp, 4, DXGI_FORMAT_D32_FLOAT);
    if (s2) SharedCopy(c, s2.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, DXGI_FORMAT_R32_FLOAT, false, dp, 4, "depth R32_TYPELESS(DS) -> shared R32_FLOAT, CopyResource");
    // 3. depth R32G8X24_TYPELESS (D32_FLOAT_S8X24, UE's default) plane 0 -> R32_FLOAT shared, CopyTextureRegion
    auto s3 = MakeSource(c, DXGI_FORMAT_R32G8X24_TYPELESS, dw, dh, D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, dp, 4, DXGI_FORMAT_D32_FLOAT_S8X24_UINT);
    if (s3) SharedCopy(c, s3.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, DXGI_FORMAT_R32_FLOAT, true, dp, 4, "depth R32G8X24_TYPELESS(DS) plane 0 -> shared R32_FLOAT, CopyTextureRegion");
    // 4. same planar depth -> shared R32_FLOAT_X8X24? not RT-able; try R32G8X24 dst without RT flag is known to fail to open. Try R32_TYPELESS dst with RT flag:
    if (s3) SharedCopy(c, s3.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, DXGI_FORMAT_R32_TYPELESS, true, dp, 4, "depth R32G8X24_TYPELESS(DS) plane 0 -> shared R32_TYPELESS RT, CopyTextureRegion");
    return 0;
}
