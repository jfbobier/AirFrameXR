@echo off
rem Checks Windows Firewall for AirFrameXR / ALVR and proposes a clean-up. Reads first; changes nothing without your Y.
rem Put it anywhere (next to AirFrameXR.exe is best: it then knows which alvr_host.exe is yours) and double-click it.
rem It asks for administrator rights because removing or adding a rule needs them.
setlocal
set "BATDIR=%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Asking for administrator rights ^(only needed to remove or add a rule^)...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t = [IO.File]::ReadAllText('%~f0'); $i = $t.LastIndexOf('#' + 'PS#'); Invoke-Expression $t.Substring($i + 4)"
echo.
pause
exit /b
#PS#
$ErrorActionPreference = 'SilentlyContinue'
function Ask($q) { if ($env:FWCHECK_ANSWERS) { $a = @($env:FWCHECK_ANSWERS -split ','); $env:FWCHECK_ANSWERS = ($a | Select-Object -Skip 1) -join ','; Write-Host "$q $($a[0])"; return $a[0] } else { return (Read-Host $q) } }   # FWCHECK_ANSWERS: for the test harness only

Write-Host ''
Write-Host '=== Windows Firewall rules of AirFrameXR / ALVR (alvr_host.exe) ===' -ForegroundColor Cyan
Write-Host 'A rule lets ONE exact alvr_host.exe in. Moving the folder needs a new rule and Windows keeps the old ones; every "Allow" prompt adds a TCP and a UDP rule.'
Write-Host 'This only reads until you answer. Nothing is changed without a Y.'
Write-Host ''

# which alvr_host.exe is "the current one": the package this file sits in, else you choose
$dir = $env:BATDIR
$exe = @("$dir\bin\alvr_host.exe", "$dir\alvr_host.exe", "$dir\..\bin\alvr_host.exe") | Where-Object { Test-Path $_ } | ForEach-Object { (Resolve-Path $_).Path } | Select-Object -First 1

$apps = @(Get-NetFirewallApplicationFilter | Where-Object { $_.Program -match 'alvr_host\.exe$' })
$rules = @($apps | ForEach-Object {
    $r = $_ | Get-NetFirewallRule; $p = $r | Get-NetFirewallPortFilter
    [pscustomobject]@{ Name = $r.Name; Display = $r.DisplayName; Dir = [string]$r.Direction; Action = [string]$r.Action; Profile = [string]$r.Profile; Proto = [string]$p.Protocol; On = ([string]$r.Enabled -eq 'True'); Program = $_.Program; Exists = ((Test-Path $_.Program) -or -not (Test-Path ([IO.Path]::GetPathRoot($_.Program)))); Status = ''; Remove = $false }
})
$active = @(Get-NetConnectionProfile | ForEach-Object { [string]$_.NetworkCategory })

if (-not $exe) {
    $existing = @($rules | Where-Object { $_.Exists -and (Test-Path $_.Program) } | Select-Object -ExpandProperty Program -Unique)
    Write-Host 'This file is not inside a AirFrameXR folder. Which alvr_host.exe is the one you use?'
    for ($i = 0; $i -lt $existing.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $existing[$i]) }
    $a = Ask 'Number from the list, or paste the full path of your alvr_host.exe (Enter = none)'
    if ($a -match '^\d+$' -and [int]$a -ge 1 -and [int]$a -le $existing.Count) { $exe = $existing[[int]$a - 1] } elseif ($a -and (Test-Path $a)) { $exe = (Resolve-Path $a).Path }
}
if ($exe) { Write-Host "The current alvr_host.exe: $exe" -ForegroundColor Green } else { Write-Host 'No current alvr_host.exe chosen: only stale and duplicate rules will be judged.' -ForegroundColor Yellow }
Write-Host ("Networks in use: {0}" -f $(if ($active) { $active -join ', ' } else { 'unknown' }))
Write-Host ''

