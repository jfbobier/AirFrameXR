// Frame generation for the host (docs/FRAME_GENERATION.md): the picture for a compositor boundary the game did not render a frame for.
//
// The game is paced at half the display rate (ovrshim appPeriodMul). For every rendered frame B the host keeps a copy, computes the optical
// flow between the previous rendered frame A (rotated into B's camera first, so the flow is in-scene motion only, never head rotation) and B
// with the GPU's optical-flow engine (NVIDIA Optical Flow API, driver nvofapi64.dll, Turing and later), and renders either B itself or the
// midpoint between A and B, rotated to the head sample the frame is stamped with. The output texture has the frame's size and format and
// feeds the usual foveation pass + NVENC like a shim slot. Two syntheses, our own D3D11 passes (no third-party code; the flow comes from the
// NVIDIA Optical Flow SDK directly): the edge-aware one (default) re-implements ideas studied in VectorXR (vector median, source-solved flow,
// per-pixel candidate choice at motion boundaries, doubtful motion shrinks toward the still frame); the crossfade one the recipe studied in
// OFXR-Bridge (rotation-compensated residual flow, confidence from colour disagreement and the engine's cost, crossfade fallback).
#pragma once
#include <d3d11.h>
#include <cstdint>

struct FrameGenConfig {
    float flowScale = 0.5f;   // flow input = the frame downscaled by this (0.25 .. 1)
    int perfLevel = 20;       // NV_OF_PERF_LEVEL: 5 slow (best), 10 medium, 20 fast
    int grid = 4;             // output vector grid (1, 2, 4)
    float maxFlowPx = 512.f;  // clamp on a flow vector, full-resolution pixels
    float confGain = 6.f;     // confidence = saturate(1 - gain * max |A - B|) at the matched points
    float copyDeg = 0.02f;    // a rendered frame whose stamp orientation is within this angle of its render orientation is copied, not resampled
    // synthesis: 1 = edge-aware (vector median, source-solved flow, per-pixel candidates at motion boundaries, doubtful motion shrinks toward
    // the still frame: ideas from VectorXR, MPL-2.0, re-implemented); 0 = the crossfade recipe (OFXR-Bridge's ideas). Eyes with game vectors keep the latter.
    int synth = 1;
    float motionLimit = 0.10f;   // edge: content shift beyond this share of the eye width falls back to the still frame (soft over the last quarter)
    float costScale = 17.f;      // edge: confidence = 1 - (luma cost - 0.02) * this
    int mvCandidate = 0;         // edge: the game's vector is one more candidate (the flow keeps running in DLSS games); 0 = vectors take the crossfade path
    float mvBias = 0.02f;        // edge: colour error added to the game vector's (it must beat the flow's candidates by this)
    float mvNoisePx = 0.f;       // lab only (VISIONALVR_FG_MV_NOISE): random offset per vector texel and frame, full-res px
    float photoScale = 13.5f;    // edge: where a vector is doubted, the previous frame is blended in by 1 - (max |dRGB| - 0.03) * this (0 = always: "edge-blend")
};

// The game's DLSS motion vectors of one eye, as published with a rendered frame (ipc.h MotionEye, docs/MOTION_VECTORS.md). NGX conventions:
// the vectors cover renderW x renderH pixels from (mvX, mvY) of the texture; a vector is the displacement from the previous frame in the
// vectors' own pixels times mvScale (where the pixel came from); the colour was rendered with the jitter offset (render pixels).
struct FrameGenVectors {
    uint32_t api = 0, flags = 0;                            // 11 / 12; NVSDK_NGX_DLSS_Feature_Flags (2 MVLowRes, 4 MVJittered, 8 DepthInverted)
    uint64_t frame = 0;                                     // the game's evaluate counter for this eye's DLSS feature
    uint32_t renderW = 0, renderH = 0, outputW = 0, outputH = 0;
    uint32_t mvX = 0, mvY = 0, depthX = 0, depthY = 0;
    float jitterX = 0, jitterY = 0, mvScaleX = 1, mvScaleY = 1;
    int reset = 0;
};

