// Frame generation for the host (docs/FRAME_GENERATION.md): the picture for a compositor boundary the game did not render a frame for.
//
// The game is paced at half the display rate (ovrshim appPeriodMul). For every rendered frame B the host keeps a copy, computes the optical
// flow between the previous rendered frame A (rotated into B's camera first, so the flow is in-scene motion only, never head rotation) and B
// with the GPU's optical-flow engine (NVIDIA Optical Flow API, driver nvofapi64.dll, Turing and later), and renders either B itself or the
// midpoint between A and B, rotated to the head sample the frame is stamped with. The output texture has the frame's size and format and
// feeds the usual foveation pass + NVENC like a shim slot. Two syntheses, our own passes: neural (the default: RIFE 4.4's motion and blend
// fields, our CUDA network, composed in the output pass; neural.h) and edge (the fallback: the optical flow's vectors, ideas studied in
// VectorXR re-implemented: vector median, source-solved flow, per-pixel candidate choice at motion boundaries, doubtful motion shrinks toward
// the still frame). Since 1.5.0 the classic crossfade (OFXR-Bridge's recipe) and edge-blend are gone: edge beat both.
#pragma once
#include <d3d11.h>
#include <cstdint>

struct FrameGenConfig {
    float flowScale = 0.5f;   // flow input = the frame downscaled by this (0.25 .. 1)
    int perfLevel = 20;       // NV_OF_PERF_LEVEL: 5 slow (best), 10 medium, 20 fast
    int grid = 4;             // output vector grid (1, 2, 4)
    float maxFlowPx = 512.f;  // clamp on a flow vector, full-resolution pixels
    float copyDeg = 0.02f;    // a rendered frame whose stamp orientation is within this angle of its render orientation is copied, not resampled
    float motionLimit = 0.10f;   // edge: content shift beyond this share of the eye width falls back to the still frame (soft over the last quarter)
    float costScale = 17.f;      // edge: confidence = 1 - (luma cost - 0.02) * this
    float photoScale = 13.5f;    // edge: where a vector is doubted, the previous frame is blended in by 1 - (max |dRGB| - 0.03) * this
    // camera turn (on by default since 1.4.1; AIRFRAMEXR_FG_CAM_TURN=0 turns it off; edge synthesis only; docs/FRAME_GENERATION.md "Stick turns"): a game's stick turn is a
    // turn of the play space about the tracking up axis that the head pose does not carry, so it reached the optical flow whole and came out as
    // double images. With it on, each rendered frame carries a camera yaw on top of its head orientation, predicted from the previous pair's
    // measured turn and removed before the flow like the head's rotation; the output is turned by the yaw interpolated at t; the edge the
    // turned newer frame does not cover is filled from the older one.
    int camTurn = 1;
    // neural fields (the synthesis "neural", the streamer's default: nvh_fg_enable synth 4; neural.h; without the network's files or on a GPU
    // before the RTX 30 series the edge synthesis draws every output): RIFE 4.4 computes, per pair, the
    // motion from the output time to each frame and a blend mask, at neuralScale of the eye resolution, with our CUDA network (nvenc/rife); the output pass
    // composes the picture from them at full resolution (one sample of each original frame through the rotation chain), keeping still content
    // exact where both frames agree within neuralStill and the fields move less than neuralStillPx full-resolution pixels. The optical flow keeps
    // running for the camera turn's measurement. The fields belong to one output time: each output uses fields of its own time (x2: one run per
    // pair at 1/2, run early; x3: one per output, at 1/3 and 2/3, the fixed fractions where RIFE 4.4 places the content right). Judge (2026-11-22): +0.91 dB on motion boundaries at 0.5, stick walks +2.3 .. +3.9.
    int neural = 0;
    float neuralScale = 0.5f;
    float neuralStill = 0.02f, neuralStillPx = 1.f;
    wchar_t neuralDir[260] = L"";   // the folder with airframexr_rife.dll and rife44.afxw (empty: the host's `neural` folder)
};

