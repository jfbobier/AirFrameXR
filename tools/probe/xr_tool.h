// The headset tools of docs/PICTURE_EDGES_DESIGN.md sections 6 and 6b, as scenes of the probe (an ordinary OpenXR app on the PC, streamed like a game):
//   XR_PROBE_TOOL=fit       "Fit the picture to your eyes": a mid-blue box on black (the scene inside the box when XR_PROBE_SCENE is set), one eye at a time, numpad 8 / 2 / 4 / 6 push an edge out until its black bar is gone.
//                           Enter = this eye is finished (the other eye comes; on the second eye it saves), Tab goes to the other eye and back.
//   XR_PROBE_TOOL=restrict  "Choose what the game renders": the scene (XR_PROBE_SCENE=<file.vab>, flat grey without it) inside the box, black outside, both eyes,
//                           numpad 8 / 2 / 4 / 6 pull an edge in; Space (held) shows the whole view. (The arrow keys are the same keys: the numpad sends them with Num Lock off.)
// Keys: the PC keyboard through GetAsyncKeyState (whatever window has the focus: with the headset on nobody can click a window back into focus), or scripted for the
// lab: XR_PROBE_KEYS="<frame>:<key>[,...]" with key = space left right up down tab enter esc, "shift+left" etc.; "+space" / "-space" press and release only (a
// plain key is a tap: down on that frame, up on the next). Frames count from the first rendered frame; scripted mode ignores the real keyboard.
// An Xbox controller (XInput, also read whatever window has the focus; for laptops without a numpad): the right stick expands the area in the stick's direction,
// the left stick reduces it (up / down / left / right = the edges, in the fit's line style the diagonals = the corners, like numpad 7 9 3 1; held, it repeats like
// a held key); A = Space on the first screen (start), then Enter (this eye is finished / save). Scripted: "rs_up", "ls_downleft", ... (a tap: deflected on that
// frame, centred on the next; "+rs_up" holds, "-rs" centres) and "a" / "+a" / "-a".
// Output: one JSON line per change on stdout (the tool app reads them): {"tool","event":"box|start|enter|esc","frame","eye","box":[[outer,inner,upper,lower] per eye],
// "panel":[...same...],"pct":..}; tangents, outer = away from the nose. XR_PROBE_TOOL_START = the fit's start box as a fraction of the panel (box style; the line style starts on the author's rays 0.97 / 0.71 / 0.69 / 0.63 capped at the panel, XR_PROBE_START_BOX replaces them),
// XR_PROBE_TOOL_SAVED_S = how long the "Saved" screen stays (default 3).
// The in-headset text is drawn with GDI into a bitmap and copied into the eye image (the plan said DirectWrite: same result, no Direct2D device needed), head-locked
// at the centre of the view axis inside 12 degrees, white on dark grey, about 1 degree cap height, only in the eye being fitted while the other is black.
#pragma once
#include <windows.h>
#include <d3d11_1.h>
#include <xinput.h>   // the structs only: XInputGetState is loaded from the system DLL (ReadPad)
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
    enum Key { KSpace, KLeft, KRight, KUp, KDown, KShift, KTab, KEnter, KEsc, KN7, KN9, KN3, KN1, KCount };   // KN7..KN1: numpad corners (upper-left, upper-right, lower-right, lower-left)
    float P[2][4] = {};     // the panel (what the runtime told) per eye: outer, inner, upper, lower tangents
    float box[2][4] = {};   // the box being shaped, same order
    // 1.4 mask (docs/FOV_CONTRACT_PLAN.md): per eye and corner (upper-outer, upper-inner, lower-inner, lower-outer) a straight line across the corner, perpendicular
    // to the direction from the view axis to the box's corner, at this fraction of the corner's distance (1 = through the corner: nothing cut). Pushed out until the lens
    // cuts it: the visible window is convex, so nothing beyond the line is visible. XR_PROBE_START_CORNERS = 8 fractions (left eye, then right eye).
    float corner[2][4] = { { 1, 1, 1, 1 }, { 1, 1, 1, 1 } };
    float pxTan[2] = {};    // stream pixels per tangent of the told view, per eye (set while drawing)
    int eye = 0;            // fit: the eye being fitted; restrict, one eye: the eye edited
    bool bothEyes = true;   // restrict
    bool compare = false;   // Space held: the whole view (restrict) / both eyes (fit)
    bool quit = false;
    bool havePanel = false;
    bool scripted = false;
    double startMs = 0, lastKeyMs = 0, savedMs = 0, savedFor = 3000;
    float startFrac = 0.4f;   // the box style; the line style starts at 0.85
    float startBox[4] = { 0.97f, 0.71f, 0.69f, 0.63f };   // XR_PROBE_START_BOX=o,i,u,d (outer, inner, upper, lower; the tool app passes the Vision Pro's window), capped at the panel
    bool useStartBox = false;
    HANDLE parentH = nullptr;   // XR_PROBE_PARENT_PID: the tool app that started this scene; when it is gone the scene ends (an orphan kept streaming stale textures into every new host)
    bool lineStyle = true;   // fit: four bright line segments (visible until the lens cuts them), not a filled box; XR_PROBE_FIT_STYLE=box = the old blue box (lab scenarios)
    int frame = 0;

    struct Ev { int frame; Key key; bool down; };
    std::vector<Ev> events; size_t nextEv = 0;
    bool down[KCount] = {}; double heldSince[KCount] = {}, lastRepeat[KCount] = {};

    bool On() const { return mode != None; }

    void Init(const char* tool, const char* keys) {
        if (!tool) return;
        mode = !strcmp(tool, "fit") ? Fit : !strcmp(tool, "restrict") ? Restrict : None;
        if (const char* s = getenv("XR_PROBE_FIT_STYLE")) lineStyle = strcmp(s, "box") != 0;
        if (const char* s = getenv("XR_PROBE_START_CORNERS")) { float v[8]; if (sscanf(s, "%f,%f,%f,%f,%f,%f,%f,%f", v, v + 1, v + 2, v + 3, v + 4, v + 5, v + 6, v + 7) == 8) for (int i = 0; i < 8; i++) corner[i / 4][i % 4] = std::max(0.4f, std::min(1.f, v[i])); }
        if (const char* s = getenv("XR_PROBE_START_BOX")) { float v[4]; if (sscanf(s, "%f,%f,%f,%f", v, v + 1, v + 2, v + 3) == 4 && v[0] > 0 && v[1] > 0 && v[2] > 0 && v[3] > 0) memcpy(startBox, v, sizeof v); }
        useStartBox = getenv("XR_PROBE_START_BOX") != nullptr && !getenv("XR_PROBE_TOOL_START");   // without it the line style starts at startFrac of the panel (85 %): any headset   // the restriction starts on the author's rays and shows the same bars only when the tool app says so (the lab's scenarios keep the whole view)
        if (const char* s = getenv("XR_PROBE_PARENT_PID")) { const DWORD pid = (DWORD)atoi(s); if (pid) parentH = OpenProcess(SYNCHRONIZE, FALSE, pid); }
        if (lineStyle) startFrac = 0.85f;
        if (const char* s = getenv("XR_PROBE_TOOL_START")) startFrac = std::max(0.2f, std::min(1.f, (float)atof(s)));
        if (const char* s = getenv("XR_PROBE_TOOL_SAVED_S")) savedFor = std::max(0.0, atof(s)) * 1000.0;
        if (keys && *keys) { scripted = true; ParseKeys(keys); }
    }

    static bool NameKey(const std::string& n, Key& k) {
        static const char* names[] = { "space", "left", "right", "up", "down", "shift", "tab", "enter", "esc", "n7", "n9", "n3", "n1" };
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
            if (ParsePad(fr, mode_, what)) continue;
            Key k; if (!NameKey(what, k)) continue;
            if (shift) events.push_back({ fr, KShift, true });
            if (mode_ == '-') events.push_back({ fr, k, false });
            else { events.push_back({ fr, k, true }); if (!mode_) events.push_back({ fr + 1, k, false }); }
            if (shift) events.push_back({ fr + 1, KShift, false });
        }
        std::stable_sort(events.begin(), events.end(), [](const Ev& a, const Ev& b) { return a.frame < b.frame; });
        std::stable_sort(padEvents.begin(), padEvents.end(), [](const PadEv& a, const PadEv& b) { return a.frame < b.frame; });
    }

    // ---- the Xbox controller --------------------------------------------------------------------------------------------------------------------------------------
    struct Pad { float lx = 0, ly = 0, rx = 0, ry = 0; bool a = false; };
    struct PadEv { int frame; int what; float x, y; };   // what: 0 left stick, 1 right stick (x, y), 2 A (x = 1 down, 0 up)
    std::vector<PadEv> padEvents; size_t nextPadEv = 0;
    Pad scriptedPad;
    bool padA = false, padShift = false, padPrev[KCount] = {};
    typedef DWORD(WINAPI* XInputGetStateFn)(DWORD, XINPUT_STATE*);
    XInputGetStateFn xget = nullptr; bool xtried = false; int xslot = -1; double xscanMs = -1e9;

    // A worn stick rests off its centre (drift), and the tool took any stick past half its travel for a key: a drifting controller moved the lines by
    // itself, which is why some users could not calibrate with one (owner, 2026-11-27). The community's practice, applied here: re-centre first, then a
    // scaled radial dead zone (Microsoft's XInput guide and its recommended sizes, 7849 and 8689 of 32767 for the left and right sticks; J. Sutphin,
    // "Doing thumbstick dead zones right"). The rest point is where the stick stays still for a second, learned again at every rest; a stick read inside
    // the dead zone at the start is taken as centred until then; one that rests further out than kMaxRest is worn beyond use: ignored, and the text says so.
    static constexpr float kDeadL = 7849.f / 32767.f, kDeadR = 8689.f / 32767.f, kMaxRest = 0.7f;
    struct StickCal {
        float cx = 0, cy = 0, sx = 0, sy = 0; double stillMs = 0; bool seen = false, known = false, broken = false;
        void Feed(float x, float y, float dz, double ms) {
            if (!seen) { seen = true; sx = x; sy = y; stillMs = ms; known = hypotf(x, y) <= dz; return; }
            if (hypotf(x - sx, y - sy) > 0.03f) { sx = x; sy = y; stillMs = ms; return; }
            const double still = ms - stillMs;
            if (still >= 1000.0 && hypotf(x, y) <= kMaxRest) { cx = x; cy = y; known = true; broken = false; }
            else if (still >= 3000.0 && !known && hypotf(x, y) > kMaxRest) broken = true;
        }
        // the deflection from the rest point, past the dead zone, rescaled over the travel left in that direction (a stick resting to the right still reaches 1 to the right)
        void Apply(float& x, float& y, float dz) const {
            if (!known || broken) { x = y = 0; return; }
            const float vx = x - cx, vy = y - cy, m = hypotf(vx, vy);
            if (m <= dz) { x = y = 0; return; }
            const float dx = vx / m, dy = vy / m, cd = cx * dx + cy * dy;
            const float reach = -cd + sqrtf(std::max(0.f, cd * cd - (cx * cx + cy * cy) + 1.f));   // from the rest point to the edge of a round gate
            const float k = std::min(1.f, (m - dz) / std::max(reach - dz, 1e-3f));
            x = dx * k; y = dy * k;
        }
    };
    StickCal calL, calR;
    // the pad as the keys see it: both sticks re-centred and through their dead zones
    Pad Condition(const Pad& raw, double nowMs) {
        calL.Feed(raw.lx, raw.ly, kDeadL, nowMs); calR.Feed(raw.rx, raw.ry, kDeadR, nowMs);
        Pad p = raw; calL.Apply(p.lx, p.ly, kDeadL); calR.Apply(p.rx, p.ry, kDeadR);
        return p;
    }

    bool ParsePad(int fr, char hold, const std::string& w) {
        if (w == "a") { if (hold != '-') padEvents.push_back({ fr, 2, 1, 0 }); if (hold != '+') padEvents.push_back({ hold == '-' ? fr : fr + 1, 2, 0, 0 }); return true; }
        if (w.size() < 2 || (w[0] != 'l' && w[0] != 'r') || w[1] != 's') return false;
        const int st = w[0] == 'l' ? 0 : 1;
        // a worn stick resting off centre (0.58 up and to the right: past the old half-travel rule, inside the dead zone's reach), for the drift scenario
        if (w.compare(2, std::string::npos, "_drift") == 0) { padEvents.push_back({ fr, st, 0.45f, 0.37f }); if (!hold) padEvents.push_back({ fr + 1, st, 0, 0 }); return true; }
        if (w.size() == 2) { if (hold == '-') padEvents.push_back({ fr, st, 0, 0 }); return hold == '-'; }
        if (w[2] != '_') return false;
        static const char* dn[8] = { "right", "upright", "up", "upleft", "left", "downleft", "down", "downright" };
        for (int i = 0; i < 8; i++)
            if (w.compare(3, std::string::npos, dn[i]) == 0) {
                const float a = i * 45.f * 0.0174532925f;
                padEvents.push_back({ fr, st, cosf(a), sinf(a) });
                if (!hold) padEvents.push_back({ fr + 1, st, 0, 0 });
                return true;
            }
        return false;
    }

    // XInput from the system DLL (no link dependency): the first connected controller; an empty slot is slow to poll, so they are looked for once a second
    bool ReadPad(double nowMs, Pad& p) {
        if (!xtried) {
            xtried = true;
            HMODULE m = LoadLibraryW(L"xinput1_4.dll");
            if (!m) m = LoadLibraryW(L"xinput9_1_0.dll");
            if (m) xget = (XInputGetStateFn)GetProcAddress(m, "XInputGetState");
        }
        if (!xget) return false;
        auto read = [&](int i) {
            XINPUT_STATE st{};
            if (xget((DWORD)i, &st) != ERROR_SUCCESS) return false;
            const XINPUT_GAMEPAD& g = st.Gamepad;
            auto ax = [](SHORT v) { return std::max(-1.f, std::min(1.f, v / 32767.f)); };
            p.lx = ax(g.sThumbLX); p.ly = ax(g.sThumbLY); p.rx = ax(g.sThumbRX); p.ry = ax(g.sThumbRY);
            p.a = (g.wButtons & XINPUT_GAMEPAD_A) != 0;
            return true;
        };
        if (xslot >= 0 && read(xslot)) return true;
        xslot = -1;
        if (nowMs - xscanMs < 1000.0) return false;
        xscanMs = nowMs;
        for (int i = 0; i < 4; i++) if (read(i)) { xslot = i; return true; }
        return false;
    }

    // The key a pad state stands for: the stick pushed further wins; past half its travel its direction picks an edge (4 sectors) or, in the fit's line style, an edge or
    // a corner (8 sectors). The right stick expands, the left reduces: in the fit a plain key pushes out, in the restriction Shift does, so `shift` follows the mode.
    void PadKeys(const Pad& p, bool k[KCount], bool& shift) const {
        const float lm = hypotf(p.lx, p.ly), rm = hypotf(p.rx, p.ry);
        const bool right = rm >= lm;
        shift = false;
        if (std::max(lm, rm) < 0.5f) return;
        const float x = right ? p.rx : p.lx, y = right ? p.ry : p.ly;
        const float ang = atan2f(y, x) * 57.2957795f + 360.f;   // 0 = right, 90 = up
        if (mode == Fit && lineStyle) {
            static const Key ks[8] = { KRight, KN9, KUp, KN7, KLeft, KN1, KDown, KN3 };
            k[ks[(int)((ang + 22.5f) / 45.f) % 8]] = true;
        } else {
            static const Key ks[4] = { KRight, KUp, KLeft, KDown };
            k[ks[(int)((ang + 45.f) / 90.f) % 4]] = true;
        }
        shift = (mode == Fit) ? !right : right;
    }

    static float Deg(float t, float d) { return tanf(atanf(t) + d * 0.0174532925f); }

    // ---- per frame: the panel from the told views, the keys, the state -------------------------------------------------------------------------------------------
    void Update(int fr, double nowMs, const XrFovf fov[2]) {
        frame = fr;
        if (parentH && WaitForSingleObject(parentH, 0) == WAIT_OBJECT_0) { quit = true; return; }
        for (int e = 0; e < 2; e++) {
            const float tl = -tanf(fov[e].angleLeft), tr = tanf(fov[e].angleRight), tu = tanf(fov[e].angleUp), td = -tanf(fov[e].angleDown);
            const float np[4] = { e == 0 ? tl : tr, e == 0 ? tr : tl, tu, td };
            bool changed = !havePanel;
            for (int k = 0; k < 4; k++) if (fabsf(np[k] - P[e][k]) > 1e-5f) changed = true;
            if (changed && havePanel) {   // the panel changed under the tool (a restart told another view): keep the same fractions
                for (int k = 0; k < 4; k++) box[e][k] *= np[k] / std::max(P[e][k], 1e-4f);
            }
            memcpy(P[e], np, sizeof np);
            if (!havePanel) for (int k = 0; k < 4; k++) box[e][k] = useStartBox ? std::min(np[k], startBox[k]) : np[k] * (mode == Fit ? startFrac : 1.f);
        }
        if (!havePanel) { havePanel = true; startMs = nowMs; lastKeyMs = nowMs; Emit("box"); }
        bool pressed[KCount] = {};
        Pad pad; bool havePad = false;
        if (scripted) {
            while (nextEv < events.size() && events[nextEv].frame <= fr) { const Ev& v = events[nextEv++]; if (v.down && !down[v.key]) pressed[v.key] = true; down[v.key] = v.down; }
            while (nextPadEv < padEvents.size() && padEvents[nextPadEv].frame <= fr) {
                const PadEv& v = padEvents[nextPadEv++];
                if (v.what == 0) { scriptedPad.lx = v.x; scriptedPad.ly = v.y; } else if (v.what == 1) { scriptedPad.rx = v.x; scriptedPad.ry = v.y; } else scriptedPad.a = v.x > 0.5f;
            }
            pad = scriptedPad; havePad = !padEvents.empty();
            if (havePad) pad = Condition(pad, nowMs);
            // a scripted stick steps once per deflection (the lab's frames, not the clock, decide)
            bool pk[KCount] = {}; if (havePad) PadKeys(pad, pk, padShift);
            for (int k = 0; k < KCount; k++) { if (pk[k] && !padPrev[k]) pressed[k] = true; padPrev[k] = pk[k]; }
        } else {
            // the numpad: 8 / 2 / 4 / 6 are the arrows (with Num Lock off they send them anyway), 7 / 9 / 3 / 1 the corners (Num Lock off: Home / Page Up / Page Down / End)
            static const int vk[KCount] = { VK_SPACE, VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN, VK_SHIFT, VK_TAB, VK_RETURN, VK_ESCAPE, VK_NUMPAD7, VK_NUMPAD9, VK_NUMPAD3, VK_NUMPAD1 };
            static const int vk2[KCount] = { 0, VK_NUMPAD4, VK_NUMPAD6, VK_NUMPAD8, VK_NUMPAD2, 0, 0, 0, 0, VK_HOME, VK_PRIOR, VK_NEXT, VK_END };
            havePad = ReadPad(nowMs, pad);
            if (havePad) pad = Condition(pad, nowMs);
            bool pk[KCount] = {}; padShift = false; if (havePad) PadKeys(pad, pk, padShift);
            for (int k = 0; k < KCount; k++) {
                const bool d = (GetAsyncKeyState(vk[k]) & 0x8000) != 0 || (vk2[k] && (GetAsyncKeyState(vk2[k]) & 0x8000) != 0) || pk[k];
                if (d && !down[k]) { pressed[k] = true; heldSince[k] = lastRepeat[k] = nowMs; }
                else if (d && ((k >= KLeft && k <= KDown) || k >= KN7) && nowMs - heldSince[k] > 400 && nowMs - lastRepeat[k] > 70) { pressed[k] = true; lastRepeat[k] = nowMs; }   // repeats while held
                down[k] = d;
            }
        }
        // A: Space on the first screen (start), then Enter (this eye is finished / save); never the held Space (compare)
        if (havePad) {
            if (pad.a && !padA && phase != Saved) pressed[phase == Intro ? KSpace : KEnter] = true;
            padA = pad.a;
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
        const bool shift = down[KShift] || padShift;
        for (int a = KLeft; a <= KDown; a++) if (pressed[a]) { Arrow((Key)a, shift); changed = true; }
        if (mode == Fit) for (int a = KN7; a <= KN1; a++) if (pressed[a]) { Corner((Key)a, shift); changed = true; }
        if (changed) Emit("box");
        if (pressed[KEnter]) {
            if (mode == Fit && eye == 0) { eye = 1; Emit("box"); }   // the first eye is finished: the other eye comes
            else { Emit("enter"); phase = Saved; savedMs = nowMs; }
        }
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

    // the corner a numpad key moves in the eye being fitted: screen corners, the left eye's outer side is its left one
    void Corner(Key a, bool shift) {
        const int e = eye;
        const bool upper = a == KN7 || a == KN9, left = a == KN7 || a == KN1;
        const bool outer = left == (e == 0);
        const int c = upper ? (outer ? 0 : 1) : (outer ? 3 : 2);   // upper-outer, upper-inner, lower-inner, lower-outer
        corner[e][c] = shift ? std::max(0.4f, corner[e][c] - 0.01f) : std::min(1.f, corner[e][c] + 0.01f);
    }

    float Pct() const {   // the share of the panel's pixels the box keeps (both eyes)
        float g = 0, v = 0;
        for (int e = 0; e < 2; e++) { g += (box[e][0] + box[e][1]) * (box[e][2] + box[e][3]); v += (P[e][0] + P[e][1]) * (P[e][2] + P[e][3]); }
        return v > 0 ? 100.f * g / v : 100.f;
    }

    void Emit(const char* ev) const {
        printf("{\"tool\":\"%s\",\"event\":\"%s\",\"frame\":%d,\"eye\":%d,\"both\":%d,\"box\":[[%.5f,%.5f,%.5f,%.5f],[%.5f,%.5f,%.5f,%.5f]],\"panel\":[[%.5f,%.5f,%.5f,%.5f],[%.5f,%.5f,%.5f,%.5f]],\"pct\":%.2f,\"corners\":[[%.3f,%.3f,%.3f,%.3f],[%.3f,%.3f,%.3f,%.3f]]}\n",
               mode == Fit ? "fit" : "restrict", ev, frame, eye, bothEyes ? 1 : 0,
               box[0][0], box[0][1], box[0][2], box[0][3], box[1][0], box[1][1], box[1][2], box[1][3],
               P[0][0], P[0][1], P[0][2], P[0][3], P[1][0], P[1][1], P[1][2], P[1][3], Pct(),
               corner[0][0], corner[0][1], corner[0][2], corner[0][3], corner[1][0], corner[1][1], corner[1][2], corner[1][3]);
        fflush(stdout);
    }

    // ---- drawing --------------------------------------------------------------------------------------------------------------------------------------------------
    struct TextTex { ID3D11Texture2D* tex = nullptr; std::wstring key; int w = 0, h = 0; };
    TextTex tt[2];

    // the text, with a word when a stick is worn beyond use (StickCal)
    std::wstring Text(int e, bool& show) const {
        std::wstring t = BaseText(e, show);
        if (show && (calL.broken || calR.broken))
            t += calL.broken && calR.broken ? L"\n(Both sticks of the controller drift too far to be used: use the numpad.)"
                                            : calL.broken ? L"\n(The left stick drifts too far to be used: the right stick and the numpad still work.)"
                                                          : L"\n(The right stick drifts too far to be used: the left stick and the numpad still work.)";
        return t;
    }
    std::wstring BaseText(int e, bool& show) const {
        show = true;
        if (mode == Fit) {
            if (lineStyle && phase == Intro) return L"Fit the picture to your eyes.\nMove each line with the numpad: 8 2 4 6 the yellow edges (up, down, left, right), 7 9 1 3 the blue corners. Push a line out until it disappears, then bring it back with Shift + the same key until you just see it.\nXbox controller: right stick pushes the line in its direction out, left stick brings it back; diagonals for the corners.\nEnter or A: this eye is finished (the second eye saves). Esc: quit.\nPress SPACE or A to start.";
            if (phase == Intro) return L"Fit the picture to your eyes.\nNumpad 8 2 4 6 push an edge out until its black bar is gone. Shift + the same key pulls it back. Xbox controller: right stick out, left stick back.\nHold SPACE: compare. Enter or A: this eye is finished (the second eye saves). Esc: quit.\nPress SPACE or A to start.";
            if (phase == Saved) return L"Saved. You can take the headset off.";
            if (compare) { show = false; return L""; }
            return std::wstring(e == 0 ? L"LEFT EYE: close your right eye" : L"RIGHT EYE: close your left eye") + L"\n" + (lineStyle ? L"numpad 8 2 4 6 edges, 7 9 1 3 corners (or right stick out, left stick back): push a line out until it disappears, back until you just see it; then ENTER or A" : L"push until the bar is gone, one step back, then ENTER or A");
        }
        if (useStartBox && phase == Intro) return L"Choose what the game renders.\nFour bright lines mark the edges of the picture the game renders; outside them it is black. Numpad 8 2 4 6 move a line inward, Shift + the same key outward (Xbox controller: left stick in, right stick out), until you just notice the black at that edge, then one step back. Tab: both eyes / one eye. Space: compare with the whole view. Enter or A: save. Esc: quit.\nPress SPACE or A to start.";
        if (phase == Intro) return L"Choose what the game renders.\nNumpad 8 2 4 6 pull an edge in, Shift + the same key pushes it back out (Xbox controller: left stick in, right stick out). Tab: both eyes / one eye. Space: compare with the whole view. Enter or A: save. Esc: quit.\nPress SPACE or A to start.";
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
            if (sceneDrawn && !lineStyle) {   // the scene the probe drew is inside the box, black outside; the eye that is not being fitted is black
                if (!eyeShown) fill(0, 0, W, H, black);
                else { fill(0, 0, W, y0, black); fill(0, y1, W, H, black); fill(0, y0, x0, y1, black); fill(x1, y0, W, y1, black); }
            }
            else c1->ClearView(rtv, black, nullptr, 0);   // (the line style never shows the scene: lines on black)
            if (eyeShown && lineStyle) {   // four bright segments on the edges (each a fraction of its edge, so a rounded window does not matter), about half a degree thick
                static const float yellow[4] = { 1.f, 0.92f, 0.15f, 1 };
                const float lt = std::max(6.f, ppt * 0.0087f), cxm = (x0 + x1) / 2, cym = (y0 + y1) / 2, hl = 0.22f * (y1 - y0), hw = 0.22f * (x1 - x0);
                fill(x0 - lt / 2, cym - hl, x0 + lt / 2, cym + hl, yellow); fill(x1 - lt / 2, cym - hl, x1 + lt / 2, cym + hl, yellow);
                fill(cxm - hw, y0 - lt / 2, cxm + hw, y0 + lt / 2, yellow); fill(cxm - hw, y1 - lt / 2, cxm + hw, y1 + lt / 2, yellow);
                // the four corner lines (1.4 mask), dotted: square dots along the line across each corner, inside the box
                static const float cyan[4] = { 0.25f, 0.85f, 1.f, 1 };
                const float bo = b[0], bi = b[1], bu = b[2], bd = b[3];
                for (int c = 0; c < 4; c++) {
                    const bool outer = c == 0 || c == 3, upper = c < 2;
                    const float cxT = (outer == (e == 0)) ? -(outer ? bo : bi) : (outer ? bo : bi), cyT = upper ? bu : -bd;   // the box's corner, screen tangents
                    const float n = sqrtf(cxT * cxT + cyT * cyT), f = corner[e][c];
                    if (n < 1e-4f) continue;
                    const float mx = f * cxT, my = f * cyT, ux = -cyT / n, uy = cxT / n;   // the line's middle and its direction
                    const float half = 0.18f * std::min(bo + bi, bu + bd);
                    for (int i = -20; i <= 20; i++) {
                        const float s = half * i / 20.f, tx = mx + s * ux, ty = my + s * uy;
                        if (tx < -(e == 0 ? bo : bi) || tx > (e == 0 ? bi : bo) || ty > bu || ty < -bd) continue;
                        const float qx = px(tx), qy = py(ty);
                        fill(qx - lt / 2, qy - lt / 2, qx + lt / 2, qy + lt / 2, cyan);
                    }
                }
            }
            else if (eyeShown && !sceneDrawn) { fill(x0, y0, x1, y1, blue); fill(x0, y0, x1, y0 + lw, line); fill(x0, y1 - lw, x1, y1, line); fill(x0, y0, x0 + lw, y1, line); fill(x1 - lw, y0, x1, y1, line); }
        } else {
            if (!sceneDrawn) { c1->ClearView(rtv, grey, nullptr, 0); const float mid[4] = { 0.3f, 0.34f, 0.4f, 1 }; fill(x0, y0, x1, y1, mid); }
            fill(0, 0, W, y0, black); fill(0, y1, W, H, black); fill(0, y0, x0, y1, black); fill(x1, y0, W, y1, black);
            if (useStartBox && !compare) {   // the same bars as the fit, on the edges of the box
                static const float yellow[4] = { 1.f, 0.92f, 0.15f, 1 };
                const float lt = std::max(6.f, ppt * 0.0087f), cxm = (x0 + x1) / 2, cym = (y0 + y1) / 2, hl = 0.22f * (y1 - y0), hw = 0.22f * (x1 - x0);
                fill(x0 - lt / 2, cym - hl, x0 + lt / 2, cym + hl, yellow); fill(x1 - lt / 2, cym - hl, x1 + lt / 2, cym + hl, yellow);
                fill(cxm - hw, y0 - lt / 2, cxm + hw, y0 + lt / 2, yellow); fill(cxm - hw, y1 - lt / 2, cxm + hw, y1 + lt / 2, yellow);
            }
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
