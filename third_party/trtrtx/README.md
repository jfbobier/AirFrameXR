# TensorRT for RTX headers

`include/`: the C++ headers of NVIDIA TensorRT for RTX 1.8.1.40 (SDK `TensorRT-RTX-1.8.1.40-Windows-amd64-cuda-13.5-Release-external.zip`),
Apache-2.0 (the notice is in each header). Used by `nvenc/hostlib/neural.cpp` (the neural frame generation lab backend), which loads
`tensorrt_rtx_1_8.dll` and `tensorrt_onnxparser_rtx_1_8.dll` at run time from the install's `neural` folder; those DLLs are under NVIDIA's
TensorRT-RTX license agreement (https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html) and are not in this repository.
`cudastub/cuda_runtime_api.h`: our two-typedef stand-in for the CUDA runtime header the TensorRT headers include (no CUDA toolkit needed).
