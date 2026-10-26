//! Frame generation judge (docs/FRAME_GENERATION_JUDGE.md, `--fg-judge <dir>`): how close are the generated frames to the frame the game would have
//! rendered? The benchmark scene (Littlest Tokyo, animated) is rendered at times A and B (the pair a game at half the display rate delivers) and at the
//! midpoint G, the ground truth. Each synthesis (classic, edge, edge-blend) generates the midpoint from A and B, with the streamer's own generator and
//! settings, and is scored against G: PSNR and SSIM over the whole frame, inside the moving region and on its boundary band, plus how much edge energy the
//! boundary has too much (double images) or too little (blur) against G. Two trivial references are scored the same way: holding B, and the 50/50 blend.
//! Scenarios: `scene` (the scene animates, the head is still), `head` (the scene is frozen, the scripted head moves: the rotation reprojection and the
//! parallax the flow has to explain), `both`. Several start times and several rates of the rendered pair (the game's fps).
//! The scoring is here, on the builder, so only small files come back: the per-case JSON and, for the first start time, crops of the moving region.
use serde_json::{json, Value};
use std::{
    ffi::{c_char, c_void, CStr, CString},
    fs,
    io::Write,
    path::{Path, PathBuf},
    time::Instant,
};

extern "C" {
    fn nvh_bench_open(scene: *const c_char, err: *mut c_char, errlen: i32) -> *mut c_void;
    fn nvh_bench_gpu(b: *mut c_void, buf: *mut c_char, len: i32) -> i32;
    fn nvh_bench_close(b: *mut c_void);
    fn nvh_bench_fgj_frame(b: *mut c_void, eye_w: i32, eye_h: i32, times3: *const f64, fov8: *const f32, ipd: f32, rgba: *mut u8, q4: *mut f32) -> i32;
    fn nvh_bench_fgj_open(b: *mut c_void, eye_w: i32, eye_h: i32, synth: i32, err: *mut c_char, errlen: i32) -> *mut c_void;
    fn nvh_bench_fgj_gen(b: *mut c_void, j: *mut c_void, eye_w: i32, eye_h: i32, times9: *const f64, fov8: *const f32, ipd: f32, rgba: *mut u8, info2: *mut f32, err: *mut c_char, errlen: i32) -> i32;
    fn nvh_bench_fgj_close(j: *mut c_void);
}

pub struct Options {
    pub eye: (i32, i32),
    pub starts: usize,
    pub gaps_ms: Vec<f64>,
    /// multipliers of the scene's own animation time in the `scene` and `both` scenarios (the animation is slow: 1 moves about 0.3 % of the frame at a 33 ms pair gap)
    pub speeds: Vec<f64>,
    pub out: PathBuf,
}

fn cstr(b: &[c_char]) -> String {
    unsafe { CStr::from_ptr(b.as_ptr()) }.to_string_lossy().to_string()
}

// ---------------------------------------------------------------- metrics

fn luma(rgba: &[u8]) -> Vec<f32> {
    rgba.chunks_exact(4).map(|p| 0.2126 * p[0] as f32 + 0.7152 * p[1] as f32 + 0.0722 * p[2] as f32).collect()
}

/// Max filter over a (2r+1)^2 square (dilation of a mask), separable.
fn dilate(m: &[bool], w: usize, h: usize, r: usize) -> Vec<bool> {
    let mut tmp = vec![false; w * h];
    for y in 0..h {
        let row = &m[y * w..(y + 1) * w];
        for x in 0..w {
            let (a, b) = (x.saturating_sub(r), (x + r).min(w - 1));
            tmp[y * w + x] = row[a..=b].iter().any(|&v| v);
        }
    }
    let mut out = vec![false; w * h];
    for x in 0..w {
        for y in 0..h {
            let (a, b) = (y.saturating_sub(r), (y + r).min(h - 1));
            out[y * w + x] = (a..=b).any(|yy| tmp[yy * w + x]);
        }
    }
    out
}

