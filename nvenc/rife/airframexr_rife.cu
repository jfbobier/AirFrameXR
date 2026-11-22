// airframexr_rife.dll (airframexr_rife.h): RIFE 4.4's fields as a fixed chain of our own CUDA kernels plus CUTLASS's implicit-GEMM convolution
// (BSD-3-Clause, header-only, compiled in). The recipe (what each step reproduces from the model code, the weight layout, the build, the
// checks): docs/NEURAL_RIFE_CUDA.md. The model code this follows: vs-rife's IFNet_HDv3_v4_4.py (MIT); RIFE by hzwer (Practical-RIFE, MIT).
//
// Per block b (scale s = 8, 4, 2, 1; c = 192, 128, 96, 64 channels):
//   kPack       the block's input at 1/s of the frame, NHWC halves (block 0: img0 img1 t; later: both frames warped by the fields so far, t,
//               the mask so far, the flow so far / s): PyTorch's bilinear downscale reads one pixel (s = 1) or the mean of 2x2 (s > 1), so the
//               warp is computed only at those points
//   10 convs    3x3 conv + bias + LeakyReLU(0.2): two with stride 2 (to 1/4s), eight with stride 1 (CUTLASS, fp16 in and out, fp32 sums)
//   last conv   ConvTranspose2d(c, 5, 4, 2, 1) to 1/2s, as an ordinary 3x3 conv at 1/4s with 4 x 5 outputs, one set of 5 per output-pixel parity
//               (each parity uses 2x2 of the 3x3 taps, the export puts zeros in the others): the 2x2 outputs around each input pixel (fp32 out)
//   kUpAcc      bilinear upscale by 2s to the frame, flow x 2s, written (block 0) or added to the fields (fp32, the caller's buffers)
#define AFX_RIFE_BUILD
#include "airframexr_rife.h"

#include <cuda_fp16.h>
#include <cuda_runtime.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <memory>
#include <vector>

// (this order: the device header uses helpers the kernel header brings)
#include "cutlass/cutlass.h"
#include "cutlass/conv/kernel/default_conv2d_fprop.h"
#include "cutlass/conv/device/implicit_gemm_convolution.h"
#include "cutlass/epilogue/thread/linear_combination.h"
#include "cutlass/epilogue/thread/linear_combination_leaky_relu.h"
#include "cutlass/version.h"

