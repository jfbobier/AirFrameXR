# visionOS client work (alvr-visionos)

Status 2026-10-14. Everything here is **compile-verified only** (GitHub Actions, Xcode 27.0, visionOS 27 SDK).
Nothing has run on a headset yet. Where this note says "verified" it means read in the code, the SDK headers,
or measured in our own logs; where it says "hypothesis" it is not proven.

Working copy: `external/alvr-visionos-pr` (gitignored; the older `external/alvr-visionos` is an untouched
CRLF copy of upstream main, ignore it).

## 1. Repo / branch layout

- Upstream: `alvr-org/alvr-visionos` (no write access). Fork: `jfbobier/alvr-visionos` (public, Actions enabled); fork `main` = upstream `main`, nothing else is ever committed to it.
- Remotes in the working copy: `origin` = fork, `upstream` = alvr-org. Fork `main` is fast-forwarded to upstream.
- One branch per concern, all compile-checked by CI on every push:

| branch | what | PR-ready? |
|---|---|---|
| `ci/github-actions-build` | workflow: Rust client core -> xcframework -> `xcodebuild` (unsigned) on the `xcode-27` runner, uploads `ALVRClient-unsigned.ipa` | yes (small, useful upstream) |
| `feat/psvr2-home-button` | PS/Home button reaches games on visionOS 27 | after a device test |
| `diag/pose-timing-log` | opt-in pose/timestamp diagnostics (see 2) | maybe; it is a debugging aid |
| `exp/reprojection-depth` | focal-depth reprojection experiment, off by default | no, experiment |
| `integration` | ci + the three above merged (runtime toggles, Debug-ish) | never; superseded by `telemetry-tuning` |
| `telemetry-tuning` | everything above + compile-time `ALVR_TELEMETRY` schedule/telemetry + 3 unsigned Release variants. Telemetry and performance tuning live ONLY here, never in `main` or PR branches | never (it may be scrapped from PRs) |
| `probe/sdk-api` | throwaway: dumps SDK headers in CI | never |

- **PR workflow.** Develop on branches based on the CI branch (so CI runs). To send a branch upstream, strip
  the CI commit: `git rebase --onto upstream/main ci/github-actions-build feat/psvr2-home-button`, push to the fork,
  open the PR with `gh pr create --repo alvr-org/alvr-visionos --head jfbobier:feat/psvr2-home-button`.
  Open the CI workflow as its own PR. Keep `main` on the fork equal to upstream (`git merge --ff-only upstream/main`).
  Upstream's `AGENTS.md` asks for short `fix:`/`perf:` prefixes and a note on user-visible changes.
- **Getting a build on the headset without a Mac.** Run the workflow, download the `ALVRClient-unsigned-ipa`
  artifact, re-sign with your own Apple developer certificate and provisioning profile (Sideloadly or similar;
  not tested for visionOS 27). Official TestFlight/App Store distribution stays with the ALVR maintainers.

## 2. Stamping bug on the Vision Pro: what is verified, what is not

Verified in the client code (`WorldTracker.sendTracking`, `RealityKitClientSystem.renderFrame`, `Renderer.swift`,
`ALVR/alvr/client_core/src/lib.rs`):

1. **The stamp is the key to a pose history.** `client_core.send_tracking` stores `(reported_timestamp, pose)` in
   `head_pose_queue` (1024 entries); `report_compositor_start(ts)` finds the pose by **exact timestamp equality**
   (first match, fallback `last_good_head_pose`). The frame's declared pose is whatever the client sent for that stamp.
2. **The stored pose is already extrapolated.** `send_tracking` predicts the head by the average pipeline latency
   (capped 90 ms / `max_prediction`) using the velocities the visionOS client computed by finite differences, and stores/sends
   that predicted pose. The pose a frame carries is therefore a ~100 ms guess, and the prediction horizon follows the
   latency the stats report. A stamp that shortens reported latency shortens the horizon.
3. **Declared timestamp != pose time when Apple refuses to predict.** The client asks `queryDeviceAnchor` at the
   display time and, on nil, walks back in 5 ms steps (up to 100 ms), then falls back to now. The pose of the
   walked-back time is sent under the unwalked stamp, and `client_core` extrapolates from the stamp as if the pose
   were taken then. How often this happens is unknown (the diagnostic measures it).
4. **Reprojection is rotation-only.** Metal path: `Renderer.updateGameStateForVideoFrame` zeroes both translations
   and draws the frame quad at z = -500. RealityKit path: the frame is a plane at `rk_panel_depth` = 80 m placed at the
   frame's pose. Either way head **translation** between the frame's declared pose and the display pose is not
   corrected for nearby content. A frame-to-frame change in staleness therefore shows as a change in parallax error.
