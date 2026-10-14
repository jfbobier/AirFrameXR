// The settings VisionALVR actually uses, as one panel: the wizard's last step shows it prefilled with the proposal, and the user may change anything.
// Everything else (ALVR's advanced options) stays in config\session.json.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace VisionALVR
{
    public class SettingsPanel : UserControl
    {
        readonly AppSettings settings;
        Dictionary<string, object> session;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        readonly Dictionary<Control, Label> reasons = new Dictionary<Control, Label>();

        public readonly NumericUpDown EyeW = Num(256, 8192, 32), EyeH = Num(256, 8192, 32), Bitrate = Num(10, 1000, 10);
        public readonly ComboBox Fps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
        public readonly RadioButton ProfFov = new RadioButton { Text = "Foveated (ALVR foveated encoding; recommended)", AutoSize = true };
        public readonly RadioButton ProfFull = new RadioButton { Text = "Full frame + split encode (experimental: 3x the pixels, needs a strong Wi-Fi link)", AutoSize = true };
        public readonly CheckBox Split = new CheckBox { Text = "Split encoding: one strip per NVENC engine (recommended; needs a GPU with 2 or more engines)", AutoSize = true };
        public readonly NumericUpDown Fcx = Dec(0.1m, 1m), Fcy = Dec(0.1m, 1m), Fsx = Dec(-1m, 1m), Fsy = Dec(-1m, 1m), Frx = Dec(1m, 10m), Fry = Dec(1m, 10m);
        readonly Button idle = new Button { Width = 90, Height = 24, FlatStyle = FlatStyle.Flat };
        public readonly CheckBox Audio = new CheckBox { Text = "Stream game audio (Windows default output)", AutoSize = true };
        public readonly CheckBox Mute = new CheckBox { Text = "Mute the PC speakers while streaming", AutoSize = true };
        public readonly ComboBox Proto = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
        public readonly NumericUpDown Packet = Num(500, 4000, 100);
        public readonly NumericUpDown Haptics = Dec(0m, 5m);
        public readonly CheckBox DebugDefault = new CheckBox { Text = "Debug logging on by default (recommended during the alpha)", AutoSize = true };
        const string V = "session_settings.video.";

        static NumericUpDown Num(int min, int max, int inc) => new NumericUpDown { Minimum = min, Maximum = max, Increment = inc, Width = 80 };
        static NumericUpDown Dec(decimal min, decimal max) => new NumericUpDown { Minimum = min, Maximum = max, Increment = 0.05m, DecimalPlaces = 2, Width = 80 };

        public SettingsPanel(AppSettings settings, Dictionary<string, object> session)
        {
            this.settings = settings;
            this.session = session;
            AutoScroll = true;
            BackColor = Color.White;
            Font = Ui.Body;
            var g = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
            void row(string label, Control c, Control reasonFor = null)
            {
                g.Controls.Add(Ui.Line(label));
                var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
                cell.Controls.Add(c);
                var r = new Label { AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.5f), MaximumSize = new Size(520, 0), Margin = new Padding(3, 0, 3, 3), Visible = false };
                cell.Controls.Add(r);
                g.Controls.Add(cell);
                reasons[reasonFor ?? c] = r;
            }
            void section(string title) { var l = Ui.Line(title, Ui.Big); l.ForeColor = Ui.Accent; l.Margin = new Padding(3, 12, 3, 3); g.Controls.Add(l); g.SetColumnSpan(l, 2); }
            Control flow(params Control[] cs) { var f = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) }; f.Controls.AddRange(cs); return f; }
            void note(Control c) { g.Controls.Add(new Label()); g.Controls.Add(c); }

            section("Video");
            var res = flow(EyeW, Ui.Line("x"), EyeH);
            row("Resolution per eye", res, EyeW);
            Fps.Items.AddRange(new object[] { "60", "72", "80", "90", "96", "100", "120" });
            row("Refresh rate (Hz)", Fps);
            row("Bitrate (Mbps)", Bitrate);
            var prof = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
            prof.Controls.Add(ProfFov);
            prof.Controls.Add(ProfFull);
            row("Encoding", prof, ProfFov);
            row("Split encoding", Split, Split);
            row("Foveation center size", flow(Fcx, Fcy), Fcx);
            row("Foveation center shift", flow(Fsx, Fsy), Fsx);
            row("Foveation edge ratio", flow(Frx, Fry, Ui.Line("(higher = fewer pixels at the edges)")), Frx);
            idle.Click += (s, e) =>
            {
                using (var cd = new ColorDialog { Color = idle.BackColor, FullOpen = true })
                    if (cd.ShowDialog(this) == DialogResult.OK) SetIdle(cd.Color);
            };
            var idleReset = new Button { Text = "Pure green (recommended)", AutoSize = true };
            idleReset.Click += (s, e) => SetIdle(Color.FromArgb(0, 255, 0));
            row("Colour when no game runs", flow(idle, idleReset), idle);
            var remind = Ui.Line("Set the SAME colour as the chroma key in the ALVR app on the headset (Settings > Chroma key), so it shows your room.");
            remind.ForeColor = Color.DimGray;
            note(remind);

            section("Audio");
            note(Audio);
            note(Mute);

            section("Network and controllers");
            Proto.Items.AddRange(new object[] { "Udp", "Tcp" });
            row("Stream protocol", flow(Proto, Ui.Line("UDP is what the headset normally uses")), Proto);
            row("Packet size (bytes)", Packet);
            row("Haptics strength", Haptics);

            section("Logging");
            note(DebugDefault);
            var more = Ui.Line("Everything else (ALVR's advanced options) stays in config\\session.json.");
            more.ForeColor = Color.DimGray;
            note(more);
            Controls.Add(g);
        }

        void SetIdle(Color c) { idle.BackColor = c; idle.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; idle.ForeColor = c.GetBrightness() > 0.5 ? Color.Black : Color.White; }

        static decimal Clamp(NumericUpDown n, double v) => Math.Max(n.Minimum, Math.Min(n.Maximum, (decimal)v));

        /// A short "why" next to a setting (the proposal's reasons), or "" to clear it.
        public void SetReason(Control c, string text)
        {
            if (reasons.TryGetValue(c, out var l)) { l.Text = text ?? ""; l.Visible = !string.IsNullOrEmpty(text); tips.SetToolTip(c, text ?? ""); }
        }

        public void ClearReasons() { foreach (var l in reasons.Values) { l.Text = ""; l.Visible = false; } }

        /// Reads the current values from session.json / visionalvr.json (what is on disk wins).
        public new void Load()
        {
            session = Session.Load();
            var s = session;
            EyeW.Value = Clamp(EyeW, Json.Num(s, V + "transcoding_view_resolution.Absolute.width", 3552));
            EyeH.Value = Clamp(EyeH, Json.Num(s, V + "transcoding_view_resolution.Absolute.height.content", 3200));
            var f = ((int)Json.Num(s, V + "preferred_fps", 90)).ToString(CultureInfo.InvariantCulture);
            if (!Fps.Items.Contains(f)) Fps.Items.Add(f);
            Fps.SelectedItem = f;
            Bitrate.Value = Clamp(Bitrate, Json.Num(s, V + "bitrate.mode.ConstantMbps", 250));
            (settings.EncodeProfile == "full-split" ? ProfFull : ProfFov).Checked = true;
            Split.Checked = settings.SplitEncode;
            Fcx.Value = Clamp(Fcx, Json.Num(s, V + "foveated_encoding.content.center_size_x", 0.45));
            Fcy.Value = Clamp(Fcy, Json.Num(s, V + "foveated_encoding.content.center_size_y", 0.4));
            Fsx.Value = Clamp(Fsx, Json.Num(s, V + "foveated_encoding.content.center_shift_x", 0.4));
            Fsy.Value = Clamp(Fsy, Json.Num(s, V + "foveated_encoding.content.center_shift_y", 0.1));
            Frx.Value = Clamp(Frx, Json.Num(s, V + "foveated_encoding.content.edge_ratio_x", 4));
            Fry.Value = Clamp(Fry, Json.Num(s, V + "foveated_encoding.content.edge_ratio_y", 5));
            try { SetIdle(ColorTranslator.FromHtml("#" + settings.IdleRgb.TrimStart('#'))); } catch { SetIdle(Color.FromArgb(0, 255, 0)); }
            Audio.Checked = Json.Bool(s, "session_settings.audio.game_audio.enabled", true);
            Mute.Checked = Json.Bool(s, "session_settings.audio.game_audio.content.mute_when_streaming", true);
            Proto.SelectedItem = Json.Str(s, "session_settings.connection.stream_protocol.variant", "Udp") == "Tcp" ? "Tcp" : "Udp";
            Packet.Value = Clamp(Packet, Json.Num(s, "session_settings.connection.packet_size", 1400));
            Haptics.Value = Clamp(Haptics, Json.Num(s, "session_settings.headset.controllers.content.haptics.content.intensity_multiplier", 1));
            DebugDefault.Checked = settings.Debug;
        }

        /// Puts a proposal into the controls (nothing is written to disk until Save).
        public void Prefill(Proposal p)
        {
            ClearReasons();
            void num(NumericUpDown n, double v, string why) { n.Value = Clamp(n, v); SetReason(n, why); }
            num(EyeW, p.EyeW, p.Why("resolution"));
            EyeH.Value = Clamp(EyeH, p.EyeH);
            var f = ((int)p.Fps).ToString(CultureInfo.InvariantCulture);
            if (!Fps.Items.Contains(f)) Fps.Items.Add(f);
            Fps.SelectedItem = f; SetReason(Fps, p.Why("fps"));
            num(Bitrate, p.BitrateMbps, p.Why("bitrate"));
            (p.Foveated ? ProfFov : ProfFull).Checked = true; SetReason(ProfFov, p.Why("foveation"));
            Split.Checked = true; SetReason(Split, "on by default: it cut the encode of the foveated frame from 6.9 to 2.5 ms on an RTX 5090 (a GPU with one engine simply ignores it)");
            Audio.Checked = p.Audio; SetReason(Audio, p.Why("audio"));
        }

        public bool Save(out string error)
        {
            error = null;
            session = Session.Load(); // keep anything edited meanwhile (e.g. pairing)
            var s = session;
            int w = (int)EyeW.Value / 32 * 32, h = (int)EyeH.Value; // ALVR aligns widths to 32
            var rate = double.Parse((string)Fps.SelectedItem, CultureInfo.InvariantCulture);
            foreach (var k in new[] { "transcoding_view_resolution", "emulated_headset_view_resolution" })
            {
                Json.Set(s, V + k + ".variant", "Absolute");
                Json.Set(s, V + k + ".Absolute.width", w);
                Json.Set(s, V + k + ".Absolute.height.set", true);
                Json.Set(s, V + k + ".Absolute.height.content", h);
            }
            Json.Set(s, V + "preferred_fps", rate);
            Json.Set(s, V + "bitrate.mode.variant", "ConstantMbps");
            Json.Set(s, V + "bitrate.mode.ConstantMbps", (int)Bitrate.Value);
            Json.Set(s, V + "foveated_encoding.enabled", ProfFov.Checked);
            Json.Set(s, V + "foveated_encoding.content.center_size_x", (double)Fcx.Value);
            Json.Set(s, V + "foveated_encoding.content.center_size_y", (double)Fcy.Value);
            Json.Set(s, V + "foveated_encoding.content.center_shift_x", (double)Fsx.Value);
            Json.Set(s, V + "foveated_encoding.content.center_shift_y", (double)Fsy.Value);
            Json.Set(s, V + "foveated_encoding.content.edge_ratio_x", (double)Frx.Value);
            Json.Set(s, V + "foveated_encoding.content.edge_ratio_y", (double)Fry.Value);
            // ALVR's negotiated-stream cache: rewritten at every connection, but the encoder tests (no headset) read it as it is
            Json.Set(s, "openvr_config.eye_resolution_width", w);
            Json.Set(s, "openvr_config.eye_resolution_height", h);
            Json.Set(s, "openvr_config.target_eye_resolution_width", w);
            Json.Set(s, "openvr_config.target_eye_resolution_height", h);
            Json.Set(s, "openvr_config.refresh_rate", (int)rate);
            Json.Set(s, "openvr_config.enable_foveated_encoding", ProfFov.Checked);
            Json.Set(s, "session_settings.audio.game_audio.enabled", Audio.Checked);
            Json.Set(s, "session_settings.audio.game_audio.content.mute_when_streaming", Mute.Checked);
            Json.Set(s, "session_settings.connection.stream_protocol.variant", (string)Proto.SelectedItem);
            Json.Set(s, "session_settings.connection.packet_size", (int)Packet.Value);
            Json.Set(s, "session_settings.headset.controllers.content.haptics.content.intensity_multiplier", (double)Haptics.Value);
            try { Json.Save(Paths.Session, s); }
            catch (Exception e) { error = "Cannot write config\\session.json: " + e.Message; return false; }
            settings.EncodeProfile = ProfFull.Checked ? "full-split" : "foveated";
            settings.SplitEncode = Split.Checked || ProfFull.Checked;   // the full-frame profile forces 3 strips whatever this says
            settings.IdleRgb = $"{idle.BackColor.R:x2}{idle.BackColor.G:x2}{idle.BackColor.B:x2}";
            settings.Debug = DebugDefault.Checked;
            settings.Save();
            Log.Write("INFO", $"settings saved: {w}x{h}/eye @ {rate} Hz, {Bitrate.Value} Mbps, {settings.EncodeProfile}, idle #{settings.IdleRgb}, audio {Audio.Checked}, {Proto.SelectedItem}, debug {settings.Debug}");
            return true;
        }
    }
}
