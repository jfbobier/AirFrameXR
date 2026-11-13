//! AirFrameXR logging (design and status: docs/ARCHITECTURE.md, "Logging"). The files are owned by the writer thread of `logsink.rs`;
//! this module is the host's API to it and the host-side aggregation.
//!
//! * `logs/AirFrameXR.log`: one line per fact that matters later (start/stop, headset connect, negotiated stream, game start/end,
//!   warnings and errors), ALVR's own warnings, errors and info lines as `[alvr]`. Small. Verbosity: `log_level` (info by default).
//! * `logs/games/<exe>/<yyyymmdd-hhmmss>.log`: one file per game run, its timeline lines (frame generation perf, memory, and at verbose
//!   the phase lock).
//! * Debug session (debug level only): `logs/debug/<yyyymmdd-hhmmss>/` per host run: `host_events.jsonl`, `alvr.log` (the raw ALVR
//!   stream: events, `[STATS]`, `[CAPS]`), `gpu.csv` (NVML, 1 Hz), the CSVs and one `shim_<exe>_<pid>.log` per game.
//!
//! The host installs the logger behind server_core (ALVR): its client statistics events (`StatisticsSummary` every second,
//! `GraphStatistics` per frame: total / encode / network / decode latency, packets lost, client fps) are captured for the status
//! lines and the network benchmark.
use alvr_common::{log, parking_lot::Mutex};
use alvr_events::EventType;
use serde_json::{json, Value};
use std::{
    fs,
    io::Write,
    path::{Path, PathBuf},
    sync::OnceLock,
};

struct State {
    logs_dir: Option<PathBuf>,
    debug_dir: Option<PathBuf>,
    events: Option<fs::File>,
    alvr: Option<fs::File>,
    alvr_default: Option<PathBuf>,
}

static STATE: OnceLock<Mutex<State>> = OnceLock::new();

fn state() -> &'static Mutex<State> {
    STATE.get_or_init(|| Mutex::new(State { logs_dir: None, debug_dir: None, events: None, alvr: None, alvr_default: None }))
}

#[cfg(windows)]
#[repr(C)]
#[derive(Default)]
struct SystemTime {
    year: u16,
    month: u16,
    dow: u16,
    day: u16,
    hour: u16,
    minute: u16,
    second: u16,
    ms: u16,
}
#[cfg(windows)]
extern "system" {
    fn GetLocalTime(t: *mut SystemTime);
}

/// Local time "yyyy-mm-dd hh:mm:ss.mmm" (date = false: "hh:mm:ss.mmm"; compact = true: "yyyymmdd-hhmmss").
pub fn local_time(date: bool, compact: bool) -> String {
    #[cfg(windows)]
    {
        let mut t = SystemTime::default();
        unsafe { GetLocalTime(&mut t) };
        if compact {
            format!("{:04}{:02}{:02}-{:02}{:02}{:02}", t.year, t.month, t.day, t.hour, t.minute, t.second)
        } else if date {
            format!("{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03}", t.year, t.month, t.day, t.hour, t.minute, t.second, t.ms)
        } else {
            format!("{:02}:{:02}:{:02}.{:03}", t.hour, t.minute, t.second, t.ms)
        }
    }
    #[cfg(not(windows))]
    {
        let s = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
        let _ = (date, compact);
        format!("{s}")
    }
}

fn open_append(path: &Path) -> Option<fs::File> {
    fs::OpenOptions::new().create(true).append(true).open(path).ok()
}

/// `logs_dir`: the install's `logs/` (None in harness runs: no general log). `alvr_default`: where server_core's log goes when
/// debug is off (the harness reads `<config>/session_log.txt`). `level`: the verbosity of `AirFrameXR.log` (debug on = Debug).
pub fn init(logs_dir: Option<&Path>, alvr_default: Option<&Path>, debug: bool, level: Level) {
    BASE_LEVEL.store(level as u8, std::sync::atomic::Ordering::Relaxed);
    {
        let mut s = state().lock();
        if let Some(d) = logs_dir {
            fs::create_dir_all(d).ok();
            s.logs_dir = Some(d.to_path_buf());
            // one writer thread owns AirFrameXR.log and the per-game logs (logsink.rs); callers only queue lines
            logsink::install(logsink::Sink::start(logsink::Config::new(d), if debug { Level::Debug } else { level }));
        }
        s.alvr_default = alvr_default.map(Path::to_path_buf);
        // with an install the raw ALVR stream exists only in the debug session folder; without one (harness runs) it is the session log file
        s.alvr = if logs_dir.is_none() { alvr_default.and_then(|p| fs::File::create(p).ok()) } else { None };
    }
    if debug {
        set_debug(true);
    }
    log::set_boxed_logger(Box::new(AlvrLogger)).ok();
    log::set_max_level(log::LevelFilter::Info);
    alvr_common::set_panic_hook();
    // the panic hook above logs through `log`: wait for the writer before the process dies
    let prev = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |i| {
        prev(i);
        flush();
    }));
}

pub use crate::logsink::Level;
use crate::logsink::{self, Dest};

/// The verbosity chosen at start (airframexr.json `log_level`, `--log-level`); debug on raises it to Debug, off returns to this.
static BASE_LEVEL: std::sync::atomic::AtomicU8 = std::sync::atomic::AtomicU8::new(Level::Info as u8);

