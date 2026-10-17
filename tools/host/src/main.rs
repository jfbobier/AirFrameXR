#![recursion_limit = "256"]
//! alvr_host: SteamVR-free ALVR streamer host (skeleton).
//!
//! Embeds `alvr_server_core` with an isolated config dir. Video comes from a `VideoSource`; the only source
//! today replays a pre-encoded Annex-B HEVC file (made by the nvenc/ test) so the whole ALVR protocol path
//! can be tested without a headset or a compositor. Frames are stamped with the latest tracking timestamp the
//! client sent (what ALVR's own driver does), so the client's timestamp-echo accounting works.
//!
//! Exit codes: 0 = pass (client connected and --min-frames sent), 1 = connected but fewer frames, 2 = no client
//! within --connect-timeout, 3 = bad arguments.
use alvr_common::{parking_lot::Mutex, ButtonType, BUTTON_INFO, HAND_LEFT_ID, HAND_RIGHT_ID, HEAD_ID};
use alvr_packets::{ButtonValue, Haptics};
use alvr_server_core::{ServerCoreContext, ServerCoreEvent};
use alvr_session::CodecType;
mod bench_quality;
mod discovery;
mod logs;
mod nvidia_profile;
mod phase;
mod pipeline;
use pipeline::FgRate;
#[cfg(feature = "simrig")]
mod simrig;
mod telemetry;
mod timing;
mod vision;

use serde_json::json;
use std::{
    fs,
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
        Arc,
    },
    thread,
    time::{Duration, Instant},
};

struct Args {
    config_dir: PathBuf,
    session: Option<PathBuf>,
    file: PathBuf,
    fps: f64,
    seconds: f64,
    connect_timeout: f64,
    min_frames: usize,
    exit_after_frames: Option<usize>,
    report: Option<PathBuf>,
    live: bool,
    width: Option<i32>,
    height: Option<i32>,
    noise: i32, // 0 smooth test pattern, 1 noisy scrolling pattern, 2 independent random frames (benchmarks)
    frames_csv: Option<PathBuf>,
    bench: Option<usize>,
    bench_sleep_ms: u64,
    gpu_keepalive: Option<i32>,
    shim: bool,
    fps_explicit: bool,
    daemon: bool,
    force_session: bool,
    split_encode: i64,
    /// when an encoded frame leaves the host (pipeline.rs): asap | vsync (next tick) | phase (boundary + encode envelope)
    send_pacing: pipeline::SendPacing,
    send_pacing_explicit: bool,
    /// shim running start: the game is released this margin + its frame-time envelope before the next boundary (0 = at the tick)
    running_start_ms: f64,
    /// head-sample policy (shim): wait for a late sample this long, then reuse the last (false) or predict it (true)
    head_wait_ms: f64,
    predict_missing_head: bool,
    /// follow the client's display rate (see vision::RateEstimator)
    follow_rate: bool,
    /// compositor boundary = display tick + this offset
    boundary_offset_ms: f64,
    /// D3DKMT process scheduling class asked for (5 realtime .. 2; 0 = leave)
    gpu_sched_class: i32,
    /// frame generation (docs/FRAME_GENERATION.md): the game is paced at half the display rate and the host interpolates the frame in
    /// between from the optical flow of the last two rendered frames. None = settings file (`video.frame_gen`, default off); env
    /// VISIONALVR_FRAME_GEN=1/0 and `--frame-gen on|off` win over it.
    frame_gen: Option<bool>,
    /// the game's DLSS motion vectors (docs/MOTION_VECTORS.md): the shim copies them with every frame when the provider is loaded; settings `video.game_motion_vectors`
    game_motion_vectors: bool,
    /// frame generation rate (pipeline::FgRate: third | half | two_thirds | uncapped; docs/FRAME_GENERATION.md, docs/FRAME_GENERATION_UNCAPPED.md);
    /// None = settings `video.frame_gen_rate` ("half" default). `--frame-gen-rate`, env VISIONALVR_FG_RATE.
    fg_rate: Option<FgRate>,
    /// uncapped mode: content lag behind the newest tracking sample, ms; 0 = automatic (the game's frame period + half a display period)
    fg_lag_ms: Option<f64>,
    /// uncapped mode: fps cap on the game's releases (0 = none), headroom for the host's own GPU work
    fg_cap_fps: Option<f64>,
    fg_flow_scale: f64,
    fg_perf: i32,
    fg_grid: i32,
    /// scripted controller input for unattended runs (harness): (seconds after game start, hand, click mask, trigger)
    auto_input: Vec<(f64, usize, u32, f32)>,
    qp_map: Option<bool>,
    dump_qpmap: Option<PathBuf>,
    idle_rgb: Option<u32>,
    gpu_priority: Option<i64>,
    encode_profile: nvidia_profile::EncodeProfile,
    split_explicit: bool,
    profile_explicit: bool,
    install_dir: Option<PathBuf>,
    logs_dir: Option<PathBuf>,
    debug: Option<bool>,
    gui: bool,
    system_check: bool,
    /// run only the headset discovery for this many seconds (headset_seen events), then exit
    discover: Option<f64>,
    gamma: Option<f32>,
    /// headset height above the floor handed to games, metres (0 = the headset's own floor estimate). Games with the action
    /// on the floor by default (Moss, Titan Isles) assume a standing user; the Vision Pro's floor estimate is unreliable, so
    /// the launcher sets a plausible seated height (1.3 m) and the headset's own vertical movement is kept on top of it.
    height_m: f32,
    /// hand pose convention fix (shim, Oculus-emulation path): local rotation in degrees (XYZ Euler, left hand; right hand mirrors
    /// y and z like ALVR's controller offsets) and local position offset in metres (in the LibOVR frame, after the rotation).
    /// Default (derived 2026-10-13, see docs/progress.md): ALVR's hand motion is the OpenXR grip pose turned by its Quest 2 offset (-15 deg
    /// about X) with its +0.11 m controller offset behind the origin; VDXR's calibration says the LibOVR hand pose is the OpenXR aim frame
    /// and the grip is +60 deg about X from it, so LibOVR = ALVR * Rx(+15) * Rx(-60) = ALVR * Rx(-45 deg), position (0, -0.048, 0.038). The earlier
    /// 180 deg about Y turned the hands to face backwards in every game (OpenXR and LibOVR).
    hand_rotation_deg: [f32; 3],
    hand_position_m: [f32; 3],
    bench_network: Option<Vec<u32>>,
    bench_step_s: f64,
    noise_block: i64,
    noise_amp: i64,
    bench_mbps: Option<u64>,
    bench_quality: bool,
    bq_plan: String,
    bq_frames: i32,
    bq_warmup: i32,
    bq_metric_every: i32,
    bq_stride: i32,
    bq_out: Option<PathBuf>,
    bq_preview: Option<PathBuf>,
    bq_ref_scale: i32,
    bench_scene: Option<PathBuf>,
    /// simrig (cargo feature): run the pipeline without a headset against the synthetic client, results in this directory
    #[cfg(feature = "simrig")]
    simrig: Option<PathBuf>,
}

fn parse_args() -> Result<Args, String> {
    let mut a = Args {
        config_dir: PathBuf::from("host_config"),
        session: None,
        file: PathBuf::new(),
        fps: 90.0,
        seconds: 30.0,
        connect_timeout: 60.0,
        min_frames: 0,
        exit_after_frames: None,
        report: None,
        live: false,
        width: None,
        height: None,
        noise: 0,
        frames_csv: None,
        bench: None,
        bench_sleep_ms: 0,
        gpu_keepalive: None,
        shim: false,
        fps_explicit: false,
        daemon: false,
        force_session: false,
        // forced 3-strip split-frame encoding: NVENC's auto mode only splits frames >= ~2000 lines high (measured on an RTX
        // 5090, tools/nvenc_split_probe), so the foveated 4224x1664 frame stayed on one engine (6.9 ms vs 2.5 ms). NVENC uses
        // min(3, engines) strips, so this is also right on 1- and 2-engine GPUs.
        split_encode: 3,
        send_pacing: pipeline::SendPacing::Asap,
        running_start_ms: 2.0,
        head_wait_ms: 3.0,
        predict_missing_head: false,
        follow_rate: true,
        boundary_offset_ms: 0.0,
        gpu_sched_class: 5,
        frame_gen: None,
        game_motion_vectors: true,
        fg_rate: None,
        fg_lag_ms: None,
        fg_cap_fps: None,
        fg_flow_scale: 0.5,
        fg_perf: 20,
        fg_grid: 4,
        auto_input: vec![],
        send_pacing_explicit: false,
        qp_map: None,
        dump_qpmap: None,
        idle_rgb: None,
        gpu_priority: None,
        encode_profile: nvidia_profile::EncodeProfile::Session,
        split_explicit: false,
        profile_explicit: false,
        install_dir: None,
        logs_dir: None,
        debug: None,
        gui: false,
        system_check: false,
        discover: None,
        gamma: None,
        height_m: 1.3,
        hand_rotation_deg: [-45.0, 0.0, 0.0],
        hand_position_m: [0.0, -0.048, 0.038],
        bench_network: None,
        bench_step_s: 8.0,
        noise_block: 16,
        noise_amp: 48,
        bench_mbps: None,
        bench_quality: false,
        bq_plan: "full".into(),
        bq_frames: 90,
        bq_warmup: 20,
        bq_metric_every: 10,
        bq_stride: 1,
        bq_out: None,
        bq_preview: None,
        bq_ref_scale: 1,
        bench_scene: None,
        #[cfg(feature = "simrig")]
        simrig: None,
    };
    let mut it = std::env::args().skip(1);
    while let Some(k) = it.next() {
        let mut v = || it.next().ok_or(format!("missing value for {k}"));
        match k.as_str() {
            "--config-dir" => a.config_dir = v()?.into(),
            "--session" => a.session = Some(v()?.into()),
            "--file" => a.file = v()?.into(),
            "--fps" => {
                a.fps = v()?.parse().map_err(|e| format!("{e}"))?;
                a.fps_explicit = true;
            }
            "--seconds" => a.seconds = v()?.parse().map_err(|e| format!("{e}"))?,
            "--connect-timeout" => a.connect_timeout = v()?.parse().map_err(|e| format!("{e}"))?,
            "--min-frames" => a.min_frames = v()?.parse().map_err(|e| format!("{e}"))?,
            "--exit-after-frames" => a.exit_after_frames = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            "--report" => a.report = Some(v()?.into()),
            "--live" => a.live = true,
            "--width" => a.width = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            "--height" => a.height = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            "--noise" => a.noise = 1,
            "--noise-temporal" => a.noise = 2,
            // VisionALVR portable install: config/ and logs/ inside this folder, settings from config/visionalvr.json
            "--install-dir" => a.install_dir = Some(v()?.into()),
            "--logs-dir" => a.logs_dir = Some(v()?.into()),
            "--debug" => a.debug = Some(true),
            "--gui" => a.gui = true, // started by VisionALVR.exe: control commands on stdin, end of stdin = quit
            "--system-check" => a.system_check = true,
            "--discover" => a.discover = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            "--gamma" => a.gamma = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            "--height-m" => a.height_m = v()?.parse().map_err(|e| format!("{e}"))?,
            "--hand-rotation" | "--hand-position" => {
                let vals: Vec<f32> = v()?.split(',').map(|x| x.trim().parse::<f32>().map_err(|e| format!("{k}: {e}"))).collect::<Result<_, _>>()?;
                if vals.len() != 3 {
                    return Err(format!("{k}: expected x,y,z"));
                }
                if k == "--hand-rotation" { a.hand_rotation_deg = [vals[0], vals[1], vals[2]] } else { a.hand_position_m = [vals[0], vals[1], vals[2]] }
            }
            "--benchmark-network" => {
                a.bench_network = Some(v()?.split(',').map(|x| x.trim().parse::<u32>().map_err(|e| format!("--benchmark-network: {e}"))).collect::<Result<_, _>>()?);
                a.live = true;
                a.noise = 2;
            }
            "--bench-step-s" => a.bench_step_s = v()?.parse().map_err(|e| format!("{e}"))?,
            "--noise-block" => a.noise_block = v()?.parse().map_err(|e| format!("{e}"))?,
            "--noise-amp" => a.noise_amp = v()?.parse().map_err(|e| format!("{e}"))?,
            "--bench-mbps" => a.bench_mbps = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            // benchmark phase 2: local quality sweep on the benchmark scene (no headset; see bench_quality.rs)
            "--benchmark-quality" => a.bench_quality = true,
            "--bq-plan" => a.bq_plan = v()?,
            "--bq-frames" => a.bq_frames = v()?.parse().map_err(|e| format!("{e}"))?,
            "--bq-warmup" => a.bq_warmup = v()?.parse().map_err(|e| format!("{e}"))?,
            "--bq-metric-every" => a.bq_metric_every = v()?.parse().map_err(|e| format!("{e}"))?,
            "--bq-stride" => a.bq_stride = v()?.parse().map_err(|e| format!("{e}"))?,
            "--bq-out" => a.bq_out = Some(v()?.into()),
            "--bq-preview" => a.bq_preview = Some(v()?.into()),
            "--bq-ref-scale" => a.bq_ref_scale = v()?.parse().map_err(|e| format!("{e}"))?,
            #[cfg(feature = "simrig")]
            "--simrig" => {
                a.simrig = Some(v()?.into());
                a.shim = true;
                a.live = true;
            }
            "--bench-scene" => a.bench_scene = Some(v()?.into()),
            "--encode-profile" => {
                a.encode_profile = nvidia_profile::EncodeProfile::parse(&v()?)?;
                a.profile_explicit = true;
            }
            "--split-encode" => {
                // NVENC split-frame encoding across several NVENC engines (single session, single bitstream)
                a.split_explicit = true;
                a.split_encode = parse_split(&v()?)?;
            }
            "--send-pacing" => {
                a.send_pacing_explicit = true;
                a.send_pacing = pipeline::SendPacing::parse(&v()?)?;
            }
            "--qp-map" => {
                a.qp_map = Some(match v()?.as_str() {
                    "on" | "1" => true,
                    "off" | "0" => false,
                    o => return Err(format!("--qp-map on|off, got {o}")),
                })
            }
            "--dump-qpmap" => a.dump_qpmap = Some(v()?.into()),
            "--idle-rgb" => a.idle_rgb = Some(parse_rgb(&v()?)?),
            "--gpu-priority" => a.gpu_priority = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            "--running-start-ms" => a.running_start_ms = v()?.parse().map_err(|e| format!("{e}"))?,
            "--no-follow-rate" => a.follow_rate = false,
            "--head-wait-ms" => a.head_wait_ms = v()?.parse().map_err(|e| format!("{e}"))?,
            "--predict-missing-head" => a.predict_missing_head = true,
            "--boundary-offset-ms" => a.boundary_offset_ms = v()?.parse().map_err(|e| format!("{e}"))?,
            "--gpu-sched-class" => a.gpu_sched_class = v()?.parse().map_err(|e| format!("{e}"))?,
            "--frame-gen" => a.frame_gen = Some(parse_on_off("--frame-gen", &v()?)?),
            "--frame-gen-rate" => a.fg_rate = Some(FgRate::parse(&v()?).ok_or("--frame-gen-rate: third | half | two_thirds | uncapped")?),
            "--frame-gen-lag-ms" => a.fg_lag_ms = Some(v()?.parse::<f64>().map_err(|e| format!("{e}"))?.clamp(0.0, 100.0)),
            "--frame-gen-cap" => a.fg_cap_fps = Some(v()?.parse::<f64>().map_err(|e| format!("{e}"))?.clamp(0.0, 240.0)),
            "--game-motion-vectors" => a.game_motion_vectors = parse_on_off("--game-motion-vectors", &v()?)?,
            "--frame-gen-flow-scale" => a.fg_flow_scale = v()?.parse::<f64>().map_err(|e| format!("{e}"))?.clamp(0.1, 1.0),
            "--frame-gen-perf" => a.fg_perf = parse_fg_perf(&v()?)?,
            "--frame-gen-grid" => a.fg_grid = v()?.parse::<i32>().map_err(|e| format!("{e}"))?,
            "--auto-input" => {
                // "20:rA,26:rA,40:lT": at 20 s and 26 s after the game starts press A on the right controller, at 40 s the left
                // trigger. Buttons: A/X primary, B/Y secondary, S thumbstick, M menu, H system, T trigger.
                for item in v()?.split(',').filter(|x| !x.trim().is_empty()) {
                    let (t, rest) = item.trim().split_once(':').ok_or_else(|| format!("--auto-input: expected s:hB, got {item}"))?;
                    let t: f64 = t.parse().map_err(|e| format!("--auto-input time {t}: {e}"))?;
                    let mut ch = rest.chars();
                    let side = match ch.next() { Some('l') | Some('L') => 0, Some('r') | Some('R') => 1, _ => return Err(format!("--auto-input: hand l/r in {item}")) };
                    let (mut mask, mut trigger) = (0u32, 0f32);
                    for b in ch {
                        match b.to_ascii_uppercase() {
                            'A' | 'X' => mask |= 1,
                            'B' | 'Y' => mask |= 2,
                            'S' => mask |= 4,
                            'M' => mask |= 8,
                            'H' => mask |= 16,
                            'T' => trigger = 1.0,
                            o => return Err(format!("--auto-input: unknown button {o} in {item}")),
                        }
                    }
                    a.auto_input.push((t, side, mask, trigger));
                }
            }
            "--force-session" => a.force_session = true,
            "--daemon" => a.daemon = true, // run until stopped: no connect/stream time limits (reconnects are handled)
            "--shim" => {
                a.shim = true;
                a.live = true;
            }
            "--gpu-keepalive" => a.gpu_keepalive = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            "--bench-sleep-ms" => a.bench_sleep_ms = v()?.parse().map_err(|e| format!("{e}"))?,
            "--bench" => a.bench = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            "--frames-csv" => a.frames_csv = Some(v()?.into()),
            other => return Err(format!("unknown argument {other}")),
        }
    }
    // environment equivalents (the installed logon task has a fixed command line)
    if a.idle_rgb.is_none() {
        if let Ok(v) = std::env::var("VISIONALVR_IDLE_RGB") {
            a.idle_rgb = Some(parse_rgb(&v)?);
        }
    }
    if a.gpu_priority.is_none() {
        if let Ok(v) = std::env::var("VISIONALVR_GPU_PRIORITY") {
            a.gpu_priority = Some(v.parse().map_err(|e| format!("VISIONALVR_GPU_PRIORITY: {e}"))?);
        }
    }
    if !a.profile_explicit {
        if let Ok(v) = std::env::var("VISIONALVR_ENCODE_PROFILE") {
            a.encode_profile = nvidia_profile::EncodeProfile::parse(&v)?;
            a.profile_explicit = true;
        }
    }
    if a.frame_gen.is_none() {
        if let Ok(v) = std::env::var("VISIONALVR_FRAME_GEN") {
            a.frame_gen = Some(v != "0" && !v.eq_ignore_ascii_case("off") && !v.eq_ignore_ascii_case("false"));
        }
    }
    if a.fg_rate.is_none() {
        if let Ok(v) = std::env::var("VISIONALVR_FG_RATE") {
            a.fg_rate = FgRate::parse(&v);
        }
    }
    if a.fg_lag_ms.is_none() {
        a.fg_lag_ms = std::env::var("VISIONALVR_FG_LAG_MS").ok().and_then(|v| v.parse::<f64>().ok()).map(|v| v.clamp(0.0, 100.0));
    }
    if a.fg_cap_fps.is_none() {
        a.fg_cap_fps = std::env::var("VISIONALVR_FG_CAP_FPS").ok().and_then(|v| v.parse::<f64>().ok()).map(|v| v.clamp(0.0, 240.0));
    }
    if let Some(v) = std::env::var("VISIONALVR_FG_FLOW_SCALE").ok().and_then(|v| v.parse::<f64>().ok()) {
        a.fg_flow_scale = v.clamp(0.1, 1.0);
    }
    if let Some(v) = std::env::var("VISIONALVR_FG_PERF").ok().and_then(|v| parse_fg_perf(&v).ok()) {
        a.fg_perf = v;
    }
    if !a.live && a.file.as_os_str().is_empty() && !a.system_check && !a.bench_quality && a.discover.is_none() {
        return Err("--file <annexb.hevc> is required unless --live".into());
    }
    Ok(a)
}

/// The negotiated ALVR encoding gamma (persisted in `openvr_config` at connect). The client decodes with pow(v, gamma); the shim
/// sends pow(v, 1/gamma) (ALVR's SteamVR compositor does the same in FrameRender.fx). 1.0 if absent.
fn read_encoding_gamma(session_path: &std::path::Path) -> f32 {
    fs::read_to_string(session_path).ok().and_then(|t| serde_json::from_str::<serde_json::Value>(&t).ok())
        .and_then(|s| s["openvr_config"]["encoding_gamma"].as_f64()).filter(|g| *g > 0.1 && *g < 10.0).unwrap_or(1.0) as f32
}

fn parse_on_off(flag: &str, v: &str) -> Result<bool, String> {
    match v.to_ascii_lowercase().as_str() {
        "on" | "1" | "true" | "yes" => Ok(true),
        "off" | "0" | "false" | "no" => Ok(false),
        o => Err(format!("{flag} on|off, got {o}")),
    }
}

