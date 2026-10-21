// Lab probe (harness/process_loopback_probe.py): Windows process-loopback capture from .NET, no driver.
//   proc_loopback.exe <include|exclude> <pid> <seconds>   -> "frames=<n> peak=<0..1> rms=<0..1> silent_packets=<n>"
// include: audio rendered by <pid> and its child processes; exclude: everything except <pid>'s tree. Taken before the device's volume and mute (what is being tested).
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class P
{
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint n);
        [PreserveSig] int GetStreamLatency(out long l);
        [PreserveSig] int GetCurrentPadding(out uint n);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr f, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr f);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint n);
    }

    [ClassInterface(ClassInterfaceType.None)]
    class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public int Hr; public object Iface;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { op.GetActivateResult(out Hr, out Iface); Done.Set(); }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid iid, IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);
    [DllImport("kernel32.dll")] static extern IntPtr CreateEvent(IntPtr a, bool manual, bool initial, string name);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [MTAThread]
    static int Main(string[] a)
    {
        bool exclude = a[0] == "exclude"; uint pid = uint.Parse(a[1]); double seconds = double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
        // AUDIOCLIENT_ACTIVATION_PARAMS { type = PROCESS_LOOPBACK(1); { pid, mode } } inside a PROPVARIANT blob (VT_BLOB = 65), 24 bytes on x64
        var prm = Marshal.AllocHGlobal(12); Marshal.WriteInt32(prm, 0, 1); Marshal.WriteInt32(prm, 4, (int)pid); Marshal.WriteInt32(prm, 8, exclude ? 1 : 0);
        var pv = Marshal.AllocHGlobal(24); for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
        Marshal.WriteInt16(pv, 0, 65); Marshal.WriteInt32(pv, 8, 12); Marshal.WriteIntPtr(pv, 16, prm);
        var h = new Handler(); var iid = typeof(IAudioClient).GUID;
        IActivateAudioInterfaceAsyncOperation op;
        ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, h, out op);
        if (!h.Done.WaitOne(10000)) { Console.WriteLine("ERROR activation timed out"); return 2; }
        if (h.Hr < 0) { Console.WriteLine("ERROR activation 0x" + h.Hr.ToString("X8")); return 3; }
        var client = (IAudioClient)h.Iface;
        // 48 kHz stereo 16-bit PCM (process loopback has no mix format to ask for)
        var f = Marshal.AllocHGlobal(18); Marshal.WriteInt16(f, 0, 1); Marshal.WriteInt16(f, 2, 2); Marshal.WriteInt32(f, 4, 48000); Marshal.WriteInt32(f, 8, 192000); Marshal.WriteInt16(f, 12, 4); Marshal.WriteInt16(f, 14, 16); Marshal.WriteInt16(f, 16, 0);
        int hr = client.Initialize(0, 0x00020000u | 0x00040000u | 0x80000000u | 0x08000000u, 200000, 0, f, IntPtr.Zero);   // loopback | event callback | auto-convert | default SRC
        if (hr < 0) { Console.WriteLine("ERROR Initialize 0x" + hr.ToString("X8")); return 4; }
        var ev = CreateEvent(IntPtr.Zero, false, false, null); client.SetEventHandle(ev);
        var gi = typeof(IAudioCaptureClient).GUID; object o; hr = client.GetService(ref gi, out o);
        if (hr < 0) { Console.WriteLine("ERROR GetService 0x" + hr.ToString("X8")); return 5; }
        var cap = (IAudioCaptureClient)o;
        client.Start();
        long frames = 0, silent = 0; double peak = 0, sum = 0; var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            WaitForSingleObject(ev, 100);
            uint n; cap.GetNextPacketSize(out n);
            while (n > 0)
            {
                IntPtr data; uint fr, fl; ulong dp, qp;
                if (cap.GetBuffer(out data, out fr, out fl, out dp, out qp) < 0) break;
                if ((fl & 2) != 0) silent++;
                else for (int i = 0; i < fr * 2; i++) { double v = Math.Abs(Marshal.ReadInt16(data, i * 2) / 32768.0); if (v > peak) peak = v; sum += v * v; }
                frames += fr; cap.ReleaseBuffer(fr); cap.GetNextPacketSize(out n);
            }
        }
        client.Stop();
        Console.WriteLine("frames={0} peak={1:0.000} rms={2:0.000} silent_packets={3}", frames, peak, frames > 0 ? Math.Sqrt(sum / (frames * 2)) : 0, silent);
        return 0;
    }
}
