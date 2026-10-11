//! Headless mock ALVR client for the harness: no window, no real decoder.
//!
//! Behaves like a headset as far as the ALVR protocol goes (discovery, stream config, scripted head pose,
//! frame/compositor/submit statistics), and writes the received video bitstream to a file. A JSON report and
//! the exit code say whether it worked.
//!
//! Exit codes: 0 = pass, 1 = streaming ran but fewer than --min-frames arrived, 2 = never connected,
//! 3 = bad arguments.
use alvr_client_core::{ClientCapabilities, ClientCoreContext, ClientCoreEvent};
use alvr_common::{
    glam::{Quat, UVec2, Vec3},
    parking_lot::Mutex,
    DeviceMotion, Fov, Pose, RelaxedAtomic, ViewParams, HAND_LEFT_ID, HAND_RIGHT_ID, HEAD_ID, LEFT_SQUEEZE_VALUE_ID,
    LEFT_THUMBSTICK_X_ID, LEFT_THUMBSTICK_Y_ID, LEFT_X_CLICK_ID, QUEST_CONTROLLER_PROFILE_ID, RIGHT_A_CLICK_ID,
    RIGHT_TRIGGER_VALUE_ID,
};
use alvr_packets::{ButtonEntry, ButtonValue, FaceData};
use serde_json::json;
use std::{
    collections::VecDeque,
    collections::HashMap,
    fs::File,
    io::Write,
    sync::{Arc, Weak},
    thread,
    time::{Duration, Instant},
};

struct Args {
    out: Option<String>,
    report: Option<String>,
    frames_csv: Option<String>,
    seconds: f64,
    connect_timeout: f64,
    min_frames: usize,
    exit_after_frames: Option<usize>,
    yaw_amp: f32,
    yaw_hz: f32,
    height: f32,
    refresh: f32,
    res: UVec2,
    prefer_10bit: bool,
    foveated: bool,
    decode_ms: u64,
    controllers: bool,
    controllers_csv: Option<String>,
    views_after_first_frame: bool,
    encoding_gamma: f32,
    /// Vision Pro display emulation: a display tick at `refresh` Hz with the visionOS client's frame rule (poll the decoded
    /// queue up to poll_ms, re-present the last frame otherwise, queue capped at queue_max), tracking once per tick with the
    /// next presentation time as timestamp, ALVR client statistics reported from the emulated display (not at arrival)
    display_sim: bool,
    poll_ms: f64,
    queue_max: usize,
    display_csv: Option<String>,
    /// head motion profile: yaw (sine, default) | still | turn (constant yaw rate) | nod (pitch sine)
    motion: String,
    /// orientation noise added to every sample (millidegrees, uniform), models tracking jitter
    noise_mdeg: f32,
    /// > 0: every other tracking sample is held and sent together with the next one (Wi-Fi bunching)
    bunch: bool,
    /// "p:ms": each tracking sample is delayed by `ms` with probability p (FIFO: later samples queue behind it), plus 0-2 ms of
    /// jitter: the headset's uplink arriving late and bunched (measured on the real headset: gaps p95 13-16 ms, max 40)
    track_late: String,
    /// timestamp jitter (ns, sd) on the tracking samples' target timestamps, quantised to 1/24 us like the real client's
    ts_jitter_ns: f64,
    /// every tracking sample sent: target timestamp, yaw, send time
    poses_csv: Option<String>,
}

