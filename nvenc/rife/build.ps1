<#
Builds the neural frame generation network (docs/NEURAL_RIFE_CUDA.md): airframexr_rife.dll (our CUDA + CUTLASS) and rife44.afxw (RIFE 4.4's
weights in the DLL's layout), from pinned inputs with fixed flags, and writes build_manifest.txt (every tool version, input and output hash; no
dates). Same inputs and tools -> same files (the linker runs with /Brepro).

    powershell -ExecutionPolicy Bypass -File nvenc\rife\build.ps1 -Out <dir> [-Python <python.exe>]     (harness: run.py build neural)

Inputs, downloaded into -Cache when missing and checked against the pinned SHA-256 (a mismatch stops the build):
    CUTLASS 4.8.1 (BSD-3-Clause; headers only, compiled in)  https://github.com/NVIDIA/cutlass/archive/refs/tags/v4.8.1.zip
    RIFE 4.4 weights (MIT)                                   https://github.com/HolyWu/vs-rife/releases/download/model/flownet_v4.4.pkl
Tools: Python 3 (standard library only; default `python` on the PATH), the CUDA toolkit 13.5 (only the components nvcc, crt, nvvm, cudart, cccl: cuda_13.5.0_windows_x86_64_network.exe -s nvcc_13.5 crt_13.5
nvvm_13.5 cudart_13.5 cccl_13.5), Visual Studio 2022 C++ (vcvars64.bat). Another toolkit version: -Cuda <dir> -AnyVersion (the recipe says what
to re-check).
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Python = "python",
    [string]$Cache = "",
    [string]$Cuda = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.5",
    [string]$Vcvars = "",
    [switch]$AnyVersion
)
$ErrorActionPreference = "Stop"
$src = $PSScriptRoot
if (-not $Cache) { $Cache = Join-Path $src "..\..\external\rife-build" }
New-Item -ItemType Directory -Force -Path $Out, $Cache | Out-Null
$Out = (Resolve-Path $Out).Path; $Cache = (Resolve-Path $Cache).Path

# ---- pins (update together with docs/NEURAL_RIFE_CUDA.md) ----
$CudaVersion = "13.5"
$CutlassVersion = "4.8.1"
$CutlassUrl = "https://github.com/NVIDIA/cutlass/archive/refs/tags/v4.8.1.zip"
$CutlassSha = "3bae692ac08657dd025ac89c88d86558d433776558339f8feef9aedd02a296e5"
$RifeUrl = "https://github.com/HolyWu/vs-rife/releases/download/model/flownet_v4.4.pkl"
$RifeSha = "a051572810b025c35013a35f044a08e6edb516ec1ffa1ac2878a48059f30d5fa"
# GPUs: SASS for Ampere (sm_86, also runs on Ada sm_89) and Blackwell (sm_120), PTX of compute_86 for anything newer (the driver compiles it)
$Gencode = @("-gencode=arch=compute_86,code=sm_86", "-gencode=arch=compute_120,code=sm_120", "-gencode=arch=compute_86,code=compute_86")
$NvccFlags = @("-O3", "-std=c++17", "--shared", "-cudart", "static", "-DNDEBUG", "--expt-relaxed-constexpr", "-Xcompiler", "/MT,/O2,/EHsc,/Zc:__cplusplus,/Zc:preprocessor,/utf-8",
               "-Xlinker", "/Brepro")

function Sha($p) { (Get-FileHash -Algorithm SHA256 $p).Hash.ToLower() }
function Fetch($url, $file, $sha) {
    if (-not (Test-Path $file)) {
        Write-Host "download $url"
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest -Uri $url -OutFile "$file.part" -UseBasicParsing
        Move-Item -Force "$file.part" $file
    }
    $h = Sha $file
    if ($h -ne $sha) { throw "$file has SHA-256 $h, pinned $sha (delete it to download again, or update the pin and the recipe)" }
}

