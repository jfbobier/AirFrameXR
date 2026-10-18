// Consumer of the game's DLSS inputs (docs/MOTION_VECTORS.md). The OptiScaler-DLSSMotionVectorExtractor fork (github.com/jfbobier/OptiScaler-DLSSMotionVectorExtractor,
// a separate GPL-3.0 project loaded by the game as its DLSS proxy) exports a small C ABI; this file is its MIT-side counterpart, declaring the
// same ABI and consuming the publications: at every DLSS evaluate, inside the call and on the game's own command list / context, the shim copies
// the motion vectors and depth into shared textures of its own; at SubmitFrame the newest copy of each eye is copied once more into the frame's
// ring slot companions (ipc.h MotionSlot) so the host reads vectors and picture together, synchronised by the slot's existing GPU-done event.
//
// Nothing here changes what the game or DLSS does: no allocation on the game's resources, no state kept past the call besides our copies.
// D3D12 games: the copies are D3D12 committed resources created on the game's device with D3D12_HEAP_FLAG_SHARED and opened on the shim's D3D11
// submission device (same process: the NT handle is used directly). D3D11 games: D3D11 shared textures on the game's device, opened on the
// submission device when it differs. The ordering D3D12 copy -> D3D11 read relies on what already orders the picture: VDXR waits for the
// game's queue before composing the frame the shim receives, and our copy was recorded before that frame's DLSS pass.
#pragma once
#include <d3d11_1.h>
#include <d3d12.h>
#ifndef PSAPI_VERSION
#define PSAPI_VERSION 2   // EnumProcessModules from kernel32 (Windows 7+), no psapi.lib
#endif
#include <psapi.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <vector>
#include "ipc.h"
#include "ipc_win.h"

namespace ngxtap {

using Microsoft::WRL::ComPtr;

// ---- the provider ABI (OptiScaler-DLSSMotionVectorExtractor OptiScaler/visionalvr/VaProvider.h, version 1) -------------------------------------------
constexpr uint32_t kProviderVersion = 1;
struct PublicationV1 {
    uint32_t struct_size;
    uint32_t version;
    uint32_t api;            // 11: cmd = ID3D11DeviceContext*, resources ID3D11Resource*; 12: ID3D12GraphicsCommandList*, ID3D12Resource*
    uint32_t stream;         // DLSS feature handle id (one per eye in VR titles)
    uint64_t frame;          // evaluate counter of the stream
    void* cmd;
    void* color;
    void* motion_vectors;
    void* depth;
    void* output;
    void* exposure;
    uint32_t render_w, render_h;
    uint32_t output_w, output_h;
    uint32_t color_x, color_y;
    uint32_t mv_x, mv_y, depth_x, depth_y;
    float jitter_x, jitter_y;
    float mv_scale_x, mv_scale_y;
    int32_t reset;
    uint32_t flags;          // NVSDK_NGX_DLSS_Feature_Flags
    void* params;            // NVSDK_NGX_Parameter*, valid during the call only
};
typedef void (*ConsumerV1)(const PublicationV1* publication, void* user);
typedef uint32_t (*ProviderVersionFn)();
typedef int (*SetConsumerV1Fn)(ConsumerV1 consumer, void* user);

constexpr int kRing = 3;          // copies per stream: the game may evaluate the next frame before the shim read the previous copy
constexpr double kStreamIdleS = 2.0;
constexpr double kRetireS = 3.0;      // how long a retired shared copy is kept alive after it was replaced or its stream dropped

// The sharable twin of a texture format: depth formats cannot be shared or copied across devices, their typeless family can.
// The format of the shared copy. D3D11 opens a D3D12 shared texture only when it was created with the render-target flag (5090, 2026-10-16:
// every copy failed E_INVALIDARG with simultaneous access alone), and a render target cannot be typeless-depth or depth-stencil: depth is copied
// into a typed colour twin (D32 -> R32_FLOAT, D16 -> R16_UNORM; the packed depth-stencil formats as their depth plane on D3D12, not copied on D3D11,
// where a cross-family copy is not allowed). UNKNOWN = not copied.
inline DXGI_FORMAT SharedFormat(DXGI_FORMAT f, bool api12) {
    switch (f) {
    case DXGI_FORMAT_D32_FLOAT: case DXGI_FORMAT_R32_FLOAT: case DXGI_FORMAT_R32_TYPELESS: return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: case DXGI_FORMAT_R32G8X24_TYPELESS: case DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS:
        return api12 ? DXGI_FORMAT_R32_FLOAT : DXGI_FORMAT_UNKNOWN;
    case DXGI_FORMAT_D24_UNORM_S8_UINT: case DXGI_FORMAT_R24G8_TYPELESS: case DXGI_FORMAT_R24_UNORM_X8_TYPELESS: return DXGI_FORMAT_UNKNOWN;
    case DXGI_FORMAT_D16_UNORM: case DXGI_FORMAT_R16_TYPELESS: case DXGI_FORMAT_R16_UNORM: return DXGI_FORMAT_R16_UNORM;
    case DXGI_FORMAT_R16G16_TYPELESS: return DXGI_FORMAT_R16G16_FLOAT;
    case DXGI_FORMAT_R32G32_TYPELESS: return DXGI_FORMAT_R32G32_FLOAT;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default: return f;
    }
}

class NgxTap {
public:
    typedef void (*LogFn)(const char* line);

