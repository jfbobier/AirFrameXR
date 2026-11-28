// configure.exe: the Settings card. No paired headset: the setup wizard (Wizard.cs; --wizard / --diagnose start it directly). Paired: two tabs, 1) the settings by hand, 2) optional
// benchmarks (encoder alone, then the network with the headset) that recommend a bitrate and show where the latency goes,
// 3) the settings AirFrameXR actually uses (everything else stays in config\session.json for hand editing).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AirFrameXR
{
    public class ConfigureForm : DpiForm
    {
        readonly AppSettings settings = new AppSettings();
        Dictionary<string, object> session = Session.Load();
        readonly Dictionary<string, object> check;
        // settings controls
        readonly NumericUpDown eyeW = Num(1024, Nvenc.MaxEyeWidth, 32), eyeH = Num(1024, Nvenc.MaxEyeHeight, 32), bitrate = Num(10, 1000, 10);
        readonly ComboBox fps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
        readonly RadioButton profFov = new RadioButton { Text = "Foveated (ALVR foveated encoding; recommended)", AutoSize = true };
        readonly RadioButton profFull = new RadioButton { Text = "Full frame + split encode (3x the pixels, needs a strong Wi-Fi link)", AutoSize = true };
        readonly NumericUpDown fcx = Dec(0.1m, 1m), fcy = Dec(0.1m, 1m), fsx = Dec(-1m, 1m), fsy = Dec(-1m, 1m), frx = Dec(1m, 10m), fry = Dec(1m, 10m);
        readonly Button idle = new Button { Width = 90, Height = 24, FlatStyle = FlatStyle.Flat };
        readonly CheckBox chromaOn = new CheckBox { Text = "Show my room where the screen is this colour", AutoSize = true };
        readonly CheckBox audio = new CheckBox { Text = "Stream game audio (Windows default output)", AutoSize = true };
        readonly CheckBox mute = new CheckBox { Text = "Mute the PC speakers while streaming", AutoSize = true };
        readonly ComboBox captureBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
        readonly CheckBox mic = new CheckBox { Text = "Headset microphone for games (VRChat voice; needs VB-CABLE)", AutoSize = true };
        readonly ComboBox planBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        List<PowerPlan.Plan> plans = new List<PowerPlan.Plan>();
        readonly ComboBox proto = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
        readonly NumericUpDown packet = Num(500, 4000, 100);
        readonly NumericUpDown haptics = Dec(0m, 5m);
        // benchmark
        readonly ListView benchList = new ListView { View = View.Details, FullRowSelect = true, Height = 190, Width = 640 };
        readonly Label benchStatus = Ui.Line("");
        readonly TextBox steps = new TextBox { Text = "100,150,200,250,300,350,400,500", Width = 260 };
        readonly NumericUpDown stepSecs = Num(4, 30, 1);
        readonly Button applyBitrate = new Button { Text = "Use recommended bitrate", AutoSize = true, Enabled = false };
        HostProcess bench;
        int recommended;

        static NumericUpDown Num(int min, int max, int inc) => new NumericUpDown { Minimum = min, Maximum = max, Increment = inc, Width = 80 };
        static NumericUpDown Dec(decimal min, decimal max) => new NumericUpDown { Minimum = min, Maximum = max, Increment = 0.05m, DecimalPlaces = 2, Width = 80 };

        public ConfigureForm(Dictionary<string, object> check)
        {
            this.check = check;
            Text = "AirFrameXR \u2013 Settings";
            Icon = Ui.AppIcon();
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96F, 96F);   // the pixel sizes below are designed at 96 dpi; without this nothing scales and only the fonts grow
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            BackColor = Color.White;
            Width = 920; Height = 860;
            Ui.FitToWorkArea(this);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            // two tabs (the launcher's one Settings card): the settings by hand, or let the tests adjust them; pairing is the wizard's (a button on the second tab)
            tabs.TabPages.Add(SettingsTab());
            tabs.TabPages.Add(BenchTab());
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var save = new Button { Text = "Save settings", AutoSize = true, Font = Ui.Bold };
            save.Click += (s, e) => SaveSettings(true);
            var launch = new Button { Text = "Save and start AirFrameXR", AutoSize = true };
            launch.Click += (s, e) => { if (SaveSettings(false)) { System.Diagnostics.Process.Start(Paths.Runtime); Close(); } };
            var import = new Button { Text = "Import ALVR settings...", AutoSize = true };
            import.Click += (s, e) => { if (AlvrImport.Run(this)) { session = Session.Load(); LoadSettings(); MessageBox.Show(this, "Imported. The settings tab shows what was read from it.", Text); } };
            bottom.Controls.Add(launch);
            bottom.Controls.Add(save);
            bottom.Controls.Add(import);
            Controls.Add(tabs);
            Controls.Add(bottom);
            LoadSettings();
            var shot = Ui.ScreenshotPath();
            if (shot != null && shot.Contains("settings")) Shown += (s, e) => tabs.SelectedIndex = 0;
            if (shot != null && (shot.Contains("bench") || shot.Contains("auto"))) Shown += (s, e) => tabs.SelectedIndex = 1;
            Ui.ScreenshotWhenShown(this, 4000);
            FormClosing += (s, e) => { bench?.Stop(1000); };
        }

        // ---------------------------------------------------------------- 2. settings
        TabPage SettingsTab()
        {
            var t = new TabPage("Manual settings") { BackColor = Color.White, Padding = new Padding(10), AutoScroll = true };
            var g = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
            void row(string label, Control c) { g.Controls.Add(Ui.Line(label)); g.Controls.Add(c); }
            void section(string title) { var l = Ui.Line(title, Ui.Big); l.ForeColor = Ui.Accent; l.Margin = new Padding(3, 12, 3, 3); g.Controls.Add(l); g.SetColumnSpan(l, 2); }
            Control flow(params Control[] cs) { var f = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) }; f.Controls.AddRange(cs); return f; }

            section("Headset");
            row("Paired headset", headsetName);
            row("IP address", headsetIp);
            var ipNote = Ui.Line("Type it when the headset is not found by itself (another network, a VPN, the Quest over USB: 127.0.0.1). The headset app must be open."); ipNote.ForeColor = Color.DimGray; ipNote.MaximumSize = new Size(560, 0);
            row("", ipNote);
            section("Video");
            sizeAuto.Click += (s, e) => SizeAutomatic();
            row("Resolution per eye", flow(eyeW, Ui.Line("x"), eyeH));
            var otherHeadset = AppSettings.KindOf(settings.HeadsetName) is HeadsetKind kn && (kn == HeadsetKind.Quest || kn == HeadsetKind.Other);
            var eyeNote = Ui.Line(otherHeadset ? $"At most {Nvenc.MaxEyeWidth} wide: the encoder's limit." : $"Vision Pro default 3648 x 3200, Apple's panel; at most {Nvenc.MaxEyeWidth} wide: the encoder's limit."); eyeNote.ForeColor = Color.DimGray; eyeNote.MaximumSize = new Size(560, 0);   // on a line of its own: beside the boxes the row was wider than the tab
            row("", eyeNote);
            sizeNote.ForeColor = Color.DimGray; sizeNote.MaximumSize = new Size(560, 0);
            row("", flow(sizeAuto, sizeNote));
            // each headset's own rates (1.5.2): the Vision Pro 90 / 96 / 100, the Quest 72 / 80 / 90 / 120 (it was offered the Vision Pro's: ALVR fell back to 90 on 100)
            var kindRates = AppSettings.KindOf(settings.HeadsetName);
            fps.Items.AddRange(kindRates == HeadsetKind.Quest ? new object[] { "72", "80", "90", "120" }
                             : kindRates == HeadsetKind.Other ? new object[] { "72", "80", "90", "96", "100", "120" }
                             : new object[] { "90", "96", "100" });
            row("Refresh rate (Hz)", fps);
            row("Bitrate (Mbps)", flow(bitrate, Ui.Line("constant; the Settings assistance tab can find yours")));
            var prof = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
            Ui.Exclusive(profFov, profFull);
            prof.Controls.Add(profFov);
            prof.Controls.Add(profFull);
            row("Encoding", prof);
            row("Foveation center size", flow(fcx, fcy));
            row("Foveation center shift", flow(fsx, fsy));
            row("Foveation edge ratio", flow(frx, fry, Ui.Line("(higher = fewer pixels at the edges)")));
            idle.Click += (s, e) =>
            {
                using (var cd = new ColorDialog { Color = idle.BackColor, FullOpen = true })
                    if (cd.ShowDialog(this) == DialogResult.OK) SetIdle(cd.Color);
            };
            var idleReset = new Button { Text = "Recommended (#00FF00)", AutoSize = true };
            idleReset.Click += (s, e) => SetIdle(ColorTranslator.FromHtml("#" + Chroma.DefaultRgb));
            chromaOn.CheckedChanged += (s, e) => { idle.Enabled = idleReset.Enabled = chromaOn.Checked; };
            row("Chroma-key passthrough", chromaOn);
            row("Key colour (also the idle screen)", flow(idle, idleReset));
            var remind = Ui.Line("Optional, off by default (the desktop mirror shows between games instead). #00FF00 is recommended. The headset app takes the colour from the streamer (the AirFrameXR clients and the Quest's ALVR app do), except the stock ALVR app on the Vision Pro (App Store), whose key is fixed: keep it off there unless you set the same colour in an ALVR app that has the picker.");
            remind.MaximumSize = new Size(560, 0);   // wraps inside its column
            remind.ForeColor = Color.DimGray;
            g.Controls.Add(new Label()); g.Controls.Add(remind);

            section("Audio");
            g.Controls.Add(new Label()); g.Controls.Add(audio);
            g.Controls.Add(new Label()); g.Controls.Add(mute);
            captureBox.Items.AddRange(new object[] { "Process loopback (no virtual cable needed)", "Virtual cable (VB-CABLE)" });
            row("Game audio capture", captureBox);
            g.Controls.Add(new Label()); g.Controls.Add(mic);
            mic.Click += (s, e) => { if (mic.Checked && AudioRoute.FindMicrophoneCable() == null) using (var f = new CableForm(true)) { f.ShowDialog(this); if (AudioRoute.FindMicrophoneCable() == null) mic.Checked = false; } };

            section("System");
            row("Windows power plan while streaming", planBox);
            var planNote = Ui.Line("The streamer switches to this plan when it starts and puts the previous one back when it closes.");
            planNote.ForeColor = Color.DimGray;
            planNote.MaximumSize = new Size(560, 0);   // wraps inside its column
            g.Controls.Add(new Label()); g.Controls.Add(planNote);

            section("Network and controllers");
            proto.Items.AddRange(new object[] { "Udp", "Tcp" });
            row("Stream protocol", flow(proto, Ui.Line("UDP is what the headset normally uses")));
            row("Packet size (bytes)", packet);
            row("Haptics strength", haptics);

            var note = Ui.Line("Everything else (ALVR's advanced options) stays in config\\session.json.");
            note.ForeColor = Color.DimGray;
            g.Controls.Add(new Label()); g.Controls.Add(note);
            t.Controls.Add(g);
            return t;
        }

        void SetIdle(Color c) { idle.BackColor = c; idle.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; idle.ForeColor = c.GetBrightness() > 0.5 ? Color.Black : Color.White; }

        const string V = "session_settings.video.";

        readonly Button sizeAuto = new Button { Text = "Automatic", AutoSize = true, AccessibleName = "Stream size from the bitrate" };
        readonly Label sizeNote = new Label { AutoSize = true };

        /// the stream size follows the bitrate again (the foveation numbers and the render choice are recomputed for it)
        void SizeAutomatic()
        {
            settings.StreamSizeManual = false; settings.Save();
            PipelineModel.PictureEdgesFoveation.ApplyToFiles(settings, true);
            session = Session.Load(); LoadSettings();
        }

        void ShowSizeNote()
        {
            sizeNote.Text = settings.StreamSizeManual ? "Typed: this size is kept whatever the bitrate; the foveation is computed for it. Automatic gives the size the bitrate pays for." : "Automatic: the size the bitrate pays for (a Vision Pro). Typing another size here keeps it.";
            sizeAuto.Enabled = settings.StreamSizeManual;
        }

        readonly TextBox headsetIp = new TextBox { Width = 140, AccessibleName = "Headset IP address" };
        readonly Label headsetName = Ui.Line("", Ui.Bold);

        void LoadSettings()
        {
            var s = session;
            var hs = Session.Headset(s);
            headsetName.Text = hs == null ? "none" : $"{hs.Item2} ({hs.Item1})";
            headsetIp.Text = hs?.Item3 ?? "";
            ShowSizeNote();
            eyeW.Value = Clamp(eyeW, Json.Num(s, V + "transcoding_view_resolution.Absolute.width", 3648));
            eyeH.Value = Clamp(eyeH, Json.Num(s, V + "transcoding_view_resolution.Absolute.height.content", 3200));
            var f = ((int)Json.Num(s, V + "preferred_fps", 90)).ToString(CultureInfo.InvariantCulture);
            fps.SelectedItem = fps.Items.Contains(f) ? f : "90";
            bitrate.Value = Clamp(bitrate, Json.Num(s, V + "bitrate.mode.ConstantMbps", 250));
            (settings.EncodeProfile == "full-split" ? profFull : profFov).Checked = true;
            fcx.Value = Clamp(fcx, Json.Num(s, V + "foveated_encoding.content.center_size_x", 0.45));
            fcy.Value = Clamp(fcy, Json.Num(s, V + "foveated_encoding.content.center_size_y", 0.4));
            fsx.Value = Clamp(fsx, Json.Num(s, V + "foveated_encoding.content.center_shift_x", 0.4));
            fsy.Value = Clamp(fsy, Json.Num(s, V + "foveated_encoding.content.center_shift_y", 0.1));
            frx.Value = Clamp(frx, Json.Num(s, V + "foveated_encoding.content.edge_ratio_x", 4));
            fry.Value = Clamp(fry, Json.Num(s, V + "foveated_encoding.content.edge_ratio_y", 5));
            // 1.4-foundation F4: with a mask profile in force, the mask's foveation or none; on the Vision Pro the numbers are the compiled ones, read-only
            foveationComputed = MaskChoice.Apply(settings, MaskChoice.Profile(settings), profFov, profFull, "Foveated (ALVR foveated encoding; recommended)",
                "Full frame + split encode (3x the pixels, needs a strong Wi-Fi link)", new[] { fcx, fcy, fsx, fsy, frx, fry });
            chromaOn.Checked = Chroma.Enabled(s);
            try { SetIdle(ColorTranslator.FromHtml("#" + Chroma.Rgb(s))); } catch { SetIdle(Color.FromArgb(0, 255, 0)); }
            idle.Enabled = chromaOn.Checked;
            audio.Checked = Json.Bool(s, "session_settings.audio.game_audio.enabled", true);
            mute.Checked = settings.MuteSpeakers;
            captureBox.SelectedIndex = settings.AudioCapture == "device" ? 1 : 0;
            mic.Checked = Json.Bool(s, "session_settings.audio.microphone.enabled", false);
            plans = PowerPlan.List();
            planBox.Items.Clear(); planBox.Items.Add("(leave unchanged)");
            foreach (var p in plans) planBox.Items.Add(p.Name);
            var curPlan = plans.FindIndex(p => string.Equals(p.Guid, settings.PowerPlan, StringComparison.OrdinalIgnoreCase));
            planBox.SelectedIndex = curPlan >= 0 ? curPlan + 1 : 0;
            proto.SelectedItem = Json.Str(s, "session_settings.connection.stream_protocol.variant", "Udp") == "Tcp" ? "Tcp" : "Udp";
            packet.Value = Clamp(packet, Json.Num(s, "session_settings.connection.packet_size", 1400));
            haptics.Value = Clamp(haptics, Json.Num(s, "session_settings.headset.controllers.content.haptics.content.intensity_multiplier", 1));
        }

        static decimal Clamp(NumericUpDown n, double v) => Math.Max(n.Minimum, Math.Min(n.Maximum, (decimal)v));
        bool foveationComputed;   // 1.4-foundation F4 (MaskChoice)

        bool SaveSettings(bool confirm)
        {
            session = Session.Load(); // keep anything edited meanwhile (e.g. pairing)
            var s = session;
            int w = (int)eyeW.Value / 32 * 32, h = (int)eyeH.Value; // ALVR aligns widths to 32
            var tooBig = Nvenc.CheckEye(w, h);
            if (tooBig != null) { MessageBox.Show(this, tooBig, Text); return false; }
            // the density reference, the fit, both resolution keys and ALVR's negotiated cache, as the wizard's panel does (until 1.3.4 this window wrote the two resolution keys only:
            // the FOV card kept showing the old size and the fit tick wrote the old reference back into the session, a user's report of 2026-11-05)
            {   // a size the user typed is theirs: it is kept (the size no longer follows the bitrate; the foveation is still computed for it) and it is the declared resolution of the game's render choice
                var was = PipelineModel.StreamEyeExpected(s);
                if (was != null && (was[0] != w || was[1] != h)) { settings.StreamSizeManual = true; settings.RenderReferenceEye = new[] { w, h }; }
            }
            var hsNow = Session.Headset(s); var ipTyped = headsetIp.Text.Trim();
            if (hsNow != null && ipTyped.Length > 0 && ipTyped != hsNow.Item3)
            {
                if (!System.Net.IPAddress.TryParse(ipTyped, out _)) { MessageBox.Show(this, "\"" + ipTyped + "\" is not an IP address (for example 192.168.1.30).", Text); return false; }
                Session.Pair(s, hsNow.Item1, hsNow.Item2, ipTyped);   // the same headset at another address
                settings.HeadsetIp = ipTyped;
                Log.Write("INFO", $"headset address typed: {ipTyped}");
            }
            PipelineModel.SaveEyeChoice(s, settings, w, h);
            var rate = double.Parse((string)fps.SelectedItem, CultureInfo.InvariantCulture);
            Json.Set(s, V + "preferred_fps", rate);
            Json.Set(s, "openvr_config.refresh_rate", (int)rate);
            Json.Set(s, "openvr_config.enable_foveated_encoding", profFov.Checked);
            Json.Set(s, V + "bitrate.mode.variant", "ConstantMbps");
            Json.Set(s, V + "bitrate.mode.ConstantMbps", (int)bitrate.Value);
            Json.Set(s, V + "foveated_encoding.enabled", profFov.Checked);
            if (!foveationComputed)   // 1.4-foundation F4: the compiled numbers shown with a mask are never the user's, and the golden master never takes them
            {
                {   // numbers that differ from what the session held are the user's: the foveation computed from the picture edges leaves them alone until a picture-edges choice is made again
                    var was = new[] { "center_size_x", "center_size_y", "center_shift_x", "center_shift_y", "edge_ratio_x", "edge_ratio_y" }.Select((k, i) => Json.Num(s, V + "foveated_encoding.content." + k, new[] { 0.45, 0.4, 0.4, 0.1, 4, 5 }[i])).ToArray();
                    var now = new[] { fcx.Value, fcy.Value, fsx.Value, fsy.Value, frx.Value, fry.Value }.Select(x => (double)x).ToArray();
                    if (Enumerable.Range(0, 6).Any(i => Math.Abs(was[i] - now[i]) > 0.0005)) settings.FoveationManual = true;
                }
                Json.Set(s, V + "foveated_encoding.content.center_size_x", (double)fcx.Value);
                Json.Set(s, V + "foveated_encoding.content.center_size_y", (double)fcy.Value);
                Json.Set(s, V + "foveated_encoding.content.center_shift_x", (double)fsx.Value);
                Json.Set(s, V + "foveated_encoding.content.center_shift_y", (double)fsy.Value);
                Json.Set(s, V + "foveated_encoding.content.edge_ratio_x", (double)frx.Value);
                Json.Set(s, V + "foveated_encoding.content.edge_ratio_y", (double)fry.Value);
            }
            Json.Set(s, "session_settings.audio.game_audio.enabled", audio.Checked);
            settings.MuteSpeakers = mute.Checked;
            settings.AudioCapture = captureBox.SelectedIndex == 1 ? "device" : "process";
            Json.Set(s, "session_settings.audio.microphone.enabled", mic.Checked);
            settings.PowerPlan = planBox.SelectedIndex > 0 && planBox.SelectedIndex <= plans.Count ? plans[planBox.SelectedIndex - 1].Guid : (planBox.SelectedIndex < 0 ? settings.PowerPlan : "");
            Json.Set(s, "session_settings.connection.stream_protocol.variant", (string)proto.SelectedItem);
            Json.Set(s, "session_settings.connection.packet_size", (int)packet.Value);
            Json.Set(s, "session_settings.headset.controllers.content.haptics.content.intensity_multiplier", (double)haptics.Value);
            Chroma.Apply(s, settings, chromaOn.Checked, $"{idle.BackColor.R:x2}{idle.BackColor.G:x2}{idle.BackColor.B:x2}");
            try { Session.Save(s); }
            catch (Exception e) { MessageBox.Show(this, "Cannot write config\\session.json: " + e.Message, Text); return false; }
            settings.EncodeProfile = profFull.Checked ? "full-split" : "foveated";
            settings.Save();
            PipelineModel.PictureEdgesFoveation.ApplyToFiles(settings, false);   // a new bitrate or rate resizes the stream (and its foveation) now, as it will at the next start; hand-set sizes stay
            Log.Write("INFO", $"settings saved: {w}x{h}/eye @ {fps.SelectedItem} Hz, {bitrate.Value} Mbps, {settings.EncodeProfile}, idle #{settings.IdleRgb}, audio {audio.Checked}, {proto.SelectedItem}");
            if (confirm) MessageBox.Show(this, "Saved. The changes apply the next time AirFrameXR starts.", Text);
            return true;
        }

        // ---------------------------------------------------------------- 3. benchmark
        TabPage BenchTab()
        {
            var t = new TabPage("Settings assistance") { BackColor = Color.White, Padding = new Padding(10) };
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0) };   // room for the vertical scroll bar: without it the filled column is as wide as the whole panel and a sideways bar appears
            p.Controls.Add(Ui.Line("Let the tests set it up", Ui.Big));
            p.Controls.Add(Ui.Line("Check your setup and get suggestions with the settings as they are, or run the setup wizard again to pair another headset and let the tests propose the settings. Either one closes this window and needs the headset app open."));
            var wizRow = new FlowLayoutPanel { AutoSize = true };
            var diag = new Button { Text = "Check my setup", AutoSize = true, Font = Ui.Bold };
            diag.Click += (s, e) => OpenWizard("--diagnose");
            var again = new Button { Text = "Run the setup wizard again", AutoSize = true };
            again.Click += (s, e) => OpenWizard("--wizard");
            wizRow.Controls.Add(diag); wizRow.Controls.Add(again);
            p.Controls.Add(wizRow);
            p.Controls.Add(Ui.Line("Single tests", Ui.Big));
            p.Controls.Add(Ui.Line("Encoder test (no headset needed): how long NVENC takes per frame with each encoding.", Ui.Bold));
            var enc = new Button { Text = "Run encoder test", AutoSize = true };
            enc.Click += (s, e) => RunEncoderTest();
            p.Controls.Add(enc);
            p.Controls.Add(Ui.Line("Quality benchmark (no headset, ~1.5 min): the benchmark scene through foveation + NVENC at ~30 settings, picture vs time.", Ui.Bold));
            var qb = new Button { Text = "Run quality benchmark", AutoSize = true };
            qb.Click += (s, e) => RunQualityTest();
            p.Controls.Add(qb);
            var credit = Ui.Line("Scene: \"Littlest Tokyo\" by Glen Fox (glenatron), CC-BY-4.0, shown in a room tiled with its own texture (see README.txt).");
            credit.ForeColor = Color.DimGray;
            p.Controls.Add(credit);
            p.Controls.Add(Ui.Line("Network test (headset paired and the headset app open): random frames at rising bitrates; the headset shows noise.", Ui.Bold));
            var f = new FlowLayoutPanel { AutoSize = true };
            f.Controls.Add(Ui.Line("Bitrates (Mbps):")); f.Controls.Add(steps);
            f.Controls.Add(Ui.Line("seconds per step:")); stepSecs.Value = 8; f.Controls.Add(stepSecs);
            p.Controls.Add(f);
            var net = new Button { Text = "Run network test", AutoSize = true };
            net.Click += (s, e) => RunNetworkTest();
            p.Controls.Add(net);
            benchList.Width = 840;
            foreach (var c in new[] { ("Test", 200), ("Mbps", 60), ("fps", 50), ("lost/s", 60), ("total ms", 70), ("encode ms", 75), ("network ms", 80), ("decode ms", 75), ("result", 160) })
                Ui.Col(benchList, c.Item1, c.Item2);
            p.Controls.Add(benchList); Ui.FitWidth(benchList, 840);
            Ui.FitColumns(benchList);
            p.Controls.Add(benchStatus);
            applyBitrate.Click += (s, e) =>
            {
                bitrate.Value = Clamp(bitrate, recommended);
                SaveSettings(false);
                MessageBox.Show(this, $"Bitrate set to {recommended} Mbps and saved.", Text);
            };
            p.Controls.Add(applyBitrate);
            var help = Ui.Line("Latency = what ALVR measures: total (tracking sent -> frame shown) = game + encode + network + decode + headset queues.");
            help.ForeColor = Color.DimGray;
            p.Controls.Add(help);
            t.Controls.Add(p);
            return t;
        }

        /// The wizard is configure.exe itself in its other modes: this window gives way to it.
        void OpenWizard(string arg)
        {
            if (BenchBusy()) return;
            try { System.Diagnostics.Process.Start(Application.ExecutablePath, arg); Close(); }
            catch (Exception e) { MessageBox.Show(this, "Cannot start the wizard: " + e.Message, Text); }
        }

        static string Fmt(Dictionary<string, object> d, string k, string f)
        {
            try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString(f, CultureInfo.InvariantCulture) : "-"; }
            catch { return "-"; }
        }

        bool BenchBusy() { if (bench != null && bench.Running) { MessageBox.Show(this, "A test is already running.", Text); return true; } return false; }

        void RunEncoderTest()
        {
            if (BenchBusy()) return;
            var profiles = new Queue<string>(new[] { "foveated", "full-split" });
            benchStatus.Text = "Encoder test running...";
            void next()
            {
                if (profiles.Count == 0)
                {
                    if (!benchStatus.Text.StartsWith("Encoder test failed")) benchStatus.Text = "Encoder test done. 'fits' = p95 under 90% of the frame time.";
                    return;
                }
                var prof = profiles.Dequeue();
                bench = new HostProcess();
                bench.OnEvent += (n, d) => BeginInvoke((Action)(() =>
                {
                    if (n == "setup_error") { benchStatus.Text = $"Encoder test failed: {d?["message"]}"; profiles.Clear(); return; }
                    if (n != "bench" || d == null) return;
                    var fits = d.TryGetValue("fits", out var ff) && ff is bool fb && fb;
                    var item = new ListViewItem(new[] { $"encoder {prof} {d["width"]}x{d["height"]}", "", Fmt(d, "fps", "0"), "", "", $"{Fmt(d, "p50_ms", "0.0")}/{Fmt(d, "p95_ms", "0.0")}", "", "", fits ? "fits" : "too slow" });
                    item.ForeColor = fits ? Color.SeaGreen : Color.Firebrick;
                    benchList.Items.Add(item);
                }));
                bench.OnExit += c => BeginInvoke((Action)next);
                var err = bench.Start($"--install-dir \"{Paths.Dir}\" --live --bench 300 --noise-temporal --encode-profile {prof}");
                if (err != null) benchStatus.Text = err;
            }
            next();
        }

        static Dictionary<string, object> Sub(Dictionary<string, object> d, string k) =>
            d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;

        // benchmark phase 2 (alvr_host --benchmark-quality): rows per configuration, the three proposals at the end. The proposals
        // are saved in config\benchmark_quality.json; applying them to the settings comes with the headset phase.
        void RunQualityTest()
        {
            if (BenchBusy()) return;
            if (!File.Exists(Path.Combine(Paths.Dir, "bench", "littlest_tokyo.vab"))) { MessageBox.Show(this, "bench\\littlest_tokyo.vab is missing from this folder.", Text); return; }
            SaveSettings(false);
            benchStatus.Text = "Quality benchmark: starting...";
            int n = 0;
            bench = new HostProcess();
            bench.OnEvent += (ev, d) => BeginInvoke((Action)(() =>
            {
                switch (ev)
                {
                    case "bq_start": benchStatus.Text = $"Quality benchmark on {d?["gpu"]} at {Fmt(d, "bitrate_mbps", "0")} Mbps..."; break;
                    case "bq_result":
                        n++;
                        var ok = d != null && d.TryGetValue("realtime", out var rt) && rt is bool rb && rb;
                        var ps = Sub(d, "psnr"); var em = Sub(d, "encode_ms");
                        var err = d != null && d.TryGetValue("error", out var e2) ? e2 as string : "";
                        var item = new ListViewItem(new[] { $"{d?["label"]}", Fmt(d, "produced_mbps", "0"), "", "", "", $"{Fmt(em, "p50", "0.0")}/{Fmt(em, "p95", "0.0")}", "", "",
                            string.IsNullOrEmpty(err) ? $"{Fmt(ps, "weighted", "0.0")} dB {(ok ? "real time" : "too slow")}" : "not supported" });
                        item.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
                        benchList.Items.Add(item);
                        benchList.EnsureVisible(benchList.Items.Count - 1);
                        benchStatus.Text = $"Quality benchmark: {n} configurations measured...";
                        break;
                    case "bq_summary":
                        var pr = Sub(d, "proposals");
                        string line(string k, string name)
                        {
                            var v = Sub(pr, k);
                            return v == null ? $"{name}: none" : $"{name}: {v["label"]} ({Fmt(Sub(v, "psnr"), "weighted", "0.0")} dB, encode {Fmt(Sub(v, "encode_ms"), "p95", "0.0")} ms)";
                        }
                        var clk = Sub(d, "gpu_clocks");
                        var stable = clk == null || !(clk.TryGetValue("stable", out var st) && st is bool sb && !sb);
                        benchStatus.Text = string.Join("\n", line("lowest_latency", "Lowest latency"), line("recommended", "Recommended"), line("best_quality", "Best quality"),
                            stable ? "Saved to config\\benchmark_quality.json (applying a proposal comes with the next version)." : "WARNING: the GPU clock changed during the test (laptop on battery? another GPU app?): run it again.");
                        Log.Write("INFO", "quality benchmark: " + benchStatus.Text.Replace("\n", " | "));
                        break;
                    case "bq_error": benchStatus.Text = $"Quality benchmark failed: {d?["message"]}"; break;
                    case "setup_error": benchStatus.Text = $"Quality benchmark failed: {d?["message"]}"; break;
                }
            }));
            bench.OnExit += c => BeginInvoke((Action)(() => { if (c != 0 && !benchStatus.Text.StartsWith("Quality benchmark failed")) benchStatus.Text = $"Quality benchmark ended (code {c}) - see logs"; }));
            var r = bench.Start($"--install-dir \"{Paths.Dir}\" --benchmark-quality");
            if (r != null) benchStatus.Text = r;
        }

        void RunNetworkTest()
        {
            if (BenchBusy()) return;
            if (Session.Headset(Session.Load()) == null) { MessageBox.Show(this, "Pair the headset first (tab 1).", Text); return; }
            var list = string.Join(",", steps.Text.Split(',').Select(x => x.Trim()).Where(x => int.TryParse(x, out _)));
            if (list.Length == 0) { MessageBox.Show(this, "Enter bitrates in Mbps, e.g. 100,200,300", Text); return; }
            SaveSettings(false);
            applyBitrate.Enabled = false;
            benchStatus.Text = "Waiting for the headset (open the headset app)...";
            bench = new HostProcess();
            bench.OnEvent += (n, d) => BeginInvoke((Action)(() =>
            {
                switch (n)
                {
                    case "client_connected": benchStatus.Text = "Headset connected; testing..."; break;
                    case "setup_error": benchStatus.Text = $"Network test failed: {d?["message"]}"; break;
                    case "bench_step_start": benchStatus.Text = $"Testing {d?["target_mbps"]} Mbps (step {Convert.ToInt32(d?["step"]) + 1})..."; break;
                    case "bench_step":
                        benchList.Items.Add(new ListViewItem(new[] { $"network {Fmt(d, "target_mbps", "0")} Mbps", Fmt(d, "actual_mbps", "0"), Fmt(d, "client_fps", "0"),
                            Fmt(d, "packets_lost_per_s", "0.0"), Fmt(d, "total_latency_ms", "0.0"), Fmt(d, "encode_ms", "0.0"), Fmt(d, "network_ms", "0.0"), Fmt(d, "decode_ms", "0.0"), "" }));
                        break;
                    case "bench_result":
                        var rec = d != null && d.TryGetValue("recommended_mbps", out var r) && r != null ? Convert.ToInt32(r) : 0;
                        if (d != null && d.TryGetValue("steps", out var st) && st is object[] arr)
                            for (int i = 0; i < arr.Length && i < benchList.Items.Count; i++)
                            {
                                var row = benchList.Items[benchList.Items.Count - arr.Length + i];
                                var ok = (arr[i] as Dictionary<string, object>)?["ok"] is bool b && b;
                                row.SubItems[8].Text = ok ? "ok" : string.Join(", ", ((arr[i] as Dictionary<string, object>)?["why"] as object[] ?? new object[0]).Cast<string>());
                                row.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
                            }
                        recommended = rec;
                        applyBitrate.Enabled = rec > 0;
                        benchStatus.Text = rec > 0 ? $"Recommended bitrate: {rec} Mbps (highest clean step minus 10%)." : "No step passed: " + (d?["note"] ?? "");
                        Log.Write("INFO", "network benchmark: " + benchStatus.Text);
                        break;
                }
            }));
            bench.OnExit += c => BeginInvoke((Action)(() => { if (!applyBitrate.Enabled && !benchStatus.Text.StartsWith("No step") && !benchStatus.Text.StartsWith("Network test failed")) benchStatus.Text = $"Network test ended (code {c}) - see logs"; }));
            var err = bench.Start($"--install-dir \"{Paths.Dir}\" --benchmark-network {list} --bench-step-s {stepSecs.Value} --connect-timeout 120 --seconds 900 --encode-profile {(profFull.Checked ? "full-split" : "foveated")}");
            if (err != null) benchStatus.Text = err;
        }

        [STAThread]
        static void Main()
        {
            Log.Component = "configure";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var args = Environment.GetCommandLineArgs();
            Log.Write("INFO", $"configure.exe {Version.Text} start ({Paths.Dir})");
            if (args.Contains("--fov")) { Application.Run(new FovManagerForm()); return; }   // the field-of-view profile manager: no system check, no streamer port
            if (args.Contains("--switches")) { Application.Run(new AdvancedForm()); return; }   // the Advanced settings window: no system check, no streamer port
            var check = File.Exists(Paths.Host) ? SystemCheck.Run() : null;
            // the launcher's one Settings card: with no paired headset (a first start, or the config folder deleted) it is the wizard, else the two tabs
            if (args.Contains("--settings") || args.Contains("--advanced"))
            {
                if (Session.Headset(Session.Load()) != null || args.Contains("--demo")) { Application.Run(new ConfigureForm(check)); return; }
                Log.Write("INFO", "settings: no paired headset, starting the wizard");
            }
            Application.Run(new WizardForm(check, args.Contains("--demo")));
        }
    }
}
