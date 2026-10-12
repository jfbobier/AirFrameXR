# configure.exe as an auto-configure wizard: plan

Status: plan (2026-10-12), nothing built yet. Today `configure.exe` (tools/gui/Configure.cs, 460 lines, WinForms .NET 4.8 compiled by Roslyn) has three
tabs: 1. Headset (pairing), 2. Benchmark (encoder test, quality benchmark, network test, each a button), 3. Settings (all the controls).
It started as a benchmark that informs a configuration; it should become a wizard that produces one.

## What exists and can be reused
| Piece | Where | Reuse |
|---|---|---|
| GPU check | `alvr_host --system-check` -> `gpu_supported`, `reason` (NVML architecture >= Ada); `VISIONALVR_ALLOW_ANY_GPU=1` | step 0 |
| Discovery | `alvr_host --discover <s>` (mDNS for the Vision Pro, UDP 9943 for Quest-style), `Discovery` class in Common.cs | step 1 |
| Pairing | `Session.Pair` writes `client_connections` (+ `manual_ips`) in session.json, `headset.*` in visionalvr.json | step 1 |
| Encoder test | `alvr_host --live --bench 300 --encode-profile X` -> `bench` event (p50/p95, fits at 90 % of the frame time) | step 3 |
| Quality benchmark | `alvr_host --benchmark-quality` -> `bq_result` / `bq_summary` (three proposals) | step 3 |
| Network test | `alvr_host --benchmark-network 100,200,...` -> `bench_step` / `bench_result` (clean bitrate) | step 3 |
| Settings controls | `SettingsTab()` (resolution, fps, bitrate, foveation, audio, protocol, ...) | step 5 |
| Process/events | `HostProcess` (JSON events from a host child process) | all tests |

## The flow
0. **Gate.** `configure.exe` runs the system check first. Not NVIDIA, or older than the 40 series: a full-page "not supported" with the reason and the
   detected GPU, and only Exit. Override: `--allow-any-gpu` (configure.exe, VisionALVR.exe and alvr_host all accept it; same effect as the existing
   environment variable) for our 3080 Ti tests. Also show which adapter will be used (laptops with an iGPU + dGPU).
1. **Headset.** List of what mDNS/UDP finds plus the known ones, merged by hostname: the paired headset from session.json (`client_connections`,
   `manual_ips`) and `headset.ip` from visionalvr.json always appear in the list, marked "known" and, when mDNS has not seen it, with a reachability
   check (ping, then the ALVR handshake). The current bug is that the list only holds what discovery reported, so a headset with a fixed IP
   shows as paired but not in the list. Fix: seed the list from the known entries at start, de-duplicate with discovery results, preselect the known
   one, and show it in both places (selected + list).
2. **Handshake and what we learned.** Connect (the ALVR app open on the headset), capture the headset's advertised capabilities, show them, let the
   user override, then choose **Autoconfigure (recommended)** -> step 3 or **Manual / update existing** -> step 5 (prefilled from the existing
   session.json + visionalvr.json). Capabilities come from `VideoStreamingCapabilities` + `display_name` (server_core/src/connection.rs ~575). The
   current host never exposes them (only the negotiated result lands in `openvr_config`), so: a small server_core patch like
   `client-stats-log.patch` (`client-caps-log.patch`: one log line with the JSON), parsed in logs.rs into a `client_capabilities` event, and a new
   `alvr_host --handshake <s>` mode that connects, emits it and disconnects (no game, no SteamVR needed).

   | Field | Shown as | Used for |
   |---|---|---|
   | display_name, hostname, protocol id | headset name / vendor, a per-headset profile | defaults (the Vision Pro profile is the only tuned one) |
   | default_view_resolution | panel resolution per eye | resolution ceiling and the 1:1 point |
   | supported_refresh_rates | list | the display period the pipeline paces on (user picks) |
   | encoder_high_profile / encoder_10_bits / encoder_av1 / prefer_10bit / prefer_full_range / prefer_hdr / preferred_encoding_gamma | codec capabilities | shown; the encode path is fixed to HEVC, 10 bit unless the headset refuses it; gamma and range follow the headset |
   | supports_foveated_encoding | yes/no | whether the foveated layout is on |
   | microphone_sample_rate | Hz | the microphone path |
