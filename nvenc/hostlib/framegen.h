// Frame generation for the host (docs/FRAME_GENERATION.md): the picture for a compositor boundary the game did not render a frame for.
//
// The game is paced at half the display rate (ovrshim appPeriodMul). For every rendered frame B the host keeps a copy, computes the optical
// flow between the previous rendered frame A (rotated into B's camera first, so the flow is in-scene motion only, never head rotation) and B
// with the GPU's optical-flow engine (NVIDIA Optical Flow API, driver nvofapi64.dll, Turing and later), and renders either B itself or the
// midpoint between A and B, rotated to the head sample the frame is stamped with. The output texture has the frame's size and format and
// feeds the usual foveation pass + NVENC like a shim slot. The synthesis follows OFXR-Bridge's shader (rotation-compensated residual
// flow, confidence from colour disagreement and the engine's cost, crossfade fallback), ported to one D3D11 pass per eye.
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
};

struct FrameGenStats {
    uint64_t ingests = 0, pairs = 0, synths = 0, reals = 0, copies = 0, flowErrors = 0, renderErrors = 0;
    float lastFlowMs = 0, lastSynthMs = 0, lastIngestMs = 0;   // GPU time of the last flow pass (prep + engine), the last output pass, the last ingest copy
    double flowMsSum = 0, synthMsSum = 0; uint64_t flowN = 0, synthN = 0;
    int flowW = 0, flowH = 0, blocksW = 0, blocksH = 0;
    bool nvofReady = false;
    char nvofInfo[160] = "";   // "api 5.0, grids ..." or the failure reason
};

class FrameGen {
public:
    FrameGen(ID3D11Device* dev, ID3D11DeviceContext* ctx);
    ~FrameGen();
    // Creates the optical-flow session for frames of w x h (the stream's side-by-side frame). false + err: no flow engine (keep the feature off).
    bool Init(int w, int h, const FrameGenConfig& cfg, char* err, int errlen);
    // Takes a rendered frame into the history (previous <- current <- src) and computes the flow between the two. qRender8: the per-eye
    // orientation (x y z w) the frame was rendered with; fov8: per eye {l, r, u, d} tangents (the host's eye FoV). 1 = two frames exist, 0 = one, -1 error.
    int Ingest(ID3D11Texture2D* src, const float* qRender8, const float* fov8, char* err, int errlen);
    // Renders the output for a boundary: mode 0 = the newest rendered frame rotated to qOut4, 1 = the frame at fraction t (0..1) between the
    // previous and the newest rendered frame (midpoint = 0.5), rotated to qOut4. Returns the output texture (owned by this object), nullptr on error.
    ID3D11Texture2D* Render(int mode, float t, const float* qOut4, const float* fov8, char* err, int errlen);
    // Reads back the GPU timings of the last passes when they are available (call after the encode of the frame).
    void CollectTimings();
    void ResetHistory();            // the app is gone (or changed): forget the frames
    bool HasFrame() const { return m_have >= 1; }
    bool HasPair() const { return m_have >= 2; }
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
