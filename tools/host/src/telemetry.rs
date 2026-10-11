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
/// the client has for the frame's timestamp (harness/frames.py, headset mode).
#[cfg(feature = "telemetry")]
pub fn head_sample(ts_ns: u64, q: [f32; 4], p: [f32; 3]) {
    use crate::logs;
    logs::csv("head_samples.csv", "t,ts_ns,qx,qy,qz,qw,px,py,pz",
        &format!("{:.6},{},{:.7},{:.7},{:.7},{:.7},{:.4},{:.4},{:.4}", logs::qpc_at(std::time::Instant::now()), ts_ns, q[0], q[1], q[2], q[3], p[0], p[1], p[2]));
}
#[cfg(not(feature = "telemetry"))]
#[inline(always)]
pub fn head_sample(_ts_ns: u64, _q: [f32; 4], _p: [f32; 3]) {}
