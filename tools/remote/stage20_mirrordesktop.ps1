# Builds tools/mirrordesktop/mirrordesktop.cpp -> out\mirrordesktop\mirrordesktop.exe: the desktop mirror, an OpenXR app (docs/DESKTOP_MIRROR.md).
$ErrorActionPreference = 'Continue'
$start = Get-Date
$root = if ($env:VISIONALVR_ROOT) { $env:VISIONALVR_ROOT } else { "$env:USERPROFILE\openxr" }
$sdk  = "$root\src\VDXR\external\OpenXR-SDK\include"
$lib  = "$root\build\oxr-sdk\src\loader\Release\openxr_loader.lib"
$src  = "$root\tools\mirrordesktop"
$out  = "$root\out\mirrordesktop"
New-Item -ItemType Directory -Force $out | Out-Null
$vcvars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
@"
@echo off
call "$vcvars" >nul
cd /d "$out"
cl /nologo /EHsc /MD /O2 /std:c++17 /I"$sdk" /I"$src" "$src\mirrordesktop.cpp" /Fe:mirrordesktop.exe /link "$lib" d3d11.lib dxgi.lib d3dcompiler.lib user32.lib wtsapi32.lib advapi32.lib
"@ | Set-Content -Encoding ASCII "$out\build.cmd"
cmd /c "$out\build.cmd" 2>&1 | Out-File -Encoding utf8 "$root\logs\mirrordesktop_build.log"
Get-Content "$root\logs\mirrordesktop_build.log" | Select-String -Pattern 'error|fatal' | Select-Object -First 10
$exe = Get-Item "$out\mirrordesktop.exe" -ErrorAction SilentlyContinue
if ($exe -and $exe.LastWriteTime -ge $start) { Write-Host "RESULT: OK $($exe.FullName) $($exe.Length)" } else { Write-Host "RESULT: FAILED (exe not rebuilt; see logs\mirrordesktop_build.log)" }
