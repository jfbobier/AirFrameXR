//! The host's stall detector and memory line (docs/permanent_record/STABILITY_PLAN.md, phase 0; scenarios in harness/stability/). Release builds carry both: one thread waking four times a second,
//! comparing a few counters, writing a line only when something is wrong (and one memory line every 30 s while a headset is connected).
//!
//! Why: the gears 2026-10-20 stalls left nothing in a log written with debug off but a statistics line missing for 38 s. A stall now names itself:
//! the whole process not running (frozen, descheduled, suspended: this thread late too), the main loop blocked, the headset's tracking silent,
//! or no frame sent, with the other states at that moment, and the end of the episode with its length.
use crate::{event, logs, nvh, Shared};
use serde_json::json;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

/// the main loop's last pass (ns since the host's origin, 0 = not started)
pub static MAIN_BEAT_NS: AtomicU64 = AtomicU64::new(0);

/// Called once per pass of the main loop.
pub fn main_beat(origin: Instant) {
    MAIN_BEAT_NS.store((origin.elapsed().as_nanos() as u64).max(1), Ordering::Relaxed);
}

const TICK: Duration = Duration::from_millis(250);
/// how long a counter may stand still while a headset is connected before it is called a stall
const STALL: Duration = Duration::from_millis(1500);
const MEMORY_EVERY: Duration = Duration::from_secs(30);/// A "no frame sent" episode is INFO when it starts: a healthy run has one at nearly every game start (the game loading, 2-3 s in) and exit (5 of 5 runs of
/// 2026-11-13), and the host cannot know in advance which one it is. It becomes a WARN only if it is still going after this long (a game that hung).
const FRAMES_WARN_AFTER: Duration = Duration::from_secs(5);

/// Whether the open "no frame sent" episode has lasted long enough for its one WARN (not warned yet, still open, `age_s` since the last frame).
fn frames_escalates(warned: bool, open: bool, age_s: f64) -> bool {
    !warned && open && age_s >= FRAMES_WARN_AFTER.as_secs_f64()
}

/// One thing that can stall: starts an episode when it stood still for `STALL`, ends it when it moves again.
struct Watch {
    name: &'static str,
    last: u64,
    since: Instant,
    open: Option<Instant>,
}

impl Watch {
    fn new(name: &'static str, now: Instant) -> Self {
        Self { name, last: u64::MAX, since: now, open: None }
    }
    /// `value` changes while the thing works; `armed` is false while it is expected to be quiet. Returns Some(true) when an episode starts, Some(false) when one ends.
    fn update(&mut self, value: u64, armed: bool, now: Instant) -> Option<bool> {
        if value != self.last || !armed {
            self.last = value;
            self.since = now;
            return self.open.take().map(|_| false);
        }
        if self.open.is_none() && now.duration_since(self.since) >= STALL {
            self.open = Some(self.since);
            return Some(true);
        }
        None
    }
    fn age(&self, now: Instant) -> f64 {
        now.duration_since(self.since).as_secs_f64()
    }
}

pub fn start(origin: Instant, shared: Arc<Shared>) {
    std::thread::Builder::new()
        .name("watchdog".into())
        .spawn(move || run(origin, shared))
        .ok();
}

