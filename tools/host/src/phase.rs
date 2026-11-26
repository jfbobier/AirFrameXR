//! Phase lock to the headset's display clock.
//!
//! The headset reports, for every frame it displayed, how long the decoded frame waited for its render tick (`decoder_queue`: decoded -> the
//! compositor starts on it). The controller slides the host's display grid (the clock thread's tick times, main.rs) a little later or
//! earlier so that the 90th percentile of those waits sits at `margin` ms; the game's release, the pick, the encode and the send all follow
//! that grid, so everything moves together. The grid's period comes from the headset's own timestamps (`display_clock.rs`), so the lock only
//! corrects the phase, in steps: at most 2 ms, one per two evaluations of about 90 reports each, nothing inside a 1.2 ms dead band.
//!
//! The wait is taken as it is. A wait longer than a period means the frame stood behind another in the headset's queue: sliding the grid
//! cannot empty that queue (one frame still leaves per tick), so such frames stay out of the statistic, and a window made mostly of them is
//! skipped; the client removes them (the Vision Pro's target pick, the Quest's buffering limit). A wait near 0 is a frame the pick's wait
//! caught: late. Until 1.5.0 the lock folded every wait into one period and steered the earliest 10 % of a circular cluster to 7 ms. That
//! made any target above half a period unreachable (at 100 Hz a true 7 read -3), so it slid the grid earlier for the whole session (-0.2 to
//! -0.8 ms/s in every field log), and it could not see the standing frame. Measured against it on a Vision Pro (2026-11-26, Gears of War
//! E-Day x3 and Dead Space x2, docs/PLAN_E2E_FRAME_TIMING.md 10.3): with the client's target pick and this lock the decoded frame waits
//! 5.0 ms instead of 9.9, and the lock settles (10 corrections a minute against 29, no slide).
//!
//! AIRFRAMEXR_PHASE_LOCK=0 disables it; AIRFRAMEXR_PHASE_MARGIN_MS (default 8) sets the target. 8 against 6 (the same evening): the frames are
//! ready 1.4 ms earlier, which costs 1.0 ms and drops a third fewer frames in the headset (15 against 23 per 1000).
use alvr_common::parking_lot::Mutex;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};
use std::time::Instant;

static PERIOD_NS: AtomicU64 = AtomicU64::new(10_000_000);
static ADJUST_NS: AtomicI64 = AtomicI64::new(0);
static ENABLED: AtomicBool = AtomicBool::new(true);
static STATE: Mutex<Option<State>> = Mutex::new(None);
static ORIGIN: std::sync::OnceLock<Instant> = std::sync::OnceLock::new();

/// the target for the 90th percentile of the waits, ms
const DEFAULT_MARGIN_MS: f64 = 8.0;
/// a wait over this many periods stood behind another frame
const STANDING: f64 = 1.05;

pub fn set_origin(o: Instant) {
    let _ = ORIGIN.set(o);
}

/// the host's start time (the origin of every event's t_ms)
pub fn origin() -> Option<Instant> {
    ORIGIN.get().copied()
}

struct State {
    waits_ms: Vec<f64>,
    ignore: u32,
    last: Instant,
    total_ms: f64,
    corrections: u64,
    recent: Vec<f64>,
}

pub fn enabled() -> bool {
    ENABLED.load(Ordering::Relaxed)
}

pub fn configure(on: bool) {
    ENABLED.store(on && std::env::var("AIRFRAMEXR_PHASE_LOCK").map(|v| v != "0").unwrap_or(true), Ordering::Relaxed);
}

/// the host's display period (ns) as the clock thread last set it
pub fn period_ns() -> u64 {
    PERIOD_NS.load(Ordering::Relaxed)
}

/// the host's display period (the clock thread, every tick)
pub fn set_period_ns(ns: u64) {
    PERIOD_NS.store(ns.max(1_000_000), Ordering::Relaxed);
}

/// a new connection: forget the old phase picture
pub fn reset() {
    *STATE.lock() = None;
    ADJUST_NS.store(0, Ordering::Relaxed);
}

/// the clock thread takes the pending correction (ns, signed: later > 0) once per tick
pub fn take_adjust_ns() -> i64 {
    ADJUST_NS.swap(0, Ordering::Relaxed)
}

/// (total slide so far in ms, number of corrections) for the logs
pub fn stats() -> (f64, u64) {
    STATE.lock().as_ref().map(|s| (s.total_ms, s.corrections)).unwrap_or((0.0, 0))
}

