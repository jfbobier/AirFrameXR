// The headset tools of docs/PICTURE_EDGES_DESIGN.md sections 6 and 6b, as scenes of the probe (an ordinary OpenXR app on the PC, streamed like a game):
//   XR_PROBE_TOOL=fit       "Fit the picture to your eyes": a mid-blue box on black, one eye at a time, arrows push an edge out until its black bar is gone.
//   XR_PROBE_TOOL=restrict  "Choose what the game renders": the scene (XR_PROBE_SCENE=<file.vab>, flat grey without it) inside the box, black outside, both eyes,
//                           arrows pull an edge in; Space (held) shows the whole view.
// Keys: the PC keyboard through GetAsyncKeyState (whatever window has the focus: with the headset on nobody can click a window back into focus), or scripted for the
// lab: XR_PROBE_KEYS="<frame>:<key>[,...]" with key = space left right up down tab enter esc, "shift+left" etc.; "+space" / "-space" press and release only (a
// plain key is a tap: down on that frame, up on the next). Frames count from the first rendered frame; scripted mode ignores the real keyboard.
// Output: one JSON line per change on stdout (the tool app reads them): {"tool","event":"box|start|enter|esc","frame","eye","box":[[outer,inner,upper,lower] per eye],
// "panel":[...same...],"pct":..}; tangents, outer = away from the nose. XR_PROBE_TOOL_START = the fit's start box as a fraction of the panel (default 0.80),
// XR_PROBE_TOOL_SAVED_S = how long the "Saved" screen stays (default 3).
// The in-headset text is drawn with GDI into a bitmap and copied into the eye image (the plan said DirectWrite: same result, no Direct2D device needed), head-locked
// at the centre of the view axis inside 12 degrees, white on dark grey, about 1 degree cap height, only in the eye being fitted while the other is black.
#pragma once
#include <windows.h>
#include <d3d11_1.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

struct XrTool {
    enum Mode { None, Fit, Restrict } mode = None;
    enum Phase { Intro, Working, Saved } phase = Intro;
    enum Key { KSpace, KLeft, KRight, KUp, KDown, KShift, KTab, KEnter, KEsc, KCount };
    float P[2][4] = {};     // the panel (what the runtime told) per eye: outer, inner, upper, lower tangents
    float box[2][4] = {};   // the box being shaped, same order
    float pxTan[2] = {};    // stream pixels per tangent of the told view, per eye (set while drawing)
    int eye = 0;            // fit: the eye being fitted; restrict, one eye: the eye edited
    bool bothEyes = true;   // restrict
    bool compare = false;   // Space held: the whole view (restrict) / both eyes (fit)
    bool quit = false;
    bool havePanel = false;
    bool scripted = false;
    double startMs = 0, lastKeyMs = 0, savedMs = 0, savedFor = 3000;
    float startFrac = 0.8f;
    int frame = 0;

    struct Ev { int frame; Key key; bool down; };
    std::vector<Ev> events; size_t nextEv = 0;
    bool down[KCount] = {}; double heldSince[KCount] = {}, lastRepeat[KCount] = {};

    bool On() const { return mode != None; }

    void Init(const char* tool, const char* keys) {
        if (!tool) return;
        mode = !strcmp(tool, "fit") ? Fit : !strcmp(tool, "restrict") ? Restrict : None;
        if (const char* s = getenv("XR_PROBE_TOOL_START")) startFrac = std::max(0.2f, std::min(1.f, (float)atof(s)));
        if (const char* s = getenv("XR_PROBE_TOOL_SAVED_S")) savedFor = std::max(0.0, atof(s)) * 1000.0;
        if (keys && *keys) { scripted = true; ParseKeys(keys); }
    }