fn parse_args() -> Result<Args, String> {
    let mut a = Args {
        out: None,
        report: None,
        frames_csv: None,
        seconds: 20.0,
        connect_timeout: 60.0,
        min_frames: 0,
        exit_after_frames: None,
        yaw_amp: 0.5,
        yaw_hz: 0.25,
        height: 1.5,
        refresh: 90.0,
        // Vision Pro defaults (what the visionOS client advertises): 3552x3200 per eye, 90 Hz, 10-bit, foveated encoding
        res: UVec2::new(3552, 3200),
        prefer_10bit: true,
        foveated: true,
        decode_ms: 2,
        controllers: false,
        controllers_csv: None,
        views_after_first_frame: false,
        display_sim: false,
        poll_ms: 5.0,
        queue_max: 2,
        display_csv: None,
        motion: "yaw".into(),
        noise_mdeg: 0.0,
        bunch: false,
        track_late: String::new(),
        ts_jitter_ns: 0.0,
        poses_csv: None,
        encoding_gamma: 1.0,
    };
    let mut it = std::env::args().skip(1);
    while let Some(k) = it.next() {
        let mut v = || it.next().ok_or(format!("missing value for {k}"));
        match k.as_str() {
            "--out" => a.out = Some(v()?),
            "--report" => a.report = Some(v()?),
            "--frames-csv" => a.frames_csv = Some(v()?),
            "--seconds" => a.seconds = v()?.parse().map_err(|e| format!("{e}"))?,
            "--connect-timeout" => a.connect_timeout = v()?.parse().map_err(|e| format!("{e}"))?,
            "--min-frames" => a.min_frames = v()?.parse().map_err(|e| format!("{e}"))?,
            "--exit-after-frames" => a.exit_after_frames = Some(v()?.parse().map_err(|e| format!("{e}"))?),
            "--yaw-amp" => a.yaw_amp = v()?.parse().map_err(|e| format!("{e}"))?,
            "--yaw-hz" => a.yaw_hz = v()?.parse().map_err(|e| format!("{e}"))?,
            "--height" => a.height = v()?.parse().map_err(|e| format!("{e}"))?,
            "--refresh" => a.refresh = v()?.parse().map_err(|e| format!("{e}"))?,
            "--decode-ms" => a.decode_ms = v()?.parse().map_err(|e| format!("{e}"))?,
            "--res" => {
                let s = v()?;
                let (w, h) = s.split_once('x').ok_or("--res WxH")?;
                a.res = UVec2::new(w.parse().map_err(|e| format!("{e}"))?, h.parse().map_err(|e| format!("{e}"))?);
            }
            "--controllers" => a.controllers = true,
            // like the stock visionOS client once it is in its immersive space: ViewsConfig only after the first decoded frame
            "--views-after-first-frame" => a.views_after_first_frame = true,
            // the visionOS client asks for 1.5 (ALVR uses it unless the session sets server_overrides_encoding_gamma)
            "--encoding-gamma" => a.encoding_gamma = v()?.parse().map_err(|e| format!("{e}"))?,
            "--controllers-csv" => a.controllers_csv = Some(v()?),
            "--display-sim" => a.display_sim = true,
            "--poll-ms" => a.poll_ms = v()?.parse().map_err(|e| format!("{e}"))?,
            "--queue-max" => a.queue_max = v()?.parse().map_err(|e| format!("{e}"))?,
            "--display-csv" => a.display_csv = Some(v()?),
            "--motion" => a.motion = v()?,
            "--noise-mdeg" => a.noise_mdeg = v()?.parse().map_err(|e| format!("{e}"))?,
            "--bunch" => a.bunch = true,
            "--track-late" => a.track_late = v()?,
            "--ts-jitter-ns" => a.ts_jitter_ns = v()?.parse().map_err(|e| format!("{e}"))?,
            "--poses-csv" => a.poses_csv = Some(v()?),
            "--no-10bit" => a.prefer_10bit = false,
            "--no-foveation" => a.foveated = false,
            other => return Err(format!("unknown argument {other}")),
        }
    }
    Ok(a)
}

struct FrameRec {
    ts_ns: u128,
    arrival_ns: u128,
    len: usize,
    yaw: Option<f32>,
    /// the sent yaw of the nearest sample within 3 us (the client's own lookup would need an exact timestamp)
    yaw_near: Option<f32>,
    decoded_ns: u128,
    queue_dropped: bool,
}

/// A decoded frame waiting for the emulated display.
struct Queued {
    ts: Duration,
    decoded_at: Instant,
}

/// One emulated display tick.
struct DisplayRec {
    tick_ns: u128,
    shown_ts_ns: u128,
    repeat: bool,
    dwell_ms: f64,
    queue_len: usize,
}

#[derive(Default)]
struct Shared {
    sent_yaw: std::collections::BTreeMap<u128, f32>,
    sent_log: Vec<(u128, f32, u128)>,
    queue: VecDeque<Queued>,
    dropped: usize,
    display: Vec<DisplayRec>,
    poses_sent: usize,
    frames: Vec<FrameRec>,
    bytes: usize,
    decoder_config_bytes: usize,
    controller_log: Vec<String>,
    haptics: Vec<(f64, u64, u64, f32, f32)>, // t_ms, device, duration_ms, frequency, amplitude
    views_sent_at_ms: Option<f64>,
}

fn event(origin: Instant, name: &str, extra: serde_json::Value) {
    println!("{}", json!({ "t_ms": origin.elapsed().as_secs_f64() * 1000.0, "event": name, "data": extra }));
}

fn pct(sorted: &[f64], p: f64) -> f64 {
    if sorted.is_empty() {
        return 0.0;
    }
    sorted[((sorted.len() - 1) as f64 * p).round() as usize]
}

