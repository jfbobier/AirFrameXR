// Frame generation (see framegen.h and docs/FRAME_GENERATION.md).
//
// Passes per rendered frame B (Ingest):   copy B into the history | prep: A seen from B's camera and B, both downscaled to the flow size
//                                         | optical-flow engine: backward flow B -> A' (+ cost) on a block grid
// Passes per output (Render):             one full-screen pass per eye: output pixel -> direction in the stamp pose -> B's camera; mode 0
//                                         samples B there; mode 1 samples A' (through B's camera and the rotation into A's) and B at the two
//                                         ends of the flow vector, weighted by t, with a confidence from the colour disagreement and the
//                                         engine's cost, falling back to the crossfade of the rotation-aligned A and B (ghosting, not holes).
#define NOMINMAX
#include "framegen.h"
#include <windows.h>
#include <d3dcompiler.h>
#include "nvOpticalFlowD3D11.h"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>

namespace {
const char* kShader = R"(
cbuffer CB : register(b0) {
    float4 oA, oB, oC;      // M_out rows: direction in the output view -> direction in the current frame's view
    float4 pA, pB, pC;      // M_prev rows: direction in the current frame's view -> direction in the previous frame's view
    float4 tanF;            // l r u d tangents of the eye
    float4 eyeRect;         // x: u0 of this eye in the SBS textures, y: u width, z: full-res SBS width (px), w: height (px)
    float4 flowP;           // x, y: flow input scale (small / full), z: grid, w: t
    int4   blocks;          // x0 y0 x1 y1: block bounds of this eye in the flow texture (inclusive)
    float4 mode;            // x: pass mode, y: max flow (full-res px), z: confidence gain, w: unused
};
Texture2D cur : register(t0);
Texture2D prev : register(t1);
Texture2D<int2> flow : register(t2);
Texture2D<uint> cost : register(t3);
SamplerState smp : register(s0);
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSOut VS(uint id : SV_VertexID) {
    VSOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = p;
    return o;
}
float3 dirOf(float2 uv) { return float3(lerp(-tanF.x, tanF.y, uv.x), lerp(tanF.z, -tanF.w, uv.y), -1); }
bool projectTo(float3 d, out float2 uv) {
    uv = float2(0, 0);
    if (d.z > -1e-4) return false;
    float2 tt = d.xy / -d.z;
    uv = float2((tt.x + tanF.x) / (tanF.x + tanF.y), 1 - (tt.y + tanF.w) / (tanF.z + tanF.w));
    return all(uv >= 0) && all(uv <= 1);
}
float3 rot(float4 a, float4 b, float4 c, float3 d) { return float3(dot(a.xyz, d), dot(b.xyz, d), dot(c.xyz, d)); }
float2 sbs(float2 uv) { return float2(eyeRect.x + uv.x * eyeRect.y, uv.y); }
float2 uvToPx(float2 uv) { return float2(sbs(uv).x * eyeRect.z, uv.y * eyeRect.w); }
float2 pxToUv(float2 px) { return float2((px.x / eyeRect.z - eyeRect.x) / eyeRect.y, px.y / eyeRect.w); }
// prep: the flow input of one eye (small). mode 0: the current frame as is; 1: the previous frame seen from the current camera (t0 = that frame)
float4 PSPrep(VSOut i) : SV_Target {
    float2 uv = i.uv;
    if (mode.x > 0.5) {
        if (!projectTo(rot(pA, pB, pC, dirOf(i.uv)), uv)) return float4(0, 0, 0, 1);
    }
    return float4(cur.SampleLevel(smp, sbs(uv), 0).rgb, 1);
}
// backward flow (current -> previous, both in the current camera) at a full-res SBS pixel, in full-res pixels, bilinear over the block grid
float2 flowAt(float2 px) {
    float2 fc = (px * flowP.xy + 0.5) / flowP.z - 0.5;
    fc = clamp(fc, float2(blocks.xy), float2(blocks.zw));
    int2 tl = int2(floor(fc));
    int2 br = min(tl + int2(1, 1), blocks.zw);
    float2 fr = fc - float2(tl);
    float2 f00 = float2(flow.Load(int3(tl, 0)));
    float2 f10 = float2(flow.Load(int3(br.x, tl.y, 0)));
    float2 f01 = float2(flow.Load(int3(tl.x, br.y, 0)));
    float2 f11 = float2(flow.Load(int3(br, 0)));
    float2 f = lerp(lerp(f00, f10, fr.x), lerp(f01, f11, fr.x), fr.y) * (1.0 / 32.0) / flowP.xy;
    float m = length(f);
    return m > mode.y ? f * (mode.y / m) : f;
}
float costAt(float2 px) {
    float2 fc = (px * flowP.xy + 0.5) / flowP.z - 0.5;
    int2 c = clamp(int2(round(fc)), blocks.xy, blocks.zw);
    return float(cost.Load(int3(c, 0)) & 0xffu) / 255.0;
}
// the previous frame at a point given in the current camera (eye-local uv)
bool samplePrev(float2 uvCur, out float3 c) {
    float2 uvP;
    c = float3(0, 0, 0);
    if (!projectTo(rot(pA, pB, pC, dirOf(uvCur)), uvP)) return false;
    c = prev.SampleLevel(smp, sbs(uvP), 0).rgb;
    return true;
}
float4 PSOut(VSOut i) : SV_Target {
    float2 uvB;
    if (!projectTo(rot(oA, oB, oC, dirOf(i.uv)), uvB)) return float4(0, 0, 0, 1);
    float3 curHere = cur.SampleLevel(smp, sbs(uvB), 0).rgb;
    if (mode.x < 0.5) return float4(curHere, 1);
    float t = flowP.w;
    float3 prevHere;
    bool havePrev = samplePrev(uvB, prevHere);
    float3 stable = havePrev ? lerp(prevHere, curHere, t) : curHere;
    float2 pB = uvToPx(uvB);
    float2 f = flowAt(pB);
    float2 uvC = pxToUv(pB - f * t);
    float2 uvPb = pxToUv(pB + f * (1 - t));
    float3 pc;
    bool okC = all(uvC >= 0) && all(uvC <= 1);
    bool okP = all(uvPb >= 0) && all(uvPb <= 1) && samplePrev(uvPb, pc);
    if (!(okC && okP)) return float4(stable, 1);
    float3 cc = cur.SampleLevel(smp, sbs(uvC), 0).rgb;
    float3 mid = lerp(pc, cc, t);
    float dis = max(abs(pc.r - cc.r), max(abs(pc.g - cc.g), abs(pc.b - cc.b)));
    float conf = saturate(1 - dis * mode.z) * saturate(1 - costAt(pB));
    return float4(lerp(stable, mid, conf), 1);
}
)";

