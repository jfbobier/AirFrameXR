// Shared by VisionALVR.exe (runtime) and configure.exe: install layout, JSON, the general log, headset discovery, the host
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

namespace VisionALVR
{
    public static class Version
    {
        public const string Text = "0.5 alpha";
    }

    /// The portable folder: everything next to the exe, config/ and logs/ inside it.
    public static class Paths
    {
        /// The package root. Everything but the launcher lives in bin\ (VisionALVR.exe, the launcher, is the only file in the root): their folder's parent is the root.
        public static readonly string Dir = RootOf(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
        static string RootOf(string d) => string.Equals(Path.GetFileName(d), "bin", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(d) ?? d) : d;
        public static string Bin => Path.Combine(Dir, "bin");
        public static string Config => Path.Combine(Dir, "config");
        public static string Logs => Path.Combine(Dir, "logs");
        public static string Docs => Path.Combine(Dir, "docs");
        public static string Session => Path.Combine(Config, "session.json");
        public static string SessionDefault => Path.Combine(Config, "session.default.json");
        public static string Settings => Path.Combine(Config, "visionalvr.json");
        public static string Host => Path.Combine(Bin, "alvr_host.exe");
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
    }

    /// The DLSS motion-vector extractor (OptiScaler-DLSSMotionVectorExtractor, one DLL) in a game folder. The package's DLSSMotionVectors\dxgi.dll is the
    /// master copy and is never touched: install = copy it next to the game's exe (as dxgi.dll, or version.dll when dxgi.dll is already someone
    /// else's), uninstall = delete that copy. config\mv_dll.json (the launcher's own file) keeps what was installed where (file, MD5, time) so an
    /// uninstall is one click and an old copy from an earlier release is recognised by its MD5.
    public static class MvDll
    {
        public static string Master => Path.Combine(Paths.Dir, "DLSSMotionVectors", "dxgi.dll");
        public static string Record => Path.Combine(Paths.Config, "mv_dll.json");
        public static readonly string[] Names = { "dxgi.dll", "version.dll" };

        public enum State { NoMaster, NoFolder, NotInstalled, Installed, OlderCopy, OtherDll }

        public static string Md5(string path)
        {
            try { using (var m = System.Security.Cryptography.MD5.Create()) using (var f = File.OpenRead(path)) return BitConverter.ToString(m.ComputeHash(f)).Replace("-", "").ToLowerInvariant(); }
            catch { return ""; }
        }

        /// The game's folder: where its exe is (the first recorded path that still exists).
        public static string GameDir(Dictionary<string, object> game)
        {
            if (game != null && game.TryGetValue("paths", out var p) && p is object[] arr)
                foreach (var o in arr) { var s = o as string; if (!string.IsNullOrEmpty(s) && File.Exists(s)) return Path.GetDirectoryName(s); }
            return null;
        }

        /// What is next to the exe: our copy (MD5 equals the master), an older copy we installed, someone else's proxy DLL, or nothing.
        public static State Check(string gameDir, string stem, out string file)
        {
            file = null;
            if (!File.Exists(Master)) return State.NoMaster;
            if (gameDir == null) return State.NoFolder;
            var master = Md5(Master);
            var mine = Installed(stem);
            State best = State.NotInstalled;
            foreach (var n in Names)
            {
                var f = Path.Combine(gameDir, n);
                if (!File.Exists(f)) continue;
                var md5 = Md5(f);
                if (md5 == master) { file = f; return State.Installed; }
                if (mine != null && string.Equals(mine, n, StringComparison.OrdinalIgnoreCase)) { file = f; best = State.OlderCopy; }
                else if (best == State.NotInstalled && n == "dxgi.dll") { file = f; best = State.OtherDll; }
            }
            return best;
        }

