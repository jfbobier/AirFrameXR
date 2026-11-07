// Tests of mirror_math.h (harness/mirror_check.py): the screen's placement for every desktop shape, the one static position, the projection into an eye, the controller's
// ray against the screen, and the beam's clipping. Plain C++: g++ -std=c++17 math_test.cpp && ./a.out
#include "mirror_math.h"
#include <cstdio>
static int fails = 0;
static void check(bool ok, const char* name) { printf("%s %s\n", ok ? "PASS" : "FAIL", name); fails += ok ? 0 : 1; }
static bool near(float a, float b, float t = 1e-3f) { return std::fabs(a - b) < t; }
static Q yaw(float deg) { const float h = rad(deg) / 2; return { 0, std::sin(h), 0, std::cos(h) }; }
static const Q ID{ 0, 0, 0, 1 };
static Eye eye(Q q, V3 p) { return { q, p, -1.2f, 1.2f, 1.2f, -1.2f }; }

int main() {
    float u_, v_;
    // placement: 1.5 m ahead of a head looking down -Z, 56.5 degrees wide (1.6 m), a little below the eyes; facing the user
    Screen s = place(ID, { 0, 1.5f, 0 }, 9.f / 16.f);
    check(near(s.center.z, -1.5f) && near(s.center.x, 0) && near(s.center.y, 1.45f), "placed 1.5 m ahead, 5 cm below the eyes");
    check(near(s.width, 2 * 1.5f * std::tan(rad(28.25f))) && near(s.height, s.width * 9.f / 16.f) && s.width > 1.55f, "56.5 degrees wide (30 % more than before), the desktop's shape");
    check(near(s.normal.z, 1.f), "faces the user");
    // a tall desktop is limited to 40 degrees high
    Screen t = place(ID, { 0, 1.5f, 0 }, 1920.f / 1080.f);
    check(near(t.height, 2 * 1.5f * std::tan(rad(26.f))) && near(t.width / t.height, 1080.f / 1920.f, 1e-2f), "a portrait desktop is capped at 52 degrees high, not stretched");
    // the head looking straight up or down keeps the world forward; the head's pitch never tilts the screen
    Q up{ -0.7071068f, 0, 0, 0.7071068f };
    check(near(place(up, { 0, 1.5f, 0 }, .5625f).normal.z, 1.f), "looking straight up: the screen stays upright and ahead");
    // turning the head 90 degrees to the left: the screen is at the head's left
    Screen l = place(yaw(90), { 0, 1.5f, 0 }, .5625f);
    check(near(l.center.x, -1.5f) && near(l.center.z, 0), "turned left: the screen is placed to the left");

    // one position: the head's yaw at the start or at a recentre, then it stays
    check(near(headYaw(yaw(90)), rad(90)) && near(headYaw(ID), 0.f), "head yaw: a left turn is positive, straight ahead is 0");
    check(near(facingYaw(0).z, -1.f) && near(facingYaw(rad(90)).x, -1.f) && near(facingYaw(rad(180)).z, 1.f), "facing: 0 ahead, 90 degrees left is -X, 180 behind");
    Screen rc = placeFacing(facingYaw(headYaw(yaw(90))), { 0, 1.5f, 0 }, .5625f);
    check(near(rc.center.x, -1.5f) && near(rc.normal.x, 1.f), "a recentre with the head turned left puts the screen on the left, facing the user");
    // the grip: down at 0.7, held down until it falls under 0.3
    check(!gripDown(0.6f, false) && gripDown(0.75f, false) && gripDown(0.5f, true) && !gripDown(0.25f, true), "grip: down above 0.7, stays down until under 0.3");
    // the hint: under the screen, same plane, centred
    Screen hb = hintBelow(s, kBadgeW, kBadgeH);
    check(near(hb.center.x, s.center.x) && hb.center.y < s.center.y - s.height / 2 && near(hb.normal.z, s.normal.z) && near(hb.width, kBadgeW), "the hint badge is centred under the screen, in its plane");
    check(!rayHit(s, { 0, 1.45f, 0 }, norm(sub(hb.center, { 0, 1.45f, 0 })), u_, v_) && rayHit(hb, { 0, 1.45f, 0 }, norm(sub(hb.center, { 0, 1.45f, 0 })), u_, v_) && near(u_, .5f, 1e-2f), "a ray at the badge misses the desktop and hits the badge in its middle");

    // projection: the screen in front of the eye lands inside its view, symmetrical; behind it: none
    float n[8];
    check(corners(s, eye(ID, { 0, 1.5f, 0 }), n) && n[0] < 0 && n[2] > 0 && n[1] > n[5] && near(n[0], -n[2], 1e-3f), "the corners are inside the eye's view, top above bottom");
    check(!corners(s, eye(yaw(180), { 0, 1.5f, 0 }), n), "behind the eye: no corners");
    // a head turn moves the screen the other way in the view
    float m[8]; corners(s, eye(yaw(10), { 0, 1.5f, 0 }), m);
    check(m[0] > n[0], "turning the head left moves the screen to the right in the view");

    // the controller's ray: straight ahead hits the middle; aimed beside: misses; away from the screen: misses
    float u, v;
    check(rayHit(s, { 0, 1.45f, 0 }, { 0, 0, -1 }, u, v) && near(u, .5f) && near(v, .5f), "ray: straight at the centre hits (0.5, 0.5)");
    check(rayHit(s, { 0, 1.45f, 0 }, norm({ 0.2f, 0.1f, -1 }), u, v) && u > .5f && v < .5f, "ray: up and to the right hits right of centre, above it (v smaller)");
    check(!rayHit(s, { 0, 1.45f, 0 }, norm({ 3.f, 0, -1 }), u, v), "ray: far to the side misses");
    check(!rayHit(s, { 0, 1.45f, 0 }, { 0, 0, 1 }, u, v), "ray: pointing away misses");
    check(near(screenPoint(s, 0.5f, 0.5f).z, -1.5f) && screenPoint(s, 0.f, 0.f).x < 0 && screenPoint(s, 0.f, 0.f).y > s.center.y, "screen point (0,0) is the top-left");
    // a hand to the right of the head, aimed at the screen's right edge, hits u near 1
    check(rayHit(s, { 0.6f, 1.2f, -0.2f }, norm(sub(screenPoint(s, 0.98f, 0.5f), { 0.6f, 1.2f, -0.2f })), u, v) && near(u, 0.98f, 0.01f), "ray from the right hand hits where it is aimed");

    // the beam: from a hand below and beside the view to the screen, clipped in front of the eye; wholly behind: none
    float seg[4];
    check(segment(eye(ID, { 0, 1.5f, 0 }), { 0.2f, 1.2f, 0.1f }, { 0, 1.5f, -2.f }, seg) && std::isfinite(seg[0]) && std::isfinite(seg[3]), "beam: starts beside and behind the eye plane, still drawn");
    check(!segment(eye(ID, { 0, 1.5f, 0 }), { 0, 1.5f, 0.5f }, { 0, 1.5f, 1.5f }, seg), "beam: wholly behind the eye: nothing");
    // the beam's hand: a firm trigger press takes it, a light touch does not; the take-over press is swallowed until the trigger is released
    check(takesBeam(0.6f) && !takesBeam(0.4f), "beam hand: trigger above 0.5 takes it, less does not");
    check(!takeOverReleased(0.3f) && takeOverReleased(0.1f), "beam hand: the take-over press ends when the trigger is released");
    printf("math_test: %s\n", fails ? "FAILED" : "OK");
    return fails ? 1 : 0;
}
