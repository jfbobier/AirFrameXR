// Minimal D3D11 OpenXR lifecycle probe. Logs every step (flushed) to a file; exit code 0 = PASS.
// Usage: xr_probe.exe <logfile> [frames=60] [timeout_s=30] [pose_csv] [input_csv] [layer_test=0]
// layer_test=1 cycles through composition-layer phases of 90 frames each (see the phase table below).
// Synthetic load (env): XR_PROBE_CPU_MS = busy CPU time per frame before rendering, XR_PROBE_GPU_COPIES = full copies of a
// 4096x4096 RGBA16F texture per frame (GPU time), to model a heavy game (CPU and GPU work that only fit a frame when they overlap).
// XR_PROBE_LOAD shapes both over time: steady (default) | jitter:<pct> (uniform +-pct per frame) | spikes:<every>:<factor> (every
// n-th frame costs factor x) | fps:<lo>:<hi> (each frame's CPU time is 1000/uniform(lo,hi) ms; use XR_PROBE_CPU_MS=1) | ramp:<s> (0 -> 2x over s seconds, then back) | burst:<on_s>:<off_s> (full load / none alternating).
// XR_PROBE_TIMING_CSV = per-frame timing (frame, t_wait_return_ms, t_end_ms, load_factor): the game side of the pacing analysis.
// XR_PROBE_MOVING=<amp deg>:<Hz>: with XR_PROBE_SCENE=dots, a rectangle swinging below the horizon ring (in-scene motion for the simrig judge).
// With input_csv it also reads Touch-profile input actions every frame (trigger/squeeze/buttons/thumbstick/grip pose) and
// fires haptics on both hands periodically.
// Each rendered frame stamps (frame_index+1) as 16 white/black 64x64 blocks in the top-left 1024x64 of the left eye
// image plus a mid-gray (linear 0.5 -> sRGB 188) 64x64 patch at x=1024, so a harness can read them back from the
// decoded stream. frame,yaw,x,y,z,predicted_display_time_ns of the head pose go to pose_csv.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <cmath>
#include <cstdarg>
#include <functional>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <algorithm>
// XR_PROBE_SCENE=<file.vab>: render the benchmark scene (Littlest Tokyo in its textured room, animated) instead of the flat stamp
// background, with the runtime's view poses: a full OpenXR scene for the pacing lab without a game.
#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_PNG
#define STBI_ONLY_JPEG
#include "bench_scene.h"
#include <vector>
#include <mmsystem.h>   // MCI: the scene's music (winmm.lib)
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_PLATFORM_WIN32
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

