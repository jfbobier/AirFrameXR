// Shared by AirFrameXR.exe (runtime) and configure.exe: install layout, JSON, the general log, headset discovery, the host
// process, the system check, OpenXR registration and the common window header. .NET Framework 4.8 (part of Windows 10/11).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

// What Visual Studio adds to every project: WinForms' per-monitor DPI support (app.config, bin\*.config) only applies to an assembly that says it targets 4.7 or newer.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]

namespace AirFrameXR
{
    public static class Version
    {
        public const string Number = "1.0.0";
        /// "1.0.0", plus " (debug build)" for a package built with the per-frame telemetry (bin\flavour.txt, written by the packaging step; docs/DEVELOPMENT.md)
        public static readonly string Text = Number + (IsDebugBuild() ? " (debug build)" : "");
        static bool IsDebugBuild()
        {
            try { return File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flavour.txt")).Trim() == "debug"; }
            catch { return false; }   // a developer run (uitest, a bare compile): no file, nothing said
        }
    }

    /// The portable folder: everything next to the exe, config/ and logs/ inside it.
    public static class Paths
    {
        /// The package root. Everything but the launcher lives in bin\ (AirFrameXR.exe, the launcher, is the only file in the root): their folder's parent is the root.
        public static readonly string Dir = RootOf(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
        static string RootOf(string d) => string.Equals(Path.GetFileName(d), "bin", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(d) ?? d) : d;
        public static string Bin => Path.Combine(Dir, "bin");
        public static string Config => Path.Combine(Dir, "config");
        public static string Logs => Path.Combine(Dir, "logs");
        public static string Docs => Path.Combine(Dir, "docs");
        public static string Session => Path.Combine(Config, "session.json");
        public static string SessionDefault => Path.Combine(Config, "session.default.json");
        public static string Settings => Path.Combine(Config, "airframexr.json");
        // before 1.0 the project was called VisionALVR (config\visionalvr.json): a folder carried over keeps its settings, the old file stays as a backup
        static Paths() { try { var old = Path.Combine(Config, "visionalvr.json"); if (!File.Exists(Settings) && File.Exists(old)) File.Copy(old, Settings); } catch { } }
        public static string Host => Path.Combine(Bin, "alvr_host.exe");
        public static string MirrorDesktop => Path.Combine(Bin, "mirrordesktop.exe");
        public static string Manifest => Path.Combine(Bin, "virtualdesktop-openxr.json");
        public static string RegisterScript => Path.Combine(Bin, "register_openxr_runtime.ps1");
        public static string Configure => Path.Combine(Bin, "configure.exe");
        public static string Runtime => Path.Combine(Bin, "streamer.exe");
    }

    /// config\games\<exe name without .exe>.json, one file per game, written by the streamer (tools/host/src/games.rs documents the layout).
    public static class Games
    {
        public static string Dir => Path.Combine(Paths.Config, "games");
        public static string Stem(string exe)
        {
            var n = Path.GetFileName(exe ?? "");
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            foreach (var c in "<>:\"/\\|?*") n = n.Replace(c, '_');
            return n.Length == 0 ? "unknown" : n;
        }
        public static string FileOf(string exe) => Path.Combine(Dir, Stem(exe) + ".json");
        public static Dictionary<string, object> Load(string exe) => Json.Load(FileOf(exe));
        public static IEnumerable<string> Files() => Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.json") : new string[0];
        /// The desktop mirror is a game to the engine (it keeps its record and log) but not to the person: no list, label, advice or clean-up shows it.
        public static bool IsHidden(string exe) => string.Equals(Path.GetFileName(exe ?? ""), "mirrordesktop.exe", StringComparison.OrdinalIgnoreCase);
        /// The same for a record file name (config\games\mirrordesktop.json).
        public static bool IsHiddenFile(string file) => IsHidden(Path.GetFileNameWithoutExtension(file) + ".exe");
    }

    /// One zip for a bug report: a game's record (when given), the general log, the newest debug run, the DLL install record, info.txt. No dialog of its own and
    /// nothing is started: the caller shows where the file went.
    public static class LogExport
    {
        public static string DefaultName(string stem) => $"AirFrameXR-{(string.IsNullOrEmpty(stem) ? "logs" : stem)}-{DateTime.Now:yyyyMMdd-HHmm}.zip";

        /// Writes the zip to `path` (replacing a file there) and returns the entry names.
        public static List<string> Write(string path, string stem)
        {
            var names = new List<string>();
            if (File.Exists(path)) File.Delete(path);
            using (var z = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            {
                void add(string file, string name)
                {
                    if (!File.Exists(file)) return;
                    // the logs are being written by the streamer: read with sharing
                    using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = z.CreateEntry(name).Open()) src.CopyTo(dst);
                    names.Add(name);
                }
                if (!string.IsNullOrEmpty(stem)) add(Games.FileOf(stem), "game/" + stem + ".json");
                add(Path.Combine(Paths.Logs, "AirFrameXR.log"), "AirFrameXR.log");
                add(Path.Combine(Paths.Logs, "mirrordesktop.log"), "mirrordesktop.log");   // the desktop mirror's own log (what it saw of the controllers and of a game starting)
                var dbg = Path.Combine(Paths.Logs, "debug");
                if (Directory.Exists(dbg))
                {
                    var newest = new DirectoryInfo(dbg).GetDirectories().OrderByDescending(d => d.LastWriteTime).FirstOrDefault();
                    if (newest != null) foreach (var f in newest.GetFiles()) add(f.FullName, "debug/" + newest.Name + "/" + f.Name);
                }
                using (var w = new StreamWriter(z.CreateEntry("info.txt").Open())) w.WriteLine($"AirFrameXR {Version.Text}\n{DateTime.Now:s}\n{Environment.OSVersion}");
                names.Add("info.txt");
            }
            Log.Write("INFO", "logs exported to " + path);
            return names;
        }

        /// Save dialog (disposed before the work starts), the export, and the inline result in `status`: "Saved to <file>" with an Open folder link. Never starts Explorer itself.
        public static void Run(IWin32Window owner, string stem, Label status, LinkLabel open)
        {
            string path;
            using (var dlg = new SaveFileDialog { Filter = "Zip|*.zip", FileName = DefaultName(stem) })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            try
            {
                Write(path, stem);
                status.ForeColor = Color.SeaGreen; status.Text = "Saved to " + path;
                open.Tag = path; open.Visible = true;
            }
            catch (Exception ex) { status.ForeColor = Color.Firebrick; status.Text = "Export failed: " + ex.Message; open.Visible = false; Log.Write("ERROR", "log export failed: " + ex.Message); }
            status.Visible = true;
        }

        public static void Reveal(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }
    }

    /// The DLSS motion-vector extractor (one DLL) that releases up to 0.6 copied next to a game's exe on request. Releases no longer ship or install it; what
    /// is left is the removal of the copies AirFrameXR made. config\mv_dll.json recorded each install (file, folder, MD5), and only a file that still has the
    /// recorded MD5 is deleted: anything else in the game folder (someone else's proxy DLL, a copy replaced since) is left alone.
    public static class MvDll
    {
        static string Record => Path.Combine(Paths.Config, "mv_dll.json");

        static string Md5(string path)
        {
            try { using (var m = System.Security.Cryptography.MD5.Create()) using (var f = File.OpenRead(path)) return BitConverter.ToString(m.ComputeHash(f)).Replace("-", "").ToLowerInvariant(); }
            catch { return ""; }
        }

        static Dictionary<string, object> Installs(Dictionary<string, object> r)
        {
            if (!(r.TryGetValue("installs", out var o) && o is Dictionary<string, object> d)) { d = new Dictionary<string, object>(); r["installs"] = d; }
            return d;
        }
        static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string ?? "" : "";

        /// The file name of the copy recorded for this game, or null when AirFrameXR installed nothing there.
        public static string Installed(string stem)
        {
            var e = Installs(Json.Load(Record)).TryGetValue(stem, out var o) ? o as Dictionary<string, object> : null;
            return e != null && Str(e, "file").Length > 0 ? Str(e, "file") : null;
        }

        /// Deletes the recorded copy when it is still the file AirFrameXR put there, and forgets the record either way. Returns a message for the user.
        public static string Uninstall(string stem)
        {
            var r = Json.Load(Record);
            var all = Installs(r);
            if (!(all.TryGetValue(stem, out var o) && o is Dictionary<string, object> e)) return "Nothing recorded as installed for this game by AirFrameXR.";
            string name = Str(e, "file"), dir = Str(e, "dir");
            var target = name.Length > 0 && dir.Length > 0 ? Path.Combine(dir, name) : null;
            string msg;
            if (target == null || !File.Exists(target)) msg = "The file is no longer in the game folder.";
            else if (Md5(target) != Str(e, "md5")) msg = $"{name} in {dir} has changed since AirFrameXR installed it; it was left in place.";
            else if (!DeleteMaybeElevated(target)) return "The file was not removed (administrator permission refused?).";
            else msg = $"Removed {name} from {dir}.";
            all.Remove(stem);
            Json.Save(Record, r);
            Log.Write("INFO", $"motion-vector DLL record removed for {stem}: {msg}");
            return msg;
        }

        static bool DeleteMaybeElevated(string path)
        {
            try { File.Delete(path); return true; } catch (UnauthorizedAccessException) { } catch (IOException) { }
            return Elevated($"/c del /f /q \"{path}\"") && !File.Exists(path);
        }
        static bool Elevated(string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("cmd.exe", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                p.WaitForExit(30000);
                return true;
            }
            catch { return false; }   // the user refused the prompt
        }
    }

    /// JSON as Dictionary / object[] trees (JavaScriptSerializer), written back indented so session.json stays hand-editable.
    public static class Json
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };

        public static Dictionary<string, object> Parse(string text) => Ser.DeserializeObject(text) as Dictionary<string, object> ?? new Dictionary<string, object>();

        public static Dictionary<string, object> Load(string path)
        {
            try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, object>(); }
            catch (Exception e) { Log.Write("WARN", $"cannot read {path}: {e.Message}"); return new Dictionary<string, object>(); }
        }