3. **Run the tests, with a checklist.** A list with an empty square in front of every item; each is ticked when done (X when failed, with the reason),
   current one marked running; Cancel and "Skip" per item; failures do not stop independent tests. All run through `HostProcess`:
   1. system check (GPU, driver, NVENC engines, power state; the existing laptop-on-battery warning)
   2. encoder speed at the handshake's resolution and rate, foveated (the real frame budget)
   3. quality benchmark (~1.5 min, picks preset / foveation geometry)
   4. network test (needs the headset): clean bitrate and loss
   5. pipeline test (needs the headset): the host's synthetic test pattern for ~20 s at the chosen rate; reads the headset's own statistics
      (decode, decoder queue, vsync queue, network, total, repeated frames) and the phase lock's convergence
   6. audio path check (default output device present, a virtual device if the user wants one)
   7. optional full-chain check (the bundled Littlest Tokyo probe through the runtime; needs the runtime registered)

   Results are saved to `config\autoconfig_results.json` so step 4/5 and support requests can use them.
4. **How your setup performs.** Each measured value next to a reference and a verdict (good / ok / poor):

   | Metric | Source | Reference (TBD, see below) |
   |---|---|---|
   | encode p95 vs frame budget | step 3.2 | < 70 % good, < 90 % ok |
   | clean bitrate, loss/s | step 3.4 | >= 200 Mbps at 0 loss good |
   | pose-to-display latency, repeated frames | step 3.5 | from the lab: stock ALVR + SteamVR on the same kind of link |
   | decode, network, queues | step 3.5 | ALVR's own statistics |

   Plus a one-line summary ("Excellent / Good / Limited") and specific advice when something is poor (6E access point in dumb-AP mode, PC on Ethernet,
   fixed headset IP, lower resolution). **Decision needed:** the reference values. Proposal: derive them from the lab comparisons (harness/compare.py)
   on the 5090 and the 3080 Ti, with stock ALVR + SteamVR as the "baseline" row.
5. **Proposed configuration.** The existing settings page, prefilled from the handshake and the tests, each value with a short reason (tooltip or a
   second column): per-eye resolution (<= panel, <= what the encoder sustains), refresh rate (highest supported that the encoder fits), bitrate (the
   network test's recommendation), foveation (supported + recommended), codec / 10 bit / gamma / range (from the capabilities), latency mode and phase
   lock (defaults from the lab), audio, idle colour. For "update existing" show what differs from the user's current values. Save writes session.json and
   visionalvr.json (unknown keys preserved) and offers "Save and start VisionALVR".

## Build order
| Phase | Content | Size |
|---|---|---|
| A | Gate (`--allow-any-gpu`), known-headset merge in the list (the IP bug), test with the screenshot mode | small |
| B | `client-caps-log.patch` + `client_capabilities` event + `--handshake`; the step 2 page with overrides | medium |
| C | Test sequencer: checklist control, `TestStep` runner over `HostProcess`, results file; wire the existing three tests, add the pipeline and audio checks | medium |
| D | Step 4 page: metrics table, thresholds file (`bench/reference.json`), advice text | medium (needs the references) |
| E | Step 5: proposer (`Proposal.Build(handshake, results)`), prefilled settings with reasons, update-existing diff | medium |
| F | Wizard shell: step list + Back/Next, replacing the tabs (keep "Advanced" with the old tabs for a while) | small |
| G | End-to-end in the harness: the mock client (`alvr_client_headless`, it takes `--refresh`/`--res`) plays the headset on loopback; a scripted wizard run per scenario checks every step's output | medium |

## Risks and open points
- The handshake needs the ALVR app open on the headset and the client trusted; on a first run the user may have to accept it. `--handshake` must not
  leave a connection behind.
- Quest-class headsets are not supported or tuned; the wizard shows what they advertise but only the Vision Pro profile is validated.
- Several tests need the headset; without it the wizard runs the no-headset subset and says what is missing.
- The reference values for step 4 are the one real unknown (see above).
- Laptops: GPU clocks and battery change encoder results; keep the existing clock-stability warning and show it in step 4.
- Keep the old three-tab configure.exe reachable (`--advanced`) until the wizard has been used on real headsets.
