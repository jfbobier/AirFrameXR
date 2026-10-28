# FoV pipeline optimisation: idea, not built

Status 2026-10-28: **a note, nothing built or measured.** Follows `docs/FOV_CALIBRATION.md` (calibrated rectangle 0.71 / 0.59 / 0.61 / 0.57,
`docs/AVP_FOV.md`). Question: now that we know what the wearer sees, should the stream frame stop covering what they do not?

## Today (read in `ovrshim/driver.cpp`, `docs/ARCHITECTURE.md`; not re-measured)

- The headset declares a box D (about 0.94 per edge with the fork's visible limit, `visibleFovLimit`; the stock app declares 1.79 / 1.02 / 1.22 / 1.02).
  The stream frame, the composition target and the encode all cover D at the stream's eye size (3552 x 3200 per eye, 7104 x 3200 side by side).
- With the override (`fov_override`, the calibrated values) the game is told the rectangle R and renders only R: recommended size = stream eye size scaled by
  R's span per axis (`GameRenderSize`, render quality 0 = that floor). The shim maps the game layer into the D frame (`EyeHomography`); the rest stays black.
- R is about half the area of D (0.71+0.59)(0.61+0.57) = 1.53 against (0.94+0.93)(0.90+0.76) = 3.10: roughly **49 % content, 51 % black margin** in every
  streamed frame. Black pixels still cost: the composition pass at 7104 x 3200, every copy of the frame, frame generation's pass (2.3 ms at 7104 x 3200 on the
  3080 Ti, `performance-learnings` notes), NVENC time (per pixel, not per bit), the headset's decode.

## The idea

Make D = R (the client declares the calibrated rectangle: the `fov-calibration` client branch, commit `70e4f18`, does) **and** set the stream's eye size to R's
size at today's pixels per degree: about 2470 x 2275 per eye (computed: 1.30 / 1.87 x 3552, 1.18 / 1.66 x 3200; use even, encoder-aligned values, e.g. 2464 x 2272).
Same sharpness in what the wearer sees, about half the pixels (about 11 Mpx per frame instead of 22.7).

Expected, in theory only:

| Stage | Effect |
|---|---|
| Game render | unchanged (it already renders R at that size) |
| Composition, copies | frame is half the size; the homography becomes an identity |
| Frame generation | pass over half the pixels (about 1.1 ms instead of 2.3 on the 3080 Ti, if linear) |
| Encode / decode / network | half the pixels; at 275 Mbps about twice the bits per pixel (cleaner compression), or lower bitrate for the same quality |
| Headset decode | half the pixels; may help the headset's repeat rate (11.9 % typical in the 2026-10-28 log) |

Needs: the new headset app (client declares R), the stream eye size changed through existing settings (`transcoding_view_resolution`,
`emulated_headset_view_resolution`, profile `stream.eye_resolution`), the override off. **No change to the shim, host or IPC is expected**: with no restriction
the game's recommended size is the stream's eye size, which is what we want over R.

## Unknown / risks

- Never run at about 2464 x 2272: NVENC alignment, the `FullSplit` / `split_encode 3` profile at that size, the profile's `match.default_view_resolution`
  (3776 x 3648), GUI or harness code that assumes 3552 x 3200. A resolution scale of 1.21 once crashed the host (8576 px wide, NVENC limit 8192); smaller
  is the safe direction, but untested.
- Not on the dead-end list of `docs/PERFORMANCE_FINDINGS.md`; `docs/FOV_CALIBRATION.md` section 5 names "stream resolution" as a separate measurement, not done.
- R's corners (about 21 % of R) are encoded and never seen. Only the QP map (`video.qp_map`) could make them cheaper; no setting without a measurement.
- The calibrated numbers become load-bearing: anything outside R is black on the headset, and changing them needs a client build (or the live "Calibrated"
  re-declare of the calibration plan, phase 2). One wearer, one fit, no repeat-fit margin yet.
- The AVP's FoV Scale slider multiplies the declared tangents too; check how it interacts (declared smaller than R leaves a gap).

## Test plan

1. **Cost side, no new app build.** Current client (declares D about 0.94). On the 5090 run the same game / probe at 3552 x 3200 and at a reduced stream size
   (about 2464 x 2272) and read encode ms, decode ms, frame generation ms, repeated %, total latency from the logs (`docs/PERFORMANCE_KNOBS.md` for the reading).
   The picture on the headset is stretched in this test, only the costs are valid.
2. If the saving is real: **geometry**, with the client branch built (Mac CI / TestFlight): D = R, reduced stream size, override off. Check with
   `fovgrid.exe` / the outline mode (`XR_PROBE_FOVGRID=outline`, `XR_PROBE_RECT=0.71,0.59,0.61,0.57`) that the rectangle sits at the visible edge, then one real
   game (HUD corners, intro screen).
3. Decide what to spend the saving on: lower bitrate, higher pixels per degree, or less encode time. A/B on the headset before keeping any of it (measurements
   beat intuition, `docs/DEVELOPMENT_PHILOSOPHY.md`).

State of the branches that this builds on (2026-10-28): streamer `fov-calibration` (profile and docs carry the calibrated values), client `fov-calibration`
(constants set, not built), personal zip `AirFrameXR-1.1.1-pre-personal.zip` on the 5090 with the override set; the install on the 5090 has the override
0.71 / 0.59 / 0.61 / 0.57. Nothing pushed.
