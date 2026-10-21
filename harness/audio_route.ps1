# Lab helper (harness/audio_route_check.py), runs in the interactive desktop session (Core Audio is not reachable from the SSH session).
#   audio_route.ps1 snapshot                 -> "default <role> <name>" for the three roles, then "device <name> vol=<0..1> mute=<0|1>" for every active playback device
#   audio_route.ps1 set <name part> <vol>    -> sets that device's volume (the test's starting state)
#   audio_route.ps1 run <streamer.exe> <seconds>   -> starts the streamer, waits, prints a snapshot at the end of the wait (streamer still running),
#                                                     closes its window, waits for it to exit, prints "exit <seconds to exit>" and a snapshot ("after")
param([string]$Action, [string]$A = "", [string]$B = "")
Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class Au {
  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class Enum_ { }
  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IEnum {
    [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IColl c); [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDev d); [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDev d); }
  [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IColl { [PreserveSig] int GetCount(out int n); [PreserveSig] int Item(int i, out IDev d); }
  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDev {
    [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); [PreserveSig] int OpenPropertyStore(int a, out IProps s); [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id); }
  [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IProps { [PreserveSig] int GetCount(out int n); [PreserveSig] int GetAt(int i, out Key k); [PreserveSig] int GetValue(ref Key k, out Var v); }
  [StructLayout(LayoutKind.Sequential)] struct Key { public Guid g; public int pid; }
  [StructLayout(LayoutKind.Explicit, Size = 16)] struct Var { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }
  [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IVol {
    [PreserveSig] int a(IntPtr n); [PreserveSig] int b(IntPtr n); [PreserveSig] int c(out int n); [PreserveSig] int d(float x, ref Guid g); [PreserveSig] int SetScalar(float l, ref Guid g);
    [PreserveSig] int e(out float x); [PreserveSig] int GetScalar(out float l); [PreserveSig] int f(int c, float x, ref Guid g); [PreserveSig] int g(int c, float x, ref Guid g);
    [PreserveSig] int h(int c, out float x); [PreserveSig] int i(int c, out float x); [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid g); [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m); }
  static string Name(IDev d) { IProps ps; d.OpenPropertyStore(0, out ps); var k = new Key { g = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 }; Var v; ps.GetValue(ref k, out v); return Marshal.PtrToStringUni(v.p); }
  static IVol Vol(IDev d) { var iid = typeof(IVol).GUID; object o; d.Activate(ref iid, 23, IntPtr.Zero, out o); return (IVol)o; }
  public static List<string> Snapshot() {
    var r = new List<string>(); var e = (IEnum)new Enum_(); string[] roles = { "console", "multimedia", "communications" };
    for (int i = 0; i < 3; i++) { IDev d; if (e.GetDefaultAudioEndpoint(0, i, out d) >= 0) r.Add("default " + roles[i] + " " + Name(d)); }
    IColl c; e.EnumAudioEndpoints(0, 1, out c); int n; c.GetCount(out n);
    for (int i = 0; i < n; i++) { IDev d; c.Item(i, out d); float v; bool m; var vo = Vol(d); vo.GetScalar(out v); vo.GetMute(out m); r.Add("device " + Name(d) + " | vol=" + v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " mute=" + (m ? 1 : 0)); }
    return r; }
  public static void SetVol(string part, float v) {
    var e = (IEnum)new Enum_(); IColl c; e.EnumAudioEndpoints(0, 1, out c); int n; c.GetCount(out n);
    for (int i = 0; i < n; i++) { IDev d; c.Item(i, out d); if (Name(d).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) { var g = Guid.Empty; Vol(d).SetScalar(v, ref g); } } }
}
"@
$inv = [Globalization.CultureInfo]::InvariantCulture
if ($Action -eq "snapshot") { [Au]::Snapshot() }
elseif ($Action -eq "set") { [Au]::SetVol($A, [single]::Parse($B, $inv)); [Au]::Snapshot() }
elseif ($Action -eq "run") {
  $p = Start-Process -FilePath $A -PassThru -WorkingDirectory (Split-Path $A)
  Start-Sleep -Seconds ([int]$B)
  "== during"; [Au]::Snapshot()
  $sw = [Diagnostics.Stopwatch]::StartNew()
  [void]$p.CloseMainWindow()
  if (-not $p.WaitForExit(30000)) { "exit TIMEOUT"; $p.Kill() } else { "exit " + $sw.Elapsed.TotalSeconds.ToString("0.0", $inv) }
  Start-Sleep -Seconds 1
  "== after"; [Au]::Snapshot()
}