5. **Your setup is the RealityKit path** (renderer toggle on). Correction to an earlier note: chroma key is NOT RealityKit-only; on visionOS 2+
   `Renderer.swift` enables it on the Metal path too (`if #available(visionOS 2.0, *) { chromaKeyEnabled = settings.chromaKeyEnabled }`),
   so the Metal renderer is a ready comparison (`telemetry-metal` build). In the RealityKit path, the timestamp is `CADisplayLink.targetTimestamp + vsyncLatency` where `vsyncLatency` is `rkAvgRenderTime`
   rounded up to whole vsyncs and can flip between 1, 2 and 3 periods.

Measured in our logs (`logs/pilot3/*/tracking.csv`, Vision Pro, 90 Hz):

- **Refuted:** the client does not emit duplicate or backward stamps. Steps are exactly 11.11 ms in 99.9% of samples
  (a handful of 22 ms, 100 ms at session start), 0 duplicates, 0 backward. The duplicates we see on the headset
  come from the game reading its pose in bursts, as already noted in PERFORMANCE_FINDINGS.
- Arrival spacing is jittery (p05/p50/p95 about 5.6/11.1/16.5 ms) while the stamps are a perfect grid: the stamp
  is a target display time, not a sample time.
- Head motion per 11 ms sample: translation p50 0.07 mm, p95 1-5 mm, p99 2-7 mm; rotation p95 0.1-0.9 deg.
  A one-sample staleness change is therefore 1-5 mm of uncorrected translation at p95: 0.06-0.6 deg of parallax at
  0.5-1 m. Same order as the rotation steps, which *are* corrected.

**Ranked hypothesis.** Host-side re-stamp (one sample ahead + a rotation-only warp) alternates the declared pose between
"real sample" and "next sample". Rotation is corrected twice (host warp, client reprojection); translation is
corrected by neither, so its residual alternates by up to one sample of head translation. The Quest's runtime
may reproject with a nearby focal plane (not verified), which would hide this; the visionOS client cannot. This fits "clean on Quest, tremble
on Vision Pro, clean with the shim-side re-stamp (constant staleness)". Alternatives not excluded: the walk-back in 3,
the latency-driven prediction horizon in 2, noisy finite-difference velocities.

**How to settle it (one session, same game and head motion):** build `integration`, enable Settings > "Log pose
timing". Each second the host log gets one `[pose-timing]` line (client log lines are mirrored by
`ClientControlPacket::Log`); per-frame rows are in `Documents/pose_timing.csv` (Files app).
Fields: `repeat` repeated stamps shown, `nosend` stamps with no client send record (lookup miss), `walkback` how often and how far
Apple's anchor query was walked back, `extrap` how far the client's predicted pose is from the sampled one,
`comp` what the reprojection must hide (deg = corrected, mm = **not** corrected), `dcomp` frame-to-frame change of
that residual (the tremble metric), `ctrlL/R hz` controller anchor cadence.
Then A/B: re-stamp off vs host-side re-stamp, each with Reprojection focal depth = Default vs 2.5 m.
- If `dcomp mm p95` jumps with host-side re-stamp and the 2.5 m setting removes the tremble: hypothesis confirmed.
- If `walkback` is frequent (>few %) or `nosend` > 0: the bug is in 3 / the lookup, fix there first (pose
  advanced by velocity from the walked-back time to the stamp).
- If neither moves: look at `extrap` (prediction horizon) next.

**Streamer-side mitigations while the client is not fixed:**
1. Use the shim-side re-stamp (constant staleness) as the Vision Pro default; it was clean on both headsets. Its cost
   (Wi-Fi jitter moves into arrivals) is the known trade.
2. If host-side re-stamp comes back, gate it on motion: re-stamp only when the translation delta between the two
   samples is below ~1 mm (head near still) and never when it is larger; the data above says that is the majority
   of samples. The threshold should come from `dcomp mm` measured on the headset.
3. Add translation to the host warp when depth is available (full reprojection), not only rotation.

## 3. PS / Home button (visionOS 27)

Read from the visionOS 27 SDK headers (`GCControllerHomeButtonSettingsManager.h`, via the CI probe):

- Opt-in is **per element**: `controller.physicalInputProfile.buttons[GCInputButtonHome].preferredSystemGestureState = .disabled`
  (`GCSystemGestureStateAlwaysReceive` is deprecated for it).
