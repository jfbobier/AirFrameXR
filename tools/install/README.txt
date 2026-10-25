AirFrameXR 0.6 - PC streamer for the ALVR app on Apple Vision Pro (OpenXR games, NVIDIA RTX 40 series or newer)
ALPHA SOFTWARE - TEST AT YOUR OWN RISK. Tested on a real headset by one person on one PC. Games may crash or show a broken
image, the stream may stutter. It registers this folder as your OpenXR runtime and adds a firewall rule (undone by
the launcher's "Switch back" button). Take the headset off if the image freezes or judders. Provided "as is", without warranty
(LICENSE.txt).

Made for: Windows + NVIDIA GeForce RTX 40 series or newer + the ALVR app (20.14.x) on Apple Vision Pro. Other ALVR headsets
may connect (same protocol) but are not tested or supported: use ALVR itself for them.

1. Unzip this folder anywhere you like (it stays self-contained: config\ and logs\ are created inside it).
2. Double-click AirFrameXR.exe (the launcher: the only file in this folder, everything else is in bin\ and the other folders).
3. In the OpenXR runtime row click "Switch to AirFrameXR" (asks for administrator rights): this folder becomes the Windows OpenXR
   runtime and alvr_host.exe is allowed through the firewall. The same button reads "Switch back to ..." afterwards and puts your
   previous runtime back. Moved the folder? Click the switch again from the new place.
4. Click Launch on "Setup wizard": open the ALVR app on the Vision Pro, pair it, check the settings. On the Vision Pro set the ALVR
   app's chroma key to the "colour when no game runs" (the idle frame is #107C10, the App Store app's own key colour: just turn the chroma key on), as the wizard shows.
5. Click Launch on "Streamer" and keep it open while you play. It connects to the headset by itself; start any OpenXR game.
   Close SteamVR and ALVR's own launcher/dashboard first: they use the same network ports.
"Diagnose" re-checks your setup when something feels wrong; "Logs and performance" shows how each game went.

Logs: logs\AirFrameXR.log (always), logs\debug\<date-time>\ (when debug logging is on). Send both when reporting a problem.

Credits: the benchmark scene bench\littlest_tokyo.vab is "Littlest Tokyo" by Glen Fox (glenatron,
https://sketchfab.com/glenatron), licensed CC-BY-4.0 (https://creativecommons.org/licenses/by/4.0/), from the three.js
examples. Changed: converted to AirFrameXR's own format (meshes decompressed, simplified shading) and shown inside a room
tiled with the scene's own texture. Its music bench\sakura_breeze.mp3 is "Sakura Breeze" by Roa Music
(https://soundcloud.com/roa_music1031), Royalty Free Music by https://www.free-stock-music.com, Creative Commons / Attribution 3.0
Unported License (CC BY 3.0, https://creativecommons.org/licenses/by/3.0/deed.en_US); changed: the first 75 seconds, re-encoded. stb_image (public domain / MIT) decodes its textures.
Third-party licences: docs\THIRD_PARTY_NOTICES.md and docs\licenses\.
