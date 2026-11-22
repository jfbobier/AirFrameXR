// Neural frame generation fields (see neural.h).
#define NOMINMAX
#include "neural.h"
#include <windows.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <type_traits>
#include <vector>
#include "airframexr_rife.h"   // the network's C interface (nvenc/rife); the DLL is loaded at run time

namespace {
// ---- the CUDA driver API, loaded from the driver's nvcuda.dll (no CUDA toolkit, no runtime DLL) ---------------------------------------------------
typedef int CUresult;
typedef int CUdevice;
typedef struct CUctx_st* CUcontext;
typedef struct CUstream_st* CUstream;
typedef struct CUevent_st* CUevent;
typedef struct CUgraphicsResource_st* CUgraphicsResource;
typedef unsigned long long CUdeviceptr;
struct Cu {
    HMODULE dll = nullptr;
    CUresult(__stdcall* Init)(unsigned int) = nullptr;
    CUresult(__stdcall* D3D11GetDevice)(CUdevice*, IDXGIAdapter*) = nullptr;
    CUresult(__stdcall* PrimaryCtxRetain)(CUcontext*, CUdevice) = nullptr;
    CUresult(__stdcall* PrimaryCtxRelease)(CUdevice) = nullptr;
    CUresult(__stdcall* CtxPush)(CUcontext) = nullptr;
    CUresult(__stdcall* CtxPop)(CUcontext*) = nullptr;
    CUresult(__stdcall* StreamCreate)(CUstream*, unsigned int) = nullptr;
    CUresult(__stdcall* StreamDestroy)(CUstream) = nullptr;
    CUresult(__stdcall* StreamSynchronize)(CUstream) = nullptr;
    CUresult(__stdcall* EventCreate)(CUevent*, unsigned int) = nullptr;
    CUresult(__stdcall* EventRecord)(CUevent, CUstream) = nullptr;
    CUresult(__stdcall* EventQuery)(CUevent) = nullptr;
    CUresult(__stdcall* EventElapsedTime)(float*, CUevent, CUevent) = nullptr;
    CUresult(__stdcall* EventDestroy)(CUevent) = nullptr;
    CUresult(__stdcall* GraphicsD3D11RegisterResource)(CUgraphicsResource*, ID3D11Resource*, unsigned int) = nullptr;
    CUresult(__stdcall* GraphicsUnregisterResource)(CUgraphicsResource) = nullptr;
    CUresult(__stdcall* GraphicsMapResources)(unsigned int, CUgraphicsResource*, CUstream) = nullptr;
    CUresult(__stdcall* GraphicsUnmapResources)(unsigned int, CUgraphicsResource*, CUstream) = nullptr;
    CUresult(__stdcall* GraphicsResourceGetMappedPointer)(CUdeviceptr*, size_t*, CUgraphicsResource) = nullptr;
    CUresult(__stdcall* GetErrorString)(CUresult, const char**) = nullptr;
    bool load(std::string& why) {
        dll = LoadLibraryA("nvcuda.dll");
        if (!dll) { why = "nvcuda.dll not found (no NVIDIA driver?)"; return false; }
        auto get = [&](auto& fn, const char* name) { fn = reinterpret_cast<std::remove_reference_t<decltype(fn)>>(GetProcAddress(dll, name)); if (!fn) why = std::string("nvcuda.dll lacks ") + name; return fn != nullptr; };
        return get(Init, "cuInit") && get(D3D11GetDevice, "cuD3D11GetDevice") && get(PrimaryCtxRetain, "cuDevicePrimaryCtxRetain") &&
               get(PrimaryCtxRelease, "cuDevicePrimaryCtxRelease_v2") && get(CtxPush, "cuCtxPushCurrent_v2") && get(CtxPop, "cuCtxPopCurrent_v2") &&
               get(StreamCreate, "cuStreamCreate") && get(StreamDestroy, "cuStreamDestroy_v2") && get(StreamSynchronize, "cuStreamSynchronize") &&
               get(EventCreate, "cuEventCreate") && get(EventRecord, "cuEventRecord") && get(EventQuery, "cuEventQuery") &&
               get(EventElapsedTime, "cuEventElapsedTime") && get(EventDestroy, "cuEventDestroy_v2") &&
               get(GraphicsD3D11RegisterResource, "cuGraphicsD3D11RegisterResource") && get(GraphicsUnregisterResource, "cuGraphicsUnregisterResource") &&
               get(GraphicsMapResources, "cuGraphicsMapResources") && get(GraphicsUnmapResources, "cuGraphicsUnmapResources") &&
               get(GraphicsResourceGetMappedPointer, "cuGraphicsResourceGetMappedPointer_v2") && get(GetErrorString, "cuGetErrorString");
    }
    std::string err(const char* what, CUresult r) const {
        const char* s = nullptr; if (GetErrorString) GetErrorString(r, &s);
        char b[200]; snprintf(b, sizeof b, "%s failed: CUDA error %d %s", what, r, s ? s : ""); return b;
    }
};

// airframexr_rife.dll (nvenc/rife, docs/NEURAL_RIFE_CUDA.md), by name: the streamer runs without it
struct Rife {
    HMODULE dll = nullptr;
    int(__cdecl* Abi)() = nullptr;
    AfxRife*(__cdecl* Create)(const wchar_t*, int, int, int, char*, int) = nullptr;
    int(__cdecl* Run)(AfxRife*, uint64_t, uint64_t, const float*, uint64_t, uint64_t, void*, char*, int) = nullptr;
    const char*(__cdecl* Info)(AfxRife*) = nullptr;
    void(__cdecl* Destroy)(AfxRife*) = nullptr;
    bool load(const std::wstring& dir, std::string& why) {
        dll = LoadLibraryExW((dir + L"\\airframexr_rife.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!dll) { why = "airframexr_rife.dll not found in the neural folder"; return false; }
        auto get = [&](auto& fn, const char* name) { fn = reinterpret_cast<std::remove_reference_t<decltype(fn)>>(GetProcAddress(dll, name)); if (!fn) why = std::string("airframexr_rife.dll lacks ") + name; return fn != nullptr; };
        if (!(get(Abi, "afx_rife_abi") && get(Create, "afx_rife_create") && get(Run, "afx_rife_run") && get(Info, "afx_rife_info") && get(Destroy, "afx_rife_destroy"))) return false;
        if (Abi() != AFX_RIFE_ABI) { why = "airframexr_rife.dll has interface " + std::to_string(Abi()) + ", this streamer needs " + std::to_string(AFX_RIFE_ABI); return false; }
        return true;
    }
};

// both frames at the network's resolution, planar float RGB per eye (N = 2 eyes, C = 3), the older one turned into the newer one's camera (black
// outside its view), as the optical flow's input pass does; zero padding past the used size (the model needs multiples of 32)
const char* kPrepHlsl = R"(
cbuffer P : register(b0) { float4 rA[2]; float4 rB[2]; float4 rC[2]; float4 tanE[2]; float4 dims; };   // dims: used w h, padded w h
Texture2D cur : register(t0);
Texture2D prev : register(t1);
SamplerState smp : register(s0);
RWStructuredBuffer<float> o0 : register(u0);
RWStructuredBuffer<float> o1 : register(u1);
[numthreads(8, 8, 1)]
void CSPrep(uint3 id : SV_DispatchThreadID) {
    const uint W = (uint)dims.z, H = (uint)dims.w, e = id.z;
    if (id.x >= W || id.y >= H) return;
    float3 a = 0, b = 0;
    if (id.x < (uint)dims.x && id.y < (uint)dims.y) {
        const float2 uv = (float2(id.xy) + 0.5) / dims.xy;
        const float x0 = e ? 0.5 : 0.0;
        b = cur.SampleLevel(smp, float2(x0 + uv.x * 0.5, uv.y), 0).rgb;
        const float4 tn = tanE[e];
        const float3 d = float3(lerp(-tn.x, tn.y, uv.x), lerp(tn.z, -tn.w, uv.y), -1);
        const float3 q = float3(dot(rA[e].xyz, d), dot(rB[e].xyz, d), dot(rC[e].xyz, d));
        if (q.z < -1e-4) {
            const float2 tt = q.xy / -q.z;
            const float2 up = float2((tt.x + tn.x) / (tn.x + tn.y), 1 - (tt.y + tn.w) / (tn.z + tn.w));
            if (all(up >= 0) && all(up <= 1)) a = prev.SampleLevel(smp, float2(x0 + up.x * 0.5, up.y), 0).rgb;
        }
    }
    const uint plane = W * H, i = id.y * W + id.x;
    [unroll] for (uint c = 0; c < 3; c++) { o0[(e * 3 + c) * plane + i] = a[c]; o1[(e * 3 + c) * plane + i] = b[c]; }
}
)";
struct PrepCb { float rA[2][4], rB[2][4], rC[2][4], tanE[2][4], dims[4]; };

template <class T> void rel(T*& p) { if (p) { p->Release(); p = nullptr; } }
void seterr(char* err, int n, const std::string& s) { if (err && n > 0) { strncpy_s(err, n, s.c_str(), _TRUNCATE); } }
}  // namespace

struct NeuralFields::Impl {
    Cu cu;
    CUdevice dev = 0; CUcontext ctx = nullptr; CUstream stream = nullptr; CUevent ev[2] = {}; bool timed = false;
    Rife rife; AfxRife* net = nullptr;
    // D3D11 side: inputs (img0 = older, img1 = newer), the outputs
    ID3D11Buffer* buf[4] = {};   // 0 img0, 1 img1, 2 flow, 3 mask
    ID3D11UnorderedAccessView* uav[2] = {};
    ID3D11ShaderResourceView* srvFlow = nullptr; ID3D11ShaderResourceView* srvMask = nullptr;
    CUgraphicsResource res[4] = {};
    ID3D11ComputeShader* cs = nullptr; ID3D11Buffer* cb = nullptr; ID3D11SamplerState* smp = nullptr;
    struct CtxScope {   // the CUDA context current on this thread for the scope
        Cu& cu; bool on;
        CtxScope(Cu& c, CUcontext x) : cu(c), on(x && c.CtxPush(x) == 0) {}
        ~CtxScope() { if (on) { CUcontext o; cu.CtxPop(&o); } }
    };
};

NeuralFields::NeuralFields(ID3D11Device* dev, ID3D11DeviceContext* ctx) : m(new Impl()) { m_dev = dev; m_ctx = ctx; }

NeuralFields::~NeuralFields() {
    Impl& I = *m;
    if (I.ctx) {
        Impl::CtxScope sc(I.cu, I.ctx);
        if (I.stream) I.cu.StreamSynchronize(I.stream);
        if (I.net) { I.rife.Destroy(I.net); I.net = nullptr; }
        for (auto& r : I.res) if (r) { I.cu.GraphicsUnregisterResource(r); r = nullptr; }
        for (auto& e : I.ev) if (e) I.cu.EventDestroy(e);
        if (I.stream) I.cu.StreamDestroy(I.stream);
    }
    if (I.ctx) I.cu.PrimaryCtxRelease(I.dev);
    for (auto& b : I.buf) rel(b);
    for (auto& u : I.uav) rel(u);
    rel(I.srvFlow); rel(I.srvMask); rel(I.cs); rel(I.cb); rel(I.smp);
    // the network's DLL stays loaded (its CUDA runtime is linked in; unloading it while the primary context lives is not worth the risk)
    delete m;
}

ID3D11ShaderResourceView* NeuralFields::FlowSrv() const { return m->srvFlow; }
ID3D11ShaderResourceView* NeuralFields::MaskSrv() const { return m->srvMask; }

bool NeuralFields::Init(int eyeW, int eyeH, float scale, const wchar_t* dir, char* err, int errlen) {
    Impl& I = *m;
    auto fail = [&](const std::string& why) { seterr(err, errlen, why); snprintf(stats.info, sizeof stats.info, "neural off: %s", why.c_str()); return false; };
    scale = std::min(1.f, std::max(0.1f, scale));
    NetW = std::max(32, (int)std::lround(eyeW * scale)); NetH = std::max(32, (int)std::lround(eyeH * scale));
    PadW = (NetW + 31) / 32 * 32; PadH = (NetH + 31) / 32 * 32;
    std::string why;
    if (!I.cu.load(why)) return fail(why);
    CUresult r = I.cu.Init(0);
    if (r) return fail(I.cu.err("cuInit", r));
    {   // the CUDA device of the D3D11 adapter
        IDXGIDevice* dx = nullptr; IDXGIAdapter* ad = nullptr;
        if (FAILED(m_dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dx)) || FAILED(dx->GetAdapter(&ad))) { rel(dx); return fail("no DXGI adapter"); }
        r = I.cu.D3D11GetDevice(&I.dev, ad);
        rel(ad); rel(dx);
        if (r) return fail(I.cu.err("cuD3D11GetDevice", r));
    }
    if ((r = I.cu.PrimaryCtxRetain(&I.ctx, I.dev))) { I.ctx = nullptr; return fail(I.cu.err("cuDevicePrimaryCtxRetain", r)); }
    Impl::CtxScope sc(I.cu, I.ctx);
    if ((r = I.cu.StreamCreate(&I.stream, 1 /* CU_STREAM_NON_BLOCKING */))) return fail(I.cu.err("cuStreamCreate", r));
    for (auto& e : I.ev) if ((r = I.cu.EventCreate(&e, 0))) return fail(I.cu.err("cuEventCreate", r));