fn stats(mut v: Vec<f64>) -> serde_json::Value {
    v.sort_by(|a, b| a.partial_cmp(b).unwrap());
    json!({ "n": v.len(), "p50": pct(&v, 0.5), "p95": pct(&v, 0.95), "max": v.last().copied().unwrap_or(0.0) })
}

fn main() {
    env_logger::init();
    let args = match parse_args() {
        Ok(a) => a,
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(3);
        }
    };
    let origin = Instant::now();

    let capabilities = ClientCapabilities {
        default_view_resolution: args.res,
        refresh_rates: vec![args.refresh],
        foveated_encoding: args.foveated,
        encoder_high_profile: true,
        encoder_10_bits: true,
        encoder_av1: false,
        prefer_10bit: args.prefer_10bit,
        prefer_full_range: true,
        preferred_encoding_gamma: args.encoding_gamma,
        prefer_hdr: false,
    };
    let ctx = Arc::new(ClientCoreContext::new(capabilities));
    ctx.resume();

    let shared = Arc::new(Mutex::new(Shared::default()));
    let streaming = Arc::new(RelaxedAtomic::new(false));
    let mut tracking_thread = None;
    let mut config_json = serde_json::Value::Null;
    let mut codec = String::from("none");
    let mut connected_at: Option<f64> = None;
    let mut stream_started = false;
    let mut got_decoder_config = false; // ALVR: all DecoderConfig events after the first are ignored until reconnection
    let mut out_file: Option<Arc<Mutex<File>>> = args.out.as_ref().map(|p| Arc::new(Mutex::new(File::create(p).expect("create --out"))));

    let deadline_connect = Duration::from_secs_f64(args.connect_timeout);
    let mut stream_started_at = origin;
    loop {
        while let Some(ev) = ctx.poll_event() {
            match ev {
                ClientCoreEvent::UpdateHudMessage(m) => event(origin, "hud", json!(m)),
                ClientCoreEvent::StreamingStarted(cfg) => {
                    stream_started = true;
                    got_decoder_config = false;
                    stream_started_at = Instant::now();
                    connected_at = Some(origin.elapsed().as_secs_f64());
                    config_json = serde_json::to_value(&cfg.negotiated_config).unwrap_or_default();
                    event(origin, "streaming_started", config_json.clone());
                    streaming.set(true);
                    let fps = cfg.negotiated_config.refresh_rate_hint;
                    let (c, s, sh) = (Arc::clone(&ctx), Arc::clone(&streaming), Arc::clone(&shared));
                    let (amp, hz, h) = (args.yaw_amp, args.yaw_hz, args.height);
                    let controllers = args.controllers;
                    let late_views = args.views_after_first_frame;
                    let motion = Motion { profile: args.motion.clone(), amp, hz, height: h, noise_mdeg: args.noise_mdeg };
                    if args.display_sim {
                        let (poll, bunch) = (args.poll_ms, args.bunch);
                        let late: (f64, f64) = args.track_late.split_once(':').map(|(p, m)| (p.parse().unwrap_or(0.0), m.parse().unwrap_or(0.0))).unwrap_or((0.0, 0.0));
                        tracking_thread = Some(thread::spawn(move || display(c, s, sh, origin, fps, motion, poll, bunch, late, args.ts_jitter_ns, late_views)));
                    } else {
                        tracking_thread = Some(thread::spawn(move || tracking(c, s, sh, origin, fps, motion, controllers, late_views)));
                    }
                }
                ClientCoreEvent::StreamingStopped => {
                    event(origin, "streaming_stopped", json!(null));
                    streaming.set(false);
                    if let Some(t) = tracking_thread.take() {
                        t.join().ok();
                    }
                }
                ClientCoreEvent::DecoderConfig { .. } if got_decoder_config => {}
                ClientCoreEvent::DecoderConfig { codec: c, config_nal } => {
                    got_decoder_config = true;
                    codec = format!("{c:?}");
                    shared.lock().decoder_config_bytes = config_nal.len();
                    event(origin, "decoder_config", json!({ "codec": codec, "config_nal_bytes": config_nal.len() }));
                    if let Some(f) = &out_file {
                        f.lock().write_all(&config_nal).ok();
                    }
                    let weak: Weak<ClientCoreContext> = Arc::downgrade(&ctx);
                    let (sh, of, decode_ms) = (Arc::clone(&shared), out_file.clone(), args.decode_ms);
                    let (display_sim, queue_max) = (args.display_sim, args.queue_max);
                    ctx.set_decoder_input_callback(Box::new(move |ts, data| {
                        let arrival = origin.elapsed();
                        if let Some(f) = &of {
                            f.lock().write_all(data).ok();
                        }
                        {
                            let mut s = sh.lock();
                            let t = ts.as_nanos();
                            let yaw = s.sent_yaw.get(&t).copied();
                            let yaw_near = s.sent_yaw.range(t.saturating_sub(3000)..=t + 3000).next().map(|(_, y)| *y);
                            s.frames.push(FrameRec { ts_ns: t, arrival_ns: arrival.as_nanos(), len: data.len(), yaw, yaw_near, decoded_ns: 0, queue_dropped: false });
                            s.bytes += data.len();
                        }
                        if let Some(c) = weak.upgrade() {
                            thread::sleep(Duration::from_millis(decode_ms));
                            c.report_frame_decoded(ts);
                            {
                                let mut s = sh.lock();
                                let t = ts.as_nanos();
                                if let Some(f) = s.frames.iter_mut().rev().take(16).find(|f| f.ts_ns == t && f.decoded_ns == 0) {
                                    f.decoded_ns = origin.elapsed().as_nanos();
                                }
                            }
                            if display_sim {
                                // like the visionOS client: decoded frames queue for the display thread, oldest dropped past the cap
                                let mut s = sh.lock();
                                s.queue.push_back(Queued { ts, decoded_at: Instant::now() });
                                while s.queue.len() > queue_max {
                                    if let Some(d) = s.queue.pop_front() {
                                        let t = d.ts.as_nanos();
                                        if let Some(f) = s.frames.iter_mut().rev().take(32).find(|f| f.ts_ns == t && !f.queue_dropped) {
                                            f.queue_dropped = true;
                                        }
                                    }
                                    s.dropped += 1;
                                }
                            } else {
                                c.report_compositor_start(ts);
                                c.report_submit(ts, Duration::from_millis(1));
                            }
                        }
                        true
                    }));
                }
                ClientCoreEvent::Haptics { device_id, duration, frequency, amplitude } => {
                    shared.lock().haptics.push((origin.elapsed().as_secs_f64() * 1000.0, device_id, duration.as_millis() as u64, frequency, amplitude));
                    event(origin, "haptics", json!({ "device": device_id, "ms": duration.as_millis() as u64, "freq": frequency, "amp": amplitude }))
                }
                ClientCoreEvent::RealTimeConfig(_) => event(origin, "realtime_config", json!(null)),
            }
        }

        let nframes = shared.lock().frames.len();
        if !stream_started && origin.elapsed() > deadline_connect {
            break;
        }
        if stream_started {
            if stream_started_at.elapsed().as_secs_f64() > args.seconds {
                break;
            }
            if let Some(n) = args.exit_after_frames {
                if nframes >= n {
                    break;
                }
            }
        }
        thread::sleep(Duration::from_millis(5));
    }

    // ALVR's own running average of tracking-sent -> frame-displayed (what it uses for head prediction)
    let pipeline_latency_ms = ctx.get_total_prediction_offset().as_secs_f64() * 1000.0;
    streaming.set(false);
    if let Some(t) = tracking_thread.take() {
        t.join().ok();
    }
    ctx.pause();
    out_file.take();

    let s = shared.lock();
    let n = s.frames.len();
    let span = match (s.frames.first(), s.frames.last()) {
        (Some(a), Some(b)) if n > 1 => (b.arrival_ns - a.arrival_ns) as f64 / 1e9,
        _ => 0.0,
    };
    let inter: Vec<f64> = s.frames.windows(2).map(|w| (w[1].arrival_ns - w[0].arrival_ns) as f64 / 1e6).collect();
    let lat: Vec<f64> = s.frames.iter().map(|f| (f.arrival_ns as f64 - f.ts_ns as f64) / 1e6).collect();
    let monotonic = s.frames.windows(2).all(|w| w[1].ts_ns >= w[0].ts_ns);
    let dup = s.frames.windows(2).filter(|w| w[1].ts_ns == w[0].ts_ns).count();
    let matched = s.frames.iter().filter(|f| f.yaw_near.is_some()).count();
    let matched_exact = s.frames.iter().filter(|f| f.yaw.is_some()).count();
    let pass = stream_started && n >= args.min_frames && (args.min_frames == 0 || matched == n);
    let report = json!({
        "pass": pass,
        "connected": stream_started,
        "connected_at_s": connected_at,
        "codec": codec,
        "negotiated_config": config_json,
        "decoder_config_nal_bytes": s.decoder_config_bytes,
        "frames": n,
        "bytes": s.bytes,
        "span_s": span,
        "fps_measured": if span > 0.0 { (n as f64 - 1.0) / span } else { 0.0 },
        "mbps_measured": if span > 0.0 { s.bytes as f64 * 8.0 / span / 1e6 } else { 0.0 },
        "interarrival_ms": stats(inter),
        "arrival_minus_timestamp_ms": stats(lat),
        "timestamps_monotonic": monotonic,
        "duplicate_timestamps": dup,
        "frames_with_matching_sent_pose": matched,
        "frames_with_exact_sent_pose": matched_exact,
        "poses_sent": s.poses_sent,
        "total_pipeline_latency_ms": pipeline_latency_ms,
        "views_after_first_frame": args.views_after_first_frame,
        "display": if args.display_sim { display_report(&s, 1000.0 / args.refresh as f64) } else { serde_json::Value::Null },
        "views_sent_at_ms": s.views_sent_at_ms,
        "haptics": {
            "count": s.haptics.len(),
            "max_amplitude": s.haptics.iter().map(|h| h.4).fold(0.0f32, f32::max),
            "devices": s.haptics.iter().map(|h| h.1).collect::<std::collections::BTreeSet<_>>(),
            "first": s.haptics.iter().take(12).map(|h| json!({ "t_ms": h.0, "device": h.1, "duration_ms": h.2, "frequency": h.3, "amplitude": h.4 })).collect::<Vec<_>>(),
        },
    });
    if let Some(p) = &args.controllers_csv {
        let mut f = File::create(p).expect("create --controllers-csv");
        writeln!(f, "ts_ns,trigger_r,squeeze_l,a_r,stick_lx,stick_ly,x_l,rx,ry,rz,lx,ly,lz").ok();
        for l in &s.controller_log {
            writeln!(f, "{l}").ok();
        }
    }
    if let Some(p) = &args.report {
        std::fs::write(p, serde_json::to_string_pretty(&report).unwrap()).ok();
    }
    if let Some(p) = &args.display_csv {
        let mut f = File::create(p).expect("create --display-csv");
        writeln!(f, "tick_ns,shown_ts_ns,repeat,dwell_ms,queue_len").ok();
        for r in &s.display {
            writeln!(f, "{},{},{},{:.3},{}", r.tick_ns, r.shown_ts_ns, r.repeat as u8, r.dwell_ms, r.queue_len).ok();
        }
    }
    if let Some(p) = &args.frames_csv {
        let mut f = File::create(p).expect("create --frames-csv");
        writeln!(f, "ts_ns,arrival_ns,len,yaw,yaw_near,decoded_ns,queue_dropped").ok();
        for r in &s.frames {
            writeln!(f, "{},{},{},{},{},{},{}", r.ts_ns, r.arrival_ns, r.len, r.yaw.map(|y| y.to_string()).unwrap_or_default(),
                r.yaw_near.map(|y| y.to_string()).unwrap_or_default(), r.decoded_ns, r.queue_dropped as u8).ok();
        }
    }
    if let Some(p) = &args.poses_csv {
        let mut f = File::create(p).expect("create --poses-csv");
        writeln!(f, "target_ts_ns,yaw,send_ns").ok();
        for (t, y, sn) in &s.sent_log {
            writeln!(f, "{t},{y},{sn}").ok();
        }
    }
    println!("{}", json!({ "event": "report", "data": report }));
    std::process::exit(if !stream_started { 2 } else if pass { 0 } else { 1 });
}

