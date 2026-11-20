// xr_modsim: an OpenXR D3D11 app that mimics the frame loop of a VR mod measured in the field (acbfvr, a UEVR vrframework derivative with
// alternate-eye rendering; docs/EXPERIMENTAL_SWITCHES.md "Game frame pacing fix"). The lab uses it to test the shim's pacing fix without the game.
//
// What it reproduces (from the runtime probe of 2026-10-27/28, private/acbfvr_probe):
//  - the render thread runs "engine frames" back to back (each costs MODSIM_ENGINE_MS of CPU and MODSIM_GPU_COPIES of GPU copies); only every second
//    one (the parity frame) talks to OpenXR: xrWaitFrame, xrBeginFrame, xrLocateViews, then hands the frame to a worker thread;
//  - the worker acquires, draws and releases the swapchain images, calls xrEndFrame for that frame, and then, with MODSIM_DUP=1 (default), makes a
//    SECOND xrWaitFrame / xrBeginFrame / xrEndFrame cycle that resubmits the very same layers (same poses, same images) for the next display slot:
//    every real frame is submitted twice;
//  - MODSIM_DUP = how many extra wait / begin / end cycles resubmit each real frame (default 1; 0 = a normal game, 2 = three submits per frame); MODSIM_DUP_STOP_S
//    ends the duplicates after that many seconds (a client that stops resubmitting); MODSIM_STATIC_POSE=1 keeps the head pose constant (every submit "the same");
//  - at the switch the engine time changes too: MODSIM_SWITCH_ENGINE_MS (new engine frame cost) and MODSIM_SWITCH_JITTER_MS (+- uniform jitter), MODSIM_JITTER_MS before it:
//    the field flip made the mod faster and raggeder;
//  - the worker is one of MODSIM_POOL threads used in turn (default 9), and from MODSIM_SWITCH_S seconds on a single thread (0 = never): the
//    mod's submit work collapsed to one thread about four minutes after the game started.
// MODSIM_OFXR=1 mimics OFXR-Bridge (external/ofxr, an OpenXR API layer doing frame generation) instead: every engine frame is a VR frame on one thread
// (no pool, no worker), and the layer's xrEndFrame submits TWO frames for it: the generated one (a different image, the game's render poses) in the slot
// the app waited for, then, after an internal wait / begin, the real one (layer.cpp submit_current_cycle: poses unchanged for projection layers). So the
// shim sees about twice the engine rate of submits, every second with the previous submit's exact render poses, the second wait taken by the layer.
// The render thread starts its next xrWaitFrame only after the worker is done (the mod serialises them with a mutex).
//
// Call shapes for the frame-identity matrix (docs/PLAN_UNIVERSAL_OPENXR.md), combinable with the above unless noted:
//  - MODSIM_ARRAY=1: one swapchain with two array slices (single-pass stereo) instead of one swapchain per eye: VDXR's copy path for slice 1;
//  - MODSIM_MSAA=N: N samples per pixel (VDXR resolves into a second swapchain: its other copy path); not with MODSIM_ARRAY; no id stamp;
//  - MODSIM_DUP_RELEASE=1: the duplicate cycle acquires the next image of each swapchain, copies the last picture into it and releases it: the
//    SAME picture, but an image release (whether the real mod does this is the open question of the plan's field check);
//  - MODSIM_QUAD=1: a quad layer (own swapchain) redrawn and released before every submit, duplicates included: a submit whose projection
//    repeats but whose picture changed (a menu or HUD over a frozen view);
//  - MODSIM_PAUSE_S=a:b: the frames posted between a and b seconds after the loop starts draw nothing and resubmit the last picture with its
//    poses (a game paused on its last frame; OpenXR allows it): no release, same poses, the app still paced by its waits.
// The app's own truth for every submit is in the CSV's END rows (columns rel, new_pic, same_pose; harness lib.analyse_identity).
//
// Usage: xr_modsim.exe <logfile> [real_frames=300] [timeout_s=60]
// Env: MODSIM_ENGINE_MS (default 8), MODSIM_GPU_COPIES (0), MODSIM_DUP (1), MODSIM_POOL (9), MODSIM_SWITCH_S (0), MODSIM_CSV = per-event CSV
// (t_ms, tid, event, a, b, rel, new_pic, same_pose): EF / WAIT_IN / WAIT_OUT / BEGIN / END with the display time in a and, for END, the real-frame
// id in b (the duplicate carries the id of the frame it repeats), the lab's equivalent of the probe's log. END rows also carry rel = swapchain
// images released since the previous END (what VDXR counts as "newly released"), new_pic = 1 when the picture differs from every earlier submit's
// (0: a resubmit, a DUP_RELEASE copy, a paused frame), same_pose = 1 when the projection poses equal the previous END's exactly; -1 on other rows.
// Each real frame stamps its id as 16 white/black 64x64 blocks in the top-left of the left eye image, like xr_probe.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_PLATFORM_WIN32
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

