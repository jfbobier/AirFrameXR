// IPC contract between OVRShim (LibOVRRT64_1.dll, loaded by VDXR inside the OpenXR app) and alvr_host.
// Both sides open the same named objects (create-or-open); the host fills the "host -> shim" part, the shim the
// "shim -> host" part. Everything is plain old data so both C++ sides can include this header.
//
// Objects (names are prefixed with the session id from env OVRSHIM_SESSION, default "VisionALVR"):
//   <p>_State   file mapping of IpcState
//   <p>_Vsync   auto-reset event, signaled by the host once per display period
//   <p>_Frame   auto-reset event, signaled by the shim after it published a composed frame
// The composed side-by-side frame lives in a small ring of legacy shared D3D11 textures created by the shim;
// their HANDLE values are published in IpcState::slotHandle.
#pragma once
#include <stdint.h>

namespace visionalvr_ipc {

constexpr uint32_t kMagic = 0x56414C31; // 'VAL1'
constexpr uint32_t kVersion = 31;
constexpr int kSlots = 6;   // SBS ring: the host holds up to 2 (newest + encoding), 2 may be in flight on the GPU

struct Pose {
    float orientation[4]; // x y z w
    float position[3];
};

// Per-hand controller input in the Quest/Touch layout ALVR emulates (after ALVR's own button mapping).
enum : uint32_t {
    kClickPrimary = 1,     // A (right) / X (left)
    kClickSecondary = 2,   // B (right) / Y (left)
    kClickThumbstick = 4,
    kClickMenu = 8,        // left menu
    kClickSystem = 16,     // right system (Apple intercepts the real Home/Meta buttons)
};
enum : uint32_t {
    kTouchPrimary = 1,
    kTouchSecondary = 2,
    kTouchThumbstick = 4,
    kTouchTrigger = 8,
    kTouchThumbrest = 16,
};
struct HandInput {
    uint32_t clicks;
    uint32_t touches;
    float trigger, squeeze, stickX, stickY;
};

// One head tracking sample (what headClientTsNs.. hold for the latest one).
struct HeadSample {
    uint64_t clientTsNs;
    double hostTimeS;
    Pose head;
    float linVel[3], angVel[3];
};
constexpr int kHeadHist = 3;

struct HostToShim {
    uint32_t hostAlive;        // incremented by the host every vsync (shim detects a dead host)
    uint32_t clientConnected;  // 1 while a headset/mock client is streaming
    uint32_t encodeBits;       // 8 or 10: format of the SBS target
    uint32_t configSeq;        // incremented by the host after every config write (FoV/eye offsets can change while an app runs)
    double displayRateHz;
    int32_t eyeWidth, eyeHeight;   // recommended per-eye render size (the SBS frame is 2*eyeWidth x eyeHeight)
    float eyeFovTan[2][4];         // left, right, up, down (positive tangents) per eye
    float eyeOffset[2][3];         // eye position relative to the head
    uint32_t adapterLuidLow;       // LUID of the GPU the host encodes on (the app must render on it too)
    int32_t adapterLuidHigh;

