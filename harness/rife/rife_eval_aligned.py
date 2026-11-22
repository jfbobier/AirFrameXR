"""RIFE 4.4 against the judge's truth WITH the generator's head-rotation handling (what the optical flow gets).
Per case: the older frame A is rotated into the newer frame B's camera (FrameGen PSPrep mode 1: rays of B's view through relRot(qA, qB) into A, black
outside A's view), RIFE interpolates (A', B) at t = 0.5 in B's camera, the result is rotated to the truth's head pose (FrameGen's output pass: rays of
G's view through relRot(qB, qG)), and where B's view does not cover them the older frame fills (the camera-turn edge fill). Head orientations come
from bench_scene.h HeadPose (deterministic), the eye tangents from the run's fgj_start event. Bicubic resampling (the generator's Catmull-Rom).

    python rife_eval_aligned.py <lab dir> "<fov8 comma list>" [scales]
"""
import json, math, sys, time, statistics
from pathlib import Path
import numpy as np
import torch
import torch.nn.functional as F

lab = Path(sys.argv[1])
fov = [float(x) for x in sys.argv[2].split(",")]
scales = [float(s) for s in (sys.argv[3] if len(sys.argv) > 3 else "1.0,0.5").split(",")]
frames = lab / "frames"
sys.path.insert(0, str(lab))
from rifepkg.IFNet_HDv3_v4_4 import IFNet  # noqa: E402  (rife_eval.py copied the model package)
import rife_eval_metrics as M  # noqa: E402

dev, dt = torch.device("cuda"), torch.float16
sd = torch.load(lab / "models" / "flownet_v4.4.pkl", map_location="cpu")
sd = {k.replace("module.", ""): v for k, v in sd.items() if "module." in k}
nets = {}
for s in scales:
    n = IFNet(s, False)
    n.load_state_dict(sd, strict=False)
    nets[s] = n.eval().to(dev, dt)


def head_quat(t):   # bench_scene.h HeadPose + XMQuaternionRotationRollPitchYaw(pitch, yaw, 0), (x y z w)
    tau = 2 * math.pi
    yaw = (25.0 * math.sin(tau * 0.13 * t) + 8.0 * math.sin(tau * 0.31 * t)) * math.pi / 180
    pitch = (-6.0 + 7.0 * math.sin(tau * 0.23 * t)) * math.pi / 180
    sp, cp, sy, cy = math.sin(pitch / 2), math.cos(pitch / 2), math.sin(yaw / 2), math.cos(yaw / 2)
    return (sp * cy, cp * sy, -sp * sy, cp * cy)


def qmat(q):        # framegen.cpp qmatRow
    x, y, z, w = q
    n = math.sqrt(x * x + y * y + z * z + w * w); x, y, z, w = x / n, y / n, z / n, w / n
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def rel_rot(qa, qb):  # direction in view b -> world -> view a
    return qmat(qa).T @ qmat(qb)


def remap(src, Mr, tan, H, W):
    """src (N,3,H,W) seen through Mr (dst-view direction -> src-view direction), same eye tangents; returns (img, valid (H,W))."""
    l, r, u, d = tan
    uu = (torch.arange(W, device=dev, dtype=torch.float32) + 0.5) / W
    vv = (torch.arange(H, device=dev, dtype=torch.float32) + 0.5) / H
    dx = (-l + uu * (l + r)).view(1, W).expand(H, W)
    dy = (u - vv * (u + d)).view(H, 1).expand(H, W)
    dirs = torch.stack([dx, dy, -torch.ones_like(dx)], -1)
    m = torch.tensor(Mr, dtype=torch.float32, device=dev)
    ds = dirs @ m.T
    ok = ds[..., 2] < -1e-4
    tx, ty = ds[..., 0] / -ds[..., 2], ds[..., 1] / -ds[..., 2]
    us = (tx + l) / (l + r)
    vs = 1 - (ty + d) / (u + d)
    ok = ok & (us >= 0) & (us <= 1) & (vs >= 0) & (vs <= 1)
    grid = torch.stack([us * 2 - 1, vs * 2 - 1], -1).unsqueeze(0).expand(src.shape[0], -1, -1, -1)
    out = F.grid_sample(src.float(), grid, mode="bicubic", padding_mode="zeros", align_corners=False).clamp(0, 1)
    return out * ok.float(), ok


grids = {}


def rife(net, s, x0, x1, t=0.5):   # x0, x1 (2,3,h,w) float in [0,1]
    _, _, h, ew = x0.shape
    tmp = max(32, int(32 / s))
    pw, ph = math.ceil(ew / tmp) * tmp, math.ceil(h / tmp) * tmp
    pad = (0, pw - ew, 0, ph - h)
    a, b = F.pad(x0.to(dt), pad), F.pad(x1.to(dt), pad)
    if (pw, ph) not in grids:
        div = torch.tensor([(pw - 1.0) / 2.0, (ph - 1.0) / 2.0], dtype=torch.float, device=dev)
        gh = torch.linspace(-1.0, 1.0, pw, device=dev).view(1, 1, 1, pw).expand(-1, -1, ph, -1)
        gv = torch.linspace(-1.0, 1.0, ph, device=dev).view(1, 1, ph, 1).expand(-1, -1, -1, pw)
        grids[(pw, ph)] = (div, torch.cat([gh, gv], 1))
    div, grid = grids[(pw, ph)]
    with torch.inference_mode():
        o = net(a, b, torch.full([2, 1, ph, pw], t, dtype=dt, device=dev), div, grid)
    return o[:, :, :h, :ew].float().clamp(0, 1)


