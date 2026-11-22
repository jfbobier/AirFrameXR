# The neural frame generation network (docs/NEURAL_RIFE_CUDA.md): airframexr_rife.dll + rife44.afxw into out\neural, built by nvenc\rife\build.ps1
# from its pinned inputs (cached in external\rife-build). Needs the CUDA toolkit 13.5 and Visual Studio 2022 on the builder, and Python 3 (standard
# library only). The package ships both files in bin\neural.
$ErrorActionPreference = 'Continue'
$root = if ($env:AIRFRAMEXR_ROOT) { $env:AIRFRAMEXR_ROOT } else { "$env:USERPROFILE\openxr" }
New-Item -ItemType Directory -Force "$root\out\neural", "$root\external\rife-build" | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File "$root\nvenc\rife\build.ps1" -Out "$root\out\neural" -Python python -Cache "$root\external\rife-build" *>&1 | ForEach-Object { "$_" }
$code = $LASTEXITCODE
if ($code -or -not (Test-Path "$root\out\neural\airframexr_rife.dll") -or -not (Test-Path "$root\out\neural\rife44.afxw")) { Write-Host "RESULT: FAILED (build.ps1 exit $code)"; exit 1 }
Write-Host "RESULT: OK"
