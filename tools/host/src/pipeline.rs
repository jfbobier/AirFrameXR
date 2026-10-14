//! Staged frame pipeline for shim mode: the compositor, encoder and sender of a VR runtime as separate threads.
//!
//! ```text
//!   shim (in the game) --IPC frame event-->  [intake]   newest-frame mailbox (the slot is marked busy for the shim)
//!   [clock] (the vsync thread in main.rs)    at each compositor boundary: mailbox -> encoder mailbox (newest wins)
//!   [encoder]                                foveation pass + NVENC on the picked slot (or the green idle frame)
//!   [sender]                                 releases each packet at its send time (asap | next tick | boundary + lead)
//!                                            -> ServerCoreContext::send_video_nal (ALVR's own sender thread)
//! ```
//!
//! Every boundary has a one-deep buffer and a newest-wins rule, so a slow or irregular stage never moves the clock: the
//! boundary cadence belongs to the clock thread alone, encode time only moves the send time, and the headset repeats its
//! last frame on an empty boundary (what SteamVR's compositor does with a late app frame). The game is paced by the shim's
//! running start (ovrshim/driver.cpp WaitForVsync) against the same boundaries, published to it through the IPC block.

use crate::{event, logs, nvh, push_bounded, sleep_spin_until, LiveCtx, Shared, HOLD_MS, NEXT_TICK_NS, SAME_TS_FRAMES, SEND_PHASE_MS};
use alvr_common::parking_lot::{Condvar, Mutex};
use alvr_server_core::ServerCoreContext;
use alvr_session::CodecType;
use serde_json::json;
use std::{
    collections::VecDeque,
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        Arc,
    },
    thread,
    time::{Duration, Instant},
};

/// When an encoded frame leaves the host.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum SendPacing {
    /// as soon as it is encoded (arrival jitter = encode jitter)
    Asap,
    /// at the display tick after the encode (ALVR's SteamVR driver: one period of latency, flat phase)
    Vsync,
    /// at boundary + lead, lead = envelope of recent encode times (flat phase without the extra period when encodes are quick)
    Phase,
}

impl SendPacing {
    pub fn parse(s: &str) -> Result<Self, String> {
        match s {
            "asap" | "off" => Ok(Self::Asap),
            "vsync" | "tick" => Ok(Self::Vsync),
            "phase" | "boundary" => Ok(Self::Phase),
            o => Err(format!("send pacing asap|vsync|phase, got {o}")),
        }
    }
    pub fn name(self) -> &'static str {
        match self {
            Self::Asap => "asap",
            Self::Vsync => "vsync",
            Self::Phase => "phase",
        }
    }
}

pub struct Config {
    pub origin: Instant,
    pub period_ns: Arc<AtomicU64>,
    pub send_pacing: SendPacing,
    pub daemon: bool,
    pub fps: f64,
}

/// The NVENC encoder and its packet-callback context. Owned by the pipeline, used by the encoder thread; the main thread
/// locks it to rebuild the encoder for a renegotiated stream (only while no game runs, so there is no contention).
pub struct Encoder {
    pub handle: *mut std::ffi::c_void,
    pub live: Box<LiveCtx>,
}
unsafe impl Send for Encoder {}

#[derive(Clone, Copy)]
struct Arrived {
    counter: u64,
    slot: u32,
    ts_ns: u64,
    submit_s: f64,
    arrival: Instant,
    /// per-eye orientation the projection layer was rendered with (ipc frameRenderQuat), valid when `has_projection`
    render_q: [f32; 8],
    has_projection: bool,
}

/// Frame-level CSV mirrors for the harness (VISIONALVR_PIPELINE_CSV = path of the sent-frames file; the fates file sits beside it):
/// `fate` writes one line per event of a frame's life on the host: A arrival (slot, ts), S superseded in the intake, D deferred to the next boundary (no newer sample to stamp with), P picked at a
/// boundary (index), O dropped by the encoder queue; frame generation: I ingested (1 = a pair exists), G synthetic frame sent, R the rendered frame sent
/// (rotated to its stamp sample), Q the rendered frame sent again (a boundary with nothing new); for G/R/Q the counter is the newest rendered frame's.
#[cfg(feature = "telemetry")]
static MIRRORS: std::sync::Mutex<Option<(std::io::BufWriter<std::fs::File>, std::io::BufWriter<std::fs::File>, u32)>> = std::sync::Mutex::new(None);
#[cfg(feature = "telemetry")]
fn mirror_open() -> bool {
    let mut g = match MIRRORS.lock() { Ok(g) => g, Err(_) => return false };
    if g.is_none() {
        let p = match std::env::var("VISIONALVR_PIPELINE_CSV") {
            Ok(p) => p,
            // headset sessions: beside the other debug logs
            Err(_) => match logs::debug_dir() { Some(d) => d.join("host_pipeline.csv").to_string_lossy().into_owned(), None => return false },
        };
        let fates = p.replace(".csv", "_fates.csv");
        let (Ok(a), Ok(b)) = (std::fs::File::create(&p), std::fs::File::create(&fates)) else { return false };
        use std::io::Write;
        let (mut a, mut b) = (std::io::BufWriter::new(a), std::io::BufWriter::new(b));
        let _ = writeln!(a, "frame,ts_ns,slot,t_boundary,t_arrival,t_encode_start,encode_ms,t_send,hold_ms,bytes,idr");
        let _ = writeln!(b, "counter,kind,t,a,b");
        *g = Some((a, b, 0));
    }
    true
}
#[cfg(not(feature = "telemetry"))]
#[inline(always)]
fn mirror_sent(_row: &str) {}
#[cfg(feature = "telemetry")]
fn mirror_sent(row: &str) {
    use std::io::Write;
    if !mirror_open() { return; }
    if let Ok(mut g) = MIRRORS.lock() { if let Some((a, _, n)) = g.as_mut() { let _ = writeln!(a, "{row}"); *n += 1; if *n % 90 == 0 { let _ = a.flush(); } } }
}
#[cfg(not(feature = "telemetry"))]
#[inline(always)]
fn fate(_counter: u64, _kind: &str, _t: Instant, _a: u64, _b: u64) {}
#[cfg(feature = "telemetry")]
fn fate(counter: u64, kind: &str, t: Instant, a: u64, b: u64) {
    use std::io::Write;
    if !mirror_open() { return; }
    if let Ok(mut g) = MIRRORS.lock() { if let Some((_, f, _)) = g.as_mut() { let _ = writeln!(f, "{counter},{kind},{:.6},{a},{b}", logs::qpc_at(t)); } }
}
/// flushes the mirrors (end of run)
#[cfg(not(feature = "telemetry"))]
pub fn mirror_flush() {}
#[cfg(feature = "telemetry")]
pub fn mirror_flush() {
    use std::io::Write;
    if let Ok(mut g) = MIRRORS.lock() { if let Some((a, b, _)) = g.as_mut() { let _ = a.flush(); let _ = b.flush(); } }
}

