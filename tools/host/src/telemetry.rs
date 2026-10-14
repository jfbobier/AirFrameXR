//! Debug-flavour telemetry that does not belong to the frame path (cargo feature `telemetry`, docs/DEVELOPMENT.md):
//!   audio_capture.csv  every WASAPI loopback capture callback of ALVR's game-audio thread (patched alvr_audio, tools/patches/audio-trace.patch):
//!                      time, frames captured, send failed, gap since the previous callback, time spent in the callback
//!   sched.csv          once a second: how late a normal-priority 10 ms sleep wakes up (CPU starvation shows here before it shows as audio
//!                      skips or a late frame): count, p50, p99, max (microseconds) and the number over 2 ms
//! Both need a debug session folder (logs::csv writes nothing without one).

#[cfg(feature = "telemetry")]
pub fn start() {
    use crate::logs;
    use std::time::{Duration, Instant};
    let _ = alvr_audio::TRACE.set(Box::new(|kind, t, v| match kind {
        "S" => logs::csv("audio_capture.csv", "t,kind,frames,send_failed,gap_us,callback_us", &format!("{:.6},S,{},{},0,0", logs::qpc_at(t), v[0], v[1])),
        _ => logs::csv("audio_capture.csv", "t,kind,frames,send_failed,gap_us,callback_us", &format!("{:.6},C,{},{},{},{}", logs::qpc_at(t), v[0], v[1], v[2], v[3])),
    }));
    std::thread::Builder::new()
        .name("sched_probe".into())
        .spawn(|| {
            let mut over: Vec<u64> = Vec::with_capacity(128);
            let mut window = Instant::now();
            loop {
                let t0 = Instant::now();
                std::thread::sleep(Duration::from_millis(10));
                over.push((t0.elapsed().as_micros() as u64).saturating_sub(10_000));
                if window.elapsed() >= Duration::from_secs(1) {
                    over.sort_unstable();
                    let n = over.len();
                    let late = over.iter().filter(|x| **x > 2000).count();
                    logs::csv("sched.csv", "t,n,p50_us,p99_us,max_us,over_2ms",
                        &format!("{:.6},{},{},{},{},{}", logs::qpc_at(Instant::now()), n, over[n / 2], over[(n * 99 / 100).min(n - 1)], over[n - 1], late));
                    over.clear();
                    window = Instant::now();
                }
            }
        })
        .ok();
}

#[cfg(not(feature = "telemetry"))]
pub fn start() {}

/// One head sample as the host received it (debug flavour): lets the analysis compare the pose a frame was rendered with against the pose
/// the client has for the frame's timestamp (harness/frames.py). Written to the debug session folder, and to VISIONALVR_HEAD_CSV when set
/// (the lab, which has no debug session).
#[cfg(feature = "telemetry")]
pub fn head_sample(ts_ns: u64, q: [f32; 4], p: [f32; 3]) {
    use crate::logs;
    use std::io::Write;
    static LAB: std::sync::Mutex<Option<std::io::BufWriter<std::fs::File>>> = std::sync::Mutex::new(None);
    let t = logs::qpc_at(std::time::Instant::now());
    let row = format!("{:.6},{},{:.7},{:.7},{:.7},{:.7},{:.5},{:.5},{:.5}", t, ts_ns, q[0], q[1], q[2], q[3], p[0], p[1], p[2]);
    logs::csv("head_samples.csv", "t,ts_ns,qx,qy,qz,qw,px,py,pz", &row);
    if let Ok(path) = std::env::var("VISIONALVR_HEAD_CSV") {
        if let Ok(mut g) = LAB.lock() {
            if g.is_none() {
                if let Ok(f) = std::fs::File::create(&path) {
                    let mut w = std::io::BufWriter::new(f);
                    let _ = writeln!(w, "t,ts_ns,qx,qy,qz,qw,px,py,pz");
                    *g = Some(w);
                }
            }
            if let Some(w) = g.as_mut() {
                let _ = writeln!(w, "{row}");
                let _ = w.flush();
            }
        }
    }
}
/// Release flavour (no `telemetry` feature): the samples are still written while a telemetry run is armed (the report needs the arrival
/// gaps and the poses of the stamps); otherwise nothing.
#[cfg(not(feature = "telemetry"))]
#[inline]
pub fn head_sample(ts_ns: u64, q: [f32; 4], p: [f32; 3]) {
    if SEG.load(Ordering::Relaxed) < 0 {
        return;
    }
    let t = crate::logs::qpc_at(Instant::now());
    crate::logs::csv("head_samples.csv", "t,ts_ns,qx,qy,qz,qw,px,py,pz",
        &format!("{:.6},{},{:.7},{:.7},{:.7},{:.7},{:.5},{:.5},{:.5}", t, ts_ns, q[0], q[1], q[2], q[3], p[0], p[1], p[2]));
}

