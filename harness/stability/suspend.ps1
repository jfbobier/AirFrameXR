# Freezes a process (every thread, like a stuck driver call or a page-out would) for the stability harness, then resumes it.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File suspend.ps1 -Name alvr_host -After 40 -For 30 -Log <file>
param([string]$Name, [double]$After = 10, [double]$For = 5, [string]$Log = "")
Add-Type -Namespace W -Name N -MemberDefinition '[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h); [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);'
function Note([string]$m) { if ($Log -ne "") { Add-Content -Path $Log -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $m) } }
Start-Sleep -Milliseconds ([int]($After * 1000))
$ps = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
if ($ps.Count -eq 0) { Note "no process named $Name"; exit 1 }
foreach ($p in $ps) { $r = [W.N]::NtSuspendProcess($p.Handle); Note "suspended $Name pid $($p.Id) (status $r) for $For s" }
Start-Sleep -Milliseconds ([int]($For * 1000))
foreach ($p in $ps) { $r = [W.N]::NtResumeProcess($p.Handle); Note "resumed $Name pid $($p.Id) (status $r)" }