fn base_level() -> Level {
    match BASE_LEVEL.load(std::sync::atomic::Ordering::Relaxed) {
        0 => Level::Error,
        1 => Level::Warn,
        2 => Level::Info,
        3 => Level::Verbose,
        _ => Level::Debug,
    }
}

/// Sets the verbosity of `AirFrameXR.log` live (stdin `log_level <error|warn|info|verbose|debug>`).
pub fn set_level(l: Level) {
    BASE_LEVEL.store(l as u8, std::sync::atomic::Ordering::Relaxed);
    if let Some(s) = logsink::global() {
        s.set_level(if debug_dir().is_some() { Level::Debug } else { l });
    }
}

pub fn level() -> Level {
    logsink::global().map(|s| s.level()).unwrap_or(Level::Info)
}

/// Is a line of this level written? (one atomic read: test before formatting a detail line)
pub fn enabled(l: Level) -> bool {
    logsink::global().map_or(false, |s| s.enabled(l))
}

/// Waits until the queued lines are on disk (exit, panic).
pub fn flush() {
    if let Some(s) = logsink::global() {
        s.flush();
    }
}

/// A line of `component` in `AirFrameXR.log` (`run`: in the running game's log instead, when one is open).
pub fn log_line(level: Level, component: &'static str, run: bool, msg: String) {
    if let Some(s) = logsink::global() {
        s.log(level, component, msg, if run { Dest::Run } else { Dest::Main });
    }
}

/// A line in the main log and, while a game runs, in its run log too.
pub fn log_line_both(level: Level, component: &'static str, msg: String) {
    if let Some(s) = logsink::global() {
        s.log(level, component, msg, Dest::Both);
    }
}

/// One line in the general log, from the host: "time LEVEL [host] message". `level` is "INFO", "WARN" or "ERROR".
pub fn general(level: &str, msg: &str) {
    let l = match level {
        "ERROR" => Level::Error,
        "WARN" => Level::Warn,
        _ => Level::Info,
    };
    log_line(l, "host", false, msg.to_string());
}

/// A line the shim logged inside a game (read from its ring buffer by main.rs): kept with the time the shim stamped it.
pub fn shim_line(level: u32, comp: &str, stamp: &str, text: &str) {
    if let Some(s) = logsink::global() {
        let l = match level {
            0 => Level::Error,
            1 => Level::Warn,
            2 => Level::Info,
            3 => Level::Verbose,
            _ => Level::Debug,
        };
        let at = logsink::Stamp::parse(stamp).unwrap_or_else(logsink::Stamp::now);
        s.log_at(at, l, comp.to_string(), text, Dest::Both);
    }
}

/// A timeline line of the running game (frame generation perf, memory, GPU): in the game's log only, nothing when no game runs.
/// `level` Verbose for the detail that is only wanted when looking for something (phase lock, tracking samples).
pub fn run_line(level: Level, component: &'static str, msg: String) {
    if let Some(s) = logsink::global() {
        s.log(level, component, msg, Dest::RunOnly);
    }
}

/// The run log of the last game that started, relative to the logs folder (`games/<exe>/<yyyymmdd-hhmmss>.log`): what a run record (games.json) points at.
static LAST_RUN_LOG: std::sync::Mutex<Option<String>> = std::sync::Mutex::new(None);

pub fn last_run_log() -> Option<String> {
    LAST_RUN_LOG.lock().ok().and_then(|g| g.clone())
}

pub fn forget_run_log() {
    if let Ok(mut g) = LAST_RUN_LOG.lock() {
        *g = None;
    }
}

/// A game started: its detail lines go to `logs/games/<exe>/<time>.log`. Returns that file.
pub fn game_start(exe: &str) -> Option<PathBuf> {
    let p = logsink::global()?.game_start(exe);
    if let (Some(dir), Some(file)) = (p.parent().and_then(|d| d.file_name()), p.file_name()) {
        if let Ok(mut g) = LAST_RUN_LOG.lock() {
            *g = Some(format!("games/{}/{}", dir.to_string_lossy(), file.to_string_lossy()));
        }
    }
    Some(p)
}

pub fn game_end(summary: &str) {
    if let Some(s) = logsink::global() {
        s.game_end(summary);
    }
}

/// Turns the debug session on (creates `logs/debug/<time>/` the first time) or off. Returns the session folder when on.
pub fn set_debug(on: bool) -> Option<PathBuf> {
    if let Some(g) = logsink::global() {
        g.set_level(if on { Level::Debug } else { base_level() });
    }
    let mut s = state().lock();
    if !on {
        s.events = None;
        s.alvr = if s.logs_dir.is_none() { s.alvr_default.as_ref().and_then(|p| open_append(p)) } else { None };
        return None;
    }
    if s.debug_dir.is_none() {
        let base = s.logs_dir.clone().or_else(|| s.alvr_default.as_ref().and_then(|p| p.parent().map(|d| d.join("logs"))))?;
        let dir = base.join("debug").join(local_time(true, true));
        fs::create_dir_all(&dir).ok()?;
        s.debug_dir = Some(dir);
    }
    let dir = s.debug_dir.clone()?;
    s.events = open_append(&dir.join("host_events.jsonl"));
    s.alvr = open_append(&dir.join("alvr.log"));
    Some(dir)
}

pub fn debug_dir() -> Option<PathBuf> {
    let s = state().lock();
    s.events.as_ref().and(s.debug_dir.clone())
}

