//! The desktop mirror's runtime state (maths and input in desktop.rs, the picture in nvenc/hostlib/desktop.cpp): where the user's head and right
//! hand are, the virtual screen, the pointer, and what the idle frame needs. Used from the tracking loop (poses, buttons, views), from the
//! pipeline's idle frame, and from the launcher's `mirror on|off` command. Nothing happens unless it is enabled, and the mouse is only moved while
//! no game runs (the game owns the hand then).
use crate::desktop::{self, Buttons, Mouse, Q, Screen, V3};
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::Mutex;
use std::time::Duration;

/// `mirror on|off` from the launcher: -1 nothing, 0 off, 1 on (taken by the main loop).
pub static MIRROR_REQ: AtomicI32 = AtomicI32::new(-1);
static ENABLED: AtomicBool = AtomicBool::new(false);
/// A game is running: the mirror is not drawn and the mouse is left alone.
static GAME: AtomicBool = AtomicBool::new(false);

/// A monitor-sized screen at arm's length: 45 degrees wide at 1.5 m (about 1.24 m, a 16:9 desktop 0.7 m tall), easy to point at with a hand.
const DIST_M: f32 = 1.5;
const FOV_DEG: f32 = 45.0;

#[derive(Default)]
struct State {
    head: Option<(Duration, Q, V3)>,
    views: Option<([f32; 8], [f32; 6])>,
    screen: Option<Screen>,
    place: bool,
    moving: bool,
    last_head: Option<Duration>,
    desk: (i32, i32),
    started: bool,
    cursor: Option<(f32, f32)>,
    mouse: Mouse,
}
static STATE: Mutex<Option<State>> = Mutex::new(None);

fn with<R>(f: impl FnOnce(&mut State) -> R) -> R {
    let mut g = STATE.lock().unwrap();
    f(g.get_or_insert_with(State::default))
}

pub fn enabled() -> bool { ENABLED.load(Ordering::Relaxed) }
pub fn set_game(running: bool) { GAME.store(running, Ordering::Relaxed); }
fn idle() -> bool { enabled() && !GAME.load(Ordering::Relaxed) }

pub fn set_enabled(on: bool) {
    if ENABLED.swap(on, Ordering::Relaxed) != on {
        crate::logs::general("INFO", &format!("desktop mirror {}", if on { "ON" } else { "off" }));
        with(|s| { s.place = true; s.cursor = None; });
    }
}

/// The screen is placed in front of the head at the next head sample (the recenter key, or enabling, does it).
pub fn replace() { with(|s| s.place = true); }

pub fn on_views(fov8: [f32; 8], off6: [f32; 6]) { with(|s| s.views = Some((fov8, off6))); }

pub fn on_head(ts: Duration, q: Q, p: V3) {
    if !enabled() { return; }
    with(|s| {
        let dt = s.last_head.map(|l| ts.saturating_sub(l).as_secs_f32()).unwrap_or(0.0);
        s.last_head = Some(ts);
        s.head = Some((ts, q, p));
        if s.desk.0 > 0 {
            let target = desktop::place(q, p, DIST_M, FOV_DEG, s.desk.1 as f32 / s.desk.0 as f32);
            // placed in front at once when enabled or recentred; afterwards it follows the user lazily (small head movements leave it alone)
            match s.screen {
                Some(cur) if !s.place => { let mut mv = s.moving; s.screen = Some(desktop::follow(&cur, &target, &mut mv, dt)); s.moving = mv; }
                _ => { s.screen = Some(target); s.place = false; s.moving = false; }
            }
        }
    });
}

/// The right hand's pose: the pointer. Moves the real cursor while the mirror is on and no game runs.
pub fn on_right_hand(pose: Option<(Q, V3)>) {
    if !idle() { return; }
    let hit = with(|s| {
        let h = match (pose, s.screen) {
            (Some((q, p)), Some(sc)) => { let (o, d) = desktop::hand_ray(q, p); desktop::ray_hit(&sc, o, d) }
            _ => None,
        };
        s.cursor = h;
        h
    });
    #[cfg(windows)]
    if let Some((u, v)) = hit { desktop::inject::move_to(u, v); }
    let _ = hit;
}

/// Trigger (pinch for hands) = left button, grip = right button; only while the pointer is on the screen.
pub fn on_buttons(trigger: f32, squeeze: f32) {
    if !idle() { return; }
    let edges = with(|s| {
        let on_screen = s.cursor.is_some();
        s.mouse.update(Buttons { left: on_screen && trigger > 0.5, right: on_screen && squeeze > 0.7 })
    });
    #[cfg(windows)]
    for e in edges { desktop::inject::button(e); }
    let _ = edges;
}

/// What the idle frame draws: the screen's corners in both eyes' NDC, the cursor, and the head sample's timestamp the frame has to carry.
pub fn frame_params() -> Option<([f32; 16], [f32; 2], Duration)> {
    if !idle() { return None; }
    with(|s| {
        let (ts, q, p) = s.head?;
        let (fov, off) = s.views?;
        let sc = s.screen?;
        let mut ndc = [f32::NAN; 16];
        let mut any = false;
        for e in 0..2 {
            let tan = [fov[e * 4], fov[e * 4 + 1], fov[e * 4 + 2], fov[e * 4 + 3]];
            if let Some(c) = desktop::eye_corners(&sc, q, p, [off[e * 3], off[e * 3 + 1], off[e * 3 + 2]], tan) {
                for (i, v) in c.iter().enumerate() { ndc[e * 8 + i * 2] = v[0]; ndc[e * 8 + i * 2 + 1] = v[1]; }
                any = true;
            }
        }
        let cur = s.cursor.map(|(u, v)| [u, v]).unwrap_or([-1.0, -1.0]);
        if any { Some((ndc, cur, ts)) } else { None }
    })
}

/// Starts the capture the first time the idle frame is wanted (and again after a stop); the desktop's size feeds the screen's aspect.
pub fn ensure_started(handle: *mut std::ffi::c_void) -> bool {
    if with(|s| s.started) { return true; }
    let (mut w, mut h, mut hdr) = (0i32, 0i32, 0i32);
    let mut err = [0 as std::ffi::c_char; 256];
    let ok = unsafe { crate::nvh::nvh_desk_start(handle, &mut w, &mut h, &mut hdr, err.as_mut_ptr(), 256) } != 0;
    if ok {
        crate::logs::general("INFO", &format!("desktop mirror: capturing {w}x{h}{}", if hdr != 0 { " (HDR, converted to SDR)" } else { "" }));
        with(|s| { s.started = true; s.desk = (w, h); s.place = true; });
    } else {
        let msg = unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }.to_string_lossy().to_string();
        crate::logs::general("WARN", &format!("desktop mirror unavailable: {msg}"));
        set_enabled(false);
    }
    ok
}

pub fn stop(handle: *mut std::ffi::c_void) {
    if with(|s| std::mem::replace(&mut s.started, false)) { unsafe { crate::nvh::nvh_desk_stop(handle) }; }
}
