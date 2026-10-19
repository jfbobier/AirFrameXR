// mirrordesktop.exe: the Windows desktop in the headset, as an ordinary OpenXR application. The streamer knows nothing about it: it is an app like any game
// (it connects through the runtime and the shim, renders a projection layer with the views the runtime gives it, reads the controllers through OpenXR
// actions), except that the host does not record it as a game. Its own jobs: a monitor-sized screen that floats in front of the user and follows them
// when they turn away, a laser beam from the controller, and the mouse (SendInput): the beam moves the cursor, trigger = left button, grip = right button.
// Everything that is not the screen is 00FF00, which the headset shows as passthrough.
//
//   mirrordesktop.exe [--log <file>] [--seconds N] [--no-mouse] [--clear RRGGBB]
//
// The picture (desktop capture, HDR -> SDR, the quad, the beam) is desktop.cpp, tested on the GPU by harness/mirror_check.py.
// A game starting from the headset takes the headset over: the shim signals Local\VisionALVR_GameStarting and this app ends its session at once.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <algorithm>
#include "desktop.cpp"
#include "mirror_math.h"
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_PLATFORM_WIN32
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

static FILE* g_log;
static void L(const char* fmt, ...) {
    if (!g_log) return;
    va_list a; va_start(a, fmt);
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(g_log, "%04d-%02d-%02d %02d:%02d:%02d.%03d [mirrordesktop] ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    vfprintf(g_log, fmt, a); fputc('\n', g_log); fflush(g_log);
    va_end(a);
}
#define CK(x) do { XrResult r_ = (x); if (XR_FAILED(r_)) { L("FAIL %s -> %d", #x, (int)r_); return 1; } } while (0)

static Q toQ(XrQuaternionf q) { return { q.x, q.y, q.z, q.w }; }
static V3 toV(XrVector3f v) { return { v.x, v.y, v.z }; }
static Eye toEye(const XrView& v) { return { toQ(v.pose.orientation), toV(v.pose.position), std::tan(v.fov.angleLeft), std::tan(v.fov.angleRight), std::tan(v.fov.angleUp), std::tan(v.fov.angleDown) }; }

// ------------------------------------------------------------------------------------------------------------------ the mouse
static void sendMouse(LONG dx, LONG dy, DWORD flags) {
    INPUT i{}; i.type = INPUT_MOUSE; i.mi.dx = dx; i.mi.dy = dy; i.mi.dwFlags = flags;
    SendInput(1, &i, sizeof i);
}
// The captured monitor is the primary one; with several monitors its rectangle is only a part of the virtual desktop, and an absolute move is over the whole of it
// (MOUSEEVENTF_VIRTUALDESK). The process is per-monitor DPI aware, so every number here is a physical pixel.
static RECT g_mon{ 0, 0, 1920, 1080 };
static void findPrimaryMonitor() {
    const HMONITOR m = MonitorFromPoint(POINT{ 0, 0 }, MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO mi{ sizeof mi }; if (GetMonitorInfoW(m, &mi)) g_mon = mi.rcMonitor;
}
static void mouseTo(float u, float v) {
    const int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN), vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    const double px = g_mon.left + std::max(0.f, std::min(1.f, u)) * (g_mon.right - g_mon.left - 1), py = g_mon.top + std::max(0.f, std::min(1.f, v)) * (g_mon.bottom - g_mon.top - 1);
    sendMouse((LONG)((px - vx) * 65535.0 / std::max(1, vw - 1) + 0.5), (LONG)((py - vy) * 65535.0 / std::max(1, vh - 1) + 0.5), MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
}
static void wheel(int ticks) { INPUT i{}; i.type = INPUT_MOUSE; i.mi.dwFlags = MOUSEEVENTF_WHEEL; i.mi.mouseData = (DWORD)(ticks * WHEEL_DELTA); SendInput(1, &i, sizeof i); }

int main(int argc, char** argv) {
    std::string logPath; int seconds = 0; bool mouse = true; uint32_t clear = 0x00FF00;
    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--log") && i + 1 < argc) logPath = argv[++i];
        else if (!strcmp(argv[i], "--seconds") && i + 1 < argc) seconds = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--no-mouse")) mouse = false;
        else if (!strcmp(argv[i], "--clear") && i + 1 < argc) clear = (uint32_t)strtoul(argv[++i], nullptr, 16) & 0xFFFFFF;
    }
    if (logPath.empty()) {   // <package>\logs\mirrordesktop.log (the exe is in <package>\bin)
        char exe[MAX_PATH]; GetModuleFileNameA(nullptr, exe, MAX_PATH);
        std::string d = exe; d = d.substr(0, d.find_last_of("\\/")); d = d.substr(0, d.find_last_of("\\/"));
        CreateDirectoryA((d + "\\logs").c_str(), nullptr);
        logPath = d + "\\logs\\mirrordesktop.log";
    }
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    findPrimaryMonitor();
    g_log = fopen(logPath.c_str(), "a");
    L("start (clear colour %06x, mouse %s)", clear, mouse ? "on" : "off");
    HANDLE gameEvent = CreateEventW(nullptr, TRUE, FALSE, L"Local\\VisionALVR_GameStarting");   // the shim of a game that starts sets it

    std::vector<const char*> ext = { XR_KHR_D3D11_ENABLE_EXTENSION_NAME };
    XrInstanceCreateInfo ici{ XR_TYPE_INSTANCE_CREATE_INFO };
    strcpy(ici.applicationInfo.applicationName, "mirrordesktop");
    ici.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
    ici.enabledExtensionCount = (uint32_t)ext.size(); ici.enabledExtensionNames = ext.data();
    XrInstance inst; CK(xrCreateInstance(&ici, &inst));
    XrSystemGetInfo sgi{ XR_TYPE_SYSTEM_GET_INFO }; sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId sys; CK(xrGetSystem(inst, &sgi, &sys));
    XrGraphicsRequirementsD3D11KHR req{ XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR };
    PFN_xrGetD3D11GraphicsRequirementsKHR getReq; CK(xrGetInstanceProcAddr(inst, "xrGetD3D11GraphicsRequirementsKHR", (PFN_xrVoidFunction*)&getReq)); CK(getReq(inst, sys, &req));
    ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr;
    IDXGIFactory1* fac; CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&fac);
    IDXGIAdapter1* ad = nullptr;
    for (UINT i = 0; fac->EnumAdapters1(i, &ad) == S_OK; i++) { DXGI_ADAPTER_DESC1 d; ad->GetDesc1(&d); if (!memcmp(&d.AdapterLuid, &req.adapterLuid, sizeof(LUID))) break; ad->Release(); ad = nullptr; }
    D3D_FEATURE_LEVEL fl = req.minFeatureLevel;
    if (FAILED(D3D11CreateDevice(ad, ad ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, 0, 0, &fl, 1, D3D11_SDK_VERSION, &dev, nullptr, &ctx))) { L("FAIL D3D11CreateDevice"); return 1; }

    uint32_t n; CK(xrEnumerateViewConfigurationViews(inst, sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &n, nullptr));
    std::vector<XrViewConfigurationView> vcv(n, { XR_TYPE_VIEW_CONFIGURATION_VIEW });
    CK(xrEnumerateViewConfigurationViews(inst, sys, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, n, &n, vcv.data()));
    const int EW = (int)vcv[0].recommendedImageRectWidth, EH = (int)vcv[0].recommendedImageRectHeight;
    L("views: %u of %dx%d", n, EW, EH);

    // the picture: both eyes side by side in one target, copied to the eyes' swapchains
    DesktopMirror mirror(dev, ctx, EW * 2, EH, DXGI_FORMAT_R8G8B8A8_UNORM);
    char err[256] = {};
    if (!mirror.Init(err, 256)) { L("desktop capture unavailable: %s", err); return 2; }
    L("capturing the desktop %dx%d%s", mirror.DesktopW(), mirror.DesktopH(), mirror.Hdr() ? " (HDR, converted to SDR)" : "");
    const float aspect = (float)mirror.DesktopH() / (float)mirror.DesktopW();

    XrGraphicsBindingD3D11KHR gb{ XR_TYPE_GRAPHICS_BINDING_D3D11_KHR }; gb.device = dev;
    XrSessionCreateInfo sci{ XR_TYPE_SESSION_CREATE_INFO }; sci.next = &gb; sci.systemId = sys;
    XrSession sess; CK(xrCreateSession(inst, &sci, &sess));
    XrSpace space, viewSpace;
    { XrReferenceSpaceCreateInfo rs{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO }; rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL; rs.poseInReferenceSpace.orientation.w = 1; CK(xrCreateReferenceSpace(sess, &rs, &space));
      rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW; CK(xrCreateReferenceSpace(sess, &rs, &viewSpace)); }

    // the controllers: aim pose, trigger, grip (the Touch profile: what the runtime offers for hands and controllers alike)
    XrPath hands[2]; xrStringToPath(inst, "/user/hand/left", &hands[0]); xrStringToPath(inst, "/user/hand/right", &hands[1]);
    XrActionSet aset; XrAction aAim{}, aTrigger{}, aSqueeze{}, aStick{};
    { XrActionSetCreateInfo asci{ XR_TYPE_ACTION_SET_CREATE_INFO }; strcpy(asci.actionSetName, "mirror"); strcpy(asci.localizedActionSetName, "Desktop mirror"); CK(xrCreateActionSet(inst, &asci, &aset)); }
    auto mk = [&](const char* name, XrActionType type, XrAction* out) { XrActionCreateInfo ai{ XR_TYPE_ACTION_CREATE_INFO }; strcpy(ai.actionName, name); strcpy(ai.localizedActionName, name); ai.actionType = type; ai.countSubactionPaths = 2; ai.subactionPaths = hands; return xrCreateAction(aset, &ai, out); };
    CK(mk("aim", XR_ACTION_TYPE_POSE_INPUT, &aAim)); CK(mk("trigger", XR_ACTION_TYPE_FLOAT_INPUT, &aTrigger)); CK(mk("squeeze", XR_ACTION_TYPE_FLOAT_INPUT, &aSqueeze)); CK(mk("stick", XR_ACTION_TYPE_VECTOR2F_INPUT, &aStick));
    {
        std::vector<XrActionSuggestedBinding> b;
        auto bind = [&](XrAction a, const char* path) { XrPath p; xrStringToPath(inst, path, &p); b.push_back({ a, p }); };
        bind(aAim, "/user/hand/left/input/aim/pose"); bind(aAim, "/user/hand/right/input/aim/pose");
        bind(aTrigger, "/user/hand/left/input/trigger/value"); bind(aTrigger, "/user/hand/right/input/trigger/value");
        bind(aSqueeze, "/user/hand/left/input/squeeze/value"); bind(aSqueeze, "/user/hand/right/input/squeeze/value");
        bind(aStick, "/user/hand/left/input/thumbstick"); bind(aStick, "/user/hand/right/input/thumbstick");
        XrPath prof; xrStringToPath(inst, "/interaction_profiles/oculus/touch_controller", &prof);
        XrInteractionProfileSuggestedBinding sb{ XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING }; sb.interactionProfile = prof; sb.countSuggestedBindings = (uint32_t)b.size(); sb.suggestedBindings = b.data();
        CK(xrSuggestInteractionProfileBindings(inst, &sb));
    }
    XrSessionActionSetsAttachInfo ati{ XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO }; ati.countActionSets = 1; ati.actionSets = &aset; CK(xrAttachSessionActionSets(sess, &ati));
    XrSpace aimSpace[2];
    for (int h = 0; h < 2; h++) { XrActionSpaceCreateInfo si{ XR_TYPE_ACTION_SPACE_CREATE_INFO }; si.action = aAim; si.subactionPath = hands[h]; si.poseInActionSpace.orientation.w = 1; CK(xrCreateActionSpace(sess, &si, &aimSpace[h])); }

    // swapchains: one per eye; the sRGB 8-bit format shares its bits with the target's UNORM one, so the picture is copied as it is
    CK(xrEnumerateSwapchainFormats(sess, 0, &n, nullptr));
    std::vector<int64_t> fmts(n); CK(xrEnumerateSwapchainFormats(sess, n, &n, fmts.data()));
    int64_t fmt = 0; for (int64_t f : fmts) if (f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) fmt = f;
    if (!fmt) { L("FAIL no R8G8B8A8_UNORM_SRGB swapchain format"); return 1; }
    XrSwapchain sc[2]; std::vector<XrSwapchainImageD3D11KHR> imgs[2];
    for (int i = 0; i < 2; i++) {
        XrSwapchainCreateInfo si{ XR_TYPE_SWAPCHAIN_CREATE_INFO }; si.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        si.format = fmt; si.sampleCount = 1; si.faceCount = 1; si.arraySize = 1; si.mipCount = 1; si.width = EW; si.height = EH;
        CK(xrCreateSwapchain(sess, &si, &sc[i]));
        uint32_t c; CK(xrEnumerateSwapchainImages(sc[i], 0, &c, nullptr));
        imgs[i].assign(c, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        CK(xrEnumerateSwapchainImages(sc[i], c, &c, (XrSwapchainImageBaseHeader*)imgs[i].data()));
    }

    // state
    Screen screen{}; bool haveScreen = false, replace = true, moving = false; int64_t lastTime = 0;
    bool seenHand = false, seenHit = false; bool left = false, right = false; ULONGLONG lastWheel = 0;
    ULONGLONG t0 = GetTickCount64(); bool running = false, exiting = false, endKey = false; uint64_t frames = 0;
    int rc = 0;
    while (!exiting) {
        if (seconds > 0 && GetTickCount64() - t0 > (ULONGLONG)seconds * 1000) { L("done after %d s", seconds); break; }
        if (WaitForSingleObject(gameEvent, 0) == WAIT_OBJECT_0) { L("a game is starting: giving the headset back"); if (running) xrRequestExitSession(sess); else break; }
        XrEventDataBuffer ev{ XR_TYPE_EVENT_DATA_BUFFER };
        if (xrPollEvent(inst, &ev) == XR_SUCCESS) {
            if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                auto* e = (XrEventDataSessionStateChanged*)&ev;
                if (e->state == XR_SESSION_STATE_READY) { XrSessionBeginInfo bi{ XR_TYPE_SESSION_BEGIN_INFO }; bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO; CK(xrBeginSession(sess, &bi)); running = true; L("session running"); }
                else if (e->state == XR_SESSION_STATE_STOPPING) { xrEndSession(sess); running = false; }
                else if (e->state == XR_SESSION_STATE_EXITING || e->state == XR_SESSION_STATE_LOSS_PENDING) exiting = true;
            }
            continue;
        }
        if (!running) { Sleep(10); continue; }

        XrFrameState fs{ XR_TYPE_FRAME_STATE }; CK(xrWaitFrame(sess, nullptr, &fs)); CK(xrBeginFrame(sess, nullptr));
        std::vector<const XrCompositionLayerBaseHeader*> lay; XrCompositionLayerProjection layer{ XR_TYPE_COMPOSITION_LAYER_PROJECTION }; XrCompositionLayerProjectionView pv[2]{};
        if (fs.shouldRender) {
            XrActiveActionSet act{ aset, XR_NULL_PATH }; XrActionsSyncInfo syn{ XR_TYPE_ACTIONS_SYNC_INFO }; syn.countActiveActionSets = 1; syn.activeActionSets = &act; xrSyncActions(sess, &syn);
            XrViewLocateInfo li{ XR_TYPE_VIEW_LOCATE_INFO }; li.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO; li.displayTime = fs.predictedDisplayTime; li.space = space;
            XrViewState vst{ XR_TYPE_VIEW_STATE }; XrView views[2] = { { XR_TYPE_VIEW }, { XR_TYPE_VIEW } }; uint32_t vc = 0;
            XrSpaceLocation head{ XR_TYPE_SPACE_LOCATION }; xrLocateSpace(viewSpace, space, fs.predictedDisplayTime, &head);
            if (XR_SUCCEEDED(xrLocateViews(sess, &li, &vst, 2, &vc, views)) && vc == 2 && (head.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) && (head.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT)) {
                // the screen: put in front at the start (and at End), then following lazily
                const Q hq = toQ(head.pose.orientation); const V3 hp = toV(head.pose.position);
                const float dt = lastTime ? (float)((fs.predictedDisplayTime - lastTime) * 1e-9) : 0.f; lastTime = fs.predictedDisplayTime;
                const Screen target = place(hq, hp, aspect);
                const bool key = (GetAsyncKeyState(VK_END) & 0x8000) != 0; if (key && !endKey) replace = true; endKey = key;
                if (!haveScreen || replace) { screen = target; haveScreen = true; replace = false; moving = false; } else screen = follow(screen, target, moving, dt);

                // the pointer: the right controller (the left one when the right is not tracked); its aim along -Z
                bool aimOk = false; V3 o{}, d{}; int hand = 1; float trig = 0, sq = 0, stickY = 0;
                for (int k = 0; k < 2 && !aimOk; k++) {
                    hand = k == 0 ? 1 : 0;
                    XrSpaceLocation al{ XR_TYPE_SPACE_LOCATION }; xrLocateSpace(aimSpace[hand], space, fs.predictedDisplayTime, &al);
                    if ((al.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) && (al.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT)) {
                        o = toV(al.pose.position); d = norm(rotate(toQ(al.pose.orientation), { 0, 0, -1 })); aimOk = true;
                        XrActionStateGetInfo gi{ XR_TYPE_ACTION_STATE_GET_INFO }; gi.subactionPath = hands[hand];
                        XrActionStateFloat st{ XR_TYPE_ACTION_STATE_FLOAT };
                        gi.action = aTrigger; xrGetActionStateFloat(sess, &gi, &st); trig = st.isActive ? st.currentState : 0;
                        gi.action = aSqueeze; xrGetActionStateFloat(sess, &gi, &st); sq = st.isActive ? st.currentState : 0;
                        XrActionStateVector2f sv{ XR_TYPE_ACTION_STATE_VECTOR2F }; gi.action = aStick; xrGetActionStateVector2f(sess, &gi, &sv); stickY = sv.isActive ? sv.currentState.y : 0;
                    }
                }
                float cu = -1, cv = -1; bool hit = false; float u = 0, v = 0;
                if (aimOk) {
                    if (frames % 300 == 0 && frames < 3000) L("aim: %s hand at (%.2f %.2f %.2f) pointing (%.2f %.2f %.2f); screen centre (%.2f %.2f %.2f) normal (%.2f %.2f %.2f) %.2fx%.2f m; head (%.2f %.2f %.2f)", hand ? "right" : "left", o.x, o.y, o.z, d.x, d.y, d.z, screen.center.x, screen.center.y, screen.center.z, screen.normal.x, screen.normal.y, screen.normal.z, screen.width, screen.height, hp.x, hp.y, hp.z);
                    if (!seenHand) { seenHand = true; L("the %s controller / hand is tracked: its beam is drawn", hand ? "right" : "left"); }
                    hit = rayHit(screen, o, d, u, v);
                    if (hit) { cu = u; cv = v; if (!seenHit) { seenHit = true; L("the pointer is on the screen"); } }
                }
                if (mouse) {
                    if (hit) mouseTo(u, v);
                    const bool l = hit && trig > 0.5f, r = hit && sq > 0.7f;
                    if (l != left) sendMouse(0, 0, l ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP);
                    if (r != right) sendMouse(0, 0, r ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP);
                    left = l; right = r;
                    // the thumbstick scrolls (a tick every 120 ms at full deflection, over the screen only)
                    if (hit && std::fabs(stickY) > 0.5f && GetTickCount64() - lastWheel > (ULONGLONG)(220 - 100 * std::fabs(stickY))) { wheel(stickY > 0 ? 1 : -1); lastWheel = GetTickCount64(); }
                }

                // the picture
                float ndc[16], extra[10]; for (float& f : ndc) f = NAN; for (float& f : extra) f = NAN; extra[0] = cu; extra[1] = cv;
                bool any = false;
                for (int e = 0; e < 2; e++) if (corners(screen, toEye(views[e]), ndc + e * 8)) any = true; else for (int k = 0; k < 8; k++) ndc[e * 8 + k] = NAN;
                if (aimOk) {
                    const V3 end = hit ? screenPoint(screen, u, v) : add(o, mul(d, 3.f));
                    for (int e = 0; e < 2; e++) segment(toEye(views[e]), o, end, extra + 2 + e * 4);
                }
                ID3D11Texture2D* tex = mirror.Render(ndc, extra, clear, err, 256);
                for (int e = 0; e < 2 && tex; e++) {
                    XrSwapchainImageAcquireInfo ai{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO }; uint32_t idx; CK(xrAcquireSwapchainImage(sc[e], &ai, &idx));
                    XrSwapchainImageWaitInfo wi{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO }; wi.timeout = XR_INFINITE_DURATION; CK(xrWaitSwapchainImage(sc[e], &wi));
                    D3D11_BOX box{ (UINT)(e * EW), 0, 0, (UINT)((e + 1) * EW), (UINT)EH, 1 };
                    ctx->CopySubresourceRegion(imgs[e][idx].texture, 0, 0, 0, 0, tex, 0, &box);
                    ctx->Flush();
                    XrSwapchainImageReleaseInfo rl{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO }; CK(xrReleaseSwapchainImage(sc[e], &rl));
                    pv[e].type = XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW; pv[e].pose = views[e].pose; pv[e].fov = views[e].fov;
                    pv[e].subImage.swapchain = sc[e]; pv[e].subImage.imageRect = { { 0, 0 }, { EW, EH } };
                }
                if (tex && any) { layer.space = space; layer.viewCount = 2; layer.views = pv; lay.push_back((XrCompositionLayerBaseHeader*)&layer); }
                else if (tex) { layer.space = space; layer.viewCount = 2; layer.views = pv; lay.push_back((XrCompositionLayerBaseHeader*)&layer); }   // the green frame: still a frame (passthrough everywhere)
            }
        }
        XrFrameEndInfo fe{ XR_TYPE_FRAME_END_INFO }; fe.displayTime = fs.predictedDisplayTime; fe.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE; fe.layerCount = (uint32_t)lay.size(); fe.layers = lay.data();
        CK(xrEndFrame(sess, &fe));
        frames++;
    }
    if (left) sendMouse(0, 0, MOUSEEVENTF_LEFTUP); if (right) sendMouse(0, 0, MOUSEEVENTF_RIGHTUP);
    L("ending after %llu frames", (unsigned long long)frames);
    if (rc == 0) L("PASS (rc=0)");
    if (running) xrRequestExitSession(sess);
    xrDestroySession(sess); xrDestroyInstance(inst);
    return rc;
}