/// The shim's clock (QPC seconds, what the shim and VDXR log) for host-side timestamps: set once from main with a paired
/// (Instant, qpc) reading, then derived from Instant.
static CLOCK: std::sync::OnceLock<(std::time::Instant, f64)> = std::sync::OnceLock::new();
pub fn set_clock(instant: std::time::Instant, qpc_s: f64) {
    let _ = CLOCK.set((instant, qpc_s));
}
pub fn qpc_now() -> f64 {
    match CLOCK.get() {
        Some((i, q)) => q + i.elapsed().as_secs_f64(),
        None => 0.0,
    }
}
pub fn qpc_at(instant: std::time::Instant) -> f64 {
    match CLOCK.get() {
        Some((i, q)) => q + instant.saturating_duration_since(*i).as_secs_f64() - i.saturating_duration_since(instant).as_secs_f64(),
        None => 0.0,
    }
}

/// Per-frame CSV files in the debug session folder (debug mode only): `name` is created on first use with `header`.
static CSVS: std::sync::Mutex<Vec<(String, std::io::BufWriter<fs::File>, u32)>> = std::sync::Mutex::new(Vec::new());
pub fn csv(name: &str, header: &str, row: &str) {
    use std::io::Write;
    let Some(dir) = debug_dir() else { return };
    let Ok(mut files) = CSVS.lock() else { return };
    if !files.iter().any(|(n, _, _)| n == name) {
        let Ok(f) = fs::File::create(dir.join(name)) else { return };
        let mut w = std::io::BufWriter::new(f);
        let _ = writeln!(w, "{header}");
        files.push((name.to_string(), w, 0));
    }
    if let Some((_, w, rows)) = files.iter_mut().find(|(n, _, _)| n == name) {
        let _ = writeln!(w, "{row}");
        *rows += 1;
        if *rows % 90 == 0 || name == "sched.csv" {
            let _ = w.flush(); // a crash loses at most a second
        }
    }
}

/// A host event (already a JSON line) into the debug session's `host_events.jsonl`.
pub fn event_line(line: &str) {
    let mut s = state().lock();
    if let Some(f) = s.events.as_mut() {
        writeln!(f, "{line}").ok();
    }
}

/// A line in the debug session folder's file `name` (e.g. gpu.csv), if debug is on.
pub fn debug_file_line(name: &str, line: &str) {
    let dir = debug_dir();
    if let Some(d) = dir {
        if let Some(mut f) = open_append(&d.join(name)) {
            writeln!(f, "{line}").ok();
        }
    }
}

// ---- server_core's logger ---------------------------------------------------------------------------------------------------

struct AlvrLogger;

impl log::Log for AlvrLogger {
    fn enabled(&self, meta: &log::Metadata) -> bool {
        meta.level() <= log::Level::Info && !meta.target().starts_with("mdns_sd")
    }