        public static void Save(string path, Dictionary<string, object> root)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            Write(sb, root, 0);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false)); // no BOM: ALVR's serde_json rejects it
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        static void Write(StringBuilder sb, object v, int ind)
        {
            string pad(int n) => new string(' ', n * 2);
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: sb.Append(Ser.Serialize(s)); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case Dictionary<string, object> d:
                    if (d.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{\n");
                    int i = 0;
                    foreach (var kv in d)
                    {
                        sb.Append(pad(ind + 1)).Append(Ser.Serialize(kv.Key)).Append(": ");
                        Write(sb, kv.Value, ind + 1);
                        sb.Append(++i < d.Count ? ",\n" : "\n");
                    }
                    sb.Append(pad(ind)).Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    var items = list.Cast<object>().ToList();
                    if (items.Count == 0) { sb.Append("[]"); break; }
                    sb.Append("[\n");
                    for (int k = 0; k < items.Count; k++)
                    {
                        sb.Append(pad(ind + 1));
                        Write(sb, items[k], ind + 1);
                        sb.Append(k + 1 < items.Count ? ",\n" : "\n");
                    }
                    sb.Append(pad(ind)).Append(']');
                    break;
                case double dd: sb.Append(Num(dd)); break;
                case float ff: sb.Append(Num(ff)); break;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); break;
                default: sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
        }

        static string Num(double d) => (d == Math.Floor(d) && Math.Abs(d) < 1e15) ? d.ToString("0.0", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

        public static object Get(Dictionary<string, object> root, string path)
        {
            object cur = root;
            foreach (var k in path.Split('.'))
            {
                if (!(cur is Dictionary<string, object> d) || !d.TryGetValue(k, out cur)) return null;
            }
            return cur;
        }

        public static void Set(Dictionary<string, object> root, string path, object value)
        {
            var keys = path.Split('.');
            var cur = root;
            for (int i = 0; i < keys.Length - 1; i++)
            {
                if (!(cur.TryGetValue(keys[i], out var next) && next is Dictionary<string, object> nd))
                {
                    nd = new Dictionary<string, object>();
                    cur[keys[i]] = nd;
                }
                cur = nd;
            }
            cur[keys[keys.Length - 1]] = value;
        }

        /// Removes the key (a setting back at its default stays out of the file); true when there was one.
        public static bool Remove(Dictionary<string, object> root, string path)
        {
            var keys = path.Split('.');
            object cur = root;
            for (int i = 0; i < keys.Length - 1; i++) if (!(cur is Dictionary<string, object> d && d.TryGetValue(keys[i], out cur))) return false;
            return cur is Dictionary<string, object> last && last.Remove(keys[keys.Length - 1]);
        }

        public static double Num(Dictionary<string, object> root, string path, double def)
        {
            var v = Get(root, path);
            try { return v == null || v is string || v is bool ? def : Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static string Str(Dictionary<string, object> root, string path, string def) => Get(root, path) as string ?? def;
        public static bool Bool(Dictionary<string, object> root, string path, bool def) => Get(root, path) is bool b ? b : def;
        public static string Serialize(object o) => Ser.Serialize(o);
    }

    /// The general log logs\AirFrameXR.log, shared with the host and the shim (one appended write per line).
    public static class Log
    {
        public static string Component = "gui";
        static readonly object Lock = new object();

        public static void Write(string level, string msg)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Paths.Logs);
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Component}] {level,-5} {msg}\r\n";
                    using (var fs = new FileStream(Path.Combine(Paths.Logs, "AirFrameXR.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var b = Encoding.UTF8.GetBytes(line);
                        fs.Write(b, 0, b.Length);
                    }
                }
            }
            catch { }
        }
    }

    /// config\airframexr.json: AirFrameXR's own settings (the ALVR ones stay in session.json).
    public class AppSettings
    {
        /// settings that were removed in 1.0.1 (they measured no benefit): dropped from the file at the next save
        static readonly string[] Retired = { "send_pacing", "intake_depth", "pick_policy", "client_queue_drain", "follow_client_rate", "boundary_offset_ms", "frame_gen_flow_scale",
            "frame_gen_hold_age", "frame_gen_perf", "frame_gen_lag_ms", "gpu_sched_class" };
        public Dictionary<string, object> Root = Load();
        static Dictionary<string, object> Load() { var r = Json.Load(Paths.Settings); foreach (var k in Retired) Json.Remove(r, "video." + k); return r; }
        public string HeadsetHost { get => Json.Str(Root, "headset.hostname", ""); set => Json.Set(Root, "headset.hostname", value); }
        public string HeadsetName { get => Json.Str(Root, "headset.name", ""); set => Json.Set(Root, "headset.name", value); }
        public string HeadsetIp { get => Json.Str(Root, "headset.ip", ""); set => Json.Set(Root, "headset.ip", value); }
        public string EncodeProfile { get => Json.Str(Root, "video.encode_profile", "foveated"); set => Json.Set(Root, "video.encode_profile", value); }
        /// NVENC split-frame encoding ("3" = one strip per engine, up to 3; "off"): on by default, the reason the project exists on a multi-engine GPU
        /// the Logs window deletes the records of games whose exe no longer exists (on by default)
        public bool AutoDeleteGameLogs { get => Json.Bool(Root, "logs.auto_delete_uninstalled", true); set => Json.Set(Root, "logs.auto_delete_uninstalled", value); }
        /// the wizard asked the chroma-key question once (headsets other than the Vision Pro): the answer is in session.json, this only stops the repeat
        public bool ChromaAsked { get => Json.Bool(Root, "chroma.asked", false); set => Json.Set(Root, "chroma.asked", value); }
        /// the key that recentres while playing (the streamer polls it): End by default; "off" disables
        public string RecenterKey { get => Json.Str(Root, "controls.recenter_key", "End"); set => Json.Set(Root, "controls.recenter_key", value); }
        /// the Windows desktop on a virtual screen between games, the right hand (pinch = click) as the mouse; on by default while chroma-key passthrough is off (the idle frame is the room otherwise), until the user chooses
        public bool MirrorDesktop { get => Json.Get(Root, "display.mirror_desktop") != null ? Json.Bool(Root, "display.mirror_desktop", false) : !Chroma.Enabled(Session.Load()); set => Json.Set(Root, "display.mirror_desktop", value); }
        public bool SplitEncode { get => Json.Str(Root, "video.split_encode", "3") != "off"; set => Json.Set(Root, "video.split_encode", value ? "3" : "off"); }
        public string IdleRgb { get => Json.Str(Root, "video.idle_rgb", Chroma.DefaultRgb); set => Json.Set(Root, "video.idle_rgb", value); }
        /// frame generation (docs/FRAME_GENERATION.md): the game renders every other frame, the streamer interpolates the one in between (off by default)
        public bool FrameGen { get => Json.Bool(Root, "video.frame_gen", false); set => Json.Set(Root, "video.frame_gen", value); }
        // frame generation rate: "third" | "half" (default) | "two_thirds" (the game's rate as a fraction of the display rate, the streamer generates the
        // frames in between; docs/FRAME_GENERATION.md), "uncapped" (experimental, from the Advanced settings; docs/FRAME_GENERATION_UNCAPPED.md)
        public string FrameGenRate { get => Json.Str(Root, "video.frame_gen_rate", "half"); set => Json.Set(Root, "video.frame_gen_rate", value); }
        /// uncapped mode's minimum spacing of the game's releases in fps (0 = none) and its content lag in ms (0 = automatic); the streamer takes a change live
        /// the share (percent) of the headset's field of view the game is told, per edge (Advanced picture and FOV settings); 100 = all of it
        public double FovOuterPct => Json.Num(Root, "video.fov_outer_pct", 100);
        public double FovInnerPct => Json.Num(Root, "video.fov_inner_pct", 100);
        public double FovUpperPct => Json.Num(Root, "video.fov_upper_pct", 100);
        public double FovLowerPct => Json.Num(Root, "video.fov_lower_pct", 100);
        public double FrameGenCapFps { get => Json.Num(Root, "video.frame_gen_cap_fps", 0); set => Json.Set(Root, "video.frame_gen_cap_fps", value); }
        // docs/FRAME_GENERATION.md "Game profiles": the launcher sets the rate from the game's recorded runs on the connected headset (on by default)
        public bool FrameGenRecommended { get => Json.Bool(Root, "video.frame_gen_recommended", true); set => Json.Set(Root, "video.frame_gen_recommended", value); }
        /// arrival margin of the phase lock in ms (host default 7; the host reads it at start) and the GPU keepalive duty in % (0 = off); 0 = not set
        public double PhaseMarginMs { get => Json.Num(Root, "video.phase_margin_ms", 0); set => Json.Set(Root, "video.phase_margin_ms", value); }
        public int GpuKeepalive { get => (int)Json.Num(Root, "video.gpu_keepalive", 0); set => Json.Set(Root, "video.gpu_keepalive", (double)value); }
        /// the Windows power plan the streamer switches to while it runs (a GUID; "" = leave it alone); the plan that was active comes back when the streamer closes
        public string PowerPlan { get => Json.Str(Root, "system.power_plan", ""); set => Json.Set(Root, "system.power_plan", value); }
        /// the PC's own output is muted while the streamer routes the game audio to the virtual cable (on by default)
        public bool MuteSpeakers { get => Json.Bool(Root, "audio.mute_pc", true); set => Json.Set(Root, "audio.mute_pc", value); }
        /// how the game audio is captured: "process" = Windows' process loopback (no virtual cable, the default output stays as it is), "device" = the VB-CABLE path
        /// (the streamer makes the cable the default output, ALVR captures it)
        public string AudioCapture { get => Json.Str(Root, "audio.capture", "process") == "device" ? "device" : "process"; set => Json.Set(Root, "audio.capture", value == "device" ? "device" : "process"); }
        public bool Debug { get => Json.Bool(Root, "debug", false); set => Json.Set(Root, "debug", value); }
        /// display (live in AirFrameXR.exe): gamma 1.2 prefilled (users' AVP experience with ALVR), the rest neutral
        public const double DefaultGamma = 1.2;
        public double Gamma { get => Json.Num(Root, "display.gamma", DefaultGamma); set => Json.Set(Root, "display.gamma", value); }
        public double Brightness { get => Json.Num(Root, "display.brightness", 0); set => Json.Set(Root, "display.brightness", value); }
        public double Contrast { get => Json.Num(Root, "display.contrast", 0); set => Json.Set(Root, "display.contrast", value); }
        public double Saturation { get => Json.Num(Root, "display.saturation", 0); set => Json.Set(Root, "display.saturation", value); }
        public double Sharpening { get => Json.Num(Root, "display.sharpening", 0); set => Json.Set(Root, "display.sharpening", value); }
        public bool HasDisplay => Json.Get(Root, "display.gamma") != null;
        /// headset height above the floor handed to games (m), re-anchored live; 1.3 = a seated adult. 0 = the headset's floor estimate
        public const double DefaultHeight = 1.3;
        public double Height { get => Json.Num(Root, "headset.height_m", DefaultHeight); set => Json.Set(Root, "headset.height_m", value); }
        /// "auto" = the headset's own floor estimate (height 0 to the engine), "manual" = Height. Someone who already set a height stays on manual; a new user starts on auto.
        public string HeightMode
        {
            get { var m = Json.Str(Root, "display.height_mode", ""); return m == "auto" || m == "manual" ? m : (Json.Get(Root, "headset.height_m") == null || Json.Num(Root, "headset.height_m", 0) <= 0 ? "auto" : "manual"); }
            set => Json.Set(Root, "display.height_mode", value);
        }
        public void Save() => Json.Save(Paths.Settings, Root);
    }

    /// What the hardware encoder can take: HEVC frames of at most 8192 pixels across, the two eyes side by side (4096 per eye), and 8192 high. A larger size makes
    /// the streamer exit at start (2026-10-24: an eye of 4288 pixels), so the settings refuse it with the reason.
    public static class Nvenc
    {
        public const int MaxEyeWidth = 4096, MaxEyeHeight = 8192;
        /// null when the per-eye size fits the encoder, else what to tell the user
        public static string CheckEye(int eyeW, int eyeH)
        {
            if (eyeW <= MaxEyeWidth && eyeH <= MaxEyeHeight) return null;
            return $"{eyeW} x {eyeH} per eye is more than the hardware encoder can encode: HEVC takes at most {2 * MaxEyeWidth} pixels across, so {MaxEyeWidth} per eye side by side (and {MaxEyeHeight} high).";
        }
    }

    /// Chroma-key passthrough: the headset turns the idle colour into the room. It is optional and off by default; the key colour lives in session.json (what the ALVR
    /// client keys), the same colour is what the streamer shows between games. Off: the streamer's idle frame is black, and the desktop mirror is on unless chosen otherwise.
    public static class Chroma
    {
        public const string DefaultRgb = "00ff00";
        const string P = "session_settings.video.passthrough.";
        public static bool Enabled(Dictionary<string, object> s) => Json.Bool(s, P + "enabled", false);
        /// the key colour as "rrggbb"
        public static string Rgb(Dictionary<string, object> s) => $"{(int)Json.Num(s, P + "content.RgbChromaKey.red", 0):x2}{(int)Json.Num(s, P + "content.RgbChromaKey.green", 255):x2}{(int)Json.Num(s, P + "content.RgbChromaKey.blue", 0):x2}";
        /// Sets passthrough on or off in `s` (not saved) and the idle colour in `st` (not saved): the key colour when on, black when off. `rgb` = "rrggbb".
        public static void Apply(Dictionary<string, object> s, AppSettings st, bool on, string rgb)
        {
            rgb = (rgb ?? DefaultRgb).TrimStart('#').ToLowerInvariant();
            if (rgb.Length != 6) rgb = DefaultRgb;
            Json.Set(s, P + "enabled", on);
            Json.Set(s, P + "content.variant", "RgbChromaKey");
            Json.Set(s, P + "content.RgbChromaKey.red", Convert.ToInt32(rgb.Substring(0, 2), 16));
            Json.Set(s, P + "content.RgbChromaKey.green", Convert.ToInt32(rgb.Substring(2, 2), 16));
            Json.Set(s, P + "content.RgbChromaKey.blue", Convert.ToInt32(rgb.Substring(4, 2), 16));
            st.IdleRgb = on ? rgb : "000000";
        }
    }

    /// Windows power plans through powercfg (what startairframexr.bat did): the configured plan is set while the streamer runs and the previous one is put back at the end.
    /// The plan to restore is written to logs\power_plan.txt first, so a streamer that crashed is undone at the next start.
    public static class PowerPlan
    {
        public sealed class Plan { public string Guid, Name; public bool Active; }
        static string StateFile => Path.Combine(Paths.Logs, "power_plan.txt");
        static readonly System.Text.RegularExpressions.Regex Line = new System.Text.RegularExpressions.Regex(@"([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s+\((.*?)\)\s*(\*)?");
        static string previous;

        static string Powercfg(string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("powercfg.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.Default });
                var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); return p.ExitCode == 0 ? o : null;
            }
            catch { return null; }
        }

        public static List<Plan> List()
        {
            var res = new List<Plan>();
            foreach (var l in (Powercfg("/list") ?? "").Split('\n'))
            {
                var m = Line.Match(l);
                if (m.Success) res.Add(new Plan { Guid = m.Groups[1].Value.ToLowerInvariant(), Name = m.Groups[2].Value, Active = m.Groups[3].Success });
            }
            return res;
        }

        /// Sets `wanted` (a GUID or a plan name) and remembers the plan that was active; returns the log line.
        public static string Apply(string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return null;
            Restore();   // a leftover from a streamer that did not close cleanly
            var plans = List();
            var want = plans.FirstOrDefault(x => string.Equals(x.Guid, wanted.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
            if (want == null) return "power plan \"" + wanted + "\" not found; left as it is";
            var cur = plans.FirstOrDefault(x => x.Active);
            if (cur != null && cur.Guid == want.Guid) return $"power plan {want.Name} is already active";
            if (Powercfg("/setactive " + want.Guid) == null) return $"could not set the power plan {want.Name}";
            previous = cur?.Guid;
            if (previous != null) { try { Directory.CreateDirectory(Paths.Logs); File.WriteAllText(StateFile, previous); } catch { } }
            return $"power plan {want.Name} set (was {cur?.Name ?? "unknown"}, restored when the streamer closes)";
        }

        /// Puts the remembered plan back (this run's, or the one a crashed run left in the file); returns the log line, null when there was nothing to restore.
        public static string Restore()
        {
            var g = previous;
            previous = null;
            try { if (g == null && File.Exists(StateFile)) g = File.ReadAllText(StateFile).Trim(); } catch { }
            try { File.Delete(StateFile); } catch { }
            if (string.IsNullOrEmpty(g)) return null;
            return Powercfg("/setactive " + g) != null ? "power plan restored" : "could not restore the power plan " + g;
        }
    }

    /// The session (ALVR settings, paired headset). Created from session.default.json on first use.
    /// A button that stays pressed: Checked / CheckedChanged like a check box, the look of one Segmented segment.
    public class ToggleButton : Button
    {
        bool on;
        public event EventHandler CheckedChanged;
        public ToggleButton() { AutoSize = true; FlatStyle = FlatStyle.Flat; Padding = new Padding(6, 2, 6, 2); UseVisualStyleBackColor = false; BackColor = Color.White; }
        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; BackColor = on ? Color.FromArgb(0xCF, 0xDD, 0xF7) : Color.White; CheckedChanged?.Invoke(this, EventArgs.Empty); }
        }
        protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !Checked; }
    }

    /// A segmented choice: a row of toggle buttons, one selected (Value = its index). Enabled greys the whole row.
    public class Segmented : FlowLayoutPanel
    {
        readonly List<RadioButton> items = new List<RadioButton>();
        public event EventHandler ValueChanged;
        public Segmented(params string[] texts)
        {
            AutoSize = true; WrapContents = false; Margin = new Padding(0);
            for (int i = 0; i < texts.Length; i++)
            {
                var r = new RadioButton { Text = texts[i], Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, AutoSize = true, Margin = new Padding(0, 0, 2, 0), Padding = new Padding(6, 2, 6, 2), TextAlign = ContentAlignment.MiddleCenter, AccessibleName = texts[i], Tag = i };
                r.FlatAppearance.CheckedBackColor = Color.FromArgb(0xCF, 0xDD, 0xF7);
                r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) ValueChanged?.Invoke(this, EventArgs.Empty); };
                items.Add(r); Controls.Add(r);
            }
        }
        public int Value
        {
            get { for (int i = 0; i < items.Count; i++) if (items[i].Checked) return i; return -1; }
            set { if (value >= 0 && value < items.Count) items[value].Checked = true; }
        }
    }

    /// Brings a working ALVR session.json into config\: the user points at their ALVR folder (or the file), we look for session.json around it, check it is an ALVR session,
    /// keep a backup of the current one and copy it over.
    public static class AlvrImport
    {
        public static string Guess()
        {
            foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\ALVR", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "\\ALVR",
                                      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\ALVR", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads" })
                if (Directory.Exists(d)) return d;
            return "";
        }

        /// session.json files found in `path` (a file or a folder): the folder itself, config\, its parent, then two levels down.
        public static List<string> Find(string path)
        {
            var r = new List<string>();
            if (File.Exists(path)) { r.Add(path); return r; }
            if (!Directory.Exists(path)) return r;
            foreach (var c in new[] { Path.Combine(path, "session.json"), Path.Combine(path, "config", "session.json"), Path.Combine(path, "..", "session.json") })
                if (File.Exists(c)) r.Add(Path.GetFullPath(c));
            if (r.Count == 0)
                try
                {
                    foreach (var d1 in Directory.GetDirectories(path))
                    {
                        var f1 = Path.Combine(d1, "session.json"); if (File.Exists(f1)) r.Add(f1);
                        foreach (var d2 in Directory.GetDirectories(d1)) { var f2 = Path.Combine(d2, "session.json"); if (File.Exists(f2)) r.Add(f2); }
                    }
                }
                catch { }   // an unreadable folder: what was found so far stands
            return r.Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !f.StartsWith(Paths.Config, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public static string Check(string file)
        {
            var d = Json.Load(file);
            return d.ContainsKey("session_settings") ? null : "That file is not an ALVR session (no session_settings in it).";
        }

        /// Backs up config\session.json and replaces it. Returns the backup path, or null with `error`.
        public static string Import(string file, out string error)
        {
            error = Check(file);
            if (error != null) return null;
            try
            {
                Directory.CreateDirectory(Paths.Config);
                string bak = null;
                if (File.Exists(Paths.Session)) { bak = Paths.Session + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"); File.Copy(Paths.Session, bak, true); }
                File.Copy(file, Paths.Session, true);
                return bak ?? "";
            }
            catch (Exception e) { error = "Cannot import: " + e.Message; return null; }
        }

        /// The whole dialog flow: ask for the ALVR folder, pick the file, confirm. True when config\session.json was replaced.
        public static bool Run(IWin32Window owner)
        {
            string pick;
            using (var dlg = new FolderBrowserDialog { Description = "Choose your ALVR folder (where ALVR is installed, or any folder holding the session.json that works for you).", ShowNewFolderButton = false })
            {
                var g = Guess(); if (g.Length > 0) dlg.SelectedPath = g;
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                pick = dlg.SelectedPath;
            }
            var found = Find(pick);
            if (found.Count == 0)
            {
                MessageBox.Show(owner, "No session.json found in that folder or just below it.\nIf you know the file, choose it directly in the next window.", "Import ALVR settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
                using (var f = new OpenFileDialog { Title = "Choose session.json", Filter = "session.json|session.json|JSON (*.json)|*.json", CheckFileExists = true })
                {
                    if (f.ShowDialog(owner) != DialogResult.OK) return false;
                    found.Add(f.FileName);
                }
            }
            var file = found[0];
            if (found.Count > 1)
            {
                var lines = string.Join("\n", found.Select((x, i) => (i + 1) + ". " + x + "  (" + File.GetLastWriteTime(x).ToString("yyyy-MM-dd HH:mm") + ")"));
                if (MessageBox.Show(owner, "Several session.json files were found:\n\n" + lines + "\n\nUse the most recent one?\n" + found.OrderByDescending(File.GetLastWriteTime).First(), "Import ALVR settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
                file = found.OrderByDescending(File.GetLastWriteTime).First();
            }
            if (MessageBox.Show(owner, "Import these ALVR settings?\n\n" + file + "\n\nYour current config\\session.json is kept as a backup next to it.", "Import ALVR settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            var bak = Import(file, out var err);
            if (bak == null) { MessageBox.Show(owner, err, "Import ALVR settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            Log.Write("INFO", "imported ALVR session " + file + (bak.Length > 0 ? ", backup " + bak : ""));
            return true;
        }
    }

    public static class Session
    {
        public static Dictionary<string, object> Load()
        {
            if (!File.Exists(Paths.Session) && File.Exists(Paths.SessionDefault)) File.Copy(Paths.SessionDefault, Paths.Session);
            return Json.Load(Paths.Session);
        }

        /// (hostname, display name, ip) of the trusted headset, or null.
        public static Tuple<string, string, string> Headset(Dictionary<string, object> s)
        {
            if (!(Json.Get(s, "client_connections") is Dictionary<string, object> cc)) return null;
            foreach (var kv in cc)
            {
                if (!(kv.Value is Dictionary<string, object> c) || !(c.TryGetValue("trusted", out var t) && t is bool tb && tb)) continue;
                var ip = c.TryGetValue("current_ip", out var cur) && cur is string cs && cs.Length > 0 ? cs
                       : (c.TryGetValue("manual_ips", out var m) && m is object[] a && a.Length > 0 ? a[0] as string : "");
                return Tuple.Create(kv.Key, c.TryGetValue("display_name", out var n) ? n as string ?? kv.Key : kv.Key, ip ?? "");
            }
            return null;
        }

        /// Pairs exactly one headset: trusted, dialled at `ip` (ALVR still re-finds it by name if its address changes).
        public static void Pair(Dictionary<string, object> s, string hostname, string name, string ip)
        {
            s["client_connections"] = new Dictionary<string, object>
            {
                [hostname] = new Dictionary<string, object>
                {
                    ["display_name"] = name, ["current_ip"] = ip, ["manual_ips"] = new object[] { ip }, ["trusted"] = true,
                    ["connection_state"] = "Disconnected",
                },
            };
        }
    }

    /// "Meta Quest over USB": pure UI. A Quest on a USB cable is not on the network; ADBForwarder (ALVR's console app) forwards the ALVR ports to it
    /// through adb, and the Quest then reaches the PC as 127.0.0.1 (nothing is broadcast, so it has to be entered by address). Nothing here touches adb.
    public static class QuestUsb
    {
        public const string Address = "127.0.0.1";
        public const string DownloadUrl = "https://github.com/alvr-org/ADBForwarder";

        /// A small dialog with the steps and the download link; true when the user chose to use 127.0.0.1 as the headset address.
        /// The "Meta Quest on a USB cable?" link for the bottom of a headset page: hidden, shown by `Arm` when nothing was found after a few seconds.
        public static LinkLabel Hint(Action onClick)
        {
            var l = new LinkLabel { Text = "Meta Quest on a USB cable?", AutoSize = true, Font = Ui.Body, Margin = new Padding(3, 8, 3, 3), Visible = false };
            l.LinkClicked += (s, e) => onClick();
            return l;
        }

        /// Shows `hint` after `seconds` unless `seen()` says a headset was found by then.
        public static void Arm(LinkLabel hint, Func<bool> seen, int seconds = 8)
        {
            hint.Visible = false;
            var t = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (!hint.IsDisposed) hint.Visible = !seen(); };
            t.Start();
        }

        public static bool Ask(IWin32Window owner)
        {
            using (var f = new DpiForm())
            {
                f.Text = "AirFrameXR \u2013 Meta Quest over USB";
                f.Icon = Ui.AppIcon();
                f.AutoScaleDimensions = new SizeF(96F, 96F);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.Font = Ui.Body;
                f.BackColor = Color.White;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.AutoSize = true;
                f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(12) };
                var title = Ui.Line("Meta Quest over a USB cable", Ui.Big);
                title.ForeColor = Ui.Accent;
                p.Controls.Add(title);
                p.Controls.Add(Ui.Line("A Quest on a USB cable is not on the network, so it is not found by itself. Two ways:"));
                p.Controls.Add(Ui.Line("1. USB tethering (Quest: Settings, Network). The headset then has a network address like any other: use Search again."));
                p.Controls.Add(Ui.Line("2. ADBForwarder (a small program from the ALVR project): download it, run it with the Quest plugged in and Developer Mode on, " +
                                       "open the ALVR app on the headset, then use " + Address + " as the headset address (button below)."));
                var link = new LinkLabel { Text = DownloadUrl, AutoSize = true, Font = Ui.Body, Margin = new Padding(3) };
                link.LinkClicked += (s, e) => { try { System.Diagnostics.Process.Start(DownloadUrl); } catch { } };
                p.Controls.Add(link);
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
                var cancel = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
                var use = new Button { Text = "Use " + Address + " as the headset address", AutoSize = true, Font = Ui.Bold, DialogResult = DialogResult.OK };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(use);
                p.Controls.Add(buttons);
                f.Controls.Add(p);
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }

    /// Headset discovery, done by alvr_host.exe (`--discover`, ALVR's own code: mDNS/Bonjour for the Vision Pro, which never
    /// broadcasts on UDP 9943, and the UDP 9943 broadcast of Quest-style clients). The GUI only shows the `headset_seen` events.
    public class Discovery : IDisposable
    {
        public class Found { public string Hostname, Ip, Protocol, ServerProtocol, Via; public bool Compatible; public DateTime Seen; }
        public event Action<Found> OnFound;
        public event Action<string> OnError;
        HostProcess host;

        public string Start(double seconds = 3600)
        {
            host = new HostProcess();
            host.OnEvent += (name, d) =>
            {
                if (name == "headset_seen" && d != null)
                    OnFound?.Invoke(new Found
                    {
                        Hostname = Str(d, "hostname"), Ip = Str(d, "ip"), Protocol = Str(d, "protocol"), ServerProtocol = Str(d, "server_protocol"),
                        Via = Str(d, "via"), Compatible = d.TryGetValue("compatible", out var c) && c is bool b && b, Seen = DateTime.Now,
                    });
                else if (name == "discovery_error" && d != null) OnError?.Invoke(Str(d, "message"));
            };
            return host.Start($"--install-dir \"{Paths.Dir}\" --discover {seconds.ToString(CultureInfo.InvariantCulture)}");
        }

        static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";

        public void Dispose() { try { host?.Stop(1000); } catch { } }
    }

    /// alvr_host.exe as a child process: JSON events from its stdout, commands to its stdin (end of stdin = it quits).
    public class HostProcess
    {
        public event Action<string, Dictionary<string, object>> OnEvent;
        public event Action<int> OnExit;
        public event Action<string> OnStderr;
        Process p;
        public bool Running => p != null && !p.HasExited;

        public string Start(string args)
        {
            if (!File.Exists(Paths.Host)) return "alvr_host.exe is missing from " + Paths.Dir;
            var psi = new ProcessStartInfo(Paths.Host, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, WorkingDirectory = Paths.Dir, StandardOutputEncoding = Encoding.UTF8,
            };
            p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null || !e.Data.StartsWith("{")) return;
                try
                {
                    var d = Json.Parse(e.Data);
                    OnEvent?.Invoke(d.TryGetValue("event", out var n) ? n as string : "", d.TryGetValue("data", out var x) ? x as Dictionary<string, object> : null);
                }
                catch { }
            };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) OnStderr?.Invoke(e.Data); };
            p.Exited += (s, e) => OnExit?.Invoke(SafeExitCode());
            try { p.Start(); } catch (Exception e) { return "cannot start alvr_host.exe: " + e.Message; }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return null;
        }

        int SafeExitCode() { try { return p.ExitCode; } catch { return -1; } }

        public event Action<string> OnSend;   // every command, running or not (harness/streamer_events.py)
        public void Send(string line) { OnSend?.Invoke(line); try { if (Running) { p.StandardInput.WriteLine(line); p.StandardInput.Flush(); } } catch { } }

        /// Asks the host to quit (it un-mutes the PC speakers on the way out); kills it after the timeout.
        public void Stop(int timeoutMs = 6000)
        {
            if (!Running) return;
            Send("quit");
            try { p.StandardInput.Close(); } catch { }
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
        }
    }

    /// `alvr_host --system-check`: the GPU (NVML architecture: Ada = 8, Blackwell = 10) and ALVR's protocol id.
    public static class SystemCheck
    {
        public static Dictionary<string, object> Run()
        {
            try
            {
                var psi = new ProcessStartInfo(Paths.Host, "--system-check") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    foreach (var line in outp.Split('\n'))
                    {
                        if (!line.TrimStart().StartsWith("{")) continue;
                        var d = Json.Parse(line);
                        if (d.TryGetValue("data", out var x) && x is Dictionary<string, object> data) return data;
                    }
                }
            }
            catch (Exception e) { Log.Write("ERROR", "system check failed: " + e.Message); }
            return null;
        }
    }

    /// HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime == this folder's manifest? Registration runs the script elevated.
    /// OpenXR API layers (the Khronos loader's registry lists): an enabled *implicit* layer is loaded into every OpenXR application, ours included, and
    /// some (hand-tracking and input tools) capture or remap the controllers before any runtime sees them. Reading the lists follows the loader's own
    /// format (see also FredEmmott/openxr-api-layers-gui, MIT, which shows the same lists): ...\Khronos\OpenXR\1\ApiLayers\Implicit|Explicit under HKLM and
    /// HKCU, in the 64-bit and the 32-bit view; a value's name is the layer manifest's path, its DWORD 0 = enabled, anything else = disabled.
    /// Disabling sets the DWORD to 1 (reversible; what AirFrameXR disabled is kept in config\layers_disabled.json so it can be put back).
    public class XrLayer
    {
        public string Hive, Section, Path, Name = "", Description = "", Library = "";
        public int View;           // 64 or 32
        public bool Enabled;
        public bool MayCaptureInput;
        public string KeyPath => @"SOFTWARE\" + (View == 32 ? @"WOW6432Node\" : "") + @"Khronos\OpenXR\1\ApiLayers\" + Section;
        public string Where => $"{Hive} {View}-bit, {Section.ToLowerInvariant()}";
    }

    public static class XrLayers
    {
        static readonly string[] InputHints = { "ultraleap", "leap", "toolkit", "openkneeboard", "neck", "motioncompensation", "hand", "controller", "input", "steamvr", "oculus", "vive" };
        public static string DisabledFile => System.IO.Path.Combine(Paths.Config, "layers_disabled.json");

        public static List<XrLayer> Enumerate()
        {
            var all = new List<XrLayer>();
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    foreach (var section in new[] { "Implicit", "Explicit" })
                    {
                        var l = new XrLayer { Hive = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU", View = view == RegistryView.Registry64 ? 64 : 32, Section = section };
                        try
                        {
                            using (var k = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(l.KeyPath.Replace(@"WOW6432Node\", "")))
                            {
                                if (k == null) continue;
                                foreach (var name in k.GetValueNames())
                                {
                                    var e = new XrLayer { Hive = l.Hive, View = l.View, Section = section, Path = name, Enabled = Convert.ToInt32(k.GetValue(name) ?? 1) == 0 };
                                    try
                                    {
                                        var j = Json.Load(name);
                                        e.Name = Json.Str(j, "api_layer.name", ""); e.Description = Json.Str(j, "api_layer.description", ""); e.Library = Json.Str(j, "api_layer.library_path", "");
                                    }
                                    catch { }
                                    if (e.Name.Length == 0) e.Name = System.IO.Path.GetFileNameWithoutExtension(name);
                                    var hay = (e.Name + " " + e.Description + " " + e.Library + " " + name).ToLowerInvariant();
                                    e.MayCaptureInput = InputHints.Any(h => hay.Contains(h));
                                    all.Add(e);
                                }
                            }
                        }
                        catch { }
                    }
            // the 64-bit and 32-bit views of one key are the same list on most PCs: a layer is shown once per (hive, section, path)
            return all.GroupBy(x => x.Hive + "|" + x.Section + "|" + x.Path.ToLowerInvariant()).Select(g => g.First()).ToList();
        }

        /// Enabled implicit layers: the ones that load into every OpenXR application.
        public static List<XrLayer> ActiveImplicit() => Enumerate().Where(l => l.Enabled && l.Section == "Implicit").ToList();

        static string RegArgs(XrLayer l, int value) =>
            $"reg add \"{l.Hive}\\{l.KeyPath}\" /v \"{l.Path}\" /t REG_DWORD /d {value} /f /reg:{l.View}";

        /// Sets the layers' DWORD (1 = disabled, 0 = enabled): HKCU directly, HKLM through one UAC prompt. True when every one took effect.
        public static bool Set(IEnumerable<XrLayer> layers, bool enable)
        {
            var list = layers.ToList();
            var machine = list.Where(l => l.Hive == "HKLM").ToList();
            foreach (var l in list.Where(l => l.Hive == "HKCU"))
                try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, l.View == 64 ? RegistryView.Registry64 : RegistryView.Registry32).CreateSubKey(l.KeyPath.Replace(@"WOW6432Node\", ""))) k.SetValue(l.Path, enable ? 0 : 1, RegistryValueKind.DWord); } catch { }
            if (machine.Count > 0)
            {
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", machine.Select(l => RegArgs(l, enable ? 0 : 1)))) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    using (var p = Process.Start(psi)) p.WaitForExit(60000);
                }
                catch (System.ComponentModel.Win32Exception) { return false; }   // UAC declined
            }
            var now = Enumerate();
            bool ok = list.All(l => now.Any(n => n.Hive == l.Hive && n.Section == l.Section && string.Equals(n.Path, l.Path, StringComparison.OrdinalIgnoreCase) && n.Enabled == enable));
            if (ok)
            {
                Remember(list, enable);
                Log.Write("INFO", $"OpenXR layers {(enable ? "enabled" : "disabled")}: " + string.Join(", ", list.Select(l => l.Name)));
            }
            return ok;
        }

        // what AirFrameXR disabled, so the runtime toggle (and the layers dialog) can put it back
        static void Remember(List<XrLayer> layers, bool enabled)
        {
            var root = Json.Load(DisabledFile);
            var cur = (root.TryGetValue("layers", out var o) && o is object[] a ? a.OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>());
            foreach (var l in layers)
            {
                cur.RemoveAll(d => (d["path"] as string) == l.Path && (d["hive"] as string) == l.Hive && (d["section"] as string) == l.Section);
                if (!enabled) cur.Add(new Dictionary<string, object> { { "hive", l.Hive }, { "view", l.View }, { "section", l.Section }, { "path", l.Path }, { "name", l.Name } });
            }
            root["layers"] = cur.Cast<object>().ToArray();
            Directory.CreateDirectory(Paths.Config);
            Json.Save(DisabledFile, root);
        }

        /// The layers AirFrameXR disabled earlier and that are still disabled.
        public static List<XrLayer> DisabledByUs()
        {
            var root = Json.Load(DisabledFile);
            var now = Enumerate();
            var r = new List<XrLayer>();
            if (root.TryGetValue("layers", out var o) && o is object[] a)
                foreach (var d in a.OfType<Dictionary<string, object>>())
                {
                    var m = now.FirstOrDefault(n => n.Hive == (d["hive"] as string) && n.Section == (d["section"] as string) && string.Equals(n.Path, d["path"] as string, StringComparison.OrdinalIgnoreCase));
                    if (m != null && !m.Enabled) r.Add(m);
                }
            return r;
        }
    }

    /// The layers dialog: every OpenXR API layer, the ones that load into every application ticked when they may capture input; Disable / Enable.
    public class LayersForm : DpiForm
    {
        readonly ListView list = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Height = 230, Width = 820, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        List<XrLayer> layers;
        readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(3, 6, 3, 6) };
        public LayersForm()
        {
            Text = "AirFrameXR \u2013 OpenXR layers";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body; Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
            col.Controls.Add(new Label { Text = "OpenXR layers on this PC", AutoSize = true, Font = Ui.Big });
            col.Controls.Add(new Label { Text = "A layer that is enabled and implicit is loaded into every OpenXR game. Some capture or remap the controllers before the runtime (ours included) ever sees them: " +
                "hands and controllers then do nothing in the game. Tick the ones to switch off; this only flips a switch in the registry, nothing is deleted, and Enable puts it back.", AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(3, 4, 3, 6) });
            string[] cols = { "Layer", "Loads", "Where", "Note" }; int[] w = { 290, 95, 170, 230 };
            for (int i = 0; i < cols.Length; i++) Ui.Col(list, cols[i], w[i]);
            col.Controls.Add(list);
            col.Controls.Add(status);
            var row = new FlowLayoutPanel { AutoSize = true };
            var dis = new Button { Text = "Disable ticked", AutoSize = true, Font = Ui.Bold, MinimumSize = new Size(120, 30) };
            var en = new Button { Text = "Enable ticked", AutoSize = true, MinimumSize = new Size(120, 30) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK, MinimumSize = new Size(100, 30) };
            dis.Click += (s, e) => Apply(false);
            en.Click += (s, e) => Apply(true);
            row.Controls.Add(dis); row.Controls.Add(en); row.Controls.Add(close);
            col.Controls.Add(row);
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            col.Dock = DockStyle.None; col.Location = Point.Empty;
            scroller.Controls.Add(col);
            Controls.Add(scroller);
            Ui.AutoFitWindow(this, null, col);
            CancelButton = close;
            Fill();
        }

        void Fill()
        {
            layers = XrLayers.Enumerate().OrderByDescending(l => l.Enabled && l.Section == "Implicit").ThenByDescending(l => l.MayCaptureInput).ThenBy(l => l.Name).ToList();
            list.Items.Clear();
            foreach (var l in layers)
            {
                var loads = l.Enabled ? (l.Section == "Implicit" ? "every game" : "if asked") : "no";
                var note = l.Enabled && l.Section == "Implicit" && l.MayCaptureInput ? "may capture controller input" : (l.Enabled && l.Section == "Implicit" ? "loads into every game" : "");
                var it = new ListViewItem(new[] { l.Name, loads, l.Where, note }) { Checked = l.Enabled && l.Section == "Implicit" && l.MayCaptureInput, ToolTipText = l.Path + "\n" + l.Description };
                if (l.Enabled && l.Section == "Implicit" && l.MayCaptureInput) it.ForeColor = Color.Firebrick;
                list.Items.Add(it);
            }
            var n = layers.Count(l => l.Enabled && l.Section == "Implicit");
            Ui.FitColumns(list);
            status.Text = layers.Count == 0 ? "No OpenXR API layer is registered." : $"{n} implicit layer(s) enabled. {layers.Count(l => l.Enabled && l.Section == "Implicit" && l.MayCaptureInput)} of them may capture input.";
        }

        void Apply(bool enable)
        {
            var picked = list.CheckedItems.Cast<ListViewItem>().Select(i => layers[i.Index]).ToList();
            if (picked.Count == 0) { status.Text = "Tick the layers first."; return; }
            var ok = XrLayers.Set(picked, enable);
            Fill();
            status.Text = (ok ? (enable ? "Enabled. " : "Disabled. ") : "Not changed (administrator permission declined?). ") + status.Text;
        }
    }

    public static class OpenXr
    {
        public static string ActiveRuntime()
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1"))
                    return k?.GetValue("ActiveRuntime") as string ?? "";
            }
            catch { return ""; }
        }

        public static bool IsOurs() => string.Equals(ActiveRuntime(), Paths.Manifest, StringComparison.OrdinalIgnoreCase);

        /// The runtime that was active before this folder took over (saved by register_openxr_runtime.ps1; "" when there is none to go back to).
        public static string Previous()
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\AirFrameXR"))
                    return k?.GetValue("PreviousActiveRuntime") as string ?? "";
            }
            catch { return ""; }
        }

        /// A runtime's name for people: the manifest's "runtime.name", else the folder it is in.
        public static string Name(string manifest)
        {
            if (string.IsNullOrEmpty(manifest)) return "none";
            if (string.Equals(manifest, Paths.Manifest, StringComparison.OrdinalIgnoreCase)) return "AirFrameXR";
            try
            {
                var j = Json.Load(manifest);
                var n = Json.Str(j, "runtime.name", "");
                if (n.Length > 0) return n;
            }
            catch { }
            try { return Path.GetFileName(Path.GetDirectoryName(manifest)); } catch { return manifest; }
        }

        /// UAC prompt, then register_openxr_runtime.ps1 -Unregister: the previous runtime is the active one again. True if this folder is no longer active.
        public static bool Unregister()
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{Paths.RegisterScript}\" -Unregister -Quiet")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(60000);
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            var ok = !IsOurs();
            Log.Write(ok ? "INFO" : "ERROR", ok ? "OpenXR runtime: back to " + ActiveRuntime() : "unregistering the OpenXR runtime did not take effect");
            return ok;
        }

        /// UAC prompt, then register_openxr_runtime.ps1. True if this folder is the active runtime afterwards.
        public static bool Register(IWin32Window owner)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{Paths.RegisterScript}\" -Quiet")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(60000);
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // UAC declined
            var ok = IsOurs();
            Log.Write(ok ? "INFO" : "ERROR", ok ? "OpenXR runtime registered: " + Paths.Manifest : "OpenXR registration did not take effect");
            return ok;
        }
    }

    public enum FirewallState { Allowed, Blocked, Missing, Unknown }

    /// One inbound/outbound rule of Windows Firewall that names an alvr_host.exe, with what the review makes of it.
    public class FwRule
    {
        public string Name = "", DisplayName = "", Direction = "", Action = "", Profile = "", Protocol = "", Program = "";
        public bool Enabled = true, ProgramExists = true;
        public string Status = "";      // plain words for the list
        public bool Propose;            // ticked by default in the review: it can go
        public bool InPlace;            // the rule that lets this folder's alvr_host.exe in
    }

    public class FwReport
    {
        public FirewallState State = FirewallState.Unknown;
        public List<FwRule> Rules = new List<FwRule>();
        public string Headline = "";
        public int Clutter => Rules.Count(r => r.Propose);
    }

    /// Windows Firewall rules of alvr_host.exe. The rule belongs to the exact exe path: a moved folder needs its own rule, and Windows leaves the old ones
    /// behind (and adds a TCP and a UDP rule at every "Allow" prompt). Read through PowerShell's NetSecurity cmdlets, whose values are English whatever
    /// the Windows language is (netsh's are not), without administrator rights; changing anything asks for them.
    public static class Firewall
    {
        const string ReadScript = "$ErrorActionPreference='SilentlyContinue'; " +
            "$rules = @(Get-NetFirewallApplicationFilter | Where-Object { $_.Program -match 'alvr_host\\.exe$' } | ForEach-Object { $r = $_ | Get-NetFirewallRule; $p = $r | Get-NetFirewallPortFilter; " +
            "[pscustomobject]@{ Name=$r.Name; DisplayName=$r.DisplayName; Enabled=[string]$r.Enabled; Direction=[string]$r.Direction; Action=[string]$r.Action; Profile=[string]$r.Profile; Protocol=[string]$p.Protocol; Program=$_.Program } }); " +
            "$profiles = @(Get-NetConnectionProfile | ForEach-Object { [string]$_.NetworkCategory }); " +
            "ConvertTo-Json -Depth 4 -Compress -InputObject ([pscustomobject]@{ profiles = $profiles; rules = $rules })";

        static string Ps(string script, int ms = 40000)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
            using (var p = System.Diagnostics.Process.Start(psi)) { var t = p.StandardOutput.ReadToEnd(); p.WaitForExit(ms); return t; }
        }

        static IEnumerable<Dictionary<string, object>> Items(object o)
        {
            if (o is object[] a) return a.OfType<Dictionary<string, object>>();
            if (o is Dictionary<string, object> d) return new[] { d };
            return new Dictionary<string, object>[0];
        }

        static bool ProfileApplies(string rule, string[] active)
        {
            if (string.IsNullOrEmpty(rule) || rule.Equals("Any", StringComparison.OrdinalIgnoreCase) || rule.Equals("All", StringComparison.OrdinalIgnoreCase) || active.Length == 0) return true;
            return active.Any(c => rule.IndexOf(c.StartsWith("Domain") ? "Domain" : c, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// Every rule that names an alvr_host.exe, judged against this folder's exe: the one that lets it in, the ones that block it, and what is just clutter.
        public static FwReport Analyze(string exe)
        {
            var rep = new FwReport();
            try
            {
                var j = Json.Parse(Ps(ReadScript).Trim());
                if (j.Count == 0) { rep.Headline = "The firewall could not be read."; return rep; }
                string[] active = j.TryGetValue("profiles", out var po0) ? (po0 is object[] pa ? pa.Select(x => Convert.ToString(x)).ToArray() : po0 is string one ? new[] { one } : new string[0]) : new string[0];
                foreach (var d in Items(j.TryGetValue("rules", out var ro) ? ro : null))
                {
                    string S(string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v) : "";
                    var r = new FwRule { Name = S("Name"), DisplayName = S("DisplayName"), Direction = S("Direction"), Action = S("Action"), Profile = S("Profile"), Protocol = S("Protocol"), Program = S("Program"), Enabled = S("Enabled").Equals("True", StringComparison.OrdinalIgnoreCase) };
                    // a program on a drive that is not there (a removed USB disk) is not proven gone
                    r.ProgramExists = r.Program.Length == 0 || File.Exists(r.Program) || !Directory.Exists(Path.GetPathRoot(r.Program));
                    rep.Rules.Add(r);
                }
                Func<FwRule, bool> effective = r => r.Enabled && ProfileApplies(r.Profile, active);
                bool Mine(FwRule r) => r.Direction.Equals("Inbound", StringComparison.OrdinalIgnoreCase) && string.Equals(r.Program, exe, StringComparison.OrdinalIgnoreCase);
                var mine = rep.Rules.Where(Mine).ToList();
                bool block = mine.Any(r => r.Action.Equals("Block", StringComparison.OrdinalIgnoreCase) && effective(r));
                var allows = mine.Where(r => r.Action.Equals("Allow", StringComparison.OrdinalIgnoreCase) && effective(r)).ToList();
                rep.State = block ? FirewallState.Blocked : (allows.Count > 0 ? FirewallState.Allowed : FirewallState.Missing);
                // the rule that counts: an any-protocol one if there is one, else the first
                var keeper = allows.FirstOrDefault(r => r.Protocol.Equals("Any", StringComparison.OrdinalIgnoreCase)) ?? allows.FirstOrDefault();
                foreach (var r in rep.Rules)
                {
                    if (Mine(r))
                    {
                        if (r.Action.Equals("Block", StringComparison.OrdinalIgnoreCase)) { r.Status = "BLOCKS this folder"; r.Propose = r.Enabled; }
                        else if (!r.Enabled) { r.Status = "this folder, switched off"; }
                        else if (!ProfileApplies(r.Profile, active)) { r.Status = "this folder, but not for the current network (" + string.Join("/", active) + ")"; }
                        else if (r == keeper) { r.Status = "in place: lets this folder in"; r.InPlace = true; }
                        else if (keeper != null && keeper.Protocol.Equals("Any", StringComparison.OrdinalIgnoreCase)) { r.Status = "redundant: the rule above already covers all protocols"; r.Propose = true; }
                        else if (allows.Any(o => o != r && o.Protocol.Equals(r.Protocol, StringComparison.OrdinalIgnoreCase) && allows.IndexOf(o) < allows.IndexOf(r))) { r.Status = "duplicate of an earlier " + r.Protocol + " rule"; r.Propose = true; }
                        else r.Status = "this folder (" + r.Protocol + ")";
                    }
                    else if (!r.ProgramExists) { r.Status = "stale: the file is gone (" + r.Program + ")"; r.Propose = true; }
                    else r.Status = "another copy of alvr_host.exe (left alone)";
                }
                int clutter = rep.Clutter;
                rep.Headline = rep.State == FirewallState.Allowed
                    ? (clutter == 0 ? "Windows Firewall lets this folder's alvr_host.exe in. Nothing to clean up." : $"Windows Firewall lets this folder's alvr_host.exe in. {clutter} leftover rule(s) can be removed.")
                    : rep.State == FirewallState.Blocked ? "Windows Firewall BLOCKS this folder's alvr_host.exe: the headset will not be found and cannot connect."
                    : "Windows Firewall has no rule for this folder's alvr_host.exe (a rule belongs to the exact folder): discovery will find nothing until it is allowed.";
            }
            catch (Exception e) { Log.Write("WARN", "firewall check: " + e.Message); rep.State = FirewallState.Unknown; rep.Headline = "The firewall could not be read."; }
            return rep;
        }

        public static FirewallState Check(string program) => Analyze(program).State;

        static FwReport cached; static string cachedFor; static DateTime cachedAt;
        /// The same analysis, reused for a minute (a window that shows the verdict many times, the test harness): PowerShell takes a few seconds.
        public static FwReport AnalyzeCached(string exe)
        {
            if (cached == null || cachedFor != exe || (DateTime.Now - cachedAt).TotalSeconds > 60) { cached = Analyze(exe); cachedFor = exe; cachedAt = DateTime.Now; }
            return cached;
        }

        /// Removes every rule of this program (a block rule beats an allow rule) and adds one inbound allow rule, through a UAC prompt. False: declined or failed.
        public static bool Allow(string program)
        {
            try
            {
                var args = $"/c netsh advfirewall firewall delete rule name=all program=\"{program}\" & netsh advfirewall firewall add rule name=\"AirFrameXR (alvr_host)\" dir=in action=allow program=\"{program}\" enable=yes profile=any";
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
                using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(30000); }
                Log.Write("INFO", $"firewall: allow rule for {program} requested");
                return true;
            }
            catch (Exception e) { Log.Write("WARN", "firewall rule not added: " + e.Message); return false; }
        }

        /// Deletes these rules (by their internal name) through one UAC prompt.
        public static bool Remove(IEnumerable<FwRule> rules)
        {
            var names = rules.Select(r => "'" + r.Name.Replace("'", "''") + "'").ToList();
            if (names.Count == 0) return true;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Remove-NetFirewallRule -Name " + string.Join(",", names).Replace("\"", "\\\"") + "\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
                using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(60000); }
                Log.Write("INFO", "firewall: removed " + names.Count + " leftover rule(s) of alvr_host.exe");
                return true;
            }
            catch (Exception e) { Log.Write("WARN", "firewall rules not removed: " + e.Message); return false; }
        }
    }

    /// The firewall review: every rule that names an alvr_host.exe, what each is, the clutter ticked, one click to remove it or to allow this folder.
    public class FirewallForm : DpiForm
    {
        readonly ListView list = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Height = 240, Width = 880, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        readonly Label headline = new Label { AutoSize = true, MaximumSize = new Size(880, 0), Font = Ui.Bold, Margin = new Padding(3, 6, 3, 6) };
        readonly Button allow = new Button { Text = "Clean up", AutoSize = true, Font = Ui.Bold, MinimumSize = new Size(130, 30) };
        readonly Button remove = new Button { Text = "Remove only the ticked rules", AutoSize = true, MinimumSize = new Size(130, 30) };
        FwReport report;
        readonly string exe;
        public FirewallForm(FwReport demo = null)
        {
            exe = Paths.Host;
            Text = "AirFrameXR \u2013 Firewall rules";
            Font = Ui.Body; Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
            col.Controls.Add(new Label { Text = "Windows Firewall and alvr_host.exe", AutoSize = true, Font = Ui.Big });
            col.Controls.Add(new Label { Text = "A rule lets one exact alvr_host.exe in. Moving the folder needs a new rule, and Windows keeps the old ones (it also adds a TCP and a UDP rule at every Allow prompt). " +
                "Ticked rules are leftovers: files that are gone, duplicates, rules that block. Rules of other copies that still exist are left alone.", AutoSize = true, MaximumSize = new Size(880, 0), Margin = new Padding(3, 4, 3, 4) });
            col.Controls.Add(headline);
            foreach (var c in new[] { ("Rule", 190), ("Action", 70), ("Protocol", 80), ("Network", 100), ("On", 40), ("What it is", 400) }) Ui.Col(list, c.Item1, c.Item2);
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var refresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(100, 30) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK, MinimumSize = new Size(100, 30) };
            // one action: the rule that is missing is added, the leftovers are removed (the rules themselves are in the details below)
            allow.Click += (s, e) =>
            {
                if (report != null && (report.State == FirewallState.Missing || report.State == FirewallState.Blocked)) Firewall.Allow(exe);
                Firewall.Remove(list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (FwRule)i.Tag));
                Load(null);
            };
            remove.Click += (s, e) => { Firewall.Remove(list.CheckedItems.Cast<ListViewItem>().Select(i => (FwRule)i.Tag)); Load(null); };
            refresh.Click += (s, e) => Load(null);
            var det = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var detRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            detRow.Controls.Add(remove); detRow.Controls.Add(refresh);
            det.Controls.Add(list); det.Controls.Add(detRow);
            row.Controls.Add(allow); row.Controls.Add(close);
            col.Controls.Add(row);
            col.Controls.Add(Ui.Expander("Details: the rules", det, demo != null));
            AcceptButton = allow;
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            col.Dock = DockStyle.None; col.Location = Point.Empty;
            scroller.Controls.Add(col);
            Controls.Add(scroller);
            Ui.AutoFitWindow(this, null, col);
            CancelButton = close;
            if (demo != null) Fill(demo); else Shown += (s, e) => Load(null);
        }

        void Load(object _)
        {
            headline.Text = "Reading the firewall..."; headline.ForeColor = Color.DimGray;
            allow.Enabled = remove.Enabled = false;
            System.Threading.Tasks.Task.Run(() => Firewall.Analyze(exe)).ContinueWith(t => { try { BeginInvoke((Action)(() => Fill(t.Result))); } catch { } });
        }

        void Fill(FwReport r)
        {
            report = r;
            headline.Text = r.Headline;
            headline.ForeColor = r.State == FirewallState.Allowed ? Color.SeaGreen : r.State == FirewallState.Unknown ? Color.DimGray : Color.Firebrick;
            list.Items.Clear();
            foreach (var x in r.Rules.OrderByDescending(x => x.InPlace).ThenBy(x => x.Propose).ThenBy(x => x.Program))
            {
                var it = new ListViewItem(new[] { x.DisplayName.Length > 0 ? x.DisplayName : x.Name, x.Action, x.Protocol, x.Profile, x.Enabled ? "yes" : "no", x.Status }) { Tag = x, Checked = x.Propose, ToolTipText = x.Program };
                if (x.Propose) it.ForeColor = Color.Firebrick; else if (x.InPlace) it.ForeColor = Color.SeaGreen;
                list.Items.Add(it);
            }
            Ui.FitColumns(list);
            allow.Enabled = r.State == FirewallState.Missing || r.State == FirewallState.Blocked || r.Rules.Any(x => x.Propose);
            remove.Enabled = r.Rules.Count > 0;
        }
    }

    /// An iOS-style toggle: a pill whose knob slides, green when on. Click or Space flips it. For the one setting that should be readable
    /// from across the room (frame generation in the streamer window); the rest of the GUI keeps plain checkboxes.
    public class ToggleSwitch : Control
    {
        bool on;
        float pos;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        public event EventHandler CheckedChanged;
        public bool Checked
        {
            get => on;
            set { if (on == value) return; on = value; anim.Start(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
        }
        public ToggleSwitch()
        {
            Size = new Size(54, 30);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            TabStop = true;
            anim.Tick += (s, e) =>
            {
                pos = Math.Max(0f, Math.Min(1f, pos + (on ? 0.18f : -0.18f)));
                if (pos <= 0f || pos >= 1f) anim.Stop();
                Invalidate();
            };
        }
        /// Sets the state without the slide (a screenshot, the initial state).
        public void SetCheckedInstant(bool v) { on = v; pos = v ? 1f : 0f; Invalidate(); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int h = Height - 2, w = Width - 2;
            var off = Color.FromArgb(0xE9, 0xE9, 0xEB);
            var onC = Color.FromArgb(0x34, 0xC7, 0x59);
            var c = Color.FromArgb((int)(off.R + (onC.R - off.R) * pos), (int)(off.G + (onC.G - off.G) * pos), (int)(off.B + (onC.B - off.B) * pos));
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                int r = h;
                path.AddArc(1, 1, r, r, 90, 180);
                path.AddArc(1 + w - r, 1, r, r, 270, 180);
                path.CloseFigure();
                using (var b = new SolidBrush(c)) g.FillPath(b, path);
                if (Focused) using (var p = new Pen(Ui.Accent, 1.5f)) g.DrawPath(p, path);
            }
            int d = h - 4;
            int x = 3 + (int)Math.Round(pos * (w - d - 4));
            using (var sh = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillEllipse(sh, x, 4, d, d);
            g.FillEllipse(Brushes.White, x, 3, d, d);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    /// The wizard's chroma-key step, for every headset but the Vision Pro (its App Store app has a fixed key colour and range that the streamer cannot change, so there
    /// the chroma stays off): transparent background yes or no, with the recommended colour. The answer is applied through Chroma.Apply.
    public class ChromaStepForm : DpiForm
    {
        public bool Transparent => yes.Checked;
        public string Rgb => $"{pick.BackColor.R:x2}{pick.BackColor.G:x2}{pick.BackColor.B:x2}";
        readonly RadioButton yes = new RadioButton { Text = "Yes: show my room where the screen is green (chroma-key passthrough)", AutoSize = true, Checked = true };
        readonly RadioButton no = new RadioButton { Text = "No: no passthrough (the desktop mirror shows between games instead)", AutoSize = true };
        readonly Button pick = new Button { Width = 90, Height = 24, FlatStyle = FlatStyle.Flat };
        public ChromaStepForm()
        {
            Text = "AirFrameXR \u2013 Passthrough";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body; Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
            col.Controls.Add(new Label { Text = "Transparent background (passthrough)?", AutoSize = true, Font = Ui.Big });
            col.Controls.Add(new Label { Text = "Between games, and in games that draw it, the streamer's pure green (#00FF00) can become transparent: the headset shows your room there. " +
                "We recommend #00FF00; it is the colour games and mods avoid least by accident. It is optional: without it the Windows desktop is mirrored on a floating screen between games.",
                AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(3, 6, 3, 6) });
            yes.Margin = new Padding(3, 6, 3, 3); no.Margin = new Padding(3);
            col.Controls.Add(yes);
            var colour = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(22, 0, 0, 4) };
            colour.Controls.Add(new Label { Text = "Key colour", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            SetColour(ColorTranslator.FromHtml("#" + Chroma.DefaultRgb));
            pick.Click += (s, e) => { using (var cd = new ColorDialog { Color = pick.BackColor, FullOpen = true }) if (cd.ShowDialog(this) == DialogResult.OK) SetColour(cd.Color); };
            var reset = new Button { Text = "Recommended (#00FF00)", AutoSize = true };
            reset.Click += (s, e) => SetColour(ColorTranslator.FromHtml("#" + Chroma.DefaultRgb));
            colour.Controls.Add(pick); colour.Controls.Add(reset);
            col.Controls.Add(colour);
            col.Controls.Add(no);
            yes.CheckedChanged += (s, e) => colour.Enabled = yes.Checked;
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var go = new Button { Text = "Continue", AutoSize = true, DialogResult = DialogResult.OK, Font = Ui.Bold, MinimumSize = new Size(100, 30) };
            row.Controls.Add(go);
            col.Controls.Add(row);
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            col.Dock = DockStyle.None; col.Location = Point.Empty;
            scroller.Controls.Add(col);
            Controls.Add(scroller);
            Ui.AutoFitWindow(this, null, col);
            AcceptButton = go;
        }
        void SetColour(Color c) { pick.BackColor = c; pick.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; pick.ForeColor = c.GetBrightness() > 0.5 ? Color.Black : Color.White; }
    }

    /// A latency the engine could not measure is shown as n/a, not as a number: above one second, or when over 10 % of the run's client samples were dropped
    /// by the engine's aggregator as garbage (`latency_outliers` of `samples`; docs/PLAN_0.5.md U7).
    public static class Latency
    {
        public const string Why = "n/a: the headset's latency samples were not usable (a duration that wrapped, or too many dropped). The other numbers are fine.";
        public static bool Absurd(double ms, double outliers = 0, double samples = 0) =>
            ms < 0 || ms > 1000 || (samples > 0 && !double.IsNaN(outliers) && outliers / samples > 0.10);
        /// ALVR's own breakdown of the streamer's share of the latency: encoder + transport (network) + decoder, in ms; null when any part is missing or not believable.
        public static double? Streamer(double enc, double net, double dec, double outliers = 0, double samples = 0) =>
            double.IsNaN(enc) || double.IsNaN(net) || double.IsNaN(dec) || Absurd(enc, outliers, samples) || Absurd(net, outliers, samples) || Absurd(dec, outliers, samples) ? (double?)null : enc + net + dec;
        /// "-" when nothing was measured, "n/a" when it was and is not believable, else the number.
        public static string Text(double ms, double outliers = 0, double samples = 0, string fmt = "0") =>
            double.IsNaN(ms) ? "-" : Absurd(ms, outliers, samples) ? "n/a" : ms.ToString(fmt, CultureInfo.InvariantCulture);
    }

    public class Tile : TableLayoutPanel
    {
        readonly Label value, note;
        public Tile(string title, string v, string n)
        {
            // design values at 96 dpi: scaled with the window, or by Ui.LateScale when the tile is created after it loaded
            AutoSize = true; ColumnCount = 1; BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9); Padding = new Padding(10, 6, 10, 6); Margin = new Padding(0, 0, 8, 8); MinimumSize = new Size(110, 0);
            Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Color.DimGray, Font = Ui.Body, Margin = new Padding(0) });
            value = new Label { Text = v, AutoSize = true, Font = new Font("Segoe UI", 15f, FontStyle.Bold), Margin = new Padding(0) };
            note = new Label { Text = n, AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0) };
            Controls.Add(value); Controls.Add(note);
        }
        static readonly ToolTip tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        public string Value => value.Text;
        public string Note => note.Text;
        public Color ValueColor => value.ForeColor;
        public void Set(string v, string n, Color? color = null, string tooltip = null)
        {
            value.Text = v; note.Text = n; value.ForeColor = color ?? Color.Black;
            foreach (Control c in new Control[] { this, value, note }) tip.SetToolTip(c, tooltip);
        }
    }

    /// Every window of the GUI: the designer pattern WinForms' own scaling needs. Built suspended with AutoScaleMode.Dpi (design values at 96 dpi), laid out once
    /// at load, where the framework scales the form, its docked panels and every control that is already in it to the monitor's dpi. Controls created later
    /// (a wizard page) are not scaled by it: Ui.LateScale. With bin\*.config (PerMonitorV2) it follows the window from monitor to monitor.
    public class DpiForm : Form
    {
        public DpiForm()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();
        }
        protected override void OnLoad(EventArgs e)
        {
            ResumeLayout(false);
            PerformLayout();
            base.OnLoad(e);
        }
    }

    public static class Ui
    {
        /// Radio buttons that sit in different containers (one of them next to a badge in its own flow panel) are not one group for Windows Forms: both could be
        /// checked, and the save then took the second one (a foveated choice came back as full frame after a restart). Checking one unchecks the others.
        public static void Exclusive(params RadioButton[] group)
        {
            foreach (var r in group)
            {
                var me = r;
                me.CheckedChanged += (s, e) => { if (me.Checked) foreach (var o in group) if (o != me) o.Checked = false; };
            }
        }
        /// Test harness only (uitest.exe): pretend the display is this many dpi. 0 = the real one.
        public static int DpiOverride;
        /// Test harness only: the pretend work area (device pixels) the window-fitting code uses instead of the real screen's.
        public static Rectangle? WorkAreaOverride;
        public static Rectangle WorkArea(Control c) => WorkAreaOverride ?? Screen.FromControl(c).WorkingArea;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, Action> refits = new System.Runtime.CompilerServices.ConditionalWeakTable<Form, Action>();
        /// Test harness: runs a window's own fitting code again (after the pretend scale and work area changed).
        public static void Refit(Form f) { if (refits.TryGetValue(f, out var a)) a(); }

        /// A window with a fixed design size (a wizard, a list window): never bigger than the work area, its minimum size too. Sizes set in code are
        /// design values at 96 dpi (the framework scales them at load).
        public static void FitToWorkArea(Form f)
        {
            bool placed = false;
            Action fit = () =>
            {
                var wa = WorkArea(f);
                f.MinimumSize = new Size(Math.Min(f.MinimumSize.Width, wa.Width), Math.Min(f.MinimumSize.Height, wa.Height));
                f.Size = new Size(Math.Min(f.Width, wa.Width), Math.Min(f.Height, wa.Height));
                // the size was set after CenterScreen placed the window: centre it again (once), and keep it inside the work area
                int x = f.Left, y = f.Top;
                if (!placed) { placed = true; if (f.StartPosition == FormStartPosition.CenterScreen) { x = wa.Left + (wa.Width - f.Width) / 2; y = wa.Top + (wa.Height - f.Height) / 2; } }
                f.Location = new Point(Math.Max(wa.Left, Math.Min(x, wa.Right - f.Width)), Math.Max(wa.Top, Math.Min(y, wa.Bottom - f.Height)));
            };
            f.Load += (s, e) => fit();
            refits.Remove(f); refits.Add(f, fit);
        }

        /// A window exactly as big as its content: the header (docked at the top) plus the body, which sits in a scrolling host. When that is more
        /// than the work area allows (1080p at 150 %, a long message) the window stops at the work area and the body scrolls instead of being cut.
        ///
        /// refitOnChange false (the streamer): the window is sized at load and by Ui.Refit only, so a notice appearing or a number getting longer never moves or resizes it.
        /// `measure` (optional) gets the sizing action and runs it with the window's fullest content showing, so the fixed size has room for everything that can appear.
        public static void AutoFitWindow(Form f, Control header, Control body, bool refitOnChange = true, Action<Action> measure = null)
        {
            if (body is Panel bp) bp.AutoSizeMode = AutoSizeMode.GrowAndShrink;   // the default GrowOnly keeps the size the framework scaled it to: empty space below the content
            bool placed = false;
            Action size = () =>
            {
                var wa = WorkArea(f);
                var chrome = new Size(f.Width - f.ClientSize.Width, f.Height - f.ClientSize.Height);
                var bs = body.PreferredSize;
                body.PerformLayout();
                int cw = Math.Max(bs.Width, body.Width) + 4, ch = (header != null ? header.Height : 0) + body.Height + 2;   // the body is AutoSize: its real height after a layout is the content
                int maxW = wa.Width - chrome.Width, maxH = wa.Height - chrome.Height;
                if (ch > maxH) { ch = maxH; cw += SystemInformation.VerticalScrollBarWidth; }
                if (cw > maxW) { cw = maxW; ch = Math.Min(maxH, ch + SystemInformation.HorizontalScrollBarHeight); }
                f.ClientSize = new Size(cw, ch);
                // CenterScreen centred the window at its design size, before this fit made it bigger: centre it once now, later fits only keep it on screen
                int x = f.Left, y = f.Top;
                if (!placed)
                {
                    placed = true;
                    var around = f.StartPosition == FormStartPosition.CenterParent && f.Owner != null ? f.Owner.Bounds : (f.StartPosition == FormStartPosition.CenterScreen ? wa : Rectangle.Empty);
                    if (around != Rectangle.Empty) { x = around.Left + (around.Width - f.Width) / 2; y = around.Top + (around.Height - f.Height) / 2; }
                }
                f.Location = new Point(Math.Max(wa.Left, Math.Min(x, wa.Right - f.Width)), Math.Max(wa.Top, Math.Min(y, wa.Bottom - f.Height)));
            };
            Action fit = measure == null ? size : () => measure(size);
            f.Load += (s, e) => fit();
            if (refitOnChange) body.SizeChanged += (s, e) => { if (f.IsHandleCreated) fit(); };
            refits.Remove(f); refits.Add(f, fit);
        }
        /// A size designed at 96 dpi, in pixels at the control's monitor dpi (for what the framework does not scale itself: list columns). The test
        /// harness overrides the dpi.
        public static int Px(Control c, int designPx) => (int)Math.Round(designPx * (DpiOverride > 0 ? DpiOverride : c.DeviceDpi) / 96.0);
        /// The reverse: pixels measured on screen (a width taken from a parent) as design units, for controls that LateScale will scale up.
        public static int Design(Control c, int px) => (int)Math.Round(px * 96.0 / c.DeviceDpi);
        /// A details-view column whose width is a 96-dpi design value (a starting width: FitColumns sizes the columns from their content).
        public static void Col(ListView lv, string header, int designPx) => lv.Columns.Add(header, Px(lv, designPx));

        /// Controls built after the window loaded (a wizard page, a rebuilt pane) are not scaled by the framework: give them the monitor's scale.
        public static T LateScale<T>(T sub, Control owner) where T : Control
        {
            var k = owner.DeviceDpi / 96f;
            if (Math.Abs(k - 1f) > 0.01f) sub.Scale(new SizeF(k, k));
            return sub;
        }

        /// Column widths from what they hold (header and cells), then the widest shrunk so the list never needs sideways scrolling. Fonts and dpi
        /// change what fits, which fixed pixel widths cannot follow. Re-run when the items change; hooked to the list's resize once.
        public static void FitColumns(ListView lv)
        {
            if (lv.View != View.Details || lv.Columns.Count == 0) return;
            if (!lv.Tag?.Equals("fit-hooked") ?? true) { lv.Tag = "fit-hooked"; lv.SizeChanged += (s, e) => FitColumns(lv); lv.FontChanged += (s, e) => FitColumns(lv); lv.ParentChanged += (s, e) => FitColumns(lv); }   // the font it inherits is only known once it has a parent
            int pad = Px(lv, 16);                                  // the list's own padding around a header or a cell
            int n = lv.Columns.Count;
            var want = new int[n]; var floor = new int[n];
            for (int i = 0; i < n; i++)
            {
                floor[i] = want[i] = TextRenderer.MeasureText(lv.Columns[i].Text, lv.Font).Width + pad;
                foreach (ListViewItem it in lv.Items)
                    if (i < it.SubItems.Count) want[i] = Math.Max(want[i], TextRenderer.MeasureText(it.SubItems[i].Text, lv.Font).Width + pad + (i == 0 ? Px(lv, 6) : 0));
            }
            int avail = lv.ClientSize.Width - 2;
            if (avail > 0)
                while (want.Sum() > avail)
                {
                    int widest = 0;
                    for (int i = 1; i < n; i++) if (want[i] - floor[i] > want[widest] - floor[widest]) widest = i;
                    int cut = Math.Min(want.Sum() - avail, want[widest] - floor[widest]);
                    if (cut <= 0) break;                           // only headers left: sideways scrolling is all that remains
                    want[widest] -= cut;
                }
            lv.BeginUpdate();
            for (int i = 0; i < n; i++) if (lv.Columns[i].Width != want[i]) lv.Columns[i].Width = want[i];
            lv.EndUpdate();
        }
        // the system message font (Segoe UI on Windows 10/11, whatever the user chose), a little larger than a dialog's: one family across the OS
        static readonly FontFamily Family = SystemFonts.MessageBoxFont.FontFamily;
        public static readonly Font Body = new Font(Family, 9.5f);
        public static readonly Font Bold = new Font(Family, 9.5f, FontStyle.Bold);
        public static readonly Font Big = new Font(Family, 11f, FontStyle.Bold);
        public static readonly Color Accent = Color.FromArgb(0x1E, 0x6B, 0xF0);

        public static Image Resource(string name)
        {
            using (var s = typeof(Ui).Assembly.GetManifestResourceStream(name)) return s == null ? null : Image.FromStream(s);
        }

        public static Icon AppIcon() { try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return null; } }

        public static Control Header(string subtitle)
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 10, 12, 4), BackColor = Color.White };
            var logo = Resource("logo_640.png");
            if (logo != null) p.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Width = 360, Height = 172, Margin = new Padding(0) });
            p.Controls.Add(new Label { Text = $"{subtitle}   ·   version {Version.Text}", AutoSize = true, Font = Body, ForeColor = Color.DimGray });
            return p;
        }

        /// A short header (small logo and the window's name with the version) for windows that must fit a laptop screen.
        public static Control CompactHeader(string subtitle)
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White, Padding = new Padding(12, 6, 12, 2), WrapContents = false };
            var logo = Resource("logo_640.png");
            if (logo != null) p.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Width = 180, Height = 76, Margin = new Padding(0) });
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(16, 26, 0, 0) };   // centred against the logo
            col.Controls.Add(new Label { Text = $"{subtitle}   \u00B7   version {Version.Text}", AutoSize = true, Font = Body, ForeColor = Color.DimGray });
            p.Controls.Add(col);
            return p;
        }

        /// Testing aid: `--screenshot <png>` renders the window into a file once it is shown, then closes it.
        public static string ScreenshotPath()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 1; i + 1 < a.Length; i++) if (a[i] == "--screenshot") return a[i + 1];
            return null;
        }

        public static void ScreenshotWhenShown(Form f, int delayMs, Action before = null, string suffix = "")
        {
            var path = ScreenshotPath();
            if (path == null) return;
            path += suffix;
            var t = new System.Windows.Forms.Timer { Interval = delayMs };
            t.Tick += (s, e) =>
            {
                t.Stop();
                before?.Invoke();
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                f.Close();
            };
            f.Shown += (s, e) => t.Start();
        }

        /// A collapsed section: a flat "> title" button that shows or hides its content. For what most people never need (progressive disclosure).
        public static Control Expander(string title, Control content, bool open = false, Action onToggle = null)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            var b = new Button { FlatStyle = FlatStyle.Flat, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Font = Bold, Cursor = Cursors.Hand, Margin = new Padding(0), BackColor = Color.Transparent, TabStop = true, AccessibleName = title };
            b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xE6, 0xEA, 0xF2);
            Action paint = () => { b.Text = (content.Visible ? "\u25BE  " : "\u25B8  ") + title; };
            content.Visible = open; paint();
            b.Click += (s, e) => { content.Visible = !content.Visible; paint(); onToggle?.Invoke(); };
            p.Controls.Add(b); p.Controls.Add(content);
            return p;
        }

        /// A statistics tile: small title, a big value, a one-line note. Sizes from its text (no fixed width), so it follows the display scale.
        public static Tile MakeTile(string title, string value = "-", string note = "", Color? color = null) { var t = new Tile(title, value, note); if (color != null) t.Set(value, note, color); return t; }

        /// A small coloured tag next to a label (instead of a suffix in its text).
        public static Label Badge(string text) => new Label { Text = text, AutoSize = true, ForeColor = Color.DarkOrange, Font = new Font(Body.FontFamily, 8.5f, FontStyle.Bold), Margin = new Padding(0, 6, 3, 0) };

        public static Label Line(string text, Font f = null) =>
            new Label { Text = text, AutoSize = true, Font = f ?? Body, Margin = new Padding(3, 3, 3, 3), MaximumSize = new Size(760, 0) }; // long lines wrap (narrower than the page: it may also have a scroll bar)
    }
}
