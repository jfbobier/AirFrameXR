using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;

namespace VisionALVR
{
    /// What startvisionalvr.bat did with svcl.exe, done in-process with the Core Audio API while the streamer runs: the virtual cable (VB-CABLE / the device named
    /// in the audio settings) becomes the default output for every role (games that play to another role would bypass ALVR's capture), the PC's own output is muted,
    /// the cable is unmuted and held at 100 % (its endpoint volume scales what ALVR captures, so the PC's volume keys would change the headset's volume).
    /// Every 3 s the default and the cable's volume and mute are put back if anything moved them (Windows when a device appears, a game, ALVR's own mute_when_streaming).
    /// A virtual device with more than 8 channels is never chosen: ALVR's capture refuses it (alvr_audio lib.rs). Stop() puts everything back. The state to restore is written to logs\audio_route.txt first, so a streamer that crashed is undone at the next start.
    /// VISIONALVR_AUDIO_ROUTE=0 disables it.
    public sealed class AudioRoute
    {
        static readonly string StateFile = Path.Combine(Paths.Logs, "audio_route.txt");
        static readonly string[] Roles = { "console", "multimedia", "communications" };

        readonly object gate = new object();
        System.Threading.Timer timer;
        string cableId, cableName, pcId, pcName;
        string[] oldDefaults = new string[3];
        bool pcWasMuted, running;
        float lastVol = -1;

        /// Raised (on a pool thread) with (level, text) for the log and the UI: level INFO / WARN.
        public event Action<string, string> Report;

        void Say(string level, string text) { Log.Write(level, "audio: " + text); try { Report?.Invoke(level, text); } catch { } }

        /// True when the cable is the default output and held (the caller then switches ALVR's own mute off). Call it before the host starts: it takes a few milliseconds.
        public bool Start(string wantedPart, bool enabled, bool mutePc)
        {
            if (!enabled || Environment.GetEnvironmentVariable("VISIONALVR_AUDIO_ROUTE") == "0") return false;
            CableMissing = false;
            if (Environment.GetEnvironmentVariable("VISIONALVR_FAKE_NO_CABLE") == "1") { CableMissing = true; Say("WARN", "no usable virtual audio cable found (VB-CABLE, at most 8 channels); the game audio follows the Windows default output"); return false; }
            try
            {
                RestoreLeftover();
                lock (gate)
                {
                    var devices = Endpoints.Render();
                    Endpoints.Dev cable = null;
                    foreach (var d in devices)
                        if (d.Channels > MaxChannels && (string.IsNullOrEmpty(wantedPart) || d.Name.IndexOf(wantedPart, StringComparison.OrdinalIgnoreCase) >= 0) && IsCable(d.Name))
                            Say("WARN", $"{d.Name} has {d.Channels} channels: ALVR cannot capture more than {MaxChannels}, it is not used");
                    cable = Pick(devices, wantedPart);
                    if (cable == null) { CableMissing = true; Say("WARN", "no usable virtual audio cable found (VB-CABLE, at most 8 channels); the game audio follows the Windows default output"); return false; }
                    cableId = cable.Id; cableName = cable.Name;
                    EnsurePlainPcm();

                    for (int i = 0; i < 3; i++) oldDefaults[i] = Endpoints.Default(i);
                    var pc = oldDefaults[0] != null && oldDefaults[0] != cableId ? oldDefaults[0] : null;
                    if (pc != null) { pcId = pc; pcName = devices.Find(x => x.Id == pc)?.Name ?? pc; pcWasMuted = Endpoints.GetMute(pc); }
                    File.WriteAllText(StateFile, string.Join("\n", oldDefaults.Select(x => x ?? "")) + "\n" + (pcId ?? "") + "\n" + (pcWasMuted ? "1" : "0") + "\n");

                    if (pcId != null && mutePc) Endpoints.SetMute(pcId, true);
                    for (int i = 0; i < 3; i++)
                        if (oldDefaults[i] != cableId) Endpoints.SetDefault(cableId, i);
                    running = true;
                    Say("INFO", $"{cableName} is now the default output" + (pcId != null && mutePc ? $", {pcName} muted (restored when the streamer closes)" : ""));
                }
                Hold(null);
                timer = new System.Threading.Timer(Hold, null, 3000, 3000);
                return true;
            }
            catch (Exception ex) { Say("WARN", "could not route the game audio to the virtual cable: " + ex.Message); return false; }
        }

