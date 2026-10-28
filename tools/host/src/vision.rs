//! AirFrameXR around the ALVR host: the portable install layout, `config/airframexr.json`, the control channel from the GUI
//! (stdin), the system check, the GPU sampler for the debug log, and the headset's identity from ALVR's client list.
use serde_json::{json, Value};
use std::{
    fs,
    io::BufRead,
    path::{Path, PathBuf},
    sync::atomic::{AtomicBool, AtomicI32, AtomicU32, Ordering},
    thread,
    time::Duration,
};

pub const VERSION: &str = "1.1.0";

/// Portable install: everything sits in the folder the user unzipped (`alvr_host.exe` next to the runtime DLLs), with
/// `config/` (session.json, airframexr.json) and `logs/` inside it.
pub struct Install {
    pub dir: PathBuf,
    pub config: PathBuf,
    pub logs: PathBuf,
}

impl Install {
    pub fn new(dir: &Path) -> Self {
        Self { dir: dir.to_path_buf(), config: dir.join("config"), logs: dir.join("logs") }
    }
}

/// `config/airframexr.json`, written by configure.exe / AirFrameXR.exe. Command-line flags win over it.
#[derive(Default, Debug)]
pub struct Settings {
    pub idle_rgb: Option<u32>,
    pub encode_profile: Option<String>,
    pub qp_map: Option<bool>,
    pub split_encode: Option<String>,
    pub gpu_priority: Option<i64>,
    /// shim running start margin in ms (0 = release the game at the display tick)
    pub running_start_ms: Option<f64>,
    /// head-sample policy of the shim (see main.rs Args::head_wait_ms) and the host's frame queue (pipeline.rs)
    pub head_wait_ms: Option<f64>,
    pub predict_missing_head: Option<bool>,
    /// the shim holds the game's next release while the host's queue holds a frame a boundary passed over (drains a standing queue without
    /// dropping a rendered frame; pipeline.rs / ovrshim WaitForVsync); default on
    pub intake_gate: Option<bool>,
    /// game frame pacing fix: a frame submitted with the same render poses as the one before is not composed or published (ovrshim SubmitFrame); default off
    pub drop_duplicate_frames: Option<bool>,
    /// slide the host's display grid so frames arrive just before the headset's tick (phase.rs); default on
    pub phase_lock: Option<bool>,
    /// arrival margin of the phase lock, ms (default 7; the environment variable AIRFRAMEXR_PHASE_MARGIN_MS wins)
    pub phase_margin_ms: Option<f64>,
    /// GPU keepalive duty in percent (a laptop GPU that drops to a low power state while streaming; --gpu-keepalive wins)
    pub gpu_keepalive: Option<i64>,
    /// frame generation (docs/FRAME_GENERATION.md): half-rate pacing + optical-flow interpolated frames; default off
    pub frame_gen: Option<bool>,
    /// "edge" (default) | "edge-blend" | "classic" (the crossfade recipe, the predictable fallback): the synthesis of generated frames
    /// (nvenc/hostlib/framegen.cpp)
    pub frame_gen_synthesis: Option<String>,
    /// "half" (default) | "uncapped": the game paced at half the display rate with strict alternation, or at the display rate with every
    /// output interpolated (docs/FRAME_GENERATION_UNCAPPED.md)
    pub frame_gen_rate: Option<String>,
    /// uncapped mode: minimum spacing of the game's releases as an fps cap (0 = none): headroom for the host's own GPU work
    pub frame_gen_cap_fps: Option<f64>,
    /// the field of view the game is told, set from the streamer (Advanced picture and FOV settings): override on/off and, per edge, the tangents the game gets
    /// (outer = the side away from the nose, inner, upper, lower; never more than the headset's own, see `fov_scale_from`). Off: the headset's, untouched.
    pub fov_override: Option<bool>,
    pub fov_outer: Option<f64>,
    pub fov_inner: Option<f64>,
    pub fov_upper: Option<f64>,
    pub fov_lower: Option<f64>,
    /// 0..1: where the game's render size sits between the floor (the stream's pixel density over the restricted view) and the stream's eye size (0 = the floor)
    pub fov_render_quality: Option<f64>,
    pub debug: Option<bool>,
    pub gamma: Option<f32>,
    /// headset height above the floor handed to games (m); 0 = the headset's own floor estimate
    pub height_m: Option<f32>,
    /// key combination that recentres while playing (`controls.recenter_key`, e.g. "Ctrl+Alt+R", "F9", "Insert"; "off" disables)
    pub recenter_key: Option<String>,
    /// hand pose convention fix for the Oculus-emulation path: rotation (deg, XYZ Euler, left hand; the right mirrors y/z) and
    /// position offset (m) applied in the hand's local frame by the shim; default [0, 180, 0] (palm/back and aim were inverted)
    pub hand_rotation_deg: Option<[f32; 3]>,
    pub hand_position_m: Option<[f32; 3]>,
    /// brightness, contrast, saturation, sharpening (0 = neutral)
    pub color: [f32; 4],
}

