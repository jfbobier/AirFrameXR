"""Export RIFE 4.4 (vs-rife model code, MIT; weights flownet_v4.4.pkl) as the FIELDS model the streamer's neural frame generation loads:
inputs img0, img1 (N,3,H,W float32 in [0,1], H and W multiples of 32: the caller pads with zeros) and t (N,1,1,1 float32, 0 = img0, 1 = img1);
outputs flow (N,4,H,W float32: xy displacement from the output time to img0, then to img1, in input pixels) and mask (N,1,H,W float32 logits:
sigmoid = img0's weight). The warp grid is built inside the graph from the input size (one file for every resolution); FP16 inside; the last
block's image warps are not computed (fields only). Then checks it under TensorRT for RTX against PyTorch on a judge frame pair.

    python export_fields_onnx.py <lab dir> [out.onnx] [check case index]
"""
import math, sys
from pathlib import Path
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

lab = Path(sys.argv[1])
out = Path(sys.argv[2]) if len(sys.argv) > 2 else lab / "rife44_fields.onnx"
case = int(sys.argv[3]) if len(sys.argv) > 3 else 9   # 9 = walk 2 m/s, start 0, 22.2 ms (fast motion: a wrong grid scale shows)
sys.path.insert(0, str(lab))
from rifepkg.IFNet_HDv3_v4_4 import IFNet  # noqa: E402

sd = torch.load(lab / "models" / "flownet_v4.4.pkl", map_location="cpu")
sd = {k.replace("module.", ""): v for k, v in sd.items() if "module." in k}
net = IFNet(1.0, False); net.load_state_dict(sd, strict=False); net = net.eval()


def warp(img, flow, step, grid):   # vs-rife warplayer, FP32 coordinates; step = (2 / (w - 1), 2 / (h - 1)): pixels -> grid units
    f = torch.cat([flow[:, 0:1].float() * step[0], flow[:, 1:2].float() * step[1]], 1)
    return F.grid_sample(img.float(), (grid + f).permute(0, 2, 3, 1), mode="bilinear", padding_mode="border", align_corners=True).to(img.dtype)


class Fields(nn.Module):
    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, img0, img1, t):
        h, w = img0.shape[2], img0.shape[3]
        a, b = img0.clamp(0, 1).half(), img1.clamp(0, 1).half()
        ts = t.half().expand(-1, -1, h, w)
        gx = torch.arange(w, device=img0.device).float() * (2.0 / (w - 1)) - 1.0
        gy = torch.arange(h, device=img0.device).float() * (2.0 / (h - 1)) - 1.0
        grid = torch.stack([gx.view(1, w).expand(h, w), gy.view(h, 1).expand(h, w)], 0).unsqueeze(0)
        step = torch.stack([gx[1] - gx[0], gy[1] - gy[0]])   # from the grid itself: stays dynamic in the graph (a Python number would be traced as a constant)
        blocks = [self.net.block0, self.net.block1, self.net.block2, self.net.block3]
        wa, wb, flow, mask = a, b, None, None
        for i in range(4):
            if flow is None:
                flow, mask = blocks[i](torch.cat((a, b, ts), 1), None, scale=self.net.scale_list[i])
            else:
                f0, m0 = blocks[i](torch.cat((wa, wb, ts, mask), 1), flow, scale=self.net.scale_list[i])
                flow, mask = flow + f0, mask + m0
            if i < 3:
                wa, wb = warp(a, flow[:, :2], step, grid), warp(b, flow[:, 2:4], step, grid)
        return flow.float(), mask.float()


dev = torch.device("cuda")
m = Fields(net).to(dev)
m.net.half()
x0 = torch.rand(2, 3, 576, 512, device=dev); x1 = torch.rand_like(x0); t = torch.full([2, 1, 1, 1], 0.5, device=dev)
with torch.inference_mode():
    torch.onnx.export(m, (x0, x1, t), str(out), input_names=["img0", "img1", "t"], output_names=["flow", "mask"], opset_version=17, dynamo=False,
                      dynamic_axes={"img0": {0: "n", 2: "h", 3: "w"}, "img1": {0: "n", 2: "h", 3: "w"}, "t": {0: "n"}, "flow": {0: "n", 2: "h", 3: "w"},
                                    "mask": {0: "n", 2: "h", 3: "w"}})
print(f"exported {out} ({out.stat().st_size / 2**20:.1f} MB)")

# check under TensorRT for RTX on a real pair (the judge dump), at a size the streamer would use (eye 1032x1104 at half resolution, padded)
import tensorrt_rtx as trt  # noqa: E402
import rife_eval_metrics as MM  # noqa: E402
A, B = (MM.ppm(lab / "frames" / f"case_{case:03d}_{k}.ppm") for k in ("A", "B"))
ew = A.shape[1] // 2
hl, wl = A.shape[0] // 2, ew // 2
H, W = math.ceil(hl / 32) * 32, math.ceil(wl / 32) * 32
def prep(img):
    x = torch.from_numpy(np.stack([img[:, :ew], img[:, ew:]])).to(dev).permute(0, 3, 1, 2).float() / 255.0
    x = F.interpolate(x, size=(hl, wl), mode="bilinear", antialias=True, align_corners=False)
    return F.pad(x, (0, W - wl, 0, H - hl)).contiguous()
i0, i1 = prep(A), prep(B)
with torch.inference_mode():
    rf, rm = m(i0, i1, t)
logger = trt.Logger(trt.Logger.WARNING)
builder = trt.Builder(logger); network = builder.create_network(0); parser = trt.OnnxParser(network, logger)
assert parser.parse(out.read_bytes()), [str(parser.get_error(k)) for k in range(parser.num_errors)]
cfg = builder.create_builder_config()
prof = builder.create_optimization_profile()
for name in ("img0", "img1"):
    prof.set_shape(name, (2, 3, H, W), (2, 3, H, W), (2, 3, H, W))
prof.set_shape("t", (2, 1, 1, 1), (2, 1, 1, 1), (2, 1, 1, 1))
cfg.add_optimization_profile(prof)
plan = builder.build_serialized_network(network, cfg)
eng = trt.Runtime(logger).deserialize_cuda_engine(plan)
ctx = eng.create_execution_context()
for name, x in (("img0", i0), ("img1", i1), ("t", t)):
    ctx.set_input_shape(name, tuple(x.shape)); ctx.set_tensor_address(name, x.data_ptr())
of, om = torch.empty_like(rf), torch.empty_like(rm)
ctx.set_tensor_address("flow", of.data_ptr()); ctx.set_tensor_address("mask", om.data_ptr())
s = torch.cuda.Stream(); ctx.execute_async_v3(s.cuda_stream); s.synchronize()
fe, me = (of - rf).abs(), (om - rm).abs()
print(f"TRT-RTX {trt.__version__} fields at {W}x{H} (case {case}): flow |d| mean {fe.mean().item():.4f} px, max {fe.max().item():.3f} px "
      f"(flow magnitude mean {rf.abs().mean().item():.2f} px); mask logits |d| mean {me.mean().item():.4f}, max {me.max().item():.3f}")
