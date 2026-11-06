# Builds the OpenXR SDK's own sample app hello_xr (src\tests\hello_xr of VDXR's OpenXR-SDK-Source submodule, unmodified) -> build\hello_xr\...\hello_xr.exe.
# It is the lab's independent, well-behaved game (a third-party implementation of the OpenXR contract: it renders the recommended size with the located FOV),
# against which the intake and the V / G logic are checked without the circularity of our own xr_probe (docs/PICTURE_EDGES_DESIGN.md section 7).
$ErrorActionPreference = 'Continue'
$start = Get-Date
$root = if ($env:AIRFRAMEXR_ROOT) { $env:AIRFRAMEXR_ROOT } else { "$env:USERPROFILE\openxr" }
$src  = "$root\src\VDXR\external\OpenXR-SDK-Source"
$bdir = "$root\build\hello_xr"
New-Item -ItemType Directory -Force $bdir | Out-Null
New-Item -ItemType Directory -Force "$root\logs" | Out-Null
$log = "$root\logs\hello_xr_build.log"
if (-not (Test-Path "$src\src\tests\hello_xr\CMakeLists.txt")) { Write-Host "RESULT: FAILED (no hello_xr in $src)"; exit 1 }
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) { $cmake = 'C:\Program Files\CMake\bin\cmake.exe' }
& $cmake -S $src -B $bdir -G "Visual Studio 17 2022" -A x64 -DBUILD_TESTS=ON -DBUILD_API_LAYERS=OFF -DBUILD_CONFORMANCE_TESTS=OFF -DBUILD_LOADER=ON 2>&1 | Out-File -Encoding utf8 $log
& $cmake --build $bdir --config Release --target hello_xr 2>&1 | Out-File -Encoding utf8 -Append $log
Get-Content $log | Select-String -Pattern 'error|fatal|CMake Error' | Select-Object -First 8
$exe = Get-ChildItem $bdir -Recurse -Filter hello_xr.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($exe -and $exe.LastWriteTime -ge $start) { Write-Host "RESULT: OK $($exe.FullName) $($exe.Length)" } else { Write-Host "RESULT: FAILED (exe not built; see logs\hello_xr_build.log)" }
