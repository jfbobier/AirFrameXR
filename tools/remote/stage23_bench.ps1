# Builds the Bench flavour's own binaries (docs/TELEMETRY_PLAN.md): perfcore.dll (managed library), perfreport.exe and perfprobe.exe (command lines over it), blackbox_recorder.exe (the Black Box Recorder: the stock streamer sources + tools/gui/BenchRecorder.cs) and blackbox_analyzer.exe (tools/gui/PerfAnalyze.cs), both with tools/gui/BlackBoxBrand.cs.
# Sources: _tmp\bench (uploaded by `run.py build bench`). Output: out\bench.
$ErrorActionPreference = 'Continue'
$root = if ($env:AIRFRAMEXR_ROOT) { $env:AIRFRAMEXR_ROOT } else { "$env:USERPROFILE\openxr" }
$src = "$root\_tmp\bench"
$out = "$root\out\bench"
$csc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Force $out | Out-Null
Set-Content -Path "$out\build.log" -Value '' -Encoding UTF8
$refs = 'System.dll', 'System.Core.dll' | ForEach-Object { "/reference:$fw\$_" }
$common = @('/nologo', '/noconfig', '/platform:x64', '/optimize+', '/langversion:latest', '/nowarn:1701,1702')
$ok = $true
$gui = "$src\gui"
$guiRefs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll' | ForEach-Object { "/reference:$fw\$_" }
$steps = @(
    @{ name = 'perfcore.dll'; args = @('/target:library') + $refs + @("/out:$out\perfcore.dll", "$src\PerfCore.cs", "$src\PerfReport.cs", "$src\PerfResources.cs") },
    @{ name = 'perfreport.exe'; args = @('/target:exe') + $refs + @("/reference:$out\perfcore.dll", "/out:$out\perfreport.exe", "$src\PerfReportCli.cs") },
    @{ name = 'blackbox_recorder.exe'; args = @('/target:winexe') + $guiRefs + @("/reference:$out\perfcore.dll", "/win32icon:$gui\res\blackbox-windows.ico", "/win32manifest:$gui\app.manifest", "/resource:$gui\res\logo_640.png,logo_640.png", "/resource:$gui\res\chroma_passthrough.png,chroma_passthrough.png", "/resource:$gui\res\blackbox_128.png,blackbox_128.png", "/out:$out\blackbox_recorder.exe", "$gui\Common.cs", "$gui\PipelineModel.cs", "$gui\AirFrameXR.cs", "$gui\FindHeadset.cs", "$gui\AudioRoute.cs", "$gui\BlackBoxBrand.cs", "$gui\BenchRecorder.cs") },
    @{ name = 'blackbox_analyzer.exe'; args = @('/target:winexe') + $guiRefs + @("/reference:$out\perfcore.dll", "/win32icon:$gui\res\blackbox-windows.ico", "/win32manifest:$gui\app.manifest", "/resource:$gui\res\logo_640.png,logo_640.png", "/resource:$gui\res\chroma_passthrough.png,chroma_passthrough.png", "/resource:$gui\res\blackbox_128.png,blackbox_128.png", "/out:$out\blackbox_analyzer.exe", "$gui\Common.cs", "$gui\BlackBoxBrand.cs", "$gui\PerfAnalyze.cs") },
    @{ name = 'perfprobe.exe'; args = @('/target:exe') + $refs + @("/reference:$out\perfcore.dll", "/out:$out\perfprobe.exe", "$src\PerfProbeCli.cs") }
)
foreach ($s in $steps) {
    $o = & $csc @common @($s.args) 2>&1
    ($o | Out-String) | Add-Content -Path "$out\build.log" -Encoding UTF8
    $o | ForEach-Object { Write-Host ("$_" -replace "[^\x20-\x7E]", '?') }
    if ($LASTEXITCODE -ne 0) { $ok = $false; Write-Host "FAILED: $($s.name)" } else { Write-Host "built $out\$($s.name)" }
}
if ($ok) { Write-Host 'RESULT: OK' } else { Write-Host 'RESULT: FAILED' }
foreach ($e in 'blackbox_recorder.exe', 'blackbox_analyzer.exe') { Copy-Item "$gui\app.exe.config" "$out\$e.config" -Force }   # WinForms' per-monitor DPI mode, like the other tools
