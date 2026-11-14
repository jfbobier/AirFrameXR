# AirFrameXR privacy policy

Last updated: 2026-11-14

AirFrameXR is a wireless PC VR streamer: a visionOS app (the client) shows what AirFrameXR running on your own
Windows PC (the streamer) renders. This page covers the visionOS app, the Windows streamer and this repository.

**In short: the developer collects no data. There is no account, no analytics, no advertising, no tracking and no server
operated by the developer. Everything stays between your headset and your PC.**

## What the app does with your data

- **No data leaves your own network.** The app connects only to the PC you choose, on your local network (LAN). It does not
  connect to any Internet service of the developer or of a third party.
- **Tracking data goes to your PC only.** To let games react to you, the app sends head pose, and, when you use them, hand
  and finger joints, spatial controller / game controller input and face-tracking expressions, to the streamer on your PC.
  They are used live by the game and are not recorded, stored or forwarded by the app. What your PC software does with them is
  under your control (the streamer keeps no copy beyond its own technical logs, below).
- **Surroundings.** The world-sensing permission is used on the headset only, to warn you about obstacles and to place the
  play area. Nothing from it is stored or sent.
- **Local Network permission.** Needed to find and talk to your PC. Camera / Bluetooth / hand / world-sensing permissions are
  asked by visionOS only when a feature needs them and can be refused or revoked in Settings.

## What is stored

- **On the headset:** only the app's settings (for example the PC address you paired with and display options) and
  technical log files (performance counters, connection events, errors). They contain no personal content, and they stay on the
  device unless you export them yourself (the app exposes its files in the Files app).
- **On your PC:** the streamer writes technical logs (frame timings, encoder and network statistics, game executable names)
  in its own folder. They never leave your PC unless you choose to share them, for example in a bug report.

## What is not done

No advertising, no profiling, no sale or sharing of data, no third-party analytics or crash-reporting SDK, no location data,
no contacts, no photos, no microphone recording by the developer. The app is not directed at children and collects nothing
from anyone.

## TestFlight and the App Store

Apple, not the developer, provides TestFlight and App Store distribution. Apple may give the developer aggregated, anonymous
download, crash and tester-feedback statistics under its own policies. If you send feedback through TestFlight, the developer
sees what you chose to send.

## Open source

The code is public, so every statement above can be checked: <https://github.com/jfbobier/AirFrameXR> (streamer) and the
visionOS client repository linked from it. Third-party licenses are in `docs/THIRD_PARTY_NOTICES.md`.

## Changes and contact

If this policy changes, the new version is published here with a new date. Questions: open an issue at
<https://github.com/jfbobier/AirFrameXR/issues>.
