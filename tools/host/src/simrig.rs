//! The simrig (cargo feature `simrig`, docs/DEVELOPMENT.md): the real host pipeline (shim IPC, intake, clock + boundary pick, encoder and sender
//! threads) running without a headset. A synthetic head (constant rotation or still) with the timing imperfections of a recorded profile
//! (harness/simrig/make_profile.py) replaces the ALVR client's tracking, the encode / network / decode stages are timers (the encode optionally
//! the real NVENC, bitstream discarded), and the viewer (nvenc/hostlib/simviewer.cpp) shows the frames like the headset would and measures
//! the dots of the probe's "dots" scene for harness/simrig/judge.py. Nothing here exists in a build without the feature.
//!
//! Knobs (environment, all optional): VISIONALVR_SIM_PROFILE (profile JSON; none = ideal timing), _SEED, _MODE (rotate|still), _OMEGA_DPS (90),
//! _PREDICT_MS (client prediction horizon, 0), _POSE_NOISE_DEG (override of the profile-derived pose noise), _REAL_ENCODE (0|1), _SHOW (1),
//! _REPROJECT (1), _SECONDS (30, viewer run), _SETTLE_S (2, after the first frame), _PHOTON_ERR_MS (0), _STAMP_ERROR (0), _QUEUE_MAX (2),
//! _POLL_MS (5), _DUMP_EVERY (0), _DOT_DEG (10), _PERIOD_MS (10), _EYE_W (1760, the viewer's eye image).
use crate::{nvh, Shared};
use serde_json::Value;
use std::ffi::{c_char, c_void};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread;
use std::time::Duration;

#[repr(C)]
struct SvCfg {
    out_dir: *const c_char,
    show: i32,
    reproject: i32,
    out_eye_w: i32,
    out_eye_h: i32,
    period_ns: f64,
    tick_jitter_ns: f64,
    photon_err_ms: f64,
    dot_deg: f64,
    poll_ms: i32,
    queue_max: i32,
    dump_every: i32,
    seconds: i32,
    seed: u64,
    t0_qpc_s: f64,
    ts_base_ns: u64,
    fov8: [f32; 8],
    gamma: f32,
    stamp_error: f64,
}

extern "C" {
    fn sv_create(nvh: *mut c_void, cfg: *const SvCfg) -> *mut c_void;
    fn sv_capture(h: *mut c_void, nvh: *mut c_void, ipc: *mut c_void, slot: u32, counter: u32) -> i32;
    fn sv_post(h: *mut c_void, ring: i32, ts_ns: u64, ready_qpc_s: f64);
    fn sv_sample(h: *mut c_void, ts_ns: u64, q: *const f32);
    fn sv_truth(h: *mut c_void, t0_ns: f64, dt_ns: f64, q4: *const f32, n: i32);
    fn sv_start(h: *mut c_void, t0_qpc_s: f64, ts_base_ns: u64);
    fn sv_running(h: *mut c_void) -> i32;
}

/// Vision Pro field of view (tangents l r u d per eye) and eye offsets, as the stock client reports them.
pub const FOV8: [f32; 8] = [1.78736, 1.02046, 1.21936, 1.02046, 1.02507, 1.80002, 1.22559, 1.02507];
pub const OFF6: [f32; 6] = [-0.031058, 0.0, 0.0, 0.031058, 0.0, 0.0];

/// xorshift + Box-Muller: seeded, no dependency, identical on every machine.
struct Rng(u64);
impl Rng {
    fn next(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        self.0 = x;
        x.wrapping_mul(0x2545F4914F6CDD1D)
    }
    fn uniform(&mut self) -> f64 {
        ((self.next() >> 11) as f64 + 0.5) / (1u64 << 53) as f64
    }
    fn gauss(&mut self) -> f64 {
        (-2.0 * self.uniform().ln()).sqrt() * (2.0 * std::f64::consts::PI * self.uniform()).cos()
    }
}