    static bool NameKey(const std::string& n, Key& k) {
        static const char* names[] = { "space", "left", "right", "up", "down", "shift", "tab", "enter", "esc" };
        for (int i = 0; i < KCount; i++) if (n == names[i]) { k = (Key)i; return true; }
        return false;
    }
    void ParseKeys(const char* spec) {
        std::string s = spec; size_t p = 0;
        while (p < s.size()) {
            size_t e = s.find(',', p); if (e == std::string::npos) e = s.size();
            std::string item = s.substr(p, e - p); p = e + 1;
            size_t c = item.find(':'); if (c == std::string::npos) continue;
            const int fr = atoi(item.substr(0, c).c_str()); std::string what = item.substr(c + 1);
            bool shift = false;
            if (what.compare(0, 6, "shift+") == 0) { shift = true; what = what.substr(6); }
            char mode_ = 0; if (!what.empty() && (what[0] == '+' || what[0] == '-')) { mode_ = what[0]; what = what.substr(1); }
            Key k; if (!NameKey(what, k)) continue;
            if (shift) events.push_back({ fr, KShift, true });
            if (mode_ == '-') events.push_back({ fr, k, false });
            else { events.push_back({ fr, k, true }); if (!mode_) events.push_back({ fr + 1, k, false }); }
            if (shift) events.push_back({ fr + 1, KShift, false });
        }
        std::stable_sort(events.begin(), events.end(), [](const Ev& a, const Ev& b) { return a.frame < b.frame; });
    }

    static float Deg(float t, float d) { return tanf(atanf(t) + d * 0.0174532925f); }

    // ---- per frame: the panel from the told views, the keys, the state -------------------------------------------------------------------------------------------
    void Update(int fr, double nowMs, const XrFovf fov[2]) {
        frame = fr;
        for (int e = 0; e < 2; e++) {
            const float tl = -tanf(fov[e].angleLeft), tr = tanf(fov[e].angleRight), tu = tanf(fov[e].angleUp), td = -tanf(fov[e].angleDown);
            const float np[4] = { e == 0 ? tl : tr, e == 0 ? tr : tl, tu, td };
            bool changed = !havePanel;
            for (int k = 0; k < 4; k++) if (fabsf(np[k] - P[e][k]) > 1e-5f) changed = true;
            if (changed && havePanel) {   // the panel changed under the tool (a restart told another view): keep the same fractions
                for (int k = 0; k < 4; k++) box[e][k] *= np[k] / std::max(P[e][k], 1e-4f);
            }
            memcpy(P[e], np, sizeof np);
            if (!havePanel) for (int k = 0; k < 4; k++) box[e][k] = np[k] * (mode == Fit ? startFrac : 1.f);
        }
        if (!havePanel) { havePanel = true; startMs = nowMs; lastKeyMs = nowMs; Emit("box"); }
        bool pressed[KCount] = {};
        if (scripted) {
            while (nextEv < events.size() && events[nextEv].frame <= fr) { const Ev& v = events[nextEv++]; if (v.down && !down[v.key]) pressed[v.key] = true; down[v.key] = v.down; }
        } else {
            static const int vk[KCount] = { VK_SPACE, VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN, VK_SHIFT, VK_TAB, VK_RETURN, VK_ESCAPE };
            for (int k = 0; k < KCount; k++) {
                const bool d = (GetAsyncKeyState(vk[k]) & 0x8000) != 0;
                if (d && !down[k]) { pressed[k] = true; heldSince[k] = lastRepeat[k] = nowMs; }
                else if (d && k >= KLeft && k <= KDown && nowMs - heldSince[k] > 400 && nowMs - lastRepeat[k] > 70) { pressed[k] = true; lastRepeat[k] = nowMs; }   // repeats while held
                down[k] = d;
            }
        }
        bool any = false;
        for (int k = 0; k < KCount; k++) if (pressed[k]) any = true;
        if (any) lastKeyMs = nowMs;
        compare = down[KSpace] && phase == Working;
        if (phase == Saved) { if (nowMs - savedMs >= savedFor) quit = true; return; }
        if (pressed[KEsc]) { Emit("esc"); quit = true; return; }
        if (phase == Intro) { if (pressed[KSpace]) { phase = Working; startMs = nowMs; lastKeyMs = nowMs; Emit("start"); } return; }
        bool changed = false;
        if (pressed[KTab]) {
            if (mode == Fit) eye ^= 1; else bothEyes = !bothEyes;
            changed = true;
        }
        const bool shift = down[KShift];
        for (int a = KLeft; a <= KDown; a++) if (pressed[a]) { Arrow((Key)a, shift); changed = true; }
        if (changed) Emit("box");
        if (pressed[KEnter]) { Emit("enter"); phase = Saved; savedMs = nowMs; }
    }

