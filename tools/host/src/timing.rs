//! Precise waits for the clock and sender threads: a high-resolution waitable timer for the bulk of the wait, then a short spin for the
//! last stretch. Windows' own timer slack decides the spin margin (the timer wakes up to ~0.5 ms late at worst); the old sleep-then-spin spun
//! 1-1.5 ms per tick, a large share of a core at 100 Hz. `AIRFRAMEXR_SPIN_US` overrides the margin (default 600).
use std::time::{Duration, Instant};

#[cfg(windows)]
mod sys {
    use std::ffi::c_void;
    extern "system" {
        pub fn CreateWaitableTimerExW(attrs: *mut c_void, name: *const u16, flags: u32, access: u32) -> *mut c_void;
        pub fn SetWaitableTimer(timer: *mut c_void, due: *const i64, period: i32, cb: *mut c_void, arg: *mut c_void, resume: i32) -> i32;
        pub fn WaitForSingleObject(h: *mut c_void, ms: u32) -> u32;
    }
    pub const HIGH_RESOLUTION: u32 = 0x2;
    pub const TIMER_ALL_ACCESS: u32 = 0x1F0003;
}

fn spin_margin() -> Duration {
    static M: std::sync::OnceLock<Duration> = std::sync::OnceLock::new();
    *M.get_or_init(|| Duration::from_micros(std::env::var("AIRFRAMEXR_SPIN_US").ok().and_then(|v| v.parse().ok()).unwrap_or(600)))
}

#[cfg(windows)]
fn timer_wait(d: Duration) -> bool {
    use std::cell::Cell;
    thread_local! { static TIMER: Cell<usize> = Cell::new(0); }
    TIMER.with(|c| {
        if c.get() == 0 {
            let h = unsafe { sys::CreateWaitableTimerExW(std::ptr::null_mut(), std::ptr::null(), sys::HIGH_RESOLUTION, sys::TIMER_ALL_ACCESS) };
            c.set(h as usize); // stays 0 when the OS has no high-resolution timers (before Windows 10 1803)
        }
        let h = c.get() as *mut std::ffi::c_void;
        if h.is_null() {
            return false;
        }
        let due = -((d.as_nanos() / 100) as i64).max(1); // relative, 100 ns units
        unsafe {
            sys::SetWaitableTimer(h, &due, 0, std::ptr::null_mut(), std::ptr::null_mut(), 0) != 0 && sys::WaitForSingleObject(h, 1000) == 0
        }
    })
}

#[cfg(not(windows))]
fn timer_wait(d: Duration) -> bool {
    std::thread::sleep(d);
    true
}

/// Waits until `t`: timer for all but the last `spin_margin`, spin for the rest. Returns at once when `t` has passed.
pub fn sleep_until(t: Instant) {
    let margin = spin_margin();
    let left = t.saturating_duration_since(Instant::now());
    if left > margin + Duration::from_micros(100) && !timer_wait(left - margin) {
        std::thread::sleep(left - margin - Duration::from_micros(400)); // no high-resolution timer: the old behaviour
    }
    while Instant::now() < t {
        std::hint::spin_loop();
    }
}

/// Like `sleep_until` but gives up early when `stop` says so (checked once after the timer wait and every spin round).
pub fn sleep_until_or(t: Instant, stop: &std::sync::atomic::AtomicBool) {
    let margin = spin_margin();
    let left = t.saturating_duration_since(Instant::now());
    if left > margin + Duration::from_micros(100) && !timer_wait(left - margin) {
        std::thread::sleep(left - margin - Duration::from_micros(400));
    }
    while Instant::now() < t && !stop.load(std::sync::atomic::Ordering::Relaxed) {
        std::hint::spin_loop();
    }
}
