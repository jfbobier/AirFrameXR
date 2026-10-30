// The Black Box Recorder (docs/BENCH_UI.md): this file is compiled ONLY into blackbox_recorder.exe (and the UI test), through the partial hooks of RuntimeForm; the stock
// streamer carries none of it. The recorder is the normal streamer window plus this panel and the Black Box header (flight-recorder identity, REC lamp). It reads Stock's settings once at start (a one-way copy: Stock's files are never opened for writing) and records, on a key or for a whole
// game session, into logs\performance\<time>_<scene>\ (perfcore.dll's probe files + the engine's per-frame files of the run, cut to the recording). Analysis is a
// separate program (perfanalyze.exe, button below): this window only records.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AirFrameXR.Perf;

namespace AirFrameXR
{
    /// What the Bench streamer needs before the window exists: the one-way copy of Stock's settings, and the recorder's own small settings (config\bench.json: never overwritten by that copy).
    public static class BenchRecorder
    {
        public static bool Enabled;                       // true in the Bench streamer (set by Prepare); the UI test sets it for its bench cases only
        public static string RecordingsDir => Path.Combine(Paths.Logs, "performance");
        public static string OwnSettingsFile => Path.Combine(Paths.Config, "bench.json");

        public static Dictionary<string, object> Own() => Json.Load(OwnSettingsFile);
        public static void SaveOwn(Dictionary<string, object> d) { try { Directory.CreateDirectory(Paths.Config); Json.Save(OwnSettingsFile, d); } catch { } }

        /// Stock's folder: the one recorded in bench.json, else (this install nested as <stock>\bench\telemetry) two levels up, else ask once.
        static string StockDir()
        {
            var own = Own();
            var saved = Json.Str(own, "stock_dir", "");
            Func<string, bool> ok = d => d.Length > 0 && Directory.Exists(Path.Combine(d, "config")) && !string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(Paths.Dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (ok(saved)) return saved;
            // nested in the bundle as <stock>\blackbox\ (one level up), or in the first layout <stock>\bench\telemetry\ (two levels up)
            foreach (var rel in new[] { "..", Path.Combine("..", "..") })
                try { var up = Path.GetFullPath(Path.Combine(Paths.Dir, rel)); if (ok(up) && File.Exists(Path.Combine(up, "bin", "streamer.exe"))) { Json.Set(own, "stock_dir", up); SaveOwn(own); return up; } } catch { }
            if (Environment.GetCommandLineArgs().Contains("--test")) return null;
            using (var f = new FolderBrowserDialog { Description = "Where is your normal AirFrameXR folder? The Black Box Recorder copies your settings and headset pairing from it (it never changes that folder).", ShowNewFolderButton = false })
            {
                if (f.ShowDialog() == DialogResult.OK && ok(f.SelectedPath)) { Json.Set(own, "stock_dir", f.SelectedPath); SaveOwn(own); return f.SelectedPath; }
            }
            return null;
        }

        public static string SnapshotNote = "";

        /// Before anything reads the settings: copy Stock's airframexr.json and session.json (the pairing, the stream fit, every setting) into this install, then switch debug logging on in
        /// the copy (the engine's per-frame files are what is recorded). One way: Stock is only read.
        public static void Prepare()
        {
            Enabled = true;
            try
            {
                var stock = StockDir();
                if (stock != null)
                {
                    Directory.CreateDirectory(Paths.Config);
                    int n = 0;
                    foreach (var f in new[] { "airframexr.json", "session.json" })
                    {
                        var src = Path.Combine(stock, "config", f);
                        if (!File.Exists(src)) continue;
                        using (var a = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var b = new FileStream(Path.Combine(Paths.Config, f), FileMode.Create, FileAccess.Write)) a.CopyTo(b);
                        n++;
                    }
                    SnapshotNote = n > 0 ? "Settings copied from " + stock + " (read only)." : "No settings found in " + stock + ": the recorder uses its own.";
                }
                else SnapshotNote = "No normal AirFrameXR folder chosen: the recorder uses its own settings.";
                var s = Json.Load(Paths.Settings); Json.Set(s, "debug", true); Json.Save(Paths.Settings, s);
                Log.Write("INFO", "bench: " + SnapshotNote);
            }
            catch (Exception e) { SnapshotNote = "Could not copy the settings: " + e.Message; Log.Write("WARN", "bench: " + SnapshotNote); }
        }
    }