fn arr3(v: &Value) -> Option<[f32; 3]> {
    let a = v.as_array()?;
    if a.len() != 3 {
        return None;
    }
    Some([a[0].as_f64()? as f32, a[1].as_f64()? as f32, a[2].as_f64()? as f32])
}

pub fn load_settings(path: &Path) -> Settings {
    // before 1.0 the project was called VisionALVR and its settings file visionalvr.json: a folder carried over keeps its settings (the old file stays as a backup)
    if !path.exists() {
        let old = path.with_file_name("visionalvr.json");
        if old.exists() && fs::copy(&old, path).is_ok() {
            crate::logs::general("INFO", "settings carried over from config/visionalvr.json (the name before 1.0)");
        }
    }
    // A UTF-8 BOM (Windows PowerShell's Set-Content / older Notepad) made serde_json reject the whole file and silently
    // dropped every setting (seen 2026-10-10): strip it.
    let v: Value = fs::read_to_string(path).ok().and_then(|t| serde_json::from_str(t.trim_start_matches('\u{feff}')).ok()).unwrap_or_default();
    let video = &v["video"];
    // settings of 1.0 that were removed because they measured no benefit (docs/PERFORMANCE_KNOBS.md, "Removed on purpose"): ignored, said once
    const RETIRED: [&str; 11] = ["send_pacing", "intake_depth", "pick_policy", "client_queue_drain", "follow_client_rate", "boundary_offset_ms", "frame_gen_flow_scale",
        "frame_gen_hold_age", "frame_gen_perf", "frame_gen_lag_ms", "gpu_sched_class"];
    let found: Vec<&str> = RETIRED.iter().copied().filter(|k| !video[*k].is_null()).collect();
    if !found.is_empty() {
        crate::logs::general("INFO", &format!("settings ignored (removed in 1.0.1, the built-in value is used): {}", found.join(", ")));
    }
    Settings {
        idle_rgb: video["idle_rgb"].as_str().and_then(|s| u32::from_str_radix(s.trim_start_matches('#'), 16).ok()),
        encode_profile: video["encode_profile"].as_str().map(str::to_string),
        qp_map: video["qp_map"].as_bool(),
        split_encode: video["split_encode"].as_str().map(str::to_string),
        gpu_priority: video["gpu_priority"].as_i64(),
        running_start_ms: video["running_start_ms"].as_f64(),
        head_wait_ms: video["head_wait_ms"].as_f64(),
        predict_missing_head: video["predict_missing_head"].as_bool(),
        intake_gate: video["intake_gate"].as_bool(),
        drop_duplicate_frames: video["drop_duplicate_frames"].as_bool(),
        phase_lock: video["phase_lock"].as_bool(),
        phase_margin_ms: video["phase_margin_ms"].as_f64(),
        gpu_keepalive: video["gpu_keepalive"].as_i64(),
        frame_gen: video["frame_gen"].as_bool(),
        frame_gen_synthesis: video["frame_gen_synthesis"].as_str().map(str::to_string),
        frame_gen_rate: video["frame_gen_rate"].as_str().map(str::to_string),
        frame_gen_cap_fps: video["frame_gen_cap_fps"].as_f64(),
        fov_override: video["fov_override"].as_bool(),
        fov_outer: video["fov_outer"].as_f64(),
        fov_inner: video["fov_inner"].as_f64(),
        fov_upper: video["fov_upper"].as_f64(),
        fov_lower: video["fov_lower"].as_f64(),
        fov_render_quality: video["fov_render_quality"].as_f64(),
        debug: v["debug"].as_bool(),
        gamma: v["display"]["gamma"].as_f64().map(|g| g as f32),
        height_m: v["headset"]["height_m"].as_f64().map(|g| g as f32),
        recenter_key: v["controls"]["recenter_key"].as_str().map(str::to_string),
        hand_rotation_deg: arr3(&v["controllers"]["hand_rotation_deg"]),
        hand_position_m: arr3(&v["controllers"]["hand_position_m"]),
        color: ["brightness", "contrast", "saturation", "sharpening"].map(|k| v["display"][k].as_f64().unwrap_or(0.0) as f32),
    }
}