// ---- AVP telemetry session: the host half of the automated A/B schedule (docs/TELEMETRY.md) ------------------------------------------
//
// A telemetry-flavour visionOS client announces itself with `[pt] start` through ALVR's client log channel (client lines arrive in
// server_core's logger at `Client <host>: [<level>] [ALVR NATIVE] [pt] ...`, see logs.rs). Every `[pt] ` line is captured with the
// host's receive time into `telemetry_client.log` in the debug session folder (turned on when the first line arrives) and never
// reaches the general log. `[pt] start` arms the scheduler: warm-up, then 20 segments of 45 s with a re-stamp mode each, switched
// live (shim side: the IPC flag the shim reads per frame; host side: an override the pipeline reads per frame). Normal clients never
// say `[pt] start`, so nothing here runs for them.

use std::sync::atomic::{AtomicI32, AtomicU64, AtomicU8, AtomicUsize, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

/// Which path makes sure frames leave with a timestamp newer than the previous one.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Mode {
    /// the frame leaves with the timestamp of the sample the game rendered with (duplicates stay)
    Off,
    /// the shim waits for the next tracking sample at submit and re-stamps (video.dup_restamp)
    Shim,
    /// the host stamps at encode time with the next sample and warps the picture (VISIONALVR_HOST_STAMP)
    Host,
}

impl Mode {
    pub fn name(self) -> &'static str {
        match self {
            Mode::Off => "off",
            Mode::Shim => "shim",
            Mode::Host => "host",
        }
    }
    pub fn parse(s: &str) -> Option<Mode> {
        match s.trim().to_ascii_lowercase().as_str() {
            "off" | "none" | "0" => Some(Mode::Off),
            "shim" => Some(Mode::Shim),
            "host" => Some(Mode::Host),
            _ => None,
        }
    }
}

/// The configured mode (everything off unless asked): VISIONALVR_RESTAMP_MODE=off|shim|host, else the legacy switches
/// VISIONALVR_HOST_STAMP=1 / VISIONALVR_DUP_RESTAMP=1.
pub fn base_mode_from_env() -> Mode {
    if let Some(m) = std::env::var("VISIONALVR_RESTAMP_MODE").ok().and_then(|v| Mode::parse(&v)) {
        return m;
    }
    if std::env::var("VISIONALVR_HOST_STAMP").map(|v| v == "1").unwrap_or(false) {
        return Mode::Host;
    }
    if std::env::var("VISIONALVR_DUP_RESTAMP").map(|v| v == "1").unwrap_or(false) {
        return Mode::Shim;
    }
    Mode::Off
}

/// Segments after the warm-up: (mode). Tier A cycles 1 and 2 (balanced order), then tier B (off/shim only).
pub const SCHEDULE: [Mode; 20] = [
    Mode::Off, Mode::Shim, Mode::Host, Mode::Off, Mode::Shim, Mode::Host,
    Mode::Host, Mode::Shim, Mode::Off, Mode::Host, Mode::Shim, Mode::Off,
    Mode::Off, Mode::Off, Mode::Shim, Mode::Shim,
    Mode::Shim, Mode::Shim, Mode::Off, Mode::Off,
];
pub const WARMUP_S: f64 = 120.0;
pub const SEGMENT_S: f64 = 45.0;
/// when, inside the warm-up, the host-side warp resources are created (identity warp on a few frames): the first host-mode frame
/// would otherwise compile the warp shader and create its views mid-segment
const PREWARM_AT_S: f64 = 30.0;
const PREWARM_FRAMES: i32 = 16;

