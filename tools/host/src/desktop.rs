//! Desktop mirror between sessions (docs/PLAN_0.5.md, phase 5): the Windows desktop on a virtual screen in front of the user, the hand as the
//! mouse. This file is the maths and the input: where the screen is, where its corners fall in each eye's image, where the hand's ray meets it,
//! and the click state. The picture itself (desktop capture, HDR -> SDR, drawing the quad into the idle frame) is nvenc/hostlib/desktop.cpp.
//! Everything here is plain arrays and std, so the tests run anywhere (`rustc --test tools/host/src/desktop.rs`).
//!
//! Spaces: the headset's raw tracking space (y up, -Z forward at the start), the same one the hand poses arrive in. A quaternion is [x, y, z, w].

pub type V3 = [f32; 3];
pub type Q = [f32; 4];

fn sub(a: V3, b: V3) -> V3 { [a[0] - b[0], a[1] - b[1], a[2] - b[2]] }
fn add(a: V3, b: V3) -> V3 { [a[0] + b[0], a[1] + b[1], a[2] + b[2]] }
fn mul(a: V3, k: f32) -> V3 { [a[0] * k, a[1] * k, a[2] * k] }
fn dot(a: V3, b: V3) -> f32 { a[0] * b[0] + a[1] * b[1] + a[2] * b[2] }
fn cross(a: V3, b: V3) -> V3 { [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]] }
fn norm(a: V3) -> V3 { let l = dot(a, a).sqrt(); if l > 1e-6 { mul(a, 1.0 / l) } else { a } }

/// q * v
pub fn rotate(q: Q, v: V3) -> V3 {
    let u = [q[0], q[1], q[2]];
    let t = mul(cross(u, v), 2.0);
    add(add(v, mul(t, q[3])), cross(u, t))
}
pub fn conj(q: Q) -> Q { [-q[0], -q[1], -q[2], q[3]] }

/// The virtual screen: centre, the unit vectors along its width (right) and height (up), and its size in metres. It faces the user (normal = -forward).
#[derive(Clone, Copy, Debug)]
pub struct Screen {
    pub center: V3,
    pub right: V3,
    pub up: V3,
    pub normal: V3,
    pub width: f32,
    pub height: f32,
}

/// Places the screen `dist` metres in front of the head, at its yaw only (a screen does not tilt with the head), `fov_deg` wide, with the
/// desktop's aspect (height / width of the desktop in pixels).
pub fn place(head_q: Q, head_p: V3, dist: f32, fov_deg: f32, aspect: f32) -> Screen {
    let mut f = rotate(head_q, [0.0, 0.0, -1.0]);
    f[1] = 0.0;
    let f = if dot(f, f) < 1e-4 { [0.0, 0.0, -1.0] } else { norm(f) };   // looking straight up or down: keep the world forward
    let up = [0.0, 1.0, 0.0];
    let right = norm(cross(f, up));
    let width = 2.0 * dist * (fov_deg.to_radians() / 2.0).tan();
    Screen { center: add(add(head_p, mul(f, dist)), [0.0, -0.05, 0.0]), right, up, normal: mul(f, -1.0), width, height: width * aspect }
}

/// The screen's corners (top-left, top-right, bottom-left, bottom-right) in one eye's NDC (x right, y up, both -1..1 across the eye's field of
/// view), for the head pose, the eye's offset from the head and its field of view as tangents [left, right, up, down]. None when a corner is at or
/// behind the eye (the screen is out of view or too close to project).
pub fn eye_corners(s: &Screen, head_q: Q, head_p: V3, eye_off: V3, tan: [f32; 4]) -> Option<[[f32; 2]; 4]> {
    let eye = add(head_p, rotate(head_q, eye_off));
    let inv = conj(head_q);
    let (hw, hh) = (s.width / 2.0, s.height / 2.0);
    let mut out = [[0f32; 2]; 4];
    for (i, (x, y)) in [(-hw, hh), (hw, hh), (-hw, -hh), (hw, -hh)].iter().enumerate() {
        let c = add(add(s.center, mul(s.right, *x)), mul(s.up, *y));
        let v = rotate(inv, sub(c, eye));
        if v[2] > -0.01 { return None; }
        let (tx, ty) = (v[0] / -v[2], v[1] / -v[2]);
        out[i] = [(tx + tan[0]) / (tan[0] + tan[1]) * 2.0 - 1.0, (ty + tan[3]) / (tan[2] + tan[3]) * 2.0 - 1.0];
    }
    Some(out)
}

/// Where a ray (origin, unit direction) meets the screen, as (u, v) over the screen with (0, 0) at its top-left; None when it misses or points away.
pub fn ray_hit(s: &Screen, origin: V3, dir: V3) -> Option<(f32, f32)> {
    let denom = dot(dir, s.normal);
    if denom.abs() < 1e-5 { return None; }
    let t = dot(sub(s.center, origin), s.normal) / denom;
    if t <= 0.0 { return None; }
    let h = sub(add(origin, mul(dir, t)), s.center);
    let (u, v) = (dot(h, s.right) / s.width + 0.5, 0.5 - dot(h, s.up) / s.height);
    if (0.0..=1.0).contains(&u) && (0.0..=1.0).contains(&v) { Some((u, v)) } else { None }
}