#[derive(Default, Clone)]
struct Dist {
    samples: Vec<f64>,
    mean: f64,
    sd: f64,
}
impl Dist {
    fn from(v: &Value) -> Self {
        Self {
            samples: v["samples"].as_array().map(|a| a.iter().filter_map(|x| x.as_f64()).collect()).unwrap_or_default(),
            mean: v["mean"].as_f64().unwrap_or(0.0),
            sd: v["sd"].as_f64().unwrap_or(0.0),
        }
    }
    /// empirical resample (the real pattern, incl. its tails); the gaussian fit when the profile carries no samples
    fn draw(&self, r: &mut Rng) -> f64 {
        if self.samples.is_empty() {
            (self.mean + self.sd * r.gauss()).max(0.0)
        } else {
            self.samples[(r.next() % self.samples.len() as u64) as usize]
        }
    }
}

struct Cfg {
    real_encode: bool,
    seconds: i32,
    settle_s: f64,
    period_ns: f64,
    omega_dps: f64,
    rotate: bool,
    predict_ms: f64,
    pose_noise_deg: f64,
    seed: u64,
    tick_jitter_ns: f64,
    photon_err_ms: f64,
    stamp_error: f64,
    show: bool,
    reproject: bool,
    queue_max: i32,
    poll_ms: i32,
    dump_every: i32,
    dot_deg: f64,
    eye_w: i32,
    uplink: Dist,
    encode: Dist,
    network: Dist,
    decode: Dist,
}

struct Sim {
    cfg: Cfg,
    viewer: usize,
    out_dir: String,
    t0_qpc: f64,
    ts_base: u64,
    first_frame_at: Mutex<Option<f64>>,
    started: AtomicBool,
    rng: Mutex<Rng>,
    last_ready: Mutex<f64>,
    stop: AtomicBool,
    /// VISIONALVR_SIM_CAPTURE=intake: the viewer's copy of a frame is taken when it arrives from the shim (diagnosis: is the slot still the frame at encode time?)
    capture_intake: bool,
    early: Mutex<std::collections::HashMap<u32, i32>>,
}

static SIM: OnceLock<Sim> = OnceLock::new();
static LAST_RING: AtomicUsize = AtomicUsize::new(usize::MAX);

pub fn active() -> bool {
    SIM.get().is_some()
}

fn env_f(k: &str, d: f64) -> f64 {
    std::env::var(format!("VISIONALVR_SIM_{k}")).ok().and_then(|v| v.parse().ok()).unwrap_or(d)
}

fn qpc() -> f64 {
    unsafe { nvh::nvh_qpc_seconds() }
}

fn sleep_until(t: f64) {
    loop {
        let d = t - qpc();
        if d <= 0.0 {
            return;
        }
        if d > 0.002 {
            thread::sleep(Duration::from_micros(((d - 0.0015) * 1e6) as u64));
        } else {
            std::hint::spin_loop();
        }
    }
}

/// yaw about +Y as a quaternion (x y z w)
fn yaw_q(deg: f64) -> [f64; 4] {
    let h = deg.to_radians() * 0.5;
    [0.0, h.sin(), 0.0, h.cos()]
}

fn qmul(a: [f64; 4], b: [f64; 4]) -> [f64; 4] {
    [
        a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
        a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
        a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
        a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
    ]
}

fn truth_yaw_deg(c: &Cfg, since_base_s: f64) -> f64 {
    if c.rotate { c.omega_dps * since_base_s } else { 0.0 }
}