    // the network: our CUDA DLL and RIFE 4.4's weights, from the folder
    const std::wstring d(dir ? dir : L"");
    if (!I.rife.load(d, why)) return fail(why);
    LARGE_INTEGER f0, f1, fq; QueryPerformanceCounter(&f0); QueryPerformanceFrequency(&fq);
    {
        char e2[300] = "";
        I.net = I.rife.Create((d + L"\\rife44.afxw").c_str(), 2, PadW, PadH, e2, sizeof e2);
        if (!I.net) return fail(e2);
    }
    // D3D11 buffers shared with CUDA: structured floats
    const UINT img = 2u * 3u * PadW * PadH, fl = 2u * 4u * PadW * PadH, mk = 2u * PadW * PadH;
    const UINT counts[4] = { img, img, fl, mk };
    for (int k = 0; k < 4; k++) {
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth = counts[k] * 4u; bd.Usage = D3D11_USAGE_DEFAULT; bd.StructureByteStride = 4; bd.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
        bd.BindFlags = D3D11_BIND_SHADER_RESOURCE | (k < 2 ? D3D11_BIND_UNORDERED_ACCESS : 0);
        if (FAILED(m_dev->CreateBuffer(&bd, nullptr, &I.buf[k]))) return fail("D3D11 buffer");
        if ((r = I.cu.GraphicsD3D11RegisterResource(&I.res[k], I.buf[k], 0))) return fail(I.cu.err("cuGraphicsD3D11RegisterResource", r));
    }
    for (int k = 0; k < 2; k++) {
        D3D11_UNORDERED_ACCESS_VIEW_DESC ud{}; ud.Format = DXGI_FORMAT_UNKNOWN; ud.ViewDimension = D3D11_UAV_DIMENSION_BUFFER; ud.Buffer.NumElements = img;
        if (FAILED(m_dev->CreateUnorderedAccessView(I.buf[k], &ud, &I.uav[k]))) return fail("D3D11 UAV");
    }
    {
        D3D11_SHADER_RESOURCE_VIEW_DESC sd{}; sd.Format = DXGI_FORMAT_UNKNOWN; sd.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
        sd.Buffer.NumElements = fl; if (FAILED(m_dev->CreateShaderResourceView(I.buf[2], &sd, &I.srvFlow))) return fail("D3D11 SRV");
        sd.Buffer.NumElements = mk; if (FAILED(m_dev->CreateShaderResourceView(I.buf[3], &sd, &I.srvMask))) return fail("D3D11 SRV");
    }
    {
        ID3DBlob *b = nullptr, *e = nullptr;
        if (FAILED(D3DCompile(kPrepHlsl, strlen(kPrepHlsl), "neural_prep", nullptr, nullptr, "CSPrep", "cs_5_0", 0, 0, &b, &e))) {
            std::string m2 = e ? (const char*)e->GetBufferPointer() : "prep shader"; rel(e); return fail(m2);
        }
        m_dev->CreateComputeShader(b->GetBufferPointer(), b->GetBufferSize(), nullptr, &I.cs); rel(b);
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth = sizeof(PrepCb); bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        m_dev->CreateBuffer(&bd, nullptr, &I.cb);
        D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR; sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD = D3D11_FLOAT32_MAX;
        m_dev->CreateSamplerState(&sd, &I.smp);
        if (!I.cs || !I.cb || !I.smp) return fail("prep pipeline objects");
    }
    {   // first run now (CUDA loads the kernels at their first launch), not on the first generated frame
        CUdeviceptr p[4] = {}; size_t sz = 0;
        if ((r = I.cu.GraphicsMapResources(4, I.res, I.stream))) return fail(I.cu.err("cuGraphicsMapResources", r));
        for (int k = 0; k < 4; k++) I.cu.GraphicsResourceGetMappedPointer(&p[k], &sz, I.res[k]);
        const float tt[2] = { 0.5f, 0.5f };
        char e2[300] = "";
        const int rr = I.rife.Run(I.net, p[0], p[1], tt, p[2], p[3], I.stream, e2, sizeof e2);
        I.cu.GraphicsUnmapResources(4, I.res, I.stream);
        r = I.cu.StreamSynchronize(I.stream);
        if (rr || r) return fail(std::string("first run failed: ") + (rr ? e2 : I.cu.err("cuStreamSynchronize", r)));
    }
    QueryPerformanceCounter(&f1);
    snprintf(stats.info, sizeof stats.info, "neural %s (fields %dx%d per eye, ready in %.2f s)", I.rife.Info(I.net), NetW, NetH,
             (double)(f1.QuadPart - f0.QuadPart) / (double)fq.QuadPart);
    return true;
}

