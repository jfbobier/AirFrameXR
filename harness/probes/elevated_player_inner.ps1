# runs ELEVATED (started with -Verb RunAs by elevated_audio.ps1): records its pid and integrity level, then plays the tone for 45 s
param([string]$Base)
$g = (whoami /groups) -join ' '
$l = if ($g -match 'S-1-16-12288') { 'HIGH integrity (elevated)' } elseif ($g -match 'S-1-16-8192') { 'MEDIUM integrity (normal)' } else { 'other integrity' }
"$PID|$l" | Out-File "$Base\elev_info.txt" -Encoding ascii
(New-Object Media.SoundPlayer "$Base\tone.wav").PlayLooping()
Start-Sleep 45