/// The pointing ray of a hand pose: from the hand, along its -Z axis (the aim of the emulated controller).
pub fn hand_ray(q: Q, p: V3) -> (V3, V3) { (p, norm(rotate(q, [0.0, 0.0, -1.0]))) }

/// Mouse buttons from the hand's input: the right hand's trigger (or the pinch, which ALVR reports as the trigger for hands) is the left button, its
/// grip the right button. `changes` are the edges since the last update, to be sent to the system.
#[derive(Default, Debug, Clone, Copy, PartialEq)]
pub struct Buttons { pub left: bool, pub right: bool }

#[derive(Default)]
pub struct Mouse { pub buttons: Buttons }

#[derive(Debug, PartialEq, Clone, Copy)]
pub enum Edge { LeftDown, LeftUp, RightDown, RightUp }

impl Mouse {
    pub fn update(&mut self, now: Buttons) -> Vec<Edge> {
        let mut e = Vec::new();
        if now.left != self.buttons.left { e.push(if now.left { Edge::LeftDown } else { Edge::LeftUp }); }
        if now.right != self.buttons.right { e.push(if now.right { Edge::RightDown } else { Edge::RightUp }); }
        self.buttons = now;
        e
    }
}

/// Desktop pixel of a screen point, over the virtual desktop of `w` x `h` pixels.
pub fn to_pixel(u: f32, v: f32, w: i32, h: i32) -> (i32, i32) {
    (((u * w as f32) as i32).clamp(0, w - 1), ((v * h as f32) as i32).clamp(0, h - 1))
}

#[cfg(windows)]
pub mod inject {
    //! SendInput with absolute coordinates over the primary monitor (0..65535).
    use super::Edge;
    #[repr(C)] struct MouseInput { dx: i32, dy: i32, data: u32, flags: u32, time: u32, extra: usize }
    #[repr(C)] struct Input { kind: u32, mi: MouseInput }
    extern "system" { fn SendInput(n: u32, inputs: *const Input, size: i32) -> u32; }
    const MOVE: u32 = 0x0001; const ABSOLUTE: u32 = 0x8000;
    const LDOWN: u32 = 0x0002; const LUP: u32 = 0x0004; const RDOWN: u32 = 0x0008; const RUP: u32 = 0x0010;

    fn send(dx: i32, dy: i32, flags: u32) {
        let i = Input { kind: 0, mi: MouseInput { dx, dy, data: 0, flags, time: 0, extra: 0 } };
        unsafe { SendInput(1, &i, std::mem::size_of::<Input>() as i32) };
    }
    /// Moves the cursor to (u, v) over the primary monitor.
    pub fn move_to(u: f32, v: f32) { send((u.clamp(0.0, 1.0) * 65535.0) as i32, (v.clamp(0.0, 1.0) * 65535.0) as i32, MOVE | ABSOLUTE); }
    pub fn button(e: Edge) { send(0, 0, match e { Edge::LeftDown => LDOWN, Edge::LeftUp => LUP, Edge::RightDown => RDOWN, Edge::RightUp => RUP }); }
}

#[cfg(test)]
mod tests {
    use super::*;
    const ID: Q = [0.0, 0.0, 0.0, 1.0];
    const TAN: [f32; 4] = [1.2, 1.2, 1.2, 1.2];
    fn yaw(deg: f32) -> Q { let h = deg.to_radians() / 2.0; [0.0, h.sin(), 0.0, h.cos()] }
    fn near(a: f32, b: f32) -> bool { (a - b).abs() < 1e-3 }

    #[test]
    fn rotate_turns_forward_left() {
        let v = rotate(yaw(90.0), [0.0, 0.0, -1.0]);
        assert!(near(v[0], -1.0) && near(v[2], 0.0), "{v:?}");
    }

    #[test]
    fn screen_is_in_front_and_faces_the_user() {
        let s = place(ID, [0.0, 1.5, 0.0], 1.6, 60.0, 9.0 / 16.0);
        assert!(near(s.center[2], -1.6) && near(s.center[1], 1.45));
        assert!(near(s.normal[2], 1.0) && near(s.right[0], 1.0));
        assert!(near(s.width, 2.0 * 1.6 * 30f32.to_radians().tan()) && near(s.height, s.width * 9.0 / 16.0));
    }

    #[test]
    fn screen_follows_the_yaw_not_the_pitch() {
        let pitch_up: Q = { let h = 40f32.to_radians() / 2.0; [h.sin(), 0.0, 0.0, h.cos()] };
        let s = place(pitch_up, [0.0, 1.5, 0.0], 1.6, 60.0, 0.5625);
        assert!(near(s.center[1], 1.45), "a screen does not tilt with the head: {:?}", s.center);
        let s = place(yaw(90.0), [0.0, 1.5, 0.0], 1.6, 60.0, 0.5625);
        assert!(near(s.center[0], -1.6) && near(s.center[2], 0.0), "{:?}", s.center);
    }