/// First start of a fresh install: `config/session.json` from the shipped `config/session.default.json`.
pub fn seed_session(config: &Path) -> bool {
    let s = config.join("session.json");
    let d = config.join("session.default.json");
    !s.exists() && d.exists() && fs::copy(&d, &s).is_ok()
}

/// The headset's address may change (DHCP). ALVR re-finds a trusted headset through its discovery broadcasts and records the
/// new `current_ip`; put it first in `manual_ips` so the next start dials it directly. Returns the changes made.
pub fn refresh_manual_ips(session: &Path) -> Vec<String> {
    let Some(mut s) = fs::read_to_string(session).ok().and_then(|t| serde_json::from_str::<Value>(&t).ok()) else { return vec![] };
    let mut changes = vec![];
    if let Some(clients) = s["client_connections"].as_object_mut() {
        for (host, c) in clients.iter_mut() {
            let Some(cur) = c["current_ip"].as_str().map(str::to_string) else { continue };
            let ips = c["manual_ips"].as_array().cloned().unwrap_or_default();
            if ips.first().and_then(Value::as_str) != Some(cur.as_str()) {
                let mut new = vec![json!(cur)];
                new.extend(ips.into_iter().filter(|i| i.as_str() != Some(cur.as_str())));
                c["manual_ips"] = json!(new);
                changes.push(format!("{host}: manual IP {cur} first"));
            }
        }
    }
    if !changes.is_empty() {
        fs::write(session, serde_json::to_string_pretty(&s).unwrap()).ok();
    }
    changes
}

/// (hostname, display name, ip) of the client ALVR is streaming to (connection_state "Streaming"), else the first trusted one.
pub fn headset(session: &Path, streaming_only: bool) -> Option<(String, String, String)> {
    let s: Value = serde_json::from_str(&fs::read_to_string(session).ok()?).ok()?;
    let clients = s["client_connections"].as_object()?;
    let pick = clients.iter().find(|(_, c)| c["connection_state"] == "Streaming")
        .or_else(|| if streaming_only { None } else { clients.iter().find(|(_, c)| c["trusted"] == true) })?;
    let (h, c) = pick;
    let ip = c["current_ip"].as_str().map(str::to_string)
        .or_else(|| c["manual_ips"].as_array().and_then(|a| a.first()).and_then(Value::as_str).map(str::to_string))
        .unwrap_or_default();
    Some((h.clone(), c["display_name"].as_str().unwrap_or(h).to_string(), ip))
}

/// The negotiated stream in one line for the general log (ALVR persists it in `openvr_config` before it reports the connection).
pub fn stream_summary(session: &Path, encoded: (i32, i32)) -> String {
    let s: Value = fs::read_to_string(session).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default();
    let c = &s["openvr_config"];
    let bitrate = &s["session_settings"]["video"]["bitrate"]["mode"];
    let br = match bitrate["variant"].as_str() {
        Some("ConstantMbps") => format!("{} Mbps constant", bitrate["ConstantMbps"]),
        Some(v) => format!("bitrate {v}"),
        None => "bitrate ?".into(),
    };
    format!(
        "stream: {}x{} per eye @ {} Hz, {}, {}-bit, {}, encoded {}x{}, {br}, encoding gamma {}",
        c["eye_resolution_width"], c["eye_resolution_height"], c["refresh_rate"],
        match c["codec"].as_i64() { Some(0) => "H.264", Some(1) => "HEVC", Some(2) => "AV1", _ => "?" },
        if c["use_10bit_encoder"] == true { 10 } else { 8 },
        if c["enable_foveated_encoding"] == true { "foveated" } else { "full frame" },
        encoded.0, encoded.1, c["encoding_gamma"],
    )
}

