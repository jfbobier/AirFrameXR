// The maths of the desktop mirror: where the screen is (in front of the user, following them), where its corners fall in each eye, where the controller's
// ray meets it. Plain C++ (no OpenXR, no D3D), so harness/mirror_check.py can test it anywhere (tools/mirrordesktop/math_test.cpp).
#pragma once
#include <algorithm>
#include <cmath>

// ------------------------------------------------------------------------------------------------------------------ the maths
struct V3 { float x, y, z; };
struct Q { float x, y, z, w; };
static V3 add(V3 a, V3 b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
static V3 sub(V3 a, V3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
static V3 mul(V3 a, float k) { return { a.x * k, a.y * k, a.z * k }; }
static float dot(V3 a, V3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
static V3 cross(V3 a, V3 b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
static V3 norm(V3 a) { const float l = std::sqrt(dot(a, a)); return l > 1e-6f ? mul(a, 1.f / l) : a; }
static V3 rotate(Q q, V3 v) { const V3 u{ q.x, q.y, q.z }; const V3 t = mul(cross(u, v), 2.f); return add(add(v, mul(t, q.w)), cross(u, t)); }
static Q conj(Q q) { return { -q.x, -q.y, -q.z, q.w }; }

// One eye: its pose and the tangents of its field of view (left and down are negative).
struct Eye { Q q; V3 p; float tl, tr, tu, td; };

struct Screen { V3 center, right, up, normal; float width, height; };
static const float kDistM = 1.5f, kFovDeg = 56.5f, kMaxTallDeg = 52.f;   // a large monitor at arm's length: 56.5 degrees wide (1.6 m) at 1.5 m
static const float kPi = 3.14159265358979f;
static float rad(float d) { return d * kPi / 180.f; }

// The screen has one position: in front of the user's head (its horizontal direction) when the mirror starts or when a grip is pressed, and it stays there.
static float headYaw(Q q) { const V3 f = rotate(q, { 0, 0, -1 }); return std::atan2(-f.x, -f.z); }
static V3 facingYaw(float yaw) { return { -std::sin(yaw), 0.f, -std::cos(yaw) }; }
// A grip press recentres the screen: down at 0.7, up again at 0.3 (a firm squeeze, one recentre per squeeze).
static bool gripDown(float grip, bool was) { return grip > (was ? 0.3f : 0.7f); }

// In front of the head in the horizontal direction `f` (a screen does not tilt with the head), a desktop-shaped rectangle facing the user.
static Screen placeFacing(V3 f, V3 hp, float aspect) {
    f.y = 0; f = dot(f, f) < 1e-4f ? V3{ 0, 0, -1 } : norm(f);
    const V3 up{ 0, 1, 0 };
    float width = 2.f * kDistM * std::tan(rad(kFovDeg) / 2.f), height = width * aspect;
    const float maxH = 2.f * kDistM * std::tan(rad(kMaxTallDeg) / 2.f);
    if (height > maxH) { height = maxH; width = height / aspect; }
    return { add(add(hp, mul(f, kDistM)), V3{ 0, -0.05f, 0 }), norm(cross(f, up)), up, mul(f, -1.f), width, height };
}
// The head's own direction (what the old continuous placement used): in front of the user wherever they look.
static Screen place(Q hq, V3 hp, float aspect) { return placeFacing(rotate(hq, { 0, 0, -1 }), hp, aspect); }
// NDC of a point in one eye (x right, y up, -1..1 across the view's field of view); false when it is at or behind the eye.
static bool ndcOf(const Eye& v, V3 p, float nearZ, float out[2]) {
    const V3 c = rotate(conj(v.q), sub(p, v.p));
    if (c.z > nearZ) return false;
    out[0] = (c.x / -c.z - v.tl) / (v.tr - v.tl) * 2.f - 1.f; out[1] = (c.y / -c.z - v.td) / (v.tu - v.td) * 2.f - 1.f;
    return true;
}
static bool corners(const Screen& s, const Eye& v, float out[8]) {
    const float hw = s.width / 2, hh = s.height / 2; const float xy[4][2] = { { -hw, hh }, { hw, hh }, { -hw, -hh }, { hw, -hh } };   // TL TR BL BR
    for (int i = 0; i < 4; i++) if (!ndcOf(v, add(add(s.center, mul(s.right, xy[i][0])), mul(s.up, xy[i][1])), -0.01f, out + i * 2)) return false;
    return true;
}
// The beam's two ends in one eye, clipped a hand's width in front of the eye (the controller is below and often beside the view).
static bool segment(const Eye& v, V3 a, V3 b, float out[4]) {
    const Q inv = conj(v.q); const V3 e = v.p;
    V3 va = rotate(inv, sub(a, e)), vb = rotate(inv, sub(b, e));
    const float N = -0.05f;
    if (va.z > N && vb.z > N) return false;
    if (va.z > N) va = add(va, mul(sub(vb, va), (N - va.z) / (vb.z - va.z)));
    if (vb.z > N) vb = add(vb, mul(sub(va, vb), (N - vb.z) / (va.z - vb.z)));
    auto f = [&](V3 p, float* o) { o[0] = (p.x / -p.z - v.tl) / (v.tr - v.tl) * 2.f - 1.f; o[1] = (p.y / -p.z - v.td) / (v.tu - v.td) * 2.f - 1.f; };
    f(va, out); f(vb, out + 2);
    return true;
}
static bool rayHit(const Screen& s, V3 o, V3 d, float& u, float& v) {
    const float den = dot(d, s.normal);
    if (std::fabs(den) < 1e-5f) return false;
    const float t = dot(sub(s.center, o), s.normal) / den;
    if (t <= 0.f) return false;
    const V3 h = sub(add(o, mul(d, t)), s.center);
    u = dot(h, s.right) / s.width + 0.5f; v = 0.5f - dot(h, s.up) / s.height;
    return u >= 0.f && u <= 1.f && v >= 0.f && v <= 1.f;
}
// The beam's hand: a firm press of the trigger (above 0.5) on the controller that does not hold it takes it over; that press is then swallowed (it does not
// click) until the trigger is released.
static bool takesBeam(float trigger) { return trigger > 0.5f; }
static bool takeOverReleased(float trigger) { return trigger < 0.15f; }
static V3 screenPoint(const Screen& s, float u, float v) { return add(add(s.center, mul(s.right, (u - 0.5f) * s.width)), mul(s.up, (0.5f - v) * s.height)); }

// The controls hint: a small badge under the screen ("? controls"); the beam on it opens the panel (the commands) in the same place, larger. Same plane as the screen.
static const float kBadgeW = 0.30f, kBadgeH = 0.075f, kPanelW = 0.96f, kPanelH = 0.48f, kHintGapM = 0.04f;
static Screen hintBelow(const Screen& s, float w, float h) {
    return { add(s.center, mul(s.up, -(s.height / 2 + kHintGapM + h / 2))), s.right, s.up, s.normal, w, h };
}

// A thumbstick as a d-pad (the left stick of a controller sends arrow keys): one arrow per PUSH. A push is a deflection past 0.6 that is clearly along one axis (at least 1.5
// times the other); the stick then stays latched on that direction, whatever it does, until it comes back under 0.3 (the hysteresis) or is pushed clearly along ANOTHER
// direction past 0.6. Holding it sends nothing more: the user pushes again for the next arrow, like pressing the d-pad again. Returns 0 (nothing), 1 up, 2 down, 3 left, 4 right.
enum { kArrowNone = 0, kArrowUp, kArrowDown, kArrowLeft, kArrowRight };
struct StickArrows {
    int latched = kArrowNone;
    int step(float x, float y) {
        const float ax = std::fabs(x), ay = std::fabs(y), m = std::hypot(x, y);
        if (m < 0.3f) { latched = kArrowNone; return kArrowNone; }   // released: the next push counts
        if (m < 0.6f) return kArrowNone;                              // not far enough yet (a latched stick stays latched down to 0.3)
        int d = kArrowNone;
        if (ax >= 1.5f * ay) d = x > 0 ? kArrowRight : kArrowLeft;
        else if (ay >= 1.5f * ax) d = y > 0 ? kArrowUp : kArrowDown;  // y > 0 is up on an OpenXR thumbstick
        if (d == kArrowNone || d == latched) return kArrowNone;
        latched = d;
        return d;
    }
};
