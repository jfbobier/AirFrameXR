//! The log writer: every line of `AirFrameXR.log` and of the per-game logs goes through one bounded queue to one writer thread.
//!
//! * Callers never touch the disk and never wait: `log` stamps the line, `try_send`s it and returns. A full queue drops the line and
//!   counts it; the writer reports the count (`N log lines dropped`) once the queue drains. A log that stalls a frame is worse than a gap.
//! * The writer thread owns the files: it formats, batches (flush every 200 ms, at once for warnings and errors, on `flush`), rolls
//!   `AirFrameXR.log` over at midnight (`AirFrameXR.<yyyy-mm-dd>.log`) and past a size cap, keeps the last N days, and routes the lines
//!   of the running game to `games/<exe>/<yyyymmdd-hhmmss>.log` (the last N runs per game are kept).
//! * Verbosity is one atomic read on the caller's side (`enabled`), so a line that is filtered out costs no allocation.
//!
//! std only (no ALVR types): it is tested on its own (`rustc --test`), see the tests at the end.
use std::{
    borrow::Cow,
    fs,
    io::{BufWriter, Read, Write},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU64, AtomicU8, Ordering},
        mpsc::{sync_channel, Receiver, RecvTimeoutError, SyncSender, TrySendError},
        Arc, OnceLock,
    },
    thread,
    time::Duration,
};

/// Severity and verbosity in one scale. A line is written when its level is at or below the sink's level:
/// `Info` (default) = events, warnings, errors and one summary line per minute; `Verbose` adds the periodic detail lines;
/// `Debug` is the debug session (the CSVs and the raw streams go there, see logs.rs).
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Debug)]
#[repr(u8)]
pub enum Level {
    Error = 0,
    Warn = 1,
    Info = 2,
    Verbose = 3,
    Debug = 4,
}

impl Level {
    pub fn name(self) -> &'static str {
        match self {
            Level::Error => "ERROR",
            Level::Warn => "WARN",
            Level::Info => "INFO",
            Level::Verbose => "VERB",
            Level::Debug => "DEBUG",
        }
    }
    pub fn parse(s: &str) -> Option<Level> {
        match s.trim().to_ascii_lowercase().as_str() {
            "error" => Some(Level::Error),
            "warn" | "warning" => Some(Level::Warn),
            "info" => Some(Level::Info),
            "verbose" | "verb" => Some(Level::Verbose),
            "debug" => Some(Level::Debug),
            _ => None,
        }
    }
    fn from_u8(v: u8) -> Level {
        match v {
            0 => Level::Error,
            1 => Level::Warn,
            2 => Level::Info,
            3 => Level::Verbose,
            _ => Level::Debug,
        }
    }
}

/// Where a line goes: the main log only, the running game's log (the main log when no game runs), or the running game's log
/// only (dropped when no game runs: the periodic timeline lines of a run).
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Dest {
    Main,
    Run,
    RunOnly,
    /// the main log and, while a game runs, its run log too (every WARN/ERROR line gets this)
    Both,
}

/// Local wall-clock time, taken by the caller when the line is logged.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct Stamp {
    pub y: u16,
    pub mo: u8,
    pub d: u8,
    pub h: u8,
    pub mi: u8,
    pub s: u8,
    pub ms: u16,
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