// ---- control channel from the GUI (stdin lines) -------------------------------------------------------------------------

pub static QUIT: AtomicBool = AtomicBool::new(false);
/// headset height request (f32 bits, metres); 0 = none pending
pub static HEIGHT_REQ: AtomicU32 = AtomicU32::new(0);
/// user display gamma (f32 bits); 0 = unchanged
pub static GAMMA_REQ: AtomicU32 = AtomicU32::new(0);
/// -1 nothing pending, 0 debug off, 1 debug on
pub static DEBUG_REQ: AtomicI32 = AtomicI32::new(-1);
/// frame generation on/off from AirFrameXR.exe (-1 none, 0 off, 1 on); applied by the main loop to the running encoder and the shim's pacing
pub static FRAME_GEN_REQ: AtomicI32 = AtomicI32::new(-1);
/// frame generation rate from AirFrameXR.exe (-1 none, else `pipeline::FgRate::index`: 0 third, 1 half, 2 two_thirds, 3 uncapped)
pub static FG_RATE_REQ: AtomicI32 = AtomicI32::new(-1);
/// fps cap of the game's releases (uncapped mode), -1 none, 0 = no cap
pub static FG_CAP_REQ: AtomicI32 = AtomicI32::new(-1);
/// uncapped mode content lag in tenths of ms (-1 none, 0 = automatic)
/// `recenter`: turn the published tracking space so the head's forward is -Z and its position the origin (taken at the next tracking sample)
pub static RECENTER_REQ: AtomicBool = AtomicBool::new(false);
/// colour correction (brightness, contrast, saturation, sharpening as f32 bits) and its "pending" flag
pub static COLOR_REQ: [AtomicU32; 4] = [AtomicU32::new(0), AtomicU32::new(0), AtomicU32::new(0), AtomicU32::new(0)];
pub static COLOR_PENDING: AtomicBool = AtomicBool::new(false);
/// the field-of-view override sent by the streamer when the user touches the tick or a field: on/off, the four tangents (outer, inner, upper, lower; f32 bits), pending flag
pub static FOV_ON: AtomicBool = AtomicBool::new(false);
pub static FOV_REQ: [AtomicU32; 4] = [AtomicU32::new(0), AtomicU32::new(0), AtomicU32::new(0), AtomicU32::new(0)];
pub static FOV_PENDING: AtomicBool = AtomicBool::new(false);
/// the render quality sent with the override (f32 bits)
pub static FOV_QUALITY_REQ: AtomicU32 = AtomicU32::new(0);
/// 0..1 for a quality given in the settings, the lab flag or the command; anything else is the floor
pub fn render_quality(v: f64) -> f32 {
    if v.is_finite() { v.clamp(0.0, 1.0) as f32 } else { 0.0 }
}
/// percent -> fraction (the lab flag `--fov-scale`), within what a restriction accepts (a view cannot be emptied)
pub fn fov_fraction(pct: f64) -> f32 {
    (pct / 100.0).clamp(0.2, 1.0) as f32
}
/// The per-edge fractions of the headset's field of view for an override given as tangents (outer, inner, upper, lower): the headset's tangents (fov8 = left eye l r u d,
/// right eye l r u d) are averaged over the two eyes per edge (outer = the left eye's left and the right eye's right), a value above the headset's is the headset's
/// (the game is never told more than the stream covers), below a fifth of it a fifth.
pub fn fov_scale_from(t: &[f32; 4], fov8: &[f32; 8]) -> [f32; 4] {
    let client = [(fov8[0] + fov8[5]) / 2.0, (fov8[1] + fov8[4]) / 2.0, (fov8[2] + fov8[6]) / 2.0, (fov8[3] + fov8[7]) / 2.0];
    let mut s = [1.0f32; 4];
    for k in 0..4 {
        // within the two-decimal mask of the headset's own value (what an untouched field shows) is the headset's own: nothing is restricted
        if t[k] > 0.0 && client[k] > 0.0 && t[k] + 0.005 < client[k] {
            s[k] = (t[k] / client[k]).clamp(0.2, 1.0);
        }
    }
    s
}

