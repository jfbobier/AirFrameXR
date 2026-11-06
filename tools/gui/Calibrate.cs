// calibrate.exe: measures what the headset really shows and saves it as the field of view the games are told (docs/FOV_CALIBRATION.md).
// Opens on the fit page (docs/PICTURE_EDGES_DESIGN.md section 6): the probe's `fit` scene is started as a child, its JSON lines (the box per eye) are shown live and Enter saves a profile (method quickfit).
// `calibrate.exe --restrict` is the same app on the probe's `restrict` scene (section 6b, "Choose what the game renders", method restriction). The 1.3 ray sweep (30 minutes) is gone from the UI.
// The result page and its saving remain: a result is applied here, or kept as a file for Import a calibration file...
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AirFrameXR
{
    public class CalibrateForm : DpiForm
    {
        const string Wearer = "my";
        const int Directions = 24;   // the demo result only: synthetic rows in the shape FovFit reads (the 1.3 sweep's files), until step B has its own result
        static string Probe => Path.Combine(Paths.Dir, "bench", "xr_probe.exe");
        string RunDir => demoDir ?? Path.Combine(Paths.Config, "calibration");
        readonly string demoDir;   // the UI harness: synthetic sweeps in a temp folder instead of config\\calibration

        readonly AppSettings settings = new AppSettings();
        readonly string headset, headsetSlug;
        readonly FlowLayoutPanel page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16, 12, 16, 12), BackColor = Color.White };
        FovFitResult result;

        readonly bool restrictTool;   // --restrict: "Choose what the game renders" (the probe's restrict scene) instead of the fit

        public CalibrateForm(string demoFolder = null, bool restrict = false)
        {
            demoDir = demoFolder; restrictTool = restrict;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;   // numbers as in the docs and the other tools
            Text = restrictTool ? "AirFrameXR – Choose what the game renders" : "AirFrameXR – Calibrate the field of view";
            Icon = Ui.AppIcon();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Width = 760; Height = 640;   // design values at 96 dpi
            MinimumSize = new Size(600, 480);
            Ui.FitToWorkArea(this);
            Controls.Add(page);
            headset = demoFolder != null ? "Apple Headset" : settings.HeadsetName;
            headsetSlug = FovFit.Slug(headset).Replace('_', '-');
            FormClosing += (s, e) => EndTool();
            ShowIntro();
            Ui.ScreenshotWhenShown(this, 1500);
        }

        /// The UI harness: a made-up window (an ellipse a little wider outward than toward the nose, both passes) and the result page for it.
        public void ApplyDemoResult()
        {
            Directory.CreateDirectory(RunDir);
            var rnd = new Random(7);
            var sb = new System.Text.StringBuilder("eye,fit,gaze,theta_deg,ecc_deg,tan_r,x_out,y,timeout,latency_ms,deg_s,display_time_ns\n");
            foreach (var gz in new[] { "following", "fixed" })
                for (int eye = 0; eye < 2; eye++)
                    for (int i = 0; i < Directions; i++)
                    {
                        double th = i * 15, a = th * Math.PI / 180;
                        double reach = Math.Sqrt(1 / (Math.Pow(Math.Cos(a) / (Math.Cos(a) >= 0 ? 0.95 : 0.72), 2) + Math.Pow(Math.Sin(a) / (Math.Sin(a) >= 0 ? 0.74 : 0.66), 2)));
                        double tan = reach * (gz == "following" ? 0.93 : 1.0) * (1 + 0.03 * (rnd.NextDouble() - 0.5));
                        double ecc = Math.Atan(tan) * 180 / Math.PI;
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},demoheadset_1,{1},{2:0.0},{3:0.000},{4:0.00000},{5:0.00000},{6:0.00000},0,50,2.00,0", eye, gz, th, ecc, tan, tan * Math.Cos(a), tan * Math.Sin(a)));
                    }
            File.WriteAllText(Path.Combine(RunDir, $"fov_sweep_{Wearer}_{headsetSlug}_run1.csv"), sb.ToString());
            ShowResult(99);
        }

        // ---------------------------------------------------------------------------------------------------------- pages

        void Reset(string title) { page.Controls.Clear(); page.Controls.Add(Ui.Line(title, Ui.Big)); }
        Label Text_(string text, bool dim = false) { var l = Ui.Line(text); l.MaximumSize = new Size(Math.Max(300, page.ClientSize.Width - 40), 0); if (dim) l.ForeColor = Color.DimGray; page.Controls.Add(l); return l; }
        Button Btn(string text, Action act) { var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 10, 8, 3), AccessibleName = text }; b.Click += (s, e) => act(); return b; }
        FlowLayoutPanel Buttons(params Button[] bs) { var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; f.Controls.AddRange(bs); page.Controls.Add(f); return f; }

        /// The page this window opens on (docs/PICTURE_EDGES_DESIGN.md section 6): "Fit the picture to your eyes", two minutes in the headset, no sweep. The probe's fit scene and the reading of its
        /// result are step B; until then Retry says so (the old 30-minute ray sweep is gone from the product, git history has it).
        void ShowIntro()
        {
            if (restrictTool) { ShowRestrictIntro(); return; }
            Reset("Fit the picture to your eyes");
            Text_("Two minutes, the headset on, one eye at a time. The tool shows a coloured box with black bars at the edges. For each edge: push it until the black bar is gone, then one step back so you see the bar return. When all four edges are done, press Enter: the other eye comes, and Enter on the second eye saves. The scene is Littlest Tokyo, as in the restriction tool. The result is kept as one of your profiles.");
            Text_("Press SPACE on the PC keyboard (or pinch with hands as controllers) to start; the text in the headset says which eye to close and which edge you are on.", true);
            var problems = new List<string>();
            if (headset.Length == 0) problems.Add("No headset is known yet: connect it once with the streamer.");
            if (demoDir == null && Process.GetProcessesByName("streamer").Length == 0 && File.Exists(Paths.Runtime)) { StartStreamer(); return; }
            if (demoDir == null && !File.Exists(Probe)) problems.Add("bench\\xr_probe.exe is missing from this package.");
            if (settings.Kind == HeadsetKind.Quest) problems.Add("A Quest has no picture edges to fit: its app declares the lens window itself. The restriction (Advanced video and performance settings) is where a Quest's view is chosen.");
            foreach (var p in problems) { var l = Text_(p); l.ForeColor = Color.Firebrick; }
            Text_($"Headset: {(headset.Length > 0 ? headset : "none seen yet")}.", true);
            Buttons(Btn("Retry", Retry), Btn("Close", Close));
            note = Text_("", true);
        }
        Label note;

        void Retry() => StartTool();

        void ShowRestrictIntro()
        {
            Reset("Choose what the game renders");
            Text_("Two minutes, the headset on, both eyes open. The tool shows a scene inside a box and black outside it: exactly what a game that honours the restriction shows. Pull an edge in until you notice the black during normal looking around, then one step back. Start with the inner edges and the lower one. The result is kept as one of your profiles and used by the games.");
            Text_("Press SPACE on the PC keyboard to start. Arrows pull an edge in, Shift + arrow pushes it back out, Tab switches between both eyes and one eye, hold SPACE to see the whole view, Enter saves, Esc leaves. Keep this window in front: it receives the keys.", true);
            var problems = new List<string>();
            if (headset.Length == 0) problems.Add("No headset is known yet: connect it once with the streamer.");
            if (demoDir == null && Process.GetProcessesByName("streamer").Length == 0 && File.Exists(Paths.Runtime)) { StartStreamer(); return; }
            if (demoDir == null && !File.Exists(Probe)) problems.Add("bench\\xr_probe.exe is missing from this package.");
            foreach (var p in problems) { var l = Text_(p); l.ForeColor = Color.Firebrick; }
            Text_($"Headset: {(headset.Length > 0 ? headset : "none seen yet")}.", true);
            Buttons(Btn("Retry", Retry), Btn("Close", Close));
            note = Text_("", true);
        }

        // ---------------------------------------------------------------------------------------------------- the tool (the probe's fit / restrict scene)

        Process probe;
        bool toolApplied, hostPrepared; bool? priorOverride; object priorView; bool priorViewKnown;
        double[][] liveBox, livePanel; double livePct; Label liveLabel, liveState;
        static string ProbeLog => Path.Combine(Paths.Config, "calibration", "probe_tool.log");

        /// While a tool runs the game must be told the whole panel (the fit measures P) and no headset view is sent: the settings the streamer forwards are set aside and put back at the end.
        void PrepareHost()
        {
            var st = new AppSettings();
            priorOverride = st.FovOverride; priorView = Json.Get(st.Root, "video.headset_view"); priorViewKnown = true;
            Json.Set(st.Root, "video.fov_override", false);
            Json.Remove(st.Root, "video.headset_view");
            st.Save(); hostPrepared = true;
            Log.Write("INFO", "calibrate: the restriction and the headset view are set aside while the tool runs");
        }

        void RestoreHost()
        {
            if (!hostPrepared) return;
            hostPrepared = false;
            try
            {
                var st = new AppSettings();
                if (!toolApplied && priorOverride == true) Json.Set(st.Root, "video.fov_override", true);
                if (priorViewKnown && priorView != null) Json.Set(st.Root, "video.headset_view", priorView);
                st.Save();
            }
            catch (Exception e) { Log.Write("WARN", "calibrate: could not put the settings back: " + e.Message); }
        }

        void EndTool()
        {
            try { if (probe != null && !probe.HasExited) probe.Kill(); } catch { }
            probe = null;
            guard.Stop();
            RestoreHost();
        }

        // While the tool runs this window must be the one that has the keyboard, and nothing may move the Windows mouse: the desktop mirror (a controller / hand beam is its mouse) is stopped
        // and kept stopped, and the keys the scene uses are swallowed here (a focused button would otherwise take Space / Enter as a click: Stop). The scene reads the keys itself.
        readonly Timer guard = new Timer { Interval = 1000 };
        bool toolKeys => probe != null && !probe.HasExited;
        static void StopMirror() { foreach (var p in Process.GetProcessesByName("mirrordesktop")) { try { p.Kill(); } catch { } p.Dispose(); } }
        void TakeFocus() { try { WindowState = FormWindowState.Normal; TopMost = true; TopMost = false; Activate(); } catch { } }
        void StartGuard()
        {
            guardTicks = 0; StopMirror(); TakeFocus();
            guard.Tick -= GuardTick; guard.Tick += GuardTick; guard.Start();
        }
        int guardTicks;
        void GuardTick(object s, EventArgs e) { StopMirror(); if (guardTicks++ < 6 && toolKeys) TakeFocus(); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (toolKeys)
                switch (keyData & Keys.KeyCode)
                {
                    case Keys.Space: case Keys.Enter: case Keys.Tab: case Keys.Escape: case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: return true;
                }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void StartTool()
        {
            if (demoDir != null) { if (note != null) note.Text = "Demo window: nothing is started."; return; }
            if (probe != null && !probe.HasExited) return;
            if (!File.Exists(Probe)) { if (note != null) { note.Text = "bench\\xr_probe.exe is missing from this package."; note.ForeColor = Color.Firebrick; } return; }
            if (!restrictTool && settings.Kind == HeadsetKind.Quest) { if (note != null) { note.Text = "A Quest has no picture edges to fit."; note.ForeColor = Color.Firebrick; } return; }
            if (Process.GetProcessesByName("streamer").Length == 0) { StartStreamer(); return; }
            PrepareHost();
            StartGuard();
            Reset(restrictTool ? "Choose what the game renders" : "Fit the picture to your eyes");
            Text_("Put the headset on. The instructions are in the headset; press SPACE on this PC's keyboard to start.");
            liveState = Text_("Starting the OpenXR scene...", true);
            liveLabel = Text_("", true);
            Text_(restrictTool
                ? "Arrows pull an edge in 1 degree, Shift + arrow pushes it back out, Tab: both eyes / one eye, hold SPACE: the whole view, Enter: save, Esc: leave. Keep this window in front: it receives the keys."
                : "Arrows push an edge out until its black bar is gone, Shift + arrow pulls it back, hold SPACE: compare, Enter: this eye is finished (the second eye saves), Esc: leave. One eye at a time: close the other. Keep this window in front: it receives the keys.", true);
            Buttons(Btn("Stop", () => { EndTool(); ShowIntro(); }));
            // the streamer takes the settings change within a moment; the scene is started after it so the game is told the whole panel
            var t = new Timer { Interval = 3000 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (hostPrepared) LaunchProbe(); };
            t.Start();
        }

        void LaunchProbe()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProbeLog));
                var psi = new ProcessStartInfo(Probe, "\"" + ProbeLog + "\" 100000000 86400") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Probe) };
                psi.EnvironmentVariables["XR_PROBE_TOOL"] = restrictTool ? "restrict" : "fit";
                var vab = Path.Combine(Paths.Dir, "bench", "littlest_tokyo.vab");
                if (File.Exists(vab)) psi.EnvironmentVariables["XR_PROBE_SCENE"] = vab;
                probe = Process.Start(psi);
                probe.OutputDataReceived += (s, e) => { if (e.Data != null) try { BeginInvoke((Action)(() => HandleLine(e.Data))); } catch { } };
                probe.EnableRaisingEvents = true;
                probe.Exited += (s, e) => { try { BeginInvoke((Action)ProbeEnded); } catch { } };
                probe.BeginOutputReadLine();
                Log.Write("INFO", "calibrate: started the " + (restrictTool ? "restrict" : "fit") + " scene");
            }
            catch (Exception e) { EndTool(); Reset("The scene did not start"); Text_(e.Message); Buttons(Btn("Retry", Retry), Btn("Close", Close)); }
        }

        bool finished;
        void ProbeEnded()
        {
            if (finished || liveLabel == null) return;
            probe = null; RestoreHost();
            ShowIntro();
            if (note != null) { note.Text = "The scene ended without saving (is the headset connected and the game runtime AirFrameXR? See the log)."; note.ForeColor = Color.DarkGoldenrod; }
        }

        static double[][] Boxes(object o)
        {
            var l = o as System.Collections.IList; if (l == null || l.Count < 2) return null;
            return new[] { 0, 1 }.Select(e => ((System.Collections.IList)l[e]).Cast<object>().Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        }

        void HandleLine(string line)
        {
            if (line.Length == 0 || line[0] != '{') return;
            Dictionary<string, object> d;
            try { d = Json.Parse(line); } catch { return; }
            var ev = Json.Str(d, "event", "");
            var box = Boxes(Json.Get(d, "box")); var panel = Boxes(Json.Get(d, "panel"));
            if (box != null) { liveBox = box; livePanel = panel; livePct = Json.Num(d, "pct", 100); }
            string Deg(double t) => (Math.Atan(t) * 180 / Math.PI).ToString("0.0", CultureInfo.InvariantCulture);
            if (liveLabel != null && liveBox != null)
                liveLabel.Text = string.Join("\n", new[] { 0, 1 }.Select(e => (e == 0 ? "Left eye:  " : "Right eye: ") + $"outer {Deg(liveBox[e][0])}  inner {Deg(liveBox[e][1])}  up {Deg(liveBox[e][2])}  down {Deg(liveBox[e][3])} degrees")) + (restrictTool ? $"\nThe game renders {livePct:0} % of its pixels." : "");
            if (ev == "start" && liveState != null) liveState.Text = "Running.";
            else if (ev == "esc") { finished = true; EndTool(); ShowIntro(); if (note != null) note.Text = "You left without saving."; }
            else if (ev == "enter" && liveBox != null) { finished = true; Finish(); }
        }

        string AskSeal()
        {
            string seal = "";
            using (var f = new DpiForm { Text = "AirFrameXR – Light seal", Font = Ui.Body, StartPosition = FormStartPosition.CenterParent, BackColor = Color.White, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink })
            {
                var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(12) };
                col.Controls.Add(Ui.Line("Which light seal do you wear? It explains why two people fit differently."));
                var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 260, AccessibleName = "Light seal" };
                box.Items.AddRange(new object[] { "I don't know", "21W", "25W", "29W", "31W", "Other" }); box.SelectedIndex = 0;
                col.Controls.Add(box);
                var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK, Margin = new Padding(3, 10, 3, 3) };
                col.Controls.Add(ok); f.Controls.Add(col); f.AcceptButton = ok;
                f.ShowDialog(this);
                seal = box.Text == "I don't know" ? "" : box.Text.Trim();
            }
            return seal;
        }

        void Finish()
        {
            try
            {
                var seal = restrictTool ? "" : AskSeal();
                var u = UserFovStore.Add(UserFovStore.FromTool(restrictTool ? "restriction" : "quickfit", liveBox, livePanel, headset, seal));
                var st = new AppSettings();
                UserFovStore.Apply(u, st, AppSettings.ReadClientFov(), headset);   // as what the game renders (G); as the headset view V too once the headset-view message exists (step C)
                st.Save(); toolApplied = true;
                Log.Write("INFO", $"calibrate: '{u.Name}' ({u.Method}) kept and applied");
                Reset(restrictTool ? "Saved" : "Your fit is saved");
                Text_($"Kept as \"{u.Name}\" in your profiles and chosen: outer {u.Tan[0]:0.00}  inner {u.Tan[1]:0.00}  upper {u.Tan[2]:0.00}  lower {u.Tan[3]:0.00} (tangents, the larger of the two eyes).");
                Text_("Needs a streamer restart for games that are already running; the next game gets it.", true);
                var map = new FovMap { Rectangle = u.Tan, Declared = AppSettings.ReadClientFov(), Margin = new Padding(3, 6, 3, 2) }; page.Controls.Add(map);
                Buttons(Btn("Close", Close));
            }
            catch (Exception e) { Reset("Could not save"); Text_(e.Message); Buttons(Btn("Retry", Retry), Btn("Close", Close)); Log.Write("WARN", "calibrate: could not save the tool's result: " + e.Message); }
        }

        /// The fit needs the streamer (it is the OpenXR runtime's other half): start it, minimized so this window stays in front, and continue when it is up.
        void StartStreamer()
        {
            Reset("Starting the streamer...");
            Text_("Open the ALVR app on your headset. This page continues by itself once the streamer is up.");
            try { Process.Start(new ProcessStartInfo(Paths.Runtime) { WorkingDirectory = Paths.Bin, WindowStyle = ProcessWindowStyle.Minimized }); Log.Write("INFO", "calibrate: started the streamer"); }
            catch (Exception e) { Reset("The streamer did not start"); Text_(e.Message); Buttons(Btn("Try again", ShowIntro), Btn("Close", Close)); return; }
            int waited = 0;
            var t = new Timer { Interval = 1000 };
            t.Tick += (s, e) =>
            {
                if (Process.GetProcessesByName("streamer").Length > 0 && ++waited >= 4) { t.Stop(); t.Dispose(); ShowIntro(); }   // up for a few seconds: its host has started
                else if (waited == 0 && ++startWait > 30) { t.Stop(); t.Dispose(); Reset("The streamer did not start"); Text_("It did not appear within 30 seconds. Start it from the main menu, then come back."); Buttons(Btn("Check again", ShowIntro), Btn("Close", Close)); }
            };
            startWait = 0; t.Start();
        }
        int startWait;

        List<string> EarlierRuns() => Directory.Exists(RunDir) ? Directory.GetFiles(RunDir, $"fov_sweep_{Wearer}_{headsetSlug}_run*.csv").OrderBy(f => f).ToList() : new List<string>();

        void ShowResult(double coverage)
        {
            var files = EarlierRuns();
            if (files.Count == 0) { ShowIntro(); return; }
            result = FovFit.Fit(files, coverage, headset, Wearer, out var problem);
            Reset(result == null ? "The measurement is not usable" : "Your field of view");
            if (result == null)
            {
                Text_(problem);
                Buttons(Btn("Back", ShowIntro));
                return;
            }
            var t = result.Rectangle;
            { var map = FovMap.FromResult(result); map.Margin = new Padding(3, 6, 3, 2); page.Controls.Add(map); }
            Text_("Blue is the field of view the games are told; the dark outline is what your headset shows.", true);
            Text_($"Outer {t.Outer:0.00}   Inner {t.Inner:0.00}   Upper {t.Upper:0.00}   Lower {t.Lower:0.00}   (tangents; the angles are {Math.Atan(t.Outer) * 180 / Math.PI:0.#} / {Math.Atan(t.Inner) * 180 / Math.PI:0.#} / {Math.Atan(t.Upper) * 180 / Math.PI:0.#} / {Math.Atan(t.Lower) * 180 / Math.PI:0.#} degrees)");
            Text_($"This rectangle covers {100 * result.Coverage:0.0} % of what you saw, from {result.Runs} run(s). It is {100 * result.SavedVsBox:0.0} % smaller than the box around your whole window, so the game renders that much less.");
            foreach (var w in result.Warnings) { var l = Text_("Note: " + w); l.ForeColor = Color.DarkGoldenrod; }
            var cov = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470, Margin = new Padding(3, 8, 3, 3), AccessibleName = "Coverage" };
            cov.Items.AddRange(new object[] { "99 % of my window (recommended: black only in the far corners)", "100 % of my window (no black, a little more to render)", "97 % of my window (fewer pixels, more black in corners)" });
            cov.SelectedIndex = coverage >= 99.5 ? 1 : coverage >= 98.5 ? 0 : 2;
            cov.SelectedIndexChanged += (s, e) => ShowResult(new[] { 99.0, 100.0, 97.0 }[cov.SelectedIndex]);
            page.Controls.Add(cov);
            Text_("Needs streamer restart. If a game shows black at an edge, choose 100 % or run the test again; the FOV restriction card in the launcher shows and changes the profile.", true);
            Buttons(Btn("Use this field of view", Apply), Btn("Save a copy...", SaveCopy), Btn("Retry", ShowIntro), Btn("Close", Close));
        }

        void Apply()
        {
            try
            {
                var tmp = Path.Combine(RunDir, result.FileName);
                File.WriteAllText(tmp, result.Json);
                var p = HmdProfiles.Parse(result.Json, result.FileName).First();
                HmdProfiles.Install(tmp, p);
                bool ok = HmdProfiles.ApplyFovDefault(p, headset);
                Log.Write("INFO", $"calibrate: '{p.Id}' installed, applied {ok}");
                KeepInStore();
                MessageBox.Show(this, ok ? "Saved. It needs a streamer restart: the games get your measured field of view after it." : "The profile is installed but the settings could not be saved: see the log.", "Calibrate", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception e) { MessageBox.Show(this, "Could not apply the calibration: " + e.Message, "Calibrate", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        /// Every calibration the user keeps goes into config\user_fov_profiles.json (once per result), so a bad one can be deleted later and none is lost to an upgrade.
        FovFitResult storedResult;
        void KeepInStore()
        {
            if (demoDir != null || storedResult == result) return;
            try { var u = UserFovStore.Add(UserFovStore.FromCalibration(result, headset)); storedResult = result; Log.Write("INFO", $"calibrate: kept as '{u.Name}' in user_fov_profiles.json"); }
            catch (Exception e) { Log.Write("WARN", "calibrate: could not keep the profile: " + e.Message); }
        }

        void SaveCopy()
        {
            KeepInStore();
            using (var dlg = new SaveFileDialog { Title = "Save the calibration profile", FileName = result.FileName, Filter = "AirFrameXR profile (*.json)|*.json" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dlg.FileName, result.Json); } catch (Exception e) { MessageBox.Show(this, "Cannot save: " + e.Message, "Calibrate", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        [STAThread]
        static void Main()
        {
            Log.Component = "calibrate";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new CalibrateForm(null, Environment.GetCommandLineArgs().Contains("--restrict")));
        }
    }
}