    fn log(&self, record: &log::Record) {
        if !self.enabled(record.metadata()) {
            return;
        }
        let msg = format!("{}", record.args());
        // The headset's raw per-frame report (patched into server_core's report_statistics: tools/patches/client-stats-log.patch):
        // one CSV row per frame the headset displayed, keyed by the frame's timestamp so it joins with host_frames.csv.
        // What the headset advertised at the handshake (tools/patches/client-caps-log.patch): the configure wizard's `client_capabilities` event.
        if let Some(inner) = msg.strip_prefix("{\"va_client_caps\":").and_then(|s| s.strip_suffix('}')) {
            if let (Ok(v), Some(o)) = (serde_json::from_str::<Value>(inner), crate::phase::origin()) {
                write_alvr("CAPS", inner);
                log_line(Level::Verbose, "alvr", false, format!("client capabilities {inner}"));
                *CLIENT_REFRESH_RATES.lock().unwrap() = v["supported_refresh_rates"].as_array().map(|a| a.iter().filter_map(|x| x.as_f64()).collect()).unwrap_or_default();
                let rates = CLIENT_REFRESH_RATES.lock().unwrap().iter().map(|r| format!("{r:.0}")).collect::<Vec<_>>().join(" / ");
                general("INFO", &format!("headset refresh rates: {}", if rates.is_empty() { "none declared".to_string() } else { rates + " Hz" }));
                crate::event(o, "client_capabilities", v);
            }
            return;
        }
        if let Some(inner) = msg.strip_prefix("{\"va_client_stats\":").and_then(|s| s.strip_suffix('}')) {
            if let Ok(v) = serde_json::from_str::<Value>(inner) {
                let ms = |k: &str| v[k].as_f64().unwrap_or(0.0) / 1000.0;
                crate::phase::observe(ms("decoder_queue_us"));   // the phase lock's only input: how long the decoded frame waited for the headset's render tick
                csv("client_frames.csv",
                    "t_recv,ts_ns,frame_interval_ms,decode_ms,decoder_queue_ms,client_comp_ms,vsync_queue_ms,total_ms",
                    &format!("{:.6},{},{:.3},{:.3},{:.3},{:.3},{:.3},{:.3}", qpc_now(), v["ts"].as_u64().unwrap_or(0), ms("frame_interval_us"),
                        ms("decode_us"), ms("decoder_queue_us"), ms("rendering_us"), ms("vsync_queue_us"), ms("total_us")));
            }
            return;
        }
        if let Some(t) = client_stats::parse_ticks(&msg) {
            client_stats::ticks(&t);   // the headset's own display-tick counts (not an error: the client sends it at error level so it is forwarded)
            return;
        }
        if msg.starts_with('{') && msg.ends_with('}') {
            match serde_json::from_str::<EventType>(&msg) {
                Ok(EventType::GraphStatistics(g)) => {
                    client_stats::graph(&g); // per frame: aggregated for the windows
                    // and one CSV row per frame the headset reported (debug mode): the client side of the timeline
                    csv("client_graph.csv",
                        "t_recv,total_ms,game_ms,server_comp_ms,encode_ms,network_ms,decode_ms,decoder_queue_ms,client_comp_ms,vsync_queue_ms,client_fps,server_fps,bitrate_mbps",
                        &format!("{:.6},{:.2},{:.2},{:.2},{:.2},{:.2},{:.2},{:.2},{:.2},{:.3},{:.1},{:.1},{:.1}", qpc_now(),
                            g.total_pipeline_latency_s * 1000.0, g.game_time_s * 1000.0, g.server_compositor_s * 1000.0, g.encoder_s * 1000.0,
                            g.network_s * 1000.0, g.decoder_s * 1000.0, g.decoder_queue_s * 1000.0, g.client_compositor_s * 1000.0,
                            g.vsync_queue_s as f64 * 1000.0, g.client_fps, g.server_fps, g.bitrate_bps / 1e6));
                    return;
                }
                Ok(EventType::StatisticsSummary(sum)) => {
                    client_stats::summary(&sum);
                    write_alvr("STATS", &serde_json::to_string(&sum).unwrap_or_default());
                    return;
                }
                Ok(EventType::Tracking(_)) | Ok(EventType::Buttons(_)) => return, // only when ALVR's raw-event logging is on: too much
                Ok(e) => {
                    write_alvr("EVENT", &serde_json::to_string(&e).unwrap_or_default());
                    return;
                }
                Err(_) => {}
            }
        }
        // server_core logs this for every input sample of a button it has no mapping for (~400 lines/s from the AVP client):
        // keep the first line per button name
        if let Some(name) = msg.strip_prefix("Received button not mapped: ") {
            static SEEN: Mutex<Vec<String>> = Mutex::new(Vec::new());
            let mut seen = SEEN.lock();
            if seen.iter().any(|n| n == name) {
                return;
            }
            seen.push(name.to_string());
        }
        // server_core's own text lines: the unified log (the raw stream stays in the debug folder's alvr.log)
        let (level, lvl) = match record.level() {
            log::Level::Error => ("ERROR", Level::Error),
            log::Level::Warn => ("WARN", Level::Warn),
            log::Level::Info => ("INFO", Level::Info),
            _ => ("DEBUG", Level::Debug),
        };
        write_alvr(level, &msg);
        log_line(lvl, "alvr", false, msg);
    }

    fn flush(&self) {}
}

fn write_alvr(kind: &str, msg: &str) {
    let mut s = state().lock();
    if let Some(f) = s.alvr.as_mut() {
        writeln!(f, "{} [{kind}] {msg}", local_time(false, false)).ok();
    }
}

/// ALVR's client statistics, aggregated per window (status lines every 10 s, encoder_stats every 2 s, benchmark steps).
/// The refresh rates the headset declared at the handshake (ALVR's client capabilities): the display rates it can actually run.
static CLIENT_REFRESH_RATES: std::sync::Mutex<Vec<f64>> = std::sync::Mutex::new(Vec::new());

/// the headset's declared refresh rates (Hz), empty until the handshake has been seen
pub fn client_refresh_rates() -> Vec<f64> {
    CLIENT_REFRESH_RATES.lock().unwrap().clone()
}

pub mod client_stats {
    use super::*;

    #[derive(Default, Clone)]
    pub struct Agg {
        n: u32,
        total: f64,
        game: f64,
        compositor: f64,
        encoder: f64,
        network: f64,
        decoder: f64,
        decoder_queue: f64,
        client_compositor: f64,
        vsync_queue: f64,
        client_frame_s: f64, // sum of 1 / client_fps over the samples with a sane value (client_n)
        client_n: u32,
        vsync_n: u32,        // samples with a sane vsync_queue (the client's unsigned subtraction can wrap to ~3e10 ms)
        vsync_late: u32,     // samples whose vsync_queue wrapped: the client finished its frame after the vsync it aimed at
        since: Option<std::time::Instant>, // first sample of the window
        server_fps: f64,
        throughputs: Vec<f64>, // bit/s per sample: the window reports their median (see window_json: one frame presented right after another gives an absurd ratio)
        bitrates: Vec<f64>,
        lat_n: u32,          // samples whose latency fields are sane (the others are outliers, see Sample::is_outlier)
        outliers: u32,
        totals: Vec<f64>,    // seconds, per sane sample: the window reports their median, a few garbage samples cannot move it
        encoders: Vec<f64>,
        networks: Vec<f64>,
        decoders: Vec<f64>,
    }

    /// One client frame's statistics (seconds, fps, bits per second).
    #[derive(Clone, Copy, Default, Debug)]
    pub struct Sample {
        pub total: f64, pub game: f64, pub compositor: f64, pub encoder: f64, pub network: f64, pub decoder: f64, pub decoder_queue: f64,
        pub client_compositor: f64, pub vsync_queue: f64, pub client_fps: f64, pub server_fps: f64, pub throughput_bps: f64, pub bitrate_bps: f64,
    }