    // Left / Right are the screen sides of the eye in the fit (the nose side is the inner edge), outer / inner in the restriction (both eyes alike).
    void Arrow(Key a, bool shift) {
        if (mode == Fit) {
            const int e = eye;
            const int edge = a == KUp ? 2 : a == KDown ? 3 : (a == KLeft) == (e == 0) ? 0 : 1;
            const float t = box[e][edge];
            box[e][edge] = shift ? std::max(0.05f, Deg(t, -0.5f)) : std::min(P[e][edge], Deg(t, +0.5f));
        } else {
            const int edge = a == KUp ? 2 : a == KDown ? 3 : a == KLeft ? 0 : 1;
            for (int e = 0; e < (bothEyes ? 2 : 1); e++) {
                const float t = box[e][edge];
                box[e][edge] = shift ? std::min(P[e][edge], Deg(t, +1.f)) : std::max(0.2f * P[e][edge], Deg(t, -1.f));   // never past V, never below a fifth of it
            }
        }
    }

    float Pct() const {   // the share of the panel's pixels the box keeps (both eyes)
        float g = 0, v = 0;
        for (int e = 0; e < 2; e++) { g += (box[e][0] + box[e][1]) * (box[e][2] + box[e][3]); v += (P[e][0] + P[e][1]) * (P[e][2] + P[e][3]); }
        return v > 0 ? 100.f * g / v : 100.f;
    }

    void Emit(const char* ev) const {
        printf("{\"tool\":\"%s\",\"event\":\"%s\",\"frame\":%d,\"eye\":%d,\"both\":%d,\"box\":[[%.5f,%.5f,%.5f,%.5f],[%.5f,%.5f,%.5f,%.5f]],\"panel\":[[%.5f,%.5f,%.5f,%.5f],[%.5f,%.5f,%.5f,%.5f]],\"pct\":%.2f}\n",
               mode == Fit ? "fit" : "restrict", ev, frame, eye, bothEyes ? 1 : 0,
               box[0][0], box[0][1], box[0][2], box[0][3], box[1][0], box[1][1], box[1][2], box[1][3],
               P[0][0], P[0][1], P[0][2], P[0][3], P[1][0], P[1][1], P[1][2], P[1][3], Pct());
        fflush(stdout);
    }

    // ---- drawing --------------------------------------------------------------------------------------------------------------------------------------------------
    struct TextTex { ID3D11Texture2D* tex = nullptr; std::wstring key; int w = 0, h = 0; };
    TextTex tt[2];

    std::wstring Text(int e, bool& show) const {
        show = true;
        if (mode == Fit) {
            if (phase == Intro) return L"Fit the picture to your eyes.\nArrows push an edge out until its black bar is gone. Shift + arrow pulls it back.\nTab: other eye. Enter: save. Esc: quit.\nPress SPACE to start.";
            if (phase == Saved) return L"Saved. You can take the headset off.";
            if (compare) { show = false; return L""; }
            return std::wstring(e == 0 ? L"LEFT EYE: close your right eye" : L"RIGHT EYE: close your left eye") + L"\npush until the bar is gone, one step back, one forward";
        }
        if (phase == Intro) return L"Choose what the game renders.\nArrows pull an edge in, Shift + arrow pushes it back out. Tab: both eyes / one eye. Space: compare with the whole view. Enter: save. Esc: quit.\nPress SPACE to start.";
        if (phase == Saved) return L"Saved.";
        if (compare) { show = false; return L""; }
        wchar_t b[160]; const float pct = Pct();
        swprintf(b, 160, L"the game renders %.0f %% of its pixels (%.0f %% less GPU work in games that honour it)", pct, 100.f - pct);
        return b;
    }