const NOT_ARMED: u8 = 255;
static MODE: AtomicU8 = AtomicU8::new(NOT_ARMED);
static SEG: AtomicI32 = AtomicI32::new(-1);
static GENERATION: AtomicU64 = AtomicU64::new(0);
static IPC_ADDR: AtomicUsize = AtomicUsize::new(0);
static PREWARM_LEFT: AtomicI32 = AtomicI32::new(0);
static BASE: Mutex<Option<(Mode, f32)>> = Mutex::new(None);
static MANUAL: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// The shared-memory block of the shim (set once the host owns it) and the configured mode / shim wait (applied again when a run ends).
pub fn configure(ipc: usize, base: Mode, dup_wait_ms: f32) {
    IPC_ADDR.store(ipc, Ordering::SeqCst);
    *BASE.lock().unwrap() = Some((base, dup_wait_ms));
}

/// The scheduled mode while a run is armed (None: the configured mode applies).
pub fn armed_mode() -> Option<Mode> {
    match MODE.load(Ordering::Relaxed) {
        0 => Some(Mode::Off),
        1 => Some(Mode::Shim),
        2 => Some(Mode::Host),
        _ => None,
    }
}

/// The segment number of the armed run (0 = warm-up, -1 = not armed).
pub fn segment() -> i32 {
    SEG.load(Ordering::Relaxed)
}

/// True once per call while the host-side warp resources still have to be created: the pipeline then sets an identity warp for the frame.
pub fn take_prewarm() -> bool {
    PREWARM_LEFT.load(Ordering::Relaxed) > 0 && PREWARM_LEFT.fetch_sub(1, Ordering::Relaxed) > 0
}

fn set_mode(m: Mode) {
    MODE.store(m as u8, Ordering::SeqCst);
}

/// Applies the mode to the shim's live flag (the host side reads `armed_mode` per frame).
fn apply_shim(mode: Mode) {
    let ipc = IPC_ADDR.load(Ordering::SeqCst);
    let wait = BASE.lock().unwrap().map(|b| b.1).unwrap_or(12.0);
    if ipc != 0 {
        unsafe { crate::nvh::nvh_ipc_set_dup_restamp(ipc as *mut std::ffi::c_void, (mode == Mode::Shim) as u32, wait) };
    }
}

struct Files {
    client: Option<std::io::BufWriter<std::fs::File>>,
}
static FILES: Mutex<Files> = Mutex::new(Files { client: None });

fn open_client_log() {
    let mut f = FILES.lock().unwrap();
    if f.client.is_some() {
        return;
    }
    let dir = crate::logs::debug_dir().or_else(|| crate::logs::set_debug(true));
    if let Some(dir) = dir {
        if let Ok(file) = std::fs::OpenOptions::new().create(true).append(true).open(dir.join("telemetry_client.log")) {
            f.client = Some(std::io::BufWriter::new(file));
        }
    }
}

/// One line into telemetry_client.log (flushed at once: ~10 lines a second, and a crash must not eat the run).
fn write_line(t: f64, text: &str) {
    use std::io::Write;
    let mut f = FILES.lock().unwrap();
    if let Some(w) = f.client.as_mut() {
        let _ = writeln!(w, "{t:.6} {text}");
        let _ = w.flush();
    }
}

/// A `[pt] ` line from the client (text starts at `[pt] `), with the host's receive time (QPC seconds, the clock of the other host CSVs).
pub fn client_line(t: f64, text: &str) {
    open_client_log();
    write_line(t, text);
    let verb = text.strip_prefix("[pt] ").and_then(|r| r.split_whitespace().next()).unwrap_or("");
    match verb {
        "start" => {
            MANUAL.store(false, Ordering::SeqCst); // a telemetry client takes over from a manual start
            arm()
        }
        "end" => {
            // the client finished (or was told to stop): stop scheduling, back to the configured mode
            write_line(crate::logs::qpc_now(), "[tel] client_end");
            disarm();
        }
        _ => {}
    }
}