/// optical-flow engine preset: fast (default) | medium | slow (best quality), or the NV_OF_PERF_LEVEL number
fn parse_fg_perf(v: &str) -> Result<i32, String> {
    Ok(match v.to_ascii_lowercase().as_str() {
        "fast" => 20,
        "medium" => 10,
        "slow" => 5,
        o => match o.parse::<i32>() {
            Ok(n) if n == 5 || n == 10 || n == 20 => n,
            _ => return Err(format!("--frame-gen-perf fast|medium|slow, got {o}")),
        },
    })
}

fn parse_split(v: &str) -> Result<i64, String> {
    Ok(match v {
        "auto" => 0,
        "auto-forced" => 1,
        "2" | "two" => 2,
        "3" | "three" => 3,
        "off" => 15,
        o => return Err(format!("--split-encode auto|auto-forced|2|3|off, got {o}")),
    })
}

/// Fills what the command line left open from `config/visionalvr.json`, then applies the encode profile's implications.
fn apply_settings(a: &mut Args, st: &vision::Settings) {
    if !a.send_pacing_explicit {
        if let Some(p) = st.send_pacing.as_deref().and_then(|p| pipeline::SendPacing::parse(p).ok()) {
            a.send_pacing = p;
        }
    }
    if let Some(v) = st.running_start_ms {
        a.running_start_ms = v;
    }
    if let Some(v) = st.head_wait_ms {
        a.head_wait_ms = v;
    }
    if st.follow_client_rate == Some(false) {
        a.follow_rate = false;
    }
    if let Some(v) = st.predict_missing_head {
        a.predict_missing_head = a.predict_missing_head || v;
    }
    // frame-queue policy, read by the pipeline (an explicit environment variable wins)
    if let Some(v) = st.intake_depth {
        if std::env::var_os("VISIONALVR_INTAKE_DEPTH").is_none() {
            std::env::set_var("VISIONALVR_INTAKE_DEPTH", v.to_string());
        }
    }
    if let Some(v) = st.pick_policy.as_deref() {
        if std::env::var_os("VISIONALVR_PICK").is_none() {
            std::env::set_var("VISIONALVR_PICK", v);
        }
    }
    if let Some(v) = st.client_queue_drain {
        if std::env::var_os("VISIONALVR_CLIENT_DRAIN").is_none() {
            std::env::set_var("VISIONALVR_CLIENT_DRAIN", if v { "1" } else { "0" });
        }
    }
    if let Some(v) = st.intake_gate {
        if std::env::var_os("VISIONALVR_INTAKE_GATE").is_none() {
            std::env::set_var("VISIONALVR_INTAKE_GATE", if v { "1" } else { "0" });
        }
    }
    phase::configure(st.phase_lock.unwrap_or(true));
    if let Some(v) = st.boundary_offset_ms {
        a.boundary_offset_ms = v;
    }
    if a.frame_gen.is_none() {
        a.frame_gen = st.frame_gen;
    }
    if let Some(v) = st.game_motion_vectors {
        a.game_motion_vectors = v;
    }
    if a.fg_rate.is_none() {
        a.fg_rate = st.frame_gen_rate.as_deref().and_then(FgRate::parse);
    }
    if a.fg_lag_ms.is_none() {
        a.fg_lag_ms = st.frame_gen_lag_ms.map(|v| v.clamp(0.0, 100.0));
    }
    if a.fg_cap_fps.is_none() {
        a.fg_cap_fps = st.frame_gen_cap_fps.map(|v| v.clamp(0.0, 240.0));
    }
    if let Some(v) = st.frame_gen_flow_scale {
        a.fg_flow_scale = v.clamp(0.1, 1.0);
    }
    if let Some(v) = st.frame_gen_perf.as_deref().and_then(|v| parse_fg_perf(v).ok()) {
        a.fg_perf = v;
    }
    if let Some(v) = st.gpu_sched_class {
        a.gpu_sched_class = v as i32;
    }
    if a.idle_rgb.is_none() {
        a.idle_rgb = st.idle_rgb;
    }
    if a.gpu_priority.is_none() {
        a.gpu_priority = st.gpu_priority;
    }
    if a.qp_map.is_none() {
        a.qp_map = st.qp_map;
    }
    if !a.split_explicit {
        if let Some(m) = st.split_encode.as_deref().and_then(|m| parse_split(m).ok()) {
            a.split_encode = m;
            a.split_explicit = true;
        }
    }
    if !a.profile_explicit {
        if let Some(p) = st.encode_profile.as_deref().and_then(|p| nvidia_profile::EncodeProfile::parse(p).ok()) {
            a.encode_profile = p;
        }
    }
    if a.debug.is_none() {
        a.debug = st.debug;
    }
    if a.gamma.is_none() {
        a.gamma = st.gamma;
    }
    if let Some(h) = st.height_m {
        a.height_m = h;
    }
    if let Some(r) = st.hand_rotation_deg {
        a.hand_rotation_deg = r;
    }
    if let Some(p) = st.hand_position_m {
        a.hand_position_m = p;
    }
    if a.encode_profile == nvidia_profile::EncodeProfile::FullSplit {
        // whole frame, straight to the encoder engines: no QP map, 3 strips (explicit --qp-map / --split-encode still win)
        if a.qp_map.is_none() {
            a.qp_map = Some(false);
        }
        if !a.split_explicit {
            a.split_encode = 3;
        }
    }
}

/// "r,g,b" (0-255 each) or "RRGGBB" -> 0xRRGGBB
fn parse_rgb(v: &str) -> Result<u32, String> {
    let parts: Vec<&str> = v.split(',').map(str::trim).collect();
    match parts.as_slice() {
        [r, g, b] => {
            let c = |x: &str| x.parse::<u8>().map(u32::from).map_err(|e| format!("--idle-rgb {v}: {e}"));
            Ok((c(r)? << 16) | (c(g)? << 8) | c(b)?)
        }
        [hex] if hex.len() == 6 => u32::from_str_radix(hex, 16).map_err(|e| format!("--idle-rgb {v}: {e}")),
        _ => Err(format!("--idle-rgb r,g,b or RRGGBB, got {v}")),
    }
}

/// Logs what NVENC was actually configured with (negotiated: preset, rate control, split-frame conditions and engine count,
/// capabilities, QP map) and optionally dumps the QP map. Returns the description for the final report.
fn describe_encoder(origin: Instant, enc: *mut std::ffi::c_void, dump: &Option<PathBuf>) -> serde_json::Value {
    let mut buf = vec![0 as std::ffi::c_char; 8192];
    let n = unsafe { nvh::nvh_describe(enc, buf.as_mut_ptr(), buf.len() as i32) };
    let mut v = json!(null);
    if n > 0 {
        let s = unsafe { std::ffi::CStr::from_ptr(buf.as_ptr()) }.to_string_lossy().to_string();
        v = serde_json::from_str(&s).unwrap_or_else(|e| json!({ "raw": s, "parse_error": e.to_string() }));
    }
    event(origin, "nvenc_config", v.clone());
    if let Some(p) = dump {
        if let Ok(c) = std::ffi::CString::new(p.to_string_lossy().as_bytes()) {
            let ok = unsafe { nvh::nvh_qpmap_dump(c.as_ptr()) };
            event(origin, "qpmap_dump", json!({ "path": p, "written": ok }));
        }
    }
    v
}

/// One `encoder_stats` event per window: encode time, frame count, bitrate, slices per frame (>1 = split-frame encode in effect).
struct StatsWindow {
    start: Instant,
    first_idx: usize,
    bytes0: u64,
    samples0: usize,
    slices0: usize,
    matched0: u32,
    fallback0: u32,
    ticks0: u64,
}

/// display-clock ticks so far (mirror of the vsync thread's counter, for the stats windows)
static VSYNC_TICKS: AtomicU64 = AtomicU64::new(0);
/// Pacing diagnostics, reported per `encoder_stats` window. Ticks are nanoseconds since the host's `origin` instant.
static LAST_TICK_NS: AtomicU64 = AtomicU64::new(0);
static NEXT_TICK_NS: AtomicU64 = AtomicU64::new(0);
/// the vsync thread runs its own grid at the client's detected display period instead of ALVR's nominal one
static FOLLOW_RATE: AtomicBool = AtomicBool::new(false);
/// app frames sent with the same client timestamp as the previous frame (the headset treats them as already shown)
static SAME_TS_FRAMES: AtomicU64 = AtomicU64::new(0);
/// send instant relative to the display-clock tick (asap: render + encode time; vsync pacing: lateness after the tick)
static SEND_PHASE_MS: std::sync::Mutex<Vec<f64>> = std::sync::Mutex::new(Vec::new());
/// time an encoded frame waited for the tick (vsync pacing only; 0 = the frame was already late)
static HOLD_MS: std::sync::Mutex<Vec<f64>> = std::sync::Mutex::new(Vec::new());
/// spacing of the headset's tracking packets arriving at the host
static TRACKING_GAP_MS: std::sync::Mutex<Vec<f64>> = std::sync::Mutex::new(Vec::new());
/// the last statistics window's tracking spacing (p95 ms, max ms, gaps over 15 ms, gaps) for the frame generation perf line
/// the client's tracking cadence was judged not to be a display rate (logged once per connection)
static RATE_IGNORED: AtomicBool = AtomicBool::new(false);
static TRACKING_GAP_LAST: Mutex<(f64, f64, u64, u64)> = Mutex::new((0.0, 0.0, 0, 0));
static LAST_TRACKING_NS: AtomicU64 = AtomicU64::new(0);

fn push_bounded(m: &std::sync::Mutex<Vec<f64>>, v: f64) {
    if let Ok(mut g) = m.lock() {
        if g.len() < 8192 {
            g.push(v);
        }
    }
}

/// p50 / p95 / max / count of a sample set (plus `over_<x>ms` counts), for the stats windows
fn pct_json(v: &mut Vec<f64>, over: &[(&str, f64)]) -> serde_json::Value {
    v.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let q = |p: f64| v.get(((v.len() as f64) * p) as usize).copied().unwrap_or(0.0);
    let mut e = json!({ "p50": q(0.5), "p95": q(0.95), "max": v.last().copied().unwrap_or(0.0), "n": v.len() });
    for (name, thr) in over {
        e[*name] = json!(v.iter().filter(|x| **x > *thr).count());
    }
    e
}

/// Waits until `t` (high-resolution timer, then a short spin: timing.rs).
fn sleep_spin_until(t: Instant) {
    timing::sleep_until(t);
}

impl StatsWindow {
    fn new() -> Self {
        Self { start: Instant::now(), first_idx: 0, bytes0: 0, samples0: 0, slices0: 0, matched0: 0, fallback0: 0, ticks0: 0 }
    }
    fn tick(&mut self, origin: Instant, encode_ms: &[f64], bytes: u64, slices: Option<(usize, usize)>, shim: Option<(u32, u32, u32)>, intake_ms: &[f64], ffr_wait_ms: &[f64], extra: Option<serde_json::Value>) {
        if self.start.elapsed() < Duration::from_secs(2) {
            return;
        }
        let secs = self.start.elapsed().as_secs_f64();
        let mut w = encode_ms[self.first_idx.min(encode_ms.len())..].to_vec();
        w.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let q = |p: f64| w.get(((w.len() as f64) * p) as usize).copied().unwrap_or(0.0);
        let (samples, slices) = slices.unwrap_or((0, 0));
        let (submit_mode, matched, fallback) = shim.unwrap_or((0, 0, 0));
        let mut e = json!({
            "frames": w.len(), "fps": w.len() as f64 / secs, "encode_ms_avg": w.iter().sum::<f64>() / w.len().max(1) as f64,
            "encode_ms_p95": q(0.95), "encode_ms_max": w.last().copied().unwrap_or(0.0),
            "mbps": (bytes - self.bytes0) as f64 * 8.0 / secs / 1e6,
            "slices_per_packet": (slices - self.slices0) as f64 / (samples - self.samples0).max(1) as f64,
        });
        if ffr_wait_ms.len() >= w.len() && !w.is_empty() {
            // encode time split: queued foveation pass done on the GPU (waits behind the game's GPU work) vs. copy + NVENC
            let n = w.len();
            let mut fw = ffr_wait_ms[ffr_wait_ms.len() - n..].to_vec();
            let rest: Vec<f64> = encode_ms[encode_ms.len() - n..].iter().zip(&fw).map(|(e, f)| (e - f).max(0.0)).collect();
            let mut rs = rest.clone();
            fw.sort_by(|a, b| a.partial_cmp(b).unwrap());
            rs.sort_by(|a, b| a.partial_cmp(b).unwrap());
            let p = |v: &Vec<f64>, q: f64| v.get(((v.len() as f64) * q) as usize).copied().unwrap_or(0.0);
            // GPU time of ALVR's foveation pass (timestamp queries); the rest of the encode time is queueing + copy + NVENC
            e["ffr_gpu_ms"] = json!({ "avg": fw.iter().sum::<f64>() / n as f64, "p95": p(&fw, 0.95) });
            e["encode_minus_ffr_ms"] = json!({ "avg": rs.iter().sum::<f64>() / n as f64, "p95": p(&rs, 0.95) });
        }
        if !intake_ms.is_empty() {
            // spacing of the app's frames arriving at the host in this window: the app's real frame rate
            let mut v = intake_ms[intake_ms.len().saturating_sub(w.len())..].to_vec();
            v.sort_by(|a, b| a.partial_cmp(b).unwrap());
            let p = |q: f64| v.get(((v.len() as f64) * q) as usize).copied().unwrap_or(0.0);
            e["app_frame_interval_ms"] = json!({ "p50": p(0.5), "p95": p(0.95), "p99": p(0.99), "max": v.last().copied().unwrap_or(0.0),
                "over_16_7ms": v.iter().filter(|x| **x > 16.7).count() }); // > 1.5 periods at 90 Hz: a missed frame
        }
        if shim.is_some() {
            // how the shim chose frame timestamps in this window (pose match vs. last pose read), and its submit mode
            e["vsync_ticks"] = json!(VSYNC_TICKS.load(Ordering::Relaxed).wrapping_sub(self.ticks0));
            e["shim"] = json!({ "submit_mode": if submit_mode == 1 { "async" } else { "sync" },
                "ts_matched": matched.wrapping_sub(self.matched0), "ts_fallback": fallback.wrapping_sub(self.fallback0) });
        }
        if let Some(c) = logs::client_stats::take(0) {
            e["client"] = c; // ALVR's client statistics over the window: total / encode / network / decode latency, client fps
        }
        // pacing diagnostics (moving-head judder investigation): duplicate timestamps, tracking spacing, send phase
        e["same_ts_frames"] = json!(SAME_TS_FRAMES.swap(0, Ordering::Relaxed));
        if let Ok(mut g) = TRACKING_GAP_MS.lock() {
            if !g.is_empty() {
                let mut v = std::mem::take(&mut *g);
                let j = pct_json(&mut v, &[("over_12ms", 12.0), ("over_15ms", 15.0)]);
                *TRACKING_GAP_LAST.lock() = (j["p95"].as_f64().unwrap_or(0.0), j["max"].as_f64().unwrap_or(0.0), j["over_15ms"].as_u64().unwrap_or(0), j["n"].as_u64().unwrap_or(0));
                e["tracking_gap_ms"] = j;
            }
        }
        if let Ok(mut g) = SEND_PHASE_MS.lock() {
            if !g.is_empty() {
                let mut v = std::mem::take(&mut *g);
                e["send_after_tick_ms"] = pct_json(&mut v, &[("over_11ms", 11.0)]);
            }
        }
        if let Ok(mut g) = HOLD_MS.lock() {
            if !g.is_empty() {
                let mut v = std::mem::take(&mut *g);
                let late = v.iter().filter(|x| **x <= 0.0).count();
                e["send_hold_ms"] = pct_json(&mut v, &[]);
                e["send_hold_ms"]["late_frames"] = json!(late);
            }
        }
        if let Some(x) = extra {
            e["pipeline"] = x;
        }
        event(origin, "encoder_stats", e);
        *self = Self { start: Instant::now(), first_idx: encode_ms.len(), bytes0: bytes, samples0: samples, slices0: slices, matched0: matched, fallback0: fallback, ticks0: VSYNC_TICKS.load(Ordering::Relaxed) };
    }
}

pub(crate) fn event(origin: Instant, name: &str, extra: serde_json::Value) {
    let wall = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs_f64()).unwrap_or(0.0);
    let line = json!({ "t_ms": origin.elapsed().as_secs_f64() * 1000.0, "unix_s": wall, "event": name, "data": extra }).to_string();
    println!("{line}");
    logs::event_line(&line);
}

// ---- HEVC Annex-B access-unit splitter ------------------------------------------------------------------

struct AccessUnit {
    data: Vec<u8>,
    is_idr: bool,
}

/// Position of the next Annex-B start code at or after `from` (a preceding zero byte counts as part of it: 4-byte code).
/// Skips 3 bytes whenever the third byte of the window rules out a start code there (packets are ~0.3-3 MB, scanned on the
/// encode thread before the frame goes to the network).
fn next_start(buf: &[u8], from: usize) -> Option<usize> {
    let mut i = from;
    while i + 3 <= buf.len() {
        let b2 = buf[i + 2];
        if b2 > 1 {
            i += 3;
        } else if b2 == 1 && buf[i] == 0 && buf[i + 1] == 0 {
            return Some(if i > 0 && buf[i - 1] == 0 { i - 1 } else { i });
        } else {
            i += 1;
        }
    }
    None
}

/// NAL unit type of the NAL whose start code begins at `s`.
fn nal_type(buf: &[u8], s: usize) -> Option<u8> {
    let sc = if buf[s..].starts_with(&[0, 0, 0]) { 4 } else { 3 };
    buf.get(s + sc).map(|h| (h >> 1) & 0x3f)
}

fn nal_ranges(buf: &[u8]) -> Vec<(usize, usize)> {
    // (start of start-code, end) for every NAL
    let mut starts = vec![];
    let mut i = 0;
    while let Some(s) = next_start(buf, i) {
        starts.push(s);
        i = s + 3;
    }
    (0..starts.len()).map(|k| (starts[k], *starts.get(k + 1).unwrap_or(&buf.len()))).collect()
}

/// VCL NAL units (slices) in an access unit.
fn count_vcl(buf: &[u8]) -> usize {
    let (mut n, mut i) = (0, 0);
    while let Some(s) = next_start(buf, i) {
        n += nal_type(buf, s).is_some_and(|t| t < 32) as usize;
        i = s + 3;
    }
    n
}

fn split_access_units(buf: &[u8]) -> Vec<AccessUnit> {
    let mut aus: Vec<AccessUnit> = vec![];
    let mut cur: Vec<u8> = vec![];
    let (mut cur_has_vcl, mut cur_idr) = (false, false);
    for (s, e) in nal_ranges(buf) {
        let sc = if buf[s] == 0 && buf[s + 1] == 0 && buf[s + 2] == 0 { 4 } else { 3 };
        let hdr = buf[s + sc];
        let nal_type = (hdr >> 1) & 0x3f;
        let is_vcl = nal_type < 32;
        let first_slice = is_vcl && buf.get(s + sc + 2).is_some_and(|b| b & 0x80 != 0);
        let starts_au = matches!(nal_type, 32..=35 | 39) || first_slice;
        if starts_au && cur_has_vcl {
            aus.push(AccessUnit { data: std::mem::take(&mut cur), is_idr: cur_idr });
            cur_has_vcl = false;
            cur_idr = false;
        }
        if is_vcl {
            cur_has_vcl = true;
            cur_idr |= matches!(nal_type, 19 | 20);
        }
        cur.extend_from_slice(&buf[s..e]);
    }
    if cur_has_vcl {
        aus.push(AccessUnit { data: cur, is_idr: cur_idr });
    }
    aus
}

/// Mirrors ALVR NalParsing.cpp: if the packet starts with VPS, the first 3 NALs (VPS, SPS, PPS) are the
/// decoder config and only the remainder is sent as the frame.
fn split_config(au: &[u8]) -> (Option<&[u8]>, &[u8]) {
    // only the first 4 NALs matter: no full scan of the frame
    let Some(mut s) = next_start(au, 0) else { return (None, au) };
    if nal_type(au, s) != Some(32) {
        return (None, au);
    }
    for _ in 0..3 {
        match next_start(au, s + 3) {
            Some(n) => s = n,
            None => return (None, au), // fewer than 4 NALs
        }
    }
    (Some(&au[..s]), &au[s..])
}

// ---- live NVENC (C++ wrapper around the unmodified ALVR encoder, see nvenc/hostlib) ---------------------------