    // Draws the tool's picture over the eye image (fit: everything; restrict: the black outside the box, over the scene the probe drew). fov = the told view of this eye.
    void Draw(int e, ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11DeviceContext1* c1, ID3D11RenderTargetView* rtv, ID3D11Texture2D* tex, const XrFovf& fov) {
        if (!havePanel) return;
        D3D11_TEXTURE2D_DESC td{}; tex->GetDesc(&td);
        const float W = (float)td.Width, H = (float)td.Height;
        const float tl = -tanf(fov.angleLeft), tr = tanf(fov.angleRight), tu = tanf(fov.angleUp), tdn = -tanf(fov.angleDown);
        const float ppt = W / (tl + tr); pxTan[e] = ppt;
        auto px = [&](float x) { return (x + tl) / (tl + tr) * W; };
        auto py = [&](float y) { return (1.f - (y + tdn) / (tu + tdn)) * H; };
        // the box of this eye (the whole view while Space is held in the restriction)
        const float* b = (mode == Restrict && compare) ? P[e] : box[e];
        const float x0 = px(e == 0 ? -b[0] : -b[1]), x1 = px(e == 0 ? b[1] : b[0]), y0 = py(b[2]), y1 = py(-b[3]);
        auto fill = [&](float a, float bb, float c, float d, const float* col) {
            D3D11_RECT r{ (LONG)floorf(a), (LONG)floorf(bb), (LONG)ceilf(c), (LONG)ceilf(d) };
            r.left = std::max<LONG>(r.left, 0); r.top = std::max<LONG>(r.top, 0); r.right = std::min<LONG>(r.right, (LONG)td.Width); r.bottom = std::min<LONG>(r.bottom, (LONG)td.Height);
            if (r.right > r.left && r.bottom > r.top) c1->ClearView(rtv, col, &r, 1);
        };
        static const float black[4] = { 0, 0, 0, 1 }, blue[4] = { 0.04f, 0.16f, 0.70f, 1 }, line[4] = { 0.40f, 0.62f, 1.f, 1 }, grey[4] = { 0.22f, 0.22f, 0.22f, 1 };
        const float lw = std::max(2.f, W / 600.f);
        bool eyeShown = true;
        if (mode == Fit) {
            // one eye at a time while fitting; the other is black. Before Space and with Space held, both eyes on the box.
            eyeShown = (phase == Intro) || compare || e == eye;
            c1->ClearView(rtv, black, nullptr, 0);
            if (eyeShown) { fill(x0, y0, x1, y1, blue); fill(x0, y0, x1, y0 + lw, line); fill(x0, y1 - lw, x1, y1, line); fill(x0, y0, x0 + lw, y1, line); fill(x1 - lw, y0, x1, y1, line); }
        } else {
            if (!sceneDrawn) { c1->ClearView(rtv, grey, nullptr, 0); const float mid[4] = { 0.3f, 0.34f, 0.4f, 1 }; fill(x0, y0, x1, y1, mid); }
            fill(0, 0, W, y0, black); fill(0, y1, W, H, black); fill(0, y0, x0, y1, black); fill(x1, y0, W, y1, black);
        }
        // the text: centred on the view axis, inside 12 degrees, only while the screen says so and (fit, working) only in the eye being fitted
        bool show; std::wstring s = Text(e, show);
        if (!show || s.empty()) return;
        if (phase == Working && (nowMs() - lastKeyMs) > 5000.0) return;   // fades after 5 s without a key; any key brings it back
        if (mode == Fit && !eyeShown) return;
        const float r12 = tanf(12.f * 0.0174532925f) * ppt;
        const int cx = (int)px(0.f), cy = (int)py(0.f);
        DrawText_(e, dev, ctx, tex, td.Format, cx, cy, (int)r12, (int)(tanf(12.f * 0.0174532925f) * (H / (tu + tdn))), s, (int)std::max(12.f, 1.45f * tanf(0.0174532925f) * ppt));
    }
    bool sceneDrawn = false;   // restrict: the probe drew its scene before Draw