namespace {

// ---- the network, hard-coded (RIFE 4.4: IFNet(scale 1, ensemble off)) ----
constexpr int kBlocks = 4, kConvBlock = 8;
constexpr int kC[kBlocks] = {192, 128, 96, 64};   // feature channels
constexpr int kIn[kBlocks] = {7, 12, 12, 12};     // input channels
constexpr int kInPad[kBlocks] = {8, 16, 16, 16};  // padded with zero weights to the tensor cores' access width (8 halves)
constexpr int kScale[kBlocks] = {8, 4, 2, 1};
constexpr float kLeaky = 0.2f;
constexpr int kLast = 24;   // the last conv's outputs: 4 parities x 5 (flow 4, mask 1), padded to a multiple of 8

// the weight file (export_weights.py, layout 2): this header, then per block conv0a W b, conv0b W b, 8 x (W b), last W b. Conv W: halves
// [K][3][3][C] (KRSC, C padded), b: halves [K]; last W: halves [24][3][3][c] (output (py * 2 + px) * 5 + co, 20..23 zero), b: floats [24].
struct FileHeader {
    char magic[8];  // "AFXRIFE1"
    uint32_t version, model, blocks, convblock;
    uint32_t c[4], in[4], inPad[4], scale[4];
    uint64_t payload;
    uint8_t sourceSha256[32];
};
static_assert(sizeof(FileHeader) == 128, "the weight file's header is 128 bytes");

size_t convW(int k, int c) { return (size_t)k * 9 * c * 2; }
size_t blockBytes(int b) {
    const int c = kC[b];
    return convW(c / 2, kInPad[b]) + c + convW(c, c / 2) + 2 * c + kConvBlock * (convW(c, c) + 2 * c) + convW(kLast, c) + kLast * 4;
}

void seterr(char* err, int errlen, const char* fmt, ...) {
    if (!err || errlen <= 0) return;
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(err, errlen, fmt, ap);
    va_end(ap);
}

// ---- our kernels ----
struct TArr { float v[4]; };

__device__ __forceinline__ float clamp01(float v) { return fminf(fmaxf(v, 0.f), 1.f); }

// grid_sample(mode bilinear, padding border, align_corners True) of one plane at pixel coordinate (x, y); the model clamps the frames to 0..1
__device__ __forceinline__ float sampleBorder(const float* __restrict__ p, int W, int H, float x, float y) {
    x = fminf(fmaxf(x, 0.f), (float)(W - 1));
    y = fminf(fmaxf(y, 0.f), (float)(H - 1));
    const int x0 = (int)x, y0 = (int)y, x1 = min(x0 + 1, W - 1), y1 = min(y0 + 1, H - 1);
    const float fx = x - (float)x0, fy = y - (float)y0;
    const float a = clamp01(__ldg(p + (size_t)y0 * W + x0)), b = clamp01(__ldg(p + (size_t)y0 * W + x1));
    const float c = clamp01(__ldg(p + (size_t)y1 * W + x0)), d = clamp01(__ldg(p + (size_t)y1 * W + x1));
    return (1.f - fy) * ((1.f - fx) * a + fx * b) + fy * ((1.f - fx) * c + fx * d);
}

// The block's input: F.interpolate(x, scale_factor=1/s, bilinear, align_corners False) samples at s*x + (s-1)/2: the pixel for s = 1, else the
// midpoint of pixels s*x + s/2 - 1 and s*x + s/2 (both axes), the mean of 2x2. Block 0 (kFirst): img0 img1 t 0. Later: warp(img0, flow.xy),
// warp(img1, flow.zw), t, mask, flow / s, 0 0 0 0.
template <bool kFirst>
__global__ void kPack(const float* __restrict__ img0, const float* __restrict__ img1, const float* __restrict__ flow, const float* __restrict__ mask,
                      TArr t, __half* __restrict__ out, int W, int H, int s, int w, int h) {
    const int x = blockIdx.x * blockDim.x + threadIdx.x, y = blockIdx.y * blockDim.y + threadIdx.y, n = blockIdx.z;
    if (x >= w || y >= h) return;
    constexpr int C = kFirst ? 8 : 16;
    const size_t pl = (size_t)W * H;
    const float* a = img0 + (size_t)n * 3 * pl;
    const float* b = img1 + (size_t)n * 3 * pl;
    const int taps = s == 1 ? 1 : 2;
    const int x0 = s == 1 ? x : s * x + s / 2 - 1, y0 = s == 1 ? y : s * y + s / 2 - 1;
    float v[12] = {};
    for (int dy = 0; dy < taps; dy++)
        for (int dx = 0; dx < taps; dx++) {
            const int px = x0 + dx, py = y0 + dy;
            const size_t o = (size_t)py * W + px;
            if (kFirst) {
#pragma unroll
                for (int c = 0; c < 3; c++) {
                    v[c] += clamp01(__ldg(a + c * pl + o));
                    v[3 + c] += clamp01(__ldg(b + c * pl + o));
                }
            } else {
                const float* f = flow + (size_t)n * 4 * pl + o;
                const float f0 = __ldg(f), f1 = __ldg(f + pl), f2 = __ldg(f + 2 * pl), f3 = __ldg(f + 3 * pl);
#pragma unroll
                for (int c = 0; c < 3; c++) {
                    v[c] += sampleBorder(a + c * pl, W, H, (float)px + f0, (float)py + f1);
                    v[3 + c] += sampleBorder(b + c * pl, W, H, (float)px + f2, (float)py + f3);
                }
                v[7] += __ldg(mask + (size_t)n * pl + o);
                v[8] += f0; v[9] += f1; v[10] += f2; v[11] += f3;
            }
        }
    const float k = 1.f / (float)(taps * taps);
    __align__(16) __half o[C];
#pragma unroll
    for (int c = 0; c < C; c++) {
        float q = 0.f;
        if (c < 6) q = v[c] * k;
        else if (c == 6) q = t.v[n];
        else if (!kFirst && c == 7) q = v[7] * k;
        else if (!kFirst && c < 12) q = v[c] * k / (float)s;
        o[c] = __float2half_rn(q);
    }
    uint4* dst = reinterpret_cast<uint4*>(out + ((size_t)(n * h + y) * w + x) * C);
    dst[0] = reinterpret_cast<const uint4*>(o)[0];
    if (!kFirst) dst[1] = reinterpret_cast<const uint4*>(o)[1];
}

// The last conv's output (h4 x w4 x 24 floats) is ConvTranspose2d's h2 x w2 x 5 picture with each 2x2 output block stored at its input pixel:
// output (y, x) = tmp[y / 2][x / 2][((y & 1) * 2 + (x & 1)) * 5 + co]. F.interpolate(that, scale_factor=2s, bilinear, align_corners False) at
// the frame's pixels: source (X + 0.5) / 2s - 0.5, clamped at 0; flow x 2s; written for block 0, added after (fp32 here; the model's are fp16).
__device__ __forceinline__ const float* lastAt(const float* tb, int w4, int y, int x) {
    return tb + ((size_t)(y >> 1) * w4 + (x >> 1)) * kLast + ((y & 1) * 2 + (x & 1)) * 5;
}
__global__ void kUpAcc(const float* __restrict__ tmp, float* __restrict__ flow, float* __restrict__ mask, int W, int H, int w2, int h2, float inv,
                       float fscale, int add) {
    const int X = blockIdx.x * blockDim.x + threadIdx.x, Y = blockIdx.y * blockDim.y + threadIdx.y, n = blockIdx.z;
    if (X >= W || Y >= H) return;
    const float sx = fmaxf(((float)X + 0.5f) * inv - 0.5f, 0.f), sy = fmaxf(((float)Y + 0.5f) * inv - 0.5f, 0.f);
    const int x0 = (int)sx, y0 = (int)sy, x1 = x0 + (x0 < w2 - 1 ? 1 : 0), y1 = y0 + (y0 < h2 - 1 ? 1 : 0);
    const float lx = sx - (float)x0, ly = sy - (float)y0;
    const int w4 = w2 / 2;
    const float* tb = tmp + (size_t)n * (h2 / 2) * w4 * kLast;
    const float* p00 = lastAt(tb, w4, y0, x0);
    const float* p01 = lastAt(tb, w4, y0, x1);
    const float* p10 = lastAt(tb, w4, y1, x0);
    const float* p11 = lastAt(tb, w4, y1, x1);
    const size_t pl = (size_t)W * H, o = (size_t)Y * W + X;
#pragma unroll
    for (int c = 0; c < 5; c++) {
        const float v = (1.f - ly) * ((1.f - lx) * __ldg(p00 + c) + lx * __ldg(p01 + c)) + ly * ((1.f - lx) * __ldg(p10 + c) + lx * __ldg(p11 + c));
        float* d = c < 4 ? flow + ((size_t)n * 4 + c) * pl + o : mask + (size_t)n * pl + o;
        const float q = c < 4 ? v * fscale : v;
        *d = add ? *d + q : q;
    }
}

// ---- the convolutions: CUTLASS implicit GEMM, NHWC fp16 in, fp32 sums; bias = the C operand broadcast (stride 0), epilogue D = acc + bias,
// then LeakyReLU(0.2) and fp16 out (the 10 convs) or fp32 out as is (the last conv) ----
using Elem = cutlass::half_t;
using Layout = cutlass::layout::TensorNHWC;
using Acc = float;   // the convs' sums (fp16 sums: 4 % faster at 960x1184, twice the error; docs/NEURAL_RIFE_CUDA.md "Tiles")
using EpiLeaky = cutlass::epilogue::thread::LinearCombinationLeakyRelu<Elem, 8, Acc, float, cutlass::epilogue::thread::ScaleType::NoBetaScaling>;
using EpiLinear = cutlass::epilogue::thread::LinearCombination<float, 4, float, float, cutlass::epilogue::thread::ScaleType::NoBetaScaling>;
using cutlass::gemm::GemmShape;
using cutlass::conv::IteratorAlgorithm;
using cutlass::conv::StrideSupport;

template <class Out, class A, class Epi, class TB, class WarpS, int Stages, IteratorAlgorithm Algo, StrideSupport SS>
using ConvOp = cutlass::conv::device::ImplicitGemmConvolution<typename cutlass::conv::kernel::DefaultConv2dFprop<
    Elem, Layout, Elem, Layout, Out, Layout, A, cutlass::arch::OpClassTensorOp, cutlass::arch::Sm80, TB, WarpS, GemmShape<16, 8, 16>, Epi,
    cutlass::gemm::threadblock::GemmIdentityThreadblockSwizzle<>, Stages, cutlass::arch::OpMultiplyAdd, Algo, SS, 8, 8>::Kernel>;

// tiles (docs/NEURAL_RIFE_CUDA.md "Tiles": what was measured). ConvTile: every 3x3 conv but one; ConvFirst: block 3's first conv (16 input
// channels at the full network size; the "few channels" iterator packs several filter taps into one K step instead of padding each tap's 16
// channels to the tile's 32: 150 -> 105 us at 960x1184, slower on the other blocks); ConvLast: the transposed conv's rewrite (24 fp32 outputs)
using ConvFirst = ConvOp<Elem, Acc, EpiLeaky, GemmShape<128, 32, 32>, GemmShape<64, 32, 32>, 4, IteratorAlgorithm::kFewChannels, StrideSupport::kStrided>;
using ConvTile = ConvOp<Elem, Acc, EpiLeaky, GemmShape<64, 64, 32>, GemmShape<32, 32, 32>, 4, IteratorAlgorithm::kOptimized, StrideSupport::kStrided>;
using ConvLast = ConvOp<float, float, EpiLinear, GemmShape<64, 32, 32>, GemmShape<32, 32, 32>, 4, IteratorAlgorithm::kOptimized, StrideSupport::kUnity>;

struct Conv {
    virtual ~Conv() {}
    virtual cutlass::Status run(cudaStream_t s) = 0;
};

template <class Op>
struct ConvT : Conv {
    using Out = typename Op::ElementC;
    Op op;
    cutlass::Status init(const cutlass::conv::Conv2dProblemSize& p, Elem* x, const Elem* w, const Out* bias, Out* y,
                         typename Op::EpilogueOutputOp::Params ep) {
        typename Op::Arguments args(p, {x, Layout::packed(cutlass::Tensor4DCoord(p.N, p.H, p.W, p.C))},
                                    {const_cast<Elem*>(w), Layout::packed(cutlass::Tensor4DCoord(p.K, p.R, p.S, p.C))},
                                    {const_cast<Out*>(bias), Layout::Stride(0)}, {y, Layout::packed(cutlass::Tensor4DCoord(p.N, p.P, p.Q, p.K))}, ep);
        cutlass::Status st = Op::can_implement(args);
        if (st != cutlass::Status::kSuccess) return st;
        return op.initialize(args, nullptr);
    }
    cutlass::Status run(cudaStream_t s) override { return op.run(s); }
};

}  // namespace

