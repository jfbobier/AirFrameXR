//! Phase lock to the headset's display clock.
//!
//! The headset reports, for every frame it displayed, how long the decoded frame waited for its render tick (`decoder_queue`: decoded -> the
//! compositor starts on it; the visionOS client renders once per display tick, a fixed lead before the vsync, so `vsync_queue`, the rest, is constant). Together with the
//! host's own display period that gives the phase of our frames' arrival relative to the headset's ticks: a frame that arrives just after a tick
//! waits almost a whole period (latency), one that arrives just before it risks missing it (a repeated picture). Without a lock the phase is
//! whatever the connection start happened to produce and drifts with every clock difference.
//!
//! The controller keeps the earliest arrivals about `margin` ms before the tick by sliding the host's display grid (the clock thread's tick
//! times, main.rs) a little later or earlier; the game's release, the pick, the encode and the send all follow that grid, so everything moves
//! together. It works on the circular distribution of arrival phases (a missed tick is one period more queue time, the same phase), takes the
//! cluster that starts after the largest empty gap, and steers the 10th percentile of that cluster to `margin`. Corrections are small, at most
//! one every half second, and the window is cleared after each (the effect takes a pipeline's time to show).
//!
//! The loop must be closed: a slide of `step` ms moves the arrivals by about the same amount the other way. A headset whose reports do not follow (a constant queue, as the
//! lab's mock client reports, or a client that does not report one worth the name) would leave the controller sliding the grid for ever (2 ms every 2 s, measured 2026-11-06:
//! 8 ms in 14 s); two corrections in a row that moved the estimate by less than a third of their step switch the lock off for the rest of the connection.
//!
//! AIRFRAMEXR_PHASE_LOCK=0 disables it; AIRFRAMEXR_PHASE_MARGIN_MS (default 7) sets the margin. The margin was 3 ms until 2026-10-17: with
//! an arrival spread of 8 ms a share of the frames landed after their tick and were shown a tick late, which the user saw as a loss of
//! smoothness; a debug session that happened to delay every frame by 4.6 ms showed the cure (headset missed periods 0.5 % against 1.2-2.3 %,
//! latency unchanged at the headset's own quantum), and 7 ms by hand was confirmed on Valor Mortis ("much better").
use alvr_common::parking_lot::Mutex;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};
use std::time::Instant;

static PERIOD_NS: AtomicU64 = AtomicU64::new(10_000_000);
static ADJUST_NS: AtomicI64 = AtomicI64::new(0);
static ENABLED: AtomicBool = AtomicBool::new(true);
static STATE: Mutex<Option<State>> = Mutex::new(None);
static ORIGIN: std::sync::OnceLock<Instant> = std::sync::OnceLock::new();

pub fn set_origin(o: Instant) {
    let _ = ORIGIN.set(o);
}

/// the host's start time (the origin of every event's t_ms)
pub fn origin() -> Option<Instant> {
    ORIGIN.get().copied()
}

struct State {
    phases_ms: Vec<f64>,
    ignore: u32,
    last: Instant,
    total_ms: f64,
    corrections: u64,
    recent: Vec<f64>,
    /// the estimate and the step of the last correction, until the next evaluation has judged its effect
    pending: Option<(f64, f64)>,
    /// corrections in a row whose effect did not show
    no_response: u32,
    /// the lock is off for this connection (the loop is open)
    dead: bool,
}

