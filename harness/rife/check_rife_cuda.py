"""Checks airframexr_rife.dll (nvenc/rife, docs/NEURAL_RIFE_CUDA.md) against RIFE 4.4 in PyTorch (vs-rife's model code, the same weights): the
fields on real judge frame pairs at the streamer's network size (eye at half resolution, padded to multiples of 32), then the GPU time of a run.

    python check_rife_cuda.py <lab dir> <dir with airframexr_rife.dll + rife44.afxw> [cases, e.g. 0,9,20] [t, e.g. 0.5]

Per case: flow |ours - reference| mean / p99 / max in network pixels, against the fp32 reference and, for scale, the fp16 reference against the
fp32 one (the precision the model ran at under TensorRT); mask logit differences; and the PSNR between the pictures the fields compose (both
frames warped by their flow, blended by sigmoid(mask), unpadded area): ours vs fp32, fp16 vs fp32. Then the median GPU time of a run.
"""
import copy
import ctypes
import json
import math
import statistics
import sys
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F

lab, dlldir = Path(sys.argv[1]), Path(sys.argv[2])
cases = [int(c) for c in (sys.argv[3] if len(sys.argv) > 3 else "0,9").split(",")]
tval = float(sys.argv[4]) if len(sys.argv) > 4 else 0.5
sys.path.insert(0, str(lab))
from rifepkg.IFNet_HDv3_v4_4 import IFNet  # noqa: E402
import rife_eval_metrics as MM  # noqa: E402

dev = torch.device("cuda")
torch.backends.cudnn.allow_tf32 = False
torch.backends.cuda.matmul.allow_tf32 = False
sd = torch.load(lab / "models" / "flownet_v4.4.pkl", map_location="cpu")
sd = {k.replace("module.", ""): v for k, v in sd.items() if "module." in k}
net32 = IFNet(1.0, False)
net32.load_state_dict(sd, strict=False)
net32 = net32.eval().to(dev).float()
net16 = copy.deepcopy(net32).half()


def grid_of(h, w):
    gx = torch.arange(w, device=dev).float() * (2.0 / (w - 1)) - 1.0
    gy = torch.arange(h, device=dev).float() * (2.0 / (h - 1)) - 1.0
    return torch.stack([gx.view(1, w).expand(h, w), gy.view(h, 1).expand(h, w)], 0).unsqueeze(0)


def warp(img, flow):   # vs-rife warplayer: grid_sample bilinear, border, align_corners True, fp32 coordinates
    h, w = img.shape[2], img.shape[3]
    f = torch.cat([flow[:, 0:1].float() * (2.0 / (w - 1)), flow[:, 1:2].float() * (2.0 / (h - 1))], 1)
    g = (grid_of(h, w) + f).permute(0, 2, 3, 1)
    return F.grid_sample(img.float(), g, mode="bilinear", padding_mode="border", align_corners=True).to(img.dtype)


def fields(net, img0, img1, t, dt):   # the model's forward to the fields (IFNet_HDv3_v4_4.py without the final blend), at precision dt
    h, w = img0.shape[2], img0.shape[3]
    a, b = img0.clamp(0, 1).to(dt), img1.clamp(0, 1).to(dt)
    ts = t.to(dt).expand(-1, -1, h, w)
    blocks = [net.block0, net.block1, net.block2, net.block3]
    wa, wb, flow, mask = a, b, None, None
    for i in range(4):
        if flow is None:
            flow, mask = blocks[i](torch.cat((a, b, ts), 1), None, scale=net.scale_list[i])
        else:
            f0, m0 = blocks[i](torch.cat((wa, wb, ts, mask), 1), flow, scale=net.scale_list[i])
            flow, mask = flow + f0, mask + m0
        if i < 3:
            wa, wb = warp(a, flow[:, :2]), warp(b, flow[:, 2:4])
    return flow.float(), mask.float()


def compose(i0, i1, flow, mask):
    m = torch.sigmoid(mask)
    return warp(i0, flow[:, :2]) * m + warp(i1, flow[:, 2:4]) * (1 - m)


def psnr(x, y):
    mse = ((x - y) ** 2).mean().item()
    return 99.0 if mse == 0 else 10 * math.log10(1.0 / mse)


def stats(d):
    d = d.flatten()
    return f"mean {d.mean().item():.4f} p99 {torch.quantile(d[:: max(1, d.numel() // 2_000_000)], 0.99).item():.3f} max {d.max().item():.3f}"