impl Stamp {
    #[cfg(windows)]
    pub fn now() -> Stamp {
        let mut t = SystemTime::default();
        unsafe { GetLocalTime(&mut t) };
        Stamp { y: t.year, mo: t.month as u8, d: t.day as u8, h: t.hour as u8, mi: t.minute as u8, s: t.second as u8, ms: t.ms }
    }
    /// Off Windows (tests): UTC from the system clock.
    #[cfg(not(windows))]
    pub fn now() -> Stamp {
        let d = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
        Stamp::from_unix(d.as_secs(), d.subsec_millis() as u16)
    }
    #[cfg(not(windows))]
    fn from_unix(secs: u64, ms: u16) -> Stamp {
        let days = (secs / 86400) as i64;
        let rem = secs % 86400;
        let z = days + 719468; // Hinnant's civil_from_days
        let era = z.div_euclid(146097);
        let doe = z.rem_euclid(146097);
        let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        let mp = (5 * doy + 2) / 153;
        let d = (doy - (153 * mp + 2) / 5 + 1) as u8;
        let mo = if mp < 10 { mp + 3 } else { mp - 9 } as u8;
        let y = (yoe + era * 400 + if mo <= 2 { 1 } else { 0 }) as u16;
        Stamp { y, mo, d, h: (rem / 3600) as u8, mi: (rem % 3600 / 60) as u8, s: (rem % 60) as u8, ms }
    }
    /// "yyyy-mm-dd hh:mm:ss.mmm" (what the shim's log ring carries), None when it is not that.
    pub fn parse(t: &str) -> Option<Stamp> {
        let b = t.as_bytes();
        if b.len() < 23 || b[4] != b'-' || b[7] != b'-' || b[10] != b' ' || b[13] != b':' || b[16] != b':' || b[19] != b'.' {
            return None;
        }
        let n = |a: usize, z: usize| t.get(a..z)?.parse::<u16>().ok();
        Some(Stamp { y: n(0, 4)?, mo: n(5, 7)? as u8, d: n(8, 10)? as u8, h: n(11, 13)? as u8, mi: n(14, 16)? as u8, s: n(17, 19)? as u8, ms: n(20, 23)? })
    }
    pub fn date(&self) -> (u16, u8, u8) {
        (self.y, self.mo, self.d)
    }
    /// "yyyy-mm-dd hh:mm:ss.mmm"
    pub fn full(&self) -> String {
        format!("{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03}", self.y, self.mo, self.d, self.h, self.mi, self.s, self.ms)
    }
    /// "yyyymmdd-hhmmss"
    pub fn compact(&self) -> String {
        format!("{:04}{:02}{:02}-{:02}{:02}{:02}", self.y, self.mo, self.d, self.h, self.mi, self.s)
    }
    fn day(&self) -> String {
        format!("{:04}-{:02}-{:02}", self.y, self.mo, self.d)
    }
}

enum Msg {
    Line { at: Stamp, level: Level, comp: Cow<'static, str>, text: String, dest: Dest },
    GameStart { at: Stamp, exe: String, path: PathBuf },
    GameEnd { at: Stamp, summary: String },
    Flush(SyncSender<()>),
}

#[derive(Clone, Debug)]
pub struct Config {
    pub dir: PathBuf,
    /// bounded queue: lines beyond it are dropped and counted
    pub queue: usize,
    /// days of rolled-over `AirFrameXR.<date>.log` kept
    pub keep_days: usize,
    /// the current file is rolled over when it passes this size
    pub max_bytes: u64,
    /// runs kept per game under `games/<exe>/`
    pub keep_runs: usize,
}

impl Config {
    pub fn new(dir: &Path) -> Config {
        Config { dir: dir.to_path_buf(), queue: 8192, keep_days: 7, max_bytes: 10 << 20, keep_runs: 20 }
    }
}

pub struct Sink {
    tx: SyncSender<Msg>,
    level: Arc<AtomicU8>,
    dropped: Arc<AtomicU64>,
    games_dir: PathBuf,
}

impl Sink {
    /// Starts the writer thread. The first `Sink` of the process is also the global one (`global()`).
    pub fn start(cfg: Config, level: Level) -> Sink {
        let (tx, rx) = sync_channel::<Msg>(cfg.queue.max(1));
        let level = Arc::new(AtomicU8::new(level as u8));
        let dropped = Arc::new(AtomicU64::new(0));
        let games_dir = cfg.dir.join("games");
        let d2 = dropped.clone();
        thread::Builder::new().name("log-writer".into()).spawn(move || Writer::new(cfg).run(rx, d2)).ok();
        Sink { tx, level, dropped, games_dir }
    }