enum Job {
    App(Arrived),
    Green,
    /// frame generation: a boundary without a new rendered frame while a rendered frame exists (the host fills it, see `FgState`)
    FgHold,
}

/// Frame generation (docs/FRAME_GENERATION.md): what the encoder thread owes the stream. The game is paced at half the display rate
/// (ipc appPeriodMul 2). Rendered frame B arrives: if a previous frame A exists, the boundary gets the interpolated frame S(A, B) and B
/// itself is due at the next boundary (`real_due`); B arriving while the previous frame is still due sends that one first and skips the
/// synthetic frame for this pair (two arrivals, two outputs: the app runs faster than half rate). A boundary with nothing due re-sends B
/// rotated to the new head sample (the headset would repeat it anyway; the rotation keeps head turns smooth). Every output is stamped with
/// the newest tracking sample newer than the last sent frame and rotated to it; with no such sample the boundary is skipped (`fg_no_sample`).
struct FgState {
    on: bool,
    /// the synthetic frame's position between the two rendered frames (0.5 = midpoint)
    t: f64,
    synth_due: bool,
    real_due: bool,
    /// the newest rendered frame's identity for the statistics rows
    last_counter: u64,
    last_submit_s: f64,
    last_arrival: Option<Instant>,
    /// the shim slot of the newest rendered frame could not be used (no projection layer): the slot went out as is
    passthrough: u64,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum FgKind {
    Synth,
    Real,
    Repeat,
}

/// what one boundary encodes
enum Plan {
    Skip,
    Green,
    Slot(Arrived),
    Fg { mode: i32, t: f64, q: [f32; 4], ts: Duration, counter: u64, submit_s: f64, arrival: Option<Instant>, kind: FgKind },
}

struct Picked {
    job: Job,
    boundary: Instant,
    boundary_idx: u64,
}

struct Outgoing {
    release_at: Instant,
    boundary: Instant,
    ts: Duration,
    frame: Vec<u8>,
    is_idr: bool,
    config: Option<Vec<u8>>,
    encoded_at: Instant,
    /// CSV columns up to the send time (frame,ts_ns,slot,t_boundary,t_arrival,t_encode_start,encode_ms)
    row: String,
    bytes: usize,
    #[cfg(feature = "simrig")]
    sim_ring: i32,
}

/// Per-frame histories the main thread drains into its own vectors (statistics windows, end-of-run report).
#[derive(Default)]
pub struct Drained {
    pub encode_ms: Vec<f64>,
    pub compose_ms: Vec<f64>,
    pub ffr_ms: Vec<f64>,
    pub ffr_wait_ms: Vec<f64>,
    pub intake_ms: Vec<f64>,
    pub frame_rows: Vec<String>,
    pub sent_ts: Vec<u64>,
}

pub struct Pipeline {
    cfg: Config,
    ipc: usize,
    ctx: Arc<ServerCoreContext>,
    shared: Arc<Shared>,
    pub enc: Mutex<Encoder>,
    /// frames the app finished and no boundary has taken yet, oldest first (at most `intake_depth`)
    intake: Mutex<VecDeque<Arrived>>,
    intake_depth: usize,
    /// the previous boundary left a frame behind in the intake (a second one in a row drops the older instead: bounded lag)
    left_behind: AtomicBool,
    pub queue_picks: AtomicU64,
    /// which frame a boundary takes when two wait: drain (default: in order, plus one dropped frame to resynchronise a queue that has stood
    /// VISIONALVR_DRAIN_AFTER (30) boundaries, at most once per VISIONALVR_DRAIN_EVERY_MS; a fallback: the intake gate normally drains the queue first,
    /// see `intake_gate`) | oldest (no drops, a standing queue stays a period late) | newest (lab only: drops the older of every pair, loses frames
    /// when the app's frame times jitter) | adaptive (newest right after an empty boundary, else oldest) | aged
    pick_policy: String,
    /// the shim holds the game's next release while a frame a boundary passed over still waits here (ipc intakeWaiting, written at every
    /// boundary): the queue drains at the next boundary without dropping a rendered frame. VISIONALVR_INTAKE_GATE=0 disables it.
    intake_gate: bool,
    /// consecutive boundaries with a frame left behind before the gate acts (VISIONALVR_GATE_AFTER, default 3)
    gate_after: u64,
    /// boundaries that left a frame behind (since start): how long queues stood, whatever drained them
    pub standing_total: AtomicU64,
    /// the last boundary that found no frame (the app is not keeping up): the gate and the drain stay out for VISIONALVR_KEEPUP_BOUNDARIES (30)
    /// boundaries after it. A GPU-bound game delivers in bunches (two frames at half the boundaries, none at a tenth: Cyberpunk at 88 fps on a 100 Hz
    /// display, 2026-10-13 evening); that is not a standing queue, and skipping its releases only lowered its frame rate (258 skips in 82 s, hold unchanged).
    last_empty_idx: AtomicU64,
    keepup_boundaries: u64,
    last_empty: AtomicBool,
    /// `drain` pick policy: consecutive boundaries that left a frame behind in the intake (a standing queue: the app delivers exactly as fast as the
    /// display consumes, so every frame waits a period more than it needs to), and when it last dropped one
    standing: AtomicU64,
    drain_last: Mutex<Option<Instant>>,
    pub intake_drains: AtomicU64,
    /// the headset's decoder queue stood a whole period or more (phase.rs): drop one frame at a boundary to let it drain (opt-in, see phase::client_drain_due)
    client_drain: bool,
    pub drains: AtomicU64,
    last_arrival: Mutex<Option<Instant>>,
    picked: Mutex<Option<Picked>>,
    picked_cv: Condvar,
    outgoing: Mutex<VecDeque<Outgoing>>,
    outgoing_cv: Condvar,
    threads: Mutex<Vec<thread::JoinHandle<()>>>,
    /// an OVR session exists and its heartbeat advances (updated by the clock thread)
    pub app_alive: AtomicBool,
    /// connected and the encoder matches the negotiated stream: boundaries pick frames
    pub active: AtomicBool,
    stop: AtomicBool,
    pub failed: Mutex<Option<String>>,
    // counters (since start)
    pub arrived: AtomicU64,
    pub superseded: AtomicU64,
    pub slots_filled: AtomicU64,
    pub slots_empty: AtomicU64,
    pub encoder_overruns: AtomicU64,
    pub green_frames: AtomicU64,
    pub send_late: AtomicU64,
    pub encode_errors: AtomicU64,
    pub idr_restarts: AtomicU64,
    pub frames_before_first_tracking: AtomicU64,
    encode_env_ms: Mutex<f64>,
    drained: Mutex<Drained>,
    last_sent_ts: Mutex<Duration>,
    frame_idx: AtomicU64,
    last_idr_restart: Mutex<Instant>,
    green_active: AtomicBool,
    last_green_boundary: AtomicU64,
    gpu_sched: Mutex<Option<(i32, i64)>>,
    // frame generation (see `FgState`)
    fg: Mutex<FgState>,
    /// mirror of `FgState::on` for the clock thread (which must never wait on the encoder thread's mutex)
    fg_on: AtomicBool,
    /// a rendered frame is in the generator's history (set by the encoder thread, read by the clock thread)
    fg_have_frame: AtomicBool,
    pub fg_synths: AtomicU64,
    pub fg_reals: AtomicU64,
    pub fg_repeats: AtomicU64,
    pub fg_holds: AtomicU64,
    /// boundaries skipped because no tracking sample newer than the last sent frame had arrived
    pub fg_no_sample: AtomicU64,
    /// rendered frames never shown: due when the next one arrived and no sample to stamp them with
    pub fg_dropped: AtomicU64,
    /// synthetic frames not generated because the previous rendered frame was still due (app faster than half rate)
    pub fg_pairs_skipped: AtomicU64,
    pub fg_errors: AtomicU64,
    /// nvh_fg_stats JSON, refreshed by the encoder thread (the statistics window must not lock the encoder)
    fg_stats: Mutex<String>,
}

/// VISIONALVR_INTAKE_GATE (default on): see `Pipeline::intake_gate`
pub fn intake_gate_enabled() -> bool {
    std::env::var("VISIONALVR_INTAKE_GATE").map(|v| v != "0").unwrap_or(true)
}

fn set_thread_priority(_p: i32) {
    #[cfg(windows)]
    unsafe {
        crate::SetThreadPriority(crate::GetCurrentThread(), _p);
    }
}

impl Pipeline {
    pub fn new(cfg: Config, ipc: usize, ctx: Arc<ServerCoreContext>, shared: Arc<Shared>, enc: Encoder) -> Arc<Self> {
        Arc::new(Self {
            cfg,
            ipc,
            ctx,
            shared,
            enc: Mutex::new(enc),
            intake: Mutex::new(VecDeque::new()),
            // Depth 2: a GPU-bound game completes frames in bunches (two within one period, then none). Newest-wins threw the
            // older of each pair away and left the next boundary empty (Cyberpunk: 90 submitted, ~65 streamed). With depth 2
            // the older one goes out at the boundary and the newer at the next. VISIONALVR_INTAKE_DEPTH=1: the old behaviour.
            // Depth 3 since 2026-10-13: at 2 the third frame of a GPU-bound game's burst was thrown away (Cyberpunk 1.7-2.0 % of frames,
            // Dead Space 0.7 %); at 3 none is, for +1.5 ms of queue hold in those games. Frame rate before latency.
            intake_depth: std::env::var("VISIONALVR_INTAKE_DEPTH").ok().and_then(|v| v.parse().ok()).unwrap_or(3usize).clamp(1, 3),
            left_behind: AtomicBool::new(false),
            queue_picks: AtomicU64::new(0),
            pick_policy: std::env::var("VISIONALVR_PICK").unwrap_or_else(|_| "drain".into()),
            intake_gate: intake_gate_enabled(),
            gate_after: std::env::var("VISIONALVR_GATE_AFTER").ok().and_then(|v| v.parse::<u64>().ok()).unwrap_or(3).max(1),
            last_empty_idx: AtomicU64::new(0),
            keepup_boundaries: std::env::var("VISIONALVR_KEEPUP_BOUNDARIES").ok().and_then(|v| v.parse::<u64>().ok()).unwrap_or(30),
            standing_total: AtomicU64::new(0),
            last_empty: AtomicBool::new(false),
            standing: AtomicU64::new(0),
            drain_last: Mutex::new(None),
            intake_drains: AtomicU64::new(0),
            client_drain: std::env::var("VISIONALVR_CLIENT_DRAIN").map(|v| v == "1").unwrap_or(false),
            drains: AtomicU64::new(0),
            last_arrival: Mutex::new(None),
            picked: Mutex::new(None),
            picked_cv: Condvar::new(),
            outgoing: Mutex::new(VecDeque::new()),
            outgoing_cv: Condvar::new(),
            threads: Mutex::new(vec![]),
            app_alive: AtomicBool::new(false),
            active: AtomicBool::new(false),
            stop: AtomicBool::new(false),
            failed: Mutex::new(None),
            arrived: AtomicU64::new(0),
            superseded: AtomicU64::new(0),
            slots_filled: AtomicU64::new(0),
            slots_empty: AtomicU64::new(0),
            encoder_overruns: AtomicU64::new(0),
            green_frames: AtomicU64::new(0),
            send_late: AtomicU64::new(0),
            encode_errors: AtomicU64::new(0),
            idr_restarts: AtomicU64::new(0),
            frames_before_first_tracking: AtomicU64::new(0),
            encode_env_ms: Mutex::new(4.0),
            drained: Mutex::new(Drained::default()),
            last_sent_ts: Mutex::new(Duration::ZERO),
            frame_idx: AtomicU64::new(0),
            last_idr_restart: Mutex::new(Instant::now() - Duration::from_secs(1)),
            green_active: AtomicBool::new(false),
            last_green_boundary: AtomicU64::new(0),
            gpu_sched: Mutex::new(None),
            fg: Mutex::new(FgState { on: false, t: 0.5, synth_due: false, real_due: false, last_counter: 0, last_submit_s: 0.0, last_arrival: None, passthrough: 0 }),
            fg_on: AtomicBool::new(false),
            fg_have_frame: AtomicBool::new(false),
            fg_synths: AtomicU64::new(0),
            fg_reals: AtomicU64::new(0),
            fg_repeats: AtomicU64::new(0),
            fg_holds: AtomicU64::new(0),
            fg_no_sample: AtomicU64::new(0),
            fg_dropped: AtomicU64::new(0),
            fg_pairs_skipped: AtomicU64::new(0),
            fg_errors: AtomicU64::new(0),
            fg_stats: Mutex::new(String::from("{\"on\":0}")),
        })
    }