#[derive(Clone)]
struct Motion {
    profile: String,
    amp: f32,
    hz: f32,
    height: f32,
    noise_mdeg: f32,
}

impl Motion {
    /// Head pose at time `t` (seconds): the yaw the harness compares with what the app saw, and the full orientation.
    fn pose(&self, t: f32, seed: u64) -> (f32, Quat) {
        use std::f32::consts::TAU;
        let (yaw, pitch) = match self.profile.as_str() {
            "still" => (0.0, 0.0),
            "turn" => ((self.amp * t) % TAU, 0.0), // constant yaw rate amp rad/s (a slow look-around)
            "nod" => (0.0, self.amp * (TAU * self.hz * t).sin()),
            _ => (self.amp * (TAU * self.hz * t).sin(), 0.0),
        };
        let mut q = Quat::from_rotation_y(yaw) * Quat::from_rotation_x(pitch);
        if self.noise_mdeg > 0.0 {
            // deterministic uniform noise (xorshift) so runs are reproducible
            let mut x = seed.wrapping_mul(0x9E3779B97F4A7C15) ^ 0xD1B54A32D192ED03;
            let mut r = || { x ^= x << 13; x ^= x >> 7; x ^= x << 17; (x >> 11) as f32 / (1u64 << 53) as f32 * 2.0 - 1.0 };
            let n = self.noise_mdeg.to_radians() / 1000.0;
            q = Quat::from_euler(alvr_common::glam::EulerRot::YXZ, r() * n, r() * n, r() * n) * q;
        }
        (yaw, q.normalize())
    }
}