mod nvh {
    use std::ffi::{c_char, c_void};
    pub type PacketCb = extern "C" fn(user: *mut c_void, data: *const u8, len: i32, ts_ns: u64, is_idr: i32);
    pub type ParamsCb = extern "C" fn(user: *mut c_void, bitrate_bps: *mut u64, framerate: *mut f32) -> i32;
    extern "C" {
        pub fn nvh_set(key: *const c_char, value: i64) -> i32;
        pub fn nvh_create(w: i32, h: i32, noise: i32, p: PacketCb, q: ParamsCb, user: *mut c_void, err: *mut c_char, errlen: i32) -> *mut c_void;
        pub fn nvh_encode(h: *mut c_void, idx: u32, ts_ns: u64, force_idr: i32, err: *mut c_char, errlen: i32) -> f64;
        pub fn nvh_destroy(h: *mut c_void);
        pub fn nvh_keepalive(h: *mut c_void, on: i32, duty_pct: i32) -> i32;
        pub fn nvh_set_f(key: *const c_char, value: f64) -> i32;
        pub fn nvh_encoded_size(w: *mut i32, h: *mut i32);
        pub fn nvh_encoder_engines(h: *mut c_void) -> i32;
        pub fn nvh_describe(h: *mut c_void, buf: *mut std::ffi::c_char, len: i32) -> i32;
        pub fn nvh_qpmap_dump(path: *const std::ffi::c_char) -> i32;
        pub fn nvh_ipc_last_compose_ms(ipc: *mut c_void) -> f32;
        pub fn nvh_last_ffr_gpu_ms(h: *mut c_void) -> f32;
        pub fn nvh_ipc_open() -> *mut c_void;
        pub fn nvh_ipc_config(ipc: *mut c_void, nvh: *mut c_void, rate_hz: f64, eye_w: i32, eye_h: i32, fov8: *const f32, off6: *const f32, bits: i32, encoding_gamma: f32);
        pub fn nvh_last_ffr_wait_ms(h: *mut c_void) -> f32;
        pub fn nvh_ipc_set_connected(ipc: *mut c_void, connected: i32);
        pub fn nvh_ipc_publish_head(ipc: *mut c_void, ts: u64, q: *const f32, p: *const f32, lv: *const f32, av: *const f32);
        pub fn nvh_ipc_publish_hand(ipc: *mut c_void, side: i32, valid: i32, q: *const f32, p: *const f32, lv: *const f32, av: *const f32);
        pub fn nvh_ipc_vsync(ipc: *mut c_void, interval_s: f64);
        pub fn nvh_ipc_publish_input(ipc: *mut c_void, side: i32, clicks: u32, touches: u32, trigger: f32, squeeze: f32, sx: f32, sy: f32);
        pub fn nvh_ipc_poll_haptic(ipc: *mut c_void, side: i32, seq: *mut u32, freq: *mut f32, amp: *mut f32);
        pub fn nvh_ipc_wait_frame(ipc: *mut c_void, timeout_ms: i32, counter: *mut u64, slot: *mut u32, ts: *mut u64, submit_time_s: *mut f64, render_q8: *mut f32, has_projection: *mut i32, display_s: *mut f64, release_s: *mut f64) -> i32;
        pub fn nvh_fg_enable(nvh: *mut c_void, on: i32, flow_scale: f64, perf_level: i32, grid: i32, err: *mut c_char, errlen: i32) -> i32;
        pub fn nvh_fg_ingest(nvh: *mut c_void, ipc: *mut c_void, slot: u32, q_render8: *const f32, err: *mut c_char, errlen: i32) -> i32;
        pub fn nvh_fg_reset(nvh: *mut c_void);
        pub fn nvh_fg_encode(nvh: *mut c_void, ipc: *mut c_void, mode: i32, t: f64, age: i32, q_out4: *const f32, frame_no: u32, ts_ns: u64, force_idr: i32, err: *mut c_char, errlen: i32) -> f64;
        pub fn nvh_fg_stats(nvh: *mut c_void, buf: *mut c_char, len: i32) -> i32;
        pub fn nvh_ipc_set_app_period_mul(ipc: *mut c_void, mul: u32);
        pub fn nvh_ipc_set_game_mv(ipc: *mut c_void, on: u32);
        pub fn nvh_ipc_set_app_min_period(ipc: *mut c_void, min_period_us: u32);
        pub fn nvh_fg_use_mv(nvh: *mut c_void, on: i32);
        pub fn nvh_ipc_motion_tap(ipc: *mut c_void, tap: *mut u32, streams: *mut u32, publications: *mut u64);
        pub fn nvh_qpc_seconds() -> f64;
        pub fn nvh_encode_green(nvh: *mut c_void, frame_no: u32, ts_ns: u64, force_idr: i32, err: *mut c_char, errlen: i32) -> f64;
        pub fn nvh_ipc_shim_alive(ipc: *mut c_void) -> i32;
        pub fn nvh_encode_shared(nvh: *mut c_void, ipc: *mut c_void, slot: u32, frame_no: u32, ts_ns: u64, force_idr: i32, err: *mut c_char, errlen: i32) -> f64;
        pub fn nvh_ipc_close(ipc: *mut c_void);
        pub fn nvh_release_shared(nvh: *mut c_void);
        pub fn nvh_ipc_set_user(ipc: *mut c_void, user_gamma: f32, debug_on: i32, debug_dir: *const u16);
        pub fn nvh_ipc_app_exe(ipc: *mut c_void, buf: *mut c_char, len: i32) -> i32;
        pub fn nvh_ipc_set_color(ipc: *mut c_void, brightness: f32, contrast: f32, saturation: f32, sharpening: f32);
        pub fn nvh_gpu_info(buf: *mut c_char, len: i32) -> i32;
        pub fn nvh_gpu_sample(buf: *mut c_char, len: i32) -> i32;
        pub fn nvh_ipc_shim_stats(ipc: *mut c_void, submit_mode: *mut u32, ts_matched: *mut u32, ts_fallback: *mut u32);
        pub fn nvh_ipc_slot_busy(ipc: *mut c_void, slot: u32, busy: i32);
        pub fn nvh_ipc_set_pacing(ipc: *mut c_void, boundary_offset_ms: f32, running_start_ms: f32);
        pub fn nvh_ipc_set_head_policy(ipc: *mut c_void, wait_ms: f32, predict: f32);
        pub fn nvh_ipc_set_eye_height(ipc: *mut c_void, eye_height_m: f32);
        pub fn nvh_ipc_set_hand_offset(ipc: *mut c_void, side: i32, quat_xyzw: *const f32, pos: *const f32);
        pub fn nvh_ipc_shim_pacing(ipc: *mut c_void, slot_busy_waits: *mut u32, app_frame_ms: *mut f32, releases: *mut u64, releases_late: *mut u64,
                                   gate_holds: *mut u64, gate_suppressed: *mut u64);
        pub fn nvh_ipc_set_intake(ipc: *mut c_void, waiting: u32);
        pub fn nvh_ipc_set_intake_gate(ipc: *mut c_void, on: u32);
        pub fn nvh_set_gpu_scheduling(cls: i32) -> i32;
    }
}

/// Per-hand input state in the Touch layout (see ovrshim/ipc.h).
#[derive(Default, Clone, Copy)]
struct HandInputState {
    clicks: u32,
    touches: u32,
    trigger: f32,
    squeeze: f32,
    stick_x: f32,
    stick_y: f32,
}

impl HandInputState {
    fn apply(&mut self, input: &str, value: &ButtonValue) {
        let on = match value {
            ButtonValue::Binary(b) => *b,
            ButtonValue::Scalar(v) => *v > 0.5,
        };
        let v = match value {
            ButtonValue::Scalar(v) => *v,
            ButtonValue::Binary(b) => *b as u32 as f32,
        };
        let set = |mask: &mut u32, bit: u32| {
            if on {
                *mask |= bit
            } else {
                *mask &= !bit
            }
        };
        match input {
            "a/click" | "x/click" => set(&mut self.clicks, 1),
            "b/click" | "y/click" => set(&mut self.clicks, 2),
            "thumbstick/click" => set(&mut self.clicks, 4),
            "menu/click" => set(&mut self.clicks, 8),
            "system/click" => set(&mut self.clicks, 16),
            "a/touch" | "x/touch" => set(&mut self.touches, 1),
            "b/touch" | "y/touch" => set(&mut self.touches, 2),
            "thumbstick/touch" => set(&mut self.touches, 4),
            "trigger/touch" => set(&mut self.touches, 8),
            "thumbrest/touch" => set(&mut self.touches, 16),
            "trigger/value" => self.trigger = v,
            "squeeze/value" => self.squeeze = v,
            "thumbstick/x" => self.stick_x = v,
            "thumbstick/y" => self.stick_y = v,
            _ => {}
        }
    }
}

pub(crate) struct LiveCtx {
    ctx: Arc<ServerCoreContext>,
    shared: Arc<Shared>,
    packet_bytes: usize,
    packet_idr: bool,
    packets: usize,
    slices: usize,           // VCL NAL units in the sampled packets (slices per frame > 1 means NVENC split the frame across engines)
    slice_samples: usize,    // packets whose slices were counted (every 8th: it is only a statistic)
    report_in_callback: bool, // false in shim mode: composed/present are reported with real timings before encoding
    cb_ns: u128,     // time spent inside on_packet (server_core hand-off)
    params_ns: u128, // time spent inside on_params
    bitrate_override: u64, // network benchmark: this bitrate instead of server_core's (0 = server_core decides)
    bytes_total: u64,
    bitrate_sent: u64,
    fps: f32,
    /// vsync send pacing: on_packet queues the encoded frame in `pending` while this is set; the main loop sends it at the tick
    hold_now: bool,
    pending: Vec<(Duration, Vec<u8>, bool, Option<Vec<u8>>)>,
}

extern "C" fn on_packet(user: *mut std::ffi::c_void, data: *const u8, len: i32, ts_ns: u64, is_idr: i32) {
    let t0 = Instant::now();
    let live = unsafe { &mut *(user as *mut LiveCtx) };
    let buf = unsafe { std::slice::from_raw_parts(data, len as usize) };
    let ts = Duration::from_nanos(ts_ns);
    let (config, frame) = split_config(buf);
    if config.is_some() {
        live.shared.config_sent.fetch_add(1, Ordering::Relaxed);
    }
    if live.hold_now {
        live.pending.push((ts, frame.to_vec(), is_idr != 0, config.map(|c| c.to_vec())));
    } else {
        if let Some(c) = config {
            live.ctx.set_video_config_nals(c.to_vec(), CodecType::Hevc);
        }
        live.ctx.send_video_nal(ts, frame.to_vec(), is_idr != 0);
    }
    if live.report_in_callback {
        live.ctx.report_composed(ts, Duration::from_millis(1));
        live.ctx.report_present(ts, Duration::from_millis(1));
    }
    if live.packets % 8 == 0 {
        live.slices += count_vcl(buf);
        live.slice_samples += 1;
    }
    live.packet_bytes += frame.len();
    live.bytes_total += buf.len() as u64;
    live.packet_idr |= is_idr != 0;
    live.packets += 1;
    live.cb_ns += t0.elapsed().as_nanos();
}

extern "C" fn on_params(user: *mut std::ffi::c_void, bitrate: *mut u64, fps: *mut f32) -> i32 {
    let t0 = Instant::now();
    let live = unsafe { &mut *(user as *mut LiveCtx) };
    if live.bitrate_override != 0 {
        if live.bitrate_override == live.bitrate_sent {
            return 0;
        }
        live.bitrate_sent = live.bitrate_override;
        unsafe {
            *bitrate = live.bitrate_override;
            *fps = live.fps;
        }
        return 1;
    }
    let r = live.ctx.get_dynamic_encoder_params();
    live.params_ns += t0.elapsed().as_nanos();
    match r {
        Some(p) => unsafe {
            *bitrate = p.bitrate_bps as u64;
            *fps = p.framerate;
            1
        },
        None => 0,
    }
}

/// Pushes the encoder configuration to the NVENC code: the encoder parameters are the hardcoded NVIDIA profile, the stream
/// parameters (refresh rate, eye resolution, foveation) come from the session's `openvr_config` as before.
fn configure_encoder(session_path: &std::path::Path, split_mode: i64, qp_map: bool, host_keys: &[(&std::ffi::CStr, i64)]) -> (i32, i32, f64) {
    let s: serde_json::Value = serde_json::from_str(&fs::read_to_string(session_path).expect("read session")).expect("parse session");
    let c = &s["openvr_config"];
    unsafe {
        nvh::nvh_set(c"nvenc_split_encode_mode".as_ptr(), split_mode);
        nvh::nvh_set(c"nvenc_qp_map".as_ptr(), qp_map as i64);
        nvh::nvh_set(c"qp_map_center_delta".as_ptr(), nvidia_profile::QP_MAP_CENTER_DELTA);
        nvh::nvh_set(c"qp_map_edge_delta".as_ptr(), nvidia_profile::QP_MAP_EDGE_DELTA);
        nvh::nvh_set_f(c"qp_map_transition".as_ptr(), nvidia_profile::QP_MAP_TRANSITION);
        for (k, v) in host_keys {
            nvh::nvh_set(k.as_ptr(), *v);
        }
    }
    for (k, n) in nvidia_profile::NVH_KEYS {
        let ck = std::ffi::CString::new(*k).unwrap();
        unsafe { nvh::nvh_set(ck.as_ptr(), *n) };
    }
    let fset = &s["session_settings"]["video"]["foveated_encoding"]["content"];
    for (k, sk) in [
        ("foveation_center_size_x", "center_size_x"), ("foveation_center_size_y", "center_size_y"),
        ("foveation_center_shift_x", "center_shift_x"), ("foveation_center_shift_y", "center_shift_y"),
        ("foveation_edge_ratio_x", "edge_ratio_x"), ("foveation_edge_ratio_y", "edge_ratio_y"),
    ] {
        // ALVR writes zeros to openvr_config when foveation is off (and its FFR code divides by them): then use the geometry from the
        // session's foveation settings (still the user's values; the QP map follows them even without the warp)
        if let Some(v) = c[k].as_f64().filter(|v| *v > 0.0).or_else(|| fset[sk].as_f64().filter(|v| *v > 0.0)) {
            let ck = std::ffi::CString::new(k).unwrap();
            unsafe { nvh::nvh_set_f(ck.as_ptr(), v) };
        }
    }
    for k in ["refresh_rate", "enable_foveated_encoding", "eye_resolution_width", "eye_resolution_height"] {
        let v = &c[k];
        if let Some(n) = v.as_i64().or_else(|| v.as_bool().map(|b| b as i64)) {
            let ck = std::ffi::CString::new(k).unwrap();
            unsafe { nvh::nvh_set(ck.as_ptr(), n) };
        }
    }
    let w = c["eye_resolution_width"].as_i64().unwrap_or(1920) as i32 * 2;
    let h = c["eye_resolution_height"].as_i64().unwrap_or(1080) as i32;
    (w, h, c["refresh_rate"].as_f64().unwrap_or(90.0))
}

/// Exit code for a setup problem the user has to fix (missing or broken config); restarting the streamer cannot help.
const EXIT_SETUP: i32 = 7;

/// Reports a setup problem in plain words (stderr, the general log, a `setup_error` event the GUIs show) and exits.
fn setup_fail(origin: Instant, message: &str) -> ! {
    eprintln!("{message}");
    logs::general("ERROR", message);
    event(origin, "setup_error", json!({ "message": message }));
    std::process::exit(EXIT_SETUP);
}

/// The session ALVR, the encoder and the benchmarks read: present and valid JSON, else a message that says what to do.
fn check_session(path: &std::path::Path, installed: bool) -> Result<(), String> {
    let text = match fs::read_to_string(path) {
        Ok(t) => t,
        Err(_) if installed => {
            return Err("config\\session.json is missing and there is no config\\session.default.json to create it from: this \
                        VisionALVR folder is incomplete. Unzip the release again (you can keep your config\\ folder)."
                .into())
        }
        Err(e) => return Err(format!("cannot read the session {}: {e} (pass --session <file> or --install-dir <VisionALVR folder>)", path.display())),
    };
    match serde_json::from_str::<serde_json::Value>(&text) {
        Ok(v) if v.is_object() => Ok(()),
        Ok(_) => Err(format!("{} is not an ALVR session (expected a JSON object)", path.display())),
        Err(e) => Err(format!(
            "{} is not valid JSON ({e}). Fix the edit, or delete the file to start again from config\\session.default.json \
             (your headset pairing is stored in it: pair again with configure.exe).",
            path.display()
        )),
    }
}

/// Vision Pro views measured in the pilot (visionalvr_views.json): used until the headset has sent its own.
const AVP_DEFAULT_FOV: [f32; 8] = [1.7823, 1.0186, 1.2169, 1.0186, 1.0249, 1.7995, 1.2253, 1.0249];
const AVP_DEFAULT_IPD: f32 = 0.0623;

/// `--benchmark-quality`: benchmark phase 2 (bench_quality.rs) from the session's stream settings, the headset's last views and the
/// bitrate (`--bench-mbps`, else the session's constant bitrate). Results: `bq_*` events, `--bq-out` JSON, a general log line.
fn run_quality_bench(origin: Instant, args: &Args, layout: &alvr_filesystem::Layout, install: Option<&vision::Install>) -> i32 {
    let session = layout.session();
    let host_keys = [(c"idle_rgb", 0x00FF00i64), (c"gpu_thread_priority", 0)];
    let qp_on = args.qp_map.unwrap_or(nvidia_profile::QP_MAP_ENABLED);
    configure_encoder(&session, args.split_encode, qp_on, &host_keys);
    let s: serde_json::Value = fs::read_to_string(&session).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or(json!({}));
    let c = &s["openvr_config"];
    let fset = &s["session_settings"]["video"]["foveated_encoding"];
    let g = |k: &str, sk: &str, d: f64| c[k].as_f64().filter(|v| *v > 0.0).or_else(|| fset["content"][sk].as_f64().filter(|v| *v > 0.0)).unwrap_or(d) as f32;
    let (fov, ipd) = match load_views(&args.config_dir.join("visionalvr_views.json")) {
        Some((f, o)) if f.iter().all(|x| x.is_finite() && *x > 0.0) => (f, (o[3] - o[0]).abs().max(0.04)),
        _ => (AVP_DEFAULT_FOV, AVP_DEFAULT_IPD),
    };
    let base = bench_quality::Base {
        eye_w: args.width.map(|w| w / 2).or(c["eye_resolution_width"].as_i64().map(|v| v as i32)).filter(|v| *v > 0).unwrap_or(3552),
        eye_h: args.height.or(c["eye_resolution_height"].as_i64().map(|v| v as i32)).filter(|v| *v > 0).unwrap_or(3200),
        fps: if args.fps_explicit { args.fps } else { c["refresh_rate"].as_f64().filter(|v| *v > 0.0).unwrap_or(90.0) },
        foveated: c["enable_foveated_encoding"].as_bool().or(fset["enabled"].as_bool()).unwrap_or(true),
        geom: [
            g("foveation_center_size_x", "center_size_x", 0.45), g("foveation_center_size_y", "center_size_y", 0.4),
            g("foveation_center_shift_x", "center_shift_x", 0.4), g("foveation_center_shift_y", "center_shift_y", 0.1),
            g("foveation_edge_ratio_x", "edge_ratio_x", 4.0), g("foveation_edge_ratio_y", "edge_ratio_y", 5.0),
        ],
        preset: nvidia_profile::NVH_KEYS.iter().find(|(k, _)| *k == "nvenc_quality_preset").map(|(_, v)| *v as i32).unwrap_or(3),
        aq: nvidia_profile::NVH_KEYS.iter().find(|(k, _)| *k == "nvenc_adaptive_quantization_mode").map(|(_, v)| *v as i32).unwrap_or(1),
        split: args.split_encode as i32,
        qp_map: qp_on,
        bitrate_mbps: args.bench_mbps.map(|m| m as f64).or(s["session_settings"]["video"]["bitrate"]["mode"]["ConstantMbps"].as_f64()).unwrap_or(250.0),
        fov,
        ipd,
        engines: 0,
    };
    let exe_dir = std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.to_path_buf()));
    let scene = args.bench_scene.clone().unwrap_or_else(|| {
        install.map(|i| i.dir.clone()).or(exe_dir).unwrap_or_default().join("bench").join("littlest_tokyo.vab")
    });
    let opt = bench_quality::Options {
        warmup: args.bq_warmup,
        frames: args.bq_frames,
        metric_every: args.bq_metric_every,
        metric_stride: args.bq_stride,
        out: args.bq_out.clone().or_else(|| install.map(|i| i.config.join("benchmark_quality.json"))),
        preview: args.bq_preview.clone(),
        plan: args.bq_plan.clone(),
        ref_scale: args.bq_ref_scale,
        keepalive: args.gpu_keepalive.unwrap_or(60),
    };
    bench_quality::run(origin, &scene, &base, &opt, &|o, n, d| event(o, n, d))
}

// ---- main ------------------------------------------------------------------------------------------------

/// Stream parameters ALVR writes into the session's `openvr_config` when it negotiates with a client (it persists them at connect,
/// before the headset sends its views). Everything the encoder and the shim's frame ring are built from.
#[derive(Clone, PartialEq, Debug)]
struct StreamCfg {
    eye_w: i64,
    eye_h: i64,
    fps: f64,
    foveated: bool,
    geom: [f64; 6],
}

