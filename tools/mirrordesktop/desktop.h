// Desktop mirror (docs/DESKTOP_MIRROR.md): the Windows desktop drawn as a quad into the stream's frame while no game runs.
// Capture: DXGI desktop duplication of the primary output on the adapter that owns it (a laptop's display is often on the other GPU), through the
// CPU into a texture of the encoder's device: one path for same-adapter and cross-adapter. SDR (BGRA8) and HDR (scRGB FP16) desktops; HDR is
// converted to SDR in the shader (Windows' SDR white level as 1.0, a soft knee above, sRGB encode). The cursor is not in the duplicated image: the
// shader draws a dot at the pointer.
#pragma once
#include <d3d11.h>
#include <cstdint>
#include <memory>

class DesktopMirror {
public:
    // dev/ctx: the encoder's device. target: size and format of the stream's composed frame (2 x eye width by eye height).
    DesktopMirror(ID3D11Device* dev, ID3D11DeviceContext* ctx, int targetW, int targetH, DXGI_FORMAT targetFmt);
    ~DesktopMirror();
    bool Init(char* err, int errlen);
    // Test hook (tools/probe/desktop_probe.cpp): no capture; the desktop image is whatever SetTestImage was given (BGRA8 or FP16 scRGB, tightly packed).
    bool InitForTest(int w, int h, bool hdr, float sdrWhite, char* err, int errlen);
    void SetTestImage(const void* pixels);
    int DesktopW() const; int DesktopH() const; bool Hdr() const;
    // Takes the newest desktop image (the previous one when nothing changed), clears the target to `clearRgb` (0xRRGGBB) and draws the screen quad
    // into each eye's half: ndc = left eye TL TR BL BR then right eye (x, y pairs; NaN x = that eye does not see the screen);
    // cursor = (u, v) over the desktop, or negative for none. Returns the target texture (owned here), nullptr on failure.
    ID3D11Texture2D* Render(const float ndc[16], const float cursor[10], uint32_t clearRgb, char* err, int errlen);
private:
    struct Impl;
    std::unique_ptr<Impl> I;
};