dll = ctypes.CDLL(str(dlldir / "airframexr_rife.dll"))
dll.afx_rife_abi.restype = ctypes.c_int
dll.afx_rife_create.restype = ctypes.c_void_p
dll.afx_rife_create.argtypes = [ctypes.c_wchar_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
dll.afx_rife_run.restype = ctypes.c_int
dll.afx_rife_run.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_uint64, ctypes.POINTER(ctypes.c_float), ctypes.c_uint64, ctypes.c_uint64,
                             ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
dll.afx_rife_info.restype = ctypes.c_char_p
dll.afx_rife_info.argtypes = [ctypes.c_void_p]
dll.afx_rife_destroy.argtypes = [ctypes.c_void_p]
print(f"airframexr_rife.dll abi {dll.afx_rife_abi()}")

err = ctypes.create_string_buffer(512)
handle, hw = None, None
results = []
for case in cases:
    A, B = (MM.ppm(lab / "frames" / f"case_{case:03d}_{k}.ppm") for k in ("A", "B"))
    ew = A.shape[1] // 2
    hl, wl = A.shape[0] // 2, ew // 2
    H, W = math.ceil(hl / 32) * 32, math.ceil(wl / 32) * 32

    def prep(img):
        x = torch.from_numpy(np.stack([img[:, :ew], img[:, ew:]])).to(dev).permute(0, 3, 1, 2).float() / 255.0
        x = F.interpolate(x, size=(hl, wl), mode="bilinear", antialias=True, align_corners=False)
        return F.pad(x, (0, W - wl, 0, H - hl)).contiguous()

    i0, i1 = prep(A), prep(B)
    t = torch.full([2, 1, 1, 1], tval, device=dev)
    if handle is None or hw != (W, H):
        if handle:
            dll.afx_rife_destroy(handle)
        handle = dll.afx_rife_create(str(dlldir / "rife44.afxw"), 2, W, H, err, len(err))
        if not handle:
            sys.exit(f"afx_rife_create: {err.value.decode()}")
        hw = (W, H)
        print(dll.afx_rife_info(handle).decode())
    fl = torch.full([2, 4, H, W], float("nan"), device=dev)
    mk = torch.full([2, 1, H, W], float("nan"), device=dev)
    tt = (ctypes.c_float * 2)(tval, tval)
    s = torch.cuda.current_stream()
    if dll.afx_rife_run(handle, i0.data_ptr(), i1.data_ptr(), tt, fl.data_ptr(), mk.data_ptr(), ctypes.c_void_p(s.cuda_stream), err, len(err)):
        sys.exit(f"afx_rife_run: {err.value.decode()}")
    torch.cuda.synchronize()
    with torch.inference_mode():
        f32, m32 = fields(net32, i0, i1, t, torch.float32)
        f16, m16 = fields(net16, i0, i1, t, torch.float16)
    nan = int(torch.isnan(fl).sum().item() + torch.isnan(mk).sum().item())
    v = (slice(None), slice(None), slice(0, hl), slice(0, wl))
    c32, cus, c16 = compose(i0, i1, f32, m32)[v], compose(i0, i1, fl, mk)[v], compose(i0, i1, f16, m16)[v]
    r = {"case": case, "size": f"{W}x{H}", "t": tval, "nan": nan, "flow_mag_mean": f32[v].abs().mean().item(),
         "ours_vs_fp32": {"flow": stats((fl - f32)[v].abs()), "mask": stats((mk - m32)[v].abs()), "compose_psnr": psnr(cus, c32)},
         "fp16_vs_fp32": {"flow": stats((f16 - f32)[v].abs()), "mask": stats((m16 - m32)[v].abs()), "compose_psnr": psnr(c16, c32)}}
    results.append(r)
    print(json.dumps(r))



def timing(h, W, H, x0, x1, f, m):   # GPU time of one run (both eyes), CUDA events on the current stream
    times = []
    s = torch.cuda.current_stream()
    for k in range(120):
        a, b = torch.cuda.Event(enable_timing=True), torch.cuda.Event(enable_timing=True)
        a.record(s)
        dll.afx_rife_run(h, x0.data_ptr(), x1.data_ptr(), tt, f.data_ptr(), m.data_ptr(), ctypes.c_void_p(s.cuda_stream), err, len(err))
        b.record(s)
        b.synchronize()
        if k >= 20:
            times.append(a.elapsed_time(b))
    print(f"run {W}x{H} x2: median {statistics.median(times):.3f} ms, p90 {sorted(times)[int(0.9 * len(times))]:.3f} ms, min {min(times):.3f} ms")


timing(handle, hw[0], hw[1], i0, i1, fl, mk)
dll.afx_rife_destroy(handle)
# the owner's stream (1920 x 2368 per eye) at half resolution: 960 x 1184 per eye (random content: the time does not depend on it)
W, H = 960, 1184
h2 = dll.afx_rife_create(str(dlldir / "rife44.afxw"), 2, W, H, err, len(err))
x0, x1 = torch.rand(2, 3, H, W, device=dev), torch.rand(2, 3, H, W, device=dev)
f, m = torch.empty(2, 4, H, W, device=dev), torch.empty(2, 1, H, W, device=dev)
timing(h2, W, H, x0, x1, f, m)
dll.afx_rife_destroy(h2)
