//! Phase lock to the headset's display clock.
//!
//! The headset reports, for every frame it displayed, how long the decoded frame waited for its display tick (`vsync_queue`). Together with the
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
//! VISIONALVR_PHASE_LOCK=0 disables it; VISIONALVR_PHASE_MARGIN_MS (default 3) sets the margin.
use alvr_common::parking_lot::Mutex;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};
use std::time::Instant;

static PERIOD_NS: AtomicU64 = AtomicU64::new(10_000_000);
static ADJUST_NS: AtomicI64 = AtomicI64::new(0);
static ENABLED: AtomicBool = AtomicBool::new(true);
static STATE: Mutex<Option<State>> = Mutex::new(None);

struct State {
    phases_ms: Vec<f64>,
    ignore: u32,
    last: Instant,
    total_ms: f64,
    corrections: u64,
}

pub fn enabled() -> bool {
    ENABLED.load(Ordering::Relaxed)
}

pub fn configure(on: bool) {
    ENABLED.store(on && std::env::var("VISIONALVR_PHASE_LOCK").map(|v| v != "0").unwrap_or(true), Ordering::Relaxed);
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

/// One headset report: the frame waited `vsync_queue_ms` for its tick.
pub fn observe(vsync_queue_ms: f64) {
    if !enabled() || !vsync_queue_ms.is_finite() {
        return;
    }
    let period_ms = PERIOD_NS.load(Ordering::Relaxed) as f64 / 1e6;
    let margin = std::env::var("VISIONALVR_PHASE_MARGIN_MS").ok().and_then(|v| v.parse::<f64>().ok()).unwrap_or(3.0);
    let mut g = STATE.lock();
    let s = g.get_or_insert_with(|| State { phases_ms: vec![], ignore: 12, last: Instant::now(), total_ms: 0.0, corrections: 0 });
    if s.ignore > 0 {
        s.ignore -= 1;
        return;
    }
    s.phases_ms.push(vsync_queue_ms.rem_euclid(period_ms));
    if s.phases_ms.len() < 40 || s.last.elapsed().as_millis() < 500 {
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
    let err = p10 - margin;
    s.phases_ms.clear();
    s.last = Instant::now();
    if err.abs() > 0.4 {
        let step = (0.4 * err).clamp(-3.0, 3.0); // positive: arrivals too early, slide the grid later
        ADJUST_NS.store((step * 1e6) as i64, Ordering::Relaxed);
        s.total_ms += step;
        s.corrections += 1;
        s.ignore = 12;
        crate::logs::general("INFO", &format!("phase lock: arrivals {p10:.1} ms before the tick (target {margin:.1}), sliding the grid {step:+.2} ms"));
    }
}
