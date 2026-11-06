// calibrate.exe: measures what the headset really shows and saves it as the field of view the games are told (docs/FOV_CALIBRATION.md).
// Opens on the fit page (docs/PICTURE_EDGES_DESIGN.md section 6, step B builds the probe's fit scene and the reading of its result); the 1.3 ray sweep (30 minutes) is gone from the UI.
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

        public CalibrateForm(string demoFolder = null)
        {
            demoDir = demoFolder;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;   // numbers as in the docs and the other tools
            Text = "AirFrameXR – Calibrate the field of view";
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
            Reset("Fit the picture to your eyes");
            Text_("Two minutes, the headset on, one eye at a time. The tool shows a coloured box with black bars at the edges. For each edge: push it until the black bar is gone, then one step back so you see the bar return. When all four edges are done, the other eye. The result is kept as one of your profiles.");
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

        void Retry()
        {
            if (note != null) { note.Text = "The fit scene is step B of docs/PICTURE_EDGES_DESIGN.md and is not built in this build: nothing was started."; note.ForeColor = Color.DarkGoldenrod; }
            Log.Write("INFO", "calibrate: Retry pressed, the fit scene is not built yet");
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
            Application.Run(new CalibrateForm());
        }
    }
}
