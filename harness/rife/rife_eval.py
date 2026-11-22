"""RIFE 4.4 (vs-rife model code, MIT) against the frame generation judge's ground truth, offline.
Reads the judge's full-frame dump (AIRFRAMEXR_FG_JUDGE_DUMP: case_NNN_{A,B,G,edge}.ppm + cases.jsonl), interpolates the midpoint of A and B per eye
(batch 2) at several RIFE scales (scale < 1 = the network at reduced resolution, the final warp at full resolution), scores every output with a numpy
port of tools/host/src/fg_judge.rs's metrics (checked against the judge's own numbers for the edge synthesis) and prints paired differences vs edge.

    python rife_eval.py <lab dir> [scales comma list]
"""
import json, math, shutil, statistics, sys, time
from pathlib import Path
import numpy as np
import torch
import torch.nn.functional as F

lab = Path(sys.argv[1])
scales = [float(s) for s in (sys.argv[2] if len(sys.argv) > 2 else "1.0,0.5,0.25").split(",")]
frames = lab / "frames"
src = next((lab / "src").glob("vs-rife-*")) / "vsrife"
pkg = lab / "rifepkg"
pkg.mkdir(exist_ok=True)
(pkg / "__init__.py").write_text("")
for f in ("IFNet_HDv3_v4_4.py", "warplayer.py"):
    shutil.copy(src / f, pkg / f)
sys.path.insert(0, str(lab))
from rifepkg.IFNet_HDv3_v4_4 import IFNet  # noqa: E402

dev, dt = torch.device("cuda"), torch.float16
try:
    sd = torch.load(lab / "models" / "flownet_v4.4.pkl", map_location="cpu")
except Exception:
    sd = torch.load(lab / "models" / "flownet_v4.4.pkl", map_location="cpu", weights_only=False)
sd = {k.replace("module.", ""): v for k, v in sd.items() if "module." in k}
nets = {}
for s in scales:
    n = IFNet(s, False)
    n.load_state_dict(sd, strict=False)
    nets[s] = n.eval().to(dev, dt)


def ppm(p):
    b = p.read_bytes()
    parts = b.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    return np.frombuffer(parts[3], np.uint8).reshape(h, w, 3)


def write_ppm(p, a):
    p.write_bytes(f"P6\n{a.shape[1]} {a.shape[0]}\n255\n".encode() + np.ascontiguousarray(a).tobytes())


grids = {}


def rife(net, s, A, B, t=0.5):
    h, W = A.shape[:2]
    ew = W // 2
    x0 = torch.from_numpy(np.stack([A[:, :ew], A[:, ew:]])).to(dev).permute(0, 3, 1, 2).to(dt) / 255.0
    x1 = torch.from_numpy(np.stack([B[:, :ew], B[:, ew:]])).to(dev).permute(0, 3, 1, 2).to(dt) / 255.0
    tmp = max(32, int(32 / s))
    pw, ph = math.ceil(ew / tmp) * tmp, math.ceil(h / tmp) * tmp
    pad = (0, pw - ew, 0, ph - h)
    x0, x1 = F.pad(x0, pad), F.pad(x1, pad)
    key = (pw, ph)
    if key not in grids:
        div = torch.tensor([(pw - 1.0) / 2.0, (ph - 1.0) / 2.0], dtype=torch.float, device=dev)
        gh = torch.linspace(-1.0, 1.0, pw, dtype=torch.float, device=dev).view(1, 1, 1, pw).expand(-1, -1, ph, -1)
        gv = torch.linspace(-1.0, 1.0, ph, dtype=torch.float, device=dev).view(1, 1, ph, 1).expand(-1, -1, -1, pw)
        grids[key] = (div, torch.cat([gh, gv], 1))
    div, grid = grids[key]
    ts = torch.full([2, 1, ph, pw], t, dtype=dt, device=dev)
    torch.cuda.synchronize()
    t0 = time.perf_counter()
    with torch.inference_mode():
        out = net(x0, x1, ts, div, grid.expand(2, -1, -1, -1))
    torch.cuda.synchronize()
    ms = (time.perf_counter() - t0) * 1000
    o = (out[:, :, :h, :ew].clamp(0, 1) * 255 + 0.5).to(torch.uint8).permute(0, 2, 3, 1).cpu().numpy()
    return np.concatenate([o[0], o[1]], axis=1), ms


# ---- the judge's metrics (fg_judge.rs score), numpy
def luma(a):
    a = a.astype(np.float32)
    return 0.2126 * a[..., 0] + 0.7152 * a[..., 1] + 0.0722 * a[..., 2]


def dilate(m, r):
    out = m
    for ax in (1, 0):
        p = np.pad(out, [(r, r) if i == ax else (0, 0) for i in range(2)], constant_values=False)
        out = np.lib.stride_tricks.sliding_window_view(p, 2 * r + 1, axis=ax).any(-1)
    return out


def gradient(l):
    g = np.zeros_like(l)
    g[:-1, :-1] = np.abs(l[:-1, 1:] - l[:-1, :-1]) + np.abs(l[1:, :-1] - l[:-1, :-1])
    return g