/// Starts the simrig: the synthetic client (head generator + uplink) and the viewer. Called once the pipeline exists.
/// `eye_size` is the shim's per-eye size (from the session), `gamma` the stream's encoding gamma.
pub fn init(out_dir: &str, ipc: usize, nvh_handle: *mut c_void, shared: &Arc<Shared>, vsync_period_ns: &Arc<std::sync::atomic::AtomicU64>, gamma: f32) {
    let profile: Value = std::env::var("VISIONALVR_SIM_PROFILE").ok().and_then(|p| std::fs::read_to_string(p).ok()).and_then(|t| serde_json::from_str(&t).ok()).unwrap_or(Value::Null);
    let ideal = profile.is_null();
    let period_ns = env_f("PERIOD_MS", 10.0) * 1e6;
    let predict_ms = env_f("PREDICT_MS", 0.0);
    let omega_noise = profile["omega_noise_dps"].as_f64().unwrap_or(0.0);
    let cfg = Cfg {
        real_encode: env_f("REAL_ENCODE", 0.0) > 0.5,
        seconds: env_f("SECONDS", 30.0) as i32,
        settle_s: env_f("SETTLE_S", 2.0),
        period_ns,
        omega_dps: env_f("OMEGA_DPS", 90.0),
        rotate: std::env::var("VISIONALVR_SIM_MODE").map(|m| m != "still").unwrap_or(true),
        predict_ms,
        // the pose the client reports is predicted `predict_ms` ahead from a noisy velocity: angle error ~ velocity noise x horizon
        // (a floor of half a period stands for the sensor noise at no prediction)
        pose_noise_deg: env_f("POSE_NOISE_DEG", omega_noise * (predict_ms / 1000.0).max(period_ns * 0.5e-9)),
        seed: env_f("SEED", 1.0) as u64,
        tick_jitter_ns: if ideal || env_f("NO_JITTER", 0.0) > 0.5 { 0.0 } else { profile["tick"]["jitter_ns_sd"].as_f64().unwrap_or(0.0) },
        photon_err_ms: env_f("PHOTON_ERR_MS", 0.0),
        stamp_error: env_f("STAMP_ERROR", 0.0),
        show: env_f("SHOW", 1.0) > 0.5,
        reproject: env_f("REPROJECT", 1.0) > 0.5,
        queue_max: env_f("QUEUE_MAX", 2.0) as i32,
        poll_ms: env_f("POLL_MS", 5.0) as i32,
        dump_every: env_f("DUMP_EVERY", 0.0) as i32,
        dot_deg: env_f("DOT_DEG", 10.0),
        eye_w: env_f("EYE_W", 1760.0) as i32,
        uplink: if ideal { Dist::default() } else { Dist::from(&profile["uplink_ms"]) },
        encode: if ideal { Dist { mean: 3.0, ..Default::default() } } else { Dist::from(&profile["encode_ms"]) },
        network: if ideal { Dist { mean: 7.0, ..Default::default() } } else { Dist::from(&profile["network_ms"]) },
        decode: if ideal { Dist { mean: 5.0, ..Default::default() } } else { Dist::from(&profile["decode_ms"]) },
    };
    std::fs::create_dir_all(out_dir).ok();
    let oh = (cfg.eye_w as f64 * (FOV8[2] + FOV8[3]) as f64 / (FOV8[0] + FOV8[1]) as f64).round() as i32;
    let dir_c = std::ffi::CString::new(out_dir).unwrap();
    let t0 = qpc() + 1.0;
    let ts_base = 3_600_000_000_000u64; // a client clock that does not start at zero
    let sc = SvCfg {
        out_dir: dir_c.as_ptr(),
        show: cfg.show as i32,
        reproject: cfg.reproject as i32,
        out_eye_w: cfg.eye_w,
        out_eye_h: oh,
        period_ns: cfg.period_ns,
        tick_jitter_ns: cfg.tick_jitter_ns,
        photon_err_ms: cfg.photon_err_ms,
        dot_deg: cfg.dot_deg,
        poll_ms: cfg.poll_ms,
        queue_max: cfg.queue_max,
        dump_every: cfg.dump_every,
        seconds: cfg.seconds,
        seed: cfg.seed ^ 0x9e3779b97f4a7c15,
        t0_qpc_s: 0.0,
        ts_base_ns: 0,
        fov8: FOV8,
        gamma,
        stamp_error: cfg.stamp_error,
    };
    let viewer = unsafe { sv_create(nvh_handle, &sc) };
    if viewer.is_null() {
        eprintln!("simrig: the viewer could not be created");
        std::process::exit(6);
    }
    // the truth trajectory the viewer corrects to: 1 ms table over the run plus margins
    let n = ((cfg.seconds as f64 + cfg.settle_s + 120.0) * 1000.0) as usize;
    let mut tq = Vec::with_capacity(n * 4);
    for i in 0..n {
        let q = yaw_q(truth_yaw_deg(&cfg, i as f64 * 1e-3 - 1.0));
        tq.extend(q.iter().map(|x| *x as f32));
    }
    // the table starts at ts_base - 1 s so that early samples (before t0) resolve too; row 0 is at (ts_base - 1 s)
    unsafe { sv_truth(viewer, ts_base as f64 - 1e9, 1e6, tq.as_ptr(), n as i32) };
    let log_cfg = serde_json::json!({
        "profile": std::env::var("VISIONALVR_SIM_PROFILE").ok(), "seed": cfg.seed, "mode": if cfg.rotate { "rotate" } else { "still" }, "omega_dps": cfg.omega_dps,
        "predict_ms": cfg.predict_ms, "pose_noise_deg": cfg.pose_noise_deg, "real_encode": cfg.real_encode, "reproject": cfg.reproject, "tick_jitter_ns": cfg.tick_jitter_ns,
        "photon_err_ms": cfg.photon_err_ms, "stamp_error": cfg.stamp_error, "queue_max": cfg.queue_max, "poll_ms": cfg.poll_ms, "period_ms": cfg.period_ns / 1e6, "eye_w": cfg.eye_w,
    });
    std::fs::write(format!("{out_dir}/sim_config.json"), serde_json::to_string_pretty(&log_cfg).unwrap()).ok();
    let seed = cfg.seed;
    let sim = Sim {
        cfg,
        viewer: viewer as usize,
        out_dir: out_dir.to_string(),
        t0_qpc: t0,
        ts_base,
        first_frame_at: Mutex::new(None),
        started: AtomicBool::new(false),
        rng: Mutex::new(Rng(seed.wrapping_mul(0x9E3779B97F4A7C15) | 1)),
        last_ready: Mutex::new(0.0),
        stop: AtomicBool::new(false),
        capture_intake: std::env::var("VISIONALVR_SIM_CAPTURE").map(|v| v == "intake").unwrap_or(false),
        early: Mutex::new(Default::default()),
    };
    NVH.store(nvh_handle as usize, Ordering::Relaxed);
    let _ = SIM.set(sim);
    // the client side: connected, stream ready, views, the display period the host's clock follows
    vsync_period_ns.store(SIM.get().unwrap().cfg.period_ns as u64, Ordering::Relaxed);
    crate::FOLLOW_RATE.store(true, Ordering::Relaxed);
    let _ = SIM_SHARED.set(Arc::clone(shared));
    *shared.pending_views.lock() = Some((FOV8, OFF6));
    shared.client_connected.store(true, Ordering::SeqCst);
    shared.stream_ready.store(true, Ordering::SeqCst);
    let _ = ipc;
    thread::Builder::new().name("simrig-head".into()).spawn(head_generator).expect("simrig head thread");
}

