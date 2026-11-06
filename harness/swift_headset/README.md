# Swift headset (the visionOS client's own code as a lab headset)

A Vision Pro stand-in that runs the client's **real** Swift code with the **real** `client_core`, so the chain can be checked end to end
with our visionOS client's behaviour instead of the Rust mock's. Built 2026-11-05 on WSL; **not yet run against a streamer** (the
builder was out of reach that evening).

| Part | Source |
|---|---|
| `EventHandler.swift`, `FFR.swift`, `Settings.swift`, `Extensions/Codable.swift`, `Extensions/Drawable.swift` | the client fork at a git ref (default `airframexr-client`), read with `git show`, compiled unchanged |
| `client_core` | the fork's ALVR submodule at that ref (Rust), built for this OS with `tools/patches/loopback-client.patch` (`ALVR_BIND_IP`) and `patches/no-pipewire.patch` (Linux: the server-side PipeWire bindings do not build on current headers; the client needs none) |
| C header | cbindgen of that `client_core` (the fork's `build_and_repack.sh` command) |
| Apple frameworks | the protocol harness's `AppleShims.swift` (same ref) + `Shims/HeadsetShims.swift` (simd pieces, inert Metal constants, `LayerRenderer.Drawable` with a projection built from the given frustum) |
| Render loop | `Headset/main.swift`, mirroring `Renderer.renderFrame`'s protocol part: frame pick, the view declaration (computed by the client's own `Drawable.swift`), tracking per presentation once the views are declared, submit report |
| Decoder | none: the access units go to the stream dump (`--out`), every frame counts as decoded |

```
harness/swift_headset/build.sh [--ref airframexr-client]     # -> harness/cache/swift_headset/swift_headset
python3 harness/swift_headset_check.py                        # build + offline checks (no streamer)
harness/cache/swift_headset/swift_headset --declare-selftest  # what the client declares for a frustum, both projection conventions
```

Command line: the Rust mock's flags where they mean the same (`--report`, `--frames-csv`, `--poses-csv`, `--out`, `--seconds`,
`--connect-timeout`, `--exit-after-frames`, `--min-frames`, `--yaw-amp`, `--yaw-hz`, `--height`, `--decode-ms`, `--display-hz`), and the
same output files (report fields, `frames.csv`, `poses.csv`, JSON event lines on stdout), so `harness/run.py` can launch it in place of
`alvr_client_headless.exe`. `--views-tan o,i,u,d` is **Apple's frustum** here (what `computeProjection` returns, left eye, right
mirrored; default 1.732 / 1 / 1.192 / 1), not the declared box: the client's code decides what it declares. `--ipd-mm`,
`--projection-y-flipped`. Flags it does not implement are listed in the report (`swift.ignored_flags`). The report adds a `swift`
block: the frustum, what was declared per eye, and the client's own foveation variables (`FFR.calculateFoveationVars` from the
streamer's settings: the encoded frame the client's decompress shader assumes, to compare with the stream it got).

## Finding (2026-11-05): the declared box is upside down under a standard projection

`_extractFrustumTangents` returns (left, right, **bottom, top**) and `Renderer.swift` builds `AlvrFov(up: z, down: -w)`. With a standard
projection matrix the client declares Apple's 1.192 / 1.0 (up / down as our docs read the `[fovtest]` logs) as up 1.0 / down 1.192,
and the logs print the same swapped order under the labels `u/d`. The client's own defaults (`EventHandler.swift`: up 45, down 50 deg)
agree with the standard reading, i.e. with a real 45 up / 50 down panel and a declaration that is flipped. If so, the rate map's vertical
table and every vertical number of `docs/PICTURE_EDGES_DESIGN.md` 5b are mirrored too. Not decided until one headset logs the raw
`computeProjection` matrix (or a fovgrid with asymmetric up / down marks is read). `swift_headset_check.py` keeps the check FAIL until then.

## Not done

- Run against the streamer. Two topologies: (a) the headset in WSL, the streamer on the builder (the streamer connects to the client:
  the laptop's firewall has blocked inbound connections from the builder before); (b) build it on the builder with Swift for Windows
  (the shims use `clock_gettime` and `Glibc.exit`, small `#if os(Windows)` changes) and run it on loopback like the Rust mock.
- `harness/run.py` wiring (`client.kind: "swift"` choosing the binary and the topology).
- `use_full_range` in `negotiated_config` (the C API's StreamingStarted event does not carry it).

## The headset view message (step C, owed here)

The host sends `{"airframexr":1,"view":{"left":[o,i,u,d],"right":[o,i,u,d]}}` over `ServerControlPacket::Reserved` (docs/PICTURE_EDGES_DESIGN.md
section 8). The client declares min(frustum, V) per eye and edge instead of the frustum alone. To test the client's real code: (1) the fork's
`client_core` needs the text as an event (`tools/patches/client-reserved-message.patch` is the change on ALVR v20.14.1; the fork's header
and Swift event enum need the same case), (2) the client's `EventHandler.swift` takes it and the declaration in `Drawable.swift` / `Renderer`'s
path cuts the tangents, (3) this headset prints the declared boxes as it does now, and the scenario `e2e_fov_headset_view` (with
`client.kind: "swift"`) checks the host's `views_config` against V. None of it is written: it is client work in the fork's repo, which is not edited
without the owner. The lab's Rust mock client already does (2) and passes through `e2e_fov_headset_view` once built on the builder.