struct FrameGenStats {
    uint64_t ingests = 0, pairs = 0, synths = 0, reals = 0, copies = 0, flowErrors = 0, renderErrors = 0;
    uint64_t flowSkipped = 0;                                        // synthetic outputs rendered without running the flow engine (both eyes had game vectors)
    uint64_t mvFrames = 0, mvEyes = 0, mvDepth = 0, mvErrors = 0;   // game motion vectors: frames with vectors for at least one eye, eye ingests, with depth, failures
    uint64_t mvUsedEyes = 0;
    uint64_t mvcSampled = 0, mvcBoundary = 0, mvcAvail = 0, mvcWins = 0;   // game vector candidate, 1 output pixel in 64: sampled, at a boundary, a vector there, it won                                         // eye passes synthesised from the game's vectors (the flow fills what they do not cover)
    int mvW = 0, mvH = 0; uint32_t mvFormat = 0, mvFlags = 0, mvApi = 0; float mvScale[2] = {1.f, 1.f};   // the last vectors seen
    float lastFlowMs = 0, lastSynthMs = 0, lastIngestMs = 0;   // GPU time of the last flow pass (prep + engine), the last output pass, the last ingest copy
    double flowMsSum = 0, synthMsSum = 0; uint64_t flowN = 0, synthN = 0;
    int flowW = 0, flowH = 0, blocksW = 0, blocksH = 0;
    bool nvofReady = false;
    char nvofInfo[160] = "";   // "api 5.0, grids ..." or the failure reason
};

class FrameGen {
public:
    // rendered frames kept (newest first by `age`): the uncapped mode shows content up to a few game frames behind the newest one
    static constexpr int kHist = 4;
    FrameGen(ID3D11Device* dev, ID3D11DeviceContext* ctx);
    ~FrameGen();
    // Creates the optical-flow session for frames of w x h (the stream's side-by-side frame). false + err: no flow engine (keep the feature off).
    bool Init(int w, int h, const FrameGenConfig& cfg, char* err, int errlen);
    // Takes a rendered frame into the history (previous <- current <- src) and computes the flow between the two. qRender8: the per-eye
    // orientation (x y z w) the frame was rendered with; fov8: per eye {l, r, u, d} tangents (the host's eye FoV). 1 = two frames exist, 0 = one, -1 error.
    int Ingest(ID3D11Texture2D* src, const float* qRender8, const float* fov8, char* err, int errlen);
    bool computeFlow(int n, char* err, int errlen);
    // Attaches the game's DLSS vectors (and depth, may be null) of one eye to the frame just ingested: copies them into this object's own
    // textures (the shim reuses its slot companions). VISIONALVR_FG_MV_DUMP=<dir> writes the first ingests as PPM images for a look.
    int IngestVectors(int eye, ID3D11Texture2D* mv, ID3D11Texture2D* depth, const FrameGenVectors& v, char* err, int errlen);
    // Synthesise from the game's vectors when a frame carries them (default on; VISIONALVR_FG_USE_MV=0 at start, the GUI tick live).
    void SetUseVectors(bool on);
    // Renders the output for a boundary: mode 0 = the rendered frame `age` frames before the newest (0 = the newest) rotated to qOut4, 1 = the
    // frame at fraction t (0..1) between that frame's predecessor and it (midpoint = 0.5), rotated to qOut4. Returns the output texture (owned
    // by this object), nullptr on error.
    ID3D11Texture2D* Render(int mode, float t, int age, const float* qOut4, const float* fov8, char* err, int errlen);
    // Reads back the GPU timings of the last passes when they are available (call after the encode of the frame).
    void CollectTimings();
    void ResetHistory();            // the app is gone (or changed): forget the frames
    bool HasFrame() const { return m_have >= 1; }
    bool HasPair() const { return m_have >= 2; }
    int History() const { return m_have; }
    ID3D11Texture2D* Output() const { return m_out; }
    int Width() const { return m_w; }
    int Height() const { return m_h; }
    FrameGenStats stats;

private:
    struct Impl;
    Impl* m_i = nullptr;
    ID3D11Device* m_dev = nullptr;
    ID3D11DeviceContext* m_ctx = nullptr;
    int m_w = 0, m_h = 0, m_have = 0;
    ID3D11Texture2D* m_out = nullptr;
    bool ensureFrames(ID3D11Texture2D* like, char* err, int errlen);
};