    void Init(ID3D11Device* submissionDevice, LogFn log) {
        std::lock_guard<std::mutex> g(m_mutex);
        m_subDev = submissionDevice;
        m_subDev1 = nullptr;
        if (m_subDev) {
            m_subDev->QueryInterface(__uuidof(ID3D11Device1), (void**)m_subDev1.ReleaseAndGetAddressOf());
        }
        m_log = log;
        char v[32];
        if (GetEnvironmentVariableA("OVRSHIM_MV_STATE", v, sizeof(v)) > 0) {
            m_srcState = (D3D12_RESOURCE_STATES)atoi(v);   // the state the game leaves the vectors in (override, like OptiScaler's MVResourceBarrier)
        }
        m_off = GetEnvironmentVariableA("OVRSHIM_MV_OFF", v, sizeof(v)) > 0 && atoi(v) != 0;
        m_depth = GetEnvironmentVariableA("OVRSHIM_MV_DEPTH", v, sizeof(v)) > 0 && atoi(v) != 0;   // the generator does not read depth yet: off by default
    }

    // Finds the provider among the loaded modules (by its export) and registers the consumer. Cheap when there is none; call until it returns true.
    bool TryAttach() {
        if (m_attached || m_off) {
            return m_attached;
        }
        HMODULE mods[1024];
        DWORD needed = 0;
        if (!EnumProcessModules(GetCurrentProcess(), mods, sizeof(mods), &needed)) {
            return false;
        }
        const size_t n = std::min<size_t>(needed / sizeof(HMODULE), 1024);
        for (size_t i = 0; i < n; i++) {
            auto set = (SetConsumerV1Fn)GetProcAddress(mods[i], "VisionALVR_SetConsumerV1");
            if (!set) {
                continue;
            }
            char path[MAX_PATH] = "";
            GetModuleFileNameA(mods[i], path, MAX_PATH);
            auto ver = (ProviderVersionFn)GetProcAddress(mods[i], "VisionALVR_ProviderVersion");
            const uint32_t v = ver ? ver() : 0;
            if (v != kProviderVersion) {
                m_state = 2;
                Log("motion vectors: provider %s speaks ABI %u, this shim %u: not used", path, v, kProviderVersion);
                return false;
            }
            if (set(&NgxTap::Thunk, this) != 1) {
                m_state = 3;
                Log("motion vectors: provider %s refused the consumer", path);
                return false;
            }
            m_set = set;
            m_attached = true;
            m_state = 1;
            Log("motion vectors: attached to %s (ABI %u), depth copies %s", path, v, m_depth ? "on" : "off");
            return true;
        }
        return false;
    }

    void Detach() {
        if (m_attached && m_set) {
            m_set(nullptr, nullptr);
        }
        m_attached = false;
    }

