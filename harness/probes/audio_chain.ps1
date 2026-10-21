# Lab helper (harness/audio_chain_check.py), interactive session.
#   audio_chain.ps1 levels <audio_level.exe> <seconds> <pc name part> <sink name part>   -> three loopback levels measured at the same time
#   audio_chain.ps1 control <audio_level.exe> <tone.wav> <pc name part>                  -> a tone played to the default output: the speakers' loopback must hear it
#   audio_chain.ps1 closestreamer                                                         -> closes the streamer window, prints how long it took to exit
param([string]$Action, [string]$A = "", [string]$B = "", [string]$C = "", [string]$D = "")
function Start-Level($exe, $mode, $part, $sec, $file) { Start-Process -FilePath $exe -ArgumentList "$mode `"$part`" $sec" -PassThru -WindowStyle Hidden -RedirectStandardOutput $file }
if ($Action -eq "levels") {
  $t = [IO.Path]::GetTempPath()
  $jobs = @{ cable = (Start-Level $A loopback "CABLE Input" $B "$t\lv_cable.txt"); speakers = (Start-Level $A loopback $C $B "$t\lv_spk.txt"); sink = (Start-Level $A loopback $D $B "$t\lv_sink.txt") }
  $jobs.Values | ForEach-Object { [void]$_.WaitForExit(60000) }
  "cable: "    + (Get-Content "$t\lv_cable.txt" -ErrorAction SilentlyContinue)
  "speakers: " + (Get-Content "$t\lv_spk.txt" -ErrorAction SilentlyContinue)
  "sink: "     + (Get-Content "$t\lv_sink.txt" -ErrorAction SilentlyContinue)
}
elseif ($Action -eq "control") {
  $player = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList "-NoProfile -Command `"(New-Object Media.SoundPlayer '$B').PlayLooping(); Start-Sleep 12`""
  Start-Sleep -Seconds 2
  $t = [IO.Path]::GetTempPath()
  $p = Start-Level $A loopback $C 3 "$t\lv_ctl.txt"; [void]$p.WaitForExit(30000)
  "control_speakers: " + (Get-Content "$t\lv_ctl.txt" -ErrorAction SilentlyContinue)
  Stop-Process -Id $player.Id -Force
}
elseif ($Action -eq "closestreamer") {
  $p = Get-Process streamer -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $p) { "streamer: not running"; return }
  $sw = [Diagnostics.Stopwatch]::StartNew(); [void]$p.CloseMainWindow()
  if ($p.WaitForExit(30000)) { "streamer: exited in " + $sw.Elapsed.TotalSeconds.ToString("0.0", [Globalization.CultureInfo]::InvariantCulture) + " s" } else { "streamer: TIMEOUT"; $p.Kill() }
}