bool NeuralFields::Run(ID3D11ShaderResourceView* prev, ID3D11ShaderResourceView* cur, const float rot[2][12], const float* tan8, float t, bool reversed,
                       char* err, int errlen) {
    Impl& I = *m;
    if (!I.net) { seterr(err, errlen, "neural fields not initialised"); return false; }
    LARGE_INTEGER c0, c1, cq; QueryPerformanceCounter(&c0); QueryPerformanceFrequency(&cq);
    // 1. both frames at the network's resolution (D3D11)
    PrepCb pc{};
    for (int e = 0; e < 2; e++) {
        memcpy(pc.rA[e], rot[e] + 0, 12); memcpy(pc.rB[e], rot[e] + 4, 12); memcpy(pc.rC[e], rot[e] + 8, 12);
        memcpy(pc.tanE[e], tan8 + 4 * e, 16);
    }
    pc.dims[0] = (float)NetW; pc.dims[1] = (float)NetH; pc.dims[2] = (float)PadW; pc.dims[3] = (float)PadH;
    m_ctx->UpdateSubresource(I.cb, 0, nullptr, &pc, 0, 0);
    ID3D11ShaderResourceView* srvs[2] = { cur, prev };
    m_ctx->CSSetShader(I.cs, nullptr, 0);
    m_ctx->CSSetConstantBuffers(0, 1, &I.cb);
    m_ctx->CSSetShaderResources(0, 2, srvs);
    m_ctx->CSSetSamplers(0, 1, &I.smp);
    m_ctx->CSSetUnorderedAccessViews(0, 2, I.uav, nullptr);
    m_ctx->Dispatch((PadW + 7) / 8, (PadH + 7) / 8, 2);
    ID3D11UnorderedAccessView* nu[2] = {}; ID3D11ShaderResourceView* ns[2] = {};
    m_ctx->CSSetUnorderedAccessViews(0, 2, nu, nullptr);
    m_ctx->CSSetShaderResources(0, 2, ns);
    m_ctx->CSSetShader(nullptr, nullptr, 0);
    m_ctx->Flush();   // the prep work reaches the GPU before CUDA's map waits on it
    // 2. the network (CUDA): map orders it after the D3D11 work above, unmap orders the D3D11 work after it
    Impl::CtxScope sc(I.cu, I.ctx);
    CUresult r = I.cu.GraphicsMapResources(4, I.res, I.stream);
    if (r) { stats.failures++; seterr(err, errlen, I.cu.err("cuGraphicsMapResources", r)); return false; }
    CUdeviceptr p[4] = {}; size_t sz = 0;
    for (int k = 0; k < 4; k++) I.cu.GraphicsResourceGetMappedPointer(&p[k], &sz, I.res[k]);
    const float tt[2] = { t, t };
    char e2[300] = "";
    I.cu.EventRecord(I.ev[0], I.stream);
    const int rr = reversed ? I.rife.Run(I.net, p[1], p[0], tt, p[2], p[3], I.stream, e2, sizeof e2) : I.rife.Run(I.net, p[0], p[1], tt, p[2], p[3], I.stream, e2, sizeof e2);
    I.cu.EventRecord(I.ev[1], I.stream);
    I.cu.GraphicsUnmapResources(4, I.res, I.stream);
    QueryPerformanceCounter(&c1);
    stats.lastCpuMs = (float)((c1.QuadPart - c0.QuadPart) * 1000.0 / (double)cq.QuadPart); stats.cpuMsSum += stats.lastCpuMs;
    if (rr) { stats.failures++; seterr(err, errlen, std::string("network run failed: ") + e2); return false; }
    stats.runs++;
    I.timed = true;
    return true;
}

void NeuralFields::CollectTiming() {
    Impl& I = *m;
    if (!I.timed || !I.ctx) return;
    Impl::CtxScope sc(I.cu, I.ctx);
    if (I.cu.EventQuery(I.ev[1]) != 0) return;   // not finished yet
    float ms = 0;
    if (I.cu.EventElapsedTime(&ms, I.ev[0], I.ev[1]) == 0) { stats.lastMs = ms; stats.msSum += ms; stats.msN++; }
    I.timed = false;
}
