# Lab helper (harness/process_loopback_probe.py), interactive session: plays a tone from a separate powershell process, then runs the probe in each mode.
param([string]$Probe, [string]$Wav)
$player = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList "-NoProfile -Command `"(New-Object Media.SoundPlayer '$Wav').PlayLooping(); Start-Sleep 40`""
Start-Sleep -Seconds 3
$explorer = (Get-Process explorer | Select-Object -First 1).Id
"player pid $($player.Id), this shell $PID, explorer $explorer"
"include_player: " + (& $Probe include $player.Id 3)
"include_other: " + (& $Probe include $PID 3)
"exclude_other: " + (& $Probe exclude $explorer 3)
Stop-Process -Id $player.Id -Force
