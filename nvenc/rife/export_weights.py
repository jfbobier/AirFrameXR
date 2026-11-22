"""RIFE 4.4's weights for airframexr_rife.dll (docs/NEURAL_RIFE_CUDA.md): flownet_v4.4.pkl (the published model, MIT) -> one .afxw file in
the layout the DLL hard-codes. Deterministic: the same .pkl gives the same bytes (no timestamps, fixed order, round-to-nearest fp16).

    python export_weights.py <flownet_v4.4.pkl> <out.afxw> [--any-source]

The .pkl's SHA-256 is pinned below; another file (a retrained 4.4, a future version) needs --any-source and, for another architecture, a new
layout in the DLL and here (the recipe says what to change). Needs torch (CPU is enough) and numpy.

Layout 2: a 128-byte header (magic AFXRIFE1, version 2, model 44, blocks 4, convblock 8, c[4], in[4], inPad[4], scale[4] as uint32, the
payload size as uint64, the source's SHA-256), then per block: conv0a W b, conv0b W b, convblock 0..7 W b, lastconv W b.
  conv W: float16 [K][3][3][C] (PyTorch's [K][C][3][3] permuted; conv0a's C zero-padded 7 -> 8, 12 -> 16), b: float16 [K]
  lastconv, ConvTranspose2d(c, 5, 4, stride 2, padding 1), rewritten as a 3x3 conv (stride 1, padding 1) at the input's resolution with 24
  outputs: output (py * 2 + px) * 5 + co is the transposed conv's output co at pixel (2m + py, 2q + px) of input pixel (m, q). Each parity reads
  input rows m - 1 + dr for dr in {py, py + 1} through kernel row ky = 3 + py - 2 dr (columns alike); the other taps are zero.
  W float16 [24][3][3][c] (outputs 20..23 zero), b float32 [24] (the 5 biases per parity, 20..23 zero)
"""
import hashlib
import struct
import sys
from pathlib import Path

import numpy as np
import torch

PKL_SHA256 = "a051572810b025c35013a35f044a08e6edb516ec1ffa1ac2878a48059f30d5fa"   # vs-rife release "model", flownet_v4.4.pkl
C = [192, 128, 96, 64]
IN = [7, 12, 12, 12]
IN_PAD = [8, 16, 16, 16]
SCALE = [8, 4, 2, 1]
CONVBLOCK = 8

args = [a for a in sys.argv[1:] if not a.startswith("--")]
src, out = Path(args[0]), Path(args[1])
raw = src.read_bytes()
sha = hashlib.sha256(raw).digest()
if sha.hex() != PKL_SHA256 and "--any-source" not in sys.argv:
    sys.exit(f"{src}: SHA-256 {sha.hex()} is not the pinned RIFE 4.4 file ({PKL_SHA256}); pass --any-source to export it anyway")

sd = torch.load(src, map_location="cpu", weights_only=True)
sd = {k.replace("module.", ""): v.detach().float().numpy() for k, v in sd.items() if k.startswith("module.")}
used = set()


def take(name, shape):
    a = sd[name]
    assert tuple(a.shape) == tuple(shape), f"{name}: {a.shape}, expected {shape}"
    used.add(name)
    return a


def conv(name, k, c, c_pad):
    w = take(name + ".weight", (k, c, 3, 3)).transpose(0, 2, 3, 1)        # KRSC
    w = np.pad(w, ((0, 0), (0, 0), (0, 0), (0, c_pad - c)))
    b = take(name + ".bias", (k,))
    return w.astype("<f2").tobytes() + b.astype("<f2").tobytes()


payload = bytearray()
for i in range(4):
    c, blk = C[i], f"block{i}"
    payload += conv(f"{blk}.conv0.0.0", c // 2, IN[i], IN_PAD[i])
    payload += conv(f"{blk}.conv0.1.0", c, c // 2, c // 2)
    for k in range(CONVBLOCK):
        payload += conv(f"{blk}.convblock.{k}.0", c, c, c)
    wt = take(f"{blk}.lastconv.weight", (c, 5, 4, 4))                    # [ci][co][ky][kx]
    bt = take(f"{blk}.lastconv.bias", (5,))
    w = np.zeros((24, 3, 3, c), np.float32)
    b = np.zeros(24, np.float32)
    for py in range(2):
        for px in range(2):
            o = (py * 2 + px) * 5
            b[o:o + 5] = bt
            for dr in (py, py + 1):
                for dc in (px, px + 1):
                    w[o:o + 5, dr, dc, :] = wt[:, :, 3 + py - 2 * dr, 3 + px - 2 * dc].T
    payload += w.astype("<f2").tobytes() + b.astype("<f4").tobytes()

# the .pkl is a training checkpoint: the teacher block and the context / refine networks of training are in it, inference (vs-rife's IFNet,
# load_state_dict(strict=False)) never uses them. Anything else left over means another architecture.
TRAINING_ONLY = ("block_tea.", "contextnet.", "unet.")
left = sorted(k for k in set(sd) - used if not k.startswith(TRAINING_ONLY))
assert not left, f"weights the layout does not use (another architecture?): {left}"

header = struct.pack("<8s4I16IQ32s", b"AFXRIFE1", 2, 44, 4, CONVBLOCK, *C, *IN, *IN_PAD, *SCALE, len(payload), sha)
assert len(header) == 128
data = header + bytes(payload)
out.write_bytes(data)
print(f"{out}: {len(data)} bytes, sha256 {hashlib.sha256(data).hexdigest()}, from {src.name} sha256 {sha.hex()}")
