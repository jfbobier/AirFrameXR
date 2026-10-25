# Builds tools/nvof_probe/fruc_probe.cpp against the NVIDIA Optical Flow SDK (C:\Nvidia, not in the repo) -> build\fruc_probe\fruc_probe.exe
# and runs it: NvOFFRUC interpolation time and quality per resolution. Output: logs\fruc_probe.jsonl (+ BMPs in build\fruc_probe\out).
#   builder.sh put tools/nvof_probe/fruc_probe.cpp _tmp/fruc_probe.cpp ; builder.sh ps tools/remote/stage19_fruc_probe.ps1 [frames] [sizes...]
param([int]$Frames = 40, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Sizes = @())
$ErrorActionPreference = 'Continue'
$start = Get-Date
$root = if ($env:AIRFRAMEXR_ROOT) { $env:AIRFRAMEXR_ROOT } else { "$env:USERPROFILE\openxr" }
$sdk  = Get-ChildItem 'C:\Nvidia' -Directory -Filter 'Optical_Flow_SDK_*' | Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) { Write-Host "RESULT: FAILED (no C:\Nvidia\Optical_Flow_SDK_* folder)"; exit 1 }
$inc  = "$($sdk.FullName)\NvOFFRUC\Interface"
$bin  = "$($sdk.FullName)\NvOFFRUC\NvOFFRUCSample\bin\win64"
$bdir = "$root\build\fruc_probe"
New-Item -ItemType Directory -Force $bdir | Out-Null
New-Item -ItemType Directory -Force "$bdir\out" | Out-Null
New-Item -ItemType Directory -Force "$root\logs" | Out-Null
$vcvars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
@"
@echo off
call "$vcvars" >nul
cd /d "$bdir"
cl /nologo /EHsc /MD /O2 /openmp /std:c++17 /DUNICODE /D_UNICODE /I"$inc" "$root\_tmp\fruc_probe.cpp" /Fe:fruc_probe.exe /link d3d11.lib dxgi.lib
"@ | Set-Content -Encoding ASCII "$bdir\build.cmd"
cmd /c "$bdir\build.cmd" 2>&1 | Out-File -Encoding utf8 "$root\logs\fruc_probe_build.log"
Get-Content "$root\logs\fruc_probe_build.log" | Select-String -Pattern 'error|fatal' | Select-Object -First 10
$exe = Get-Item "$bdir\fruc_probe.exe" -ErrorAction SilentlyContinue
if (-not ($exe -and $exe.LastWriteTime -ge $start)) { Write-Host "RESULT: FAILED (exe not rebuilt; see logs\fruc_probe_build.log)"; exit 1 }
Write-Host "built $($exe.FullName) $($exe.Length) bytes; SDK $($sdk.Name)"
if ($Sizes.Count -eq 0) { $Sizes = @('1760x1600', '4224x1664', '7104x3200') }
# one process per size: a second NvOFFRUC instance in the same process fails NvOFFRUCRegisterResource (status 4) after the first was destroyed
'' | Out-File -Encoding utf8 "$root\logs\fruc_probe.jsonl"
$smi = Start-Process nvidia-smi -ArgumentList '--query-gpu=timestamp,pstate,clocks.sm,clocks.video,utilization.gpu,power.draw','--format=csv,noheader','-lms','250' -RedirectStandardOutput "$root\logs\fruc_gpu.csv" -PassThru -NoNewWindow
foreach ($sz in $Sizes) {
    & $exe.FullName $bin "$bdir\out" "$Frames" $sz 2>&1 | Tee-Object -Append -FilePath "$root\logs\fruc_probe.jsonl"
}
Stop-Process -Id $smi.Id -ErrorAction SilentlyContinue
Get-Content "$root\logs\fruc_gpu.csv" | Select-Object -Last 12
Write-Host "RESULT: OK"
