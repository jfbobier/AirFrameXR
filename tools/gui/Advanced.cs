// configure.exe --switches: the Advanced settings window. The experimental and tuning switches of docs/EXPERIMENTAL_SWITCHES.md, grouped, each with the moment
// its change takes effect (live in the running streamer, at the next streamer start, at the next game start) and its value read back from the file or the registry.
// Everything applies when changed; a switch at its default is removed from config\airframexr.json. The streamer may stay open: it forwards the live ones itself.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AirFrameXR
{
    public class AdvancedForm : DpiForm
    {
        enum Kind { Bool, Number, Choice }
        class Knob
        {
            public string Key, Title, Desc, Tip, When, Section;
            public Kind Kind;
            public double Def, Min, Max, Step; public int Decimals;
            public bool DefBool;
            public string[] Choices; public string DefChoice;
            public Control Ctl;
        }

        const string Live = "live", Restart = "streamer restart", NextGame = "next game start";
        readonly List<Knob> knobs = new List<Knob>();
        readonly Label status = Ui.Line("");
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        readonly CheckBox uncapped = new CheckBox { AutoSize = true, Text = "Uncapped mode", AccessibleName = "Uncapped mode" };
        readonly CheckBox gate = new CheckBox { AutoSize = true, Text = "Frame-start gate", AccessibleName = "Frame-start gate" };
        readonly Label gateNow = Ui.Line("");
        bool loading;
        const string GateKey = @"SOFTWARE\VirtualDesktop-OpenXR", GateValue = "ovrshim_vdxr_running_start";
        const string V = "video.";

        static Knob B(string sec, string key, string title, bool def, string when, string desc) => new Knob { Section = sec, Key = key, Title = title, Kind = Kind.Bool, DefBool = def, When = when, Desc = desc };
        static Knob N(string sec, string key, string title, double def, double min, double max, double step, int dec, string when, string desc) => new Knob { Section = sec, Key = key, Title = title, Kind = Kind.Number, Def = def, Min = min, Max = max, Step = step, Decimals = dec, When = when, Desc = desc };
        static Knob C(string sec, string key, string title, string def, string[] choices, string when, string desc) => new Knob { Section = sec, Key = key, Title = title, Kind = Kind.Choice, DefChoice = def, Choices = choices, When = when, Desc = desc };

        public AdvancedForm()
        {
            Text = "AirFrameXR – Advanced settings";
            Icon = Ui.AppIcon();
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            BackColor = Color.White;
            Width = 1000; Height = 860;
            Ui.FitToWorkArea(this);

            const string FG = "Frame generation (experimental)", AR = "Arrival and pacing", GE = "GPU and encoder";
            knobs.AddRange(new[]
            {
                N(FG, "frame_gen_cap_fps", "Cap on the game's rate (fps)", 0, 0, 240, 5, 0, Live, "Uncapped mode only; 0 = none. A cap near 90 % of the game's GPU-bound rate shrinks its render queue (Dead Space 62 to 31 ms lag, 2026-10-15); with the frame-start gate on the queue is smaller to begin with, so measure."),
                C(FG, "frame_gen_synthesis", "Generated-frame synthesis", "edge", new[] { "edge", "classic", "edge-blend" }, Restart, "edge (default): sharp motion boundaries; where the motion is uncertain the real frame is shown (a little judder) instead of a double image (ideas from VectorXR). classic: the algorithm of 0.6 and earlier, the predictable fallback if edge misbehaves in a game. edge-blend: edge, but uncertain motion blends both frames (smoothest in the lab, double images where the motion estimate fails)."),
                B(AR, "phase_lock", "Phase lock", true, Restart, "Slides the host's display grid so frames arrive the phase margin before the headset's render tick. Known debt: it slides 12-25 times a minute whatever it sees."),
                N(AR, "running_start_ms", "Running-start margin (ms)", 2, 0, 10, 0.5, 1, Restart, "The game is released envelope + margin before its boundary; 0 releases it at the tick. With the frame-start gate on this acts at frame start: 1-4 are plausible."),
                B(AR, "intake_gate", "Intake gate", true, Restart, "The shim skips one release when the host's queue has stood 3 boundaries: drains a standing queue without dropping a frame. Works when the game keeps up, fires uselessly 3 times a second in GPU-bound games."),
                B(GE, "qp_map", "HEVC QP map", true, Restart, "A trade-off, on by default (since 0.1): more bits for the centre of the foveated picture, fewer for the edges (quality level -3 at the centre, +6 at the edges). Measured against the native render in the lab: the centre gains 0.1-0.3 dB and the periphery loses 0.2-0.9 dB at 40-100 Mbps, and the stream carries about 5 % fewer bits; at 250 Mbps nothing changes. No encode-time cost measured (within 0.5 ms). Untick it for an even picture with the most detail at the edges. Hover for the numbers and what is not measured."),
            });

            knobs.Find(k => k.Key == "qp_map").Tip =
                "HEVC QP map: what it does and what it costs\r\n\r\n" +
                "Each 32 x 32 block of the foveated frame gets a quality offset: -3 in the region you look at, +6 at the edges, with a smooth ramp.\r\n" +
                "The bitrate is the same budget, so the centre gets more of it and the edges less.\r\n\r\n" +
                "Measured (quality benchmark, Littlest Tokyo, RTX 3080 Ti laptop, 2064 x 2208 per eye), map on against off:\r\n" +
                "   250 Mbps: centre +0.02 dB, edges -0.04 dB\r\n" +
                "   100 Mbps: centre +0.06 dB, edges -0.20 dB\r\n" +
                "    40 Mbps: centre +0.14 dB, edges -0.55 dB\r\n" +
                "The stream carries about 5 % fewer bits (the encoder does not spend what the map frees). Encode time and slices per frame are unchanged.\r\n\r\n" +
                "Not measured: how it looks (PSNR is not what your eyes see; judge by looking, the switch takes effect at the next streamer start), and split encoding " +
                "across several NVENC engines (the lab GPU has one). The full-frame + split encoding profile turns the map off by itself. Spatial AQ is separate and always on.";
            var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(12, 4, 12, 12), Dock = DockStyle.None, Location = Point.Empty };
            var intro = Ui.Line("Switches that change timing, for people who measure. Each one says when it takes effect: live = the running streamer takes it within seconds; streamer restart = close and start the streamer; next game start = start the game again. " +
                "A switch at its default is removed from config\\airframexr.json. The signals to judge by are in logs\\AirFrameXR.log: the 'frame generation perf' and 'frame generation samples' lines every 10 s, and 'game run recorded' at the end of a game.");
            intro.MaximumSize = new Size(900, 0); intro.ForeColor = Color.DimGray;
            body.Controls.Add(intro);

            Section(body, FG);
            Row(body, uncapped, "Experimental", Live, "The game runs at its own rate and every frame sent is interpolated: a game that cannot hold the rate stops stuttering, at the price of latency in the scene (head turns stay immediate). On the headset (2026-10-15) it was better below and above 50 fps, not at 50; it has not worked reliably since, so treat it as a test.");
            uncapped.CheckedChanged += (s, e) => { if (!loading) SetUncapped(uncapped.Checked); };
            foreach (var k in knobs.Where(x => x.Section == FG)) AddKnob(body, k);
            Section(body, AR);
            foreach (var k in knobs.Where(x => x.Section == AR)) AddKnob(body, k);
            Section(body, GE);
            foreach (var k in knobs.Where(x => x.Section == GE)) AddKnob(body, k);
            Section(body, "Frame-start gate (the OpenXR layer)");
            var gateRow = Row(body, gate, null, NextGame, "On (default): the game's xrWaitFrame waits for the shim's release, so the running start and the GPU throttle act at frame start (frame start to GPU done 55 to 42 ms at the 1/2 cap in the rig; no fps loss in 6 h on the headset). Off: the pre-0.6 behaviour, the game wakes one display period minus 2 ms after the previous release. " +
                "Needs bin\\virtualdesktop-openxr.dll of 0.6 or newer. Changing it asks for administrator rights once.");
            gateRow.Controls.Add(gateNow);
            gate.CheckedChanged += (s, e) => { if (!loading) SetGate(gate.Checked); };

            var bottom = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
            var reset = new Button { Text = "Back to defaults", AutoSize = true };
            reset.Click += (s, e) => ResetAll();
            bottom.Controls.Add(reset);
            bottom.Controls.Add(status);
            body.Controls.Add(bottom);
            var note = Ui.Line("Not offered: the removed switches (game_pacing jit / queue, the automatic rate controller, the duplicate-timestamp re-stamp paths, predicted samples) and head_wait_ms; each is documented in docs/PERFORMANCE_KNOBS.md with the measurement that ruled it out. Everything else of ALVR stays in config\\session.json.");
            note.MaximumSize = new Size(900, 0); note.ForeColor = Color.DimGray;
            body.Controls.Add(note);

            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            scroller.Controls.Add(body);
            Controls.Add(scroller);
            var hdr = Ui.CompactHeader("Advanced settings");
            Controls.Add(hdr);
            Ui.AutoFitWindow(this, hdr, body);
            LoadValues();
            Ui.ScreenshotWhenShown(this, 1500);
        }

        static void Section(FlowLayoutPanel body, string title)
        {
            var l = Ui.Line(title, Ui.Big); l.ForeColor = Ui.Accent; l.Margin = new Padding(3, 14, 3, 2);
            body.Controls.Add(l);
        }

        /// one switch: the control and its effect moment on a line, the explanation under it; returns the line
        FlowLayoutPanel Row(FlowLayoutPanel body, Control ctl, string badge, string when, string desc, string tip = null)
        {
            var top = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            ctl.Margin = new Padding(3, 4, 12, 0);
            top.Controls.Add(ctl);
            if (badge != null) top.Controls.Add(Ui.Badge(badge));
            top.Controls.Add(new Label { Text = when, AutoSize = true, ForeColor = when == Live ? Color.SeaGreen : Color.DarkGoldenrod, Font = Ui.Bold, Margin = new Padding(8, 5, 3, 0) });
            body.Controls.Add(top);
            var d = Ui.Line(desc); d.ForeColor = Color.DimGray; d.Font = new Font(Ui.Body.FontFamily, 8.5f); d.MaximumSize = new Size(900, 0); d.Margin = new Padding(22, 0, 3, 2);
            body.Controls.Add(d);
            tips.SetToolTip(ctl, tip ?? desc);
            return top;
        }

        void AddKnob(FlowLayoutPanel body, Knob k)
        {
            Control shown;
            switch (k.Kind)
            {
                case Kind.Bool:
                    var cb = new CheckBox { Text = k.Title, AutoSize = true, AccessibleName = k.Title };
                    cb.CheckedChanged += (s, e) => Changed(k);
                    k.Ctl = shown = cb; break;
                case Kind.Number:
                    var nu = new NumericUpDown { Minimum = (decimal)k.Min, Maximum = (decimal)k.Max, Increment = (decimal)k.Step, DecimalPlaces = k.Decimals, Width = 80, AccessibleName = k.Title };
                    nu.ValueChanged += (s, e) => Changed(k);
                    k.Ctl = nu; shown = Pair(k.Title, nu); break;
                default:
                    var co = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, AccessibleName = k.Title };
                    co.Items.AddRange(k.Choices);
                    co.SelectedIndexChanged += (s, e) => Changed(k);
                    k.Ctl = co; shown = Pair(k.Title, co); break;
            }
            Row(body, shown, null, k.When, k.Desc + "  Default: " + DefaultText(k) + ".", k.Tip);
        }

        static FlowLayoutPanel Pair(string text, Control c)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            f.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 5, 6, 0) });
            f.Controls.Add(c);
            return f;
        }

        static string DefaultText(Knob k) => k.Kind == Kind.Bool ? (k.DefBool ? "on" : "off") : k.Kind == Kind.Number ? k.Def.ToString("0.##", CultureInfo.InvariantCulture) + (k.Key == "frame_gen_cap_fps" ? " (none)" : k.Key == "frame_gen_lag_ms" ? " (automatic)" : "") : k.DefChoice;

        /// the values on disk (what is read back, not the last click)
        void LoadValues()
        {
            loading = true;
            var st = new AppSettings();
            foreach (var k in knobs)
            {
                var p = V + k.Key;
                switch (k.Kind)
                {
                    case Kind.Bool: ((CheckBox)k.Ctl).Checked = Json.Bool(st.Root, p, k.DefBool); break;
                    case Kind.Number: ((NumericUpDown)k.Ctl).Value = Math.Max((decimal)k.Min, Math.Min((decimal)k.Max, (decimal)Json.Num(st.Root, p, k.Def))); break;
                    default: var v = Json.Str(st.Root, p, k.DefChoice); var co = (ComboBox)k.Ctl; co.SelectedItem = co.Items.Contains(v) ? v : k.DefChoice; break;
                }
            }
            uncapped.Checked = st.FrameGenRate == "uncapped";
            ShowGate();
            loading = false;
        }

        void Changed(Knob k)
        {
            if (loading) return;
            var st = new AppSettings();   // fresh: the streamer saves the same file too
            var p = V + k.Key;
            object val; bool isDef; string text;
            switch (k.Kind)
            {
                case Kind.Bool: var b = ((CheckBox)k.Ctl).Checked; val = b; isDef = b == k.DefBool; text = b ? "on" : "off"; break;
                case Kind.Number: var n = (double)((NumericUpDown)k.Ctl).Value; val = n; isDef = Math.Abs(n - k.Def) < 1e-9; text = n.ToString("0.##", CultureInfo.InvariantCulture); break;
                default: var c = (string)((ComboBox)k.Ctl).SelectedItem; val = c; isDef = c == k.DefChoice; text = c; break;
            }
            if (isDef) Json.Remove(st.Root, p); else Json.Set(st.Root, p, val);
            Save(st, $"{k.Key} = {text}{(isDef ? " (default)" : "")}", k.When);
        }

        void SetUncapped(bool on)
        {
            var st = new AppSettings();
            var cur = st.FrameGenRate;
            if (on) { if (cur != "uncapped") Json.Set(st.Root, "video.frame_gen_rate_before_uncapped", cur); st.FrameGenRate = "uncapped"; }
            else st.FrameGenRate = Back(st);
            Save(st, "frame_gen_rate = " + st.FrameGenRate, Live);
        }

        /// the finite rate the streamer's slider had before uncapped was ticked
        static string Back(AppSettings st) { var b = Json.Str(st.Root, "video.frame_gen_rate_before_uncapped", "half"); return b == "uncapped" ? "half" : b; }

        void Save(AppSettings st, string what, string when)
        {
            try { st.Save(); }
            catch (Exception e) { status.Text = "Cannot write config\\airframexr.json: " + e.Message; status.ForeColor = Color.Firebrick; return; }
            Log.Write("INFO", "advanced: " + what + " (" + when + ")");
            var running = Process.GetProcessesByName("streamer").Length > 0;
            status.Text = "Saved: " + what + ". " + (when == Live ? (running ? "The running streamer takes it within a couple of seconds." : "It takes effect when the streamer starts.") : "It takes effect after a streamer restart.");
            status.ForeColor = Color.SeaGreen;
        }

        void ResetAll()
        {
            var st = new AppSettings();
            foreach (var k in knobs) Json.Remove(st.Root, V + k.Key);
            if (st.FrameGenRate == "uncapped") st.FrameGenRate = Back(st);
            Save(st, "all the switches back to their defaults", Restart);
            LoadValues();
        }

        // ---------------------------------------------------------------- the frame-start gate: HKLM, one UAC prompt to write
        static int GateValueNow()
        {
            try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(GateKey)) return k?.GetValue(GateValue) is int v ? v : 0; }
            catch { return 0; }
        }

        void ShowGate()
        {
            var now = GateValueNow();
            var was = loading; loading = true;
            gate.Checked = now != 1;
            loading = was;
            gateNow.Text = now == 1 ? "now: off (registry value 1)" : "now: on";
            gateNow.Margin = new Padding(8, 5, 3, 0);
        }

        void SetGate(bool on)
        {
            var args = on ? $"/c reg delete HKLM\\{GateKey} /v {GateValue} /f" : $"/c reg add HKLM\\{GateKey} /v {GateValue} /t REG_DWORD /d 1 /f";
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit(60000);
            }
            catch (System.ComponentModel.Win32Exception) { }   // UAC declined: the read-back below puts the box right
            ShowGate();
            var done = (GateValueNow() != 1) == on;
            status.Text = done ? $"Frame-start gate {(on ? "on" : "off")}: it takes effect at the next game start." : "The registry was not changed (the administrator prompt was declined).";
            status.ForeColor = done ? Color.SeaGreen : Color.Firebrick;
            Log.Write(done ? "INFO" : "WARN", "advanced: frame-start gate " + (on ? "on" : "off") + (done ? "" : " (not changed)"));
        }
    }
}