fn disarm() {
    GENERATION.fetch_add(1, Ordering::SeqCst);
    MODE.store(NOT_ARMED, Ordering::SeqCst);
    SEG.store(-1, Ordering::SeqCst);
    PREWARM_LEFT.store(0, Ordering::SeqCst);
    let base = BASE.lock().unwrap().map(|b| b.0).unwrap_or(Mode::Off);
    apply_shim(base);
    crate::logs::csv_flush_all();
}

/// VISIONALVR_TELEMETRY_SCALE (lab only): multiplies every schedule duration (0.08: a 17 minute run in 82 s).
fn time_scale() -> f64 {
    std::env::var("VISIONALVR_TELEMETRY_SCALE").ok().and_then(|v| v.parse::<f64>().ok()).filter(|v| *v > 0.0 && *v <= 1.0).unwrap_or(1.0)
}

fn arm() {
    let generation = GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
    let scale = time_scale();
    let (warmup, segment, prewarm_at) = (WARMUP_S * scale, SEGMENT_S * scale, PREWARM_AT_S * scale);
    write_line(crate::logs::qpc_now(), &format!("[tel] armed warmup_s={warmup:.1} segment_s={segment:.1} segments={} scale={scale}", SCHEDULE.len()));
    crate::logs::general("INFO", &format!("telemetry run armed by the client: warm-up {warmup:.0} s, then {} segments of {segment:.0} s (docs/TELEMETRY.md)", SCHEDULE.len()));
    set_mode(Mode::Off);
    SEG.store(0, Ordering::SeqCst);
    apply_shim(Mode::Off);
    let _ = std::thread::Builder::new().name("telemetry_schedule".into()).spawn(move || {
        let t0 = Instant::now();
        let (mut last_seg, mut prewarmed) = (-1i32, false);
        loop {
            std::thread::sleep(Duration::from_millis(100));
            if GENERATION.load(Ordering::SeqCst) != generation {
                return; // re-armed (a new client session) or disarmed
            }
            let e = t0.elapsed().as_secs_f64();
            if e >= prewarm_at && !prewarmed {
                prewarmed = true;
                PREWARM_LEFT.store(PREWARM_FRAMES, Ordering::SeqCst);
            }
            let (seg, mode) = if e < warmup {
                (0, Mode::Off)
            } else {
                let idx = ((e - warmup) / segment) as usize;
                if idx >= SCHEDULE.len() {
                    write_line(crate::logs::qpc_now(), &format!("[tel] end t={:.6}", crate::logs::qpc_now()));
                    sim_end();
                    crate::logs::general("INFO", "telemetry run finished: you can stop playing");
                    disarm();
                    return;
                }
                (idx as i32 + 1, SCHEDULE[idx])
            };
            if seg != last_seg {
                last_seg = seg;
                // shim flag first (it takes effect at the next frame submit), then the host-side override, then the logged change
                apply_shim(mode);
                set_mode(mode);
                SEG.store(seg, Ordering::SeqCst);
                write_line(crate::logs::qpc_now(), &format!("[tel] cond seg={seg} mode={} t={:.6}", mode.name(), crate::logs::qpc_now()));
                crate::logs::general("INFO", &format!("telemetry segment {seg}/{} re-stamp mode {}", SCHEDULE.len(), mode.name()));
            }
        }
    });
}