struct Cb {
    float oA[4], oB[4], oC[4];
    float pA[4], pB[4], pC[4];
    float tanF[4];
    float eyeRect[4];
    float flowP[4];
    int blocks[4];
    float mode[4];
};

void qmatRow(const float* q, double m[9]) {   // row-major rotation matrix of a unit quaternion (x y z w)
    double x = q[0], y = q[1], z = q[2], w = q[3];
    const double n = std::sqrt(x * x + y * y + z * z + w * w);
    if (n > 0) { x /= n; y /= n; z /= n; w /= n; } else { x = y = z = 0; w = 1; }
    m[0] = 1 - 2 * (y * y + z * z); m[1] = 2 * (x * y - z * w);     m[2] = 2 * (x * z + y * w);
    m[3] = 2 * (x * y + z * w);     m[4] = 1 - 2 * (x * x + z * z); m[5] = 2 * (y * z - x * w);
    m[6] = 2 * (x * z - y * w);     m[7] = 2 * (y * z + x * w);     m[8] = 1 - 2 * (x * x + y * y);
}
// M = Ra^T * Rb: a direction in frame b -> world -> frame a
void relRot(const float* qa, const float* qb, float out[9]) {
    double ra[9], rb[9];
    qmatRow(qa, ra); qmatRow(qb, rb);
    for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) { double acc = 0; for (int k = 0; k < 3; k++) acc += ra[k * 3 + r] * rb[k * 3 + c]; out[r * 3 + c] = (float)acc; }
}
void rows(const float m[9], float* a, float* b, float* c) {
    a[0] = m[0]; a[1] = m[1]; a[2] = m[2]; a[3] = 0;
    b[0] = m[3]; b[1] = m[4]; b[2] = m[5]; b[3] = 0;
    c[0] = m[6]; c[1] = m[7]; c[2] = m[8]; c[3] = 0;
}
void seterr(char* err, int n, const char* s) { if (err && n > 0) { strncpy(err, s, n - 1); err[n - 1] = 0; } }
template <class T> void rel(T*& p) { if (p) { p->Release(); p = nullptr; } }
}

