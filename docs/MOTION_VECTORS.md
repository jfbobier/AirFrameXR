# Game motion vectors for frame generation (design note, 2026-10-14)

**Removed in 0.6.1 (2026-10-25).** The shim tap, the IPC fields, the host's vector passes, the launcher's tick and per-game DLL install,
the extractor DLL in the zip and the lab's vector scenarios are gone; the code is archived on branch `exp/dlss-vectors` (with the frame
pinning fix and `harness/mv_diag.py`). Why, from the evidence of 2026-10-24/25 (the last two sections below):

- Correct vectors barely beat our optical flow. In the Valor Mortis snapshots where the vectors belonged to their frame pair, their mean
  match error against the images was 5-10 % below the flow's (0.0190 vs 0.0202, 0.0158 vs 0.0176), sometimes above it (0.0278 vs 0.0263 in
  a fast turn); the edge synthesis, given the vector as one more candidate, found it better than every flow candidate on ~2.5 % of the
  motion-boundary pixels. The rig's exact vectors placed a fast clean marker 2x better than the flow, a case real DLSS vectors (render
  resolution, jittered) do not reach.
- Real games delivered them unreliably. The tap paired frames with another frame's vectors under VDXR's async submission (fixed by pinning
  at the swapchain commit, proven in the rig), and in Mortis gameplay the vectors still matched no frame pair after that fix (cause not
  found; one hypothesis: the engine's camera differs from the pose the headset is given).
- The cost: a GPL DLL in every zip and in game folders, incompatibilities (Luke Ross mods, anti-cheat), IPC and host passes, and a large
  share of the debugging time of 0.4-0.6. What it saved: the optical flow pass in DLSS games (~2.2 ms GPU per rendered frame on the 5090).

What follows is the history, kept as written.