fn read_stream_cfg(session_path: &std::path::Path) -> StreamCfg {
    let s: serde_json::Value = serde_json::from_str(&fs::read_to_string(session_path).expect("read session")).expect("parse session");
    let c = &s["openvr_config"];
    let g = |k: &str| c[k].as_f64().unwrap_or(0.0);
    StreamCfg {
        eye_w: c["eye_resolution_width"].as_i64().unwrap_or(0),
        eye_h: c["eye_resolution_height"].as_i64().unwrap_or(0),
        fps: g("refresh_rate"),
        foveated: c["enable_foveated_encoding"].as_bool().unwrap_or(false),
        // the geometry only matters (and ALVR only keeps it) while foveation is on
        geom: if c["enable_foveated_encoding"].as_bool().unwrap_or(false) {
            [
                g("foveation_center_size_x"), g("foveation_center_size_y"), g("foveation_center_shift_x"),
                g("foveation_center_shift_y"), g("foveation_edge_ratio_x"), g("foveation_edge_ratio_y"),
            ]
        } else {
            [0.0; 6]
        },
    }
}

#[cfg(windows)]
extern "system" {
    fn GetCurrentThread() -> *mut std::ffi::c_void;
    fn SetThreadPriority(thread: *mut std::ffi::c_void, priority: i32) -> i32;
}

/// n / p50 / p95 / p99 / max (ms) of a list of intervals, plus how many exceed 1.5x the nominal period.
fn interval_stats(v: &[f64], period_ms: f64) -> serde_json::Value {
    let mut s = v.to_vec();
    s.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let q = |p: f64| s.get(((s.len() as f64) * p) as usize).copied().unwrap_or(0.0);
    json!({
        "n": s.len(), "min": s.first().copied().unwrap_or(0.0), "p01": q(0.01), "p50": s.get(s.len() / 2).copied().unwrap_or(0.0), "p95": q(0.95), "p99": q(0.99),
        "under_0p5_period": s.iter().filter(|x| **x < 0.5 * period_ms).count(),
        "max": s.last().copied().unwrap_or(0.0), "over_1p5_period": s.iter().filter(|x| **x > 1.5 * period_ms).count(),
    })
}

/// Highest bitrate step that held the frame rate without packet loss and without the network latency climbing (the link is
/// saturating), minus 10% headroom.
fn bench_recommendation(results: &[serde_json::Value], fps: f64) -> serde_json::Value {
    let f = |r: &serde_json::Value, k: &str| r[k].as_f64().unwrap_or(f64::NAN);
    let min_net = results.iter().map(|r| f(r, "network_ms")).filter(|x| x.is_finite()).fold(f64::MAX, f64::min);
    let mut best: Option<f64> = None;
    let judged: Vec<serde_json::Value> = results.iter().map(|r| {
        let (t, a) = (f(r, "target_mbps"), f(r, "actual_mbps"));
        let mut why = vec![];
        if !(f(r, "client_fps") >= 0.95 * fps) { why.push("headset fps dropped"); }
        if !(f(r, "packets_lost_per_s") <= 1.0) { why.push("packet loss"); }
        if !(f(r, "network_ms") <= min_net + 4.0) { why.push("network latency rising"); }
        if !(a >= 0.8 * t) { why.push("encoder could not reach the bitrate"); }
        if why.is_empty() && best.map_or(true, |b| t > b) {
            best = Some(t);
        }
        json!({ "target_mbps": t, "ok": why.is_empty(), "why": why })
    }).collect();
    json!({
        "steps": judged,
        "best_ok_mbps": best,
        "recommended_mbps": best.map(|b| (b * 0.9 / 10.0).round() * 10.0),
        "note": if best.is_none() { "no step passed: check the Wi-Fi link (5/6 GHz, close to the router) and try lower bitrates" } else { "" },
    })
}

/// The user's display gamma and the debug session folder, for the shim.
fn push_user(ipc: *mut std::ffi::c_void, gamma: f32, debug: bool) {
    #[cfg(windows)]
    let dir: Vec<u16> = {
        use std::os::windows::ffi::OsStrExt;
        logs::debug_dir().map(|d| d.as_os_str().encode_wide().chain(std::iter::once(0)).collect()).unwrap_or_else(|| vec![0])
    };
    #[cfg(not(windows))]
    let dir: Vec<u16> = vec![0];
    unsafe { nvh::nvh_ipc_set_user(ipc, gamma, debug as i32, dir.as_ptr()) };
}

/// Per-frame histories in --daemon mode: past this many entries the oldest half is dropped. Returns how many were dropped.
const HISTORY_CAP: usize = 1 << 18;
fn bound_history<T>(v: &mut Vec<T>) -> usize {
    if v.len() > HISTORY_CAP {
        let n = v.len() / 2;
        v.drain(..n);
        n
    } else {
        0
    }
}

/// An encode failed. In --daemon mode (the installed host) a transient failure, e.g. a shim ring that was just recreated by a new
/// app, must not end the process: drop the frame, reopen the shared slots, restart from an IDR. Gives up after 30 in a row.
/// Returns false when the caller should stop.
/// The headset's last views (FoV tangents, eye offsets), kept across runs so an OpenXR app can start before this connection's
/// ViewsConfig arrives (the visionOS client sends it after its first decoded frame).
fn load_views(path: &std::path::Path) -> Option<([f32; 8], [f32; 6])> {
    let v: serde_json::Value = serde_json::from_str(&fs::read_to_string(path).ok()?).ok()?;
    let arr = |k: &str| -> Option<Vec<f32>> { v[k].as_array()?.iter().map(|x| x.as_f64().map(|f| f as f32)).collect() };
    Some((arr("fov_tan")?.try_into().ok()?, arr("eye_offsets")?.try_into().ok()?))
}

fn save_views(path: &std::path::Path, v: &([f32; 8], [f32; 6])) {
    fs::write(path, json!({ "fov_tan": v.0, "eye_offsets": v.1 }).to_string()).ok();
}

/// `session_settings.audio.game_audio` as ALVR will use it (server_core captures and streams it by itself).
fn game_audio_config(session_path: &std::path::Path) -> serde_json::Value {
    let s: serde_json::Value = fs::read_to_string(session_path).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default();
    let g = &s["session_settings"]["audio"]["game_audio"];
    json!({
        "enabled": g["enabled"].as_bool(),
        "device": if g["content"]["device"]["set"].as_bool() == Some(true) { g["content"]["device"]["content"].clone() } else { json!("windows default output") },
        "mute_when_streaming": g["content"]["mute_when_streaming"].as_bool(),
        "windows_default_id": default_playback_device_id(),
    })
}

#[cfg(windows)]
fn default_playback_device_id() -> Option<String> {
    alvr_audio::AudioDevice::new_output(None).and_then(|d| alvr_audio::get_windows_device_id(&d)).ok()
}
#[cfg(not(windows))]
fn default_playback_device_id() -> Option<String> {
    None
}

#[derive(Default)]
pub(crate) struct Shared {
    /// views (fov tangents, eye offsets) from the latest ViewsConfig event, applied by the main loop (which owns the encoder)
    pending_views: Mutex<Option<([f32; 8], [f32; 6])>>,
    /// ClientConnected seen, not yet handled by the main loop (which checks the encoder against the negotiated stream)
    connect_pending: AtomicBool,
    /// the encoder matches the current connection's negotiated stream: frames may be sent. Deliberately NOT tied to the
    /// headset's ViewsConfig: the stock visionOS client sends its views only after it has decoded a first frame
    stream_ready: AtomicBool,
    audio_device_events: AtomicUsize,
    /// (hostname, name, ip) of the connected headset
    headset: Mutex<Option<(String, String, String)>>,
    latest_sample_ts: Mutex<Option<Duration>>,
    /// the last tracking samples with their head orientation (x y z w), oldest first (at most 48, half a second): frame generation
    /// (pipeline.rs) stamps every generated frame with the newest one newer than the last sent frame and rotates the picture to it
    pub(crate) recent_samples: Mutex<std::collections::VecDeque<(Duration, [f32; 4])>>,
    client_connected: AtomicBool,
    restart_from_idr: AtomicBool,
    tracking_events: AtomicUsize,
    idr_requests: AtomicUsize,
    frames_sent: AtomicUsize,
    bytes_sent: AtomicU64,
    config_sent: AtomicUsize,
}

/// Keeps the last tracking samples for the frame generator (see `Shared::recent_samples`); a clock restart (the headset reconnected) clears it.
pub(crate) fn push_recent_sample(shared: &Shared, ts: Duration, q: [f32; 4]) {
    let mut ring = shared.recent_samples.lock();
    if ring.back().map(|(t, _)| ts > *t).unwrap_or(true) {
        ring.push_back((ts, q));
        while ring.len() > 48 {
            ring.pop_front();
        }
    } else if ring.back().map(|(t, _)| ts + Duration::from_secs(1) < *t).unwrap_or(false) {
        ring.clear();
        ring.push_back((ts, q));
    }
}

/// The shim's pacing for the frame-generation rate in force: (release step in display periods, minimum release spacing in us; 0 = none).
/// Generation off: every boundary, no cap. Third / half: every 3rd / 2nd boundary. Two thirds: free-running held to two thirds of the
/// display rate (the pipeline refreshes it when the display period changes). Uncapped: free-running, the user's fps cap if any.
fn fg_pacing(args: &Args, fg_active: bool, display_period_s: f64) -> (u32, u32) {
    if !fg_active {
        return (1, 0);
    }
    let rate = args.fg_rate.unwrap_or(FgRate::Half);
    let cap = match rate {
        FgRate::Uncapped => match args.fg_cap_fps { Some(f) if f > 0.0 => (1_000_000.0 / f).round() as u32, _ => 0 },
        r => r.min_period_us(display_period_s),
    };
    (rate.period_mul(), cap)
}

/// Applies `fg_pacing` to the shim (ipc appPeriodMul and appMinPeriodUs).
fn apply_fg_pacing(ipc: *mut std::ffi::c_void, p: Option<&pipeline::Pipeline>, args: &Args, fg_active: bool) {
    let period_s = p.map(|p| p.period().as_secs_f64()).unwrap_or(1.0 / args.fps);
    let (mul, cap) = fg_pacing(args, fg_active, period_s);
    unsafe {
        nvh::nvh_ipc_set_app_period_mul(ipc, mul);
        nvh::nvh_ipc_set_app_min_period(ipc, cap);
    }
}

/// Every frame-generation choice, logged whenever one changes (the night's tests are read back from these lines).
fn log_fg_config(origin: Instant, args: &Args, fg_active: bool, why: &str) {
    let rate = args.fg_rate.unwrap_or(FgRate::Half).name();
    let cap = args.fg_cap_fps.unwrap_or(0.0);
    let lag = args.fg_lag_ms.unwrap_or(0.0);
    logs::general("INFO", &format!("frame generation config ({why}): generation {}, rate {rate}, game motion vectors {}, fps cap {}, content lag {} ms",
        if fg_active { "ON" } else { "off" }, if args.game_motion_vectors { "on" } else { "off" }, if cap > 0.0 { format!("{cap:.0}") } else { "none".into() },
        if lag > 0.0 { format!("{lag:.1}") } else { "auto".into() }));
    event(origin, "frame_gen_config", json!({ "why": why, "on": fg_active, "rate": rate, "game_motion_vectors": args.game_motion_vectors,
        "cap_fps": cap, "lag_ms": lag, "flow_scale": args.fg_flow_scale, "perf": args.fg_perf }));
}

/// What the host records per game (config/games.json, keyed by the executable's file name, then the headset's name, then the generation level:
/// the rate name or "off"): the last run at that level, for the launcher's "Recommended" tick. The host records and decides nothing; the
/// launcher reads the file and proposes. A run shorter than 20 s is not recorded. The headset's repeated-period share is given as the median
/// 10-second window of the run (what the game is like most of the time) and its 90th percentile (its heavy parts): the proposal is made on the
/// typical window, hiccups being a fact of life in VR (user, 2026-10-17).
fn games_record_run(args: &Args, origin: Instant, exe: &str, headset: &str, hz: f64, level: &str, secs: f64, rendered_fps: f64, streamed_fps: f64, windows: &[(f64, f64, f64)], skipped: f64) {
    let pct = |v: &mut Vec<f64>, q: f64| -> f64 {
        if v.is_empty() { return 0.0; }
        v.sort_by(|a, b| a.partial_cmp(b).unwrap());
        v[(((v.len() - 1) as f64) * q).round() as usize]
    };
    let mut c: Vec<f64> = windows.iter().map(|w| w.0).collect();
    let mut h: Vec<f64> = windows.iter().map(|w| w.1).collect();
    let mut f: Vec<f64> = windows.iter().map(|w| w.2).collect();
    let unix = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
    // rendered_fps_low: the slowest tenth of the run's 10-second windows (its heavy parts), next to the run's average
    let mut rec = json!({ "seconds": secs, "display_hz": hz, "rendered_fps": rendered_fps, "rendered_fps_low": if f.is_empty() { rendered_fps } else { pct(&mut f, 0.1) }, "streamed_fps": streamed_fps,
        "headset_repeated_typ": pct(&mut c, 0.5), "headset_repeated_p90": pct(&mut c, 0.9), "host_repeats_typ": pct(&mut h, 0.5),
        "skipped": skipped, "windows": windows.len(), "updated": unix });
    let path = args.config_dir.join("games.json");
    let mut all = std::fs::read_to_string(&path).ok().and_then(|t| serde_json::from_str::<serde_json::Value>(&t).ok()).filter(|v| v.is_object()).unwrap_or_else(|| json!({}));
    let runs = all[exe][headset][level]["runs"].as_u64().unwrap_or(0) + 1;
    rec["runs"] = json!(runs);
    all[exe][headset][level] = rec.clone();
    if let Ok(t) = serde_json::to_string_pretty(&all) {
        let _ = std::fs::create_dir_all(args.config_dir.as_path());
        let _ = std::fs::write(&path, t);
    }
    logs::general("INFO", &format!("game run recorded: {exe} on {headset} at {level}, {secs:.0} s, {rendered_fps:.1} fps rendered (slowest tenth {:.1}), {streamed_fps:.1} streamed, headset repeated {:.1} % typical / {:.1} % worst (run {runs})",
        rec["rendered_fps_low"].as_f64().unwrap_or(0.0), rec["headset_repeated_typ"].as_f64().unwrap_or(0.0) * 100.0, rec["headset_repeated_p90"].as_f64().unwrap_or(0.0) * 100.0));
    rec["exe"] = json!(exe);
    rec["headset"] = json!(headset);
    rec["level"] = json!(level);
    event(origin, "game_run", rec);
}

/// Generation on or off on the running stream (the launcher's tick): the generator is created or dropped on the running encoder (under
/// the encoder lock, so never while a frame is being encoded) and the shim's pacing follows at the next boundary.
fn set_frame_gen_live(origin: Instant, p: &pipeline::Pipeline, args: &Args, want: bool, fg_active: &mut bool, ipc: *mut std::ffi::c_void, why: &str) {
    if want == *fg_active {
        return;
    }
    if !want {
        p.set_frame_gen(false);   // the encoder thread plans plain frames from here on
    }
    let g = p.enc.lock();
    if want {
        *fg_active = enable_frame_gen(origin, g.handle, args);
    } else {
        let mut err = [0 as std::ffi::c_char; 256];
        unsafe { nvh::nvh_fg_enable(g.handle, 0, args.fg_flow_scale, args.fg_perf, args.fg_grid, err.as_mut_ptr(), 256) };
        *fg_active = false;
        logs::general("INFO", &format!("frame generation OFF ({why}): full-rate pacing"));
        event(origin, "frame_gen", json!({ "requested": false, "on": false, "why": why }));
    }
    drop(g);
    p.set_frame_gen(*fg_active);
    apply_fg_pacing(ipc, Some(p), args, *fg_active);
    log_fg_config(origin, args, *fg_active, why);
}

/// Creates the generator on the encoder (after nvh_create, the stream's frame size being known): true when it runs; false (logged) when the
/// GPU or driver has no optical-flow engine, in which case the stream runs as without the feature.
fn enable_frame_gen(origin: Instant, handle: *mut std::ffi::c_void, args: &Args) -> bool {
    let mut err = [0 as std::ffi::c_char; 256];
    let ok = unsafe { nvh::nvh_fg_enable(handle, 1, args.fg_flow_scale, args.fg_perf, args.fg_grid, err.as_mut_ptr(), 256) } != 0;
    unsafe { nvh::nvh_fg_use_mv(handle, args.game_motion_vectors as i32) };
    let mut buf = [0 as std::ffi::c_char; 768];
    unsafe { nvh::nvh_fg_stats(handle, buf.as_mut_ptr(), 768) };
    let stats = unsafe { std::ffi::CStr::from_ptr(buf.as_ptr()) }.to_string_lossy().to_string();
    if ok {
        logs::general("INFO", &format!("frame generation ON: rate {}, flow scale {:.2}, perf {}, grid {}; {}", args.fg_rate.unwrap_or(FgRate::Half).name(), args.fg_flow_scale, args.fg_perf, args.fg_grid,
            serde_json::from_str::<serde_json::Value>(&stats).ok().and_then(|v| v["info"].as_str().map(str::to_string)).unwrap_or_default()));
    } else {
        let msg = unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }.to_string_lossy().to_string();
        logs::general("WARN", &format!("frame generation requested but unavailable, streaming without it: {msg}"));
    }
    event(origin, "frame_gen", json!({ "requested": true, "on": ok, "flow_scale": args.fg_flow_scale, "perf": args.fg_perf, "grid": args.fg_grid, "rate": args.fg_rate.unwrap_or(FgRate::Half).name(),
        "error": if ok { None } else { Some(unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }.to_string_lossy().to_string()) },
        "gen": serde_json::from_str::<serde_json::Value>(&stats).unwrap_or(serde_json::Value::Null) }));
    ok
}

