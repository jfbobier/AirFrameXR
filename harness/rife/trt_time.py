"""RIFE 4.4 as a TensorRT engine: ONNX export at a fixed shape (both eyes batched, FP16), strongly typed engine build, timing; plus the size of the
TensorRT runtime DLLs a native (no Python) integration would ship.

    python trt_time.py <lab dir> [eye WxH] [scales]
"""
import math, os, sys, time, statistics
from pathlib import Path
import torch
import torch.nn as nn
import tensorrt as trt

lab = Path(sys.argv[1])
ew, eh = (int(x) for x in (sys.argv[2] if len(sys.argv) > 2 else "1920x2368").split("x"))
scales = [float(s) for s in (sys.argv[3] if len(sys.argv) > 3 else "1.0,0.5,0.25").split(",")]
sys.path.insert(0, str(lab))
from rifepkg.IFNet_HDv3_v4_4 import IFNet  # noqa: E402

dev = torch.device("cuda")
sd = torch.load(lab / "models" / "flownet_v4.4.pkl", map_location="cpu")
sd = {k.replace("module.", ""): v for k, v in sd.items() if "module." in k}

libdir = Path(trt.__file__).parent.parent / "tensorrt_libs"
print("TensorRT", trt.__version__, "runtime DLLs in", libdir)
tot = 0
for f in sorted(libdir.glob("*.dll")):
    sz = f.stat().st_size; tot += sz
    print(f"  {sz / 2**20:9.1f} MB  {f.name}")
print(f"  {tot / 2**20:9.1f} MB  total")


class Wrap(nn.Module):   # the grid and its scale baked in; inputs: both eyes' frames and the timestep
    def __init__(self, net, pw, ph):
        super().__init__()
        self.net = net
        self.register_buffer("div", torch.tensor([(pw - 1.0) / 2.0, (ph - 1.0) / 2.0], dtype=torch.float))
        gh = torch.linspace(-1.0, 1.0, pw).view(1, 1, 1, pw).expand(-1, -1, ph, -1)
        gv = torch.linspace(-1.0, 1.0, ph).view(1, 1, ph, 1).expand(-1, -1, -1, pw)
        self.register_buffer("grid", torch.cat([gh, gv], 1).contiguous())

    def forward(self, img0, img1, timestep):
        return self.net(img0, img1, timestep, self.div, self.grid)


logger = trt.Logger(trt.Logger.WARNING)
for s in scales:
    tmp = max(32, int(32 / s))
    pw, ph = math.ceil(ew / tmp) * tmp, math.ceil(eh / tmp) * tmp
    net = IFNet(s, False); net.load_state_dict(sd, strict=False)
    m = Wrap(net.eval(), pw, ph).to(dev).half()
    m.div.data = m.div.data.float(); m.grid.data = m.grid.data.float()
    x0 = torch.rand(2, 3, ph, pw, device=dev, dtype=torch.float16); x1 = torch.rand_like(x0)
    ts = torch.full([2, 1, ph, pw], 0.5, device=dev, dtype=torch.float16)
    onnx_path = lab / f"rife44_{pw}x{ph}_s{s:g}.onnx"
    t0 = time.perf_counter()
    with torch.inference_mode():
        torch.onnx.export(m, (x0, x1, ts), str(onnx_path), input_names=["img0", "img1", "timestep"], output_names=["out"], opset_version=17, dynamo=False)
    t1 = time.perf_counter()
    builder = trt.Builder(logger)
    network = builder.create_network(1 << int(trt.NetworkDefinitionCreationFlag.STRONGLY_TYPED))
    parser = trt.OnnxParser(network, logger)
    if not parser.parse(onnx_path.read_bytes()):
        print("ONNX parse failed:", [str(parser.get_error(i)) for i in range(parser.num_errors)]); continue
    cfg = builder.create_builder_config()
    cfg.set_memory_pool_limit(trt.MemoryPoolType.WORKSPACE, 4 << 30)
    plan = builder.build_serialized_network(network, cfg)
    t2 = time.perf_counter()
    if plan is None:
        print("engine build failed"); continue
    engine_path = lab / f"rife44_{pw}x{ph}_s{s:g}.engine"
    engine_path.write_bytes(bytes(plan))
    rt = trt.Runtime(logger)
    eng = rt.deserialize_cuda_engine(plan)
    ctx = eng.create_execution_context()
    out = torch.empty(2, 3, ph, pw, device=dev, dtype=torch.float16 if eng.get_tensor_dtype("out") == trt.float16 else torch.float32)
    for name, t in (("img0", x0), ("img1", x1), ("timestep", ts), ("out", out)):
        ctx.set_tensor_address(name, t.data_ptr())
    stream = torch.cuda.Stream()
    times = []
    with torch.cuda.stream(stream):
        for k in range(60):
            a, b = torch.cuda.Event(enable_timing=True), torch.cuda.Event(enable_timing=True)
            a.record(stream); ctx.execute_async_v3(stream.cuda_stream); b.record(stream); b.synchronize()
            if k >= 10: times.append(a.elapsed_time(b))
    # a real frame pair (RIFE_REAL=<case index> of the judge dump, at the dump's eye size): TensorRT against PyTorch on it
    if os.environ.get("RIFE_REAL"):
        sys.path.insert(0, str(lab)); import rife_eval_metrics as MM, numpy as np
        i = int(os.environ["RIFE_REAL"])
        def ld(k):
            a = MM.ppm(lab / "frames" / f"case_{i:03d}_{k}.ppm"); w2 = a.shape[1] // 2
            t = torch.from_numpy(np.stack([a[:, :w2], a[:, w2:]])).to(dev).permute(0, 3, 1, 2).half() / 255.0
            return torch.nn.functional.pad(t, (0, pw - w2, 0, ph - a.shape[0]))
        x0.copy_(ld("A")); x1.copy_(ld("B"))
        ctx.execute_async_v3(stream.cuda_stream); stream.synchronize()
    # check: TensorRT against PyTorch on the same input (FP16 both)
    with torch.inference_mode():
        ref = m(x0, x1, ts).float()
    dd = (out.float() - ref).abs()
    err = dd.max().item()
    print(f"   TRT vs torch: mean |d| {dd.mean().item() * 255:.3f} levels, 99.9th pct {torch.quantile(dd.flatten()[::97], 0.999).item() * 255:.2f} levels, max {err * 255:.1f} levels")
    print(f"eye {ew}x{eh} (padded {pw}x{ph}) x2 eyes, scale {s:g}: TensorRT FP16 median {statistics.median(times):.2f} ms, p90 {sorted(times)[int(0.9 * len(times))]:.2f}, "
          f"max {max(times):.2f}; engine {engine_path.stat().st_size / 2**20:.1f} MB, onnx {onnx_path.stat().st_size / 2**20:.1f} MB; export {t1 - t0:.1f} s, build {t2 - t1:.1f} s; "
          f"max |TRT - torch| {err:.4f}")
