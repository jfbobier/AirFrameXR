# Lab build of the real OFXR-Bridge layer (djules75 V551, 382065f) for the e2e_ofxr_tokyo_* rows, into C:\_ofxr on the builder (lab only; nothing
# registered system-wide, the scenarios load it through XR_API_LAYER_PATH + XR_ENABLE_API_LAYERS). Recipe = the layer's own CI (.github/workflows/build.yml):
# FidelityFX SDK v1.1.4 (Optical Flow + DX12, static), DLSS headers v310.9.1, openvr.h v2.5.1, each checked against its pin; safetyhook comes by FetchContent.
# From WSL:  git -C external/ofxr/OFXR-Bridge-djules75 archive --format=zip -o /tmp/ofxr-382065f.zip 382065f
#            scp /tmp/ofxr-382065f.zip tools/remote/ofxr_lab_build.ps1 <builder>:C:/_ofxr/ ; ssh <builder> powershell -ExecutionPolicy Bypass -File C:\_ofxr\ofxr_lab_build.ps1
# Built and smoke-tested 2026-11-23 on the 3080 Ti (e2e_ofxr_tokyo_on: 88.1 new frames/s, app 42.1 fps, as on 2026-11-13).
$ErrorActionPreference = 'Continue'   # native tools (git, cmake) write progress to stderr; exit codes and outputs are checked instead
$R = 'C:\_ofxr'
Set-Location $R
$log = "$R\build.log"
function Step($m) { "$(Get-Date -Format HH:mm:ss) $m" | Tee-Object -FilePath $log -Append }
Step 'start'
if (-not (Test-Path "$R\src\CMakeLists.txt")) { Expand-Archive -Path "$R\ofxr-382065f.zip" -DestinationPath "$R\src" -Force }
Set-Location "$R\src"
$FFX_ROOT = 'external/FidelityFX-SDK-v1.1.4'
if (-not (Test-Path "$FFX_ROOT/sdk/bin/ffx_sdk/ffx_opticalflow_x64.lib")) {
  $head = ''
  if (Test-Path $FFX_ROOT) { $head = "$(git -C $FFX_ROOT rev-parse HEAD 2>$null)".Trim() }
  if ($head -ne 'c6efa6bf7f2027b3ec94f28578bb5965eabb9e55') {
    if (Test-Path $FFX_ROOT) { Step 'ffx: partial clone from the first attempt, cloning again'; Remove-Item -Recurse -Force $FFX_ROOT }
    Step 'ffx clone'
    cmd /c "git clone --branch v1.1.4 --depth 1 https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK.git $FFX_ROOT >> $log 2>&1"
    $head = "$(git -C $FFX_ROOT rev-parse HEAD 2>$null)".Trim()
  }
  if ($head -ne 'c6efa6bf7f2027b3ec94f28578bb5965eabb9e55') { Step "FFX head $head does not match the pin"; exit 2 }
  Step 'ffx configure'
  cmake -S "$FFX_ROOT/sdk" -B build-fidelityfx -G "Visual Studio 17 2022" -A x64 -DFFX_ALL=OFF -DFFX_OF=ON -DFFX_API_BACKEND=DX12_X64 -DFFX_BUILD_AS_DLL=OFF *>> $log
  Step 'ffx build'
  cmake --build build-fidelityfx --config Release --parallel *>> $log
  foreach ($lib in 'ffx_backend_dx12_x64.lib', 'ffx_opticalflow_x64.lib') { if (-not (Test-Path "$FFX_ROOT/sdk/bin/ffx_sdk/$lib")) { Step "FFX did not produce $lib"; exit 3 } }
}
if (-not (Test-Path 'external/DLSS/include/nvsdk_ngx_params.h')) {
  $env:GIT_LFS_SKIP_SMUDGE = '1'
  if (Test-Path external/DLSS) { Remove-Item -Recurse -Force external/DLSS }
  cmd /c "git -c advice.detachedHead=false clone --branch v310.9.1 --depth 1 --filter=blob:none --sparse https://github.com/NVIDIA/DLSS.git external/DLSS >> $log 2>&1"
  cmd /c "git -C external/DLSS sparse-checkout set include >> $log 2>&1"
  $head = (git -C external/DLSS rev-parse HEAD).Trim()
  if ($head -ne '374959484e79a640feaba44c93ac8cfb0a03f5b5') { Step "DLSS head $head does not match the pin"; exit 4 }
}
if (-not (Test-Path 'external/openvr/openvr.h')) {
  New-Item -ItemType Directory -Force external/openvr | Out-Null
  curl.exe -sSL -o external/openvr/openvr.h 'https://raw.githubusercontent.com/ValveSoftware/openvr/v2.5.1/headers/openvr.h'
}
$hash = (Get-FileHash external/openvr/openvr.h -Algorithm SHA256).Hash
if ($hash -ne '94E5545370159C85F87CD6E15DD3739F7C919FC7A6E869F5E4ED463533A07ED0') { Step "openvr.h hash $hash does not match"; exit 5 }
$fxc = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\fxc.exe' | Sort-Object FullName -Descending | Select-Object -First 1
Step "fxc $($fxc.FullName); layer configure"
cmake -S . -B build -G "Visual Studio 17 2022" -A x64 -DXRFG_FXC_EXECUTABLE="$($fxc.FullName)" -DXRFG_BUILD_TESTS=OFF -DXRFG_BUILD_STANDALONE=OFF *>> $log
Step 'layer build'
cmake --build build --config Release --parallel *>> $log
$dll = 'build/Release/XR_APILAYER_XRFrameBridge_diagnostic.dll'   # without the standalone target there is no build/Release/ofxr staging folder
if (-not (Test-Path $dll)) { Step 'layer dll missing'; exit 6 }
New-Item -ItemType Directory -Force "$R\lab" | Out-Null
Copy-Item $dll, 'layer/ofxr_bridge.ini' "$R\lab" -Force
if (Test-Path 'build/Release/OFXR_vulkan_queue_layer.dll') { Copy-Item 'build/Release/OFXR_vulkan_queue_layer.dll' "$R\lab" -Force }
$manifest = @'
{
  "file_format_version": "1.0.0",
  "api_layer": {
    "name": "XR_APILAYER_XRFrameBridge_diagnostic",
    "library_path": ".\\XR_APILAYER_XRFrameBridge_diagnostic.dll",
    "api_version": "1.0",
    "implementation_version": "551",
    "description": "OFXR Bridge V551 (382065f), AirFrameXR lab copy, explicit layer"
  }
}
'@
Set-Content -Path "$R\lab\XR_APILAYER_XRFrameBridge_diagnostic.json" -Value $manifest -Encoding ascii
Get-ChildItem "$R\lab" | ForEach-Object { Step ("{0,10} {1}" -f $_.Length, $_.Name) }
Step 'done'