# ---- inputs ----
$cutZip = Join-Path $Cache "cutlass-$CutlassVersion.zip"
Fetch $CutlassUrl $cutZip $CutlassSha
$cutDir = Join-Path $Cache "cutlass-$CutlassVersion"
if (-not (Test-Path (Join-Path $cutDir "include\cutlass\version.h"))) {
    Write-Host "extract $cutZip"
    Expand-Archive -Force $cutZip $Cache
}
$ver = Get-Content (Join-Path $cutDir "include\cutlass\version.h") -Raw
$parts = foreach ($k in "MAJOR", "MINOR", "PATCH") { if ($ver -match "#define CUTLASS_$k (\d+)") { $Matches[1] } }
if (($parts -join ".") -ne $CutlassVersion) { throw "CUTLASS headers say $($parts -join '.'), pinned $CutlassVersion" }
$pkl = Join-Path $Cache "flownet_v4.4.pkl"
Fetch $RifeUrl $pkl $RifeSha

# ---- tools ----
$nvcc = Join-Path $Cuda "bin\nvcc.exe"
if (-not (Test-Path $nvcc)) { throw "no nvcc at $nvcc (-Cuda <toolkit dir>)" }
$nvccVer = (& $nvcc --version | Select-String "release").ToString().Trim()
if ($nvccVer -notmatch "release $([regex]::Escape($CudaVersion)),") {
    if (-not $AnyVersion) { throw "nvcc is '$nvccVer', pinned $CudaVersion (-AnyVersion to build anyway, then re-run the recipe's checks)" }
    Write-Warning "nvcc is '$nvccVer', pinned $CudaVersion"
}
if (-not $Vcvars) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $Vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
}
if (-not (Test-Path $Vcvars)) { throw "no vcvars64.bat ($Vcvars)" }

# ---- weights ----
$afxw = Join-Path $Out "rife44.afxw"
& $Python -I (Join-Path $src "export_weights.py") $pkl $afxw
if ($LASTEXITCODE) { throw "export_weights.py failed" }

# ---- DLL ----
$dll = Join-Path $Out "airframexr_rife.dll"
$obj = Join-Path $Cache "obj"
New-Item -ItemType Directory -Force -Path $obj | Out-Null
$nvArgs = @("`"$(Join-Path $src 'airframexr_rife.cu')`"", "-o", "`"$dll`"", "-I", "`"$(Join-Path $cutDir 'include')`"", "-I", "`"$src`"") + $NvccFlags + $Gencode +
        @("-Xlinker", "/IMPLIB:`"$(Join-Path $obj 'airframexr_rife.lib')`"")
$cmd = "call `"$Vcvars`" >nul && `"$nvcc`" $($nvArgs -join ' ')"
Write-Host "nvcc $($nvArgs -join ' ')"
$sw = [Diagnostics.Stopwatch]::StartNew()
cmd /c $cmd
if ($LASTEXITCODE) { throw "nvcc failed ($LASTEXITCODE)" }
Write-Host ("built in {0:N0} s" -f $sw.Elapsed.TotalSeconds)
$clVer = (cmd /c "call `"$Vcvars`" >nul && cl 2>&1" | Select-Object -First 1).ToString().Trim()

# ---- manifest (no dates: two builds from the same inputs give the same file) ----
$m = @(
    "airframexr_rife build manifest (nvenc/rife/build.ps1, docs/NEURAL_RIFE_CUDA.md)",
    "nvcc: $nvccVer",
    "cl: $clVer",
    "CUTLASS: $CutlassVersion $CutlassUrl sha256 $CutlassSha",
    "RIFE weights: $RifeUrl sha256 $RifeSha",
    "nvcc flags: $($NvccFlags -join ' ') $($Gencode -join ' ')",
    "airframexr_rife.cu sha256 $(Sha (Join-Path $src 'airframexr_rife.cu'))",
    "airframexr_rife.h sha256 $(Sha (Join-Path $src 'airframexr_rife.h'))",
    "export_weights.py sha256 $(Sha (Join-Path $src 'export_weights.py'))",
    "airframexr_rife.dll $((Get-Item $dll).Length) bytes sha256 $(Sha $dll)",
    "rife44.afxw $((Get-Item $afxw).Length) bytes sha256 $(Sha $afxw)"
)
$m | Set-Content -Encoding ascii (Join-Path $Out "build_manifest.txt")
$m | ForEach-Object { Write-Host $_ }
