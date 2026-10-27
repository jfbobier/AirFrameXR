// AirFrameXR.exe: the runtime window. Checks the GPU and the OpenXR registration, starts the streamer host as soon as it opens
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

namespace AirFrameXR
{
    public class RuntimeForm : DpiForm
    {
        readonly AppSettings settings = new AppSettings();
        readonly HostProcess host = new HostProcess();
        readonly AudioRoute audio = new AudioRoute();
        readonly Label headset = Ui.Line("Headset: -", Ui.Big);
        readonly Button find = new Button { Text = "Find headset...", AutoSize = true, Margin = new Padding(12, 0, 0, 0) };
        readonly Label findHint = Ui.Line("Not connecting? Use Find headset.");
        // Windows Firewall: the rule belongs to this exact folder; a missing or blocking rule means no discovery and no connection
        readonly Label fwWarn = Ui.Line("", Ui.Bold);
        readonly Button fwFix = new Button { Text = "Allow through the firewall", AutoSize = true };
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
        readonly CheckBox debug = new CheckBox { Text = "Debug logging", AutoSize = true, Font = Ui.Body };
        readonly ToggleSwitch frameGen = new ToggleSwitch { Margin = new Padding(3, 6, 10, 3), AccessibleName = "Frame generation" };
        readonly ToggleSwitch mirror = new ToggleSwitch { Margin = new Padding(3, 6, 10, 3), AccessibleName = "Mirror the Windows desktop between sessions" };
        // the game's rate as a fraction of the display rate: 1/3, 1/2 (default), 2/3 (settings video.frame_gen_rate third | half | two_thirds)
        static readonly string[] FgRates = { "third", "half", "two_thirds" };
        static readonly string[] FgRateText = {
            "1/3: two generated frames per rendered one (games at 34-49 fps).",
            "1/2: one generated frame per rendered one (default).",
            "2/3: every frame sent is interpolated (games above 67 fps)." };
        readonly Segmented fgRate = new Segmented("1/3", "1/2", "2/3") { AccessibleName = "Game rate" };
        class TipText { public Action<string> On; public string Text { set { On?.Invoke(value); } } }   // explanations go to tooltips, not to the window
        readonly TipText fgRateText = new TipText();
        readonly Segmented heightMode = new Segmented("Auto", "Manual") { AccessibleName = "Eye height mode" };
        readonly NumericUpDown heightBox = new NumericUpDown { DecimalPlaces = 2, Increment = 0.05m, Minimum = 0.5m, Maximum = 2.2m, Width = 64, AccessibleName = "Eye height in metres" };
        readonly Label heightReport = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 7, 0, 0), Visible = false };
        readonly Label audioLine = new Label { AutoSize = true, Visible = false, Margin = new Padding(3, 4, 3, 0), MaximumSize = new Size(560, 0) };   // the virtual audio device's volume, only when it matters
        readonly Label exportStatus = new Label { AutoSize = true, Margin = new Padding(3, 4, 3, 0), MaximumSize = new Size(560, 0) };
        readonly LinkLabel exportLink = new LinkLabel { Text = "Open folder", AutoSize = true, Margin = new Padding(3, 4, 3, 0) };
        // uncapped generation (settings video.frame_gen_rate uncapped, docs/FRAME_GENERATION_UNCAPPED.md): the game runs at its own rate, every output interpolated
        // experimental, set in the Advanced settings window: here only the state, with a note under the slider while it is on
        readonly CheckBox fgUnc = new CheckBox { Text = "Uncapped", AutoSize = true, Visible = false };
        readonly Label fgUncNote = new Label { Text = "Uncapped (experimental) is on: set in Advanced settings", AutoSize = true, ForeColor = Color.DarkGoldenrod, Visible = false, Margin = new Padding(3, 0, 0, 2) };
        readonly ToggleButton fgRecommend = new ToggleButton { Text = "Auto", Margin = new Padding(0, 0, 8, 0), AccessibleName = "Choose the game rate automatically from this game's recorded runs" };
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
            Text = "AirFrameXR \u2013 Streamer";
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
            runtime.Text = $"GPU: {S(gpu, "name")} ({S(gpu, "arch_name")}, driver {S(gpu, "driver")})   ·   OpenXR runtime: {(OpenXr.IsOurs() ? "AirFrameXR (this folder)" : "NOT registered")}";
            runtime.ForeColor = Color.DimGray;
            var hrow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            hrow.Controls.Add(headset);
            hrow.Controls.Add(find);
            find.Click += (s, e) => FindHeadset(false);
            findHint.ForeColor = Color.DimGray;
            findHint.Font = new Font("Segoe UI", 8.5f);
            body.Controls.Add(hrow);
            tips.SetToolTip(find, "Not connecting? Search the network for the headset.");
            var fwRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };   // a blocking problem: at the top, and the window grows to show it (Refit below)
            fwRow.Controls.Add(fwWarn); fwRow.Controls.Add(fwFix);
            body.Controls.Add(fwRow);
            fwWarn.ForeColor = Color.Firebrick;
            fwWarn.Visible = fwFix.Visible = false;
            fwFix.Click += (s, e) => { Firewall.Allow(Paths.Host); CheckFirewall(); };
            body.Controls.Add(game);
            body.Controls.Add(runtime);

            var box = statsBox;
            stats.Font = new Font("Segoe UI", 9f);
            stats.MinimumSize = new Size(0, 22);   // its line is reserved
            stats.ForeColor = Color.DimGray;
            var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Font = Ui.Body };
            foreach (var t in new[] { tGame, tStreamed, tShown, tBitrate, tLatency, tLoss }) tiles.Controls.Add(t);
            var statsCol = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Font = Ui.Body };
            statsCol.Controls.Add(tiles);
            statsCol.Controls.Add(stats);
            box.Controls.Add(statsCol);
            body.Controls.Add(box);

            // Components, top to bottom (docs/UI_SPEC.md): Streaming (above), Frame generation, Position, Picture, Desktop mirror, then the debug row. Everything applies live.
            if (!settings.HasDisplay) { settings.Gamma = AppSettings.DefaultGamma; settings.Save(); } // the streamer reads it at start
            GroupBox Group(string title, Control content)
            {
                var g2 = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Font = Ui.Bold };
                content.Font = Ui.Body; content.Dock = DockStyle.Fill;
                g2.Controls.Add(content);
                return g2;
            }
            FlowLayoutPanel Col() => new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
            FlowLayoutPanel Row() => new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };

            // Frame generation: the switch and the game's rate (Auto = proposed from the game's recorded runs, or 1/3, 1/2, 2/3 of the display rate). The explanations
            // are tooltips.
            fgRateText.On = t => { foreach (var c in new Control[] { frameGen, fgRecommend }.Concat(fgRate.Controls.Cast<Control>())) tips.SetToolTip(c, t); };
            var fgCol = Col();
            var fgRow = Row();
            fgUnc.Margin = new Padding(14, 8, 0, 0);
            fgRow.Controls.Add(frameGen); fgRow.Controls.Add(fgRecommend); fgRow.Controls.Add(fgRate);
            fgCol.Controls.Add(fgRow);
            fgCol.Controls.Add(fgUncNote);
            body.Controls.Add(Group("Frame generation", fgCol));

            // Position and Picture side by side, each a short stack (the window stays narrow); the desktop mirror switch sits with Recenter, the Advanced picture
            // settings open full width below. Not one titled box per feature: the space goes where the controls are.
            var posCol = Col();
            var heightRow = Row();
            heightRow.Controls.Add(new Label { Text = "Eye height", AutoSize = true, Margin = new Padding(3, 7, 6, 0) });
            heightRow.Controls.Add(heightMode);
            heightBox.Margin = new Padding(8, 2, 2, 0);
            heightRow.Controls.Add(heightBox);
            heightRow.Controls.Add(new Label { Text = "m", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
            posCol.Controls.Add(heightRow);
            heightReport.Margin = new Padding(3, 0, 0, 2);
            posCol.Controls.Add(heightReport);
            var posRow = Row();
            recenter.Margin = new Padding(3, 3, 18, 3);
            posRow.Controls.Add(recenter);
            posRow.Controls.Add(new Label { Text = "Desktop mirror", AutoSize = true, Margin = new Padding(3, 8, 0, 0) });
            posRow.Controls.Add(mirror);
            posCol.Controls.Add(posRow);

            // Picture: the two settings people touch, stacked; the rest behind Advanced
            var pic = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Font = Ui.Body, Margin = new Padding(0) };
            var adv = new TableLayoutPanel { AutoSize = true, ColumnCount = 6, Font = Ui.Body };
            AddKnob(pic, "Gamma", 50, 200, AppSettings.DefaultGamma, () => settings.Gamma, v => settings.Gamma = v, "Above 1 is brighter.");
            AddKnob(pic, "Sharpening", 0, 100, 0, () => settings.Sharpening, v => settings.Sharpening = v);
            AddKnob(adv, "Brightness", -30, 30, 0, () => settings.Brightness, v => settings.Brightness = v);
            AddKnob(adv, "Contrast", -30, 30, 0, () => settings.Contrast, v => settings.Contrast = v);
            AddKnob(adv, "Saturation", -50, 50, 0, () => settings.Saturation, v => settings.Saturation = v, "Below 0: less vivid.");
            var reset = new Button { Text = "Defaults", AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            reset.Click += (s, e) => { foreach (var k in knobs) k.Bar.Value = (int)Math.Round(k.Def * 100); };
            tips.SetToolTip(reset, "Gamma 1.2, the rest neutral.");
            var picCol = Col();
            picCol.Controls.Add(pic);
            picCol.Controls.Add(reset);
            var pair = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
            pair.Controls.Add(Group("Picture", picCol));
            pair.Controls.Add(Group("Position", posCol));
            body.Controls.Add(pair);
            body.Controls.Add(Ui.Expander("Advanced", adv, false, () => Ui.Refit(this)));
            tips.SetToolTip(mirror, "Between games the headset shows the Windows desktop on a floating screen; your hand is the mouse (pinch = click). Off by default.");
            tips.SetToolTip(fgRecommend, "Auto: the rate is set from this game's recorded runs on this headset, the fastest one that stayed clean. Pick 1/3, 1/2 or 2/3 yourself to leave Auto.");
            tips.SetToolTip(recenter, "Forward becomes forward and your position the origin. Done at every game start; the recenter key (End by default) does the same while playing.");
            tips.SetToolTip(heightMode, "Auto: the headset's own floor estimate, right when it saw the room (start standing). Manual: the value you type (about 1.30 m seated).");
            tips.SetToolTip(heightBox, "Eye height above the floor, 0.5-2.2 m.");

            // debug and feedback: always visible, on or off (U3)
            var dbgRow = Row();
            dbgRow.Margin = new Padding(0, 8, 0, 0);
            var exportBtn = new Button { Text = "Export logs...", AutoSize = true };
            var openLogs = new Button { Text = "Open logs folder", AutoSize = true };
            exportStatus.Visible = exportLink.Visible = false;
            exportLink.LinkClicked += (s, e) => LogExport.Reveal(exportLink.Tag as string);
            exportBtn.Click += (s, e) => LogExport.Run(this, currentExe.Length > 0 ? Games.Stem(currentExe) : null, exportStatus, exportLink);
            openLogs.Click += (s, e) => { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", Paths.Logs); };
            debug.Margin = new Padding(3, 6, 12, 0);
            tips.SetToolTip(debug, "Writes files to logs\\debug\\ (costs about 4.6 ms per frame): turn it on to report a problem, off after.");
            // the main window (settings, game statistics, headset setup): starts the launcher, or brings the one that stepped aside for this window back
            var mainMenu = new Button { Text = "Main menu", AutoSize = true };
            mainMenu.Click += (s, e) =>
            {
                var exe = Path.Combine(Paths.Bin, "launcher.exe");
                try { if (File.Exists(exe)) Process.Start(exe); else MessageBox.Show(this, "bin\\launcher.exe is missing from this folder.", "AirFrameXR"); }
                catch (Exception ex) { footer.Text = "Cannot open the main menu: " + ex.Message; footer.ForeColor = Color.Firebrick; }
            };
            tips.SetToolTip(mainMenu, "Open the main window (settings, game statistics, restart headset setup). The streamer keeps running.");
            dbgRow.Controls.Add(debug); dbgRow.Controls.Add(exportBtn); dbgRow.Controls.Add(openLogs); dbgRow.Controls.Add(mainMenu);
            body.Controls.Add(dbgRow);
            var exportRow = Row();
            exportRow.Controls.Add(exportStatus); exportRow.Controls.Add(exportLink);
            footer.ForeColor = Color.DimGray;
            // one slot for the notices (export result, audio, the footer line): two lines are reserved, so they never move the window
            var notices = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), MinimumSize = new Size(0, 46) };
            notices.Controls.Add(exportRow); notices.Controls.Add(audioLine); notices.Controls.Add(footer);
            body.Controls.Add(notices);

            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            scroller.Controls.Add(body);
            Controls.Add(scroller);
            var hdr = Ui.CompactHeader("Integrated OpenXR renderer + streamer for ALVR client VR headsets");
            Controls.Add(hdr);
            Ui.AutoFitWindow(this, hdr, body, false, WithWorstCase);   // fixed size: only Advanced changes it


            debug.Checked = settings.Debug;
            debug.CheckedChanged += (s, e) =>
            {
                host.Send("debug " + (debug.Checked ? "on" : "off"));
                settings.Debug = debug.Checked;
                settings.Save();
            };
            heightBox.Value = (decimal)Math.Max(0.5, Math.Min(2.2, settings.Height > 0 ? settings.Height : AppSettings.DefaultHeight));
            UpdateHeightUi();
            heightMode.ValueChanged += (s, e) =>
            {
                if (heightUiFromCode) return;
                settings.HeightMode = heightMode.Value == 0 ? "auto" : "manual"; settings.Save();
                Log.Write("INFO", "eye height " + settings.HeightMode);
                UpdateHeightUi(); SendDisplay();
            };
            heightBox.ValueChanged += (s, e) => { settings.Height = (double)heightBox.Value; settings.Save(); if (!HeightAuto) SendDisplay(); };
            mirrorTimer.Tick += (s, e) => MirrorStart();
            frameGen.Checked = settings.FrameGen;
            mirror.Checked = settings.MirrorDesktop;
            mirror.CheckedChanged += (s, e) =>
            {
                settings.MirrorDesktop = mirror.Checked;
                MirrorSync();
                settings.Save();
                Log.Write("INFO", "desktop mirror " + (mirror.Checked ? "on" : "off"));
            };
            Action fgEnable = () => { fgRate.Enabled = frameGen.Checked && !fgUnc.Checked && !(fgRecommend.Checked && recommendationApplied); fgRecommend.Enabled = frameGen.Checked && !fgUnc.Checked; fgUnc.Enabled = frameGen.Checked; };
            fgEnableAll = fgEnable;
            frameGen.CheckedChanged += (s, e) =>
            {
                host.Send("frame_gen " + (frameGen.Checked ? "on" : "off"));   // the running streamer switches at the next boundary; the setting is read at start too
                settings.FrameGen = frameGen.Checked;
                settings.Save();
                fgEnable();
                Log.Write("INFO", "frame generation " + (frameGen.Checked ? "on" : "off"));
            };
            // "uncapped" is the tick; the slider keeps its own position (1/2 when the config holds uncapped) for when the tick goes off
            var savedRate = Array.IndexOf(FgRates, settings.FrameGenRate);
            fgRate.Value = savedRate >= 0 ? savedRate : 1;
            fgRateText.Text = FgRateText[fgRate.Value];
            fgUnc.Checked = settings.FrameGenRate == "uncapped";
            fgRecommend.Checked = settings.FrameGenRecommended;
            fgEnable();
            fgRate.ValueChanged += (s, e) =>
            {
                var rate = FgRates[fgRate.Value];
                fgRateText.Text = FgRateText[fgRate.Value];
                if (fgRateFromHost || fgUnc.Checked || rate == settings.FrameGenRate) return;   // uncapped: the slider only waits for the tick to go off
                host.Send("frame_gen_rate " + rate);
                settings.FrameGenRate = rate;
                settings.Save();
                Log.Write("INFO", "frame generation rate " + rate);
            };
            fgUnc.CheckedChanged += (s, e) =>
            {
                fgEnable();
                if (fgUncNote.Visible != fgUnc.Checked) { fgUncNote.Visible = fgUnc.Checked; if (IsHandleCreated) BeginInvoke((Action)(() => Ui.Refit(this))); }
            };
            fgUncNote.Visible = fgUnc.Checked;
            ConfigWatchStart();
            fgRecommend.CheckedChanged += (s, e) =>
            {
                settings.FrameGenRecommended = fgRecommend.Checked;
                settings.Save();
                Log.Write("INFO", "recommended rate " + (fgRecommend.Checked ? "on" : "off"));
                ApplyRecommendation(fgRecommend.Checked);
            };
            recenter.Click += (s, e) => { host.Send("recenter"); Log.Write("INFO", "recenter"); };


            // a blocking message (firewall, a setup or encode error in the footer) is shown in full: the window grows for it, once
            fwWarn.VisibleChanged += (s, e) => { if (IsHandleCreated) BeginInvoke((Action)(() => Ui.Refit(this))); };
            footer.TextChanged += (s, e) => { if (IsHandleCreated) BeginInvoke((Action)(() => Ui.Refit(this))); };
            host.OnSend += l => Sent.Add(l);
            host.OnEvent += (name, data) => BeginInvoke((Action)(() => HostEvent(name, data)));
            host.OnExit += code => BeginInvoke((Action)(() => HostExited(code)));
            Shown += (s, e) =>
            {
                StartHost();
                CheckFirewall();
                if (autoFind) { var t = new Timer { Interval = 3000 }; t.Tick += (a, b) => { t.Stop(); FindHeadset(true); }; t.Start(); }
            };
            Ui.ScreenshotWhenShown(this, 12000);
            FormClosing += (s, e) => { closing = true; MirrorSync(); footer.Text = "Stopping the streamer..."; Refresh(); host.Stop(); audio.Stop(); { var m = PowerPlan.Restore(); if (m != null) Log.Write("INFO", m); } Log.Write("INFO", "streamer.exe closed"); };
        }

        // The Advanced settings window is a separate process: it writes config\airframexr.json, and this timer hands the switches that act live to the running streamer
        // (the rate, the uncapped mode's cap and lag) and reloads the file, so that this window's own saves never put old values back over its edits.
        readonly Timer configWatch = new Timer { Interval = 1500 };

        // The headset line: red = not found, orange-yellow = available (ALVR is connecting to it, or the streamer saw its app on the network in the last 30 s: the mDNS
        // sighting repeats every ~11 s, but ALVR often dials a paired headset's address before any sighting, so its own connection_state in session.json counts too),
        // green = connected (the engine's client_connected: the stream is up, after the headset's stream-ready).
        enum Link { Red, Orange, Green }
        Link link = Link.Red;
        DateTime lastSeen = DateTime.MinValue;
        readonly Timer linkTimer = new Timer { Interval = 1000 };
        static bool AlvrConnecting()
        {
            try { if (Json.Get(Session.Load(), "client_connections") is Dictionary<string, object> cc) foreach (var v in cc.Values) if (v is Dictionary<string, object> c && c.TryGetValue("connection_state", out var st) && st as string == "Connecting") return true; } catch { }
            return false;
        }
        void SetLink(Link l, string text)
        {
            link = l;
            headset.Text = text;
            headset.ForeColor = l == Link.Green ? Color.SeaGreen : l == Link.Orange ? Color.DarkGoldenrod : Color.Firebrick;
        }
        void LinkTick()
        {
            if (link == Link.Green || closing || finding) return;
            var name = pairedName.Length > 0 ? $"{pairedName} ({pairedIp})" : "headset";
            if (AlvrConnecting()) { if (link != Link.Orange || !headset.Text.Contains("connecting")) SetLink(Link.Orange, $"Headset: detected - {name}, connecting..."); }
            else if (link == Link.Orange && (DateTime.UtcNow - lastSeen).TotalSeconds > 30) SetLink(Link.Red, $"Headset: not found - searching for {name} ...");
        }
        DateTime configSeen; string watchRate; double watchCap; string watchFov;
        /// the field-of-view override as the host command takes it: "off", or "on outer inner upper lower render_quality" (tangents, then 0..1)
        string FovLine() => settings.FovOverride && settings.FovFits(AppSettings.ReadClientFov()) && settings.FovOuter > 0 && settings.FovInner > 0 && settings.FovUpper > 0 && settings.FovLower > 0
            ? "on " + string.Join(" ", new[] { settings.FovOuter, settings.FovInner, settings.FovUpper, settings.FovLower }.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture))) + " " + settings.FovRenderQuality.ToString("0.00", CultureInfo.InvariantCulture)
            : "off";

        void ConfigWatchStart()
        {
            watchRate = settings.FrameGenRate; watchCap = settings.FrameGenCapFps; watchFov = FovLine();
            try { configSeen = File.GetLastWriteTimeUtc(Paths.Settings); } catch { }
            configWatch.Tick += (s, e) =>
            {
                DateTime w; try { w = File.GetLastWriteTimeUtc(Paths.Settings); } catch { return; }
                if (w == configSeen) return;
                configSeen = w;
                settings.Root = Json.Load(Paths.Settings);
                ForwardConfigChanges();
            };
            configWatch.Start();
        }

        /// What the Advanced window changed in config\airframexr.json that the running host takes live: the frame generation rate and cap, and the field of view the game is
        /// told (a game that starts later takes it). Only a value that differs from the last one sent is sent: nothing is repeated while the user does not touch a field.
        public void ForwardConfigChanges()
        {
            if (settings.FrameGenRate != watchRate)
            {
                watchRate = settings.FrameGenRate;
                host.Send("frame_gen_rate " + watchRate);
                fgRateFromHost = true;
                fgUnc.Checked = watchRate == "uncapped";
                var pos = Array.IndexOf(FgRates, watchRate);
                if (pos >= 0 && fgRate.Value != pos) fgRate.Value = pos;
                fgRateFromHost = false;
                Log.Write("INFO", "frame generation rate " + watchRate + " (Advanced settings)");
            }
            if (FovLine() != watchFov) { watchFov = FovLine(); host.Send("fov_override " + watchFov); Log.Write("INFO", "field of view override: " + watchFov + " (Advanced picture and FOV settings; the next game takes it)"); }
            if (settings.FrameGenCapFps != watchCap) { watchCap = settings.FrameGenCapFps; host.Send("frame_gen_cap " + watchCap.ToString("0.#", CultureInfo.InvariantCulture)); Log.Write("INFO", "frame generation cap " + watchCap + " fps (Advanced settings)"); }
        }

        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400 };

        // The desktop mirror is an OpenXR app of its own (bin\mirrordesktop.exe): it runs while the toggle is on, a headset is connected and no game runs. A game
        // starting takes the headset (the app leaves by itself when the shim signals it; this only makes sure it is gone), and the mirror returns when it ends.
        static bool IsMirrorApp(string exe) => Games.IsHidden(exe);
        bool mirrorInHost;
        // A game that ends is not always over: some start an OpenXR session for a moment (a console or launcher stage), end it, and open the real one seconds later. The mirror waits
        // this long after a game ends before it comes back, so it is not on the headset when the game returns (the mirror also waits by itself while another app has the runtime loaded).
        const int MirrorReturnDelayS = 5;
        DateTime mirrorNotBefore = DateTime.MinValue;
        Process mirrorProc; bool headsetConnected; int mirrorFails; DateTime mirrorStarted;
        readonly Timer mirrorTimer = new Timer { Interval = 1500 };
        void MirrorSync()
        {
            mirrorTimer.Stop();
            bool want = mirror.Checked && headsetConnected && !gameRunning && !closing && !finding && mirrorFails < 3;
            bool alive = mirrorProc != null && !mirrorProc.HasExited;
            if (!want && alive) { try { mirrorProc.Kill(); } catch { } mirrorProc = null; Log.Write("INFO", "desktop mirror stopped"); }
            else if (want && !alive) { mirrorTimer.Interval = (int)Math.Max(1500, (mirrorNotBefore - DateTime.Now).TotalMilliseconds); mirrorTimer.Start(); }   // a moment after a game ends or the headset connects: the previous session has to be gone
        }
        void MirrorStart()
        {
            mirrorTimer.Stop();
            if (!(mirror.Checked && headsetConnected && !gameRunning && !closing && !finding) || (mirrorProc != null && !mirrorProc.HasExited)) return;
            if (!File.Exists(Paths.MirrorDesktop)) { footer.Text = "bin\\mirrordesktop.exe is missing: the desktop mirror cannot start."; footer.ForeColor = Color.Firebrick; return; }
            try
            {
                var p = Process.Start(new ProcessStartInfo(Paths.MirrorDesktop) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Paths.Bin });
                p.EnableRaisingEvents = true;
                mirrorProc = p; mirrorStarted = DateTime.Now;
                p.Exited += (s, e) => BeginInvoke((Action)(() =>
                {
                    if (mirrorProc != p) return;
                    mirrorProc = null;
                    int code = 0; try { code = p.ExitCode; } catch { }
                    bool quick = (DateTime.Now - mirrorStarted).TotalSeconds < 8;
                    Log.Write(code == 0 || !quick ? "INFO" : "WARN", $"desktop mirror ended (code {code})");
                    if (code == 2) { mirrorFails = 3; footer.Text = "The desktop mirror cannot capture the screen (a locked or protected display): see logs\\mirrordesktop.log."; footer.ForeColor = Color.Firebrick; }
                    else if (quick && code != 0) mirrorFails++;
                    else mirrorFails = 0;
                    MirrorSync();
                }));
                Log.Write("INFO", "desktop mirror started");
            }
            catch (Exception e) { Log.Write("ERROR", "desktop mirror: " + e.Message); mirrorFails = 3; }
        }

        void NeedFind(bool on) { findHint.Visible = find.Visible = on; }

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
                        fwWarn.Text = st == FirewallState.Blocked ? "Windows Firewall blocks alvr_host.exe: the headset cannot connect."
                                    : "No Windows Firewall rule for alvr_host.exe in this folder: the headset may not connect.";
                        Log.Write(bad ? "WARN" : "INFO", "firewall: " + st);
                    }));
                }
                catch { }
            });
        }

        void AddKnob(TableLayoutPanel t, string name, int min, int max, double def, Func<double> get, Action<double> set, string tip = null)
        {
            var k = new Knob { Name = name, Def = def, Get = get, Set = set, Value = Ui.Line("0.00"),
                               Bar = new TrackBar { Minimum = min, Maximum = max, TickStyle = TickStyle.None, SmallChange = 5, LargeChange = 10, Width = 170, AutoSize = false, Height = 26, AccessibleName = name } };
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
        bool heightFromHeadset, heightUiFromCode;
        double reportedHeight;
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
                fgRateText.Text = $"No run recorded yet for {currentExe} on {headsetName}.";
            }
            else if (apply)
            {
                var pos = Array.IndexOf(FgRates, rec.Value.level);
                if (pos >= 0 && fgRate.Value != pos) fgRate.Value = pos;   // sends frame_gen_rate and saves, like a hand move
                recommendationApplied = true;
                fgRateText.Text = $"Auto for {currentExe}: {rec.Value.why}";
            }
            else
            {
                fgRateText.Text = $"Recorded for {currentExe}: {rec.Value.why}";
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
            UpdateHeightUi();
            SendDisplay();
        }

        // Auto (or any non-Apple headset, whose floor is right) sends 0 = the headset's own floor estimate; Manual sends the typed value
        bool HeightAuto => heightFromHeadset || settings.HeightMode == "auto";
        void UpdateHeightUi()
        {
            heightUiFromCode = true;
            heightMode.Enabled = !heightFromHeadset;
            heightMode.Value = HeightAuto ? 0 : 1;
            heightBox.Enabled = !HeightAuto;
            heightReport.Visible = HeightAuto && reportedHeight > 0;
            heightReport.Text = reportedHeight > 0 ? "headset reports " + reportedHeight.ToString("0.00", CultureInfo.InvariantCulture) + " m" : "";
            heightUiFromCode = false;
        }

        void SendDisplay()
        {
            string f(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            host.Send("gamma " + f(settings.Gamma));
            host.Send("height " + f(HeightAuto ? 0 : settings.Height));
            host.Send($"color {f(settings.Brightness)} {f(settings.Contrast)} {f(settings.Saturation)} {f(settings.Sharpening)}");
        }

        /// The virtual cable as the default output, the PC's output muted, the cable held at 100 %: what startairframexr.bat did, restored when the streamer closes.
        /// Before the host starts: ALVR's own mute_when_streaming mutes the captured device (the cable now), so the streamer switches it off in session.json.
        void StartAudioRoute()
        {
            var s = Session.Load();
            var enabled = Json.Bool(s, "session_settings.audio.game_audio.enabled", true);
            var part = Json.Bool(s, "session_settings.audio.game_audio.content.device.set", false) ? Convert.ToString(Json.Get(s, "session_settings.audio.game_audio.content.device.content.NameSubstring")) : "";
            audio.Report += AudioNote;
            var cfg = new AppSettings();
            var micOn = Json.Bool(s, "session_settings.audio.microphone.enabled", false);
            // the headset microphone and the cable route cannot coexist (ALVR refuses a microphone that points at the captured cable): the microphone wants process loopback
            var process = cfg.AudioCapture == "process" || micOn;
            if (micOn && cfg.AudioCapture != "process") Log.Write("INFO", "audio: the microphone is on, game audio is captured with process loopback (the cable is the microphone's)");
            if (micOn)
            {
                audio.StartMicrophone(true);
                if (AudioRoute.FindMicrophoneCable() == null && !Environment.GetCommandLineArgs().Contains("--test") && Ui.ScreenshotPath() == null) using (var f = new CableForm(true)) f.ShowDialog(this);
            }
            if (process)
            {
                // Windows' process loopback: ALVR captures the PC's audio itself (the host inherits this variable), nothing else to route
                Environment.SetEnvironmentVariable("AIRFRAMEXR_AUDIO_CAPTURE", "process");
                audio.StartMuteOnly(enabled, cfg.MuteSpeakers);
            }
            else
            {
                Environment.SetEnvironmentVariable("AIRFRAMEXR_AUDIO_CAPTURE", null);
                if (audio.Start(part, enabled, cfg.MuteSpeakers) && Json.Bool(s, "session_settings.audio.game_audio.content.mute_when_streaming", true))
                {
                    Json.Set(s, "session_settings.audio.game_audio.content.mute_when_streaming", false);
                    try { Json.Save(Paths.Session, s); Log.Write("INFO", "audio: ALVR's mute_when_streaming switched off (the streamer mutes the PC output itself)"); } catch (Exception e) { Log.Write("WARN", "audio: cannot write session.json: " + e.Message); }
                }
                if (audio.CableMissing && !Environment.GetCommandLineArgs().Contains("--test") && Ui.ScreenshotPath() == null) using (var f = new CableForm()) f.ShowDialog(this);
            }
            var plan = new AppSettings().PowerPlan;
            if (plan.Length > 0) System.Threading.Tasks.Task.Run(() => { var m = PowerPlan.Apply(plan); if (m != null) Log.Write("INFO", m); });
        }

        void AudioNote(string level, string text)
        {
            try { BeginInvoke((Action)(() => { if (!closing) SetAudioLine("Audio: " + text, level == "WARN" ? Color.DarkOrange : Color.DimGray); })); } catch { }
        }

        void StartHost()
        {
            headsetConnected = false; MirrorSync();
            var hs = Session.Headset(Session.Load());
            pairedName = hs?.Item2 ?? "";
            pairedIp = hs?.Item3 ?? "";
            SetLink(Link.Red, hs == null ? "Headset: none paired - click Find headset" : $"Headset: not found - searching for {pairedName} ({pairedIp}) ...");
            if (!linkTimer.Enabled) { linkTimer.Tick += (s, e) => LinkTick(); linkTimer.Start(); }
            NeedFind(true);
            var args = $"--install-dir \"{Paths.Dir}\" --shim --daemon --gui" + (debug.Checked ? " --debug" : "");
            StartAudioRoute();
            var err = host.Start(args);
            if (err != null)
            {
                MessageBox.Show(this, err, "AirFrameXR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log.Write("ERROR", err);
                return;
            }
            footer.Text = "Streamer running. Open ALVR on the headset.";
            Log.Write("INFO", "streamer started: alvr_host " + args);
        }

        /// The streamer owns UDP 9943 (headset discovery), so it stops while the dialog searches; a chosen headset is paired (saved address) and
        /// the streamer starts again either way.
        void FindHeadset(bool autoPick)
        {
            if (finding) return;
            if (gameRunning && !autoPick && MessageBox.Show(this, "A game is running. Searching restarts the streamer: the game loses the headset for a few seconds. Continue?",
                    "AirFrameXR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            finding = true;
            find.Enabled = false;
            closing = true;
            SetLink(Link.Red, "Headset: searching the network...");
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
                footer.Text = setupError ?? "The streamer reported a setup problem. Check logs\\AirFrameXR.log.";
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
            else footer.Text = $"The streamer exited (code {code}) several times. Check logs\\AirFrameXR.log.";
        }

        static string S(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        static double D(Dictionary<string, object> d, string k) { try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN; } catch { return double.NaN; } }
        static string F(double v, string fmt) => double.IsNaN(v) ? "  -" : v.ToString(fmt, CultureInfo.InvariantCulture);

        internal void HostEvent(string name, Dictionary<string, object> d)
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
                    SetLink(Link.Green, $"Headset: connected - {(n.Length > 0 ? n : pairedName)} ({(ip.Length > 0 ? ip : pairedIp)})");
                    NeedFind(false);
                    headsetConnected = true; MirrorSync();
                    if (ip.Length > 0 && ip != settings.HeadsetIp) { settings.HeadsetIp = ip; settings.Save(); }
                    break;
                case "client_disconnected":
                    headsetConnected = false; MirrorSync();
                    NeedFind(true);
                    lastSeen = DateTime.MinValue;
                    SetLink(Link.Red, $"Headset: disconnected - searching for {pairedName} ({pairedIp}) ...");
                    break;
                case "headset_seen":
                    // the streamer's discovery (mDNS) saw a headset: show it while nothing is connected; a paired headset whose
                    // address changed is dialled by the streamer itself, keep our note of its address in step
                    if (link != Link.Green)
                    {
                        lastSeen = DateTime.UtcNow;
                        var seenName = S(d, "hostname"); var seenIp = S(d, "ip");
                        var paired = pairedName.Length > 0 && (seenName == settings.HeadsetHost || seenIp == pairedIp);
                        SetLink(Link.Orange, paired ? $"Headset: detected - {pairedName} seen at {seenIp}, waiting for it to connect..." : $"Headset: detected - {seenName} seen at {seenIp}, not paired (click Find headset)");
                        if (paired && seenIp.Length > 0 && seenIp != settings.HeadsetIp) { settings.HeadsetIp = seenIp; settings.Save(); pairedIp = seenIp; }
                    }
                    break;
                case "game_started":
                    if (IsMirrorApp(S(d, "exe")))
                    {
                        // the desktop mirror is an OpenXR app like a game (the engine records it as one), but it is not played: no generation (it would only add latency to a
                        // static screen), and it must not count as a game for the mirror's own start/stop logic
                        mirrorInHost = true;
                        if (frameGen.Checked) host.Send("frame_gen off");
                        break;
                    }
                    game.Text = "Game: " + S(d, "exe");
                    gameRunning = true; MirrorSync();
                    currentExe = S(d, "exe");
                    lastSession = false;
                    statsBox.Text = "Streaming (live, updated every 10 s)";
                    ApplyRecommendation(fgRecommend.Checked);
                    break;
                case "game_ended":
                    if (IsMirrorApp(S(d, "exe")))
                    {
                        mirrorInHost = false;
                        if (settings.FrameGen) host.Send("frame_gen on");   // the user's choice comes back for the next game
                        break;
                    }
                    gameRunning = false; mirrorNotBefore = DateTime.Now.AddSeconds(MirrorReturnDelayS); MirrorSync();
                    game.Text = $"Game: none (last: {S(d, "exe")}, {F(D(d, "seconds") / 60, "0.0")} min)";
                    break;
                case "game_run":
                    if (IsMirrorApp(S(d, "exe"))) break;   // the mirror's record is kept by the engine like any game's; the Streaming box keeps the last game's numbers
                    // the streamer recorded the run: the Streaming box turns into the run's record and what will be proposed next time
                    // (no pop-up; the live numbers come back with the next game)
                    {
                        lastSession = true;
                        var mins = D(d, "seconds") / 60;
                        statsBox.Text = $"Streaming: last session, {S(d, "exe")}, {F(mins, "0.0")} min, {S(d, "headset")}";
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
                        var latBad = Latency.Absurd(D(net, "latency_ms"), D(net, "latency_outliers"), D(net, "latency_samples")) || Latency.Absurd(D(net, "latency_p95_ms"));
                        tLatency.Set(latBad ? "n/a" : F(D(net, "latency_ms"), "0") + " ms", latBad ? "not measured" : $"p95 {F(D(net, "latency_p95_ms"), "0")}", null, latBad ? Latency.Why : null);
                        tLoss.Set(F(D(net, "packets_lost"), "0"), "packets lost", D(net, "packets_lost") > 0 ? Color.Firebrick : (Color?)null);
                        stats.Text = $"Host repeats {F(D(d, "host_repeats_typ") * 100, "0.0")} %, skipped {F(D(d, "skipped") * 100, "0.0")} %." + (rec == null ? "" : $"  Next time: {(ri >= 0 ? names[ri] : rec.Value.level)}.");
                    }
                    break;
                case "status":
                    var c = d != null && d.TryGetValue("client", out var cc) ? cc as Dictionary<string, object> : null;
                    if (d != null && d.TryGetValue("headset", out var ho) && ho is Dictionary<string, object> hd) HeightSource(S(hd, "name"));
                    if (d != null && d.ContainsKey("fg_rate") && S(d, "fg_on") == "True")
                    {
                        // the rate in force (the slider and the uncapped tick follow the streamer, e.g. a rate set in the config file)
                        var hostRate = S(d, "fg_rate");
                        var pos = Array.IndexOf(FgRates, hostRate);
                        fgRateFromHost = true;
                        if (fgUnc.Checked != (hostRate == "uncapped")) fgUnc.Checked = hostRate == "uncapped";
                        if (pos >= 0 && fgRate.Value != pos) fgRate.Value = pos;
                        fgRateFromHost = false;
                    }
                    if (gameRunning)   // only a real game's windows are shown; a mirror or idle window never replaces the last session's numbers
                    {
                        tGame.Set(F(D(d, "app_fps"), "0.0") + " fps", "");
                        tStreamed.Set(F(D(d, "stream_fps"), "0.0") + " fps", "not streamed " + S(d, "frames_not_streamed"));
                        tShown.Set(F(double.IsNaN(D(c, "displayed_fps")) ? D(c, "client_fps") : D(c, "displayed_fps"), "0.0") + " fps", "");
                        tBitrate.Set(F(D(d, "mbps"), "0") + " Mbps", "");
                        var liveBad = Latency.Absurd(D(c, "total_latency_ms"), D(c, "latency_outliers"), D(c, "samples"));
                        tLatency.Set(liveBad ? "n/a" : F(D(c, "total_latency_ms"), "0") + " ms", liveBad ? "not measured" : $"enc {F(D(c, "encode_ms"), "0")} + net {F(D(c, "network_ms"), "0")} + dec {F(D(c, "decode_ms"), "0")}", null, liveBad ? Latency.Why : null);
                        tLoss.Set(S(d, "packets_lost"), "packets lost", D(d, "packets_lost") > 0 ? Color.Firebrick : (Color?)null);
                        stats.Text = "";
                    }
                    break;
                case "audio_note":   // what AudioRoute reports (also replayed by the streamer_events harness)
                    SetAudioLine("Audio: " + S(d, "text"), S(d, "level") == "WARN" ? Color.DarkOrange : Color.DimGray);   // already on the UI thread here
                    break;
                case "height_calibrated":
                    reportedHeight = D(d, "headset_y"); UpdateHeightUi();
                    break;
                case "encode_error":
                    footer.Text = "Encode error: " + S(d, "message");
                    break;
            }
        }

        /// uitest.exe: the fullest the window gets (long lines, the firewall warning, every tile filled), to audit the layout at any size and scale.
        /// Commands sent to the engine, in order (the test harness reads them; the live window ignores them).
        public readonly List<string> Sent = new List<string>();

        /// Operates a control the way a person would (harness/streamer_events.py): name = frame_gen | rate | auto | recenter | height_mode | height | gamma | sharpening | brightness | contrast | saturation | debug | mirror.
        public void UiAction(string name, string value)
        {
            bool on = value == "on";
            void bar(string n) { var k = knobs.First(x => x.Bar.AccessibleName == n); k.Bar.Value = (int)Math.Round(double.Parse(value, CultureInfo.InvariantCulture) * 100); }
            switch (name)
            {
                case "frame_gen": frameGen.Checked = on; break;
                case "rate": fgRate.Value = int.Parse(value); break;
                case "auto": fgRecommend.Checked = on; break;
                case "uncapped": fgUnc.Checked = on; break;
                case "recenter": recenter.PerformClick(); break;
                case "height_mode": heightMode.Value = value == "auto" ? 0 : 1; break;
                case "height": heightBox.Value = decimal.Parse(value, CultureInfo.InvariantCulture); break;
                case "gamma": bar("Gamma"); break;
                case "sharpening": bar("Sharpening"); break;
                case "brightness": bar("Brightness"); break;
                case "contrast": bar("Contrast"); break;
                case "saturation": bar("Saturation"); break;
                case "debug": debug.Checked = on; break;
                case "mirror": mirror.Checked = on; break;
                case "settings_fov":   // the replay harness: the Advanced window wrote the override ("off" or "on outer inner upper lower"), the watcher compares and forwards
                    {
                        var p = value.Split(' ');
                        string[] keys = { "video.fov_outer", "video.fov_inner", "video.fov_upper", "video.fov_lower", "video.fov_render_quality" };
                        global::AirFrameXR.Json.Set(settings.Root, "video.fov_override", p[0] == "on");
                        for (int i = 0; i < 5 && p[0] == "on" && i + 1 < p.Length; i++) global::AirFrameXR.Json.Set(settings.Root, keys[i], double.Parse(p[i + 1], CultureInfo.InvariantCulture));
                        ForwardConfigChanges();
                        break;
                    }
                case "settings_baseline": watchRate = settings.FrameGenRate; watchCap = settings.FrameGenCapFps; watchFov = FovLine(); break;   // the replay harness: what the streamer sent at its start
                case "settings_touch": ForwardConfigChanges(); break;   // the replay harness: the file changed but not a forwarded value
                case "advanced":
                    {
                        Button b = null; var todo = new Stack<Control>(); todo.Push(this);
                        while (todo.Count > 0 && b == null) { var c = todo.Pop(); if (c is Button bb && bb.AccessibleName == "Advanced") b = bb; foreach (Control k in c.Controls) todo.Push(k); }
                        b?.PerformClick();
                        break;
                    }
            }
        }
        public decimal HeightMin => heightBox.Minimum;
        public decimal HeightMax => heightBox.Maximum;

        void SetAudioLine(string text, Color c) { audioLine.Text = text; audioLine.ForeColor = c; audioLine.Visible = text.Length > 0; }
        public void DemoAudio(bool bad) => SetAudioLine(bad ? "Audio: cannot set CABLE Input (VB-Audio Virtual Cable) to 100 %. The headset's volume follows the PC's." : "Audio: CABLE Input (VB-Audio Virtual Cable) held at 100 % (was 40 %)", bad ? Color.DarkOrange : Color.DimGray);
        /// Runs `doFit` with everything that can appear showing at its longest (the firewall warning, Find headset, the audio and export lines, the height
        /// report, the long footer, the widest tiles), then puts the window's real state back: the window is sized for the fullest case once.
        void WithWorstCase(Action doFit)
        {
            var tiles = new[] { tGame, tStreamed, tShown, tBitrate, tLatency, tLoss };
            var labels = new Control[] { headset, game, runtime, heightReport, stats, statsBox };
            var shown = new Control[] { find, heightReport };
            var hidden = new Control[] { audioLine, exportStatus, exportLink };   // their line is reserved by the notices slot
            var hidBack = hidden.Select(c => (c, c.Visible)).ToList();
            var textBack = labels.Select(c => (c, c.Text, c.ForeColor)).ToList();
            var visBack = shown.Select(c => (c, c.Visible)).ToList();
            var tileBack = tiles.Select(t => (t, t.Value, t.Note, t.ValueColor)).ToList();
            try
            {
                headset.Text = "Headset: connected - Apple Vision Pro (192.168.100.123)";
                game.Text = "Game: Cyberpunk2077.exe (last: Cyberpunk2077.exe, 123.4 min)";
                heightReport.Text = "headset reports 1.62 m";
                stats.Text = "Host repeats 12.3 %, skipped 12.3 %.  Next time: 1/2.";
                statsBox.Text = "Streaming: last session, Cyberpunk2077.exe, 123.4 min, Apple Vision Pro";
                tGame.Set("144.4 fps", "average, slowest tenth 120.0"); tStreamed.Set("144.4 fps", "123.4 min at 1/2"); tShown.Set("144.4 fps", "repeated periods 12.3 %");
                tBitrate.Set("1000 Mbps", "average"); tLatency.Set("123 ms", "enc 12 + net 12 + dec 12"); tLoss.Set("1234", "packets lost");
                foreach (var c in shown) c.Visible = true;
                foreach (var c in hidden) c.Visible = false;
                doFit();
            }
            finally
            {
                foreach (var (c, t, col) in textBack) { c.Text = t; c.ForeColor = col; }
                foreach (var (c, v) in visBack) c.Visible = v;
                foreach (var (c, v) in hidBack) c.Visible = v;
                foreach (var (t, v, n, col) in tileBack) t.Set(v, n, col == Color.Black ? (Color?)null : col);
            }
        }

        public void DemoDebugOn() { debug.Checked = true; settings.Debug = false; settings.Save(); exportStatus.Text = "Saved to C:\\Users\\someone\\Documents\\AirFrameXR-logs-20261020-1200.zip"; exportStatus.ForeColor = Color.SeaGreen; exportStatus.Visible = exportLink.Visible = true; }

        public void ApplyDemoState(bool worstCase = true)
        {
            HostEvent("client_connected", new Dictionary<string, object> { { "name", "Apple Vision Pro" }, { "ip", "192.168.1.50" } });
            HostEvent("height_calibrated", new Dictionary<string, object> { { "headset_y", 1.62 } });
            headset.Text = worstCase ? "Headset: connected - Apple Vision Pro of a person with a long name (192.168.100.123)" : "Headset: connected - Apple Vision Pro (192.168.1.50)"; headset.ForeColor = Color.SeaGreen;
            runtime.Text = "GPU: NVIDIA GeForce RTX 5090 (Blackwell, driver 617.42)   \u00B7   OpenXR runtime: AirFrameXR (this folder)";
            if (!worstCase)
            {
                NeedFind(false);
                game.Text = "Game: Cyberpunk2077.exe";
                tGame.Set("44.8 fps", "average, slowest tenth 38.2"); tStreamed.Set("89.7 fps", "25.0 min at 1/2"); tShown.Set("89.1 fps", "repeated periods 1.0 %");
                tBitrate.Set("120 Mbps", "average"); tLatency.Set("57 ms", "enc 6 + net 9 + dec 14"); tLoss.Set("0", "packets lost");
                stats.Text = ""; frameGen.SetCheckedInstant(true);
                return;
            }
            NeedFind(true);
            fwWarn.Text = "Windows Firewall blocks alvr_host.exe: the headset cannot connect."; fwWarn.Visible = fwFix.Visible = true;
            game.Text = "Game: Cyberpunk2077.exe (Cyberpunk 2077) after 123 min";
            tGame.Set("144.4 fps", "average, slowest tenth 120.0"); tStreamed.Set("144.4 fps", "123.4 min at 1/2"); tShown.Set("144.4 fps", "repeated periods 12.3 %", Color.Firebrick);
            tBitrate.Set("1000 Mbps", "average"); tLatency.Set("123 ms", "enc 12 + net 12 + dec 12"); tLoss.Set("1234", "packets lost", Color.Firebrick);
            stats.Text = "Host repeats 12.3 %, skipped 12.3 %.  Next time: 1/2.";
            fgRateText.Text = FgRateText[2];
            footer.Text = "config\\session.json is missing: this folder is incomplete. Unzip the release again (keep your config\\ folder).";
            footer.ForeColor = Color.Firebrick;
        }

        [STAThread]
        static void Main()
        {
            Log.Component = "gui";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (new System.Threading.Mutex(true, "AirFrameXR_Runtime_Window", out bool first))
            {
                if (!first) { MessageBox.Show("AirFrameXR is already running.", "AirFrameXR"); return; }
                Log.Write("INFO", $"streamer.exe {Version.Text} start ({Paths.Dir})");
                if (!File.Exists(Paths.Host))
                {
                    MessageBox.Show("bin\\alvr_host.exe was not found. Unzip the whole AirFrameXR folder and start AirFrameXR.exe from it.", "AirFrameXR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                var check = SystemCheck.Run();
                if (check == null || !(check.TryGetValue("ok", out var ok) && ok is bool b && b))
                {
                    var why = check != null && check.TryGetValue("reason", out var r) ? r as string : "the system check failed (see logs)";
                    Log.Write("ERROR", "refusing to start: " + why);
                    MessageBox.Show("AirFrameXR cannot run on this PC: " + why, "AirFrameXR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                var testing = Ui.ScreenshotPath() != null || Environment.GetCommandLineArgs().Contains("--test");   // --test: no prompts (harness)
                if (!OpenXr.IsOurs() && !testing)
                {
                    var cur = OpenXr.ActiveRuntime();
                    var q = MessageBox.Show($"AirFrameXR is not the Windows OpenXR runtime (currently: {(cur.Length > 0 ? cur : "none")}).\n\n" +
                                            "Register this folder now? (asks for administrator rights; the OpenXR runtime button of the launcher puts the previous one back)",
                                            "AirFrameXR", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (q == DialogResult.Yes && !OpenXr.Register(null))
                        MessageBox.Show("Registration did not take effect. Use the OpenXR runtime button of the launcher (AirFrameXR.exe).", "AirFrameXR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                if (Session.Headset(Session.Load()) == null && !testing)
                {
                    if (MessageBox.Show("No headset is paired yet. Open configure.exe to set up and pair your headset? (Or answer No and use Find headset in the window.)", "AirFrameXR", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
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