static FILE* g_log;
static void L(const char* fmt, ...) {
    va_list a; va_start(a, fmt);
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(g_log, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    vfprintf(g_log, fmt, a); fputc('\n', g_log); fflush(g_log);
    va_end(a);
}
static int fail(const char* what, XrResult r) { L("FAIL %s -> %d", what, (int)r); return 1; }
#define CK(x) do { XrResult r_ = (x); if (XR_FAILED(r_)) return fail(#x, r_); L("ok   %s", #x); } while (0)

static const char* stateName(XrSessionState s) {
    switch (s) {
    case XR_SESSION_STATE_IDLE: return "IDLE"; case XR_SESSION_STATE_READY: return "READY";
    case XR_SESSION_STATE_SYNCHRONIZED: return "SYNCHRONIZED"; case XR_SESSION_STATE_VISIBLE: return "VISIBLE";
    case XR_SESSION_STATE_FOCUSED: return "FOCUSED"; case XR_SESSION_STATE_STOPPING: return "STOPPING";
    case XR_SESSION_STATE_LOSS_PENDING: return "LOSS_PENDING"; case XR_SESSION_STATE_EXITING: return "EXITING";
    default: return "?"; }
}

// XR_PROBE_MV=synthetic|truth: the probe stands in for the game's DLSS proxy (docs/MOTION_VECTORS.md). It exports the provider ABI of
// OptiScaler-DLSSMotionVectorExtractor (VaProvider.h v1, mirrored in ovrshim/ngxtap.h) so the shim finds and registers with it, and once per eye and frame
// publishes a motion-vector texture (R16G16_FLOAT at half the eye size: the DLSS render size) and a depth texture (D32_FLOAT, a depth-stencil
// target like a game's) on the app's immediate context.
// `synthetic` (plumbing check): vx = 8 sin(2 pi y / h), vy = 4 cos(2 pi x / w), the right eye negated, the 16 x 16 block at the origin holds
// vx = frame % 32; the depth is cleared to 0.25 (even frames) or 0.75 (odd). The host's VISIONALVR_FG_MV_DUMP images can be checked against these.
// `truth` (ground truth for the synthesis): the exact NGX-convention vectors of the dots scene, rendered by a pixel shader per eye: the camera
// motion of the world-fixed background (where each pixel's direction projected in the previous frame's view, minus its position) and, inside
// the moving marker, the marker's own displacement; both in render-size pixels, previous minus current.
extern "C" {
struct DLSSVEC_PublicationV1 {
    uint32_t struct_size, version, api, stream; uint64_t frame;
    void* cmd; void* color; void* motion_vectors; void* depth; void* output; void* exposure;
    uint32_t render_w, render_h, output_w, output_h, color_x, color_y, mv_x, mv_y, depth_x, depth_y;
    float jitter_x, jitter_y, mv_scale_x, mv_scale_y; int32_t reset; uint32_t flags; void* params;
};
typedef void (*DLSSVEC_ConsumerV1)(const DLSSVEC_PublicationV1*, void*);
static DLSSVEC_ConsumerV1 g_mvConsumer = nullptr; static void* g_mvConsumerUser = nullptr;
__declspec(dllexport) uint32_t DLSSVEC_ProviderVersion() { return 1; }
__declspec(dllexport) int DLSSVEC_SetConsumerV1(DLSSVEC_ConsumerV1 c, void* u) { g_mvConsumer = c; g_mvConsumerUser = u; return 1; }
}
struct SyntheticMv {
    ID3D11Texture2D* mv[2] = {}; ID3D11Texture2D* depth[2] = {}; ID3D11DepthStencilView* dsv[2] = {}; ID3D11RenderTargetView* rtv[2] = {};
    UINT w = 0, h = 0; uint64_t frame = 0; uint64_t published = 0;
    bool truth = false;
    // truth mode: the shader and the per-eye state of the previous frame
    ID3D11VertexShader* vs = nullptr; ID3D11PixelShader* ps = nullptr; ID3D11Buffer* cb = nullptr;
    struct TruthCb { float tanF[4]; float mA[4], mB[4], mC[4]; float marker[4]; float disp[4]; };
    float prevQ[2][4] = {}; bool prevValid[2] = {};
    float markerC[2][2] = {}; bool markerValid[2] = {}, prevMarkerValid[2] = {}; float prevMarkerC[2][2] = {}; float markerHalf[2][2] = {};
    static void rotRows(const float* q, float m[9]) {   // row-major rotation matrix of a unit quaternion (x y z w): world = R * local
        const float x = q[0], y = q[1], z = q[2], w = q[3];
        m[0] = 1 - 2 * (y * y + z * z); m[1] = 2 * (x * y - z * w);     m[2] = 2 * (x * z + y * w);
        m[3] = 2 * (x * y + z * w);     m[4] = 1 - 2 * (x * x + z * z); m[5] = 2 * (y * z - x * w);
        m[6] = 2 * (x * z - y * w);     m[7] = 2 * (y * z + x * w);     m[8] = 1 - 2 * (x * x + y * y);
    }
    bool initTruth(ID3D11Device* dev, std::string& err) {
        static const char* src = R"(
cbuffer CB : register(b0) { float4 tanF; float4 mA, mB, mC; float4 marker; float4 disp; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSOut VS(uint id : SV_VertexID) { VSOut o; float2 p = float2((id << 1) & 2, id & 2); o.pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1); o.uv = p; return o; }
float2 PS(VSOut i) : SV_Target {
    float2 px = i.uv * disp.zw;
    if (all(px >= marker.xy) && all(px < marker.zw)) return disp.xy;
    float3 d = float3(lerp(-tanF.x, tanF.y, i.uv.x), lerp(tanF.z, -tanF.w, i.uv.y), -1);
    float3 dp = float3(dot(mA.xyz, d), dot(mB.xyz, d), dot(mC.xyz, d));   // the same direction in the previous frame's view
    if (dp.z > -1e-4) return float2(0, 0);
    float2 tt = dp.xy / -dp.z;
    float2 uvP = float2((tt.x + tanF.x) / (tanF.x + tanF.y), 1 - (tt.y + tanF.w) / (tanF.z + tanF.w));
    return (uvP - i.uv) * disp.zw;
})";
        ComPtr<ID3DBlob> bvs, bps;
        if (!compile(src, "VS", "vs_5_0", bvs, err) || !compile(src, "PS", "ps_5_0", bps, err)) return false;
        if (FAILED(dev->CreateVertexShader(bvs->GetBufferPointer(), bvs->GetBufferSize(), nullptr, &vs)) ||
            FAILED(dev->CreatePixelShader(bps->GetBufferPointer(), bps->GetBufferSize(), nullptr, &ps))) { err = "truth shaders"; return false; }
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth = sizeof(TruthCb); bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        if (FAILED(dev->CreateBuffer(&bd, nullptr, &cb))) { err = "truth cb"; return false; }
        for (int e = 0; e < 2; e++) if (FAILED(dev->CreateRenderTargetView(mv[e], nullptr, &rtv[e]))) { err = "truth rtv"; return false; }
        truth = true;
        return true;
    }
    // The marker of this frame for one eye (full eye pixels: centre and half sizes), recorded by the dots renderer before publishTruth.
    void setMarker(int eye, bool visible, float cx, float cy, float hw, float hh) {
        markerValid[eye] = visible; markerC[eye][0] = cx; markerC[eye][1] = cy; markerHalf[eye][0] = hw; markerHalf[eye][1] = hh;
    }
    void publishTruth(ID3D11DeviceContext* ctx, int eye, UINT outW, UINT outH, const float tanF[4], const float q[4]) {
        if (!g_mvConsumer || !mv[eye] || !truth) return;
        TruthCb c{};
        memcpy(c.tanF, tanF, sizeof c.tanF);
        float rc[9], rp[9];
        rotRows(q, rc); rotRows(prevValid[eye] ? prevQ[eye] : q, rp);
        // direction in the current view -> world (R_cur) -> previous view (R_prev^T): M = R_prev^T R_cur
        for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) {
            float v = 0; for (int j = 0; j < 3; j++) v += rp[j * 3 + r] * rc[j * 3 + k];
            (r == 0 ? c.mA : r == 1 ? c.mB : c.mC)[k] = v;
        }
        if (markerValid[eye] && prevMarkerValid[eye]) {
            c.marker[0] = (markerC[eye][0] - markerHalf[eye][0]) * 0.5f; c.marker[1] = (markerC[eye][1] - markerHalf[eye][1]) * 0.5f;
            c.marker[2] = (markerC[eye][0] + markerHalf[eye][0]) * 0.5f; c.marker[3] = (markerC[eye][1] + markerHalf[eye][1]) * 0.5f;
            c.disp[0] = (prevMarkerC[eye][0] - markerC[eye][0]) * 0.5f; c.disp[1] = (prevMarkerC[eye][1] - markerC[eye][1]) * 0.5f;
        } else { c.marker[0] = c.marker[1] = -1.f; c.marker[2] = c.marker[3] = -2.f; }
        c.disp[2] = (float)w; c.disp[3] = (float)h;
        ctx->UpdateSubresource(cb, 0, nullptr, &c, 0, 0);
        ctx->OMSetRenderTargets(1, &rtv[eye], nullptr);
        ctx->OMSetBlendState(nullptr, nullptr, 0xffffffff); ctx->OMSetDepthStencilState(nullptr, 0); ctx->RSSetState(nullptr);
        D3D11_VIEWPORT vp{}; vp.Width = (float)w; vp.Height = (float)h; vp.MaxDepth = 1.f; ctx->RSSetViewports(1, &vp);
        ctx->IASetInputLayout(nullptr); ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(vs, nullptr, 0); ctx->PSSetShader(ps, nullptr, 0); ctx->PSSetConstantBuffers(0, 1, &cb);
        ctx->Draw(3, 0);
        ID3D11RenderTargetView* nullRtv = nullptr; ctx->OMSetRenderTargets(1, &nullRtv, nullptr);
        ctx->ClearDepthStencilView(dsv[eye], D3D11_CLEAR_DEPTH, 0.5f, 0);
        DLSSVEC_PublicationV1 p{}; p.struct_size = sizeof p; p.version = 1; p.api = 11; p.stream = 1000 + eye; p.frame = frame; p.cmd = ctx;
        p.motion_vectors = mv[eye]; p.depth = depth[eye]; p.render_w = w; p.render_h = h; p.output_w = outW; p.output_h = outH;
        p.mv_scale_x = p.mv_scale_y = 1.f; p.reset = prevValid[eye] ? 0 : 1; p.flags = 0x2 | 0x8;
        g_mvConsumer(&p, g_mvConsumerUser);
        published++;
        memcpy(prevQ[eye], q, sizeof prevQ[eye]); prevValid[eye] = true;
        prevMarkerValid[eye] = markerValid[eye]; prevMarkerC[eye][0] = markerC[eye][0]; prevMarkerC[eye][1] = markerC[eye][1];
        markerValid[eye] = false;
        if (eye == 1) frame++;
    }
    static uint16_t half(float f) {   // float -> IEEE half (round to nearest, values here are small)
        uint32_t x; memcpy(&x, &f, 4); const uint32_t sign = (x >> 16) & 0x8000; int e = (int)((x >> 23) & 0xff) - 127 + 15; uint32_t m = x & 0x7fffff;
        if (e <= 0) return (uint16_t)sign; if (e >= 31) return (uint16_t)(sign | 0x7c00);
        return (uint16_t)(sign | (e << 10) | ((m + 0x1000) >> 13));
    }
    bool init(ID3D11Device* dev, UINT eyeW, UINT eyeH) {
        w = eyeW / 2; h = eyeH / 2;
        std::vector<uint16_t> px(w * h * 2);
        for (int eye = 0; eye < 2; eye++) {
            for (UINT y = 0; y < h; y++) for (UINT x = 0; x < w; x++) {
                const float sgn = eye ? -1.f : 1.f;
                px[(y * w + x) * 2] = half(sgn * 8.f * sinf(6.2831853f * y / h)); px[(y * w + x) * 2 + 1] = half(sgn * 4.f * cosf(6.2831853f * x / w));
            }
            D3D11_TEXTURE2D_DESC d{}; d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1; d.Format = DXGI_FORMAT_R16G16_FLOAT; d.SampleDesc.Count = 1;
            d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
            D3D11_SUBRESOURCE_DATA sd{ px.data(), w * 4, 0 };
            if (FAILED(dev->CreateTexture2D(&d, &sd, &mv[eye]))) return false;
            D3D11_TEXTURE2D_DESC dd = d; dd.Format = DXGI_FORMAT_D32_FLOAT; dd.BindFlags = D3D11_BIND_DEPTH_STENCIL;
            if (FAILED(dev->CreateTexture2D(&dd, nullptr, &depth[eye])) || FAILED(dev->CreateDepthStencilView(depth[eye], nullptr, &dsv[eye]))) return false;
        }
        return true;
    }
    void publish(ID3D11DeviceContext* ctx, int eye, UINT outW, UINT outH) {
        if (!g_mvConsumer || !mv[eye]) return;
        uint16_t block[16 * 16 * 2];
        for (int i = 0; i < 256; i++) { block[i * 2] = half((float)(frame % 32)); block[i * 2 + 1] = half(0.f); }
        D3D11_BOX box{ 0, 0, 0, 16, 16, 1 };
        ctx->UpdateSubresource(mv[eye], 0, &box, block, 16 * 4, 0);
        ctx->ClearDepthStencilView(dsv[eye], D3D11_CLEAR_DEPTH, frame % 2 ? 0.75f : 0.25f, 0);
        DLSSVEC_PublicationV1 p{}; p.struct_size = sizeof p; p.version = 1; p.api = 11; p.stream = 1000 + eye; p.frame = frame; p.cmd = ctx;
        p.motion_vectors = mv[eye]; p.depth = depth[eye]; p.render_w = w; p.render_h = h; p.output_w = outW; p.output_h = outH;
        p.mv_scale_x = p.mv_scale_y = 1.f; p.reset = frame == 0 ? 1 : 0; p.flags = 0x2 | 0x8;   // MVLowRes, DepthInverted
        g_mvConsumer(&p, g_mvConsumerUser);
        published++;
    }
};