struct FrameGen::Impl {
    FrameGenConfig cfg;
    // D3D pipeline
    ID3D11VertexShader* vs = nullptr; ID3D11PixelShader* psPrep = nullptr; ID3D11PixelShader* psOut = nullptr;
    ID3D11Buffer* cb = nullptr; ID3D11SamplerState* smp = nullptr;
    // frame history: two copies of rendered frames and their per-eye render orientations
    ID3D11Texture2D* real[2] = {}; ID3D11ShaderResourceView* realSrv[2] = {};
    float realQ[2][8] = {};
    int cur = 0;                      // index of the newest rendered frame in real[]
    DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN;
    ID3D11RenderTargetView* outRtv = nullptr;
    // optical flow
    HMODULE dll = nullptr;
    NV_OF_D3D11_API_FUNCTION_LIST api{};
    NvOFHandle of = nullptr;
    int flowW = 0, flowH = 0, blocksW = 0, blocksH = 0, grid = 4;
    ID3D11Texture2D* flowIn[2] = {}; ID3D11RenderTargetView* flowInRtv[2] = {};
    ID3D11Texture2D* flowOut = nullptr; ID3D11ShaderResourceView* flowOutSrv = nullptr;
    ID3D11Texture2D* costTex = nullptr; ID3D11ShaderResourceView* costSrv = nullptr;
    NvOFGPUBufferHandle hIn[2] = {}, hFlow = nullptr, hCost = nullptr;
    // timings
    ID3D11Query* qDisjoint = nullptr; ID3D11Query* qT[4] = {};
    bool flowTimed = false, synthTimed = false;
    float fov8[8] = {};
};

FrameGen::FrameGen(ID3D11Device* dev, ID3D11DeviceContext* ctx) : m_i(new Impl()), m_dev(dev), m_ctx(ctx) {}

FrameGen::~FrameGen() {
    Impl& I = *m_i;
    if (I.of) {
        for (auto& h : I.hIn) if (h) I.api.nvOFUnregisterResourceD3D11(h);
        if (I.hFlow) I.api.nvOFUnregisterResourceD3D11(I.hFlow);
        if (I.hCost) I.api.nvOFUnregisterResourceD3D11(I.hCost);
        I.api.nvOFDestroy(I.of);
    }
    if (I.dll) FreeLibrary(I.dll);
    for (int k = 0; k < 2; k++) { rel(I.realSrv[k]); rel(I.real[k]); rel(I.flowInRtv[k]); rel(I.flowIn[k]); }
    rel(I.flowOutSrv); rel(I.flowOut); rel(I.costSrv); rel(I.costTex); rel(I.outRtv); rel(m_out);
    rel(I.vs); rel(I.psPrep); rel(I.psOut); rel(I.cb); rel(I.smp); rel(I.qDisjoint);
    for (auto& q : I.qT) rel(q);
    delete m_i;
}

