// Frame generation for the host (docs/FRAME_GENERATION.md): the picture for a compositor boundary the game did not render a frame for.
//
// The game is paced at half the display rate (ovrshim appPeriodMul). For every rendered frame B the host keeps a copy, computes the optical
// flow between the previous rendered frame A (rotated into B's camera first, so the flow is in-scene motion only, never head rotation) and B
// with the GPU's optical-flow engine (NVIDIA Optical Flow API, driver nvofapi64.dll, Turing and later), and renders either B itself or the
// midpoint between A and B, rotated to the head sample the frame is stamped with. The output texture has the frame's size and format and
// feeds the usual foveation pass + NVENC like a shim slot. Two syntheses, our own D3D11 passes (no third-party code; the flow comes from the
// NVIDIA Optical Flow SDK directly): the edge-aware one (default) re-implements ideas studied in VectorXR (vector median, source-solved flow,
// per-pixel candidate choice at motion boundaries, doubtful motion shrinks toward the still frame); the classic one (crossfade) the recipe
// studied in OFXR-Bridge (rotation-compensated residual flow, confidence from colour disagreement and the engine's cost, crossfade fallback),
// kept as the predictable fallback.
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
    // the still frame: ideas from VectorXR, MPL-2.0, re-implemented); 0 = classic, the crossfade recipe (OFXR-Bridge's ideas).
    int synth = 1;
    float motionLimit = 0.10f;   // edge: content shift beyond this share of the eye width falls back to the still frame (soft over the last quarter)
    float costScale = 17.f;      // edge: confidence = 1 - (luma cost - 0.02) * this
    float photoScale = 13.5f;    // edge: where a vector is doubted, the previous frame is blended in by 1 - (max |dRGB| - 0.03) * this (0 = always: "edge-blend")
    // camera turn (on by default since 1.4.1; AIRFRAMEXR_FG_CAM_TURN=0 turns it off; edge synthesis only; docs/FRAME_GENERATION.md "Stick turns"): a game's stick turn is a
    // turn of the play space about the tracking up axis that the head pose does not carry, so it reached the optical flow whole and came out as
    // double images. With it on, each rendered frame carries a camera yaw on top of its head orientation, predicted from the previous pair's
    // measured turn and removed before the flow like the head's rotation; the output is turned by the yaw interpolated at t; the edge the
    // turned newer frame does not cover is filled from the older one.
    int camTurn = 1;
    // head-locked HUD (fg-testing branch; AIRFRAMEXR_FG_HUD=1; off by default): what stays where it is on the screen between the pair's two rendered frames
    // (compared unaligned: no head rotation) and has texture is a head-locked overlay, and the output pass shows the newer frame's own pixel there instead of
    // rotating it with the world (the rotation to the stamp moved a head-locked HUD by the head's turn). Smooth content is harmless to misjudge: it looks
    // the same shifted. The mask is grown by the head's shift in pixels (+ hudDil quarter-resolution texels) so that a flat panel is covered too.
    int hud = 0;
    float hudThr = 0.02f;    // a pixel is still when max |A - B| (encoded colour) is below this
    float hudTex = 0.04f;    // ... and textured when the newer frame's luma step to its right and lower neighbour sums above this; a 4 x 4 texel is HUD when 3 in 4 of its textured pixels are still
    float hudVote = 0.75f;   // the share of a texel's textured pixels that must be still
    int hudDil = 0;
    int fb = 0;              // forward/backward consistency (see FrameGenFb note above)
    float fbTol = 2.f, fbGain = 0.25f;
    // stereo flow fusion (fg-testing branch; AIRFRAMEXR_FG_STEREO=1; off by default; edge synthesis only): the two eyes see the same motion, shifted by the disparity. The engine
    // runs once more on the newer frame against its own eyes swapped (left against right, right against left) to find where each block's content is in the other eye; the other
    // eye's temporal vector at that place is a candidate for this block, taken when its match cost in THIS eye beats the block's own by stereoMargin (a wrong or noisy vector
    // replaced by what the other eye found). Costs a third flow run per pair.
    int noHints = 0;         // lab (AIRFRAMEXR_FG_NOHINTS=1): the engine's temporal hints (the previous execute's flow as a starting point) off in every run. The extra runs of fb / stereo sit between
                             // the pair's backward runs and would hint them with another direction's flow: compare those variants against a no-hints reference
    int stereo = 0;
    float stereoMargin = 0.004f, stereoMaxCost = 0.05f;          // extra growth of the mask, in texels of the quarter-resolution mask (4 px)
    // the game's depth (fg-testing branch; lab: AIRFRAMEXR_FG_DEPTH=1 or the judge's edge_depth generator; off by default; edge synthesis only): frames
    // ingested with their depth (what a game's XR_KHR_composition_layer_depth carries, VDXR's ovrLayerType_EyeFovDepth) let the output pass tell the
    // surfaces apart where the colours cannot. Each candidate vector is classified by the distance its two ends see: the same surface in both frames
    // (scored by the colour test, as without depth), shown by the newer frame only (its older end is hidden behind something nearer: newly uncovered),
    // or by the older frame only (its newer end is hidden: about to be covered). The one-sided kinds cost depthOcc instead of a colour error and are
    // drawn from their one frame (no blend with the other frame's occluder: no halo); among both-sided candidates within depthMargin of the best colour
    // error, the nearest surface wins (it is in front at t). Runs where the block vectors disagree (as the colour test does) and where the solved
    // vector's two ends are not one surface. Judge with the scene's own depth (2026-11-22, docs/FRAME_GENERATION_JUDGE.md): this rule -0.6 dB on the
    // boundary band, mode 1 +0.02 .. +0.04 dB: not worth forwarding a game's depth for; kept as a lab switch.
    int depth = 0;
    float depthTol = 0.06f;      // two distances are one surface within this share of the nearer
    float depthOcc = 0.08f;      // the colour error a one-sided (occlusion) candidate is charged
    float depthMargin = 0.03f;   // near-tie of colour errors within which the nearer surface wins
    // 0: the rule above; 1: the edge synthesis exactly, depth only breaking near-ties at its motion boundaries (the nearer one-surface candidate wins);
    // 2: 1 + the ghost gate (the older frame is not blended in where its end of the vector is a nearer surface than the newer frame's)
    int depthMode = 0;
};