int main(int argc, char** argv) {
    const char* logPath = argc > 1 ? argv[1] : "xr_probe.log";
    int frames = argc > 2 ? atoi(argv[2]) : 60;
    int timeoutS = argc > 3 ? atoi(argv[3]) : 30;
    const char* poseCsv = argc > 4 ? argv[4] : nullptr;
    FILE* poseF = poseCsv ? fopen(poseCsv, "w") : nullptr;
    // follow-the-frames markers: a magenta block at the projection of a world-fixed point, in both eyes, next to the 16-bit frame
    // counter in the left eye image; XR_PROBE_MARKERS_CSV logs where the app put it (what the view pose it used implies)
    FILE* markF = getenv("XR_PROBE_MARKERS_CSV") ? fopen(getenv("XR_PROBE_MARKERS_CSV"), "w") : nullptr;
    if (markF) fprintf(markF, "frame,eye,px,py,w,h,qx,qy,qz,qw,posx,posy,posz,tl,tr,tu,td,display_time,wall_ns\n");
    if (poseF) fprintf(poseF, "frame,yaw,x,y,z,display_time_ns\n");
    const char* inputCsv = (argc > 5 && strcmp(argv[5], "-") != 0) ? argv[5] : nullptr;
    FILE* inputF = inputCsv ? fopen(inputCsv, "w") : nullptr;
    if (inputF) fprintf(inputF, "frame,display_time_ns,trigger_r,squeeze_l,a_r,x_l,stick_lx,stick_ly,rx,ry,rz,lx,ly,lz,pose_l_valid,pose_r_valid,haptics_sent\n");
    g_log = fopen(logPath, "w");
    if (!g_log) return 2;
    const char* rt = getenv("XR_RUNTIME_JSON");
    L("start frames=%d timeout=%ds XR_RUNTIME_JSON=%s", frames, timeoutS, rt ? rt : "(unset)");
    ULONGLONG t0 = GetTickCount64();

    const int layerMode = argc > 6 ? atoi(argv[6]) : 0;   // 1: quads/cylinder/order, 2: also cube layers
    const bool layerTest = layerMode != 0;
    const bool cubeTest = layerMode == 2;
    std::vector<const char*> ext = { XR_KHR_D3D11_ENABLE_EXTENSION_NAME };
    if (layerTest) { ext.push_back(XR_KHR_COMPOSITION_LAYER_CYLINDER_EXTENSION_NAME); if (cubeTest) ext.push_back(XR_KHR_COMPOSITION_LAYER_CUBE_EXTENSION_NAME); }
    XrInstanceCreateInfo ici{ XR_TYPE_INSTANCE_CREATE_INFO };
    strcpy(ici.applicationInfo.applicationName, "xr_probe");
    ici.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0); // 1.0: every runtime accepts it (SteamVR rejects the SDK's 1.1 with API_VERSION_UNSUPPORTED)
    ici.enabledExtensionCount = (uint32_t)ext.size(); ici.enabledExtensionNames = ext.data();
    XrInstance inst;
    CK(xrCreateInstance(&ici, &inst));
    XrInstanceProperties ip{ XR_TYPE_INSTANCE_PROPERTIES };
    CK(xrGetInstanceProperties(inst, &ip));
    L("runtime: %s %u.%u.%u", ip.runtimeName, XR_VERSION_MAJOR(ip.runtimeVersion), XR_VERSION_MINOR(ip.runtimeVersion), XR_VERSION_PATCH(ip.runtimeVersion));

    XrSystemGetInfo sgi{ XR_TYPE_SYSTEM_GET_INFO }; sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId sys;
    CK(xrGetSystem(inst, &sgi, &sys));
    XrSystemProperties sp{ XR_TYPE_SYSTEM_PROPERTIES };
    CK(xrGetSystemProperties(inst, sys, &sp));
    L("system: %s maxSwapchain %ux%u", sp.systemName, sp.graphicsProperties.maxSwapchainImageWidth, sp.graphicsProperties.maxSwapchainImageHeight);

    XrGraphicsRequirementsD3D11KHR req{ XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR };
    PFN_xrGetD3D11GraphicsRequirementsKHR getReq;
    CK(xrGetInstanceProcAddr(inst, "xrGetD3D11GraphicsRequirementsKHR", (PFN_xrVoidFunction*)&getReq));
    CK(getReq(inst, sys, &req));

    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr;
    IDXGIFactory1* fac; CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&fac);
    IDXGIAdapter1* ad = nullptr;
    for (UINT i = 0; fac->EnumAdapters1(i, &ad) == S_OK; i++) {
        DXGI_ADAPTER_DESC1 d; ad->GetDesc1(&d);
        if (memcmp(&d.AdapterLuid, &req.adapterLuid, sizeof(LUID)) == 0) break;
        ad->Release(); ad = nullptr;
    }
    D3D_FEATURE_LEVEL fl = req.minFeatureLevel;
    HRESULT hr = D3D11CreateDevice(ad, ad ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, 0, 0, &fl, 1, D3D11_SDK_VERSION, &dev, nullptr, &ctx);
    if (FAILED(hr)) { L("FAIL D3D11CreateDevice 0x%08x", (unsigned)hr); return 1; }
    L("ok   D3D11CreateDevice");

    Scene scene; bool sceneOn = false;
    ID3D11DepthStencilView* sceneDsv[2] = {};
    UINT sceneDsvW[2] = {}, sceneDsvH[2] = {};
    // XR_PROBE_SCENE=dots: black sky, white rectangles every XR_PROBE_DOT_DEG (default 10) degrees on the horizon ring (world-fixed, drawn per eye from the
    // pose the app located: the simplest scene whose motion is known exactly: the follow-the-frames "judge" of a constant head rotation reads them)
    const bool dotsOn = getenv("XR_PROBE_SCENE") && strcmp(getenv("XR_PROBE_SCENE"), "dots") == 0;
    const double dotDeg = getenv("XR_PROBE_DOT_DEG") ? atof(getenv("XR_PROBE_DOT_DEG")) : 10.0;
    // XR_PROBE_MOVING=<amplitude deg>:<Hz>: a moving rectangle 7 deg below the horizon ring, swinging in world yaw as amp * sin(2 pi f t) with t =
    // the frame's predicted display time (so its motion is smooth in display time whatever the frame rate): the simrig viewer measures it on its own row and
    // harness/simrig/judge.py scores how smoothly in-scene motion reaches the display (repeated frames freeze it, generated frames should not)
    double movingAmp = 0, movingHz = 0;
    if (const char* mv = getenv("XR_PROBE_MOVING")) { movingAmp = atof(mv); if (const char* c = strchr(mv, ':')) movingHz = atof(c + 1); }
    // XR_PROBE_MOVING_CLOCK=wall: the marker's time is the wall clock at the frame's start (what a game engine's delta-time animation does), not the
    // predicted display time (quantised to the display grid; two frames can share one). The uncapped generation is judged against the former.
    const bool movingWall = getenv("XR_PROBE_MOVING_CLOCK") && strcmp(getenv("XR_PROBE_MOVING_CLOCK"), "wall") == 0;
    double movingWallT0 = 0.0, movingFrameStartMs = 0.0;
    long long movingT0 = 0;
    bool soundOn = false;
    if (const char* scenePath = dotsOn ? nullptr : getenv("XR_PROBE_SCENE")) {
        std::string err;
        sceneOn = LoadScene(dev, ctx, scenePath, scene, err);
        if (sceneOn && !scene.room.count) MakeRoom(dev, scene);
        L("scene %s: %s", scenePath, sceneOn ? "loaded" : err.c_str());
        // the scene's music (Sakura Breeze, CC BY 3.0: THIRD_PARTY_NOTICES.md) loops through the Windows default output, like a game's sound: the audio lab and the wizard's
        // scene test hear it end to end. XR_PROBE_SOUND=<file> picks another file, XR_PROBE_SOUND=0 is silence; by default sakura_breeze.mp3 next to the scene file.
        if (sceneOn) {
            const char* sv = getenv("XR_PROBE_SOUND");
            std::string snd = sv ? sv : std::string(scenePath).substr(0, std::string(scenePath).find_last_of("\\/") + 1) + "sakura_breeze.mp3";
            if (!(sv && strcmp(sv, "0") == 0) && GetFileAttributesA(snd.c_str()) != INVALID_FILE_ATTRIBUTES) {
                char cmd[1024], ret[128];
                snprintf(cmd, sizeof cmd, "open \"%s\" type mpegvideo alias xrbgm", snd.c_str());
                MCIERROR e = mciSendStringA(cmd, ret, sizeof ret, nullptr);
                if (!e) e = mciSendStringA("play xrbgm repeat", ret, sizeof ret, nullptr);
                soundOn = !e;
                L("scene sound %s: %s", snd.c_str(), soundOn ? "playing" : "FAILED");
            }
        }
    }
    // the scene is normalised around the origin (~3 m); the benchmark looks at it from (0, 0.2, 1.9): put it where a standing
    // viewer at the mock headset's height (1.5 m) sees the same picture
    // A real headset's LOCAL space has an arbitrary origin and yaw (the tracking floor, the direction it was started in): the scene is re-placed once,
    // from the first valid head pose, 1.9 m ahead of where the head looks horizontally and 0.2 m below the eyes (the mock headset at 1.5 m facing -z gives this same default).
    XMMATRIX scenePlace = XMMatrixTranslation(0.f, 1.3f, -1.9f);
    bool sceneAtHead = false;

    XrGraphicsBindingD3D11KHR gb{ XR_TYPE_GRAPHICS_BINDING_D3D11_KHR }; gb.device = dev;
    XrSessionCreateInfo sci{ XR_TYPE_SESSION_CREATE_INFO }; sci.next = &gb; sci.systemId = sys;
    XrSession sess;
    CK(xrCreateSession(inst, &sci, &sess));

    XrReferenceSpaceCreateInfo rsi{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO };
    // XR_PROBE_SPACE=stage: locate the views in the STAGE (floor level) space instead of LOCAL (eye level): what a game that thinks in absolute heights sees
    const bool stageSpace = getenv("XR_PROBE_SPACE") && strcmp(getenv("XR_PROBE_SPACE"), "stage") == 0;
    rsi.referenceSpaceType = stageSpace ? XR_REFERENCE_SPACE_TYPE_STAGE : XR_REFERENCE_SPACE_TYPE_LOCAL; rsi.poseInReferenceSpace.orientation.w = 1;
    L("reference space: %s", stageSpace ? "STAGE" : "LOCAL");
    XrSpace space;
    CK(xrCreateReferenceSpace(sess, &rsi, &space));
    XrSpace viewSpace{};
    { XrReferenceSpaceCreateInfo vi{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO }; vi.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW; vi.poseInReferenceSpace.orientation.w = 1; CK(xrCreateReferenceSpace(sess, &vi, &viewSpace)); }
    int hapticsSent = 0;
    // synthetic load (see the header)
    const double loadCpuMs = getenv("XR_PROBE_CPU_MS") ? atof(getenv("XR_PROBE_CPU_MS")) : 0.0;
    const int loadGpuCopies = getenv("XR_PROBE_GPU_COPIES") ? atoi(getenv("XR_PROBE_GPU_COPIES")) : 0;
    // load pattern (see the header): a per-frame factor applied to both the CPU time and the GPU copies
    std::string loadPattern = getenv("XR_PROBE_LOAD") ? getenv("XR_PROBE_LOAD") : "steady";
    double lpA = 0, lpB = 0;
    { size_t c1 = loadPattern.find(':'); if (c1 != std::string::npos) { lpA = atof(loadPattern.c_str() + c1 + 1); size_t c2 = loadPattern.find(':', c1 + 1); if (c2 != std::string::npos) lpB = atof(loadPattern.c_str() + c2 + 1); loadPattern = loadPattern.substr(0, c1); } }
    uint64_t lpSeed = 0x9E3779B97F4A7C15ull;
    auto loadFactor = [&](int frame, double tSec) -> double {
        if (loadPattern == "jitter") { lpSeed ^= lpSeed << 13; lpSeed ^= lpSeed >> 7; lpSeed ^= lpSeed << 17; const double u = (double)(lpSeed >> 11) / 9007199254740992.0 * 2.0 - 1.0; return 1.0 + u * lpA / 100.0; }
        if (loadPattern == "fps") { lpSeed ^= lpSeed << 13; lpSeed ^= lpSeed >> 7; lpSeed ^= lpSeed << 17; const double u = (double)(lpSeed >> 11) / 9007199254740992.0; const double fps = lpA + u * (lpB - lpA); return (1000.0 / fps) / std::max(loadCpuMs, 0.001); } // frame time drawn from [lpA, lpB] fps (CPU_MS=1, no GPU copies)
        if (loadPattern == "spikes") { const int every = (int)std::max(1.0, lpA); return (frame % every == every - 1) ? std::max(1.0, lpB) : 1.0; }
        if (loadPattern == "ramp") { const double T = std::max(1.0, lpA); const double ph = fmod(tSec, 2 * T); return ph < T ? 1.0 + ph / T : 3.0 - ph / T; }
        if (loadPattern == "burst") { const double on = std::max(0.1, lpA), off = std::max(0.1, lpB); return fmod(tSec, on + off) < on ? 1.0 : 0.0; }
        return 1.0;
    };
    FILE* timingF = getenv("XR_PROBE_TIMING_CSV") ? fopen(getenv("XR_PROBE_TIMING_CSV"), "w") : nullptr;
    if (timingF) fprintf(timingF, "frame,display_time_ns,t_wait_return_ms,t_end_ms,load_factor\n");
    ID3D11Texture2D* loadTex[2] = {};
    if (loadGpuCopies > 0) {
        D3D11_TEXTURE2D_DESC td{}; td.Width = td.Height = 4096; td.MipLevels = td.ArraySize = 1; td.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        dev->CreateTexture2D(&td, nullptr, &loadTex[0]); dev->CreateTexture2D(&td, nullptr, &loadTex[1]);
    }
    L("load cpu_ms=%.2f gpu_copies=%d pattern=%s (%.2f, %.2f)", loadCpuMs, loadGpuCopies, loadPattern.c_str(), lpA, lpB);
    LARGE_INTEGER qpf; QueryPerformanceFrequency(&qpf);
    auto qpcMs = [&] { LARGE_INTEGER t; QueryPerformanceCounter(&t); return (double)t.QuadPart * 1000.0 / (double)qpf.QuadPart; };
    double loopStartMs = 0;

    // ---- input actions (Touch profile), only when an input log was requested
    XrActionSet aset = XR_NULL_HANDLE; XrAction aTrigger{}, aSqueeze{}, aPrimary{}, aStick{}, aPose{}, aHaptic{};
    XrPath hands[2]; XrSpace handSpace[2] = {};
    if (inputF) {
        XrActionSetCreateInfo asci{ XR_TYPE_ACTION_SET_CREATE_INFO }; strcpy(asci.actionSetName, "gameplay"); strcpy(asci.localizedActionSetName, "Gameplay");
        CK(xrCreateActionSet(inst, &asci, &aset));
        xrStringToPath(inst, "/user/hand/left", &hands[0]); xrStringToPath(inst, "/user/hand/right", &hands[1]);
        auto mk = [&](const char* name, XrActionType type, XrAction* out) {
            XrActionCreateInfo ai{ XR_TYPE_ACTION_CREATE_INFO }; strcpy(ai.actionName, name); strcpy(ai.localizedActionName, name);
            ai.actionType = type; ai.countSubactionPaths = 2; ai.subactionPaths = hands; return xrCreateAction(aset, &ai, out); };
        CK(mk("trigger", XR_ACTION_TYPE_FLOAT_INPUT, &aTrigger)); CK(mk("squeeze", XR_ACTION_TYPE_FLOAT_INPUT, &aSqueeze));
        CK(mk("primary", XR_ACTION_TYPE_BOOLEAN_INPUT, &aPrimary)); CK(mk("stick", XR_ACTION_TYPE_VECTOR2F_INPUT, &aStick));
        CK(mk("pose", XR_ACTION_TYPE_POSE_INPUT, &aPose)); CK(mk("haptic", XR_ACTION_TYPE_VIBRATION_OUTPUT, &aHaptic));
        std::vector<XrActionSuggestedBinding> b;
        auto bind = [&](XrAction a, const char* path) { XrPath p; xrStringToPath(inst, path, &p); b.push_back({ a, p }); };
        bind(aTrigger, "/user/hand/left/input/trigger/value"); bind(aTrigger, "/user/hand/right/input/trigger/value");
        bind(aSqueeze, "/user/hand/left/input/squeeze/value"); bind(aSqueeze, "/user/hand/right/input/squeeze/value");
        bind(aPrimary, "/user/hand/left/input/x/click"); bind(aPrimary, "/user/hand/right/input/a/click");
        bind(aStick, "/user/hand/left/input/thumbstick"); bind(aStick, "/user/hand/right/input/thumbstick");
        bind(aPose, "/user/hand/left/input/grip/pose"); bind(aPose, "/user/hand/right/input/grip/pose");
        bind(aHaptic, "/user/hand/left/output/haptic"); bind(aHaptic, "/user/hand/right/output/haptic");
        XrPath prof; xrStringToPath(inst, "/interaction_profiles/oculus/touch_controller", &prof);
        XrInteractionProfileSuggestedBinding sb{ XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING }; sb.interactionProfile = prof; sb.countSuggestedBindings = (uint32_t)b.size(); sb.suggestedBindings = b.data();
        CK(xrSuggestInteractionProfileBindings(inst, &sb));
    }

    if (inputF) {
        XrSessionActionSetsAttachInfo ai{ XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO }; ai.countActionSets = 1; ai.actionSets = &aset;
        CK(xrAttachSessionActionSets(sess, &ai));
        for (int h = 0; h < 2; h++) {
            XrActionSpaceCreateInfo si{ XR_TYPE_ACTION_SPACE_CREATE_INFO }; si.action = aPose; si.subactionPath = hands[h]; si.poseInActionSpace.orientation.w = 1;
            CK(xrCreateActionSpace(sess, &si, &handSpace[h]));
        }
    }
    uint32_t n; CK(xrEnumerateViewConfigurationViews(inst, sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &n, nullptr));
    std::vector<XrViewConfigurationView> vcv(n, { XR_TYPE_VIEW_CONFIGURATION_VIEW });
    CK(xrEnumerateViewConfigurationViews(inst, sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, n, &n, vcv.data()));
    L("views: %u, recommended %ux%u", n, vcv[0].recommendedImageRectWidth, vcv[0].recommendedImageRectHeight);
    SyntheticMv synMv; const bool synMvOn = getenv("XR_PROBE_MV") && strcmp(getenv("XR_PROBE_MV"), "synthetic") == 0;
    const bool truthMvOn = getenv("XR_PROBE_MV") && strcmp(getenv("XR_PROBE_MV"), "truth") == 0;
    if (synMvOn || truthMvOn) {
        std::string merr;
        if (!synMv.init(dev, vcv[0].recommendedImageRectWidth, vcv[0].recommendedImageRectHeight)) L("FAIL motion vectors: textures");
        else if (truthMvOn && !synMv.initTruth(dev, merr)) L("FAIL motion vectors (truth): %s", merr.c_str());
        else L("ok   %s motion vectors %ux%u per eye (provider ABI exported)", truthMvOn ? "ground-truth" : "synthetic", synMv.w, synMv.h);
    }

    CK(xrEnumerateSwapchainFormats(sess, 0, &n, nullptr));
    std::vector<int64_t> fmts(n);
    CK(xrEnumerateSwapchainFormats(sess, n, &n, fmts.data()));
    int64_t fmt = fmts[0];
    for (int64_t f : fmts) if (f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) fmt = f;
    L("swapchain formats: %u, using %lld", n, (long long)fmt);

    std::vector<XrSwapchain> sc(vcv.size());
    std::vector<std::vector<XrSwapchainImageD3D11KHR>> imgs(vcv.size());
    for (size_t i = 0; i < vcv.size(); i++) {
        XrSwapchainCreateInfo si{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
        si.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        si.format = fmt; si.sampleCount = 1; si.faceCount = 1; si.arraySize = 1; si.mipCount = 1;
        si.width = vcv[i].recommendedImageRectWidth; si.height = vcv[i].recommendedImageRectHeight;
        CK(xrCreateSwapchain(sess, &si, &sc[i]));
        uint32_t c; CK(xrEnumerateSwapchainImages(sc[i], 0, &c, nullptr));
        imgs[i].assign(c, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        CK(xrEnumerateSwapchainImages(sc[i], c, &c, (XrSwapchainImageBaseHeader*)imgs[i].data()));
        L("swapchain %zu: %u images", i, c);
    }

    // ---- composition-layer test resources: phases of 90 frames
    //  0 head-locked quad   1 world-locked rotated quad   2 cylinder   3 cube (behind a transparent projection layer)
    //  4 quad for the LEFT eye only   5 alpha-blended quad over an opaque one (sub-rect of the texture)
    struct LayerSwap { XrSwapchain sc{}; std::vector<XrSwapchainImageD3D11KHR> imgs; uint32_t w = 0, h = 0; };
    LayerSwap quadSw, alphaSw, cubeSw;
    auto makeLayerSwap = [&](LayerSwap& ls, uint32_t w, uint32_t h, uint32_t faces) -> int {
        XrSwapchainCreateInfo si{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
        si.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        si.format = fmt; si.sampleCount = 1; si.faceCount = faces; si.arraySize = 1; si.mipCount = 1; si.width = w; si.height = h;
        XrResult r2 = xrCreateSwapchain(sess, &si, &ls.sc); if (XR_FAILED(r2)) return fail("xrCreateSwapchain(layer)", r2);
        uint32_t c; xrEnumerateSwapchainImages(ls.sc, 0, &c, nullptr);
        ls.imgs.assign(c, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        xrEnumerateSwapchainImages(ls.sc, c, &c, (XrSwapchainImageBaseHeader*)ls.imgs.data());
        ls.w = w; ls.h = h; return 0;
    };
    if (layerTest) {
        if (makeLayerSwap(quadSw, 512, 512, 1) || makeLayerSwap(alphaSw, 64, 64, 1) || (cubeTest && makeLayerSwap(cubeSw, 256, 256, 6))) return 1;
        L("layer test swapchains created");
    }
    // draws into the next image of a layer swapchain; fn gets one RTV per face (1 or 6) and the context
    auto drawLayerSwap = [&](LayerSwap& ls, const std::function<void(std::vector<ID3D11RenderTargetView*>&)>& fn) -> int {
        XrSwapchainImageAcquireInfo ai{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO }; uint32_t idx;
        XrResult r2 = xrAcquireSwapchainImage(ls.sc, &ai, &idx); if (XR_FAILED(r2)) return fail("acquire(layer)", r2);
        XrSwapchainImageWaitInfo wi{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO }; wi.timeout = XR_INFINITE_DURATION;
        r2 = xrWaitSwapchainImage(ls.sc, &wi); if (XR_FAILED(r2)) return fail("wait(layer)", r2);
        std::vector<ID3D11RenderTargetView*> rtvs;
        D3D11_TEXTURE2D_DESC td; ls.imgs[idx].texture->GetDesc(&td);
        for (UINT f = 0; f < td.ArraySize; f++) {
            D3D11_RENDER_TARGET_VIEW_DESC rd{}; rd.Format = (DXGI_FORMAT)fmt;
            if (td.ArraySize > 1) { rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2DARRAY; rd.Texture2DArray.FirstArraySlice = f; rd.Texture2DArray.ArraySize = 1; }
            else rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            ID3D11RenderTargetView* v = nullptr; dev->CreateRenderTargetView(ls.imgs[idx].texture, &rd, &v); rtvs.push_back(v);
        }
        fn(rtvs);
        for (auto* v : rtvs) if (v) v->Release();
        ctx->Flush();
        XrSwapchainImageReleaseInfo rl{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
        r2 = xrReleaseSwapchainImage(ls.sc, &rl); if (XR_FAILED(r2)) return fail("release(layer)", r2);
        return 0;
    };
    auto pose = [](float x, float y, float z, float yawRad) { XrPosef p{}; p.orientation = { 0.f, sinf(yawRad / 2), 0.f, cosf(yawRad / 2) }; p.position = { x, y, z }; return p; };

    bool running = false, exiting = false; int done = 0; bool sawReady = false, sawSync = false;
    int rc = 0;
    while (!exiting && done < frames) {
        if (GetTickCount64() - t0 > (ULONGLONG)timeoutS * 1000) { L("FAIL timeout (done=%d/%d ready=%d)", done, frames, sawReady); rc = 3; break; }
        XrEventDataBuffer ev{ XR_TYPE_EVENT_DATA_BUFFER };
        XrResult pr = xrPollEvent(inst, &ev);
        if (pr == XR_SUCCESS) {
            if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                auto* e = (XrEventDataSessionStateChanged*)&ev;
                L("event: session state -> %s", stateName(e->state));
                if (e->state == XR_SESSION_STATE_READY) {
                    XrSessionBeginInfo bi{ XR_TYPE_SESSION_BEGIN_INFO }; bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                    CK(xrBeginSession(sess, &bi)); running = true; sawReady = true;
                } else if (e->state == XR_SESSION_STATE_SYNCHRONIZED) sawSync = true;
                else if (e->state == XR_SESSION_STATE_STOPPING) { CK(xrEndSession(sess)); running = false; }
                else if (e->state == XR_SESSION_STATE_EXITING || e->state == XR_SESSION_STATE_LOSS_PENDING) exiting = true;
            } else L("event: type %d", (int)ev.type);
            continue;
        }
        if (!running) { Sleep(10); continue; }

        XrFrameState fs{ XR_TYPE_FRAME_STATE };
        XrResult r = xrWaitFrame(sess, nullptr, &fs);
        if (XR_FAILED(r)) return fail("xrWaitFrame", r);
        r = xrBeginFrame(sess, nullptr);
        if (XR_FAILED(r)) return fail("xrBeginFrame", r);
        if (done == 0) loopStartMs = qpcMs();
        const double tWaitRet = qpcMs();
        movingFrameStartMs = tWaitRet;
        const double lf = loadFactor(done, (tWaitRet - loopStartMs) / 1000.0);
        if (loadCpuMs > 0 && lf > 0) { const double t = qpcMs(); while (qpcMs() - t < loadCpuMs * lf) YieldProcessor(); }
        if (loadTex[0] && loadTex[1]) { const int n = (int)(loadGpuCopies * lf + 0.5); for (int k = 0; k < n; k++) ctx->CopyResource(loadTex[(k + 1) & 1], loadTex[k & 1]); }
        if (inputF) {
            XrActiveActionSet act{ aset, XR_NULL_PATH }; XrActionsSyncInfo syn{ XR_TYPE_ACTIONS_SYNC_INFO }; syn.countActiveActionSets = 1; syn.activeActionSets = &act;
            xrSyncActions(sess, &syn);
            auto getF = [&](XrAction a, int h) { XrActionStateGetInfo gi{ XR_TYPE_ACTION_STATE_GET_INFO }; gi.action = a; gi.subactionPath = hands[h]; XrActionStateFloat st{ XR_TYPE_ACTION_STATE_FLOAT }; xrGetActionStateFloat(sess, &gi, &st); return st.currentState; };
            auto getB = [&](XrAction a, int h) { XrActionStateGetInfo gi{ XR_TYPE_ACTION_STATE_GET_INFO }; gi.action = a; gi.subactionPath = hands[h]; XrActionStateBoolean st{ XR_TYPE_ACTION_STATE_BOOLEAN }; xrGetActionStateBoolean(sess, &gi, &st); return (int)st.currentState; };
            XrActionStateGetInfo gs{ XR_TYPE_ACTION_STATE_GET_INFO }; gs.action = aStick; gs.subactionPath = hands[0]; XrActionStateVector2f sv{ XR_TYPE_ACTION_STATE_VECTOR2F }; xrGetActionStateVector2f(sess, &gs, &sv);
            XrSpaceLocation loc[2] = { { XR_TYPE_SPACE_LOCATION }, { XR_TYPE_SPACE_LOCATION } };
            for (int h = 0; h < 2; h++) xrLocateSpace(handSpace[h], space, fs.predictedDisplayTime, &loc[h]);
            const bool vl = (loc[0].locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) != 0, vr = (loc[1].locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) != 0;
            // periodic haptics: right hand every 90 frames, left hand offset by 45
            if (done % 90 == 10 || done % 90 == 55) {
                const int h = (done % 90 == 10) ? 1 : 0;
                XrHapticVibration hv{ XR_TYPE_HAPTIC_VIBRATION }; hv.amplitude = h ? 0.8f : 0.5f; hv.frequency = XR_FREQUENCY_UNSPECIFIED; hv.duration = 60000000;
                XrHapticActionInfo hi{ XR_TYPE_HAPTIC_ACTION_INFO }; hi.action = aHaptic; hi.subactionPath = hands[h];
                if (XR_SUCCEEDED(xrApplyHapticFeedback(sess, &hi, (XrHapticBaseHeader*)&hv))) hapticsSent++;
            }
            fprintf(inputF, "%d,%lld,%.5f,%.5f,%d,%d,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%d,%d,%d\n", done + 1, (long long)fs.predictedDisplayTime,
                getF(aTrigger, 1), getF(aSqueeze, 0), getB(aPrimary, 1), getB(aPrimary, 0), sv.currentState.x, sv.currentState.y,
                loc[1].pose.position.x, loc[1].pose.position.y, loc[1].pose.position.z, loc[0].pose.position.x, loc[0].pose.position.y, loc[0].pose.position.z,
                vl, vr, hapticsSent);
            fflush(inputF);
        }

        std::vector<XrCompositionLayerProjectionView> pv(vcv.size(), { XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW });
        XrCompositionLayerProjection layer{ XR_TYPE_COMPOSITION_LAYER_PROJECTION };
        std::vector<const XrCompositionLayerBaseHeader*> lay;
        XrCompositionLayerQuad q1{ XR_TYPE_COMPOSITION_LAYER_QUAD }, q2{ XR_TYPE_COMPOSITION_LAYER_QUAD };
        XrCompositionLayerCylinderKHR cylL{ XR_TYPE_COMPOSITION_LAYER_CYLINDER_KHR };
        XrCompositionLayerCubeKHR cubeL{ XR_TYPE_COMPOSITION_LAYER_CUBE_KHR };
        uint32_t nl = 0;
        if (fs.shouldRender) {
            XrViewLocateInfo li{ XR_TYPE_VIEW_LOCATE_INFO };
            li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO; li.displayTime = fs.predictedDisplayTime; li.space = space;
            XrViewState vs{ XR_TYPE_VIEW_STATE }; std::vector<XrView> views(vcv.size(), { XR_TYPE_VIEW }); uint32_t vc;
            r = xrLocateViews(sess, &li, &vs, (uint32_t)views.size(), &vc, views.data());
            if (XR_FAILED(r)) return fail("xrLocateViews", r);
            if (done % 20 == 0) L("frame %d: viewState 0x%llx pos(%.3f %.3f %.3f) period %lld ns", done, (unsigned long long)vs.viewStateFlags,
                views[0].pose.position.x, views[0].pose.position.y, views[0].pose.position.z, (long long)fs.predictedDisplayPeriod);
            if (poseF) {
                const XrQuaternionf q = views[0].pose.orientation;
                const double yaw = atan2(2.0 * (q.w * q.y + q.x * q.z), 1.0 - 2.0 * (q.y * q.y + q.x * q.x));
                fprintf(poseF, "%d,%.6f,%.5f,%.5f,%.5f,%lld\n", done + 1, yaw, views[0].pose.position.x, views[0].pose.position.y, views[0].pose.position.z, (long long)fs.predictedDisplayTime);
                fflush(poseF);
            }
            if (sceneOn && !sceneAtHead && (vs.viewStateFlags & XR_VIEW_STATE_POSITION_VALID_BIT) && (vs.viewStateFlags & XR_VIEW_STATE_ORIENTATION_VALID_BIT)) {
                const XrPosef& hp = views[0].pose;
                const float qx = hp.orientation.x, qy = hp.orientation.y, qz = hp.orientation.z, qw = hp.orientation.w;
                // horizontal forward = the view's -z axis projected on the floor plane (a head looking straight down keeps the previous placement)
                const float fx = -(2.f * (qx * qz + qw * qy)), fz = -(1.f - 2.f * (qx * qx + qy * qy));
                const float n = sqrtf(fx * fx + fz * fz);
                if (n > 0.2f) {
                    const float dx = fx / n, dz = fz / n;
                    // the scene's -z axis must point along (dx, dz): rotate about y by the yaw that maps (0,-1) to it
                    const float yaw = atan2f(dx, -dz);
                    scenePlace = XMMatrixRotationY(yaw) * XMMatrixTranslation(hp.position.x + dx * 1.9f, hp.position.y - 0.2f, hp.position.z + dz * 1.9f);
                    sceneAtHead = true;
                    L("scene placed at the head: eyes (%.2f %.2f %.2f), facing yaw %.0f deg", hp.position.x, hp.position.y, hp.position.z, yaw * 57.29578f);
                }
            }
            SceneFrame sceneFrame;
            if (sceneOn) BeginSceneFrame(scene, ctx, (qpcMs() - loopStartMs) / 1000.0, sceneFrame, scenePlace);
            if (synMvOn) {   // the "DLSS evaluate" of each eye, left first, before the frame is submitted (as a game's render pass would)
                for (int e = 0; e < 2 && e < (int)vcv.size(); e++) synMv.publish(ctx, e, vcv[e].recommendedImageRectWidth, vcv[e].recommendedImageRectHeight);
                synMv.frame++;
            }
            for (size_t i = 0; i < vcv.size(); i++) {
                XrSwapchainImageAcquireInfo ai{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO }; uint32_t idx;
                r = xrAcquireSwapchainImage(sc[i], &ai, &idx); if (XR_FAILED(r)) return fail("xrAcquireSwapchainImage", r);
                XrSwapchainImageWaitInfo wi{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO }; wi.timeout = XR_INFINITE_DURATION;
                r = xrWaitSwapchainImage(sc[i], &wi); if (XR_FAILED(r)) return fail("xrWaitSwapchainImage", r);
                // stamp: dark background, 16 counter blocks and a gray patch in the left eye image
                ID3D11RenderTargetView* rtv = nullptr;
                D3D11_RENDER_TARGET_VIEW_DESC rd{}; rd.Format = (DXGI_FORMAT)fmt; rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
                if (SUCCEEDED(dev->CreateRenderTargetView(imgs[i][idx].texture, &rd, &rtv))) {
                    float bg[4] = { 0.02f, 0.02f, 0.02f + (i ? 0.1f : 0.f), 1 };
                    if (cubeTest && (done / 90) % 6 == 3) { bg[0] = bg[1] = bg[2] = bg[3] = 0.f; }   // cube phase: transparent, cube is drawn behind
                    if (sceneOn && i < 2) {
                        D3D11_TEXTURE2D_DESC td{}; imgs[i][idx].texture->GetDesc(&td);
                        if (!sceneDsv[i] || sceneDsvW[i] != td.Width || sceneDsvH[i] != td.Height) {
                            if (sceneDsv[i]) { sceneDsv[i]->Release(); sceneDsv[i] = nullptr; }
                            D3D11_TEXTURE2D_DESC dd{}; dd.Width = td.Width; dd.Height = td.Height; dd.MipLevels = 1; dd.ArraySize = 1;
                            dd.Format = DXGI_FORMAT_D32_FLOAT; dd.SampleDesc.Count = 1; dd.Usage = D3D11_USAGE_DEFAULT; dd.BindFlags = D3D11_BIND_DEPTH_STENCIL;
                            ID3D11Texture2D* dt = nullptr;
                            if (SUCCEEDED(dev->CreateTexture2D(&dd, nullptr, &dt))) { dev->CreateDepthStencilView(dt, nullptr, &sceneDsv[i]); dt->Release(); }
                            sceneDsvW[i] = td.Width; sceneDsvH[i] = td.Height;
                        }
                        const float sky[4] = { 0.62f, 0.72f, 0.82f, 1 };
                        ctx->ClearRenderTargetView(rtv, sky);
                        if (sceneDsv[i]) {
                            ctx->ClearDepthStencilView(sceneDsv[i], D3D11_CLEAR_DEPTH, 1, 0);
                            ctx->OMSetRenderTargets(1, &rtv, sceneDsv[i]);
                            const XrPosef& xp = views[i].pose; const XrFovf& xf = views[i].fov;
                            const XMMATRIX pose = XMMatrixRotationQuaternion(XMVectorSet(xp.orientation.x, xp.orientation.y, xp.orientation.z, xp.orientation.w))
                                                * XMMatrixTranslation(xp.position.x, xp.position.y, xp.position.z);
                            const float tan4[4] = { -tanf(xf.angleLeft), tanf(xf.angleRight), tanf(xf.angleUp), -tanf(xf.angleDown) };
                            const XMMATRIX vp = XMMatrixInverse(nullptr, pose) * Projection(tan4, 0.05f, 100.f);
                            D3D11_VIEWPORT v{ 0, 0, (float)td.Width, (float)td.Height, 0, 1 };
                            DrawSceneEye(scene, ctx, sceneFrame, v, vp, scenePlace);
                            ID3D11RenderTargetView* none = nullptr;
                            ctx->OMSetRenderTargets(1, &none, nullptr);
                        }
                    } else {
                        ctx->ClearRenderTargetView(rtv, bg);
                    }
                    if (dotsOn && i < 2) {
                        D3D11_TEXTURE2D_DESC dt{}; imgs[i][idx].texture->GetDesc(&dt);
                        const XrPosef& dp = views[i].pose; const XrFovf& df = views[i].fov;
                        const XMVECTOR dq = XMVectorSet(dp.orientation.x, dp.orientation.y, dp.orientation.z, dp.orientation.w);
                        const XMMATRIX dpose = XMMatrixRotationQuaternion(dq) * XMMatrixTranslation(dp.position.x, dp.position.y, dp.position.z);
                        const XMMATRIX dview = XMMatrixInverse(nullptr, dpose);
                        const float dtl = -tanf(df.angleLeft), dtr = tanf(df.angleRight), dtu = tanf(df.angleUp), dtd = -tanf(df.angleDown);
                        const float pxPerTanX = (float)dt.Width / (dtl + dtr), pxPerTanY = (float)dt.Height / (dtu + dtd);
                        ID3D11DeviceContext1* dc = nullptr;
                        if (SUCCEEDED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&dc))) {
                            const float black[4] = { 0, 0, 0, 1 }, white[4] = { 1, 1, 1, 1 };
                            ctx->ClearRenderTargetView(rtv, black);
                            const int n = (int)(360.0 / dotDeg + 0.5);
                            for (int k = 0; k < n; k++) {
                                const double th = k * dotDeg * 3.14159265358979 / 180.0;
                                // a point 3 m away on the horizon ring around the world origin's height (the head's own height: the ring is level with the eyes)
                                const XMVECTOR wp = XMVectorSet((float)(3.0 * sin(th)), dp.position.y, (float)(-3.0 * cos(th)), 1.f);
                                const XMVECTOR vv = XMVector3TransformCoord(wp, dview);
                                const float vx = XMVectorGetX(vv), vy = XMVectorGetY(vv), vz = XMVectorGetZ(vv);
                                if (vz > -0.1f) continue;                                  // behind the eye
                                const float tx = vx / -vz, ty = vy / -vz;
                                const float cx = (tx + dtl) * pxPerTanX, cy = (1.f - (ty + dtd) / (dtu + dtd)) * (float)dt.Height;
                                const float hw = std::max(2.f, 0.35f * 0.0174533f * pxPerTanX * (k == 0 ? 2.f : 1.f));   // 0.7 deg wide (the 0 deg reference twice that)
                                const float hh = 4.f * 0.0174533f * pxPerTanY * 1.f;
                                D3D11_RECT dr{ (LONG)(cx - hw), (LONG)(cy - hh), (LONG)(cx + hw), (LONG)(cy + hh) };
                                dr.left = std::max<LONG>(dr.left, 0); dr.top = std::max<LONG>(dr.top, 0);
                                dr.right = std::min<LONG>(dr.right, (LONG)dt.Width); dr.bottom = std::min<LONG>(dr.bottom, (LONG)dt.Height);
                                if (dr.right > dr.left && dr.bottom > dr.top) dc->ClearView(rtv, white, &dr, 1);
                            }
                            dc->Release();
                        }
                    }
                    if (markF && i < 2) {
                        FILETIME wft; GetSystemTimePreciseAsFileTime(&wft);
                        const unsigned long long wallNs = ((((unsigned long long)wft.dwHighDateTime << 32) | wft.dwLowDateTime) - 116444736000000000ull) * 100ull;
                        D3D11_TEXTURE2D_DESC mt{}; imgs[i][idx].texture->GetDesc(&mt);
                        const XrPosef& xp = views[i].pose; const XrFovf& xf = views[i].fov;
                        const XMMATRIX mpose = XMMatrixRotationQuaternion(XMVectorSet(xp.orientation.x, xp.orientation.y, xp.orientation.z, xp.orientation.w))
                                             * XMMatrixTranslation(xp.position.x, xp.position.y, xp.position.z);
                        const float mtan[4] = { -tanf(xf.angleLeft), tanf(xf.angleRight), tanf(xf.angleUp), -tanf(xf.angleDown) };
                        const XMMATRIX mvp = XMMatrixInverse(nullptr, mpose) * Projection(mtan, 0.05f, 100.f);
                        XMFLOAT3 ndc; XMStoreFloat3(&ndc, XMVector3TransformCoord(XMVectorSet(0.f, 1.3f, -3.f, 1.f), mvp));
                        const float mx = (ndc.x * 0.5f + 0.5f) * (float)mt.Width, my = (1.f - (ndc.y * 0.5f + 0.5f)) * (float)mt.Height;
                        fprintf(markF, "%d,%d,%.2f,%.2f,%u,%u,%.7f,%.7f,%.7f,%.7f,%.5f,%.5f,%.5f,%.6f,%.6f,%.6f,%.6f,%lld,%llu\n", done + 1, (int)i, mx, my, mt.Width, mt.Height,
                            xp.orientation.x, xp.orientation.y, xp.orientation.z, xp.orientation.w, xp.position.x, xp.position.y, xp.position.z,
                            mtan[0], mtan[1], mtan[2], mtan[3], (long long)fs.predictedDisplayTime, wallNs);
                        fflush(markF);
                        ID3D11DeviceContext1* mc = nullptr;
                        if (SUCCEEDED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&mc))) {
                            const float magenta[4] = { 1, 0, 1, 1 };
                            D3D11_RECT mr{ (LONG)(mx - 24), (LONG)(my - 24), (LONG)(mx + 24), (LONG)(my + 24) };
                            mr.left = std::max<LONG>(mr.left, 0); mr.top = std::max<LONG>(mr.top, 0);
                            mr.right = std::min<LONG>(mr.right, (LONG)mt.Width); mr.bottom = std::min<LONG>(mr.bottom, (LONG)mt.Height);
                            if (mr.right > mr.left && mr.bottom > mr.top) mc->ClearView(rtv, magenta, &mr, 1);
                            mc->Release();
                        }
                    }
                    if (dotsOn && movingAmp > 0 && movingHz > 0 && i < 2) {
                        // drawn after the magenta reference square (XR_PROBE_MARKERS_CSV), which would otherwise hide it near the centre
                        D3D11_TEXTURE2D_DESC dt{}; imgs[i][idx].texture->GetDesc(&dt);
                        const XrPosef& dp = views[i].pose; const XrFovf& df = views[i].fov;
                        const XMVECTOR dq = XMVectorSet(dp.orientation.x, dp.orientation.y, dp.orientation.z, dp.orientation.w);
                        const XMMATRIX dview = XMMatrixInverse(nullptr, XMMatrixRotationQuaternion(dq) * XMMatrixTranslation(dp.position.x, dp.position.y, dp.position.z));
                        const float dtl = -tanf(df.angleLeft), dtr = tanf(df.angleRight), dtu = tanf(df.angleUp), dtd = -tanf(df.angleDown);
                        const float pxPerTanX = (float)dt.Width / (dtl + dtr), pxPerTanY = (float)dt.Height / (dtu + dtd);
                        ID3D11DeviceContext1* dc = nullptr;
                        if (SUCCEEDED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&dc))) {
                            const float white[4] = { 1, 1, 1, 1 };
                                if (movingT0 == 0) movingT0 = (long long)fs.predictedDisplayTime;
                                if (movingWall && movingWallT0 == 0.0) movingWallT0 = movingFrameStartMs;
                                const double ts = movingWall ? (movingFrameStartMs - movingWallT0) * 1e-3 : (double)((long long)fs.predictedDisplayTime - movingT0) * 1e-9;
                                const double th = movingAmp * sin(2.0 * 3.14159265358979 * movingHz * ts) * 3.14159265358979 / 180.0;
                                const double drop = 3.0 * tan(7.0 * 3.14159265358979 / 180.0);   // 7 deg below the ring's centre (the ring's rectangles reach 4 deg down)
                                const XMVECTOR wp = XMVectorSet((float)(3.0 * sin(th)), (float)(dp.position.y - drop), (float)(-3.0 * cos(th)), 1.f);
                                const XMVECTOR vv = XMVector3TransformCoord(wp, dview);
                                const float vx = XMVectorGetX(vv), vy = XMVectorGetY(vv), vz = XMVectorGetZ(vv);
                                if (vz < -0.1f) {
                                    const float tx = vx / -vz, ty = vy / -vz;
                                    const float cx = (tx + dtl) * pxPerTanX, cy = (1.f - (ty + dtd) / (dtu + dtd)) * (float)dt.Height;
                                    const float hw = std::max(2.f, 0.35f * 0.0174533f * pxPerTanX), hh = 2.f * 0.0174533f * pxPerTanY;
                                    D3D11_RECT dr{ (LONG)(cx - hw), (LONG)(cy - hh), (LONG)(cx + hw), (LONG)(cy + hh) };
                                    dr.left = std::max<LONG>(dr.left, 0); dr.top = std::max<LONG>(dr.top, 0);
                                    dr.right = std::min<LONG>(dr.right, (LONG)dt.Width); dr.bottom = std::min<LONG>(dr.bottom, (LONG)dt.Height);
                                    if (dr.right > dr.left && dr.bottom > dr.top) dc->ClearView(rtv, white, &dr, 1);
                                    synMv.setMarker((int)i, true, cx, cy, hw, hh);
                                }
                            dc->Release();
                        }
                    }
                    if (truthMvOn && i < 2) {   // the eye's "DLSS evaluate": exact vectors of this view against the previous frame's
                        const XrFovf& tf = views[i].fov;
                        const float tanF[4] = { -tanf(tf.angleLeft), tanf(tf.angleRight), tanf(tf.angleUp), -tanf(tf.angleDown) };
                        const float q[4] = { views[i].pose.orientation.x, views[i].pose.orientation.y, views[i].pose.orientation.z, views[i].pose.orientation.w };
                        D3D11_TEXTURE2D_DESC ot{}; imgs[i][idx].texture->GetDesc(&ot);
                        synMv.publishTruth(ctx, (int)i, ot.Width, ot.Height, tanF, q);
                    }
                    ID3D11DeviceContext1* c1 = nullptr;
                    if (i == 0 && SUCCEEDED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&c1))) {
                        const uint32_t stamp = (uint32_t)(done + 1);
                        const float white[4] = { 1, 1, 1, 1 }, black[4] = { 0, 0, 0, 1 }, gray[4] = { 0.5f, 0.5f, 0.5f, 1 };
                        for (int bi = 0; bi < 16; bi++) {
                            D3D11_RECT r{ bi * 64, 0, (bi + 1) * 64, 64 };
                            c1->ClearView(rtv, ((stamp >> bi) & 1) ? white : black, &r, 1);
                        }
                        D3D11_RECT gr{ 1024, 0, 1088, 64 };
                        c1->ClearView(rtv, gray, &gr, 1);
                        c1->Release();
                    }
                    rtv->Release();
                }
                ctx->Flush();
                XrSwapchainImageReleaseInfo rl{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
                r = xrReleaseSwapchainImage(sc[i], &rl); if (XR_FAILED(r)) return fail("xrReleaseSwapchainImage", r);
                pv[i].pose = views[i].pose; pv[i].fov = views[i].fov;
                pv[i].subImage.swapchain = sc[i]; pv[i].subImage.imageRect = { {0,0}, {(int)vcv[i].recommendedImageRectWidth, (int)vcv[i].recommendedImageRectHeight} };
            }
            layer.space = space; layer.viewCount = (uint32_t)pv.size(); layer.views = pv.data();
            const XrCompositionLayerBaseHeader* proj = (XrCompositionLayerBaseHeader*)&layer;
            if (!layerTest) {
                lay.push_back(proj);
            } else {
                const int phase = (done / 90) % 6;
                auto drawQuadrants = [&](std::vector<ID3D11RenderTargetView*>& r) {
                    ID3D11DeviceContext1* c1 = nullptr;
                    if (FAILED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&c1))) return;
                    const float red[4] = { 1, 0, 0, 1 }, green[4] = { 0, 1, 0, 1 }, blue[4] = { 0, 0, 1, 1 }, white[4] = { 1, 1, 1, 1 };
                    D3D11_RECT tl{ 0, 0, 256, 256 }, tr{ 256, 0, 512, 256 }, bl{ 0, 256, 256, 512 }, br{ 256, 256, 512, 512 };
                    c1->ClearView(r[0], red, &tl, 1); c1->ClearView(r[0], green, &tr, 1); c1->ClearView(r[0], blue, &bl, 1); c1->ClearView(r[0], white, &br, 1);
                    c1->Release();
                };
                auto fillQuad = [&](XrCompositionLayerQuad& q, XrSpace sp, XrPosef ps, float w, float h, const LayerSwap& ls, XrRect2Di rect) {
                    q.space = sp; q.pose = ps; q.size = { w, h }; q.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
                    q.subImage.swapchain = ls.sc; q.subImage.imageRect = rect; q.subImage.imageArrayIndex = 0;
                };
                const XrRect2Di full512{ {0, 0}, {512, 512} };
                if (phase == 3 && cubeTest) {
                    // cube faces +X red, -X green, +Y blue, -Y yellow, +Z magenta, -Z cyan; the cube goes first, the (transparent) projection layer on top
                    if (drawLayerSwap(cubeSw, [&](std::vector<ID3D11RenderTargetView*>& r) {
                            const float c[6][4] = { {1,0,0,1}, {0,1,0,1}, {0,0,1,1}, {1,1,0,1}, {1,0,1,1}, {0,1,1,1} };
                            for (int f = 0; f < 6; f++) ctx->ClearRenderTargetView(r[f], c[f]);
                        })) return 1;
                    cubeL.space = space; cubeL.orientation = { 0, 0, 0, 1 }; cubeL.swapchain = cubeSw.sc; cubeL.imageArrayIndex = 0; cubeL.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
                    layer.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
                    lay.push_back((XrCompositionLayerBaseHeader*)&cubeL);
                    lay.push_back(proj);
                } else {
                    lay.push_back(proj);
                    if (drawLayerSwap(quadSw, drawQuadrants)) return 1;
                    if (phase == 0) fillQuad(q1, viewSpace, pose(0, 0, -2, 0), 1.0f, 1.0f, quadSw, full512);
                    if (phase == 1) fillQuad(q1, space, pose(0.3f, 1.5f, -2.0f, 25.f * 3.14159265f / 180.f), 1.0f, 1.0f, quadSw, full512);
                    if (phase == 3) {   // submission order: a small green quad submitted after a red one covers its center
                        fillQuad(q1, viewSpace, pose(0, 0, -2, 0), 1.0f, 1.0f, quadSw, { {0, 0}, {256, 256} });
                        fillQuad(q2, viewSpace, pose(0, 0, -2, 0), 0.4f, 0.4f, quadSw, { {256, 0}, {256, 256} });
                    }
                    if (phase == 4) { fillQuad(q1, viewSpace, pose(0, 0, -2, 0), 1.0f, 1.0f, quadSw, full512); q1.eyeVisibility = XR_EYE_VISIBILITY_LEFT; }
                    if (phase == 5) {
                        fillQuad(q1, viewSpace, pose(0, 0, -2, 0), 1.0f, 1.0f, quadSw, { {0, 0}, {256, 256} });   // opaque red (sub-rect)
                        if (drawLayerSwap(alphaSw, [&](std::vector<ID3D11RenderTargetView*>& r) { const float c[4] = { 1, 1, 1, 0.5f }; ctx->ClearRenderTargetView(r[0], c); })) return 1;
                        fillQuad(q2, viewSpace, pose(0, 0, -1.9f, 0), 0.5f, 0.5f, alphaSw, { {0, 0}, {64, 64} });  // white, 50% straight alpha
                        q2.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT | XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
                    }
                    if (phase == 2) {
                        cylL.space = space; cylL.pose = pose(0, 1.5f, 0, 0); cylL.radius = 2.5f; cylL.centralAngle = 1.2f; cylL.aspectRatio = 2.0f;
                        cylL.eyeVisibility = XR_EYE_VISIBILITY_BOTH; cylL.subImage.swapchain = quadSw.sc; cylL.subImage.imageRect = full512; cylL.subImage.imageArrayIndex = 0;
                        lay.push_back((XrCompositionLayerBaseHeader*)&cylL);
                    } else {
                        lay.push_back((XrCompositionLayerBaseHeader*)&q1);
                        if (phase == 5 || phase == 3) lay.push_back((XrCompositionLayerBaseHeader*)&q2);
                    }
                }
            }
            nl = (uint32_t)lay.size();
        }
        XrFrameEndInfo fe{ XR_TYPE_FRAME_END_INFO };
        fe.displayTime = fs.predictedDisplayTime; fe.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE; fe.layerCount = nl; fe.layers = lay.data();
        r = xrEndFrame(sess, &fe);
        if (XR_FAILED(r)) return fail("xrEndFrame", r);
        if (timingF) { fprintf(timingF, "%d,%lld,%.3f,%.3f,%.3f\n", done + 1, (long long)fs.predictedDisplayTime, tWaitRet, qpcMs(), lf); if (done % 30 == 0) fflush(timingF); }
        done++;
    }
    if (soundOn) mciSendStringA("close xrbgm", nullptr, 0, nullptr);
    L("loop done: frames=%d ready=%d synchronized=%d", done, sawReady, sawSync);
    if (synMvOn || truthMvOn) L("motion vectors: %llu publications, consumer %s", (unsigned long long)synMv.published, g_mvConsumer ? "registered" : "never registered");
    if (done > 1) L("app fps %.2f over %d frames", (done - 1) * 1000.0 / (qpcMs() - loopStartMs), done - 1);
    if (rc == 0 && done < frames) rc = 4;
    if (running) { xrRequestExitSession(sess); }
    xrDestroySession(sess); xrDestroyInstance(inst);
    L("%s (rc=%d)", rc == 0 ? "PASS" : "FAIL", rc);
    return rc;
}