/// The synthetic client: samples every period (+ jitter), the pose the client would report (truth at ts + prediction horizon + noise),
/// delivered to the shim through the uplink FIFO (sampled delay, in order).
fn head_generator() {
    let s = SIM.get().unwrap();
    let ipc = IPC.load(Ordering::Relaxed) as *mut c_void;
    let viewer = s.viewer as *mut c_void;
    let mut rng = Rng(s.cfg.seed.wrapping_mul(0xD1B54A32D192ED03) | 1);
    let mut pending: std::collections::VecDeque<(f64, u64, [f32; 4])> = Default::default();
    let mut last_deliver = 0f64;
    let mut k = 0u64;
    let t0 = s.t0_qpc;
    let mut next_gen = t0 + s.cfg.tick_jitter_ns * 1e-9 * rng.gauss();
    // started (and its first sample generated) one period before the viewer's t0 so the client has samples for the first ticks
    while !s.stop.load(Ordering::Relaxed) {
        let t_next_del = pending.front().map(|p| p.0).unwrap_or(f64::MAX);
        let t = next_gen.min(t_next_del);
        sleep_until(t);
        if next_gen <= t_next_del {
            let tw = next_gen;
            let ts = s.ts_base + ((tw - t0) * 1e9).max(-1e9) as u64;
            let since = (tw - t0) + s.cfg.predict_ms * 1e-3;
            let yaw = truth_yaw_deg(&s.cfg, since);
            let nz = s.cfg.pose_noise_deg;
            let dq = [rng.gauss() * nz * 0.5, rng.gauss() * nz, rng.gauss() * nz * 0.5];
            let mut q = qmul(yaw_q(yaw), {
                let (x, y, z) = (dq[0].to_radians() * 0.5, dq[1].to_radians() * 0.5, dq[2].to_radians() * 0.5);
                let n = (1.0 + x * x + y * y + z * z).sqrt();
                [x / n, y / n, z / n, 1.0 / n]
            });
            let n = (q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]).sqrt();
            for v in q.iter_mut() {
                *v /= n;
            }
            let qf = [q[0] as f32, q[1] as f32, q[2] as f32, q[3] as f32];
            unsafe { sv_sample(viewer, ts, qf.as_ptr()) };
            let up = (s.cfg.uplink.draw(&mut rng) - 0.0).max(0.0) * 1e-3 + 0.002;
            let deliver = (tw + up).max(last_deliver);
            last_deliver = deliver;
            pending.push_back((deliver, ts, qf));
            k += 1;
            next_gen = t0 + k as f64 * s.cfg.period_ns * 1e-9 + s.cfg.tick_jitter_ns * 1e-9 * rng.gauss();
        } else if let Some((_, ts, qf)) = pending.pop_front() {
            let (p, z) = ([0f32, 1.6, 0.0], [0f32; 3]);
            if let Some(sh) = SIM_SHARED.get() {
                crate::push_recent_sample(sh, Duration::from_nanos(ts), qf); // the frame generator stamps from this ring
            }
            unsafe { nvh::nvh_ipc_publish_head(ipc, ts, qf.as_ptr(), p.as_ptr(), z.as_ptr(), z.as_ptr()) };
        }
    }
}