        static Dictionary<string, object> Load() => Json.Load(Record);
        static Dictionary<string, object> Installs(Dictionary<string, object> r)
        {
            if (!(r.TryGetValue("installs", out var o) && o is Dictionary<string, object> d)) { d = new Dictionary<string, object>(); r["installs"] = d; }
            return d;
        }
        public static string Installed(string stem)
        {
            var r = Load();
            return Installs(r).TryGetValue(stem, out var o) && o is Dictionary<string, object> e && e.TryGetValue("file", out var f) ? f as string : null;
        }
        static void Save(Dictionary<string, object> r) { Directory.CreateDirectory(Paths.Config); Json.Save(Record, r); }

        public static bool Dismissed(string stem)
        {
            var r = Load();
            return r.TryGetValue("dismissed", out var o) && o is object[] a && a.Any(x => (x as string) == stem);
        }
        public static void Dismiss(string stem)
        {
            var r = Load();
            var l = (r.TryGetValue("dismissed", out var o) && o is object[] a ? a.Select(x => x as string).ToList() : new List<string>());
            if (!l.Contains(stem)) l.Add(stem);
            r["dismissed"] = l.Cast<object>().ToArray();
            Save(r);
        }

        /// Copies the master next to the game's exe. dxgi.dll unless a different one is there (then version.dll). Elevated copy when the folder is
        /// not writable. Returns a message for the user, and the file name through `installed` (null on failure).
        public static string Install(string gameDir, string stem, out string installed)
        {
            installed = null;
            if (!File.Exists(Master)) return "The DLL is not in this release (DLSSMotionVectors\\dxgi.dll is missing).";
            var master = Md5(Master);
            string name = "dxgi.dll";
            var d = Path.Combine(gameDir, "dxgi.dll");
            if (File.Exists(d) && Md5(d) != master && Installed(stem) != "dxgi.dll") name = "version.dll";
            var target = Path.Combine(gameDir, name);
            if (File.Exists(target) && Md5(target) != master && Installed(stem) != name) return $"{name} and dxgi.dll in the game folder belong to something else; nothing was changed.";
            if (!CopyMaybeElevated(Master, target)) return "The copy did not happen (administrator permission refused?).";
            if (Md5(target) != master) return "The copy is not identical to the release's DLL; check the game folder.";
            var r = Load();
            Installs(r)[stem] = new Dictionary<string, object> { { "file", name }, { "dir", gameDir }, { "md5", master }, { "time", DateTime.Now.ToString("s") } };
            Save(r);
            Log.Write("INFO", $"motion-vector DLL installed for {stem}: {target}");
            installed = name;
            return $"Installed as {name} in {gameDir}. Uninstall from the Logs window removes exactly this file.";
        }

        public static string Uninstall(string gameDir, string stem)
        {
            var name = Installed(stem);
            if (name == null) return "Nothing recorded as installed for this game by VisionALVR; remove the DLL by hand if you put it there.";
            var target = Path.Combine(gameDir, name);
            if (File.Exists(target) && !DeleteMaybeElevated(target)) return "The file was not removed (administrator permission refused?).";
            var r = Load();
            Installs(r).Remove(stem);
            Save(r);
            Log.Write("INFO", $"motion-vector DLL removed for {stem}: {target}");
            return $"Removed {name} from {gameDir}.";
        }

        static bool CopyMaybeElevated(string from, string to)
        {
            try { File.Copy(from, to, true); return true; } catch (UnauthorizedAccessException) { } catch (IOException) { }
            return Elevated($"/c copy /y \"{from}\" \"{to}\"") && File.Exists(to);
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

        public static double Num(Dictionary<string, object> root, string path, double def)
        {
            var v = Get(root, path);
            try { return v == null || v is string || v is bool ? def : Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static string Str(Dictionary<string, object> root, string path, string def) => Get(root, path) as string ?? def;
        public static bool Bool(Dictionary<string, object> root, string path, bool def) => Get(root, path) is bool b ? b : def;
        public static string Serialize(object o) => Ser.Serialize(o);
    }

    /// The general log logs\VisionALVR.log, shared with the host and the shim (one appended write per line).
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
                    using (var fs = new FileStream(Path.Combine(Paths.Logs, "VisionALVR.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var b = Encoding.UTF8.GetBytes(line);
                        fs.Write(b, 0, b.Length);
                    }
                }
            }
            catch { }
        }
    }