    pub fn set_level(&self, l: Level) {
        self.level.store(l as u8, Ordering::Relaxed);
    }
    pub fn level(&self) -> Level {
        Level::from_u8(self.level.load(Ordering::Relaxed))
    }
    /// One atomic read: callers test this before formatting a message.
    pub fn enabled(&self, l: Level) -> bool {
        l as u8 <= self.level.load(Ordering::Relaxed)
    }
    pub fn dropped(&self) -> u64 {
        self.dropped.load(Ordering::Relaxed)
    }

    fn send(&self, m: Msg) {
        if let Err(TrySendError::Full(_)) = self.tx.try_send(m) {
            self.dropped.fetch_add(1, Ordering::Relaxed);
        }
    }

    pub fn log(&self, level: Level, comp: impl Into<Cow<'static, str>>, text: impl Into<String>, dest: Dest) {
        if self.enabled(level) {
            self.send(Msg::Line { at: Stamp::now(), level, comp: comp.into(), text: text.into(), dest });
        }
    }

    /// A line stamped elsewhere (the shim's, read from its ring buffer): `at` is when it was logged, not when it was written.
    pub fn log_at(&self, at: Stamp, level: Level, comp: impl Into<Cow<'static, str>>, text: impl Into<String>, dest: Dest) {
        if self.enabled(level) {
            self.send(Msg::Line { at, level, comp: comp.into(), text: text.into(), dest });
        }
    }

    /// A game starts: its lines (`Dest::Run`) go to `games/<exe>/<yyyymmdd-hhmmss>.log` until `game_end`. Returns that path (for `games.json`).
    pub fn game_start(&self, exe: &str) -> PathBuf {
        let at = Stamp::now();
        let path = self.games_dir.join(sanitize(exe)).join(format!("{}.log", at.compact()));
        self.send(Msg::GameStart { at, exe: exe.to_string(), path: path.clone() });
        path
    }
    /// The game ended; `summary` is the closing line of its log.
    pub fn game_end(&self, summary: &str) {
        self.send(Msg::GameEnd { at: Stamp::now(), summary: summary.to_string() });
    }

    /// Waits (at most 2 s) until everything queued so far is on disk: at exit and in the panic hook.
    pub fn flush(&self) {
        let (a, b) = sync_channel(1);
        if self.tx.send_timeout_compat(Msg::Flush(a)) {
            let _ = b.recv_timeout(Duration::from_secs(2));
        }
    }
}

// std's SyncSender has no send_timeout: a blocking send on a full queue could hang a panic hook, so try a few times.
trait SendTimeout {
    fn send_timeout_compat(&self, m: Msg) -> bool;
}
impl SendTimeout for SyncSender<Msg> {
    fn send_timeout_compat(&self, mut m: Msg) -> bool {
        for _ in 0..200 {
            match self.try_send(m) {
                Ok(()) => return true,
                Err(TrySendError::Full(x)) => {
                    m = x;
                    thread::sleep(Duration::from_millis(10));
                }
                Err(TrySendError::Disconnected(_)) => return false,
            }
        }
        false
    }
}

/// A file-name-safe version of an exe name ("Game.exe" -> "Game.exe"; separators and odd characters become '_').
pub fn sanitize(exe: &str) -> String {
    let s: String = exe.chars().map(|c| if c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '_' | ' ') { c } else { '_' }).collect();
    let s = s.trim().trim_matches('.').to_string();
    if s.is_empty() {
        "unknown".to_string()
    } else {
        s
    }
}

static GLOBAL: OnceLock<Sink> = OnceLock::new();
pub fn install(sink: Sink) -> &'static Sink {
    let _ = GLOBAL.set(sink);
    GLOBAL.get().unwrap()
}
pub fn global() -> Option<&'static Sink> {
    GLOBAL.get()
}

// ---- the writer thread ------------------------------------------------------------------------------------------------------

