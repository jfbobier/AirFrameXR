# FoV pipeline optimisation: the analysed plan (branch `fov-restriction-aware-engine`)

Status 2026-10-29: **analysed and measured on the 3080 Ti, nothing on a headset yet.** This replaces the note of 2026-10-28 ("the idea of
shrinking the stream to the calibrated rectangle"), which is kept in its last form in the git history (`9a9050b`). Follows
`docs/FOV_CALIBRATION.md` (the calibrated rectangle R = 0.96 / 0.71 / 0.69 / 0.61, `docs/AVP_FOV.md`). Numbers below come from
`harness/fov_stream_geometry.py` (geometry, exact port of ALVR's foveation maths), `harness/run.py bench-quality --eye` (encode),
`harness/fg_judge.py --eye` (frame generation) and the new scenario `e2e_fov_stream_r` (the whole chain at the shrunk size), all run on
2026-10-29 on the RTX 3080 Ti laptop (one NVENC engine, P0, video clock 1545-1567 MHz), results in `harness/results/20261029T10*`.

## 0. The answer in six lines

1. The frame path is already "FoV-aware" where it can be: the game renders only R, the shim composes R into the frame with one pass, the
   host's foveation pass renders straight into NVENC's input (zero copy). **No frame copy on the path is worth removing** (the only ones left are
   frame generation's history copy, about 0.1 ms on the 5090, and the compose pass itself, which also applies the encoding gamma and colour).
2. What is wasted is **geometry**: the stream frame covers the declared box D while only R has content. On the AirFrameXR client D is
   0.94 / 0.93 / 0.90 / 0.76 and R is 70 % of its area (not 49 %: the note of 10-28 used the 1.1.0 rectangle); on the stock app D is
   1.79 / 1.02 / 1.22 / 1.02 and R is 34 %.
3. Only the client can close that gap on the v20 protocol (the client maps the frame over the box *it* declares). So the plan is: **the client
   declares R, the stream frame is sized to R at the same pixels per degree, and ALVR's foveation is re-anchored** so the full-density band keeps
   its place in degrees. The shim, the IPC and the host's frame path need no change for it; the host's override then restricts nothing.
4. Measured gain at equal quality (same density, same bits per pixel): every unwarped stage (compose, frame generation, VRAM) drops by 30 %.
   **On the author's own profile (full frame, no foveation, 3 split strips on the 5090) the encoded frame drops by the same 30 %**: 7104 x 3200
   -> 6336 x 2496, encode 9 ms -> about 6.5 and headset decode 9 -> about 6.5 (proportions measured on the 3080 Ti: 20.2 -> 15.4 ms at P1),
   about 5 ms off the critical path. With the foveated profile the encoded frame drops by 13 % only (keeping the full-density band in degrees),
   because ALVR's foveation already compresses most of the never-seen margin 4-5x; keeping the session's foveation *fractions* instead gives 30 %
   but narrows the band from -12..+32 to -18..+24 degrees horizontally, a quality change to judge by eye.
5. On the 3080 Ti the foveated encode goes 11.3 -> 8.4 ms at P3 (-25 %) for the fractions variant, about 10.0 ms (-11 %) for the iso variant;
   frame generation's flow and synthesis drop 29-31 % (measured). Latency, not fps, is what the shrink buys: the game's GPU saving is already
   banked by the restriction itself. With the client under our control more is available (section 2c), and the client's own queues, not the
   PC, hold the largest latency reserve.
6. **NVENC split encoding**: the SDK guide (13.0, section 8.14) says the *automatic* mode splits only frames **2112 pixels high or more (HEVC;
   2048 for AV1)**, with P1-P2 high-quality or P1-P4 low-latency presets; the forced modes split "for all configurations". Our foveated frame is
   1664 high today and 1312-1536 after the shrink, so it only ever split because the profile forces 3 strips; the full frame (3200, 2496 after
   the shrink) is above the threshold either way. Shrinking does not break split encoding as long as the mode stays forced; it would silently
   stop splitting the foveated frame in auto mode (it already does today).

## 1. The frame path with a restricted field of view, as it is (read in the code; sizes for the Vision Pro profile)

| Stage | Where | Frame it works on | Passes / copies | Pays for the black margin D minus R? |
|---|---|---|---|---|
| Game render | the game, size from `GetRecommendedResolution` (`ovrshim/render_size.h`: stream eye size x R's span per axis, render quality 0 = that floor) | R at the stream's density | the game's own | no (this is the saving the override delivers) |
| Compose | shim `ComposeLayers` -> `ReprojectPS` with `EyeHomography(m_eyeFov, layerFov)`: one full-frame draw per eye into the ring slot (R10G10B10A2), clear to black first; the encoding gamma, colour correction and sharpening are applied in this pass. Two passes (linear FP16 intermediate + encode pass) only with several layers | D, 7104 x 3200 | 1 draw (2 with quads) | yes: the pass covers D, R is 70 % of it |
| Ring | 6 shared slots of D size, ownership by `slotBusyMask` | D | none (shared handles) | VRAM: 6 x 91 MB |
| Frame generation (when on) | `FrameGen::Ingest` **copies** the slot into its history (`CopyResource`, 91 MB), flow preparation at 50 %, NVOF, synthesis pass per eye | D | 1 copy + 3 passes | yes, all of them |
| Foveation pass | ALVR's `CompressAxisAligned` (`FFR.cpp`), **zero copy into NVENC's next input frame** (`encodeWithFfr`, `AIRFRAMEXR_ZEROCOPY`) | reads D, writes F = 4224 x 1664 | 1 draw, measured **0.11 ms** on the 3080 Ti | reads D, but the margin lands mostly in the 4-5x compressed periphery of F |
| NVENC | HEVC 10-bit, P3 low latency, QP map, split mode 3 (forced) | F | encode time is per pixel of F | only through F's periphery |
| Network, decode, client | ALVR, the headset's decoder, the client's quad over the declared box | F | | the client samples F over D; outside R it shows black |

Facts that bound the design: VDXR latches the FoV and the recommended size at session creation (a change is for the next game start); the stream
size and the foveation parameters are sent to the client in ALVR's `StreamConfig` at connection, from `session.json` (`openvr_config` written by
`server_core`), and cannot change without a reconnection; the client's `ViewsConfig` (what it declares) arrives after that, and on the stock app only
after its first decoded frame. So "the host sizes the stream from what the client declares" is impossible in-session: **the PC must know the
rectangle before the connection**, which it does (the profile and the Advanced window hold it).

## 2. Geometry (computed, `harness/fov_stream_geometry.py`)

Today's stream (3552 x 3200 over D) is 1899 x 1928 px per tangent unit. R at that density is **3168 x 2496 per eye** (ALVR floors sizes to 32):
15.8 Mpx side by side instead of 22.7 (-30 %). With the stock app's box the same density gives 2112 x 1888.

Where ALVR's foveation puts its full-density band today, in degrees (left eye; the shader mirrors the right eye): the band in source space is
`[(1 - cs)(1 + sh) / 2, that + cs]` of the eye's width from the **outer** edge (derived from `CompressAxisAlignedPixelShader.hlsl`, checked by the
tool's self-test), so with centre size 0.45 x 0.4 and shift 0.4 / 0.1:

| Frame | Per eye | Encoded F | Full-density band, horizontal (outer .. inner) | vertical (down .. up) | Frame px | Encoded px |
|---|---|---|---|---|---|---|
| today, over D (AirFrameXR client) | 3552 x 3200 | 4224 x 1664, 7.03 Mpx | -12.3 .. +31.8 deg | -17.4 .. +19.3 deg | | |
| today, over D (stock app) | 3552 x 3200 | 4224 x 1664 | -35 .. +29 deg | -23 .. +26 deg | | |
| **R frame, same foveation fractions** | 3168 x 2496 | 3776 x 1312, 4.95 Mpx | -17.6 .. +23.5 deg | -14.6 .. +14.5 deg | **-30 %** | **-30 %** |
| **R frame, band re-anchored to today's degrees** (centre size 0.50 x 0.51, shift 0.78 / 0.07) | 3168 x 2496 | 3968 x 1536, 6.09 Mpx | -12.1 .. +31.9 deg | -17.4 .. +19.1 deg | **-30 %** | **-13 %** |
| **R frame, no foveation** (the author's full-frame profile) | 3168 x 2496 | 6336 x 2496, 15.8 Mpx | the whole frame | | **-30 %** | **-30 %** |

Two things this table says that the 10-28 note did not:

- **The encode saving at equal quality is small.** ALVR's foveation already compresses the never-seen margin 4x horizontally and 5x vertically;
  removing it from the frame removes mostly pixels that were already cheap. The unwarped stages (compose, frame generation, the ring's VRAM) get
  the full 29 %.
- **The band is 11 degrees off the optical axis on the AirFrameXR client today.** ALVR's default shift (0.4 toward the nose) was made for the
  Quest's asymmetric frustum; on the client's near-symmetric box it puts the band's middle at +10 degrees nasal, so the outer 12-44 degrees of the
  visible window are at 4x compression while 24-32 degrees nasal are at full density. The R box is itself asymmetric (0.96 outer / 0.71 inner), so
  the *same fractions* on the R frame land the band 3 degrees nasal: closer to the axis, narrower, cheaper. Which of the two rows is "iso quality"
  is a judgement for the headset (section 6, A/B 2); the plan carries both as presets.

The corners of R (about 21 % of it) are inside the frame and outside the visible ellipse; only the QP map could make them cheaper and no setting
is proposed without a measurement.

## 2b. Everything is a parameter: the pipeline model the UI must carry

The author's baseline is one point (the calibrated rectangle, 70 % of the declared box, no foveation, render quality 0, 3552 x 3200); the plan
must hold for the whole space the user can set, and the UI must tell the user what the pipeline will do with a given setting **before** the
game runs. The inputs and the stage sizes they decide:

| Input | Where it is set | What it decides |
|---|---|---|
| The box the headset declares (D, per eye) | the client (stock app: its frustum, times its FoV Scale; AirFrameXR client: the visible limit) | the stream frame's angular cover; saved in `config\airframexr_views.json` at each connection |
| The rectangle the game renders (R) | the client when it declares R, else the streamer's override (tangents per edge, from the headset's own down to a fifth; the calibration saw 50 % of the area as a plausible tight choice) | the game's view; with the override, the black margin in the frame |
| The stream resolution | Settings panel (3552 x 3200 today) | the pixels per tangent unit over D; **proposal: keep the field as the size over the headset's full box (the panel reference the user knows) and derive the stream frame over whatever box is declared at that density**, so the number keeps its meaning across clients and restrictions |
| The render quality slider | Advanced window, 0..1 | the game's size between the floor (the stream's density over R) and the ceiling; today the ceiling is the stream's eye size, so **when the client declares R the floor equals the ceiling and the slider disappears**; the user wants it to reach the panel's size (3552 x 3200 over R, "upsample the restricted view to the panel"): the ceiling must become the panel reference, a small shim change (`render_size.h` ceiling from a new IPC field, step S9) |
| Foveation on / off and its geometry | Settings (encode profile), session | the encoded frame F and the full-density band in degrees (section 2) |
| Split mode, preset, tuning, engines | profile / Advanced, the GPU | the strips NVENC will use (section 4): forced modes always split, auto only at 2112 rows or more with P1-P4 low latency |

`harness/fov_stream_geometry.py` is that model as an oracle: `--restrict 50 --no-foveation --split-mode 0 --engines 3`, `--quality 1 --panel
3552x3200`, `--eye`, `--preset`, `--tuning`; it prints every stage size and a `warnings` list, and its self-test covers the band formula and the
strip rules (2112 rows, the preset table, forced modes, the engine cap). Examples it gives: a stock-client user at 50 % of the declared area with
the full frame in auto mode gets 4992 x 2240 encoded and still 3 strips (2240 rows); the same user with foveation on gets a 1312-1536-row frame
and **1 strip in auto mode**, which is the warning the UI must show (today this case is silent: the profile's forced 3 hides it, and a user who
picks auto, or whose GPU has one engine, sees nothing). The launcher's C# port of this model (`PipelineModel`, step S3b) is checked against the
script over a grid (`pipeline_model_check.py`, section 8).

What the user should see, in two places:

- **Before the run**, in the Advanced window next to the FoV and quality fields: the stage sizes the settings imply (game render, stream frame,
  encoded frame, strips) and the warnings.
- **After the run**, in the Game statistics app (`performance.exe`, the per-game record): a **frame pipeline strip** for the last session, one box
  per stage with what really happened: game render size (shim log line "field of view told to the game"), the declared box and the rectangle
  (`views_config`, `fov_restrict`), the stream frame, frame generation (on / off, flow size, flow and synthesis ms), the foveated frame and its
  band, NVENC (preset, strips expected and `slices_per_frame` seen, encode p50 / p95, bitrate), the network (Mbps), the headset (decode ms,
  repeated periods) and, next to each box, the predicted value from the settings so a mismatch shows (strips expected 3, seen 1). The host
  already publishes every number in `encoder_stats`, `nvenc_config` and the client statistics; what is missing is the record: `games.rs` writes
  one entry per run with fps and repeats, it must gain a `pipeline` block with these facts (host change, step S8). The GUI then draws it; no
  engine logic decides anything.

## 2c. With the client under our control: what more end-to-end gives, ranked by gain over risk

1. **The client declares R** (fork branch `fov-calibration`, built next) and the stream is sized to it: -30 % on every stage for the full-frame
   profile, about 5 ms of encode + decode on the 5090 at 90-100 Hz. The largest FoV-related gain; settings and client only.
2. **Per-eye rectangles**: the calibration found the two eyes differ (right eye 44 degrees outward, 28 toward the nose; left 37 / 35) and a box
   per eye needs 13 % fewer pixels than the mirrored one. The shim and the host are per-eye already (`m_eyeFov[2]`, `eyeFovTan[2][4]`); the
   client declares per eye today; only the streamer's override is mirrored. Comes free with the client declaring, or with the declare channel.
3. **A visible-shape warp** instead of ALVR's rectangular two-level foveation, on both ends (host `FFR` shader, client un-warp shader, negotiated
   over the `Reserved` channel): compress only what lies outside the measured visible ellipse inside R (about 21 % of R, the corners) and keep the
   rest at full density. At 4x on the corners that is about 16 % fewer encoded pixels **at no visible cost**, on top of 1; or the budget to run
   milder foveation inside the window. Engine on both sides, medium effort; gated by the quality benchmark (band judge) and a headset look.
4. **The client's queues** are where the latency is: on the 2026-10-23 debug session the headset's vsync queue was 22.9 ms and its decoder queue
   8.3 ms against 9.5 ms of encode and 9.1 of decode (`docs/PERFORMANCE_FINDINGS.md`), and stock ALVR shows the same queue. Not a FoV item and
   not free (every earlier attempt to shave the headset's queue traded against repeated frames), but with the fork it is ours to study: how the
   decoded frame waits for a drawable, and whether a decoded frame can go to the next drawable instead of the one after. A separate plan;
   named here because no PC-side change buys 20 ms.
5. **The declare channel** (host -> client rectangle, `docs/FOV_CALIBRATION.md` phase 2): no gain by itself, it moves the rectangle to the PC
   (per wearer, per eye, no client rebuild) and lets the launcher's pipeline model be the single owner of the geometry.

Not available: a per-frame field of view (the v20 video header has no room for it) and anything that makes the stock app show less than it declares.

## 3. Measured on the 3080 Ti (2026-10-29, one NVENC engine, 250 Mbps, 10-bit HEVC, QP map on, GPU keep-alive, P0)

Encode, from `bench-quality full --eye` (p50 / p95 ms per frame):

| Configuration | 3552 x 3200 over D | 3168 x 2496 (R frame, same fractions) | Change |
|---|---|---|---|
| strong foveation (the profile's 4 / 5), P3 | 4224 x 1664: **11.3 / 11.8** | 3776 x 1312: **8.4 / 9.5** | -25 % time for -30 % pixels |
| strong foveation, P1 | 7.6 / 8.5 | 6.6 / 7.6 | -13 % |
| mild foveation (2.5 / 3), P1 | 4800 x 1952: 9.8 / 10.8 | 4288 x 1504: 7.4 / 9.1 | -24 % |
| no foveation, P1 | 7104 x 3200: 20.2 / 21.8 | 6336 x 2496: 15.4 / 17.6 | -24 % |
| no foveation, P3 | 32.2 / 32.7 | not in the sweep | |

A third point, the stock-density R size (2464 x 2272, encoded 2944 x 1184 = 3.49 Mpx): P3 6.7 / 7.4 ms. The three points sit on a line,
**1.30 ms per encoded Mpx plus 2.1 ms** at P3 strong foveation on this GPU, so the iso-band variant (6.09 Mpx) lands at about 10.0 ms: -11 %. The
5090 encodes the foveated frame in 3.1 ms p50 with three engines (`docs/PERFORMANCE_FINDINGS.md`); the same proportions give about 2.8 ms (iso)
or 2.3 ms (fractions). The headset decodes in proportion to the same pixels (5.4-6.4 ms p50 today).

Frame generation (`fg_judge.py --eye`, the judge's GPU timing of its last case, same conditions at both sizes; the judge does not keep the GPU
clocked, so only the ratio is meaningful): flow **24.1 -> 16.7 ms (-31 %)**, edge synthesis **7.25 -> 5.12 ms (-29 %)**, classic 7.78 -> 5.64. Both
scale with the frame, as expected (the flow input is half the frame, the synthesis one pass over it); on the 5090 that is 2.2 + 0.24 ms today, so
about 1.5 + 0.17 ms after the shrink.

The whole chain at the R size (`e2e_fov_stream_r`: host, shim, probe app, mock headset on the builder, foveation on) **passed all 33 checks** on
the first run: the host negotiated 3168 x 2496, the encoded frame was 3776 x 1312 as the independent port of ALVR's layout predicts, every
decoded frame carried the right counter, the gray patch and the pose round trip held, encode p95 8.35 ms. Against `e2e_openxr_foveated` of
2026-10-28 on the same build: encode p50 9.97 -> 7.16 ms (-28 %), foveation pass 0.107 -> 0.080 ms; the shim's compose reading (1.74 -> 2.27 ms)
is **not usable**: the laptop sat in P8 for 80-90 % of both runs (`gpu.csv`) and that figure is compose start to GPU done, clock-bound. The compose
saving has to be read on the 5090 (`compose_ms` in `encoder_stats`, S5).

## 4. NVENC facts that matter here (SDK 13.0 programming guide, section 8.14, and the host's own capability log)

- Split frame encoding exists for HEVC and AV1 on GPUs with two or more NVENCs; it "improves the encoding speed" and "degrades quality" (strips are
  encoded independently); total throughput across engines is unchanged.
- **Auto mode** (`NV_ENC_SPLIT_AUTO_MODE`, the default) splits only when the frame is **at least 2112 pixels high (HEVC) / 2048 (AV1)** and the
  preset is P1-P2 (high quality tuning) or P1-P4 (low / ultra-low latency). Forced modes (`AUTO_FORCED`, `TWO`, `THREE`, `FOUR`) split regardless
  of resolution, with the strip count capped by the engine count. Disabled by weighted prediction, alpha layer, sub-frame readback and
  video-memory output (the host checks and logs these as `split.conditions_met`).
- Consequence: today's foveated frame (1664 high) never met the auto condition; it splits on the 5090 because the profile forces 3 strips. The
  shrunk foveated frame (1312-1536 high) is the same case. The full frame stays above 2112 at the R size (2496) and even at the stock-density R
  size (1888 would **not**): a full-frame profile that relies on auto mode must switch to forced when the frame shrinks below 2112. The harness
  should assert the strip count, not the mode (section 8).
- Whether forcing 3 strips on a 1312-pixel frame is worth its quality cost against 2 strips or none was never measured; the quality benchmark's
  `split` knob and `slices_per_frame` reading exist for it. One row for the 5090 session (section 6).
- Limits read back from the driver (3080 Ti): width 129-8192, height 33-8192; ALVR aligns the eye size to 32 and the foveated frame to 32; nothing
  in the shrunk sizes is near a limit. The 8192-wide crash of a resolution scale 1.21 is the opposite direction.

## 5. What "iso quality" means for the knobs

Three quantities define the picture the wearer sees; the plan keeps the first two and makes the third a choice:

1. **Pixels per degree in R**: unchanged (the eye size is derived from the density, not chosen).
2. **Bits per encoded pixel**: unchanged or better (the bitrate stays; a lower bitrate for the same bits per pixel is a separate A/B).
3. **The full-density band of the foveation**: either today's degrees (re-anchored preset) or today's fractions (narrower, cheaper). The launcher
   computes both from the declared box, the rectangle and the session's foveation; the user picks; the headset decides.

"No linear upscaling": the game renders R at the stream's density (render quality 0), the compose pass is a 1:1 resample (identity homography when
D = R), the client samples the frame over R. No stage scales R's pixels up.

## 6. The plan (owners, gates, what is measured)

| # | Step | Where (rule: engine only if named) | Gate / measurement | State |
|---|---|---|---|---|
| S0 | Baseline numbers at both sizes: encode, frame generation, compose, the chain | harness, 3080 Ti | this document, section 3 and 9 | done 2026-10-29 |
| S1 | **Geometry oracle**: eye size at constant density, encoded size, band in degrees, re-anchored foveation parameters | `harness/fov_stream_geometry.py` | self-test; the launcher's maths (S3) must equal it | done |
| S2 | **Chain at the R size** in the lab: negotiation, encoded layout, counter and gray patch through the stream, pacing, timings | `harness/scenarios/e2e_fov_stream_r.json`, `_full` | PASS on the builder | **PASS 2026-10-29** (both) |
| S3 | **Launcher: "Fit the stream to the headset's window"** (Advanced picture and FOV settings): a tick that sizes the stream from the headset's declared tangents (`config\airframexr_views.json`), the rectangle and the current density, writes `transcoding_view_resolution` / `emulated_headset_view_resolution` (Absolute, 32-aligned, 4096 per eye at most) and the foveation geometry (preset: re-anchored or same fractions) into `session.json`; shows the sizes and the band in degrees next to the fields; the density reference is the resolution the user saved (`video.stream_reference_eye` / `_fov`), not a profile key | `tools/gui/Advanced.cs`, `AirFrameXR.cs` (the `views_config` event rewrites the session), `SettingsPanel.cs` | `pipeline_model_check.py` (section 8) equals the oracle; `ui_matrix.py` row `advanced-switches-fit` | **built 2026-10-29** (branch), lab verdicts in section 9 |
| S4 | **Client declares a per-edge ceiling, not R** (decision of `docs/FOV_CLIENT_CEILING.md`, 2026-10-29: the per-person rectangle stays on the PC; fork branch `fov-calibration` `acf3de9`, constants 1.03 / 0.79 / 0.75 / 0.86, about 1.05 / 0.80 / 0.76 / 0.87 declared) and a TestFlight build. With it the fit tick saves about 6 %, not 30 %: the 30 % needs the client to declare the wearer's rectangle, which is S6 (the declare channel). The stock app's FoV Scale slider is not applied on this path any more (`Renderer.swift` 783-786): drop `client_fov_scale` from the profile's notes | client fork, docs | `fovgrid.exe` outline at the visible edge; the host's `views_config` event shows R | build pending |
| S5 | **Headset A/B** (5090 + Vision Pro, `harness/live.py` variants, same game section, same bitrate): (1) stream over D 3552 x 3200 against R 3168 x 2496 same fractions, (2) against R re-anchored; read `client_frames.csv` (decode ms, total latency, repeated periods), `encoder_stats` (encode p50/p95, `ffr_ms`, `compose_ms`, `gen.flow_ms`), and the wearer's verdict on the band; (3) split 3 against 2 against off on the 1312/1536-high frame (`slices_per_frame`, encode ms, eyes) | live harness (settings only) | numbers in `docs/PERFORMANCE_FINDINGS.md`; the preset that wins becomes the profile's default | after S3 + S4 |
| S6 | Optional, **engine**: the host tells the client the rectangle (`ServerControlPacket::Reserved` JSON, phase 2 of `docs/FOV_CALIBRATION.md`: `server_core` `send_reserved`, host stdin `cal_declare`, client `ClientCoreEvent::Reserved` -> re-declare) so the rectangle lives on the PC (per wearer, per eye) and the client needs no rebuild; with it, per-eye boxes (the calibration found 13 % fewer pixels than the mirrored one) | host (`tools/host`, vendored `server_core` patch), client fork; **shim and IPC untouched** | `e2e_fov_declared_r*` with a mock that declares R; a re-declaration reaches the next game start cleanly (the untested `pending_views` path) | after S5 says the shrink is worth keeping |
| S7 | Docs and profile: `docs/AVP_FOV.md` (the band finding), `docs/PERFORMANCE_KNOBS.md` (stream density as a structural choice), `profiles/apple-vision-pro.profile.json` (density, foveation preset), `docs/progress.md` | docs | | with S3 / S5 |
| S3b | **Pipeline model in the launcher** (`tools/gui/PipelineModel.cs`, a port of `fov_stream_geometry.py`): stage sizes and warnings from the current settings in the Advanced window (FoV, quality, resolution, encode profile, split mode, preset, the GPU's engine count from `config\airframexr_gpu.json`, which the host writes at every encoder creation); the split warning whenever the expected strip count is below what the GPU can do | `tools/gui/` | `pipeline_model_check.py` equals the oracle over a 148-case grid; `ui_matrix.py` row | **built 2026-10-29**, lab verdicts in section 9 |
| S8 | **Frame pipeline strip in Game statistics**: the host's run record gains a `pipeline` block (declared box, scale, quality, ceiling, game render size from the shim (`ShimToHost::recommendedW/H`), stream frame, encoded frame, foveation geometry, generation sizes and ms, strips expected / seen, encode p50 / p95 and foveation-pass ms over the run's encoder windows, compose ms); `performance.exe` draws one tile per stage and names a split that did not engage | host `main.rs` `pipeline_facts` (engine) + `tools/gui/Performance.cs` | `e2e_fov_stream_r_fg` (`checks.game_run.pipeline`): **PASS 2026-10-29**; `ui_matrix.py` row `performance-pipeline`; `performance_check.py` | **built and lab-checked 2026-10-29** |
| S9 | **Render quality ceiling** when the client declares R: the slider's top becomes the panel reference (the saved resolution), not the stream's eye size; IPC v36 carries `renderCeiling` (host -> shim) and `recommendedW/H` (shim -> host), `render_size.h` takes the ceiling, the host passes `stream_reference_eye` when the fit is on (lab `--fov-ceiling WxH`) and reads the slider whatever the override | shim + IPC + host (engine, small) | `e2e_fov_stream_r_quality_full`: the app is told 3552 x 3200 while the stream is 3168 x 2496: **recommended size PASS 2026-10-29** (first run; the scenario's counter readback was size-bound and is dropped, rerun in section 9) | **built 2026-10-29** |
| S10 | **Visible-shape warp** (2c item 3): host shader + client shader + negotiation | engine, both ends | quality benchmark with the band judge; encode ms; headset look | after S5, if the shrink is kept |
| S11 | **The render quality slider's pixels must reach the stream**: the compose pass averaged nothing (one bilinear tap), so the slider's supersampling (at most about 1.1 x 1.2 linear over the rectangle: the ceiling is the unrestricted pixel count, by design) was invisible; `ReprojectPS.hlsl` now takes up to 3 x 3 taps over the output pixel's footprint | shim shader (engine) | lab: a probe pattern with a 1-pixel checkerboard at quality 1 must come out mid-grey and flat in the stream (to add: probe pattern + `e2e_fov_restrict_quality_filter`); `compose_ms` before / after on the 5090; a tester's look | **written 2026-10-29, not compiled** (laptop builder offline) |

Rejected after measurement, so nobody re-opens them without new evidence:

- **Merging the foveation pass into the shim's compose pass** (one pass fewer on the host): the pass costs 0.11 ms on the 3080 Ti at 7104 x 3200
  (`ffr_gpu_ms_avg`), under the project's 1 ms bar, and it would put frame generation on a warped frame.
- **Removing frame generation's history copy** by holding ring slots: about 0.1 ms on the 5090 (91 MB), 0.2-0.5 ms on the laptop; would need
  more than 6 slots. Below the bar.
- **A host-side warp that compresses only D minus R** on a stock client: ALVR's client un-warps with the two-level parameters of `StreamConfig`
  (centre, shift, edge ratio); there is no third level to put the margin in. The override (game saving only) stays the stock-client answer.
- **The host sizing the stream from the client's declared box at connection**: the size is negotiated before the box arrives (section 1).

## 7. Expected effect per stage (5090 + Vision Pro, iso band preset; measured where said)

| Stage | Today | Expected | Basis |
|---|---|---|---|
| Game GPU | renders R (3168 x 2496 equivalent) | unchanged | already restricted |
| Compose (shim) | one pass over 7104 x 3200: **0.34-0.52 ms on the 5090** (2026-10-29 night) | -30 %, about 0.1-0.15 ms | measured in the run record's pipeline block |
| Frame generation | flow 2.2 ms + synthesis 0.24 ms (5090, 7104 x 3200) | about 1.5 + 0.17 ms | measured -31 % / -29 % on the 3080 Ti |
| Foveation pass | 0.11 ms (3080 Ti) | 0.08 ms | per pixel |
| NVENC | 3.1 ms p50 foveated; **7.55 ms p50 full frame, 3 strips (5090, 2026-10-29 night)** | foveated: about 2.8 ms (iso), 2.3 ms (fractions); full frame: about 5.3 ms | 3080 Ti: 1.30 ms per Mpx + 2.1 ms, three sizes on a line; the 5090 full-frame point measured |
| Network | 300 Mbps, 0.39 bpp | 0.44 bpp at the same bitrate, or -12 % bitrate at the same bpp | arithmetic |
| Decode (headset) | 5.4-6.4 ms p50 foveated; **6.9 ms p50 full frame (2026-10-29 night)** | foveated: about -0.7 ms (iso), -1.7 ms (fractions); full frame: about -2.1 ms | per pixel |
| VRAM (ring + generator) | 6 + 3 frames of 91 MB | -29 % | arithmetic |
| Total latency | 67 ms p50 typical | about -1 to -3 ms on the critical path (encode + decode), invisible under the panel's tick quantum unless it crosses a tick | the arrival-margin finding of 2026-10-17 |

Fps: none from the shrink (the game already renders R). The honest headline is **1-3 ms of latency and 30 % less GPU work in the host's own
passes**, plus cleaner compression at the same bitrate.

## 8. Harness: updates and new tests

Existing pieces reused: `bench-quality --eye WxH` (encode cost per size), `fg_judge.py --eye` (generation cost per size), the `fov_restrict`
checks (`analyse_fov_restrict`), the `foveation` layout check (`fov_vars` port), `streamer_events.py`, `ui_matrix.py`, `live.py`.

| Test | What it checks | Needs | State |
|---|---|---|---|
| `fov_stream_geometry.py --selftest` | the band formula against the shader's equations; prints the sizes and bands of this document | nothing (numpy) | **done, PASS** |
| `e2e_fov_stream_r` | the chain with the stream at 3168 x 2496, foveation on: negotiated size, encoded frame = ALVR's layout for that size, counter and gray patch through the decoded stream, pose round trip, pacing guards, encode p95 under 16 ms; compose / foveation / encode timings for the comparison | builder | **added, PASS 2026-10-29 (33 checks)** |
| `e2e_fov_stream_r_full` | the author's baseline at the R size: no foveation, the encoded frame is the stream frame 6336 x 2496, the same chain checks; encode time for the no-foveation curve | builder | **added, PASS 2026-10-29** |
| `pipeline_model_check.py` | the launcher's `PipelineModel` against `fov_stream_geometry.py` over a grid: restriction 100 / 70 / 50 %, render quality 0 / 0.5 / 1, foveation off / mild / strong, resolution 2848 / 3552 / 4256, split auto / forced 3 / off, presets P1-P5 low latency and P2-P3 high quality, engines 1 / 2 / 3; every stage size and every warning equal | `uitest.exe` | after S3b |
| `e2e_split_rules` (optional, 5090) | the split rules against the driver: foveated 1664 rows in auto mode -> 1 slice per frame; forced 3 -> 3; full frame 2496 rows auto P3 LL -> 3; full frame auto P5 -> 1 (`slices_per_frame` from `encoder_stats`); documents why the profile forces | a GPU with 3 engines | to add |
| `game_record_pipeline` (in `host_tests.py` or `streamer_events.py`) | the run record's `pipeline` block from a fixture session: sizes, strips expected and seen, encode p50 / p95, decode ms present and plausible; `performance.exe` renders the strip (`ui_matrix.py` row) | `uitest.exe`, fixture logs | after S8 |
| `e2e_fov_stream_r_fg` | the same with frame generation at half rate (`e2e_fg_latency` at the R size): `gen.flow_size` follows the frame, latency believable, no generator errors | builder | to add (copy of `e2e_fg_latency` with the size overrides) |
| `e2e_fov_declared_r` | the mock headset **declares R** (new mock flag `--views-tan o,i,u,l`, `tools/mock_client`, lab only) with the override set to the same R: the host logs `fov_restrict` with scale 1.0 ("none"), the app gets R's tangents and a recommended size equal to the stream's eye size, the decoded frame has **no black margin** (the inverse of `fov_restrict_margin_black`: content to the edges) | builder, mock flag | to add |
| `e2e_fov_declared_r_tighter` | the mock declares R, the override asks 0.8 x R: the restriction is computed against R (`fov_scale_from`), margins black at the right place | builder, mock flag | to add |
| `stream_size_check.py` | the launcher's FoV-aware size and foveation maths (S3) equal `fov_stream_geometry.py` for the Vision Pro, the stock box and a Quest 3; 32-aligned, never above 4096 x 8192 per eye; the tick writes both resolution keys and the foveation keys; the Advanced window shows the band in degrees (`ui_matrix.py` row) | `uitest.exe` | after S3 |
| `fov_profile_check.py` | extend: the profile's density key reproduces 3552 x 3200 over D and 3168 x 2496 over R | `uitest.exe` | after S3 |
| `e2e_fov_stream_r_split` (optional, 5090) | forced 3 strips on the 1312-high frame: `slices_per_frame` 3; and the auto-mode control: 1 strip (the 2112 rule), so the harness documents why the profile forces | a GPU with 3 engines | to add next to `e2e_openxr_foveated_split` |
| `bench-quality --eye` sweep, scripted | the three sizes of section 3 in one command with a table (what was done by hand today) | builder | small wrapper, optional |
| `live.py` variants | `fov_stream_d`, `fov_stream_r_fractions`, `fov_stream_r_band` (S5): the session's two resolution keys and the foveation keys per variant (`live.py` applies `max_prediction_ms` and `bitrate_mbps` today; add the resolution and foveation edits) | headset | after S3 |
| `memory_baseline_fullres` (stability) | a second baseline at the R size: VRAM of host + app lower by about 29 % | builder, 10 min | optional |

The mock client flag and the `live.py` edits are lab tooling, not the engine.

## 9. Run log

- 2026-10-29, 3080 Ti: `bench-quality full --eye 3552x3200` and `--eye 3168x2496`, `quick --eye 2464x2272` (the stock-density R size, for the
  curve), `fg_judge.py --eye 3552x3200 / 3168x2496 --starts 1 --gaps 33.3 --speeds 8`, `run e2e_fov_stream_r`. Encode numbers in section 3.
  The bench's own `slower_preset_not_worse` / `encode_time_by_preset` checks read FAIL because they look for a "split auto" label while the
  fixture session has split 3: a label mismatch in `run.py bench_quality`, not a measurement problem (to fix separately).
  Results: `20261029T100202Z_bench_quality_full_3552x3200`, `20261029T100328Z_bench_quality_full_3168x2496`, `20261029T100441Z_bench_quality_quick_2464x2272`,
  `20261029T100452Z_fg_judge` (3552 x 3200), `20261029T100515Z_fg_judge` (3168 x 2496), `20261029T100537Z_e2e_fov_stream_r` (PASS).
- 2026-10-29, later: `run e2e_fov_stream_r_full` PASS (`20261029T102927Z_e2e_fov_stream_r_full`): the host negotiated 3168 x 2496 without foveation, the
  decoded stream was 6336 x 2496, counter, gray patch and pose round trip held; encode p50 / p95 20.3 / 22.0 ms on the laptop's single engine
  (the probe's simple picture; the benchmark scene reads 32 ms at P3 for 7104 x 3200), compose 1.25 ms, 1 slice per frame as expected with one engine.
- 2026-10-29, afternoon (the build of S3, S3b, S8, S9 and the harness): host, shim and mock client built on the laptop; first round of the new scenarios:
  `e2e_fov_stream_r_fg` **PASS** (frame generation on the fitted stream; the run record's `pipeline` block present and plausible: sizes, strips, encode p50 / p95);
  `e2e_fov_stream_r_quality_full`: the app was told **3552 x 3200 over the 3168 x 2496 stream (the ceiling works)**, but the scenario had inherited the probe's
  counter readback, which is size-bound (the 64-px blocks shrink with the composition), so it read FAIL on that and was corrected (readback dropped);
  `e2e_fov_declared_r` / `_tighter`: the tangents and recommended sizes exact, the margins black, but the mock still declared +-1.0 (its tracking thread had
  its own view declaration, patched after the run) and the content-inside sample read 93 % on the asymmetric rectangle (the check now samples two depths).
  Then the laptop dropped off the network (its temporary address stopped answering; its usual address is now another machine with another SSH key, so the
  wrapper refuses it, rightly): the rerun of the corrected scenarios, the FoV regressions (`e2e_fov_override`, `e2e_fov_restrict_none`,
  `e2e_fov_restrict_quality_half`, `e2e_openxr_probe` on the new IPC), the GUI build and its checks (`pipeline_model_check.py`, `ui_matrix.py --only
  advanced-switches` and `--only performance`, `fov_profile_check.py`, `performance_check.py`) are **owed**. The GUI compiles in WSL (`tools/wsl/check_gui.sh`),
  the host and the mock pass `cargo check`, the shim and the host library pass `check_cpp.sh`.
- 2026-10-29, after the laptop's Windows update: the owed round, all green. `e2e_fov_declared_r`, `e2e_fov_declared_r_tighter` (the mock declaring the
  rectangle, patched), `e2e_fov_stream_r_quality_full` (the ceiling: the app told 3552 x 3200 over the 3168 x 2496 stream), the FoV regressions
  `e2e_fov_override`, `e2e_fov_restrict_none`, `e2e_fov_restrict_quality_half` and `e2e_openxr_probe` on the new IPC: **7 of 7 PASS**.
  `pipeline_model_check.py`: **148 of 148 cases, 0 mismatches** between the launcher's `PipelineModel` and the Python oracle (sizes, bands, strips, warnings).
  `ui_matrix.py --only advanced-switches` and `--only performance` (the fit tick row and the Game statistics strip among them): 0 FAIL, 0 warnings.
  `fov_profile_check.py` and `performance_check.py`: PASS. So S2, S3, S3b, S8 and S9 are **built and lab-checked**; what the lab cannot judge (the picture on a
  headset, the compose saving on the 5090, the split strips at the shrunk size) stays owed to S5.
- 2026-10-29 night, 5090 + Vision Pro, 1.2.0-pre, the ceiling client, full-frame profile, override on (`private/logs/5090-2026-10-29-fovgrid`): the run record's
  `pipeline` block in the field: declared box 1.045 / 0.800 / 0.759 / 0.871, scale 92 / 89 / 91 / 70 %, game render 3212 x 2550 (the probe reported 3212 x 2552:
  VDXR's own rounding of the recommended size, 2 px), stream 3552 x 3200, encoded 7104 x 3200, **3 strips expected and 3.0 seen** (3 engines, forced), encode p50 / p95
  **7.55 / 7.6 ms**, **compose 0.34-0.52 ms**, headset decode 6.9 ms p50, total latency 66.7 ms (vsync queue 22 ms, decoder queue 7.9 ms), 99 fps streamed at 100 Hz,
  headset repeated 6-7 % typical. So the compose saving of the fit is worth about 0.1-0.15 ms on the 5090 (section 7's row), and the 30 % encode + decode saving
  (about 2.3 + 2.1 ms at these numbers) is the whole prize of the fitted stream. The fit tick was not used tonight; the stream stayed 3552 x 3200.
- Not done: the fit tick and the band presets on the headset (S5 proper, the live variants); the split-rule scenarios on the 5090; S6 (the declare channel) and S10
  (the visible-shape warp), both gated on S5.