static FILE* g_log;
static FILE* g_csv;
static std::mutex g_logMx;
static LARGE_INTEGER g_qpf;
static double qpcMs() { LARGE_INTEGER t; QueryPerformanceCounter(&t); return (double)t.QuadPart * 1000.0 / (double)g_qpf.QuadPart; }
static void L(const char* fmt, ...) {
    std::lock_guard<std::mutex> lk(g_logMx);
    va_list a; va_start(a, fmt);
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(g_log, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    vfprintf(g_log, fmt, a); fputc('\n', g_log); fflush(g_log);
    va_end(a);
}
static void ev(const char* name, long long a = 0, long long b = 0, int rel = -1, int newPic = -1, int samePose = -1) {
    if (!g_csv) return;
    std::lock_guard<std::mutex> lk(g_logMx);
    fprintf(g_csv, "%.3f,%lu,%s,%lld,%lld,%d,%d,%d\n", qpcMs(), GetCurrentThreadId(), name, a, b, rel, newPic, samePose);
}
static int fail(const char* what, XrResult r) { L("FAIL %s -> %d", what, (int)r); return 1; }
#define CK(x) do { XrResult r_ = (x); if (XR_FAILED(r_)) return fail(#x, r_); L("ok   %s", #x); } while (0)

static XrSession g_sess; static XrSpace g_space;
static ID3D11Device* g_dev; static ID3D11DeviceContext* g_ctx; static std::mutex g_gpuMx;
static std::vector<XrSwapchain> g_sc(2);
static std::vector<std::vector<XrSwapchainImageD3D11KHR>> g_imgs(2);
static uint32_t g_w[2], g_h[2];
static int64_t g_fmt;

struct Task { int id; XrTime time; XrView views[2]; bool paused = false; };
struct Worker {
    std::thread th; std::mutex m; std::condition_variable cv; std::deque<Task> q; bool stop = false;
};
static std::atomic<int> g_taskDone{0};
static std::atomic<int> g_dupN{1};
static bool g_staticPose = false;
static bool g_ofxr = false;
static bool g_strobe = false;   // MODSIM_STROBE=1: real frames flat green, the generated ones flat magenta (a headset test: does the display show both?)
static std::atomic<bool> g_failed{false};
// call shapes (header comment)
static bool g_array = false;          // MODSIM_ARRAY=1: g_sc[1] == g_sc[0], the eye is the array slice
static int g_samples = 1;             // MODSIM_MSAA
static bool g_dupRelease = false;     // MODSIM_DUP_RELEASE=1
static bool g_quad = false;           // MODSIM_QUAD=1
static XrSwapchain g_quadSc = XR_NULL_HANDLE;
static std::vector<XrSwapchainImageD3D11KHR> g_quadImgs;
static const int kQuadPx = 512;
static uint32_t g_lastIdx[2] = {};    // the image each swapchain released last (MODSIM_DUP_RELEASE copies it)
// The app's truth per submit (CSV END rows). The submits of one frame never overlap (the render thread waits for the worker; OFXR mode is one
// thread), so plain variables are enough for the pose; the counters are atomics because the worker changes from frame to frame.
static std::atomic<int> g_relSinceEnd{0};
static std::atomic<bool> g_newPicSinceEnd{false};
static XrPosef g_lastEndPose[2];
static bool g_haveLastEnd = false;
static std::atomic<int> g_truthEnds{0}, g_truthNoRelease{0}, g_truthRepeatPic{0}, g_truthSamePose{0};

// draws the next image of both eyes (a flat colour from the frame id, the id stamped in the left eye) and releases them
static bool drawFrame(int id) {
    const int nsc = g_array ? 1 : 2;   // MODSIM_ARRAY: one image holds both eyes
    for (int s = 0; s < nsc; s++) {
        XrSwapchainImageAcquireInfo ai{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO }; uint32_t idx;
        if (XR_FAILED(xrAcquireSwapchainImage(g_sc[s], &ai, &idx))) return false;
        XrSwapchainImageWaitInfo wi{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO }; wi.timeout = XR_INFINITE_DURATION;
        if (XR_FAILED(xrWaitSwapchainImage(g_sc[s], &wi))) return false;
        {
            std::lock_guard<std::mutex> g(g_gpuMx);
            for (int i = g_array ? 0 : s; i < (g_array ? 2 : s + 1); i++) {
                D3D11_RENDER_TARGET_VIEW_DESC rd{}; rd.Format = (DXGI_FORMAT)g_fmt;
                if (g_array) { rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2DARRAY; rd.Texture2DArray.FirstArraySlice = (UINT)i; rd.Texture2DArray.ArraySize = 1; }
                else rd.ViewDimension = g_samples > 1 ? D3D11_RTV_DIMENSION_TEXTURE2DMS : D3D11_RTV_DIMENSION_TEXTURE2D;
                ID3D11RenderTargetView* rtv = nullptr;
                g_dev->CreateRenderTargetView(g_imgs[s][idx].texture, &rd, &rtv);
                if (rtv) {
                    float c[4] = { 0.1f + 0.1f * (float)(id % 7), 0.2f, 0.3f + 0.05f * (float)(id % 5), 1.f };
                    if (g_strobe) { const bool gen = id >= 100000; c[0] = gen ? 0.8f : 0.f; c[1] = gen ? 0.f : 0.8f; c[2] = gen ? 0.8f : 0.f; }
                    g_ctx->ClearRenderTargetView(rtv, c);
                    ID3D11DeviceContext1* c1 = nullptr;
                    if (i == 0 && g_samples == 1 && SUCCEEDED(g_ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&c1))) {
                        const float white[4] = { 1, 1, 1, 1 }, black[4] = { 0, 0, 0, 1 };
                        for (int bi = 0; bi < 16; bi++) { D3D11_RECT r{ bi * 64, 0, (bi + 1) * 64, 64 }; c1->ClearView(rtv, ((id >> bi) & 1) ? white : black, &r, 1); }
                        c1->Release();
                    }
                    rtv->Release();
                }
            }
            g_ctx->Flush();
        }
        XrSwapchainImageReleaseInfo rl{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
        if (XR_FAILED(xrReleaseSwapchainImage(g_sc[s], &rl))) return false;
        g_lastIdx[s] = idx; g_relSinceEnd++;
    }
    g_newPicSinceEnd = true;
    return true;
}

// MODSIM_DUP_RELEASE: the next image of each swapchain receives a copy of the last picture and is released (the same picture, but a release)
static bool rereleaseLast() {
    const int nsc = g_array ? 1 : 2;
    for (int s = 0; s < nsc; s++) {
        XrSwapchainImageAcquireInfo ai{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO }; uint32_t idx;
        if (XR_FAILED(xrAcquireSwapchainImage(g_sc[s], &ai, &idx))) return false;
        XrSwapchainImageWaitInfo wi{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO }; wi.timeout = XR_INFINITE_DURATION;
        if (XR_FAILED(xrWaitSwapchainImage(g_sc[s], &wi))) return false;
        if (idx != g_lastIdx[s]) { std::lock_guard<std::mutex> g(g_gpuMx); g_ctx->CopyResource(g_imgs[s][idx].texture, g_imgs[s][g_lastIdx[s]].texture); g_ctx->Flush(); }
        XrSwapchainImageReleaseInfo rl{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
        if (XR_FAILED(xrReleaseSwapchainImage(g_sc[s], &rl))) return false;
        g_lastIdx[s] = idx; g_relSinceEnd++;
    }
    return true;
}

// MODSIM_QUAD: the quad layer's next picture (a flat colour that changes at every submit)
static bool drawQuad() {
    static int n = 0; n++;
    XrSwapchainImageAcquireInfo ai{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO }; uint32_t idx;
    if (XR_FAILED(xrAcquireSwapchainImage(g_quadSc, &ai, &idx))) return false;
    XrSwapchainImageWaitInfo wi{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO }; wi.timeout = XR_INFINITE_DURATION;
    if (XR_FAILED(xrWaitSwapchainImage(g_quadSc, &wi))) return false;
    {
        std::lock_guard<std::mutex> g(g_gpuMx);
        D3D11_RENDER_TARGET_VIEW_DESC rd{}; rd.Format = (DXGI_FORMAT)g_fmt; rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        ID3D11RenderTargetView* rtv = nullptr;
        g_dev->CreateRenderTargetView(g_quadImgs[idx].texture, &rd, &rtv);
        if (rtv) { const float c[4] = { 0.9f, 0.1f + 0.08f * (float)(n % 10), 0.1f, 1.f }; g_ctx->ClearRenderTargetView(rtv, c); rtv->Release(); }
        g_ctx->Flush();
    }
    XrSwapchainImageReleaseInfo rl{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
    if (XR_FAILED(xrReleaseSwapchainImage(g_quadSc, &rl))) return false;
    g_relSinceEnd++; g_newPicSinceEnd = true;
    return true;
}

static bool endFrame(const Task& t, XrTime displayTime, int id) {
    if (g_quad && !drawQuad()) return false;
    XrCompositionLayerProjectionView pv[2] = { { XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW }, { XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW } };
    for (int i = 0; i < 2; i++) {
        pv[i].pose = t.views[i].pose; pv[i].fov = t.views[i].fov;
        pv[i].subImage.swapchain = g_sc[i]; pv[i].subImage.imageRect = { { 0, 0 }, { (int)g_w[i], (int)g_h[i] } };
        pv[i].subImage.imageArrayIndex = g_array ? (uint32_t)i : 0;
    }
    XrCompositionLayerProjection layer{ XR_TYPE_COMPOSITION_LAYER_PROJECTION };
    layer.space = g_space; layer.viewCount = 2; layer.views = pv;
    XrCompositionLayerQuad quad{ XR_TYPE_COMPOSITION_LAYER_QUAD };   // MODSIM_QUAD: 0.6 m square 1.5 m ahead, in front of the projection
    quad.space = g_space; quad.eyeVisibility = XR_EYE_VISIBILITY_BOTH; quad.subImage.swapchain = g_quadSc;
    quad.subImage.imageRect = { { 0, 0 }, { kQuadPx, kQuadPx } }; quad.pose.orientation.w = 1.f; quad.pose.position.z = -1.5f; quad.size = { 0.6f, 0.6f };
    const XrCompositionLayerBaseHeader* lay[2] = { (XrCompositionLayerBaseHeader*)&layer, (XrCompositionLayerBaseHeader*)&quad };
    XrFrameEndInfo fe{ XR_TYPE_FRAME_END_INFO };
    fe.displayTime = displayTime; fe.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE; fe.layerCount = g_quad ? 2 : 1; fe.layers = lay;
    // the truth for this submit: images released since the previous submit, a picture never submitted before, the previous submit's poses
    const int rel = g_relSinceEnd.exchange(0);
    const bool newPic = g_newPicSinceEnd.exchange(false);
    const bool samePose = g_haveLastEnd && memcmp(&g_lastEndPose[0], &t.views[0].pose, sizeof(XrPosef)) == 0 && memcmp(&g_lastEndPose[1], &t.views[1].pose, sizeof(XrPosef)) == 0;
    g_lastEndPose[0] = t.views[0].pose; g_lastEndPose[1] = t.views[1].pose; g_haveLastEnd = true;
    g_truthEnds++; if (rel == 0) g_truthNoRelease++; if (!newPic) g_truthRepeatPic++; if (samePose) g_truthSamePose++;
    ev("END", (long long)displayTime, id, rel, newPic ? 1 : 0, samePose ? 1 : 0);
    return XR_SUCCEEDED(xrEndFrame(g_sess, &fe));
}

static void workerLoop(Worker* w) {
    for (;;) {
        Task t;
        {
            std::unique_lock<std::mutex> lk(w->m);
            w->cv.wait(lk, [&] { return w->stop || !w->q.empty(); });
            if (w->q.empty()) return;
            t = w->q.front(); w->q.pop_front();
        }
        if ((!t.paused && !drawFrame(t.id)) || !endFrame(t, t.time, t.id)) { g_failed = true; g_taskDone++; continue; }   // paused: the last picture again
        for (int k = 0, n = g_dupN.load(); k < n; k++) {
            // the extra cycle: a new frame (wait, begin) that only resubmits the same layers
            XrFrameState fs{ XR_TYPE_FRAME_STATE };
            ev("WAIT_IN", 0, t.id);
            if (XR_FAILED(xrWaitFrame(g_sess, nullptr, &fs))) { g_failed = true; g_taskDone++; continue; }
            ev("WAIT_OUT", (long long)fs.predictedDisplayTime, t.id);
            if (XR_FAILED(xrBeginFrame(g_sess, nullptr))) { g_failed = true; g_taskDone++; continue; }
            ev("BEGIN", (long long)fs.predictedDisplayTime, t.id);
            if (g_dupRelease && !rereleaseLast()) { g_failed = true; }
            if (!endFrame(t, fs.predictedDisplayTime, t.id)) { g_failed = true; }
        }
        g_taskDone++;
    }
}

int main(int argc, char** argv) {
    QueryPerformanceFrequency(&g_qpf);
    const char* logPath = argc > 1 ? argv[1] : "xr_modsim.log";
    const int realFrames = argc > 2 ? atoi(argv[2]) : 300;
    const int timeoutS = argc > 3 ? atoi(argv[3]) : 60;
    g_log = fopen(logPath, "w"); if (!g_log) return 2;
    if (const char* c = getenv("MODSIM_CSV")) { g_csv = fopen(c, "w"); if (g_csv) fprintf(g_csv, "t_ms,tid,ev,a,b,rel,new_pic,same_pose\n"); }
    const double engineMs = getenv("MODSIM_ENGINE_MS") ? atof(getenv("MODSIM_ENGINE_MS")) : 8.0;
    const int gpuCopies = getenv("MODSIM_GPU_COPIES") ? atoi(getenv("MODSIM_GPU_COPIES")) : 0;
    g_dupN = getenv("MODSIM_DUP") ? std::max(0, atoi(getenv("MODSIM_DUP"))) : 1;
    g_staticPose = getenv("MODSIM_STATIC_POSE") && atoi(getenv("MODSIM_STATIC_POSE")) != 0;
    g_ofxr = getenv("MODSIM_OFXR") && atoi(getenv("MODSIM_OFXR")) != 0;
    g_strobe = getenv("MODSIM_STROBE") && atoi(getenv("MODSIM_STROBE")) != 0;
    g_array = getenv("MODSIM_ARRAY") && atoi(getenv("MODSIM_ARRAY")) != 0;
    g_samples = std::max(1, getenv("MODSIM_MSAA") ? atoi(getenv("MODSIM_MSAA")) : 1);
    g_dupRelease = getenv("MODSIM_DUP_RELEASE") && atoi(getenv("MODSIM_DUP_RELEASE")) != 0;
    g_quad = getenv("MODSIM_QUAD") && atoi(getenv("MODSIM_QUAD")) != 0;
    double pauseFrom = 0.0, pauseTo = 0.0;   // MODSIM_PAUSE_S=a:b
    if (const char* ps = getenv("MODSIM_PAUSE_S")) { if (sscanf(ps, "%lf:%lf", &pauseFrom, &pauseTo) != 2 || pauseTo <= pauseFrom) { pauseFrom = pauseTo = 0.0; } }
    const double dupStopS = getenv("MODSIM_DUP_STOP_S") ? atof(getenv("MODSIM_DUP_STOP_S")) : 0.0;
    const double jitterMs = getenv("MODSIM_JITTER_MS") ? atof(getenv("MODSIM_JITTER_MS")) : 0.0;
    const double switchEngineMs = getenv("MODSIM_SWITCH_ENGINE_MS") ? atof(getenv("MODSIM_SWITCH_ENGINE_MS")) : -1.0;
    const double switchJitterMs = getenv("MODSIM_SWITCH_JITTER_MS") ? atof(getenv("MODSIM_SWITCH_JITTER_MS")) : -1.0;
    uint32_t rng = 12345u;
    auto rnd = [&]() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return (double)(rng & 0xFFFFFF) / 16777216.0; };   // 0..1
    const int pool = std::max(1, getenv("MODSIM_POOL") ? atoi(getenv("MODSIM_POOL")) : 9);
    const double switchS = getenv("MODSIM_SWITCH_S") ? atof(getenv("MODSIM_SWITCH_S")) : 0.0;
    L("start real_frames=%d timeout=%ds engine_ms=%.1f gpu_copies=%d dup=%d pool=%d switch_s=%.1f dup_stop_s=%.1f static_pose=%d jitter_ms=%.1f switch_engine_ms=%.1f switch_jitter_ms=%.1f", realFrames, timeoutS, engineMs, gpuCopies, g_dupN.load(), pool, switchS, dupStopS, (int)g_staticPose, jitterMs, switchEngineMs, switchJitterMs);
    L("shapes: array=%d msaa=%d dup_release=%d quad=%d pause_s=%.1f:%.1f ofxr=%d", (int)g_array, g_samples, (int)g_dupRelease, (int)g_quad, pauseFrom, pauseTo, (int)g_ofxr);
    if (g_array && g_samples > 1) { L("FAIL MODSIM_MSAA with MODSIM_ARRAY is not supported"); return 2; }

    XrInstanceCreateInfo ici{ XR_TYPE_INSTANCE_CREATE_INFO };
    const char* ext[] = { XR_KHR_D3D11_ENABLE_EXTENSION_NAME };
    strcpy(ici.applicationInfo.applicationName, "xr_modsim"); ici.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
    ici.enabledExtensionCount = 1; ici.enabledExtensionNames = ext;
    XrInstance inst; CK(xrCreateInstance(&ici, &inst));
    XrSystemGetInfo sgi{ XR_TYPE_SYSTEM_GET_INFO }; sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId sys; CK(xrGetSystem(inst, &sgi, &sys));
    XrGraphicsRequirementsD3D11KHR req{ XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR };
    PFN_xrGetD3D11GraphicsRequirementsKHR getReq;
    CK(xrGetInstanceProcAddr(inst, "xrGetD3D11GraphicsRequirementsKHR", (PFN_xrVoidFunction*)&getReq));
    CK(getReq(inst, sys, &req));
    IDXGIFactory1* fac; CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&fac);
    IDXGIAdapter1* ad = nullptr;
    for (UINT i = 0; fac->EnumAdapters1(i, &ad) == S_OK; i++) { DXGI_ADAPTER_DESC1 d; ad->GetDesc1(&d); if (memcmp(&d.AdapterLuid, &req.adapterLuid, sizeof(LUID)) == 0) break; ad->Release(); ad = nullptr; }
    D3D_FEATURE_LEVEL fl = req.minFeatureLevel;
    if (FAILED(D3D11CreateDevice(ad, ad ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, 0, 0, &fl, 1, D3D11_SDK_VERSION, &g_dev, nullptr, &g_ctx))) { L("FAIL D3D11CreateDevice"); return 1; }
    ID3D11Multithread* mt = nullptr; if (SUCCEEDED(g_dev->QueryInterface(__uuidof(ID3D11Multithread), (void**)&mt))) { mt->SetMultithreadProtected(TRUE); mt->Release(); }
    XrGraphicsBindingD3D11KHR gb{ XR_TYPE_GRAPHICS_BINDING_D3D11_KHR }; gb.device = g_dev;
    XrSessionCreateInfo sci{ XR_TYPE_SESSION_CREATE_INFO }; sci.next = &gb; sci.systemId = sys;
    CK(xrCreateSession(inst, &sci, &g_sess));
    XrReferenceSpaceCreateInfo rsi{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO }; rsi.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL; rsi.poseInReferenceSpace.orientation.w = 1;
    CK(xrCreateReferenceSpace(g_sess, &rsi, &g_space));
    uint32_t n; CK(xrEnumerateViewConfigurationViews(inst, sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &n, nullptr));
    std::vector<XrViewConfigurationView> vcv(n, { XR_TYPE_VIEW_CONFIGURATION_VIEW });
    CK(xrEnumerateViewConfigurationViews(inst, sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, n, &n, vcv.data()));
    CK(xrEnumerateSwapchainFormats(g_sess, 0, &n, nullptr));
    std::vector<int64_t> fmts(n); CK(xrEnumerateSwapchainFormats(g_sess, n, &n, fmts.data()));
    g_fmt = fmts[0]; for (int64_t f : fmts) if (f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) g_fmt = f;
    for (int i = 0; i < 2; i++) {
        g_w[i] = vcv[i].recommendedImageRectWidth; g_h[i] = vcv[i].recommendedImageRectHeight;
        if (g_array) { g_w[i] = std::max(vcv[0].recommendedImageRectWidth, vcv[1].recommendedImageRectWidth); g_h[i] = std::max(vcv[0].recommendedImageRectHeight, vcv[1].recommendedImageRectHeight); }
        if (g_array && i == 1) { g_sc[1] = g_sc[0]; g_imgs[1] = g_imgs[0]; continue; }   // one swapchain, slice = eye
        XrSwapchainCreateInfo si{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
        si.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        si.format = g_fmt; si.sampleCount = (uint32_t)g_samples; si.faceCount = 1; si.arraySize = g_array ? 2 : 1; si.mipCount = 1;
        si.width = g_w[i]; si.height = g_h[i];
        CK(xrCreateSwapchain(g_sess, &si, &g_sc[i]));
        uint32_t c; CK(xrEnumerateSwapchainImages(g_sc[i], 0, &c, nullptr));
        g_imgs[i].assign(c, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        CK(xrEnumerateSwapchainImages(g_sc[i], c, &c, (XrSwapchainImageBaseHeader*)g_imgs[i].data()));
    }
    if (g_quad) {
        XrSwapchainCreateInfo si{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
        si.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        si.format = g_fmt; si.sampleCount = 1; si.faceCount = 1; si.arraySize = 1; si.mipCount = 1; si.width = kQuadPx; si.height = kQuadPx;
        CK(xrCreateSwapchain(g_sess, &si, &g_quadSc));
        uint32_t c; CK(xrEnumerateSwapchainImages(g_quadSc, 0, &c, nullptr));
        g_quadImgs.assign(c, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        CK(xrEnumerateSwapchainImages(g_quadSc, c, &c, (XrSwapchainImageBaseHeader*)g_quadImgs.data()));
    }
    // GPU load: copies of a big texture on the render thread's engine frames
    ID3D11Texture2D* loadTex[2] = {};
    if (gpuCopies > 0) {
        D3D11_TEXTURE2D_DESC td{}; td.Width = 4096; td.Height = 4096; td.MipLevels = 1; td.ArraySize = 1; td.Format = DXGI_FORMAT_R16G16B16A16_FLOAT; td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT;
        g_dev->CreateTexture2D(&td, nullptr, &loadTex[0]); g_dev->CreateTexture2D(&td, nullptr, &loadTex[1]);
    }

    std::vector<Worker*> workers;
    for (int i = 0; i < pool; i++) { auto* w = new Worker(); w->th = std::thread(workerLoop, w); workers.push_back(w); }

    bool running = false, exiting = false; int ef = 0, posted = 0, rc = 0;
    const ULONGLONG t0 = GetTickCount64(); double loopStart = 0;
    while (!exiting && posted < realFrames) {
        if (GetTickCount64() - t0 > (ULONGLONG)timeoutS * 1000) { L("FAIL timeout (posted=%d/%d)", posted, realFrames); rc = 3; break; }
        if (g_failed) { L("FAIL an xr call in a worker failed"); rc = 4; break; }
        XrEventDataBuffer e{ XR_TYPE_EVENT_DATA_BUFFER };
        if (xrPollEvent(inst, &e) == XR_SUCCESS) {
            if (e.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                auto* s = (XrEventDataSessionStateChanged*)&e;
                L("event: session state -> %d", (int)s->state);
                if (s->state == XR_SESSION_STATE_READY) { XrSessionBeginInfo bi{ XR_TYPE_SESSION_BEGIN_INFO }; bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO; CK(xrBeginSession(g_sess, &bi)); running = true; }
                else if (s->state == XR_SESSION_STATE_STOPPING) { CK(xrEndSession(g_sess)); running = false; }
                else if (s->state == XR_SESSION_STATE_EXITING || s->state == XR_SESSION_STATE_LOSS_PENDING) exiting = true;
            }
            continue;
        }
        if (!running) { Sleep(10); continue; }
        if (loopStart == 0) loopStart = qpcMs();

        // an engine frame: CPU work and GPU copies (only the parity frames talk to OpenXR)
        ev("EF", ef, ef & 1);
        const bool switched = switchS > 0 && loopStart > 0 && (qpcMs() - loopStart) / 1000.0 >= switchS;
        if (dupStopS > 0 && loopStart > 0 && (qpcMs() - loopStart) / 1000.0 >= dupStopS && g_dupN.load() != 0) { g_dupN = 0; L("duplicates stop now"); }
        {
            const double base = (switched && switchEngineMs >= 0) ? switchEngineMs : engineMs;
            const double jit = (switched && switchJitterMs >= 0) ? switchJitterMs : jitterMs;
            const double cost = std::max(0.0, base + (jit > 0 ? (rnd() * 2.0 - 1.0) * jit : 0.0));
            const double t = qpcMs(); while (qpcMs() - t < cost) YieldProcessor();
        }
        if (loadTex[0]) { std::lock_guard<std::mutex> g(g_gpuMx); for (int k = 0; k < gpuCopies; k++) g_ctx->CopyResource(loadTex[(k + 1) & 1], loadTex[k & 1]); g_ctx->Flush(); }
        const bool vrFrame = g_ofxr || (ef & 1) == 1;
        ef++;
        if (!vrFrame) continue;

        while (g_taskDone.load() < posted && !g_failed && GetTickCount64() - t0 <= (ULONGLONG)timeoutS * 1000) Sleep(0);   // the worker finishes the last frame first
        XrFrameState fs{ XR_TYPE_FRAME_STATE };
        ev("WAIT_IN", 0, posted + 1);
        XrResult r = xrWaitFrame(g_sess, nullptr, &fs); if (XR_FAILED(r)) return fail("xrWaitFrame", r);
        ev("WAIT_OUT", (long long)fs.predictedDisplayTime, posted + 1);
        r = xrBeginFrame(g_sess, nullptr); if (XR_FAILED(r)) return fail("xrBeginFrame", r);
        ev("BEGIN", (long long)fs.predictedDisplayTime, posted + 1);
        Task t{}; t.id = posted + 1; t.time = fs.predictedDisplayTime;
        XrViewLocateInfo li{ XR_TYPE_VIEW_LOCATE_INFO }; li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO; li.displayTime = fs.predictedDisplayTime; li.space = g_space;
        XrViewState vs{ XR_TYPE_VIEW_STATE }; XrView views[2] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } }; uint32_t vc;
        r = xrLocateViews(g_sess, &li, &vs, 2, &vc, views); if (XR_FAILED(r)) return fail("xrLocateViews", r);
        static XrView firstViews[2]; static bool haveFirst = false;
        if (g_staticPose) { if (!haveFirst) { firstViews[0] = views[0]; firstViews[1] = views[1]; haveFirst = true; } views[0] = firstViews[0]; views[1] = firstViews[1]; }
        t.views[0] = views[0]; t.views[1] = views[1];
        {   // MODSIM_PAUSE_S: inside the window the frame resubmits the last drawn picture with its poses and id (no draw, no release)
            static Task lastDrawn{}; static bool haveDrawn = false; static int pauseState = 0;
            const double ls = (qpcMs() - loopStart) / 1000.0;
            const bool paused = pauseTo > pauseFrom && ls >= pauseFrom && ls < pauseTo && haveDrawn;
            if (paused) { t.views[0] = lastDrawn.views[0]; t.views[1] = lastDrawn.views[1]; t.id = lastDrawn.id; t.paused = true; }
            else { lastDrawn = t; haveDrawn = true; }
            if (paused && pauseState == 0) { pauseState = 1; L("pause: from now on the last picture again (%.1f s)", ls); }
            if (!paused && pauseState == 1) { pauseState = 2; L("pause ends (%.1f s)", ls); }
        }
        if (g_ofxr) {   // the layer's end: generated frame in the app's slot, then an internal cycle for the real frame (same poses, other image)
            if (!drawFrame(t.id + 100000) || !endFrame(t, t.time, t.id)) { L("FAIL ofxr generated frame"); rc = 4; break; }
            XrFrameState fs2{ XR_TYPE_FRAME_STATE };
            ev("WAIT_IN", 0, t.id);
            if (XR_FAILED(xrWaitFrame(g_sess, nullptr, &fs2))) { L("FAIL ofxr internal wait"); rc = 4; break; }
            ev("WAIT_OUT", (long long)fs2.predictedDisplayTime, t.id);
            if (XR_FAILED(xrBeginFrame(g_sess, nullptr))) { L("FAIL ofxr internal begin"); rc = 4; break; }
            ev("BEGIN", (long long)fs2.predictedDisplayTime, t.id);
            if (!drawFrame(t.id) || !endFrame(t, fs2.predictedDisplayTime, t.id)) { L("FAIL ofxr real frame"); rc = 4; break; }
            posted++; g_taskDone++;
            continue;
        }
        const bool single = switchS > 0 && (qpcMs() - loopStart) / 1000.0 >= switchS;
        static bool announced = false; if (single && !announced) { announced = true; L("switch: from now on one worker thread (%.1f s)", (qpcMs() - loopStart) / 1000.0); }
        Worker* w = workers[single ? 0 : (size_t)(posted % pool)];
        { std::lock_guard<std::mutex> lk(w->m); w->q.push_back(t); }
        w->cv.notify_one();
        posted++;
    }
    while (g_taskDone.load() < posted && GetTickCount64() - t0 < (ULONGLONG)(timeoutS + 5) * 1000) Sleep(1);
    const double secs = (qpcMs() - loopStart) / 1000.0;
    L("done: %d real frames in %.1f s (%.1f real fps), %d engine frames, rc=%d", posted, secs, secs > 0 ? posted / secs : 0.0, ef, rc);
    L("identity truth: %d submits, %d with no image released, %d repeating an earlier picture, %d with the previous submit's poses",
      g_truthEnds.load(), g_truthNoRelease.load(), g_truthRepeatPic.load(), g_truthSamePose.load());
    if (running) {   // leave the session the way a game does
        xrRequestExitSession(g_sess);
        for (int i = 0; i < 200 && running; i++) {
            XrEventDataBuffer e{ XR_TYPE_EVENT_DATA_BUFFER };
            if (xrPollEvent(inst, &e) == XR_SUCCESS && e.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED && ((XrEventDataSessionStateChanged*)&e)->state == XR_SESSION_STATE_STOPPING) { xrEndSession(g_sess); running = false; }
            else Sleep(10);
        }
    }
    for (auto* w : workers) { { std::lock_guard<std::mutex> lk(w->m); w->stop = true; } w->cv.notify_all(); }
    for (auto* w : workers) w->th.join();
    if (g_csv) fflush(g_csv);
    xrDestroySession(g_sess); xrDestroyInstance(inst);
    L(rc == 0 ? "PASS (rc=0)" : "FAIL rc=%d", rc);
    return rc;
}
