"""Where does RIFE 4.4 (airframexr_rife.dll) put the content for an asked time t? The 1.4.2 hotfix's measurement (docs/NEURAL_RIFE_CUDA.md
"Output time"), kept as a regression check for the network and for the engine's correction (framegen.cpp kNeuralEdgeLo / kNeuralEdgeHi /
kNeuralRevHi).

For each t, the fields of a dumped judge pair: the content's displacement from frame 0 is -F_t->0, the full motion at that point M = F_t->1 -
F_t->0 (linear motion), so the effective time t_eff = -F_t->0 . M / |M|^2 over the moving pixels (median). Printed per t: direct (A, B, t),
reversed (B, A, 1 - t, fields swapped back) and the network's part of the engine's rule (0.5 < t < 0.6: reversed, else direct; below 0.12
and above 0.9 the engine draws with the edge synthesis instead, marked "edge"), with its error. 2026-11-22 (5 walking cases): direct worst 0.18
(content backwards near t = 0, stuck at 0.5 just above it); with the rule, the network's times within 0.04 from 0.12 to 0.9.

    python t_linearity.py <lab dir> <dir with airframexr_rife.dll + rife44.afxw> [cases, default 7,8,9,17,26]   (lab venv, a judge dump)
"""
import ctypes, math, sys
from pathlib import Path
import numpy as np, torch, torch.nn.functional as F

lab, dlldir = Path(sys.argv[1]), Path(sys.argv[2])
cases = [int(c) for c in (sys.argv[3] if len(sys.argv) > 3 else "7,8,9,17,26").split(",")]
sys.path.insert(0, str(lab))
import rife_eval_metrics as MM  # noqa: E402

EDGE_LO, EDGE_HI, REV_HI = 0.12, 0.90, 0.60   # framegen.cpp kNeuralEdgeLo, kNeuralEdgeHi, kNeuralRevHi
dev = torch.device("cuda"); torch.zeros(1, device=dev)
dll = ctypes.CDLL(str(dlldir / "airframexr_rife.dll"))
dll.afx_rife_create.restype = ctypes.c_void_p
dll.afx_rife_create.argtypes = [ctypes.c_wchar_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
dll.afx_rife_run.argtypes = [ctypes.c_void_p, ctypes.c_uint64, ctypes.c_uint64, ctypes.POINTER(ctypes.c_float), ctypes.c_uint64, ctypes.c_uint64,
                             ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
dll.afx_rife_destroy.argtypes = [ctypes.c_void_p]
err = ctypes.create_string_buffer(512)
ts = [0.02, 0.05, 0.08, 0.10, 0.12, 0.15, 0.2, 0.3, 1 / 3, 0.4, 0.45, 0.48, 0.5, 0.52, 0.55, 0.58, 0.6, 2 / 3, 0.7, 0.8, 0.85, 0.9, 0.95, 0.98]
tab = {t: [] for t in ts}
for case in cases:
    A, B = (MM.ppm(lab / "frames" / f"case_{case:03d}_{k}.ppm") for k in ("A", "B"))
    ew = A.shape[1] // 2; hl, wl = A.shape[0] // 2, ew // 2
    H, W = math.ceil(hl / 32) * 32, math.ceil(wl / 32) * 32

    def prep(img):
        x = torch.from_numpy(np.stack([img[:, :ew], img[:, ew:]])).to(dev).permute(0, 3, 1, 2).float() / 255.0
        x = F.interpolate(x, size=(hl, wl), mode="bilinear", antialias=True, align_corners=False)
        return F.pad(x, (0, W - wl, 0, H - hl)).contiguous()

    i0, i1 = prep(A), prep(B)
    h = dll.afx_rife_create(str(dlldir / "rife44.afxw"), 2, W, H, err, 512)
    if not h:
        sys.exit(f"afx_rife_create: {err.value.decode()}")
    fl = torch.empty(2, 4, H, W, device=dev); mk = torch.empty(2, 1, H, W, device=dev)
    s = torch.cuda.current_stream()

    def run(x0, x1, t):
        tt = (ctypes.c_float * 2)(t, t)
        dll.afx_rife_run(h, x0.data_ptr(), x1.data_ptr(), tt, fl.data_ptr(), mk.data_ptr(), ctypes.c_void_p(s.cuda_stream), err, 512)
        torch.cuda.synchronize()
        return fl[:, :, :hl, :wl].clone()

    def teff(fa, fb):   # fa: flow from the output to frame A, fb: to frame B
        M = fb - fa; m2 = (M * M).sum(1); sel = m2 > 4.0
        return (-(fa * M).sum(1) / m2.clamp(min=1e-6))[sel].median().item() if sel.any() else float("nan")

    for t in ts:
        d, r = run(i0, i1, t), run(i1, i0, 1 - t)
        tab[t].append((teff(d[:, 0:2], d[:, 2:4]), teff(r[:, 2:4], r[:, 0:2])))
    dll.afx_rife_destroy(h)


def mean(v):
    v = [x for x in v if x == x]
    return sum(v) / len(v) if v else float("nan")


print(f"cases {cases}: t_eff (mean over the cases with moving pixels)")
print("     t   direct  reversed  engine   engine error")
worst_d = worst_e = 0.0
for t in ts:
    dm, rm = mean([x[0] for x in tab[t]]), mean([x[1] for x in tab[t]])
    if dm == dm:
        worst_d = max(worst_d, abs(dm - t))
    if t < EDGE_LO or t > EDGE_HI:
        print(f"  {t:5.3f}  {dm:6.3f}  {rm:7.3f}    edge")
        continue
    e = rm if 0.5 < t < REV_HI else dm
    worst_e = max(worst_e, abs(e - t))
    print(f"  {t:5.3f}  {dm:6.3f}  {rm:7.3f}  {e:6.3f}  {e - t:+.3f}")
print(f"worst |t_eff - t|: direct {worst_d:.3f}, the network's part of the engine's rule {worst_e:.3f}")