    bool Attached() const { return m_attached; }
    uint32_t State() const { return m_state; }
    uint64_t Publications() const { return m_publications; }
    uint64_t CopyFailures() const { return m_copyFailures; }
    void SetEnabled(bool on) { m_enabled = on; }

    // After the frame is composed into `slot` (same context, before its GPU-done event): copies the newest vectors of each eye into the slot's
    // companion textures and fills the block to publish with the frame. Streams idle for two seconds are forgotten.
    visionalvr_ipc::MotionSlot Harvest(ID3D11DeviceContext* ctx, uint32_t slot, double nowS) {
        visionalvr_ipc::MotionSlot out{};
        if (!m_attached || !m_enabled || slot >= (uint32_t)visionalvr_ipc::kSlots) {
            return out;
        }
        std::lock_guard<std::mutex> g(m_mutex);
        m_harvestSeq++;
        // drop idle streams (a level change recreates the DLSS features with new ids)
        for (size_t i = 0; i < m_streams.size();) {
            if (nowS - m_streams[i].lastS > kStreamIdleS) {
                for (int r = 0; r < kRing; r++) {
                    Retire(m_streams[i].mv[r]);
                    Retire(m_streams[i].depth[r]);
                }
                m_streams.erase(m_streams.begin() + i);
            } else {
                i++;
            }
        }
        // retired copies are freed by GpuDone (the GPU completion of the frame after their last use); the age limit is the fallback
        for (size_t i = 0; i < m_retired.size();) {
            if (nowS - m_retired[i].atS > kRetireS) {
                m_retired.erase(m_retired.begin() + i);
            } else {
                i++;
            }
        }
        // eye order = order of first appearance (Unreal evaluates the left view first)
        std::vector<Stream*> order;
        for (auto& s : m_streams) {
            order.push_back(&s);
        }
        std::sort(order.begin(), order.end(), [](const Stream* a, const Stream* b) { return a->firstSeen < b->firstSeen; });
        out.streams = (uint32_t)order.size();
        for (int e = 0; e < 2 && e < (int)order.size(); e++) {
            Stream& s = *order[e];
            if (s.ring < 0) {
                continue;
            }
            visionalvr_ipc::MotionEye info = s.info;
            Copy& mv = s.mv[s.ring];
            Copy& dp = s.depth[s.ring];
            info.mvHandle = CopyToSlot(ctx, m_slotTex[slot][e][0], mv, info.mvFormat, info.mvWidth, info.mvHeight);
            info.depthHandle = CopyToSlot(ctx, m_slotTex[slot][e][1], dp, info.depthFormat, info.depthWidth, info.depthHeight);
            info.valid = info.mvHandle ? 1u : 0u;
            out.eye[e] = info;
        }
        return out;
    }

    /// The harvest number of the frame being composed (PendingFrame::tapSeq): hand it back in GpuDone when that frame's GPU work completed.
    uint64_t HarvestSeq() const { return m_harvestSeq; }

    /// The GPU completed the frame harvested as `seq` (the shim's per-slot GPU-done event, after the runtime's fence wait for a D3D12 game's
    /// work): every copy retired before that harvest is no longer referenced by any command list or context and is freed here.
    void GpuDone(uint64_t seq) {
        std::lock_guard<std::mutex> g(m_mutex);
        for (size_t i = 0; i < m_retired.size();) {
            if (m_retired[i].seq < seq) {
                m_retired.erase(m_retired.begin() + i);
            } else {
                i++;
            }
        }
    }