fn main() {
    let mut args = match parse_args() {
        Ok(a) => a,
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(3);
        }
    };
    let origin = Instant::now();
    phase::set_origin(origin);
    let gpu_info: serde_json::Value = {
        let mut b = vec![0 as std::ffi::c_char; 2048];
        unsafe { nvh::nvh_gpu_info(b.as_mut_ptr(), b.len() as i32) };
        serde_json::from_str(&unsafe { std::ffi::CStr::from_ptr(b.as_ptr()) }.to_string_lossy()).unwrap_or(json!({}))
    };
    if args.system_check {
        let v = vision::system_check(gpu_info);
        println!("{}", json!({ "event": "system_check", "data": v }));
        std::process::exit(if v["ok"] == true { 0 } else { 6 });
    }
    let install = args.install_dir.as_deref().map(vision::Install::new);
    let mut seeded = false;
    if let Some(i) = &install {
        args.config_dir = i.config.clone();
        fs::create_dir_all(&i.config).ok();
        // an explicit --session (seeded below) wins over the shipped default
        seeded = args.session.is_none() && vision::seed_session(&i.config);
        let st = vision::load_settings(&i.config.join("visionalvr.json"));
        for (k, v) in st.color.iter().enumerate() {
            vision::COLOR_REQ[k].store(v.to_bits(), Ordering::SeqCst);
        }
        vision::COLOR_PENDING.store(true, Ordering::SeqCst);
        apply_settings(&mut args, &st);
    } else {
        apply_settings(&mut args, &vision::Settings::default());
    }
    fs::create_dir_all(&args.config_dir).expect("create config dir");
    let layout = alvr_filesystem::Layout::new(&args.config_dir);
    let logs_dir = install.as_ref().map(|i| i.logs.clone()).or(args.logs_dir.clone());
    let alvr_log = match &install {
        Some(i) => i.logs.join("alvr.log"),
        None => layout.session_log(),
    };
    fs::create_dir_all(alvr_log.parent().unwrap_or(&args.config_dir)).ok();
    logs::init(logs_dir.as_deref(), Some(&alvr_log), args.debug == Some(true));
    telemetry::start();
    let mut debug_on = args.debug == Some(true);
    let mut user_gamma = args.gamma.unwrap_or(1.0).clamp(0.5, 2.0);
    event(origin, "host_start", json!({ "version": vision::VERSION, "alvr": env!("CARGO_PKG_VERSION"), "args": std::env::args().skip(1).collect::<Vec<_>>(),
        "install_dir": args.install_dir, "debug_dir": logs::debug_dir(), "gpu": gpu_info }));
    logs::general("INFO", &format!(
        "VisionALVR {} host start ({}) | GPU {} ({}, driver {}) | profile {:?} | debug {}{}",
        vision::VERSION, args.install_dir.as_ref().map(|d| d.display().to_string()).unwrap_or_else(|| args.config_dir.display().to_string()),
        gpu_info["name"].as_str().unwrap_or("?"), gpu_info["arch_name"].as_str().unwrap_or("?"), gpu_info["driver"].as_str().unwrap_or("?"),
        args.encode_profile, if debug_on { "on: " } else { "off" },
        logs::debug_dir().map(|d| d.display().to_string()).unwrap_or_default(),
    ));
    if seeded {
        logs::general("INFO", "first start: config/session.json created from config/session.default.json");
    }
    if args.gui {
        vision::spawn_stdin_control();
    }
    if let Some(secs) = args.discover {
        // GUI pairing: ALVR's two discovery protocols (mDNS for the Vision Pro, UDP 9943 broadcasts), nothing else runs
        let stop = Arc::new(AtomicBool::new(false));
        let stop2 = Arc::clone(&stop);
        if !args.gui {
            vision::spawn_stdin_control(); // the GUI closes stdin (or sends "quit") to stop early
        }
        thread::spawn(move || {
            let until = Instant::now() + Duration::from_secs_f64(secs);
            while Instant::now() < until && !vision::QUIT.load(Ordering::SeqCst) {
                thread::sleep(Duration::from_millis(100));
            }
            stop2.store(true, Ordering::Relaxed);
        });
        let r = discovery::run(stop, true, &|s| {
            logs::general("INFO", &format!("headset seen ({}): {} at {} (protocol {}{})", s.via, s.hostname, s.ip, s.protocol, if s.compatible { "" } else { ", INCOMPATIBLE" }));
            event(origin, "headset_seen", discovery::event_json(&s));
        });
        if let Err(e) = r {
            event(origin, "discovery_error", json!({ "message": e.to_string() }));
            std::process::exit(6);
        }
        std::process::exit(0);
    }
    vision::spawn_gpu_sampler(|| {
        let mut b = vec![0 as std::ffi::c_char; 256];
        (unsafe { nvh::nvh_gpu_sample(b.as_mut_ptr(), b.len() as i32) } > 0)
            .then(|| unsafe { std::ffi::CStr::from_ptr(b.as_ptr()) }.to_string_lossy().to_string())
    });
    if let Some(s) = &args.session {
        // The session also holds ALVR's trusted-client list and the user's edits: only seed it, never overwrite.
        let dest = args.config_dir.join("session.json");
        if !dest.exists() || args.force_session {
            if let Err(e) = fs::copy(s, &dest) {
                setup_fail(origin, &format!("cannot copy the session {} to {}: {e}", s.display(), dest.display()));
            }
        }
    }
    if args.live || args.bench_quality {
        if let Err(m) = check_session(&args.config_dir.join("session.json"), install.is_some()) {
            setup_fail(origin, &m);
        }
    }
    if args.live {
        // encoder parameters are fixed (NVIDIA profile); log what differed from the session so nothing is overridden silently
        match nvidia_profile::apply_to_session(&args.config_dir.join("session.json")) {
            Ok(changed) => event(origin, "nvidia_profile", json!({ "overrides": changed })),
            Err(e) => {
                eprintln!("cannot apply NVIDIA profile to session.json: {e}");
                std::process::exit(3);
            }
        }
        // benchmarks try profiles without changing the user's session (they set the encoder's foveation directly)
        let profile_to_session = if args.bench.is_some() || args.bench_network.is_some() { nvidia_profile::EncodeProfile::Session } else { args.encode_profile };
        match nvidia_profile::apply_encode_profile(&args.config_dir.join("session.json"), profile_to_session) {
            Ok(changed) => event(origin, "encode_profile", json!({
                "profile": format!("{:?}", args.encode_profile), "session_changes": changed,
                "qp_map": args.qp_map, "split_encode_mode": args.split_encode,
            })),
            Err(e) => {
                eprintln!("cannot apply the encode profile to session.json: {e}");
                std::process::exit(3);
            }
        }
    }
    if args.bench_quality {
        std::process::exit(run_quality_bench(origin, &args, &layout, install.as_ref()));
    }
    let (aus, idr_count) = if args.live {
        (vec![], 0)
    } else {
        let stream = fs::read(&args.file).expect("read --file");
        let aus = split_access_units(&stream);
        let idr_count = aus.iter().filter(|a| a.is_idr).count();
        event(origin, "source", json!({ "file": args.file, "bytes": stream.len(), "access_units": aus.len(), "idr": idr_count }));
        if aus.is_empty() || !aus[0].is_idr {
            eprintln!("source must start with an IDR access unit");
            std::process::exit(3);
        }
        (aus, idr_count)
    };

    if install.is_some() {
        for c in vision::refresh_manual_ips(&layout.session()) {
            logs::general("INFO", &format!("headset address updated from ALVR's last connection: {c}"));
        }
        match vision::headset(&layout.session(), false) {
            Some((h, name, ip)) => logs::general("INFO", &format!("paired headset: {name} ({h}) at {ip}")),
            None => logs::general("WARN", "no paired headset in config/session.json: run configure.exe"),
        }
    }
    alvr_server_core::initialize_environment(layout.clone()); // (logging: logs::init above replaces server_core's init_logging)

    let (ctx, events) = ServerCoreContext::new();
    let ctx = Arc::new(ctx);
    let shared = Arc::new(Shared::default());

    // live NVENC encoder (settings from the session's openvr_config, like ALVR's C++ Settings.cpp)
    let mut live: Option<Box<LiveCtx>> = None;
    let mut eye_size = (0i32, 0i32); // per-eye render size the app uses (the shim composes this; the encoder may see less)
    let mut enc_handle: *mut std::ffi::c_void = std::ptr::null_mut();
    let mut enc_size = (0, 0);
    let qp_on = args.qp_map.unwrap_or_else(|| match std::env::var("VISIONALVR_QP_MAP").as_deref() {
        Ok("0") | Ok("off") => false,
        Ok("1") | Ok("on") => true,
        _ => nvidia_profile::QP_MAP_ENABLED,
    });
    let mut nvenc_cfg = json!(null);
    let mut cur_cfg = read_stream_cfg(&layout.session());
    let host_keys = [
        (c"idle_rgb", args.idle_rgb.unwrap_or(0x00FF00) as i64),
        (c"gpu_thread_priority", args.gpu_priority.unwrap_or(0)),
        (c"noise_block", args.noise_block),
        (c"noise_amp", args.noise_amp),
    ];
    let audio_cfg = game_audio_config(&layout.session());
    event(origin, "game_audio", audio_cfg.clone());
    // The headset's sound level must not follow the PC's volume: ALVR loop-back captures the default output, and when that is the VB-Audio cable
    // device its endpoint volume scales what is captured (the PC's volume keys then change the headset's volume). Keep that device at 100 %
    // while the host runs (checked every 3 s, so a change is undone within seconds). VISIONALVR_FORCE_CABLE_VOLUME=0 disables it.
    #[cfg(windows)]
    if audio_cfg["enabled"].as_bool().unwrap_or(true) && std::env::var("VISIONALVR_FORCE_CABLE_VOLUME").map(|v| v != "0").unwrap_or(true) {
        std::thread::spawn(move || {
            let mut last_logged = -1.0f32;
            loop {
                if let Ok(dev) = alvr_audio::AudioDevice::new_output(None) {
                    if let Ok(name) = dev.name() {
                        let lower = name.to_ascii_lowercase();
                        if lower.contains("cable") || lower.contains("vb-audio") {
                            if let Ok((vol, _muted)) = alvr_audio::get_volume_windows_device(&dev) {
                                if vol < 0.999 {
                                    let ok = alvr_audio::set_volume_windows_device(&dev, 1.0).is_ok();
                                    if (vol - last_logged).abs() > 0.01 {
                                        last_logged = vol;
                                        event(origin, "audio_volume_forced", json!({ "device": name, "was": vol, "set_ok": ok }));
                                    }
                                }
                            }
                        }
                    }
                }
                std::thread::sleep(Duration::from_secs(3));
            }
        });
    }
    let mut reconfigs = 0usize;
    if args.live {
        let (w, h, session_fps) = configure_encoder(&layout.session(), args.split_encode, qp_on, &host_keys);
        eye_size = (w / 2, h);
        if !args.fps_explicit {
            args.fps = session_fps; // the session's refresh rate (what the client is asked for), not a hard-coded 90
        }
        let benchmarking = args.bench.is_some() || args.bench_network.is_some();
        if benchmarking && args.encode_profile != nvidia_profile::EncodeProfile::Session {
            // no headset negotiated anything yet: the profile decides whether the encoder sees ALVR's foveated layout
            unsafe { nvh::nvh_set(c"enable_foveated_encoding".as_ptr(), (args.encode_profile == nvidia_profile::EncodeProfile::Foveated) as i64) };
        }
        let (w, h) = if args.shim || (benchmarking && args.width.is_none()) {
            // the encoded frame is ALVR's foveated layout when foveation is negotiated, else the full side-by-side frame
            let (mut ew, mut eh) = (0i32, 0i32);
            unsafe { nvh::nvh_encoded_size(&mut ew, &mut eh) };
            (ew, eh)
        } else {
            (args.width.unwrap_or(w), args.height.unwrap_or(h))
        };
        enc_size = (w, h);
        let mut l = Box::new(LiveCtx { ctx: Arc::clone(&ctx), shared: Arc::clone(&shared), packet_bytes: 0, packet_idr: false, packets: 0, slices: 0, slice_samples: 0, report_in_callback: !args.shim, cb_ns: 0, params_ns: 0, bitrate_override: 0, bytes_total: 0, bitrate_sent: 0, fps: args.fps as f32, hold_now: false, pending: vec![] });
        let mut err = [0 as std::ffi::c_char; 256];
        enc_handle = unsafe {
            nvh::nvh_create(w, h, args.noise, on_packet, on_params, &mut *l as *mut LiveCtx as *mut _, err.as_mut_ptr(), 256)
        };
        if enc_handle.is_null() {
            let msg = unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }.to_string_lossy().to_string();
            eprintln!("NVENC init failed: {msg}");
            std::process::exit(4);
        }
        // network benchmark: no game keeps the GPU clocked, and a lightly loaded GPU downclocks NVENC (~3x slower on the laptop)
        let keepalive = args.gpu_keepalive.or(if args.bench_network.is_some() { Some(90) } else { None });
        if let Some(duty) = keepalive {
            unsafe { nvh::nvh_keepalive(enc_handle, 1, duty) };
        }
        event(origin, "encoder", json!({ "width": w, "height": h, "noise": args.noise, "gpu_keepalive_duty_pct": args.gpu_keepalive }));
        nvenc_cfg = describe_encoder(origin, enc_handle, &args.dump_qpmap);
        live = Some(l);
    }
    let mut ipc: *mut std::ffi::c_void = std::ptr::null_mut();
    if args.shim {
        ipc = unsafe { nvh::nvh_ipc_open() };
        if ipc.is_null() {
            eprintln!("cannot open the OVRShim IPC objects");
            std::process::exit(4);
        }
        event(origin, "ipc", json!({ "shim_alive": unsafe { nvh::nvh_ipc_shim_alive(ipc) } }));
    }
    let ipc_addr = ipc as usize;
    if let (Some(n), Some(l)) = (args.bench, live.as_mut()) {
        // encoder-only benchmark: back-to-back frames, nothing connected, at the session's bitrate (or --bench-mbps)
        let mbps = args.bench_mbps.unwrap_or_else(|| {
            fs::read_to_string(layout.session()).ok().and_then(|t| serde_json::from_str::<serde_json::Value>(&t).ok())
                .and_then(|s| s["session_settings"]["video"]["bitrate"]["mode"]["ConstantMbps"].as_u64()).unwrap_or(250)
        });
        l.bitrate_override = mbps * 1_000_000;
        let mut v = vec![];
        for i in 0..n {
            let mut err = [0 as std::ffi::c_char; 256];
            let ms = unsafe { nvh::nvh_encode(enc_handle, i as u32, i as u64 * 11_000_000, (i == 0) as i32, err.as_mut_ptr(), 256) };
            if ms < 0.0 {
                println!("{}", json!({ "event": "bench_error", "frame": i, "message": unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }.to_string_lossy() }));
                std::process::exit(5);
            }
            v.push(ms);
            if args.bench_sleep_ms > 0 {
                thread::sleep(Duration::from_millis(args.bench_sleep_ms));
            }
        }
        let avg = v.iter().sum::<f64>() / v.len() as f64;
        let mut sorted = v[v.len().min(10)..].to_vec(); // the first frames include the encoder's warm-up
        sorted.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let q = |p: f64| sorted.get(((sorted.len() as f64) * p) as usize).copied().unwrap_or(0.0);
        let (p50, p95) = (q(0.5), q(0.95));
        let budget = 1000.0 / args.fps;
        let data = json!({ "frames": n, "avg_ms": avg, "p50_ms": p50, "p95_ms": p95, "min_ms": v.iter().cloned().fold(f64::MAX, f64::min),
            "max_ms": v.iter().cloned().fold(0.0, f64::max), "width": enc_size.0, "height": enc_size.1, "profile": format!("{:?}", args.encode_profile),
            "split_encode_mode": args.split_encode, "nvenc_engines": unsafe { nvh::nvh_encoder_engines(enc_handle) },
            "send_pacing": args.send_pacing.name(),
            "slices_per_frame": live.as_ref().map(|l| l.slices as f64 / l.slice_samples.max(1) as f64),
            "fps": args.fps, "budget_ms": budget, "fits": p95 < budget * 0.9, "noise": args.noise, "noise_block": args.noise_block, "noise_amp": args.noise_amp,
            "target_mbps": mbps, "produced_mbps_at_fps": live.as_ref().map(|l| l.bytes_total as f64 * 8.0 / n.max(1) as f64 * args.fps / 1e6) });
        println!("{}", json!({ "event": "bench", "data": data }));
        logs::general("INFO", &format!("encoder benchmark {:?} {}x{}: p50 {p50:.1} ms, p95 {p95:.1} ms ({} at {} Hz)", args.encode_profile, enc_size.0, enc_size.1,
            if p95 < budget * 0.9 { "fits" } else { "does NOT fit" }, args.fps));
        std::process::exit(0);
    }
    // Shim mode: the staged pipeline owns the encoder from here on (intake / clock pick / encoder / sender threads).
    let mut pipeline: Option<Arc<pipeline::Pipeline>> = None;
    let vsync_period_ns = Arc::new(AtomicU64::new((1e9 / args.fps) as u64));
    let mut fg_active = false;
    if args.shim {
        let l = live.take().expect("live encoder context");
        let h = std::mem::replace(&mut enc_handle, std::ptr::null_mut());
        let p = pipeline::Pipeline::new(
            pipeline::Config { origin, period_ns: Arc::clone(&vsync_period_ns), send_pacing: args.send_pacing, daemon: args.daemon, fps: args.fps },
            ipc_addr, Arc::clone(&ctx), Arc::clone(&shared), pipeline::Encoder { handle: h, live: l });
        logs::set_clock(Instant::now(), unsafe { nvh::nvh_qpc_seconds() });
        unsafe { nvh::nvh_ipc_set_pacing(ipc, args.boundary_offset_ms as f32, args.running_start_ms as f32) };
        unsafe { nvh::nvh_ipc_set_intake_gate(ipc, if pipeline::intake_gate_enabled() { 1 } else { 0 }) };
        unsafe { nvh::nvh_ipc_set_head_policy(ipc, args.head_wait_ms as f32, if args.predict_missing_head { 1.0 } else { 0.0 }) };
        {
            // hand pose convention fix (see Args::hand_rotation_deg); the right hand mirrors y and z like ALVR's own offsets
            fn apply_hand_offset(ipc: *mut std::ffi::c_void, rot_deg: [f32; 3], pos_m: [f32; 3]) {
                use alvr_common::glam::{EulerRot, Quat};
                let r = rot_deg.map(f32::to_radians);
                for (side, (ry, rz, px)) in [(0, (r[1], r[2], pos_m[0])), (1, (-r[1], -r[2], -pos_m[0]))] {
                    let q = Quat::from_euler(EulerRot::XYZ, r[0], ry, rz).to_array();
                    let p = [px, pos_m[1], pos_m[2]];
                    unsafe { nvh::nvh_ipc_set_hand_offset(ipc, side, q.as_ptr(), p.as_ptr()) };
                }
            }
            apply_hand_offset(ipc, args.hand_rotation_deg, args.hand_position_m);
            event(origin, "hand_offset", json!({ "rotation_deg": args.hand_rotation_deg, "position_m": args.hand_position_m }));
            // Tuning aid: config/hand_offset.json {"rotation_deg":[x,y,z],"position_m":[x,y,z]} is re-read every second while the host runs and
            // wins over the settings, so a candidate can be tried in a running game without restarting anything. Delete the file to stop.
            let (ipc_addr, path, base) = (ipc as usize, args.config_dir.join("hand_offset.json"), (args.hand_rotation_deg, args.hand_position_m));
            std::thread::spawn(move || {
                let mut last: Option<std::time::SystemTime> = None;
                loop {
                    std::thread::sleep(Duration::from_secs(1));
                    let m = fs::metadata(&path).and_then(|m| m.modified()).ok();
                    if m == last {
                        continue;
                    }
                    last = m;
                    let (rot, pos) = match fs::read_to_string(&path).ok().and_then(|t| serde_json::from_str::<serde_json::Value>(&t).ok()) {
                        Some(v) => {
                            let a3 = |k: &str, d: [f32; 3]| -> [f32; 3] {
                                v[k].as_array().filter(|a| a.len() == 3).map(|a| [0, 1, 2].map(|i| a[i].as_f64().unwrap_or(d[i] as f64) as f32)).unwrap_or(d)
                            };
                            (a3("rotation_deg", base.0), a3("position_m", base.1))
                        }
                        None => base,
                    };
                    apply_hand_offset(ipc_addr as *mut std::ffi::c_void, rot, pos);
                    event(origin, "hand_offset", json!({ "rotation_deg": rot, "position_m": pos, "source": if m.is_some() { "config/hand_offset.json" } else { "settings" } }));
                }
            });
        }
        p.set_gpu_scheduling(args.gpu_sched_class);
        // frame generation: the generator lives on the encoder; the shim paces the game at half rate only while it runs
        if args.frame_gen.unwrap_or(false) {
            fg_active = enable_frame_gen(origin, p.enc.lock().handle, &args);
            p.set_frame_gen(fg_active);
        }
        p.set_frame_gen_rate(args.fg_rate.unwrap_or(FgRate::Half), args.fg_lag_ms.unwrap_or(0.0));
        apply_fg_pacing(ipc, Some(p.as_ref()), &args, fg_active);
        unsafe { nvh::nvh_ipc_set_game_mv(ipc, if args.game_motion_vectors { 1 } else { 0 }) };
        log_fg_config(origin, &args, fg_active, "start");
        p.start();
        event(origin, "pacing_config", json!({ "send_pacing": args.send_pacing.name(), "running_start_ms": args.running_start_ms,
            "head_wait_ms": args.head_wait_ms, "predict_missing_head": args.predict_missing_head, "boundary_offset_ms": args.boundary_offset_ms, "gpu_sched_class": args.gpu_sched_class, "ring_slots": 6,
            "frame_gen": fg_active, "rate": args.fg_rate.unwrap_or(FgRate::Half).name(), "app_period_mul": fg_pacing(&args, fg_active, 1.0 / args.fps).0 }));
        #[cfg(feature = "simrig")]
        if let Some(dir) = &args.simrig {
            simrig::set_ipc(ipc_addr);
            simrig::init(&dir.to_string_lossy(), ipc_addr, p.enc.lock().handle, &shared, &vsync_period_ns, read_encoding_gamma(&layout.session()));
        }
        pipeline = Some(p);
    }
    let app_alive = || pipeline.as_ref().map(|p| p.app_alive.load(Ordering::Relaxed)).unwrap_or(false);
    let mut encode_ms: Vec<f64> = vec![];
    let mut compose_ms: Vec<f64> = vec![];
    let mut ffr_ms: Vec<f64> = vec![];
    let mut ffr_wait_ms: Vec<f64> = vec![];
    let mut frame_rows: Vec<String> = vec![]; // header added when written
    ctx.start_connection();
    event(origin, "listening", json!({ "config_dir": args.config_dir }));
    let discovery_stop = Arc::new(AtomicBool::new(false));
    {
        let stop = Arc::clone(&discovery_stop);
        thread::spawn(move || {
            if let Err(e) = discovery::run(stop, false, &|s| event(origin, "headset_seen", discovery::event_json(&s))) {
                logs::general("WARN", &format!("headset discovery (mDNS) not running: {e}"));
            }
        });
    }

    // event thread
    {
        let (ctx, shared) = (Arc::clone(&ctx), Arc::clone(&shared));
        let vsync_period_ns = Arc::clone(&vsync_period_ns);
        let session_path = layout.session();
        let mut height_target = args.height_m;
        thread::spawn(move || {
            let ipc = ipc_addr as *mut std::ffi::c_void;
            let mut hands = [HandInputState::default(); 2];
            // Height calibration. VDXR treats our LibOVR as an eye-level-origin runtime (no Virtual Desktop server running): LOCAL space is the head
            // pose as published, STAGE / LOCAL_FLOOR add the eye height we report (OVR_KEY_EYE_HEIGHT). So, like Oculus' runtime: the anchored head
            // (at connect, after a short settle, and whenever the launcher sends a new height) is published at y = 0 (eye-level origin) with the
            // headset's own vertical movement on top, and the eye height games get is `height_target` (0 = the headset's own floor estimate: its y
            // at the anchor). Floor-level games then see the player at that height, eye-level games at the origin (before this a floor-level game saw
            // height + 1.675 m and an eye-level game a head 1.3 m above its origin: the scene looked far too low).
            let mut height_offset = 0f32;
            let mut height_anchored = false;
            // `recenter` (the launcher, for a headset whose own forward is the room's, not the player's): the published tracking space is
            // turned so the head's forward at that moment is -Z and its position the origin; height as above. Nothing until asked.
            let mut recenter: Option<(alvr_common::glam::Quat, alvr_common::glam::Vec3)> = None;
            let mut recenter_pending = false;
            let mut samples_since_connect = 0u32;
            let mut rate_est = vision::RateEstimator::new();
            let nominal_period_ns = (1e9 / args.fps) as u64;
            let mut buttons_seen = 0usize;
            let mut invalid_views = 0usize;
            for ev in events {
                match ev {
                    ServerCoreEvent::ClientConnected => {
                        phase::reset();
                        height_anchored = false;
                        samples_since_connect = 0;
                        rate_est.reset();
                        RATE_IGNORED.store(false, Ordering::Relaxed);
                        recenter = None;
                        recenter_pending = false;
                        shared.connect_pending.store(true, Ordering::SeqCst);
                        shared.client_connected.store(true, Ordering::SeqCst);
                        shared.restart_from_idr.store(true, Ordering::SeqCst);
                        let hs = vision::headset(&session_path, true);
                        if let Some((h, name, ip)) = &hs {
                            logs::general("INFO", &format!("headset connected: {name} ({h}) at {ip}"));
                        }
                        *shared.headset.lock() = hs.clone();
                        event(origin, "client_connected", json!(hs.map(|(h, n, ip)| json!({ "hostname": h, "name": n, "ip": ip }))));
                    }
                    ServerCoreEvent::ClientDisconnected => {
                        shared.client_connected.store(false, Ordering::SeqCst);
                        shared.stream_ready.store(false, Ordering::SeqCst);
                        *shared.latest_sample_ts.lock() = None;
                        if !ipc.is_null() {
                            unsafe { nvh::nvh_ipc_set_connected(ipc, 0) };
                        }
                        logs::general("INFO", "headset disconnected");
                        event(origin, "client_disconnected", json!(null));
                    }
                    ServerCoreEvent::Tracking { sample_timestamp } => {
                        let n = shared.tracking_events.fetch_add(1, Ordering::Relaxed);
                        let now_ns = origin.elapsed().as_nanos() as u64;
                        let prev_ns = LAST_TRACKING_NS.swap(now_ns, Ordering::Relaxed);
                        if prev_ns != 0 {
                            push_bounded(&TRACKING_GAP_MS, now_ns.saturating_sub(prev_ns) as f64 / 1e6);
                        }
                        *shared.latest_sample_ts.lock() = Some(sample_timestamp);
                        if args.follow_rate {
                            if let Some(period) = rate_est.push(sample_timestamp.as_nanos() as u64) {
                                // only a cadence that is one of the refresh rates the headset declared at the handshake is its display's
                                // (Vision Pro: 100 / 96 / 90, Quest 3: 72 / 80 / 90 / 120); a headset that tracks faster than it displays
                                // (Quest 3: 270 Hz for a 90 Hz panel) says nothing about its display period this way
                                let hz = 1e9 / period as f64;
                                let declared = logs::client_refresh_rates();
                                let known: &[f64] = if declared.is_empty() { &[60.0, 72.0, 80.0, 90.0, 96.0, 100.0, 120.0, 144.0] } else { &declared };
                                if !known.iter().any(|r| (hz - r).abs() / r < 0.03) {
                                    if !RATE_IGNORED.swap(true, Ordering::Relaxed) {
                                        logs::general("INFO", &format!("client tracking cadence {hz:.1} Hz is none of the headset's refresh rates ({}): several samples per frame, host clock stays on the nominal {:.1} Hz",
                                            known.iter().map(|r| format!("{r:.0}")).collect::<Vec<_>>().join(" / "), 1e9 / nominal_period_ns as f64));
                                        event(origin, "display_rate", json!({ "client_period_ms": period as f64 / 1e6, "client_hz": hz, "host_period_ms": nominal_period_ns as f64 / 1e6, "following": false, "ignored": true, "declared_hz": known }));
                                    }
                                    continue;
                                }
                                let cur = vsync_period_ns.load(Ordering::Relaxed);
                                let off_nominal = (period as f64 - nominal_period_ns as f64).abs() / nominal_period_ns as f64 > 0.025;
                                let target = if off_nominal { period } else { nominal_period_ns };
                                if (target as f64 - cur as f64).abs() / cur as f64 > 0.005 {
                                    vsync_period_ns.store(target, Ordering::Relaxed);
                                    FOLLOW_RATE.store(off_nominal, Ordering::Relaxed);
                                    event(origin, "display_rate", json!({ "client_period_ms": period as f64 / 1e6, "client_hz": 1e9 / period as f64,
                                        "host_period_ms": target as f64 / 1e6, "following": off_nominal }));
                                    logs::general("INFO", &format!("client display rate {:.1} Hz: host clock {}", 1e9 / period as f64,
                                        if off_nominal { "follows it" } else { "back on the nominal grid" }));
                                }
                            }
                        }
                        if !ipc.is_null() {
                            // server_core hands out angular velocity in the device's own frame (what OpenVR wants); OVR/OpenXR report
                            // it in tracking space (VDXR passes it to the app as a base-space velocity): rotate it back
                            let world_av = |m: &alvr_common::DeviceMotion| (m.pose.orientation * m.angular_velocity).to_array();
                            samples_since_connect = samples_since_connect.saturating_add(1);
                            let req = vision::HEIGHT_REQ.swap(0, Ordering::SeqCst);
                            if req != 0 {
                                let h = f32::from_bits(req);
                                height_target = if h <= f32::MIN_POSITIVE { 0.0 } else { h };
                                height_anchored = false;
                            }
                            if vision::RECENTER_REQ.swap(false, Ordering::SeqCst) {
                                recenter_pending = true;
                            }
                            if let Some(m) = ctx.get_device_motion(*HEAD_ID, sample_timestamp) {
                                if recenter_pending && samples_since_connect >= 45 {
                                    use alvr_common::glam::{Quat, Vec3};
                                    let mut fwd = m.pose.orientation * Vec3::NEG_Z;
                                    fwd.y = 0.0;
                                    if fwd.length() > 0.1 {
                                        let r = Quat::from_rotation_arc(fwd.normalize(), Vec3::NEG_Z);
                                        let anchor = Vec3::new(m.pose.position.x, 0.0, m.pose.position.z);
                                        let turned = fwd.normalize().x.atan2(-fwd.normalize().z).to_degrees();
                                        recenter = Some((r, anchor));
                                        logs::general("INFO", &format!("recenter: forward turned {turned:+.0} deg, origin at the head (x {:.2} z {:.2})", anchor.x, anchor.z));
                                        event(origin, "recenter", json!({ "turned_deg": turned, "anchor_x": anchor.x, "anchor_z": anchor.z }));
                                    }
                                    recenter_pending = false;
                                }
                                if !height_anchored && samples_since_connect >= 45 {
                                    // half a second of tracking after connect: the pose is real, anchor the height on it
                                    let eye_height = if height_target > 0.0 { height_target } else { m.pose.position.y.max(0.3) };
                                    height_offset = -m.pose.position.y;
                                    height_anchored = true;
                                    unsafe { nvh::nvh_ipc_set_eye_height(ipc, eye_height) };
                                    logs::general("INFO", &format!("height: headset at {:.2} m -> eye-level origin (published y {:+.2}), eye height {:.2} m for floor-level games",
                                        m.pose.position.y, m.pose.position.y + height_offset, eye_height));
                                    event(origin, "height_calibrated", json!({ "target_m": height_target, "headset_y": m.pose.position.y, "offset_m": height_offset, "eye_height_m": eye_height }));
                                }
                                // Head: pose only, zero velocity, exactly like ALVR's SteamVR driver (Hmd::OnPoseUpdated). The
                                // visionOS client sends a finite-difference velocity of two ARKit poses (noisy); extrapolating
                                // the head with it over ~25 ms made the view bob, and the headset reprojects against the
                                // un-extrapolated pose anyway.
                                let (mut q, mut p) = (m.pose.orientation.to_array(), m.pose.position.to_array());
                                if let Some((r, anchor)) = recenter {
                                    q = (r * m.pose.orientation).to_array();
                                    p = (r * (m.pose.position - anchor)).to_array();
                                }
                                p[1] += height_offset;
                                push_recent_sample(&shared, sample_timestamp, q);
                                let (lv, av) = ([0f32; 3], [0f32; 3]);
                                unsafe { nvh::nvh_ipc_publish_head(ipc, sample_timestamp.as_nanos() as u64, q.as_ptr(), p.as_ptr(), lv.as_ptr(), av.as_ptr()) };
                                telemetry::head_sample(sample_timestamp.as_nanos() as u64, q, p);
                            }
                            for (side, id) in [(0, *HAND_LEFT_ID), (1, *HAND_RIGHT_ID)] {
                                match ctx.get_device_motion(id, sample_timestamp) {
                                    Some(m) => {
                                        let (mut q, mut p) = (m.pose.orientation.to_array(), m.pose.position.to_array());
                                        let (mut lv, mut av) = (m.linear_velocity.to_array(), world_av(&m));
                                        if let Some((r, anchor)) = recenter {
                                            q = (r * m.pose.orientation).to_array();
                                            p = (r * (m.pose.position - anchor)).to_array();
                                            lv = (r * m.linear_velocity).to_array();
                                            av = (r * alvr_common::glam::Vec3::from_array(av)).to_array();
                                        }
                                        p[1] += height_offset; // hands move with the head's frame
                                        unsafe { nvh::nvh_ipc_publish_hand(ipc, side, 1, q.as_ptr(), p.as_ptr(), lv.as_ptr(), av.as_ptr()) };
                                    }
                                    None => unsafe { nvh::nvh_ipc_publish_hand(ipc, side, 0, std::ptr::null(), std::ptr::null(), std::ptr::null(), std::ptr::null()) },
                                }
                            }
                        }
                        if n % 270 == 0 {
                            let head = ctx.get_device_motion(*HEAD_ID, sample_timestamp);
                            event(origin, "tracking", json!({
                                "n": n, "sample_ts_ns": sample_timestamp.as_nanos() as u64,
                                "head_orientation": head.map(|m| m.pose.orientation.to_array()),
                            }));
                        }
                    }
                    ServerCoreEvent::Buttons(entries) => {
                        // after ALVR's own mapping: paths are in the emulated (Quest/Touch) profile
                        for e in &entries {
                            if let Some(info) = BUTTON_INFO.get(&e.path_id) {
                                let side = if info.device_id == *HAND_LEFT_ID { 0 } else { 1 };
                                if let Some(input) = info.path.split("/input/").nth(1) {
                                    let _ = ButtonType::Binary; // (type documented by the path suffix)
                                    hands[side].apply(input, &e.value);
                                }
                            }
                        }
                        buttons_seen += entries.len();
                        if !ipc.is_null() {
                            for side in 0..2 {
                                let h = hands[side];
                                unsafe { nvh::nvh_ipc_publish_input(ipc, side as i32, h.clicks, h.touches, h.trigger, h.squeeze, h.stick_x, h.stick_y) };
                            }
                        }
                        if buttons_seen % 200 < entries.len() {
                            event(origin, "buttons", json!({ "seen": buttons_seen }));
                        }
                    }
                    ServerCoreEvent::RequestIDR => {
                        shared.idr_requests.fetch_add(1, Ordering::Relaxed);
                        shared.restart_from_idr.store(true, Ordering::SeqCst);
                    }
                    ServerCoreEvent::ViewsConfig(cfg) if !ipc.is_null() => {
                        // ALVR FOV is in radians (left/down negative); OVR wants positive tangents.
                        let t = |f: &alvr_common::Fov| [(-f.left).tan(), f.right.tan(), f.up.tan(), (-f.down).tan()];
                        let (l, r) = (t(&cfg.fov[0]), t(&cfg.fov[1]));
                        let fov8 = [l[0], l[1], l[2], l[3], r[0], r[1], r[2], r[3]];
                        if !fov8.iter().all(|x| x.is_finite() && *x > 0.0) {
                            // the visionOS client sends placeholder views (NaN FoV) about 10x/s until the user enters its
                            // immersive space: not a FoV, keep the last good one
                            let n = invalid_views;
                            invalid_views += 1;
                            if n == 0 || n.is_power_of_two() {
                                event(origin, "views_config_ignored", json!({ "count": n + 1, "reason": "non-finite or non-positive FoV" }));
                            }
                            continue;
                        }
                        let (a, b) = (cfg.local_view_transforms[0].position.to_array(), cfg.local_view_transforms[1].position.to_array());
                        let off6 = [a[0], a[1], a[2], b[0], b[1], b[2]];
                        // the main loop applies it: it may have to rebuild the encoder for the negotiated stream parameters first
                        *shared.pending_views.lock() = Some((fov8, off6));
                        event(origin, "views_config", json!({ "fov_tan": fov8, "eye_offsets": off6 }));
                    }
                    ServerCoreEvent::ViewsConfig(cfg) => event(origin, "views_config", json!({
                        "ipd_m": cfg.local_view_transforms[1].position.x - cfg.local_view_transforms[0].position.x,
                        "fov": cfg.fov.iter().map(|f| [f.left, f.right, f.up, f.down]).collect::<Vec<_>>(),
                    })),
                    ServerCoreEvent::SetOpenvrProperty { prop, .. }
                        if matches!(prop.key, alvr_session::OpenvrPropKey::AudioDefaultPlaybackDeviceIdString) =>
                    {
                        // server_core's game-audio thread names the playback device it loop-back captures (and the default device
                        // again when it stops). SteamVR would make that device the Windows default; nothing does here, so games
                        // must already play to it: warn if it is not the default (a configured `game_audio.device`).
                        let default_id = default_playback_device_id();
                        let n = shared.audio_device_events.fetch_add(1, Ordering::Relaxed);
                        event(origin, "audio_device", json!({
                            "n": n, "device_id": prop.value, "windows_default_id": default_id,
                            "is_windows_default": default_id.as_deref() == Some(prop.value.as_str()),
                        }));
                    }
                    ServerCoreEvent::ShutdownPending | ServerCoreEvent::RestartPending => {
                        event(origin, "shutdown_pending", json!(null))
                    }
                    _ => {}
                }
            }
        });
    }

    // Display clock: its own thread, so a long encode can never delay a vsync tick (the OpenXR app waits on it). Ticks follow ALVR's
    // pacing grid (`duration_until_next_vsync`); the interval to the next tick is published so the shim can predict display times.
    let vsync_ticks = Arc::new(AtomicU64::new(0));
    let vsync_stop = Arc::new(AtomicBool::new(false));
    let vsync_thread = if args.shim {
        let (ctx, ticks, period_ns, stop) = (Arc::clone(&ctx), Arc::clone(&vsync_ticks), Arc::clone(&vsync_period_ns), Arc::clone(&vsync_stop));
        let daemon = args.daemon;
        let clock_pipeline = pipeline.clone();
        let boundary_offset = Duration::from_secs_f64(args.boundary_offset_ms.max(0.0) / 1000.0);
        Some(thread::spawn(move || {
            #[cfg(windows)]
            unsafe {
                SetThreadPriority(GetCurrentThread(), 2); // THREAD_PRIORITY_HIGHEST
            }
            let ipc = ipc_addr as *mut std::ffi::c_void;
            let mut intervals: Vec<f64> = vec![];
            let mut last: Option<Instant> = None;
            let mut next = Instant::now();
            let mut grid: Option<Instant> = None;
            while !stop.load(Ordering::Relaxed) {
                // high-resolution timer to just before the tick, spin the rest (timing.rs)
                timing::sleep_until_or(next, &stop);
                let now = Instant::now();
                let nominal = Duration::from_nanos(period_ns.load(Ordering::Relaxed));
                // ALVR measures "until the next grid point" from its own, slightly later, Instant::now(); waking at now + interval
                // therefore lands a few microseconds BEFORE the grid point, and ALVR then answers "a few microseconds". Clamping
                // that to 0.5 ms produced a second tick 0.5 ms later on ~15-20% of ticks (~107 Hz instead of 90: apps ran at
                // 97-111 fps in the first headset test). A remainder under half a period means "this grid point": aim at the next.
                phase::set_period_ns(nominal.as_nanos() as u64);
                let mut interval = if FOLLOW_RATE.load(Ordering::Relaxed) || phase::enabled() {
                    // the client's display period (detected from its tracking timestamps): a free-running grid at that period
                    let mut g = grid.map(|g| g + nominal).unwrap_or(now + nominal);
                    let adj = phase::take_adjust_ns(); // the phase lock slides the grid (phase.rs)
                    if adj >= 0 {
                        g += Duration::from_nanos(adj as u64);
                    } else {
                        g = g.checked_sub(Duration::from_nanos((-adj) as u64)).unwrap_or(g);
                    }
                    if g < now + nominal / 2 {
                        g = now + nominal; // fell behind (a stall): restart the grid
                    }
                    grid = Some(g);
                    g.saturating_duration_since(now)
                } else {
                    grid = None;
                    ctx.duration_until_next_vsync().unwrap_or(nominal)
                };
                if interval < nominal / 2 {
                    interval += nominal;
                }
                LAST_TICK_NS.store(now.duration_since(origin).as_nanos() as u64, Ordering::Relaxed);
                NEXT_TICK_NS.store((now + interval).duration_since(origin).as_nanos() as u64, Ordering::Relaxed);
                unsafe { nvh::nvh_ipc_vsync(ipc, interval.as_secs_f64()) };
                ticks.fetch_add(1, Ordering::Relaxed);
                let tick_idx = VSYNC_TICKS.fetch_add(1, Ordering::Relaxed) + 1;
                if let Some(p) = &clock_pipeline {
                    // compositor boundary: the newest complete app frame goes to the encoder (pipeline.rs)
                    let b = if boundary_offset > Duration::ZERO && boundary_offset + Duration::from_millis(2) < interval {
                        sleep_spin_until(now + boundary_offset);
                        now + boundary_offset
                    } else {
                        now
                    };
                    p.on_boundary(tick_idx, b);
                }
                if let Some(l) = last {
                    let ms = now.duration_since(l).as_secs_f64() * 1000.0;
                    if ms > 1.8 * nominal.as_secs_f64() * 1000.0 {
                        event(origin, "vsync_late", json!({ "interval_ms": ms, "nominal_ms": nominal.as_secs_f64() * 1000.0 }));
                    }
                    intervals.push(ms);
                    if daemon {
                        bound_history(&mut intervals);
                    }
                }
                last = Some(now);
                next = now + interval;
            }
            intervals
        }))
    } else {
        None
    };
    let last_green_tick = 0u64;
    let mut last_sent_ts = Duration::ZERO;
    let mut stats_win = StatsWindow::new();
    let mut intake_ms: Vec<f64> = vec![];
    let last_intake: Option<Instant> = None;

    // sender (the "compositor + encoder" stand-in)
    let period = Duration::from_secs_f64(1.0 / args.fps);
    let mut idx = 0usize;
    let mut next = Instant::now();
    let mut connected_at: Option<Instant> = None;
    let mut dropped_no_ts = 0usize;
    #[derive(Clone, Copy)]
    struct HapticState {
        seq: u32,
        freq: f32,
        amp: f32,
        last_change: Instant,
        last_send: Instant,
    }
    let mut haptic_state = [HapticState { seq: 0, freq: 0.0, amp: 0.0, last_change: Instant::now() - Duration::from_secs(10), last_send: Instant::now() - Duration::from_secs(10) }; 2];
    let mut haptics_sent = 0usize;
    let mut green_active = false;
    let mut green_frames = 0usize;
    let min_idr_interval = Duration::from_millis(100);
    let mut last_idr_restart = Instant::now() - min_idr_interval;
    let mut idr_restarts_honoured = 0usize;
    let mut sent_ts: Vec<u64> = vec![];
    let views_path = args.config_dir.join("visionalvr_views.json");
    let mut last_views = load_views(&views_path);
    let mut views_published = false;
    let mut deferred_reconfig = false;
    let (mut encode_errors, consecutive_errors) = (0usize, 0usize);
    if !ipc.is_null() {
        push_user(ipc, user_gamma, debug_on);
    }
    struct GameRun {
        exe: String,
        start: Instant,
        frames0: usize,
        submitted0: u32,
        /// frame generation counters at the start: boundaries skipped for want of a tracking sample, outputs (synthetic + rendered)
        skips0: u64,
        outputs0: u64,
        /// the same baselines for the run record (games.json), restarted when the level changes during the run: a record belongs to one level
        seg_start: Instant,
        seg_frames0: usize,
        seg_submitted0: u32,
        seg_skips0: u64,
        seg_outputs0: u64,
    }
    // (boundaries skipped for want of a tracking sample, outputs) so far, for the game summaries
    let fg_counts = |p: &Option<Arc<pipeline::Pipeline>>| p.as_ref().map(|p| (p.fg_no_sample.load(Ordering::Relaxed), p.fg_synths.load(Ordering::Relaxed) + p.fg_reals.load(Ordering::Relaxed))).unwrap_or((0, 0));
    let skipped_text = |g: &GameRun, p: &Option<Arc<pipeline::Pipeline>>| {
        let (s, o) = fg_counts(p);
        let (s, o) = (s.wrapping_sub(g.skips0), o.wrapping_sub(g.outputs0));
        if s + o > 0 { format!(", {s} boundaries skipped for want of a tracking sample ({:.1} %)", 100.0 * s as f64 / (s + o) as f64) } else { String::new() }
    };
    let mut game: Option<GameRun> = None;
    // the run record (games.json): one (headset repeated share, host repeat share) sample per status tick (10 s) while a game runs
    let mut run_windows: Vec<(f64, f64, f64)> = Vec::new();
    let mut run_client0 = (0u64, 0u64);
    let mut run_rep0 = (0u64, 0u64);
    // scripted input (--auto-input): index of the next event to fire and the release instant of a pressed one
    let mut auto_next = 0usize;
    let mut auto_release: Option<(Instant, usize)> = None;
    let shim_submitted = |ipc: *mut std::ffi::c_void| -> u32 {
        let (mut m, mut a, mut b) = (0u32, 0u32, 0u32);
        if !ipc.is_null() {
            unsafe { nvh::nvh_ipc_shim_stats(ipc, &mut m, &mut a, &mut b) };
        }
        a.wrapping_add(b)
    };
    // the level changed during a run (the launcher, or the user): the segment played so far is recorded under the level it ran at (20 s or
    // more; the 2/3 segment of a Gears of War run was lost without this, 2026-10-17) and the record restarts here: a record belongs to one level
    let restart_segment = |game: &mut Option<GameRun>, pipeline: &Option<std::sync::Arc<pipeline::Pipeline>>, ipc: *mut std::ffi::c_void, windows: &mut Vec<(f64, f64, f64)>, client0: &mut (u64, u64), rep0: &mut (u64, u64), old_level: &str, args: &Args| {
        if let Some(g) = game.as_mut() {
            let (s, o) = fg_counts(pipeline);
            let seg_secs = g.seg_start.elapsed().as_secs_f64();
            if seg_secs > 20.0 {
                let (ds, d_o) = (s.wrapping_sub(g.seg_skips0), o.wrapping_sub(g.seg_outputs0));
                let skipped = if ds + d_o > 0 { ds as f64 / (ds + d_o) as f64 } else { 0.0 };
                let seg_frames = shared.frames_sent.load(Ordering::Relaxed) - g.seg_frames0;
                let seg_submitted = shim_submitted(ipc).wrapping_sub(g.seg_submitted0);
                let headset = shared.headset.lock().clone().map(|(_, n, _)| n).unwrap_or_default();
                let hz = pipeline.as_ref().map(|p| 1.0 / p.period().as_secs_f64()).unwrap_or(args.fps);
                games_record_run(args, origin, &g.exe, &headset, hz, old_level, seg_secs, seg_submitted as f64 / seg_secs, seg_frames as f64 / seg_secs, windows, skipped);
            }
            g.seg_start = Instant::now();
            g.seg_frames0 = shared.frames_sent.load(Ordering::Relaxed);
            g.seg_submitted0 = shim_submitted(ipc);
            g.seg_skips0 = s;
            g.seg_outputs0 = o;
            windows.clear();
            *client0 = logs::client_stats::client_frames_missed();
            *rep0 = pipeline.as_ref().map(|p| (p.fg_repeats.load(Ordering::Relaxed), p.fg_synths.load(Ordering::Relaxed) + p.fg_reals.load(Ordering::Relaxed))).unwrap_or((0, 0));
        }
    };
    let mut status_at = Instant::now();
    let mut motion_tap_seen = (0u32, 0u32);
    let (mut perf_rel, mut perf_late) = (0u64, 0u64);   // shim release counters at the last fg_perf line
    let mut perf_fg = [0u64; 6];   // pipeline counters at the last fg_perf line: synths, reals, no_sample, sample_waits, sample_wait_us, in_hand
    // (tap state, streams) last reported: the shim's DLSS motion-vector tap (docs/MOTION_VECTORS.md)
    struct BenchState {
        i: usize,
        start: Instant,
        measuring: bool,
        mstart: Instant,
        bytes0: u64,
        lost0: usize,
        frames0: usize,
        results: Vec<serde_json::Value>,
    }
    let mut bench: Option<BenchState> = None;
    let (mut st_frames, mut st_bytes, mut st_submitted, mut st_lost) = (0usize, 0u64, 0u32, 0usize);
    loop {
        if vision::QUIT.load(Ordering::SeqCst) {
            logs::general("INFO", "stop requested (VisionALVR.exe closed or sent quit)");
            break;
        }
        if !ipc.is_null() {
            // live settings from VisionALVR.exe
            if vision::COLOR_PENDING.swap(false, Ordering::SeqCst) {
                let c = [0, 1, 2, 3].map(|k| f32::from_bits(vision::COLOR_REQ[k].load(Ordering::SeqCst)));
                unsafe { nvh::nvh_ipc_set_color(ipc, c[0], c[1], c[2], c[3]) };
                event(origin, "color", json!({ "brightness": c[0], "contrast": c[1], "saturation": c[2], "sharpening": c[3] }));
            }
            let g = vision::GAMMA_REQ.swap(0, Ordering::SeqCst);
            let d = vision::DEBUG_REQ.swap(-1, Ordering::SeqCst);
            if g != 0 || d >= 0 {
                if g != 0 {
                    user_gamma = f32::from_bits(g);
                }
                if d >= 0 && (d == 1) != debug_on {
                    debug_on = d == 1;
                    logs::set_debug(debug_on);
                    logs::general("INFO", &format!("debug logging {}", logs::debug_dir().map(|p| format!("on: {}", p.display())).unwrap_or_else(|| "off".into())));
                }
                push_user(ipc, user_gamma, debug_on);
                event(origin, "user_settings", json!({ "gamma": user_gamma, "debug": debug_on, "debug_dir": logs::debug_dir() }));
            }
            // frame generation on/off from VisionALVR.exe: the generator is created or dropped on the running encoder (under the encoder lock, so
            // never while a frame is being encoded) and the shim's pacing follows at the next boundary; the setting is also what the next start reads
            let fgr = vision::FRAME_GEN_REQ.swap(-1, Ordering::SeqCst);
            if fgr >= 0 {
                let want = fgr == 1;
                let old_level = if fg_active { args.fg_rate.unwrap_or(FgRate::Half).name() } else { "off" };
                args.frame_gen = Some(want);
                if let Some(p) = pipeline.as_ref() {
                    set_frame_gen_live(origin, p.as_ref(), &args, want, &mut fg_active, ipc, "frame_gen");
                }
                restart_segment(&mut game, &pipeline, ipc, &mut run_windows, &mut run_client0, &mut run_rep0, old_level, &args);
            }
            // frame generation rate, the game's motion vectors, the fps cap and the content lag from VisionALVR.exe (live; the settings are read at start too)
            let rate = vision::FG_RATE_REQ.swap(-1, Ordering::SeqCst);
            if rate >= 0 {
                let r = FgRate::from_index(rate);
                let old_level = if fg_active { args.fg_rate.unwrap_or(FgRate::Half).name() } else { "off" };
                args.fg_rate = Some(r);
                if let Some(p) = pipeline.as_ref() {
                    p.set_frame_gen_rate(r, args.fg_lag_ms.unwrap_or(0.0));
                }
                apply_fg_pacing(ipc, pipeline.as_deref(), &args, fg_active);
                log_fg_config(origin, &args, fg_active, "frame_gen_rate");
                restart_segment(&mut game, &pipeline, ipc, &mut run_windows, &mut run_client0, &mut run_rep0, old_level, &args);
            }
            let mv = vision::GAME_MV_REQ.swap(-1, Ordering::SeqCst);
            if mv >= 0 {
                args.game_motion_vectors = mv == 1;
                unsafe { nvh::nvh_ipc_set_game_mv(ipc, mv as u32) };
                if let Some(p) = pipeline.as_ref() {
                    let g = p.enc.lock();
                    unsafe { nvh::nvh_fg_use_mv(g.handle, mv) };
                }
                log_fg_config(origin, &args, fg_active, "game_mv");
            }
            let cap = vision::FG_CAP_REQ.swap(-1, Ordering::SeqCst);
            if cap >= 0 {
                args.fg_cap_fps = Some(cap as f64);
                apply_fg_pacing(ipc, pipeline.as_deref(), &args, fg_active);
                log_fg_config(origin, &args, fg_active, "frame_gen_cap");
            }
            let lag = vision::FG_LAG_REQ.swap(-1, Ordering::SeqCst);
            if lag >= 0 {
                args.fg_lag_ms = Some(lag as f64 / 10.0);
                if let Some(p) = pipeline.as_ref() {
                    p.set_frame_gen_rate(args.fg_rate.unwrap_or(FgRate::Half), args.fg_lag_ms.unwrap_or(0.0));
                }
                log_fg_config(origin, &args, fg_active, "frame_gen_lag");
            }
            // game start / end, for the general log and the GUI
            let alive = app_alive();
            if alive && game.is_none() {
                let mut b = [0 as std::ffi::c_char; 256];
                unsafe { nvh::nvh_ipc_app_exe(ipc, b.as_mut_ptr(), b.len() as i32) };
                let exe = unsafe { std::ffi::CStr::from_ptr(b.as_ptr()) }.to_string_lossy().to_string();
                let exe = if exe.is_empty() { "(unknown app)".to_string() } else { exe };
                logs::general("INFO", &format!("game started: {exe}"));
                event(origin, "game_started", json!({ "exe": exe }));
                run_windows.clear();
                run_client0 = logs::client_stats::client_frames_missed();
                run_rep0 = pipeline.as_ref().map(|p| (p.fg_repeats.load(Ordering::Relaxed), p.fg_synths.load(Ordering::Relaxed) + p.fg_reals.load(Ordering::Relaxed))).unwrap_or((0, 0));
                let (skips0, outputs0) = fg_counts(&pipeline);
                let (frames0, submitted0, now) = (shared.frames_sent.load(Ordering::Relaxed), shim_submitted(ipc), Instant::now());
                game = Some(GameRun { exe, start: now, frames0, submitted0, skips0, outputs0, seg_start: now, seg_frames0: frames0, seg_submitted0: submitted0, seg_skips0: skips0, seg_outputs0: outputs0 });
            } else if !alive && game.is_some() {
                let g = game.take().unwrap();
                let secs = g.start.elapsed().as_secs_f64();
                let frames = shared.frames_sent.load(Ordering::Relaxed) - g.frames0;
                let submitted = shim_submitted(ipc).wrapping_sub(g.submitted0);
                logs::general("INFO", &format!(
                    "game ended: {} after {}m{:02}s, {} frames rendered ({:.1} fps), {} streamed ({:.1} fps){}",
                    g.exe, secs as u64 / 60, secs as u64 % 60, submitted, submitted as f64 / secs.max(1e-3), frames, frames as f64 / secs.max(1e-3), skipped_text(&g, &pipeline)));
                event(origin, "game_ended", json!({ "exe": g.exe, "seconds": secs, "frames_rendered": submitted, "frames_streamed": frames }));
                let seg_secs = g.seg_start.elapsed().as_secs_f64();
                if seg_secs > 20.0 {
                    let (s0, o0) = fg_counts(&pipeline);
                    let (s, o) = (s0.wrapping_sub(g.seg_skips0), o0.wrapping_sub(g.seg_outputs0));
                    let skipped = if s + o > 0 { s as f64 / (s + o) as f64 } else { 0.0 };
                    let seg_frames = shared.frames_sent.load(Ordering::Relaxed) - g.seg_frames0;
                    let seg_submitted = shim_submitted(ipc).wrapping_sub(g.seg_submitted0);
                    let level = if fg_active { args.fg_rate.unwrap_or(FgRate::Half).name() } else { "off" };
                    let headset = shared.headset.lock().clone().map(|(_, n, _)| n).unwrap_or_default();
                    let hz = pipeline.as_ref().map(|p| 1.0 / p.period().as_secs_f64()).unwrap_or(args.fps);
                    games_record_run(&args, origin, &g.exe, &headset, hz, level, seg_secs, seg_submitted as f64 / seg_secs, seg_frames as f64 / seg_secs, &run_windows, skipped);
                }
            }
            if let Some(g) = &game {
                if let Some((until, side)) = auto_release {
                    if Instant::now() >= until {
                        unsafe { nvh::nvh_ipc_publish_input(ipc, side as i32, 0, 0, 0.0, 0.0, 0.0, 0.0) };
                        auto_release = None;
                    }
                }
                if auto_release.is_none() && auto_next < args.auto_input.len() && g.start.elapsed().as_secs_f64() >= args.auto_input[auto_next].0 {
                    let (t, side, mask, trigger) = args.auto_input[auto_next];
                    unsafe { nvh::nvh_ipc_publish_input(ipc, side as i32, mask, mask, trigger, 0.0, 0.0, 0.0) };
                    auto_release = Some((Instant::now() + Duration::from_millis(150), side));
                    event(origin, "auto_input", json!({ "t": t, "hand": side, "clicks": mask, "trigger": trigger }));
                    auto_next += 1;
                }
            }
        }
        if status_at.elapsed() >= Duration::from_secs(10) {
            // every 10 s for VisionALVR.exe's status lines (the logs have the 2 s detail)
            let secs = status_at.elapsed().as_secs_f64();
            let (frames, bytes, submitted, lost) = (shared.frames_sent.load(Ordering::Relaxed), shared.bytes_sent.load(Ordering::Relaxed),
                shim_submitted(ipc), logs::client_stats::packets_lost_total());
            let rendered = submitted.wrapping_sub(st_submitted) as usize;
            let streamed = frames - st_frames;
            event(origin, "status", json!({
                "connected": shared.client_connected.load(Ordering::SeqCst),
                "headset": shared.headset.lock().clone().map(|(h, n, ip)| json!({ "hostname": h, "name": n, "ip": ip })),
                "game": game.as_ref().map(|g| g.exe.clone()),
                "app_fps": rendered as f64 / secs, "stream_fps": streamed as f64 / secs,
                "frames_not_streamed": rendered.saturating_sub(streamed),
                "mbps": (bytes - st_bytes) as f64 * 8.0 / secs / 1e6,
                "packets_lost": lost.saturating_sub(st_lost),
                "client": logs::client_stats::take(1),
                "gamma": user_gamma, "debug": debug_on,
                "fg_on": fg_active, "fg_rate": args.fg_rate.unwrap_or(FgRate::Half).name(),
            }));
            (st_frames, st_bytes, st_submitted, st_lost) = (frames, bytes, submitted, lost);
            status_at = Instant::now();
            if game.is_some() {
                // one window of the run record: the headset's repeated display periods (its own statistics) and the host's repeats
                let (cf, cm) = logs::client_stats::client_frames_missed();
                let (d_f, d_m) = (cf.saturating_sub(run_client0.0), cm.saturating_sub(run_client0.1));
                run_client0 = (cf, cm);
                let (rep, out) = pipeline.as_ref().map(|p| (p.fg_repeats.load(Ordering::Relaxed), p.fg_synths.load(Ordering::Relaxed) + p.fg_reals.load(Ordering::Relaxed))).unwrap_or((0, 0));
                let (d_r, d_o) = (rep.saturating_sub(run_rep0.0), out.saturating_sub(run_rep0.1));
                run_rep0 = (rep, out);
                if d_f + d_m > 0 {
                    run_windows.push((d_m as f64 / (d_f + d_m) as f64, if d_o + d_r > 0 { d_r as f64 / (d_o + d_r) as f64 } else { 0.0 }, rendered as f64 / secs));
                }
            }
            if fg_active {
                // bottleneck log for frame generation: the game's own pace (shim envelope, late releases = free-running), the generator's
                // GPU times, the uncapped mode's lag and repeats, and the GPU's utilisation and clocks (NVML)
                if let Some(p) = pipeline.as_ref() {
                    let u = p.fg_uncapped_stats();
                    let (mut waits, mut app_ms, mut rel, mut late, mut gh, mut gs) = (0u32, 0f32, 0u64, 0u64, 0u64, 0u64);
                    unsafe { nvh::nvh_ipc_shim_pacing(ipc, &mut waits, &mut app_ms, &mut rel, &mut late, &mut gh, &mut gs) };
                    let mut gb = [0 as std::ffi::c_char; 512];
                    let gpu = if unsafe { nvh::nvh_gpu_sample(gb.as_mut_ptr(), gb.len() as i32) } > 0 {
                        unsafe { std::ffi::CStr::from_ptr(gb.as_ptr()) }.to_string_lossy().to_string()
                    } else { String::new() };
                    let gen = p.fg_stats_json();
                    let (d_rel, d_late) = (rel.wrapping_sub(perf_rel), late.wrapping_sub(perf_late));
                    (perf_rel, perf_late) = (rel, late);
                    logs::general("INFO", &format!("frame generation perf: app {:.1} fps (envelope {app_ms:.1} ms, {d_late}/{d_rel} releases late), stream {:.1} fps, game period {:.1} ms (p50 {:.1} p95 {:.1} max {:.1}), lag {:.1} ms (slack p5 {:.1} p50 {:.1}), mean t {:.2} mean age {:.2}, behind {} ahead {}, gpu [{gpu}]",
                        rendered as f64 / secs, streamed as f64 / secs, u.game_period_ms, u.interval_p50, u.interval_p95, u.interval_max, u.lag_ms, u.slack_p5, u.slack_p50, u.mean_t, u.mean_age, u.behind, u.ahead));
                    // the tracking samples the outputs are stamped with: skipped boundaries (none newer than the last sent frame), waits for one,
                    // stamps that left one in hand, and the spacing of the headset's samples at the host (the cause of the misses, 2026-10-16)
                    let now_fg = [p.fg_synths.load(Ordering::Relaxed), p.fg_reals.load(Ordering::Relaxed), p.fg_no_sample.load(Ordering::Relaxed),
                        p.fg_sample_waits.load(Ordering::Relaxed), p.fg_sample_wait_us.load(Ordering::Relaxed), p.fg_in_hand.load(Ordering::Relaxed)];
                    let d: Vec<u64> = now_fg.iter().zip(perf_fg.iter()).map(|(a, b)| a.wrapping_sub(*b)).collect();
                    perf_fg = now_fg;
                    let outputs = d[0] + d[1];
                    let (gap_p95, gap_max, gap_over15, gap_n) = *TRACKING_GAP_LAST.lock();
                    if outputs + d[2] > 0 {
                        logs::general("INFO", &format!("frame generation samples: {} outputs, {} boundaries skipped for want of a tracking sample ({:.1} %), {} waited for one (mean {:.1} ms), {} stamped with one in hand; tracking spacing p95 {gap_p95:.1} ms max {gap_max:.1} ({gap_over15}/{gap_n} gaps over 15 ms)",
                            outputs, d[2], 100.0 * d[2] as f64 / (outputs + d[2]) as f64, d[3], if d[3] > 0 { d[4] as f64 / 1000.0 / d[3] as f64 } else { 0.0 }, d[5]));
                    }
                    event(origin, "fg_perf", json!({ "app_fps": rendered as f64 / secs, "stream_fps": streamed as f64 / secs, "app_envelope_ms": app_ms,
                        "outputs": outputs, "no_sample": d[2], "sample_waits": d[3], "sample_wait_ms_sum": d[4] as f64 / 1000.0, "in_hand": d[5],
                        "tracking_gap_p95_ms": gap_p95, "tracking_gap_max_ms": gap_max, "tracking_gaps_over_15ms": gap_over15, "tracking_gaps": gap_n,
                        "releases": d_rel, "releases_late": d_late, "slot_waits": waits, "game_period_ms": u.game_period_ms, "lag_ms": u.lag_ms, "mean_t": u.mean_t,
                        "unc_synths": u.synths, "mean_age": u.mean_age, "behind": u.behind, "ahead": u.ahead, "arrivals": u.arrivals, "slack_ms_p5": u.slack_p5, "slack_ms_p50": u.slack_p50,
                        "interval_ms_p50": u.interval_p50, "interval_ms_p95": u.interval_p95, "interval_ms_max": u.interval_max, "gpu_csv": gpu,
                        "rate": args.fg_rate.unwrap_or(FgRate::Half).name(),
                        "gen": serde_json::from_str::<serde_json::Value>(&gen).unwrap_or(serde_json::Value::Null) }));
                }
            }
            if !ipc.is_null() {
                let (mut tap, mut streams, mut pubs) = (0u32, 0u32, 0u64);
                unsafe { nvh::nvh_ipc_motion_tap(ipc, &mut tap, &mut streams, &mut pubs) };
                if (tap, streams) != motion_tap_seen {
                    motion_tap_seen = (tap, streams);
                    let state = match tap { 0 => "no provider in the game", 1 => "attached", 2 => "provider has another ABI version", _ => "provider refused the consumer" };
                    logs::general("INFO", &format!("game motion vectors: {state}, {streams} DLSS stream(s), {pubs} evaluates seen"));
                    event(origin, "motion_tap", json!({ "state": tap, "streams": streams, "evaluates": pubs }));
                }
            }
        }
        if args.shim {
            // Connect: ALVR persisted the negotiated stream (openvr_config) before it reported ClientConnected, so the encoder is
            // checked against it right here, before any frame. Frames do NOT wait for the headset's ViewsConfig: the stock
            // visionOS client sends its views only after it has decoded a first frame (when it is already in its immersive
            // space), so waiting for them would deadlock. Views only feed the shim (FoV/IPD for the OpenXR app).
            let connect = shared.connect_pending.swap(false, Ordering::SeqCst);
            if connect {
                views_published = false;
            }
            if connect || (deferred_reconfig && !app_alive()) {
                // Rebuild the encoder if the negotiated stream (resolution, refresh rate, foveation) differs from what it was built
                // from (a stale `openvr_config` cache). Not while an app runs: its swapchain/ring sizes are fixed; then it is
                // retried as soon as the app has exited.
                let want = read_stream_cfg(&layout.session());
                if want == cur_cfg {
                    deferred_reconfig = false;
                } else if app_alive() {
                    if !deferred_reconfig {
                        logs::general("WARN", "the headset negotiated a different stream while a game runs: the encoder is rebuilt when the game exits (restart the game)");
                        event(origin, "reconfigure_deferred", json!({ "running": format!("{cur_cfg:?}"), "negotiated": format!("{want:?}") }));
                    }
                    deferred_reconfig = true;
                } else {
                    let mut g = pipeline.as_ref().expect("pipeline").enc.lock();
                    unsafe { nvh::nvh_destroy(g.handle) };
                    let (w, h, session_fps) = configure_encoder(&layout.session(), args.split_encode, qp_on, &host_keys);
                    eye_size = (w / 2, h);
                    if !args.fps_explicit {
                        args.fps = session_fps;
                    }
                    let (mut ew, mut eh) = (0i32, 0i32);
                    unsafe { nvh::nvh_encoded_size(&mut ew, &mut eh) };
                    let user = &mut *g.live as *mut LiveCtx as *mut std::ffi::c_void;
                    let mut err = [0 as std::ffi::c_char; 256];
                    g.handle = unsafe { nvh::nvh_create(ew, eh, args.noise, on_packet, on_params, user, err.as_mut_ptr(), 256) };
                    if g.handle.is_null() {
                        eprintln!("NVENC re-init failed: {}", unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }.to_string_lossy());
                        std::process::exit(4);
                    }
                    if let Some(duty) = args.gpu_keepalive {
                        unsafe { nvh::nvh_keepalive(g.handle, 1, duty) };
                    }
                    if args.frame_gen.unwrap_or(false) {
                        // a new encoder: the generator is created again for the new frame size
                        fg_active = enable_frame_gen(origin, g.handle, &args);
                        let p = pipeline.as_ref().expect("pipeline");
                        p.set_frame_gen(fg_active);
                        apply_fg_pacing(ipc, Some(p.as_ref()), &args, fg_active);
                    }
                    nvenc_cfg = describe_encoder(origin, g.handle, &args.dump_qpmap);
                    logs::general("INFO", &format!("encoder rebuilt for the negotiated stream: {ew}x{eh} (was {}x{})", enc_size.0, enc_size.1));
                    event(origin, "reconfigured", json!({
                        "from": format!("{cur_cfg:?}"), "to": format!("{want:?}"),
                        "encoder": [ew, eh], "was": [enc_size.0, enc_size.1], "was_deferred": deferred_reconfig,
                    }));
                    enc_size = (ew, eh);
                    vsync_period_ns.store((1e9 / args.fps) as u64, Ordering::Relaxed);
                    cur_cfg = want;
                    reconfigs += 1;
                    idx = 0;
                    deferred_reconfig = false;
                    views_published = false; // new eye size for the shim
                }
                if connect {
                    logs::general("INFO", &vision::stream_summary(&layout.session(), enc_size));
                    shared.stream_ready.store(true, Ordering::SeqCst);
                }
            }
            if let Some(v) = shared.pending_views.lock().take() {
                last_views = Some(v);
                save_views(&views_path, &v);
                views_published = false;
            }
            if !views_published && shared.stream_ready.load(Ordering::SeqCst) {
                // the shim presents the device to an OpenXR app only once it has the FoV: this connection's views, or the ones
                // the same headset sent last time (they arrive again right after the first decoded frame)
                if let Some((fov8, off6)) = last_views {
                    unsafe {
                        nvh::nvh_ipc_config(ipc, pipeline.as_ref().map(|p| p.enc.lock().handle).unwrap_or(enc_handle), args.fps, eye_size.0, eye_size.1, fov8.as_ptr(), off6.as_ptr(), 10, read_encoding_gamma(&layout.session()));
                        nvh::nvh_ipc_set_connected(ipc, 1); // after the config: the shim waits for this flag
                    }
                    views_published = true;
                }
            }
        }
        let connected = shared.client_connected.load(Ordering::SeqCst);
        if let Some(p) = &pipeline {
            p.active.store(connected && shared.stream_ready.load(Ordering::SeqCst), Ordering::Relaxed);
        }
        if connected && connected_at.is_none() {
            connected_at = Some(Instant::now());
        }
        if !args.daemon {
            if !connected && connected_at.is_none() && origin.elapsed().as_secs_f64() > args.connect_timeout {
                break;
            }
            if let Some(t) = connected_at {
                if t.elapsed().as_secs_f64() > args.seconds {
                    break;
                }
            }
        }
        #[cfg(feature = "simrig")]
        if simrig::finished() {
            break;
        }
        if let Some(n) = args.exit_after_frames {
            if shared.frames_sent.load(Ordering::Relaxed) >= n {
                break;
            }
        }

        if args.shim {
            // Display clock + frame intake. The shim (inside the OpenXR app) waits on our vsync and posts composed
            // side-by-side frames; each one is encoded as soon as it arrives.
            // App vibration (ovr_SetControllerVibration) -> ALVR haptic pulses for the headset's controllers.
            for side in 0..2usize {
                let (mut seq, mut freq, mut amp) = (0u32, 0f32, 0f32);
                unsafe { nvh::nvh_ipc_poll_haptic(ipc, side as i32, &mut seq, &mut freq, &mut amp) };
                let st = &mut haptic_state[side];
                if seq != st.seq {
                    st.seq = seq;
                    st.last_change = Instant::now();
                    st.freq = freq;
                    st.amp = amp;
                }
                let active = st.amp > 0.0 && st.last_change.elapsed() < Duration::from_millis(150);
                if active && connected && st.last_send.elapsed() >= Duration::from_millis(30) {
                    st.last_send = Instant::now();
                    haptics_sent += 1;
                    ctx.send_haptics(Haptics {
                        device_id: if side == 0 { *HAND_LEFT_ID } else { *HAND_RIGHT_ID },
                        duration: Duration::from_millis(40),
                        frequency: if st.freq > 1.0 { st.freq } else if st.freq > 0.0 { st.freq * 320.0 } else { 160.0 }, // VDXR passes Hz; OVR docs say 0..1
                        amplitude: st.amp,
                    });
                }
            }
            if !connected || !shared.stream_ready.load(Ordering::SeqCst) {
                thread::sleep(Duration::from_millis(2));
                continue;
            }
            // The pipeline threads do intake / pick / encode / send; this loop keeps the bookkeeping and the statistics.
            let p = pipeline.as_ref().expect("pipeline");
            if let Some(msg) = p.failed.lock().clone() {
                eprintln!("encode failed: {msg}");
                break;
            }
            let d = p.drain();
            encode_ms.extend(d.encode_ms);
            compose_ms.extend(d.compose_ms);
            ffr_ms.extend(d.ffr_ms);
            ffr_wait_ms.extend(d.ffr_wait_ms);
            intake_ms.extend(d.intake_ms);
            frame_rows.extend(d.frame_rows);
            sent_ts.extend(d.sent_ts);
            green_frames = p.green_frames.load(Ordering::Relaxed) as usize;
            green_active = p.green_frames.load(Ordering::Relaxed) > 0 && !app_alive();
            idr_restarts_honoured = p.idr_restarts.load(Ordering::Relaxed) as usize;
            encode_errors = p.encode_errors.load(Ordering::Relaxed) as usize;
            dropped_no_ts = p.frames_before_first_tracking.load(Ordering::Relaxed) as usize;
            if let Some(ts) = sent_ts.last() {
                last_sent_ts = last_sent_ts.max(Duration::from_nanos(*ts));
            }
            let slices = { let g = p.enc.lock(); (g.live.slice_samples, g.live.slices) };
            let shim_stats = {
                let (mut m, mut a, mut b) = (0u32, 0u32, 0u32);
                unsafe { nvh::nvh_ipc_shim_stats(ipc, &mut m, &mut a, &mut b) };
                (m, a, b)
            };
            stats_win.tick(origin, &encode_ms, shared.bytes_sent.load(Ordering::Relaxed), Some(slices), Some(shim_stats), &intake_ms, &ffr_wait_ms, Some(p.stats_json()));
            if args.daemon {
                // the logon-task host runs for days: keep the per-frame histories bounded
                stats_win.first_idx = stats_win.first_idx.saturating_sub(bound_history(&mut encode_ms));
                bound_history(&mut compose_ms);
                bound_history(&mut ffr_ms);
                bound_history(&mut ffr_wait_ms);
                bound_history(&mut frame_rows);
                bound_history(&mut sent_ts);
                bound_history(&mut intake_ms);
            }
            let _ = (last_green_tick, consecutive_errors, min_idr_interval, last_idr_restart, last_intake);
            thread::sleep(Duration::from_millis(2));
            continue;
        }

        next += period;
        thread::sleep(next.saturating_duration_since(Instant::now()));
        if !connected {
            continue;
        }
        if let (Some(steps), Some(l)) = (&args.bench_network, live.as_mut()) {
            // Network benchmark: random frames (nothing for the encoder to reuse, so each frame costs the full bitrate) at each
            // bitrate step; 2 s to settle, then `bench_step_s` measured with ALVR's own client statistics.
            let now = Instant::now();
            let b = bench.get_or_insert_with(|| {
                l.bitrate_override = steps[0] as u64 * 1_000_000;
                event(origin, "bench_step_start", json!({ "step": 0, "target_mbps": steps[0] }));
                BenchState { i: 0, start: now, measuring: false, mstart: now, bytes0: 0, lost0: 0, frames0: 0, results: vec![] }
            });
            if !b.measuring && now.duration_since(b.start) >= Duration::from_secs(2) {
                logs::client_stats::take(2);
                b.measuring = true;
                b.mstart = now;
                b.bytes0 = shared.bytes_sent.load(Ordering::Relaxed);
                b.lost0 = logs::client_stats::packets_lost_total();
                b.frames0 = shared.frames_sent.load(Ordering::Relaxed);
            }
            if b.measuring && now.duration_since(b.mstart).as_secs_f64() >= args.bench_step_s {
                let secs = now.duration_since(b.mstart).as_secs_f64();
                let c = logs::client_stats::take(2).unwrap_or(json!({}));
                let r = json!({
                    "target_mbps": steps[b.i],
                    "actual_mbps": (shared.bytes_sent.load(Ordering::Relaxed) - b.bytes0) as f64 * 8.0 / secs / 1e6,
                    "sent_fps": (shared.frames_sent.load(Ordering::Relaxed) - b.frames0) as f64 / secs,
                    "packets_lost_per_s": (logs::client_stats::packets_lost_total().saturating_sub(b.lost0)) as f64 / secs,
                    "client_fps": c["client_fps"], "total_latency_ms": c["total_latency_ms"], "encode_ms": c["encode_ms"],
                    "network_ms": c["network_ms"], "decode_ms": c["decode_ms"], "decoder_queue_ms": c["decoder_queue_ms"],
                });
                event(origin, "bench_step", r.clone());
                b.results.push(r);
                b.i += 1;
                if b.i < steps.len() {
                    l.bitrate_override = steps[b.i] as u64 * 1_000_000;
                    b.start = now;
                    b.measuring = false;
                    event(origin, "bench_step_start", json!({ "step": b.i, "target_mbps": steps[b.i] }));
                } else {
                    let res = bench_recommendation(&b.results, args.fps);
                    logs::general("INFO", &format!("network benchmark: {}", res));
                    event(origin, "bench_result", res);
                    break;
                }
            }
        }
        // Like ALVR's own driver, keep sending even before tracking arrives (this also opens the return path
        // through firewalls that only admit UDP replies to flows the server started). Until the first real
        // tracking packet, stamp frames with time since connect.
        let ts = match *shared.latest_sample_ts.lock() {
            Some(ts) => ts,
            None => {
                dropped_no_ts += 1;
                connected_at.map(|t| t.elapsed()).unwrap_or_default()
            }
        };
        if shared.restart_from_idr.load(Ordering::SeqCst) && last_idr_restart.elapsed() >= min_idr_interval {
            shared.restart_from_idr.store(false, Ordering::SeqCst);
            last_idr_restart = Instant::now();
            idr_restarts_honoured += 1;
            idx = 0;
        }
        if let Some(l) = live.as_mut() {
            // idx was reset to 0 by an honoured IDR request (or first frame after connect)
            let force_idr = idx == 0;
            let frame_no = shared.frames_sent.load(Ordering::Relaxed);
            idx += 1;
            l.packet_bytes = 0;
            l.packet_idr = false;
            let mut err = [0 as std::ffi::c_char; 256];
            let ms = unsafe { nvh::nvh_encode(enc_handle, frame_no as u32, ts.as_nanos() as u64, force_idr as i32, err.as_mut_ptr(), 256) };
            if ms < 0.0 {
                eprintln!("encode failed: {}", unsafe { std::ffi::CStr::from_ptr(err.as_ptr()) }.to_string_lossy());
                break;
            }
            encode_ms.push(ms);
            stats_win.tick(origin, &encode_ms, shared.bytes_sent.load(Ordering::Relaxed) + l.packet_bytes as u64, Some((l.slice_samples, l.slices)), None, &[], &[], None);
            frame_rows.push(format!("{frame_no},{},{},{},{ms:.2}", ts.as_nanos(), l.packet_bytes, l.packet_idr as u8));
            sent_ts.push(ts.as_nanos() as u64);
            shared.frames_sent.fetch_add(1, Ordering::Relaxed);
            shared.bytes_sent.fetch_add(l.packet_bytes as u64, Ordering::Relaxed);
            continue;
        }
        let au = &aus[idx % aus.len()];
        idx += 1;
        let (config, frame) = split_config(&au.data);
        if let Some(c) = config {
            ctx.set_video_config_nals(c.to_vec(), CodecType::Hevc);
            shared.config_sent.fetch_add(1, Ordering::Relaxed);
        }
        ctx.send_video_nal(ts, frame.to_vec(), au.is_idr);
        ctx.report_composed(ts, Duration::from_millis(1));
        ctx.report_present(ts, Duration::from_millis(1));
        sent_ts.push(ts.as_nanos() as u64);
        shared.frames_sent.fetch_add(1, Ordering::Relaxed);
        shared.bytes_sent.fetch_add(frame.len() as u64, Ordering::Relaxed);
    }

    if let Some(g) = game.take() {
        // the host stops while a game still runs (VisionALVR.exe closed mid-game): its summary still belongs in the log
        let secs = g.start.elapsed().as_secs_f64();
        let frames = shared.frames_sent.load(Ordering::Relaxed) - g.frames0;
        let submitted = shim_submitted(ipc).wrapping_sub(g.submitted0);
        logs::general("INFO", &format!(
            "game ended: {} after {}m{:02}s, {} frames rendered ({:.1} fps), {} streamed ({:.1} fps){} (streamer stopping)",
            g.exe, secs as u64 / 60, secs as u64 % 60, submitted, submitted as f64 / secs.max(1e-3), frames, frames as f64 / secs.max(1e-3), skipped_text(&g, &pipeline)));
        event(origin, "game_ended", json!({ "exe": g.exe, "seconds": secs, "frames_rendered": submitted, "frames_streamed": frames, "host_stopping": true }));
    }
    let frames = shared.frames_sent.load(Ordering::Relaxed);
    let connected = connected_at.is_some();
    let distinct_ts = {
        let mut v = sent_ts.clone();
        v.dedup();
        v.len()
    };
    vsync_stop.store(true, Ordering::Relaxed);
    discovery_stop.store(true, Ordering::Relaxed);
    let vsync_intervals = vsync_thread.map(|t| t.join().unwrap_or_default()).unwrap_or_default();
    let mut pipeline_report = json!(null);
    if let Some(p) = pipeline.take() {
        p.stop();
        pipeline_report = p.stats_json();
        let d = p.drain();
        encode_ms.extend(d.encode_ms);
        frame_rows.extend(d.frame_rows);
        sent_ts.extend(d.sent_ts);
        match Arc::try_unwrap(p) {
            Ok(p) => {
                let e = p.enc.into_inner();
                enc_handle = e.handle;
                live = Some(e.live);
            }
            Err(p) => {
                enc_handle = p.enc.lock().handle; // threads are joined: nobody else uses it any more
                std::mem::forget(p);
            }
        }
    }
    let vsync_intervals_json = interval_stats(&vsync_intervals, 1000.0 / args.fps);
    let encoder_json = if args.live {
        let mut v = encode_ms.clone();
        v.sort_by(|a, b| a.partial_cmp(b).unwrap());
        json!({
            "width": enc_size.0, "height": enc_size.1,
            "shim_compose_ms_avg": compose_ms.iter().sum::<f64>() / compose_ms.len().max(1) as f64,
            "ffr_gpu_ms_avg": ffr_ms.iter().sum::<f64>() / ffr_ms.len().max(1) as f64,
            "nvenc_engines": unsafe { nvh::nvh_encoder_engines(enc_handle) },
            "split_encode_mode": args.split_encode,
            "slices_per_frame": live.as_ref().map(|l| l.slices as f64 / l.slice_samples.max(1) as f64),
            "callback_ms_avg": live.as_ref().map(|l| l.cb_ns as f64 / 1e6 / l.packets.max(1) as f64),
            "params_ms_avg": live.as_ref().map(|l| l.params_ns as f64 / 1e6 / encode_ms.len().max(1) as f64),
            "encode_ms": {
                "n": v.len(),
                "avg": v.iter().sum::<f64>() / v.len().max(1) as f64,
                "p50": v.get(v.len() / 2).copied().unwrap_or(0.0),
                "p95": v.get(((v.len() as f64) * 0.95) as usize).copied().unwrap_or(0.0),
                "max": v.last().copied().unwrap_or(0.0),
            },
        })
    } else {
        json!(null)
    };
    let pass = connected && frames >= args.min_frames;
    let report = json!({
        "pass": pass,
        "connected": connected,
        "frames_sent": frames,
        "bytes_sent": shared.bytes_sent.load(Ordering::Relaxed),
        "distinct_timestamps_sent": distinct_ts,
        "config_nal_sends": shared.config_sent.load(Ordering::Relaxed),
        "idr_requests": shared.idr_requests.load(Ordering::Relaxed),
        "idr_restarts_honoured": idr_restarts_honoured,
        "tracking_events": shared.tracking_events.load(Ordering::Relaxed),
        "frames_before_first_tracking": dropped_no_ts,
        "source_access_units": aus.len(),
        "source_idr": idr_count,
        "fps_target": args.fps,
        "refresh_hz": args.fps,
        "live": args.live,
        "haptics_sent": haptics_sent,
        "green_frames_sent": green_frames,
        "green_active_at_exit": green_active,
        "reconfigurations": reconfigs,
        "reconfigure_still_deferred": deferred_reconfig,
        "encode_errors": encode_errors,
        "audio": json!({ "config": audio_cfg, "device_events": shared.audio_device_events.load(Ordering::Relaxed) }),
        "shim": if ipc.is_null() { json!(null) } else {
            let (mut m, mut a, mut b) = (0u32, 0u32, 0u32);
            unsafe { nvh::nvh_ipc_shim_stats(ipc, &mut m, &mut a, &mut b) };
            json!({ "submit_mode": if m == 1 { "async" } else { "sync" }, "ts_matched": a, "ts_fallback": b })
        },
        "nvenc_config": nvenc_cfg,
        "qp_map": qp_on,
        "pacing": json!({ "vsync_ticks": vsync_ticks.load(Ordering::Relaxed), "vsync_interval_ms": vsync_intervals_json, "frame_intake_ms": interval_stats(&intake_ms, 1000.0 / args.fps) }),
        "encoder": encoder_json,
        "pipeline": pipeline_report,
    });
    if let Some(p) = &args.frames_csv {
        fs::write(p, format!("frame,ts_ns,bytes,idr,encode_ms\n{}", frame_rows.join("\n"))).ok();
    }
    if !ipc.is_null() {
        unsafe { nvh::nvh_ipc_set_connected(ipc, 0) };
    }
    if !enc_handle.is_null() {
        unsafe { nvh::nvh_destroy(enc_handle) };
    }
    if !ipc.is_null() {
        unsafe { nvh::nvh_ipc_close(ipc) };
    }
    drop(live);
    if let Some(p) = &args.report {
        if let Err(e) = fs::write(p, serde_json::to_string_pretty(&report).unwrap()) {
            eprintln!("\ncannot write report {}: {e}", p.display());
        }
    }
    println!("{}", json!({ "event": "report", "data": report }));
    // dropping ServerCoreContext joins its threads (ALVR un-mutes the PC speakers here); do it explicitly before exit
    drop(ctx);
    logs::general("INFO", &format!("host stop ({} frames streamed{})", frames, if encode_errors > 0 { format!(", {encode_errors} encode errors") } else { String::new() }));
    std::process::exit(if !connected { 2 } else if pass { 0 } else { 1 });
}