    /// The OpenXR runtime during a recording. Windows has ONE active OpenXR runtime and a game loads it, so to record a game the Black Box has to be that runtime while you record. This is NOT the normal
    /// register script (that one also moves the firewall rule and forgets the runtime before it): it sets exactly one registry value, remembers what it was in config\bench.json, and puts back exactly that.
    /// Each switch asks Windows for administrator rights (the registry key is machine-wide).
    public static class BenchRuntime
    {
        const string Key = @"HKLM:\SOFTWARE\Khronos\OpenXR\1";
        public static string Before => Json.Str(BenchRecorder.Own(), "runtime_before", "");
        public static bool Switched => Json.Bool(BenchRecorder.Own(), "runtime_switched", false);
        public static string BeforeName => Switched ? (Before.Length > 0 ? OpenXr.Name(Before) : "no runtime") : "";

        static bool Elevated(string script)
        {
            try
            {
                var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + enc) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(60000);
                return true;
            }
            catch (Win32Exception) { return false; }   // permission declined
        }
        static string Q(string v) { return "'" + v.Replace("'", "''") + "'"; }

        /// switch to this folder's runtime; remembers the one that was active. True when this folder is the active runtime afterwards.
        public static bool UseBlackBox()
        {
            var active = OpenXr.ActiveRuntime();
            if (OpenXr.IsOurs()) return true;
            if (!Elevated("$k = '" + Key + "'; if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }; Set-ItemProperty -Path $k -Name ActiveRuntime -Value " + Q(Paths.Manifest) + " -Type String")) return false;
            bool ok = OpenXr.IsOurs();
            if (ok)
            {
                var own = BenchRecorder.Own(); Json.Set(own, "runtime_before", active); Json.Set(own, "runtime_switched", true); BenchRecorder.SaveOwn(own);
                Log.Write("INFO", "bench: OpenXR runtime now the Black Box's; before: '" + active + "'");
            }
            return ok;
        }

        /// put back exactly what was active before (only if the Black Box's runtime is still the active one). True when the Black Box is no longer the active runtime.
        public static bool PutBack()
        {
            if (!OpenXr.IsOurs()) { Forget(); return true; }
            if (!Switched) return false;   // not ours to undo: the normal launcher's OpenXR runtime button does it
            string before = Before;
            var script = "$k = '" + Key + "'; if ((Get-ItemProperty -Path $k -Name ActiveRuntime -ErrorAction SilentlyContinue).ActiveRuntime -ieq " + Q(Paths.Manifest) + ") { " +
                (before.Length > 0 ? "Set-ItemProperty -Path $k -Name ActiveRuntime -Value " + Q(before) + " -Type String" : "Remove-ItemProperty -Path $k -Name ActiveRuntime -ErrorAction SilentlyContinue") + " }";
            if (!Elevated(script)) return false;
            bool ok = !OpenXr.IsOurs();
            if (ok) { Forget(); Log.Write("INFO", "bench: OpenXR runtime put back: '" + before + "'"); }
            return ok;
        }
        static void Forget() { var own = BenchRecorder.Own(); Json.Set(own, "runtime_switched", false); Json.Set(own, "runtime_before", ""); BenchRecorder.SaveOwn(own); }

        public const string Why = "Windows has one active OpenXR runtime, and a game loads it. To record a game, the Black Box has to be that runtime while you record (it is the telemetry build). ";