    uint32_t ActiveStreams() const {
        std::lock_guard<std::mutex> g(m_mutex);
        return (uint32_t)m_streams.size();
    }

private:
    struct Copy {
        ComPtr<ID3D12Resource> r12;      // D3D12 games: the shared copy on the game's device
        ComPtr<ID3D11Texture2D> t11;     // D3D11 games: the shared copy on the game's device
        ComPtr<ID3D11Texture2D> view;    // the copy as seen by the submission device
        uint32_t w = 0, h = 0;
        DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN;
        bool failed = false;             // creation or sharing failed for this source (logged once)
    };
    struct Stream {
        uint32_t id = 0;
        uint64_t firstSeen = 0;
        double lastS = 0;
        int ring = -1;
        Copy mv[kRing], depth[kRing];
        visionalvr_ipc::MotionEye info{};
    };
    // a copy taken out of service: its D3D12 resource may still be referenced by a game command list not yet executed, its D3D11 view by a
    // queued copy on the submission context; neither API defers the other's destruction, so retired copies live on for kRetireS (the shim's
    // submission device died with an SM page fault seconds after copies were replaced or streams dropped, 2026-10-16)
    struct Retired {
        ComPtr<ID3D12Resource> r12;
        ComPtr<ID3D11Texture2D> t11, view;
        double atS = 0;
        uint64_t seq = 0;   // the harvest number current when it was retired: the GPU completion of harvest seq + 1 frees it (GpuDone)
    };
    void Retire(Copy& c) {
        if (c.r12 || c.t11 || c.view) {
            Retired r;
            r.r12 = c.r12;
            r.t11 = c.t11;
            r.view = c.view;
            r.atS = visionalvr_ipc::QpcSeconds();
            r.seq = m_harvestSeq;
            m_retired.push_back(r);
        }
        c.r12.Reset();
        c.t11.Reset();
        c.view.Reset();
    }
    struct SlotTex {
        ComPtr<ID3D11Texture2D> tex;
        uint64_t handle = 0;
        uint32_t w = 0, h = 0;
        DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN;
    };

    static void Thunk(const PublicationV1* p, void* user) {
        if (user) {
            ((NgxTap*)user)->OnPublication(p);
        }
    }

    void OnPublication(const PublicationV1* p) {
        if (!p || p->struct_size < sizeof(PublicationV1) || p->version != kProviderVersion || !m_enabled || !p->cmd || !p->motion_vectors) {
            return;
        }
        if (p->api != 11 && p->api != 12) {
            return;
        }
        std::lock_guard<std::mutex> g(m_mutex);
        m_publications++;
        Stream* s = nullptr;
        for (auto& x : m_streams) {
            if (x.id == p->stream) {
                s = &x;
            }
        }
        if (!s) {
            if (m_streams.size() >= 8) {
                return;
            }
            m_streams.emplace_back();
            s = &m_streams.back();
            s->id = p->stream;
            s->firstSeen = m_publications;
            Log("motion vectors: stream %u (%s) render %ux%u output %ux%u mv %s depth %s flags 0x%x scale %.2f %.2f", p->stream, p->api == 12 ? "dx12" : "dx11",
                p->render_w, p->render_h, p->output_w, p->output_h, p->motion_vectors ? "yes" : "no", p->depth ? "yes" : "no", p->flags, p->mv_scale_x, p->mv_scale_y);
        }
        s->lastS = visionalvr_ipc::QpcSeconds();   // seen, copied or not: a stream whose copies fail stays known (and logged once) instead of being dropped and recreated
        const int ring = (s->ring + 1) % kRing;
        bool ok;
        if (p->api == 12) {
            auto* cmd = (ID3D12GraphicsCommandList*)p->cmd;
            ok = Copy12(cmd, s->mv[ring], (ID3D12Resource*)p->motion_vectors, "vectors");
            if (p->depth && m_depth) {
                Copy12(cmd, s->depth[ring], (ID3D12Resource*)p->depth, "depth");
            }
        } else {
            auto* ctx = (ID3D11DeviceContext*)p->cmd;
            ok = Copy11(ctx, s->mv[ring], (ID3D11Resource*)p->motion_vectors, "vectors");
            if (p->depth && m_depth) {
                Copy11(ctx, s->depth[ring], (ID3D11Resource*)p->depth, "depth");
            }
        }
        if (!ok) {
            return;
        }
        s->ring = ring;
        visionalvr_ipc::MotionEye& i = s->info;
        i = {};
        i.api = p->api;
        i.stream = p->stream;
        i.flags = p->flags;
        i.frame = p->frame;
        i.mvFormat = (uint32_t)s->mv[ring].fmt;
        i.mvWidth = s->mv[ring].w;
        i.mvHeight = s->mv[ring].h;
        if (p->depth && s->depth[ring].view) {
            i.depthFormat = (uint32_t)s->depth[ring].fmt;
            i.depthWidth = s->depth[ring].w;
            i.depthHeight = s->depth[ring].h;
        }
        i.renderW = p->render_w; i.renderH = p->render_h; i.outputW = p->output_w; i.outputH = p->output_h;
        i.mvX = p->mv_x; i.mvY = p->mv_y; i.depthX = p->depth_x; i.depthY = p->depth_y;
        i.jitterX = p->jitter_x; i.jitterY = p->jitter_y; i.mvScaleX = p->mv_scale_x; i.mvScaleY = p->mv_scale_y;
        i.reset = p->reset;
    }

