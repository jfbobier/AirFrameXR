# AirFrameXR — AirWave Visual Identity v1.0

**Direction:** premium, precision-engineered, high-performance XR graphics. The two radiating arcs signal wireless transport; the fluid crossbar hints at generated frames/motion; the central A makes the symbol recognizable even without a wordmark.

**Master files**: vector SVG in `logos/`, `icons/svg/`, and `banners/`. Exported PNGs are for direct integration. The Windows icon includes individual 16px, 24px, 32px, 48px, 64px, 128px, 256px images, with simplified artwork at the three smallest sizes.

## Suggested places to use each asset

| Usage | File |
| --- | --- |
| Windows launcher / EXE icon | `icons/airframexr-windows.ico` |
| Windows small taskbar and tray | `icons/png/tray-white-16.png` or `tray-color-16.png`; use dark mark on light taskbars |
| GitHub README header | `banners/github-readme-banner.png` |
| GitHub repository social preview | `banners/github-social-preview.png` (1280×640) |
| Splash/loading screen | `banners/splash-screen.png` (1920×1080) |
| Transparent mark on dark surfaces | `logos/airframexr-mark-color.svg` |
| On dark UI | `logos/airframexr-horizontal-dark.svg` |
| On light UI | `logos/airframexr-horizontal-light.svg` |
| Web favicon | `icons/favicon.ico` or `icons/svg/favicon.svg` |
| Color reference | `brand-guide/tokens.css` and `tokens.json` |
| Identity overview | `brand-guide/identity-overview.png` |

## Usage conventions

- Keep the name as **AirFrameXR**, with capital A, F and XR.
- Keep generous clear space around the mark; do not crowd the side airwaves with additional decorations.
- Use electric blue + cyan as product accents; white/silver is for legible titles. The purple tint in the earlier proposal is intentionally **not** part of the core identity.
- Prefer the **simplified mark** for 32 pixels and below. Do not use the full lockup in a small taskbar button.
- Prefer SVG for websites/README and PNG/ICO for executables or tooling that cannot display SVG.
- Use descriptive text to explain the product, e.g. "OpenXR runtime · NVIDIA-optimized streaming · Frame generation"; avoid incorporating hardware-vendor marks into the logo.
- When referencing ALVR, credit the protocol compatibility in text, not the logo. Suggested disclaimer: "Compatible with ALVR protocol 20.14. AirFrameXR is an independent project and is not affiliated with the ALVR project."

## Quick GitHub README snippet

```markdown
<p align="center">
  <img src="branding/banners/github-readme-banner.png" alt="AirFrameXR — Wireless XR. Higher Frames." width="100%" />
</p>
```

If placing the contents of this directory under `branding/` in the repository, the relative path above works unchanged.

## Windows resource example

```rc
#define IDI_AIRFRAMEXR 101
IDI_AIRFRAMEXR ICON "branding\\icons\\airframexr-windows.ico"
```

Adjust the relative paths based on your RC build location. If using a framework-specific icon resource, point that property to the ICO file instead.

## Deliverable status and limits

Assets are newly drawn for this project, guided by the approved AirWave concept. No existing ALVR, NVIDIA, Meta, or Apple trademark artwork is included. Text in the main SVG wordmarks is live text for easy editing; the rendered PNGs preserve the bundled layout. SVG viewers with different font availability may render spacing differently unless you use outlined variants in `logos/outlines/`. These are design assets, **not** a legal trademark clearance or confirmation of exclusive rights. Verify name/logo availability before registration or major distribution.