function Applies($profile) { if (-not $profile -or $profile -match '^(Any|All)$' -or -not $active) { return $true }; foreach ($c in $active) { $k = $c -replace '^DomainAuthenticated$', 'Domain'; if ($profile -match $k) { return $true } }; return $false }
function Eff($r) { return ($r.On -and (Applies $r.Profile)) }
$mine = @($rules | Where-Object { $exe -and $_.Dir -eq 'Inbound' -and $_.Program -ieq $exe })
$allows = @($mine | Where-Object { $_.Action -eq 'Allow' -and (Eff $_) })
$block = @($mine | Where-Object { $_.Action -eq 'Block' -and (Eff $_) }).Count -gt 0
$keeper = ($allows | Where-Object { $_.Proto -eq 'Any' } | Select-Object -First 1); if (-not $keeper) { $keeper = $allows | Select-Object -First 1 }
foreach ($r in $rules) {
    $isMine = $exe -and $r.Dir -eq 'Inbound' -and $r.Program -ieq $exe
    if ($isMine) {
        if ($r.Action -eq 'Block') { $r.Status = 'BLOCKS the current exe'; $r.Remove = $r.On }
        elseif (-not $r.On) { $r.Status = 'current exe, switched off' }
        elseif (-not (Applies $r.Profile)) { $r.Status = "current exe, but not for the network in use" }
        elseif ($r -eq $keeper) { $r.Status = 'IN PLACE: lets the current exe in' }
        elseif ($keeper -and $keeper.Proto -eq 'Any') { $r.Status = 'redundant: the in-place rule covers all protocols'; $r.Remove = $true }
        elseif (@($allows | Where-Object { $_ -ne $r -and $_.Proto -eq $r.Proto -and $allows.IndexOf($_) -lt $allows.IndexOf($r) }).Count -gt 0) { $r.Status = "duplicate of an earlier $($r.Proto) rule"; $r.Remove = $true }
        else { $r.Status = "current exe ($($r.Proto))" }
    }
    elseif (-not $r.Exists) { $r.Status = 'STALE: the file no longer exists'; $r.Remove = $true }
    else { $r.Status = 'another copy that still exists (left alone)' }
}

if ($rules.Count -eq 0) { Write-Host 'No firewall rule names an alvr_host.exe.' -ForegroundColor Yellow }
$i = 0
foreach ($r in $rules | Sort-Object @{e={$_.Program}}, Proto) {
    $i++; $col = if ($r.Remove) { 'Red' } elseif ($r.Status -like 'IN PLACE*') { 'Green' } else { 'Gray' }
    Write-Host ("{0,2}. {1}" -f $i, $r.Display) -ForegroundColor $col
    Write-Host ("      {0} {1}  protocol {2}  network {3}  enabled {4}" -f $r.Dir, $r.Action, $r.Proto, $r.Profile, $r.On) -ForegroundColor $col
    Write-Host ("      {0}" -f $r.Program) -ForegroundColor $col
    Write-Host ("      -> {0}{1}" -f $r.Status, $(if ($r.Remove) { '   [proposed for removal]' } else { '' })) -ForegroundColor $col
}
Write-Host ''
$state = if ($block) { 'BLOCKED' } elseif ($allows.Count -gt 0) { 'ALLOWED' } elseif ($exe) { 'NO RULE' } else { 'not judged' }
Write-Host ("Current exe: {0}" -f $state) -ForegroundColor $(if ($state -eq 'ALLOWED') { 'Green' } elseif ($state -eq 'not judged') { 'Yellow' } else { 'Red' })

$del = @($rules | Where-Object { $_.Remove })
if ($del.Count -gt 0) {
    Write-Host ''
    $a = Ask ("Remove the {0} proposed rule(s)?  [Y] all   [O] one by one   [N] none" -f $del.Count)
    if ($a -match '^[Yy]') { $pick = $del }
    elseif ($a -match '^[Oo]') { $pick = @($del | Where-Object { (Ask ("  Remove '{0}' ({1}, {2})?  [Y/N]" -f $_.Display, $_.Proto, $_.Status)) -match '^[Yy]' }) }
    else { $pick = @() }
    foreach ($r in $pick) { Remove-NetFirewallRule -Name $r.Name -ErrorAction SilentlyContinue; Write-Host ("  removed: {0} ({1})" -f $r.Display, $r.Program) -ForegroundColor Green }
}
if ($exe -and ($state -eq 'NO RULE' -or $state -eq 'BLOCKED')) {
    Write-Host ''
    $a = Ask 'Add one rule that lets the current alvr_host.exe in (all protocols, all networks)?  [Y/N]'
    if ($a -match '^[Yy]') {
        New-NetFirewallRule -DisplayName 'AirFrameXR (alvr_host)' -Direction Inbound -Action Allow -Program $exe -Profile Any -Enabled True | Out-Null
        Write-Host '  added: AirFrameXR (alvr_host)' -ForegroundColor Green
    }
}
Write-Host ''
Write-Host 'Done. Run this again to see the result.'
