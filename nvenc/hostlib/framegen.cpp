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
#include <vector>

namespace {
const char* kShader = R"(
cbuffer CB : register(b0) {
    float4 oA, oB, oC;      // M_out rows: direction in the output view -> direction in the current frame's view
    float4 pA, pB, pC;      // M_prev rows: direction in the current frame's view -> direction in the previous frame's view
    float4 tanF;            // l r u d tangents of the eye
    float4 eyeRect;         // x: u0 of this eye in the SBS textures, y: u width, z: full-res SBS width (px), w: height (px)
    float4 flowP;           // x, y: flow input scale (small / full), z: grid, w: t
    int4   blocks;          // x0 y0 x1 y1: block bounds of this eye in the flow texture (inclusive)
    float4 mode;            // x: pass mode, y: max flow (full-res px), z: confidence gain, w: 1 = the game's vectors are bound (t4) for this eye
    float4 mvRect;          // game vectors: x, y: first texel of the used rectangle, z, w: its size (texels; the vectors are in these pixels)
    float4 mvInfo;          // x, y: unused, z, w: NGX MV scale
};
Texture2D cur : register(t0);
Texture2D prev : register(t1);
Texture2D<int2> flow : register(t2);
Texture2D<uint> cost : register(t3);
Texture2D<float4> mvec : register(t4);
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
// Catmull-Rom (bicubic) fetch of a colour texture at an SBS uv, 5 bilinear taps (corner taps dropped, weights renormalised). The output is a
// resample of the rendered frame at sub-pixel offsets on every frame (the rotation to the stamp sample, the flow displacement); bilinear
// made every output visibly softer than the game (headset session 2026-10-14). Taps are kept inside this eye's column of the SBS texture.
float3 fetchCR(Texture2D tex, float2 uvS) {
    float2 size = eyeRect.zw;
    float2 inv = 1.0 / size;
    float2 pos = uvS * size;
    float2 tc1 = floor(pos - 0.5) + 0.5;
    float2 f = pos - tc1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 off12 = w2 / w12;
    float2 t0 = (tc1 - 1.0) * inv, t3 = (tc1 + 2.0) * inv, t12 = (tc1 + off12) * inv;
    float2 lo = float2(eyeRect.x, 0) + 0.5 * inv, hi = float2(eyeRect.x + eyeRect.y, 1) - 0.5 * inv;
    float3 r = 0;
    float wsum = 0;
    float w;
    w = w12.x * w0.y;  r += tex.SampleLevel(smp, clamp(float2(t12.x, t0.y), lo, hi), 0).rgb * w;  wsum += w;
    w = w0.x * w12.y;  r += tex.SampleLevel(smp, clamp(float2(t0.x, t12.y), lo, hi), 0).rgb * w;  wsum += w;
    w = w12.x * w12.y; r += tex.SampleLevel(smp, clamp(t12, lo, hi), 0).rgb * w;                   wsum += w;
    w = w3.x * w12.y;  r += tex.SampleLevel(smp, clamp(float2(t3.x, t12.y), lo, hi), 0).rgb * w;  wsum += w;
    w = w12.x * w3.y;  r += tex.SampleLevel(smp, clamp(float2(t12.x, t3.y), lo, hi), 0).rgb * w;  wsum += w;
    return saturate(r / wsum);
}
// The game's motion vector (docs/MOTION_VECTORS.md) at an eye-local uv of the current frame B, as the residual backward flow in B's camera in
// full-res pixels (what flowAt gives from the optical flow): NGX vectors point from the pixel to where it was in the previous frame A, in
// the vectors' own pixels times the scale, and include the camera motion; the point in A is rotated into B's camera (the transpose of
// M_prev) and the flow is the displacement from there to the pixel. false where there is no usable vector (outside the view, out of range).
bool vecFlowAt(float2 uvB, out float2 f) {
    f = float2(0, 0);
    int2 tx = int2(floor(mvRect.xy + uvB * mvRect.zw));
    tx = clamp(tx, int2(mvRect.xy), int2(mvRect.xy + mvRect.zw) - int2(1, 1));
    float2 mv = mvec.Load(int3(tx, 0)).xy * mvInfo.zw;
    if (any(isnan(mv)) || any(abs(mv) > mvRect.zw)) return false;
    float2 uvA = uvB + mv / mvRect.zw;
    if (any(uvA < 0) || any(uvA > 1)) return false;
    float3 dA = dirOf(uvA);
    float3 dB = float3(pA.x * dA.x + pB.x * dA.y + pC.x * dA.z, pA.y * dA.x + pB.y * dA.y + pC.y * dA.z, pA.z * dA.x + pB.z * dA.y + pC.z * dA.z);
    float2 uvAinB;
    if (!projectTo(dB, uvAinB)) return false;
    f = uvToPx(uvAinB) - uvToPx(uvB);
    return length(f) <= mode.y;
}
// the previous frame at a point given in the current camera (eye-local uv)
bool samplePrev(float2 uvCur, out float3 c) {
    float2 uvP;
    c = float3(0, 0, 0);
    if (!projectTo(rot(pA, pB, pC, dirOf(uvCur)), uvP)) return false;
    c = fetchCR(prev, sbs(uvP));
    return true;
}
float4 PSOut(VSOut i) : SV_Target {
    float2 uvB;
    if (!projectTo(rot(oA, oB, oC, dirOf(i.uv)), uvB)) return float4(0, 0, 0, 1);
    float3 curHere = fetchCR(cur, sbs(uvB));
    if (mode.x < 0.5) return float4(curHere, 1);
    float t = flowP.w;
    float3 prevHere;
    bool havePrev = samplePrev(uvB, prevHere);
    float3 stable = havePrev ? lerp(prevHere, curHere, t) : curHere;
    float2 pB = uvToPx(uvB);
    float2 f;
    bool vec = mode.w > 0.5 && vecFlowAt(uvB, f);   // the game's vectors where they exist, the optical flow elsewhere
    if (!vec) f = flowAt(pB);
    float2 uvC = pxToUv(pB - f * t);
    float2 uvPb = pxToUv(pB + f * (1 - t));
    float3 pc;
    bool okC = all(uvC >= 0) && all(uvC <= 1);
    bool okP = all(uvPb >= 0) && all(uvPb <= 1) && samplePrev(uvPb, pc);
    if (!(okC && okP)) return float4(stable, 1);
    float3 cc = fetchCR(cur, sbs(uvC));
    float3 mid = lerp(pc, cc, t);
    float dis = max(abs(pc.r - cc.r), max(abs(pc.g - cc.g), abs(pc.b - cc.b)));
    float conf = saturate(1 - dis * mode.z) * (vec ? 1.0 : saturate(1 - costAt(pB)));
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
    float mvRect[4];
    float mvInfo[4];
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
    // the game's DLSS vectors (and depth) per history frame and eye, when the shim published them with the frame (ipc.h MotionSlot)
    ID3D11Texture2D* mvTex[2][2] = {}; ID3D11ShaderResourceView* mvSrv[2][2] = {};
    ID3D11Texture2D* depthTex[2][2] = {};
    bool mvValid[2][2] = {};
    FrameGenVectors mvInfo[2][2] = {};
    int mvDumps = 0; const char* mvDumpDir = nullptr;
    bool useVectors = true;           // VISIONALVR_FG_USE_MV=0: ingest the vectors but synthesise from the flow alone (A/B)
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
    ID3D11Query* qDisjoint = nullptr; ID3D11Query* qDisjointR = nullptr; ID3D11Query* qT[4] = {};
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
    for (int k = 0; k < 2; k++) for (int e = 0; e < 2; e++) { rel(I.mvSrv[k][e]); rel(I.mvTex[k][e]); rel(I.depthTex[k][e]); }
    rel(I.flowOutSrv); rel(I.flowOut); rel(I.costSrv); rel(I.costTex); rel(I.outRtv); rel(m_out);
    rel(I.vs); rel(I.psPrep); rel(I.psOut); rel(I.cb); rel(I.smp); rel(I.qDisjoint); rel(I.qDisjointR);
    for (auto& q : I.qT) rel(q);
    delete m_i;
}

bool FrameGen::Init(int w, int h, const FrameGenConfig& cfg, char* err, int errlen) {
    Impl& I = *m_i;
    if (const char* u = getenv("VISIONALVR_FG_USE_MV")) I.useVectors = atoi(u) != 0;
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
    m_dev->CreateQuery(&qd, &I.qDisjointR);
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
    I.mvValid[next][0] = I.mvValid[next][1] = false;   // vectors arrive with IngestVectors right after, or not at all for this frame
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

// A copy of `src` in `dst` (recreated when the size or format differs), shader-readable; `srv` optional.
static bool copyLike(ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11Texture2D* src, ID3D11Texture2D*& dst, ID3D11ShaderResourceView** srv, char* err, int errlen) {
    D3D11_TEXTURE2D_DESC sd{}; src->GetDesc(&sd);
    if (dst) {
        D3D11_TEXTURE2D_DESC dd{}; dst->GetDesc(&dd);
        if (dd.Width != sd.Width || dd.Height != sd.Height || dd.Format != sd.Format) { rel(dst); if (srv) rel(*srv); }
    }
    if (!dst) {
        D3D11_TEXTURE2D_DESC d = sd;
        d.MipLevels = 1; d.ArraySize = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE; d.CPUAccessFlags = 0; d.MiscFlags = 0;
        HRESULT hr = dev->CreateTexture2D(&d, nullptr, &dst);
        if (FAILED(hr)) { char b[96]; snprintf(b, sizeof b, "vector texture %ux%u format %u: 0x%08x", d.Width, d.Height, (unsigned)d.Format, (unsigned)hr); seterr(err, errlen, b); return false; }
        if (srv) {
            // typeless depth copies get a readable view; everything else its own format
            D3D11_SHADER_RESOURCE_VIEW_DESC vd{}; vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; vd.Texture2D.MipLevels = 1;
            vd.Format = d.Format == DXGI_FORMAT_R32_TYPELESS ? DXGI_FORMAT_R32_FLOAT : d.Format == DXGI_FORMAT_R24G8_TYPELESS ? DXGI_FORMAT_R24_UNORM_X8_TYPELESS
                      : d.Format == DXGI_FORMAT_R32G8X24_TYPELESS ? DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS : d.Format == DXGI_FORMAT_R16_TYPELESS ? DXGI_FORMAT_R16_UNORM : d.Format;
            if (FAILED(dev->CreateShaderResourceView(dst, &vd, srv))) *srv = nullptr;
        }
    }
    ctx->CopyResource(dst, src);
    return true;
}

static float halfToFloat(uint16_t h) {
    const uint32_t s = (h >> 15) & 1, e = (h >> 10) & 0x1f, m = h & 0x3ff;
    float v;
    if (e == 0) v = ldexpf((float)m, -24);
    else if (e == 31) v = m ? NAN : INFINITY;
    else v = ldexpf((float)(m + 1024), (int)e - 25);
    return s ? -v : v;
}

// Debug look at the vectors (VISIONALVR_FG_MV_DUMP=<dir>): mv_<n>_eye<e>.ppm with R/G = 128 + 4 * (vx, vy) in texture pixels, B = 128; the
// sidecar .txt holds the parameters. A GPU stall (Map) each time: the first few ingests only.
static void dumpVectors(ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11Texture2D* tex, const FrameGenVectors& v, const char* dir, int n, int eye) {
    D3D11_TEXTURE2D_DESC d{}; tex->GetDesc(&d);
    if (d.Format != DXGI_FORMAT_R16G16_FLOAT && d.Format != DXGI_FORMAT_R32G32_FLOAT) return;
    D3D11_TEXTURE2D_DESC sd = d; sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
    ID3D11Texture2D* st = nullptr;
    if (FAILED(dev->CreateTexture2D(&sd, nullptr, &st))) return;
    ctx->CopyResource(st, tex);
    D3D11_MAPPED_SUBRESOURCE m{};
    if (SUCCEEDED(ctx->Map(st, 0, D3D11_MAP_READ, 0, &m))) {
        char path[520]; snprintf(path, sizeof path, "%s/mv_%d_eye%d.ppm", dir, n, eye);
        if (FILE* f = fopen(path, "wb")) {
            fprintf(f, "P6\n%u %u\n255\n", d.Width, d.Height);
            std::vector<unsigned char> row(d.Width * 3);
            for (UINT y = 0; y < d.Height; y++) {
                const unsigned char* src = (const unsigned char*)m.pData + y * m.RowPitch;
                for (UINT x = 0; x < d.Width; x++) {
                    float vx, vy;
                    if (d.Format == DXGI_FORMAT_R16G16_FLOAT) { const uint16_t* p = (const uint16_t*)src + x * 2; vx = halfToFloat(p[0]); vy = halfToFloat(p[1]); }
                    else { const float* p = (const float*)src + x * 2; vx = p[0]; vy = p[1]; }
                    auto q = [](float a) { a = 128.f + 4.f * a; return (unsigned char)(a < 0 ? 0 : a > 255 ? 255 : a); };
                    row[x * 3] = q(vx); row[x * 3 + 1] = q(vy); row[x * 3 + 2] = 128;
                }
                fwrite(row.data(), 1, row.size(), f);
            }
            fclose(f);
        }
        snprintf(path, sizeof path, "%s/mv_%d_eye%d.txt", dir, n, eye);
        if (FILE* f = fopen(path, "w")) {
            fprintf(f, "api %u flags 0x%x frame %llu texture %ux%u format %u render %ux%u output %ux%u mv base %u,%u depth base %u,%u jitter %.4f %.4f scale %.4f %.4f reset %d\n",
                    v.api, v.flags, (unsigned long long)v.frame, d.Width, d.Height, (unsigned)d.Format, v.renderW, v.renderH, v.outputW, v.outputH, v.mvX, v.mvY, v.depthX, v.depthY,
                    v.jitterX, v.jitterY, v.mvScaleX, v.mvScaleY, v.reset);
            fclose(f);
        }
        ctx->Unmap(st, 0);
    }
    st->Release();
}

int FrameGen::IngestVectors(int eye, ID3D11Texture2D* mv, ID3D11Texture2D* depth, const FrameGenVectors& v, char* err, int errlen) {
    Impl& I = *m_i;
    if (eye < 0 || eye > 1 || !mv) { seterr(err, errlen, "bad vectors"); stats.mvErrors++; return -1; }
    if (m_have < 1) { seterr(err, errlen, "no frame to attach the vectors to"); stats.mvErrors++; return -1; }
    if (!copyLike(m_dev, m_ctx, mv, I.mvTex[I.cur][eye], &I.mvSrv[I.cur][eye], err, errlen)) { stats.mvErrors++; return -1; }
    bool hasDepth = false;
    if (depth) {
        char e2[128];
        hasDepth = copyLike(m_dev, m_ctx, depth, I.depthTex[I.cur][eye], nullptr, e2, sizeof e2);
    }
    I.mvValid[I.cur][eye] = true;
    I.mvInfo[I.cur][eye] = v;
    stats.mvEyes++;
    if (hasDepth) stats.mvDepth++;
    if (eye == 0 || !I.mvValid[I.cur][0]) stats.mvFrames++;
    D3D11_TEXTURE2D_DESC d{}; mv->GetDesc(&d);
    stats.mvW = (int)d.Width; stats.mvH = (int)d.Height; stats.mvFormat = (uint32_t)d.Format; stats.mvFlags = v.flags; stats.mvApi = v.api;
    stats.mvScale[0] = v.mvScaleX; stats.mvScale[1] = v.mvScaleY;
    if (I.mvDumps == 0 && !I.mvDumpDir) { const char* dd = getenv("VISIONALVR_FG_MV_DUMP"); I.mvDumpDir = dd && *dd ? _strdup(dd) : ""; }
    if (I.mvDumpDir && *I.mvDumpDir && I.mvDumps < 6) { dumpVectors(m_dev, m_ctx, I.mvTex[I.cur][eye], v, I.mvDumpDir, I.mvDumps / 2, eye); I.mvDumps++; }
    return 1;
}

ID3D11Texture2D* FrameGen::Render(int mode, float t, const float* qOut4, const float* fov8, char* err, int errlen) {
    Impl& I = *m_i;
    if (m_have < 1 || !m_out) { seterr(err, errlen, "frame generation: no rendered frame yet"); return nullptr; }
    if (mode == 1 && m_have < 2) mode = 0;
    const int prev = 1 - I.cur;
    if (mode == 0) {
        // The stamp sample's orientation equals the render orientation within 0.02 deg (0.7 px at 3552 px per 100 deg; the head is still): copy
        // the rendered frame as is. The resample through the rotation warp costs sharpness on every frame otherwise (headset session 2026-10-14).
        float worst = 0.f;
        for (int e = 0; e < 2; e++) {
            const float* a = I.realQ[I.cur] + e * 4;
            float d = fabsf(a[0] * qOut4[0] + a[1] * qOut4[1] + a[2] * qOut4[2] + a[3] * qOut4[3]);
            worst = std::max(worst, 2.f * acosf(std::min(1.f, d)) * 57.29578f);
        }
        if (worst < I.cfg.copyDeg) {
            m_ctx->CopyResource(m_out, I.real[I.cur]);
            stats.reals++; stats.copies++;
            return m_out;
        }
    }
    m_ctx->Begin(I.qDisjointR);
    m_ctx->End(I.qT[2]);
    m_ctx->OMSetRenderTargets(1, &I.outRtv, nullptr);
    m_ctx->IASetInputLayout(nullptr); m_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    m_ctx->VSSetShader(I.vs, nullptr, 0); m_ctx->PSSetShader(I.psOut, nullptr, 0);
    m_ctx->PSSetSamplers(0, 1, &I.smp); m_ctx->PSSetConstantBuffers(0, 1, &I.cb);
    ID3D11ShaderResourceView* srvs[4] = { I.realSrv[I.cur], m_have >= 2 ? I.realSrv[prev] : I.realSrv[I.cur], I.flowOutSrv, I.costSrv };
    m_ctx->PSSetShaderResources(0, 4, srvs);
    for (int e = 0; e < 2; e++) {
        Cb c{};
        // the game's vectors of the current frame for this eye (docs/MOTION_VECTORS.md), when the shim published them
        ID3D11ShaderResourceView* mvSrv = (mode == 1 && I.useVectors && I.mvValid[I.cur][e]) ? I.mvSrv[I.cur][e] : nullptr;
        m_ctx->PSSetShaderResources(4, 1, &mvSrv);
        if (mvSrv) {
            const FrameGenVectors& v = I.mvInfo[I.cur][e];
            const bool lowRes = (v.flags & 2u) != 0;   // MVLowRes: vectors at the render size, else at the output size
            c.mvRect[0] = (float)v.mvX; c.mvRect[1] = (float)v.mvY;
            c.mvRect[2] = (float)(lowRes ? v.renderW : v.outputW); c.mvRect[3] = (float)(lowRes ? v.renderH : v.outputH);
            c.mvInfo[2] = v.mvScaleX; c.mvInfo[3] = v.mvScaleY;
            c.mode[3] = 1.f;
            stats.mvUsedEyes++;
        }
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
    ID3D11ShaderResourceView* nulls[5] = {}; m_ctx->PSSetShaderResources(0, 5, nulls);
    ID3D11RenderTargetView* nullRtv = nullptr; m_ctx->OMSetRenderTargets(1, &nullRtv, nullptr);
    m_ctx->End(I.qT[3]);
    m_ctx->End(I.qDisjointR);
    I.synthTimed = true;
    if (mode == 1) stats.synths++; else stats.reals++;
    return m_out;
}

void FrameGen::CollectTimings() {
    Impl& I = *m_i;
    auto pair = [&](ID3D11Query* dis, ID3D11Query* a, ID3D11Query* b, float* last, double* sum, uint64_t* n) {
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj{};
        if (m_ctx->GetData(dis, &dj, sizeof dj, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) return false;
        UINT64 t0 = 0, t1 = 0;
        if (m_ctx->GetData(a, &t0, 8, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK || m_ctx->GetData(b, &t1, 8, D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) return false;
        if (!dj.Disjoint && dj.Frequency && t1 > t0) { *last = (float)((t1 - t0) * 1000.0 / (double)dj.Frequency); *sum += *last; (*n)++; }
        return true;
    };
    if (I.flowTimed && pair(I.qDisjoint, I.qT[0], I.qT[1], &stats.lastFlowMs, &stats.flowMsSum, &stats.flowN)) I.flowTimed = false;
    if (I.synthTimed && pair(I.qDisjointR, I.qT[2], I.qT[3], &stats.lastSynthMs, &stats.synthMsSum, &stats.synthN)) I.synthTimed = false;
}

void FrameGen::ResetHistory() {
    m_have = 0;
    for (int k = 0; k < 2; k++) m_i->mvValid[k][0] = m_i->mvValid[k][1] = false;
}
