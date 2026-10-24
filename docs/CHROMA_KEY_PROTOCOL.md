# Chroma key over the ALVR protocol

How the streamer's passthrough chroma key (`video.passthrough`, RGB or HSV) reaches a client,
and what the visionOS client does with it. Checked against ALVR 20.14.1 and the alvr-visionos
submodule pin (`e3fd4480`).

## The protocol is not handshake-only

There are two delivery paths:

1. **Stream start.** The full `Settings` JSON is part of `StreamConfig`, sent when streaming
   starts. It includes `video.passthrough` (`"Disabled"` or `{"Enabled": {...}}`).
2. **Live updates.** The server runs a `real_time_update_thread` for as long as the client is
   streaming (`server_core/src/connection.rs`). It re-reads the session settings every
   `REAL_TIME_UPDATE_INTERVAL` and sends a `RealTimeConfig` (`passthrough` +
   `clientside_post_processing`) in a `ServerControlPacket::ReservedBuffer`. A slider moved in
   the dashboard therefore reaches the client within one interval, without reconnecting.

The Quest client uses both: `client_openxr` handles `ClientCoreEvent::RealTimeConfig` and calls
`update_real_time_config`, so keying changes apply live.

## What the visionOS client sees

The protocol allows live updates, but the client's bridge drops them:

- `client_core` queues `ClientCoreEvent::RealTimeConfig(config)` correctly.
- `client_core/src/c_api.rs` maps it to `AlvrEvent::RealTimeConfig {}`, an empty struct
  commented "Unimplemented". The values never cross into Swift.
- `EventHandler.swift` handles the event with `print("TODO real-time config")`.
- Only the stream-start path is reachable: `alvr_get_settings_json()` returns the settings
  captured at `StreamingStarted`, and Swift reads `video.passthrough` from it.

So with the stock app nothing from the streamer is used, and with PR branch
`feat/streamer-chroma-key` the key is applied **once per stream start**; later dashboard
changes are ignored until the next connection.

## What live updates would need (out of scope)

Our scope is the client app only: ALVR is not modified unless ALVR itself proposes it, so the PR
stops at stream-start keying.

1. `c_api.rs`: give `AlvrEvent::RealTimeConfig` fields (or store the config like `SETTINGS`
   and expose a getter), e.g. the passthrough mode plus the 3x4 channel floats the Quest
   shader takes.
2. alvr-visionos: on the event, update the cached passthrough (`Settings._cached`) so
   `ChromaKeyParams.current` sees it; the existing change check then rebuilds the pipelines.
3. That is a change in ALVR itself, so a separate ALVR PR plus a submodule bump.

The server resends every interval, so the client must compare before acting: a pipeline
rebuild per packet would be wrong. `ChromaKeyParams` is `Equatable` for that reason.
