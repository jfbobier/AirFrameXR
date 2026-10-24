// Frame generation (see framegen.h and docs/FRAME_GENERATION.md).
//
// Passes per rendered frame B (Ingest):   copy B into the history
// Passes per pair (first synthetic output): prep: A seen from B's camera and B, both downscaled to the flow size | optical-flow engine:
//                                         backward flow B -> A' (+ cost) on a block grid | edge only: our luma cost per block, 3x3 median
// Passes per output (Render):             one full-screen pass per eye: output pixel -> direction in the stamp pose -> B's camera; mode 0
//                                         samples B there; mode 1 synthesises the frame at t between A and B: edge (default) solves the
//                                         vector where the content comes from, picks the candidate both frames agree on at motion
//                                         boundaries and shrinks doubtful motion toward B; classic samples A' and B at the two ends of the
//                                         flow vector with a confidence from the colour disagreement and the engine's cost, falling back to
//                                         the crossfade of the rotation-aligned A and B (ghosting, not holes).
#define NOMINMAX
#include "framegen.h"
#include <windows.h>
#include <d3dcompiler.h>
#include "nvOpticalFlowD3D11.h"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
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
    float4 mode;            // x: pass mode, y: max flow (full-res px), z: confidence gain
    float4 flowIn;          // PSUnpack: x flow-input width of one eye, y flow-input height, z max flow (full-res px)
    float4 synth;           // output pass: x 1 = edge-aware synthesis (else the classic crossfade recipe), y motion limit (full-res px), z cost scale, w photometric scale
};
Texture2D cur : register(t0);
Texture2D prev : register(t1);
Texture2D<int2> flow : register(t2);
Texture2D<uint> cost : register(t3);
Texture2D<float4> filt : register(t4);   // the pair's filtered flow (PSFilter): xy backward flow (full-res px), z match cost; PSFilter reads PSUnpack's output here
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
// the previous frame at a point given in the current camera (eye-local uv)
bool samplePrev(float2 uvCur, out float3 c) {
    float2 uvP;
    c = float3(0, 0, 0);
    if (!projectTo(rot(pA, pB, pC, dirOf(uvCur)), uvP)) return false;
    c = fetchCR(prev, sbs(uvP));
    return true;
}
)"
// MSVC caps one string literal at 16 KB: the shader continues in a second literal
R"(// ---- edge-aware synthesis (ideas from VectorXR, MPL-2.0, github.com/DienerTech/vectorxr; re-implemented, no VectorXR code) -------------
// PSUnpack (per flow block, blocksW x blocksH): the engine's vector in full-res px (xy) and our own match cost (z): the mean luma difference of
// the block's flow-input texels against the aligned previous frame at the vector, plus a small motion penalty. The engine's cost byte has no
// fixed scale; a luma cost lets one threshold hold. t0 = the current frame (flow input), t1 = the aligned previous one, t2 = the engine's flow.
// blocks: x first block of eye 1, y blocksW, z blocksH, w grid; flowIn: x flow-input width of one eye, y flow-input height, z max flow (full-res px).
float lumaOf(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
float4 PSUnpack(VSOut i) : SV_Target {
    int2 c = int2(i.pos.xy);
    int grid = blocks.w;
    int eyeW = int(flowIn.x), eyeH = int(flowIn.y);
    int x0 = c.x >= blocks.x ? eyeW : 0;
    float2 d = float2(flow.Load(int3(c, 0))) * (1.0 / 32.0);   // backward, flow-input px: content at q in the current frame is at q + d in the previous
    float sum = 0, zero = 0, n = 0;
    for (int y = 0; y < grid; y++) {
        for (int x = 0; x < grid; x++) {
            int2 q = c * grid + int2(x, y);
            if (q.x - x0 >= eyeW || q.y >= eyeH) continue;
            // the previous frame at the exact sub-texel vector: a whole-texel offset alone costs ~0.1 at a hard edge moving a fraction of a
            // texel, which zeroed the confidence of correct vectors (rig marker drawn at the real frame's position: judder, 2026-10-24)
            float2 sp = float2(q) + 0.5 + d;
            float lc = lumaOf(cur.Load(int3(q, 0)).rgb);
            bool outside = sp.x < x0 || sp.x > x0 + eyeW || sp.y < 0 || sp.y > eyeH;
            sum += outside ? 0.25 : abs(lc - lumaOf(prev.SampleLevel(smp, clamp(sp, float2(x0 + 0.5, 0.5), float2(x0 + eyeW - 0.5, eyeH - 0.5)) / float2(2 * eyeW, eyeH), 0).rgb));
            zero += abs(lc - lumaOf(prev.Load(int3(q, 0)).rgb));
            n += 1;
        }
    }
    float cst = sum / max(n, 1.0) + 0.05 * length(d) / float(eyeW);
    zero /= max(n, 1.0);
    // a vector must beat no motion: on flat content (sky, a black background) the engine returns noise vectors that match as well as
    // standing still, and they carry thin detail nearby to the wrong place (the rig's dots, 2026-10-24)
    if (zero <= cst + 0.004) return float4(0, 0, zero, 1);
    float2 f = d / flowP.xy;
    float m = length(f);
    if (m > flowIn.z) f *= flowIn.z / m;
    return float4(f, cst, 1);
}
// PSFilter: cost-weighted vector median over the 3x3 blocks around (within the eye): an isolated wrong vector loses to its neighbours without
// the averaging that would blur a motion boundary. t7 = PSUnpack's output.
float4 PSFilter(VSOut i) : SV_Target {
    int2 c = int2(i.pos.xy);
    int lo = c.x >= blocks.x ? blocks.x : 0, hi = c.x >= blocks.x ? blocks.y - 1 : blocks.x - 1;
    float4 s[9];
    [unroll] for (int k = 0; k < 9; k++) {
        int2 q = c + int2(k % 3 - 1, k / 3 - 1);
        q = int2(clamp(q.x, lo, hi), clamp(q.y, 0, blocks.z - 1));
        s[k] = filt.Load(int3(q, 0));
    }
    int best = 4;
    float bestScore = 1e30;
    [unroll] for (int a = 0; a < 9; a++) {
        float score = 0;
        [unroll] for (int b = 0; b < 9; b++) score += length(s[a].xy - s[b].xy) / (s[b].z + 0.01);
        if (score < bestScore) { bestScore = score; best = a; }
    }
    return float4(s[best].xy, min(s[best].z, s[4].z), 1);
}
// the filtered flow at a full-res SBS pixel, bilinear over the block grid (within this eye's blocks)
float4 filtAt(float2 px) {
    float2 fc = (px * flowP.xy + 0.5) / flowP.z - 0.5;
    fc = clamp(fc, float2(blocks.xy), float2(blocks.zw));
    int2 tl = int2(floor(fc));
    int2 br = min(tl + int2(1, 1), blocks.zw);
    float2 fr = fc - float2(tl);
    return lerp(lerp(filt.Load(int3(tl, 0)), filt.Load(int3(br.x, tl.y, 0)), fr.x), lerp(filt.Load(int3(tl.x, br.y, 0)), filt.Load(int3(br, 0)), fr.x), fr.y);
}
float maxDiff(float3 a, float3 b) { float3 d = abs(a - b); return max(d.r, max(d.g, d.b)); }
// how differently the two frames see the content candidate f brings to pO at time t (s = 1 - t): B at pO - f s, A at pO - f s + f
float candidateError(float2 pO, float2 f, float s) {
    float2 uvC = pxToUv(pO - f * s);
    float2 uvA = pxToUv(pO - f * s + f);
    if (any(uvC < 0) || any(uvC > 1)) return 1;
    float2 uvP;
    if (!projectTo(rot(pA, pB, pC, dirOf(uvA)), uvP)) return 1;
    return maxDiff(cur.SampleLevel(smp, sbs(uvC), 0).rgb, prev.SampleLevel(smp, sbs(uvP), 0).rgb);
}
// One output pixel at pO (B's camera): the vector is solved where the content comes from (two fixed-point steps: the flow at the output pixel
// belongs to whatever B shows there, not to what moves onto it); at a motion boundary (the block vectors around disagree by more than half a
// pixel) the candidate both frames agree on wins, which gives pixel-sharp edges instead of a smear of the block vectors; doubtful motion
// shrinks toward the still image of B (local judder) instead of crossfading two positions (a double image); A is blended in only where it
// agrees with B at the matched points.
float3 edgeSynth(float2 pO, float t) {
    float s = 1 - t;
    float4 here = filtAt(pO);
    float4 sol = filtAt(pO - here.xy * s);
    sol = filtAt(pO - sol.xy * s);
    float4 cand[7];
    cand[0] = sol; cand[1] = here;
    float2 fc = clamp((pO * flowP.xy + 0.5) / flowP.z - 0.5, float2(blocks.xy), float2(blocks.zw));
    int2 base = int2(floor(fc));
    [unroll] for (int k = 0; k < 4; k++) cand[2 + k] = filt.Load(int3(min(base + int2(k & 1, k >> 1), blocks.zw), 0));
    cand[6] = float4(0, 0, here.z, 1);
    float4 chosen = cand[0];
    float spread = 0;
    [unroll] for (int j = 1; j < 6; j++) spread = max(spread, length(cand[j].xy - chosen.xy));
    [branch] if (spread > 0.5) {
        float best = candidateError(pO, chosen.xy, s);
        [unroll] for (int k = 1; k < 7; k++) {
            float e = candidateError(pO, cand[k].xy, s) + (k == 6 ? 0.03 : 0.01);
            if (e < best) { best = e; chosen = cand[k]; }
        }
    }
    float shift = length(chosen.xy * s);
    // no agreement term on the confidence (VectorXR has none when interpolating): every pixel along a moving hard edge is a motion boundary
    // whose bilinear samples always disagree a little, and shrinking those vectors held the edges back (rig marker position error x2,
    // 2026-10-24). Noise vectors on flat content are zeroed in PSUnpack instead.
    float conf = saturate((synth.y - shift) / (0.25 * synth.y)) * saturate(1 - max(chosen.z - 0.02, 0) * synth.z);
    float2 f = chosen.xy * conf;
    float2 uvC = pxToUv(pO - f * s);
    float3 cc = fetchCR(cur, sbs(clamp(uvC, 0, 1)));
    float3 pc;
    if (!samplePrev(pxToUv(pO - f * s + f), pc)) return cc;
    // A is blended in by the time weight; the photometric gate applies only as far as the vector is doubted: a trusted vector's two ends,
    // blended, cancel its small error (each end is off the other way), and gating them at hard edges (they always differ a little there)
    // left the error in (rig marker position error x2, 2026-10-24); a doubted, shrunk vector blends only where the frames agree (no double image)
    float photo = lerp(saturate(1 - max(maxDiff(pc, cc) - 0.03, 0) * synth.w), 1, conf);
    return lerp(cc, pc, s * photo);
}
float4 PSOut(VSOut i) : SV_Target {
    float2 uvB;
    if (!projectTo(rot(oA, oB, oC, dirOf(i.uv)), uvB)) return float4(0, 0, 0, 1);
    [branch] if (mode.x > 0.5 && synth.x > 0.5) return float4(edgeSynth(uvToPx(uvB), flowP.w), 1);
    float3 curHere = fetchCR(cur, sbs(uvB));
    if (mode.x < 0.5) return float4(curHere, 1);
    float t = flowP.w;
    float3 prevHere;
    bool havePrev = samplePrev(uvB, prevHere);
    float3 stable = havePrev ? lerp(prevHere, curHere, t) : curHere;
    float2 pB = uvToPx(uvB);
    float2 f = flowAt(pB);
    // t = 0 is the previous frame A, 1 the newest B. The content shown at pB comes from B-pixel b = pB - f (1 - t) and from its A position b + f = pB + f t
    // (the half-rate mode only ever used t = 0.5, where this and the swapped form coincide; the uncapped mode exposed the swap: JUDDER, 2026-10-15).
    float2 uvC = pxToUv(pB - f * (1 - t));
    float2 uvPb = pxToUv(pB + f * t);
    float3 pc;
    bool okC = all(uvC >= 0) && all(uvC <= 1);
    bool okP = all(uvPb >= 0) && all(uvPb <= 1) && samplePrev(uvPb, pc);
    if (!(okC && okP)) return float4(stable, 1);
    float3 cc = fetchCR(cur, sbs(uvC));
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
    float flowIn[4];
    float synth[4];
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
    // frame history: a ring of kHist rendered frames and their per-eye render orientations; pair k = (k-1, k) has its flow in pairFlow[k]
    static constexpr int H = FrameGen::kHist;
    ID3D11Texture2D* real[H] = {}; ID3D11ShaderResourceView* realSrv[H] = {};
    float realQ[H][8] = {};
    ID3D11Texture2D* pairFlow[H] = {}; ID3D11ShaderResourceView* pairFlowSrv[H] = {};
    ID3D11Texture2D* pairCost[H] = {}; ID3D11ShaderResourceView* pairCostSrv[H] = {};
    // edge-aware synthesis (cfg.synth 1): the engine's vectors with our luma cost (unpack), then the 3x3 median (filter) per history pair
    ID3D11PixelShader* psUnpack = nullptr; ID3D11PixelShader* psFilter = nullptr;
    ID3D11Texture2D* unpackTex = nullptr; ID3D11ShaderResourceView* unpackSrv = nullptr; ID3D11RenderTargetView* unpackRtv = nullptr;
    ID3D11Texture2D* pairFilt[H] = {}; ID3D11ShaderResourceView* pairFiltSrv[H] = {}; ID3D11RenderTargetView* pairFiltRtv[H] = {};
    ID3D11Texture2D* zeroFilt = nullptr; ID3D11ShaderResourceView* zeroFiltSrv = nullptr;   // no flow: zero motion at the maximum cost
    // the pair's flow is computed by the first render that needs it (a synthetic output), not at ingest
    bool pairFlowValid[H] = {};
    ID3D11Texture2D* zeroFlow = nullptr; ID3D11ShaderResourceView* zeroFlowSrv = nullptr;   // flow-less synthesis: zero flow, maximum cost
    ID3D11Texture2D* maxCost = nullptr; ID3D11ShaderResourceView* maxCostSrv = nullptr;
    int cur = 0;                      // ring index of the newest rendered frame in real[]
    DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN;
    ID3D11RenderTargetView* outRtv = nullptr;
    // optical flow
    HMODULE dll = nullptr;
    NV_OF_D3D11_API_FUNCTION_LIST api{};
    NvOFHandle of = nullptr;
    int flowW = 0, flowH = 0, blocksW = 0, blocksH = 0, grid = 4;
    ID3D11Texture2D* flowIn[2] = {}; ID3D11RenderTargetView* flowInRtv[2] = {}; ID3D11ShaderResourceView* flowInSrv[2] = {};
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
    for (int k = 0; k < 2; k++) { rel(I.flowInRtv[k]); rel(I.flowInSrv[k]); rel(I.flowIn[k]); }
    for (int k = 0; k < Impl::H; k++) { rel(I.pairFiltSrv[k]); rel(I.pairFiltRtv[k]); rel(I.pairFilt[k]); }
    rel(I.unpackSrv); rel(I.unpackRtv); rel(I.unpackTex); rel(I.zeroFiltSrv); rel(I.zeroFilt); rel(I.psUnpack); rel(I.psFilter);
    for (int k = 0; k < Impl::H; k++) { rel(I.realSrv[k]); rel(I.real[k]); rel(I.pairFlowSrv[k]); rel(I.pairFlow[k]); rel(I.pairCostSrv[k]); rel(I.pairCost[k]); }
    rel(I.flowOutSrv); rel(I.flowOut); rel(I.costSrv); rel(I.costTex); rel(I.outRtv); rel(m_out);
    rel(I.zeroFlowSrv); rel(I.zeroFlow); rel(I.maxCostSrv); rel(I.maxCost);
    rel(I.vs); rel(I.psPrep); rel(I.psOut); rel(I.cb); rel(I.smp); rel(I.qDisjoint); rel(I.qDisjointR);
    for (auto& q : I.qT) rel(q);
    delete m_i;
}

bool FrameGen::Init(int w, int h, const FrameGenConfig& cfg, char* err, int errlen) {
    Impl& I = *m_i;
    I.cfg = cfg;
    m_w = w; m_h = h;
    // shaders
    ID3DBlob *vb = nullptr, *p1 = nullptr, *p2 = nullptr, *p5 = nullptr, *p6 = nullptr, *e = nullptr;
    if (FAILED(D3DCompile(kShader, strlen(kShader), "framegen", nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vb, &e)) ||
        FAILED(D3DCompile(kShader, strlen(kShader), "framegen", nullptr, nullptr, "PSPrep", "ps_5_0", 0, 0, &p1, &e)) ||
        FAILED(D3DCompile(kShader, strlen(kShader), "framegen", nullptr, nullptr, "PSOut", "ps_5_0", 0, 0, &p2, &e)) ||
        FAILED(D3DCompile(kShader, strlen(kShader), "framegen", nullptr, nullptr, "PSUnpack", "ps_5_0", 0, 0, &p5, &e)) ||
        FAILED(D3DCompile(kShader, strlen(kShader), "framegen", nullptr, nullptr, "PSFilter", "ps_5_0", 0, 0, &p6, &e))) {
        seterr(err, errlen, e ? (const char*)e->GetBufferPointer() : "frame generation shader compile failed");
        if (e) e->Release();
        return false;
    }
    m_dev->CreateVertexShader(vb->GetBufferPointer(), vb->GetBufferSize(), nullptr, &I.vs);
    m_dev->CreatePixelShader(p1->GetBufferPointer(), p1->GetBufferSize(), nullptr, &I.psPrep);
    m_dev->CreatePixelShader(p2->GetBufferPointer(), p2->GetBufferSize(), nullptr, &I.psOut);
    m_dev->CreatePixelShader(p5->GetBufferPointer(), p5->GetBufferSize(), nullptr, &I.psUnpack);
    m_dev->CreatePixelShader(p6->GetBufferPointer(), p6->GetBufferSize(), nullptr, &I.psFilter);
    vb->Release(); p1->Release(); p2->Release(); p5->Release(); p6->Release();
    D3D11_BUFFER_DESC bd{}; bd.ByteWidth = sizeof(Cb); bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    m_dev->CreateBuffer(&bd, nullptr, &I.cb);
    D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR; sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; sd.MaxLOD = D3D11_FLOAT32_MAX;
    m_dev->CreateSamplerState(&sd, &I.smp);
    if (!I.vs || !I.psPrep || !I.psOut || !I.psUnpack || !I.psFilter || !I.cb || !I.smp) { seterr(err, errlen, "frame generation pipeline objects"); return false; }
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
            FAILED(m_dev->CreateRenderTargetView(I.flowIn[k], nullptr, &I.flowInRtv[k])) ||
            FAILED(m_dev->CreateShaderResourceView(I.flowIn[k], nullptr, &I.flowInSrv[k]))) { seterr(err, errlen, "flow input texture"); return false; }
        if (I.api.nvOFRegisterResourceD3D11(I.of, I.flowIn[k], &I.hIn[k]) != NV_OF_SUCCESS) { seterr(err, errlen, "nvOFRegisterResourceD3D11 (input)"); return false; }
    }
    if (!tex(I.blocksW, I.blocksH, DXGI_FORMAT_R16G16_SINT, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET | D3D11_BIND_UNORDERED_ACCESS, &I.flowOut) ||
        FAILED(m_dev->CreateShaderResourceView(I.flowOut, nullptr, &I.flowOutSrv))) { seterr(err, errlen, "flow output texture"); return false; }
    if (I.api.nvOFRegisterResourceD3D11(I.of, I.flowOut, &I.hFlow) != NV_OF_SUCCESS) { seterr(err, errlen, "nvOFRegisterResourceD3D11 (flow)"); return false; }
    if (!tex(I.blocksW, I.blocksH, DXGI_FORMAT_R32_UINT, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET | D3D11_BIND_UNORDERED_ACCESS, &I.costTex) ||
        FAILED(m_dev->CreateShaderResourceView(I.costTex, nullptr, &I.costSrv))) { seterr(err, errlen, "flow cost texture"); return false; }
    if (I.api.nvOFRegisterResourceD3D11(I.of, I.costTex, &I.hCost) != NV_OF_SUCCESS) { seterr(err, errlen, "nvOFRegisterResourceD3D11 (cost)"); return false; }
    {
        // flow-less synthesis (the engine failed): zero flow and the maximum cost bound to the flow slots, so the output pass falls back to
        // the crossfade (classic) or to the rendered frame (edge)
        std::vector<uint8_t> z((size_t)I.blocksW * I.blocksH * 4, 0), mx((size_t)I.blocksW * I.blocksH * 4, 0xFF);
        D3D11_TEXTURE2D_DESC zd{}; zd.Width = I.blocksW; zd.Height = I.blocksH; zd.MipLevels = 1; zd.ArraySize = 1; zd.SampleDesc.Count = 1;
        zd.Usage = D3D11_USAGE_IMMUTABLE; zd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA zi{ z.data(), (UINT)(I.blocksW * 4), 0 }, mi{ mx.data(), (UINT)(I.blocksW * 4), 0 };
        zd.Format = DXGI_FORMAT_R16G16_SINT;
        if (FAILED(m_dev->CreateTexture2D(&zd, &zi, &I.zeroFlow)) || FAILED(m_dev->CreateShaderResourceView(I.zeroFlow, nullptr, &I.zeroFlowSrv))) { seterr(err, errlen, "zero flow texture"); return false; }
        zd.Format = DXGI_FORMAT_R32_UINT;
        if (FAILED(m_dev->CreateTexture2D(&zd, &mi, &I.maxCost)) || FAILED(m_dev->CreateShaderResourceView(I.maxCost, nullptr, &I.maxCostSrv))) { seterr(err, errlen, "max cost texture"); return false; }
    }
    for (int k = 0; k < Impl::H; k++) {   // the flow of each history pair (the engine writes one output; it is copied per pair)
        if (!tex(I.blocksW, I.blocksH, DXGI_FORMAT_R16G16_SINT, D3D11_BIND_SHADER_RESOURCE, &I.pairFlow[k]) || FAILED(m_dev->CreateShaderResourceView(I.pairFlow[k], nullptr, &I.pairFlowSrv[k])) ||
            !tex(I.blocksW, I.blocksH, DXGI_FORMAT_R32_UINT, D3D11_BIND_SHADER_RESOURCE, &I.pairCost[k]) || FAILED(m_dev->CreateShaderResourceView(I.pairCost[k], nullptr, &I.pairCostSrv[k]))) {
            seterr(err, errlen, "pair flow textures"); return false;
        }
    }
    {
        auto filtTex = [&](ID3D11Texture2D** t, ID3D11ShaderResourceView** sv, ID3D11RenderTargetView** rt) {
            return tex(I.blocksW, I.blocksH, DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET, t) &&
                   SUCCEEDED(m_dev->CreateShaderResourceView(*t, nullptr, sv)) && SUCCEEDED(m_dev->CreateRenderTargetView(*t, nullptr, rt));
        };
        bool ok = filtTex(&I.unpackTex, &I.unpackSrv, &I.unpackRtv);
        for (int k = 0; k < Impl::H && ok; k++) ok = filtTex(&I.pairFilt[k], &I.pairFiltSrv[k], &I.pairFiltRtv[k]);
        // half floats (0, 0, 1, 1): zero motion, cost 1
        std::vector<uint16_t> zf((size_t)I.blocksW * I.blocksH * 4, 0);
        for (size_t k = 0; k < zf.size(); k += 4) { zf[k + 2] = 0x3C00; zf[k + 3] = 0x3C00; }
        D3D11_TEXTURE2D_DESC zd{}; zd.Width = I.blocksW; zd.Height = I.blocksH; zd.MipLevels = 1; zd.ArraySize = 1; zd.SampleDesc.Count = 1;
        zd.Usage = D3D11_USAGE_IMMUTABLE; zd.BindFlags = D3D11_BIND_SHADER_RESOURCE; zd.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        D3D11_SUBRESOURCE_DATA zi{ zf.data(), (UINT)(I.blocksW * 8), 0 };
        ok = ok && SUCCEEDED(m_dev->CreateTexture2D(&zd, &zi, &I.zeroFilt)) && SUCCEEDED(m_dev->CreateShaderResourceView(I.zeroFilt, nullptr, &I.zeroFiltSrv));
        if (!ok) { seterr(err, errlen, "filtered flow textures"); return false; }
    }
    stats.flowW = I.flowW; stats.flowH = I.flowH; stats.blocksW = I.blocksW; stats.blocksH = I.blocksH;
    stats.nvofReady = true;
    snprintf(stats.nvofInfo, sizeof stats.nvofInfo, "NVOF API %d.%d, flow input %dx%d, grid %d (%dx%d vectors), perf %d, frame %dx%d, synthesis %s",
             NV_OF_API_MAJOR_VERSION, NV_OF_API_MINOR_VERSION, I.flowW, I.flowH, I.grid, I.blocksW, I.blocksH, cfg.perfLevel, w, h, !cfg.synth ? "classic" : cfg.photoScale > 0 ? "edge" : "edge-blend");
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
    for (int k = 0; k < Impl::H; k++) { rel(I.realSrv[k]); rel(I.real[k]); }
    rel(I.outRtv); rel(m_out);
    m_have = 0;
    D3D11_TEXTURE2D_DESC td{}; td.Width = ld.Width; td.Height = ld.Height; td.MipLevels = 1; td.ArraySize = 1; td.Format = ld.Format; td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    for (int k = 0; k < Impl::H; k++) {
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
    const int next = m_have == 0 ? 0 : (I.cur + 1) % Impl::H;
    m_ctx->CopyResource(I.real[next], src);
    memcpy(I.realQ[next], qRender8, sizeof(float) * 8);
    memcpy(I.fov8, fov8, sizeof(float) * 8);
    I.cur = next;
    m_have = std::min(Impl::H, m_have + 1);
    stats.ingests++;
    if (m_have < 2) return 0;
    stats.pairs++;
    I.pairFlowValid[I.cur] = false;   // computed by the first render of this pair that needs it (computeFlow)
    return 1;
}