fn head_motion(m: &Motion, ts: Duration, seed: u64) -> (f32, DeviceMotion) {
    let (yaw, q) = m.pose(ts.as_secs_f32(), seed);
    (yaw, DeviceMotion { pose: Pose { orientation: q, position: Vec3::new(0.0, m.height, 0.0) }, linear_velocity: Vec3::ZERO, angular_velocity: Vec3::ZERO })
}

/// Vision Pro display emulation (see Args::display_sim). One thread: at every display tick it sends the tracking sample for
/// the next presentation (what the visionOS client does once per display frame) and picks the frame to show with the client's
/// rule (poll the decoded queue up to poll_ms, else re-present the last frame), reporting ALVR's client statistics from that.
#[allow(clippy::too_many_arguments)]
fn display(
    ctx: Arc<ClientCoreContext>,
    streaming: Arc<RelaxedAtomic>,
    shared: Arc<Mutex<Shared>>,
    origin: Instant,
    fps: f32,
    motion: Motion,
    poll_ms: f64,
    bunch: bool,
    late: (f64, f64),
    ts_jitter: f64,
    late_views: bool,
) {
    let period = Duration::from_secs_f64(1.0 / fps as f64);
    let vp = ViewParams { pose: Pose::default(), fov: Fov { left: -1.0, right: 1.0, up: 1.0, down: -1.0 } };
    let mut views_sent = false;
    let mut tick = Instant::now();
    let mut tick_idx = 0u64;
    let mut last_shown: Option<Duration> = None;
    let mut held: Option<(Duration, DeviceMotion, f32)> = None;
    // tracking samples waiting for their (late) send time, FIFO
    let mut outq: std::collections::VecDeque<(Instant, Duration, DeviceMotion, f32)> = std::collections::VecDeque::new();
    let mut rng: u64 = 0x9E3779B97F4A7C15;
    let mut next_f = move || {
        rng ^= rng << 13;
        rng ^= rng >> 7;
        rng ^= rng << 17;
        (rng >> 11) as f64 / 9007199254740992.0
    };
    let face = || FaceData { eye_gazes: [None, None], fb_face_expression: None, htc_eye_expression: None, htc_lip_expression: None };
    while streaming.value() {
        // tracking for the frame that will be presented at the next tick
        if !views_sent && (!late_views || !shared.lock().frames.is_empty()) {
            ctx.send_view_params([vp, vp]);
            views_sent = true;
            shared.lock().views_sent_at_ms = Some(origin.elapsed().as_secs_f64() * 1000.0);
        }
        let nominal = tick.duration_since(origin) + period;
        let (yaw, m) = head_motion(&motion, nominal, tick_idx);
        // the real client's target timestamps jitter by a few tens of ns around the regular grid (quantised to 1/24 us)
        let target = if ts_jitter > 0.0 {
            let g = (next_f() + next_f() + next_f() + next_f() - 2.0) * 1.7320508; // ~N(0,1)
            let q = ((g * ts_jitter) / 41.6667).round() * 41.6667;
            Duration::from_nanos((nominal.as_nanos() as i64 + q as i64).max(0) as u64)
        } else {
            nominal
        };
        {
            let mut s = shared.lock();
            s.sent_yaw.insert(target.as_nanos(), yaw);
            s.poses_sent += 1;
        }
        if bunch && tick_idx % 2 == 1 {
            held = Some((target, m, yaw)); // sent with the next sample: two packets back to back, then a gap
        } else {
            if let Some((hts, hm, hy)) = held.take() {
                shared.lock().sent_log.push((hts.as_nanos(), hy, origin.elapsed().as_nanos()));
                ctx.send_tracking(hts, vec![(*HEAD_ID, hm)], [None, None], face());
            }
            if late.0 > 0.0 {
                let extra = next_f() * 2.0 + if next_f() < late.0 { late.1 } else { 0.0 };
                let mut due = Instant::now() + Duration::from_secs_f64(extra / 1000.0);
                if let Some(last) = outq.back() {
                    due = due.max(last.0); // FIFO: a late sample holds the ones behind it
                }
                outq.push_back((due, target, m, yaw));
            } else {
                shared.lock().sent_log.push((target.as_nanos(), yaw, origin.elapsed().as_nanos()));
                ctx.send_tracking(target, vec![(*HEAD_ID, m)], [None, None], face());
            }
        }
        // the frame for this tick: poll the decoded queue up to poll_ms
        let deadline = tick + Duration::from_secs_f64(poll_ms / 1000.0);
        let mut picked: Option<Queued> = None;
        loop {
            {
                let mut s = shared.lock();
                if let Some(q) = s.queue.pop_front() {
                    picked = Some(q);
                    break;
                }
            }
            while outq.front().map_or(false, |f| f.0 <= Instant::now()) {
                let (_, t, mm, yy) = outq.pop_front().unwrap();
                shared.lock().sent_log.push((t.as_nanos(), yy, origin.elapsed().as_nanos()));
                ctx.send_tracking(t, vec![(*HEAD_ID, mm)], [None, None], face());
            }
            if Instant::now() >= deadline {
                break;
            }
            thread::sleep(Duration::from_micros(200));
        }
        let now = Instant::now();
        let queue_len = shared.lock().queue.len();
        match picked {
            Some(q) => {
                let dwell = now.duration_since(q.decoded_at);
                ctx.report_compositor_start(q.ts);
                ctx.report_submit(q.ts, dwell);
                last_shown = Some(q.ts);
                shared.lock().display.push(DisplayRec { tick_ns: now.duration_since(origin).as_nanos(), shown_ts_ns: q.ts.as_nanos(), repeat: false, dwell_ms: dwell.as_secs_f64() * 1000.0, queue_len });
            }
            None => {
                shared.lock().display.push(DisplayRec { tick_ns: now.duration_since(origin).as_nanos(), shown_ts_ns: last_shown.map(|t| t.as_nanos()).unwrap_or(0), repeat: true, dwell_ms: 0.0, queue_len });
            }
        }
        tick += period;
        tick_idx += 1;
        while Instant::now() < tick {
            while outq.front().map_or(false, |f| f.0 <= Instant::now()) {
                let (_, t, mm, yy) = outq.pop_front().unwrap();
                shared.lock().sent_log.push((t.as_nanos(), yy, origin.elapsed().as_nanos()));
                ctx.send_tracking(t, vec![(*HEAD_ID, mm)], [None, None], face());
            }
            thread::sleep(Duration::from_micros(200).min(tick.saturating_duration_since(Instant::now())));
        }
    }
}