// forward/backward flow consistency (fg-testing branch; AIRFRAMEXR_FG_FB=1; off by default; edge synthesis only): the engine also runs the other way (aligned older
// frame -> newer), and a block's vector whose round trip does not come back (|forward(a) + backward| beyond fbTol px, a = where the backward vector points in the older frame)
// is an occlusion or a wrong match: its confidence falls (the vector shrinks toward the still newer frame) by fbGain per px beyond the tolerance. The recipe of NVIDIA's FRUC
// and FSR 3, re-implemented. Costs a second flow run per pair.
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
    uint64_t depthFrames = 0, depthOutputs = 0;   // cfg.depth: frames ingested with depth, synthetic outputs whose pair had depth on both frames
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
    // frameS: the frame's content time in seconds (the display time it was rendered for; 0 = unknown): the camera turn predicts a pair's turn as the
    // measured rate times the pair's own time step (under our pacing a game's frames are 2 or 3 display periods apart, and so is its turn)
    // depthNdc (cfg.depth; nullptr = none): the frame's depth, side by side like src (any size: sampled by uv), R32_FLOAT or R32_TYPELESS, as the game
    // wrote it; proj2 = { P22, P23 } of its projection (LibOVR ovrTimewarpProjectionDesc, which VDXR fills from the layer's nearZ / farZ): the view
    // distance along -z = P23 / (d + P22), standard, reversed and infinite-far depth alike
    int Ingest(ID3D11Texture2D* src, const float* qRender8, const float* fov8, char* err, int errlen, double frameS = 0.0,
               ID3D11Texture2D* depthNdc = nullptr, const float* proj2 = nullptr);
    bool computeFlow(int n, char* err, int errlen);
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
    // head-locked HUD (cfg.hud): the pair's still-and-textured mask (once per pair), grown by `radius` texels into the mask the output pass reads
    void hudPasses(int n, int prev, int radius);
    ID3D11Device* m_dev = nullptr;
    ID3D11DeviceContext* m_ctx = nullptr;
    int m_w = 0, m_h = 0, m_have = 0;
    ID3D11Texture2D* m_out = nullptr;
    bool ensureFrames(ID3D11Texture2D* like, char* err, int errlen);
};