fn erode(m: &[bool], w: usize, h: usize, r: usize) -> Vec<bool> {
    let inv: Vec<bool> = m.iter().map(|&v| !v).collect();
    dilate(&inv, w, h, r).into_iter().map(|v| !v).collect()
}

fn psnr_of(mse: f64) -> Option<f64> {
    if mse <= 1e-9 {
        Some(99.0)
    } else {
        Some(10.0 * (255.0 * 255.0 / mse).log10())
    }
}

/// PSNR of `a` against the truth over the pixels of `mask` (None = all); None when the mask is empty.
fn psnr(a: &[f32], g: &[f32], mask: Option<&[bool]>) -> Option<f64> {
    let (mut s, mut n) = (0f64, 0usize);
    for i in 0..a.len() {
        if mask.map_or(true, |m| m[i]) {
            let d = (a[i] - g[i]) as f64;
            s += d * d;
            n += 1;
        }
    }
    if n == 0 { None } else { psnr_of(s / n as f64) }
}

/// SSIM of 8 x 8 windows at stride 4 (uniform weights, luma 0..255): the value per window and the window's centre pixel.
fn ssim_windows(a: &[f32], g: &[f32], w: usize, h: usize) -> Vec<(f32, usize)> {
    const C1: f64 = (0.01 * 255.0) * (0.01 * 255.0);
    const C2: f64 = (0.03 * 255.0) * (0.03 * 255.0);
    let mut out = Vec::with_capacity((w / 4) * (h / 4));
    let mut y = 0;
    while y + 8 <= h {
        let mut x = 0;
        while x + 8 <= w {
            let (mut sa, mut sg, mut saa, mut sgg, mut sag) = (0f64, 0f64, 0f64, 0f64, 0f64);
            for yy in y..y + 8 {
                let base = yy * w + x;
                for i in base..base + 8 {
                    let (p, q) = (a[i] as f64, g[i] as f64);
                    sa += p;
                    sg += q;
                    saa += p * p;
                    sgg += q * q;
                    sag += p * q;
                }
            }
            let n = 64.0;
            let (ma, mg) = (sa / n, sg / n);
            let (va, vg, cov) = (saa / n - ma * ma, sgg / n - mg * mg, sag / n - ma * mg);
            let s = ((2.0 * ma * mg + C1) * (2.0 * cov + C2)) / ((ma * ma + mg * mg + C1) * (va + vg + C2));
            out.push((s as f32, (y + 4) * w + x + 4));
            x += 4;
        }
        y += 4;
    }
    out
}

fn ssim_mean(win: &[(f32, usize)], mask: Option<&[bool]>) -> Option<f64> {
    let (mut s, mut n) = (0f64, 0usize);
    for &(v, c) in win {
        if mask.map_or(true, |m| m[c]) {
            s += v as f64;
            n += 1;
        }
    }
    if n == 0 { None } else { Some(s / n as f64) }
}

/// |dx| + |dy| of the luma.
fn gradient(l: &[f32], w: usize, h: usize) -> Vec<f32> {
    let mut g = vec![0f32; w * h];
    for y in 0..h - 1 {
        for x in 0..w - 1 {
            let i = y * w + x;
            g[i] = (l[i + 1] - l[i]).abs() + (l[i + w] - l[i]).abs();
        }
    }
    g
}

struct Truth {
    w: usize,
    h: usize,
    g: Vec<f32>,
    grad: Vec<f32>,
    moving: Vec<bool>,
    band: Vec<bool>,
    moving_frac: f64,
    band_frac: f64,
    grad_band: f64,
}

