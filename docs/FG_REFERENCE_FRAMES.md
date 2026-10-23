# Frame generation and the encoder's reference frames (parked, 2026-10-22)

Question: with frame generation on, would image quality improve if NVENC's keyframes / references were the real (rendered) frames
instead of the generated ones? Built and measured on a throwaway branch `0.6` (deleted 2026-10-23; the code is in the appendix).
Verdict: it works as designed, the effect is too small to see, and it was not the cause of the "sometimes perfect, sometimes bad"
frame generation in Gears of War (that cause is below).

## Why not literal keyframes

The stream is HEVC, CBR, infinite GOP, P-frames only, VBV about 1.1 frames; IDRs only at (re)start and on the client's IDR request.
An I/IDR frame needs 5-10x the bits of a P frame for the same quality: inside a 1.1-frame VBV it would be starved (worse quality on
every real frame), and a larger VBV means bursts on Wi-Fi. What can be aligned with real frames is the *reference chain*.

## What was built (setting `video.frame_gen_nonref`, HEVC, default off)

- The encoder takes picture types from the host (`enablePTD = 0`, a `tools/host/build.rs` patch of ALVR's VideoEncoderNVENC):
  IDR on a (re)start, P otherwise, `hevcPicParams.refPicFlag = 0` for generated frames and repeats (one-shot `nvh_set_nonref`).
- A pending recovery IDR waits up to 25 ms for a real frame instead of landing on a generated one (`idr_deferred`).
- Telemetry: NVENC's `frameAvgQP` and the flag per frame in the debug `host_frames.csv`.

NVENC facts learned (RTX 5090, driver 617.42, SDK 12.2 header):
- With `enablePTD = 0`, HEVC also needs `displayPOCSyntax` from the host (counted from 0 at each IDR); without it the first P fails in
  `nvEncEncodePicture`. The header only lists this for H.264.
- `pictureType = NV_ENC_PIC_TYPE_NONREF_P` as *input* is rejected (`NV_ENC_ERR_INVALID_PARAM` at the bitstream lock). A P with
  `refPicFlag = 0` works: the NAL type stays `TRAIL_R` and the picture is left out of the next frame's reference picture set.
  Verify with ffmpeg `-bsf:v trace_headers` (unwrap the 8-bit POC lsb, resolve SPS-indexed RPS, apply `num_ref_idx_l0_active`).
- `NV_ENC_PIC_FLAG_DISABLE_ENC_STATE_ADVANCE` is not a way to do it (the next frame would reuse the POC).
- With split-frame encoding the driver averages the strips' `frameAvgQP` as unsigned numbers: negative 10-bit QPs read ~1.43e9.
  Decode: signed value of `qp * strips mod 2^32`, divided by the strips.
- Split-frame encoding (3 strips) is unaffected by `enablePTD = 0`; encode time unchanged.

## Measurements

Lab (5090, mock client, xr_probe at 1/2): structure verified (1200 non-reference pictures = 1198 generated + 2 repeats, 0 frames
predicting from them, real frames referencing the previous real frame); live NVENC e2e and the generation-off path with the setting
on unchanged. The lab content sits at the QP floor (about -5), so it cannot show quality; bytes at that QP: generated +31 / +33 %,
real +12 / +21 % in 2 of 3 pairs (a generated frame can no longer predict from the previous generated one; a real frame predicts
from two periods back).

Gears of War E-Day, Vision Pro, 300 Mbps constant, full-split, 1/2, one session each (ON 207 s, OFF 169 s), same section: **the user saw
no difference.** Gears is rate-limited even at 300 Mbps (frames ~400 KB against a ~417 KB budget, QP 22-30).

| | ON | OFF |
|---|---|---|
| real frames: QP / size | 24.5 / 392 KB | 25.8 / 317 KB |
| generated frames: QP / size | 25.9 / 435 KB | 25.4 / 415 KB |
| stream | 276 Mbps | 264 Mbps |
| headset decode p50 | 7.9 ms | 7.5 ms |
| recovery IDRs in play | 7, all on real frames (3 deferred) | 3, one on a generated frame |

-1.4 QP on half the frames, +0.4 on the other half: below what is visible at QP 25. If revisited: worth it only at a bitrate where
artifacts visibly persist (lower Mbps, Quest over Wi-Fi), measured with PSNR of the real frames, not by eye at 300 Mbps.

Ideas not built: QP +k on generated frames (helps real frames only with a VBV above 1.1 frames: latency); the optical-flow vectors as
NVENC external ME hints (they need remapping through the foveation).

## The actual cause of the "random" Gears behaviour (2026-10-22 logs)

The Vision Pro ran at **100 Hz** all day (the ALVR app's "Stream refresh rate" setting; the session's `preferred_fps 90` does not
override it), so 1/2 = **50 fps**, and Gears renders 47-50 fps GPU-bound and at the power cap (540-570 W, clock reason 0x4). The
10-second `frame generation perf` lines of a 93-minute session show two stable states with sudden, sticky switches:

| state | game fps | releases late | GPU | power | e.g. |
|---|---|---|---|---|---|
| good | 49.9-50.0 (holds the cap) | 57-65 % | 94-97 % | ~540 W | 20:40-20:52, 21:03-21:24 |
| bad | 47.4-47.8 (~2.5 repeats/s) | 94-97 % | 98-99 % | ~560 W | 20:53-21:01, 21:25-21:27 |

The same scenery costs more GPU in the bad state: hypothesis (unproven) = once the game is late, its rendering overlaps our
per-boundary GPU work (flow, synthesis, foveation, encode) and our realtime GPU scheduling class (5) pre-empts it mid-frame, which
keeps it late; on time, our work runs while it waits. Load spikes knock it from one state to the other. Phase lock slides the grid
~29 times a minute in both states (arrivals 1-3 ms before the tick, target 7): it is saturated by the GPU-bound game, not the cause.

Tests, one per session, judged from the 10-second lines (bad = ~47.5 fps with ~96 % late; good = 50.0): headset at 90 Hz in the ALVR
app (1/2 = 45 fps: 5-10 % headroom); `video.gpu_sched_class` 0 instead of 5 (if the bad state gets rarer or shorter, the
pre-emption hypothesis holds); 1/3 (33 fps cap: always held, at the price of more interpolation and one longer game period of latency).

## Appendix: the code (diff of branch 0.6 against main 8d425a8, code files only)

The lab scenarios were `harness/scenarios/pacing/exp/fg_nonref_{on,off}.json` (copies of `e2e_fg_latency` with host env
`VISIONALVR_FG_NONREF`), plus `e2e_live_nvenc` and a generation-off variant with the setting on.

```diff
diff --git a/nvenc/hostlib/nvh.cpp b/nvenc/hostlib/nvh.cpp
index 2f30275..af9c3e8 100644
--- a/nvenc/hostlib/nvh.cpp
+++ b/nvenc/hostlib/nvh.cpp
@@ -103,6 +103,12 @@ static uint32_t g_idleRgb = 0x00FF00;   // solid frame sent while no OpenXR app
 static int g_gpuThreadPriority = 0;     // -7..7, 0 = leave the default
 static int g_noiseBlock = 16;           // noise == 2: side of the random blocks (the floor: what is left at the coarsest quantizer)
 static int g_noiseAmp = 48;             // noise == 2: per-pixel noise on top, in 10-bit levels (the ceiling: kept at fine quantizers)
+// manual picture types (tools/host/build.rs): fg_nonref asks for them at encoder creation, nvh_set_nonref marks the next encode non-reference
+extern "C" { int g_nvh_manual_ptd = 0; int g_nvh_nonref = 0; }
+void nvh_set_nonref(int on) { g_nvh_nonref = on ? 1 : 0; }
+// average QP of the last encoded frame (NvEncoder.cpp sets it at the bitstream lock), -1 when no encode ran since the last call
+extern "C" { int g_nvh_last_qp = -1; }
+int nvh_take_qp(void) { const int q = g_nvh_last_qp; g_nvh_last_qp = -1; return q; }
 
 int nvh_set(const char* key, int64_t v) {
     auto& S = Settings::Instance();
@@ -141,6 +147,7 @@ int nvh_set(const char* key, int64_t v) {
         {"qp_map_center_delta", [&](int64_t x) { S.m_qpMapCenterDelta = (int)x; }},
         {"qp_map_edge_delta", [&](int64_t x) { S.m_qpMapEdgeDelta = (int)x; }},
         {"idle_rgb", [&](int64_t x) { g_idleRgb = (uint32_t)x & 0xFFFFFF; }},
+        {"fg_nonref", [&](int64_t x) { g_nvh_manual_ptd = x != 0; }},
         {"gpu_thread_priority", [&](int64_t x) { g_gpuThreadPriority = (int)std::max<int64_t>(-7, std::min<int64_t>(7, x)); }},
         {"noise_block", [&](int64_t x) { g_noiseBlock = (int)std::max<int64_t>(1, std::min<int64_t>(64, x)); }},
         {"noise_amp", [&](int64_t x) { g_noiseAmp = (int)std::max<int64_t>(0, std::min<int64_t>(512, x)); }},
diff --git a/nvenc/hostlib/nvh.h b/nvenc/hostlib/nvh.h
index 17d76cd..c01c611 100644
--- a/nvenc/hostlib/nvh.h
+++ b/nvenc/hostlib/nvh.h
@@ -10,11 +10,17 @@ typedef int (*nvh_params_cb)(void* user, uint64_t* bitrate_bps, float* framerate
 
 // Settings use the openvr_config key names of the ALVR session (e.g. "nvenc_quality_preset"). Call before nvh_create.
 // Host-only keys: "idle_rgb" (0xRRGGBB, the frame sent while no app runs), "gpu_thread_priority" (-7..7, 0 = default).
+// "fg_nonref" (0/1, HEVC): the encoder takes picture types from us instead of deciding them, so nvh_set_nonref can mark a frame.
 int nvh_set(const char* key, int64_t value);
 int nvh_set_f(const char* key, double value);   // foveation_center_size_x/y, foveation_center_shift_x/y, foveation_edge_ratio_x/y
 // Size of the encoded frame: ALVR's foveated layout (2 * optimized eye width x optimized eye height) when
 // enable_foveated_encoding is set, else 2*eye_w x eye_h. Needs eye_resolution_* and the foveation settings set first.
 void nvh_encoded_size(int* w, int* h);
+// The next encode (one shot, consumed by it) is a non-reference P picture: nothing is predicted from it. Needs fg_nonref at creation;
+// a forced IDR wins.
+void nvh_set_nonref(int on);
+// The encoder's average QP of the last encoded frame, then -1 until the next encode (telemetry).
+int nvh_take_qp(void);
 // width/height: encoded frame (SBS). noise: 1 = high-entropy scrolling content, 0 = smooth content.
 void* nvh_create(int width, int height, int noise, nvh_packet_cb pcb, nvh_params_cb qcb, void* user, char* err, int errlen);
 // Renders the test pattern (frame_index stamped as 16 blocks, bit i of the index, in the top-left 2048x128) and
diff --git a/tools/host/build.rs b/tools/host/build.rs
index 86bca32..5b91d38 100644
--- a/tools/host/build.rs
+++ b/tools/host/build.rs
@@ -110,11 +110,25 @@ void FFR::RenderTo(ID3D11Texture2D* target) {\n\
     let idr_anchor = "    m_NvNecoder->EncodeFrame(vPacket, &picParams);";
     assert!(enc_cpp.contains(idr_anchor), "EncodeFrame anchor not found");
     let enc_cpp = enc_cpp.replacen(idr_anchor, "    if (g_nvh_qp_map && Settings::Instance().m_qpMapEnabled) {\n        picParams.qpDeltaMap = g_nvh_qp_map;\n        picParams.qpDeltaMapSize = g_nvh_qp_map_size;\n    }\n    if (g_nvh_recon_ptr) {\n        picParams.outputReconBuffer = g_nvh_recon_ptr;\n        picParams.encodePicFlags |= NV_ENC_PIC_FLAG_OUTPUT_RECON_FRAME;\n    }\n    m_NvNecoder->EncodeFrame(vPacket, &picParams);", 1);
+    // manual picture types (host setting fg_nonref, HEVC only: H.264 would also need the POC and frame_num syntax from us). The encoder
+    // decides it once at creation (enablePTD cannot change in a reconfigure); then Transmit sets every picture's type: IDR on a (re)start,
+    // non-reference P for a generated frame (nvh_set_nonref, one shot), P otherwise. Real frames then predict from the last real frame only.
+    // A non-reference picture is a P with refPicFlag 0 (pictureType NV_ENC_PIC_TYPE_NONREF_P as input: NV_ENC_ERR_INVALID_PARAM, 5090 driver
+    // 617.42). The picture order count is ours too in this mode (HEVC displayPOCSyntax, 0 at each IDR): without it the first P is rejected.
+    let ptd_init = "    initializeParams.encodeConfig = &encodeConfig;\n\n    FillEncodeConfig(";
+    assert!(enc_cpp.contains(ptd_init), "Initialize FillEncodeConfig anchor not found");
+    let enc_cpp = enc_cpp.replacen(ptd_init, "    initializeParams.encodeConfig = &encodeConfig;\n    m_manualPtd = (g_nvh_manual_ptd && m_codec == ALVR_CODEC_HEVC) ? 1 : 0;\n\n    FillEncodeConfig(", 1);
+    let ptd_cfg = "    m_NvNecoder->CreateDefaultEncoderParams(\n        &initializeParams, encoderGUID, qualityPreset, tuningPreset\n    );\n";
+    assert!(enc_cpp.contains(ptd_cfg), "CreateDefaultEncoderParams anchor not found");
+    let enc_cpp = enc_cpp.replacen(ptd_cfg, &format!("{ptd_cfg}    initializeParams.enablePTD = m_manualPtd ? 0 : 1;\n"), 1);
+    let ptd_pic = "        picParams.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR;\n    }\n";
+    assert!(enc_cpp.contains(ptd_pic), "FORCEIDR anchor not found");
+    let enc_cpp = enc_cpp.replacen(ptd_pic, &format!("{ptd_pic}    if (m_manualPtd) {{\n        const bool nonref = g_nvh_nonref && !insertIDR;\n        picParams.encodePicFlags = insertIDR ? NV_ENC_PIC_FLAG_OUTPUT_SPSPPS : 0;\n        picParams.pictureType = insertIDR ? NV_ENC_PIC_TYPE_IDR : NV_ENC_PIC_TYPE_P;\n        picParams.codecPicParams.hevcPicParams.refPicFlag = nonref ? 0 : 1;\n        if (insertIDR) m_poc = 0;\n        picParams.codecPicParams.hevcPicParams.displayPOCSyntax = m_poc++;\n    }}\n    g_nvh_nonref = 0;\n"), 1);
     // benchmark (nvenc/hostlib/bench.cpp): NVENC's reconstructed frame, i.e. the picture a decoder gets, for the quality metric
     let enc_cpp = enc_cpp.replacen(anchor, &format!("    initializeParams.enableReconFrameOutput = g_nvh_recon_enable ? 1 : 0;\n{anchor}"), 1);
     let inc_anchor = "#include \"VideoEncoderNVENC.h\"";
     assert!(enc_cpp.contains(inc_anchor), "include anchor not found");
-    let enc_cpp = enc_cpp.replacen(inc_anchor, &format!("{inc_anchor}\n#include <cstdio>\n#include <string>\nextern \"C\" {{ extern int8_t* g_nvh_qp_map; extern uint32_t g_nvh_qp_map_size; extern void* g_nvh_recon_ptr; extern int g_nvh_recon_enable; extern void* g_nvh_prepared_input; }}"), 1);
+    let enc_cpp = enc_cpp.replacen(inc_anchor, &format!("{inc_anchor}\n#include <cstdio>\n#include <string>\nextern \"C\" {{ extern int8_t* g_nvh_qp_map; extern uint32_t g_nvh_qp_map_size; extern void* g_nvh_recon_ptr; extern int g_nvh_recon_enable; extern void* g_nvh_prepared_input; extern int g_nvh_manual_ptd; extern int g_nvh_nonref; }}"), 1);
     let mut enc_cpp = enc_cpp;
     enc_cpp.push_str(DESCRIBE_CPP);
     enc_cpp.push_str("\nint VideoEncoderNVENC::GetEncoderEngineCount() {\n    return m_NvNecoder ? m_NvNecoder->GetCapabilityValue(NV_ENC_CODEC_HEVC_GUID, NV_ENC_CAPS_NUM_ENCODER_ENGINES) : 0;\n}\n");
@@ -124,7 +138,7 @@ void FFR::RenderTo(ID3D11Texture2D* target) {\n\
     assert!(enc_h.contains("    void Shutdown();"));
     fs::write(
         out.join("VideoEncoderNVENC.h"),
-        enc_h.replace("    void Shutdown();", "    void Shutdown();\n    int GetEncoderEngineCount();\n    std::string DescribeConfig();\n    NvEncoder* GetNvEncoder() { return m_NvNecoder.get(); }"),
+        enc_h.replace("    void Shutdown();", "    void Shutdown();\n    int GetEncoderEngineCount();\n    std::string DescribeConfig();\n    NvEncoder* GetNvEncoder() { return m_NvNecoder.get(); }\n    int m_manualPtd = 0;\n    uint32_t m_poc = 0;"),
     )
     .unwrap();
     // NvEncoder.h: public accessors for the session handle and API table (the benchmark registers the reconstructed-frame surface
@@ -164,6 +178,11 @@ void FFR::RenderTo(ID3D11Texture2D* target) {\n\
         init_call,
         "{ NVENCSTATUS st_ = m_nvenc.nvEncInitializeEncoder(m_hEncoder, &m_initializeParams); if (st_ != NV_ENC_SUCCESS) { const char* le_ = m_nvenc.nvEncGetLastErrorString(m_hEncoder); std::ostringstream o_; o_ << \"nvEncInitializeEncoder returned \" << st_ << \": \" << (le_ ? le_ : \"(no driver message)\"); throw NVENCException::makeNVENCException(o_.str(), st_, __FUNCTION__, __FILE__, __LINE__); } }",
     );
+    // telemetry (host_frames.csv qp): the encoder's own average QP of the frame just locked, for the host to read after Transmit
+    let lock_copy = "vPacket[i].insert(vPacket[i].end(), &pData[0], &pData[lockBitstreamData.bitstreamSizeInBytes]);";
+    assert!(nv.contains(lock_copy), "bitstream copy anchor not found in NvEncoder.cpp");
+    let nv = nv.replace(lock_copy, &format!("{lock_copy}\n        g_nvh_last_qp = (int)lockBitstreamData.frameAvgQP;"));
+    let nv = format!("extern \"C\" int g_nvh_last_qp;\n{nv}");
     let nv_path = out.join("NvEncoder_122.cpp");
     fs::write(&nv_path, nv).unwrap();
 
@@ -265,13 +284,13 @@ std::string VideoEncoderNVENC::DescribeConfig() {
     else strips = -1;   // auto / auto-forced: chosen by the driver
     char b[3600];
     snprintf(b, sizeof b,
-        "{\"hevc\":%d,\"size\":[%u,%u],\"fps\":%.1f,\"preset_tuning\":%d,\"rc_mode\":%d,\"avg_bps\":%u,\"max_bps\":%u,\"vbv\":%u,\"aq_spatial\":%d,\"aq_temporal\":%d,\"multi_pass\":%d,"
+        "{\"hevc\":%d,\"size\":[%u,%u],\"fps\":%.1f,\"preset_tuning\":%d,\"rc_mode\":%d,\"avg_bps\":%u,\"max_bps\":%u,\"vbv\":%u,\"aq_spatial\":%d,\"aq_temporal\":%d,\"multi_pass\":%d,\"ptd\":%d,"
         "\"qp_map_mode\":%d,\"gop\":%u,\"frame_interval_p\":%d,\"idr_period\":%u,\"bit_depth_out\":%d,\"chroma_idc\":%u,\"cu_min\":%d,\"cu_max\":%d,"
         "\"split\":{\"requested_mode\":%u,\"engines\":%d,\"conditions_met\":%d,\"weighted_pred\":%d,\"alpha\":%d,\"subframe_write\":%d,\"vidmem_output\":%d,\"timing_sei\":%d,\"expected_strips\":%d},"
         "\"caps\":{\"width_max\":%d,\"height_max\":%d,\"width_min\":%d,\"height_min\":%d,\"10bit\":%d,\"dyn_bitrate_change\":%d,\"dyn_res_change\":%d,\"lookahead\":%d,\"temporal_aq\":%d,\"weighted_pred\":%d,\"mb_per_sec_max\":%d,\"multi_ref\":%d,\"recon_output\":%d,\"output_stats\":%d}}",
         isHevc ? 1 : 0, ip.encodeWidth, ip.encodeHeight, ip.frameRateDen ? (double)ip.frameRateNum / ip.frameRateDen : 0.0, (int)ip.tuningInfo,
         (int)cfg.rcParams.rateControlMode, cfg.rcParams.averageBitRate, cfg.rcParams.maxBitRate, cfg.rcParams.vbvBufferSize,
-        (int)cfg.rcParams.enableAQ, (int)cfg.rcParams.enableTemporalAQ, (int)cfg.rcParams.multiPass,
+        (int)cfg.rcParams.enableAQ, (int)cfg.rcParams.enableTemporalAQ, (int)cfg.rcParams.multiPass, (int)ip.enablePTD,
         (int)cfg.rcParams.qpMapMode, cfg.gopLength, (int)cfg.frameIntervalP, hv.idrPeriod, (int)hv.outputBitDepth, hv.chromaFormatIDC, (int)hv.minCUSize, (int)hv.maxCUSize,
         mode, engines, conditions ? 1 : 0, (int)ip.enableWeightedPrediction, (int)hv.enableAlphaLayerEncoding, (int)ip.enableSubFrameWrite, (int)ip.enableOutputInVidmem,
         (int)hv.outputPictureTimingSEI, strips,
diff --git a/tools/host/src/main.rs b/tools/host/src/main.rs
index 66928e7..fa14635 100644
--- a/tools/host/src/main.rs
+++ b/tools/host/src/main.rs
@@ -482,6 +482,11 @@ fn apply_settings(a: &mut Args, st: &vision::Settings) {
             std::env::set_var("VISIONALVR_INTAKE_GATE", if v { "1" } else { "0" });
         }
     }
+    if let Some(v) = st.frame_gen_nonref {
+        if std::env::var_os("VISIONALVR_FG_NONREF").is_none() {
+            std::env::set_var("VISIONALVR_FG_NONREF", if v { "1" } else { "0" });
+        }
+    }
     if let Some(v) = st.phase_margin_ms {
         if std::env::var_os("VISIONALVR_PHASE_MARGIN_MS").is_none() {
             std::env::set_var("VISIONALVR_PHASE_MARGIN_MS", v.to_string());
@@ -909,6 +914,8 @@ mod nvh {
         pub fn nvh_keepalive(h: *mut c_void, on: i32, duty_pct: i32) -> i32;
         pub fn nvh_set_f(key: *const c_char, value: f64) -> i32;
         pub fn nvh_encoded_size(w: *mut i32, h: *mut i32);
+        pub fn nvh_set_nonref(on: i32);
+        pub fn nvh_take_qp() -> i32;
         pub fn nvh_encoder_engines(h: *mut c_void) -> i32;
         pub fn nvh_describe(h: *mut c_void, buf: *mut std::ffi::c_char, len: i32) -> i32;
         pub fn nvh_qpmap_dump(path: *const std::ffi::c_char) -> i32;
@@ -1763,6 +1770,7 @@ fn main() {
         (c"gpu_thread_priority", args.gpu_priority.unwrap_or(0)),
         (c"noise_block", args.noise_block),
         (c"noise_amp", args.noise_amp),
+        (c"fg_nonref", pipeline::fg_nonref_enabled() as i64),
     ];
     let audio_cfg = game_audio_config(&layout.session());
     event(origin, "game_audio", audio_cfg.clone());
diff --git a/tools/host/src/pipeline.rs b/tools/host/src/pipeline.rs
index 940c5e9..224d51f 100644
--- a/tools/host/src/pipeline.rs
+++ b/tools/host/src/pipeline.rs
@@ -281,6 +281,11 @@ struct Outgoing {
     /// CSV columns up to the send time (frame,ts_ns,slot,t_boundary,t_arrival,t_encode_start,encode_ms)
     row: String,
     bytes: usize,
+    /// telemetry: the encoder's average QP of the frame (-1 unknown) and whether it was encoded non-reference (fg_nonref)
+    #[cfg_attr(not(feature = "telemetry"), allow(dead_code))]
+    qp: i32,
+    #[cfg_attr(not(feature = "telemetry"), allow(dead_code))]
+    nonref: bool,
     #[cfg(feature = "simrig")]
     sim_ring: i32,
 }
@@ -357,6 +362,10 @@ pub struct Pipeline {
     pub send_late: AtomicU64,
     pub encode_errors: AtomicU64,
     pub idr_restarts: AtomicU64,
+    /// fg_nonref: generated frames and repeats are encoded non-reference, and a recovery IDR waits (up to IDR_WAIT_MAX) for a real frame
+    fg_nonref: bool,
+    /// boundaries that passed a pending recovery IDR over to wait for a real frame
+    pub idr_deferred: AtomicU64,
     pub frames_before_first_tracking: AtomicU64,
     encode_env_ms: Mutex<f64>,
     drained: Mutex<Drained>,
@@ -414,6 +423,14 @@ pub struct Pipeline {
     pub fg_stale_stamps: AtomicU64,
 }
 
+/// VISIONALVR_FG_NONREF (settings video.frame_gen_nonref, default off): see `Pipeline::fg_nonref`
+pub fn fg_nonref_enabled() -> bool {
+    std::env::var("VISIONALVR_FG_NONREF").map(|v| v == "1").unwrap_or(false)
+}
+
+/// fg_nonref: longest wait of a recovery IDR for a real frame (two boundaries at 1/3 and 100 Hz), after which it goes on whatever frame comes
+const IDR_WAIT_MAX: Duration = Duration::from_millis(25);
+
 /// VISIONALVR_INTAKE_GATE (default on): see `Pipeline::intake_gate`
 pub fn intake_gate_enabled() -> bool {
     std::env::var("VISIONALVR_INTAKE_GATE").map(|v| v != "0").unwrap_or(true)
@@ -474,6 +491,8 @@ impl Pipeline {
             send_late: AtomicU64::new(0),
             encode_errors: AtomicU64::new(0),
             idr_restarts: AtomicU64::new(0),
+            fg_nonref: fg_nonref_enabled(),
+            idr_deferred: AtomicU64::new(0),
             frames_before_first_tracking: AtomicU64::new(0),
             encode_env_ms: Mutex::new(4.0),
             drained: Mutex::new(Drained::default()),
@@ -1168,6 +1187,7 @@ impl Pipeline {
         let origin = self.cfg.origin;
         let mut consecutive_errors = 0usize;
         let mut fg_encodes = 0u64;
+        let mut idr_wait: Option<Instant> = None; // fg_nonref: when the pending recovery IDR first met a generated frame
         loop {
             let p = {
                 let mut g = self.picked.lock();
@@ -1229,13 +1249,23 @@ impl Pipeline {
                 }
             };
             let is_green = matches!(plan, Plan::Green);
+            // a generated frame or a repeat: nothing is predicted from it (the next real frame references the last real one)
+            let nonref = self.fg_nonref && matches!(plan, Plan::Fg { kind: FgKind::Synth | FgKind::Repeat, .. });
             {
                 let mut last_idr = self.last_idr_restart.lock();
                 if self.shared.restart_from_idr.load(Ordering::SeqCst) && last_idr.elapsed() >= Duration::from_millis(100) {
-                    self.shared.restart_from_idr.store(false, Ordering::SeqCst);
-                    *last_idr = Instant::now();
-                    self.idr_restarts.fetch_add(1, Ordering::Relaxed);
-                    self.frame_idx.store(0, Ordering::Relaxed);
+                    // the recovery IDR goes on a real frame when one comes soon: an IDR is a reference, a generated one would carry its
+                    // artifacts into every frame after it
+                    let since = *idr_wait.get_or_insert_with(Instant::now);
+                    if nonref && since.elapsed() < IDR_WAIT_MAX {
+                        self.idr_deferred.fetch_add(1, Ordering::Relaxed);
+                    } else {
+                        idr_wait = None;
+                        self.shared.restart_from_idr.store(false, Ordering::SeqCst);
+                        *last_idr = Instant::now();
+                        self.idr_restarts.fetch_add(1, Ordering::Relaxed);
+                        self.frame_idx.store(0, Ordering::Relaxed);
+                    }
                 }
             }
             let force_idr = self.frame_idx.fetch_add(1, Ordering::Relaxed) == 0;
@@ -1273,6 +1303,7 @@ impl Pipeline {
             }
             let t0 = Instant::now();
             let mut err = [0 as std::ffi::c_char; 256];
+            unsafe { nvh::nvh_set_nonref(nonref as i32) }; // one shot, consumed by the encode below (and cleared after it in case none ran)
             let ms = match &plan {
                 #[cfg(feature = "simrig")]
                 Plan::Slot(a) if crate::simrig::active() => crate::simrig::encode(handle, ipc, a.slot, counter as u32, ts.as_nanos() as u64, force_idr, err.as_mut_ptr(), 256),
@@ -1286,6 +1317,8 @@ impl Pipeline {
                 },
                 Plan::Green | Plan::Skip => unsafe { nvh::nvh_encode_green(handle, 0, ts.as_nanos() as u64, force_idr as i32, err.as_mut_ptr(), 256) },
             };
+            unsafe { nvh::nvh_set_nonref(0) };
+            let qp = unsafe { nvh::nvh_take_qp() };
             if let Some(s) = slot {
                 // the foveation pass consumed the slot (Transmit returns after the packet is out): the shim may reuse it
                 unsafe { nvh::nvh_ipc_slot_busy(ipc, s, 0) };
@@ -1388,7 +1421,7 @@ impl Pipeline {
             let mut q = self.outgoing.lock();
             for (k, (pts, frame, is_idr, config)) in pending.into_iter().enumerate() {
                 q.push_back(Outgoing { release_at, boundary: p.boundary, ts: pts, frame, is_idr, config, encoded_at,
-                    row: if k + 1 == n { row.clone() } else { String::new() }, bytes,
+                    row: if k + 1 == n { row.clone() } else { String::new() }, bytes, qp, nonref,
                     #[cfg(feature = "simrig")] sim_ring });
             }
             drop(q);
@@ -1438,9 +1471,9 @@ impl Pipeline {
                     o.bytes, o.is_idr as u8));
                 #[cfg(feature = "telemetry")]
                 {
-                    logs::csv("host_frames.csv", "frame,ts_ns,slot,t_boundary,t_arrival,t_encode_start,encode_ms,t_send,hold_ms,bytes,idr",
-                        &format!("{},{:.6},{:.3},{},{}", o.row, logs::qpc_at(now), o.release_at.saturating_duration_since(o.encoded_at).as_secs_f64() * 1000.0,
-                            o.bytes, o.is_idr as u8));
+                    logs::csv("host_frames.csv", "frame,ts_ns,slot,t_boundary,t_arrival,t_encode_start,encode_ms,t_send,hold_ms,bytes,idr,qp,nonref",
+                        &format!("{},{:.6},{:.3},{},{},{},{}", o.row, logs::qpc_at(now), o.release_at.saturating_duration_since(o.encoded_at).as_secs_f64() * 1000.0,
+                            o.bytes, o.is_idr as u8, o.qp, o.nonref as u8));
                 }
             }
         }
@@ -1478,6 +1511,8 @@ impl Pipeline {
             "green_frames": self.green_frames.load(Ordering::Relaxed),
             "encode_errors": self.encode_errors.load(Ordering::Relaxed),
             "idr_restarts": self.idr_restarts.load(Ordering::Relaxed),
+            "idr_deferred": self.idr_deferred.load(Ordering::Relaxed),
+            "fg_nonref": self.fg_nonref,
             "encode_envelope_ms": *self.encode_env_ms.lock(),
             "gpu_scheduling": self.gpu_sched.lock().map(|(c, st)| json!({ "class": c, "status": st })),
             "frame_gen": {
diff --git a/tools/host/src/vision.rs b/tools/host/src/vision.rs
index d623487..515624d 100644
--- a/tools/host/src/vision.rs
+++ b/tools/host/src/vision.rs
@@ -72,6 +72,9 @@ pub struct Settings {
     pub frame_gen_lag_ms: Option<f64>,
     /// uncapped mode: minimum spacing of the game's releases as an fps cap (0 = none): headroom for the host's own GPU work
     pub frame_gen_cap_fps: Option<f64>,
+    /// generated frames (and repeats) encoded as non-reference P pictures, the recovery IDR moved to a real frame (HEVC); default off
+    /// while unmeasured (the environment variable VISIONALVR_FG_NONREF wins)
+    pub frame_gen_nonref: Option<bool>,
     /// D3DKMT process scheduling class to ask for (5 realtime .. 2; 0 = leave)
     pub gpu_sched_class: Option<i64>,
     pub debug: Option<bool>,
@@ -127,6 +130,7 @@ pub fn load_settings(path: &Path) -> Settings {
         frame_gen_rate: video["frame_gen_rate"].as_str().map(str::to_string),
         frame_gen_lag_ms: video["frame_gen_lag_ms"].as_f64(),
         frame_gen_cap_fps: video["frame_gen_cap_fps"].as_f64(),
+        frame_gen_nonref: video["frame_gen_nonref"].as_bool(),
         gpu_sched_class: video["gpu_sched_class"].as_i64(),
         debug: v["debug"].as_bool(),
         gamma: v["display"]["gamma"].as_f64().map(|g| g as f32),
```