    /// Frame generation on/off (before `start`, or whenever the encoder is rebuilt); `t` = the synthetic frame's position between the rendered frames.
    pub fn set_frame_gen(&self, on: bool, t: f64) {
        let mut st = self.fg.lock();
        st.on = on;
        self.fg_on.store(on, Ordering::Relaxed);
        st.t = t.clamp(0.05, 0.95);
        st.synth_due = false;
        st.real_due = false;
        if !on {
            self.fg_have_frame.store(false, Ordering::Relaxed);
        }
    }

    /// the newest tracking sample newer than the last sent frame: the stamp (and the rotation target) of a generated frame
    fn fg_pick_sample(&self) -> Option<(Duration, [f32; 4])> {
        let last = *self.last_sent_ts.lock();
        let ring = self.shared.recent_samples.lock();
        ring.back().filter(|(t, _)| *t > last).copied()
    }

    /// A rendered frame arrived (frame generation on): what this boundary encodes. Releases the slot.
    fn fg_plan_app(&self, handle: *mut std::ffi::c_void, ipc: *mut std::ffi::c_void, a: Arrived) -> Plan {
        if !a.has_projection {
            // no projection layer (a loading screen made of quads): the slot goes out as is, nothing to interpolate
            let mut st = self.fg.lock();
            st.passthrough += 1;
            st.real_due = false;
            st.synth_due = false;
            return Plan::Slot(a);
        }
        let mut st = self.fg.lock();
        let mut plan = Plan::Skip;
        if st.real_due && self.fg_have_frame.load(Ordering::Relaxed) {
            // the previous rendered frame is still due: it goes out now, before the new one replaces it in the history; no synthetic frame
            // for this pair (the app delivered faster than half rate: two frames, two boundaries)
            match self.fg_pick_sample() {
                Some((sts, q)) => {
                    plan = Plan::Fg { mode: 0, t: 0.0, q, ts: sts, counter: st.last_counter, submit_s: st.last_submit_s, arrival: st.last_arrival, kind: FgKind::Real };
                    self.fg_pairs_skipped.fetch_add(1, Ordering::Relaxed);
                }
                None => {
                    self.fg_no_sample.fetch_add(1, Ordering::Relaxed);
                    self.fg_dropped.fetch_add(1, Ordering::Relaxed);
                }
            }
            st.real_due = false;
            st.synth_due = false;
        }
        let mut err = [0 as std::ffi::c_char; 256];
        let r = unsafe { nvh::nvh_fg_ingest(handle, ipc, a.slot, a.render_q.as_ptr(), err.as_mut_ptr(), 256) };
        unsafe { nvh::nvh_ipc_slot_busy(ipc, a.slot, 0) };
        fate(a.counter, "I", Instant::now(), r as u64, 0);
        if r < 0 {
            let n = self.fg_errors.fetch_add(1, Ordering::Relaxed) + 1;
            let msg = unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }.to_string_lossy().to_string();
            if n <= 3 || n % 300 == 0 {
                logs::general("WARN", &format!("frame generation: ingest failed ({n}): {msg}"));
                event(self.cfg.origin, "frame_gen_error", json!({ "stage": "ingest", "message": msg, "n": n }));
            }
            return plan;
        }
        self.fg_have_frame.store(true, Ordering::Relaxed);
        st.last_counter = a.counter;
        st.last_submit_s = a.submit_s;
        st.last_arrival = Some(a.arrival);
        if !matches!(plan, Plan::Skip) {
            st.real_due = true; // the new frame is due at the next boundary
            return plan;
        }
        match self.fg_pick_sample() {
            Some((sts, q)) => {
                if r == 1 {
                    st.real_due = true;
                    Plan::Fg { mode: 1, t: st.t, q, ts: sts, counter: a.counter, submit_s: a.submit_s, arrival: Some(a.arrival), kind: FgKind::Synth }
                } else {
                    st.real_due = false;
                    Plan::Fg { mode: 0, t: 0.0, q, ts: sts, counter: a.counter, submit_s: a.submit_s, arrival: Some(a.arrival), kind: FgKind::Real }
                }
            }
            None => {
                st.synth_due = r == 1;
                st.real_due = true;
                self.fg_no_sample.fetch_add(1, Ordering::Relaxed);
                Plan::Skip
            }
        }
    }

