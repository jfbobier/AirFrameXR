"""The fields model (export_fields_onnx.py) as a PORTABLE TensorRT engine, the kind the 34 MB lean runtime can run without building on the user's
machine: version compatible (+ the lean runtime excluded from the plan), hardware compatibility Ampere+ (RTX 30/40/50), a range of input sizes in
one profile. Timed against a plain engine (this GPU, this size) at the same size.

    python trt_portable_time.py <lab dir> [eye WxH at the network's resolution]
"""
import sys, time, statistics
from pathlib import Path
import torch
import tensorrt as trt

lab = Path(sys.argv[1])
W, H = (int(x) for x in (sys.argv[2] if len(sys.argv) > 2 else "960x1184").split("x"))
onnx = (lab / "rife44_fields.onnx").read_bytes()
logger = trt.Logger(trt.Logger.WARNING)
dev = torch.device("cuda")


def build(portable):
    b = trt.Builder(logger)
    net = b.create_network(1 << int(trt.NetworkDefinitionCreationFlag.STRONGLY_TYPED))
    p = trt.OnnxParser(net, logger)
    assert p.parse(onnx), [str(p.get_error(k)) for k in range(p.num_errors)]
    cfg = b.create_builder_config()
    cfg.set_memory_pool_limit(trt.MemoryPoolType.WORKSPACE, 4 << 30)
    prof = b.create_optimization_profile()
    lo, hi = ((2, 3, 256, 256), (2, 3, 2048, 2048)) if portable else ((2, 3, H, W), (2, 3, H, W))
    for name in ("img0", "img1"):
        prof.set_shape(name, lo, (2, 3, H, W), hi)
    prof.set_shape("t", (2, 1, 1, 1), (2, 1, 1, 1), (2, 1, 1, 1))
    cfg.add_optimization_profile(prof)
    if portable:
        cfg.set_flag(trt.BuilderFlag.VERSION_COMPATIBLE)
        cfg.set_flag(trt.BuilderFlag.EXCLUDE_LEAN_RUNTIME)
        cfg.hardware_compatibility_level = trt.HardwareCompatibilityLevel.AMPERE_PLUS
    t0 = time.perf_counter()
    plan = b.build_serialized_network(net, cfg)
    return plan, time.perf_counter() - t0


for portable in (False, True):
    plan, secs = build(portable)
    if plan is None:
        print("build failed", "portable" if portable else "plain"); continue
    rt = trt.Runtime(logger)
    if portable:
        rt.engine_host_code_allowed = True
    eng = rt.deserialize_cuda_engine(plan)
    ctx = eng.create_execution_context()
    i0 = torch.rand(2, 3, H, W, device=dev); i1 = torch.rand_like(i0); t = torch.full([2, 1, 1, 1], 0.5, device=dev)
    fl = torch.empty(2, 4, H, W, device=dev); mk = torch.empty(2, 1, H, W, device=dev)
    for name, x in (("img0", i0), ("img1", i1), ("t", t)):
        ctx.set_input_shape(name, tuple(x.shape)); ctx.set_tensor_address(name, x.data_ptr())
    ctx.set_tensor_address("flow", fl.data_ptr()); ctx.set_tensor_address("mask", mk.data_ptr())
    s = torch.cuda.Stream(); times = []
    for k in range(60):
        a, b2 = torch.cuda.Event(enable_timing=True), torch.cuda.Event(enable_timing=True)
        a.record(s); ctx.execute_async_v3(s.cuda_stream); b2.record(s); b2.synchronize()
        if k >= 10: times.append(a.elapsed_time(b2))
    print(f"{'portable (version compatible, Ampere+, sizes 256..2048)' if portable else 'plain (this GPU, fixed size)':58s} {W}x{H} x2: median {statistics.median(times):.2f} ms, "
          f"p90 {sorted(times)[int(0.9 * len(times))]:.2f}; engine {plan.nbytes / 2**20:.1f} MB, build {secs:.0f} s")