static NVH: AtomicUsize = AtomicUsize::new(0);
/// the host's shared state: the synthetic samples also feed `recent_samples` (frame generation stamps from it)
static SIM_SHARED: OnceLock<Arc<Shared>> = OnceLock::new();
static IPC: AtomicUsize = AtomicUsize::new(0);

pub fn set_ipc(ipc: usize) {
    IPC.store(ipc, Ordering::Relaxed);
}

/// Replaces `nvh_encode_shared` for an app frame: the sampled encode time (or the real encode), and the frame's copy for the viewer.
/// Returns the encode time in ms (< 0: error, `err` filled) like nvh_encode_shared; the viewer's ring index goes to LAST_RING.
pub fn encode(nvh_handle: *mut c_void, ipc: *mut c_void, slot: u32, counter: u32, ts_ns: u64, force_idr: bool, err: *mut c_char, errlen: i32) -> f64 {
    let s = SIM.get().unwrap();
    let now = qpc();
    let first = *s.first_frame_at.lock().unwrap().get_or_insert(now);
    let viewer = s.viewer as *mut c_void;
    if !s.started.load(Ordering::Relaxed) && now - first >= s.cfg.settle_s {
        // the viewer's tick 0 lands on the next period boundary of the head generator's clock
        let k = ((now + 0.2 - s.t0_qpc) / (s.cfg.period_ns * 1e-9)).ceil();
        let t0v = s.t0_qpc + k * s.cfg.period_ns * 1e-9;
        unsafe { sv_start(viewer, t0v, s.ts_base + (k * s.cfg.period_ns) as u64) };
        s.started.store(true, Ordering::Relaxed);
    }
    let ms = if s.cfg.real_encode {
        unsafe { nvh::nvh_encode_shared(nvh_handle, ipc, slot, counter, ts_ns, force_idr as i32, err, errlen) }
    } else {
        let d = s.cfg.encode.draw(&mut s.rng.lock().unwrap());
        sleep_until(now + d * 1e-3);
        d
    };
    let ring = if s.capture_intake {
        s.early.lock().unwrap().remove(&counter).unwrap_or(-1)
    } else if ms >= 0.0 && s.started.load(Ordering::Relaxed) {
        unsafe { sv_capture(viewer, nvh_handle, ipc, slot, counter) }
    } else {
        -1
    };
    LAST_RING.store(if ring >= 0 { ring as usize } else { usize::MAX }, Ordering::Relaxed);
    ms
}

