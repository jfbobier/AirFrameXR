// configure.exe --switches: the Advanced picture and FOV settings window. The experimental and tuning switches of docs/EXPERIMENTAL_SWITCHES.md, grouped, each with the moment
// its change takes effect (live in the running streamer, at the next streamer start, at the next game start) and its value read back from the file or the registry.
// Everything applies when changed; a switch at its default is removed from config\airframexr.json. The streamer may stay open: it forwards the live ones itself.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
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

        public AdvancedForm(double[] clientFovForTest = null, bool tickForTest = false, int[] streamEyeForTest = null)
        {
            testClient = clientFovForTest; testTick = tickForTest; testEye = streamEyeForTest;
            Text = "AirFrameXR – Advanced picture and FOV settings";
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
                N(FG, "frame_gen_cap_fps", "Cap on the game's rate (fps)", 0, 0, 240, 5, 0, Live, "Uncapped mode only; 0 = no cap. Holds the game back to at most this many frames per second, so a GPU-bound game stops building a queue of frames in front of the GPU. Rule of thumb from the nights: a little under the rate the game sustains (about 90 %). On Dead Space it cut the lag from 62 to 31 ms; 70 did nothing for 40 fps games, which needed about 37. With the frame-start gate on the queue is smaller to begin with, so measure. Hover for the numbers."),
                C(FG, "frame_gen_synthesis", "Generated-frame synthesis", "edge", new[] { "edge", "classic", "edge-blend" }, Restart, "How a generated frame is drawn from the optical flow. edge (default): sharp motion boundaries; where the motion is uncertain the real frame is shown (a little local judder) instead of a double image. classic: the algorithm of 0.6 and earlier, the predictable fallback if edge misbehaves in a game. edge-blend: edge, but uncertain motion blends both frames. What we know: in games on the headset edge looks visibly better than classic, and edge could not be told apart from edge-blend. The lab's ground-truth test agrees (a frame generated from two rendered frames of the animated benchmark scene, compared with the frame the scene really has there): edge keeps the parts that did not move intact where classic softens them (whole-frame PSNR +2.0 dB, sharper in 63 of 63 cases), and edge against edge-blend is a statistical tie. Edge also costs less GPU (0.24 ms against 0.33 ms per generated frame). Hover for the numbers."),
                B(AR, "phase_lock", "Phase lock", true, Restart, "Slides the host's frame clock (up to 2 ms every 2-4 s) so frames arrive 7 ms before the headset's render tick. The margin matters: at 3 ms 1.2-2.3 % of the headset's refreshes repeated, at 7 ms 0.5 %, with the same 67 ms latency. Known flaw: it slides 12-25 times a minute even when it has nothing to correct (999 slides in an hour of a game running free at 56 fps). Leave it on; turn it off only to compare. Hover for the numbers."),
                N(AR, "running_start_ms", "Running-start margin (ms)", 2, 0, 10, 0.5, 1, Restart, "How much earlier than the display tick the game is released, on top of its own frame-time envelope: 0 releases it at the tick, higher gives the game's frame more time to finish before the host needs it. The default is 2 ms; 1-4 are plausible. Honest status: no headset A/B for this value. In the lab a run with 0 was the cleanest (0.1 % repeats, no empty boundary) and a lead capped below one game period changed nothing. Hover for the context."),
                B(AR, "intake_gate", "Intake gate", true, Restart, "When the host's queue has stood 3 frames in a row (the game runs a frame ahead of the stream), the shim skips one release of the game, so the queue drains without dropping a rendered frame. In the lab on a GPU-bound scene it cut pose-to-display latency to 32 ms against 43 ms for plain in-order sending (33 ms with the drop-based drain), same fps and repeats. It only acts when the game keeps up: in GPU-bound games that deliver in bunches it fired uselessly 3 times a second, hence its guard. Hover for the numbers."),
                B(GE, "qp_map", "HEVC QP map", true, Restart, "A trade-off, on by default (since 0.1): more bits for the centre of the foveated picture, fewer for the edges (quality level -3 at the centre, +6 at the edges). Measured against the native render in the lab: the centre gains 0.1-0.3 dB and the periphery loses 0.2-0.9 dB at 40-100 Mbps, and the stream carries about 5 % fewer bits; at 250 Mbps nothing changes. No encode-time cost measured (within 0.5 ms). Untick it for an even picture with the most detail at the edges. Hover for the numbers and what is not measured."),
            });

            knobs.Find(k => k.Key == "frame_gen_cap_fps").Tip =
                "Cap on the game's rate (uncapped mode only)\r\n" +
                "\r\n" +
                "Headset nights of 2026-10-15/16 (RTX 5090, Vision Pro at 100 Hz), uncapped, free-running against capped at 70 fps:\r\n" +
                "   Dead Space:  80 fps, lag 62 ms, newest rendered frame 35 ms old (2.8 periods)  ->  67 fps, lag 41 then 31 ms, 13 ms (0.9 periods)\r\n" +
                "   Avatar:      98 fps, lag 22-61 ms  ->  67 fps, lag 30 ms\r\n" +
                "   Cyberpunk:   89 fps, lag 61 ms  ->  67 fps, lag 33 ms\r\n" +
                "   Carma, Valor Mortis (about 40 fps): 70 changed nothing (lag 78-80 ms either way); they needed a cap near 37.\r\n" +
                "Free-running, 90-96 % of the releases were late (the GPU-bound signature); capped, 0 %.\r\n" +
                "The user: capping at 70 felt better than free-running in every game that could exceed it.\r\n" +
                "\r\n" +
                "Pick the number from the 'frame generation perf' line in logs\\AirFrameXR.log: the game's app fps with most releases late. The frame-start gate (below) was added after these runs and makes the queue smaller to begin with: re-measure.";
            knobs.Find(k => k.Key == "frame_gen_synthesis").Tip =
                "Generated-frame synthesis: what confirms what\r\n" +
                "\r\n" +
                "Looks (the maintainer, side by side in games on the headset, 2026-10): edge is visibly better than classic; edge and edge-blend could not be told apart. Edge 'looked great' in Valor Mortis (2026-10-24).\r\n" +
                "\r\n" +
                "Lab ground-truth judge (harness/fg_judge.py, 2026-10-26: Littlest Tokyo animated scene, RTX 3080 Ti laptop, 1032 x 1104 per eye, 63 cases = 3 rates of the rendered pair x head still / head moving / both x 3 start times). The midpoint is generated from two rendered frames and compared with the frame the scene really has there (G):\r\n" +
                "   Whole-frame PSNR, scene moving and head still: classic 43.2 dB, hold the last frame 47.7, blend 51.8, edge 54.1, edge-blend 54.3. Classic degrades the parts that did not move (it re-samples the whole frame): worse than simply repeating a frame.\r\n" +
                "   Over all cases with motion, edge against classic: whole-frame PSNR +2.0 dB (52 of 63 cases), SSIM in the moving region +0.013 (59 of 63), blur at the motion boundaries lower in 63 of 63. Edge shows slightly more edge energy than the truth at the boundaries (+0.006), i.e. a little more double-edge risk.\r\n" +
                "   Edge against edge-blend: PSNR on the boundary band -0.12 +- 0.08 dB, SSIM +0.004 +- 0.003: a statistical tie. Edge-blend hedges where the motion is uncertain, which PSNR rewards; edge is slightly sharper (blur -0.004 +- 0.002) and carries slightly more edge energy than the truth (+0.006). Not distinguishable by eye either.\r\n" +
                "   With the head moving the three are within 0.1 dB of each other: the rotation reprojection dominates.\r\n" +
                "Cost (RTX 5090, 7104 x 3200 frame, per generated output): edge synthesis 0.24 ms, classic 0.33 ms; the optical flow itself 2.17 ms either way. No added latency in any mode.\r\n" +
                "Older marker test (high-frequency rms of the marker's position error, degrees): classic 0.024-0.031, edge 0.030-0.036, edge-blend 0.017-0.022. It measures where the marker is, not sharpness: it does not contradict the above.\r\n" +
                "\r\n" +
                "Limits: one scene, one GPU, PSNR / SSIM are not what the eye sees (PSNR rewards blending), and the judge cannot see judder over time.\r\n" +
                "\r\n" +
                "edge: 3 x 3 vector median, the vector solved where the content comes from, per-pixel choice among neighbouring vectors at motion boundaries; doubtful motion shrinks towards the real frame.\r\n" +
                "classic: the crossfade recipe of 0.4-0.6. edge-blend: edge without the photometric gate (doubtful motion blends both frames).";
            knobs.Find(k => k.Key == "phase_lock").Tip =
                "Phase lock\r\n" +
                "\r\n" +
                "The arrival cluster of the frames spans 8-9 ms of a 10 ms display period (7.9-9.0 ms with frame generation off). The lock slides the host's frame clock so the earliest 10 % arrive 'phase margin' ms before the headset's render tick (default 7 ms, set from Diagnose).\r\n" +
                "Margin 3 ms: a share of the frames landed after the tick and were shown a tick late: the headset repeated 1.2-2.3 % of its refreshes.\r\n" +
                "Margin 7 ms: 0.5 %, and the total latency stayed 67 ms, because the panel shows a frame at its next tick either way. Cost: up to one period of latency, invisible until a frame crosses a tick.\r\n" +
                "Flaw: it corrects 12-25 times a minute in every session (999 slides in an hour of Gears with frame generation off, where a free-running 56 fps game gives it nothing to measure). A guard is planned: no action when the stream runs well under the display rate.";
            knobs.Find(k => k.Key == "running_start_ms").Tip =
                "Running-start margin\r\n" +
                "\r\n" +
                "The shim releases the game at: next host boundary - the game's frame-time envelope - this margin (the design follows SteamVR's running start). One release per boundary; a game slower than a period free-runs.\r\n" +
                "With the frame-start gate on, the game's xrWaitFrame returns at this release, so the margin decides when the game starts its frame.\r\n" +
                "What we know: the default (2 ms) held through the headset nights; the lab's run with 0 was the cleanest (0.1 % repeats, no empty boundary) but was never A/B'd on a headset; a lead capped below one game period changed nothing.\r\n" +
                "Nothing here is measured against a headset: change it only to compare, one value at a time, with the 'frame generation perf' line (releases late) and your own eyes.";
            knobs.Find(k => k.Key == "intake_gate").Tip =
                "Intake gate\r\n" +
                "\r\n" +
                "Lab, GPU-bound scene, pose-to-display latency: 32 ms with the gate, 43 ms for plain in-order sending, 33 ms with the drop-based drain; same fps and repeats. Neutral on steady and jittery scenes; spiky scenes pay about 1 fps.\r\n" +
                "Why: a standing queue costs a whole display period (stock ALVR's in-order queue held about 11.7 ms between arrival and encode start on the same rig), and only the game can drain it without losing a rendered frame.\r\n" +
                "A single frame left behind is not a standing queue (a late frame paired with an on-time one: both are shown in order); 3 boundaries in a row are.\r\n" +
                "Guard: a game that delivers in bunches, with empty boundaries in between (GPU-bound), got 3 useless skips a second; so it acts only when no empty boundary came in the last 30 boundaries.";
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
            var intro = Ui.Line("Switches that change the picture the game renders and the timing, for people who measure. Each one says when it takes effect: live = the running streamer takes it within seconds; streamer restart = close and start the streamer; next game start = start the game again. " +
                "A switch at its default is removed from config\\airframexr.json. The signals to judge by are in logs\\AirFrameXR.log: the 'frame generation perf' and 'frame generation samples' lines every 10 s, and 'game run recorded' at the end of a game.");
            intro.MaximumSize = new Size(900, 0); intro.ForeColor = Color.DimGray;
            body.Controls.Add(intro);

            BuildFov(body);
            Section(body, FG);
            Row(body, uncapped, "Experimental", Live, "Frame generation without a fixed rate. Normally frame generation holds the game to a fraction of the display rate (1/3, 1/2, 2/3); here the game runs at its own speed and every frame the headset gets is interpolated between the two newest rendered ones, shown a little behind so the next real frame is already known. It pays off when the game cannot hold half the display rate (below 50 fps: Carma 25 % repeats at half rate, 4.5 % uncapped) or runs well above it (Lies of P 74 fps rendered against 50). At exactly 50 fps it changes nothing and adds latency: about two game periods of scene latency (head turns stay immediate). Headset nights 2026-10-15/16; it has not worked reliably since, so treat it as a test. Hover for the numbers.", "Uncapped mode, what the headset nights measured (RTX 5090, Vision Pro 100 Hz, nine games, 16 runs, optical flow)\r\n" +
                "\r\n" +
                "Smoothness, repeated outputs, half rate against uncapped:\r\n" +
                "   Carma (41 fps):          25 %  ->  4.5 %\r\n" +
                "   Valor Mortis (40 fps):   16 %  ->  1-10 %   (1.2 % with a 70 fps cap)\r\n" +
                "   Lies of P:               rendered 50 fps at half rate  ->  74 fps uncapped (dips to 49)\r\n" +
                "   Mortal Shell II (50 fps): no difference (1-3 % repeats either way), plus the lag: half rate stays the right mode there.\r\n" +
                "   Luke Ross titles above 50: 80-98 fps rendered (Dead Space 80, Cyberpunk 89, Avatar 98), repeats 0 %.\r\n" +
                "Cost, free-running: the lag in force is 22-80 ms, the newest rendered frame already 2.2-2.9 game periods old when an output is made (the game's render queue). Capped at 70: 0.7-0.9 periods (see the cap).\r\n" +
                "Client intervals over 15 ms: 1-3 % in every mode. GPU: optical flow + synthesis 2.6-3.3 ms + 0.4 ms per output on the 5090 at 3552 x 1600 flow input.\r\n" +
                "The user: 'works as intended'; capping at 70 fps felt better than free-running in every game that could exceed it; no obvious smoothness gain over half rate.");
            uncapped.CheckedChanged += (s, e) => { if (!loading) SetUncapped(uncapped.Checked); };
            foreach (var k in knobs.Where(x => x.Section == FG)) AddKnob(body, k);
            Section(body, AR);
            foreach (var k in knobs.Where(x => x.Section == AR)) AddKnob(body, k);
            Section(body, GE);
            foreach (var k in knobs.Where(x => x.Section == GE)) AddKnob(body, k);
            Section(body, "Frame-start gate (the OpenXR layer)");
            var gateRow = Row(body, gate, null, NextGame, "On (default): the game's xrWaitFrame waits for the shim's release, so the GPU throttle and the running start act at the game's frame start. Off: the pre-0.6 behaviour, where VDXR's own running start woke the game a display period minus 2 ms after its previous release, one frame ahead of the release, so the game's render queue sat at 2-3 frames. Lab A/B on a GPU-bound scene: frame start to GPU done 55 -> 42 ms (p50) at the same fps, one display period less between the pose being read and the frame being finished. On the headset: no fps loss in 6 h (Gears of War at 1/2: 49.0 fps median against a 50 fps cap). Needs bin\\virtualdesktop-openxr.dll of 0.6 or newer; changing it asks for administrator rights once. Hover for the numbers.", "Frame-start gate (VDXR patch 5)\r\n" +
                "\r\n" +
                "Cause: VDXR's async submission thread calls the shim's release, but the game's xrWaitFrame did not wait for it: it woke the game at the earlier of 'the shim released' and 'one display period minus 2 ms after the previous release'. So in every GPU-bound game the CPU ran a frame ahead of the release, the frame was submitted before the shim's throttle could look at the GPU, and the render queue sat at 2-3 frames. xrWaitFrame returned 10-13 ms before the shim's release of the same frame.\r\n" +
                "Lab A/B (RTX 3080 Ti laptop, stream 3520 x 1600 at 100 Hz, 40 s runs), stock VDXR against gate on:\r\n" +
                "   GPU-bound under the 1/2 cap: fps 46.2 -> 46.5; frame start to GPU done p50 / p95 55.1 / 66.2 -> 41.8 / 50.4 ms; xrWaitFrame returned 12.3 ms before the release -> 0.\r\n" +
                "   GPU-bound, generation off:   fps 55.1 -> 55.1; frame start to GPU done p50 / p95 46.5 / 56.5 -> 35.9 / 45.3 ms.\r\n" +
                "   Host repeats and motion verdicts unchanged (7.5 % -> 6.9 %). The pose a frame is rendered with is read at xrWaitFrame, so the gain comes straight off the pose-to-photon latency.\r\n" +
                "Headset (Vision Pro, 5090, first night): Gears of War E-Day 4 h 39 at 1/2 held 49.0 fps median against the 50 fps cap (the day before it read 36-49); End of Abyss 89.2 of 90; no fps loss anywhere, so the CPU-overlap risk did not show. The latency gain itself is not isolated on the headset (every session had the gate): the clean test is a GPU-bound game with the gate off for the reference run.\r\n" +
                "Off = VDXR's own behaviour (registry value ovrshim_vdxr_running_start = 1).");
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
            var hdr = Ui.CompactHeader("Advanced picture and FOV settings");
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

        // ---- the field of view the game is told: an override of the headset's, the headset's own values shown while it is off ------------------------------------------
        CheckBox fovTick;
        readonly NumericUpDown[] fovNum = new NumericUpDown[4];
        readonly Label[] fovDeg = new Label[4];
        Label fovNote, fovShare;
        TrackBar fovQ;
        FlowLayoutPanel fovQRow;
        int[] streamEye;   // the stream's per-eye size (session.json, the user's resolution settings); null = not known yet
        FlowLayoutPanel fovFields;
        double[] clientFov;   // the headset's tangents per edge (outer, inner, upper, lower), averaged over the two eyes; null = no headset seen yet
        readonly double[] testClient; readonly bool testTick; readonly int[] testEye;   // the UI harness: a pretend headset, the override ticked
        static readonly string[] FovNames = { "Outer", "Inner", "Upper", "Lower" };
        static readonly string[] FovKeys = { "fov_outer", "fov_inner", "fov_upper", "fov_lower" };
        const string FovTip =
                "Field of view the game is told\r\n" +
                "\r\n" +
                "What it is: an override of the field of view the game is told it has, edge by edge (outer = the side away from your nose, inner = towards it, upper, lower), as tangents (what a flat projection uses; the angle is shown next to each field). Unticked, the game gets the headset's own, as it reported it (the greyed fields show it). Ticked, the fields open, starting from the headset's values; you can only go down from them (the game is never told more than the stream covers) and down to a fifth.\r\n" +
                "What you get: the game renders a smaller view with proportionally fewer pixels, so a GPU-bound game gets faster; you give up what was in the margin you trimmed. The edges of the Vision Pro's view are optically weaker than its centre, so a little off the edges costs little to see. Like SteamVR's Video > Field of View sliders, in absolute values.\r\n" +
                "What does not change: the stream. The frame still covers the headset's whole view (the headset maps it over the field of view it declared), the trimmed margin is black, encode, decode and network cost are the same, frame generation and the client are untouched. The saving is the game's GPU work. If the headset's own app also has a FoV scale, the two multiply: the headset's values shown here already include it.\r\n" +
                "\r\n" +
                "How games get it: through the OpenXR handshake. xrLocateViews returns the four angles per eye and xrEnumerateViewConfigurationViews the recommended image size; AirFrameXR returns the restricted angles and a size reduced in proportion to the area. UEVR reads exactly these (the four angles per eye for its projection and the first view's recommended size, scaled by its own resolution slider) when it starts, so it renders the narrower view. Other OpenXR games are expected to behave the same way (not tested here).\r\n" +
                "When it applies: at the NEXT game start. The runtime reads the field of view once when the game starts and UEVR does not re-read it, so a game that is already running keeps what it began with. Changing a field while the streamer runs is enough; nothing else to restart.\r\n" +
                "\r\n" +
                "The Vision Pro's field of view as visionOS reports it (left eye): tangents 1.78 outer, 1.02 inner, 1.22 up, 1.02 down = 60.7, 45.5, 50.6, 45.5 degrees (about 106 x 96 degrees). Share of the pixels the game renders, for example: outer 1.60 and upper 1.10 -> 88.5 %; every edge 90 % of the headset's -> 81 %; 85 % -> 72 %. The fields need the headset's values: connect the headset once (they are saved when it connects).";

        const string FovQualityTip =
                "Game render quality\r\n" +
                "\r\n" +
                "What it is: how many pixels the game renders per eye, between two limits worked out from your settings. Left end: the stream's pixel density over the smaller view you chose (the fewest pixels, the biggest GPU saving). Right end: the stream's own per-eye size (the pixels an unrestricted game renders).\r\n" +
                "What you get: the pixels the restriction saved can be spent on a finer render. The game's picture is filtered down to the stream's pixels, so you get cleaner edges and less shimmer (like supersampling); you do not get detail beyond what the stream carries.\r\n" +
                "What it costs: game GPU time and video memory, rising with the pixels shown. Encode, decode and network are unchanged.\r\n" +
                "The limits follow your resolution settings (the per-eye size of the stream, e.g. a Quest's lower one) and the field of view fields above; the slider only works while a restriction is on.\r\n" +
                "When it applies: at the NEXT game start, like the field of view.";

        /// The stream's per-eye size from the user's resolution settings (session.json: the negotiated cache the host reads too), null when unknown.
        static int[] ReadStreamEye()
        {
            try
            {
                var s = Session.Load();
                int w = (int)Json.Num(s, "openvr_config.eye_resolution_width", 0), h = (int)Json.Num(s, "openvr_config.eye_resolution_height", 0);
                return w > 0 && h > 0 ? new[] { w, h } : null;
            }
            catch { return null; }
        }

        static int EvenAtLeast64(double v) { return Math.Max(64, ((int)Math.Round(v, MidpointRounding.AwayFromZero) + 1) / 2 * 2); }

        /// the game's render size per eye at quality q (0..1): the same maths as the shim (ovrshim/render_size.h), widthRatio / heightRatio = the restricted span of the headset's
        public static int[] GameRenderSize(int eyeW, int eyeH, double widthRatio, double heightRatio, double q)
        {
            q = Math.Max(0, Math.Min(1, q));
            double fw = eyeW * widthRatio, fh = eyeH * heightRatio;
            return new[] { EvenAtLeast64(fw + (eyeW - fw) * q), EvenAtLeast64(fh + (eyeH - fh) * q) };
        }

        /// The headset's tangents as the host saved them at its last connection (config\airframexr_views.json: left eye l r u d, right eye l r u d), per edge averaged over the eyes.
        static double[] ReadClientFov()
        {
            try
            {
                var d = Json.Load(Path.Combine(Paths.Config, "airframexr_views.json"));
                var a = Json.Get(d, "fov_tan") as System.Collections.IList;
                if (a == null || a.Count < 8) return null;
                var f = new double[8];
                for (int i = 0; i < 8; i++) f[i] = Convert.ToDouble(a[i], CultureInfo.InvariantCulture);
                if (f.Any(x => double.IsNaN(x) || !(x > 0))) return null;
                return new[] { (f[0] + f[5]) / 2, (f[1] + f[4]) / 2, (f[2] + f[6]) / 2, (f[3] + f[7]) / 2 };
            }
            catch { return null; }
        }

        void BuildFov(FlowLayoutPanel body)
        {
            clientFov = testClient ?? ReadClientFov();
            Section(body, "Field of view the game is told");
            var intro = Ui.Line("The field of view the game is told it has, edge by edge, as tangents (the angle is shown next to each field), like SteamVR's Video > Field of View. The game renders a smaller view with proportionally fewer pixels (UEVR and OpenXR games read the four angles and the recommended size when they start); the stream still covers the whole headset view and the margin is black, so only the game's GPU work changes. Takes effect at the next game start. Hover for details.");
            intro.ForeColor = Color.DimGray; intro.MaximumSize = new Size(900, 0);
            body.Controls.Add(intro);
            fovTick = new CheckBox { AutoSize = true, Text = "Override the headset's field of view", AccessibleName = "Override the headset's field of view" };
            Row(body, fovTick, null, NextGame, "Off: the game gets the headset's own field of view (greyed below, as the headset reported it). On: the fields open, starting from the headset's values; you can only go down from them.", FovTip);
            fovFields = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(22, 4, 0, 0) };
            for (int k = 0; k < 4; k++)
            {
                var nud = new NumericUpDown { DecimalPlaces = 2, Increment = 0.01m, Minimum = 0.10m, Maximum = 9.99m, Width = 70, AccessibleName = FovNames[k] };
                nud.ValueChanged += (s, e) => FovValueChanged();
                fovNum[k] = nud;
                fovDeg[k] = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(2, 5, 14, 0) };
                fovFields.Controls.Add(Pair(FovNames[k], nud));
                fovFields.Controls.Add(fovDeg[k]);
                tips.SetToolTip(nud, FovTip);
            }
            body.Controls.Add(fovFields);
            fovNote = Ui.Line(""); fovNote.ForeColor = Color.DimGray; fovNote.Margin = new Padding(22, 2, 3, 0); fovNote.MaximumSize = new Size(900, 0);
            fovShare = Ui.Line(""); fovShare.ForeColor = Color.SeaGreen; fovShare.Margin = new Padding(22, 0, 3, 2); fovShare.MaximumSize = new Size(900, 0);
            body.Controls.Add(fovNote);
            body.Controls.Add(fovShare);
            fovQRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(22, 2, 0, 0), Visible = false };
            fovQ = new TrackBar { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, SmallChange = 5, LargeChange = 10, Width = 200, AutoSize = false, Height = 26, AccessibleName = "Game render quality" };
            fovQ.ValueChanged += (s, e) => FovQualityChanged();
            fovQRow.Controls.Add(new Label { Text = "Game render quality", AutoSize = true, Margin = new Padding(0, 5, 6, 0) });
            fovQRow.Controls.Add(fovQ);
            tips.SetToolTip(fovQ, FovQualityTip);
            body.Controls.Add(fovQRow);
            fovTick.CheckedChanged += (s, e) => FovTickChanged();
        }

        /// the fields' text and state from the settings (override on/off, the saved values) and the headset's values
        void LoadFov()
        {
            var st = new AppSettings();
            var was = loading; loading = true;
            bool on = testTick || st.FovOverride;
            streamEye = testEye ?? ReadStreamEye();
            fovQ.Value = (int)Math.Round(st.FovRenderQuality * 100);
            fovTick.Enabled = clientFov != null;
            fovTick.Checked = on && clientFov != null;
            fovFields.Visible = clientFov != null;
            if (clientFov == null)
            {
                fovNote.Text = "The headset's field of view is not known yet: connect the headset once with the streamer (it is saved when it connects) and these fields appear.";
                fovShare.Text = "";
            }
            else
            {
                var saved = new[] { st.FovOuter, st.FovInner, st.FovUpper, st.FovLower };
                for (int k = 0; k < 4; k++)
                {
                    var top = Math.Ceiling(clientFov[k] * 100) / 100;
                    fovNum[k].Minimum = (decimal)Math.Max(0.10, Math.Floor(clientFov[k] * 20) / 100);
                    fovNum[k].Maximum = (decimal)top;
                    var v = on && saved[k] > 0 ? saved[k] : testEye != null ? clientFov[k] * new[] { 0.7, 0.9, 0.8, 0.6 }[k] : clientFov[k];   // the UI harness shows a restriction
                    fovNum[k].Value = Math.Max(fovNum[k].Minimum, Math.Min(fovNum[k].Maximum, (decimal)Math.Round(v, 2)));
                    fovNum[k].Enabled = on;
                }
            }
            loading = was;
            FovLabels();
        }

        /// the values the game is told: the fields' (the headset's own, untouched, where a field still shows its value)
        double[] FovValues()
        {
            var v = new double[4];
            for (int k = 0; k < 4; k++) v[k] = Math.Abs((double)fovNum[k].Value - clientFov[k]) < 0.005 ? clientFov[k] : (double)fovNum[k].Value;
            return v;
        }

        void FovLabels()
        {
            if (clientFov == null) return;
            var v = FovValues();
            for (int k = 0; k < 4; k++) fovDeg[k].Text = "= " + (Math.Atan(Math.Min(v[k], clientFov[k])) * 180 / Math.PI).ToString("0.0", CultureInfo.InvariantCulture) + "\u00B0";
            if (!fovTick.Checked)
            {
                fovNote.Text = "Greyed: the headset's own field of view as it reported it (including any FoV scale set in its app). Nothing is changed.";
                fovShare.Text = "";
                fovQRow.Visible = false;
                return;
            }
            fovNote.Text = "Override on: the game is told these values (never more than the headset's). Applies at the next game start.";
            double w0 = clientFov[0] + clientFov[1], h0 = clientFov[2] + clientFov[3];
            double w = Math.Min(v[0], clientFov[0]) + Math.Min(v[1], clientFov[1]), h = Math.Min(v[2], clientFov[2]) + Math.Min(v[3], clientFov[3]);
            fovShare.Text = "The game renders " + (100 * w * h / (w0 * h0)).ToString("0", CultureInfo.InvariantCulture) + " % of the pixels the headset's whole view would.";
            QualityLabel(w / w0, h / h0);   // appends its line to fovShare
        }

        /// the render quality row: floor and ceiling from the stream's size and the restriction, the size at the slider; hidden unless a restriction is on
        void QualityLabel(double wr, double hr)
        {
            bool restricted = wr < 0.995 || hr < 0.995;
            bool show = streamEye != null && fovTick.Checked && restricted;   // not read back from Visible: it is false until the window is shown
            fovQRow.Visible = show;
            if (!show) return;
            var floor = GameRenderSize(streamEye[0], streamEye[1], wr, hr, 0);
            var now = GameRenderSize(streamEye[0], streamEye[1], wr, hr, fovQ.Value / 100.0);
            double mpx(int[] z) { return z[0] * (double)z[1] / 1e6; }
            fovShare.Text += "\r\nIn that view the game renders " + now[0] + " \u00D7 " + now[1] + " per eye (" + mpx(now).ToString("0.0", CultureInfo.InvariantCulture) + " Mpx, " + (100 * mpx(now) / mpx(new[] { streamEye[0], streamEye[1] })).ToString("0", CultureInfo.InvariantCulture)
                + " % of an unrestricted game's). Left end of the slider: " + floor[0] + " \u00D7 " + floor[1] + ", right end: " + streamEye[0] + " \u00D7 " + streamEye[1] + " (the stream's own size).";
        }

        void FovStore(AppSettings st)
        {
            if (fovTick.Checked)
            {
                Json.Set(st.Root, V + "fov_override", true);
                var v = FovValues();
                for (int k = 0; k < 4; k++) Json.Set(st.Root, V + FovKeys[k], Math.Round(v[k], 4));
                Json.Set(st.Root, V + "fov_render_quality", Math.Round(fovQ.Value / 100.0, 2));
            }
            else Json.Remove(st.Root, V + "fov_override");   // the saved values stay: ticking again brings them back
        }

        void FovTickChanged()
        {
            if (loading || clientFov == null) return;
            var st = new AppSettings();
            if (fovTick.Checked)   // opens from what the headset reports, or the values saved last time
            {
                var was = loading; loading = true;
                var saved = new[] { st.FovOuter, st.FovInner, st.FovUpper, st.FovLower };
                for (int k = 0; k < 4; k++)
                {
                    var v = saved[k] > 0 ? saved[k] : clientFov[k];
                    fovNum[k].Value = Math.Max(fovNum[k].Minimum, Math.Min(fovNum[k].Maximum, (decimal)Math.Round(v, 2)));
                    fovNum[k].Enabled = true;
                }
                loading = was;
            }
            else foreach (var n in fovNum) n.Enabled = false;
            FovStore(st);
            Save(st, "field of view override " + (fovTick.Checked ? "on" : "off"), NextGame);
            if (!fovTick.Checked) LoadFov(); else FovLabels();
        }

        void FovQualityChanged()
        {
            if (loading || clientFov == null) return;
            FovLabels();
            if (!fovTick.Checked) return;
            var st = new AppSettings();
            FovStore(st);
            Save(st, "game render quality " + fovQ.Value + " %", NextGame);
        }

        void FovValueChanged()
        {
            if (loading || clientFov == null) return;
            FovLabels();
            if (!fovTick.Checked) return;
            var st = new AppSettings();
            FovStore(st);
            Save(st, "field of view " + string.Join(" ", FovValues().Select(x => x.ToString("0.00", CultureInfo.InvariantCulture))), NextGame);
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
            LoadFov();
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
            Json.Remove(st.Root, V + "fov_override");
            foreach (var fk in FovKeys) Json.Remove(st.Root, V + fk);
            Json.Remove(st.Root, V + "fov_render_quality");
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
