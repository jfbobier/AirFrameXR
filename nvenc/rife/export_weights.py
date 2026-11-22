"""RIFE 4.4's weights for airframexr_rife.dll (docs/NEURAL_RIFE_CUDA.md): flownet_v4.4.pkl (the published model, MIT) -> one .afxw file in
the layout the DLL hard-codes. Deterministic: the same .pkl gives the same bytes (no timestamps, fixed order, round-to-nearest-even fp16).

    python export_weights.py <flownet_v4.4.pkl> <out.afxw> [--any-source]

Python 3 standard library only (no torch, no numpy): the .pkl is PyTorch's zip checkpoint (<name>/data.pkl, a pickle of an ordered dict of
tensors whose bytes are in <name>/data/<key>), read with an unpickler that accepts nothing but those few types.
The .pkl's SHA-256 is pinned below; another file (a retrained 4.4, a future version) needs --any-source and, for another architecture, a new
layout in the DLL and here (the recipe says what to change).

Layout 2: a 128-byte header (magic AFXRIFE1, version 2, model 44, blocks 4, convblock 8, c[4], in[4], inPad[4], scale[4] as uint32, the
payload size as uint64, the source's SHA-256), then per block: conv0a W b, conv0b W b, convblock 0..7 W b, lastconv W b.
  conv W: float16 [K][3][3][C] (PyTorch's [K][C][3][3] permuted; conv0a's C zero-padded 7 -> 8, 12 -> 16), b: float16 [K]
  lastconv, ConvTranspose2d(c, 5, 4, stride 2, padding 1), rewritten as a 3x3 conv (stride 1, padding 1) at the input's resolution with 24
  outputs: output (py * 2 + px) * 5 + co is the transposed conv's output co at pixel (2m + py, 2q + px) of input pixel (m, q). Each parity reads
  input rows m - 1 + dr for dr in {py, py + 1} through kernel row ky = 3 + py - 2 dr (columns alike); the other taps are zero.
  W float16 [24][3][3][c] (outputs 20..23 zero), b float32 [24] (the 5 biases per parity, 20..23 zero)
"""
import array
import collections
import hashlib
import io
import pickle
import struct
import sys
import zipfile
from pathlib import Path

PKL_SHA256 = "a051572810b025c35013a35f044a08e6edb516ec1ffa1ac2878a48059f30d5fa"   # vs-rife release "model", flownet_v4.4.pkl
C = [192, 128, 96, 64]
IN = [7, 12, 12, 12]
IN_PAD = [8, 16, 16, 16]
SCALE = [8, 4, 2, 1]
CONVBLOCK = 8
TRAINING_ONLY = ("block_tea.", "contextnet.", "unet.")   # in the training checkpoint, never used by inference (vs-rife loads with strict=False)


class Tensor:
    """A float32 tensor of the checkpoint: its values (flat, the storage) and how to index them."""

    def __init__(self, storage, offset, size, stride):
        self.data, self.offset, self.shape, self.stride = storage, offset, tuple(size), tuple(stride)

    def at(self, *idx):
        return self.data[self.offset + sum(i * s for i, s in zip(idx, self.stride))]


def load_checkpoint(raw):
    z = zipfile.ZipFile(io.BytesIO(raw))
    pkl = next(n for n in z.namelist() if n.endswith("/data.pkl"))
    prefix = pkl[: -len("data.pkl")]
    storages = {}

    def rebuild(storage, offset, size, stride, *rest):
        return Tensor(storage, offset, size, stride)

    class Unpickler(pickle.Unpickler):
        def find_class(self, module, name):
            if (module, name) == ("collections", "OrderedDict"):
                return collections.OrderedDict
            if (module, name) == ("torch._utils", "_rebuild_tensor_v2"):
                return rebuild
            if module == "torch" and name == "FloatStorage":
                return name
            raise pickle.UnpicklingError(f"unexpected {module}.{name} in the checkpoint (another format: see the recipe)")

        def persistent_load(self, pid):
            kind, stype, key, _location, numel = pid
            if kind != "storage" or stype != "FloatStorage":
                raise pickle.UnpicklingError(f"unexpected storage {pid}")
            if key not in storages:
                a = array.array("f")
                a.frombytes(z.read(prefix + "data/" + key))
                if sys.byteorder != "little":
                    a.byteswap()
                assert len(a) == numel, (key, len(a), numel)
                storages[key] = a
            return storages[key]

    return Unpickler(io.BytesIO(z.read(pkl))).load()


def f16(values):
    return struct.pack(f"<{len(values)}e", *values)


def f32(values):
    return struct.pack(f"<{len(values)}f", *values)


args = [a for a in sys.argv[1:] if not a.startswith("--")]
src, out = Path(args[0]), Path(args[1])
raw = src.read_bytes()
sha = hashlib.sha256(raw).digest()
if sha.hex() != PKL_SHA256 and "--any-source" not in sys.argv:
    sys.exit(f"{src}: SHA-256 {sha.hex()} is not the pinned RIFE 4.4 file ({PKL_SHA256}); pass --any-source to export it anyway")

sd = {k.replace("module.", ""): v for k, v in load_checkpoint(raw).items() if k.startswith("module.")}
used = set()


def take(name, shape):
    t = sd[name]
    assert t.shape == tuple(shape), f"{name}: {t.shape}, expected {shape}"
    used.add(name)
    return t


def conv(name, k, c, c_pad):
    w, b = take(name + ".weight", (k, c, 3, 3)), take(name + ".bias", (k,))
    vals = [w.at(o, i, r, s) if i < c else 0.0 for o in range(k) for r in range(3) for s in range(3) for i in range(c_pad)]   # KRSC
    return f16(vals) + f16([b.at(o) for o in range(k)])


payload = bytearray()
for i in range(4):
    c, blk = C[i], f"block{i}"
    payload += conv(f"{blk}.conv0.0.0", c // 2, IN[i], IN_PAD[i])
    payload += conv(f"{blk}.conv0.1.0", c, c // 2, c // 2)
    for k in range(CONVBLOCK):
        payload += conv(f"{blk}.convblock.{k}.0", c, c, c)
    wt = take(f"{blk}.lastconv.weight", (c, 5, 4, 4))                    # [ci][co][ky][kx]
    bt = take(f"{blk}.lastconv.bias", (5,))
    w = [0.0] * (24 * 9 * c)                                              # [24][3][3][c]
    b = [0.0] * 24
    for py in range(2):
        for px in range(2):
            o = (py * 2 + px) * 5
            for co in range(5):
                b[o + co] = bt.at(co)
                for dr in (py, py + 1):
                    for dc in (px, px + 1):
                        base = (((o + co) * 3 + dr) * 3 + dc) * c
                        for ci in range(c):
                            w[base + ci] = wt.at(ci, co, 3 + py - 2 * dr, 3 + px - 2 * dc)
    payload += f16(w) + f32(b)

left = sorted(k for k in set(sd) - used if not k.startswith(TRAINING_ONLY))
assert not left, f"weights the layout does not use (another architecture?): {left}"

header = struct.pack("<8s4I16IQ32s", b"AFXRIFE1", 2, 44, 4, CONVBLOCK, *C, *IN, *IN_PAD, *SCALE, len(payload), sha)
assert len(header) == 128
data = header + bytes(payload)
out.write_bytes(data)
print(f"{out}: {len(data)} bytes, sha256 {hashlib.sha256(data).hexdigest()}, from {src.name} sha256 {sha.hex()}")
