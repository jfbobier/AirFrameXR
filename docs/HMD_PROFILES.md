# Headset profiles

VisionALVR cannot test every headset, and it does not try to know them all. A **profile** is one small JSON file that says how a given headset
(and its client app) should be configured. Profiles are **shared among users, peer to peer** (the VisionALVR Discord, like UEVR profiles): the
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
   verdict with what it saw: encode time against the frame period, a streaming test with the headset at the configured bitrate (packet loss, frame
   rate held, network latency not rising), decode and headset queues, audio, and **whether split encoding really engaged** (engines and strips per
   frame reported by the encoder; a GPU that cannot split is flagged).
4. **Save.** The values go into `config\session.json` and `config\visionalvr.json`, then the profile's `session_overrides` are written into
   `session.json`.

## Making or editing a profile

Copy `profiles\template.profile.json`, rename it `<headset>.profile.json`, fill in values **you have run**, and share it. Keys (all optional except
`id`, `stream`, `network`):

| key | meaning |
|---|---|
| `id`, `display_name`, `author` | identity; the same `id` replaces another file's profile |
| `match` | `name_contains` [strings], `default_view_resolution` [w, h], `supported_refresh_rates_include` [Hz] |
| `stream` | `eye_resolution` [w, h], `fps`, `codec` (hevc), `bit_depth`, `full_range`, `encoding_gamma`, `foveated`, `split_encode` (0-3), `protocol` (udp/tcp) |
| `network` | `default` (variant id) and `variants[]`: `id`, `label`, `bitrate_mbps` |
| `session_overrides` | ALVR session settings as dotted paths into `config\session.json`, for example `session_settings.video.encoder_config.nvenc.quality_preset.variant`: `"P3"` |
| `notes`, `verified` | what another user should know; GPU, client, date and result of the runs that back the values |

A profile only changes this PC's configuration files, and only when you run the wizard and save.

## The Apple Vision Pro profile

`profiles\apple-vision-pro.profile.json`: 3552x3200 per eye at 90 fps, HEVC 10-bit, full frame (no foveation), split encoding 3, 230 Mbps on a good
Wi-Fi 6/6E link, plus the 22 ALVR settings the author tuned by A/B testing on stock ALVR + SteamVR (QuestPro emulation, NVENC P3 without multi-pass,
CABAC, 1.5 buffered frames, no microphone, the VideoToolbox low-latency option, chroma-key passthrough, ...). They are also the defaults of
`config\session.default.json` in the package (the author's session without foveation, 230 Mbps).
