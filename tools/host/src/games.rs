//! Per-game records: config/games/<exe name without .exe>.json, one file per game (like UEVR's per-exe folders).
//!
//! ```text
//! { "exe": "Game.exe", "name": "<main window title>", "paths": ["C:\\...\\Game.exe"],
//!   "levels": { "<headset>": { "<off|third|half|two_thirds>": <last run at that level> } },   // what the launcher's "Recommended" reads
//!   "sessions": [ <the last SESSIONS_KEPT sessions, newest last> ] }
//! ```
//! The host only records; the launcher proposes. The launcher's own choices (DLL installs, dismissed popups) live in config/mv_dll.json, not here.
use serde_json::{json, Value};
use std::path::{Path, PathBuf};

/// Sessions kept in full per game; the per-level `levels` summary (last run at each level) stays whatever the sessions are.
pub const SESSIONS_KEPT: usize = 30;

/// File name stem for an executable: the name without `.exe`, characters Windows forbids in file names replaced.
pub fn stem(exe: &str) -> String {
    let n = exe.rsplit(['\\', '/']).next().unwrap_or(exe);
    let n = if n.len() > 4 && n[n.len() - 4..].eq_ignore_ascii_case(".exe") { &n[..n.len() - 4] } else { n };
    let s: String = n.chars().map(|c| if "<>:\"/\\|?*".contains(c) || c.is_control() { '_' } else { c }).collect();
    if s.is_empty() { "unknown".into() } else { s }
}

pub fn dir(config_dir: &Path) -> PathBuf {
    config_dir.join("games")
}

pub fn file(config_dir: &Path, exe: &str) -> PathBuf {
    dir(config_dir).join(format!("{}.json", stem(exe)))
}

pub fn load(config_dir: &Path, exe: &str) -> Value {
    std::fs::read_to_string(file(config_dir, exe)).ok().and_then(|t| serde_json::from_str::<Value>(&t).ok()).filter(|v| v.is_object())
        .unwrap_or_else(|| json!({ "exe": exe, "name": "", "paths": [], "levels": {}, "sessions": [] }))
}

fn save(config_dir: &Path, exe: &str, g: &Value) {
    if let Ok(t) = serde_json::to_string_pretty(g) {
        let _ = std::fs::create_dir_all(dir(config_dir));
        let _ = std::fs::write(file(config_dir, exe), t);
    }
}

/// Window title and full path of a running game (None until its window exists), refreshed by the caller until found.
pub struct Meta {
    pub title: String,
    pub path: String,
}

/// Records one run: the per-level summary (`level_rec`, what Recommended reads) and a session entry (`session`, level_rec's fields plus
/// network, motion-vector and GPU facts), trimming the sessions to SESSIONS_KEPT.
pub fn record(config_dir: &Path, exe: &str, headset: &str, level: &str, level_rec: Value, session: Value, meta: Option<&Meta>) -> u64 {
    let mut g = load(config_dir, exe);
    g["exe"] = json!(exe);
    if let Some(m) = meta {
        if !m.title.is_empty() { g["name"] = json!(m.title); }
        if !m.path.is_empty() {
            let mut paths: Vec<Value> = g["paths"].as_array().cloned().unwrap_or_default();
            if !paths.iter().any(|p| p.as_str().map(|s| s.eq_ignore_ascii_case(&m.path)).unwrap_or(false)) { paths.push(json!(m.path)); }
            g["paths"] = json!(paths);
        }
    }
    let runs = g["levels"][headset][level]["runs"].as_u64().unwrap_or(0) + 1;
    let mut rec = level_rec;
    rec["runs"] = json!(runs);
    g["levels"][headset][level] = rec;
    let mut sessions: Vec<Value> = g["sessions"].as_array().cloned().unwrap_or_default();
    sessions.push(session);
    if sessions.len() > SESSIONS_KEPT {
        let cut = sessions.len() - SESSIONS_KEPT;
        sessions.drain(..cut);
    }
    g["sessions"] = json!(sessions);
    save(config_dir, exe, &g);
    runs
}