    impl Sample {
        /// A duration that went negative and wrapped (a re-stamped or generated frame whose timestamps do not pair with the client's) reads as years:
        /// any component outside 0..2 s or a total above 5 s is not a latency, it is garbage (Lies of P showed 4 958 802 102 ms on the streamer).
        pub fn is_outlier(&self) -> bool {
            let bad = |x: f64| !(0.0..=2.0).contains(&x);
            bad(self.game) || bad(self.compositor) || bad(self.encoder) || bad(self.network) || bad(self.decoder) || bad(self.decoder_queue) || bad(self.client_compositor)
                || !(0.0..=5.0).contains(&self.total)
        }
    }

    pub fn median(v: &mut Vec<f64>) -> Option<f64> {
        if v.is_empty() { return None; }
        v.sort_by(|a, b| a.partial_cmp(b).unwrap());
        Some(v[v.len() / 2])
    }

    /// the first outliers of a run are kept raw in the debug folder to find where they come from
    static OUTLIER_LOGGED: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

    const ZERO: Agg = Agg {
        n: 0, total: 0.0, game: 0.0, compositor: 0.0, encoder: 0.0, network: 0.0, decoder: 0.0, decoder_queue: 0.0,
        client_compositor: 0.0, vsync_queue: 0.0, client_frame_s: 0.0, client_n: 0, vsync_n: 0, vsync_late: 0, since: None, server_fps: 0.0, throughputs: Vec::new(), bitrates: Vec::new(),
        lat_n: 0, outliers: 0, totals: Vec::new(), encoders: Vec::new(), networks: Vec::new(), decoders: Vec::new(),
    };
    /// Windows: 0 = encoder_stats (2 s), 1 = status (10 s), 2 = benchmark step.
    static WINDOWS: std::sync::Mutex<[Agg; 3]> = std::sync::Mutex::new([ZERO, ZERO, ZERO]);
    static PACKETS_LOST_TOTAL: std::sync::Mutex<usize> = std::sync::Mutex::new(0);
    /// the headset's own count: frames it displayed, and display periods it had to repeat (a gap between displayed frames of more than
    /// 1.5 periods = that many periods without a new frame); cumulative, for the automatic level's verdict on "generation off"
    static CLIENT_FRAMES: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    static CLIENT_MISSED: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    /// The gaps between two displayed frames, in display periods, binned (1.3.10, read-only): below 0.75, about 1, 1.25 to 1.75 (not a whole
    /// number of periods: a late frame, not a skipped refresh), about 2, 2.25 to 3.25, above 3.25. One line per minute in the general log, so
    /// "headset repeated" can be checked against what the gaps really are. See `gap_bin`.
    static GAP_BINS: [std::sync::atomic::AtomicU64; 6] = [
        std::sync::atomic::AtomicU64::new(0), std::sync::atomic::AtomicU64::new(0), std::sync::atomic::AtomicU64::new(0),
        std::sync::atomic::AtomicU64::new(0), std::sync::atomic::AtomicU64::new(0), std::sync::atomic::AtomicU64::new(0),
    ];
    static GAP_LOGGED: std::sync::Mutex<Option<std::time::Instant>> = std::sync::Mutex::new(None);

    /// Bin of a gap `ratio` display periods long (see GAP_BINS). The edges sit halfway between whole periods, so a gap of 2 periods with a
    /// millisecond of jitter is still "about 2", and 1.5 periods (a late frame, no skipped refresh) has a bin of its own.
    pub(super) fn gap_bin(ratio: f64) -> usize {
        if ratio < 0.75 { 0 } else if ratio < 1.25 { 1 } else if ratio < 1.75 { 2 } else if ratio < 2.25 { 3 } else if ratio < 3.25 { 4 } else { 5 }
    }

    /// Once a minute while frames arrive: the bins since the last line, with the repeat share they imply (whole periods skipped, counted from the
    /// "about 2" and later bins) and the share of irregular gaps (1.25 to 1.75 periods).
    fn log_gap_bins() {
        let mut last = GAP_LOGGED.lock().unwrap();
        let now = std::time::Instant::now();
        let due = match *last { Some(t) => now.duration_since(t).as_secs() >= 60, None => { *last = Some(now); false } };
        if !due { return; }
        *last = Some(now);
        let b: Vec<u64> = GAP_BINS.iter().map(|a| a.swap(0, std::sync::atomic::Ordering::Relaxed)).collect();
        let n: u64 = b.iter().sum();
        if n == 0 { return; }
        let skipped = b[3] + 2 * b[4] + 3 * b[5];   // periods without a new frame, from the whole-period bins (the >3.25 bin counted as 3 at least)
        super::log_line_both(Level::Info, "host", format!(
            "client frame gaps (display periods) over the last minute, {n} frames: <0.75 {}, ~1 {}, 1.25-1.75 {} ({:.1} %, late but not skipped), ~2 {}, 2.25-3.25 {}, >3.25 {}; periods skipped {:.1} % of periods shown",
            b[0], b[1], b[2], 100.0 * b[2] as f64 / n as f64, b[3], b[4], b[5], 100.0 * skipped as f64 / (n + skipped) as f64));
    }

