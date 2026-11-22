# The neural frame generation network: RIFE 4.4 in our own CUDA (the recipe)

Since 1.4.2 (2026-11-22, on by default). The streamer's neural fields (docs/FRAME_GENERATION.md "Neural fields") come from one small DLL we build
ourselves: RIFE 4.4's network, hard-coded, as a chain of our CUDA kernels and CUTLASS convolutions. This page is the recipe: what the code
reproduces from the model, the weight layout, the build, the checks, and what to change for a new CUDA, CUTLASS or RIFE.

Sources: `nvenc/rife/` (`airframexr_rife.h` the C interface, `airframexr_rife.cu` the network, `export_weights.py` the weights, `build.ps1` the
build). Lab: `harness/rife/check_rife_cuda.py` (against PyTorch). User: `nvenc/hostlib/neural.cpp` (loads the DLL, feeds it from D3D11).

## What ships

| file | size | what | licence |
|---|---|---|---|
| `airframexr_rife.dll` | 747 KB | our kernels + CUTLASS's convolution kernels + the CUDA runtime, linked in. Machine code for sm_86 (RTX 30; also runs on RTX 40, sm_89) and sm_120 (RTX 50), PTX for compute_86 (the driver compiles it for newer GPUs) | ours; CUTLASS BSD-3-Clause; CUDA runtime: NVIDIA CUDA EULA (redistributable part) |
| `rife44.afxw` | 10.5 MB | RIFE 4.4's weights in the DLL's layout | MIT (Practical-RIFE by hzwer, file from vs-rife's release) |

Both go in the host's `neural` folder (or `AIRFRAMEXR_FG_NEURAL_DIR`). CUDA comes from the driver (`nvcuda.dll`). No TensorRT, no ONNX, no Python.
It needs compute capability 8.0 or later (RTX 30 or newer); on anything else the DLL says so and the streamer keeps the edge synthesis.

Clean room: written from vs-rife's model code (`IFNet_HDv3_v4_4.py`, MIT) and CUTLASS's public API and examples (16, 17). No code read from
other inference engines (libaji / animejanai-inference has no licence).

## Build: one command, pinned inputs, same bytes every time

On the Windows builder (`python3 harness/run.py build neural` does this into `out\neural`; `harness/bundle.py` runs it; the package ships
both files in `bin\neural` and fails without them):

    powershell -ExecutionPolicy Bypass -File nvenc\rife\build.ps1 -Out <dir> [-Python <python.exe>]

| pin | value |
|---|---|
| CUDA toolkit | 13.5 (nvcc V13.5.37); only `nvcc_13.5 crt_13.5 nvvm_13.5 cudart_13.5 cccl_13.5` from `cuda_13.5.0_windows_x86_64_network.exe -s ...` (no driver) |
| C++ | Visual Studio 2022 (cl 19.44), found by vswhere |
| CUTLASS | 4.8.1, `https://github.com/NVIDIA/cutlass/archive/refs/tags/v4.8.1.zip`, SHA-256 `3bae692a...a296e5` (headers only) |
| RIFE 4.4 | `https://github.com/HolyWu/vs-rife/releases/download/model/flownet_v4.4.pkl`, SHA-256 `a0515728...30d5fa` |
| Python | 3, standard library only (`export_weights.py` reads PyTorch's zip checkpoint itself; same bytes as a torch + numpy export) |
| flags | `-O3 -std=c++17 --shared -cudart static -DNDEBUG --expt-relaxed-constexpr`, cl `/MT /O2 /EHsc /Zc:__cplusplus /Zc:preprocessor /utf-8`, link `/Brepro`, gencode sm_86 + sm_120 + compute_86 PTX |

The script downloads the two inputs into `external/rife-build` when missing and stops on a hash mismatch, checks nvcc's version (`-AnyVersion`
to build with another), exports the weights, compiles (about 10 s), and writes `build_manifest.txt`: every tool version, input and output hash,
no dates. Two builds in the same folder give identical files; the weights are identical anywhere (`c844ab0f...`), the DLL is not: built from
the same source in another folder (the release builder's against the lab's) its bytes differ (nvcc names a file's internal symbols after its
path), its results do not (`check_rife_cuda.py` on the 1.4.2 release DLL: the same flow and mask differences as the tested build, 1.81 ms at
960 x 1184).

The builder needs the CUDA toolkit (today only the 5090 has it). For the lab's `target\release` host, copy `out\neural\*` into
`target\release\neural`.

Pitfalls met: CUDA 13's CCCL refuses MSVC's traditional preprocessor (`/Zc:preprocessor`; cl's own message never showed, nvcc said only
"error 0x2"); CUTLASS's device convolution header must come after the kernel header (`implicit_gemm_tensor_c_size`).

## The network, hard-coded

RIFE 4.4 = vs-rife's `IFNet(scale 1, ensemble off)`: four blocks at 1/8, 1/4, 1/2 and 1 of the input size, each refining the fields of the one
before. Inputs: both frames, float RGB 0..1 (clamped), and t. Outputs: flow (4 channels: x y from the output time to frame 0, then to frame 1,
in network pixels) and mask (the logit of frame 0's weight). The streamer feeds it the eye at half resolution, padded to multiples of 32.

Per block (scale s, channels c; `airframexr_rife.cu`):

| step | model code | ours |
|---|---|---|
| input | `cat(img0, img1, t)` (block 0) or `cat(warp(img0, flow01), warp(img1, flow10), t, mask)`, `F.interpolate(1/s)`, then `+ interpolate(flow, 1/s) / s` | `kPack`: one NHWC fp16 tensor, 8 channels (7 + 0) or 16 (12 + 0) |
| conv0 | 2 x (3x3 conv stride 2 + LeakyReLU 0.2): c/2, then c channels at 1/4s | CUTLASS fprop, bias + LeakyReLU in the epilogue |
| convblock | 8 x (3x3 conv + LeakyReLU), no residual in 4.4 | same |
| lastconv | `ConvTranspose2d(c, 5, 4, 2, 1)` to 1/2s | CUTLASS fprop, a 3x3 conv with 24 outputs (below), fp32 out |
| output | `interpolate(x 2s)`, flow x 2s, added to the fields (block 0: the fields) | `kUpAcc` |

Shapes at the owner's stream (eye 1920 x 2368, network 960 x 1184 per eye, both eyes batched):

| block | s | c | input | conv0 out | convs at | last conv | upscale |
|---|---|---|---|---|---|---|---|
| 0 | 8 | 192 | 120 x 148 x 8 | 60 x 74 x 96 | 30 x 37 x 192 | 30 x 37 x 24 (= 60 x 74 x 5) | x 16 |
| 1 | 4 | 128 | 240 x 296 x 16 | 120 x 148 x 64 | 60 x 74 x 128 | 60 x 74 x 24 | x 8 |
| 2 | 2 | 96 | 480 x 592 x 16 | 240 x 296 x 48 | 120 x 148 x 96 | 120 x 148 x 24 | x 4 |
| 3 | 1 | 64 | 960 x 1184 x 16 | 480 x 592 x 32 | 240 x 296 x 64 | 240 x 296 x 24 | x 2 |

Equivalences the code relies on (each checked):

1. `F.interpolate(scale_factor=1/s, bilinear, align_corners False)` with an integer s reads the point s x + (s-1)/2: the pixel itself for s = 1,
   else the midpoint of 2 x 2 pixels, their mean. So the warp is computed only at the pixels the downscale reads (`kPack`), never stored.
2. `grid_sample(bilinear, padding border, align_corners True)` with the grid + flow / ((size - 1) / 2) = sample at pixel + flow, the coordinate
   clamped into the image.
3. `ConvTranspose2d(c, 5, kernel 4, stride 2, padding 1)`: output pixel (2m + py, 2q + px) reads input rows m - 1 + dr for dr in {py, py + 1}
   through kernel row ky = 3 + py - 2 dr (columns alike). So it is a 3x3 conv (padding 1) at the input's size with 4 x 5 outputs, output
   (py * 2 + px) * 5 + co, the unused taps zero; `kUpAcc` reads the parity's 5 values. Checked against `F.conv_transpose2d`: max difference
   3.8e-6 on values up to 20.
4. The bias is CUTLASS's C operand with stride 0 (CUTLASS example 17), `ScaleType::NoBetaScaling`: D = acc + bias, then LeakyReLU.

Precision: activations fp16 (as the model runs under fp16), sums fp32, the fields fp32 (the fp16 model keeps them in fp16; ours is closer to
the fp32 model, see Checks).

## Weight file, layout 2

128-byte header: `AFXRIFE1`, uint32 version 2, model 44, blocks 4, convblock 8, c[4], in[4], inPad[4], scale[4], uint64 payload size,
the source .pkl's SHA-256. The DLL compares every field with its constants and refuses anything else. Then per block, in order:

- conv0a: W fp16 [c/2][3][3][inPad] (PyTorch's [K][C][3][3] as KRSC, input channels 7 -> 8, 12 -> 16 with zeros), b fp16 [c/2]
- conv0b: W fp16 [c][3][3][c/2], b fp16 [c]
- convblock 0..7: W fp16 [c][3][3][c], b fp16 [c]
- lastconv as the 3x3 conv: W fp16 [24][3][3][c] (outputs 20..23 zero), b fp32 [24] (the 5 biases per parity)

The .pkl is a training checkpoint: `block_tea.*`, `contextnet.*` and `unet.*` are training-only (vs-rife's inference never loads them) and are
skipped by name; any other key left over stops the export (another architecture).

## Interface

`airframexr_rife.h`: `afx_rife_abi()` (1), `afx_rife_create(weights, n, w, h)` on the CUDA context current on the calling thread,
`afx_rife_run(img0, img1, t[n], flow, mask, stream)` (device pointers, enqueue only), `afx_rife_info`, `afx_rife_destroy`. Fixed buffers inside
(the packed input, two activation buffers, the last conv's output: 140 MB at 960 x 1184 x 2, plus the 10.5 MB of weights), so a run
allocates nothing.

The streamer (`neural.cpp`): retains the D3D11 adapter's primary CUDA context, loads the DLL by name and checks the interface number, writes
both frames at the network's size into D3D11 buffers registered with CUDA (a compute pass: the older frame turned into the newer camera),
maps them, runs, unmaps (map and unmap order the D3D11 and CUDA work), and composes the output from the fields in its own pass.

## Checks (2026-11-22, RTX 5090, driver 621.24)

Against PyTorch (`check_rife_cuda.py`: vs-rife's model code, same weights, judge frame pairs at 544 x 576 per eye, t = 0.5):

| case | flow, ours vs fp32 model (px: mean / p99 / max) | flow, fp16 model vs fp32 | composed picture PSNR, ours / fp16 model vs fp32 |
|---|---|---|---|
| 0 | 0.0004 / 0.004 / 0.031 | 0.0008 / 0.006 / 0.028 | 91.8 / 85.8 dB |
| 9 (walk 2 m/s) | 0.0005 / 0.004 / 0.034 | 0.0011 / 0.007 / 0.052 | 86.8 / 80.1 dB |
| 20 | 0.0004 / 0.003 / 0.024 | 0.0005 / 0.004 / 0.036 | 95.3 / 91.3 dB |
| 45 | 0.0003 / 0.007 / 0.075 | 0.0006 / 0.015 / 0.161 | 78.4 / 72.1 dB |

t = 1/3 alike (cases 9, 45: 81.9 / 77.4 and 79.2 / 78.0 dB). No NaN.

Time, both eyes: 0.72 ms at 544 x 576 per eye, 1.82 ms at 960 x 1184 (back-to-back runs, CUDA events); in the judge 0.73 ms (1032 x 1104 eyes)
and 1.77 ms (1920 x 2368 eyes); in the streamer path (`e2e_fg_neural`, 880 x 800, the probe loading the GPU) 1.24 ms, CPU 0.28 ms per run.

Frame generation judge (docs/FRAME_GENERATION_JUDGE.md), neural minus edge, boundary band PSNR: +0.91 dB (60 cases, 1032 x 1104; t = 1/3:
+0.89), +1.04 dB (36 cases, 1920 x 2368), within 0.002 dB of the earlier runtime. `e2e_fg_neural` (x2) and `e2e_fg_neural_13` (x3) pass (every generated
output composed from the fields, 0 failures, 90 fps held).

Played (the author, 5090 + Vision Pro, 2026-11-22): Hellraiser at x2 with this DLL, "works incredibly well"; at 1.5x the view wobbled (see
"Output time": the 1.5x rate was removed in 1.5.0). Not tested: the sm_86 machine code on an RTX 30 / 40 (built, never run), the PTX path, any PC
but the 5090.
The streamer's neural log lines are debug-level (debug mode only).

## Output time: why only x2 and x3 (2026-11-22, 1.5.0)

Field (the author, Hellraiser at 1.5x on 1.4.2): the view wobbled around its average; 2x and 3x were clean. The 1.5x rate and the uncapped mode
followed the game's real timing (a content clock), so each output asked the network for another time t anywhere in 0..0.98; 2x asks for 0.5,
3x for 1/3 and 2/3. RIFE 4.4 does not put the content at every asked time (`harness/rife/t_linearity.py`, 5 walking scenes; the PyTorch model
gives the same numbers, so it is the model, not this port):

| asked t | where the content lands | judge, neural minus edge (band PSNR, 60 cases) |
|---|---|---|
| 0.02-0.10 | wrong: behind frame 0 (-0.13 at 0.05) | -15.0 dB at 0.05 (0 of 60) |
| 0.15-0.45, 0.6-0.85, 0.5 | within 0.02 (1/3: 0.322, 0.5: 0.499, 2/3: 0.668) | +0.89 at 1/3, +0.91 at 0.5, +0.71 at 2/3 |
| 0.5-0.6 | stuck near 0.5 (0.55: 0.519) | +0.54 at 0.55 |
| 0.9-0.98 | beyond frame 1 | -1.7 dB at 0.95 |

In that 1.5x session 31.8 % of the outputs asked for one of the weak zones. A 1.4.2 hotfix (never released) handed the outputs near a rendered
frame to the edge synthesis and ran 0.5-0.6 with the pair reversed (0.55 -> 0.547): it stopped the wobble, but in the headset 1.5x was then no
better than the optical-flow generation, and x2 looked better. 1.5.0 keeps the fixed rates only (x2: t = 1/2, x3: t = 1/3 and 2/3, all inside
the range where the network is right) and removed the content clock, the hotfix and the 1.5x / uncapped modes. OFXR-Bridge's RIFE backend made
the same choice: whole multipliers, RIFE sampled at fixed fractions whatever the timing jitter (their V741 / V795). `t_linearity.py` stays as
the regression check of the network's output time.

## Tiles and what was measured

At 960 x 1184 x 2 (1.82 ms): the 3x3 convs about 1.35 ms (block 3's at about 190 TFLOPS, near the 5090's fp16 with fp32 sums), `kPack` 0.22 ms,
`kUpAcc` 0.13 ms, gaps between the 52 launches about 0.1 ms.

- One CUTLASS tile for the 3x3 convs: threadblock 64 x 64 x 32, warps 32 x 32 x 32, 4 stages, `kOptimized` iterator.
- Block 3's first conv (16 input channels at the full size): the "few channels" iterator, tile 128 x 32: 150 -> 105 us. On blocks 1 and 2 it
  was slower (block 2: 27 -> 50 us), so block 3 only.
- A 64 x 32 tile for block 2's 96-channel convs: slower (44 -> 50 us each). Not kept.
- fp16 sums: 1.826 -> 1.751 ms (4 %), the error twice as large (the fp16 model's). Not kept.
- The transposed conv as our own kernel (one thread per input pixel): 372 us of 961 at 544 x 576 (block 0 has 612 such pixels). As the
  3x3 conv: 1.06 -> 0.73 ms.

Not done: a CUDA graph (the launch gaps); fusing block 3's packing into its first conv (the 16-channel full-size input is written and read once,
72 MB at 960 x 1184).

## Updating

**Another CUDA toolkit.** Install its `nvcc crt nvvm cudart cccl` components, run `build.ps1 -Cuda <dir> -AnyVersion`, then
`check_rife_cuda.py` (the table above: ours vs fp32 must stay below the fp16 model's) and the judge. When they pass, change `$CudaVersion` in
`build.ps1` and the pin here. Watch: compiler flags CCCL demands, deprecated GPU targets (13.x dropped everything before sm_75).

**Another CUTLASS.** Change the version, URL and SHA-256 in `build.ps1`, build, check. The 2.x convolution API used here
(`conv::device::ImplicitGemmConvolution`, `conv::kernel::DefaultConv2dFprop`, `LinearCombinationLeakyRelu`) has been stable across 3.x and 4.x.

**A new GPU generation.** The compute_86 PTX runs on it (compiled by the driver at first use). For machine code add a `-gencode` line in
`build.ps1` (a new major version: `arch=compute_XY,code=sm_XY`) and time it.

**Another RIFE.** Diff the new `IFNet_HDv3_v4_X.py` against 4.4's, then change in step: (1) the constants at the top of `airframexr_rife.cu`
(`kC`, `kIn`, `kInPad`, `kScale`, `kConvBlock`, `kLast`); (2) `export_weights.py` (key names, shapes, the training-only prefixes, the pinned
SHA-256), and the layout number in both (the DLL refuses a file of another layout); (3) `kPack`'s channel list if the block inputs change;
(4) new layer kinds need code: a residual in the convblock (an epilogue with a second source), an image encoder ahead of the blocks (later
versions have one: more input channels, another conv chain), a different last layer (e.g. a deconv to more channels + PixelShuffle: the parity
rewrite still applies, with more outputs); (5) the interface number in `airframexr_rife.h` if the inputs or outputs change. Then the same
checks: `check_rife_cuda.py` against that version's PyTorch code (the `fields` function there follows the model's forward), the judge, the
streamer scenarios.

## Why not TensorRT

The first backend (2026-11-22, same branch) ran an ONNX export of the same model under NVIDIA TensorRT for RTX 1.8.1: 198 MB of DLL + a 2.2 MB
parser, a GPU-agnostic engine built at start. The standard TensorRT lean runtime is 36 MB plus an engine per GPU and size. This DLL and its
weights are 11 MB and build in 10 s from pinned sources. Timings of TensorRT for RTX are not given here (its licence forbids publishing
benchmark results); the judge's picture quality was the same to 0.002 dB.