bool FrameGen::Init(int w, int h, const FrameGenConfig& cfg, char* err, int errlen) {
    Impl& I = *m_i;
    I.cfg = cfg;
    m_w = w; m_h = h;
    // shaders
    ID3DBlob *vb = nullptr, *p1 = nullptr, *p2 = nullptr, *e = nullptr;
    if (FAILED(D3DCompile(kShader, strlen(kShader), "framegen", nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vb, &e)) ||
        FAILED(D3DCompile(kShader, strlen(kShader), "framegen", nullptr, nullptr, "PSPrep", "ps_5_0", 0, 0, &p1, &e)) ||
        FAILED(D3DCompile(kShader, strlen(kShader), "framegen", nullptr, nullptr, "PSOut", "ps_5_0", 0, 0, &p2, &e))) {
        seterr(err, errlen, e ? (const char*)e->GetBufferPointer() : "frame generation shader compile failed");
        if (e) e->Release();
        return false;
    }
    m_dev->CreateVertexShader(vb->GetBufferPointer(), vb->GetBufferSize(), nullptr, &I.vs);
    m_dev->CreatePixelShader(p1->GetBufferPointer(), p1->GetBufferSize(), nullptr, &I.psPrep);
    m_dev->CreatePixelShader(p2->GetBufferPointer(), p2->GetBufferSize(), nullptr, &I.psOut);
    vb->Release(); p1->Release(); p2->Release();
    D3D11_BUFFER_DESC bd{}; bd.ByteWidth = sizeof(Cb); bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    m_dev->CreateBuffer(&bd, nullptr, &I.cb);
    D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR; sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD = D3D11_FLOAT32_MAX;
    m_dev->CreateSamplerState(&sd, &I.smp);
    if (!I.vs || !I.psPrep || !I.psOut || !I.cb || !I.smp) { seterr(err, errlen, "frame generation pipeline objects"); return false; }
    D3D11_QUERY_DESC qd{ D3D11_QUERY_TIMESTAMP_DISJOINT, 0 }, qt{ D3D11_QUERY_TIMESTAMP, 0 };
    m_dev->CreateQuery(&qd, &I.qDisjoint);
    for (auto& q : I.qT) m_dev->CreateQuery(&qt, &q);

    // the optical-flow engine (driver library; absent on non-NVIDIA or pre-Turing systems)
    I.dll = LoadLibraryA("nvofapi64.dll");
    if (!I.dll) { seterr(err, errlen, "nvofapi64.dll not found: this driver / GPU has no optical-flow engine (Turing or later needed)"); snprintf(stats.nvofInfo, sizeof stats.nvofInfo, "no nvofapi64.dll"); return false; }
    typedef NV_OF_STATUS(NVOFAPI * PFNCreate)(uint32_t, NV_OF_D3D11_API_FUNCTION_LIST*);
    auto create = (PFNCreate)GetProcAddress(I.dll, "NvOFAPICreateInstanceD3D11");
    if (!create) { seterr(err, errlen, "NvOFAPICreateInstanceD3D11 missing in nvofapi64.dll"); return false; }
    NV_OF_STATUS st = create(NV_OF_API_VERSION, &I.api);
    if (st != NV_OF_SUCCESS) {
        char b[160]; snprintf(b, sizeof b, "NvOFAPICreateInstanceD3D11 (API %d.%d) failed: status %d (driver too old?)", NV_OF_API_MAJOR_VERSION, NV_OF_API_MINOR_VERSION, (int)st);
        seterr(err, errlen, b); snprintf(stats.nvofInfo, sizeof stats.nvofInfo, "%s", b); return false;
    }
    st = I.api.nvCreateOpticalFlowD3D11(m_dev, m_ctx, &I.of);
    if (st != NV_OF_SUCCESS || !I.of) {
        char b[160]; snprintf(b, sizeof b, "nvCreateOpticalFlowD3D11 failed: status %d (no optical-flow engine on this GPU?)", (int)st);
        seterr(err, errlen, b); snprintf(stats.nvofInfo, sizeof stats.nvofInfo, "%s", b); I.of = nullptr; return false;
    }
    // supported output grids: the requested one, else the nearest larger, else the largest
    uint32_t grids[8] = {}; uint32_t n = 8;
    I.grid = cfg.grid;
    if (I.api.nvOFGetCaps(I.of, NV_OF_CAPS_SUPPORTED_OUTPUT_GRID_SIZES, grids, &n) == NV_OF_SUCCESS && n > 0) {
        bool ok = false; uint32_t best = 0, largest = 0;
        for (uint32_t k = 0; k < n && k < 8; k++) {
            if ((int)grids[k] == cfg.grid) ok = true;
            if (grids[k] > (uint32_t)cfg.grid && (best == 0 || grids[k] < best)) best = grids[k];
            largest = std::max(largest, grids[k]);
        }
        if (!ok) I.grid = (int)(best ? best : largest);
    }
    uint32_t wmin = 32, hmin = 32, wmax = 8192, hmax = 8192, one = 1;
    I.api.nvOFGetCaps(I.of, NV_OF_CAPS_WIDTH_MIN, &wmin, &one); one = 1; I.api.nvOFGetCaps(I.of, NV_OF_CAPS_HEIGHT_MIN, &hmin, &one);
    one = 1; I.api.nvOFGetCaps(I.of, NV_OF_CAPS_WIDTH_MAX, &wmax, &one); one = 1; I.api.nvOFGetCaps(I.of, NV_OF_CAPS_HEIGHT_MAX, &hmax, &one);
    const float sc = std::min(1.f, std::max(0.1f, cfg.flowScale));
    auto align = [](int v, int a) { return (v + a - 1) / a * a; };
    I.flowW = std::min<int>((int)wmax, std::max<int>((int)wmin, align((int)std::lround(w * sc), 2 * I.grid)));
    I.flowH = std::min<int>((int)hmax, std::max<int>((int)hmin, align((int)std::lround(h * sc), 2 * I.grid)));
    I.blocksW = (I.flowW + I.grid - 1) / I.grid; I.blocksH = (I.flowH + I.grid - 1) / I.grid;
    NV_OF_INIT_PARAMS ip{};
    ip.width = I.flowW; ip.height = I.flowH; ip.outGridSize = (NV_OF_OUTPUT_VECTOR_GRID_SIZE)I.grid; ip.hintGridSize = NV_OF_HINT_VECTOR_GRID_SIZE_UNDEFINED;
    ip.mode = NV_OF_MODE_OPTICALFLOW; ip.perfLevel = (NV_OF_PERF_LEVEL)cfg.perfLevel; ip.enableExternalHints = NV_OF_FALSE; ip.enableOutputCost = NV_OF_TRUE;
    ip.disparityRange = NV_OF_STEREO_DISPARITY_RANGE_UNDEFINED; ip.enableRoi = NV_OF_FALSE; ip.predDirection = NV_OF_PRED_DIRECTION_FORWARD; ip.enableGlobalFlow = NV_OF_FALSE;
    ip.inputBufferFormat = NV_OF_BUFFER_FORMAT_ABGR8;
    st = I.api.nvOFInit(I.of, &ip);
    if (st != NV_OF_SUCCESS) {
        char le[MIN_ERROR_STRING_SIZE + 1] = ""; uint32_t ls = MIN_ERROR_STRING_SIZE; I.api.nvOFGetLastError(I.of, le, &ls);
        char b[200]; snprintf(b, sizeof b, "nvOFInit %dx%d grid %d perf %d failed: status %d %s", I.flowW, I.flowH, I.grid, cfg.perfLevel, (int)st, le);
        seterr(err, errlen, b); snprintf(stats.nvofInfo, sizeof stats.nvofInfo, "%s", b); return false;
    }
    // buffers: ours, registered with the engine (B8G8R8A8 inputs, R16G16_SINT flow in S10.5, R32_UINT cost)
    auto tex = [&](int tw, int th, DXGI_FORMAT f, UINT bind, ID3D11Texture2D** out) {
        D3D11_TEXTURE2D_DESC td{}; td.Width = tw; td.Height = th; td.MipLevels = 1; td.ArraySize = 1; td.Format = f; td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = bind;
        return SUCCEEDED(m_dev->CreateTexture2D(&td, nullptr, out));
    };
    for (int k = 0; k < 2; k++) {
        if (!tex(I.flowW, I.flowH, DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE, &I.flowIn[k]) ||
            FAILED(m_dev->CreateRenderTargetView(I.flowIn[k], nullptr, &I.flowInRtv[k]))) { seterr(err, errlen, "flow input texture"); return false; }
        if (I.api.nvOFRegisterResourceD3D11(I.of, I.flowIn[k], &I.hIn[k]) != NV_OF_SUCCESS) { seterr(err, errlen, "nvOFRegisterResourceD3D11 (input)"); return false; }
    }
    if (!tex(I.blocksW, I.blocksH, DXGI_FORMAT_R16G16_SINT, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET | D3D11_BIND_UNORDERED_ACCESS, &I.flowOut) ||
        FAILED(m_dev->CreateShaderResourceView(I.flowOut, nullptr, &I.flowOutSrv))) { seterr(err, errlen, "flow output texture"); return false; }
    if (I.api.nvOFRegisterResourceD3D11(I.of, I.flowOut, &I.hFlow) != NV_OF_SUCCESS) { seterr(err, errlen, "nvOFRegisterResourceD3D11 (flow)"); return false; }
    if (!tex(I.blocksW, I.blocksH, DXGI_FORMAT_R32_UINT, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET | D3D11_BIND_UNORDERED_ACCESS, &I.costTex) ||
        FAILED(m_dev->CreateShaderResourceView(I.costTex, nullptr, &I.costSrv))) { seterr(err, errlen, "flow cost texture"); return false; }
    if (I.api.nvOFRegisterResourceD3D11(I.of, I.costTex, &I.hCost) != NV_OF_SUCCESS) { seterr(err, errlen, "nvOFRegisterResourceD3D11 (cost)"); return false; }
    stats.flowW = I.flowW; stats.flowH = I.flowH; stats.blocksW = I.blocksW; stats.blocksH = I.blocksH;
    stats.nvofReady = true;
    snprintf(stats.nvofInfo, sizeof stats.nvofInfo, "NVOF API %d.%d, flow input %dx%d, grid %d (%dx%d vectors), perf %d, frame %dx%d",
             NV_OF_API_MAJOR_VERSION, NV_OF_API_MINOR_VERSION, I.flowW, I.flowH, I.grid, I.blocksW, I.blocksH, cfg.perfLevel, w, h);
    return true;
}

