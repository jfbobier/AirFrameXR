//! VisionALVR logging.
//!
//! * General log `logs/VisionALVR.log`: appended by every component (host, shim inside each game, GUIs), one line per fact that
//!   matters later: start/stop, headset connect/disconnect, negotiated stream, game start/end, warnings and errors. Small.
//! * Debug session (only when debug is on): `logs/debug/<yyyymmdd-hhmmss>/` per host run with the verbose files: `host_events.jsonl`
//!   (every host event, `encoder_stats` every 2 s), `alvr.log` (server_core's own log), `gpu.csv` (NVML, 1 Hz), and one
//!   `shim_<exe>_<pid>.log` per game (the shim finds the folder through the IPC block).
//!
//! The host also installs the logger behind server_core (ALVR): its log lines go to `alvr.log`, its warnings/errors to the
//! general log, and its client statistics events (`StatisticsSummary` every second, `GraphStatistics` per frame: total / encode /
//! network / decode latency, packets lost, client fps) are captured for the status lines and the network benchmark.
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
    general: Option<fs::File>,
    debug_dir: Option<PathBuf>,
    events: Option<fs::File>,
    alvr: Option<fs::File>,
    alvr_default: Option<PathBuf>,
}

static STATE: OnceLock<Mutex<State>> = OnceLock::new();

fn state() -> &'static Mutex<State> {
    STATE.get_or_init(|| Mutex::new(State { logs_dir: None, general: None, debug_dir: None, events: None, alvr: None, alvr_default: None }))
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
/// debug is off (the harness reads `<config>/session_log.txt`).
pub fn init(logs_dir: Option<&Path>, alvr_default: Option<&Path>, debug: bool) {
    {
        let mut s = state().lock();
        if let Some(d) = logs_dir {
            fs::create_dir_all(d).ok();
            let g = d.join("VisionALVR.log");
            // keep the general log bounded: past 10 MB the old one is kept as .1
            if fs::metadata(&g).map(|m| m.len() > 10 << 20).unwrap_or(false) {
                fs::rename(&g, d.join("VisionALVR.log.1")).ok();
            }
            s.general = open_append(&g);
            s.logs_dir = Some(d.to_path_buf());
        }
        s.alvr_default = alvr_default.map(Path::to_path_buf);
        s.alvr = alvr_default.and_then(|p| fs::File::create(p).ok());
    }
    if debug {
        set_debug(true);
    }
    log::set_boxed_logger(Box::new(AlvrLogger)).ok();
    log::set_max_level(log::LevelFilter::Info);
    alvr_common::set_panic_hook();
}

/// One line in the general log: "time [host] LEVEL message".
pub fn general(level: &str, msg: &str) {
    let mut s = state().lock();
    if let Some(f) = s.general.as_mut() {
        writeln!(f, "{} [host] {level:<5} {msg}", local_time(true, false)).ok();
        f.flush().ok();
    }
}

/// Turns the debug session on (creates `logs/debug/<time>/` the first time) or off. Returns the session folder when on.
pub fn set_debug(on: bool) -> Option<PathBuf> {
    let mut s = state().lock();
    if !on {
        s.events = None;
        s.alvr = s.alvr_default.as_ref().and_then(|p| open_append(p));
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
                *CLIENT_REFRESH_RATES.lock().unwrap() = v["supported_refresh_rates"].as_array().map(|a| a.iter().filter_map(|x| x.as_f64()).collect()).unwrap_or_default();
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
        let level = match record.level() {
            log::Level::Error => "ERROR",
            log::Level::Warn => "WARN",
            log::Level::Info => "INFO",
            _ => "DEBUG",
        };
        write_alvr(level, &msg);
        if record.level() <= log::Level::Warn {
            general(level, &format!("alvr: {msg}"));
        }
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
        throughput_bps: f64,
        bitrate_bps: f64,
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
        client_compositor: 0.0, vsync_queue: 0.0, client_frame_s: 0.0, client_n: 0, vsync_n: 0, vsync_late: 0, since: None, server_fps: 0.0, throughput_bps: 0.0, bitrate_bps: 0.0,
        lat_n: 0, outliers: 0, totals: Vec::new(), encoders: Vec::new(), networks: Vec::new(), decoders: Vec::new(),
    };
    /// Windows: 0 = encoder_stats (2 s), 1 = status (10 s), 2 = benchmark step.
    static WINDOWS: std::sync::Mutex<[Agg; 3]> = std::sync::Mutex::new([ZERO, ZERO, ZERO]);
    static PACKETS_LOST_TOTAL: std::sync::Mutex<usize> = std::sync::Mutex::new(0);
    /// the headset's own count: frames it displayed, and display periods it had to repeat (a gap between displayed frames of more than
    /// 1.5 periods = that many periods without a new frame); cumulative, for the automatic level's verdict on "generation off"
    static CLIENT_FRAMES: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    static CLIENT_MISSED: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

    /// (frames the headset displayed, display periods it repeated) so far
    pub fn client_frames_missed() -> (u64, u64) {
        (CLIENT_FRAMES.load(std::sync::atomic::Ordering::Relaxed), CLIENT_MISSED.load(std::sync::atomic::Ordering::Relaxed))
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
            self.throughput_bps += x.throughput_bps;
            self.bitrate_bps += x.bitrate_bps;
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
            "throughput_mbps": (a.throughput_bps / n / 1e6 * 10.0).round() / 10.0, "bitrate_mbps": (a.bitrate_bps / n / 1e6 * 10.0).round() / 10.0,
        }))
    }

    #[cfg(test)]
    mod tests {
        use super::*;

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