struct FrameGenStats {
    uint64_t ingests = 0, pairs = 0, synths = 0, reals = 0, copies = 0, flowErrors = 0, renderErrors = 0;
    float lastFlowMs = 0, lastSynthMs = 0, lastIngestMs = 0;   // GPU time of the last flow pass (prep + engine), the last output pass, the last ingest copy
    double flowMsSum = 0, synthMsSum = 0; uint64_t flowN = 0, synthN = 0;
    int flowW = 0, flowH = 0, blocksW = 0, blocksH = 0;
    int maskPts[2] = {};               // the mask polygon each eye's output pass draws (0: the whole eye)
    float maskVisible[2] = { 1, 1 };   // the share of the eye that polygon covers (what the output pass still computes)
    // camera turn (cfg.camTurn): the last measured turn of a pair, the prediction in force, the share of textured blocks that agreed, blocks used,
    // CPU time of the last measurement, measurements taken / skipped as not ready or not confident
    float camTurnDeg = 0, camTurnPredDeg = 0, camTurnAgree = 0, camTurnCpuMs = 0;
    int camTurnBlocks = 0;
    uint64_t camTurnMeasured = 0, camTurnNotReady = 0, camTurnDoubtful = 0, camTurnLate = 0, camTurnCorrections = 0, camTurnWaits = 0;
    double camTurnWaitMsSum = 0; float camTurnWaitMsMax = 0;   // the first outputs that waited for their pair's measurement (a turn in progress)
    bool nvofReady = false;
    char nvofInfo[160] = "";   // "api 5.0, grids ..." or the failure reason
    // neural fields (cfg.neural): on and running, network runs (per pair, and again when an output's time differs), outputs composed from them,
    // failures, GPU ms of the last run and the average, CPU ms of the last Run call, the network's size, what is running or why not
    bool neuralOn = false;
    uint64_t neuralRuns = 0, neuralOutputs = 0, neuralFailures = 0;
    float neuralMs = 0, neuralMsAvg = 0, neuralCpuMs = 0;
    int neuralW = 0, neuralH = 0;
    char neuralInfo[200] = "";
};

class FrameGen {
public:
    // rendered frames kept (newest first by `age`; the fixed rates draw from the newest pair, the camera turn reads the pair before it)
    static constexpr int kHist = 4;
    FrameGen(ID3D11Device* dev, ID3D11DeviceContext* ctx);
    ~FrameGen();
    // Creates the optical-flow session for frames of w x h (the stream's side-by-side frame). false + err: no flow engine (keep the feature off).
    bool Init(int w, int h, const FrameGenConfig& cfg, char* err, int errlen);
    // Takes a rendered frame into the history (previous <- current <- src) and computes the flow between the two. qRender8: the per-eye
    // orientation (x y z w) the frame was rendered with; fov8: per eye {l, r, u, d} tangents (the host's eye FoV). 1 = two frames exist, 0 = one, -1 error.
    // frameS: the frame's content time in seconds (the display time it was rendered for; 0 = unknown): the camera turn predicts a pair's turn as the
    // measured rate times the pair's own time step (under our pacing a game's frames are 2 or 3 display periods apart, and so is its turn)
    int Ingest(ID3D11Texture2D* src, const float* qRender8, const float* fov8, char* err, int errlen, double frameS = 0.0);
    bool computeFlow(int n, char* err, int errlen);
    // neural fields (cfg.neural) of pair (n-1, n) at time t; false: not available for this output (the edge synthesis draws it)
    bool neuralPass(int n, float t);
    // Renders the output for a boundary: mode 0 = the rendered frame `age` frames before the newest (0 = the newest) rotated to qOut4, 1 = the
    // frame at fraction t (0..1) between that frame's predecessor and it (midpoint = 0.5), rotated to qOut4. Returns the output texture (owned
    // by this object), nullptr on error.
    ID3D11Texture2D* Render(int mode, float t, int age, const float* qOut4, const float* fov8, char* err, int errlen);
    // The mask of the profile in force (docs/PLAN_1_4_FOUNDATION.md F1, polygon_uv_v1: n points x, y in the eye's uv, u = 0 at the outer edge, v = 0 at the
    // top, convex): the output pass then computes only the pixels inside it plus a margin (2 px + `band` x the eye width: the band past the outline where the
    // encoder's QP climbs to the highest, nvh_build_qp_map), the rest of the eye is never generated. nullptr / n < 3: the whole eye.
    void SetMask(int eye, const float* uv, int n, float band = 0.f);
    // Lab (the frame generation judge): what this GPU's optical-flow engine offers for external hints, as text ("hints in optical-flow mode: yes/no,
    // hint grids 1 2 4 8, output grids 1 2 4, perf levels ..."); empty before Init. Nothing in the streamer calls it.
    void DescribeCaps(char* out, int outlen);
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
    // camera turn: reads pair n's measurement if the GPU has finished it (false = not ready / none), updates the prediction; `correct`: a turn
    // that contradicts the prediction moves this pair's (and newer frames') yaw and redoes the pair's flow
    bool harvestTurn(int n, bool correct, bool wait);
    ID3D11Device* m_dev = nullptr;
    ID3D11DeviceContext* m_ctx = nullptr;
    int m_w = 0, m_h = 0, m_have = 0;
    ID3D11Texture2D* m_out = nullptr;
    bool ensureFrames(ID3D11Texture2D* like, char* err, int errlen);
};
