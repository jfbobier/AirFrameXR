# Samples memory once a second for the stability harness: per process private bytes, working set, handles (the chain's processes) and per process
# dedicated GPU memory (Windows' "GPU Process Memory" counters), plus the system's free RAM and the GPU's total used VRAM (nvidia-smi).
# Usage: powershell -NoProfile -File memsample.ps1 -Out <csv> [-Names alvr_host,xr_probe,alvr_client_headless,vram_ballast] [-StopFile <file>]
# CSV: t_s,kind,name,pid,private_mb,ws_mb,handles,gpu_mb   (kind = proc | sys; sys rows: name=ram_free_mb in private_mb, gpu_mb = total VRAM used)
param([string]$Out, [string[]]$Names = @("alvr_host", "xr_probe", "alvr_client_headless", "vram_ballast", "mirrordesktop"), [string]$StopFile = "")
$ErrorActionPreference = "SilentlyContinue"
Set-Content -Path $Out -Value "t_s,kind,name,pid,private_mb,ws_mb,handles,gpu_mb"
$t0 = Get-Date
while ($true) {
    if ($StopFile -ne "" -and (Test-Path $StopFile)) { break }
    $t = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
    $gpu = @{}
    $c = Get-Counter -Counter "\GPU Process Memory(*)\Dedicated Usage" -ErrorAction SilentlyContinue
    if ($c) {
        foreach ($s in $c.CounterSamples) {
            if ($s.InstanceName -match "^pid_(\d+)_") { $p = [int]$Matches[1]; $gpu[$p] = [double]$gpu[$p] + $s.CookedValue / 1MB }
        }
    }
    $lines = @()
    foreach ($n in $Names) {
        foreach ($p in (Get-Process -Name $n)) {
            $g = [math]::Round([double]$gpu[$p.Id], 1)
            $lines += ("{0},proc,{1},{2},{3},{4},{5},{6}" -f $t, $p.ProcessName, $p.Id, [math]::Round($p.PrivateMemorySize64 / 1MB, 1), [math]::Round($p.WorkingSet64 / 1MB, 1), $p.HandleCount, $g)
        }
    }
    $os = Get-CimInstance Win32_OperatingSystem
    $used = (& nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits | Select-Object -First 1)
    $lines += ("{0},sys,ram_free_mb,0,{1},0,0,{2}" -f $t, [math]::Round($os.FreePhysicalMemory / 1024, 0), $used)
    Add-Content -Path $Out -Value $lines
    Start-Sleep -Milliseconds 800
}
