# AirFrameXR Black Box — visual assets

A performance-flight-recorder identity for the **Black Box** companion tool within AirFrameXR.
The artwork shares AirFrameXR's deep navy, blue and cyan visual language, with a small red
record indicator to distinguish the recorder from the main streamer.

## Recommended files

| Use | File |
| --- | --- |
| App executable / Windows shortcut | `icons/blackbox-windows.ico` |
| Small taskbar icons | `icons/png/blackbox-16.png`, `blackbox-24.png`, `blackbox-32.png` |
| High-res icon | `icons/png/blackbox-512.png` or `blackbox-1024.png` |
| Master editable app icon | `icons/svg/blackbox-app-icon.svg` |
| Simplified small icon | `icons/svg/blackbox-app-icon-small.svg` |
| Main GitHub / docs banner | `banner/blackbox-banner.png` (2400 x 800) |
| Editable banner | `banner/blackbox-banner.svg` |
| Social preview | `banner/blackbox-social.png` (1280 x 640) |
| Cinematic concept (secondary) | `banner/blackbox-cinematic-concept.png` |
| Preview sheet | `preview/blackbox-contact-sheet.png` |

The Windows ICO embeds **individually rendered** 16, 24, 32, 48, 64, 128 and 256 pixel
artwork; the three smallest sizes use a simplified motif, rather than a blurry shrink of the full icon.

## README snippet

```md
![AirFrameXR Black Box — performance flight recorder](branding/blackbox/banner/blackbox-banner.png)
```

Adjust this path to wherever you put the assets in the repo.

## Windows resource example

```rc
#define IDI_AIRFRAMEXR_BLACKBOX 201
IDI_AIRFRAMEXR_BLACKBOX ICON "branding\\blackbox\\icons\\blackbox-windows.ico"
```

## Naming and visual rationale

- Suggested feature name: **AirFrameXR Black Box** (no assumption about executable filename).
- Descriptor: **Performance Flight Recorder**.
- Palette: navy `#031022`, cyan `#00D9F5`, electric blue `#087BFF`, recording red `#FF5154`.
- A cyan frame-timing waveform indicates performance telemetry; a restrained red lamp indicates recording.
- The icon depicts a physical *black box* in subtle perspective, without vendor logos.
- This is original project artwork, not affiliated with NVIDIA, ALVR, Meta, Apple, or any other brand.
- `blackbox-cinematic-concept.png` is an AI-generated marketing illustration, not a screenshot of the app.

## Quality note

The SVGs are editable vector masters. They use Inter as a preferred typeface with fallbacks;
text layout can differ in another editor without Inter. The PNG exports are pixel-perfect to
this bundle. No font files are included.