    /// The headset's own display-tick counts (1.3.10): the visionOS and the Quest client each send a `va_ticks {...}` line through the client log every 10 s:
    /// what each of its display ticks showed (`fresh` = a new frame; `resubmit`, `reprojected`, `none` = the same frame again or nothing; `drops` = frames its
    /// decoder queue discarded). This is what "headset repeated" is made of when it is there; the gaps between reported frames (above) are the fallback
    /// for a client that does not send it (stock ALVR, an older build). One definition on both platforms: repeated = (resubmit + reprojected + none) / ticks.
    #[derive(Debug, Clone, PartialEq)]
    pub struct Ticks {
        pub client: String,
        pub secs: f64,
        pub ticks: u64,
        pub fresh: u64,
        pub resubmit: u64,
        pub reprojected: u64,
        pub none: u64,
        pub drops: u64,
    }

    impl Ticks {
        /// Share of the ticks that did not show a new frame (0 when no tick was counted).
        pub fn repeated(&self) -> f64 {
            if self.ticks == 0 { 0.0 } else { (self.ticks.saturating_sub(self.fresh)) as f64 / self.ticks as f64 }
        }
    }

    /// The `va_ticks` line inside a client log message ("Client name: [error] va_ticks {...}" or the bare line), None when the message has none or it is not valid.
    pub fn parse_ticks(msg: &str) -> Option<Ticks> {
        let at = msg.find("va_ticks {")?;
        let v: Value = serde_json::from_str(msg[at + "va_ticks ".len()..].trim()).ok()?;
        let n = |k: &str| v[k].as_u64().unwrap_or(0);
        let t = Ticks { client: v["client"].as_str().unwrap_or("?").to_string(), secs: v["t"].as_f64().unwrap_or(0.0), ticks: n("ticks"), fresh: n("fresh"),
            resubmit: n("resubmit"), reprojected: n("reprojected"), none: n("none"), drops: n("drops") };
        // the counts must add up (a client that miscounts must not move the figure)
        if t.ticks == 0 || t.fresh + t.resubmit + t.reprojected + t.none != t.ticks || t.secs <= 0.0 { return None; }
        Some(t)
    }

    static TICKS_TOTAL: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    static TICKS_FRESH: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    /// the gap-based counters when the first `va_ticks` window arrived: the figure continues from there, so the totals never jump (a run's baseline may predate it)
    static TICKS_ANCHOR: std::sync::Mutex<Option<(u64, u64)>> = std::sync::Mutex::new(None);

    /// A `va_ticks` window arrived: counted, and one line in the run's log.
    pub fn ticks(t: &Ticks) {
        use std::sync::atomic::Ordering::Relaxed;
        {
            let mut a = TICKS_ANCHOR.lock().unwrap();
            if a.is_none() {
                *a = Some((CLIENT_FRAMES.load(Relaxed), CLIENT_MISSED.load(Relaxed)));
            }
        }
        TICKS_TOTAL.fetch_add(t.ticks, Relaxed);
        TICKS_FRESH.fetch_add(t.fresh, Relaxed);
        super::run_line(super::Level::Info, "client", format!(
            "headset ticks ({}): {:.0} s, {} ticks, {:.1} % without a new frame (resubmit {}, reprojected {}, none {}), {} frames dropped by its queue",
            t.client, t.secs, t.ticks, 100.0 * t.repeated(), t.resubmit, t.reprojected, t.none, t.drops));
    }

    /// (frames the headset displayed new, display ticks that showed no new frame) so far: from the headset's own tick counts when it sends them, else from the gaps between
    /// the frames it reported.
    pub fn client_frames_missed() -> (u64, u64) {
        use std::sync::atomic::Ordering::Relaxed;
        match *TICKS_ANCHOR.lock().unwrap() {
            Some((f0, m0)) => {
                let (total, fresh) = (TICKS_TOTAL.load(Relaxed), TICKS_FRESH.load(Relaxed));
                (f0 + fresh, m0 + total.saturating_sub(fresh))
            }
            None => (CLIENT_FRAMES.load(Relaxed), CLIENT_MISSED.load(Relaxed)),
        }
    }

    /// Does the figure come from the headset's own counts?
    pub fn ticks_reported() -> bool {
        TICKS_ANCHOR.lock().unwrap().is_some()
    }
    static LAST_SUMMARY: std::sync::Mutex<Option<Value>> = std::sync::Mutex::new(None);

    impl Agg {
        fn add(&mut self, x: &Sample) {
            self.n += 1;
            self.since.get_or_insert_with(std::time::Instant::now);
            if x.is_outlier() {
                self.outliers += 1;
            } else {
                self.lat_n += 1;
                self.total += x.total;
                self.game += x.game;
                self.compositor += x.compositor;
                self.encoder += x.encoder;
                self.network += x.network;
                self.decoder += x.decoder;
                self.decoder_queue += x.decoder_queue;
                self.client_compositor += x.client_compositor;
                self.totals.push(x.total);
                self.encoders.push(x.encoder);
                self.networks.push(x.network);
                self.decoders.push(x.decoder);
            }
            if (0.0..1.0).contains(&x.vsync_queue) {
                self.vsync_queue += x.vsync_queue;
                self.vsync_n += 1;
            } else {
                self.vsync_late += 1;
            }
            // client_fps is per frame (1 / gap between two displayed frames): average the gaps, not the rates, which
            // overweighted short gaps; 0 and absurd values (1704 fps seen) are dropped
            if x.client_fps > 1.0 && x.client_fps < 500.0 {
                self.client_frame_s += 1.0 / x.client_fps;
                self.client_n += 1;
            }
            self.server_fps += x.server_fps;
            self.throughputs.push(x.throughput_bps);
            self.bitrates.push(x.bitrate_bps);
        }
    }