    // D3D12: a shared committed copy on the game's device, opened on the submission device; the copy recorded on the game's command list.
    bool Copy12(ID3D12GraphicsCommandList* cmd, Copy& c, ID3D12Resource* src, const char* what) {
        if (c.failed) {
            return false;
        }
        const D3D12_RESOURCE_DESC d = src->GetDesc();
        const DXGI_FORMAT sf = SharedFormat(d.Format, true);
        if (!c.r12 || c.w != d.Width || c.h != d.Height || c.fmt != sf) {
            Retire(c);
            if (d.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D || d.SampleDesc.Count != 1 || !m_subDev1 || sf == DXGI_FORMAT_UNKNOWN) {
                c.failed = true;
                m_copyFailures++;
                Log("motion vectors: %s %ux%u format %u not copied: dimension %d, samples %u, device1 %d, shareable %d", what, (unsigned)d.Width, d.Height, (unsigned)d.Format,
                    (int)d.Dimension, d.SampleDesc.Count, m_subDev1 ? 1 : 0, sf != DXGI_FORMAT_UNKNOWN);
                return false;
            }
            ComPtr<ID3D12Device> dev;
            if (FAILED(src->GetDevice(__uuidof(ID3D12Device), (void**)dev.ReleaseAndGetAddressOf()))) {
                c.failed = true;
                return false;
            }
            D3D12_RESOURCE_DESC nd = d;
            nd.Alignment = 0;
            nd.MipLevels = 1;
            nd.DepthOrArraySize = 1;
            nd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
            nd.Format = sf;
            // render target + simultaneous access: the pair D3D11 accepts on OpenSharedResource1 (bit-exact round trip verified on the 5090 for
            // R16G16_FLOAT vectors, D32 depth and the depth plane of D32_S8X24, tools/interop_test 2026-10-16)
            nd.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
            D3D12_HEAP_PROPERTIES hp{};
            hp.Type = D3D12_HEAP_TYPE_DEFAULT;
            const char* step = "CreateCommittedResource";
            HRESULT hr = dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_SHARED, &nd, D3D12_RESOURCE_STATE_COMMON, nullptr, __uuidof(ID3D12Resource),
                                                      (void**)c.r12.ReleaseAndGetAddressOf());
            HANDLE h = nullptr;
            if (SUCCEEDED(hr)) {
                step = "CreateSharedHandle";
                hr = dev->CreateSharedHandle(c.r12.Get(), nullptr, GENERIC_ALL, nullptr, &h);
            }
            if (SUCCEEDED(hr)) {
                step = "OpenSharedResource1";
                hr = m_subDev1->OpenSharedResource1(h, __uuidof(ID3D11Texture2D), (void**)c.view.ReleaseAndGetAddressOf());
                CloseHandle(h);
            }
            if (FAILED(hr)) {
                c.failed = true;
                m_copyFailures++;
                c.r12.Reset();
                c.view.Reset();
                Log("motion vectors: %s %ux%u format %u: shared copy failed at %s 0x%08x", what, (unsigned)d.Width, d.Height, (unsigned)d.Format, step, (unsigned)hr);
                return false;
            }
            c.w = (uint32_t)d.Width;
            c.h = d.Height;
            c.fmt = nd.Format;
            Log("motion vectors: %s %ux%u format %u -> shared copy (format %u)", what, c.w, c.h, (unsigned)d.Format, (unsigned)c.fmt);
        }
        D3D12_RESOURCE_BARRIER b{};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = src;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = m_srcState;
        b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
        if (m_srcState != D3D12_RESOURCE_STATE_COPY_SOURCE) {
            cmd->ResourceBarrier(1, &b);
        }
        D3D12_TEXTURE_COPY_LOCATION dst{};
        dst.pResource = c.r12.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION from{};
        from.pResource = src;
        from.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        from.SubresourceIndex = 0;
        cmd->CopyTextureRegion(&dst, 0, 0, 0, &from, nullptr);
        if (m_srcState != D3D12_RESOURCE_STATE_COPY_SOURCE) {
            std::swap(b.Transition.StateBefore, b.Transition.StateAfter);
            cmd->ResourceBarrier(1, &b);
        }
        return true;
    }