def psnr(a, g, m=None):
    d = (a - g) if m is None else (a - g)[m]
    if d.size == 0:
        return None
    mse = float(np.mean(d.astype(np.float64) ** 2))
    return 99.0 if mse <= 1e-9 else 10 * math.log10(255 * 255 / mse)


def truth_of(A, B, G):
    la, lb, lg = luma(A), luma(B), luma(G)
    raw = np.abs(la - lb) > 6
    moving = dilate(raw, 6)
    inner = ~dilate(~raw, 6)
    band = moving & ~inner
    gg = gradient(lg)
    return dict(lg=lg, moving=moving, band=band, gg=gg, gband=max(float(gg[band].sum()), 1e-9))


def score(T, out):
    l = luma(out)
    d = gradient(l)[T["band"]] - T["gg"][T["band"]]
    return {"psnr": psnr(l, T["lg"]), "psnr_moving": psnr(l, T["lg"], T["moving"]), "psnr_band": psnr(l, T["lg"], T["band"]),
            "edge_excess": float(d[d > 0].sum()) / T["gband"], "edge_deficit": float(-d[d < 0].sum()) / T["gband"]}


cases = [json.loads(l) for l in (frames / "cases.jsonl").read_text().splitlines() if l.strip()]
rows, check, times = [], [], {s: [] for s in scales}
crops = lab / "crops"
crops.mkdir(exist_ok=True)
# warm-up (cuDNN autotune, first-call allocations)
A0 = ppm(frames / "case_000_A.ppm")
for s in scales:
    for _ in range(3):
        rife(nets[s], s, A0, A0)
for c in cases:
    i, case = c["idx"], c["case"]
    A, B, G, E = (ppm(frames / f"case_{i:03d}_{k}.ppm") for k in ("A", "B", "G", "edge"))
    T = truth_of(A, B, G)
    r = {"idx": i, "scenario": case["scenario"], "speed": case["speed"], "gap": case["gap_ms"], "start": case["start"], "edge": score(T, E)}
    check.append((r["edge"]["psnr_band"], case["edge"]["psnr_band"]))
    outs = {}
    for s in scales:
        o, ms = rife(nets[s], s, A, B)
        times[s].append(ms)
        r[f"rife{s:g}"] = score(T, o)
        outs[s] = o
    rows.append(r)
    if case["start"] == 0 and case["gap_ms"] > 30 and case["scenario"] in ("walk", "scene", "head") and case["speed"] in (1, 8, 1.0, 8.0):
        # 480 x 360 around the band's centre of mass: truth | edge | rife at each scale
        ys, xs = np.nonzero(T["band"])
        cy, cx = (int(ys.mean()), int(xs.mean())) if len(ys) else (G.shape[0] // 2, G.shape[1] // 2)
        y0, x0 = max(0, min(cy - 180, G.shape[0] - 360)), max(0, min(cx - 240, G.shape[1] - 480))
        tiles = [G, E] + [outs[s] for s in scales]
        write_ppm(crops / f"{case['scenario']}_x{case['speed']:g}_g{int(case['gap_ms'])}.ppm", np.concatenate([t[y0:y0 + 360, x0:x0 + 480] for t in tiles], axis=1))

mism = max(abs(a - b) for a, b in check if a is not None and b is not None)
print(f"metric port check: edge band PSNR, numpy vs the judge, largest difference {mism:.4f} dB over {len(check)} cases")
print(f"RIFE 4.4 FP16 PyTorch, both eyes batched, eye {cases[0]['eye']}: " + ", ".join(
    f"scale {s:g} median {statistics.median(times[s]):.2f} ms (p90 {sorted(times[s])[int(0.9 * len(times[s]))]:.2f})" for s in scales))


def label(r):
    return "head" if r["scenario"] == "head" else f"walk {r['speed']:g}m/s" if r["scenario"] == "walk" else f"{r['scenario']} x{r['speed']:g}"


keys = [("psnr_band", +1), ("psnr_moving", +1), ("psnr", +1), ("edge_excess", -1), ("edge_deficit", -1)]
for s in scales:
    k = f"rife{s:g}"
    print(f"\n== RIFE scale {s:g} minus edge (paired over {len(rows)} cases; positive = RIFE better)")
    for key, sign in keys:
        d = [(r[k][key] - r["edge"][key]) * sign for r in rows if r[k][key] is not None and r["edge"][key] is not None]
        m = sum(d) / len(d)
        se = math.sqrt(sum((x - m) ** 2 for x in d) / (len(d) - 1) / len(d))
        print(f"  {key:13s} {m:+8.3f} +- {se:6.3f}  RIFE wins {sum(1 for x in d if x > 0):3d}/{len(d)}")
    print("  band PSNR by scenario (edge -> RIFE):")
    for lab_ in sorted({label(r) for r in rows}, key=lambda x: (x.split()[0] != "head", x)):
        sel = [r for r in rows if label(r) == lab_]
        e = sum(r["edge"]["psnr_band"] for r in sel) / len(sel)
        f = sum(r[k]["psnr_band"] for r in sel) / len(sel)
        print(f"    {lab_:12s} {e:6.2f} -> {f:6.2f} ({f - e:+5.2f})")
(lab / "rife_eval.json").write_text(json.dumps({"rows": rows, "times": times}))
