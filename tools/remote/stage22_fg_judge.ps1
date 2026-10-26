# Runs the frame generation judge (docs/FRAME_GENERATION_JUDGE.md) on the builder: alvr_host --fg-judge, events to a log file, results in runs\<Label>\out.
param([string]$Label = 'fgj', [string]$Eye = '1032x1104', [int]$Starts = 6, [string]$Gaps = '22.2,33.3,44.4', [string]$Speeds = '1,8,24')
$ErrorActionPreference = 'Continue'
$root = if ($env:AIRFRAMEXR_ROOT) { $env:AIRFRAMEXR_ROOT } else { "$env:USERPROFILE\openxr" }
$run = "$root\runs\$Label"
New-Item -ItemType Directory -Force "$run\cfg" | Out-Null
New-Item -ItemType Directory -Force "$run\out" | Out-Null
$Gaps = $Gaps.Replace('+', ',')
$Speeds = $Speeds.Replace('+', ',')   # a comma list is an array to PowerShell: the caller joins with +
$exe = "$root\src\ALVR-v20.14.1\target\release\alvr_host.exe"
$args = "--config-dir $run\cfg --session $run\session.json --force-session --fg-judge $run\out --bench-scene $root\bench\littlest_tokyo.vab --fgj-eye $Eye --fgj-starts $Starts --fgj-gaps $Gaps --fgj-speeds $Speeds"
$t = Get-Date
& cmd /c "`"$exe`" $args > `"$run\stdout.jsonl`" 2> `"$run\stderr.txt`""
Write-Host "exit $LASTEXITCODE after $([int]((Get-Date) - $t).TotalSeconds) s"
Write-Host "RESULT: DONE"
