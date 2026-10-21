using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;

namespace VisionALVR
{
    /// What startvisionalvr.bat did with svcl.exe, done in-process with the Core Audio API while the streamer runs: the virtual cable (VB-CABLE / the device named
    /// in the audio settings) becomes the default output for every role (games that play to another role would bypass ALVR's capture), the PC's own output is muted,
    /// the cable is unmuted and held at 100 % (its endpoint volume scales what ALVR captures, so the PC's volume keys would change the headset's volume).
    /// Stop() puts everything back. The state to restore is written to logs\audio_route.txt first, so a streamer that crashed is undone at the next start.
    /// VISIONALVR_AUDIO_ROUTE=0 disables it.
    public sealed class AudioRoute
    {
        static readonly string StateFile = Path.Combine(Paths.Logs, "audio_route.txt");
        static readonly string[] Roles = { "console", "multimedia", "communications" };

        readonly object gate = new object();
        Timer timer;
        string cableId, cableName, pcId, pcName;
        string[] oldDefaults = new string[3];
        bool pcWasMuted, running;
        float lastVol = -1;

        /// Raised (on a pool thread) with (level, text) for the log and the UI: level INFO / WARN.
        public event Action<string, string> Report;

        void Say(string level, string text) { Log.Write(level, "audio: " + text); try { Report?.Invoke(level, text); } catch { } }

        public void Start(string wantedPart, bool enabled)
        {
            if (!enabled || Environment.GetEnvironmentVariable("VISIONALVR_AUDIO_ROUTE") == "0") return;
            try
            {
                RestoreLeftover();
                lock (gate)
                {
                    var devices = Endpoints.Render();
                    Endpoints.Dev cable = null;
                    foreach (var d in devices)
                        if (!string.IsNullOrEmpty(wantedPart) && d.Name.IndexOf(wantedPart, StringComparison.OrdinalIgnoreCase) >= 0) { cable = d; break; }
                    if (cable == null)
                        foreach (var d in devices)
                            if (d.Name.IndexOf("cable input", StringComparison.OrdinalIgnoreCase) >= 0 || d.Name.IndexOf("vb-audio", StringComparison.OrdinalIgnoreCase) >= 0) { cable = d; break; }
                    if (cable == null) { Say("WARN", "no virtual audio cable found (VB-CABLE); the game audio follows the Windows default output"); return; }
                    cableId = cable.Id; cableName = cable.Name;

                    for (int i = 0; i < 3; i++) oldDefaults[i] = Endpoints.Default(i);
                    var pc = oldDefaults[0] != null && oldDefaults[0] != cableId ? oldDefaults[0] : null;
                    if (pc != null) { pcId = pc; pcName = devices.Find(x => x.Id == pc)?.Name ?? pc; pcWasMuted = Endpoints.GetMute(pc); }
                    File.WriteAllText(StateFile, string.Join("\n", oldDefaults.Select(x => x ?? "")) + "\n" + (pcId ?? "") + "\n" + (pcWasMuted ? "1" : "0") + "\n");

                    if (pcId != null) Endpoints.SetMute(pcId, true);
                    for (int i = 0; i < 3; i++)
                        if (oldDefaults[i] != cableId) Endpoints.SetDefault(cableId, i);
                    running = true;
                    Say("INFO", $"{cableName} is now the default output" + (pcId != null ? $", {pcName} muted (restored when the streamer closes)" : ""));
                }
                Hold(null);
                timer = new Timer(Hold, null, 3000, 3000);
            }
            catch (Exception ex) { Say("WARN", "could not route the game audio to the virtual cable: " + ex.Message); }
        }

        /// Cable unmuted and at 100 %; reports the first state and every correction.
        void Hold(object _)
        {
            lock (gate)
            {
                if (!running || cableId == null) return;
                try
                {
                    var vol = Endpoints.GetVolume(cableId);
                    var muted = Endpoints.GetMute(cableId);
                    if (vol < 0.999f || muted)
                    {
                        Endpoints.SetMute(cableId, false);
                        Endpoints.SetVolume(cableId, 1f);
                        var now = Endpoints.GetVolume(cableId);
                        if (now >= 0.999f) Say("INFO", $"{cableName} was at {vol * 100:0} %{(muted ? " (muted)" : "")}, set back to 100 %");
                        else if (Math.Abs(vol - lastVol) > 0.01f) Say("WARN", $"{cableName} could not be set to 100 % (stays at {now * 100:0} %)");
                    }
                    else if (lastVol < 0) Say("INFO", $"{cableName} is at 100 %, held there while the streamer runs");
                    lastVol = vol;
                }
                catch (Exception ex) { if (lastVol != -2) Say("WARN", $"{cableName}: cannot read or set the volume: {ex.Message}"); lastVol = -2; }
            }
        }

        public void Stop()
        {
            try { timer?.Dispose(); } catch { }
            lock (gate)
            {
                if (!running) return;
                running = false;
                try
                {
                    Restore(oldDefaults, pcId, pcWasMuted);
                    Say("INFO", "default output and mute state restored");
                }
                catch (Exception ex) { Say("WARN", "restoring the audio devices failed: " + ex.Message); }
                try { File.Delete(StateFile); } catch { }
            }
        }

