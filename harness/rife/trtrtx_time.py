"""RIFE 4.4 ONNX (from trt_time.py) through TensorRT for RTX: ahead-of-time build (GPU-agnostic), on-device JIT at first use, steady-state timing."""
import sys, time, statistics
from pathlib import Path
import torch
import tensorrt_rtx as trt
lab = Path(sys.argv[1]); onnx_path = lab / sys.argv[2]
logger = trt.Logger(trt.Logger.WARNING)
t0 = time.perf_counter()
builder = trt.Builder(logger)
network = builder.create_network(0)
parser = trt.OnnxParser(network, logger)
if not parser.parse(onnx_path.read_bytes()):
    sys.exit("parse: " + "; ".join(str(parser.get_error(i)) for i in range(parser.num_errors)))
cfg = builder.create_builder_config()
plan = builder.build_serialized_network(network, cfg)
t1 = time.perf_counter()
rt = trt.Runtime(logger)
eng = rt.deserialize_cuda_engine(plan)
ctx = eng.create_execution_context()
dev = torch.device("cuda")
shape = lambda n: tuple(eng.get_tensor_shape(n))
dtype = lambda n: torch.float16 if eng.get_tensor_dtype(n) == trt.float16 else torch.float32
bufs = {n: torch.rand(shape(n), device=dev, dtype=dtype(n)) for n in ("img0", "img1", "timestep", "out")}
for n, t in bufs.items():
    ctx.set_tensor_address(n, t.data_ptr())
stream = torch.cuda.Stream()
t2 = time.perf_counter()
ctx.execute_async_v3(stream.cuda_stream); stream.synchronize()
t3 = time.perf_counter()
times = []
for k in range(60):
    a, b = torch.cuda.Event(enable_timing=True), torch.cuda.Event(enable_timing=True)
    a.record(stream); ctx.execute_async_v3(stream.cuda_stream); b.record(stream); b.synchronize()
    if k >= 10: times.append(a.elapsed_time(b))
print(f"TRT-RTX {trt.__version__} {onnx_path.name} {shape('img0')}: AOT build {t1 - t0:.1f} s, engine {plan.nbytes / 2**20:.1f} MB, first inference (JIT) {(t3 - t2) * 1000:.0f} ms, "
      f"steady median {statistics.median(times):.2f} ms p90 {sorted(times)[int(0.9 * len(times))]:.2f} max {max(times):.2f}")
