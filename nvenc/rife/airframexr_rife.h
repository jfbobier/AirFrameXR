// airframexr_rife.dll: RIFE 4.4's motion and blend fields (fg-neural branch, lab), our own CUDA (docs/NEURAL_RIFE_CUDA.md, the recipe).
// The network is hard-coded (4 blocks, the 4.4 channel counts); its weights come from one file that nvenc/rife/export_weights.py writes from
// the published flownet_v4.4.pkl. The streamer loads the DLL at run time (nvenc/hostlib/neural.cpp) and drives it with CUDA device pointers
// on the CUDA context and stream it owns; nothing here touches D3D11.
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef AFX_RIFE_BUILD
#define AFX_RIFE_API __declspec(dllexport)
#else
#define AFX_RIFE_API __declspec(dllimport)
#endif

// The interface version; the streamer refuses a DLL that reports another one.
#define AFX_RIFE_ABI 1

typedef struct AfxRife AfxRife;

AFX_RIFE_API int afx_rife_abi(void);

// Creates the network on the CUDA context current on the calling thread: n images (the eyes) of w x h (multiples of 32), weights from `weights`
// (the export's .afxw file, UTF-16 path). nullptr + err on failure.
AFX_RIFE_API AfxRife* afx_rife_create(const wchar_t* weights, int n, int w, int h, char* err, int errlen);

// Enqueues one run on `stream` (a CUstream / cudaStream_t of the same context). img0, img1: float [n][3][h][w], values in 0..1 (clamped);
// t: n host values (0 = img0, 1 = img1); flow: float [n][4][h][w] written (x y displacement from the output time to img0, then to img1, in
// pixels); mask: float [n][1][h][w] written (the logit of img0's weight). 0 = enqueued, else err.
AFX_RIFE_API int afx_rife_run(AfxRife* r, uint64_t img0, uint64_t img1, const float* t, uint64_t flow, uint64_t mask, void* stream, char* err,
                              int errlen);

// What runs: "RIFE 4.4, 2 x 544x576, sm_120, ..." (for the streamer's log).
AFX_RIFE_API const char* afx_rife_info(AfxRife* r);

AFX_RIFE_API void afx_rife_destroy(AfxRife* r);

#ifdef __cplusplus
}
#endif
