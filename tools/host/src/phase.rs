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
/// raw decoder queue of the last ~60 reports (ms, not modulo the period), the median of it, and the drain schedule
static RAW: Mutex<Vec<f64>> = Mutex::new(Vec::new());
static DRAIN: Mutex<DrainState> = Mutex::new(DrainState { median_ms: 0.0, last: None, backoff_s: 3.0, rearmed_at: None });
struct DrainState {
    median_ms: f64,
    last: Option<Instant>,
    backoff_s: f64,
    rearmed_at: Option<Instant>,
}

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

/// True once (then silent for a backoff) when the headset's decoded frames have been waiting a whole display period or more for 60 reports in a row:
/// the phase lock works modulo the period and cannot see that (a frame that missed its tick looks like an early one), but it is a standing FIFO queue
/// that adds one period of latency until a frame is missing. The caller skips one frame; the backoff (3 s, doubled when the queue comes back within
/// 15 s, at most 60 s) keeps an app that rebuilds the queue every second from being skipped every second.
pub fn client_drain_due() -> bool {
    let period_ms = PERIOD_NS.load(Ordering::Relaxed) as f64 / 1e6;
    let mut d = DRAIN.lock();
    if d.median_ms < period_ms {
        return false;
    }
    let now = Instant::now();
    if let Some(l) = d.last {
        if l.elapsed().as_secs_f64() < d.backoff_s {
            return false;
        }
        // back again soon after the last drain: wait longer next time; long calm: back to 3 s
        d.backoff_s = if l.elapsed().as_secs_f64() < d.backoff_s + 15.0 { (d.backoff_s * 2.0).min(60.0) } else { 3.0 };
    }
    d.last = Some(now);
    d.rearmed_at = Some(now);
    d.median_ms = 0.0;
    RAW.lock().clear(); // judge the next decision on frames sent after the skip
    true
}

/// One headset report: the decoded frame waited `queue_ms` for the headset's render tick.
pub fn observe(queue_ms: f64) {
    if !enabled() || !queue_ms.is_finite() {
        return;
    }
    {
        let mut r = RAW.lock();
        r.push(queue_ms);
        if r.len() >= 60 {
            let mut v = r.clone();
            v.sort_by(|a, b| a.partial_cmp(b).unwrap());
            DRAIN.lock().median_ms = v[v.len() / 2];
            r.clear();
        }
    }
    let period_ms = PERIOD_NS.load(Ordering::Relaxed) as f64 / 1e6;
    let margin = std::env::var("AIRFRAMEXR_PHASE_MARGIN_MS").ok().and_then(|v| v.parse::<f64>().ok()).unwrap_or(7.0);
    let mut g = STATE.lock();
    let s = g.get_or_insert_with(|| State { phases_ms: vec![], ignore: 12, last: Instant::now(), total_ms: 0.0, corrections: 0, recent: vec![] });
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
    let acted = s.recent.len() >= 2 && err.abs() > 1.2;
    if acted {
        let step = (0.3 * err).clamp(-2.0, 2.0); // positive: arrivals too early, slide the grid later
        ADJUST_NS.store((step * 1e6) as i64, Ordering::Relaxed);
        s.total_ms += step;
        s.corrections += 1;
        s.ignore = 12;
        s.recent.clear();
        crate::logs::general("INFO", &format!("phase lock: arrivals {est:.1} ms before the render tick (target {margin:.1}), sliding the grid {step:+.2} ms"));
    }
    if let Some(o) = ORIGIN.get() {
        crate::event(*o, "phase_lock", serde_json::json!({ "p10_ms": p10, "est_ms": est, "cluster_start_ms": m0, "spread_ms": rel[n - 1], "period_ms": period_ms, "acted": acted, "total_slide_ms": s.total_ms, "corrections": s.corrections }));
    }
}