/// One row per frame the sender put on the wire while a run is armed (the join key with the client's rows is `final_ts_ns`).
/// Columns: t_send (QPC s), seg, mode, frame, orig_ts_ns (sample the game rendered with), final_ts_ns (what the client keys on),
/// restamped, warp_mdeg, queue_wait_ms (arrival at the host -> encode start), encode_ms, hold_ms, bytes, idr, deferred_total, dropped_total.
/// The pose difference between orig and final stamp comes from head_samples.csv (the host's received samples) in the report.
pub fn host_frame(row: &str) {
    if SEG.load(Ordering::Relaxed) < 0 {
        return;
    }
    if SIM_ON.load(Ordering::Relaxed) {
        let f: Vec<&str> = row.split(',').collect();
        if let (Some(t), Some(seg), Some(ts)) = (f.get(0).and_then(|v| v.parse::<f64>().ok()), f.get(1).and_then(|v| v.parse::<i32>().ok()), f.get(5).and_then(|v| v.parse::<u64>().ok())) {
            sim_frame(t, seg, ts);
        }
    }
    crate::logs::csv("telemetry_host.csv",
        "t_send,seg,mode,frame,orig_ts_ns,final_ts_ns,restamped,warp_mdeg,queue_wait_ms,encode_ms,hold_ms,bytes,idr,deferred_total,dropped_total", row);
}


// ---- lab hook: the simrig has no client, so the host plays its part (VISIONALVR_TELEMETRY_SIM=1) --------------------------------------
// After a few seconds it sends `[pt] start` through the same entry as a real client line, and for every frame the sender puts on the wire
// it writes client rows in the real format (stamp = the host's final stamp, repeat = same stamp as the previous row, synthetic residuals),
// plus `cond`, 1 Hz `sum` and `end` lines, so the whole chain (capture, schedule, live mode switches, CSV, report) runs on real frames.

static SIM_ON: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
static SIM: Mutex<SimState> = Mutex::new(SimState { rows: Vec::new(), last_stamp: 0, seg: -1, n: 100, last_sum: 0.0 });

struct SimState {
    rows: Vec<String>,
    last_stamp: u64,
    seg: i32,
    n: u64,
    last_sum: f64,
}

pub fn sim_start() {
    if std::env::var("VISIONALVR_TELEMETRY_SIM").map(|v| v == "1").unwrap_or(false) {
        SIM_ON.store(true, Ordering::SeqCst);
        let _ = std::thread::Builder::new().name("telemetry_sim_client".into()).spawn(|| {
            std::thread::sleep(Duration::from_secs(8));
            client_line(crate::logs::qpc_now(), "[pt] start n=1 ver=1 build=sim renderer=sim telemetry=1");
        });
    }
}

const SIM_COLS: &str = "present_s,timestamp_ns,age_ms,repeated,had_send,walkback_ms,anchor_minus_used_ms,fallback,extrap_deg,extrap_mm,comp_deg,comp_mm,dcomp_deg,dcomp_mm,head_deg_s,gpu_ms,decode_ms,queue_ms";

fn sim_frame(t_send: f64, seg: i32, final_ts: u64) {
    let mut st = SIM.lock().unwrap();
    let repeated = (final_ts == st.last_stamp) as u8;
    st.last_stamp = final_ts;
    // residuals made up from the stamp so the numbers are deterministic but not constant
    let k = (final_ts / 1000 % 997) as f64 / 997.0;
    st.rows.push(format!("{t_send:.6},{final_ts},{:.3},{repeated},1,0.0,0.1,0,{:.3},{:.3},{:.3},{:.3},{:.4},{:.3},{:.2},3.0,6.0,2.0",
        92.0 + k * 4.0, k * 0.3, k * 2.0, 0.2 + k * 0.5, 0.5 + k, k * 0.01, k * 0.4, 10.0 + k * 40.0));
    if st.seg != seg {
        let n = st.n + 1;
        st.n = n;
        st.seg = seg;
        let line = format!("[pt] cond n={n} seg={seg} focal=0 pick=oldest t={t_send:.3}");
        drop(st);
        client_line(crate::logs::qpc_now(), &line);
        st = SIM.lock().unwrap();
    }
    if st.rows.len() >= 12 {
        let n = st.n + 1;
        st.n = n;
        let line = format!("[pt] rows n={n} seg={seg} cols={SIM_COLS} | {}", st.rows.join(";"));
        st.rows.clear();
        let sum = if t_send - st.last_sum >= 1.0 {
            st.last_sum = t_send;
            st.n += 1;
            Some(format!("[pt] sum n={} seg={seg} frames=90 repeat=3 nosend=0 walkback=0 fallback=0 age_p50=93.0 age_p95=95.0 ctrlL_hz=90.0 ctrlR_hz=90.0 thermal=0 gpu_ms_p95=3.5 decode_ms_p95=7.0", st.n))
        } else {
            None
        };
        drop(st);
        client_line(crate::logs::qpc_now(), &line);
        if let Some(l) = sum {
            client_line(crate::logs::qpc_now(), &l);
        }
    }
}

