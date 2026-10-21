# Builds streamer.exe, configure.exe, performance.exe, launcher.exe (all go to the package's bin\) and VisionALVR.exe (the stub that starts bin\launcher.exe: the only file in the package root) (C# WinForms, .NET Framework 4.8 = part of Windows) with Visual Studio's Roslyn compiler
# against the framework's own assemblies (no SDK needed). Sources: _tmp\gui (uploaded by `run.py build gui`). Output: out\gui.
$ErrorActionPreference = 'Continue'
$root = if ($env:VISIONALVR_ROOT) { $env:VISIONALVR_ROOT } else { "$env:USERPROFILE\openxr" }
$src = "$root\_tmp\gui"
$out = "$root\out\gui"
$csc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Force $out | Out-Null
Set-Content -Path "$out\build.log" -Value '' -Encoding UTF8
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll' | ForEach-Object { "/r:$fw\$_" }
$common = @('/nologo', '/noconfig', '/target:winexe', '/platform:x64', '/optimize+', '/langversion:latest', '/nowarn:1701,1702',
            "/win32icon:$src\res\visionalvr.ico", "/win32manifest:$src\app.manifest", "/resource:$src\res\logo_640.png,logo_640.png", "/resource:$src\res\chroma_passthrough.png,chroma_passthrough.png") + $refs
$ok = $true
foreach ($t in @(@{ exe = 'streamer.exe'; files = @("$src\Common.cs", "$src\VisionALVR.cs", "$src\FindHeadset.cs", "$src\AudioRoute.cs") }, @{ exe = 'configure.exe'; files = @("$src\Common.cs", "$src\AudioRoute.cs", "$src\Configure.cs", "$src\SettingsPanel.cs", "$src\Reference.cs", "$src\TestRunner.cs", "$src\Wizard.cs", "$src\Profiles.cs", "$src\Verdict.cs", "$src\Advice.cs") }, @{ exe = 'launcher.exe'; files = @("$src\Common.cs", "$src\Launcher.cs") }, @{ exe = 'VisionALVR.exe'; files = @("$src\Stub.cs"); manifest = 'app.stub.manifest'; plain = $true }, @{ exe = 'performance.exe'; files = @("$src\Common.cs", "$src\Performance.cs") })) {
    $c = if ($t.manifest) { $common | ForEach-Object { if ($_ -like '/win32manifest:*') { "/win32manifest:$src\$($t.manifest)" } else { $_ } } } else { $common }   # the stub keeps its own manifest (DPI awareness for its message box)
    $o = & $csc @c "/out:$out\$($t.exe)" @($t.files) 2>&1
    ($o | Out-String) | Add-Content -Path "$out\build.log" -Encoding UTF8
    $o | ForEach-Object { Write-Host ("$_" -replace "[^\x20-\x7E]", '?') }
    if ($LASTEXITCODE -ne 0) { $ok = $false; Write-Host "FAILED: $($t.exe)" } else { Write-Host "built $out\$($t.exe)" }
}
if ($ok) { Write-Host 'RESULT: OK' } else { Write-Host 'RESULT: FAILED' }