/// Replaces `nvh_fg_encode` (frame generation): the generated picture is always rendered and encoded for real (the viewer must see it),
/// then captured for the viewer like a slot (nvh_sim_slot returns the generator's output after such an encode).
pub fn encode_fg(nvh_handle: *mut c_void, ipc: *mut c_void, mode: i32, t: f64, q_out: &[f32; 4], counter: u32, ts_ns: u64, force_idr: bool, err: *mut c_char, errlen: i32) -> f64 {
    let s = SIM.get().unwrap();
    let now = qpc();
    let first = *s.first_frame_at.lock().unwrap().get_or_insert(now);
    let viewer = s.viewer as *mut c_void;
    if !s.started.load(Ordering::Relaxed) && now - first >= s.cfg.settle_s {
        let k = ((now + 0.2 - s.t0_qpc) / (s.cfg.period_ns * 1e-9)).ceil();
        let t0v = s.t0_qpc + k * s.cfg.period_ns * 1e-9;
        unsafe { sv_start(viewer, t0v, s.ts_base + (k * s.cfg.period_ns) as u64) };
        s.started.store(true, Ordering::Relaxed);
    }
    let ms = unsafe { nvh::nvh_fg_encode(nvh_handle, ipc, mode, t, q_out.as_ptr(), counter, ts_ns, force_idr as i32, err, errlen) };
    // every output its own ring slot: a rendered frame goes out more than once (R then Q) and the viewer may still hold the earlier picture
    // in its queue when the next one is captured (keying by frame counter overwrote it: a one-period error on R/Q frames, 2026-10-14)
    static FG_CAPTURES: AtomicUsize = AtomicUsize::new(0);
    let key = FG_CAPTURES.fetch_add(1, Ordering::Relaxed) as u32;
    let ring = if ms >= 0.0 && s.started.load(Ordering::Relaxed) { unsafe { sv_capture(viewer, nvh_handle, ipc, 0, key) } } else { -1 };
    LAST_RING.store(if ring >= 0 { ring as usize } else { usize::MAX }, Ordering::Relaxed);
    ms
}

pub fn last_ring() -> i32 {
    let r = LAST_RING.load(Ordering::Relaxed);
    if r == usize::MAX { -1 } else { r as i32 }
}

/// Replaces `send_video_nal`: the frame reaches the viewer after the sampled network + decode time (in order, like the stream).
pub fn delivered(ring: i32, ts_ns: u64) {
    let s = SIM.get().unwrap();
    if ring < 0 {
        return;
    }
    let (net, dec) = {
        let mut r = s.rng.lock().unwrap();
        (s.cfg.network.draw(&mut r), s.cfg.decode.draw(&mut r))
    };
    let mut last = s.last_ready.lock().unwrap();
    let ready = (qpc() + (net + dec) * 1e-3).max(*last);
    *last = ready;
    unsafe { sv_post(s.viewer as *mut c_void, ring, ts_ns, ready) };
}

pub fn finished() -> bool {
    SIM.get().map(|s| s.started.load(Ordering::Relaxed) && unsafe { sv_running(s.viewer as *mut c_void) } == 0).unwrap_or(false)
}

pub fn stop() {
    if let Some(s) = SIM.get() {
        s.stop.store(true, Ordering::Relaxed);
        let _ = &s.out_dir;
    }
}

/// Called by the intake thread the moment a frame arrives (VISIONALVR_SIM_CAPTURE=intake).
pub fn on_arrival(ipc: *mut c_void, slot: u32, counter: u32) {
    let s = SIM.get().unwrap();
    if s.capture_intake && s.started.load(Ordering::Relaxed) {
        let ring = unsafe { sv_capture(s.viewer as *mut c_void, NVH.load(Ordering::Relaxed) as *mut c_void, ipc, slot, counter) };
        s.early.lock().unwrap().insert(counter, ring);
    }
}
