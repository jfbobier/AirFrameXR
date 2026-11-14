#!/usr/bin/env python3
"""PFM (float RGB, what the shim's picture probe dumps) -> 8-bit PNG, stdlib only.

    python3 harness/pfm2png.py in.pfm out.png [--mode clip|srgb|aces] [--exposure 1.0]

clip: values 0..1 as they are (use for the encoded output slot: it already holds display values);
srgb: linear -> sRGB encode of the clipped value (a game's FP16 swapchain, SDR view, what is above 1.0 is clipped);
aces: linear -> a filmic curve first (look at what is above 1.0). Pixels at NaN / Inf are painted magenta. Prints min / max.
"""
import math, struct, sys, zlib


def main():
    a = [x for x in sys.argv[1:] if not x.startswith("--")]
    mode = sys.argv[sys.argv.index("--mode") + 1] if "--mode" in sys.argv else "clip"
    exp = float(sys.argv[sys.argv.index("--exposure") + 1]) if "--exposure" in sys.argv else 1.0
    if mode in ("clip", "srgb", "aces"):
        a = [x for x in a if x not in (mode, str(exp))]
    src, dst = a[0], a[1]
    b = open(src, "rb").read()
    parts = b.split(b"\n", 3)
    assert parts[0] == b"PF", "not a colour PFM"
    w, h = map(int, parts[1].split())
    scale = float(parts[2])
    data = struct.unpack(("<" if scale < 0 else ">") + "f" * (w * h * 3), parts[3][: w * h * 12])
    lo, hi, bad = float("inf"), float("-inf"), 0

    def enc(v):
        v *= exp
        if mode == "aces":
            v = (v * (2.51 * v + 0.03)) / (v * (2.43 * v + 0.59) + 0.14)
        v = max(0.0, min(1.0, v))
        if mode in ("srgb", "aces"):
            v = 12.92 * v if v <= 0.0031308 else 1.055 * v ** (1 / 2.4) - 0.055
        return int(v * 255 + 0.5)

    rows = []
    for y in range(h - 1, -1, -1):  # PFM is bottom-to-top
        row = bytearray([0])
        for x in range(w):
            r, g, bl = data[(y * w + x) * 3: (y * w + x) * 3 + 3]
            if not all(map(math.isfinite, (r, g, bl))):
                bad += 1
                row += bytes((255, 0, 255))
                continue
            lo, hi = min(lo, r, g, bl), max(hi, r, g, bl)
            row += bytes((enc(r), enc(g), enc(bl)))
        rows.append(bytes(row))

    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(b"".join(rows), 6)) + chunk(b"IEND", b"")
    open(dst, "wb").write(png)
    print(f"{src}: {w}x{h}, min {lo:.4f} max {hi:.4f}, NaN/Inf pixels {bad} -> {dst} ({mode})")


main()
