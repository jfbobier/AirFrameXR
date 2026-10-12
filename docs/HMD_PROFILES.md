# Headset profiles (`profiles/hmd_profiles.json`)

VisionALVR cannot test every headset. The configure wizard therefore does not search for a configuration: the headset's handshake
identifies the client, a **community-maintained profile** gives the starting configuration, you can change anything, and the wizard
then **verifies the configuration on your PC** and gives a verdict.

## Flow

1. **Identify.** The handshake (`client_capabilities` in the host log) gives the headset name, the default view resolution, the
   supported refresh rates and what the client supports (foveation, codecs, 10 bit, microphone). A profile matches when its
   `match` fields all hold (`name_contains` any of, `default_view_resolution` equal, `supported_refresh_rates_include` all present).
   No match: a generic profile from the capabilities alone, marked "unverified".
2. **Propose.** The profile's `stream` values and a network variant (`network.variants`; you pick the one that describes your
   Wi-Fi, for example after a bad previous run). Everything is editable in the wizard before the test.
3. **Verify** (what we see with *this* configuration, no bandwidth search): a streaming test of about 30 s at the configured bitrate
   with the benchmark scene, then a verdict per check:
   - encode time p95 below 90% of the frame period (and below the profile's budget);
   - client fps equals the target, no packets lost, network latency not rising;
   - decode and headset queue times in range;
   - **split encoding really engaged** (the encoder reports its engines and strips per frame: a configuration that asks for split 3 on a
     GPU that cannot do it is flagged, with the fallback);
   - audio output present.
   Each check says what it saw and what to change if it failed.
4. **Save.** The values go into `config\session.json` and `config\visionalvr.json` (one writer, so a setting cannot be silently overridden
   by another file).

## File

`profiles/hmd_profiles.json` in the repository is the source of truth. The package ships a copy; the wizard may download the current
file from the public repository when you ask it to (never silently), validates it against the schema and ignores unknown keys.
`config\hmd_profiles.local.json` (same format) overrides or adds profiles on your machine and is never overwritten.

Entry fields: `id`, `display_name`, `match`, `stream` (`eye_resolution`, `fps`, `codec`, `bit_depth`, `full_range`, `encoding_gamma`,
`foveated`, `split_encode` 0-3, `protocol`), `network` (`default`, `variants[]` with `id`, `label`, `bitrate_mbps`), `notes[]`,
`verified[]` (gpu, client, date, what was observed). **Only add values you ran.** A pull request that adds a headset should include the
`client_capabilities` line from its log.