/// Repeats / skips / dwell of the emulated display: the headset-side numbers that separate a smooth stream from a juddering one.
fn display_report(s: &Shared, period_ms: f64) -> serde_json::Value {
    let d = &s.display;
    let warm = d.len().min(90);
    let d = &d[warm..];
    let shown: Vec<&DisplayRec> = d.iter().filter(|r| !r.repeat).collect();
    let repeats = d.iter().filter(|r| r.repeat && r.shown_ts_ns != 0).count();
    let steps: Vec<f64> = shown.windows(2).map(|w| (w[1].shown_ts_ns as f64 - w[0].shown_ts_ns as f64) / 1e6 / period_ms).collect();
    let skips = steps.iter().filter(|x| **x >= 1.5).count();
    let mut dwell: Vec<f64> = shown.iter().map(|r| r.dwell_ms).collect();
    dwell.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let span_s = match (d.first(), d.last()) {
        (Some(a), Some(b)) if d.len() > 1 => (b.tick_ns - a.tick_ns) as f64 / 1e9,
        _ => 0.0,
    };
    json!({
        "ticks": d.len(), "shown": shown.len(), "repeats": repeats, "repeat_pct": 100.0 * repeats as f64 / d.len().max(1) as f64,
        "skips": skips, "skip_pct": 100.0 * skips as f64 / steps.len().max(1) as f64,
        "dropped_in_queue": s.dropped,
        "dwell_ms": { "p05": pct(&dwell, 0.05), "p50": pct(&dwell, 0.5), "p95": pct(&dwell, 0.95) },
        "display_fps": if span_s > 0.0 { (d.len() as f64 - 1.0) / span_s } else { 0.0 },
        "warmup_ticks_skipped": warm,
    })
}