    // D3D11: a shared copy on the game's device (the submission device itself when they are the same), the copy on the game's context.
    bool Copy11(ID3D11DeviceContext* ctx, Copy& c, ID3D11Resource* src, const char* what) {
        if (c.failed) {
            return false;
        }
        ComPtr<ID3D11Texture2D> tex;
        if (FAILED(src->QueryInterface(__uuidof(ID3D11Texture2D), (void**)tex.ReleaseAndGetAddressOf()))) {
            c.failed = true;
            return false;
        }
        D3D11_TEXTURE2D_DESC d{};
        tex->GetDesc(&d);
        const DXGI_FORMAT sf = SharedFormat(d.Format, false);
        if (!c.t11 || c.w != d.Width || c.h != d.Height || c.fmt != sf) {
            Retire(c);
            ComPtr<ID3D11Device> dev;
            ctx->GetDevice(dev.ReleaseAndGetAddressOf());
            if (!dev || d.SampleDesc.Count != 1 || sf == DXGI_FORMAT_UNKNOWN) {
                c.failed = true;
                m_copyFailures++;
                Log("motion vectors: %s %ux%u format %u not copied: samples %u, shareable %d", what, d.Width, d.Height, (unsigned)d.Format, d.SampleDesc.Count, sf != DXGI_FORMAT_UNKNOWN);
                return false;
            }
            D3D11_TEXTURE2D_DESC nd = d;
            nd.MipLevels = 1;
            nd.ArraySize = 1;
            nd.Format = sf;
            nd.Usage = D3D11_USAGE_DEFAULT;
            nd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            nd.CPUAccessFlags = 0;
            nd.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
            HRESULT hr = dev->CreateTexture2D(&nd, nullptr, c.t11.ReleaseAndGetAddressOf());
            if (SUCCEEDED(hr)) {
                if (dev.Get() == m_subDev) {
                    c.view = c.t11;
                } else {
                    ComPtr<IDXGIResource> res;
                    HANDLE h = nullptr;
                    hr = c.t11.As(&res);
                    if (SUCCEEDED(hr)) {
                        hr = res->GetSharedHandle(&h);
                    }
                    if (SUCCEEDED(hr)) {
                        hr = m_subDev->OpenSharedResource(h, __uuidof(ID3D11Texture2D), (void**)c.view.ReleaseAndGetAddressOf());
                    }
                }
            }
            if (FAILED(hr)) {
                c.failed = true;
                m_copyFailures++;
                c.t11.Reset();
                c.view.Reset();
                Log("motion vectors: %s %ux%u format %u: shared copy failed 0x%08x", what, d.Width, d.Height, (unsigned)d.Format, (unsigned)hr);
                return false;
            }
            c.w = d.Width;
            c.h = d.Height;
            c.fmt = nd.Format;
            Log("motion vectors: %s %ux%u format %u -> shared copy (format %u, %s device)", what, c.w, c.h, (unsigned)d.Format, (unsigned)c.fmt,
                dev.Get() == m_subDev ? "same" : "other");
        }
        ctx->CopySubresourceRegion(c.t11.Get(), 0, 0, 0, 0, src, 0, nullptr);
        return true;
    }