    pub fn graph(g: &alvr_events::GraphStatistics) {
        let x = Sample {
            total: g.total_pipeline_latency_s as f64, game: g.game_time_s as f64, compositor: g.server_compositor_s as f64, encoder: g.encoder_s as f64,
            network: g.network_s as f64, decoder: g.decoder_s as f64, decoder_queue: g.decoder_queue_s as f64, client_compositor: g.client_compositor_s as f64,
            vsync_queue: g.vsync_queue_s as f64, client_fps: g.client_fps as f64, server_fps: g.server_fps as f64, throughput_bps: g.throughput_bps as f64, bitrate_bps: g.bitrate_bps as f64,
        };
        if x.is_outlier() && OUTLIER_LOGGED.fetch_add(1, std::sync::atomic::Ordering::Relaxed) < 10 {
            debug_file_line("client_stats_outliers.jsonl", &format!("{{\"t\":{:.6},\"sample\":\"{:?}\"}}", qpc_now(), x));
        }
        for a in WINDOWS.lock().unwrap().iter_mut() {
            a.add(&x);
        }
        let g = &x;
        if g.client_fps > 1.0 && g.client_fps < 500.0 {
            let gap_s = 1.0 / g.client_fps;
            let period_s = crate::phase::period_ns() as f64 / 1e9;
            CLIENT_FRAMES.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            GAP_BINS[gap_bin(gap_s / period_s)].fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            log_gap_bins();
            if gap_s > 1.5 * period_s {
                CLIENT_MISSED.fetch_add(((gap_s / period_s).round() as u64).saturating_sub(1), std::sync::atomic::Ordering::Relaxed);
            }
        }
    }

    pub fn summary(s: &alvr_events::StatisticsSummary) {
        *PACKETS_LOST_TOTAL.lock().unwrap() = s.packets_lost_total;
        *LAST_SUMMARY.lock().unwrap() = serde_json::to_value(s).ok();
    }

    pub fn packets_lost_total() -> usize {
        *PACKETS_LOST_TOTAL.lock().unwrap()
    }

    pub fn last_summary() -> Option<Value> {
        LAST_SUMMARY.lock().unwrap().clone()
    }

    /// Averages of window `w` since its last take (milliseconds, fps, Mbps), then resets it. None if no client statistics arrived.
    pub fn take(w: usize) -> Option<Value> {
        let a = std::mem::take(&mut WINDOWS.lock().unwrap()[w]);
        window_json(a)
    }

    /// The window as JSON. Latency figures are medians of the sane samples (None = null when every sample was an outlier); `latency_outliers` counts the dropped ones.
    pub(super) fn window_json(mut a: Agg) -> Option<Value> {
        if a.n == 0 {
            return None;
        }
        let lat_n = a.lat_n.max(1) as f64;
        let lat = |x: f64| -> Value { if a.lat_n > 0 { json!((x / lat_n * 1000.0 * 100.0).round() / 100.0) } else { Value::Null } };
        let med = |v: &mut Vec<f64>| -> Value { median(v).map(|m| json!((m * 1000.0 * 100.0).round() / 100.0)).unwrap_or(Value::Null) };
        let (total_ms, encode_ms, network_ms, decode_ms) = (med(&mut a.totals), med(&mut a.encoders), med(&mut a.networks), med(&mut a.decoders));
        let n = a.n as f64;
        let r1 = |x: f64| (x * 10.0).round() / 10.0;
        // ALVR's per-frame rate is the frame's bits over the gap since the previous frame was presented, the gap floored at 1 us: two frames
        // presented back to back give a sample of 1e5 Mbit/s, and one such sample moved the mean of a 77 min Dead Space run to 6801 Mbps
        // (ALVR's own per-second rate said 220-290 for a 275 Mbps stream, 2026-11-09). The median is not moved by a handful of them.
        let mbps = |v: &mut Vec<f64>| median(v).map(|m| r1(m / 1e6)).unwrap_or(0.0);
        let client_fps = if a.client_n > 0 { r1(a.client_n as f64 / a.client_frame_s) } else { 0.0 };
        let vsync_queue_ms = if a.vsync_n > 0 { (a.vsync_queue / a.vsync_n as f64 * 1000.0 * 100.0).round() / 100.0 } else { 0.0 };
        // frames the headset reported as displayed, per second of window (frames it skipped never report)
        let secs = a.since.map(|t| t.elapsed().as_secs_f64()).unwrap_or(0.0);
        let displayed_fps = if secs > 0.5 { r1(n / secs) } else { client_fps };
        Some(json!({
            "samples": a.n,
            "latency_outliers": a.outliers,
            "total_latency_ms": total_ms, "game_ms": lat(a.game), "server_compositor_ms": lat(a.compositor),
            "encode_ms": encode_ms, "network_ms": network_ms, "decode_ms": decode_ms,
            "decoder_queue_ms": lat(a.decoder_queue), "client_compositor_ms": lat(a.client_compositor), "vsync_queue_ms": vsync_queue_ms, "vsync_late": a.vsync_late,
            "client_fps": client_fps, "displayed_fps": displayed_fps, "server_fps": (a.server_fps / n * 10.0).round() / 10.0,
            "throughput_mbps": mbps(&mut a.throughputs), "bitrate_mbps": mbps(&mut a.bitrates),
        }))
    }

