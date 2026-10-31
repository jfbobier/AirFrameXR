# ALVR v21 migration plan

Status: analysis, 2026-10-31. Nothing in the engine was changed for this document. Every item below that touches
`tools/host/`, `nvenc/` or the IPC is engine work and needs the go-ahead the project rules require.

Sources: upstream `alvr-org/ALVR` master at `adaa16ba` (2026-10-21, the commit behind the `v21.0.0-dev14+nightly.2026.10.31`
build; fetched into `external/ALVR-master` as `upstream/master`, working tree untouched), diffed against `v20.14.1`
(`a9f6542f`, what we ship); our code at `main` 2205331; the client forks `external/alvr-quest` (41 commits on v20.14.1) and
`external/alvr-visionos-pr` (56 commits on upstream alvr-visionos, ALVR submodule pinned at `e3fd4480` = v20.14.1 + 4 commits).
Upstream issues #1722, #3426, #3433, #3440 and the open PR list were read on 2026-10-31.

## 0. Summary

- **v21 is a hard protocol break.** The protocol id goes from `"20"` to `"21"` (pre-releases: `"21-dev14"`, bumped on every
  wire change). A v21 host pairs with no v20 client and vice versa; there is no compatibility mode. Both our clients and the host
  move together, in one release.
- **There is no v21 tag and no published date.** Master is `21.0.0-dev14`; at least one more wire break is likely before the tag
  (PR #3433, decoder config in the frame header) and issue #3440 may revert the September foveation commits. We should not ship
  anything built on a `devNN`.
- **Our frame path is mostly untouched by v21**, because the host already bypasses ALVR's encoder, driver and pacing. The migration
  is concentrated in five places: the `ServerCoreContext` API (four changed calls and events), the `openvr_config` cache in
  `session.json` (removed upstream, read by the host, the GUIs and the harness), the six source patches, the two clients'
  bindings, and the lab's mock client. One behaviour moves: **pose prediction leaves the client and becomes the host's job.**
- **Benefits are real but modest**: the host gets raw poses with velocities and can predict to its own display boundary; every
  video packet carries the exact view poses it was rendered with (the client stops guessing them from its own history); canted eye
  poses pass through; a clean extension channel (`ext_str`) for the wearer's rectangle; Quest CPU/GPU performance levels; the PSVR2
  profile upstream. Nothing in v21 changes the encoder, the codecs or the bandwidth for a Windows/NVIDIA host.
- **Recommendation**: prepare now, release later. Phase 0 to 2 (host and lab on a pinned master commit) can start when the
  engine go-ahead is given; the clients and the release wait for the `v21.0.0` tag. Release as **AirFrameXR 2.0** with both
  clients, the launcher's existing protocol check telling v20 users to update the headset app.

## 1. Where v21 stands (2026-10-31)

| Fact | Detail |
|---|---|
| Version on master | `21.0.0-dev14`, Rust edition 2024, CI on Rust 1.97.1 (we have 1.99). v20.x lives on a separate `v20` branch; master is 450 commits ahead and 377 behind v20.14.1 (features were backported both ways). |
| Protocol id | `protocol_id()` = major, or `major-pre` for pre-releases (`alvr/common/src/version.rs`). `"21-dev14"` today, `"21"` at the tag and for every 21.x.y after it. The server rejects a client whose id differs. |
| Dev bumps | dev00 2023-06 ... dev10 bincode 2 + little-endian framing (2025-07), dev11 max view resolution (2025-11), dev12 stream packetization (2025-11), dev13 per-eye foveation centres (2026-09-11), dev14 encoder-aligned centres per frame (2026-09-17). |
| Pending | PR #3433 (decoder config in the video header, tested on a "Steam Frame" branch) would be dev15. Issue #3440 "Sanitize ALVR from AI generated code" (opened 2026-10-31) targets the foveation PRs behind dev13/14. Four Linux direct-mode regressions open (#3412, #3420, #3421, #3431); none touch the Windows path. |
| External pressure | Valve (issue #3426, 2026-10-14) says v20.14.1 is "not supportable" with current SteamVR on Linux; the maintainers answer "use nightly". No release date anywhere public. |
| Apple Vision Pro upstream | `alvr-org/alvr-visionos` still pins ALVR at `e3fd4480` (v20.14.1 + 4). No v21 work exists there. We would be the first Swift port. |

## 2. Benefits

### 2.1 Performance (what can be measured, and how)

| Change in v21 | What it gives us | Measurement that decides it |
|---|---|---|
| **Raw poses with velocities; the server predicts** (`get_motion_to_photon_latency()` now returns the statistics average clamped to `max_prediction_ms`; v20 clients pre-predicted by the average pipeline latency and zeroed head velocity). | The host knows the real display boundary (its vsync grid) and can predict each sample to it instead of inheriting the client's average-latency prediction. Less over-prediction when the pipeline is short, no under-prediction when it stretches. Also retires the visionOS fork's 90 ms prediction-cap patch. | Rig (`cargo feature simrig`, the judge): moving-head trembling and pose age vs. the v20 build at equal settings. Headset: the Black Box latency table. |
| **Per-frame view poses in the video header** (`VideoPacketHeader { timestamp, global_view_params: [ViewParams; 2], foveation_center_shifts, is_idr }`). The client reprojects from the pose the frame was really rendered with; it no longer looks the pose up in its own history by timestamp. | Removes a class of judder upstream fixed in 2025 (pose desync). For frame generation the host states the pose of each synthetic frame (the sample it was re-stamped to) instead of relying on the timestamp match. | Rig: frozen periods and `regime_switches` at the same FG rate. |
| **Canted eye poses pass through** (`LocalViewParams([ViewParams; 2])` carries the full per-eye pose; the client-side circumscribe-to-orthogonal conversion was deleted). | The Vision Pro's displays are canted; the stream today covers the circumscribed orthogonal rectangle. If the shim rendered the true canted views (LibOVR's `HmdToEyePose` is a full pose), fewer encoded pixels for the same visible area. | Area of the canted view vs. the circumscribed one for the calibrated AVP tangents (`docs/AVP_FOV.md`): a number before any code. |
| **Quest performance levels** (`RealTimeConfig.cpu/gpu_performance_level` → `XR_EXT_performance_settings`). | Steadier decode and compositor times on Quest 3 (SustainedHigh / Boost). | Client statistics `decoder_s`, `client_compositor_s`, `vsync_queue_s` p95 with and without. |
| `max_view_resolution` negotiation (server clamps the transcoding resolution to the client's swapchain limit, 32-aligned). | A guard, not a gain. | none |
| Full range only (`use_full_range` removed, NVENC `videoFullRangeFlag = 1`). | Nothing: we already force full range in `nvidia_profile.rs`. | none |
| bincode 2 varints, 14-byte UDP shard prefix (was 18). | Below the noise at our bitrates. | none |
| 5.1 downmix channel map fix in `alvr_audio` (dad45717). | Correct game audio when the default playback device is 5.1. | Listen. |

### 2.2 Features we could add

- **The wearer's rectangle ("declare channel", S6 of the FoV work).** v21 keeps `Reserved(String)` / `ReservedBuffer` on both
  control-packet enums and adds a JSON `ext_str` to the client capabilities, the negotiated config and the decoder config. The
  client can declare its calibrated FoV in `ext_str` at the handshake, before the first frame, instead of over a reserved packet
  after the stream starts. Plan S6 on v21 rather than on the v20 reserved packet.
- **Proximity / presence** (`ServerCoreEvent::ProximityState(bool)`, from `XR_EXT_user_presence` on Quest). The host could idle
  the encoder and the game clock when the headset is off the head. Not in the C API yet (Rust only), so the visionOS client
  would need a one-line addition in our submodule.
- **Battery and playspace events** (`Battery`, `PlayspaceSync`) reach the host as typed events: battery in the streamer window
  costs a few lines in the GUI.
- **PSVR2 Sense upstream**: `ControllersEmulationMode::PSVR2Sense` and the interaction profile `/interaction_profiles/sony/playstation_vr2_sense`
  (with trigger and grip proximity sensors) are in master. Our visionOS fork's submodule patch for the same variant goes away.
  Note the path differs from the fork's `.../playstation_vr2_sense_controller`.
- **Eye-tracked foveated encoding plumbing** (per-eye centres per frame, an OSC gaze input on port 9945, `alvr_get_combined_eye_gaze`).
  Usable on a Quest Pro. **Not on the Vision Pro**: visionOS does not expose gaze to applications. We would pass `None` for the
  centres and keep the fixed layout; the plumbing stays available.
- **Upstream `alvr/client_mock`** exists on master. Worth a look as a replacement for `tools/mock_client` before porting ours.

### 2.3 What v21 does not bring us

Linux direct mode (the largest change in the diff), the Intel VPL encoder, AMF fixes, the axum dashboard API, the dashboard and
launcher, body tracking and detached controllers (SteamVR trackers), Quest 1 / Pico emulation. No new codec, no new NVENC
preset or parameter, no change to the Windows encode pipeline besides full range and the foveation cbuffer.

## 3. Regressions and risks

| # | Risk | Effect on us | Mitigation |
|---|---|---|---|
| R1 | Hard wire break, no dual protocol. | 1.x clients (TestFlight visionOS 1.2.x, Quest APK 0.9.0) cannot pair with a 2.0 host. Stock Quest store clients stay v20 until Meta ships v21. | Our users run our clients anyway. Release host and both clients together; the launcher's `headset_seen.compatible` check already exists and should say "update the headset app". Keep 1.x zips published. Two host binaries behind a toggle is possible (protocol id is compile-time) but not worth it unless a measured share of users is stuck on a v20 client. |
| R2 | No tag; every `devNN` breaks pairing again; #3433 and #3440 pending. | Work on a dev commit may need a re-pin; client builds on a dev id are throwaway. | Pin one commit for the host and lab work; port the clients only at the tag. |
| R3 | **Prediction moves to the host.** If the host forwards raw poses unchanged, the game renders with poses one pipeline latency stale. | Visible lag on day one unless implemented. The Vision Pro's velocities are finite differences of ARKit poses (noisy; the host zeroes head velocity today, `main.rs:2188`). | Implement host prediction before the first headset test; orientation-only extrapolation first, measured on the rig. The shim already carries a head-sample policy (`predictMissingHead`). |
| R4 | `send_video_nal` needs `[ViewParams; 2]` for every packet; upstream's driver drops a frame with no matching pose. | Every rendered and synthetic frame must resolve to a pose. The shim stamps frames with the client timestamp of the pose the app rendered with (`frameClientTsNs`, matched by `RenderPose`; `tsFallback` counts misses); the host keeps recent samples (`push_recent_sample`). | Compose view params from the recent-sample queue plus the last `LocalViewParams`; fall back to the newest sample rather than dropping; deepen the queue if the FG path or a long game pipeline misses (upstream keeps 360). Count fallbacks in the statistics. |
| R5 | UDP broadcast discovery removed (mDNS only); client hostnames become `NNNN.client.local.`. | `discovery.rs:91-97` (UDP 9943 parser) goes dead; `harness/find_headset_e2e.py` builds that packet; existing `client_connections` entries stop matching. | Keep the mDNS branch of `discovery.rs`, delete the UDP one; the harness announces over mDNS; the pairing flow re-trusts by the new hostname. |
| R6 | `StatisticsSummary` loses `packets_lost_total` / `packets_lost_per_sec` (`report_packet_loss` removed). | `logs.rs:250,442`: the network benchmark and the status line lose the packet-loss figure. | Find a replacement before porting the bench: IDR requests per minute from `RequestIDR`, or the client's `video_decoder_queue` spikes; otherwise say the metric is gone. |
| R7 | Six source patches to rebase. `audio-process-loopback.patch` sits on an API that changed (cpal 0.16, `AudioDevice` type removed); `loopback-client.patch` targets `sockets/src/backend/udp.rs`, which moved to `stream_socket/udp.rs`; the two log patches move lines in `connection.rs` and `statistics.rs`. | Build breaks loudly (by design). | Rebase in order, re-run `stage9_patch.ps1`; `docs/UPSTREAM.md` is already wrong about the count (says one, there are six) and gets fixed in the same pass. |
| R8 | Vendored encoder C++ is v20: `FFR.cpp` gained a per-eye centre cbuffer, `VideoEncoderNVENC.cpp` hard-codes full range, `Settings.cpp` is deleted (settings now arrive as a `#[repr(C)] Settings` from Rust). | Our `nvenc/shim/alvr_server/Settings.h` re-declaration, the ~20 `build.rs` text anchors and the three copies of `CalculateFoveationVars` (`nvh.cpp:216`, `PipelineModel.cs:74`, `harness/lib.py:742`) must agree with the server's `align_foveation_center_shift`. | Decision D6 below: keep the v20 C++ and feed it the server's aligned params, or re-vendor from v21. Either way a lab check that the client's and the host's foveation layouts match (`e2e_fov_stream_r` already covers the geometry). |
| R9 | Session schema renames: `use_10bit`, `encoding_gamma`, `hdr.enable` become `Option`; `server_overrides_*` and `use_full_range` removed; `foveated_encoding.center_size/center_shift/edge_ratio` become arrays; `recentering_mode`, `*_buffer_config`, `face_tracking.*`, `body_tracking.sources.*` renamed. Unknown keys are dropped silently on merge. | `nvidia_profile.rs` force-writes, the GUIs, `profiles/*.profile.json` `session_overrides` and the harness fixtures would write keys nobody reads: the negotiation with the client silently disagrees with the encoder. | A harness check that every key we write exists in `alvr_session::Settings` (serialize the default and compare paths). |
| R10 | Quest fork rebase: our Vulkan port rewrote `alvr/graphics/src/{lib,stream}.rs` and `client_openxr/src/{lib,graphics,interaction,stream,lobby}.rs`; upstream rewrote the same files (interaction.rs 1258 lines changed, foveation moved to a uniform buffer, wgpu 25, openxr 0.21). | Not a rebase, a re-port of the Vulkan path onto master's graphics. The fork also removed `mdns-sd` from `client_core`, which v21 requires. | Budget it as a port; keep `QUEST_VULKAN_PORT.md` as the guide. |
| R11 | No Swift reference for v21. | We port the C-API call sites ourselves (list in 5.2); TestFlight review adds days. | The Rust `client_openxr` and `c_api.rs` are the reference; cbindgen regenerates the header. |
| R12 | Toolchain: edition 2024 and let-chains in ALVR (fine on 1.99), bincode 2, wgpu 25, openxr 0.21, cpal 0.16, `android-activity` pinned to 0.6.0 (0.6.1 freezes). | Build-side only. | `tools/wsl/check_host.sh` on the pinned commit before any code change (Phase 0). |

Not a risk for us: the Windows frame-freeze issue #2931 (SteamVR direct mode, which we do not use); `restart()` removed (the host
never called it); `alvr_events` kept; `Quest2Touch`, hand gestures, H264/HEVC/AV1 and `max_buffering_frames` all kept.

## 4. Technical impact, server side

### 4.1 Host engine (`tools/host`)

The host uses `ServerCoreContext` directly (no C ABI, no encoder, no driver). Unchanged calls: `initialize_environment`,
`ServerCoreContext::new`, `start_connection`, `set_video_config_nals`, `report_composed`, `report_present`,
`duration_until_next_vsync`, `get_dynamic_encoder_params`, `get_device_motion` (signature), `send_haptics`, the `alvr_common`
button tables, `protocol_id*`, `Layout`. What changes:

| Where | v20 | v21 | Work |
|---|---|---|---|
| `pipeline.rs:1347,1350`, `main.rs:1067,2997` | `send_video_nal(ts, nals, is_idr)` | `send_video_nal(ts, [ViewParams; 2], Option<[[f32;2];2]>, is_idr, nals)` | Build the two global view poses: head pose for `ts` from the recent-sample queue × the last `LocalViewParams` (eye-to-head); `None` for the centres while the layout is fixed. Fallback and counter (R4). **M** |
| `main.rs:2077` | `ClientConnected` | `ClientConnected(ServerNegotiatedStreamingConfig { transcoding_view_resolution, emulated_headset_view_resolution, refresh_rate, foveated_encoding: Option<AlvrFoveatedEncodingParams>, codec, h264_profile, use_10bit_encoder, encoding_gamma, enable_hdr })` | Replace every `openvr_config.*` read (`configure_encoder` `main.rs:1112-1152`, `1212-1222`, `1271-1290`, gamma `447-450`, `vision.rs:197-212`) with this payload. Upstream currently sets `emulated = transcoding` (a slip to report). **M** |
| `main.rs:2105` | `Tracking { sample_timestamp }` | `Tracking { poll_timestamp }`; poses are raw, with velocities | Rename, then **host prediction** (R3): predict head (and hands) from `poll_timestamp` to the shim's display time; keep publishing the sample timestamp as the frame tag. **L**, measured on the rig. |
| `main.rs:2255,2276` | `ViewsConfig(cfg)` with `fov[i]`, `local_view_transforms[i].position` | `LocalViewParams([ViewParams { pose, fov }; 2])` | Read pose + fov per eye. Decision D5: circumscribe to orthogonal in the host (port the deleted `canted_view_to_proportional_circumscribed_orthogonal`) so the shim's `eyeFovTan` contract and the FoV restriction stay as they are; canted rendering is a later measured step. **S** |
| `main.rs:1389` | `AudioDevice::new_output(None)`, `get_windows_device_id` | free functions on `cpal::Device` | Mechanical. **S** |
| `logs.rs:250,442` | `StatisticsSummary.packets_lost_total` | removed | R6. **S** + decision |
| `logs.rs:239` | `GraphStatistics` | + `bitrate_directives` | None (serde ignores). |
| `discovery.rs:91-97` | UDP 9943 announce parser + mDNS | mDNS only | Delete the UDP branch; `HANDSHAKE_PACKET_SIZE_BYTES` may disappear. **S** |
| new events | — | `Battery`, `PlayspaceSync`, `GameRenderLatencyFeedback`, `ProximityState` | Ignore or forward to the GUI (battery). **S** |
| `nvidia_profile.rs:121-145`, `NVH_KEYS` | writes `session_settings.video.*` v20 keys and `openvr_config`-named C++ keys | renamed keys (R9) | Rewrite the key list; the `NVH_KEYS` names are ours to keep. **S** |
| `main.rs:1727` | reports `CARGO_PKG_VERSION` as `alvr` | 21.x | None. |

The host's own clock, boundary pick, intake, encoder thread, sender thread, FG and the IPC (`ipc.h` v36) do not change for v21.
The IPC already carries what the new header needs (`frameClientTsNs`, `headClientTsNs`, `eyeFovTan`); prediction policy may add a
field (the display time a sample was predicted to), which bumps the IPC version.

### 4.2 Encoder (`nvenc/`)

Decision D6. Two options:

- **Keep the vendored v20 C++** (`nvenc/upstream/`, checksummed) and feed `FFR` the server's `AlvrFoveatedEncodingParams`
  (`encoded_view_resolution`, `view_ratio`, `center_size`, `center_shifts` per eye, `edge_ratio`) instead of recomputing from
  the six settings. Minimal change; the three `CalculateFoveationVars` copies become "take the server's numbers". Full range is
  already forced by `nvidia_profile.rs`.
- **Re-vendor from v21** (`alvr/server_openvr/cpp/platform/win32`: `FFR.cpp` with the per-eye `FoveationVars` cbuffer and a
  rewritten `CompressAxis` that avoids a 0 × NaN edge, `VideoEncoderNVENC.cpp` full range, `Settings.cpp` gone). Re-anchor the
  ~20 `build.rs` patches and replace `nvenc/shim/alvr_server/Settings.h` with v21's `#[repr(C)] Settings` from `bindings.h`.
  Brings the per-eye centre path for free (eye tracking later) at the cost of a day of patch work and a re-verification of the
  split-encode, QP-map and recon-frame patches.

Recommendation: option 1 for the migration, option 2 only when a measurement asks for per-eye centres. Either way the lab's
foveation geometry checks (`e2e_fov_stream_r`, `fov_profile_check.py`) must pass against a v21 client.

### 4.3 ALVR source patches (`tools/patches/`, applied by `stage9_patch.ps1`)

| Patch | Target in v21 | Work |
|---|---|---|
| `client-caps-log.patch` (logs `va_client_caps`) | `server_core/src/connection.rs`, the capabilities struct lost `multimodal_protocol` and `prefer_full_range`, gained `max_view_resolution`, `ext_str`; `supported_refresh_rates` → `refresh_rates`, `supports_foveated_encoding` → `foveated_encoding` | Rebase; the GUI reader (`Wizard.cs:377`, `Reference.cs:24-40`) and `profiles/*.json` `match` keys follow the renames. **S** |
| `client-stats-log.patch` (logs `va_client_stats`) | `server_core/src/statistics.rs`; `ClientStatistics` unchanged | Rebase. **S** |
| `audio-process-loopback.patch` (48 kHz process loopback behind `AIRFRAMEXR_AUDIO_CAPTURE=process`) | `alvr_audio` API now free functions on `cpal::Device`, cpal 0.16, `record_audio_blocking(is_running, StreamSender<()>, &Device, channels, mute)` | Rewrite against the new API. **M** |
| `audio-trace.patch` (`TRACE` hook) | `alvr_audio/src/lib.rs` | Rebase. **S** |
| `loopback-client.patch` (`ALVR_BIND_IP`, `SO_REUSEADDR`) | `sockets/src/stream_socket/udp.rs` (file moved and rewritten), `client_core/src/sockets.rs` | Rebase. **S** |
| `client-audio-device.patch` (mock client) | `client_core/src/connection.rs` | Rebase. **S** |

`docs/UPSTREAM.md` is updated with the real list.

### 4.4 Configuration files

**`session.json`**: `SessionConfig` becomes `{ server_version, client_connections, session_settings, restart_settings_hash, steamvr_hmd_init_config }`.
`openvr_config` (the ~80-field cache the host, the GUIs and the harness read, and the GUIs write) is gone; its replacement
`steamvr_hmd_init_config` holds only the eye resolutions and the refresh rate for SteamVR's benefit.

Proposed rule (decision D3): **the host owns the negotiated stream config.** On `ClientConnected` it writes the payload to a
host-owned file (for example `config\airframexr_stream.json`, next to `airframexr_gpu.json`) and the GUIs and the harness read
that, never ALVR's cache. The GUI's writes into `openvr_config` (`SettingsPanel.cs:252-257`, `PipelineModel.cs:272-275`) become
writes into `session_settings` (`transcoding_view_resolution`, `emulated_headset_view_resolution`, `preferred_fps`,
`foveated_encoding.enabled`), which is where ALVR reads them from anyway.

Key renames to apply everywhere we write (`nvidia_profile.rs`, `SettingsPanel.cs:236-262`, `Configure.cs:273-295`,
`Common.cs:484-498`, `PipelineModel.cs:227`, `profiles/*.profile.json`, `harness/live.py:183-210`, fixtures):

| v20 path (`session_settings.`) | v21 |
|---|---|
| `video.encoder_config.use_10bit` + `server_overrides_use_10bit` | `video.encoder_config.use_10bit: Option<bool>` (`null` = client preference) |
| `video.encoder_config.encoding_gamma` + override | `encoding_gamma: Option<f32>` |
| `video.encoder_config.use_full_range` + override | removed |
| `video.encoder_config.hdr.enable_hdr` + `server_overrides_enable_hdr` | `hdr.enable: Option<bool>` |
| `video.foveated_encoding.content.center_size_x/y`, `center_shift_x/y`, `edge_ratio_x/y` | `center_size: [f32;2]`, `center_shift: [f32;2]`, `edge_ratio: [f32;2]` |
| `connection.server_send_buffer_bytes` etc. | `connection.server_buffer_config.{send_size_bytes, recv_size_bytes}`, same for client |
| `headset.controllers.content.multimodal_tracking` | `headset.multimodal_tracking.{enabled, detached_controllers_steamvr_sink}` |
| `headset.position_recentering_mode`, `rotation_recentering_mode` | `headset.recentering_mode` |
| `video.passthrough.*`, `video.preferred_codec`, `video.bitrate.*`, `headset.controllers.content.emulation_mode`, `hand_skeleton`, `max_prediction_ms`, `connection.stream_protocol`, `packet_size`, `audio.*` | unchanged |

`tools/install/session.default.json` and `harness/fixtures/session.user.json` carry `server_version: 20.14.1`: regenerate from a
v21 server's first run. `client_connections` entries keyed by the old hostname format need re-pairing (R5).

**`airframexr.json`**: no ALVR field leaks into it; unchanged. The host-owned stream config file is new.

**`profiles/*.profile.json`**: `session_overrides` dotted paths follow the table; `stream`/`match` keys follow the capabilities
renames; `verified` strings say "ALVR 21".

### 4.5 GUIs (`tools/gui/`)

The GUIs talk to ALVR only through `session.json` and the host's stdin/stdout, so the impact is the key table above, the
`openvr_config` writes (D3), the `client_capabilities` field names, the pairing flow's hostname format, and the dropped
packet-loss figure in the network benchmark (R6). The pipeline model's `CalculateFoveationVars` port (`PipelineModel.cs:74`) is
unchanged if the host keeps fixed centres (same maths as upstream's `align_foveation_center_shift`); verify with
`pipeline_model_check.py` against the server's numbers. Optional: battery in the streamer window. **S** to **M** in total.

### 4.6 Harness, mock client, tools

| Item | Work |
|---|---|
| `tools/mock_client` (945 lines, uses `alvr_client_core`) | `ClientCapabilities` (+`platform`, +`max_view_resolution`, −`prefer_full_range`), `ClientCoreContext::new(caps, vec![])`, `send_tracking(TrackingData { poll_timestamp, device_motions, hand_skeletons, face, body })`, `send_active_interaction_profile(.., input_ids)`, `report_compositor_start -> Option<VideoFrameMetadata>`, `StreamingStarted(Box<ClientStreamConfig>)`; it announces over mDNS. Or adopt upstream `alvr/client_mock` if it covers `--views-tan` and the loopback bind. **M** |
| `harness/lib.py:112-131, 947-957`, `live.py:183-210`, `run.py:312` | `openvr_config` → the host-owned stream file; key renames. **S** |
| `harness/find_headset_e2e.py:39-48` | builds the 56-byte UDP announce in PowerShell; v21 clients announce over mDNS only. Rewrite with an mDNS announce (the mock client can do it). **S** |
| `tools/alvr_pcap_report.py` | 18-byte big-endian shard prefix → 14-byte little-endian (`u16 stream_id, u32 packet_index, u32 shards_count, u32 shard_index`), bincode 2 varints in headers. **S** |
| Stock baselines (`stock.py`, `steamvr_baseline.py`) | drive an installed stock ALVR v20.14.1 + SteamVR; keep as the v20 reference until a stock v21 exists. none |
| New check | every session key we write exists in the v21 schema (R9). **S** |

## 5. Technical impact, client side

### 5.1 Shared protocol touchpoints (both clients, all inside `alvr_client_core`)

The clients get the wire changes for free by linking the v21 `alvr_client_core`: bincode 2, little-endian framing, the
14-byte shard prefix, mDNS announce (`NNNN.client.local.`, TXT `protocol = "21"`), the new handshake
(`ConnectionAcceptedInfo` with a real `VideoStreamingCapabilities`, no more JSON hidden in negative refresh rates), the
typed `ClientNegotiatedStreamingConfig`, `RealTimeConfig` as a typed packet sent only on change, `TrackingData` with
velocities and no client prediction, `VideoPacketHeader` with view poses and centres. What each client must change is the
binding and the behaviour on top.

Chroma key: the `video.passthrough` schema (Blend / RgbChromaKey / HsvChromaKey) is unchanged; upstream fixed the HSV index
bug our Quest fork fixed (`stream.wgsl` compared with 3, Rust sends 2), so that fork commit drops. The streamer's key reaches
the Vision Pro client through the settings JSON at stream start as today (it ignores real-time updates, a known TODO).

### 5.2 Vision Pro client (`external/alvr-visionos-pr`, Swift + Metal, `alvr_client_core` C API via `ALVRClientCore.xcframework`)

**Reference implementation**: none in Swift. The reference is `alvr/client_openxr` (Rust) for behaviour and
`alvr/client_core/src/c_api.rs` for the ABI; `build_and_repack.sh` regenerates the header with cbindgen. Upstream alvr-visionos
has no v21 branch. Our submodule carries four patches on v20.14.1 that must be re-decided:

| Submodule patch (e3fd4480) | In v21 |
|---|---|
| `send_tracking` head prediction capped at 90 ms, tracker prediction off | Moot: the client no longer predicts. Drop. |
| `alvr_send_tracking_and_face_data` (hands + gazes + 70 FB face floats) | Gone. v21's C `alvr_send_tracking` takes hand skeletons and one combined gaze `AlvrQuat*`; face expressions have no C entry. Re-add a small C function in our submodule only if we use face data (we do not stream it to a game). |
| `ControllersEmulationMode::PSVR2Sense` | Upstream. Drop; align the profile path to `/interaction_profiles/sony/playstation_vr2_sense`. |
| CoreAudio workaround in `alvr_audio` | Rebase onto cpal 0.16. |
| `connection.rs` disconnect `break` commented out | Re-evaluate on v21's connection loop. |

Swift call sites that change:

| Call | v20 → v21 | Where |
|---|---|---|
| `AlvrClientCapabilities` | + `max_view_width/height`, − `prefer_full_range` | `EventHandler.swift:186-194` |
| `AlvrEvent.StreamingStarted` | + `foveated_encoding: AlvrFoveatedEncodingParams` (aligned, per eye) | `EventHandler.swift:807-808`; `FFR.swift:10-25,69,106` stops reading the six settings keys and recomputing the alignment: take the struct |
| `AlvrEvent.DecoderConfig.codec`, `alvr_get_decoder_config` | enum renamed `AlvrCodecType` | `EventHandler.swift:856-877` |
| `alvr_send_tracking` | `(poll_ts, motions, count, hand_skeletons, const AlvrQuat* combined_eye_gaze)`; send raw ARKit poses with velocities, no lookahead | `WorldTracker.swift:3507,3569` |
| `alvr_report_compositor_start` | `(ts, AlvrVideoFrameMetadata*) -> bool`; view params and centres now come from the server per frame; keep the previous metadata on `false` | `Renderer.swift:773`; the renderer reprojects from these poses |
| `alvr_send_view_params` | same signature, now raw canted poses are allowed; the local circumscribe (`_cantedViewToProportionalCircumscribedOrthogonal`, `Renderer.swift:809-810`) can stay while the host circumscribes (D5) | `Renderer.swift:779-833` |
| `alvr_send_active_interaction_profile` | `(device_id, profile_id, const uint64_t* input_ids, count)` | `WorldTracker.swift:598-599`, `InputSink.swift:63` |
| `alvr_send_custom_interaction_profile` | removed (merged into the above) | if used |
| FoV ceiling / wearer rectangle | declare via `ext_str` at the handshake (2.2) | new |
| Hostname, mDNS TXT | `.client.local.`, `protocol = "21"`; `alvr_mdns_service`, `alvr_protocol_id` unchanged | `EventHandler.swift:333-341` |

Unchanged: lifecycle (`alvr_initialize`, `alvr_resume/pause`, `alvr_destroy`), `alvr_poll_event`, `alvr_hud_message`,
`alvr_get_settings_json`, `alvr_send_button` (177 sites) and `alvr_path_string_to_id`, `alvr_send_battery`, `alvr_send_playspace`,
`alvr_report_frame_decoded/submit`, `alvr_set_decoder_input_callback`, the whole VideoToolbox decode path (H264/HEVC/AV1, 10-bit
formats), the chroma-key shaders and the ring, the Home-button handling, hands-as-controllers, the height offset.

Decoding, chroma key, controllers and head pose therefore split as: **decoding unchanged**, **chroma key unchanged**,
**controllers**: one signature plus the PSVR2 path and the dropped emulation patch, **head pose**: the two tracking calls and
the per-frame metadata, which is where the testing time goes. Size: **M** for the port, plus TestFlight.

### 5.3 Quest client (`external/alvr-quest`, Rust, 41 commits on v20.14.1)

The fork changes nothing on the wire (zero diff under `packets`, `sockets`, `common`, `session`). It is a Vulkan port of the
GLES client plus hands-as-controllers, pinch gestures, a height offset and a welcome panel. Upstream rewrote the same files
(R10), so the path is a **re-port onto master**, not a rebase:

| Fork area | Upstream v21 state | Work |
|---|---|---|
| `alvr/graphics/src/{lib,stream,staging}.rs` (Vulkan, AHardwareBuffer import, BT.709 full range) | wgpu 25, foveation parameters in a uniform buffer (`FoveationUniforms`, binding 2) fed from `AlvrFoveatedEncodingParams` and per-frame centre shifts; still GLES upstream | Carry the uniform-buffer foveation and the centre shifts into the Vulkan path. **L** |
| `client_openxr/src/interaction.rs` (hands as controllers, `XR_FB_hand_tracking_aim` pinch, double pinch, removed vendors) | 1258 lines changed: `face_sources`, `body_source`, `input_ids` per hand, detached controllers | Re-apply on the new file. **M** |
| `client_openxr/src/stream.rs`, `lib.rs`, `graphics.rs` | `last_good_video_frame_metadata` from the header, proximity, performance levels, openxr 0.21, API 1.1.36 | Re-apply the Vulkan session creation and the hand-driven motion. **M** |
| `client_core`: GL C API removed, HUD strings, height offset storage, `mdns-sd` dependency removed | `c_api.rs` 666 lines changed; **mDNS is mandatory** | Restore `mdns-sd`; re-apply the small bits. **S** |
| `panel.rs`, `lobby.rs`, brand assets, xtask loader cleanup | additions | Re-apply. **S** |
| Manifest | `min_sdk_version 28`, `CHANGE_WIFI_MULTICAST_STATE`, Android XR entries, `android-activity = "=0.6.0"` | Merge. **S** |

Decoding (MediaCodec, unchanged upstream), chroma key (upstream has the fix), controllers (same Quest profile; new `input_ids`
are filled by upstream code), head pose (the client no longer predicts; views from the header).

## 6. Migration plan

Branch `alvr-21` in this repo; client branches `alvr-21` in both forks. Pin the ALVR commit in `deps.lock.json` (a new
`builder_sources` entry; `setup_builder.ps1` and `stage8_host.ps1` take the tree name from it). Keep `main` and 1.x on v20
until the release.

| Phase | What | Gate | Can start |
|---|---|---|---|
| 0 Drift list | `cargo check` of `tools/host` and `tools/mock_client` against the pinned commit (the compiler enumerates the work); the session-key existence check (R9); the canted-vs-circumscribed area number (2.1). No engine edits. | The list exists; D1 to D6 decided. | now |
| 1 Host | 4.1 in this order: build integration and patches (4.3); mechanical API changes; `ClientConnected` payload and the host-owned stream file (D3); discovery cleanup; statistics replacement (R6); **host prediction** (R3) last, as its own measured step on the rig. | Builds; rig judge equal or better than the v20 build at equal settings; `e2e_baseline` numbers reproduced. | after the engine go-ahead |
| 2 Lab | Mock client port (or upstream `client_mock`), harness key renames, mDNS announce, pcap tool, fixtures. | The full suite green on builder loopback with protocol id 21 (36 of 41 passed on 2026-10-27 with v20 is the reference). | with phase 1 |
| 3 Clients | Vision Pro first (5.2), then Quest (5.3). Submodule / base bumped to the tag. | Both pair with the phase-1 host; `va_client_caps` shows the new fields. | **at the `v21.0.0` tag** |
| 4 Headset A/B | Same game, same settings, 1.x on v20 vs 2.0 on v21: Black Box latency table, FG regime switches, chroma edges, controllers (PSVR2 and Quest 3), the FoV ceiling, audio. | No regression on the measured columns; prediction verified with a moving head. | after 3 |
| 5 Release | AirFrameXR 2.0: host, launcher, both clients; release notes; `docs/UPSTREAM.md`, `docs/ARCHITECTURE.md` (pose path), `docs/progress.md`. The launcher's `headset_seen.compatible` message tells v20 users to update the headset app. 1.x zips stay published for v20 clients. | | after 4 |

Rollback is a version choice: the 1.x host and clients keep working with each other; nothing in the user's `airframexr.json`
changes meaning.

Out of scope for the migration (to keep it targeted): canted rendering in the shim, eye-tracked foveation, proximity-driven
idling, body tracking, the Linux path, re-vendoring the encoder (unless D6 says otherwise). Each is a separate measured step
after 2.0.

## 7. Decisions needed

- **D1** Target `21.0.0` stable only; develop on pinned master `adaa16ba` and re-pin at the tag. (Recommended.)
- **D2** Hard cut-over as 2.0 with both clients, no dual-protocol host. (Recommended; the alternative is two host binaries
  behind a launcher toggle.)
- **D3** The host owns the negotiated stream config in a host-owned file; GUIs and harness stop reading ALVR's cache.
  (Recommended; it also removes the GUIs' writes into ALVR's internals.)
- **D4** Host-side prediction policy: predict to the shim's display time; orientation first; measured on the rig before
  position and hands. Needs the engine go-ahead and possibly an IPC field.
- **D5** Keep the shim orthogonal: circumscribe canted eye poses in the host for 2.0; canted rendering later, on a measured
  pixel saving.
- **D6** Encoder C++: keep vendored v20 and feed it the server's aligned foveation parameters (recommended), or re-vendor from v21.
- **D7** Packet-loss metric replacement, or accept its loss in the network benchmark.
- **D8** Whether to adopt upstream `alvr/client_mock` for the lab instead of porting `tools/mock_client`.

## 8. Sources

- Upstream diff `v20.14.1..upstream/master` in `external/ALVR-master` (314 files, +17611 / −23708); key files:
  `alvr/packets/src/lib.rs`, `alvr/sockets/src/*`, `alvr/common/src/{version,inputs,c_api}.rs`, `alvr/server_core/src/{lib,connection,c_api}.rs`,
  `alvr/session/src/{lib,settings}.rs`, `alvr/events/src/lib.rs`, `alvr/audio/src/lib.rs`, `alvr/client_core/src/{lib,c_api,connection}.rs`,
  `alvr/client_openxr/src/*`, `alvr/graphics/src/*`, `alvr/server_openvr/src/{lib,foveated_encoding,vsync}.rs`.
- Upstream issues and PRs: #1722 (v21 release todos), #3426 (SteamVR compatibility), #3433 (decoder config in header),
  #3440 (AI-code sanitization), #3398 / #3414 (Linux direct mode), #2931 / #3212 (Windows freeze), #3412, #3420, #3421, #3431.
- Our code: `tools/host/src/{main,pipeline,logs,discovery,vision,nvidia_profile}.rs`, `tools/host/build.rs`, `nvenc/`,
  `tools/patches/`, `tools/remote/stage{8,9,13}_*.ps1`, `tools/gui/`, `profiles/`, `harness/`, `tools/mock_client/`,
  `external/alvr-quest` (`QUEST_VULKAN_PORT.md`), `external/alvr-visionos-pr` (`README.md`, `AGENTS.md`), `docs/UPSTREAM.md`,
  `docs/ARCHITECTURE.md`.