        /// The game audio reaches the headset as plain PCM at the device's rate (ALVR tells the client the rate it captures at): 2 channels, 48 kHz (what the headsets
        /// play natively, nothing to resample), 16 bit. Checked at start; a different Default format is set back, a refusal is reported. Spatial sound (Windows Sonic)
        /// on the device changes the mix too: it is only reported (the setting lives in Windows' protected store, it cannot be switched from here).
        void EnsurePlainPcm()
        {
            var f = Endpoints.MixFormat(cableId);
            if (f == null) { Say("WARN", $"{cableName}: the format cannot be read"); return; }
            if (f[0] != 2 || f[1] != 48000)
            {
                try
                {
                    Endpoints.SetPcmFormat(cableId, 2, 48000);
                    var g = Endpoints.MixFormat(cableId);
                    if (g != null && g[0] == 2 && g[1] == 48000) { Say("INFO", $"{cableName} was {f[0]} ch / {f[1]} Hz, set to plain PCM, 2 ch / 48000 Hz"); return; }
                    Say("WARN", $"{cableName} is {f[0]} ch / {f[1]} Hz and could not be set to 2 ch / 48000 Hz: set it in Sound > {cableName} > Properties > Advanced");
                }
                catch (Exception ex) { Say("WARN", $"{cableName} is {f[0]} ch / {f[1]} Hz and could not be set to 2 ch / 48000 Hz ({ex.Message}): set it in Sound > Properties > Advanced"); }
            }
            else Log.Write("INFO", $"audio: {cableName} format {f[0]} ch / {f[1]} Hz / {f[2]} bit");
            try
            {
                var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + EndpointGuid(cableId) + @"\FxProperties");
                var spatial = key?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                if (!string.IsNullOrEmpty(spatial)) Say("WARN", $"spatial sound is on for {cableName} ({spatial}): switch it to Off in Sound > Properties > Spatial sound, the headset hears the plain stereo mix otherwise altered");
            }
            catch { }
        }

        static string EndpointGuid(string id) { var i = id.LastIndexOf('.'); return i >= 0 ? id.Substring(i + 1) : id; }

        /// True after Start found no usable virtual cable (the caller asks the user to install one).
        public bool CableMissing;