    /// config\visionalvr.json: VisionALVR's own settings (the ALVR ones stay in session.json).
    public class AppSettings
    {
        public Dictionary<string, object> Root = Json.Load(Paths.Settings);
        public string HeadsetHost { get => Json.Str(Root, "headset.hostname", ""); set => Json.Set(Root, "headset.hostname", value); }
        public string HeadsetName { get => Json.Str(Root, "headset.name", ""); set => Json.Set(Root, "headset.name", value); }
        public string HeadsetIp { get => Json.Str(Root, "headset.ip", ""); set => Json.Set(Root, "headset.ip", value); }
        public string EncodeProfile { get => Json.Str(Root, "video.encode_profile", "foveated"); set => Json.Set(Root, "video.encode_profile", value); }
        /// NVENC split-frame encoding ("3" = one strip per engine, up to 3; "off"): on by default, the reason the project exists on a multi-engine GPU
        /// the Logs window deletes the records of games whose exe no longer exists (on by default)
        public bool AutoDeleteGameLogs { get => Json.Bool(Root, "logs.auto_delete_uninstalled", true); set => Json.Set(Root, "logs.auto_delete_uninstalled", value); }
        /// the Vision Pro's chroma-key tick is set in the ALVR app on the headset (no way to read it from here): the wizard asks once and remembers the answer
        public bool AvpChromaConfirmed { get => Json.Bool(Root, "avp.chroma_confirmed", false); set => Json.Set(Root, "avp.chroma_confirmed", value); }
        /// the key that recentres while playing (the streamer polls it): End by default; "off" disables
        public string RecenterKey { get => Json.Str(Root, "controls.recenter_key", "End"); set => Json.Set(Root, "controls.recenter_key", value); }
        /// the Windows desktop on a virtual screen between games, the right hand (pinch = click) as the mouse; off by default
        public bool MirrorDesktop { get => Json.Bool(Root, "display.mirror_desktop", false); set => Json.Set(Root, "display.mirror_desktop", value); }
        public bool SplitEncode { get => Json.Str(Root, "video.split_encode", "3") != "off"; set => Json.Set(Root, "video.split_encode", value ? "3" : "off"); }
        public string IdleRgb { get => Json.Str(Root, "video.idle_rgb", "00ff00"); set => Json.Set(Root, "video.idle_rgb", value); }
        /// frame generation (docs/FRAME_GENERATION.md): the game renders every other frame, the streamer interpolates the one in between (off by default)
        public bool FrameGen { get => Json.Bool(Root, "video.frame_gen", false); set => Json.Set(Root, "video.frame_gen", value); }
        // frame generation rate: "third" | "half" (default) | "two_thirds" (the game's rate as a fraction of the display rate, the streamer generates the
        // frames in between; docs/FRAME_GENERATION.md), "uncapped" from the config only (docs/FRAME_GENERATION_UNCAPPED.md)
        public string FrameGenRate { get => Json.Str(Root, "video.frame_gen_rate", "half"); set => Json.Set(Root, "video.frame_gen_rate", value); }
        // the game's DLSS motion vectors drive the synthesis when the OptiScaler-DLSSMotionVectorExtractor provider is loaded in the game (docs/MOTION_VECTORS.md)
        public bool GameMotionVectors { get => Json.Bool(Root, "video.game_motion_vectors", true); set => Json.Set(Root, "video.game_motion_vectors", value); }
        // docs/FRAME_GENERATION.md "Game profiles": the launcher sets the rate from the game's recorded runs on the connected headset (on by default)
        public bool FrameGenRecommended { get => Json.Bool(Root, "video.frame_gen_recommended", true); set => Json.Set(Root, "video.frame_gen_recommended", value); }
        public bool Debug { get => Json.Bool(Root, "debug", false); set => Json.Set(Root, "debug", value); }
        /// display (live in VisionALVR.exe): gamma 1.2 prefilled (users' AVP experience with ALVR), the rest neutral
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
        public void Save() => Json.Save(Paths.Settings, Root);
    }