        /// at the start of the recorder: explain, and offer the switch
        public static void StartupPrompt()
        {
            if (OpenXr.IsOurs() || Environment.GetCommandLineArgs().Contains("--test") || Ui.ScreenshotPath() != null) return;
            var cur = OpenXr.ActiveRuntime(); string name = OpenXr.Name(cur);
            var q = MessageBox.Show(Why + "\n\nActive now: " + name + (cur.Length > 0 ? " (" + cur + ")" : "") + ".\n\nSwitch to the Black Box now? It remembers " + name + " and puts it back with one click (a button in the recorder window; it also asks when you close it). Each switch asks Windows for administrator rights.\n\nChoose No to look around: games will not be recorded until you use the button in the window.",
                "AirFrameXR Black Box", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (q == DialogResult.Yes && !UseBlackBox()) MessageBox.Show("The switch did not take effect (the permission prompt was declined?). Use the button in the recorder window.", "AirFrameXR Black Box", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// A hidden window that owns the global hotkey. A registered hotkey is swallowed: the game never sees the key (F9 is "quick load" in some games).
    internal sealed class BenchHotkey : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        public event Action Pressed;
        bool registered, polled; int vk; bool ctrl; bool wasDown;
        public int LastError;
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 50 };
        public BenchHotkey() { CreateHandle(new CreateParams()); poll.Tick += (s, e) => Poll(); }
        /// F12 is reserved for debuggers and cannot be registered: it is polled instead (the key then also reaches the game).
        public bool Set(int fkey, bool withCtrl)
        {
            Clear(); vk = 0x6F + fkey; ctrl = withCtrl;     // VK_F1 = 0x70
            if (fkey == 12) { polled = true; poll.Start(); return true; }
            registered = RegisterHotKey(Handle, 1, (withCtrl ? 2u : 0u) | 0x4000u, (uint)vk);   // MOD_NOREPEAT
            LastError = registered ? 0 : Marshal.GetLastWin32Error();
            return registered;
        }
        void Clear() { if (registered) UnregisterHotKey(Handle, 1); registered = false; polled = false; poll.Stop(); wasDown = false; }
        void Poll() { bool down = (GetAsyncKeyState(vk) & 0x8000) != 0 && (!ctrl || (GetAsyncKeyState(0x11) & 0x8000) != 0); if (down && !wasDown && Pressed != null) Pressed(); wasDown = down; }
        protected override void WndProc(ref Message m) { if (m.Msg == 0x312 && Pressed != null) Pressed(); else base.WndProc(ref m); }
        public void Dispose() { Clear(); if (Handle != IntPtr.Zero) DestroyHandle(); poll.Dispose(); }
    }

    public partial class RuntimeForm
    {
        Probe benchProbe; string benchDir, benchDebugDir, benchScene; DateTime benchStart; bool benchStopping;
        BenchHotkey benchHot;
        readonly TextBox benchSceneBox = new TextBox { Width = 200 };
        readonly CheckBox benchFull = new CheckBox { Text = "Full session (game start to game closed)", AutoSize = true, Margin = new Padding(12, 5, 3, 0) };
        readonly CheckBox benchBeep = new CheckBox { Text = "Beep on start / stop", AutoSize = true, Checked = true, Margin = new Padding(3, 5, 3, 0) };
        readonly CheckBox benchCtrl = new CheckBox { Text = "Ctrl +", AutoSize = true, Margin = new Padding(12, 5, 0, 0) };
        readonly ComboBox benchKey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64 };
        readonly Button benchBtn = new Button { Text = "Start recording", AutoSize = true, Font = Ui.Bold };
        readonly Button benchDeep = new Button { Text = "Deep trace (admin)...", AutoSize = true };
        readonly Button benchAnalyze = new Button { Text = "Analyze recordings...", AutoSize = true };
        readonly Button benchFolderBtn = new Button { Text = "Recordings folder", AutoSize = true };
        readonly Label benchState = new Label { AutoSize = false, Font = Ui.Body, Width = 600, Height = 48, Margin = new Padding(3, 6, 3, 0) };
        readonly Label benchRt = new Label { AutoSize = false, Font = Ui.Body, Width = 600, Height = 54, Margin = new Padding(3, 8, 3, 0) };
        readonly Button benchRtUse = new Button { Text = "Use Black Box for recording...", AutoSize = true };
        readonly Button benchRtBack = new Button { Text = "Put back", AutoSize = true };
        readonly Label benchSaved = new Label { AutoSize = false, Font = Ui.Body, ForeColor = Color.DimGray, Width = 560, Height = 20, Margin = new Padding(3, 0, 3, 0) };
        readonly Label benchBanner = new Label { AutoSize = true, Font = Ui.Body, ForeColor = Color.DimGray, Margin = new Padding(3, 0, 0, 8) };
        BlackBoxHeader benchHeader;
        readonly System.Windows.Forms.Timer benchTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        string benchLastDir = "";
        bool benchDemo;

        static partial void BenchPrepare() { BenchRecorder.Prepare(); }
        static partial void BenchRuntimeCheck(ref bool handled) { if (!BenchRecorder.Enabled) return; handled = true; BenchRuntime.StartupPrompt(); }

        partial void BenchHeader(ref Control hdr) { if (!BenchRecorder.Enabled) return; benchHeader = new BlackBoxHeader("Recorder"); hdr = benchHeader; }

        partial void BenchBuild(TableLayoutPanel body)
        {
            if (!BenchRecorder.Enabled) return;
            Text = "AirFrameXR Black Box \u2013 Recorder";
            benchBanner.Text = "Records the settings the normal streamer has now (copied at start, never changed). Per-frame logging is on.";
            body.Controls.Add(benchBanner); body.Controls.SetChildIndex(benchBanner, 0);
            debug.Checked = true; debug.Enabled = false;   // the recording needs the engine's per-frame files
            var own = BenchRecorder.Own();
            benchSceneBox.Text = Json.Str(own, "scene", "");
            benchFull.Checked = Json.Bool(own, "full_session", false);
            benchBeep.Checked = Json.Bool(own, "beep", true);
            benchCtrl.Checked = Json.Bool(own, "ctrl", false);
            for (int i = 2; i <= 12; i++) benchKey.Items.Add("F" + i);
            int fk = 8; try { fk = Convert.ToInt32(Json.Get(own, "key_f"), CultureInfo.InvariantCulture); } catch { }
            benchKey.SelectedItem = "F" + (fk >= 2 && fk <= 12 ? fk : 8);

            var line1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            line1.Controls.Add(new Label { Text = "Scene", AutoSize = true, Margin = new Padding(3, 7, 6, 0) }); line1.Controls.Add(benchSceneBox); line1.Controls.Add(benchFull);
            var line2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            line2.Controls.Add(new Label { Text = "Start / stop key", AutoSize = true, Margin = new Padding(3, 7, 4, 0) }); line2.Controls.Add(benchCtrl); line2.Controls.Add(benchKey); line2.Controls.Add(benchBeep); line2.Controls.Add(benchBtn);
            var line3 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            line3.Controls.Add(benchAnalyze); line3.Controls.Add(benchFolderBtn); line3.Controls.Add(benchDeep);
            var rtRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
            rtRow.Controls.Add(benchRtUse); rtRow.Controls.Add(benchRtBack);
            var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            col.Controls.Add(benchRt); col.Controls.Add(rtRow); col.Controls.Add(line1); col.Controls.Add(line2); col.Controls.Add(benchState); col.Controls.Add(benchSaved); col.Controls.Add(line3);
            var grp = new GroupBox { Text = "Recording", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Font = Ui.Bold };
            col.Font = Ui.Body; col.Dock = DockStyle.Fill; grp.Controls.Add(col);
            body.Controls.Add(grp);
            benchState.Text = BenchRecorder.SnapshotNote + "  Start the game, then press the key (or tick Full session first).";
            tips.SetToolTip(benchDeep, "GPU and CPU scheduling for 20 s with Windows' own recorder (wpr). Windows asks for permission once; the stream keeps running. Declining changes nothing.");
            tips.SetToolTip(benchFull, "Starts when a game starts and stops when it closes: for games where the problem comes late. The key still stops it early.");

            tips.SetToolTip(benchRtUse, BenchRuntime.Why + "The Black Box remembers the runtime that was active and puts it back exactly. Windows asks for administrator rights.");
            benchRtUse.Click += (s, e) => { if (BenchRuntime.UseBlackBox()) BenchRuntimeRefresh(); else benchRt.Text = "The switch did not take effect (the permission prompt was declined?)."; };
            benchRtBack.Click += (s, e) => { if (BenchRuntime.PutBack()) BenchRuntimeRefresh(); else benchRt.Text = "Nothing was put back (the permission prompt was declined?). The normal launcher's OpenXR runtime button also switches back."; };
            BenchRuntimeRefresh();
            benchBtn.Click += (s, e) => BenchToggle();
            benchAnalyze.Click += (s, e) => BenchOpenAnalyzer(benchLastDir);
            benchFolderBtn.Click += (s, e) => { try { Directory.CreateDirectory(BenchRecorder.RecordingsDir); Process.Start("explorer.exe", "\"" + BenchRecorder.RecordingsDir + "\""); } catch { } };
            benchDeep.Click += (s, e) => BenchDeepTrace();
            benchFull.CheckedChanged += (s, e) => { BenchSaveOwn(); if (benchFull.Checked && gameRunning && benchProbe == null) BenchStart(true); else if (benchFull.Checked) BenchSetState(benchProbe == null ? "Full session: waiting for a game to start." : benchState.Text, Color.DarkOrange); };
            benchKey.SelectedIndexChanged += (s, e) => { BenchSaveOwn(); BenchHotkeySet(); };
            benchCtrl.CheckedChanged += (s, e) => { BenchSaveOwn(); BenchHotkeySet(); };
            benchBeep.CheckedChanged += (s, e) => BenchSaveOwn();
            benchSceneBox.Leave += (s, e) => BenchSaveOwn();
            benchTimer.Tick += (s, e) => BenchTick(); benchTimer.Start();
            HandleCreated += (s, e) => { if (benchHot == null && !benchDemo) { benchHot = new BenchHotkey(); benchHot.Pressed += () => BeginInvoke((Action)BenchToggle); BenchHotkeySet(); } };
            benchSaved.Text = "";
            benchAnalyze.Enabled = Directory.Exists(BenchRecorder.RecordingsDir);
        }

        void BenchSaveOwn()
        {
            var own = BenchRecorder.Own();
            Json.Set(own, "scene", benchSceneBox.Text); Json.Set(own, "full_session", benchFull.Checked); Json.Set(own, "beep", benchBeep.Checked); Json.Set(own, "ctrl", benchCtrl.Checked);
            Json.Set(own, "key_f", int.Parse(((string)benchKey.SelectedItem).Substring(1), CultureInfo.InvariantCulture));
            BenchRecorder.SaveOwn(own);
        }

        string BenchKeyName() { return (benchCtrl.Checked ? "Ctrl+" : "") + (string)benchKey.SelectedItem; }
        void BenchHotkeySet()
        {
            if (benchHot == null) return;
            bool ok = benchHot.Set(int.Parse(((string)benchKey.SelectedItem).Substring(1), CultureInfo.InvariantCulture), benchCtrl.Checked);
            if (!ok) BenchSetState("The key " + BenchKeyName() + " is taken by another program: pick another key (the button still works).", Color.DarkOrange);
            else if (benchProbe == null) BenchSetState("Ready. Press " + BenchKeyName() + " to start and stop" + ((string)benchKey.SelectedItem == "F12" ? " (F12 is read, not reserved: the game sees it too)" : " (the game does not see the key)") + ".", Color.DimGray);
        }

        void BenchSetState(string text, Color c) { benchState.Text = text; benchState.ForeColor = c; }
        void BenchBeepNow(bool start) { if (benchBeep.Checked && !benchDemo) try { (start ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Exclamation).Play(); } catch { } }

        static string BenchNewestDebugDir()
        {
            try { var d = Path.Combine(Paths.Logs, "debug"); return Directory.Exists(d) ? Directory.GetDirectories(d).OrderByDescending(x => Directory.GetCreationTimeUtc(x)).FirstOrDefault() : null; } catch { return null; }
        }

        void BenchToggle() { if (benchProbe != null) BenchStop("key"); else BenchStart(false); }

        void BenchStart(bool auto)
        {
            if (benchProbe != null || benchStopping || benchDemo) return;
            var dbg = BenchNewestDebugDir();
            if (dbg == null) { BenchSetState("No per-frame record is being written yet (the streamer is starting, or the headset is not connected).", Color.DarkOrange); return; }
            string scene = benchSceneBox.Text.Trim();
            if (scene.Length == 0) scene = currentExe.Length > 0 ? Games.Stem(currentExe) : "scene";
            scene = string.Join("_", scene.Split(Path.GetInvalidFileNameChars()));
            var dir = Path.Combine(BenchRecorder.RecordingsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + scene);
            var names = new List<string> { "alvr_host" }; if (currentExe.Length > 0) names.Add(Games.Stem(currentExe));
            try
            {
                Directory.CreateDirectory(dir);
                benchProbe = new Probe(new ProbeOptions { Dir = dir, Level = Level.L1, ProcessNames = names.ToArray(), SettingsDir = Paths.Config });
                benchProbe.Start();
            }
            catch (Exception e) { benchProbe = null; BenchSetState("Could not start the recorder: " + e.Message, Color.Firebrick); return; }
            benchDir = dir; benchDebugDir = dbg; benchScene = scene; benchStart = DateTime.UtcNow;
            benchBtn.Text = "Stop recording"; benchSaved.Text = ""; if (benchHeader != null) benchHeader.Recording = true;
            BenchBeepNow(true); BenchTick();
            Log.Write("INFO", "bench: recording started " + dir + (auto ? " (full session)" : ""));
        }

        void BenchStop(string why)
        {
            var p = benchProbe; if (p == null || benchStopping) return;
            benchStopping = true; benchProbe = null;
            var dir = benchDir; var dbg = benchDebugDir; var sc = benchScene; var secs = (DateTime.UtcNow - benchStart).TotalSeconds; var game = currentExe; bool full = benchFull.Checked;
            BenchBeepNow(false);
            BenchSetState("Saving the recording ...", Color.DimGray); benchBtn.Enabled = false;
            Task.Run(() =>
            {
                try { p.Stop(); } catch { }
                long frames = 0;
                try { frames = BenchCopyEngineFiles(dbg, dir); } catch (Exception e) { Log.Write("WARN", "bench: copy failed: " + e.Message); }
                try { Json.Save(Path.Combine(dir, "bench.json"), new Dictionary<string, object> { { "scene", sc }, { "game", game }, { "seconds", secs }, { "warmup_s", 10.0 }, { "full_session", full }, { "stopped_by", why }, { "frames", frames } }); } catch { }
                try { BeginInvoke((Action)(() => BenchSaved(dir, secs, frames))); } catch { benchStopping = false; }
            });
        }

        void BenchSaved(string dir, double secs, long frames)
        {
            benchStopping = false; benchBtn.Enabled = true; benchBtn.Text = "Start recording"; benchLastDir = dir; if (benchHeader != null) benchHeader.Recording = false;
            BenchSetState("Saved: " + Path.GetFileName(dir) + "  \u00B7  " + TimeSpan.FromSeconds(secs).ToString(@"h\:mm\:ss") + "  \u00B7  " + frames.ToString("N0", CultureInfo.InvariantCulture) + " frames of the stream", Color.SeaGreen);
            benchSaved.Text = "Nothing is analyzed yet: click Analyze recordings."; benchAnalyze.Enabled = true;
            Log.Write("INFO", "bench: recording saved " + dir);
        }

        // The engine's per-frame files of the run, cut to the recording (a streamer run holds many): rows whose time column is inside [start - 0.5 s, end + 0.5 s]. Returns the sent-frame count.
        internal static long BenchCopyEngineFiles(string debugDir, string dir)
        {
            if (debugDir == null || !Directory.Exists(debugDir)) return 0;
            double t0 = double.NaN, t1 = double.NaN;
            try
            {
                var sj = File.ReadAllText(Path.Combine(dir, "session.json"));
                Func<string, double> num = k => { int i = sj.IndexOf("\"" + k + "\":"); if (i < 0) return double.NaN; i += k.Length + 3; int j = sj.IndexOfAny(new[] { ',', '\n' }, i); double v; return double.TryParse(sj.Substring(i, j - i).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN; };
                t0 = num("started_qpc_s") - 0.5; t1 = num("ended_qpc_s") + 0.5;
            }
            catch { }
            long frames = 0;
            var cols = new Dictionary<string, int> { { "host_frames.csv", 7 }, { "host_pipeline.csv", 7 }, { "host_pipeline_fates.csv", 2 }, { "client_graph.csv", 0 }, { "client_frames.csv", 0 }, { "tracking.csv", 0 } };
            foreach (var f in Directory.GetFiles(debugDir))
            {
                var n = Path.GetFileName(f);
                int col; bool trim = cols.TryGetValue(n, out col);
                bool take = trim || n == "host_events.jsonl" || (n.StartsWith("shim_") && n.EndsWith("_frames.csv"));
                if (!take) continue;
                try
                {
                    using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var r = new StreamReader(src)) using (var w = new StreamWriter(Path.Combine(dir, n), false, new System.Text.UTF8Encoding(false)))
                    {
                        w.NewLine = "\n";
                        string line; bool header = true;
                        while ((line = r.ReadLine()) != null)
                        {
                            if (header || !trim || double.IsNaN(t0)) { w.WriteLine(line); header = false; continue; }
                            var parts = line.Split(',');
                            double t; if (parts.Length <= col || !double.TryParse(parts[col], NumberStyles.Float, CultureInfo.InvariantCulture, out t)) continue;
                            if (t < t0 || t > t1) continue;
                            w.WriteLine(line); if (n == "host_frames.csv") frames++;
                        }
                    }
                }
                catch (Exception e) { Log.Write("WARN", "bench: could not copy " + n + ": " + e.Message); }
            }
            return frames;
        }

        // the OpenXR runtime line: who is active, why it matters, and the one click that puts the previous one back
        void BenchRuntimeRefresh()
        {
            if (benchDemo) return;
            bool ours = OpenXr.IsOurs(); string before = BenchRuntime.BeforeName;
            if (ours)
            {
                benchRt.Text = "OpenXR runtime: Black Box (this folder). Games started now load the telemetry build and are recorded." + (BenchRuntime.Switched ? "\nWhen you are done, put back " + before + "." : "\nNot switched from here: the normal launcher's OpenXR runtime button switches back.");
                benchRt.ForeColor = Color.SeaGreen; benchRtUse.Visible = false; benchRtBack.Visible = BenchRuntime.Switched; benchRtBack.Text = "Put back " + before;
            }
            else
            {
                var name = OpenXr.Name(OpenXr.ActiveRuntime());
                benchRt.Text = "OpenXR runtime: " + name + ". A game started now would NOT be recorded: Windows has one active runtime and games load it, so the Black Box must be it while you record.";
                benchRt.ForeColor = Color.DarkOrange; benchRtUse.Visible = true; benchRtBack.Visible = false;
            }
        }

        void BenchTick()
        {
            BenchRuntimeRefresh();
            if (benchDemo) return;
            if (benchProbe == null) { return; }
            var el = (DateTime.UtcNow - benchStart).TotalSeconds; var p = benchProbe;
            var lost = p.LostRows;
            BenchSetState("Recording " + TimeSpan.FromSeconds(el).ToString(@"h\:mm\:ss") + (benchFull.Checked ? " (full session)" : "") + " \u00B7 GPU " + (p.LiveGpuUtil.Length > 0 ? p.LiveGpuUtil + " %" : "n/a") + " \u00B7 " + (p.LiveClock.Length > 0 ? p.LiveClock + " MHz" : "n/a") + " \u00B7 " + (p.LivePower.Length > 0 ? p.LivePower + " W" : "n/a") + " \u00B7 recorder " + (lost == 0 ? "OK" : lost + " lost") + "\nPress " + BenchKeyName() + " or the button to stop.", Color.SeaGreen);
        }

        partial void BenchHostEvent(string name, Dictionary<string, object> d)
        {
            if (!BenchRecorder.Enabled || benchDemo) return;
            var exe = d != null && d.TryGetValue("exe", out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
            if (IsMirrorApp(exe)) return;
            if (name == "game_started" && benchFull.Checked && benchProbe == null) { currentExe = exe; BeginInvoke((Action)(() => BenchStart(true))); }
            else if (name == "game_ended" && benchProbe != null && benchFull.Checked) BeginInvoke((Action)(() => BenchStop("game closed")));
        }

        partial void BenchClosing()
        {
            if (!BenchRecorder.Enabled || benchDemo) return;
            try { benchTimer.Stop(); if (benchHot != null) benchHot.Dispose(); } catch { }
            try
            {
                if (OpenXr.IsOurs() && BenchRuntime.Switched && MessageBox.Show("Put back " + BenchRuntime.BeforeName + " as the OpenXR runtime now? (Windows asks for permission.)\n\nIf you keep the Black Box as the runtime, games you start later still load the telemetry build and the normal streamer will not be recorded or used until you switch back (the normal launcher's OpenXR runtime button does it).",
                    "AirFrameXR Black Box", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) BenchRuntime.PutBack();
            }
            catch { }
            var p = benchProbe;
            if (p != null)
            {
                benchProbe = null;
                try { p.Stop(); BenchCopyEngineFiles(benchDebugDir, benchDir); Json.Save(Path.Combine(benchDir, "bench.json"), new Dictionary<string, object> { { "scene", benchScene }, { "seconds", (DateTime.UtcNow - benchStart).TotalSeconds }, { "warmup_s", 10.0 }, { "stopped_by", "streamer closed" } }); } catch { }
            }
        }

        static void BenchOpenAnalyzer(string select)
        {
            try
            {
                var exe = Path.Combine(Paths.Bin, "blackbox_analyzer.exe");
                if (!File.Exists(exe)) { MessageBox.Show("bin\\blackbox_analyzer.exe is missing from this folder.", "AirFrameXR"); return; }
                Process.Start(new ProcessStartInfo(exe, string.IsNullOrEmpty(select) ? "" : "--select \"" + select + "\"") { UseShellExecute = false, WorkingDirectory = Paths.Bin });
            }
            catch (Exception e) { MessageBox.Show("Cannot open the analyzer: " + e.Message, "AirFrameXR"); }
        }

        void BenchDeepTrace()
        {
            if (benchDemo) return;
            var dir = benchProbe != null && benchDir != null ? benchDir : Path.Combine(BenchRecorder.RecordingsDir, "deep_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            try
            {
                Directory.CreateDirectory(dir);
                var etl = Path.Combine(dir, "deep_trace.etl");
                // Windows' own recorder (wpr.exe, built in): one elevated cmd for the whole 20 s, so Windows asks once
                Process.Start(new ProcessStartInfo("cmd.exe", "/c wpr -start GeneralProfile -start GPU -filemode & timeout /t 20 /nobreak >nul & wpr -stop \"" + etl + "\"") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                benchSaved.Text = "Deep trace running for 20 s: " + etl; benchSaved.ForeColor = Color.SeaGreen;
                Log.Write("INFO", "bench: deep trace started -> " + etl);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { benchSaved.Text = "Deep trace skipped (permission declined). The recording is unchanged."; benchSaved.ForeColor = Color.SeaGreen; }   // a normal outcome
            catch (Exception e) { benchSaved.Text = "Deep trace could not start: " + e.Message; benchSaved.ForeColor = Color.DarkOrange; }
        }

        /// the UI matrix: the panel in each of its states with fixed content (no probe, no hotkey)
        public void BenchDemoState(string st)
        {
            benchDemo = true; try { benchTimer.Stop(); } catch { }
            if (st == "recording" || st == "saved" || st == "full-waiting") { benchRt.Text = "OpenXR runtime: Black Box (this folder). Games started now load the telemetry build and are recorded.\nWhen you are done, put back Virtual Desktop OpenXR."; benchRt.ForeColor = Color.SeaGreen; benchRtUse.Visible = false; benchRtBack.Visible = true; benchRtBack.Text = "Put back Virtual Desktop OpenXR"; }
            else { benchRt.Text = "OpenXR runtime: AirFrameXR. A game started now would NOT be recorded: Windows has one active runtime and games load it, so the Black Box must be it while you record."; benchRt.ForeColor = Color.DarkOrange; benchRtUse.Visible = true; benchRtBack.Visible = false; }
            benchSceneBox.Text = "Lies of P corridor";
            if (st == "full-waiting") { benchFull.Checked = true; BenchSetState("Full session: waiting for a game to start.", Color.DarkOrange); }
            else if (st == "recording") { if (benchHeader != null) benchHeader.Recording = true; benchBtn.Text = "Stop recording"; BenchSetState("Recording 0:42:17 (full session) \u00B7 GPU 78 % \u00B7 1905 MHz \u00B7 231.4 W \u00B7 recorder OK\nPress F8 or the button to stop.", Color.SeaGreen); }
            else if (st == "saved") { BenchSetState("Saved: 20261030-210512_Lies of P corridor  \u00B7  1:32:08  \u00B7  496 102 frames of the stream", Color.SeaGreen); benchSaved.Text = "Nothing is analyzed yet: click Analyze recordings."; }
            else if (st == "key-taken") BenchSetState("The key F8 is taken by another program: pick another key (the button still works).", Color.DarkOrange);
            else BenchSetState("Settings copied from C:\\Users\\player\\AirFrameXR (read only).  Start the game, then press the key (or tick Full session first).", Color.DimGray);
        }
    }
}