struct Writer {
    cfg: Config,
    main: Option<BufWriter<fs::File>>,
    main_day: String,
    main_bytes: u64,
    game: Option<BufWriter<fs::File>>,
    last_flush: std::time::Instant,
    reported_drops: u64,
}

impl Writer {
    fn new(cfg: Config) -> Writer {
        Writer { cfg, main: None, main_day: String::new(), main_bytes: 0, game: None, last_flush: std::time::Instant::now(), reported_drops: 0 }
    }

    fn run(mut self, rx: Receiver<Msg>, dropped: Arc<AtomicU64>) {
        fs::create_dir_all(&self.cfg.dir).ok();
        loop {
            match rx.recv_timeout(Duration::from_millis(200)) {
                Ok(m) => {
                    self.handle(m);
                    while let Ok(m) = rx.try_recv() {
                        self.handle(m);
                    }
                }
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => {
                    self.flush_all();
                    return;
                }
            }
            let d = dropped.load(Ordering::Relaxed);
            if d > self.reported_drops {
                let n = d - self.reported_drops;
                self.reported_drops = d;
                self.write_main(Stamp::now(), Level::Warn, "log", &format!("{n} log lines dropped (queue full)"));
            }
            if self.last_flush.elapsed() >= Duration::from_millis(200) {
                self.flush_all();
            }
        }
    }

    fn handle(&mut self, m: Msg) {
        match m {
            Msg::Line { at, level, comp, text, dest } => {
                let dest = if level <= Level::Warn && dest == Dest::Main { Dest::Both } else { dest };
                if dest == Dest::Both && self.game.is_some() {
                    let line = format!("{} [{}] {:<5} {}\n", at.full(), comp, level.name(), text);
                    if let Some(g) = self.game.as_mut() {
                        g.write_all(line.as_bytes()).ok();
                    }
                    self.write_main(at, level, &comp, &text);
                } else if dest == Dest::RunOnly && self.game.is_none() {
                    // no game: nothing to attach the line to
                } else if dest != Dest::Main && dest != Dest::Both && self.game.is_some() {
                    let line = format!("{} [{}] {:<5} {}\n", at.full(), comp, level.name(), text);
                    if let Some(g) = self.game.as_mut() {
                        g.write_all(line.as_bytes()).ok();
                    }
                } else {
                    self.write_main(at, level, &comp, &text);
                }
                if level <= Level::Warn {
                    self.flush_all();
                }
            }
            Msg::GameStart { at, exe, path } => {
                self.end_game(at, "log closed (next game started)");
                if let Some(dir) = path.parent() {
                    fs::create_dir_all(dir).ok();
                    self.game = fs::OpenOptions::new().create(true).append(true).open(&path).ok().map(BufWriter::new);
                    prune(dir, None, self.cfg.keep_runs);
                }
                if let Some(g) = self.game.as_mut() {
                    writeln!(g, "{} [log] {:<5} {exe}: run log started", at.full(), Level::Info.name()).ok();
                }
            }
            Msg::GameEnd { at, summary } => self.end_game(at, &summary),
            Msg::Flush(ack) => {
                self.flush_all();
                let _ = ack.send(());
            }
        }
    }

    fn end_game(&mut self, at: Stamp, summary: &str) {
        if let Some(mut g) = self.game.take() {
            writeln!(g, "{} [log] {:<5} {summary}", at.full(), Level::Info.name()).ok();
            g.flush().ok();
        }
    }

    fn flush_all(&mut self) {
        if let Some(f) = self.main.as_mut() {
            f.flush().ok();
        }
        if let Some(g) = self.game.as_mut() {
            g.flush().ok();
        }
        self.last_flush = std::time::Instant::now();
    }

    fn main_path(&self) -> PathBuf {
        self.cfg.dir.join("AirFrameXR.log")
    }

