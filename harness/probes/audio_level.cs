// Lab probe (harness/audio_chain_check.py): the level of what is played to a playback device (WASAPI loopback of that endpoint, i.e. after its volume and
// mute: a muted output reads silence), or of a recording device.
//   audio_level.exe loopback|capture <part of the device name> <seconds>   -> "peak=0.123 rms=0.045 frames=48000 device=<name>"
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class L
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class EnumCom { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnum { [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IColl c); }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IColl { [PreserveSig] int GetCount(out int n); [PreserveSig] int Item(int i, out IDev d); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDev { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); [PreserveSig] int OpenPropertyStore(int a, out IProps s); }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IProps { [PreserveSig] int GetCount(out int n); [PreserveSig] int GetAt(int i, out Key k); [PreserveSig] int GetValue(ref Key k, out Var v); }
    [StructLayout(LayoutKind.Sequential)] struct Key { public Guid g; public int pid; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] struct Var { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long dur, long per, IntPtr fmt, IntPtr sess);
        [PreserveSig] int GetBufferSize(out uint n); [PreserveSig] int GetStreamLatency(out long l); [PreserveSig] int GetCurrentPadding(out uint n);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr f, out IntPtr c); [PreserveSig] int GetMixFormat(out IntPtr f);
        [PreserveSig] int GetDevicePeriod(out long d, out long m); [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h); [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICap { [PreserveSig] int GetBuffer(out IntPtr d, out uint fr, out uint fl, out ulong dp, out ulong qp); [PreserveSig] int ReleaseBuffer(uint fr); [PreserveSig] int GetNextPacketSize(out uint n); }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref Var v);

    static string Name(IDev d) { IProps ps; d.OpenPropertyStore(0, out ps); var k = new Key { g = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 }; Var v; ps.GetValue(ref k, out v); var s = Marshal.PtrToStringUni(v.p); PropVariantClear(ref v); return s; }

    [MTAThread]
    static int Main(string[] a)
    {
        bool loop = a[0] == "loopback"; double seconds = double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
        IColl c; ((IEnum)new EnumCom()).EnumAudioEndpoints(loop ? 0 : 1, 1, out c); int n; c.GetCount(out n);
        IDev dev = null; string name = null;
        for (int i = 0; i < n && dev == null; i++) { IDev d; c.Item(i, out d); var nm = Name(d); if (nm.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0) { dev = d; name = nm; } }
        if (dev == null) { Console.WriteLine("ERROR no device named " + a[1]); return 2; }
        var iid = typeof(IClient).GUID; object o; dev.Activate(ref iid, 23, IntPtr.Zero, out o); var cl = (IClient)o;
        IntPtr fmt; cl.GetMixFormat(out fmt);
        int ch = Marshal.ReadInt16(fmt, 2), bits = Marshal.ReadInt16(fmt, 14), tag = (ushort)Marshal.ReadInt16(fmt, 0);
        bool isFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadInt32(fmt, 24) == 3);
        int hr = cl.Initialize(0, loop ? 0x00020000u : 0u, 10000000, 0, fmt, IntPtr.Zero);
        if (hr < 0) { Console.WriteLine("ERROR Initialize 0x" + hr.ToString("X8")); return 3; }
        var gi = typeof(ICap).GUID; cl.GetService(ref gi, out o); var cap = (ICap)o;
        cl.Start();
        double peak = 0, sum = 0; long frames = 0, samples = 0; var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(10);
            uint k; cap.GetNextPacketSize(out k);
            while (k > 0)
            {
                IntPtr d; uint fr, fl; ulong dp, qp; if (cap.GetBuffer(out d, out fr, out fl, out dp, out qp) < 0) break;
                if ((fl & 2) == 0)
                    for (int i = 0; i < fr * ch; i++)
                    {
                        double v = isFloat ? Math.Abs(BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(d, i * 4)), 0)) : Math.Abs(Marshal.ReadInt16(d, i * 2) / 32768.0);
                        if (v > peak) peak = v; sum += v * v; samples++;
                    }
                frames += fr; cap.ReleaseBuffer(fr); cap.GetNextPacketSize(out k);
            }
        }
        cl.Stop();
        Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "peak={0:0.0000} rms={1:0.0000} frames={2} device={3}", peak, samples > 0 ? Math.Sqrt(sum / samples) : 0, frames, name));
        return 0;
    }
}