        static Endpoints.Dev Pick(List<Endpoints.Dev> devices, string wantedPart)
        {
            var capturable = devices.FindAll(d => d.Channels <= MaxChannels);
            Endpoints.Dev cable = null;
            if (!string.IsNullOrEmpty(wantedPart)) cable = capturable.Find(d => d.Name.IndexOf(wantedPart, StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var key in new[] { "cable input", "vb-audio" })   // the plain stereo input first
                if (cable == null) cable = capturable.Find(d => d.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
            return cable;
        }

        /// The recording side of a virtual cable (CABLE Output, VoiceMeeter Output) ALVR's microphone path ends in, or null (VISIONALVR_FAKE_NO_CABLE=1: the lab pretends there is none).
        public static string FindMicrophoneCable()
        {
            if (Environment.GetEnvironmentVariable("VISIONALVR_FAKE_NO_CABLE") == "1") return null;
            try
            {
                var devices = Endpoints.Capture();
                foreach (var key in new[] { "cable output", "voicemeeter output" })
                {
                    var d = devices.Find(x => x.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (d != null) return d.Name;
                }
            }
            catch { }
            return null;
        }

        /// The name of the virtual cable the streamer would use, or null (VISIONALVR_FAKE_NO_CABLE=1: the lab pretends there is none).
        public static string FindCable(string wantedPart = "")
        {
            if (Environment.GetEnvironmentVariable("VISIONALVR_FAKE_NO_CABLE") == "1") return null;
            try { return Pick(Endpoints.Render(), wantedPart)?.Name; } catch { return null; }
        }

        /// Process-loopback capture (AudioCapture "process"): ALVR hears the PC's audio whatever the output's mute, volume and format, so nothing is switched and no cable is
        /// needed; the only thing left to do is to mute the PC's own output while streaming (when asked), restored at close or after a crash.
        public void StartMuteOnly(bool enabled, bool mutePc)
        {
            if (!enabled || !mutePc || Environment.GetEnvironmentVariable("VISIONALVR_AUDIO_ROUTE") == "0") return;
            try
            {
                RestoreLeftover();
                lock (gate)
                {
                    for (int i = 0; i < 3; i++) oldDefaults[i] = Endpoints.Default(i);
                    pcId = oldDefaults[0];
                    if (pcId == null) return;
                    pcName = Endpoints.Render().Find(x => x.Id == pcId)?.Name ?? pcId;
                    pcWasMuted = Endpoints.GetMute(pcId);
                    File.WriteAllText(StateFile, string.Join("\n", oldDefaults.Select(x => x ?? "")) + "\n" + pcId + "\n" + (pcWasMuted ? "1" : "0") + "\n");
                    Endpoints.SetMute(pcId, true);
                    running = true;
                    Say("INFO", $"{pcName} muted while streaming (restored when the streamer closes); the game audio is captured per process, no virtual cable");
                }
            }
            catch (Exception ex) { Say("WARN", "could not mute the PC output: " + ex.Message); }
        }

        const int MaxChannels = 8;   // alvr_audio refuses a capture device with more
        static bool IsCable(string name) => name.IndexOf("cable", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("vb-audio", StringComparison.OrdinalIgnoreCase) >= 0;

        /// Cable unmuted and at 100 %; reports the first state and every correction.
        void Hold(object _)
        {
            lock (gate)
            {
                if (!running || cableId == null) return;
                try
                {
                    for (int i = 0; i < 3; i++)   // something moved the default (a device that appeared, a game, a driver): back to the cable
                        if (Endpoints.Default(i) != cableId) { Endpoints.SetDefault(cableId, i); Say("INFO", $"the {Roles[i]} default output had changed, set back to {cableName}"); }
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

        // ---- the headset microphone (ALVR's own path: it plays the headset's voice into a virtual cable's playback side, the game reads its recording side) -----------
        static readonly string MicStateFile = Path.Combine(Paths.Logs, "audio_mic.txt");
        readonly string[] micOld = new string[3];
        bool micRunning;

        /// When the microphone is enabled in the audio settings: the virtual cable's recording side (CABLE Output, else VoiceMeeter Output) becomes the default recording device for every
        /// role while the streamer runs, so a game (VRChat) hears the headset's voice without picking a device; the previous default comes back at close or after a crash.
        /// Does nothing when the microphone is off.
        public void StartMicrophone(bool micEnabled)
        {
            if (!micEnabled || Environment.GetEnvironmentVariable("VISIONALVR_AUDIO_ROUTE") == "0") return;
            try
            {
                RestoreLeftoverMic();
                var devices = Endpoints.Capture();
                Endpoints.Dev mic = null;
                foreach (var key in new[] { "cable output", "voicemeeter output" })
                    if (mic == null) mic = devices.Find(d => d.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                if (mic == null) { Say("WARN", "microphone: no virtual cable recording device (CABLE Output) found: the headset's voice cannot reach games until VB-CABLE is installed"); return; }
                for (int i = 0; i < 3; i++) micOld[i] = Endpoints.DefaultCapture(i);
                File.WriteAllText(MicStateFile, string.Join("\n", micOld.Select(x => x ?? "")) + "\n");
                for (int i = 0; i < 3; i++) if (micOld[i] != mic.Id) Endpoints.SetDefault(mic.Id, i);
                micRunning = true;
                Say("INFO", $"microphone: {mic.Name} is the default recording device while streaming (restored when the streamer closes)");
            }
            catch (Exception ex) { Say("WARN", "microphone: could not set the default recording device: " + ex.Message); }
        }

        void StopMicrophone()
        {
            if (!micRunning) return;
            micRunning = false;
            try { for (int i = 0; i < 3; i++) if (!string.IsNullOrEmpty(micOld[i])) Endpoints.SetDefault(micOld[i], i); Say("INFO", "microphone: default recording device restored"); }
            catch (Exception ex) { Say("WARN", "restoring the recording device failed: " + ex.Message); }
            try { File.Delete(MicStateFile); } catch { }
        }

        static void RestoreLeftoverMic()
        {
            if (!File.Exists(MicStateFile)) return;
            try
            {
                var l = File.ReadAllLines(MicStateFile);
                for (int i = 0; i < 3 && i < l.Length; i++) if (!string.IsNullOrEmpty(l[i])) Endpoints.SetDefault(l[i], i);
                Log.Write("INFO", "audio: restored the recording device left by a streamer that did not close cleanly");
            }
            catch (Exception ex) { Log.Write("WARN", "audio: leftover recording device not restored: " + ex.Message); }
            try { File.Delete(MicStateFile); } catch { }
        }

        public void Stop()
        {
            StopMicrophone();
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
            public sealed class Dev { public string Id, Name; public int Channels; }

            [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
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
                [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
                [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
                [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
                [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
                [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
                [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
                [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
                [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
                [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
            }

            const int FlowRender = 0, FlowCapture = 1, DeviceActive = 1, ClsCtxAll = 23;
            static readonly Guid VolumeIid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
            static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})"); }
            static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

            public static List<Dev> Render() => List(FlowRender);
            public static List<Dev> Capture() => List(FlowCapture);

            static List<Dev> List(int flow)
            {
                var res = new List<Dev>();
                Check(Enumerator().EnumAudioEndpoints(flow, DeviceActive, out var col), "EnumAudioEndpoints");
                col.GetCount(out int n);
                for (int i = 0; i < n; i++)
                {
                    col.Item(i, out var d); d.GetId(out var id);
                    res.Add(new Dev { Id = id, Name = NameOf(d), Channels = ChannelsOf(d) });
                }
                return res;
            }

            [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IAudioClient
            {
                [PreserveSig] int Initialize(int mode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
                [PreserveSig] int GetBufferSize(out uint n);
                [PreserveSig] int GetStreamLatency(out long l);
                [PreserveSig] int GetCurrentPadding(out uint n);
                [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
                [PreserveSig] int GetMixFormat(out IntPtr format);
            }

            /// The device's shared-mode mix format channel count (WAVEFORMATEX.nChannels), the format ALVR's loopback capture gets; 2 when it cannot be read.
            static int ChannelsOf(IMMDevice d)
            {
                try
                {
                    var iid = typeof(IAudioClient).GUID;
                    if (d.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var o) < 0) return 2;
                    ((IAudioClient)o).GetMixFormat(out var fmt);
                    var n = Marshal.ReadInt16(fmt, 2);
                    Marshal.FreeCoTaskMem(fmt);
                    return n;
                }
                catch { return 2; }
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

            /// (channels, sample rate, bits, format tag) of the shared-mode mix format, the device's "Default format" in the Sound control panel; null when unreadable.
            public static int[] MixFormat(string id)
            {
                try
                {
                    Check(Enumerator().GetDevice(id, out var d), "GetDevice");
                    var iid = typeof(IAudioClient).GUID;
                    Check(d.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var o), "Activate(IAudioClient)");
                    Check(((IAudioClient)o).GetMixFormat(out var f), "GetMixFormat");
                    var r = new[] { (int)Marshal.ReadInt16(f, 2), Marshal.ReadInt32(f, 4), (int)Marshal.ReadInt16(f, 14), (int)(ushort)Marshal.ReadInt16(f, 0) };
                    Marshal.FreeCoTaskMem(f);
                    return r;
                }
                catch { return null; }
            }

            /// Sets the device's Default format to plain PCM: `channels`, `rate`, 16 bit (WAVEFORMATEXTENSIBLE: the plain WAVEFORMATEX is refused with NOT_FOUND).
            /// Shared mode then mixes at that rate and channel count (the engine's own mix stays 32-bit float).
            public static void SetPcmFormat(string id, int channels, int rate)
            {
                var f = Marshal.AllocHGlobal(40);
                try
                {
                    for (int i = 0; i < 40; i++) Marshal.WriteByte(f, i, 0);
                    Marshal.WriteInt16(f, 0, unchecked((short)0xFFFE)); Marshal.WriteInt16(f, 2, (short)channels); Marshal.WriteInt32(f, 4, rate);
                    Marshal.WriteInt32(f, 8, rate * channels * 2); Marshal.WriteInt16(f, 12, (short)(channels * 2)); Marshal.WriteInt16(f, 14, 16);
                    Marshal.WriteInt16(f, 16, 22); Marshal.WriteInt16(f, 18, 16); Marshal.WriteInt32(f, 20, channels == 2 ? 3 : 0);
                    Marshal.Copy(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray(), 0, f + 24, 16);   // KSDATAFORMAT_SUBTYPE_PCM
                    Check(((IPolicyConfig)new PolicyConfigCom()).SetDeviceFormat(id, f, f), "SetDeviceFormat");
                }
                finally { Marshal.FreeHGlobal(f); }
            }

            public static string Default(int role) => DefaultOf(FlowRender, role);
            public static string DefaultCapture(int role) => DefaultOf(FlowCapture, role);
            static string DefaultOf(int flow, int role) { return Enumerator().GetDefaultAudioEndpoint(flow, role, out var d) < 0 ? null : (d.GetId(out var id) < 0 ? null : id); }

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

    /// Shown (blocking) when the PC has no virtual audio cable: the game audio reaches the headset through one (VB-CABLE by VB-Audio, a separate product).
    public class CableForm : DpiForm
    {
        public const string DownloadUrl = "https://vb-audio.com/Cable/index.htm";

        public CableForm(bool forMicrophone = false)
        {
            Text = "VisionALVR \u2013 Virtual audio cable";
            Font = Ui.Body; Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
            col.Controls.Add(new Label { Text = forMicrophone ? "The microphone needs a virtual audio cable" : "No virtual audio cable found", AutoSize = true, Font = Ui.Big });
            col.Controls.Add(new Label
            {
                Text = (forMicrophone
                       ? "Your voice from the headset's microphone reaches games (VRChat, chat) through a virtual audio device: VisionALVR plays it into the cable and, while it streams, makes the cable's recording side the Windows default microphone (the previous one comes back when it closes). " +
                         "Without one, games do not hear the headset microphone.\n\n"
                       : "The game's sound reaches the headset through a virtual audio device: VisionALVR makes it the Windows output while it streams, mutes your speakers, and puts everything back when it closes. " +
                         "Without one, the headset has no game sound.\n\n") +
                       "VB-CABLE (VB-Audio) is a free virtual cable. Click \"Open the download page\", install it (administrator rights, a restart may be needed), then start the streamer again.\n\n" +
                       "VB-CABLE is a separate product of VB-Audio Software, not part of VisionALVR. VisionALVR does not distribute, control or support it and accepts no responsibility for it: " +
                       "read its licence and install it at your own risk.",
                AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(3, 8, 3, 8)
            });
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var open = new Button { Text = "Open the download page", AutoSize = true, Font = Ui.Bold, MinimumSize = new Size(130, 30) };
            var go = new Button { Text = forMicrophone ? "Continue without the microphone" : "Continue without game sound", AutoSize = true, MinimumSize = new Size(130, 30), DialogResult = DialogResult.Cancel };
            open.Click += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DownloadUrl) { UseShellExecute = true }); } catch { } };
            row.Controls.Add(open); row.Controls.Add(go);
            col.Controls.Add(row);
            Controls.Add(col);
            AcceptButton = open; CancelButton = go;
            Ui.AutoFitWindow(this, null, col);
        }
    }

    /// Measures the PC's sound with Windows' process loopback, the capture API the host uses in "process" mode (everything except this process): the wizard's audio test calls it while the
    /// scene plays its music. Same calls as harness/probes/proc_loopback.cs. Error = Windows without the API (before Windows 10 2004) or the activation refused.
    public static class ProcessLoopbackMeter
    {
        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivateCompletionHandler { void ActivateCompleted(IActivateAsyncOperation op); }
        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivateAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }
        [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAgile { }
        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IClient
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
        interface ICapture
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint n);
        }

        [ClassInterface(ClassInterfaceType.None)]
        class Handler : IActivateCompletionHandler, IAgile
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public int Hr; public object Iface;
            public void ActivateCompleted(IActivateAsyncOperation op) { op.GetActivateResult(out Hr, out Iface); Done.Set(); }
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid iid, IntPtr activationParams, IActivateCompletionHandler handler, out IActivateAsyncOperation op);
        [DllImport("kernel32.dll")] static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        /// Listens for `seconds` to everything the PC plays except this process; peak and rms are 0..1 of 16-bit stereo samples. Returns null on success, else the reason.
        public static string Measure(double seconds, out double peak, out double rms)
        {
            peak = rms = 0;
            IntPtr prm = IntPtr.Zero, pv = IntPtr.Zero, fmt = IntPtr.Zero, ev = IntPtr.Zero;
            try
            {
                // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK = 1; pid; mode EXCLUDE_TARGET_PROCESS_TREE = 1 } in a VT_BLOB PROPVARIANT (24 bytes on x64)
                prm = Marshal.AllocHGlobal(12); Marshal.WriteInt32(prm, 0, 1); Marshal.WriteInt32(prm, 4, System.Diagnostics.Process.GetCurrentProcess().Id); Marshal.WriteInt32(prm, 8, 1);
                pv = Marshal.AllocHGlobal(24); for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 65); Marshal.WriteInt32(pv, 8, 12); Marshal.WriteIntPtr(pv, 16, prm);
                var h = new Handler(); var iid = typeof(IClient).GUID; IActivateAsyncOperation op;
                ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, h, out op);
                if (!h.Done.WaitOne(10000)) return "the activation timed out";
                if (h.Hr < 0) return $"the activation was refused (0x{h.Hr:X8})";
                var client = (IClient)h.Iface;
                fmt = Marshal.AllocHGlobal(18);   // 48 kHz stereo 16-bit PCM, what the host asks for
                Marshal.WriteInt16(fmt, 0, 1); Marshal.WriteInt16(fmt, 2, 2); Marshal.WriteInt32(fmt, 4, 48000); Marshal.WriteInt32(fmt, 8, 192000); Marshal.WriteInt16(fmt, 12, 4); Marshal.WriteInt16(fmt, 14, 16); Marshal.WriteInt16(fmt, 16, 0);
                int hr = client.Initialize(0, 0x00020000u | 0x00040000u | 0x80000000u | 0x08000000u, 200000, 0, fmt, IntPtr.Zero);   // loopback | event | auto-convert | default SRC
                if (hr < 0) return $"Initialize failed (0x{hr:X8})";
                ev = CreateEvent(IntPtr.Zero, false, false, null); client.SetEventHandle(ev);
                var gi = typeof(ICapture).GUID; object o; hr = client.GetService(ref gi, out o);
                if (hr < 0) return $"GetService failed (0x{hr:X8})";
                var cap = (ICapture)o;
                client.Start();
                double sum = 0; long samples = 0; var end = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < end)
                {
                    WaitForSingleObject(ev, 100);
                    uint n; cap.GetNextPacketSize(out n);
                    while (n > 0)
                    {
                        IntPtr data; uint fr, fl; ulong dp, qp;
                        if (cap.GetBuffer(out data, out fr, out fl, out dp, out qp) < 0) break;
                        if ((fl & 2) == 0)
                            for (int i = 0; i < fr * 2; i++) { double v = Math.Abs(Marshal.ReadInt16(data, i * 2) / 32768.0); if (v > peak) peak = v; sum += v * v; samples++; }
                        else samples += fr * 2;
                        cap.ReleaseBuffer(fr); cap.GetNextPacketSize(out n);
                    }
                }
                client.Stop();
                rms = samples > 0 ? Math.Sqrt(sum / samples) : 0;
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally
            {
                if (ev != IntPtr.Zero) CloseHandle(ev);
                if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
                if (pv != IntPtr.Zero) Marshal.FreeHGlobal(pv);
                if (prm != IntPtr.Zero) Marshal.FreeHGlobal(prm);
            }
        }
    }
}