bool FrameGen::ensureFrames(ID3D11Texture2D* like, char* err, int errlen) {
    Impl& I = *m_i;
    D3D11_TEXTURE2D_DESC ld{}; like->GetDesc(&ld);
    if (I.real[0] && I.fmt == ld.Format && (int)ld.Width == m_w && (int)ld.Height == m_h) return true;
    if ((int)ld.Width != m_w || (int)ld.Height != m_h) {
        char b[160]; snprintf(b, sizeof b, "frame generation: slot %ux%u differs from the stream frame %dx%d", ld.Width, ld.Height, m_w, m_h);
        seterr(err, errlen, b); return false;
    }
    for (int k = 0; k < 2; k++) { rel(I.realSrv[k]); rel(I.real[k]); }
    rel(I.outRtv); rel(m_out);
    m_have = 0;
    D3D11_TEXTURE2D_DESC td{}; td.Width = ld.Width; td.Height = ld.Height; td.MipLevels = 1; td.ArraySize = 1; td.Format = ld.Format; td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    for (int k = 0; k < 2; k++) {
        if (FAILED(m_dev->CreateTexture2D(&td, nullptr, &I.real[k])) || FAILED(m_dev->CreateShaderResourceView(I.real[k], nullptr, &I.realSrv[k]))) { seterr(err, errlen, "frame history texture"); return false; }
    }
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    if (FAILED(m_dev->CreateTexture2D(&td, nullptr, &m_out)) || FAILED(m_dev->CreateRenderTargetView(m_out, nullptr, &I.outRtv))) { seterr(err, errlen, "frame generation output texture"); return false; }
    I.fmt = ld.Format;
    return true;
}