    fn open_main(&mut self, at: Stamp) {
        let path = self.main_path();
        // the day of an existing file is the date its first line starts with
        let (day, len) = match fs::File::open(&path) {
            Ok(mut f) => {
                let mut head = [0u8; 10];
                let n = f.read(&mut head).unwrap_or(0);
                (std::str::from_utf8(&head[..n]).ok().filter(|s| s.len() == 10).map(str::to_string), f.metadata().map(|m| m.len()).unwrap_or(0))
            }
            Err(_) => (None, 0),
        };
        if let Some(d) = &day {
            if *d != at.day() {
                self.roll(&path, d);
            }
        }
        let len = if day.as_deref().map_or(false, |d| d != at.day()) { 0 } else { len };
        self.main = fs::OpenOptions::new().create(true).append(true).open(&path).ok().map(BufWriter::new);
        self.main_day = at.day();
        self.main_bytes = len;
    }

    /// Renames the current file to `AirFrameXR.<day>.log` (`-2`, `-3`... when that exists) and prunes old days. A failed rename
    /// (another process holds the file open without delete sharing) keeps appending to the same file.
    fn roll(&mut self, path: &Path, day: &str) {
        let mut target = self.cfg.dir.join(format!("AirFrameXR.{day}.log"));
        let mut n = 2;
        while target.exists() {
            target = self.cfg.dir.join(format!("AirFrameXR.{day}-{n}.log"));
            n += 1;
        }
        fs::rename(path, &target).ok();
        prune_days(&self.cfg.dir, self.cfg.keep_days);
    }

    fn write_main(&mut self, at: Stamp, level: Level, comp: &str, text: &str) {
        if self.main.is_none() || self.main_day != at.day() {
            if let Some(f) = self.main.as_mut() {
                f.flush().ok();
            }
            if self.main.is_some() {
                let (path, day) = (self.main_path(), self.main_day.clone());
                self.main = None;
                self.roll(&path, &day);
            }
            self.open_main(at);
        } else if self.main_bytes > self.cfg.max_bytes {
            if let Some(f) = self.main.as_mut() {
                f.flush().ok();
            }
            let (path, day) = (self.main_path(), format!("{}-{}", self.main_day, at.compact().split('-').nth(1).unwrap_or("")));
            self.main = None;
            self.roll(&path, &day);
            self.open_main(at);
        }
        let line = format!("{} [{}] {:<5} {}\n", at.full(), comp, level.name(), text);
        if let Some(f) = self.main.as_mut() {
            if f.write_all(line.as_bytes()).is_ok() {
                self.main_bytes += line.len() as u64;
            }
        }
    }
}

/// Keeps the files of the newest `keep_days` days among the rolled-over main logs `AirFrameXR.<yyyy-mm-dd>[-n].log`.
fn prune_days(dir: &Path, keep_days: usize) {
    let Ok(rd) = fs::read_dir(dir) else { return };
    let files: Vec<(String, PathBuf)> = rd
        .filter_map(|e| e.ok())
        .filter_map(|e| {
            let n = e.file_name().to_string_lossy().into_owned();
            let day = n.strip_prefix("AirFrameXR.")?.strip_suffix(".log")?.get(..10)?.to_string();
            Some((day, e.path()))
        })
        .collect();
    let mut days: Vec<&String> = files.iter().map(|(d, _)| d).collect();
    days.sort();
    days.dedup();
    if days.len() > keep_days {
        let oldest_kept = days[days.len() - keep_days].clone();
        for (d, p) in &files {
            if *d < oldest_kept {
                fs::remove_file(p).ok();
            }
        }
    }
}