    #[test]
    fn corners_are_symmetric_when_looking_at_the_centre() {
        let s = place(ID, [0.0, 1.5, 0.0], 1.6, 60.0, 0.5625);
        let c = eye_corners(&s, ID, [0.0, 1.5, 0.0], [0.0; 3], TAN).unwrap();
        assert!(near(c[0][0], -c[1][0]) && near(c[2][0], -c[3][0]) && near(c[0][0], c[2][0]), "left and right edges are symmetric and vertical");
        assert!(near(c[0][1], c[1][1]) && near(c[2][1], c[3][1]) && c[0][1] > c[2][1], "top above bottom");
        // 60 degree wide screen in a 2 x atan(1.2) = 100 degree view: tan(30)/1.2 = 0.48 of the half width
        assert!(near(c[1][0], 0.577350 / 1.2), "{:?}", c);
    }

    #[test]
    fn eyes_see_the_screen_shifted_by_the_parallax() {
        let s = place(ID, [0.0, 1.5, 0.0], 1.6, 60.0, 0.5625);
        let l = eye_corners(&s, ID, [0.0, 1.5, 0.0], [-0.032, 0.0, 0.0], TAN).unwrap();
        let r = eye_corners(&s, ID, [0.0, 1.5, 0.0], [0.032, 0.0, 0.0], TAN).unwrap();
        // the left eye sees it further right in its image than the right eye does
        assert!(l[0][0] > r[0][0] && (l[0][0] - r[0][0]) > 0.01 && (l[0][0] - r[0][0]) < 0.1);
    }

    #[test]
    fn a_screen_behind_the_user_is_not_projected() {
        let s = place(ID, [0.0, 1.5, 0.0], 1.6, 60.0, 0.5625);
        assert!(eye_corners(&s, yaw(180.0), [0.0, 1.5, 0.0], [0.0; 3], TAN).is_none());
    }

    #[test]
    fn turning_the_head_moves_the_screen_the_other_way() {
        let s = place(ID, [0.0, 1.5, 0.0], 1.6, 60.0, 0.5625);
        let a = eye_corners(&s, ID, [0.0, 1.5, 0.0], [0.0; 3], TAN).unwrap();
        let b = eye_corners(&s, yaw(10.0), [0.0, 1.5, 0.0], [0.0; 3], TAN).unwrap();   // head turned left: the screen moves right in the image
        assert!(b[0][0] > a[0][0] + 0.1);
    }

    #[test]
    fn a_ray_straight_at_the_centre_hits_the_middle() {
        let s = place(ID, [0.0, 1.5, 0.0], 1.6, 60.0, 0.5625);
        let (u, v) = ray_hit(&s, [0.0, 1.45, 0.3], [0.0, 0.0, -1.0]).unwrap();
        assert!(near(u, 0.5) && near(v, 0.5), "{u} {v}");
    }

    #[test]
    fn a_ray_to_the_right_edge_lands_on_the_right_of_the_desktop() {
        let s = place(ID, [0.0, 1.5, 0.0], 1.6, 60.0, 0.5625);
        let dir = norm([s.width / 2.0 - 0.01, 0.0, -1.6]);
        let (u, _) = ray_hit(&s, [0.0, 1.45, 0.0], dir).unwrap();
        assert!(u > 0.98 && u <= 1.0, "{u}");
        assert!(ray_hit(&s, [0.0, 1.45, 0.0], norm([s.width, 0.0, -1.6])).is_none(), "beyond the edge");
        assert!(ray_hit(&s, [0.0, 1.45, 0.0], [0.0, 0.0, 1.0]).is_none(), "pointing away");
    }

    #[test]
    fn up_is_up_on_the_desktop() {
        let s = place(ID, [0.0, 1.5, 0.0], 1.6, 60.0, 0.5625);
        let (_, v_hi) = ray_hit(&s, [0.0, 1.45, 0.0], norm([0.0, 0.3, -1.6])).unwrap();
        assert!(v_hi < 0.5, "aiming up reaches the upper half (v small): {v_hi}");
    }

    #[test]
    fn hand_ray_follows_the_hand_orientation() {
        let (_, d) = hand_ray(yaw(90.0), [0.0; 3]);
        assert!(near(d[0], -1.0));
    }

    #[test]
    fn click_edges() {
        let mut m = Mouse::default();
        assert!(m.update(Buttons::default()).is_empty());
        assert_eq!(m.update(Buttons { left: true, right: false }), vec![Edge::LeftDown]);
        assert!(m.update(Buttons { left: true, right: false }).is_empty(), "held: no repeat");
        assert_eq!(m.update(Buttons { left: false, right: true }), vec![Edge::LeftUp, Edge::RightDown]);
    }

    #[test]
    fn pixels_are_clamped_to_the_desktop() {
        assert_eq!(to_pixel(0.0, 0.0, 1920, 1080), (0, 0));
        assert_eq!(to_pixel(1.0, 1.0, 1920, 1080), (1919, 1079));
        assert_eq!(to_pixel(0.5, 0.5, 1920, 1080), (960, 540));
    }
}
