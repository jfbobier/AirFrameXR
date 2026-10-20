# Lab helper (harness/audio_volume_check.py): get / set the endpoint volume of the default playback device through Core Audio.
#   audio_volume.ps1 get            -> 0.40
#   audio_volume.ps1 set 0.4
#   audio_volume.ps1 watch <seconds> -> polls every 250 ms, prints "<elapsed s> <volume>" until it reads 1.00 or the time is up
param([string]$Action, [string]$Arg = "")
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume { int a(); int b(); int c(); int d(); int SetMasterVolumeLevelScalar(float level, ref Guid ctx); int e(); int GetMasterVolumeLevelScalar(out float level); }
[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice { int Activate(ref Guid id, int clsCtx, IntPtr p, out IAudioEndpointVolume v); }
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator { int f(); int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev); }
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
public static class Vol {
  static IAudioEndpointVolume Ep() { var e = (IMMDeviceEnumerator)(new MMDeviceEnumeratorCom()); IMMDevice d; e.GetDefaultAudioEndpoint(0, 1, out d); var g = typeof(IAudioEndpointVolume).GUID; IAudioEndpointVolume v; d.Activate(ref g, 23, IntPtr.Zero, out v); return v; }
  public static float Get() { float l; Ep().GetMasterVolumeLevelScalar(out l); return l; }
  public static void Set(float l) { var g = Guid.Empty; Ep().SetMasterVolumeLevelScalar(l, ref g); }
}
"@
$inv = [Globalization.CultureInfo]::InvariantCulture
if ($Action -eq "get") { [Vol]::Get().ToString("0.00", $inv) }
elseif ($Action -eq "set") { [Vol]::Set([single]::Parse($Arg, $inv)); [Vol]::Get().ToString("0.00", $inv) }
elseif ($Action -eq "watch") {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt [double]::Parse($Arg, $inv)) {
    $v = [Vol]::Get(); "{0} {1}" -f $sw.Elapsed.TotalSeconds.ToString("0.00", $inv), $v.ToString("0.00", $inv)
    if ($v -ge 0.999) { break }
    Start-Sleep -Milliseconds 250
  }
}