/// Did a slide of `step` ms fail to move the estimate (ms before the render tick, from `before` to `now`) the other way by at least a third of it?
fn open_loop(before: f64, now: f64, step: f64) -> bool {
    step != 0.0 && -(now - before) / step < 0.33
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

/// One headset report: the decoded frame waited `queue_ms` for the headset's render tick.
pub fn observe(queue_ms: f64) {
    if !enabled() || !queue_ms.is_finite() {
        return;
    }
    let period_ms = PERIOD_NS.load(Ordering::Relaxed) as f64 / 1e6;
    let margin = std::env::var("AIRFRAMEXR_PHASE_MARGIN_MS").ok().and_then(|v| v.parse::<f64>().ok()).unwrap_or(7.0);
    let mut g = STATE.lock();
    let s = g.get_or_insert_with(|| State { phases_ms: vec![], ignore: 12, last: Instant::now(), total_ms: 0.0, corrections: 0, recent: vec![], pending: None, no_response: 0, dead: false });
    if s.ignore > 0 {
        s.ignore -= 1;
        return;
    }
    s.phases_ms.push(queue_ms.rem_euclid(period_ms));
    if s.phases_ms.len() < 90 || s.last.elapsed().as_millis() < 1000 {
        return;
    }
    // circular cluster: it starts after the largest empty gap
    let mut v = s.phases_ms.clone();
    v.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let n = v.len();
    let (mut best_gap, mut start_idx) = (v[0] + period_ms - v[n - 1], 0usize); // the gap across the wrap
    for i in 1..n {
        let gap = v[i] - v[i - 1];
        if gap > best_gap {
            best_gap = gap;
            start_idx = i;
        }
    }
    let start = v[start_idx];
    let mut rel: Vec<f64> = v.iter().map(|x| (x - start).rem_euclid(period_ms)).collect();
    rel.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let m0 = if start > period_ms / 2.0 { start - period_ms } else { start }; // negative: part of the cluster already missed its tick
    let p10 = m0 + rel[n / 10];
    if let Some((before, step)) = s.pending.take() {
        if open_loop(before, p10, step) { s.no_response += 1 } else { s.no_response = 0 }
        if s.no_response >= 2 && !s.dead {
            s.dead = true;
            crate::logs::general("INFO", &format!("phase lock: the headset's reports did not follow the last two slides (the estimate stayed near {p10:.1} ms): the lock is off for this connection, the grid stays {:+.1} ms from where it began", s.total_ms));
        }
    }
    // the estimate is noisy (a 10th percentile of ~90 samples): act on the median of the last three, and only outside a dead band
    s.recent.push(p10);
    if s.recent.len() > 3 {
        s.recent.remove(0);
    }
    let mut r = s.recent.clone();
    r.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let est = r[r.len() / 2];
    let err = est - margin;
    s.phases_ms.clear();
    s.last = Instant::now();
    let acted = !s.dead && s.recent.len() >= 2 && err.abs() > 1.2;
    if acted {
        let step = (0.3 * err).clamp(-2.0, 2.0); // positive: arrivals too early, slide the grid later
        ADJUST_NS.store((step * 1e6) as i64, Ordering::Relaxed);
        s.total_ms += step;
        s.corrections += 1;
        s.pending = Some((est, step));
        s.ignore = 12;
        s.recent.clear();
        crate::logs::general("INFO", &format!("phase lock: arrivals {est:.1} ms before the render tick (target {margin:.1}), sliding the grid {step:+.2} ms"));
    }
    if let Some(o) = ORIGIN.get() {
        crate::event(*o, "phase_lock", serde_json::json!({ "p10_ms": p10, "est_ms": est, "cluster_start_ms": m0, "spread_ms": rel[n - 1], "period_ms": period_ms, "acted": acted, "total_slide_ms": s.total_ms, "corrections": s.corrections, "no_response": s.no_response, "off": s.dead }));
    }
}

#[cfg(test)]
mod tests {
    use super::open_loop;

    #[test]
    fn a_slide_that_moves_the_estimate_the_other_way_closes_the_loop() {
        assert!(!open_loop(0.0, -1.9, 2.0));   // 2 ms later: the arrivals came 1.9 ms closer to the tick
        assert!(!open_loop(10.0, 8.5, 2.0));   // three quarters of it
        assert!(!open_loop(0.0, 1.5, -2.0));   // a slide earlier moves it the other way
    }

    #[test]
    fn a_report_that_does_not_follow_is_an_open_loop() {
        assert!(open_loop(0.0, 0.0, -2.0));    // the lab's mock: the queue is 0 whatever the slide
        assert!(open_loop(5.0, 5.3, 2.0));     // moved the wrong way
        assert!(open_loop(5.0, 4.6, 2.0));     // a fifth of the step: noise
    }
}