    // The slot companion: a legacy shared D3D11 texture on the submission device, recreated when the source size changes. Returns its handle (0 = none).
    uint64_t CopyToSlot(ID3D11DeviceContext* ctx, SlotTex& st, Copy& c, uint32_t& fmtOut, uint32_t& wOut, uint32_t& hOut) {
        if (!c.view || !m_subDev) {
            fmtOut = 0; wOut = 0; hOut = 0;
            return 0;
        }
        if (!st.tex || st.w != c.w || st.h != c.h || st.fmt != c.fmt) {
            if (st.tex) {
                Retired r;
                r.t11 = st.tex;
                r.atS = visionalvr_ipc::QpcSeconds();
                r.seq = m_harvestSeq;
                m_retired.push_back(r);
            }
            st.tex.Reset();
            st.handle = 0;
            D3D11_TEXTURE2D_DESC nd{};
            nd.Width = c.w;
            nd.Height = c.h;
            nd.MipLevels = 1;
            nd.ArraySize = 1;
            nd.Format = c.fmt;
            nd.SampleDesc.Count = 1;
            nd.Usage = D3D11_USAGE_DEFAULT;
            nd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            nd.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
            ComPtr<IDXGIResource> res;
            HANDLE h = nullptr;
            HRESULT hr = m_subDev->CreateTexture2D(&nd, nullptr, st.tex.ReleaseAndGetAddressOf());
            if (SUCCEEDED(hr)) {
                hr = st.tex.As(&res);
            }
            if (SUCCEEDED(hr)) {
                hr = res->GetSharedHandle(&h);
            }
            if (FAILED(hr)) {
                st.tex.Reset();
                if (hr == DXGI_ERROR_DEVICE_REMOVED && !m_removedLogged) {
                    m_removedLogged = true;
                    Log("motion vectors: the submission device is removed, reason 0x%08x: no more copies this session", (unsigned)m_subDev->GetDeviceRemovedReason());
                }
                if (!m_removedLogged) {
                    Log("motion vectors: slot companion %ux%u format %u failed 0x%08x", c.w, c.h, (unsigned)c.fmt, (unsigned)hr);
                }
                fmtOut = 0; wOut = 0; hOut = 0;
                return 0;
            }
            st.handle = (uint64_t)(uintptr_t)h;
            st.w = c.w;
            st.h = c.h;
            st.fmt = c.fmt;
        }
        ctx->CopyResource(st.tex.Get(), c.view.Get());
        fmtOut = (uint32_t)st.fmt;
        wOut = st.w;
        hOut = st.h;
        return st.handle;
    }

    void Log(const char* fmt, ...) {
        if (!m_log) {
            return;
        }
        char line[512];
        va_list ap;
        va_start(ap, fmt);
        vsnprintf(line, sizeof(line), fmt, ap);
        va_end(ap);
        m_log(line);
    }

    mutable std::mutex m_mutex;
    ID3D11Device* m_subDev = nullptr;
    ComPtr<ID3D11Device1> m_subDev1;
    LogFn m_log = nullptr;
    SetConsumerV1Fn m_set = nullptr;
    bool m_attached = false;
    bool m_off = false;
    std::atomic<bool> m_enabled{true};
    uint32_t m_state = 0;
    uint64_t m_publications = 0;
    uint64_t m_copyFailures = 0;
    std::vector<Retired> m_retired;
    uint64_t m_harvestSeq = 0;
    bool m_depth = false;
    bool m_removedLogged = false;      // sources whose shared copy could not be made (each logged once)
    D3D12_RESOURCE_STATES m_srcState = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;   // what DLSS requires of its inputs
    std::vector<Stream> m_streams;
    SlotTex m_slotTex[visionalvr_ipc::kSlots][2][2];   // [slot][eye][0 vectors, 1 depth]
};

} // namespace ngxtap
