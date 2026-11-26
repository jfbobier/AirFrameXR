# Ideas from other streamers, read against what we know

Written 2026-11-26. A survey of how other wireless VR and game streamers time their frames, and what each idea would mean for this
project given what is already measured here. Nothing in this document is built or tested by us. It is input for the frame timing
experiment (`docs/PLAN_E2E_FRAME_TIMING.md`), not a plan of its own.

Left out on the owner's decision: eye-tracked foveation (one line in section 5), and the Wi-Fi channel advice for Apple devices
(the owner's access point is already on channel 149 for that reason).

## 1. Sources, and how far to trust each

| Source | What was read | Trust |
|---|---|---|
| **WiVRn** (github.com/WiVRn/WiVRn, commit 2529e0bf) | the code of both ends: pacer, clock offset, packets, client pick, encoders | high: rules and constants read in code |
| **Steam Link**, through **OpenVRLink** (github.com/shinyquagsire23/OpenVRLink) | the authors' reverse-engineering notes (`docs/`) and their Swift client | medium: not Valve's code; nothing of the PC side; their main capture ran in an emulator on a wired LAN |
| **Moonlight / Sunshine** | the public code of the pacer, the video queue and the sender | high for what the code does |
| **NVIDIA CloudXR** | the public web client's bundle (message definitions, defaults), official docs | medium: the definitions exist, their use by native clients is not shown; no algorithm is published |
| **ClearXR** (github.com/clear-xr) | both repositories | high, and empty: a thin wrapper around NVIDIA's closed runtime and Apple's closed framework |
| **Meta Link / Air Link, Virtual Desktop, GeForce NOW, Parsec** | engineering blogs, documentation, release notes | low on pacing: little is public |
| **Patents** (Sony US10974142B1 and US11110349B2, NVIDIA US11700402B1, AMD US12170801B2) | the claims | a design on paper, not proof that it ships or works |
| **oxrsys** (an independent Vision Pro streaming client, PR 47) | one pull request with measurements | one developer's numbers |

## 2. What we know here that every idea must be read against

From `docs/PLAN_E2E_FRAME_TIMING.md` (sections 2 and 10.1), `docs/PERFORMANCE_FINDINGS.md` and `docs/VISIONOS_TELEMETRY_LEARNINGS.md`:

- **Where the time is** (5090 + Vision Pro, 100 Hz, Dead Space x2): game 6.3 ms, encode 3.0, decode 6.0, a decoded frame waits about
  10 ms for the client's pick, client render 6.7, GPU done to display 20.5. Apple's own part, rendering deadline to display, was
  measured at about 17.7 ms: at most 3 ms of the last leg is ours.
- **Finding A, a standing second frame.** In the saved field logs 30 to 54 % of frames wait more than a period, in runs of up to 288
  frames. The client picks the oldest queued frame and never drops one (catch-up off since 1.3.9), so a second frame stays once it
  is there. Ending those runs is worth at most 1.8 to 5 ms of mean wait.
- **Finding B, the phase lock cannot reach its target.** `phase.rs` folds the arrival phase at half a period, so a true 7 ms reads
  -3 ms. The lock slides the host's grid earlier for ever (-0.2 to -0.7 ms/s in all 19 field logs), the host delivers about 0.05 %
  more frames than the display shows, and that surplus is what stands in the queue. The effective margin in the field is 0 to 4 ms,
  not 7.
- **The grid's period is not the display's**: 10.0 ms (or the rate estimator's sticky 10.0145 ms) against a measured 9.9941 to
  10.0009 ms, and the two clocks differ by 30 to 180 ppm.
- **The arrival spread is 8 to 9 ms of a 10 ms period.** With frame generation the two kinds of output differ: rendered ones are
  ready 2.6 to 3 ms after the boundary, generated ones 3 to 8 ms (6 to 10 ms without the early hand-over).
- **The early hand-over** cut the host's late outputs (4.0 -> 0.7 %) and the headset counted nothing of it: a fixed margin cannot
  turn a narrower spread into latency.
- **Repeated display periods are about 1 %** and the owner's link loses no packets (0 in every window of 2026-11-25).
- **What failed before, and stays out**: touching the tracking-sample path (re-stamping, predicted samples) trembled on the Vision
  Pro; the Auto controller of October oscillated and was removed; "newest frame wins" threw away 15 to 30 % of frames in GPU-bound
  games; the 1.3.8 catch-up rule cost 1.1 points of fresh frames.
- **Fix candidates already on the experiment branch** (`exp-frame-timing`): lock v2 (the wait without the fold, plus a rate term),
  lock v3 (steered by the client's signed slack), `catchup = sustained`, a wait that ends at the latest safe pick.

## 3. The design the others converge on

WiVRn (code), Steam Link (notes) and CloudXR (message definitions) do the same four things. The patents describe them too.

### 3.1 A target display time on every frame; the client shows the nearest, not the oldest or newest

- **Others.** WiVRn stamps each frame with the display time it is meant for, in the headset's clock, keeps three decoded frames,
  and at each tick shows the one whose target is nearest (`client/scenes/stream.cpp`). Steam Link does the same, deletes older
  frames and holds one that is more than half a period early (`docs/svl-video/decode-handoff.md` in OpenVRLink). CloudXR's
  feedback message carries a target and an actual display time.
- **Here.** Our pick is "the oldest queued frame", which is exactly what lets a second frame stand (Finding A). A pick by target tick
  cannot keep a standing frame: a frame whose tick has passed is skipped, a frame that is early waits its turn. It replaces the
  `sustained` heuristic (wait for N picks at depth 2, then drop) with a rule that has no N.
- **We may not need a new field on the wire.** A frame already carries the headset-clock timestamp of the tracking sample it was
  stamped with, and that timestamp is the presentation time of the tick the sample was taken in. So "target tick = frame timestamp
  + L periods" needs one number, L, which either the streamer sends once by message or the client learns as the smallest distance
  seen over the last half second. This is my inference from the code; it needs a check that the timestamps step by exactly one
  period per output in every mode (generation off, x2, x3, a boundary without a fresh sample).
- **What it would also steady.** The total latency flips between two values a period apart (57 / 67 ms) because the same frame can
  be shown on one tick or the next. A pick by target tick makes that distance constant. The Vision Pro is known to tremble when the
  staleness of the declared pose alternates, so a constant distance is in the right direction; whether it is visible is unknown.
- **Risk.** In the Steam Link capture 13 % of decoded frames were never shown and frames waited 15 ms for a slot. That is an
  emulator on a LAN, so it proves nothing either way, but a nearest-match pick can discard more than ours. Count frames never shown.
- **Test.** The lab rig runs the real `EventHandler.swift`: a third value for the `catchup` switch (`target`) against `off` and
  `sustained`, on the rig's stall scenario that reproduces Finding A.

### 3.2 Lock to the headset's tick, and keep the margin out of the fold

- **Others.** WiVRn's server tracks the instant of the headset's render tick from the client's reports, wraps only that phase into
  plus or minus half a period (gain 0.1), and subtracts the margin afterwards (`server/compositor/pacer.cpp`). A margin above half a
  period is therefore representable. Valve's client still has a loop with our fold; current hosts no longer act on it.
- **Here.** This is Finding B's cause, seen from the other side: we fold the quantity that contains the margin. Lock v2 (no fold) and
  v3 (signed slack) already remove the fold. WiVRn confirms the direction and adds one rule worth copying: reports from frames that
  were shown more than once, or never shown, are left out of the statistics. Our input today reads the wrong way in exactly those
  cases (a frame caught by the wait reports "on the tick", a frame behind another reports only its excess).

### 3.3 The margin is a measured percentile, not a number of milliseconds

- **Others.** WiVRn: the 99.5th percentile of "scheduled send to decode done" over the last 5,000 frames, plus 1 ms, recomputed
  every 100 frames. NVIDIA's patent: the 99.7th percentile of transmission plus presentation wait. Sony's: the offset at which
  about 99.99 % of frames make the next tick.
- **Why it cannot oscillate.** The statistic is the transit time of the pipeline, which does not depend on where in the period the
  host aims. Setting the lead from it is not a feedback loop on the thing being steered. That is the difference from the October
  Auto controller and from a margin that reacts to late frames.
- **Here, without a shared clock.** The client's signed slack (latest safe pick minus decode done, for the tick the frame was meant
  for) is that same statistic seen from the headset, and it needs no clock offset. Lock v3 already steers on it. Two changes would
  make it WiVRn's rule: steer a high percentile of late-ness (the share of frames allowed to miss) instead of a level in
  milliseconds, and make the slack unambiguous with the target tick of 3.1.
- **What to expect, honestly.** The rule does not create a gain. It gives the smallest wait for a chosen share of late frames. With
  an 8 to 9 ms spread, the 99th percentile sits 5 to 6 ms above the mean transit, so the mean wait would be about 5 to 6 ms at
  today's 1 % of repeats, against 10 ms now. Most of that difference is the standing frame of Finding A, already counted in the
  plan's upper bound. The new part is that **any narrowing of the spread becomes latency by itself**: this is what would let the
  early hand-over, a lower bitrate or a paced send show up at the headset, which the fixed margin prevented.
- **Our rule against automatic modes.** This is a statistic with one fixed parameter (the allowed share of late frames), not a mode
  the user sees. If the experiment finds one constant that suits every game, the constant ships and this stays in the drawer.

### 3.4 Lock the rate, not only the phase

- **Others.** Sony's patents tune the server's frame period until the client's decode-ready times stop drifting. A merged Sunshine
  fix (PR 5282) describes the symptom of a 0.1 % mismatch: the client's queue grows and shrinks by one frame every 8 seconds.
- **Here.** That is our measured state: the grid at 10.0 ms against a display at 9.9941 ms, a slide of half a millisecond per
  second, a surplus frame every 20 seconds. The plan's v2 adds a rate term to the lock. The lab already showed that integrating on
  the folded estimate runs away.
- **A safer form (my proposal).** The host receives the headset's tick times with every tracking sample, on the headset's clock, with
  an exact cadence; the plan measured the display period from them to four digits. A straight-line fit of arrival time against
  those timestamps over a minute gives the display period in the host's clock to a few parts per million, with no loop at all. Feed
  that period to the grid, and leave the lock a slow phase trim. To check: the fit's noise under real Wi-Fi bunching (tracking
  samples arrive with a p95 gap of 11 to 18 ms), and that the sticky rate estimator in `vision.rs` is what it replaces.

### 3.5 A clock offset between PC and headset

- **Others.** WiVRn pings over UDP (100 samples, drops any with a round trip over three times the mean). Steam Link uses no extra
  messages: both ends keep the minimum of "arrival minus send" on timestamps already in the traffic and exchange their estimates.
  CloudXR's server stamps its frame start and pose receipt in headset time in each frame.
- **Here.** ALVR v20 has none, which is why its "network" figure is a remainder and not a measurement. But section 3.3 shows the
  margin does not need it. A clock offset would buy diagnostics: the real transit, split into send, air and decode, per kind of
  output. Steam Link's form fits us best, since frame timestamps and tracking timestamps already ride on UDP both ways and the
  estimates can travel once a second on the existing message channel.
- **Verdict.** Worth adding to the experiment's telemetry if cheap, because it finally measures the network leg. Not a prerequisite
  for anything else.

### 3.6 A report per frame, with several timestamps

- **Others.** WiVRn: ten timestamps per frame from encode start to display, plus how many times it was shown, sent after the client's
  submit so the report can never delay a frame. Steam Link: four timestamps, repeated four times so they survive loss.
- **Here.** The plan's `va_time` (percentiles per 10 s) and `va_slack` (once a second) carry what the experiment needs, computed off
  the render thread. A per-frame report is only needed if the per-kind split of section 4.1 has to be measured on the headset.

## 4. Other ideas, each against our facts

### 4.1 Different lead for generated and rendered outputs (derived here, not found elsewhere)

One lead has to cover the slower kind. Generated outputs are ready up to 5 ms later than rendered ones, so every rendered output
waits that much longer at the headset than it needs. With a target tick per frame the host could start a generated output's work
earlier than the grid boundary instead: the pair it interpolates already exists, and the early hand-over is what makes the frame
available early. Expected: the two kinds arrive with the same slack, and the spread the margin must cover shrinks toward the
narrower of the two. Unmeasured; the host's per-kind "ready after the boundary" lines exist to size it.

### 4.2 A standing-queue trim (Moonlight)

Moonlight's pacer keeps half a second of queue depth and trims to one frame only if the depth never fell to one in that window. It
is `catchup = sustained` with N = 50 picks at 100 Hz; the plan starts its search at N = 10. Take 50 as the other end of the range.
If 3.1 works, this rule is not needed.

### 4.3 Frames in flight (oxrsys)

One Vision Pro client gained about a frame by holding two frames in flight instead of three, and measured 20 to 30 ms from decode to
display. Our queue holds at most two decoded frames and Apple's part is the measured 17.7 ms. So the only frame we hold too many is
the standing one of Finding A. Nothing further to gain here; it confirms the floor.

### 4.4 Release the game from its measured CPU and GPU time (WiVRn)

- **Others.** WiVRn wakes the game at "display time minus smoothed CPU and GPU time minus 1.5 ms", and at once when the game itself
  is the bottleneck.
- **Here.** Our just-in-time rule acts only while the GPU is still busy, so it held nothing in a game at its cap (Dead Space x2) or
  in Unreal games (Gears: release to submit as long as the GPU frame). WiVRn's rule covers the capped case: Dead Space's frames
  arrive about 6 ms before the boundary that uses them. It would do nothing for Gears either.
- **Verdict.** It shortens the age of the pose the game rendered with, which the headset does not count and rotation correction
  hides. A late release that misses its boundary costs a repeat. Low priority; it belongs with the parked just-in-time work.

### 4.5 No queue in front of the encoder (WiVRn drops instead)

We tried the equivalent ("newest frame wins") and it discarded 15 to 30 % of frames in GPU-bound games. The in-order queue and the
intake gate are measured choices. No.

### 4.6 Loss recovery without IDR spikes

- **Others.** Moonlight asks the encoder to invalidate the lost reference frame before it ever asks for an IDR. Steam Link sends
  light parity always (8 data + 2 parity packets, interleaved), a budgeted selective retransmit, and an IDR only on a counter.
- **Here.** ALVR v20 answers a lost packet with an IDR request and has no parity or retransmit (read in `client_core`). NVENC
  supports reference invalidation. The owner's link lost no packet in any window, so this is for testers' networks.
- **Verdict.** Later, and only after counting IDRs and lost packets in tester logs.

### 4.7 Packet pacing inside a frame

- **Others.** Sunshine and the GeForce NOW client send in batches against a per-millisecond budget. Steam Link sends a frame's
  packets back to back.
- **Here.** ALVR sends one blocking call per packet on a thread of default priority, and how long a frame takes to leave the host has
  never been measured. Pacing spreads a frame out, which adds latency unless it prevents queuing or loss, and we have no loss.
- **Verdict.** Measure first: first-to-last packet time per frame on the host and on the client. That also settles whether the
  7.6 ms "network" leg is air time or something of ours. Add it to the experiment's lines.

### 4.8 Extrapolation on the headset for short bursts of late frames (Meta Air Link)

A new feature on the client, not a tuning. Our client already reprojects the last frame on a late tick. Only worth a thought if the
experiment's count of hitches (runs of three or more non-fresh ticks) shows bursts are what the owner sees. No for now.

### 4.9 Bitrate that backs off on late presentation (Steam Link's key names)

An automatic mode on a setting the owner sets by hand. The measured lever is known: 150 instead of 275 Mbps saves about 4 ms of
decode and remainder. No.

### 4.10 Prediction horizon from the measured loop

CloudXR derives it from the measured pose-to-render latency, Steam Link's host extrapolates to the display time the client
reports, WiVRn's server tells the headset which horizons to sample. This is the tracking-sample path, which is out of bounds here.
A constant tick distance (3.1) would steady the horizon as a side effect. No change proposed.

### 4.11 Decoder and radio settings

Already in place: Apple's low-latency streaming entitlement in our Vision Pro client; the low-latency Wi-Fi lock, the maximum
operating rate and Qualcomm's low-latency decoder key in the Quest client. Apple documents its decoder session as real-time by
default. Nothing to add.

### 4.12 Dropping to an integer fraction of the display rate when the game cannot hold it (CloudXR)

That is our x2 / x3 frame generation, chosen by hand. Already there.

## 5. Not pursued

- **Eye-tracked foveation** (Steam Frame, WiVRn, Apple's foveated streaming framework): out of scope by the owner's decision.
- **ClearXR**: nothing of its own on timing.
- **CloudXR's dejitter profiles** (fast, smooth, disabled): names only, no algorithm published.

## 6. Summary

| Idea | From | Meets which fact | Verdict |
|---|---|---|---|
| Target tick per frame, client shows the nearest | WiVRn, Steam Link, CloudXR | the standing second frame (A); the 57 / 67 flip | **test in the rig first**, as a third `catchup` value |
| Lock on the tick, margin outside the fold, repeats left out of the statistics | WiVRn | the fold (B) | **adopt in lock v2 / v3**: the "leave repeats out" rule is the new part |
| Margin as a percentile of signed slack | WiVRn, patents | the fixed 7 ms; the hand-over's unused gain | **test** as lock v3's target |
| Rate from the headset's tick timestamps, fed forward | Sony, Sunshine | grid 10.0 against display 9.9941 ms | **test** in place of v2's rate term |
| Clock offset from timestamps already in the traffic | Steam Link | "network" is a remainder | add to telemetry if cheap |
| Different lead per kind of output | derived here | generated outputs up to 5 ms later | size it from existing host lines, then decide |
| First-to-last packet time per frame | Sunshine, Steam Link | the unmeasured send path | **measure** in the experiment |
| Standing-queue trim over half a second | Moonlight | A | a value for N (50); not needed if the target tick works |
| Game release from measured CPU and GPU time | WiVRn | capped games; parked just-in-time | later |
| Reference invalidation, parity, retransmit | Moonlight, Steam Link | IDR on every loss | later, with tester evidence |
| Burst extrapolation, automatic bitrate, drop before encode, prediction horizon | Meta, Steam Link, WiVRn, CloudXR | features or paths we closed by measurement | no |

**What the first four add up to, if they work:** a decoded frame's mean wait of about 5 to 6 ms instead of 10 at the same share of
repeated ticks, which is the plan's existing upper bound reached by rules instead of heuristics, plus a chain in which a narrower
spread turns into latency by itself. They do not touch Apple's 17.7 ms.