/// Commands (one per line): `quit`, `gamma <0.5..2.0>`, `debug on|off`, `frame_gen on|off`, `frame_gen_rate third|half|two_thirds|uncapped`,
/// `frame_gen_cap <fps>` (0 = none), `fov_override off` | `fov_override on <outer> <inner> <upper> <lower> [<render quality 0..1>]` (the tangents the game is told and where its render size sits between the floor and the stream's eye size, taken by the next game), `recenter`,
/// `color <brightness> <contrast> <saturation> <sharpening>`,
/// `height <m>` (headset height above the floor handed to games, re-anchored to the current pose; 0 = headset floor).
/// End of input (the GUI went away) = quit.
/// Virtual-key codes of a combination like "Ctrl+Alt+R" (modifiers, then one key: a letter or digit, F1-F24, Insert, Delete, Home, End, PageUp,
/// PageDown, Pause); None for "off" or a name that is not understood.
pub fn parse_hotkey(s: &str) -> Option<Vec<i32>> {
    let mut keys = Vec::new();
    for t in s.split('+').map(|t| t.trim().to_ascii_lowercase()) {
        let vk = match t.as_str() {
            "ctrl" | "control" => 0x11,
            "alt" => 0x12,
            "shift" => 0x10,
            "insert" | "ins" => 0x2D,
            "delete" | "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "pause" => 0x13,
            k if k.len() == 1 && k.as_bytes()[0].is_ascii_alphanumeric() => k.as_bytes()[0].to_ascii_uppercase() as i32,
            k if k.starts_with('f') && k[1..].parse::<i32>().map(|n| (1..=24).contains(&n)).unwrap_or(false) => 0x70 + k[1..].parse::<i32>().unwrap() - 1,
            _ => return None,
        };
        keys.push(vk);
    }
    if keys.is_empty() { None } else { Some(keys) }
}

/// Polls the key combination and requests a recenter on every press (any window has the focus: the game does). Windows only.
#[cfg(windows)]
pub fn spawn_recenter_hotkey(spec: &str) {
    extern "system" {
        fn GetAsyncKeyState(vk: i32) -> i16;
    }
    let Some(keys) = parse_hotkey(spec) else {
        crate::logs::general("WARN", &format!("recenter key '{spec}' is not understood (e.g. Ctrl+Alt+R, F9, Insert): no recenter hotkey"));
        return;
    };
    crate::logs::general("INFO", &format!("recenter hotkey: {spec}"));
    thread::spawn(move || {
        let mut was = false;
        loop {
            let down = keys.iter().all(|&k| unsafe { GetAsyncKeyState(k) } < 0);
            if down && !was {
                RECENTER_REQ.store(true, Ordering::SeqCst);
                crate::logs::general("INFO", "recenter (hotkey)");
            }
            was = down;
            thread::sleep(Duration::from_millis(25));
        }
    });
}

#[cfg(not(windows))]
pub fn spawn_recenter_hotkey(_spec: &str) {}