    #[cfg(test)]
    mod tests {
        #[test]
        fn va_ticks_line_is_parsed_and_checked() {
            let line = "Client Vision Pro: [error] va_ticks {\"v\":1,\"client\":\"visionos\",\"t\":10.01,\"ticks\":1000,\"fresh\":960,\"resubmit\":10,\"reprojected\":20,\"none\":10,\"drops\":3}";
            let t = super::parse_ticks(line).unwrap();
            assert_eq!((t.client.as_str(), t.ticks, t.fresh, t.drops), ("visionos", 1000, 960, 3));
            assert!((t.repeated() - 0.04).abs() < 1e-9);
            // counts that do not add up, an empty window, or no line at all
            assert!(super::parse_ticks("va_ticks {\"t\":10,\"ticks\":10,\"fresh\":5,\"none\":1}").is_none());
            assert!(super::parse_ticks("va_ticks {\"t\":10,\"ticks\":0}").is_none());
            assert!(super::parse_ticks("Client x: [info] hello").is_none());
        }

        use super::*;

        #[test]
        fn gap_bins_sit_between_whole_periods() {
            // 100 Hz: a 20 ms gap with a millisecond of jitter is still "about 2" periods, a 15 ms one is a late frame, not a skipped refresh
            let p = 10.0;
            assert_eq!(gap_bin(9.9 / p), 1);
            assert_eq!(gap_bin(20.9 / p), 3);
            assert_eq!(gap_bin(19.0 / p), 3);
            assert_eq!(gap_bin(15.0 / p), 2);
            assert_eq!(gap_bin(17.4 / p), 2);
            assert_eq!(gap_bin(30.0 / p), 4);
            assert_eq!(gap_bin(80.0 / p), 5);
            assert_eq!(gap_bin(3.0 / p), 0);
        }

        fn clean(total_ms: f64) -> Sample {
            Sample { total: total_ms / 1000.0, game: 0.011, compositor: 0.002, encoder: 0.006, network: 0.009, decoder: 0.014, decoder_queue: 0.003, client_compositor: 0.002,
                     vsync_queue: 0.004, client_fps: 90.0, server_fps: 90.0, throughput_bps: 1.2e8, bitrate_bps: 1.2e8 }
        }
        fn window(samples: &[Sample]) -> Value {
            let mut a = ZERO;
            for s in samples { a.add(s); }
            window_json(a).unwrap()
        }

        #[test]
        fn clean_samples_are_unchanged() {
            let w = window(&[clean(50.0), clean(52.0), clean(54.0)]);
            assert_eq!(w["latency_outliers"], 0);
            assert_eq!(w["total_latency_ms"].as_f64().unwrap(), 52.0);
            assert_eq!(w["encode_ms"].as_f64().unwrap(), 6.0);
            assert_eq!(w["decode_ms"].as_f64().unwrap(), 14.0);
        }

        #[test]
        fn a_wrapped_duration_is_dropped_and_counted() {
            let mut bad = clean(50.0);
            bad.total = 4_958_802.102;   // seconds: the streamer showed 4 958 802 102 ms
            bad.encoder = 137.0;
            let w = window(&[clean(50.0), bad, clean(52.0), bad, clean(54.0)]);
            assert_eq!(w["latency_outliers"], 2);
            assert_eq!(w["samples"], 5);
            assert_eq!(w["total_latency_ms"].as_f64().unwrap(), 52.0);
            assert_eq!(w["encode_ms"].as_f64().unwrap(), 6.0);
            assert!(w["game_ms"].as_f64().unwrap() < 12.0);
        }

        #[test]
        fn a_negative_component_is_an_outlier() {
            let mut bad = clean(50.0);
            bad.network = -0.001;
            assert!(bad.is_outlier());
            let mut slow = clean(50.0);
            slow.total = 5.5;
            assert!(slow.is_outlier());
            assert!(!clean(200.0).is_outlier());
        }

        #[test]
        fn a_few_absurd_rates_do_not_move_the_bitrate() {
            // 2000 frames at 275 Mbit/s and 5 presented back to back (1 us gap: 1e11 bit/s)
            let mut v: Vec<Sample> = (0..2000).map(|_| { let mut s = clean(50.0); s.bitrate_bps = 2.75e8; s.throughput_bps = 2.7e8; s }).collect();
            for _ in 0..5 { let mut s = clean(50.0); s.bitrate_bps = 1.0e11; s.throughput_bps = 1.0e11; v.push(s); }
            let w = window(&v);
            assert_eq!(w["bitrate_mbps"].as_f64().unwrap(), 275.0);
            assert_eq!(w["throughput_mbps"].as_f64().unwrap(), 270.0);
        }

        #[test]
        fn a_window_of_outliers_reports_no_latency() {
            let mut bad = clean(50.0);
            bad.decoder = 3.0e9;
            let w = window(&[bad, bad]);
            assert_eq!(w["latency_outliers"], 2);
            assert!(w["total_latency_ms"].is_null() && w["game_ms"].is_null() && w["decode_ms"].is_null());
            assert!(w["displayed_fps"].as_f64().is_some());   // the frame counts still stand
        }
    }
}
