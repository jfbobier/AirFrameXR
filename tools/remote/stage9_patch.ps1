# Idempotently applies the AirFrameXR patches to the builder's ALVR v20.14.1 checkout:
#   loopback-client.patch   mock client / harness loopback
#   client-stats-log.patch  server_core logs the headset's raw per-frame statistics (host: client_frames.csv)
#   client-caps-log.patch   server_core logs the capabilities the headset advertised at the handshake (host: client_capabilities event)
#   audio-trace.patch       alvr_audio: a trace hook in the capture callback (host telemetry.rs, debug flavour)
$ErrorActionPreference = 'Continue'
$root = if ($env:AIRFRAMEXR_ROOT) { $env:AIRFRAMEXR_ROOT } else { "$env:USERPROFILE\openxr" }
Set-Location "$root\src\ALVR-v20.14.1"
$ok = $true
foreach ($name in 'loopback-client.patch', 'client-stats-log.patch', 'client-caps-log.patch', 'audio-trace.patch', 'audio-process-loopback.patch', 'client-audio-device.patch') {
    $patch = "$root\_tmp\$name"
    if (-not (Test-Path $patch)) { Write-Host "patch: $name missing in _tmp"; $ok = $false; continue }
    git apply --reverse --check $patch 2>$null
    if ($LASTEXITCODE -eq 0) { Write-Host "patch: $name already applied"; continue }
    # a checkout patched before the 1.0 rename carries the old names (VISIONALVR_ environment variables...): undo that version, the current one is applied below
    $legacy = "$patch.legacy"
    $text = [IO.File]::ReadAllText($patch).Replace('AIRFRAMEXR', 'VISIONALVR').Replace('AirFrameXR', 'VisionALVR').Replace('airframexr', 'visionalvr')
    [IO.File]::WriteAllText($legacy, $text, (New-Object Text.UTF8Encoding($false)))
    git apply --reverse --check $legacy 2>$null
    if ($LASTEXITCODE -eq 0) { git apply --reverse $legacy; Write-Host "patch: $name pre-rename version removed" }
    [IO.File]::Delete($legacy)
    git apply --check $patch
    if ($LASTEXITCODE -ne 0) { Write-Host "patch: $name DOES NOT APPLY"; $ok = $false; continue }
    git apply $patch
    Write-Host "patch: $name applied"
}
if (-not $ok) { exit 1 }