    /// A boundary without a new rendered frame (frame generation on): the synthetic or the rendered frame still due, else the rendered frame again.
    fn fg_plan_hold(&self) -> Plan {
        if !self.fg_have_frame.load(Ordering::Relaxed) {
            return Plan::Skip;
        }
        let mut st = self.fg.lock();
        let Some((sts, q)) = self.fg_pick_sample() else {
            self.fg_no_sample.fetch_add(1, Ordering::Relaxed);
            return Plan::Skip;
        };
        let (counter, submit_s, arrival) = (st.last_counter, st.last_submit_s, st.last_arrival);
        if st.synth_due {
            st.synth_due = false;
            Plan::Fg { mode: 1, t: st.t, q, ts: sts, counter, submit_s, arrival, kind: FgKind::Synth }
        } else if st.real_due {
            st.real_due = false;
            Plan::Fg { mode: 0, t: 0.0, q, ts: sts, counter, submit_s, arrival, kind: FgKind::Real }
        } else {
            Plan::Fg { mode: 0, t: 0.0, q, ts: sts, counter, submit_s, arrival, kind: FgKind::Repeat }
        }
    }

    fn ipc(&self) -> *mut std::ffi::c_void {
        self.ipc as *mut std::ffi::c_void
    }

    /// `aged` pick: how long the older of two waiting frames may have waited (VISIONALVR_PICK_AGE_MS, default 0.7 of a display period)
    fn pick_age(&self) -> Duration {
        match std::env::var("VISIONALVR_PICK_AGE_MS").ok().and_then(|v| v.parse::<f64>().ok()) {
            Some(ms) => Duration::from_secs_f64(ms / 1000.0),
            None => self.period().mul_f64(0.7),
        }
    }

    fn period(&self) -> Duration {
        Duration::from_nanos(self.cfg.period_ns.load(Ordering::Relaxed).max(1_000_000))
    }

    /// Asks the driver for a GPU scheduling class for this process (5 = realtime, what SteamVR's compositor gets, so the
    /// encoder's copy and foveation pass never queue behind the game's GPU work), falling back class by class to 2.
    pub fn set_gpu_scheduling(&self, class: i32) {
        if class <= 0 {
            return;
        }
        let mut c = class.min(5);
        while c >= 2 {
            let st = unsafe { nvh::nvh_set_gpu_scheduling(c) } as i64;
            if st == 0 {
                *self.gpu_sched.lock() = Some((c, 0));
                event(self.cfg.origin, "gpu_scheduling", json!({ "requested": class, "class": c, "status": 0 }));
                return;
            }
            *self.gpu_sched.lock() = Some((c, st));
            c -= 1;
        }
        let st = self.gpu_sched.lock().map(|(_, s)| s).unwrap_or(0);
        event(self.cfg.origin, "gpu_scheduling", json!({ "requested": class, "class": null, "status": st,
            "hint": "the realtime class needs an elevated host (SeIncreaseBasePriorityPrivilege)" }));
        logs::general("WARN", &format!("GPU scheduling class {class} refused (status 0x{st:x}): run the host elevated for the realtime class"));
    }

    pub fn start(self: &Arc<Self>) {
        let mut threads = self.threads.lock();
        let p = Arc::clone(self);
        threads.push(thread::Builder::new().name("intake".into()).spawn(move || p.intake_loop()).expect("intake thread"));
        let p = Arc::clone(self);
        threads.push(thread::Builder::new().name("encoder".into()).spawn(move || p.encoder_loop()).expect("encoder thread"));
        let p = Arc::clone(self);
        threads.push(thread::Builder::new().name("sender".into()).spawn(move || p.sender_loop()).expect("sender thread"));
    }

