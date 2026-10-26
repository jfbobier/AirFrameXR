# Takes a picture of the primary screen with every window minimised (the wallpaper, the icons, the taskbar), then brings the windows back: the real-desktop
# screenshots of the desktop mirror (harness/mirror_screenshot.py --real). Run in the interactive session; the picture goes to the path given.
param([string]$Out)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);'
[W.U]::SetProcessDPIAware() | Out-Null
$shell = New-Object -ComObject Shell.Application
$shell.MinimizeAll()
Start-Sleep -Milliseconds 800
# the window this script runs in (a console, a terminal) is not covered by "show desktop"
foreach ($p in Get-Process WindowsTerminal, cmd, conhost, OpenConsole, powershell -ErrorAction SilentlyContinue) { if ($p.MainWindowHandle -ne 0) { [W.U]::ShowWindow($p.MainWindowHandle, 6) | Out-Null } }
Start-Sleep -Milliseconds 1500
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$shell.UndoMinimizeALL()
Write-Host "saved $($b.Width)x$($b.Height)"