fn score(t: &Truth, l: &[f32]) -> Value {
    let win = ssim_windows(l, &t.g, t.w, t.h);
    let grad = gradient(l, t.w, t.h);
    let (mut excess, mut deficit) = (0f64, 0f64);
    for i in 0..l.len() {
        if t.band[i] {
            let d = (grad[i] - t.grad[i]) as f64;
            if d > 0.0 { excess += d } else { deficit -= d }
        }
    }
    let norm = t.grad_band.max(1e-9);
    json!({
        "psnr": psnr(l, &t.g, None), "psnr_moving": psnr(l, &t.g, Some(&t.moving)), "psnr_band": psnr(l, &t.g, Some(&t.band)),
        "ssim": ssim_mean(&win, None), "ssim_moving": ssim_mean(&win, Some(&t.moving)),
        "edge_excess": excess / norm, "edge_deficit": deficit / norm,
    })
}

// ---------------------------------------------------------------- crops

fn write_ppm(path: &Path, rgba: &[u8], w: usize, x0: usize, y0: usize, cw: usize, ch: usize, gain: Option<(&[u8], f32)>) {
    let mut buf = format!("P6\n{cw} {ch}\n255\n").into_bytes();
    for y in y0..y0 + ch {
        for x in x0..x0 + cw {
            let i = (y * w + x) * 4;
            for c in 0..3 {
                let v = match gain {
                    Some((truth, k)) => ((rgba[i + c] as f32 - truth[i + c] as f32).abs() * k).min(255.0) as u8,
                    None => rgba[i + c],
                };
                buf.push(v);
            }
        }
    }
    let _ = fs::File::create(path).and_then(|mut f| f.write_all(&buf));
}

/// 480 x 360 around the centre of mass of the boundary band (clipped to the frame).
fn crop_origin(t: &Truth, cw: usize, ch: usize) -> (usize, usize) {
    let (mut sx, mut sy, mut n) = (0f64, 0f64, 0f64);
    for i in 0..t.band.len() {
        if t.band[i] {
            sx += (i % t.w) as f64;
            sy += (i / t.w) as f64;
            n += 1.0;
        }
    }
    let (cx, cy) = if n > 0.0 { ((sx / n) as usize, (sy / n) as usize) } else { (t.w / 2, t.h / 2) };
    (cx.saturating_sub(cw / 2).min(t.w - cw), cy.saturating_sub(ch / 2).min(t.h - ch))
}

// ---------------------------------------------------------------- the run