struct AfxRife {
    int n = 0, W = 0, H = 0;
    char* weights = nullptr;  // device copy of the payload
    __half *x = nullptr, *a = nullptr, *b = nullptr;
    float* tmp = nullptr;
    struct Block {
        std::vector<std::unique_ptr<Conv>> convs;   // the 10 convs, then the last
    } blk[kBlocks];
    char info[200] = "";
};

extern "C" {

AFX_RIFE_API int afx_rife_abi(void) { return AFX_RIFE_ABI; }

AFX_RIFE_API void afx_rife_destroy(AfxRife* r) {
    if (!r) return;
    for (auto& bl : r->blk) bl.convs.clear();
    cudaFree(r->weights);
    cudaFree(r->x);
    cudaFree(r->a);
    cudaFree(r->b);
    cudaFree(r->tmp);
    delete r;
}

AFX_RIFE_API AfxRife* afx_rife_create(const wchar_t* path, int n, int w, int h, char* err, int errlen) {
    if (n < 1 || n > 4 || w < 32 || h < 32 || w % 32 || h % 32) {
        seterr(err, errlen, "afx_rife: size %d x %dx%d (1..4 images, multiples of 32)", n, w, h);
        return nullptr;
    }
    int dev = 0, major = 0, minor = 0;
    cudaError_t e = cudaGetDevice(&dev);
    if (!e) e = cudaDeviceGetAttribute(&major, cudaDevAttrComputeCapabilityMajor, dev);
    if (!e) e = cudaDeviceGetAttribute(&minor, cudaDevAttrComputeCapabilityMinor, dev);
    if (e) {
        seterr(err, errlen, "afx_rife: no CUDA device (%s)", cudaGetErrorString(e));
        return nullptr;
    }
    if (major < 8) {
        seterr(err, errlen, "afx_rife: compute capability %d.%d, needs 8.0 or later (RTX 30 series or newer)", major, minor);
        return nullptr;
    }

    // the weights
    FILE* f = _wfopen(path, L"rb");
    if (!f) {
        seterr(err, errlen, "afx_rife: cannot open the weight file");
        return nullptr;
    }
    FileHeader hd{};
    size_t expect = 0;
    for (int b = 0; b < kBlocks; b++) expect += blockBytes(b);
    bool okh = fread(&hd, sizeof hd, 1, f) == 1 && !memcmp(hd.magic, "AFXRIFE1", 8) && hd.version == 2 && hd.model == 44 && hd.blocks == kBlocks &&
               hd.convblock == kConvBlock && hd.payload == expect;
    for (int b = 0; okh && b < kBlocks; b++)
        okh = hd.c[b] == (uint32_t)kC[b] && hd.in[b] == (uint32_t)kIn[b] && hd.inPad[b] == (uint32_t)kInPad[b] && hd.scale[b] == (uint32_t)kScale[b];
    if (!okh) {
        fclose(f);
        seterr(err, errlen, "afx_rife: the weight file is not RIFE 4.4 in layout 2 (re-export with nvenc/rife/export_weights.py)");
        return nullptr;
    }
    std::vector<char> host(expect);
    const bool okr = fread(host.data(), 1, expect, f) == expect && fgetc(f) == EOF;
    fclose(f);
    if (!okr) {
        seterr(err, errlen, "afx_rife: the weight file is truncated or too long");
        return nullptr;
    }

    std::unique_ptr<AfxRife, void (*)(AfxRife*)> r(new AfxRife, afx_rife_destroy);
    r->n = n; r->W = w; r->H = h;
    size_t xs = 0, as = 0, bs = 0, ts = 0;  // elements
    for (int b = 0; b < kBlocks; b++) {
        const size_t s = kScale[b], c = kC[b], p0 = (size_t)n * (h / s) * (w / s);
        xs = std::max(xs, p0 * kInPad[b]);
        as = std::max(as, std::max(p0 / 4 * (c / 2), p0 / 16 * c));
        bs = std::max(bs, p0 / 16 * c);
        ts = std::max(ts, p0 / 16 * kLast);
    }
    e = cudaMalloc(&r->weights, expect);
    if (!e) e = cudaMemcpy(r->weights, host.data(), expect, cudaMemcpyHostToDevice);
    if (!e) e = cudaMalloc(&r->x, xs * 2);
    if (!e) e = cudaMalloc(&r->a, as * 2);
    if (!e) e = cudaMalloc(&r->b, bs * 2);
    if (!e) e = cudaMalloc(&r->tmp, ts * 4);
    if (e) {
        seterr(err, errlen, "afx_rife: GPU memory (%s)", cudaGetErrorString(e));
        return nullptr;
    }

    // the layers, on fixed buffers: X -> conv0a -> A -> conv0b -> B -> 8 convs alternating B -> A -> B ... (ends in B) -> last conv -> tmp
    char* p = r->weights;
    auto take = [&p](size_t bytes) { char* q = p; p += bytes; return q; };
    for (int b = 0; b < kBlocks; b++) {
        const int s = kScale[b], c = kC[b], hs = h / s, ws = w / s;
        struct L { int H, W, C, K, stride; Elem *in, *out; } ls[2 + kConvBlock];
        ls[0] = {hs, ws, kInPad[b], c / 2, 2, (Elem*)r->x, (Elem*)r->a};
        ls[1] = {hs / 2, ws / 2, c / 2, c, 2, (Elem*)r->a, (Elem*)r->b};
        for (int k = 0; k < kConvBlock; k++)
            ls[2 + k] = {hs / 4, ws / 4, c, c, 1, (Elem*)(k % 2 ? r->a : r->b), (Elem*)(k % 2 ? r->b : r->a)};
        for (const L& l : ls) {
            const Elem* wgt = (const Elem*)take(convW(l.K, l.C));
            const Elem* bias = (const Elem*)take((size_t)l.K * 2);
            const int P = l.H / l.stride, Q = l.W / l.stride;
            cutlass::conv::Conv2dProblemSize pb(n, l.H, l.W, l.C, l.K, 3, 3, P, Q, 1, 1, l.stride, l.stride, 1, 1,
                                                cutlass::conv::Mode::kCrossCorrelation);
            std::unique_ptr<Conv> conv;
            cutlass::Status st;
            if (&l == ls && b == 3) {
                auto c0 = std::make_unique<ConvT<ConvFirst>>();
                st = c0->init(pb, l.in, wgt, bias, l.out, {1.f, 1.f, kLeaky});
                conv = std::move(c0);
            } else {
                auto c1 = std::make_unique<ConvT<ConvTile>>();
                st = c1->init(pb, l.in, wgt, bias, l.out, {1.f, 1.f, kLeaky});
                conv = std::move(c1);
            }
            if (st != cutlass::Status::kSuccess) {
                seterr(err, errlen, "afx_rife: block %d conv %dx%dx%d -> %d stride %d: %s", b, l.W, l.H, l.C, l.K, l.stride, cutlassGetStatusString(st));
                return nullptr;
            }
            r->blk[b].convs.push_back(std::move(conv));
        }
        {   // the last conv: B -> tmp (fp32)
            const Elem* wgt = (const Elem*)take(convW(kLast, c));
            const float* bias = (const float*)take((size_t)kLast * 4);
            cutlass::conv::Conv2dProblemSize pb(n, hs / 4, ws / 4, c, kLast, 3, 3, hs / 4, ws / 4, 1, 1, 1, 1, 1, 1, cutlass::conv::Mode::kCrossCorrelation);
            auto conv = std::make_unique<ConvT<ConvLast>>();
            const cutlass::Status st = conv->init(pb, (Elem*)r->b, wgt, bias, r->tmp, {1.f, 1.f});
            if (st != cutlass::Status::kSuccess) {
                seterr(err, errlen, "afx_rife: block %d last conv: %s", b, cutlassGetStatusString(st));
                return nullptr;
            }
            r->blk[b].convs.push_back(std::move(conv));
        }
    }
    cudaDeviceProp pr{};
    cudaGetDeviceProperties(&pr, dev);
    snprintf(r->info, sizeof r->info, "RIFE 4.4 (own CUDA + CUTLASS %d.%d.%d), %d x %dx%d, %s sm_%d%d", CUTLASS_MAJOR, CUTLASS_MINOR, CUTLASS_PATCH, n, w, h,
             pr.name, major, minor);
    return r.release();
}

AFX_RIFE_API const char* afx_rife_info(AfxRife* r) { return r ? r->info : ""; }

AFX_RIFE_API int afx_rife_run(AfxRife* r, uint64_t img0, uint64_t img1, const float* t, uint64_t flow, uint64_t mask, void* stream, char* err,
                              int errlen) {
    if (!r || !img0 || !img1 || !t || !flow || !mask) {
        seterr(err, errlen, "afx_rife_run: missing argument");
        return 1;
    }
    cudaStream_t st = (cudaStream_t)stream;
    TArr ta{};
    for (int k = 0; k < r->n; k++) ta.v[k] = t[k];
    const float *i0 = (const float*)img0, *i1 = (const float*)img1;
    float *fl = (float*)flow, *mk = (float*)mask;
    for (int b = 0; b < kBlocks; b++) {
        const int s = kScale[b], ws = r->W / s, hs = r->H / s, w4 = ws / 4, h4 = hs / 4;
        const dim3 bp(16, 8), gp((ws + 15) / 16, (hs + 7) / 8, r->n);
        if (b == 0) kPack<true><<<gp, bp, 0, st>>>(i0, i1, fl, mk, ta, r->x, r->W, r->H, s, ws, hs);
        else kPack<false><<<gp, bp, 0, st>>>(i0, i1, fl, mk, ta, r->x, r->W, r->H, s, ws, hs);
        for (auto& c : r->blk[b].convs) {
            const cutlass::Status cs = c->run(st);
            if (cs != cutlass::Status::kSuccess) {
                seterr(err, errlen, "afx_rife_run: block %d conv: %s (%s)", b, cutlassGetStatusString(cs), cudaGetErrorString(cudaGetLastError()));
                return 1;
            }
        }
        kUpAcc<<<dim3((r->W + 31) / 32, (r->H + 7) / 8, r->n), dim3(32, 8), 0, st>>>(r->tmp, fl, mk, r->W, r->H, w4 * 2, h4 * 2, 1.f / (2.f * s), 2.f * s,
                                                                                     b > 0);
    }
    const cudaError_t e = cudaGetLastError();
    if (e) {
        seterr(err, errlen, "afx_rife_run: %s", cudaGetErrorString(e));
        return 1;
    }
    return 0;
}

}  // extern "C"