    pub fn stop(&self) {
        self.stop.store(true, Ordering::SeqCst);
        self.picked_cv.notify_all();
        self.outgoing_cv.notify_all();
        let threads: Vec<_> = self.threads.lock().drain(..).collect();
        for t in threads {
            t.join().ok();
        }
        mirror_flush();
    }

    // ---- intake: IPC frame event -> newest-frame mailbox --------------------------------------------------------------

    fn intake_loop(&self) {
        set_thread_priority(2); // THREAD_PRIORITY_HIGHEST: arrival times are measurements
        let ipc = self.ipc();
        while !self.stop.load(Ordering::Relaxed) {
            let (mut counter, mut slot, mut cts, mut submit_s) = (0u64, 0u32, 0u64, 0f64);
            let (mut render_q, mut has_proj) = ([0f32; 8], 0i32);
            if unsafe { nvh::nvh_ipc_wait_frame(ipc, 2, &mut counter, &mut slot, &mut cts, &mut submit_s, render_q.as_mut_ptr(), &mut has_proj) } == 0 {
                continue;
            }
            let now = Instant::now();
            // hold the slot for the shim until the encoder has read it (or a newer frame supersedes it)
            unsafe { nvh::nvh_ipc_slot_busy(ipc, slot, 1) };
            #[cfg(feature = "simrig")]
            if crate::simrig::active() {
                crate::simrig::on_arrival(ipc, slot, counter as u32);
            }
            let a = Arrived { counter, slot, ts_ns: cts, submit_s, arrival: now, render_q, has_projection: has_proj != 0 };
            {
                let mut q = self.intake.lock();
                fate(counter, "A", now, slot as u64, cts);
                q.push_back(a);
                while q.len() > self.intake_depth {
                    if let Some(old) = q.pop_front() {
                        fate(old.counter, "S", now, 0, 0);
                        if old.slot != slot {
                            unsafe { nvh::nvh_ipc_slot_busy(ipc, old.slot, 0) };
                        }
                        self.superseded.fetch_add(1, Ordering::Relaxed);
                    }
                }
            }
            self.arrived.fetch_add(1, Ordering::Relaxed);
            let mut last = self.last_arrival.lock();
            if let Some(l) = *last {
                let mut d = self.drained.lock();
                if d.intake_ms.len() < 1 << 16 {
                    d.intake_ms.push(now.duration_since(l).as_secs_f64() * 1000.0);
                }
            }
            *last = Some(now);
        }
    }

    // ---- clock: one call per compositor boundary (the vsync thread) ---------------------------------------------------

    /// Called by the clock thread at boundary `idx` (time `boundary`): the newest complete app frame goes to the encoder; with
    /// no app, the green idle frame at ~30 fps; an app that produced nothing since the previous boundary leaves it empty (the
    /// headset re-presents its last frame).
    pub fn on_boundary(&self, idx: u64, boundary: Instant) {
        let ipc = self.ipc();
        let alive = unsafe { nvh::nvh_ipc_shim_alive(ipc) } != 0;
        self.app_alive.store(alive, Ordering::Relaxed);
        if !self.active.load(Ordering::Relaxed) {
            if self.intake_gate {
                unsafe { nvh::nvh_ipc_set_intake(ipc, 0) }; // nothing is picked while inactive: never hold the app for it
            }
            return;
        }
        // the app keeps up when no boundary has been empty lately (see `last_empty_idx`): only then is a queue a standing queue
        let keeps_up = idx.wrapping_sub(self.last_empty_idx.load(Ordering::Relaxed)) > self.keepup_boundaries;
        let newest = {
            let mut q = self.intake.lock();
            // two waiting: send the older now and the newer at the next boundary (the stream runs one period late until an empty
            // boundary drains the queue; a third frame arriving first drops the oldest, see intake_loop)
            let take_older = q.len() >= 2
                && match self.pick_policy.as_str() {
                    "newest" => false,
                    "adaptive" => !self.last_empty.load(Ordering::Relaxed),
                    // in order like `oldest`, but a queue that has stood VISIONALVR_DRAIN_AFTER (8) boundaries without ever emptying drops its older frame
                    // once (one rendered frame never shown, one period of latency back), at most once per VISIONALVR_DRAIN_EVERY_MS (1000). The earlier
                    // backoff doubled to 60 s whenever the queue came back, which in a GPU-bound game (a bunch every few seconds) left it standing most
                    // of the time (Cyberpunk 2026-10-13: 3-4 drains in 100 s, hold 9.9 ms). With the intake gate on, the gate drains the queue first and
                    // this fires only when it could not (shim without the gate, or the app ignoring its release).
                    "drain" => {
                        // 30 boundaries (0.3 s): the gate normally clears a standing queue within a few boundaries; the drain is the last resort and must
                        // not fire on the arrival jitter the duplicate-timestamp re-stamp adds (77 frames dropped in 206 s of End of Abyss at 8)
                        let after = std::env::var("VISIONALVR_DRAIN_AFTER").ok().and_then(|v| v.parse::<u64>().ok()).unwrap_or(30).max(1);
                        let every = std::env::var("VISIONALVR_DRAIN_EVERY_MS").ok().and_then(|v| v.parse::<f64>().ok()).unwrap_or(1000.0).max(0.0) / 1000.0;
                        let mut last = self.drain_last.lock();
                        let due = keeps_up && self.standing.load(Ordering::Relaxed) >= after
                            && last.map(|t| boundary.saturating_duration_since(t).as_secs_f64() >= every).unwrap_or(true);
                        if due {
                            *last = Some(boundary);
                            self.intake_drains.fetch_add(1, Ordering::Relaxed);
                        }
                        !due
                    }
                    // oldest unless it has already waited long: a frame that waits more than a fraction of a period means a standing queue
                    // (the app delivers as fast as the display consumes: the queue never drains and every frame is a period late), so drop
                    // it for the newer one. A fresh bunch (a late frame followed by its catch-up frame) still goes out in order.
                    "aged" => q.front().map(|f| boundary.saturating_duration_since(f.arrival) < self.pick_age()).unwrap_or(true),
                    _ => true,
                };
            let a = if take_older { q.pop_front() } else { q.pop_back() };
            if take_older {
                self.queue_picks.fetch_add(1, Ordering::Relaxed);
            } else {
                for old in q.drain(..) {
                    fate(old.counter, "S", boundary, 1, 0);
                    unsafe { nvh::nvh_ipc_slot_busy(ipc, old.slot, 0) };
                    self.superseded.fetch_add(1, Ordering::Relaxed);
                }
            }
            self.left_behind.store(!q.is_empty(), Ordering::Relaxed);
            let standing = if q.is_empty() {
                self.standing.store(0, Ordering::Relaxed);
                0
            } else {
                self.standing_total.fetch_add(1, Ordering::Relaxed);
                self.standing.fetch_add(1, Ordering::Relaxed) + 1
            };
            if self.intake_gate {
                // A frame left behind at ONE boundary is usually a late frame paired with an on-time one (arrival jitter: the lab's "steady"
                // probe showed depth 2 at every third boundary and never twice in a row); in order, both are shown and nothing is lost. A queue
                // that stands VISIONALVR_GATE_AFTER (3) boundaries in a row is the app running a frame ahead of the stream (Cyberpunk: 95 % of
                // boundaries): only then does the shim skip one release (see `intake_gate`). Gating on a single boundary skipped 16 % of releases.
                let waiting = if keeps_up && standing >= self.gate_after { q.len() as u32 } else { 0 };
                unsafe { nvh::nvh_ipc_set_intake(ipc, waiting) };
            }
            a
        };
        if self.client_drain && newest.is_some() && alive && crate::phase::client_drain_due() {
            // the headset's decoded frames wait a whole period or more (a FIFO standing queue that the phase lock, modulo the period, cannot see):
            // not sending this one lets the headset's queue run dry once (one skipped picture) and gives the period back
            if let Some(a) = newest {
                fate(a.counter, "S", boundary, 1, 0);
                unsafe { nvh::nvh_ipc_slot_busy(ipc, a.slot, 0) };
                self.superseded.fetch_add(1, Ordering::Relaxed);
                self.drains.fetch_add(1, Ordering::Relaxed);
                event(self.cfg.origin, "client_queue_drain", json!({ "boundary": idx }));
            }
            return;
        }
        let job = match newest {
            Some(a) => {
                fate(a.counter, "P", boundary, idx, 0);
                self.last_empty.store(false, Ordering::Relaxed);
                self.slots_filled.fetch_add(1, Ordering::Relaxed);
                Job::App(a)
            }
            None if alive => {
                self.last_empty.store(true, Ordering::Relaxed);
                self.last_empty_idx.store(idx, Ordering::Relaxed);
                if self.fg_have_frame.load(Ordering::Relaxed) && self.fg_on.load(Ordering::Relaxed) {
                    // frame generation: the host fills this boundary from the rendered frames it holds (by design every other boundary)
                    self.fg_holds.fetch_add(1, Ordering::Relaxed);
                    Job::FgHold
                } else {
                    self.slots_empty.fetch_add(1, Ordering::Relaxed);
                    return;
                }
            }
            None => {
                let every = (self.cfg.fps / 30.0).round().max(1.0) as u64;
                if idx.wrapping_sub(self.last_green_boundary.load(Ordering::Relaxed)) < every {
                    return;
                }
                self.last_green_boundary.store(idx, Ordering::Relaxed);
                Job::Green
            }
        };
        let mut picked = self.picked.lock();
        if let Some(old) = picked.replace(Picked { job, boundary, boundary_idx: idx }) {
            // the encoder is still busy with the previous boundary's frame and had this one waiting: newest wins
            if let Job::App(a) = old.job {
                fate(a.counter, "O", boundary, 0, 0);
                unsafe { nvh::nvh_ipc_slot_busy(ipc, a.slot, 0) };
            }
            self.encoder_overruns.fetch_add(1, Ordering::Relaxed);
        }
        drop(picked);
        self.picked_cv.notify_one();
    }