- It only takes effect when the **user's "in-app action" for the controller Home button is "Defer to app"**
  (Settings, visionOS 27). The app cannot force it. `GCControllerHomeButtonSettingsManager` (visionOS 27+) reads the
  setting (`controllerHomeButtonInAppAction` -> `.defer` / `.systemDefault`), publishes changes (`settingsUpdates`)
  and can open the right Settings page (`openControllerHomeButtonSettings(for: .customizeInAppAction)`). It only works
  while a controller is connected. The `.disabled` *values* of the setting enums are macOS-only; they are not what we need.
- `feat/psvr2-home-button`: claims the button by default (new setting "Send the controller PS/Home button to games"),
  shows status and an Open Settings button, maps PS -> system click, Create/Options -> menu click. Until the button is
  known to arrive, the menu button keeps doubling as the system click (old behavior).
- **Untested on device.** The PSVR2 element name `"Button Home"` is assumed (it is the Apple constant); if the button
  does not appear in the input state, the PSVR2 element list needs a one-time dump. Whether the PS button then
  shows up on both hands is also unverified.
- Streamer side: PS now arrives as the `system` click of each hand; the runtime has to expose that to games.

## 4. 90 Hz controllers and head pose

- Apple's WWDC26 session ("Build next-generation experiences with visionOS 27") only says accessories are tracked "at the
  highest possible frequency ... matching the display's native refresh rate"; the 90 Hz figure for accessories is from
  press coverage, not from Apple. It names **no new API** for it, so this is a cadence to measure, not a switch.
- The client already consumes `AccessoryTrackingProvider.anchorUpdates` and calls `predictAnchor(for:at:)` on every
  tracking send, so a faster update stream shortens the extrapolation without code changes. The diagnostic prints the
  real anchor update rate (`ctrlL/R hz`, p95 gap).
- visionOS 27 adds `GCSpatialAccessory` (new type, notifications, `input`, `haptics`). Whether PSVR2 Sense moves from
  `GCController` to it on 27 is unknown; the client currently finds them via `GCController.spatialControllers()`.
  Check the controller list on a 27 headset before building on it.
- No documented change for head pose; the 90 Hz in Apple's transcript is hand tracking. Nothing to switch for the head.

## 5. Other client improvements seen (not implemented)

- Frame pick spins on `sched_yield()` for up to 5 ms (Metal) / 1 ms (RealityKit) per frame; a semaphore signalled by the
  decoder callback would remove the CPU burn and the wakeup jitter.
- Both renderers consume frames oldest-first (`removeFirst`); with 2+ queued, dropping to the newest trades a frame for
  latency (matches our "standing queue" findings; coordinate with the host pacing before changing).
- RealityKit `vsyncLatency` flips between whole vsyncs from an EMA of render time; a hysteresis would make the stamp
  grid steadier.
- Velocity for the head is a finite difference of two Apple predictions; Apple's `DeviceAnchor` velocity could replace it
  if it proves less noisy (test with the diagnostic first).
- Per-frame `viewTransforms` (pupil swim) are still sent once; frames use the first.
- Foveated data outside CloudXR (out of scope for now, noted in the plan).

## 6. Telemetry session (branch `telemetry-tuning`, builds from CI)

Three unsigned Release `.ipa` (release page of the fork, drafted 2026-10-14): `telemetry-rk`, `normal-rk`, `telemetry-metal`.
All: no entitlements (low-latency-streaming and the app group are absent from the bundle, CI fails if the strings appear),
no ReplayKit eye-broadcast extension (it needs the app group), distinct bundle ids (`alvr.client.vatelrk`, `.vanormal`, `.vatelmetal`)
so they coexist with the official app, fresh-settings defaults = chroma key on (pure green) + renderer per variant.
`ALVR_TELEMETRY` compiles the whole telemetry module in (`PoseTimingLog.swift`); the normal build contains none of it.

Schedule (client side; the streamer follows the same numbering, started by the `[pt] start` line): warm-up 120 s, then 20 segments
of 45 s: seg 1-12 focal depth {default, 2.5 m} x streamer re-stamp {off, shim, host} (two cycles, reversed), seg 13-20 frame pick
{oldest, newest} x re-stamp {off, shim} (two cycles, reversed). Total 17 min. The client announces `[pt] start/cond/sum/rows/end` lines
at ERROR level (the default `client_log_report_level` drops lower levels). Runs once per app launch (restart the app to run it again);
a stream gap > 15 s restarts an unfinished run.

Known limits: nothing here has run on a headset; free-account signing drops the entitlements, so network behaviour (AWDL) may differ
from the official build (router on channel 44, Handoff/AirDrop off).