    /// The session (ALVR settings, paired headset). Created from session.default.json on first use.
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

        public void Send(string line) { try { if (Running) { p.StandardInput.WriteLine(line); p.StandardInput.Flush(); } } catch { } }

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
    /// Disabling sets the DWORD to 1 (reversible; what VisionALVR disabled is kept in config\layers_disabled.json so it can be put back).
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

        // what VisionALVR disabled, so the runtime toggle (and the layers dialog) can put it back
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

        /// The layers VisionALVR disabled earlier and that are still disabled.
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
    public class LayersForm : Form
    {
        readonly ListView list = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Height = 230, Width = 820, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        List<XrLayer> layers;
        readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(3, 6, 3, 6) };
        public LayersForm()
        {
            Text = "VisionALVR - OpenXR layers";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body; Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; BackColor = Color.White;
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
            Controls.Add(col);
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
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\VisionALVR"))
                    return k?.GetValue("PreviousActiveRuntime") as string ?? "";
            }
            catch { return ""; }
        }

        /// A runtime's name for people: the manifest's "runtime.name", else the folder it is in.
        public static string Name(string manifest)
        {
            if (string.IsNullOrEmpty(manifest)) return "none";
            if (string.Equals(manifest, Paths.Manifest, StringComparison.OrdinalIgnoreCase)) return "VisionALVR";
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

    /// Window chrome shared by both tools: logo, version, the red alpha warning.
    /// Windows Firewall state of alvr_host.exe (the rules are per executable path: every folder the package is unzipped to needs its own). Without an
    /// allow rule the headset is not found by mDNS and the stream may not connect; a dismissed Windows prompt leaves a BLOCK rule that beats any allow.
    public enum FirewallState { Allowed, Blocked, Missing, Unknown }

    public static class Firewall
    {
        public static FirewallState Check(string program)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", "advfirewall firewall show rule name=all dir=in verbose")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage) };
                string text;
                using (var p = System.Diagnostics.Process.Start(psi)) { text = p.StandardOutput.ReadToEnd(); p.WaitForExit(20000); }
                bool allow = false, block = false, understood = false;
                foreach (var blk in text.Replace("\r", "").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string prog = null, action = null; bool enabled = true;
                    foreach (var raw in blk.Split('\n'))
                    {
                        var i = raw.IndexOf(':');
                        if (i < 0) continue;
                        var k = raw.Substring(0, i).Trim(); var v = raw.Substring(i + 1).Trim();
                        if (k.StartsWith("Program", StringComparison.OrdinalIgnoreCase)) prog = v;
                        else if (k.StartsWith("Action", StringComparison.OrdinalIgnoreCase)) action = v;
                        else if (k.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Activ", StringComparison.OrdinalIgnoreCase)) enabled = !(v.Equals("No", StringComparison.OrdinalIgnoreCase) || v.Equals("Non", StringComparison.OrdinalIgnoreCase));
                    }
                    if (action != null && (action.Equals("Allow", StringComparison.OrdinalIgnoreCase) || action.Equals("Autoriser", StringComparison.OrdinalIgnoreCase) || action.Equals("Block", StringComparison.OrdinalIgnoreCase) || action.Equals("Bloquer", StringComparison.OrdinalIgnoreCase))) understood = true;
                    if (prog == null || action == null || !enabled || !string.Equals(prog, program, StringComparison.OrdinalIgnoreCase)) continue;
                    if (action.Equals("Allow", StringComparison.OrdinalIgnoreCase) || action.Equals("Autoriser", StringComparison.OrdinalIgnoreCase)) allow = true;
                    else if (action.Equals("Block", StringComparison.OrdinalIgnoreCase) || action.Equals("Bloquer", StringComparison.OrdinalIgnoreCase)) block = true;
                }
                if (!understood) return FirewallState.Unknown;   // another Windows language: do not claim anything
                return block ? FirewallState.Blocked : (allow ? FirewallState.Allowed : FirewallState.Missing);
            }
            catch (Exception e) { Log.Write("WARN", "firewall check: " + e.Message); return FirewallState.Unknown; }
        }

        /// Removes every rule of this program (a block rule beats an allow rule) and adds one inbound allow rule, through a UAC prompt. False: declined or failed.
        public static bool Allow(string program)
        {
            try
            {
                var args = $"/c netsh advfirewall firewall delete rule name=all program=\"{program}\" & netsh advfirewall firewall add rule name=\"VisionALVR (alvr_host)\" dir=in action=allow program=\"{program}\" enable=yes profile=any";
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
                using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(30000); }
                Log.Write("INFO", $"firewall: allow rule for {program} requested");
                return true;
            }
            catch (Exception e) { Log.Write("WARN", "firewall rule not added: " + e.Message); return false; }
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

    /// "Install the DLSS motion-vector DLL?" after a game that gave no vectors.
    public class DllOfferForm : Form
    {
        public bool DontAsk => dont.Checked;
        readonly CheckBox dont = new CheckBox { Text = "Don't ask again for this game", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
        public DllOfferForm(string game)
        {
            Text = "VisionALVR - DLSS motion vectors";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body; Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; BackColor = Color.White;
            var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
            col.Controls.Add(new Label { Text = game + ": no DLSS motion vectors were available this session.", AutoSize = true, Font = Ui.Bold, MaximumSize = new Size(480, 0) });
            col.Controls.Add(new Label { Text = "Frame generation is better when the game's own motion vectors are used. A small DLL placed next to the game's exe provides them (games that use DLSS). " +
                "It can be removed again from the Logs window, which lists what was installed. Install it for this game?", AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(3, 8, 3, 3) });
            col.Controls.Add(dont);
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var yes = new Button { Text = "Install", AutoSize = true, DialogResult = DialogResult.Yes, Font = Ui.Bold, MinimumSize = new Size(100, 30) };
            var no = new Button { Text = "Not now", AutoSize = true, DialogResult = DialogResult.No, MinimumSize = new Size(100, 30) };
            row.Controls.Add(yes); row.Controls.Add(no);
            col.Controls.Add(row);
            Controls.Add(col);
            AcceptButton = yes; CancelButton = no;
        }
    }

    /// Vision Pro only: the chroma-key passthrough is a setting of the ALVR app on the headset that the streamer can neither set nor read, so
    /// the wizard shows where it is and asks the user to confirm.
    public class ChromaStepForm : Form
    {
        public bool Confirmed => done.Checked;
        readonly CheckBox done = new CheckBox { Text = "I have set it in the ALVR app on the Vision Pro", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
        public ChromaStepForm()
        {
            Text = "VisionALVR - one setting on the Vision Pro";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body; Icon = Ui.AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; BackColor = Color.White;
            var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
            col.Controls.Add(new Label { Text = "Turn on passthrough in the ALVR app", AutoSize = true, Font = Ui.Big });
            col.Controls.Add(new Label { Text = "Between games VisionALVR shows a pure green screen, which the Vision Pro turns into your room when chroma-key passthrough is on; the game appears when it starts. " +
                "This is a setting of the ALVR app on the headset: VisionALVR cannot change or read it. In the ALVR app, tick \"Enable Chroma Keyed Passthrough\" and set the colour to pure green (#00FF00), as in the picture.",
                AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(3, 6, 3, 6) });
            var img = Ui.Resource("chroma_passthrough.png");
            if (img != null) col.Controls.Add(new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Width = 440, Height = (int)(440.0 * img.Height / img.Width), Margin = new Padding(3) });
            col.Controls.Add(done);
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var go = new Button { Text = "Continue", AutoSize = true, DialogResult = DialogResult.OK, Font = Ui.Bold, MinimumSize = new Size(100, 30), Enabled = false };
            var skip = new Button { Text = "Skip", AutoSize = true, DialogResult = DialogResult.Cancel, MinimumSize = new Size(100, 30) };
            done.CheckedChanged += (s, e) => go.Enabled = done.Checked;
            row.Controls.Add(go); row.Controls.Add(skip);
            col.Controls.Add(row);
            Controls.Add(col);
            CancelButton = skip;
        }
    }

    public class Tile : TableLayoutPanel
    {
        readonly Label value, note;
        public Tile(string title, string v, string n)
        {
            var k = Ui.SystemScale;   // built after the window loaded: its pixel sizes are scaled by hand
            AutoSize = true; ColumnCount = 1; BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9); Padding = new Padding((int)(10 * k), (int)(6 * k), (int)(10 * k), (int)(6 * k)); Margin = new Padding(0, 0, (int)(8 * k), (int)(8 * k)); MinimumSize = new Size((int)(110 * k), 0);
            Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Color.DimGray, Font = Ui.Body, Margin = new Padding(0) });
            value = new Label { Text = v, AutoSize = true, Font = new Font("Segoe UI", 15f, FontStyle.Bold), Margin = new Padding(0) };
            note = new Label { Text = n, AutoSize = true, ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0) };
            Controls.Add(value); Controls.Add(note);
        }
        public void Set(string v, string n, Color? color = null) { value.Text = v; note.Text = n; value.ForeColor = color ?? Color.Black; }
    }

    public static class Ui
    {
        /// Test harness only (uitest.exe): pretend the display is this many dpi. 0 = the real one.
        public static int DpiOverride;
        /// Test harness only: the pretend work area (device pixels) the window-fitting code uses instead of the real screen's.
        public static Rectangle? WorkAreaOverride;
        public static Rectangle WorkArea(Control c) => WorkAreaOverride ?? Screen.FromControl(c).WorkingArea;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, Action> refits = new System.Runtime.CompilerServices.ConditionalWeakTable<Form, Action>();
        /// Test harness: runs a window's own fitting code again (after the pretend scale and work area changed).
        public static void Refit(Form f) { if (refits.TryGetValue(f, out var a)) a(); }

        /// A window with a fixed design size (a wizard, a list window): never bigger than the work area, its minimum size too. Sizes set in code are
        /// raw pixels the framework does not scale: multiply them by SystemScale first.
        public static void FitToWorkArea(Form f)
        {
            Action fit = () =>
            {
                var wa = WorkArea(f);
                f.MinimumSize = new Size(Math.Min(f.MinimumSize.Width, wa.Width), Math.Min(f.MinimumSize.Height, wa.Height));
                f.Size = new Size(Math.Min(f.Width, wa.Width), Math.Min(f.Height, wa.Height));
            };
            f.Load += (s, e) => fit();
            refits.Remove(f); refits.Add(f, fit);
        }

        /// A window exactly as big as its content: the header (docked at the top) plus the body, which sits in a scrolling host. When that is more
        /// than the work area allows (1080p at 150 %, a long message) the window stops at the work area and the body scrolls instead of being cut.
        public static void AutoFitWindow(Form f, Control header, Control body)
        {
            Action fit = () =>
            {
                var wa = WorkArea(f);
                var chrome = new Size(f.Width - f.ClientSize.Width, f.Height - f.ClientSize.Height);
                var bs = body.PreferredSize;
                int cw = Math.Max(bs.Width, body.Width) + 4, ch = (header != null ? header.Height : 0) + Math.Max(bs.Height, body.Height) + 2;
                int maxW = wa.Width - chrome.Width, maxH = wa.Height - chrome.Height;
                if (ch > maxH) { ch = maxH; cw += SystemInformation.VerticalScrollBarWidth; }
                if (cw > maxW) { cw = maxW; ch = Math.Min(maxH, ch + SystemInformation.HorizontalScrollBarHeight); }
                f.ClientSize = new Size(cw, ch);
            };
            f.Load += (s, e) => fit();
            body.SizeChanged += (s, e) => { if (f.IsHandleCreated) fit(); };
            refits.Remove(f); refits.Add(f, fit);
        }
        static float systemScale;
        /// the system dpi / 96, never the test override (the harness scales everything else itself)
        public static float SystemScale { get {
#if NATIVE_DPI
            return 1f;   // experiment: WinForms' own per-monitor scaling does it
#endif
            if (systemScale <= 0) using (var g = Graphics.FromHwnd(IntPtr.Zero)) systemScale = g.DpiX / 96f; return systemScale; } }
        public static int DpiOf(Control c) => DpiOverride > 0 ? DpiOverride : (c != null ? c.DeviceDpi : 96);
        /// A size designed at 96 dpi, in pixels at the display's dpi (for what the framework does not scale itself: list columns).
        public static int Px(Control c, int designPx) => (int)Math.Round(designPx * DpiOf(c) / 96.0);
        /// A details-view column whose width is a 96-dpi design value (a starting width: FitColumns sizes the columns from their content).
        public static void Col(ListView lv, string header, int designPx) => lv.Columns.Add(header, Px(lv, designPx));

        /// The scale the framework applied to the window at load (the system dpi / 96): what controls created later must be given by hand.
        public static float LoadScale(Control c)
        {
#if NATIVE_DPI
            return 1f;
#endif
            var f = c.FindForm() ?? c as Form;
            return f != null && f.CurrentAutoScaleDimensions.Width > 0 ? f.CurrentAutoScaleDimensions.Width / 96f : 1f;
        }
        /// Controls built after the window loaded (a wizard page, a rebuilt pane) are not scaled by the framework: give them the window's scale.
        public static T LateScale<T>(T sub, Control owner) where T : Control
        {
            var k = LoadScale(owner);
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
        public static readonly Font Body = new Font("Segoe UI", 9.5f);
        public static readonly Font Bold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        public static readonly Font Big = new Font("Segoe UI", 11f, FontStyle.Bold);
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
            p.Controls.Add(new Label
            {
                Text = "ALPHA SOFTWARE - use at your own risk. For testing only.", AutoSize = true, Font = Bold, ForeColor = Color.White,
                BackColor = Color.FromArgb(0xC6, 0x28, 0x28), Padding = new Padding(6, 3, 6, 3), Margin = new Padding(0, 6, 0, 4),
            });
            return p;
        }

        /// A short header (small logo, version, the alpha warning on one line) for windows that must fit a laptop screen.
        public static Control CompactHeader(string subtitle)
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.White, Padding = new Padding(12, 6, 12, 2), WrapContents = false };
            var logo = Resource("logo_640.png");
            if (logo != null) p.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Width = 180, Height = 76, Margin = new Padding(0) });
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(16, 10, 0, 0) };
            col.Controls.Add(new Label { Text = $"{subtitle}   \u00B7   version {Version.Text}", AutoSize = true, Font = Body, ForeColor = Color.DimGray });
            col.Controls.Add(new Label { Text = "ALPHA SOFTWARE - use at your own risk. For testing only.", AutoSize = true, Font = Bold, ForeColor = Color.White, BackColor = Color.FromArgb(0xC6, 0x28, 0x28), Padding = new Padding(6, 3, 6, 3), Margin = new Padding(0, 6, 0, 0) });
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

        /// A statistics tile: small title, a big value, a one-line note. Sizes from its text (no fixed width), so it follows the display scale.
        public static Tile MakeTile(string title, string value = "-", string note = "", Color? color = null) { var t = new Tile(title, value, note); if (color != null) t.Set(value, note, color); return t; }

        public static Label Line(string text, Font f = null) =>
            new Label { Text = text, AutoSize = true, Font = f ?? Body, Margin = new Padding(3, 3, 3, 3), MaximumSize = new Size(820, 0) }; // long lines wrap
    }
}
