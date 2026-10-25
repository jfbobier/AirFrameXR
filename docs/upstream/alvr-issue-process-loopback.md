<!-- posted 2026-10-22: https://github.com/alvr-org/ALVR/issues/3427 -->
**Title:** Windows game audio: capture with process loopback (no virtual audio cable, constant volume) - working patch available

**Body:**

Two long-standing Windows pain points come from capturing the game audio through the default output device:

1. **Inconsistent volume in the headset.** With a virtual cable (VB-CABLE) as the captured device, the headset's volume follows the PC's: the level that was set on the PC's output when the stream started is the level the headset gets, and changing the PC volume keys changes it. Muting the speakers while streaming (`mute_when_streaming`) also acts on the captured device.
2. **A kernel-mode audio driver has to be installed** (VB-CABLE or similar: administrator rights, sometimes a reboot, the default device set for every role, otherwise games that play to another role bypass the capture).

Windows 10 version 2004 and newer can capture audio per process without any device: `ActivateAudioInterfaceAsync` on `VAD\Process_Loopback` with `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE` for the host's own process captures everything the PC plays except the host, as plain PCM in a format we choose (48 kHz stereo 16-bit), before the output device's volume, mute and format are applied.

What we measured (a Windows test PC, a mock headset on the same PC and a microphone):
- the capture level is the same with the output muted, at 10 % and at 100 %, and with the output's own format at 44.1, 48 and 96 kHz;
- a process that is not the target is silent in include mode; exclude mode hears everything else;
- a player running elevated (high integrity) is captured by a normal-integrity host, in both modes (UEVR and mods often need elevation);
- with the host in this mode the Windows default output is left alone, no cable is involved, and the PC speakers can be muted independently while the headset keeps its sound.

We implemented it against v20.14.1 in our fork's host: one new file in `alvr_audio` (about 250 lines with the `windows` crate, no new dependency except `windows-core`) and two small hunks in `server_core/src/connection.rs` (the sample rate becomes 48000 and the capture thread calls the new function instead of `record_audio_blocking`). The existing device capture stays as the fallback:

https://github.com/jfbobier/AirFrameXR/blob/main/tools/patches/audio-process-loopback.patch

Not covered yet: the minimum Windows build was not checked, it was tested with tone and music players and a mock headset (and on a real headset only through the older device path), and it is selected by an environment variable, not a session setting.

If there is interest we can turn it into a PR for master (a `GameAudioConfig` option such as "Capture: process loopback / device", falling back to the device capture when the activation fails). Otherwise the patch is MIT licensed and free to take.
