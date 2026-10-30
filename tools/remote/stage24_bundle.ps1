# Assembles the bundle: the stock (release) package with the Black Box (the telemetry package) nested inside it as blackbox\, and blackbox_perf.exe next to AirFrameXR.exe in the root, one folder and one zip
# (docs/TELEMETRY_PLAN.md, "Architecture"). Inputs, made by `run.py build ... package` once per flavour and stashed by harness/bundle.py:
#   out\stash\<stockname>   the stock package folder (release build, public or personal)
#   out\stash\<debugname>   the telemetry package folder (the Black Box programs inside its bin\)
# Output: out\bundle\<name>\AirFrameXR (folder) and out\bundle\<name>\AirFrameXR-<version>-bundle[-personal].zip. Nothing is deleted: a new run writes to a new name.
param($stock, $debug, $name, $zipname)
$ErrorActionPreference = 'Continue'
$root = $env:AIRFRAMEXR_ROOT
$st = "$root\out\stash\$stock"; $dbg = "$root\out\stash\$debug"
$dst = "$root\out\bundle\$name\AirFrameXR"
if ((Test-Path $dst)) { Write-Host "FAILED: $dst exists (use a new name)"; exit 1 }
New-Item -ItemType Directory -Force $dst | Out-Null
robocopy $st $dst /E /XD logs /XF session.json /NFL /NDL /NJH /NJS /NP | Out-Null
# the Black Box install: its own bin\, docs and licences; not its own scene assets (bench\), logs or the settings (it copies the stock ones when it starts)
$nest = "$dst\blackbox"
robocopy $dbg $nest /E /XD logs bench profiles /XF session.json airframexr.json /NFL /NDL /NJH /NJS /NP | Out-Null
if (-not (Test-Path "$root\out\bench\blackbox_perf.exe")) { Write-Host "FAILED: out\bench\blackbox_perf.exe missing (run.py build bench)"; exit 1 }
Copy-Item "$root\out\bench\blackbox_perf.exe" "$dst\blackbox_perf.exe" -Force
foreach ($f in 'alvr_host.exe', 'LibOVRRT64_1.dll', 'blackbox_recorder.exe', 'blackbox_analyzer.exe', 'perfcore.dll') { if (-not (Test-Path "$nest\bin\$f")) { Write-Host "FAILED: $nest\bin\$f missing"; exit 1 } }
if ((Get-Content "$nest\bin\flavour.txt") -ne 'debug') { Write-Host "FAILED: the nested package is not the telemetry build"; exit 1 }
if ((Get-Content "$dst\bin\flavour.txt") -ne 'release') { Write-Host "FAILED: the stock package is not the release build"; exit 1 }
$zip = "$root\out\bundle\$name\$zipname"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$fs = [IO.File]::Open($zip, [IO.FileMode]::Create)
$za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
$base = (Split-Path $dst -Parent) + '\'
foreach ($f in Get-ChildItem $dst -Recurse -File) {
    $rel = $f.FullName.Substring($base.Length).Replace('\', '/')
    $e = $za.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
    $es = $e.Open(); $in = [IO.File]::OpenRead($f.FullName); $in.CopyTo($es); $in.Dispose(); $es.Dispose()
}
$za.Dispose(); $fs.Dispose()
Write-Host "zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Write-Host 'RESULT: OK'