int FrameGen::Ingest(ID3D11Texture2D* src, const float* qRender8, const float* fov8, char* err, int errlen) {
    Impl& I = *m_i;
    if (!stats.nvofReady) { seterr(err, errlen, "frame generation not initialised"); return -1; }
    if (!ensureFrames(src, err, errlen)) return -1;
    const int next = m_have == 0 ? 0 : 1 - I.cur;
    m_ctx->CopyResource(I.real[next], src);
    memcpy(I.realQ[next], qRender8, sizeof(float) * 8);
    memcpy(I.fov8, fov8, sizeof(float) * 8);
    I.cur = next;
    m_have = std::min(2, m_have + 1);
    stats.ingests++;
    if (m_have < 2) return 0;
    stats.pairs++;
    const int prev = 1 - I.cur;
    // prep: flowIn[0] = the previous frame seen from the current camera, flowIn[1] = the current frame (both downscaled)
    m_ctx->Begin(I.qDisjoint);
    m_ctx->End(I.qT[0]);
    m_ctx->IASetInputLayout(nullptr); m_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    m_ctx->VSSetShader(I.vs, nullptr, 0); m_ctx->PSSetShader(I.psPrep, nullptr, 0);
    m_ctx->PSSetSamplers(0, 1, &I.smp); m_ctx->PSSetConstantBuffers(0, 1, &I.cb);
    ID3D11ShaderResourceView* nulls[4] = {};
    for (int pass = 0; pass < 2; pass++) {   // 0: previous (rotated), 1: current
        const int frame = pass == 0 ? prev : I.cur;
        m_ctx->OMSetRenderTargets(1, &I.flowInRtv[pass], nullptr);
        m_ctx->PSSetShaderResources(0, 1, &I.realSrv[frame]);
        for (int e = 0; e < 2; e++) {
            Cb c{};
            float m[9];
            relRot(I.realQ[prev] + e * 4, I.realQ[I.cur] + e * 4, m);   // direction in the current view -> the previous view
            rows(m, c.pA, c.pB, c.pC);
            memcpy(c.tanF, fov8 + e * 4, sizeof(float) * 4);
            c.eyeRect[0] = e ? 0.5f : 0.f; c.eyeRect[1] = 0.5f; c.eyeRect[2] = (float)m_w; c.eyeRect[3] = (float)m_h;
            c.mode[0] = pass == 0 ? 1.f : 0.f;
            m_ctx->UpdateSubresource(I.cb, 0, nullptr, &c, 0, 0);
            D3D11_VIEWPORT vp{}; vp.TopLeftX = e ? I.flowW * 0.5f : 0.f; vp.Width = I.flowW * 0.5f; vp.Height = (float)I.flowH; vp.MaxDepth = 1.f;
            m_ctx->RSSetViewports(1, &vp);
            m_ctx->Draw(3, 0);
        }
    }
    m_ctx->PSSetShaderResources(0, 4, nulls);
    ID3D11RenderTargetView* nullRtv = nullptr; m_ctx->OMSetRenderTargets(1, &nullRtv, nullptr);
    // the engine: backward flow from the current frame to the aligned previous one
    NV_OF_EXECUTE_INPUT_PARAMS in{};
    in.inputFrame = I.hIn[1]; in.referenceFrame = I.hIn[0]; in.externalHints = nullptr; in.disableTemporalHints = NV_OF_FALSE;
    NV_OF_EXECUTE_OUTPUT_PARAMS out{};
    out.outputBuffer = I.hFlow; out.outputCostBuffer = I.hCost;
    const NV_OF_STATUS st = I.api.nvOFExecute(I.of, &in, &out);
    m_ctx->End(I.qT[1]);
    m_ctx->End(I.qDisjoint);
    I.flowTimed = true;
    if (st != NV_OF_SUCCESS) {
        stats.flowErrors++;
        char le[MIN_ERROR_STRING_SIZE + 1] = ""; uint32_t ls = MIN_ERROR_STRING_SIZE; I.api.nvOFGetLastError(I.of, le, &ls);
        char b[160]; snprintf(b, sizeof b, "nvOFExecute failed: status %d %s", (int)st, le);
        seterr(err, errlen, b);
        return -1;
    }
    return 1;
}

