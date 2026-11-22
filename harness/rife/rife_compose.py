"""The product design, offline: RIFE 4.4 run at LOW resolution for its fields only (intermediate flows to both frames + blend mask), composed at full
resolution like a FrameGen output pass would: per output pixel (truth's head pose) -> the newer frame B's camera (rotation) -> the fields there ->
ONE bicubic sample of the original B at p + F_t1 and ONE of the original A at p + F_t0 carried through B -> A's rotation, blended by the mask;
outside B's view the older frame fills (the camera-turn fill). The network sees the older frame aligned into B's camera (as the flow does).

    python rife_compose.py <lab dir> "<fov8>" [network fractions, e.g. 1,0.5,0.25]
"""
import json, math, sys
from pathlib import Path
import numpy as np
import torch
import torch.nn.functional as F

lab = Path(sys.argv[1])
fov = [float(x) for x in sys.argv[2].split(",")]
fracs = [float(s) for s in (sys.argv[3] if len(sys.argv) > 3 else "1,0.5,0.25").split(",")]
GATE = float(sys.argv[4]) if len(sys.argv) > 4 else 0.0   # still-frame gate threshold (colour 0..1; 0 = off)
GATE_PX = float(sys.argv[5]) if len(sys.argv) > 5 else 1e9   # ... and only where the fields move less than this (full-res px)
sys.path.insert(0, str(lab))
from rifepkg.IFNet_HDv3_v4_4 import IFNet  # noqa: E402
from rifepkg.warplayer import warp  # noqa: E402
import rife_eval_metrics as M  # noqa: E402

dev, dt = torch.device("cuda"), torch.float16
sd = torch.load(lab / "models" / "flownet_v4.4.pkl", map_location="cpu")
sd = {k.replace("module.", ""): v for k, v in sd.items() if "module." in k}
net = IFNet(1.0, False); net.load_state_dict(sd, strict=False); net = net.eval().to(dev, dt)


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




def fields(img0, img1, t=0.5):
    """IFNet.forward (vs-rife 4.4) up to the last block: flow (4: to img0 xy, to img1 xy, in input pixels) and the mask logits."""
    _, _, h, w = img0.shape
    tmp = 32
    pw, ph = math.ceil(w / tmp) * tmp, math.ceil(h / tmp) * tmp
    a = F.pad(img0, (0, pw - w, 0, ph - h)).to(dt); b = F.pad(img1, (0, pw - w, 0, ph - h)).to(dt)
    div = torch.tensor([(pw - 1.0) / 2.0, (ph - 1.0) / 2.0], dtype=torch.float, device=dev)
    gh = torch.linspace(-1.0, 1.0, pw, device=dev).view(1, 1, 1, pw).expand(-1, -1, ph, -1)
    gv = torch.linspace(-1.0, 1.0, ph, device=dev).view(1, 1, ph, 1).expand(-1, -1, -1, pw)
    grid = torch.cat([gh, gv], 1)
    ts = torch.full([a.shape[0], 1, ph, pw], t, dtype=dt, device=dev)
    blocks = [net.block0, net.block1, net.block2, net.block3]
    wa, wb, flow, mask = a, b, None, None
    with torch.inference_mode():
        for i in range(4):
            if flow is None:
                flow, mask = blocks[i](torch.cat((a, b, ts), 1), None, scale=net.scale_list[i])
            else:
                f0, m0 = blocks[i](torch.cat((wa, wb, ts, mask), 1), flow, scale=net.scale_list[i])
                flow, mask = flow + f0, mask + m0
            wa = warp(a, flow[:, :2], div, grid); wb = warp(b, flow[:, 2:4], div, grid)
    return flow[:, :, :h, :w].float(), mask[:, :, :h, :w].float()


def sample(img, u, v, mode="bicubic"):   # img (1,C,H,W), u v (H,W) in [0,1] of that image
    g = torch.stack([u * 2 - 1, v * 2 - 1], -1).unsqueeze(0)
    return F.grid_sample(img, g, mode=mode, padding_mode="border", align_corners=False)


def ray_uv(Mr, tan, H, W, u=None, v=None):
    """for pixels (u, v) of a view (default: its pixel centres), the uv in another view through Mr (this view's direction -> the other's), valid"""
    l, r, up, d = tan
    if u is None:
        u = ((torch.arange(W, device=dev, dtype=torch.float32) + 0.5) / W).view(1, W).expand(H, W)
        v = ((torch.arange(H, device=dev, dtype=torch.float32) + 0.5) / H).view(H, 1).expand(H, W)
    dirs = torch.stack([-l + u * (l + r), up - v * (up + d), -torch.ones_like(u)], -1) @ torch.tensor(Mr, dtype=torch.float32, device=dev).T
    ok = dirs[..., 2] < -1e-4
    us = (dirs[..., 0] / -dirs[..., 2] + l) / (l + r)
    vs = 1 - (dirs[..., 1] / -dirs[..., 2] + d) / (up + d)
    return us, vs, ok & (us >= 0) & (us <= 1) & (vs >= 0) & (vs <= 1)