/// The statistic of one window of waits (ms, sorted in place): the 90th percentile of the frames that did not stand behind another,
/// None when they are fewer than a third of the window (nothing to steer on).
fn estimate(waits_ms: &mut [f64], period_ms: f64) -> Option<f64> {
    waits_ms.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let alone = waits_ms.iter().take_while(|w| **w <= period_ms * STANDING).count();
    if alone == 0 || alone < waits_ms.len() / 3 {
        return None;
    }
    Some(waits_ms[(alone * 9 / 10).min(alone - 1)])
}

/// One headset report: the decoded frame waited `queue_ms` for the headset's render tick.
pub fn observe(queue_ms: f64) {
    if !enabled() || !queue_ms.is_finite() {
        return;
    }
    let period_ms = PERIOD_NS.load(Ordering::Relaxed) as f64 / 1e6;
    let margin = std::env::var("AIRFRAMEXR_PHASE_MARGIN_MS").ok().and_then(|v| v.parse::<f64>().ok()).unwrap_or(DEFAULT_MARGIN_MS);
    let mut g = STATE.lock();
    let s = g.get_or_insert_with(|| State { waits_ms: vec![], ignore: 12, last: Instant::now(), total_ms: 0.0, corrections: 0, recent: vec![] });
    if s.ignore > 0 {
        s.ignore -= 1;
        return;
    }
    s.waits_ms.push(queue_ms);
    if s.waits_ms.len() < 90 || s.last.elapsed().as_millis() < 1000 {
        return;
    }
    let n = s.waits_ms.len();
    let p90 = estimate(&mut s.waits_ms, period_ms);
    let standing = s.waits_ms.iter().filter(|w| **w > period_ms * STANDING).count() as f64 / n as f64;
    s.waits_ms.clear();
    s.last = Instant::now();
    let Some(p90) = p90 else {
        return; // mostly standing frames: nothing to steer on in this window
    };
    // the estimate is noisy (a percentile of ~90 samples): act on the median of the last three, and only outside a dead band
    s.recent.push(p90);
    if s.recent.len() > 3 {
        s.recent.remove(0);
    }
    let mut r = s.recent.clone();
    r.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let est = r[r.len() / 2];
    let err = est - margin;
    let acted = s.recent.len() >= 2 && err.abs() > 1.2;
    if acted {
        let step = (0.3 * err).clamp(-2.0, 2.0); // positive: arrivals too early, slide the grid later
        ADJUST_NS.store((step * 1e6) as i64, Ordering::Relaxed);
        s.total_ms += step;
        s.corrections += 1;
        s.ignore = 12;
        s.recent.clear();
        crate::logs::run_line(crate::logs::Level::Verbose, "phase", format!("phase lock: waits p90 {est:.1} ms (target {margin:.1}), sliding the grid {step:+.2} ms"));
    }
    if let Some(o) = ORIGIN.get() {
        crate::event(*o, "phase_lock", serde_json::json!({ "p90_ms": p90, "est_ms": est, "standing": standing, "period_ms": period_ms, "target_ms": margin,
            "acted": acted, "total_slide_ms": s.total_ms, "corrections": s.corrections }));
    }
}

#[cfg(test)]
mod tests {
    use super::estimate;

    #[test]
    fn standing_frames_stay_out_of_the_estimate() {
        // 60 frames waiting 1 to 7 ms and 30 standing behind another (11 to 14 ms): the 90th percentile of the 60
        let mut w: Vec<f64> = (0..60).map(|i| 1.0 + 6.0 * i as f64 / 59.0).chain((0..30).map(|i| 11.0 + i as f64 / 10.0)).collect();
        let p = estimate(&mut w, 10.0).unwrap();
        assert!((p - 6.5).abs() < 0.1, "{p}");
    }

    #[test]
    fn a_window_of_mostly_standing_frames_is_skipped() {
        let mut w: Vec<f64> = (0..20).map(|_| 3.0).chain((0..70).map(|_| 12.0)).collect();
        assert!(estimate(&mut w, 10.0).is_none());
    }

    #[test]
    fn a_wait_of_seven_reads_seven() {
        // 1.5.0 folded the wait at half a period and read a true 7 ms as -3
        let mut w = vec![7.0; 90];
        assert_eq!(estimate(&mut w, 10.0), Some(7.0));
    }
}