pub fn spawn_stdin_control() {
    thread::spawn(|| {
        let stdin = std::io::stdin();
        for line in stdin.lock().lines() {
            let Ok(line) = line else { break };
            let mut w = line.split_whitespace();
            match (w.next(), w.next()) {
                (Some("quit"), _) => break,
                (Some("gamma"), Some(g)) => {
                    if let Ok(g) = g.parse::<f32>() {
                        GAMMA_REQ.store(g.clamp(0.5, 2.0).to_bits(), Ordering::SeqCst);
                    }
                }
                (Some("debug"), Some(v)) => DEBUG_REQ.store((v == "on" || v == "1") as i32, Ordering::SeqCst),
                (Some("frame_gen"), Some(v)) => FRAME_GEN_REQ.store((v == "on" || v == "1") as i32, Ordering::SeqCst),
                (Some("frame_gen_rate"), Some(v)) => {
                    if let Some(r) = crate::pipeline::FgRate::parse(v) {
                        FG_RATE_REQ.store(r.index(), Ordering::SeqCst);
                    }
                }
                (Some("recenter"), _) => RECENTER_REQ.store(true, Ordering::SeqCst),
                (Some("frame_gen_cap"), Some(v)) => {
                    if let Ok(f) = v.parse::<f64>() {
                        FG_CAP_REQ.store(f.clamp(0.0, 240.0).round() as i32, Ordering::SeqCst);
                    }
                }
                (Some("height"), Some(h)) => {
                    if let Ok(h) = h.parse::<f32>() {
                        HEIGHT_REQ.store(if h <= 0.0 { f32::MIN_POSITIVE } else { h.clamp(0.5, 2.5) }.to_bits(), Ordering::SeqCst);
                    }
                }
                (Some("fov_override"), Some("off")) => {
                    FOV_ON.store(false, Ordering::SeqCst);
                    FOV_PENDING.store(true, Ordering::SeqCst);
                }
                (Some("fov_override"), Some("on")) => {
                    let vals: Vec<f32> = w.filter_map(|x| x.parse().ok()).collect();
                    if (vals.len() == 4 || vals.len() == 5) && vals[..4].iter().all(|v| v.is_finite() && *v > 0.0) {
                        for (k, t) in vals[..4].iter().enumerate() {
                            FOV_REQ[k].store(t.to_bits(), Ordering::SeqCst);
                        }
                        FOV_QUALITY_REQ.store(vals.get(4).map_or(0.0, |q| render_quality(*q as f64)).to_bits(), Ordering::SeqCst);
                        FOV_ON.store(true, Ordering::SeqCst);
                        FOV_PENDING.store(true, Ordering::SeqCst);
                    }
                }
                (Some("color"), Some(first)) => {
                    let vals: Vec<f32> = std::iter::once(first).chain(w).filter_map(|x| x.parse().ok()).collect();
                    if vals.len() == 4 {
                        for (k, v) in vals.iter().enumerate() {
                            COLOR_REQ[k].store(v.to_bits(), Ordering::SeqCst);
                        }
                        COLOR_PENDING.store(true, Ordering::SeqCst);
                    }
                }
                _ => {}
            }
        }
        QUIT.store(true, Ordering::SeqCst);
    });
}

// ---- system check -----------------------------------------------------------------------------------------------------------

/// The host is built against NVENC API 12.2 (split-frame encoding needs >= 12.1): the driver must support at least that.
pub const MIN_NVENC_API: (u32, u32) = (12, 2);

pub fn system_check(gpu_info: Value) -> Value {
    let name = gpu_info["name"].as_str().unwrap_or("").to_string();
    let nvenc = gpu_info["nvenc_api"].as_str().unwrap_or("").to_string();
    let api = nvenc.split_once('.').and_then(|(a, b)| Some((a.parse::<u32>().ok()?, b.parse::<u32>().ok()?)));
    let has_nvidia = gpu_info["nvml"].as_i64().unwrap_or(0) == 1 || name.to_lowercase().contains("nvidia");
    let ok = has_nvidia && api.map_or(false, |v| v >= MIN_NVENC_API);
    json!({
        "version": VERSION,
        "alvr_protocol": alvr_common::protocol_id(),
        // what a compatible headset puts in its discovery broadcast (bytes 16..24, little endian), as a string (u64)
        "alvr_protocol_id": alvr_common::protocol_id_u64().to_string(),
        "gpu": gpu_info,
        "nvenc_api": nvenc,
        "ok": ok,
        "reason": if ok { "".to_string() } else if !has_nvidia {
            "AirFrameXR streams with NVIDIA's hardware video encoder (NVENC); no NVIDIA GPU was found".to_string()
        } else if api.is_none() {
            "the NVIDIA video encoder library (nvEncodeAPI64.dll) was not found: install or update the NVIDIA driver".to_string()
        } else {
            format!("the NVIDIA driver supports NVENC API {nvenc}; AirFrameXR needs {}.{} or newer: update the NVIDIA driver", MIN_NVENC_API.0, MIN_NVENC_API.1)
        },
    })
}

// ---- GPU sampler (debug sessions) -------------------------------------------------------------------------------------------

pub fn spawn_gpu_sampler(sample: fn() -> Option<String>) {
    thread::spawn(move || {
        let mut header_for: Option<PathBuf> = None;
        loop {
            thread::sleep(Duration::from_secs(1));
            let Some(dir) = crate::logs::debug_dir() else { continue };
            if header_for.as_ref() != Some(&dir) {
                crate::logs::debug_file_line("gpu.csv", "time,gpu_pct,mem_pct,enc_pct,dec_pct,graphics_mhz,memory_mhz,video_mhz,power_w,temp_c,pstate,vram_used_mb,clock_event_reasons");
                header_for = Some(dir);
            }
            if let Some(row) = sample() {
                crate::logs::debug_file_line("gpu.csv", &format!("{},{row}", crate::logs::local_time(false, false)));
            }
        }
    });
}