fn run(origin: Instant, shared: Arc<Shared>) {
    let t0 = Instant::now();
    let (mut tracking, mut frames, mut main) = (Watch::new("tracking", t0), Watch::new("frames", t0), Watch::new("main loop", t0));
    let mut last_tick = t0;
    let mut last_memory = t0;
    let mut frames_warned = false;
    loop {
        std::thread::sleep(TICK);
        let now = Instant::now();
        // this thread late too: the whole process did not run (suspended, a driver or the OS holding every thread, a page-out storm)
        let late = now.duration_since(last_tick);
        last_tick = now;
        if late >= STALL + TICK {
            logs::general("WARN", &format!("stall detected: the host process did not run for {:.1} s (every thread, this watchdog included: frozen or descheduled by the system)", late.as_secs_f64()));
            event(origin, "stall_detected", json!({ "kind": "process", "seconds": late.as_secs_f64() }));
            for w in [&mut tracking, &mut frames, &mut main] {
                w.last = u64::MAX; // the counters could not move meanwhile: start counting again
                w.since = now;
                w.open = None;
            }
            continue;
        }
        let connected = shared.client_connected.load(Ordering::SeqCst);
        let main_ns = MAIN_BEAT_NS.load(Ordering::Relaxed);
        let ticks = [
            tracking.update(shared.tracking_events.load(Ordering::Relaxed) as u64, connected, now),
            frames.update(shared.frames_sent.load(Ordering::Relaxed) as u64, connected, now),
            main.update(main_ns, main_ns != 0, now),
        ];
        let names = [tracking.name, frames.name, main.name];
        for (i, t) in ticks.iter().enumerate() {
            match t {
                Some(true) => {
                    let level = if names[i] == "frames" { "INFO" } else { "WARN" }; // frames: WARN only if it lasts (FRAMES_WARN_AFTER, below)
                    logs::general(level, &format!("stall detected: {} has not moved for {:.1} s (tracking {:.1} s, frames sent {:.1} s, main loop {:.1} s since last change)",
                        names[i], STALL.as_secs_f64(), tracking.age(now), frames.age(now), main.age(now)));
                    event(origin, "stall_detected", json!({ "kind": names[i], "tracking_s": tracking.age(now), "frames_s": frames.age(now), "main_s": main.age(now) }));
                }
                Some(false) => {
                    if names[i] == "frames" {
                        frames_warned = false;
                    }
                    logs::general("INFO", &format!("stall ended: {} moves again", names[i]));
                    event(origin, "stall_ended", json!({ "kind": names[i] }));
                }
                None => {}
            }
        }
        if frames_escalates(frames_warned, frames.open.is_some(), frames.age(now)) {
            frames_warned = true;
            logs::general("WARN", &format!("stall continues: frames has not moved for {:.1} s (tracking {:.1} s, main loop {:.1} s since last change)", frames.age(now), tracking.age(now), main.age(now)));
            event(origin, "stall_continues", json!({ "kind": "frames", "frames_s": frames.age(now), "tracking_s": tracking.age(now), "main_s": main.age(now) }));
        }
        if connected && now.duration_since(last_memory) >= MEMORY_EVERY {
            last_memory = now;
            let mut b = [0 as std::ffi::c_char; 256];
            if unsafe { nvh::nvh_mem_sample(b.as_mut_ptr(), b.len() as i32) } > 0 {
                let row = unsafe { std::ffi::CStr::from_ptr(b.as_ptr()) }.to_string_lossy().to_string();
                let v: Vec<u64> = row.split(',').filter_map(|x| x.parse().ok()).collect();
                if v.len() == 9 {
                    logs::run_line(logs::Level::Info, "memory", format!("memory: host {} MB private ({} MB working set), {} handles; RAM {} of {} MB free; GPU {} of {} MB used, host process {} MB of a {} MB VRAM budget",
                        v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]));
                    event(origin, "memory", json!({ "private_mb": v[0], "ws_mb": v[1], "handles": v[2], "ram_free_mb": v[3], "ram_total_mb": v[4],
                        "gpu_used_mb": v[5], "gpu_total_mb": v[6], "host_vram_mb": v[7], "vram_budget_mb": v[8] }));
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_frames_stall_warns_once_and_only_when_it_lasts() {
        // a healthy game start / exit: 1.5 s to a few seconds, no WARN
        assert!(!frames_escalates(false, true, 1.5));
        assert!(!frames_escalates(false, true, 4.9));
        // a hung game: one WARN at 5 s, not again while the same episode goes on
        assert!(frames_escalates(false, true, 5.0));
        assert!(!frames_escalates(true, true, 9.0));
        // no open episode: nothing, whatever the age
        assert!(!frames_escalates(false, false, 30.0));
    }

    #[test]
    fn an_episode_opens_after_the_stall_time_and_closes_when_it_moves() {
        let t0 = Instant::now();
        let mut w = Watch::new("frames", t0);
        assert_eq!(w.update(1, true, t0), None);
        assert_eq!(w.update(1, true, t0 + Duration::from_millis(1000)), None);
        assert_eq!(w.update(1, true, t0 + Duration::from_millis(1600)), Some(true));
        assert_eq!(w.update(1, true, t0 + Duration::from_millis(3000)), None);
        assert_eq!(w.update(2, true, t0 + Duration::from_millis(3100)), Some(false));
    }
}
