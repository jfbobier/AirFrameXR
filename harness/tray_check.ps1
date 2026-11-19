# Runs ON the PC (called by tray_check.py). A test folder $wd holds a copy of bin\ (real alvr_host.exe, the new tray.exe) and a STAND-IN streamer.exe (sleeps).
# A 100 ms sampler records, for that folder only: stand-in streamers running, `alvr_host --discover` children running. Phases then drive the tray
# the way the field does (a headset announcing itself, a streamer started by someone else) and the verdicts are computed from the samples.
param($wd, $hostname, $proto, $cooldown, $limit = 0)
$ErrorActionPreference = 'Stop'
$bin = "$wd\bin"
$samples = New-Object System.Collections.Generic.List[object]
$t0 = [Diagnostics.Stopwatch]::StartNew()
function Snap {
    $ps = @(Get-CimInstance Win32_Process -Filter "Name='streamer.exe' OR Name='alvr_host.exe'" | Where-Object { $_.ExecutablePath -like "$wd\*" })
    $s = @($ps | Where-Object { $_.Name -eq 'streamer.exe' }).Count
    $d = @($ps | Where-Object { $_.Name -eq 'alvr_host.exe' -and $_.CommandLine -match '--discover' }).Count
    [pscustomobject]@{ t = [math]::Round($t0.Elapsed.TotalSeconds, 2); streamers = $s; discover = $d }
}
function Watch($sec) { $end = $t0.Elapsed.TotalSeconds + $sec; while ($t0.Elapsed.TotalSeconds -lt $end) { $samples.Add((Snap)); Start-Sleep -Milliseconds 100 } }
function Mark($name) { $script:marks += ,@($name, [math]::Round($t0.Elapsed.TotalSeconds, 2)) }
$marks = @()
$results = @()
function Verdict($name, $ok, $detail) { $script:results += [pscustomobject]@{ name = $name; ok = [bool]$ok; detail = $detail } }
function Between($a, $b) { $samples | Where-Object { $_.t -ge $a -and $_.t -le $b } }
function Announcer($name, $from = '127.0.0.2') {
    Start-Job -ArgumentList $name, $proto, $from -ScriptBlock {
        param($h, $p, $f)
        $pk = New-Object byte[] 56
        [Text.Encoding]::ASCII.GetBytes('ALVR').CopyTo($pk, 0)
        [BitConverter]::GetBytes([uint64]$p).CopyTo($pk, 16)
        [Text.Encoding]::ASCII.GetBytes($h).CopyTo($pk, 24)
        $u = New-Object System.Net.Sockets.UdpClient (New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse($f)), 0)
        while ($true) { $u.Send($pk, 56, '127.0.0.1', 9943) | Out-Null; Start-Sleep -Milliseconds 500 }
    }
}
function StopStandIn { Get-CimInstance Win32_Process -Filter "Name='streamer.exe'" | Where-Object { $_.ExecutablePath -like "$wd\*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
function WaitFor($cond, $sec) { $end = $t0.Elapsed.TotalSeconds + $sec; while ($t0.Elapsed.TotalSeconds -lt $end) { $samples.Add((Snap)); if (& $cond $samples[$samples.Count - 1]) { return $true }; Start-Sleep -Milliseconds 100 }; $false }

# the tray is started by the caller (an interactive task: it needs a desktop); wait for it
$tray = $null
for ($i = 0; $i -lt 50 -and -not $tray; $i++) { $tray = Get-Process tray -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$wd\*" }; Start-Sleep -Milliseconds 200 }
if (-not $tray) { Write-Output (@{ error = 'tray.exe did not start' } | ConvertTo-Json -Compress); exit 2 }

if ($limit -gt 0) {
    # the discovery process dies by itself after its time limit (3600 s shipped, $limit s here): the tray must start a new one, any number of times, and still react
    $ok = WaitFor { param($s) $s.discover -eq 1 } 8
    $gaps = 0; $prev = 1; $end = $t0.Elapsed.TotalSeconds + $limit * 3.5
    while ($t0.Elapsed.TotalSeconds -lt $end) { $x = Snap; $samples.Add($x); if ($prev -eq 1 -and $x.discover -eq 0) { $gaps++ }; $prev = $x.discover; Start-Sleep -Milliseconds 100 }
    Verdict 'discovery_expiry_renewed' ($ok -and $gaps -ge 2 -and $prev -eq 1) "the discovery child ended and was replaced $gaps times in $([math]::Round($limit * 3.5)) s, one running at the end: $prev"
    $j = Announcer $hostname
    $started = WaitFor { param($s) $s.streamers -ge 1 } 12
    Stop-Job $j; Remove-Job $j
    Verdict 'still_detects_after_expiry' $started 'the paired headset starts the streamer after several expiries'
    $samples | Export-Csv "$wd\samples.csv" -NoTypeInformation
    @{ results = $results; marks = $marks } | ConvertTo-Json -Depth 4 -Compress | Out-File "$wd\verdicts.json" -Encoding ascii
    Write-Output 'DONE'
    exit 0
}

# A: idle, no streamer: exactly one discovery child
$ok = WaitFor { param($s) $s.discover -eq 1 } 8
Watch 3
$a = Between ($t0.Elapsed.TotalSeconds - 3) $t0.Elapsed.TotalSeconds
Verdict 'idle_watches' ($ok -and -not ($a | Where-Object { $_.discover -ne 1 -or $_.streamers -ne 0 })) "discover children: $(($a | Select-Object -ExpandProperty discover | Sort-Object -Unique) -join ',')"

# E: another headset announces: nothing starts
Mark 'other_headset'
$j = Announcer 'some-other-headset' '127.0.0.3'
Watch 6
Stop-Job $j; Remove-Job $j
$e = Between ($marks[-1][1]) $t0.Elapsed.TotalSeconds
Verdict 'other_headset_ignored' (-not ($e | Where-Object { $_.streamers -ne 0 })) "max streamers $(($e | Measure-Object streamers -Maximum).Maximum)"

# D: the paired headset announces (and keeps announcing): the tray starts the streamer once, discovery stops, and nothing is started a second time
Mark 'paired_announce'
$j = Announcer $hostname
$started = WaitFor { param($s) $s.streamers -ge 1 } 12
$tStart = $t0.Elapsed.TotalSeconds
$off = WaitFor { param($s) $s.discover -eq 0 } 3
$tOff = $t0.Elapsed.TotalSeconds
Watch 10
Stop-Job $j; Remove-Job $j
$d = Between $tStart $t0.Elapsed.TotalSeconds
$late = $d | Where-Object { $_.t -gt $tStart + 1.5 -and $_.discover -ne 0 }
Verdict 'paired_starts_streamer' $started "streamer seen $([math]::Round($tStart - $marks[-1][1], 1)) s after the first announce"
Verdict 'start_is_single' (-not ($d | Where-Object { $_.streamers -gt 1 })) "max streamers $(($d | Measure-Object streamers -Maximum).Maximum) while announces continued"
Verdict 'idle_while_streamer_runs' ($off -and -not $late) "discovery child gone $([math]::Round($tOff - $tStart, 1)) s after the streamer appeared; $(@($late).Count) later samples with a child"

# C: the streamer closes: cooldown (no discovery, no restart despite announces), then watching again
Mark 'streamer_closed'
$j = Announcer $hostname
StopStandIn
$tc = $t0.Elapsed.TotalSeconds
Watch ([math]::Max(1, $cooldown - 2))
$c1 = Between ($tc + 0.5) $t0.Elapsed.TotalSeconds
Verdict 'cooldown_no_restart' (-not ($c1 | Where-Object { $_.streamers -ne 0 -or $_.discover -ne 0 })) "during the $cooldown s cooldown: max streamers $(($c1 | Measure-Object streamers -Maximum).Maximum), max discover $(($c1 | Measure-Object discover -Maximum).Maximum)"
Stop-Job $j; Remove-Job $j
$resumed = WaitFor { param($s) $s.discover -eq 1 } ($cooldown + 6)
Verdict 'resumes_after_cooldown' $resumed "discovery child back $([math]::Round($t0.Elapsed.TotalSeconds - $tc, 1)) s after the close"

# B: a streamer started by someone else (the launcher): the discovery child must go before the streamer's host would bind UDP 9943
Mark 'external_streamer'
$null = Start-Process "$bin\streamer.exe" -WorkingDirectory $bin
$tx = $t0.Elapsed.TotalSeconds
$gone = WaitFor { param($s) $s.discover -eq 0 } 5
$tg = $t0.Elapsed.TotalSeconds
Watch 8
$b = Between $tx $t0.Elapsed.TotalSeconds
$late = $b | Where-Object { $_.t -gt $tg -and $_.discover -ne 0 }
Verdict 'external_start_stops_discovery' ($gone -and ($tg - $tx) -lt 1.5 -and -not $late) "discovery child gone $([math]::Round($tg - $tx, 2)) s after the streamer appeared (limit 1.5 s); $(@($late).Count) later samples with a child"
StopStandIn

# F: the tray is killed hard (no Exit): its discovery child must not be left holding UDP 9943
$null = WaitFor { param($s) $s.discover -eq 1 } ($cooldown + 6)
Mark 'tray_killed'
Get-Process tray | Where-Object { $_.Path -like "$wd\*" } | Stop-Process -Force
$tk = $t0.Elapsed.TotalSeconds
$orphan = -not (WaitFor { param($s) $s.discover -eq 0 } 5)
Verdict 'killed_tray_leaves_no_child' (-not $orphan) "discovery child gone $([math]::Round($t0.Elapsed.TotalSeconds - $tk, 1)) s after the tray was killed"

$samples | Export-Csv "$wd\samples.csv" -NoTypeInformation
@{ results = $results; marks = $marks } | ConvertTo-Json -Depth 4 -Compress | Out-File "$wd\verdicts.json" -Encoding ascii
Write-Output 'DONE'