pub fn run(origin: Instant, scene: &Path, fov: [f32; 8], ipd: f32, opt: &Options, event: &dyn Fn(Instant, &str, Value)) -> i32 {
    let (ew, eh) = opt.eye;
    let (w, h) = (ew as usize * 2, eh as usize);
    let mut err = [0 as c_char; 256];
    let cpath = CString::new(scene.to_string_lossy().as_bytes()).unwrap();
    let b = unsafe { nvh_bench_open(cpath.as_ptr(), err.as_mut_ptr(), 256) };
    if b.is_null() {
        event(origin, "fgj_error", json!({ "message": format!("benchmark scene: {}", cstr(&err)), "scene": scene }));
        return 5;
    }
    let mut gpu = [0 as c_char; 128];
    unsafe { nvh_bench_gpu(b, gpu.as_mut_ptr(), 128) };
    let names = ["classic", "edge", "edge_blend"];
    let mut gens = [std::ptr::null_mut::<c_void>(); 3];
    for (k, g) in gens.iter_mut().enumerate() {
        *g = unsafe { nvh_bench_fgj_open(b, ew, eh, k as i32, err.as_mut_ptr(), 256) };
        if g.is_null() {
            event(origin, "fgj_error", json!({ "message": format!("frame generator {}: {}", names[k], cstr(&err)) }));
            return 6;
        }
    }
    let _ = fs::create_dir_all(&opt.out);
    event(origin, "fgj_start", json!({ "gpu": cstr(&gpu), "eye": [ew, eh], "starts": opt.starts, "gaps_ms": opt.gaps_ms, "speeds": opt.speeds, "scene": scene }));
    let bytes = w * h * 4;
    let (mut fa, mut fb, mut fg) = (vec![0u8; bytes], vec![0u8; bytes], vec![0u8; bytes]);
    let mut outs = [vec![0u8; bytes], vec![0u8; bytes], vec![0u8; bytes]];
    let mut cases: Vec<Value> = vec![];
    let mut failures = 0;
    for start in 0..opt.starts {
        let t0 = 0.5 + 1.9 * start as f64;
        for &gap in &opt.gaps_ms {
            let g = gap / 1000.0;
            let mut scenarios: Vec<(&str, f64)> = vec![("head", 1.0)];
            for &sp in &opt.speeds {
                scenarios.push(("scene", sp));
                scenarios.push(("both", sp));
            }
            for (scenario, speed) in scenarios {
                // (scene time, head position time, head orientation time) of A, B and the truth G (the midpoint)
                let tm = |dt: f64| match scenario {
                    "scene" => [t0 + dt * speed, t0, t0],
                    "head" => [t0, t0 + dt, t0 + dt],
                    _ => [t0 + dt * speed, t0 + dt, t0 + dt],
                };
                let (ta, tb, tg) = (tm(0.0), tm(g), tm(g / 2.0));
                let times9: Vec<f64> = ta.iter().chain(tb.iter()).chain(tg.iter()).copied().collect();
                let ok_frames = unsafe {
                    nvh_bench_fgj_frame(b, ew, eh, ta.as_ptr(), fov.as_ptr(), ipd, fa.as_mut_ptr(), std::ptr::null_mut()) > 0
                        && nvh_bench_fgj_frame(b, ew, eh, tb.as_ptr(), fov.as_ptr(), ipd, fb.as_mut_ptr(), std::ptr::null_mut()) > 0
                        && nvh_bench_fgj_frame(b, ew, eh, tg.as_ptr(), fov.as_ptr(), ipd, fg.as_mut_ptr(), std::ptr::null_mut()) > 0
                };
                if !ok_frames {
                    event(origin, "fgj_error", json!({ "message": "scene render / readback failed", "start": start, "gap_ms": gap, "scenario": scenario }));
                    failures += 1;
                    continue;
                }
                let mut timings = [[0f32; 2]; 3];
                let mut gen_ok = true;
                for k in 0..3 {
                    let r = unsafe {
                        nvh_bench_fgj_gen(b, gens[k], ew, eh, times9.as_ptr(), fov.as_ptr(), ipd, outs[k].as_mut_ptr(), timings[k].as_mut_ptr(), err.as_mut_ptr(), 256)
                    };
                    if r <= 0 {
                        event(origin, "fgj_error", json!({ "message": format!("{}: {}", names[k], cstr(&err)), "start": start, "gap_ms": gap, "scenario": scenario }));
                        gen_ok = false;
                        break;
                    }
                }
                if !gen_ok {
                    failures += 1;
                    continue;
                }
                let (la, lb, lg) = (luma(&fa), luma(&fb), luma(&fg));
                let raw: Vec<bool> = la.iter().zip(lb.iter()).map(|(p, q)| (p - q).abs() > 6.0).collect();
                let moving = dilate(&raw, w, h, 6);
                let inner = erode(&raw, w, h, 6);
                let band: Vec<bool> = moving.iter().zip(inner.iter()).map(|(&m, &i)| m && !i).collect();
                let grad = gradient(&lg, w, h);
                let nb = band.iter().filter(|&&v| v).count();
                let grad_band: f64 = (0..band.len()).filter(|&i| band[i]).map(|i| grad[i] as f64).sum();
                let truth = Truth {
                    w,
                    h,
                    moving_frac: moving.iter().filter(|&&v| v).count() as f64 / moving.len() as f64,
                    band_frac: nb as f64 / band.len() as f64,
                    g: lg,
                    grad,
                    moving,
                    band,
                    grad_band,
                };
                let blend: Vec<f32> = la.iter().zip(lb.iter()).map(|(p, q)| 0.5 * (p + q)).collect();
                let lo: Vec<Vec<f32>> = outs.iter().map(|o| luma(o)).collect();
                let case = json!({
                    "start": start, "t0": t0, "gap_ms": gap, "scenario": scenario, "speed": speed,
                    "moving_frac": truth.moving_frac, "band_frac": truth.band_frac,
                    "hold": score(&truth, &lb), "blend": score(&truth, &blend),
                    "classic": score(&truth, &lo[0]), "edge": score(&truth, &lo[1]), "edge_blend": score(&truth, &lo[2]),
                    "flow_ms": timings[1][0], "synth_ms": { "classic": timings[0][1], "edge": timings[1][1], "edge_blend": timings[2][1] },
                });
                event(origin, "fgj_case", case.clone());
                cases.push(case);
                if start == 0 && truth.band_frac > 0.0005 {
                    let (cw, ch) = (480usize.min(w), 360usize.min(h));
                    let (x0, y0) = crop_origin(&truth, cw, ch);
                    let stem = format!("{scenario}_x{}_g{}", speed as i64, gap as i64);
                    write_ppm(&opt.out.join(format!("{stem}_truth.ppm")), &fg, w, x0, y0, cw, ch, None);
                    write_ppm(&opt.out.join(format!("{stem}_hold.ppm")), &fb, w, x0, y0, cw, ch, None);
                    for k in 0..3 {
                        write_ppm(&opt.out.join(format!("{stem}_{}.ppm", names[k])), &outs[k], w, x0, y0, cw, ch, None);
                        write_ppm(&opt.out.join(format!("{stem}_{}_diff.ppm", names[k])), &outs[k], w, x0, y0, cw, ch, Some((&fg, 4.0)));
                    }
                }
            }
        }
    }
    for g in gens {
        unsafe { nvh_bench_fgj_close(g) };
    }
    unsafe { nvh_bench_close(b) };
    let doc = json!({ "gpu": cstr(&gpu), "eye": [ew, eh], "gaps_ms": opt.gaps_ms, "speeds": opt.speeds, "starts": opt.starts, "cases": cases, "failures": failures });
    let _ = fs::write(opt.out.join("fgj.json"), serde_json::to_string(&doc).unwrap_or_default());
    event(origin, "fgj_done", json!({ "cases": cases.len(), "failures": failures }));
    if failures > 0 { 7 } else { 0 }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn psnr_and_ssim_of_identical_and_shifted_frames() {
        let (w, h) = (64usize, 64usize);
        let a: Vec<f32> = (0..w * h).map(|i| ((i % w) * 4) as f32).collect();
        assert_eq!(psnr(&a, &a, None), Some(99.0));
        let b: Vec<f32> = a.iter().map(|v| v + 4.0).collect();
        let p = psnr(&b, &a, None).unwrap();
        assert!((p - 10.0 * (255.0f64 * 255.0 / 16.0).log10()).abs() < 1e-6);
        let win = ssim_windows(&a, &a, w, h);
        assert!(ssim_mean(&win, None).unwrap() > 0.999);
        let shifted: Vec<f32> = (0..w * h).map(|i| (((i % w) + 3) * 4) as f32).collect();
        assert!(ssim_mean(&ssim_windows(&shifted, &a, w, h), None).unwrap() < 0.999);
    }

    #[test]
    fn masks_grow_and_shrink() {
        let (w, h) = (32usize, 32usize);
        let mut m = vec![false; w * h];
        for y in 12..20 {
            for x in 12..20 {
                m[y * w + x] = true;
            }
        }
        let d = dilate(&m, w, h, 2);
        assert_eq!(d.iter().filter(|&&v| v).count(), 12 * 12);
        let e = erode(&m, w, h, 2);
        assert_eq!(e.iter().filter(|&&v| v).count(), 4 * 4);
    }
}