def times_of(case, dt_):   # fg_judge.rs tm(): (scene, head position, head orientation)
    t0, sc, sp = case["t0"], case["scenario"], case["speed"]
    if sc == "scene": return (t0 + dt_ * sp, t0, t0)
    if sc in ("pan", "panstart", "panstop", "panrev", "walk"): return (t0 + dt_, t0, t0)
    if sc == "head": return (t0, t0 + dt_, t0 + dt_)
    return (t0 + dt_ * sp, t0 + dt_, t0 + dt_)


def to_t(img):    # SBS uint8 -> (2,3,H,ew) float
    ew = img.shape[1] // 2
    return torch.from_numpy(np.stack([img[:, :ew], img[:, ew:]])).to(dev).permute(0, 3, 1, 2).float() / 255.0


def to_img(x):
    o = (x.clamp(0, 1) * 255 + 0.5).to(torch.uint8).permute(0, 2, 3, 1).cpu().numpy()
    return np.concatenate([o[0], o[1]], axis=1)


cases = [json.loads(l) for l in (frames / "cases.jsonl").read_text().splitlines() if l.strip()]
rows = []
for c in cases:
    i, case = c["idx"], c["case"]
    A, B, G, E = (M.ppm(frames / f"case_{i:03d}_{k}.ppm") for k in ("A", "B", "G", "edge"))
    g = case["gap_ms"] / 1000
    qa, qb, qg = (head_quat(times_of(case, d)[2]) for d in (0.0, g, g / 2))
    H, ew = A.shape[0], A.shape[1] // 2
    tA, tB = to_t(A), to_t(B)
    T = M.truth_of(A, B, G)
    r = {"idx": i, "scenario": case["scenario"], "speed": case["speed"], "gap": case["gap_ms"], "edge": M.score(T, E)}
    for s in scales:
        eyes = []
        Ap = torch.cat([remap(tA[e:e + 1], rel_rot(qa, qb), fov[4 * e:4 * e + 4], H, ew)[0] for e in range(2)])
        R = rife(nets[s], s, Ap, tB)
        for e in range(2):
            tan = fov[4 * e:4 * e + 4]
            out, ok = remap(R[e:e + 1], rel_rot(qb, qg), tan, H, ew)
            fill, _ = remap(tA[e:e + 1], rel_rot(qa, qg), tan, H, ew)
            eyes.append(torch.where(ok, out, fill))
        o = to_img(torch.cat(eyes))
        r[f"rifeA{s:g}"] = M.score(T, o)
    rows.append(r)

lab_ = lambda r: "head" if r["scenario"] == "head" else f"walk {r['speed']:g}m/s" if r["scenario"] == "walk" else f"{r['scenario']} x{r['speed']:g}"
for s in scales:
    k = f"rifeA{s:g}"
    print(f"\n== RIFE scale {s:g}, rotation handled like the flow, minus edge (paired over {len(rows)} cases; positive = RIFE better)")
    for key, sign in (("psnr_band", 1), ("psnr_moving", 1), ("psnr", 1), ("edge_excess", -1), ("edge_deficit", -1)):
        d = [(r[k][key] - r["edge"][key]) * sign for r in rows if r[k][key] is not None and r["edge"][key] is not None]
        m = sum(d) / len(d); se = math.sqrt(sum((x - m) ** 2 for x in d) / (len(d) - 1) / len(d))
        print(f"  {key:13s} {m:+8.3f} +- {se:6.3f}  RIFE wins {sum(1 for x in d if x > 0):3d}/{len(d)}")
    print("  band PSNR / whole-frame PSNR by scenario (edge -> RIFE):")
    for L in sorted({lab_(r) for r in rows}, key=lambda x: (x.split()[0] != "head", x)):
        sel = [r for r in rows if lab_(r) == L]
        eb = sum(r["edge"]["psnr_band"] for r in sel) / len(sel); fb = sum(r[k]["psnr_band"] for r in sel) / len(sel)
        ew_ = sum(r["edge"]["psnr"] for r in sel) / len(sel); fw = sum(r[k]["psnr"] for r in sel) / len(sel)
        print(f"    {L:12s} band {eb:6.2f} -> {fb:6.2f} ({fb - eb:+5.2f})   frame {ew_:6.2f} -> {fw:6.2f} ({fw - ew_:+5.2f})")
(lab / "rife_eval_aligned.json").write_text(json.dumps(rows))
