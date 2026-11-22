import sys, math, time, statistics, torch, torch.nn.functional as F
from pathlib import Path
lab = Path(sys.argv[1]); sys.path.insert(0, str(lab))
from rifepkg.IFNet_HDv3_v4_4 import IFNet
sd = torch.load(lab / "models" / "flownet_v4.4.pkl", map_location="cpu")
sd = {k.replace("module.", ""): v for k, v in sd.items() if "module." in k}
dev, dt = torch.device("cuda"), torch.float16
ew, h = 1920, 2368
for s in (1.0, 0.5, 0.25):
    n = IFNet(s, False); n.load_state_dict(sd, strict=False); n = n.eval().to(dev, dt)
    tmp = max(32, int(32 / s)); pw, ph = math.ceil(ew / tmp) * tmp, math.ceil(h / tmp) * tmp
    x0 = torch.rand(2, 3, ph, pw, device=dev, dtype=dt); x1 = torch.rand(2, 3, ph, pw, device=dev, dtype=dt)
    div = torch.tensor([(pw - 1.0) / 2.0, (ph - 1.0) / 2.0], dtype=torch.float, device=dev)
    gh = torch.linspace(-1.0, 1.0, pw, device=dev).view(1, 1, 1, pw).expand(-1, -1, ph, -1)
    gv = torch.linspace(-1.0, 1.0, ph, device=dev).view(1, 1, ph, 1).expand(-1, -1, -1, pw)
    grid = torch.cat([gh, gv], 1); ts = torch.full([2, 1, ph, pw], 0.5, dtype=dt, device=dev)
    t = []
    with torch.inference_mode():
        for k in range(25):
            torch.cuda.synchronize(); a = time.perf_counter(); n(x0, x1, ts, div, grid); torch.cuda.synchronize()
            if k >= 5: t.append((time.perf_counter() - a) * 1000)
    print(f"owner's stream {ew}x{h} per eye, batch 2, FP16 PyTorch eager, scale {s:g}: median {statistics.median(t):.2f} ms, max {max(t):.2f} ms")