/// Keeps the newest `keep` files of `dir` (names sort by date).
fn prune(dir: &Path, prefix: Option<&str>, keep: usize) {
    let Ok(rd) = fs::read_dir(dir) else { return };
    let mut names: Vec<PathBuf> = rd
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| p.is_file() && p.extension().map_or(false, |x| x == "log"))
        .filter(|p| match (prefix, p.file_name().and_then(|n| n.to_str())) {
            (Some(pre), Some(n)) => n.starts_with(pre) && n != "AirFrameXR.log",
            (None, Some(_)) => true,
            _ => false,
        })
        .collect();
    names.sort();
    if names.len() > keep {
        for p in &names[..names.len() - keep] {
            fs::remove_file(p).ok();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("logsink_{name}_{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }
    fn read(p: &Path) -> String {
        fs::read_to_string(p).unwrap_or_default()
    }

    #[test]
    fn lines_arrive_in_order_with_format() {
        let d = tmp("order");
        let s = Sink::start(Config::new(&d), Level::Info);
        for i in 0..50 {
            s.log(Level::Info, "host", format!("line {i}"), Dest::Main);
        }
        s.flush();
        let t = read(&d.join("AirFrameXR.log"));
        let lines: Vec<&str> = t.lines().collect();
        assert_eq!(lines.len(), 50);
        assert!(lines[0].contains("[host] INFO  line 0"), "{}", lines[0]);
        assert!(lines[49].ends_with("line 49"));
        assert_eq!(lines[0].as_bytes()[4], b'-'); // starts with the date
    }

    #[test]
    fn level_filter_and_live_change() {
        let d = tmp("level");
        let s = Sink::start(Config::new(&d), Level::Info);
        s.log(Level::Verbose, "host", "hidden", Dest::Main);
        s.log(Level::Warn, "host", "shown", Dest::Main);
        s.set_level(Level::Verbose);
        s.log(Level::Verbose, "host", "now shown", Dest::Main);
        s.log(Level::Debug, "host", "still hidden", Dest::Main);
        s.flush();
        let t = read(&d.join("AirFrameXR.log"));
        assert!(t.contains("WARN  shown") && t.contains("VERB  now shown"));
        assert!(!t.contains("still hidden"));
        assert_eq!(t.lines().count(), 2);
    }

    #[test]
    fn full_queue_drops_and_reports_without_blocking() {
        let d = tmp("drop");
        let mut c = Config::new(&d);
        c.queue = 4;
        let s = Sink::start(c, Level::Info);
        let t0 = std::time::Instant::now();
        for i in 0..20000 {
            s.log(Level::Info, "host", format!("x{i}"), Dest::Main);
        }
        assert!(t0.elapsed() < Duration::from_secs(2), "callers must not wait for the disk");
        assert!(s.dropped() > 0);
        thread::sleep(Duration::from_millis(500));
        s.flush();
        let t = read(&d.join("AirFrameXR.log"));
        assert!(t.contains("log lines dropped (queue full)"));
    }

    #[test]
    fn warn_and_both_lines_reach_the_run_log_too() {
        let d = tmp("both");
        let s = Sink::start(Config::new(&d), Level::Info);
        s.log(Level::Warn, "host", "warn no game", Dest::Main);
        s.log(Level::Info, "host", "both no game", Dest::Both);
        let p = s.game_start("G.exe");
        s.log(Level::Warn, "host", "stall detected", Dest::Main);
        s.log(Level::Error, "host", "an error", Dest::Run);
        s.log(Level::Info, "shim", "shim line", Dest::Both);
        s.log(Level::Info, "host", "main only", Dest::Main);
        s.log(Level::Verbose, "host", "filtered", Dest::Both);
        s.game_end("done");
        s.flush();
        let main = read(&d.join("AirFrameXR.log"));
        let run = read(&d.join(&p));
        for t in ["warn no game", "both no game", "stall detected", "shim line", "main only"] {
            assert!(main.contains(t), "main has {t}");
        }
        assert!(run.contains("stall detected") && run.contains("shim line") && run.contains("an error"));
        assert!(!run.contains("main only") && !run.contains("warn no game") && !run.contains("filtered"));
        assert!(!main.contains("filtered"));
    }

    #[test]
    fn game_lines_go_to_the_run_log_and_runs_are_pruned() {
        let d = tmp("game");
        let mut c = Config::new(&d);
        c.keep_runs = 2;
        let s = Sink::start(c, Level::Info);
        s.log(Level::Info, "host", "before", Dest::Run); // no game: main log
        let mut paths = vec![];
        for i in 0..3 {
            paths.push(s.game_start("My/Game.exe"));
            s.log(Level::Info, "shim", format!("in run {i}"), Dest::Run);
            s.log(Level::Info, "host", format!("main only {i}"), Dest::Main);
            s.game_end("run summary");
            s.flush();
            thread::sleep(Duration::from_millis(1100)); // run files are named by the second
        }
        let main = read(&d.join("AirFrameXR.log"));
        assert!(main.contains("before") && main.contains("main only 2") && !main.contains("in run"));
        assert!(paths[0].starts_with(d.join("games").join("My_Game.exe")));
        assert!(!paths[0].exists(), "oldest run pruned");
        let last = read(&paths[2]);
        assert!(last.contains("in run 2") && last.contains("run summary") && !last.contains("main only"));
    }

    #[test]
    fn existing_file_of_an_earlier_day_is_rolled_over_and_old_days_pruned() {
        let d = tmp("roll");
        fs::write(d.join("AirFrameXR.log"), "2020-01-02 10:00:00.000 [host] INFO  old\n").unwrap();
        for i in 1..=5 {
            fs::write(d.join(format!("AirFrameXR.2019-12-0{i}.log")), "x").unwrap();
        }
        let mut c = Config::new(&d);
        c.keep_days = 1;
        let s = Sink::start(c, Level::Info);
        s.log(Level::Info, "host", "today", Dest::Main);
        s.flush();
        assert!(read(&d.join("AirFrameXR.2020-01-02.log")).contains("old"));
        for i in 1..=5 {
            assert!(!d.join(format!("AirFrameXR.2019-12-0{i}.log")).exists(), "day 0{i} should be pruned (keep_days = 1)");
        }
        let t = read(&d.join("AirFrameXR.log"));
        assert!(t.contains("today") && !t.contains("old"));
    }

    #[test]
    fn run_only_lines_need_a_running_game() {
        let d = tmp("runonly");
        let s = Sink::start(Config::new(&d), Level::Info);
        s.log(Level::Info, "perf", "no game", Dest::RunOnly);
        let p = s.game_start("g.exe");
        s.log(Level::Info, "perf", "in game", Dest::RunOnly);
        s.game_end("done");
        s.flush();
        assert!(!read(&d.join("AirFrameXR.log")).contains("perf"));
        assert!(read(&p).contains("in game"));
    }

    #[test]
    fn stamps_parse_and_a_stamped_line_keeps_its_time() {
        let st = Stamp::parse("2026-11-12 13:14:15.678").unwrap();
        assert_eq!((st.y, st.mo, st.d, st.h, st.mi, st.s, st.ms), (2026, 11, 12, 13, 14, 15, 678));
        assert!(Stamp::parse("nonsense").is_none());
        let d = tmp("stamped");
        let s = Sink::start(Config::new(&d), Level::Info);
        s.log_at(st, Level::Info, "shim:g.exe", "game start", Dest::Main);
        s.flush();
        let t = read(&d.join("AirFrameXR.log"));
        assert!(t.starts_with("2026-11-12 13:14:15.678 [shim:g.exe] INFO  game start"), "{t}");
    }

    #[test]
    fn sanitize_keeps_names_safe() {
        assert_eq!(sanitize("a/b\\c:d.exe"), "a_b_c_d.exe");
        assert_eq!(sanitize(".."), "unknown");
        assert_eq!(sanitize(""), "unknown");
    }

    #[cfg(not(windows))]
    #[test]
    fn civil_date_from_unix() {
        let s = Stamp::from_unix(1_700_000_000, 7); // 2023-11-14 22:13:20 UTC
        assert_eq!((s.y, s.mo, s.d, s.h, s.mi, s.s), (2023, 11, 14, 22, 13, 20));
        assert_eq!(s.full(), "2023-11-14 22:13:20.007");
    }
}