    // Latest head pose, tagged with the client tracking timestamp it belongs to.
    uint64_t headSeq;              // seqlock: odd while the host is writing
    uint64_t headClientTsNs;       // client tracking timestamp (ALVR "target timestamp")
    double headHostTimeS;          // shim clock (QPC seconds) when the host received the sample
    Pose head;
    float headLinVel[3], headAngVel[3];
    uint32_t handValid[2];
    Pose hand[2];
    float handLinVel[2][3], handAngVel[2][3];
    double nextVsyncTimeS;         // predicted time of the next vsync (shim clock)
    HandInput handInput[2];        // left, right
    double vsyncIntervalS;         // time from the latest tick to the next one, as scheduled by ALVR's pacing clock
    float encodingGamma;           // negotiated ALVR encoding gamma: the client decodes with pow(v, gamma), so frames are sent as pow(v, 1/gamma)
    float userGamma;               // the user's display gamma (VisionALVR.exe slider): > 1 brighter; 0 or 1 = none. Read every frame
    float colorBrightness, colorContrast, colorSaturation, colorSharpening; // user colour correction (0 = neutral), on the final output
    uint32_t debugOn;              // 1: write the verbose shim log into debugDir
    wchar_t debugDir[260];         // the host's debug session folder (logs/debug/<time>), empty when debug is off
    uint32_t slotBusyMask;         // bit i: the host holds ring slot i (newest published frame, or being read by its encoder): the shim never renders into it
    float boundaryOffsetMs;        // the host's compositor boundary = display tick + this offset (ms)
    float runningStartMs;          // > 0: WaitToBeginFrame releases the app appFrameMs + this margin before the next boundary (running start); 0: at the tick
    // Hand pose convention fix: ALVR's controller poses follow its SteamVR driver's (OpenVR Touch) convention; LibOVR's Touch poses
    // differ by a fixed local transform (VDXR's grip/aim offsets were calibrated against the Oculus runtime). Applied per hand as
    // pose' = (q * offsetQ, p + q * offsetQ * offsetP); the right hand mirrors the rotation (x, -y, -z) like ALVR's own offsets.
    float handOffsetQuat[2][4];    // x y z w, per side (left, right); all zero = identity
    float handOffsetPos[2][3];
    // The samples before the latest, newest first (same seqlock as the head): the shim consumes tracking samples in order,
    // one per frame, whatever the phase between the app's release and the sample's arrival (see GetHmdPose).
    HeadSample headHist[kHeadHist];
    float headWaitMs;              // how long a frame waits for its head sample before reusing/predicting (default 3)
    float predictMissingHead;      // > 0: a late head sample is predicted (timestamp and pose) instead of reusing the last one
    // The user's standing eye height above the floor (m), reported to games as OVR_KEY_EYE_HEIGHT. VDXR treats this LibOVR as an eye-level
    // origin runtime: LOCAL space = the head pose as published (y about 0 at the anchor), STAGE / LOCAL_FLOOR = that + this eye height. 0 = default (1.675).
    float eyeHeightM;
    // Intake gate (the host's frame queue, pipeline.rs): intakeWaiting = frames the host holds that compositor boundaries have passed over
    // for several boundaries in a row (a standing queue: the app runs a frame ahead of the stream, every frame a period late; a single
    // left-behind frame is jitter and stays 0). With intakeGate set the shim skips one release of the app while it is > 0, so the queue
    // drains when that gap reaches the host, without dropping a rendered frame.
    uint32_t intakeWaiting;
    uint32_t intakeGate;
    // Frame generation (host pipeline.rs / nvenc/hostlib/framegen.cpp): the app is paced at 1 / appPeriodMul of the display rate (2 = every
    // other compositor boundary; the host interpolates the frame in between from the optical flow of the last two rendered frames) and its
    // predicted display time moves one display period later (an interpolated frame is shown before each rendered one). 0 or 1 = every boundary.
    uint32_t appPeriodMul;
    // Game motion vectors (docs/MOTION_VECTORS.md, ovrshim/ngxtap.h): 1 = when the VisionALVR-OptiScaler provider is loaded in the game, copy
    // the motion vectors and depth of every DLSS evaluate and publish them with the frame (ShimToHost::motion). 0 = leave the tap idle.
    uint32_t gameMotionVectors;
    // Uncapped frame generation (docs/FRAME_GENERATION_UNCAPPED.md): the app is paced at every boundary (appPeriodMul 1) and may be held to a
    // minimum spacing between releases (an fps cap chosen by the user: headroom for the host's own GPU work). 0 = no cap.
    uint32_t appMinPeriodUs;
};

// The DLSS inputs of one eye for the frame in a ring slot: the shim's copies of the motion vectors and depth the game handed DLSS,
// as legacy shared D3D11 textures (one pair per slot and eye, reused while their size holds; the host opens them like the slots).
// Sizes follow NGX: the vectors cover renderW x renderH pixels from (mvX, mvY) of a mvWidth x mvHeight texture; one vector = the
// pixel's displacement from the previous frame in the vectors' own pixels times mvScale (NGX convention: where the pixel came from).
struct MotionEye {
    uint32_t valid;                 // 1: this eye's vectors exist for the frame in the slot
    uint32_t api;                   // 11 or 12: the API the game evaluated DLSS in
    uint32_t stream;                // DLSS feature id in the game (one per eye in VR titles)
    uint32_t flags;                 // NVSDK_NGX_DLSS_Feature_Flags: 1 IsHDR, 2 MVLowRes, 4 MVJittered, 8 DepthInverted, 16 DoSharpening, 32 AutoExposure
    uint64_t frame;                 // the stream's evaluate counter
    uint64_t mvHandle, depthHandle; // shared texture handles (0 = none)
    uint32_t mvFormat, depthFormat; // DXGI_FORMAT of the copies (typed: vectors as the game has them, depth as R32_FLOAT / R16_UNORM, the depth plane of packed depth-stencil)
    uint32_t mvWidth, mvHeight, depthWidth, depthHeight;
    uint32_t renderW, renderH, outputW, outputH;   // DLSS input (render) and output (display) sizes
    uint32_t mvX, mvY, depthX, depthY;             // subrect bases
    float jitterX, jitterY, mvScaleX, mvScaleY;    // NGX jitter offset (render pixels) and MV scale
    int32_t reset;                  // the game asked for a history reset (camera cut)
    uint32_t pad;
};
struct MotionSlot {
    uint32_t streams;               // DLSS streams active in the game (2 = one per eye, 1 = a single evaluate: eye[0] only)
    uint32_t pad;
    MotionEye eye[2];               // left, right (streams ordered by first appearance)
};

struct Haptic {
    uint32_t seq;                  // incremented by the shim on every ovr_SetControllerVibration call
    float frequency, amplitude;
};

struct ShimToHost {
    uint32_t shimAlive;            // set by the shim while an OVR session exists
    uint32_t shimHeartbeat;        // incremented by the shim every display tick (stops if the app dies)
    Haptic haptic[2];              // left, right
    uint64_t frameCounter;         // frames published by the shim
    uint32_t frameSlot;            // ring slot of the latest frame
    uint32_t frameSeq;             // seqlock for the frame fields: odd while the shim is writing them
    uint64_t frameClientTsNs;      // client tracking timestamp of the pose the app rendered that frame with
    double frameSubmitTimeS;       // shim clock when the frame was submitted
    double frameDisplayTimeS;      // the display time the app rendered the frame for (its projection layer's SensorSampleTime, shim clock); 0 = none
    double frameReleaseTimeS;      // shim clock when WaitToBeginFrame released the app for this frame: the frame's start, what a wall-clock engine animates from
    float frameRenderQuat[2][4];   // orientation (x y z w, tracking space) each eye's projection layer was rendered with (frame generation rotates from it)
    uint32_t frameHasProjection;   // 1 when the frame has a projection layer (frameRenderQuat valid)
    uint64_t slotHandle[kSlots];   // legacy shared texture handles (HANDLE values)
    int32_t sbsWidth, sbsHeight;
    uint32_t sbsFormat;            // DXGI_FORMAT of the SBS slots
    float lastComposeMs;           // compose start -> GPU done for the last published frame (ms)
    uint32_t submitMode;           // 0: SubmitFrame waits for the GPU on the app thread, 1: a shim thread waits and publishes
    uint32_t tsMatched;            // frames whose timestamp was found by matching the layer's RenderPose to a pose the app read
    uint32_t tsFallback;           // frames stamped with the last pose the app read (no projection layer, or no pose matched)
    wchar_t appExe[128];           // file name of the app's executable (for the logs)
    uint32_t slotBusyWaits;        // SubmitFrame had to wait for a free ring slot (host too slow, or a GPU hang)
    float appFrameMs;              // envelope of release (WaitToBeginFrame return) -> frame GPU-complete: the running-start lead
    uint64_t releases;             // WaitToBeginFrame releases
    uint64_t releasesLate;         // releases whose computed time was already past (app slower than a display period: free-running)
    uint64_t gateHolds;            // releases the intake gate skipped for a standing host queue (see HostToShim::intakeGate)
    uint64_t gateSuppressed;       // standing queue seen while the gate was quiet after a skip (its effect still in flight)
    // Game motion vectors (HostToShim::gameMotionVectors): tap state and the per-slot publications (written with the frame fields).
    uint32_t motionTap;            // 0 no provider in the game, 1 attached, 2 provider found with another ABI version, 3 attach failed
    uint32_t motionStreams;        // DLSS streams seen in the last two seconds
    uint64_t motionPublications;   // DLSS evaluates the tap has seen
    MotionSlot motion[kSlots];
};

struct IpcState {
    uint32_t magic;
    uint32_t version;
    HostToShim host;
    ShimToHost shim;
};

} // namespace visionalvr_ipc