// The optical flow of pair (n-1, n), the previous frame aligned into the current camera first: run by the first synthetic render of the pair.
bool FrameGen::computeFlow(int n, char* err, int errlen) {
    Impl& I = *m_i;
    const float* fov8 = I.fov8;
    const int prev = (n + Impl::H - 1) % Impl::H;
    // prep: flowIn[0] = the previous frame seen from the current camera, flowIn[1] = the current frame (both downscaled)
    m_ctx->Begin(I.qDisjoint);
    m_ctx->End(I.qT[0]);
    m_ctx->IASetInputLayout(nullptr); m_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    m_ctx->VSSetShader(I.vs, nullptr, 0); m_ctx->PSSetShader(I.psPrep, nullptr, 0);
    m_ctx->PSSetSamplers(0, 1, &I.smp); m_ctx->PSSetConstantBuffers(0, 1, &I.cb);
    ID3D11ShaderResourceView* nulls[4] = {};
    for (int pass = 0; pass < 2; pass++) {   // 0: previous (rotated), 1: current
        const int frame = pass == 0 ? prev : n;
        m_ctx->OMSetRenderTargets(1, &I.flowInRtv[pass], nullptr);
        m_ctx->PSSetShaderResources(0, 1, &I.realSrv[frame]);
        for (int e = 0; e < 2; e++) {
            Cb c{};
            float m[9];
            relRot(I.realQ[prev] + e * 4, I.realQ[n] + e * 4, m);   // direction in the current view -> the previous view
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
    if (st == NV_OF_SUCCESS) { m_ctx->CopyResource(I.pairFlow[n], I.flowOut); m_ctx->CopyResource(I.pairCost[n], I.costTex); I.pairFlowValid[n] = true; }
    if (st == NV_OF_SUCCESS && I.cfg.synth) {
        // edge-aware synthesis: the vectors with our luma cost, then the 3x3 median, into this pair's filtered flow
        Cb c{};
        c.flowP[0] = (float)I.flowW / (float)m_w; c.flowP[1] = (float)I.flowH / (float)m_h;
        c.blocks[0] = I.blocksW / 2; c.blocks[1] = I.blocksW; c.blocks[2] = I.blocksH; c.blocks[3] = I.grid;
        c.flowIn[0] = (float)(I.flowW / 2); c.flowIn[1] = (float)I.flowH; c.flowIn[2] = I.cfg.maxFlowPx;
        m_ctx->UpdateSubresource(I.cb, 0, nullptr, &c, 0, 0);
        D3D11_VIEWPORT vp{}; vp.Width = (float)I.blocksW; vp.Height = (float)I.blocksH; vp.MaxDepth = 1.f;
        m_ctx->RSSetViewports(1, &vp);
        ID3D11ShaderResourceView* in3[3] = { I.flowInSrv[1], I.flowInSrv[0], I.flowOutSrv };   // t0 cur = the current frame, t1 prev = the aligned previous
        m_ctx->OMSetRenderTargets(1, &I.unpackRtv, nullptr);
        m_ctx->PSSetShader(I.psUnpack, nullptr, 0);
        m_ctx->PSSetShaderResources(0, 3, in3);
        m_ctx->Draw(3, 0);
        m_ctx->PSSetShaderResources(0, 3, nulls);
        m_ctx->OMSetRenderTargets(1, &I.pairFiltRtv[n], nullptr);
        m_ctx->PSSetShader(I.psFilter, nullptr, 0);
        m_ctx->PSSetShaderResources(4, 1, &I.unpackSrv);
        m_ctx->Draw(3, 0);
        m_ctx->PSSetShaderResources(4, 1, nulls);
        m_ctx->OMSetRenderTargets(1, &nullRtv, nullptr);
    }
    m_ctx->End(I.qT[1]);
    m_ctx->End(I.qDisjoint);
    I.flowTimed = true;
    if (st != NV_OF_SUCCESS) {
        stats.flowErrors++;
        char le[MIN_ERROR_STRING_SIZE + 1] = ""; uint32_t ls = MIN_ERROR_STRING_SIZE; I.api.nvOFGetLastError(I.of, le, &ls);
        char b[160]; snprintf(b, sizeof b, "nvOFExecute failed: status %d %s", (int)st, le);
        seterr(err, errlen, b);
        return false;
    }
    return true;
}

ID3D11Texture2D* FrameGen::Render(int mode, float t, int age, const float* qOut4, const float* fov8, char* err, int errlen) {
    Impl& I = *m_i;
    if (m_have < 1 || !m_out) { seterr(err, errlen, "frame generation: no rendered frame yet"); return nullptr; }
    age = std::max(0, std::min(age, m_have - 1));
    if (mode == 1 && m_have < age + 2) mode = 0;
    const int n = (I.cur + Impl::H - age) % Impl::H;        // the pair's newer frame
    const int prev = (n + Impl::H - 1) % Impl::H;           // its predecessor (valid when m_have >= age + 2)
    const bool havePair = m_have >= age + 2;
    if (mode == 0) {
        // The stamp sample's orientation equals the render orientation within 0.02 deg (0.7 px at 3552 px per 100 deg; the head is still): copy
        // the rendered frame as is. The resample through the rotation warp costs sharpness on every frame otherwise (headset session 2026-10-14).
        float worst = 0.f;
        for (int e = 0; e < 2; e++) {
            const float* a = I.realQ[n] + e * 4;
            float d = fabsf(a[0] * qOut4[0] + a[1] * qOut4[1] + a[2] * qOut4[2] + a[3] * qOut4[3]);
            worst = std::max(worst, 2.f * acosf(std::min(1.f, d)) * 57.29578f);
        }
        if (worst < I.cfg.copyDeg) {
            m_ctx->CopyResource(m_out, I.real[n]);
            stats.reals++; stats.copies++;
            return m_out;
        }
    }
    // the pair's optical flow, once, by the first synthetic output that needs it
    if (mode == 1 && havePair && !I.pairFlowValid[n]) {
        char e2[160];
        computeFlow(n, e2, sizeof e2);   // a failure leaves the zero flow bound below: the classic synthesis's crossfade fallback
    }
    m_ctx->Begin(I.qDisjointR);
    m_ctx->End(I.qT[2]);
    m_ctx->OMSetRenderTargets(1, &I.outRtv, nullptr);
    m_ctx->IASetInputLayout(nullptr); m_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    m_ctx->VSSetShader(I.vs, nullptr, 0); m_ctx->PSSetShader(I.psOut, nullptr, 0);
    m_ctx->PSSetSamplers(0, 1, &I.smp); m_ctx->PSSetConstantBuffers(0, 1, &I.cb);
    const bool flowOk = havePair && I.pairFlowValid[n];
    ID3D11ShaderResourceView* srvs[4] = { I.realSrv[n], havePair ? I.realSrv[prev] : I.realSrv[n], flowOk ? I.pairFlowSrv[n] : I.zeroFlowSrv, flowOk ? I.pairCostSrv[n] : I.maxCostSrv };
    m_ctx->PSSetShaderResources(0, 4, srvs);
    m_ctx->PSSetShaderResources(4, 1, flowOk && I.cfg.synth ? &I.pairFiltSrv[n] : &I.zeroFiltSrv);
    for (int e = 0; e < 2; e++) {
        Cb c{};
        c.synth[0] = I.cfg.synth ? 1.f : 0.f; c.synth[1] = std::max(1.f, I.cfg.motionLimit * m_w * 0.5f);
        c.synth[2] = I.cfg.costScale; c.synth[3] = I.cfg.photoScale;
        float m[9];
        relRot(I.realQ[n] + e * 4, qOut4, m);               // direction in the output (stamp) view -> the pair's newer frame's view
        rows(m, c.oA, c.oB, c.oC);
        relRot(I.realQ[havePair ? prev : n] + e * 4, I.realQ[n] + e * 4, m);   // newer view -> its predecessor's view
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
}
