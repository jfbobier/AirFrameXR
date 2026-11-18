# Builds VDXR, the OpenXR runtime, from the AirFrameXR fork at the commit pinned in deps.lock.json (builder_sources.VDXR:
# remote jfbobier/VirtualDesktop-OpenXR, branch airframexr, full_hash; harness/run.py build vdxr puts the lock in _tmp).
# The fork is upstream VirtualDesktop-OpenXR (mbucchia, MIT) plus our commits (`git log <upstream_hash>..<full_hash>` in the fork):
#  - eyeVisibility honored on quad/cylinder/cube layers in "Oculus runtime" mode (VDXR computes the per-eye flags but only sets them
#    when Virtual Desktop's own server is running; OVRShim implements the flags);
#  - all 6 faces (and mips) of a cube swapchain copied (upstream copies subresource 0 only);
#  - display name "AirFrameXR (VDXR)" instead of "VirtualDesktopXR (Standalone)", which confused users;
#  - VDXR's own running start off by default: xrWaitFrame waits for the shim's release (rig 2026-10-23: frame start -> GPU done
#    55 -> 42 ms); registry DWORD ovrshim_vdxr_running_start = 1 (HKLM\SOFTWARE\VirtualDesktop-OpenXR) restores it for an A/B;
#  - XR_KHR_composition_layer_cube advertised (HAS_CUBE_LAYERS in RuntimeConfiguration.h);
#  - no slice copy or commit when the app released nothing since the previous frame (texture arrays, MSAA): an OVR commit
#    then always means a newly released picture;
#  - per-frame facts to OVRShim through its ovrshim_Trace export (virtualdesktop-openxr/airframexr_trace.h).
# Until 1.3.11 the first five were regex text patches applied here to an upstream checkout; the fork's first five commits
# reproduce their text exactly.
# -Unpatched builds stock upstream at upstream_hash instead (A/B against stock VDXR).
# The builder's src\VDXR checkout is moved to the requested commit (checkout -f: edits in that folder are overwritten; the
# build-time patches of earlier versions were the only ones).
param([switch]$Unpatched)
$ErrorActionPreference = 'Continue'
$start = Get-Date
$root = if ($env:AIRFRAMEXR_ROOT) { $env:AIRFRAMEXR_ROOT } else { "$env:USERPROFILE\openxr" }
$v = "$root\src\VDXR"
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'

$lockFile = "$root\_tmp\deps.lock.json"
if (-not (Test-Path $lockFile)) { Write-Host "RESULT: FAILED (no _tmp\deps.lock.json; harness/run.py build vdxr puts it there)"; exit 1 }
if (-not (Test-Path "$v\.git")) { Write-Host "RESULT: FAILED (no VDXR checkout in $v; run harness/run.py setup)"; exit 1 }
$pin = (Get-Content $lockFile -Raw | ConvertFrom-Json).builder_sources.VDXR
$hash = if ($Unpatched) { $pin.upstream_hash } else { $pin.full_hash }
$remote = if ($Unpatched) { $pin.upstream_remote } else { $pin.remote }
if (-not $hash -or -not $remote) { Write-Host "RESULT: FAILED (deps.lock.json has no VDXR remote/hash for this build)"; exit 1 }

git -C $v cat-file -e "$($hash)^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) {
  git -C $v fetch -q --depth 1 $remote $hash 2>&1 | Select-Object -Last 2
}
git -C $v checkout -q -f $hash 2>&1 | Select-Object -Last 2
git -C $v submodule update --init --recursive --depth 1 2>&1 | Select-Object -Last 3
$head = (git -C $v rev-parse HEAD 2>$null)
$changed = (git -C $v status --porcelain --untracked-files=no 2>$null) -join ' '
if ($head -ne $hash -or $changed) { Write-Host "RESULT: FAILED (src\VDXR at $head, wanted $hash; changed: $changed)"; exit 1 }
Write-Host "vdxr: $head ($(if ($Unpatched) { 'stock upstream' } else { 'AirFrameXR fork' }))"

$env:CL = ''   # earlier versions passed /DHAS_CUBE_LAYERS this way; the fork defines it in RuntimeConfiguration.h
Set-Location $v
& $msbuild virtualdesktop-openxr\virtualdesktop-openxr.vcxproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 "/p:SolutionDir=$v\\" /m /v:minimal /nologo 2>&1 |
  Out-File -Encoding utf8 "$root\logs\vdxr_patched_build.log"
$dll = "$v\bin\x64\Release\virtualdesktop-openxr.dll"
if ((Test-Path $dll) -and ((Get-Item $dll).LastWriteTime -ge $start)) {
  Get-Item $dll | Select-Object FullName, Length, LastWriteTime
  Write-Host "RESULT: OK"
} else {
  Get-Content "$root\logs\vdxr_patched_build.log" | Select-String -Pattern 'error' | Select-Object -First 8
  Write-Host "RESULT: FAILED (see logs\vdxr_patched_build.log)"
}
