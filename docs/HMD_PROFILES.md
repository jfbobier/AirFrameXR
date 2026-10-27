# Headset profiles

AirFrameXR cannot test every headset, and it does not try to know them all. A **profile** is one small JSON file that says how a given headset
(and its client app) should be configured. Profiles are **shared among users, peer to peer** (the AirFrameXR Discord, like UEVR profiles): the
program never downloads anything, there is no central database. The repository only holds a **template** and the **Apple Vision Pro** profile (the
project's first target), which ships in the package as an example.

## Using a profile

**Import profile...** on the wizard's headset page opens a file picker. It reads the profile, shows what ALVR reports about *your* headset next to what
the profile expects, and warns about every mismatch (name, panel resolution, refresh rates, 10-bit, foveation, a stream larger than the panel) without
ever refusing: a profile written on a Quest 2 usually suits a Quest 3, so you decide. On a yes it copies the file into `config\profiles\` (you can
also drop the file there yourself) and uses it for this run.

At the handshake the setup wizard (`configure.exe`) matches the headset's handshake
against every profile it finds (`profiles\` shipped with the package, then `config\profiles\`; your file replaces a shipped one with the same `id`):

1. **Identify.** The handshake gives the headset name, the default view resolution and the supported refresh rates. A profile matches when its
   `match` fields hold: `name_contains` (any), `default_view_resolution` (equal), `supported_refresh_rates_include` (all present). No match: a generic
   proposal built from what the headset advertised, marked as such.
2. **Propose.** The profile's `stream` values and one **network variant** (the bitrate depends on your Wi-Fi: you pick the line that describes yours,
   for instance after a bad earlier run). The wizard shows everything and lets you change it before anything runs.
3. **Verify.** The wizard does not search for a configuration or measure your bandwidth: it checks *this* configuration on *this* PC and gives a
   verdict with what it saw: encode time against the frame period, a network check at the configured bitrate (packet loss, frame rate held, network latency not rising), a
   **scene test** (Littlest Tokyo streamed through the real pipeline with your head pose: tracking arrives, frames, latency, decode and headset queues),
   audio, and **whether split encoding really engaged** (its own row: engines and strips per frame from the encoder test and the live stream; a GPU that
   cannot split is flagged).
4. **Save.** The values go into `config\session.json` and `config\airframexr.json`, then the profile's `session_overrides` are written into
   `session.json`.

## Exporting yours

At the end of the wizard (the configuration page) **Export profile...** saves the configuration you ended up with as a profile file, ready to share:
the headset's identity exactly as ALVR declared it at the handshake (`exported_by.alvr_declared`: name, hostname, panel, refresh rates, codec
capabilities, gamma), the stream settings, the ALVR settings you changed from the shipped defaults (`session_overrides`; machine-specific ones such as
audio device names are left out), your bitrate with a one-line description of your network, and the numbers of the verdict (`verified`: GPU, frames
shown, latency, packet loss, encode time, whether split encoding engaged). You can type your name and the network description first; edit the
file by hand (notes, more network variants) before sharing if you like.

## Making or editing a profile

Copy `profiles\template.profile.json`, rename it `<headset>.profile.json`, fill in values **you have run**, and share it. Keys (all optional except
`id`, `stream`, `network`):

| key | meaning |
|---|---|
| `id`, `display_name`, `author` | identity; the same `id` replaces another file's profile |
| `match` | `name_contains` [strings], `default_view_resolution` [w, h], `supported_refresh_rates_include` [Hz] |
| `stream` | `eye_resolution` [w, h], `fps`, `codec` (hevc), `bit_depth`, `full_range`, `encoding_gamma`, `foveated`, `split_encode` (0-3), `protocol` (udp/tcp) |
| `network` | `default` (variant id) and `variants[]`: `id`, `label`, `bitrate_mbps` |
| `fov` | optional, a measured visible window (see below): `visible_deg` {outer, inner, upper, lower} (degrees from the view axis to the edge of what the headset shows), `margin_deg` (default 3), `render_quality` (0 to 1), `note` |
| `session_overrides` | ALVR session settings as dotted paths into `config\session.json`, for example `session_settings.video.encoder_config.nvenc.quality_preset.variant`: `"P3"` |
| `notes`, `verified` | what another user should know; GPU, client, date and result of the runs that back the values |

A profile only changes this PC's configuration files, and only when you run the wizard and save.

### A measured field of view (`fov`)

A headset does not always show the whole field of view it declares (the Vision Pro declares 106 x 96 degrees and shows about 80 x 74: [AVP_FOV.md](AVP_FOV.md)). A profile can carry the
measured window: the angle from the view axis to each visible edge, a margin to add, and a render quality to suggest. Nothing applies it by itself. *Advanced picture and FOV settings*
looks up the profile whose `match.name_contains` fits the saved headset's name and, if it has `fov`, shows a button, "Use the <headset>'s measured values (visible + 3 degrees: ...)". One
click ticks the override, fills the four fields with `tan(visible + margin)` (never above the headset's own values) and the render quality slider, and saves; they can be changed after.
The wizard only says on the proposal that such values exist. A headset that shows all it declares (the Quest 3) has no `fov` and no button. Values from another person's fit may not fit
yours: check the corners for black with `fovgrid.exe` ([FOV_GRID.md](FOV_GRID.md)).

## Starting-point profiles for other headsets

`profiles\` also holds small starting points for the Meta Quest 2, 3, 3S and Pro and the Pico 4 / 4 Ultra. They are **compiled from public guidance, not tested by
this project** (they have no `verified` entry, and say so): HEVC at 100-150 Mbps (two network variants), foveated encoding on, 90 Hz (72 Hz for the Pico 4), and everything else
left to ALVR's and the headset's own defaults (resolution, gamma, encoder details). The sources are listed in each file (mainly ALVR's wiki, Settings tutorial). If you
have run one of them, improve it and share it: that is what the `verified` section is for.

## The Apple Vision Pro profile

`profiles\apple-vision-pro.profile.json`: 3552x3200 per eye at 90 fps, HEVC 10-bit, ALVR foveated encoding (centre 0.45 x 0.4, shift 0.4 / 0.1, edge
ratio 4 x 5: a 4224x1664 encoded frame), split encoding 3, 300 Mbps on a good Wi-Fi 6/6E link, plus the 22 ALVR settings the author tuned by A/B testing on stock ALVR + SteamVR (QuestPro emulation, NVENC P3 without multi-pass,
CABAC, 1.5 buffered frames, no microphone, the VideoToolbox low-latency option, chroma-key passthrough, ...). They are also the defaults of
`config\session.default.json` in the package (the author's session, foveated, 300 Mbps). Foveated is the measured choice on the RTX 5090: encode 3.1 ms against
7.7 ms for the full 7104x3200 frame, a lighter headset decode, and three times the bits per pixel at the same bitrate; the full frame looked no better. The
full frame (encode profile `full-split`) remains selectable in the Settings window.

It also carries the Vision Pro's measured visible window (`fov`: 40.3 / 40.0 / 39.0 / 34.4 degrees outer / inner / upper / lower, margin 3 degrees, render quality 0.5), which the Advanced window offers with one button (tangents 0.94 / 0.93 / 0.90 / 0.76). One person's left eye, not yet tried in a game; the note in the profile says so.