/// Lab only: the schedule's end also ends the simulated client.
fn sim_end() {
    if SIM_ON.load(Ordering::Relaxed) {
        let n = {
            let mut st = SIM.lock().unwrap();
            st.n += 1;
            st.n
        };
        // `client_line("end")` would disarm; the scheduler already did when it got here
        write_line(crate::logs::qpc_now(), &format!("[pt] end n={n}"));
    }
}


// ---- manual start (no telemetry client): the same schedule from the host's own clock --------------------------------------------------
// For a session with the official client (host-only data: telemetry_host.csv + the host's client_frames.csv / head samples). Either create
// the flag file `<config dir>\telemetry_start.flag` while the headset streams (the host deletes it and starts the clock; `telemetry_stop.flag`
// stops a run), or start the host with VISIONALVR_TELEMETRY_AUTOSTART=1 (the clock starts 10 s after the headset connected). The host logs
// `[tel] start manual=1`. Default behaviour is unchanged: without the file or the variable nothing watches anything but the file's existence.

pub fn start_manual() {
    open_client_log();
    MANUAL.store(true, Ordering::SeqCst);
    write_line(crate::logs::qpc_now(), "[tel] start manual=1");
    arm();
}

/// Watches for the flag files (every 500 ms) and the autostart variable. `connected` tells whether the headset is connected.
pub fn watch(config_dir: std::path::PathBuf, connected: Box<dyn Fn() -> bool + Send>) {
    let _ = std::thread::Builder::new().name("telemetry_watch".into()).spawn(move || {
        let start_flag = config_dir.join("telemetry_start.flag");
        let stop_flag = config_dir.join("telemetry_stop.flag");
        let autostart = std::env::var("VISIONALVR_TELEMETRY_AUTOSTART").map(|v| v == "1").unwrap_or(false);
        let (mut connected_since, mut auto_done, mut waiting_logged) = (None::<Instant>, false, false);
        loop {
            std::thread::sleep(Duration::from_millis(500));
            let up = connected();
            if up {
                connected_since.get_or_insert_with(Instant::now);
            } else {
                connected_since = None;
            }
            if stop_flag.exists() {
                let _ = std::fs::remove_file(&stop_flag);
                if SEG.load(Ordering::Relaxed) >= 0 {
                    write_line(crate::logs::qpc_now(), "[tel] stop manual=1");
                    crate::logs::general("INFO", "telemetry run stopped by telemetry_stop.flag");
                    disarm();
                }
            }
            if start_flag.exists() {
                if up {
                    let _ = std::fs::remove_file(&start_flag);
                    crate::logs::general("INFO", "telemetry run started by telemetry_start.flag (manual, host clock)");
                    start_manual();
                    waiting_logged = false;
                } else if !waiting_logged {
                    waiting_logged = true;
                    crate::logs::general("INFO", "telemetry_start.flag found: waiting for the headset to connect");
                }
            }
            if autostart && !auto_done {
                if let Some(t) = connected_since {
                    if t.elapsed() >= Duration::from_secs(10) {
                        auto_done = true;
                        crate::logs::general("INFO", "telemetry run started by VISIONALVR_TELEMETRY_AUTOSTART (manual, host clock)");
                        start_manual();
                    }
                }
            }
        }
    });
}