frames = lab / "frames"
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
    Ap = torch.cat([remap(tA[e:e + 1], rel_rot(qa, qb), fov[4 * e:4 * e + 4], H, ew)[0] for e in range(2)])   # what the network sees (B's camera)
    for f in fracs:
        hl, wl = max(32, round(H * f)), max(32, round(ew * f))
        lo = lambda x: F.interpolate(x, size=(hl, wl), mode="bilinear", antialias=True, align_corners=False) if f != 1 else x
        flow, mask = fields(lo(Ap), lo(tB))                 # (2,4,hl,wl) in low-res px, (2,1,hl,wl)
        eyes = []
        for e in range(2):
            tan = fov[4 * e:4 * e + 4]
            uB, vB, okB = ray_uv(rel_rot(qb, qg), tan, H, ew)                       # output pixel -> B's camera
            fl = sample(flow[e:e + 1], uB, vB, "bilinear")[0]                         # fields at that point (low-res px)
            mk = torch.sigmoid(sample(mask[e:e + 1], uB, vB, "bilinear")[0, 0])
            u1, v1 = uB + fl[2] / wl, vB + fl[3] / hl                                 # B at p + F_t1 (uv: low-res px / low-res size)
            cb = sample(tB[e:e + 1], u1.clamp(0, 1), v1.clamp(0, 1))[0]
            u0, v0 = uB + fl[0] / wl, vB + fl[1] / hl                                 # A' at p + F_t0, carried into A's own camera
            uA, vA, okA = ray_uv(rel_rot(qa, qb), tan, H, ew, u0, v0)
            ca = sample(tA[e:e + 1], uA, vA)[0]
            out = torch.where(okA, ca * mk + cb * (1 - mk), cb)
            if GATE > 0:
                # the still-frame gate (edge's "a vector must beat standing still"): where the aligned older frame and the newer one already agree at the
                # output point, nothing moved there: their time blend at zero motion, no resample through the (never exactly zero) low-resolution fields
                pz = sample(Ap[e:e + 1], uB, vB)[0]; bz = sample(tB[e:e + 1], uB, vB)[0]
                fpx = torch.maximum(torch.hypot(fl[0] * ew / wl, fl[1] * H / hl), torch.hypot(fl[2] * ew / wl, fl[3] * H / hl))   # full-res px
                still = ((pz - bz).abs().amax(0) < GATE) & okB & (fpx < GATE_PX)
                out = torch.where(still, 0.5 * (pz + bz), out)
            fu, fv, _ = ray_uv(rel_rot(qa, qg), tan, H, ew)                           # outside B's view: the older frame
            fill = sample(tA[e:e + 1], fu, fv)[0]
            eyes.append(torch.where(okB, out, fill).clamp(0, 1).unsqueeze(0))
        r[f"comp{f:g}"] = M.score(T, to_img(torch.cat(eyes)))
    rows.append(r)

lab_ = lambda r: "head" if r["scenario"] == "head" else f"walk {r['speed']:g}m/s" if r["scenario"] == "walk" else f"{r['scenario']} x{r['speed']:g}"
for f in fracs:
    k = f"comp{f:g}"
    print(f"\n== RIFE fields at {f:g} of the eye resolution, composed at full resolution (one sample per frame), minus edge ({len(rows)} cases; + = RIFE better)")
    for key, sign in (("psnr_band", 1), ("psnr_moving", 1), ("psnr", 1), ("edge_excess", -1), ("edge_deficit", -1)):
        d = [(r[k][key] - r["edge"][key]) * sign for r in rows if r[k][key] is not None and r["edge"][key] is not None]
        m = sum(d) / len(d); se = math.sqrt(sum((x - m) ** 2 for x in d) / (len(d) - 1) / len(d))
        print(f"  {key:13s} {m:+8.3f} +- {se:6.3f}  RIFE wins {sum(1 for x in d if x > 0):3d}/{len(d)}")
    print("  band / whole-frame PSNR by scenario (edge -> RIFE):")
    for L in sorted({lab_(r) for r in rows}, key=lambda x: (x.split()[0] != "head", x)):
        sel = [r for r in rows if lab_(r) == L]
        eb = sum(r["edge"]["psnr_band"] for r in sel) / len(sel); fb = sum(r[k]["psnr_band"] for r in sel) / len(sel)
        e2 = sum(r["edge"]["psnr"] for r in sel) / len(sel); f2 = sum(r[k]["psnr"] for r in sel) / len(sel)
        print(f"    {L:12s} band {eb:6.2f} -> {fb:6.2f} ({fb - eb:+5.2f})   frame {e2:6.2f} -> {f2:6.2f} ({f2 - e2:+5.2f})")
(lab / "rife_compose.json").write_text(json.dumps(rows))
