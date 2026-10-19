# Assembles the portable VisionALVR folder out\dist\VisionALVR (what a user unzips anywhere) and out\dist\VisionALVR-<version>.zip.
# Layout: the root holds ONE file, VisionALVR.exe (the launcher); everything else is in a subfolder:
#   bin\   everything the launcher starts or loads: streamer.exe, configure.exe (setup wizard and --diagnose), performance.exe, alvr_host.exe,
#          register_openxr_runtime.ps1 and the OpenXR runtime (LibOVRRT64_1.dll, virtualdesktop-openxr.dll + .json: VDXR loads the shim from its own folder)
#   DLSSMotionVectors\dxgi.dll (the extractor a user copies next to a game; never loaded from here), bench\, profiles\, config\, docs\, logs\ (created on use)
$ErrorActionPreference = 'Continue'
$root = if ($env:VISIONALVR_ROOT) { $env:VISIONALVR_ROOT } else { "$env:USERPROFILE\openxr" }
$version = if ($env:VISIONALVR_VERSION) { $env:VISIONALVR_VERSION } else { '0.2-alpha' }
$pkg = "$root\out\dist\VisionALVR"   # a folder no earlier layout used (out\visionalvr = the old installer package)
# an earlier layout (loose files in the root, or libs\ / licenses\ folders) would hide a broken new one: the old folder is moved aside once, never deleted
$stale = (Test-Path $pkg) -and ((@(Get-ChildItem $pkg -File | Where-Object { $_.Name -ne 'VisionALVR.exe' }).Count -gt 0) -or (Test-Path "$pkg\libs") -or (Test-Path "$pkg\licenses") -or (Test-Path "$pkg\bin\logs"))
if ($stale) { Move-Item $pkg "$pkg.old-$((Get-Date).ToString('yyyyMMdd-HHmmss'))" }
New-Item -ItemType Directory -Force $pkg, "$pkg\config", "$pkg\bin", "$pkg\docs" | Out-Null
Copy-Item "$root\out\vdxr-shim\*.dll", "$root\out\vdxr-shim\*.json" "$pkg\bin" -Force                 # virtualdesktop-openxr.dll / .json, LibOVRRT64_1.dll
Copy-Item "$root\src\ALVR-v20.14.1\target\release\alvr_host.exe" "$pkg\bin" -Force
foreach ($f in 'streamer.exe', 'configure.exe', 'performance.exe') { if (Test-Path "$root\out\gui\$f") { Copy-Item "$root\out\gui\$f" "$pkg\bin" -Force } }
if (Test-Path "$root\out\gui\VisionALVR.exe") { Copy-Item "$root\out\gui\VisionALVR.exe" $pkg -Force }   # the launcher: the only file in the root
Copy-Item "$root\_tmp\install\register_openxr_runtime.ps1" "$pkg\bin" -Force
Copy-Item "$root\_tmp\install\README.txt" "$pkg\docs" -Force
Copy-Item "$root\_tmp\install\session.default.json" "$pkg\config\session.default.json" -Force
# licences: notices, the project licence (if present) and the upstream texts
New-Item -ItemType Directory -Force "$pkg\docs\licenses" | Out-Null
Copy-Item "$root\_tmp\install\THIRD_PARTY_NOTICES.md" "$pkg\docs" -Force
if (Test-Path "$root\_tmp\install\LICENSE.txt") { Copy-Item "$root\_tmp\install\LICENSE.txt" "$pkg\docs" -Force }
Copy-Item "$root\_tmp\install\licenses\*.txt" "$pkg\docs\licenses" -Force
# the benchmark scene (tools/bench_scene/build.sh, uploaded by `run.py build host`)
New-Item -ItemType Directory -Force "$pkg\bench" | Out-Null
if (Test-Path "$root\bench\littlest_tokyo.vab") { Copy-Item "$root\bench\littlest_tokyo.vab" "$pkg\bench\littlest_tokyo.vab" -Force }
# the OpenXR scene app of the wizard's scene test (renders that scene through the real shim path with the headset's head pose)
if (Test-Path "$root\build\probe\xr_probe.exe") { Copy-Item "$root\build\probe\xr_probe.exe" "$pkg\bench\xr_probe.exe" -Force }
# the DLSS motion-vector extractor (OptiScaler fork, GPL-3.0; source and licence named in THIRD_PARTY_NOTICES.md): a user copies it next to a game's exe
$dlssvecMarker = "$root\_tmp\install\DLSSMotionVectors\present.txt"
$shipDlssvec = (Test-Path $dlssvecMarker) -and ((Get-Content $dlssvecMarker -Raw).Trim() -eq '1') -and (Test-Path "$root\_tmp\install\DLSSMotionVectors\dxgi.dll")
if ($shipDlssvec) { New-Item -ItemType Directory -Force "$pkg\DLSSMotionVectors" | Out-Null; Copy-Item "$root\_tmp\install\DLSSMotionVectors\dxgi.dll" "$pkg\DLSSMotionVectors\dxgi.dll" -Force }
# the wizard's community headset profiles
New-Item -ItemType Directory -Force "$pkg\profiles" | Out-Null
if (Test-Path "$root\_tmp\install\profiles") { Copy-Item "$root\_tmp\install\profiles\*.json" "$pkg\profiles" -Force }
# personal build (harness/run.py package with VISIONALVR_PERSONAL=1): a ready config\visionalvr.json and a zip suffix; a generic build leaves both empty
$versionFile = "$root\_tmp\install\version.txt"
if ((Test-Path $versionFile) -and ((Get-Content $versionFile -Raw).Trim().Length -gt 0)) { $version = (Get-Content $versionFile -Raw).Trim() }
$suffixFile = "$root\_tmp\install\version_suffix.txt"
if (Test-Path $suffixFile) { $version += (Get-Content $suffixFile -Raw).Trim() }
$persFile = "$root\_tmp\install\visionalvr.personal.json"
$persShip = @()
if ((Test-Path $persFile) -and ((Get-Content $persFile -Raw).Trim().Length -gt 2)) { Copy-Item $persFile "$pkg\config\visionalvr.json" -Force; $persShip = @('config\visionalvr.json') }
# personal build: extra files from the build PC (tools of the owner's own session, e.g. an audio switcher) in extras\ (never in the root)
$persFiles = @()
$pfList = "$root\_tmp\install\personal_files.txt"
if (Test-Path $pfList) { foreach ($line in (Get-Content $pfList)) { $f = $line.Trim(); if (-not $f) { continue }; if (-not (Test-Path $f)) { Write-Host "FAILED: personal extra file missing: $f (a personal release must ship startvisionalvr.bat and svcl.exe; backup in private\personal_files)"; exit 1 }; New-Item -ItemType Directory -Force "$pkg\extras" | Out-Null; Copy-Item $f "$pkg\extras" -Force; $persFiles += ("extras\" + (Split-Path $f -Leaf)) } }
$zip = "$root\out\dist\VisionALVR-$version.zip"
# the zip holds only what ships (never logs\, config\session.json or results a test run may have left in the folder);
# everything sits under VisionALVR\ so it unzips to one folder. logs\ and the user's config files are created on first use.
$ship = @('VisionALVR.exe', 'bin\alvr_host.exe', 'bin\streamer.exe', 'bin\configure.exe', 'bin\performance.exe', 'bin\register_openxr_runtime.ps1',
          'bin\LibOVRRT64_1.dll', 'bin\virtualdesktop-openxr.dll', 'bin\virtualdesktop-openxr.json',
          'config\session.default.json', 'bench\littlest_tokyo.vab', 'bench\xr_probe.exe', 'docs\README.txt', 'docs\THIRD_PARTY_NOTICES.md', 'docs\LICENSE.txt')
$ship += $persShip
$ship += $persFiles
if ($shipDlssvec) { $ship += 'DLSSMotionVectors\dxgi.dll' }
$ship += Get-ChildItem "$pkg\profiles" -File -Filter *.json | ForEach-Object { "profiles\$($_.Name)" }
$ship += Get-ChildItem "$pkg\docs\licenses" -File | ForEach-Object { "docs\licenses\$($_.Name)" }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$fs = [IO.File]::Open($zip, [IO.FileMode]::Create)
$za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
foreach ($f in $ship) {
  if (Test-Path "$pkg\$f") { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, "$pkg\$f", ('VisionALVR/' + $f.Replace('\', '/')), [IO.Compression.CompressionLevel]::Optimal) | Out-Null }
}
$za.Dispose(); $fs.Dispose()
Get-ChildItem $pkg -Recurse -File | Select-Object @{n='file';e={$_.FullName.Substring($pkg.Length + 1)}}, Length | Format-Table -AutoSize | Out-String -Width 200
$missing = @('VisionALVR.exe', 'bin\alvr_host.exe', 'bin\streamer.exe', 'bin\configure.exe', 'bin\performance.exe', 'bin\register_openxr_runtime.ps1', 'bin\LibOVRRT64_1.dll', 'bin\virtualdesktop-openxr.dll', 'bin\virtualdesktop-openxr.json', 'bench\littlest_tokyo.vab', 'bench\xr_probe.exe', 'docs\THIRD_PARTY_NOTICES.md', 'docs\licenses\ALVR-LICENSE.txt') | Where-Object { -not (Test-Path "$pkg\$_") }
$rootFiles = @(Get-ChildItem $pkg -File | ForEach-Object { $_.Name })
if ($rootFiles.Count -ne 1 -or $rootFiles[0] -ne 'VisionALVR.exe') { Write-Host "RESULT: FAILED the root must hold only VisionALVR.exe, found: $($rootFiles -join ', ')"; exit 1 }
if ($missing) { Write-Host "RESULT: FAILED missing $($missing -join ', ')" } else { Write-Host "RESULT: OK $pkg ($zip)" }
