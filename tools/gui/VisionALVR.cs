// VisionALVR.exe: the runtime window. Checks the GPU and the OpenXR registration, starts the streamer host as soon as it opens
// (no start button), shows the headset / game state and the key numbers every 10 s, and offers the live settings (display gamma,
// debug logging). Pairing and the one-time settings live in configure.exe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VisionALVR
{
    public class RuntimeForm : DpiForm
    {
        readonly AppSettings settings = new AppSettings();
        readonly HostProcess host = new HostProcess();
        readonly Label headset = Ui.Line("Headset: -", Ui.Big);
        readonly Button find = new Button { Text = "Find headset...", AutoSize = true, Margin = new Padding(12, 0, 0, 0) };
        readonly Label findHint = Ui.Line("VisionALVR dials the saved address and re-finds a paired headset by name. If it does not connect (new address, another headset), use Find headset.");
        // Windows Firewall: the rule belongs to this exact folder; a missing or blocking rule means no discovery and no connection
        readonly Label fwWarn = Ui.Line("", Ui.Bold);
        readonly Button fwFix = new Button { Text = "Allow VisionALVR through the firewall (asks for administrator)", AutoSize = true };
        readonly Dictionary<string, object> check;
        readonly bool autoFind;
        bool finding, gameRunning;
        readonly Label game = Ui.Line("Game: none", Ui.Bold);
        readonly Label runtime = Ui.Line("");
        // the Streaming box: tiles (live numbers during a game, the last session's after it) and one line of text under them
        readonly Tile tGame = Ui.MakeTile("Game"), tStreamed = Ui.MakeTile("Streamed"), tShown = Ui.MakeTile("Headset shows"), tBitrate = Ui.MakeTile("Bitrate"), tLatency = Ui.MakeTile("Latency"), tLoss = Ui.MakeTile("Loss");
        readonly Label stats = Ui.Line("Waiting for the first statistics (every 10 s)...");
        // the Streaming box shows live numbers while a game runs and, once it has ended, the run's record and what will be proposed next time
        readonly GroupBox statsBox = new GroupBox { Text = "Streaming (updated every 10 s)", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Font = Ui.Bold };
        bool lastSession;
        // display controls: (label, min, max, default, settings getter/setter), values x100 on the track bars
        class Knob { public string Name; public TrackBar Bar; public Label Value; public double Def; public Func<double> Get; public Action<double> Set; }
        readonly List<Knob> knobs = new List<Knob>();
        readonly CheckBox debug = new CheckBox { Text = "Debug logging (files in logs\\debug\\)", AutoSize = true, Font = Ui.Body };
        readonly ToggleSwitch frameGen = new ToggleSwitch { Margin = new Padding(3, 6, 10, 3), AccessibleName = "Frame generation" };
        readonly ToggleSwitch mirror = new ToggleSwitch { Margin = new Padding(3, 6, 10, 3), AccessibleName = "Mirror the Windows desktop between sessions" };
        // the game's rate as a fraction of the display rate: 1/3, 1/2 (default), 2/3 (settings video.frame_gen_rate third | half | two_thirds)
        static readonly string[] FgRates = { "third", "half", "two_thirds" };
        static readonly string[] FgRateText = {
            "1/3: the game at a third of the display rate, two generated frames per rendered one (games at 34-49 fps)",
            "1/2: the game at half the display rate, one generated frame per rendered one (the default)",
            "2/3: the game held to two thirds of the display rate, every frame sent is interpolated (games above 67 fps)" };
        readonly TrackBar fgRate = new TrackBar { Minimum = 0, Maximum = 2, TickFrequency = 1, SmallChange = 1, LargeChange = 1, Width = 110, AutoSize = false, Height = 30, Margin = new Padding(0, 0, 0, 0) };
        readonly Label fgRateText = Ui.Line("");
        readonly CheckBox fgMv = new CheckBox { Text = "DLSS motion vectors", AutoSize = true, Margin = new Padding(14, 3, 0, 0) };
        readonly CheckBox fgRecommend = new CheckBox { Text = "Recommended", AutoSize = true, Margin = new Padding(8, 3, 0, 0) };
        readonly Button recenter = new Button { Text = "Recenter", AutoSize = true, Margin = new Padding(8, 3, 3, 3) };
        bool fgRateFromHost;   // the slider follows the host's rate: no command back
        // Game profiles (config\games\<exe>.json, written by the streamer at every game end: per game, headset and level the last run's numbers). The
        // launcher proposes a level from them when "Recommended" is ticked: the slider is set and greyed; unticked, the slider is yours again.
        // No run recorded for the game on this headset: nothing proposed, the slider stays live. The streamer itself decides nothing.
        string currentExe = "", headsetName = "";
        bool recommendationApplied;
        readonly Label footer = Ui.Line("", new Font("Segoe UI", 8.5f));
        string pairedName = "", pairedIp = "";
        bool closing;
        int restarts;
        string setupError;   // the streamer's `setup_error` message: a problem to fix, restarting it would not help

        public RuntimeForm(Dictionary<string, object> check, bool autoFind = false)
        {
            this.check = check;
            this.autoFind = autoFind;
            Text = "VisionALVR \u2013 Streamer";
            Icon = Ui.AppIcon();
            AutoScaleDimensions = new SizeF(96F, 96F);   // the pixel sizes below are designed at 96 dpi; without this nothing scales and only the fonts grow
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition = FormStartPosition.CenterScreen;   // like ALVR's dashboard, not the top-left corner (user, 2026-10-17)
            MaximizeBox = false;
            BackColor = Color.White;

            // the window is as big as its content, up to the work area; beyond that the body scrolls (Ui.AutoFitWindow)
            var body = new TableLayoutPanel { Dock = DockStyle.None, Location = Point.Empty, AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 4, 12, 12) };
            var gpu = check != null && check.TryGetValue("gpu", out var g) ? g as Dictionary<string, object> : null;
            runtime.Text = $"GPU: {S(gpu, "name")} ({S(gpu, "arch_name")}, driver {S(gpu, "driver")})   ·   OpenXR runtime: {(OpenXr.IsOurs() ? "VisionALVR (this folder)" : "NOT registered")}";
            runtime.ForeColor = Color.DimGray;
            var hrow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            hrow.Controls.Add(headset);
            hrow.Controls.Add(find);
            find.Click += (s, e) => FindHeadset(false);
            findHint.ForeColor = Color.DimGray;
            findHint.Font = new Font("Segoe UI", 8.5f);
            body.Controls.Add(hrow);
            body.Controls.Add(findHint);
            fwWarn.ForeColor = Color.Firebrick;
            fwWarn.Visible = fwFix.Visible = false;
            fwFix.Click += (s, e) => { Firewall.Allow(Paths.Host); CheckFirewall(); };
            body.Controls.Add(fwWarn);
            body.Controls.Add(fwFix);
            body.Controls.Add(game);
            body.Controls.Add(runtime);

            var box = statsBox;
            stats.Font = new Font("Segoe UI", 9f);
            stats.ForeColor = Color.DimGray;
            var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Font = Ui.Body };
            foreach (var t in new[] { tGame, tStreamed, tShown, tBitrate, tLatency, tLoss }) tiles.Controls.Add(t);
            var statsCol = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Font = Ui.Body };
            statsCol.Controls.Add(tiles);
            statsCol.Controls.Add(stats);
            box.Controls.Add(statsCol);
            body.Controls.Add(box);

            // Position and picture, two knobs per row so the window fits a laptop screen. Games with the action on the floor by default (Moss,
            // Titan Isles) assume a standing user; the headset's floor estimate is unreliable, so the streamer hands games the eye height set here
            // (above the floor) when you enter VR and keeps your own vertical movement on top of it. Everything is applied live.
            var live = new GroupBox { Text = "Position and picture (applied live)", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Font = Ui.Bold };
            var lt = new TableLayoutPanel { AutoSize = true, ColumnCount = 6, Dock = DockStyle.Fill, Font = Ui.Body };
            if (!settings.HasDisplay) { settings.Gamma = AppSettings.DefaultGamma; settings.Save(); } // the streamer reads it at start
            AddKnob(lt, "Eye height, m", 0, 220, AppSettings.DefaultHeight, () => settings.Height, v => settings.Height = v,
                    "Your eye height above the floor, as games see it: about 1.30 m seated, 1.6-1.7 m standing; 0 = the headset's own floor estimate. Games that place you from the floor use it; moving it re-anchors at your current head position.");
            heightKnob = knobs[knobs.Count - 1];
            AddKnob(lt, "Gamma (>1 brighter)", 50, 200, AppSettings.DefaultGamma, () => settings.Gamma, v => settings.Gamma = v);
            AddKnob(lt, "Brightness", -30, 30, 0, () => settings.Brightness, v => settings.Brightness = v);
            AddKnob(lt, "Contrast", -30, 30, 0, () => settings.Contrast, v => settings.Contrast = v);
            AddKnob(lt, "Saturation", -50, 50, 0, () => settings.Saturation, v => settings.Saturation = v, "Below 0: less vivid.");
            AddKnob(lt, "Sharpening", 0, 100, 0, () => settings.Sharpening, v => settings.Sharpening = v);
            var reset = new Button { Text = "Defaults", AutoSize = true };
            reset.Click += (s, e) => { foreach (var k in knobs) k.Bar.Value = (int)Math.Round(k.Def * 100); };
            tips.SetToolTip(reset, "Eye height 1.30 m, gamma 1.2, the rest neutral.");
            var util = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            util.Controls.Add(reset);
            util.Controls.Add(recenter);
            lt.Controls.Add(util);
            lt.SetColumnSpan(util, 6);
            var fgRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            fgRow.Controls.Add(frameGen);
            var fgText = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            fgText.Controls.Add(new Label { Text = "Frame generation", AutoSize = true, Font = Ui.Bold, Margin = new Padding(0, 4, 0, 0) });
            fgText.Controls.Add(new Label { Text = "The game renders at half the display rate and the streamer fills the frame in between. For games that cannot hold 70-75 fps; applies at once.", AutoSize = true, Font = Ui.Body, ForeColor = Color.DimGray, MaximumSize = new Size(560, 0), Margin = new Padding(0) });
            fgRow.Controls.Add(fgText);
            lt.Controls.Add(fgRow);
            lt.SetColumnSpan(fgRow, 6);
            // the generation's knobs (docs/FRAME_GENERATION.md): the game's rate as a fraction of the display rate (1/3, 1/2, 2/3) and the game's
            // motion vectors. Every change is sent to the running streamer, saved, and logged by both sides. The uncapped rate, the fps cap and
            // the content lag stay available in config\visionalvr.json and on the host's command line for experiments.
            var fgOpts = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(44, 2, 0, 0) };
            fgOpts.Controls.Add(new Label { Text = "Game rate", AutoSize = true, Font = Ui.Body, Margin = new Padding(0, 6, 6, 0) });
            fgOpts.Controls.Add(new Label { Text = "1/3", AutoSize = true, Font = Ui.Body, Margin = new Padding(0, 6, 2, 0) });
            fgOpts.Controls.Add(fgRate);
            fgOpts.Controls.Add(new Label { Text = "2/3", AutoSize = true, Font = Ui.Body, Margin = new Padding(2, 6, 0, 0) });
            fgOpts.Controls.Add(fgRecommend);
            fgOpts.Controls.Add(fgMv);
            lt.Controls.Add(fgOpts);
            lt.SetColumnSpan(fgOpts, 6);
            fgRateText.Margin = new Padding(44, 0, 0, 4);
            fgRateText.ForeColor = Color.DimGray;
            lt.Controls.Add(fgRateText);
            lt.SetColumnSpan(fgRateText, 6);
            // the desktop mirror: between games the headset shows the Windows desktop on a virtual screen and the right hand (pinch = click, grip = right
            // click) is the mouse. For games started from the headset (VR controller games); off by default: the green passthrough screen is for games
            // started from the desktop (UEVR). While on, the passthrough key colour is not shown between games.
            var mirrorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            mirrorRow.Controls.Add(mirror);
            var mirrorText = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            mirrorText.Controls.Add(new Label { Text = "Mirror Windows desktop between sessions", AutoSize = true, Font = Ui.Bold, Margin = new Padding(0, 4, 0, 0) });
            mirrorText.Controls.Add(new Label { Text = "Shows the desktop in the headset when no game runs, and your right hand is the mouse (pinch = click). For games you start from the headset; replaces the passthrough screen while on.", AutoSize = true, Font = Ui.Body, ForeColor = Color.DimGray, MaximumSize = new Size(560, 0), Margin = new Padding(0) });
            mirrorRow.Controls.Add(mirrorText);
            lt.Controls.Add(mirrorRow);
            lt.SetColumnSpan(mirrorRow, 6);
            tips.SetToolTip(fgRate, "How fast the game renders, as a fraction of the display rate; the streamer generates the frames in between. 1/2 is the default.");
            tips.SetToolTip(fgRecommend, "The slider is set from this game's recorded runs on this headset (config\\games\\<game>.json): the fastest rate whose typical 10-second window repeated under 5 % of the headset's periods; a rate that filled its cap suggests the next one once. Untick to choose yourself.");
            tips.SetToolTip(recenter, "Turn the tracking space so your current forward is forward and your position the origin (done by the streamer on its own at every game start; the recenter key (End by default, set in the wizard) does the same while playing).");
            tips.SetToolTip(fgMv, "Synthesise from the game's DLSS motion vectors when the OptiScaler-DLSSMotionVectorExtractor DLL is in the game folder (else optical flow).");
            live.Controls.Add(lt);
            body.Controls.Add(live);

            debug.Margin = new Padding(3, 8, 3, 3);   // a diagnostic aid, off unless asked for: it lives at the bottom
            body.Controls.Add(debug);
            footer.ForeColor = Color.DimGray;
            body.Controls.Add(footer);

            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            scroller.Controls.Add(body);
            Controls.Add(scroller);
            var hdr = Ui.CompactHeader("Streamer for ALVR headsets (Vision Pro, Quest, ...)");
            Controls.Add(hdr);
            Ui.AutoFitWindow(this, hdr, body);


            debug.Checked = settings.Debug;
            debug.CheckedChanged += (s, e) =>
            {
                host.Send("debug " + (debug.Checked ? "on" : "off"));
                settings.Debug = debug.Checked;
                settings.Save();
            };
            frameGen.Checked = settings.FrameGen;
            mirror.Checked = settings.MirrorDesktop;
            mirror.CheckedChanged += (s, e) =>
            {
                host.Send("mirror " + (mirror.Checked ? "on" : "off"));
                settings.MirrorDesktop = mirror.Checked;
                settings.Save();
                Log.Write("INFO", "desktop mirror " + (mirror.Checked ? "on" : "off"));
            };
            Action fgEnable = () => { fgRate.Enabled = frameGen.Checked && !(fgRecommend.Checked && recommendationApplied); fgRecommend.Enabled = fgMv.Enabled = frameGen.Checked; };
            fgEnableAll = fgEnable;
            frameGen.CheckedChanged += (s, e) =>
            {
                host.Send("frame_gen " + (frameGen.Checked ? "on" : "off"));   // the running streamer switches at the next boundary; the setting is read at start too
                settings.FrameGen = frameGen.Checked;
                settings.Save();
                fgEnable();
                Log.Write("INFO", "frame generation " + (frameGen.Checked ? "on" : "off"));
            };
            // "uncapped" (config only) shows at the 2/3 position and is kept until the slider moves
            var savedRate = Array.IndexOf(FgRates, settings.FrameGenRate);
            fgRate.Value = savedRate >= 0 ? savedRate : (settings.FrameGenRate == "uncapped" ? 2 : 1);
            fgRateText.Text = settings.FrameGenRate == "uncapped" ? "uncapped (config): the game runs free, every frame sent is interpolated" : FgRateText[fgRate.Value];
            fgMv.Checked = settings.GameMotionVectors;
            fgRecommend.Checked = settings.FrameGenRecommended;
            fgEnable();
            fgRate.ValueChanged += (s, e) =>
            {
                var rate = FgRates[fgRate.Value];
                fgRateText.Text = FgRateText[fgRate.Value];
                if (fgRateFromHost || rate == settings.FrameGenRate) return;
                host.Send("frame_gen_rate " + rate);
                settings.FrameGenRate = rate;
                settings.Save();
                Log.Write("INFO", "frame generation rate " + rate);
            };
            fgMv.CheckedChanged += (s, e) =>
            {
                host.Send("game_mv " + (fgMv.Checked ? "on" : "off"));
                settings.GameMotionVectors = fgMv.Checked;
                settings.Save();
                Log.Write("INFO", "game motion vectors " + (fgMv.Checked ? "on" : "off"));
            };
            fgRecommend.CheckedChanged += (s, e) =>
            {
                settings.FrameGenRecommended = fgRecommend.Checked;
                settings.Save();
                Log.Write("INFO", "recommended rate " + (fgRecommend.Checked ? "on" : "off"));
                ApplyRecommendation(fgRecommend.Checked);
            };
            recenter.Click += (s, e) => { host.Send("recenter"); Log.Write("INFO", "recenter"); };


            host.OnEvent += (name, data) => BeginInvoke((Action)(() => HostEvent(name, data)));
            host.OnExit += code => BeginInvoke((Action)(() => HostExited(code)));
            Shown += (s, e) =>
            {
                StartHost();
                CheckFirewall();
                if (autoFind) { var t = new Timer { Interval = 3000 }; t.Tick += (a, b) => { t.Stop(); FindHeadset(true); }; t.Start(); }
            };
            Ui.ScreenshotWhenShown(this, 12000);
            FormClosing += (s, e) => { closing = true; footer.Text = "Stopping the streamer..."; Refresh(); host.Stop(); Log.Write("INFO", "streamer.exe closed"); };
        }

        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400 };

        void CheckFirewall()
        {
            if (Ui.ScreenshotPath() != null) return;
            System.Threading.Tasks.Task.Run(() => Firewall.Check(Paths.Host)).ContinueWith(t =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        var st = t.Result;
                        var bad = st == FirewallState.Blocked || st == FirewallState.Missing;
                        fwWarn.Visible = fwFix.Visible = bad;
                        fwWarn.Text = st == FirewallState.Blocked ? "Windows Firewall BLOCKS alvr_host.exe in this folder (a dismissed Windows prompt does that): the headset will not be found and cannot connect."
                                    : "Windows Firewall has no rule for alvr_host.exe in this folder: the headset may not be found or may not connect until it is allowed (the rule belongs to this exact folder).";
                        Log.Write(bad ? "WARN" : "INFO", "firewall: " + st);
                    }));
                }
                catch { }
            });
        }

        void AddKnob(TableLayoutPanel t, string name, int min, int max, double def, Func<double> get, Action<double> set, string tip = null)
        {
            var k = new Knob { Name = name, Def = def, Get = get, Set = set, Value = Ui.Line("0.00"),
                               Bar = new TrackBar { Minimum = min, Maximum = max, TickFrequency = (max - min) / 6, SmallChange = 5, LargeChange = 10, Width = 170, AutoSize = false, Height = 32, AccessibleName = name } };
            k.Bar.Value = (int)Math.Max(min, Math.Min(max, Math.Round(get() * 100)));
            k.Value.Text = (k.Bar.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            k.Bar.ValueChanged += (s, e) =>
            {
                var v = k.Bar.Value / 100.0;
                k.Value.Text = v.ToString("0.00", CultureInfo.InvariantCulture);
                k.Set(v);
                settings.Save();
                SendDisplay();
            };
            var label = Ui.Line(name + ":");
            t.Controls.Add(label);
            t.Controls.Add(k.Bar);
            t.Controls.Add(k.Value);
            if (tip != null) { tips.SetToolTip(label, tip); tips.SetToolTip(k.Bar, tip); }
            knobs.Add(k);
        }

        // The eye-height override exists for the Vision Pro, whose floor estimate is unreliable. A Quest (or any other headset) reports its
        // floor height correctly, and the override only displaced the user (Valor Mortis, 2026-10-17): for those the knob is greyed out and
        // the host is handed 0 = the headset's own floor; the saved value stays for the next Vision Pro session.
        Knob heightKnob;
        bool heightFromHeadset;
        Action fgEnableAll;

        // The proposal from config\games\<exe>.json for the running game on the connected headset (null = no run recorded). Levels in order; a level
        // counts when its last run lasted 30 s or more; "clean" = in the typical 10-second window the headset repeated under 5 % of its periods
        // AND the host repeated under 5 % of its outputs (a game under the level's cap makes the host re-send its last frame: the headset shows a
        // frame every period, frozen content; Valor Mortis at 1/2 on the Vision Pro read 0.4 % headset / 14 % host, 2026-10-17). The fastest
        // clean level is proposed; if that run rendered at least 95 % of the level's fps cap (the game was held back) and the next level has no
        // run yet, the next level is proposed once. No clean level: the one with the fewest repeats (headset and host added).
        static readonly string[] Levels = { "third", "half", "two_thirds" };
        static readonly double[] LevelCap = { 1.0 / 3, 0.5, 2.0 / 3 };
        (string level, string why)? Recommend(string exe, string hs)
        {
            try
            {
                var gd = Games.Load(exe);
                if (!(gd.TryGetValue("levels", out var lv) && lv is Dictionary<string, object> byHeadset && byHeadset.TryGetValue(hs, out var h) && h is Dictionary<string, object> runs)) return null;
                int best = -1; bool bestClean = false; double bestScore = 9.0; Dictionary<string, object> bestRun = null;
                for (int i = 0; i < Levels.Length; i++)
                {
                    if (!(runs.TryGetValue(Levels[i], out var ro) && ro is Dictionary<string, object> run) || D(run, "seconds") < 30) continue;
                    double headsetRep = D(run, "headset_repeated_typ"), hostRep = D(run, "host_repeats_typ");
                    if (double.IsNaN(hostRep)) hostRep = 0;
                    bool clean = headsetRep < 0.05 && hostRep < 0.05;
                    // a clean level beats any dirty one and a faster clean level beats a slower one; among dirty ones the fewest repeats
                    if (clean || (!bestClean && (best < 0 || headsetRep + hostRep < bestScore))) { best = i; bestClean = clean; bestScore = headsetRep + hostRep; bestRun = run; }
                }
                if (best < 0) return null;
                string[] names = { "1/3", "1/2", "2/3" };
                var fps = D(bestRun, "rendered_fps"); var hz = D(bestRun, "display_hz");
                var low = D(bestRun, "rendered_fps_low");
                var text = $"{names[best]}: last run {fps:0} fps{(low > 0 ? $", {low:0} in its slowest tenth" : "")}, headset repeated {D(bestRun, "headset_repeated_typ") * 100:0.0} % typical ({D(bestRun, "headset_repeated_p90") * 100:0.0} % in its heavy parts), host repeats {D(bestRun, "host_repeats_typ") * 100:0.0} %";
                if (bestClean && best < 2 && hz > 0 && fps >= 0.95 * hz * LevelCap[best] && !runs.ContainsKey(Levels[best + 1]))
                    return (Levels[best + 1], $"{names[best + 1]} to try: {names[best]} was clean and filled its cap ({fps:0} fps)");
                return (Levels[best], text);
            }
            catch { return null; }
        }

        void ApplyRecommendation(bool apply)
        {
            recommendationApplied = false;
            if (currentExe.Length == 0 || headsetName.Length == 0) { fgEnableAll?.Invoke(); return; }
            var rec = Recommend(currentExe, headsetName);
            if (rec == null)
            {
                fgRateText.Text = $"No run recorded yet for {currentExe} on {headsetName}: the slider is yours; the run is recorded when the game ends.";
            }
            else if (apply)
            {
                var pos = Array.IndexOf(FgRates, rec.Value.level);
                if (pos >= 0 && fgRate.Value != pos) fgRate.Value = pos;   // sends frame_gen_rate and saves, like a hand move
                recommendationApplied = true;
                fgRateText.Text = $"Recommended for {currentExe} on {headsetName}: {rec.Value.why}";
            }
            else
            {
                fgRateText.Text = $"Recorded for {currentExe} on {headsetName}: {rec.Value.why} (untick Recommended keeps the slider yours)";
            }
            fgEnableAll?.Invoke();
        }

        void HeightSource(string headsetName)
        {
            if (string.IsNullOrEmpty(headsetName)) return;
            this.headsetName = headsetName;
            var fromHeadset = !headsetName.Contains("Apple");
            if (fromHeadset == heightFromHeadset) return;
            heightFromHeadset = fromHeadset;
            heightKnob.Bar.Enabled = !fromHeadset;
            tips.SetToolTip(heightKnob.Bar, fromHeadset ? $"{headsetName} reports its own floor height: the override is off for it." : "Your eye height above the floor, as games see it: about 1.30 m seated, 1.6-1.7 m standing; 0 = the headset's own floor estimate.");
            SendDisplay();
        }

        void SendDisplay()
        {
            string f(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            host.Send("gamma " + f(settings.Gamma));
            host.Send("height " + f(heightFromHeadset ? 0 : settings.Height));
            host.Send($"color {f(settings.Brightness)} {f(settings.Contrast)} {f(settings.Saturation)} {f(settings.Sharpening)}");
        }

        void StartHost()
        {
            var hs = Session.Headset(Session.Load());
            pairedName = hs?.Item2 ?? "";
            pairedIp = hs?.Item3 ?? "";
            headset.Text = hs == null ? "Headset: none paired - click Find headset" : $"Headset: searching for {pairedName} ({pairedIp}) ...";
            headset.ForeColor = Color.DarkOrange;
            findHint.Visible = true;
            var args = $"--install-dir \"{Paths.Dir}\" --shim --daemon --gui" + (debug.Checked ? " --debug" : "");
            var err = host.Start(args);
            if (err != null)
            {
                MessageBox.Show(this, err, "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log.Write("ERROR", err);
                return;
            }
            footer.Text = "Streamer running. Open the ALVR app on the headset; it connects by itself. Close this window to stop.";
            Log.Write("INFO", "streamer started: alvr_host " + args);
        }

        /// The streamer owns UDP 9943 (headset discovery), so it stops while the dialog searches; a chosen headset is paired (saved address) and
        /// the streamer starts again either way.
        void FindHeadset(bool autoPick)
        {
            if (finding) return;
            if (gameRunning && !autoPick && MessageBox.Show(this, "A game is running. Searching restarts the streamer: the game loses the headset for a few seconds. Continue?",
                    "VisionALVR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            finding = true;
            find.Enabled = false;
            closing = true;
            headset.Text = "Headset: searching the network...";
            headset.ForeColor = Color.DarkOrange;
            host.Stop();
            Log.Write("INFO", "find headset: streamer stopped, searching");
            try
            {
                using (var dlg = new FindHeadsetForm(Session.Headset(Session.Load()), autoPick))
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        var session = Session.Load();
                        Session.Pair(session, dlg.ChosenHost, dlg.ChosenName, dlg.ChosenIp);
                        Json.Save(Paths.Session, session);
                        settings.HeadsetHost = dlg.ChosenHost; settings.HeadsetName = dlg.ChosenName; settings.HeadsetIp = dlg.ChosenIp;
                        settings.Save();
                        Log.Write("INFO", $"find headset: paired {dlg.ChosenName} ({dlg.ChosenHost}) at {dlg.ChosenIp}");
                    }
                    else Log.Write("INFO", "find headset: cancelled");
            }
            catch (Exception e) { Log.Write("ERROR", "find headset failed: " + e.Message); }
            closing = false;
            finding = false;
            find.Enabled = true;
            restarts = 0;
            StartHost();
        }

        void HostExited(int code)
        {
            if (closing) return;
            Log.Write("ERROR", $"alvr_host.exe exited unexpectedly (code {code})");
            headset.Text = "Streamer stopped (see logs)";
            headset.ForeColor = Color.Firebrick;
            if (code == 7)   // setup problem (e.g. config\session.json missing or broken): say what to do, do not restart
            {
                headset.Text = "Setup problem: the streamer cannot start";
                footer.Text = setupError ?? "The streamer reported a setup problem. Check logs\\VisionALVR.log.";
                footer.ForeColor = Color.Firebrick;
                return;
            }
            if (restarts++ < 3)
            {
                footer.Text = $"The streamer exited (code {code}); restarting ({restarts}/3)...";
                var t = new Timer { Interval = 2000 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); StartHost(); };
                t.Start();
            }
            else footer.Text = $"The streamer exited (code {code}) several times. Check logs\\VisionALVR.log.";
        }

        static string S(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        static double D(Dictionary<string, object> d, string k) { try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN; } catch { return double.NaN; } }
        static string F(double v, string fmt) => double.IsNaN(v) ? "  -" : v.ToString(fmt, CultureInfo.InvariantCulture);

        void HostEvent(string name, Dictionary<string, object> d)
        {
            switch (name)
            {
                case "setup_error":
                    setupError = S(d, "message");
                    break;
                case "client_connected":
                    HeightSource(S(d, "name"));
                    var n = S(d, "name");
                    var ip = S(d, "ip");
                    headset.Text = $"Headset: connected - {(n.Length > 0 ? n : pairedName)} ({(ip.Length > 0 ? ip : pairedIp)})";
                    headset.ForeColor = Color.SeaGreen;
                    findHint.Visible = false;
                    if (ip.Length > 0 && ip != settings.HeadsetIp) { settings.HeadsetIp = ip; settings.Save(); }
                    break;
                case "client_disconnected":
                    findHint.Visible = true;
                    headset.Text = $"Headset: searching for {pairedName} ({pairedIp}) ...";
                    headset.ForeColor = Color.DarkOrange;
                    break;
                case "headset_seen":
                    // the streamer's discovery (mDNS) saw a headset: show it while nothing is connected; a paired headset whose
                    // address changed is dialled by the streamer itself, keep our note of its address in step
                    if (headset.ForeColor != Color.SeaGreen)
                    {
                        var seenName = S(d, "hostname"); var seenIp = S(d, "ip");
                        var paired = pairedName.Length > 0 && (seenName == settings.HeadsetHost || seenIp == pairedIp);
                        headset.Text = paired ? $"Headset: {pairedName} seen at {seenIp}, connecting..." : $"Headset: {seenName} seen at {seenIp} - not paired (click Find headset)";
                        if (paired && seenIp.Length > 0 && seenIp != settings.HeadsetIp) { settings.HeadsetIp = seenIp; settings.Save(); pairedIp = seenIp; }
                    }
                    break;
                case "game_started":
                    game.Text = "Game: " + S(d, "exe");
                    gameRunning = true;
                    currentExe = S(d, "exe");
                    lastSession = false;
                    statsBox.Text = "Streaming (updated every 10 s)";
                    ApplyRecommendation(fgRecommend.Checked);
                    break;
                case "game_ended":
                    gameRunning = false;
                    game.Text = $"Game: none (last: {S(d, "exe")}, {F(D(d, "seconds") / 60, "0.0")} min)";
                    break;
                case "game_run":
                    // the streamer recorded the run: the Streaming box turns into the run's record and what will be proposed next time
                    // (no pop-up; the live numbers come back with the next game)
                    {
                        lastSession = true;
                        statsBox.Text = "Streaming (last session stats)";
                        string[] names = { "1/3", "1/2", "2/3" };
                        var lvl = S(d, "level"); var li = Array.IndexOf(FgRates, lvl);
                        var rec = Recommend(S(d, "exe"), S(d, "headset"));
                        var ri = rec == null ? -1 : Array.IndexOf(FgRates, rec.Value.level);
                        var net = d != null && d.TryGetValue("net", out var no) ? no as Dictionary<string, object> : null;
                        var rep = D(d, "headset_repeated_typ") * 100;
                        tGame.Set(F(D(d, "rendered_fps"), "0.0") + " fps", $"average, slowest tenth {F(D(d, "rendered_fps_low"), "0.0")}");
                        tStreamed.Set(F(D(d, "streamed_fps"), "0.0") + " fps", $"{F(D(d, "seconds") / 60, "0.0")} min at {(li >= 0 ? names[li] : lvl)}");
                        tShown.Set(F(D(net, "displayed_fps"), "0.0") + " fps", $"repeated periods {F(rep, "0.0")} %", rep >= 5 ? Color.Firebrick : (Color?)null);
                        tBitrate.Set(F(D(net, "bitrate_mbps"), "0") + " Mbps", "average");
                        tLatency.Set(F(D(net, "latency_ms"), "0") + " ms", $"p95 {F(D(net, "latency_p95_ms"), "0")}");
                        tLoss.Set(F(D(net, "packets_lost"), "0"), "packets lost", D(net, "packets_lost") > 0 ? Color.Firebrick : (Color?)null);
                        stats.Text = $"Host repeats {F(D(d, "host_repeats_typ") * 100, "0.0")} % (game under the cap), skipped {F(D(d, "skipped") * 100, "0.0")} %. " +
                                     $"Frame generation recommendation: {(rec == null ? "none yet" : (ri >= 0 ? names[ri] : rec.Value.level) + " next time (" + rec.Value.why + ")")}";
                        OfferDll(d);
                    }
                    break;
                case "status":
                    var c = d != null && d.TryGetValue("client", out var cc) ? cc as Dictionary<string, object> : null;
                    if (d != null && d.TryGetValue("headset", out var ho) && ho is Dictionary<string, object> hd) HeightSource(S(hd, "name"));
                    if (d != null && d.ContainsKey("fg_rate") && S(d, "fg_on") == "True")
                    {
                        // the rate in force (the slider follows the streamer, e.g. a rate set in the config file)
                        var pos = Array.IndexOf(FgRates, S(d, "fg_rate"));
                        if (pos >= 0 && fgRate.Value != pos) { fgRateFromHost = true; fgRate.Value = pos; fgRateFromHost = false; }
                    }
                    if (!lastSession)
                    {
                        tGame.Set(F(D(d, "app_fps"), "0.0") + " fps", "");
                        tStreamed.Set(F(D(d, "stream_fps"), "0.0") + " fps", "not streamed " + S(d, "frames_not_streamed"));
                        tShown.Set(F(double.IsNaN(D(c, "displayed_fps")) ? D(c, "client_fps") : D(c, "displayed_fps"), "0.0") + " fps", "");
                        tBitrate.Set(F(D(d, "mbps"), "0") + " Mbps", "");
                        tLatency.Set(F(D(c, "total_latency_ms"), "0") + " ms", $"enc {F(D(c, "encode_ms"), "0")} + net {F(D(c, "network_ms"), "0")} + dec {F(D(c, "decode_ms"), "0")}");
                        tLoss.Set(S(d, "packets_lost"), "packets lost", D(d, "packets_lost") > 0 ? Color.Firebrick : (Color?)null);
                        stats.Text = "Updated every 10 s.";
                    }
                    break;
                case "encode_error":
                    footer.Text = "Encode error: " + S(d, "message");
                    break;
            }
        }

        /// uitest.exe: the fullest the window gets (long lines, the firewall warning, every tile filled), to audit the layout at any size and scale.
        public void ApplyDemoState(bool worstCase = true)
        {
            headset.Text = worstCase ? "Headset: connected - Apple Vision Pro of a person with a long name (192.168.100.123)" : "Headset: connected - Apple Vision Pro (192.168.1.50)"; headset.ForeColor = Color.SeaGreen;
            runtime.Text = "GPU: NVIDIA GeForce RTX 5090 (Blackwell, driver 617.42)   \u00B7   OpenXR runtime: VisionALVR (this folder)";
            if (!worstCase)
            {
                findHint.Visible = false;
                game.Text = "Game: Cyberpunk2077.exe";
                tGame.Set("44.8 fps", "average, slowest tenth 38.2"); tStreamed.Set("89.7 fps", "25.0 min at 1/2"); tShown.Set("89.1 fps", "repeated periods 1.0 %");
                tBitrate.Set("120 Mbps", "average"); tLatency.Set("57 ms", "enc 6 + net 9 + dec 14"); tLoss.Set("0", "packets lost");
                stats.Text = "Updated every 10 s."; frameGen.SetCheckedInstant(true);
                return;
            }
            findHint.Visible = true;
            fwWarn.Text = "Windows Firewall blocks alvr_host.exe: the headset will not be found by discovery and the stream may not connect."; fwWarn.Visible = fwFix.Visible = true;
            game.Text = "Game: Cyberpunk2077.exe (Cyberpunk 2077) after 123 min";
            tGame.Set("144.4 fps", "average, slowest tenth 120.0"); tStreamed.Set("144.4 fps", "123.4 min at 1/2"); tShown.Set("144.4 fps", "repeated periods 12.3 %", Color.Firebrick);
            tBitrate.Set("1000 Mbps", "average"); tLatency.Set("123 ms", "enc 12 + net 12 + dec 12"); tLoss.Set("1234", "packets lost", Color.Firebrick);
            stats.Text = "Host repeats 12.3 % (game under the cap), skipped 12.3 %. Frame generation recommendation: 1/2 next time (1/2: last run 44 fps, 38 in its slowest tenth, headset repeated 1.2 % typical (5.6 % in its heavy parts), host repeats 0.0 %)";
            fgRateText.Text = FgRateText[2];
            footer.Text = "config\\session.json is missing and there is no config\\session.default.json to create it from: this VisionALVR folder is incomplete. Unzip the release again (you can keep your config\\ folder).";
            footer.ForeColor = Color.Firebrick;
        }

        // After a game with generation on and the DLSS-vectors tick on, if the game gave no vectors and the extractor DLL is not in its folder:
        // offer to install it (once per game unless asked again). Installed copies are managed in the Logs window.
        void OfferDll(Dictionary<string, object> d)
        {
            try
            {
                var exe = S(d, "exe"); var stem = Games.Stem(exe);
                var mv = d != null && d.TryGetValue("mv", out var mo) ? mo as Dictionary<string, object> : null;
                if (S(d, "level") == "off" || mv == null || !(mv.TryGetValue("enabled", out var eo) && eo is bool en && en) || D(mv, "frames") > 0) return;
                if (MvDll.Dismissed(stem)) return;
                var dir = MvDll.GameDir(Games.Load(exe));
                var st = MvDll.Check(dir, stem, out _);
                if (st == MvDll.State.NoMaster || st == MvDll.State.NoFolder || st == MvDll.State.Installed) return;
                using (var dlg = new DllOfferForm(S(Games.Load(exe), "name").Length > 0 ? S(Games.Load(exe), "name") : exe))
                {
                    var r = dlg.ShowDialog(this);
                    if (dlg.DontAsk) MvDll.Dismiss(stem);
                    if (r == DialogResult.Yes) MessageBox.Show(this, MvDll.Install(dir, stem, out _), "VisionALVR");
                }
            }
            catch (Exception e) { Log.Write("WARN", "DLL offer failed: " + e.Message); }
        }

        [STAThread]
        static void Main()
        {
            Log.Component = "gui";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (new System.Threading.Mutex(true, "VisionALVR_Runtime_Window", out bool first))
            {
                if (!first) { MessageBox.Show("VisionALVR is already running.", "VisionALVR"); return; }
                Log.Write("INFO", $"streamer.exe {Version.Text} start ({Paths.Dir})");
                if (!File.Exists(Paths.Host))
                {
                    MessageBox.Show("bin\\alvr_host.exe was not found. Unzip the whole VisionALVR folder and start VisionALVR.exe from it.", "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                var check = SystemCheck.Run();
                if (check == null || !(check.TryGetValue("ok", out var ok) && ok is bool b && b))
                {
                    var why = check != null && check.TryGetValue("reason", out var r) ? r as string : "the system check failed (see logs)";
                    Log.Write("ERROR", "refusing to start: " + why);
                    MessageBox.Show("VisionALVR cannot run on this PC: " + why, "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                var testing = Ui.ScreenshotPath() != null || Environment.GetCommandLineArgs().Contains("--test");   // --test: no prompts (harness)
                if (!OpenXr.IsOurs() && !testing)
                {
                    var cur = OpenXr.ActiveRuntime();
                    var q = MessageBox.Show($"VisionALVR is not the Windows OpenXR runtime (currently: {(cur.Length > 0 ? cur : "none")}).\n\n" +
                                            "Register this folder now? (asks for administrator rights; the OpenXR runtime button of the launcher puts the previous one back)",
                                            "VisionALVR", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (q == DialogResult.Yes && !OpenXr.Register(null))
                        MessageBox.Show("Registration did not take effect. Use the OpenXR runtime button of the launcher (VisionALVR.exe).", "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                if (Session.Headset(Session.Load()) == null && !testing)
                {
                    if (MessageBox.Show("No headset is paired yet. Open configure.exe to set up and pair your headset? (Or answer No and use Find headset in the window.)", "VisionALVR", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        Process.Start(Paths.Configure);
                        return;
                    }
                }
                Application.Run(new RuntimeForm(check, Environment.GetCommandLineArgs().Contains("--find-auto")));
            }
        }
    }
}
