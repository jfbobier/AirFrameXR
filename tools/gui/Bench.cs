// bench.exe: the diagnostics window of the Bench flavour (docs/BENCH_UI.md, docs/TELEMETRY_PLAN.md). Started on purpose to answer "where does the time go
// in this game?": Prepare (name the scene, check the sources) -> Capture (the headset is on; Start/Stop also on F9) -> Read (one verdict, the findings, the worst
// frames, the report). It runs as the normal user: only the Deep trace button asks Windows for permission, for one action, and may be declined.
// The streamer is NOT inside this window: it is the normal streamer.exe of this (debug = telemetry) package, started from here with debug logging on; the
// per-frame files it writes (host_frames.csv, client_graph.csv, ...) are copied into the session folder when the capture stops, next to perfcore.dll's probe files.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AirFrameXR.Perf;

namespace AirFrameXR
{
    public class BenchForm : DpiForm
    {
        enum Phase { Prepare, Capture, Read }
        Phase phase = Phase.Prepare;
        readonly AppSettings settings = new AppSettings();
        readonly Label banner = new Label { AutoSize = true, Font = Ui.Bold, Margin = new Padding(0, 0, 0, 6) };
        readonly Label status = new Label { AutoSize = true, Font = Ui.Body, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 8) };
        readonly ListBox sessions = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, Font = Ui.Body };
        readonly List<string> sessionDirs = new List<string>();
        readonly FlowLayoutPanel right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 8, 12, 8), BackColor = Color.White };
        // prepare
        readonly TextBox scene = new TextBox { Width = 260, Text = "scene 1" };
        readonly TextBox procs = new TextBox { Width = 260, Text = "alvr_host" };
        readonly ComboBox duration = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        readonly NumericUpDown warm = new NumericUpDown { Minimum = 0, Maximum = 60, Value = 10, Width = 60 };
        readonly Button btnStreamer = new Button { Text = "Start the streamer (debug logging on)", AutoSize = true };
        readonly Button btnStart = new Button { Text = "Start capture  (F9)", AutoSize = true, Font = Ui.Bold };
        readonly Button btnDeep = new Button { Text = "Deep trace (admin) ...", AutoSize = true };
        readonly Label startNote = new Label { AutoSize = true, ForeColor = Color.DimGray, Font = Ui.Body };
        readonly Label deepNote = new Label { AutoSize = true, ForeColor = Color.DimGray, Font = Ui.Body };
        // capture
        readonly Label state = new Label { AutoSize = true, Font = Ui.Big, Margin = new Padding(0, 6, 0, 6) };
        readonly FlowLayoutPanel tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), MaximumSize = new Size(100, 0) };
        readonly Tile tElapsed = Ui.MakeTile("Recording", "-"), tGpu = Ui.MakeTile("GPU", "-"), tClock = Ui.MakeTile("Core clock", "-"), tPower = Ui.MakeTile("Power", "-"),
                      tHost = Ui.MakeTile("Frames recorded", "-"), tLost = Ui.MakeTile("Recorder", "-");
        // read
        readonly Label verdict = new Label { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Margin = new Padding(0, 4, 0, 6), MaximumSize = new Size(100, 0) };
        readonly FlowLayoutPanel findings = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        readonly ListView worst = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, Height = 180, Width = 100, HideSelection = false };
        readonly Button btnReport = new Button { Text = "Open report.md", AutoSize = true }, btnFolder = new Button { Text = "Open folder", AutoSize = true }, btnExport = new Button { Text = "Export session...", AutoSize = true };
        readonly Label exportNote = new Label { AutoSize = true, ForeColor = Color.DimGray };
        readonly FlowLayoutPanel readBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0), Visible = false };
        readonly FlowLayoutPanel captureBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0), Visible = false };
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };

        Probe probe; string sessionDir; DateTime capStart; double capLimit; string capDebugDir; double capWarm; string capScene;
        bool demo, hotkeyOk;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        const int HotkeyId = 0x4146, WmHotkey = 0x312;

        public static string SessionsDir { get { return Path.Combine(Paths.Logs, "bench"); } }

        public BenchForm()
        {
            Text = "AirFrameXR Bench \u2013 performance diagnosis";
            Icon = Ui.AppIcon();
            Font = Ui.Body;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Width = 1180; Height = 820; MinimumSize = new Size(820, 560);
            Ui.FitToWorkArea(this);

            duration.Items.AddRange(new object[] { "60 s", "90 s", "120 s", "Until I stop it" }); duration.SelectedIndex = 1;
            var left = new Panel { Dock = DockStyle.Left, Width = 300, Padding = new Padding(8), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            var leftTitle = new Label { Text = "Sessions", Dock = DockStyle.Top, Font = Ui.Bold, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
            left.Controls.Add(sessions); left.Controls.Add(leftTitle);
            sessions.BackColor = left.BackColor;
            sessions.SelectedIndexChanged += (s, e) => { if (phase != Phase.Capture && sessions.SelectedIndex >= 0 && sessions.SelectedIndex < sessionDirs.Count) ShowSession(sessionDirs[sessions.SelectedIndex]); };

            tiles.Controls.AddRange(new Control[] { tElapsed, tGpu, tClock, tPower, tHost, tLost });
            captureBox.Controls.Add(state); captureBox.Controls.Add(tiles);
            var stop = new Button { Text = "Stop  (F9)", AutoSize = true, Font = Ui.Bold, Name = "stop" };
            stop.Click += (s, e) => StopCapture();
            captureBox.Controls.Add(stop);

            worst.Columns.Add("frame"); worst.Columns.Add("sent at (QPC s)"); worst.Columns.Add("gap ms"); worst.Columns.Add("queue ms"); worst.Columns.Add("encode ms"); worst.Columns.Add("hold ms"); worst.Columns.Add("stage");
            var btns = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            btns.Controls.AddRange(new Control[] { btnReport, btnFolder, btnExport });
            readBox.Controls.Add(verdict); readBox.Controls.Add(findings);
            readBox.Controls.Add(new Label { Text = "Worst late frames", Font = Ui.Bold, AutoSize = true, Margin = new Padding(0, 10, 0, 4) });
            readBox.Controls.Add(worst); readBox.Controls.Add(btns); readBox.Controls.Add(exportNote);

            var prep = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            Func<string, Control, Control> labeled = (t, c) => { var f = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) }; f.Controls.Add(new Label { Text = t, AutoSize = true, Width = 170, Margin = new Padding(0, 5, 6, 0) }); f.Controls.Add(c); return f; };
            prep.Controls.Add(new Label { Text = "Prepare", Font = Ui.Big, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            prep.Controls.Add(labeled("Scene name", scene));
            prep.Controls.Add(labeled("Duration", duration));
            prep.Controls.Add(labeled("Warm-up left out (s)", warm));
            prep.Controls.Add(labeled("Processes whose GPU engines are read", procs));
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            row.Controls.AddRange(new Control[] { btnStreamer, btnStart, btnDeep });
            prep.Controls.Add(row); prep.Controls.Add(startNote); prep.Controls.Add(deepNote);
            btnStart.Name = "start"; btnDeep.Name = "deep"; btnStreamer.Name = "streamer";

            right.Controls.Add(banner); right.Controls.Add(status); right.Controls.Add(prep); right.Controls.Add(captureBox); right.Controls.Add(readBox);
            Controls.Add(right); Controls.Add(left);

            btnStreamer.Click += (s, e) => StartStreamer();
            btnStart.Click += (s, e) => { if (phase == Phase.Capture) StopCapture(); else StartCapture(); };
            btnDeep.Click += (s, e) => DeepTrace();
            btnReport.Click += (s, e) => { var f = Path.Combine(sessionDir ?? "", "report.md"); if (File.Exists(f)) Process.Start("notepad.exe", "\"" + f + "\""); };
            btnFolder.Click += (s, e) => { if (Directory.Exists(sessionDir)) Process.Start("explorer.exe", "\"" + sessionDir + "\""); };
            btnExport.Click += (s, e) => Export();
            right.SizeChanged += (s, e) => FitWidth(); Load += (s, e) => FitWidth();   // widths follow the pane (no fixed pixels: the matrix flags a sideways scroll bar)
            timer.Tick += (s, e) => Tick(); timer.Start();
            Load += (s, e) => { if (!demo) { FillSessions(); Refresh2(); } };
            HandleCreated += (s, e) => { if (!demo) hotkeyOk = RegisterHotKey(Handle, HotkeyId, 0, 0x78); };   // F9
            FormClosing += (s, e) => { if (phase == Phase.Capture && !demo && MessageBox.Show(this, "A capture is running. Stop it and keep what was recorded?", "AirFrameXR Bench", MessageBoxButtons.OKCancel) == DialogResult.Cancel) { e.Cancel = true; return; } if (phase == Phase.Capture && !demo) try { probe.Stop(); } catch { } try { UnregisterHotKey(Handle, HotkeyId); } catch { } };
            Refresh2();
            Ui.ScreenshotWhenShown(this, 1500);
        }

        // the width the content may use: the pane minus its padding and the vertical scroll bar
        int Wd() { return Math.Max(Ui.Px(this, 280), right.ClientSize.Width - right.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - Ui.Px(this, 4)); }
        void FitWidth()
        {
            int w = Wd();
            worst.Width = w; verdict.MaximumSize = new Size(w, 0); tiles.MaximumSize = new Size(w, 0);
            foreach (Control c in findings.Controls) if (c is Label) c.MaximumSize = new Size(w - ((Label)c).Margin.Left, 0);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId) { if (phase == Phase.Capture) StopCapture(); else StartCapture(); return; }
            base.WndProc(ref m);
        }

        // ---- state of the machine ----------------------------------------------------------------------------------------
        static bool HostRunning() { try { return Process.GetProcessesByName("alvr_host").Length > 0; } catch { return false; } }
        static bool StreamerRunning() { try { return Process.GetProcessesByName("streamer").Length > 0; } catch { return false; } }
        static string NewestDebugDir()
        {
            try { var d = Path.Combine(Paths.Logs, "debug"); return Directory.Exists(d) ? Directory.GetDirectories(d).OrderByDescending(x => Directory.GetCreationTimeUtc(x)).FirstOrDefault() : null; } catch { return null; }
        }
        static bool IsTelemetryBuild() { try { return File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flavour.txt")).Trim() == "debug"; } catch { return false; } }
        static long CountLines(string path)
        {
            try { using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { long n = 0; var buf = new byte[1 << 16]; int r; while ((r = fs.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < r; i++) if (buf[i] == 10) n++; return Math.Max(0, n - 1); } }
            catch { return 0; }
        }

        void Refresh2()
        {
            if (demo) return;
            bool tele = IsTelemetryBuild();
            bool host = HostRunning();
            var dbg = NewestDebugDir();
            bool recording = host && dbg != null && File.Exists(Path.Combine(dbg, "host_frames.csv")) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(dbg, "host_frames.csv"))).TotalSeconds < 15;
            banner.Text = tele ? "BENCH: measurement build (per-frame logging on). Not for normal play." : "This is a stock build: it does not record per-frame data. Use the Bench package.";
            banner.ForeColor = tele ? Color.DarkOrange : Color.Firebrick;
            status.Text = "Streamer: " + (host ? "running" : "not running") + "   \u00B7   Debug logging: " + (settings.Debug ? "on" : "off") + "   \u00B7   Stream record: " + (recording ? "being written" : "not being written") + "   \u00B7   GPU: " + GpuNote();
            if (phase != Phase.Capture)
            {
                btnStart.Enabled = tele && host;
                startNote.Text = !tele ? "Needs the telemetry (debug) build." : !host ? "Start the streamer first, connect the headset and start the game." : !recording ? "The streamer is running but no per-frame file is being written yet: start the game (and check that debug logging is on)." : "Ready. Put the headset on, then press F9 or the button.";
                startNote.ForeColor = btnStart.Enabled && recording ? Color.SeaGreen : Color.DarkOrange;
                btnDeep.Enabled = host;
                deepNote.Text = deepNoteText;
            }
        }
        string deepNoteText = "Deep trace: GPU and CPU scheduling for 20 s with Windows' own recorder. Windows asks for permission; the stream keeps running.";
        string gpuNoteCache; DateTime gpuNoteAt;
        string GpuNote()
        {
            if (gpuNoteCache != null && (DateTime.UtcNow - gpuNoteAt).TotalSeconds < 30) return gpuNoteCache;
            gpuNoteAt = DateTime.UtcNow;
            try { gpuNoteCache = File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll")) || File.Exists(@"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll") ? "NVML found" : "NVML not found (no GPU sensors)"; } catch { gpuNoteCache = "unknown"; }
            return gpuNoteCache;
        }

        void SetPhase(Phase p)
        {
            phase = p;
            captureBox.Visible = p == Phase.Capture; readBox.Visible = p == Phase.Read;
            btnStart.Text = p == Phase.Capture ? "Stop  (F9)" : "Start capture  (F9)";
            foreach (Control c in new Control[] { scene, procs, duration, warm, btnStreamer }) c.Enabled = p != Phase.Capture;
        }

        // ---- actions ---------------------------------------------------------------------------------------------------------
        void StartStreamer()
        {
            try
            {
                if (!settings.Debug) { settings.Debug = true; settings.Save(); Log.Write("INFO", "bench: debug logging switched on for the capture"); }
                if (StreamerRunning() || HostRunning()) { startNote.Text = "The streamer is already running. Debug logging applies from its next start: restart it if the stream record stays 'not being written'."; return; }
                Process.Start(new ProcessStartInfo(Paths.Runtime) { UseShellExecute = false, WorkingDirectory = Paths.Bin });
                startNote.Text = "Streamer started. Connect the headset, start the game.";
            }
            catch (Exception e) { startNote.Text = "Could not start the streamer: " + e.Message; }
        }

        void StartCapture()
        {
            if (phase == Phase.Capture || demo) return;
            string nm = string.Join("_", (scene.Text.Trim().Length == 0 ? "scene" : scene.Text.Trim()).Split(Path.GetInvalidFileNameChars()));
            capDebugDir = NewestDebugDir();
            if (!IsTelemetryBuild() || !HostRunning()) { Refresh2(); return; }
            capScene = nm; capWarm = (double)warm.Value;
            capLimit = duration.SelectedIndex == 0 ? 60 : duration.SelectedIndex == 1 ? 90 : duration.SelectedIndex == 2 ? 120 : 0;
            sessionDir = Path.Combine(SessionsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + nm);
            try
            {
                Directory.CreateDirectory(sessionDir);
                probe = new Probe(new ProbeOptions { Dir = sessionDir, Level = Level.L1, ProcessNames = procs.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries), SettingsDir = Paths.Config });
                probe.Start();
            }
            catch (Exception e) { startNote.Text = "Could not start the recorder: " + e.Message; return; }
            capStart = DateTime.UtcNow;
            SetPhase(Phase.Capture); Tick();
            Log.Write("INFO", "bench: capture started " + sessionDir);
        }

        void StopCapture()
        {
            if (phase != Phase.Capture || demo) return;
            var p = probe; probe = null; var dir = sessionDir; var dbg = capDebugDir ?? NewestDebugDir(); var warmS = capWarm; var sc = capScene; var secs = (DateTime.UtcNow - capStart).TotalSeconds;
            state.Text = "Stopping and building the report ..."; state.ForeColor = Color.DimGray; btnStart.Enabled = false;
            timer.Stop();
            Task.Run(() =>
            {
                try { p.Stop(); } catch { }
                CopyEngineFiles(dbg, dir);
                try
                {
                    var meta = new Dictionary<string, object> { { "scene", sc }, { "warmup_s", warmS }, { "seconds", secs }, { "processes", procs.Text } };
                    Json.Save(Path.Combine(dir, "bench.json"), meta);
                    var rep = Report.Build(dir, warmS);
                    File.WriteAllText(Path.Combine(dir, "report.md"), rep.Markdown, new System.Text.UTF8Encoding(false));
                } catch (Exception e) { Log.Write("ERROR", "bench: report failed: " + e.Message); }
                try { BeginInvoke((Action)(() => { timer.Start(); FillSessions(); ShowSession(dir); btnStart.Enabled = true; Refresh2(); })); } catch { }
            });
        }

        // The engine's per-frame files of the debug run being recorded: copied whole (report.cs cuts the window); a file that is still open for writing is read with sharing.
        static void CopyEngineFiles(string debugDir, string dir)
        {
            if (debugDir == null || !Directory.Exists(debugDir)) { Log.Write("WARN", "bench: no debug folder to copy the stream record from"); return; }
            foreach (var f in Directory.GetFiles(debugDir))
            {
                var n = Path.GetFileName(f);
                bool take = n == "host_frames.csv" || n == "host_pipeline.csv" || n == "host_pipeline_fates.csv" || n == "client_graph.csv" || n == "client_frames.csv" || n == "tracking.csv" || n == "host_events.jsonl" || (n.StartsWith("shim_") && n.EndsWith("_frames.csv"));
                if (!take) continue;
                try
                {
                    using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var dst = new FileStream(Path.Combine(dir, n), FileMode.Create, FileAccess.Write)) src.CopyTo(dst);
                }
                catch (Exception e) { Log.Write("WARN", "bench: could not copy " + n + ": " + e.Message); }
            }
        }

        void Tick()
        {
            if (demo) return;
            if (phase != Phase.Capture) { if (DateTime.UtcNow.Second % 2 == 0) Refresh2(); return; }
            var el = (DateTime.UtcNow - capStart).TotalSeconds;
            bool inWarm = el < capWarm;
            state.Text = (inWarm ? "Warm-up " : "Recording ") + TimeSpan.FromSeconds(el).ToString(@"mm\:ss") + (capLimit > 0 ? " of " + TimeSpan.FromSeconds(capLimit).ToString(@"mm\:ss") : "");
            state.ForeColor = inWarm ? Color.DarkOrange : Color.SeaGreen;
            tElapsed.Set(TimeSpan.FromSeconds(el).ToString(@"mm\:ss"), inWarm ? "warm-up (left out)" : "in the report");
            var p = probe;
            if (p != null)
            {
                tGpu.Set(p.LiveGpuUtil.Length > 0 ? p.LiveGpuUtil + " %" : "n/a", "utilisation");
                tClock.Set(p.LiveClock.Length > 0 ? p.LiveClock + " MHz" : "n/a", p.LiveLimiter == "0x1" || p.LiveLimiter.Length == 0 ? "no limiter" : "limiter " + p.LiveLimiter, p.LiveLimiter == "0x1" || p.LiveLimiter.Length == 0 ? (Color?)null : Color.DarkOrange);
                tPower.Set(p.LivePower.Length > 0 ? p.LivePower + " W" : "n/a", p.LiveTemp.Length > 0 ? p.LiveTemp + " C" : "");
                tLost.Set(p.LostRows == 0 ? "OK" : p.LostRows + " lost", "events lost: " + p.LostRows, p.LostRows == 0 ? Color.SeaGreen : Color.Firebrick);
            }
            var dbg = capDebugDir ?? NewestDebugDir();
            tHost.Set(dbg != null ? CountLines(Path.Combine(dbg, "host_frames.csv")).ToString(CultureInfo.InvariantCulture) : "n/a", "sent frames in the stream record");
            if (capLimit > 0 && el >= capLimit) StopCapture();
        }

        void DeepTrace()
        {
            if (demo) return;
            var dir = sessionDir != null && phase == Phase.Capture ? sessionDir : Path.Combine(SessionsDir, "deep_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            var etl = Path.Combine(dir, "deep_trace.etl");
            try
            {
                // Windows' own recorder (wpr.exe, built in): one elevated cmd for the whole 20 s, so Windows asks once
                var psi = new ProcessStartInfo("cmd.exe", "/c wpr -start GeneralProfile -start GPU -filemode & timeout /t 20 /nobreak >nul & wpr -stop \"" + etl + "\"") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                Process.Start(psi);
                deepNoteText = "Deep trace running for 20 s (Windows' recorder). The file will be: " + etl;
                deepNote.ForeColor = Color.SeaGreen;
                Log.Write("INFO", "bench: deep trace started -> " + etl);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                deepNoteText = "Deep trace skipped (Windows' permission was declined). The capture is unchanged."; deepNote.ForeColor = Color.SeaGreen;   // a normal outcome, not an error
            }
            catch (Exception e) { deepNoteText = "Deep trace could not start: " + e.Message; deepNote.ForeColor = Color.DarkOrange; }
            deepNote.Text = deepNoteText;
        }

        void Export()
        {
            if (sessionDir == null || !Directory.Exists(sessionDir)) return;
            using (var sfd = new SaveFileDialog { Filter = "Zip|*.zip", FileName = Path.GetFileName(sessionDir) + ".zip" })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (File.Exists(sfd.FileName)) File.Delete(sfd.FileName);
                    using (var z = ZipFile.Open(sfd.FileName, ZipArchiveMode.Create))
                        foreach (var f in Directory.GetFiles(sessionDir).Where(f => !f.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) || new FileInfo(f).Length < 200L * 1024 * 1024))
                            z.CreateEntryFromFile(f, Path.GetFileName(f));
                    exportNote.Text = "Saved to " + sfd.FileName; exportNote.ForeColor = Color.SeaGreen;
                }
                catch (Exception e) { exportNote.Text = "Export failed: " + e.Message; exportNote.ForeColor = Color.Firebrick; }
            }
        }

        // ---- sessions and the Read view ------------------------------------------------------------------------------------
        void FillSessions()
        {
            sessionDirs.Clear(); sessions.Items.Clear();
            try
            {
                if (Directory.Exists(SessionsDir))
                    foreach (var d in Directory.GetDirectories(SessionsDir).OrderByDescending(x => Path.GetFileName(x)))
                    {
                        if (!File.Exists(Path.Combine(d, "session.json")) && !File.Exists(Path.Combine(d, "report.md"))) continue;
                        sessionDirs.Add(d); sessions.Items.Add(Path.GetFileName(d));
                    }
            }
            catch { }
            if (sessions.Items.Count == 0) sessions.Items.Add("(no session yet)");
        }

        void ShowSession(string dir)
        {
            sessionDir = dir;
            ReportData rep;
            double warmS = 10;
            try { var meta = Json.Load(Path.Combine(dir, "bench.json")); object w; if (meta.TryGetValue("warmup_s", out w)) warmS = Convert.ToDouble(w, CultureInfo.InvariantCulture); } catch { }
            try { rep = Report.Build(dir, warmS); } catch (Exception e) { rep = new ReportData { VerdictStatus = "INSUFFICIENT", Verdict = "The report could not be built: " + e.Message }; }
            ShowReport(rep);
            SetPhase(Phase.Read);
        }

        static Color StatusColor(string s) { return s == "OK" ? Color.SeaGreen : s == "PROBLEM" ? Color.Firebrick : s == "WATCH" ? Color.DarkOrange : Color.DimGray; }

        void ShowReport(ReportData rep)
        {
            verdict.Text = rep.VerdictStatus + ": " + rep.Verdict; verdict.ForeColor = StatusColor(rep.VerdictStatus);
            findings.SuspendLayout(); findings.Controls.Clear();
            foreach (var f in rep.Findings.Where(f => f.Id != "T11").OrderBy(f => f.Status == "PROBLEM" ? 0 : f.Status == "WATCH" ? 1 : f.Status == "INSUFFICIENT" ? 2 : 3))
            {
                findings.Controls.Add(new Label { Text = f.Status + "   " + f.Title, AutoSize = true, Font = Ui.Bold, ForeColor = StatusColor(f.Status), Margin = new Padding(0, 6, 0, 0), MaximumSize = new Size(Wd(), 0) });
                if (!string.IsNullOrEmpty(f.Detail)) findings.Controls.Add(new Label { Text = f.Detail, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 0, 0, 0), MaximumSize = new Size(Wd() - 12, 0) });
                if (!string.IsNullOrEmpty(f.Test) && f.Status != "OK") findings.Controls.Add(new Label { Text = "Try: " + f.Test, AutoSize = true, Margin = new Padding(12, 0, 0, 0), MaximumSize = new Size(Wd() - 12, 0) });
            }
            findings.ResumeLayout();
            FitWidth();
            worst.Items.Clear();
            foreach (var w in rep.Worst) worst.Items.Add(new ListViewItem(new[] { w.Frame.ToString(CultureInfo.InvariantCulture), w.T.ToString("F3", CultureInfo.InvariantCulture), w.GapMs.ToString("F1", CultureInfo.InvariantCulture), Nn(w.QueueMs), Nn(w.EncodeMs), Nn(w.HoldMs), w.Class }));
            Ui.FitColumns(worst);
            exportNote.Text = "";
        }
        static string Nn(double v) { return double.IsNaN(v) ? "n/a" : v.ToString("F1", CultureInfo.InvariantCulture); }

        // ---- demo states for the UI matrix (harness/ui_matrix.py): the same controls, filled with fixed content ----------------
        public void ApplyDemoState(string st)
        {
            demo = true; timer.Stop(); try { UnregisterHotKey(Handle, HotkeyId); } catch { }
            sessions.Items.Clear();
            if (st != "idle") { sessions.Items.Add("20261030-210512_Lies of P corridor"); sessions.Items.Add("20261030-203340_Lies of P corridor"); sessions.Items.Add("20261029-224103_Cyberpunk market"); }
            else sessions.Items.Add("(no session yet)");
            banner.Text = "BENCH: measurement build (per-frame logging on). Not for normal play."; banner.ForeColor = Color.DarkOrange;
            status.Text = st == "idle" ? "Streamer: not running   \u00B7   Debug logging: off   \u00B7   Stream record: not being written   \u00B7   GPU: NVML found"
                                       : "Streamer: running   \u00B7   Debug logging: on   \u00B7   Stream record: being written   \u00B7   GPU: NVML found";
            scene.Text = st == "long" ? "A very long scene name that somebody typed to describe the corridor with the fog and the three enemies" : "Lies of P corridor";
            if (st == "idle") { btnStart.Enabled = false; btnDeep.Enabled = false; startNote.Text = "Start the streamer first, connect the headset and start the game."; startNote.ForeColor = Color.DarkOrange; }
            else { btnStart.Enabled = true; startNote.Text = "Ready. Put the headset on, then press F9 or the button."; startNote.ForeColor = Color.SeaGreen; }
            deepNote.Text = deepNoteText;
            if (st == "capture")
            {
                SetPhase(Phase.Capture);
                state.Text = "Recording 00:42 of 01:30"; state.ForeColor = Color.SeaGreen;
                tElapsed.Set("00:42", "in the report"); tGpu.Set("78 %", "utilisation"); tClock.Set("1905 MHz", "limiter 0x4", Color.DarkOrange); tPower.Set("231.4 W", "71 C"); tHost.Set("3 780", "sent frames in the stream record"); tLost.Set("OK", "events lost: 0", Color.SeaGreen);
            }
            else if (st == "read-smooth" || st == "read-problem" || st == "read-insufficient")
            {
                var rep = new ReportData();
                if (st == "read-smooth") { rep.VerdictStatus = "OK"; rep.Verdict = "Smooth: 0.31 % late frames, encode p99 4.2 ms"; rep.Findings.Add(new Finding { Id = "T02", Status = "OK", Title = "Deadlines met", Detail = "gap p50 11.09, p95 11.25, p99 11.40, max 24.10 ms; late frames 21 of 6900 (0.31 %), longest run 1." }); }
                else if (st == "read-problem")
                {
                    rep.VerdictStatus = "PROBLEM"; rep.Verdict = "Late frames: 4.20 % - Late gaps: mostly encode took long";
                    rep.Findings.Add(new Finding { Id = "T02", Status = "PROBLEM", Title = "Late frames: 4.20 %", Detail = "gap p50 11.09, p95 22.20, p99 33.30, max 61.0 ms; late frames 290 of 6900 (4.20 %), longest run 3. Threshold: 1.00 %.", Test = "Compare the stage that made the late frames, then change one thing and capture again." });
                    rep.Findings.Add(new Finding { Id = "T03", Status = "PROBLEM", Title = "Late gaps: mostly encode took long", Detail = "Encode took long: 231; Waiting for the encoder (queue): 40; Upstream: 19.", Test = "Try a faster preset, lower bitrate or resolution scale, and capture again." });
                    rep.Findings.Add(new Finding { Id = "T07", Status = "WATCH", Title = "A GPU limiter was active 38.00 % of the time", Detail = "GPU util p50 97 / p95 100 %; core clock 1410-1905 MHz; power max 231 W. Limiter: software power cap." , Test = "Capture again with the limiter removed to see whether the late frames follow it." });
                    rep.Findings.Add(new Finding { Id = "T04", Status = "OK", Title = "Encode fits the period", Detail = "encode ms p50 3.9, p95 7.4, p99 12.1." });
                    for (int i = 0; i < 6; i++) rep.Worst.Add(new WorstFrame { Frame = 2103 + i * 311, T = 11852.123 + i * 3.7, GapMs = 33.3 - i, QueueMs = 1.2 + i, EncodeMs = 14.2 - i, HoldMs = 0.5, Class = "Encode took long" });
                }
                else { rep.VerdictStatus = "INSUFFICIENT"; rep.Verdict = "Not enough data: host_frames.csv is missing: the streamer of this session was not the telemetry build, or debug logging was off."; rep.Findings.Add(new Finding { Id = "T01", Status = "INSUFFICIENT", Title = "No stream record in this session", Detail = "host_frames.csv is missing.", Test = "Run the capture again with the Bench streamer and a game running for at least 30 s." }); }
                ShowReport(rep); SetPhase(Phase.Read);
                exportNote.Text = "Saved to C:\\Users\\player\\Desktop\\20261030-210512_Lies of P corridor.zip"; exportNote.ForeColor = Color.SeaGreen;
            }
            else SetPhase(Phase.Prepare);
        }

        [STAThread]
        static void Main()
        {
            Log.Component = "bench";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BenchForm());
        }
    }
}