/// Title and path of the process `exe` (file name, case-insensitive), if it is running and has a visible top-level window.
#[cfg(windows)]
pub fn find_meta(exe: &str) -> Option<Meta> {
    use std::ffi::c_void;
    #[repr(C)]
    struct ProcessEntry { size: u32, usage: u32, pid: u32, heap: usize, module: u32, threads: u32, parent: u32, pri: i32, flags: u32, exe: [u16; 260] }
    extern "system" {
        fn CreateToolhelp32Snapshot(flags: u32, pid: u32) -> *mut c_void;
        fn Process32FirstW(h: *mut c_void, e: *mut ProcessEntry) -> i32;
        fn Process32NextW(h: *mut c_void, e: *mut ProcessEntry) -> i32;
        fn CloseHandle(h: *mut c_void) -> i32;
        fn OpenProcess(access: u32, inherit: i32, pid: u32) -> *mut c_void;
        fn QueryFullProcessImageNameW(h: *mut c_void, flags: u32, buf: *mut u16, len: *mut u32) -> i32;
        fn EnumWindows(cb: extern "system" fn(*mut c_void, isize) -> i32, lparam: isize) -> i32;
        fn GetWindowThreadProcessId(h: *mut c_void, pid: *mut u32) -> u32;
        fn IsWindowVisible(h: *mut c_void) -> i32;
        fn GetWindow(h: *mut c_void, cmd: u32) -> *mut c_void;
        fn GetWindowTextW(h: *mut c_void, buf: *mut u16, max: i32) -> i32;
    }
    let want = exe.rsplit(['\\', '/']).next().unwrap_or(exe).to_lowercase();
    let mut pids: Vec<u32> = Vec::new();
    unsafe {
        let snap = CreateToolhelp32Snapshot(2, 0);
        if snap.is_null() || snap as isize == -1 { return None; }
        let mut e: ProcessEntry = std::mem::zeroed();
        e.size = std::mem::size_of::<ProcessEntry>() as u32;
        let mut ok = Process32FirstW(snap, &mut e);
        while ok != 0 {
            let n = String::from_utf16_lossy(&e.exe[..e.exe.iter().position(|&c| c == 0).unwrap_or(260)]).to_lowercase();
            if n == want { pids.push(e.pid); }
            ok = Process32NextW(snap, &mut e);
        }
        CloseHandle(snap);
    }
    if pids.is_empty() { return None; }
    struct Ctx { pids: Vec<u32>, title: String }
    extern "system" fn each(h: *mut c_void, lparam: isize) -> i32 {
        unsafe {
            let ctx = &mut *(lparam as *mut Ctx);
            let mut pid = 0u32;
            GetWindowThreadProcessId(h, &mut pid);
            if ctx.pids.contains(&pid) && IsWindowVisible(h) != 0 && GetWindow(h, 4).is_null() {   // 4 = GW_OWNER: top-level, not an owned dialog
                let mut b = [0u16; 256];
                let n = GetWindowTextW(h, b.as_mut_ptr(), 256);
                if n > 0 && ctx.title.is_empty() { ctx.title = String::from_utf16_lossy(&b[..n as usize]); return 0; }
            }
        }
        1
    }
    let mut ctx = Ctx { pids: pids.clone(), title: String::new() };
    unsafe { EnumWindows(each, &mut ctx as *mut Ctx as isize) };
    let mut path = String::new();
    unsafe {
        let h = OpenProcess(0x1000, 0, pids[0]);   // PROCESS_QUERY_LIMITED_INFORMATION
        if !h.is_null() {
            let mut b = [0u16; 1024];
            let mut len = 1024u32;
            if QueryFullProcessImageNameW(h, 0, b.as_mut_ptr(), &mut len) != 0 { path = String::from_utf16_lossy(&b[..len as usize]); }
            CloseHandle(h);
        }
    }
    if ctx.title.is_empty() && path.is_empty() { return None; }
    Some(Meta { title: ctx.title, path })
}

#[cfg(not(windows))]
pub fn find_meta(_exe: &str) -> Option<Meta> {
    None
}