/// Estimates the client's display period from the regular spacing of its tracking-sample timestamps (the headset's refresh rate can
/// differ from the 90 Hz the stream asks for: visionOS ran 100 Hz in one session, 90 Hz in the others). A candidate must hold for three
/// evaluations (90 samples each) before it is reported. The caller checks that the cadence is a display rate at all: a headset that
/// tracks faster than it displays (Quest 3, 270 Hz) is not reporting its display period this way (2026-10-16: the 4 ms floor here
/// turned its 3.7 ms cadence into a 241 Hz "display" and the host paced everything at 241 Hz).
pub struct RateEstimator {
    prev: u64,
    diffs: std::collections::VecDeque<u64>,
    pushes: u32,
    cand: u64,
    cand_count: u32,
}

impl RateEstimator {
    pub fn new() -> Self {
        Self { prev: 0, diffs: Default::default(), pushes: 0, cand: 0, cand_count: 0 }
    }
    pub fn reset(&mut self) {
        *self = Self::new();
    }
    /// Feed a sample timestamp (ns); Some(period ns) once a stable estimate exists (repeated at every evaluation).
    pub fn push(&mut self, ts_ns: u64) -> Option<u64> {
        let prev = std::mem::replace(&mut self.prev, ts_ns);
        if prev == 0 || ts_ns <= prev {
            return None;
        }
        let d = ts_ns - prev;
        if !(2_000_000..25_000_000).contains(&d) {
            return None;
        }
        self.diffs.push_back(d);
        if self.diffs.len() > 90 {
            self.diffs.pop_front();
        }
        self.pushes += 1;
        if self.pushes % 90 != 0 || self.diffs.len() < 60 {
            return None;
        }
        let mut v: Vec<u64> = self.diffs.iter().copied().collect();
        v.sort_unstable();
        let med = v[v.len() / 2];
        // a missed sample shows as a double gap; the median ignores up to half of them
        if self.cand != 0 && (med as f64 - self.cand as f64).abs() / (self.cand as f64) < 0.01 {
            self.cand_count += 1;
        } else {
            self.cand = med;
            self.cand_count = 1;
        }
        if self.cand_count >= 3 { Some(self.cand) } else { None }
    }
}

#[cfg(test)]
mod fov_tests {
    use super::fov_scale_from;
    // the Vision Pro's views as the host saved them: left eye l r u d, right eye l r u d
    const AVP: [f32; 8] = [1.7823, 1.0186, 1.2169, 1.0186, 1.0249, 1.7995, 1.2253, 1.0249];

    #[test]
    fn the_headsets_own_values_restrict_nothing() {
        // outer (1.7823 + 1.7995) / 2 = 1.7909, inner 1.0218, upper 1.2211, lower 1.0218: the mask shows 1.79 1.02 1.22 1.02
        assert_eq!(fov_scale_from(&[1.79, 1.02, 1.22, 1.02], &AVP), [1.0; 4]);
        assert_eq!(fov_scale_from(&[1.7909, 1.0218, 1.2211, 1.0218], &AVP), [1.0; 4]);
        assert_eq!(fov_scale_from(&[3.0, 2.0, 2.0, 2.0], &AVP), [1.0; 4], "never more than the headset's");
    }

    #[test]
    fn an_override_is_a_fraction_per_edge_clamped_to_a_fifth() {
        let s = fov_scale_from(&[1.5, 1.02, 0.9, 0.1], &AVP);
        assert!((s[0] - 1.5 / 1.7909).abs() < 1e-6 && s[1] == 1.0);
        assert!((s[2] - 0.9 / 1.2211).abs() < 1e-6);
        assert_eq!(s[3], 0.2, "a view cannot be emptied");
        assert_eq!(fov_scale_from(&[0.0, -1.0, f32::NAN, 0.0], &AVP), [1.0; 4], "garbage means not restricted");
    }
}
