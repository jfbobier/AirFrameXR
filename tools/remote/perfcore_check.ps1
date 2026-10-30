# Runs perfprobe.exe on the builder for harness/perfcore_check.py: an L0 capture, an L1 capture during which a watched settings file changes, and an L1 capture with no process names.
param($rid)
$root = "$env:USERPROFILE\$(Split-Path $env:AIRFRAMEXR_ROOT -Leaf)"
$b = "$env:AIRFRAMEXR_ROOT\out\bench"
$r = "$env:AIRFRAMEXR_ROOT\runs\$rid"
New-Item -ItemType Directory -Force "$r\cfg" | Out-Null
Set-Content "$r\cfg\airframexr.json" '{"a":1}'
Set-Content "$r\cfg\session.json" '{"b":2}'
$p0 = Start-Process "$b\perfprobe.exe" -ArgumentList @('capture', "$r\l0", 'L0', '6') -PassThru -WindowStyle Hidden
$p0.WaitForExit()
$p1 = Start-Process "$b\perfprobe.exe" -ArgumentList @('capture', "$r\l1", 'L1', '12', 'explorer,dwm', "$r\cfg") -PassThru -WindowStyle Hidden
Start-Sleep 8
Set-Content "$r\cfg\airframexr.json" '{"a":2}'
$p1.WaitForExit()
$p2 = Start-Process "$b\perfprobe.exe" -ArgumentList @('capture', "$r\l1_nonames", 'L1', '5') -PassThru -WindowStyle Hidden
$p2.WaitForExit()
"exit codes: $($p0.ExitCode) $($p1.ExitCode) $($p2.ExitCode)" | Set-Content "$r\codes.txt"
