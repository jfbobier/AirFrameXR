# Assembles the portable AirFrameXR folder out\dist\AirFrameXR (what a user unzips anywhere) and out\dist\AirFrameXR-<version>.zip.
# Layout: the root holds ONE file, AirFrameXR.exe (a stub that starts bin\launcher.exe); everything else is in a subfolder:
#   bin\   everything the launcher starts or loads: launcher.exe, streamer.exe, configure.exe (setup wizard and --diagnose), performance.exe (each with its .exe.config), alvr_host.exe,
#          register_openxr_runtime.ps1 and the OpenXR runtime (LibOVRRT64_1.dll, virtualdesktop-openxr.dll + .json: VDXR loads the shim from its own folder)
#   bench\, profiles\, config\, docs\, logs\ (created on use)
$ErrorActionPreference = 'Continue'
$root = if ($env:AIRFRAMEXR_ROOT) { $env:AIRFRAMEXR_ROOT } else { "$env:USERPROFILE\openxr" }
$version = if ($env:AIRFRAMEXR_VERSION) { $env:AIRFRAMEXR_VERSION } else { '1.1.0-pre' }
$pkg = "$root\out\dist\AirFrameXR"   # a folder no earlier layout used (out\airframexr = the old installer package)
# an earlier layout (loose files in the root, or libs\ / licenses\ folders) would hide a broken new one: the old folder is moved aside once, never deleted
$stale = (Test-Path $pkg) -and ((@(Get-ChildItem $pkg -File | Where-Object { $_.Name -ne 'AirFrameXR.exe' }).Count -gt 0) -or (Test-Path "$pkg\libs") -or (Test-Path "$pkg\licenses") -or (Test-Path "$pkg\bin\logs"))
if ($stale) { Move-Item $pkg "$pkg.old-$((Get-Date).ToString('yyyyMMdd-HHmmss'))" }
New-Item -ItemType Directory -Force $pkg, "$pkg\config", "$pkg\bin", "$pkg\docs" | Out-Null
Copy-Item "$root\out\vdxr-shim\*.dll", "$root\out\vdxr-shim\*.json" "$pkg\bin" -Force                 # virtualdesktop-openxr.dll / .json, LibOVRRT64_1.dll
Copy-Item "$root\src\ALVR-v20.14.1\target\release\alvr_host.exe" "$pkg\bin" -Force
foreach ($f in 'streamer.exe', 'configure.exe', 'performance.exe', 'launcher.exe') {
    if (Test-Path "$root\out\gui\$f") { Copy-Item "$root\out\gui\$f" "$pkg\bin" -Force }
    Copy-Item "$root\_tmp\gui\app.exe.config" "$pkg\bin\$f.config" -Force   # WinForms' per-monitor DPI mode (tools/gui/app.exe.config)
}
if (Test-Path "$root\out\mirrordesktop\mirrordesktop.exe") { Copy-Item "$root\out\mirrordesktop\mirrordesktop.exe" "$pkg\bin" -Force }   # the desktop mirror (an OpenXR app the streamer starts)
if (Test-Path "$root\out\gui\AirFrameXR.exe") { Copy-Item "$root\out\gui\AirFrameXR.exe" $pkg -Force }   # the stub: the only file in the root (starts bin\launcher.exe)
Copy-Item "$root\_tmp\install\register_openxr_runtime.ps1" "$pkg\bin" -Force
Copy-Item "$root\_tmp\install\README.txt" "$pkg\docs" -Force
Copy-Item "$root\_tmp\install\session.default.json" "$pkg\config\session.default.json" -Force
# licences: notices, the project licence (if present) and the upstream texts
New-Item -ItemType Directory -Force "$pkg\docs\licenses" | Out-Null
Copy-Item "$root\_tmp\install\THIRD_PARTY_NOTICES.md" "$pkg\docs" -Force
if (Test-Path "$root\_tmp\install\LICENSE.txt") { Copy-Item "$root\_tmp\install\LICENSE.txt" "$pkg\docs" -Force }
# only the licence texts the repo lists (_tmp\install\licenses.txt, written by run.py): the staging folder and the package folder keep files
# the repo has dropped since, which must not ship
$licenses = @(Get-Content "$root\_tmp\install\licenses.txt" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($l in $licenses) { Copy-Item "$root\_tmp\install\licenses\$l" "$pkg\docs\licenses" -Force }
# the benchmark scene (tools/bench_scene/build.sh, uploaded by `run.py build host`)
New-Item -ItemType Directory -Force "$pkg\bench" | Out-Null
if (Test-Path "$root\bench\littlest_tokyo.vab") { Copy-Item "$root\bench\littlest_tokyo.vab" "$pkg\bench\littlest_tokyo.vab" -Force }
if (Test-Path "$root\bench\sakura_breeze.mp3") { Copy-Item "$root\bench\sakura_breeze.mp3" "$pkg\bench\sakura_breeze.mp3" -Force }   # the scene's music (credits in THIRD_PARTY_NOTICES.md)
# the OpenXR scene app of the wizard's scene test (renders that scene through the real shim path with the headset's head pose)
if (Test-Path "$root\build\probe\xr_probe.exe") { Copy-Item "$root\build\probe\xr_probe.exe" "$pkg\bench\xr_probe.exe" -Force }
# the wizard's community headset profiles
New-Item -ItemType Directory -Force "$pkg\profiles" | Out-Null
if (Test-Path "$root\_tmp\install\profiles") { Copy-Item "$root\_tmp\install\profiles\*.json" "$pkg\profiles" -Force }
# personal build (harness/run.py package with AIRFRAMEXR_PERSONAL=1): a ready config\airframexr.json and a zip suffix; a generic build leaves both empty
$versionFile = "$root\_tmp\install\version.txt"
if ((Test-Path $versionFile) -and ((Get-Content $versionFile -Raw).Trim().Length -gt 0)) { $version = (Get-Content $versionFile -Raw).Trim() }
$suffixFile = "$root\_tmp\install\version_suffix.txt"
if (Test-Path $suffixFile) { $version += (Get-Content $suffixFile -Raw).Trim() }
# the flavour of this build (harness/run.py writes _tmp\flavour.txt: debug = per-frame telemetry, release = none): bin\flavour.txt tells the windows, and a debug zip says so in its name
$flavour = if (Test-Path "$root\_tmp\flavour.txt") { (Get-Content "$root\_tmp\flavour.txt" -Raw).Trim() } else { 'debug' }
Set-Content -Path "$pkg\bin\flavour.txt" -Value $flavour -NoNewline
if ($flavour -ne 'release') { $version += '-debug' }
$persFile = "$root\_tmp\install\airframexr.personal.json"
$persShip = @()
if ((Test-Path $persFile) -and ((Get-Content $persFile -Raw).Trim().Length -gt 2)) { Copy-Item $persFile "$pkg\config\airframexr.json" -Force; $persShip = @('config\airframexr.json') }
# personal build: extra files from the build PC (tools of the owner's own session, e.g. an audio switcher) in extras\ (never in the root)
$persFiles = @()
$pfList = "$root\_tmp\install\personal_files.txt"
if (Test-Path $pfList) { foreach ($line in (Get-Content $pfList)) { $f = $line.Trim(); if (-not $f) { continue }; if (-not (Test-Path $f)) { Write-Host "FAILED: personal extra file missing: $f (listed in extra_files)"; exit 1 }; New-Item -ItemType Directory -Force "$pkg\extras" | Out-Null; Copy-Item $f "$pkg\extras" -Force; $persFiles += ("extras\" + (Split-Path $f -Leaf)) } }
$zip = "$root\out\dist\AirFrameXR-$version.zip"
# the zip holds only what ships (never logs\, config\session.json or results a test run may have left in the folder);
# everything sits under AirFrameXR\ so it unzips to one folder. logs\ and the user's config files are created on first use.
$ship = @('AirFrameXR.exe', 'bin\alvr_host.exe', 'bin\streamer.exe', 'bin\configure.exe', 'bin\performance.exe', 'bin\launcher.exe', 'bin\mirrordesktop.exe', 'bin\streamer.exe.config', 'bin\configure.exe.config', 'bin\performance.exe.config', 'bin\launcher.exe.config', 'bin\register_openxr_runtime.ps1', 'bin\flavour.txt',
          'bin\LibOVRRT64_1.dll', 'bin\virtualdesktop-openxr.dll', 'bin\virtualdesktop-openxr.json',
          'config\session.default.json', 'bench\littlest_tokyo.vab', 'bench\sakura_breeze.mp3', 'bench\xr_probe.exe', 'docs\README.txt', 'docs\THIRD_PARTY_NOTICES.md', 'docs\LICENSE.txt')
$ship += $persShip
$ship += $persFiles
$ship += Get-ChildItem "$pkg\profiles" -File -Filter *.json | ForEach-Object { "profiles\$($_.Name)" }
$ship += $licenses | ForEach-Object { "docs\licenses\$_" }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$fs = [IO.File]::Open($zip, [IO.FileMode]::Create)
$za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
foreach ($f in $ship) {
  if (Test-Path "$pkg\$f") { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, "$pkg\$f", ('AirFrameXR/' + $f.Replace('\', '/')), [IO.Compression.CompressionLevel]::Optimal) | Out-Null }
}
$za.Dispose(); $fs.Dispose()
Get-ChildItem $pkg -Recurse -File | Select-Object @{n='file';e={$_.FullName.Substring($pkg.Length + 1)}}, Length | Format-Table -AutoSize | Out-String -Width 200
$missing = @('AirFrameXR.exe', 'bin\alvr_host.exe', 'bin\streamer.exe', 'bin\configure.exe', 'bin\performance.exe', 'bin\launcher.exe', 'bin\launcher.exe.config', 'bin\streamer.exe.config', 'bin\register_openxr_runtime.ps1', 'bin\LibOVRRT64_1.dll', 'bin\virtualdesktop-openxr.dll', 'bin\virtualdesktop-openxr.json', 'bench\littlest_tokyo.vab', 'bench\xr_probe.exe', 'docs\THIRD_PARTY_NOTICES.md', 'docs\licenses\ALVR-LICENSE.txt') | Where-Object { -not (Test-Path "$pkg\$_") }
$rootFiles = @(Get-ChildItem $pkg -File | ForEach-Object { $_.Name })
if ($rootFiles.Count -ne 1 -or $rootFiles[0] -ne 'AirFrameXR.exe') { Write-Host "RESULT: FAILED the root must hold only AirFrameXR.exe, found: $($rootFiles -join ', ')"; exit 1 }
if ($missing) { Write-Host "RESULT: FAILED missing $($missing -join ', ')" } else { Write-Host "RESULT: OK $pkg ($zip)" }
