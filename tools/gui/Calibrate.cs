// calibrate.exe: measures what the headset really shows and saves it as the field of view the games are told (docs/FOV_CALIBRATION.md).
// It runs the sweep (bench\xr_probe.exe in its sweep mode: a dot drifts outward in 24 directions per eye, the wearer clicks when it is gone), twice (eyes
// following, eyes fixed), then FovFit turns every run into a profile file. The result is applied here, or kept as a file for Import a calibration file...
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
    /// The measurement as a picture, in tangent space with the nose on the left: a grey ray from the centre for every direction, a dot where the square vanished on it, the outline of
    /// what the headset shows, and the rectangle the games are told.
    public class FovPlot : Control
    {
        readonly FovFitResult r;
        public FovPlot(FovFitResult result) { r = result; DoubleBuffered = true; BackColor = Color.White; Size = new Size(520, 340); AccessibleName = "Your visible window with the measured rays"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float x0 = Math.Min(-(float)r.Rectangle.Inner, (float)r.Window.Min(p => p.X)), x1 = Math.Max((float)r.Rectangle.Outer, (float)r.Window.Max(p => p.X));
            float y0 = Math.Min(-(float)r.Rectangle.Lower, (float)r.Window.Min(p => p.Y)), y1 = Math.Max((float)r.Rectangle.Upper, (float)r.Window.Max(p => p.Y));
            float pad = 0.06f * Math.Max(x1 - x0, y1 - y0); x0 -= pad; x1 += pad; y0 -= pad; y1 += pad;
            float k = Math.Min((Width - 2) / (x1 - x0), (Height - 2) / (y1 - y0));
            float ox = (Width - k * (x1 - x0)) / 2, oy = (Height - k * (y1 - y0)) / 2;
            Func<float, float, PointF> map = (x, y) => new PointF(ox + (x - x0) * k, oy + (y1 - y) * k);
            var c = map(0, 0);
            using (var axis = new Pen(Color.Gainsboro)) { g.DrawLine(axis, map(x0, 0), map(x1, 0)); g.DrawLine(axis, map(0, y0), map(0, y1)); }
            using (var ray = new Pen(Color.Silver, 1f))
            using (var dot = new SolidBrush(Color.DimGray))
                foreach (var w in r.Window) { var q = map((float)w.X, (float)w.Y); g.DrawLine(ray, c, q); g.FillEllipse(dot, q.X - 3, q.Y - 3, 6, 6); }
            using (var outline = new Pen(Color.DimGray, 1.5f)) g.DrawPolygon(outline, r.Window.Select(w => map((float)w.X, (float)w.Y)).ToArray());
            var a = map(-(float)r.Rectangle.Inner, (float)r.Rectangle.Upper); var b = map((float)r.Rectangle.Outer, -(float)r.Rectangle.Lower);
            using (var rect = new Pen(Ui.Accent, 2.5f)) g.DrawRectangle(rect, a.X, a.Y, b.X - a.X, b.Y - a.Y);
            using (var f = new Font(Font.FontFamily, 8.5f))
            {
                g.DrawString("nose", f, Brushes.DimGray, 4, c.Y + 2); g.DrawString("outer", f, Brushes.DimGray, Width - 38, c.Y + 2);
                g.DrawString("up", f, Brushes.DimGray, c.X + 4, 2); g.DrawString("down", f, Brushes.DimGray, c.X + 4, Height - 16);
            }
        }
    }

    public class CalibrateForm : DpiForm
    {
        const int Directions = 24, Eyes = 2, StepsPerPass = Directions * Eyes;
        const string Wearer = "my";
        static string Probe => Path.Combine(Paths.Dir, "bench", "xr_probe.exe");
        string RunDir => demoDir ?? Path.Combine(Paths.Config, "calibration");
        readonly string demoDir;   // the UI harness: synthetic sweeps in a temp folder instead of config\\calibration

        readonly AppSettings settings = new AppSettings();
        readonly string headset, headsetSlug;
        readonly FlowLayoutPanel page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16, 12, 16, 12), BackColor = Color.White };
        readonly Timer poll = new Timer { Interval = 500 };
        Process probe;
        string csv, fit, gaze;
        int run, doneSince;
        Label progressText; ProgressBar bar;
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
            poll.Tick += (s, e) => Poll();
            FormClosing += (s, e) => StopProbe();
            if (demoFolder == null) ShowIntro();
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

        void Reset(string title) { page.Controls.Clear(); progressText = null; bar = null; page.Controls.Add(Ui.Line(title, Ui.Big)); }
        Label Text_(string text, bool dim = false) { var l = Ui.Line(text); l.MaximumSize = new Size(Math.Max(300, page.ClientSize.Width - 40), 0); if (dim) l.ForeColor = Color.DimGray; page.Controls.Add(l); return l; }
        Button Btn(string text, Action act) { var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 10, 8, 3) }; b.Click += (s, e) => act(); return b; }
        FlowLayoutPanel Buttons(params Button[] bs) { var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; f.Controls.AddRange(bs); page.Controls.Add(f); return f; }

        List<string> EarlierRuns() => Directory.Exists(RunDir) ? Directory.GetFiles(RunDir, $"fov_sweep_{Wearer}_{headsetSlug}_run*.csv").OrderBy(f => f).ToList() : new List<string>();

        void ShowIntro()
        {
            Reset("Measure what your headset shows");
            Text_("The lens cuts the edges of the picture, and how much depends on your headset and on how it sits on your face. This test finds your own edges, so the games render only what you can see. It takes about 30 minutes (two passes of 48 short steps).");
            Text_("In the headset: keep your head still, only your eyes move. A small square drifts outward from the centre. Follow it with your eyes and click once (controller trigger, or a pinch with hands as controllers) the moment it is gone and you see only black. Do not click when it appears or fades. The second pass is the same with your eyes fixed on the + in the middle.");
            var problems = new List<string>();
            if (headset.Length == 0) problems.Add("No headset is known yet: connect it once with the streamer.");
            if (Process.GetProcessesByName("streamer").Length == 0) problems.Add("Start the streamer and connect your headset first (Main menu > Streamer), then come back here.");
            if (!File.Exists(Probe)) problems.Add("bench\\xr_probe.exe is missing from this package.");
            var earlier = EarlierRuns();
            if (problems.Count > 0)
            {
                foreach (var p in problems) { var l = Text_(p); l.ForeColor = Color.Firebrick; }
                Buttons(Btn("Check again", ShowIntro), Btn("Close", Close));
                return;
            }
            Text_($"Headset: {headset}." + (earlier.Count > 0 ? $" {earlier.Count} earlier run(s) are kept and used together with this one: the larger window of all runs wins." : " Two or three runs, with the headset taken off and put back on between them, give a safer result; the first one is enough to start."), true);
            Text_("Calibration needs the field of view override off, and the headset app's FoV scale at 1.0. Put the headset on only when the test says so.", true);
            var b = new List<Button> { Btn("Start the test", StartRun) };
            if (earlier.Count > 0)
            {
                b.Add(Btn("Make the profile from the earlier runs", () => ShowResult(99)));
                b.Add(Btn("Forget earlier runs", () => { foreach (var f in earlier) try { File.Delete(f); } catch { } ShowIntro(); }));
            }
            Buttons(b.ToArray());
        }

        void StartRun()
        {
            if (settings.FovOverride)
            {
                if (MessageBox.Show(this, "The field of view override is on, and it would cut the measurement off. Turn it off for the test? (Your result is applied afterwards if you choose so.)", "Calibrate", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Json.Set(settings.Root, "video.fov_override", false); settings.Save();
                Log.Write("INFO", "calibrate: field of view override turned off for the test");
            }
            Directory.CreateDirectory(RunDir);
            run = EarlierRuns().Count + 1;
            csv = Path.Combine(RunDir, $"fov_sweep_{Wearer}_{headsetSlug}_run{run}.csv");
            fit = $"{headsetSlug}_{run}";
            StartPass("following");
        }

        void StartPass(string g)
        {
            gaze = g; doneSince = 0;
            Reset(g == "following" ? "Pass 1 of 2: follow the square" : "Pass 2 of 2: eyes on the +");
            Text_(g == "following"
                ? "Put the headset on and keep your head still. Click once to start; then, in each step, follow the square with your eyes and click when it is gone and you see black."
                : "Same steps, but keep your eyes fixed on the + in the middle. Do not follow the square: click when, looking at the +, you can no longer see it at the edge of your view.");
            Text_("One click per step. If you clicked too early, carry on. If you cannot see the square at all in a step, do not click: the step ends by itself after about 30 seconds.", true);
            progressText = Text_("Starting...");
            bar = new ProgressBar { Minimum = 0, Maximum = StepsPerPass, Width = 420, Margin = new Padding(3, 8, 3, 8) };
            page.Controls.Add(bar);
            Buttons(Btn("Stop", () => { StopProbe(); ShowIntro(); }));
            var psi = new ProcessStartInfo(Probe, $"\"{Path.Combine(Paths.Logs, "fov_sweep.log")}\" 100000000 86400") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Probe) };
            Directory.CreateDirectory(Paths.Logs);
            var env = psi.EnvironmentVariables;
            env["XR_RUNTIME_JSON"] = Paths.Manifest;
            env["XR_PROBE_SCENE"] = "fovgrid"; env["XR_PROBE_FOVGRID"] = "sweep";
            env["XR_PROBE_SWEEP_FIT"] = fit; env["XR_PROBE_SWEEP_GAZE"] = g;
            env["XR_PROBE_SWEEP_DEG_S"] = "2"; env["XR_PROBE_SWEEP_START_DEG"] = "15"; env["XR_PROBE_SWEEP_EYES"] = "01";
            env["XR_PROBE_SWEEP_CSV"] = csv;
            try { probe = Process.Start(psi); Log.Write("INFO", $"calibrate: pass {g}, run {run}, {csv}"); }
            catch (Exception e) { MessageBox.Show(this, "Could not start the test: " + e.Message, "Calibrate", MessageBoxButtons.OK, MessageBoxIcon.Warning); ShowIntro(); return; }
            poll.Start();
        }

        /// Steps this pass has written: the probe appends one row per direction, left eye first.
        int StepsDone()
        {
            try
            {
                using (var fs = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    sr.ReadLine();
                    int n = 0; string line;
                    while ((line = sr.ReadLine()) != null) { var c = line.Split(','); if (c.Length > 2 && c[1] == fit && c[2] == gaze) n++; }
                    return n;
                }
            }
            catch { return 0; }
        }

        void Poll()
        {
            if (progressText == null) return;
            int n = Math.Min(StepsDone(), StepsPerPass);
            bar.Value = n;
            progressText.Text = n == 0 ? "Waiting for your first click (it only starts the test)..." : $"{(n <= Directions ? "Left" : "Right")} eye, step {Math.Min(n + (n < StepsPerPass ? 1 : 0), StepsPerPass) - (n > Directions ? Directions : 0)} of {Directions}";
            if (n >= StepsPerPass && ++doneSince >= 3)   // the last row is in: give the probe a moment, then end it
            {
                poll.Stop(); StopProbe();
                if (gaze == "following") ShowBetween(); else ShowResult(99);
            }
            else if (probe != null && probe.HasExited && n < StepsPerPass)
            {
                poll.Stop(); probe = null;
                MessageBox.Show(this, "The test program stopped before the end. Check that the streamer is running and the headset connected (logs\\fov_sweep.log has the details), then run the test again.", "Calibrate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                try { if (n < StepsPerPass) File.Delete(csv); } catch { }   // an incomplete run is not kept: it would skew the window
                ShowIntro();
            }
        }

        void StopProbe()
        {
            poll.Stop();
            try { if (probe != null && !probe.HasExited) { probe.Kill(); probe.WaitForExit(3000); } } catch { }
            probe = null;
        }

        void ShowBetween()
        {
            Reset("Pass 1 done");
            Text_("Take a short break if you like; the headset can stay on. The second pass has the same 48 steps, but this time keep your eyes fixed on the + in the middle. Both passes matter: with the eyes following the square the window is smaller than with the eyes forward, and a game is played both ways.");
            Buttons(Btn("Start pass 2", () => StartPass("fixed")), Btn("Stop here", () => { try { File.Delete(csv); } catch { } ShowIntro(); }));
        }

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
            page.Controls.Add(new FovPlot(result) { Margin = new Padding(3, 6, 3, 2) });
            Text_("Grey: the 24 directions the square travelled, with a dot where it vanished for you; the dark outline is what your headset shows; blue is the field of view the games are told.", true);
            Text_($"Outer {t.Outer:0.00}   Inner {t.Inner:0.00}   Upper {t.Upper:0.00}   Lower {t.Lower:0.00}   (tangents; the angles are {Math.Atan(t.Outer) * 180 / Math.PI:0.#} / {Math.Atan(t.Inner) * 180 / Math.PI:0.#} / {Math.Atan(t.Upper) * 180 / Math.PI:0.#} / {Math.Atan(t.Lower) * 180 / Math.PI:0.#} degrees)");
            Text_($"This rectangle covers {100 * result.Coverage:0.0} % of what you saw, from {result.Runs} run(s). It is {100 * result.SavedVsBox:0.0} % smaller than the box around your whole window, so the game renders that much less.");
            foreach (var w in result.Warnings) { var l = Text_("Note: " + w); l.ForeColor = Color.DarkGoldenrod; }
            var cov = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470, Margin = new Padding(3, 8, 3, 3), AccessibleName = "Coverage" };
            cov.Items.AddRange(new object[] { "99 % of my window (recommended: black only in the far corners)", "100 % of my window (no black, a little more to render)", "97 % of my window (fewer pixels, more black in corners)" });
            cov.SelectedIndex = coverage >= 99.5 ? 1 : coverage >= 98.5 ? 0 : 2;
            cov.SelectedIndexChanged += (s, e) => ShowResult(new[] { 99.0, 100.0, 97.0 }[cov.SelectedIndex]);
            page.Controls.Add(cov);
            Text_("Applies at the next game start. If a game shows black at an edge, choose 100 % or run the test again; Advanced picture and FOV settings shows and changes the values.", true);
            Buttons(Btn("Use this field of view", Apply), Btn("Save a copy...", SaveCopy), Btn("Measure again", ShowIntro), Btn("Close", Close));
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
                MessageBox.Show(this, ok ? "Saved. The games get your measured field of view from their next start." : "The profile is installed but the settings could not be saved: see the log.", "Calibrate", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception e) { MessageBox.Show(this, "Could not apply the calibration: " + e.Message, "Calibrate", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void SaveCopy()
        {
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
