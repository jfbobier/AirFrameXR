// Stand-in for the CUDA runtime header that TensorRT-RTX's NvInferRuntimeBase.h includes: the handle types its API uses, with the struct tags of
// the CUDA headers (ABI-identical). The library itself needs no CUDA runtime DLL (it depends on Windows system DLLs only); the host reaches CUDA through
// the driver's nvcuda.dll (nvenc/hostlib/neural.cpp). No CUDA toolkit is needed to build.
#pragma once
typedef struct CUstream_st* cudaStream_t;
typedef struct CUevent_st* cudaEvent_t;
typedef unsigned long long CUmemGenericAllocationHandle;