#[allow(clippy::too_many_arguments)]
fn tracking(
    ctx: Arc<ClientCoreContext>,
    streaming: Arc<RelaxedAtomic>,
    shared: Arc<Mutex<Shared>>,
    origin: Instant,
    fps: f32,
    motion: Motion,
    controllers: bool,
    late_views: bool,
) {
    if controllers {
        // Announce the Quest/Touch profile (what ALVR emulates) for both hands.
        ctx.send_active_interaction_profile(*HAND_LEFT_ID, *QUEST_CONTROLLER_PROFILE_ID);
        ctx.send_active_interaction_profile(*HAND_RIGHT_ID, *QUEST_CONTROLLER_PROFILE_ID);
    }
    let mut last_buttons: [Option<ButtonValue>; 3] = [None, None, None];
    let vp = ViewParams { pose: Pose::default(), fov: Fov { left: -1.0, right: 1.0, up: 1.0, down: -1.0 } };
    let mut views_sent = false;
    let mut next = Instant::now();
    while streaming.value() {
        if !views_sent && (!late_views || !shared.lock().frames.is_empty()) {
            ctx.send_view_params([vp, vp]);
            views_sent = true;
            shared.lock().views_sent_at_ms = Some(origin.elapsed().as_secs_f64() * 1000.0);
        }
        let ts = origin.elapsed();
        let (yaw, head) = head_motion(&motion, ts, ts.as_nanos() as u64);
        shared.lock().sent_yaw.insert(ts.as_nanos(), yaw);
        shared.lock().poses_sent += 1;
        let mut motions = vec![(*HEAD_ID, head)];
        if controllers {
            // Scripted controllers, deterministic in time so the harness can compare what the app sees.
            let t = ts.as_secs_f32();
            let tri = |t: f32, period: f32| {
                let p = (t / period).fract();
                if p < 0.5 { p * 2.0 } else { 2.0 - p * 2.0 }
            };
            let lpos = Vec3::new(-0.25, 1.2 + 0.05 * (t * 1.3).sin(), -0.3);
            let rpos = Vec3::new(0.25 + 0.1 * (std::f32::consts::TAU * 0.5 * t).sin(), 1.3, -0.3 + 0.05 * (t * 0.7).cos());
            for (id, pos) in [(*HAND_LEFT_ID, lpos), (*HAND_RIGHT_ID, rpos)] {
                motions.push((id, DeviceMotion { pose: Pose { orientation: Quat::IDENTITY, position: pos }, linear_velocity: Vec3::ZERO, angular_velocity: Vec3::ZERO }));
            }
            let trigger_r = tri(t, 4.0);
            let squeeze_l = tri(t + 1.0, 3.0) * 0.8;
            let a_r = (t as i64) % 2 == 1;
            let stick = ((std::f32::consts::TAU * t / 3.0).sin(), (std::f32::consts::TAU * t / 3.0).cos() * 0.5);
            let x_l = (t as i64) % 3 == 2;
            // continuous values every tick, clicks only on change (like the headset)
            let mut entries = vec![
                ButtonEntry { path_id: *RIGHT_TRIGGER_VALUE_ID, value: ButtonValue::Scalar(trigger_r) },
                ButtonEntry { path_id: *LEFT_SQUEEZE_VALUE_ID, value: ButtonValue::Scalar(squeeze_l) },
                ButtonEntry { path_id: *LEFT_THUMBSTICK_X_ID, value: ButtonValue::Scalar(stick.0) },
                ButtonEntry { path_id: *LEFT_THUMBSTICK_Y_ID, value: ButtonValue::Scalar(stick.1) },
            ];
            for (i, (path, on)) in [(*RIGHT_A_CLICK_ID, a_r), (*LEFT_X_CLICK_ID, x_l)].into_iter().enumerate() {
                let v = ButtonValue::Binary(on);
                let changed = match (&last_buttons[i], &v) {
                    (Some(ButtonValue::Binary(p)), ButtonValue::Binary(n)) => p != n,
                    _ => true,
                };
                if changed {
                    entries.push(ButtonEntry { path_id: path, value: ButtonValue::Binary(on) });
                    last_buttons[i] = Some(v);
                }
            }
            ctx.send_buttons(entries);
            shared.lock().controller_log.push(format!(
                "{},{},{},{},{},{},{},{},{},{},{},{},{}",
                ts.as_nanos(), trigger_r, squeeze_l, a_r as u8, stick.0, stick.1, x_l as u8, rpos.x, rpos.y, rpos.z, lpos.x, lpos.y, lpos.z
            ));
        }
        ctx.send_tracking(
            ts,
            motions,
            [None, None],
            FaceData { eye_gazes: [None, None], fb_face_expression: None, htc_eye_expression: None, htc_lip_expression: None },
        );
        next += Duration::from_secs_f32(1.0 / fps / 3.0);
        thread::sleep(next.saturating_duration_since(Instant::now()));
    }
}
