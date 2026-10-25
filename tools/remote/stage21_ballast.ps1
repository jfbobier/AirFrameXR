# Builds tools/stability/vram_ballast.cpp -> build\stability\vram_ballast.exe (the stability harness' VRAM / RAM ballast).
#   builder.sh put tools/stability/vram_ballast.cpp _tmp/vram_ballast.cpp ; builder.sh ps tools/remote/stage21_ballast.ps1
$ErrorActionPreference = 'Continue'
$start = Get-Date
$root = if ($env:AIRFRAMEXR_ROOT) { $env:AIRFRAMEXR_ROOT } else { "$env:USERPROFILE\openxr" }
$bdir = "$root\build\stability"
New-Item -ItemType Directory -Force $bdir | Out-Null
New-Item -ItemType Directory -Force "$root\logs" | Out-Null
$vcvars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
@"
@echo off
call "$vcvars" >nul
cd /d "$bdir"
cl /nologo /EHsc /MD /O2 /std:c++17 "$root\_tmp\vram_ballast.cpp" /Fe:vram_ballast.exe /link d3d11.lib dxgi.lib
"@ | Set-Content -Encoding ASCII "$bdir\build.cmd"
cmd /c "$bdir\build.cmd" 2>&1 | Out-File -Encoding utf8 "$root\logs\ballast_build.log"
Get-Content "$root\logs\ballast_build.log" | Select-String -Pattern 'error|fatal' | Select-Object -First 10
$exe = Get-Item "$bdir\vram_ballast.exe" -ErrorAction SilentlyContinue
if (-not ($exe -and $exe.LastWriteTime -ge $start)) { Write-Host "RESULT: FAILED (exe not rebuilt; see logs\ballast_build.log)"; exit 1 }
Write-Host "RESULT: OK $($exe.FullName) $($exe.Length) bytes"
