# Lab helper (harness/elevated_audio_check.py), interactive normal-token session: starts a tone player ELEVATED (the UAC prompt on the builder must be accepted), then runs process
# loopback with this normal token: exclude-everything-but-an-unrelated-process, include-the-elevated-player, include-an-unrelated-process, and the speakers' device loopback.
param([string]$Base)
Remove-Item "$Base\elev_info.txt" -ErrorAction SilentlyContinue
try { $p = Start-Process powershell -Verb RunAs -PassThru -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$Base\elevated_player_inner.ps1`" `"$Base`"" -ErrorAction Stop } catch { "could not start elevated: " + $_.Exception.Message; return }
$t = 0; while (-not (Test-Path "$Base\elev_info.txt") -and $t -lt 90) { Start-Sleep 1; $t++ }   # time to accept the prompt
if (-not (Test-Path "$Base\elev_info.txt")) { "no elevated player after 90 s (prompt not accepted?)"; return }
Start-Sleep 3
$info = (Get-Content "$Base\elev_info.txt") -split '\|'
$epid = [int]$info[0].Trim()
"player_pid: $epid"; "player_integrity: " + $info[1].Trim()
$g = (whoami /groups) -join ' '; "this_shell_integrity: " + $(if ($g -match 'S-1-16-12288') { 'HIGH integrity (elevated)' } elseif ($g -match 'S-1-16-8192') { 'MEDIUM integrity (normal)' } else { 'other integrity' })
$e = (Get-Process explorer | Select-Object -First 1).Id
"exclude_other: " + (& "$Base\proc_loopback.exe" exclude $e 3)
"include_player: " + (& "$Base\proc_loopback.exe" include $epid 3)
"include_other: " + (& "$Base\proc_loopback.exe" include $e 3)
"speakers_loopback: " + (& "$Base\audio_level.exe" loopback Realtek 3)