        static void Restore(string[] defaults, string pc, bool pcWasMuted)
        {
            for (int i = 0; i < 3; i++) if (!string.IsNullOrEmpty(defaults[i])) Endpoints.SetDefault(defaults[i], i);
            if (!string.IsNullOrEmpty(pc)) Endpoints.SetMute(pc, pcWasMuted);
        }

        static void RestoreLeftover()
        {
            if (!File.Exists(StateFile)) return;
            try
            {
                var l = File.ReadAllLines(StateFile);
                if (l.Length >= 5) { Restore(new[] { l[0], l[1], l[2] }, l[3], l[4] == "1"); Log.Write("INFO", "audio: restored the devices left by a streamer that did not close cleanly"); }
            }
            catch (Exception ex) { Log.Write("WARN", "audio: leftover state not restored: " + ex.Message); }
            try { File.Delete(StateFile); } catch { }
        }

        // ---- Core Audio COM ----------------------------------------------------------------------------------------------------------------------------
        static class Endpoints
        {
            public sealed class Dev { public string Id, Name; }

            [ComImport, Guid("BCDE0395-E52F-49B4-B4B2-1E8DF2F4DAC7")] class MMDeviceEnumeratorCom { }
            [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigCom { }

            [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IMMDeviceEnumerator
            {
                [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
                [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
                [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            }
            [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IMMDeviceCollection
            {
                [PreserveSig] int GetCount(out int count);
                [PreserveSig] int Item(int i, out IMMDevice device);
            }
            [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IMMDevice
            {
                [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
                [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
                [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            }
            [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IPropertyStore
            {
                [PreserveSig] int GetCount(out int count);
                [PreserveSig] int GetAt(int i, out PropertyKey key);
                [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            }
            [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid fmtid; public int pid; }
            [StructLayout(LayoutKind.Explicit, Size = 16)] struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr ptr; }
            [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

            [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IAudioEndpointVolume
            {
                [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
                [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
                [PreserveSig] int GetChannelCount(out int n);
                [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
                [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
                [PreserveSig] int GetMasterVolumeLevel(out float db);
                [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
                [PreserveSig] int SetChannelVolumeLevel(int ch, float db, ref Guid ctx);
                [PreserveSig] int SetChannelVolumeLevelScalar(int ch, float level, ref Guid ctx);
                [PreserveSig] int GetChannelVolumeLevel(int ch, out float db);
                [PreserveSig] int GetChannelVolumeLevelScalar(int ch, out float level);
                [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
                [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            }

            // the undocumented policy interface every default-device switcher uses (svcl, SoundVolumeView, AudioSwitch)
            [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IPolicyConfig
            {
                [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
                [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
                [PreserveSig] int ResetDeviceFormat(IntPtr a);
                [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
                [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
                [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
                [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
                [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
                [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
                [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
                [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
                [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
            }

            const int FlowRender = 0, DeviceActive = 1, ClsCtxAll = 23;
            static readonly Guid VolumeIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
            static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})"); }
            static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

            public static List<Dev> Render()
            {
                var res = new List<Dev>();
                Check(Enumerator().EnumAudioEndpoints(FlowRender, DeviceActive, out var col), "EnumAudioEndpoints");
                col.GetCount(out int n);
                for (int i = 0; i < n; i++)
                {
                    col.Item(i, out var d); d.GetId(out var id);
                    res.Add(new Dev { Id = id, Name = NameOf(d) });
                }
                return res;
            }

            static string NameOf(IMMDevice d)
            {
                try
                {
                    d.OpenPropertyStore(0, out var ps);
                    var key = new PropertyKey { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
                    ps.GetValue(ref key, out var v);
                    var s = v.vt == 31 ? Marshal.PtrToStringUni(v.ptr) : null;
                    PropVariantClear(ref v);
                    return s ?? "";
                }
                catch { return ""; }
            }

            public static string Default(int role) { return Enumerator().GetDefaultAudioEndpoint(FlowRender, role, out var d) < 0 ? null : (d.GetId(out var id) < 0 ? null : id); }

            public static void SetDefault(string id, int role) => Check(((IPolicyConfig)new PolicyConfigCom()).SetDefaultEndpoint(id, role), "SetDefaultEndpoint " + Roles[role]);

            static IAudioEndpointVolume Volume(string id)
            {
                Check(Enumerator().GetDevice(id, out var d), "GetDevice");
                var iid = VolumeIid;
                Check(d.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var o), "Activate(IAudioEndpointVolume)");
                return (IAudioEndpointVolume)o;
            }
            public static float GetVolume(string id) { Check(Volume(id).GetMasterVolumeLevelScalar(out var v), "GetMasterVolumeLevelScalar"); return v; }
            public static void SetVolume(string id, float v) { var g = Guid.Empty; Check(Volume(id).SetMasterVolumeLevelScalar(v, ref g), "SetMasterVolumeLevelScalar"); }
            public static bool GetMute(string id) { Check(Volume(id).GetMute(out var m), "GetMute"); return m; }
            public static void SetMute(string id, bool m) { var g = Guid.Empty; Check(Volume(id).SetMute(m, ref g), "SetMute"); }
        }
    }
}
