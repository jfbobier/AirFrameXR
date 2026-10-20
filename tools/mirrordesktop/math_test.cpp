// Tests of mirror_math.h (harness/mirror_check.py): the screen's placement for every desktop shape, the lazy follow, the projection into an eye, the controller's
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

    // the four positions: the screen stays in its quarter until the head is 55 degrees from it, then moves to the nearest quarter (a quarter turn)
    check(quadrantFor(rad(40), 0, 0) == 0 && quadrantFor(rad(-50), 0, 0) == 0, "quarters: a head turned 40 or 50 degrees leaves the screen where it is");
    check(quadrantFor(rad(60), 0, 0) == 1 && quadrantFor(rad(-60), 0, 0) == 3, "quarters: 60 degrees one way is the next quarter, the other way the previous one");
    check(quadrantFor(rad(100), 0, 1) == 1 && quadrantFor(rad(30), 0, 1) == 0, "quarters: hysteresis (100 degrees stays in quarter 1; back at 30 degrees it returns to 0)");
    check(quadrantFor(rad(179), 0, 0) == 2 && quadrantFor(rad(-179), 0, 0) == 2, "quarters: behind the user, from either side");
    check(quadrantFor(rad(95), rad(90), 0) == 0, "quarters count from the reference yaw (the head's yaw at the start)");
    check(near(cardinal(0, 0).z, -1.f) && near(cardinal(0, 1).x, -1.f) && near(cardinal(0, 2).z, 1.f) && near(cardinal(0, 3).x, 1.f), "quarters: 0 ahead, 1 to the left (turning left is positive yaw), 2 behind, 3 to the right");
    check(near(headYaw(yaw(90)), rad(90)) && near(headYaw(ID), 0.f), "head yaw: a left turn is positive, straight ahead is 0");
    Screen q1 = placeFacing(cardinal(0, 1), { 0, 1.5f, 0 }, .5625f);
    check(near(q1.center.x, -1.5f) && near(q1.normal.x, 1.f), "the screen in quarter 1 is on the left, facing the user");
    // walking: 10 cm leaves it, 20 cm makes it follow (it keeps its quarter)
    bool mv = false; Screen w0 = place(ID, { 0, 1.5f, 0 }, .5625f);
    follow(w0, place(ID, { 0.1f, 1.5f, 0 }, .5625f), mv, 0.004f); check(!mv, "walking 10 cm leaves the screen where it is");
    Screen w2 = follow(w0, place(ID, { 0.2f, 1.5f, 0 }, .5625f), mv, 0.004f); check(mv && w2.center.x > w0.center.x, "walking 20 cm makes the screen follow");

    // follow: small turns leave it alone, big ones make it glide round, and it ends facing the user
    bool moving = false;
    Screen s10 = follow(s, placeFacing(cardinal(0, 0), { 0, 1.5f, 0 }, .5625f), moving, 0.004f);   // a 10 degree head turn: the screen's quarter has not changed
    check(!moving && near(s10.normal.x, s.normal.x), "follow: a head turn inside the quarter leaves the screen where it is");
    Screen tg = place(yaw(60), { 0, 1.5f, 0 }, .5625f), c = s;
    for (int i = 0; i < 600; i++) c = follow(c, tg, moving, 0.004f);
    check(dot(c.normal, tg.normal) > 0.9995f && !moving && near(c.width, tg.width), "follow: 60 degrees, it glides in front of the user again");
    // walking away 2 m
    c = s; moving = false;
    Screen far = place(ID, { 2.f, 1.5f, 0 }, .5625f);
    for (int i = 0; i < 600; i++) c = follow(c, far, moving, 0.004f);
    check(near(c.center.x, far.center.x, 0.05f), "follow: walking away, the screen comes along");

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
    printf("math_test: %s\n", fails ? "FAILED" : "OK");
    return fails ? 1 : 0;
}
