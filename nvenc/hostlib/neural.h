// Neural frame generation fields (fg-neural branch, lab): RIFE 4.4's motion and blend fields for a pair of rendered frames, computed by our own
// CUDA network (nvenc/rife, docs/NEURAL_RIFE_CUDA.md) at a fraction of the eye resolution. The streamer's own output pass composes the picture at
// full resolution from these fields (framegen.cpp neuralSynth): one sample of each original frame.
//
// Loaded at run time from one folder (default: the host's `neural` folder): airframexr_rife.dll (nvenc/rife/build.ps1; CUTLASS compiled in,
// BSD-3-Clause) and rife44.afxw (RIFE 4.4's weights, MIT, nvenc/rife/export_weights.py); CUDA through the driver's nvcuda.dll. Without them the
// streamer works as before (the edge synthesis).
//
// Data path per pair: a D3D11 compute pass writes both frames at the network's resolution (the older one turned into the newer one's camera, as
// the optical flow sees them) into buffers CUDA maps; the engine writes the fields into buffers the output pass reads. Map / unmap order the D3D11
// and CUDA work on the GPU.
#pragma once
#include <d3d11.h>
#include <cstdint>

struct NeuralStats {
    uint64_t runs = 0, failures = 0;
    float lastMs = 0, lastCpuMs = 0;     // GPU time of the last network run (CUDA events), CPU time of the last Run call
    double msSum = 0, cpuMsSum = 0; uint64_t msN = 0;
    char info[200] = "";                 // what is running, or why not
};

class NeuralFields {
public:
    NeuralFields(ID3D11Device* dev, ID3D11DeviceContext* ctx);
    ~NeuralFields();
    // eyeW x eyeH: one eye of the side-by-side stream frame; scale: the network's resolution as a share of it (0.5 = half). dir: the folder with the
    // DLLs and the model (UTF-16). false + err: not available (the caller keeps the edge synthesis).
    bool Init(int eyeW, int eyeH, float scale, const wchar_t* dir, char* err, int errlen);
    // The fields of the pair at time t (0 = prev, 1 = cur). prev / cur: the side-by-side frames; rot: per eye three rows (x y z, w unused) turning a
    // direction of cur's view into prev's view; tan8: per eye l r u d tangents.
    bool Run(ID3D11ShaderResourceView* prev, ID3D11ShaderResourceView* cur, const float rot[2][12], const float* tan8, float t, char* err, int errlen);
    // flow: per eye 4 planes (x y to prev, x y to cur, network pixels), mask: per eye 1 plane (logit of prev's weight); planes PadW x PadH floats
    ID3D11ShaderResourceView* FlowSrv() const;
    ID3D11ShaderResourceView* MaskSrv() const;
    void CollectTiming();   // the last run's GPU time, when it has finished (no wait)
    int NetW = 0, NetH = 0, PadW = 0, PadH = 0;
    NeuralStats stats;

private:
    struct Impl;
    Impl* m = nullptr;
    ID3D11Device* m_dev = nullptr;
    ID3D11DeviceContext* m_ctx = nullptr;
};