    static double nowMs() { LARGE_INTEGER f, c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c); return c.QuadPart * 1000.0 / f.QuadPart; }

    // GDI text into a BGRA bitmap, then into a texture of the swapchain's own format (4 bytes per pixel, R8G8B8A8 / B8G8R8A8 families) copied into the eye image.
    void DrawText_(int e, ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11Texture2D* eyeTex, DXGI_FORMAT fmt, int cx, int cy, int halfW, int halfH, const std::wstring& s, int fontPx) {
        // the runtime's swapchain images may be TYPELESS (VDXR's are): the same bytes, the same copy family
        const bool bgra = fmt == DXGI_FORMAT_B8G8R8A8_UNORM || fmt == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || fmt == DXGI_FORMAT_B8G8R8A8_TYPELESS;
        const bool rgba = fmt == DXGI_FORMAT_R8G8B8A8_UNORM || fmt == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || fmt == DXGI_FORMAT_R8G8B8A8_TYPELESS;
        if (!bgra && !rgba) return;
        const int areaW = std::max(64, 2 * halfW), areaH = std::max(48, 2 * halfH);
        const std::wstring key = s + L"|" + std::to_wstring(areaW) + L"x" + std::to_wstring(areaH) + L"|" + std::to_wstring(fontPx) + L"|" + std::to_wstring((int)fmt);
        TextTex& t = tt[e];
        if (t.key != key || !t.tex) {
            if (t.tex) { t.tex->Release(); t.tex = nullptr; }
            HDC dc = CreateCompatibleDC(nullptr);
            BITMAPINFO bi{}; bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER); bi.bmiHeader.biWidth = areaW; bi.bmiHeader.biHeight = -areaH; bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32; bi.bmiHeader.biCompression = BI_RGB;
            void* bits = nullptr; HBITMAP bmp = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
            HGDIOBJ oldB = SelectObject(dc, bmp);
            const RECT all{ 0, 0, areaW, areaH };
            HBRUSH br = CreateSolidBrush(RGB(32, 32, 32)); FillRect(dc, &all, br); DeleteObject(br);
            SetBkMode(dc, TRANSPARENT); SetTextColor(dc, RGB(255, 255, 255));
            // the largest font (down to 10 px) whose wrapped text fits the area
            int fp = fontPx; RECT calc{ 0, 0, areaW - 16, 0 }; HFONT font = nullptr, oldF = nullptr;
            for (;; fp = std::max(10, fp - 2)) {
                if (font) { SelectObject(dc, oldF); DeleteObject(font); }
                font = CreateFontW(-fp, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");
                oldF = (HFONT)SelectObject(dc, font);
                calc = { 0, 0, areaW - 16, 0 }; DrawTextW(dc, s.c_str(), -1, &calc, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX);
                if (calc.bottom <= areaH - 16 || fp <= 10) break;
            }
            RECT at{ 8, (LONG)std::max(8, (int)((areaH - calc.bottom) / 2)), (LONG)areaW - 8, (LONG)areaH - 8 };
            DrawTextW(dc, s.c_str(), -1, &at, DT_WORDBREAK | DT_NOPREFIX);
            SelectObject(dc, oldF); DeleteObject(font);
            std::vector<uint32_t> out((size_t)areaW * areaH);
            const uint32_t* src = (const uint32_t*)bits;
            for (size_t i = 0; i < out.size(); i++) {
                const uint32_t v = src[i]; const uint32_t B = v & 255, G = (v >> 8) & 255, R = (v >> 16) & 255;
                out[i] = 0xFF000000u | (bgra ? (R << 16 | G << 8 | B) : (B << 16 | G << 8 | R));
            }
            SelectObject(dc, oldB); DeleteObject(bmp); DeleteDC(dc);
            D3D11_TEXTURE2D_DESC cd{}; cd.Width = areaW; cd.Height = areaH; cd.MipLevels = 1; cd.ArraySize = 1; cd.Format = fmt; cd.SampleDesc.Count = 1; cd.Usage = D3D11_USAGE_IMMUTABLE; cd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA sd{ out.data(), (UINT)areaW * 4, 0 };
            if (FAILED(dev->CreateTexture2D(&cd, &sd, &t.tex))) { t.tex = nullptr; return; }
            t.key = key; t.w = areaW; t.h = areaH;
        }
        D3D11_TEXTURE2D_DESC ed{}; eyeTex->GetDesc(&ed);
        int dx = cx - t.w / 2, dy = cy - t.h / 2;
        D3D11_BOX src{ 0, 0, 0, (UINT)t.w, (UINT)t.h, 1 };
        if (dx < 0) { src.left = -dx; dx = 0; }
        if (dy < 0) { src.top = -dy; dy = 0; }
        if (dx + (int)(src.right - src.left) > (int)ed.Width) src.right = src.left + (ed.Width - dx);
        if (dy + (int)(src.bottom - src.top) > (int)ed.Height) src.bottom = src.top + (ed.Height - dy);
        if (src.right > src.left && src.bottom > src.top) ctx->CopySubresourceRegion(eyeTex, 0, dx, dy, 0, t.tex, 0, &src);
    }
};