    // ---- encoder ------------------------------------------------------------------------------------------------------

    fn encoder_loop(&self) {
        set_thread_priority(2);
        let ipc = self.ipc();
        let origin = self.cfg.origin;
        let mut consecutive_errors = 0usize;
        let mut fg_encodes = 0u64;
        loop {
            let p = {
                let mut g = self.picked.lock();
                while g.is_none() && !self.stop.load(Ordering::Relaxed) {
                    self.picked_cv.wait(&mut g);
                }
                if self.stop.load(Ordering::Relaxed) {
                    return;
                }
                g.take().unwrap()
            };
            let mut enc = self.enc.lock();
            if enc.handle.is_null() {
                if let Job::App(a) = &p.job {
                    unsafe { nvh::nvh_ipc_slot_busy(ipc, a.slot, 0) };
                }
                continue;
            }
            let handle = enc.handle;
            // ---- what this boundary encodes: the picked slot, the green idle frame, or (frame generation) a generated picture
            let fg_on = self.fg_on.load(Ordering::Relaxed);
            let plan = match (&p.job, fg_on) {
                (Job::Green, _) => Plan::Green,
                (Job::App(a), false) => Plan::Slot(*a),
                (Job::App(a), true) => self.fg_plan_app(handle, ipc, *a),
                (Job::FgHold, true) => self.fg_plan_hold(),
                (Job::FgHold, false) => Plan::Skip,
            };
            if matches!(plan, Plan::Skip) {
                continue;
            }
            // timestamp: the pose the app rendered with (client tracking timestamp); generated frames carry the sample they were rotated
            // to; green frames carry no pose and are stamped clearly in the past, never behind the last frame sent
            let (ts, slot, counter, submit_s) = match &plan {
                Plan::Slot(a) => {
                    let ts = if a.ts_ns != 0 {
                        Duration::from_nanos(a.ts_ns)
                    } else {
                        self.frames_before_first_tracking.fetch_add(1, Ordering::Relaxed);
                        self.shared.latest_sample_ts.lock().unwrap_or_default()
                    };
                    (ts, Some(a.slot), a.counter, a.submit_s)
                }
                Plan::Fg { ts, counter, submit_s, .. } => (*ts, None, *counter, *submit_s),
                Plan::Green | Plan::Skip => {
                    let ts = self.shared.latest_sample_ts.lock().map(|t| t.saturating_sub(Duration::from_millis(50)))
                        .unwrap_or_else(|| origin.elapsed()).max(*self.last_sent_ts.lock());
                    (ts, None, 0, 0.0)
                }
            };
            let is_green = matches!(plan, Plan::Green);
            {
                let mut last_idr = self.last_idr_restart.lock();
                if self.shared.restart_from_idr.load(Ordering::SeqCst) && last_idr.elapsed() >= Duration::from_millis(100) {
                    self.shared.restart_from_idr.store(false, Ordering::SeqCst);
                    *last_idr = Instant::now();
                    self.idr_restarts.fetch_add(1, Ordering::Relaxed);
                    self.frame_idx.store(0, Ordering::Relaxed);
                }
            }
            let force_idr = self.frame_idx.fetch_add(1, Ordering::Relaxed) == 0;
            {
                let mut last = self.last_sent_ts.lock();
                if !is_green && ts != Duration::ZERO && ts == *last {
                    SAME_TS_FRAMES.fetch_add(1, Ordering::Relaxed);
                }
                *last = (*last).max(ts);
            }
            let l = &mut *enc.live;
            l.packet_bytes = 0;
            l.packet_idr = false;
            l.pending.clear();
            l.hold_now = true; // packets are queued for the sender thread
            if is_green {
                self.ctx.report_present(ts, Duration::ZERO);
                self.ctx.report_composed(ts, Duration::ZERO);
                if !self.green_active.swap(true, Ordering::Relaxed) {
                    unsafe { nvh::nvh_release_shared(handle) }; // the app's ring is gone: free its VRAM (and the generator's frame history)
                    self.fg_have_frame.store(false, Ordering::Relaxed);
                    let mut st = self.fg.lock();
                    st.real_due = false;
                    st.synth_due = false;
                }
            } else {
                // real timings for ALVR's statistics: the app presented at submit_s, the shim finished composing then
                let since_submit = Duration::from_secs_f64((unsafe { nvh::nvh_qpc_seconds() } - submit_s).max(0.0));
                self.ctx.report_present(ts, since_submit);
                self.ctx.report_composed(ts, since_submit);
                self.green_active.store(false, Ordering::Relaxed);
            }
            let t0 = Instant::now();
            let mut err = [0 as std::ffi::c_char; 256];
            let ms = match &plan {
                #[cfg(feature = "simrig")]
                Plan::Slot(a) if crate::simrig::active() => crate::simrig::encode(handle, ipc, a.slot, counter as u32, ts.as_nanos() as u64, force_idr, err.as_mut_ptr(), 256),
                Plan::Slot(a) => unsafe {
                    nvh::nvh_encode_shared(handle, ipc, a.slot, counter as u32, ts.as_nanos() as u64, force_idr as i32, err.as_mut_ptr(), 256)
                },
                #[cfg(feature = "simrig")]
                Plan::Fg { mode, t, q, .. } if crate::simrig::active() => crate::simrig::encode_fg(handle, ipc, *mode, *t, q, counter as u32, ts.as_nanos() as u64, force_idr, err.as_mut_ptr(), 256),
                Plan::Fg { mode, t, q, .. } => unsafe {
                    nvh::nvh_fg_encode(handle, ipc, *mode, *t, q.as_ptr(), counter as u32, ts.as_nanos() as u64, force_idr as i32, err.as_mut_ptr(), 256)
                },
                Plan::Green | Plan::Skip => unsafe { nvh::nvh_encode_green(handle, 0, ts.as_nanos() as u64, force_idr as i32, err.as_mut_ptr(), 256) },
            };
            if let Some(s) = slot {
                // the foveation pass consumed the slot (Transmit returns after the packet is out): the shim may reuse it
                unsafe { nvh::nvh_ipc_slot_busy(ipc, s, 0) };
            }
            let encoded_at = Instant::now();
            if ms < 0.0 {
                l.pending.clear();
                let msg = unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }.to_string_lossy().to_string();
                let total = self.encode_errors.fetch_add(1, Ordering::Relaxed) + 1;
                consecutive_errors += 1;
                event(origin, "encode_error", json!({ "message": msg, "consecutive": consecutive_errors, "total": total, "generated": matches!(plan, Plan::Fg { .. }) }));
                if consecutive_errors == 1 || consecutive_errors >= 30 {
                    logs::general("ERROR", &format!("encode failed ({consecutive_errors} in a row): {msg}"));
                }
                if !self.cfg.daemon || consecutive_errors >= 30 {
                    *self.failed.lock() = Some(msg);
                    return;
                }
                unsafe { nvh::nvh_release_shared(handle) };
                self.fg_have_frame.store(false, Ordering::Relaxed);
                self.shared.restart_from_idr.store(true, Ordering::SeqCst);
                continue;
            }
            consecutive_errors = 0;
            if let Plan::Fg { kind, mode, .. } = &plan {
                match kind {
                    FgKind::Synth => self.fg_synths.fetch_add(1, Ordering::Relaxed),
                    FgKind::Real => self.fg_reals.fetch_add(1, Ordering::Relaxed),
                    FgKind::Repeat => self.fg_repeats.fetch_add(1, Ordering::Relaxed),
                };
                fate(counter, match kind { FgKind::Synth => "G", FgKind::Real => "R", FgKind::Repeat => "Q" }, t0, *mode as u64, ts.as_nanos() as u64);
                fg_encodes += 1;
                if fg_encodes % 10 == 1 {
                    let mut buf = [0 as std::ffi::c_char; 768];
                    unsafe { nvh::nvh_fg_stats(handle, buf.as_mut_ptr(), 768) };
                    *self.fg_stats.lock() = unsafe { std::ffi::CStr::from_ptr(buf.as_ptr()) }.to_string_lossy().to_string();
                }
            }
            let period_ms = self.period().as_secs_f64() * 1000.0;
            let lead_ms = {
                // envelope of the encode time (fast attack, slow decay: 0.05 ms per frame), the send lead in phase pacing
                let mut env = self.encode_env_ms.lock();
                *env = ms.max(*env - 0.05).clamp(1.0, 100.0);
                (*env + 0.5).clamp(1.0, (period_ms - 1.0).max(1.0))
            };
            let release_at = match self.cfg.send_pacing {
                SendPacing::Asap => encoded_at,
                SendPacing::Vsync => {
                    let next = origin + Duration::from_nanos(NEXT_TICK_NS.load(Ordering::Relaxed));
                    if next > encoded_at { next } else { encoded_at }
                }
                SendPacing::Phase => {
                    let t = p.boundary + Duration::from_secs_f64(lead_ms / 1000.0);
                    if t < encoded_at {
                        self.send_late.fetch_add(1, Ordering::Relaxed);
                        encoded_at
                    } else {
                        t
                    }
                }
            };
            // CSV identity: the slot (app frames), -1 (green), -2 (synthetic), -3 (a rendered frame re-stamped by the generator)
            let (arrival_s, slot_s) = match &plan {
                Plan::Slot(a) => (logs::qpc_at(a.arrival), a.slot as i64),
                Plan::Fg { arrival, kind, .. } => (arrival.map(logs::qpc_at).unwrap_or(0.0), if *kind == FgKind::Synth { -2 } else { -3 }),
                Plan::Green | Plan::Skip => (0.0, -1),
            };
            let row = format!("{},{},{},{:.6},{:.6},{:.6},{:.3}", if is_green { -1 } else { counter as i64 }, ts.as_nanos(), slot_s,
                logs::qpc_at(p.boundary), arrival_s, logs::qpc_at(t0), ms);
            let bytes = l.packet_bytes;
            #[allow(unused_mut)]
            let mut pending: Vec<_> = l.pending.drain(..).collect();
            #[cfg(feature = "simrig")]
            let sim_ring = if crate::simrig::active() && !is_green {
                if pending.is_empty() {
                    pending.push((ts, vec![0u8], force_idr, None)); // the simulated encode produces no bitstream: a token for the sender
                }
                crate::simrig::last_ring()
            } else {
                -1
            };
            drop(enc);
            {
                let mut d = self.drained.lock();
                if d.encode_ms.len() < 1 << 16 {
                    d.encode_ms.push(ms);
                    d.compose_ms.push(unsafe { nvh::nvh_ipc_last_compose_ms(ipc) } as f64);
                    let enc_g = self.enc.lock();
                    d.ffr_ms.push(unsafe { nvh::nvh_last_ffr_gpu_ms(enc_g.handle) } as f64);
                    d.ffr_wait_ms.push(unsafe { nvh::nvh_last_ffr_wait_ms(enc_g.handle) } as f64);
                    d.frame_rows.push(format!("{},{},{},{},{ms:.2}", if is_green { -1 } else { counter as i64 }, ts.as_nanos(), bytes, 0));
                    d.sent_ts.push(ts.as_nanos() as u64);
                }
            }
            if is_green {
                self.green_frames.fetch_add(1, Ordering::Relaxed);
            }
            self.shared.bytes_sent.fetch_add(bytes as u64, Ordering::Relaxed);
            let n = pending.len();
            let mut q = self.outgoing.lock();
            for (k, (pts, frame, is_idr, config)) in pending.into_iter().enumerate() {
                q.push_back(Outgoing { release_at, boundary: p.boundary, ts: pts, frame, is_idr, config, encoded_at,
                    row: if k + 1 == n { row.clone() } else { String::new() }, bytes,
                    #[cfg(feature = "simrig")] sim_ring });
            }
            drop(q);
            self.outgoing_cv.notify_one();
            let _ = p.boundary_idx;
        }
    }

    // ---- sender -------------------------------------------------------------------------------------------------------

    fn sender_loop(&self) {
        set_thread_priority(2);
        loop {
            let o = {
                let mut q = self.outgoing.lock();
                while q.is_empty() && !self.stop.load(Ordering::Relaxed) {
                    self.outgoing_cv.wait(&mut q);
                }
                if self.stop.load(Ordering::Relaxed) {
                    return;
                }
                q.pop_front().unwrap()
            };
            if o.release_at > Instant::now() {
                sleep_spin_until(o.release_at);
            }
            let now = Instant::now();
            if let Some(c) = o.config {
                self.ctx.set_video_config_nals(c, CodecType::Hevc);
                self.shared.config_sent.fetch_add(1, Ordering::Relaxed);
            }
            #[cfg(feature = "simrig")]
            if crate::simrig::active() {
                if !o.row.is_empty() {
                    crate::simrig::delivered(o.sim_ring, o.ts.as_nanos() as u64);
                }
            } else {
                self.ctx.send_video_nal(o.ts, o.frame, o.is_idr);
            }
            #[cfg(not(feature = "simrig"))]
            self.ctx.send_video_nal(o.ts, o.frame, o.is_idr);
            if !o.row.is_empty() {
                self.shared.frames_sent.fetch_add(1, Ordering::Relaxed);
                push_bounded(&HOLD_MS, o.release_at.saturating_duration_since(o.encoded_at).as_secs_f64() * 1000.0);
                push_bounded(&SEND_PHASE_MS, now.saturating_duration_since(o.boundary).as_secs_f64() * 1000.0);
                mirror_sent(&format!("{},{:.6},{:.3},{},{}", o.row, logs::qpc_at(now), o.release_at.saturating_duration_since(o.encoded_at).as_secs_f64() * 1000.0,
                    o.bytes, o.is_idr as u8));
                #[cfg(feature = "telemetry")]
                {
                    logs::csv("host_frames.csv", "frame,ts_ns,slot,t_boundary,t_arrival,t_encode_start,encode_ms,t_send,hold_ms,bytes,idr",
                        &format!("{},{:.6},{:.3},{},{}", o.row, logs::qpc_at(now), o.release_at.saturating_duration_since(o.encoded_at).as_secs_f64() * 1000.0,
                            o.bytes, o.is_idr as u8));
                }
            }
        }
    }

    // ---- statistics ---------------------------------------------------------------------------------------------------

    pub fn drain(&self) -> Drained {
        std::mem::take(&mut *self.drained.lock())
    }

    /// Counters for the 2 s statistics windows (deltas are taken by the caller) and the end-of-run report.
    pub fn stats_json(&self) -> serde_json::Value {
        let (mut waits, mut app_ms, mut releases, mut late, mut gate_holds, mut gate_suppressed) = (0u32, 0f32, 0u64, 0u64, 0u64, 0u64);
        unsafe { nvh::nvh_ipc_shim_pacing(self.ipc(), &mut waits, &mut app_ms, &mut releases, &mut late, &mut gate_holds, &mut gate_suppressed) };
        json!({
            "send_pacing": self.cfg.send_pacing.name(),
            "arrived": self.arrived.load(Ordering::Relaxed),
            "superseded": self.superseded.load(Ordering::Relaxed),
            "queue_picks": self.queue_picks.load(Ordering::Relaxed),
            "drains": self.drains.load(Ordering::Relaxed),
            "intake_drains": self.intake_drains.load(Ordering::Relaxed),
            "standing_total": self.standing_total.load(Ordering::Relaxed),
            "intake_gate": self.intake_gate,
            "gate_after": self.gate_after,
            "keepup_boundaries": self.keepup_boundaries,
            "intake_depth": self.intake_depth,
            "pick_policy": self.pick_policy,
            "slots_filled": self.slots_filled.load(Ordering::Relaxed),
            "slots_empty": self.slots_empty.load(Ordering::Relaxed),
            "encoder_overruns": self.encoder_overruns.load(Ordering::Relaxed),
            "send_late": self.send_late.load(Ordering::Relaxed),
            "green_frames": self.green_frames.load(Ordering::Relaxed),
            "encode_errors": self.encode_errors.load(Ordering::Relaxed),
            "idr_restarts": self.idr_restarts.load(Ordering::Relaxed),
            "encode_envelope_ms": *self.encode_env_ms.lock(),
            "gpu_scheduling": self.gpu_sched.lock().map(|(c, st)| json!({ "class": c, "status": st })),
            "frame_gen": {
                "on": self.fg_on.load(Ordering::Relaxed), "synths": self.fg_synths.load(Ordering::Relaxed), "reals": self.fg_reals.load(Ordering::Relaxed),
                "repeats": self.fg_repeats.load(Ordering::Relaxed), "holds": self.fg_holds.load(Ordering::Relaxed), "no_sample": self.fg_no_sample.load(Ordering::Relaxed),
                "dropped": self.fg_dropped.load(Ordering::Relaxed), "pairs_skipped": self.fg_pairs_skipped.load(Ordering::Relaxed), "errors": self.fg_errors.load(Ordering::Relaxed),
                "passthrough": self.fg.try_lock().map(|s| s.passthrough).unwrap_or(0),
                "gen": serde_json::from_str::<serde_json::Value>(&self.fg_stats.lock()).unwrap_or(serde_json::Value::Null),
            },
            "shim": { "slot_busy_waits": waits, "app_frame_ms": app_ms, "releases": releases, "releases_late": late, "gate_holds": gate_holds, "gate_suppressed": gate_suppressed },
        })
    }
}