ID3D11Texture2D* FrameGen::Render(int mode, float t, const float* qOut4, const float* fov8, char* err, int errlen) {
    Impl& I = *m_i;
    if (m_have < 1 || !m_out) { seterr(err, errlen, "frame generation: no rendered frame yet"); return nullptr; }
    if (mode == 1 && m_have < 2) mode = 0;
    const int prev = 1 - I.cur;
    m_ctx->End(I.qT[2]);
    m_ctx->OMSetRenderTargets(1, &I.outRtv, nullptr);
    m_ctx->IASetInputLayout(nullptr); m_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    m_ctx->VSSetShader(I.vs, nullptr, 0); m_ctx->PSSetShader(I.psOut, nullptr, 0);
    m_ctx->PSSetSamplers(0, 1, &I.smp); m_ctx->PSSetConstantBuffers(0, 1, &I.cb);
    ID3D11ShaderResourceView* srvs[4] = { I.realSrv[I.cur], m_have >= 2 ? I.realSrv[prev] : I.realSrv[I.cur], I.flowOutSrv, I.costSrv };
    m_ctx->PSSetShaderResources(0, 4, srvs);
    for (int e = 0; e < 2; e++) {
        Cb c{};
        float m[9];
        relRot(I.realQ[I.cur] + e * 4, qOut4, m);           // direction in the output (stamp) view -> the current frame's view
        rows(m, c.oA, c.oB, c.oC);
        relRot(I.realQ[m_have >= 2 ? prev : I.cur] + e * 4, I.realQ[I.cur] + e * 4, m);   // current view -> previous view
        rows(m, c.pA, c.pB, c.pC);
        memcpy(c.tanF, fov8 + e * 4, sizeof(float) * 4);
        c.eyeRect[0] = e ? 0.5f : 0.f; c.eyeRect[1] = 0.5f; c.eyeRect[2] = (float)m_w; c.eyeRect[3] = (float)m_h;
        c.flowP[0] = (float)I.flowW / (float)m_w; c.flowP[1] = (float)I.flowH / (float)m_h; c.flowP[2] = (float)I.grid; c.flowP[3] = t;
        const int x0 = (e * I.flowW / 2) / I.grid, x1 = std::min(I.blocksW - 1, ((e + 1) * I.flowW / 2 - 1) / I.grid);
        c.blocks[0] = x0; c.blocks[1] = 0; c.blocks[2] = std::max(x0, x1); c.blocks[3] = I.blocksH - 1;
        c.mode[0] = mode == 1 ? 1.f : 0.f; c.mode[1] = I.cfg.maxFlowPx; c.mode[2] = I.cfg.confGain;
        m_ctx->UpdateSubresource(I.cb, 0, nullptr, &c, 0, 0);
        D3D11_VIEWPORT vp{}; vp.TopLeftX = e ? m_w * 0.5f : 0.f; vp.Width = m_w * 0.5f; vp.Height = (float)m_h; vp.MaxDepth = 1.f;
        m_ctx->RSSetViewports(1, &vp);
        m_ctx->Draw(3, 0);
    }
    ID3D11ShaderResourceView* nulls[4] = {}; m_ctx->PSSetShaderResources(0, 4, nulls);
    ID3D11RenderTargetView* nullRtv = nullptr; m_ctx->OMSetRenderTargets(1, &nullRtv, nullptr);
    m_ctx->End(I.qT[3]);
    I.synthTimed = true;
    if (mode == 1) stats.synths++; else stats.reals++;
    return m_out;
}

void FrameGen::CollectTimings() {
    Impl& I = *m_i;
    if (!I.flowTimed && !I.synthTimed) return;
    D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj{};
    if (m_ctx->GetData(I.qDisjoint, &dj, sizeof dj, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK || dj.Disjoint || !dj.Frequency) return;
    UINT64 t[4] = {};
    for (int k = 0; k < 4; k++) if (m_ctx->GetData(I.qT[k], &t[k], 8, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) return;
    const double toMs = 1000.0 / (double)dj.Frequency;
    if (I.flowTimed && t[1] > t[0]) { stats.lastFlowMs = (float)((t[1] - t[0]) * toMs); stats.flowMsSum += stats.lastFlowMs; stats.flowN++; }
    if (I.synthTimed && t[3] > t[2]) { stats.lastSynthMs = (float)((t[3] - t[2]) * toMs); stats.synthMsSum += stats.lastSynthMs; stats.synthN++; }
    I.flowTimed = I.synthTimed = false;
}

void FrameGen::ResetHistory() {
    m_have = 0;
}
