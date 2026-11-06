// uitest.exe (harness/ui_matrix.py): builds every window, dialog and wizard page of the GUI, pretends the display is each of a list of
// resolutions at a Windows scale, and audits the layout: does the window fit the work area, is any control clipped by its parent, do siblings
// overlap, is text cut off (buttons, labels, list headers and cells). Writes a PNG per case and results.json. Not shipped.
//
//   uitest.exe --out <dir> [--matrix 1920x1080@100,1920x1080@125,...] [--only <form name>]
//
// Emulation: the forms are built and shown (off screen) at the real display's dpi, which is what the framework scales them to; then everything
// is scaled by target/real (sizes, margins, fonts) and re-laid out. List columns are not scaled by the framework: the code gives them through
// Ui.Col, which follows Ui.DpiOverride, so a column that was left in raw pixels shows up as cut-off text. Close to the real thing, not the real
// thing: real Windows lays out from scratch at the new dpi; a final look on a real display at each scale is still worth it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace AirFrameXR
{
    /// The suggestions panel on its own, fed with fixture metrics and the fixture logs (what the results page shows below the verdict).
    class AdviceHost : DpiForm
    {
        public AdviceHost()
        {
            Text = "AirFrameXR - suggestions"; Font = Ui.Body; BackColor = Color.White; Icon = Ui.AppIcon();
            var res = new Results { EncodeP95 = 9.0, SceneRan = true, HeadSamplesPerS = 60, HeadGapP95Ms = 22, SameTsPct = 9 };
            var panel = Advice.Panel(Advice.From(res, new Handshake { Rates = new List<double> { 90, 100 } }, 90, true, LogFacts.Read(Paths.Logs, Games.Dir), null));
            var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
            col.Controls.Add(panel);
            col.Dock = DockStyle.None; col.Location = Point.Empty;
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            scroller.Controls.Add(col); Controls.Add(scroller);
            Ui.AutoFitWindow(this, null, col);
        }
    }

    /// a profile with a measured window, as the shipped Vision Pro one has (profiles\apple-vision-pro.profile.json), for the Advanced window's button
    /// The launcher's FOV card on its own, with a pretend headset box and the harness's own profile file (ui_matrix cases and the self-tests).
    class FovPanelTestForm : DpiForm
    {
        public readonly FovRestrictionPanel Panel;
        public FovPanelTestForm(double[] client, bool tick, int[] eye, bool fit, string store)
        {
            Text = "AirFrameXR \u2013 FOV restriction (test)"; Font = Ui.Body; BackColor = Color.White; StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(12); Width = 760; Height = 600;
            Ui.FitToWorkArea(this);
            Panel = new FovRestrictionPanel(client, tick, eye, fit, store) { Dock = DockStyle.Top };
            Controls.Add(Panel);
            Ui.ScreenshotWhenShown(this, 1200);
        }
    }

    static class UiTestProfiles
    {
        public static HmdProfile WithMeasuredFov() => HmdProfiles.Parse("{\"id\":\"apple-vision-pro-test\",\"display_name\":\"Apple Vision Pro (ALVR visionOS client)\",\"match\":{\"name_contains\":[\"Apple\"]},\"stream\":{\"eye_resolution\":[3552,3200]},\"network\":{\"default\":\"a\",\"variants\":[{\"id\":\"a\",\"label\":\"a\",\"bitrate_mbps\":300}]},\"fov\":{\"visible_deg\":{\"outer\":40.3,\"inner\":40.0,\"upper\":39.0,\"lower\":34.4},\"margin_deg\":3,\"render_quality\":0.5,\"client_fov_scale\":0.8,\"note\":\"test\"}}", "test")[0];
    }

    static class UiTest
    {
        // harness/recorder_check.py: the recorder's pieces that need no headset: the engine files are cut to the recording, the hotkeys register (F8 registered, F12 polled), the
        // recorder's own settings survive, the analyzer's recording folder test
        static int RecorderSelfTest(string outDir)
        {
            var lines = new List<string>();
            Action<bool, string> check = (ok, what) => lines.Add("CHECK " + (ok ? "PASS " : "FAIL ") + what);
            var tmp = Path.Combine(Path.GetTempPath(), "recorder_selftest_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var dbg = Path.Combine(tmp, "debug"); var rec = Path.Combine(tmp, "rec");
            Directory.CreateDirectory(dbg); Directory.CreateDirectory(rec);
            var hf = new List<string> { "frame,ts_ns,slot,t_boundary,t_arrival,t_encode_start,encode_ms,t_send,hold_ms,bytes,idr" };
            for (int i = 0; i < 6000; i++) { double t = 1000.0 + i / 90.0; hf.Add(i + ",0,0," + t.ToString("F6", CultureInfo.InvariantCulture) + "," + t.ToString("F6", CultureInfo.InvariantCulture) + "," + t.ToString("F6", CultureInfo.InvariantCulture) + ",3.000," + t.ToString("F6", CultureInfo.InvariantCulture) + ",1.000,100,0"); }
            File.WriteAllLines(Path.Combine(dbg, "host_frames.csv"), hf);
            File.WriteAllLines(Path.Combine(dbg, "client_graph.csv"), new[] { "t_recv,total_ms", "1010.000000,50", "1020.000000,51", "1090.000000,52" });
            File.WriteAllText(Path.Combine(dbg, "notes.txt"), "not copied");
            File.WriteAllText(Path.Combine(rec, "session.json"), "{\n  \"started_qpc_s\": 1010.000000,\n  \"ended_qpc_s\": 1030.000000,\n  \"complete\": true\n}\n");
            long frames = RuntimeForm.BenchCopyEngineFiles(dbg, rec);
            check(frames > 1700 && frames < 1900, "the copy keeps the frames inside the recording (20 s at 90 Hz +- the 0.5 s margins): " + frames);
            check(File.ReadAllLines(Path.Combine(rec, "host_frames.csv"))[0].StartsWith("frame,ts_ns"), "the header is kept");
            var cg = File.ReadAllLines(Path.Combine(rec, "client_graph.csv"));
            check(cg.Length == 3, "client_graph.csv is cut to the window: " + (cg.Length - 1) + " rows");
            check(!File.Exists(Path.Combine(rec, "notes.txt")), "files that are not per-frame records are not copied");
            using (var h = new BenchHotkey())
            {
                bool f8 = h.Set(8, false); int err = h.LastError;
                if (!f8 && err == 1459) lines.Add("CHECK SKIP hotkey registration: this session has no interactive desktop (win32 error 1459, ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION); not tested here");
                else
                {
                    check(f8, "F8 registers as a global hotkey (win32 error " + h.LastError + ")");
                    check(h.Set(8, true), "Ctrl+F8 registers (win32 error " + h.LastError + ")");
                    check(h.Set(7, false), "F7 registers (win32 error " + h.LastError + ")");
                }
                check(h.Set(12, false), "F12 is accepted (polled: a reserved key cannot be registered)");
            }
            { // the runtime switch: when the Black Box is not the active runtime there is nothing to put back, and the remembered state is cleared (no elevation needed)
                var o2 = BenchRecorder.Own(); global::AirFrameXR.Json.Set(o2, "runtime_switched", true); global::AirFrameXR.Json.Set(o2, "runtime_before", "C:\\x\\other.json"); BenchRecorder.SaveOwn(o2);
                bool before = BenchRuntime.Switched; bool ok = BenchRuntime.PutBack();
                check(!OpenXr.IsOurs() && before && ok && !BenchRuntime.Switched && BenchRuntime.Before.Length == 0, "put back with nothing to undo clears the remembered runtime without asking Windows for anything");
            }
            var own = BenchRecorder.Own(); global::AirFrameXR.Json.Set(own, "scene", "selftest"); BenchRecorder.SaveOwn(own);
            check(global::AirFrameXR.Json.Str(BenchRecorder.Own(), "scene", "") == "selftest", "the recorder's own settings file round-trips (config\\bench.json)");
            try { Directory.Delete(tmp, true); } catch { }
            File.WriteAllLines(Path.Combine(outDir, "recorder.txt"), lines);
            return lines.Any(l => l.StartsWith("CHECK FAIL")) ? 1 : 0;
        }

        class Case { public string Name; public Func<Form> Make; public Action<Form> Prepare; }
        static readonly List<string> Json = new List<string>();

        static IEnumerable<Case> Cases()
        {
            yield return new Case { Name = "launcher", Make = () => new LauncherForm() };
            yield return new Case { Name = "launcher-ready", Make = () => new LauncherForm(true) };
            yield return new Case { Name = "streamer", Make = () => new RuntimeForm(null), Prepare = f => ((RuntimeForm)f).ApplyDemoState() };
            yield return new Case { Name = "streamer-clean", Make = () => new RuntimeForm(null), Prepare = f => ((RuntimeForm)f).ApplyDemoState(false) };
            yield return new Case { Name = "streamer-advanced", Make = () => new RuntimeForm(null), Prepare = f => { ((RuntimeForm)f).ApplyDemoState(false); ((RuntimeForm)f).UiAction("advanced", "on"); } };
            yield return new Case { Name = "streamer-empty", Make = () => new RuntimeForm(null) };
            yield return new Case { Name = "streamer-avp-auto", Make = () => new RuntimeForm(null), Prepare = f => ((RuntimeForm)f).ApplyDemoState(false) };   // the fixture config has no height set: a Vision Pro starts on Auto
            yield return new Case { Name = "streamer-quest", Make = () => new RuntimeForm(null), Prepare = f => { ((RuntimeForm)f).ApplyDemoState(false); ((RuntimeForm)f).HostEvent("client_connected", new Dictionary<string, object> { { "name", "Meta Quest 3" }, { "ip", "192.168.1.60" } }); } };
            yield return new Case { Name = "streamer-audio-bad", Make = () => new RuntimeForm(null), Prepare = f => { ((RuntimeForm)f).ApplyDemoState(true); ((RuntimeForm)f).DemoAudio(true); } };
            yield return new Case { Name = "streamer-debug-on", Make = () => new RuntimeForm(null), Prepare = f => { ((RuntimeForm)f).ApplyDemoState(true); ((RuntimeForm)f).DemoDebugOn(); } };
            yield return new Case { Name = "streamer-headset-seen", Make = () => new RuntimeForm(null), Prepare = f => { ((RuntimeForm)f).ApplyDemoState(false); ((RuntimeForm)f).HostEvent("client_disconnected", new Dictionary<string, object>()); ((RuntimeForm)f).HostEvent("headset_seen", new Dictionary<string, object> { { "hostname", "1234.client.alvr" }, { "ip", "192.168.1.50" } }); } };
            yield return new Case { Name = "streamer-headset-lost", Make = () => new RuntimeForm(null), Prepare = f => { ((RuntimeForm)f).ApplyDemoState(false); ((RuntimeForm)f).HostEvent("client_disconnected", new Dictionary<string, object>()); } };
            yield return new Case { Name = "advanced-switches", Make = () => new AdvancedForm() };
            var avpBox = new[] { 1.045, 0.800, 0.759, 0.871 };   // the AirFrameXR client's declared box
            yield return new Case { Name = "fov-panel-off", Make = () => new FovPanelTestForm(avpBox, false, new[] { 3552, 3200 }, false, FovManagerForm.DemoStore()) };
            yield return new Case { Name = "fov-panel-on", Make = () => new FovPanelTestForm(avpBox, true, new[] { 3552, 3200 }, false, FovManagerForm.DemoStore()) };
            yield return new Case { Name = "fov-panel-fit", Make = () => new FovPanelTestForm(avpBox, true, new[] { 3552, 3200 }, true, FovManagerForm.DemoStore()) };   // the stream fit on, the fold open: the quality row and the preview
            yield return new Case { Name = "fov-panel-empty", Make = () => new FovPanelTestForm(avpBox, false, new[] { 3552, 3200 }, false, Path.Combine(Path.GetTempPath(), "airframexr_fovpanel_empty_" + Guid.NewGuid().ToString("N") + ".json")) };
            yield return new Case { Name = "rec-idle", Make = () => { BenchRecorder.Enabled = true; try { return new RuntimeForm(null); } finally { BenchRecorder.Enabled = false; } }, Prepare = f => { ((RuntimeForm)f).ApplyDemoState(false); ((RuntimeForm)f).BenchDemoState("idle"); } };
            yield return new Case { Name = "rec-full-waiting", Make = () => { BenchRecorder.Enabled = true; try { return new RuntimeForm(null); } finally { BenchRecorder.Enabled = false; } }, Prepare = f => { ((RuntimeForm)f).ApplyDemoState(false); ((RuntimeForm)f).BenchDemoState("full-waiting"); } };
            yield return new Case { Name = "rec-recording", Make = () => { BenchRecorder.Enabled = true; try { return new RuntimeForm(null); } finally { BenchRecorder.Enabled = false; } }, Prepare = f => { ((RuntimeForm)f).ApplyDemoState(false); ((RuntimeForm)f).BenchDemoState("recording"); } };
            yield return new Case { Name = "rec-saved", Make = () => { BenchRecorder.Enabled = true; try { return new RuntimeForm(null); } finally { BenchRecorder.Enabled = false; } }, Prepare = f => { ((RuntimeForm)f).ApplyDemoState(false); ((RuntimeForm)f).BenchDemoState("saved"); } };
            yield return new Case { Name = "rec-key-taken", Make = () => { BenchRecorder.Enabled = true; try { return new RuntimeForm(null); } finally { BenchRecorder.Enabled = false; } }, Prepare = f => { ((RuntimeForm)f).ApplyDemoState(false); ((RuntimeForm)f).BenchDemoState("key-taken"); } };
            yield return new Case { Name = "analyze-empty", Make = () => new AnalyzeForm(), Prepare = f => ((AnalyzeForm)f).ApplyDemoState("empty") };
            yield return new Case { Name = "analyze-unanalyzed", Make = () => new AnalyzeForm(), Prepare = f => ((AnalyzeForm)f).ApplyDemoState("unanalyzed") };
            yield return new Case { Name = "analyze-busy", Make = () => new AnalyzeForm(), Prepare = f => ((AnalyzeForm)f).ApplyDemoState("busy") };
            yield return new Case { Name = "analyze-smooth", Make = () => new AnalyzeForm(), Prepare = f => ((AnalyzeForm)f).ApplyDemoState("smooth") };
            yield return new Case { Name = "analyze-problem", Make = () => new AnalyzeForm(), Prepare = f => ((AnalyzeForm)f).ApplyDemoState("problem") };
            yield return new Case { Name = "analyze-insufficient", Make = () => new AnalyzeForm(), Prepare = f => ((AnalyzeForm)f).ApplyDemoState("insufficient") };
            yield return new Case { Name = "analyze-problem-tall", Make = () => new AnalyzeForm(), Prepare = f => { ((AnalyzeForm)f).ApplyDemoState("problem"); f.Height = Ui.WorkArea(f).Height - 40; } };   // the README picture: the window as tall as the screen allows, so the table and the charts show together
            yield return new Case { Name = "analyze-long", Make = () => new AnalyzeForm(), Prepare = f => ((AnalyzeForm)f).ApplyDemoState("long") };
            yield return new Case { Name = "calibrate-result", Make = () => new CalibrateForm(Path.Combine(Path.GetTempPath(), "airframexr_calibrate_demo_" + Guid.NewGuid().ToString("N"))), Prepare = f => ((CalibrateForm)f).ApplyDemoResult() };   // the README picture: the rays, the window, the rectangle
            yield return new Case { Name = "performance", Make = () => new PerformanceForm("Cyberpunk2077.exe") };
            yield return new Case { Name = "performance-pipeline", Make = () => new PerformanceForm("LiesOfP-Win64-Shipping.exe") };   // a record whose pipeline block says split encoding did not engage
            for (int p = 0; p <= 7; p++)
            {
                int page = p;
                yield return new Case { Name = "wizard-page" + page, Make = () => new WizardForm(null, true), Prepare = f => ((WizardForm)f).ForTest(page) };
            }
            // docs/PICTURE_EDGES_DESIGN.md section 9: the field-of-view step as a Quest user sees it (the restriction only, no headset calibration)
            yield return new Case { Name = "wizard-page7-quest", Make = () => new WizardForm(null, true), Prepare = f => { WizardForm.DemoHeadset = "quest3"; try { ((WizardForm)f).ForTest(7); } finally { WizardForm.DemoHeadset = null; } } };
            yield return new Case { Name = "configure-advanced", Make = () => new ConfigureForm(null) };
            var quest3 = new[] { 1.376, 0.839, 0.966, 1.428 };
            yield return new Case { Name = "fov-manager", Make = () => new FovManagerForm(FovManagerForm.DemoStore(), new[] { 1.045, 0.800, 0.759, 0.871 }) };
            yield return new Case { Name = "fov-manager-red", Make = () => new FovManagerForm(FovManagerForm.DemoStore(), quest3), Prepare = f => ((FovManagerForm)f).Card.TypeForTest("Degrees", "95", "40", "43", "54") };
            yield return new Case { Name = "fov-manager-yellow", Make = () => new FovManagerForm(FovManagerForm.DemoStore(), quest3), Prepare = f => ((FovManagerForm)f).Card.TypeForTest("Tangents", "1.3", "0.5", "0.9", "1.0") };
            yield return new Case { Name = "fov-manager-valid", Make = () => new FovManagerForm(FovManagerForm.DemoStore(), quest3), Prepare = f => ((FovManagerForm)f).Card.TypeForTest("Degrees", "51", "40", "43", "54") };
            yield return new Case { Name = "fov-manager-per-eye", Make = () => new FovManagerForm(FovManagerForm.DemoStore(), quest3), Prepare = f => ((FovManagerForm)f).Card.PerEyeForTest("Degrees", new[] { "51", "40", "43", "54" }, new[] { "49", "42", "43", "52" }) };   // schema 2: the right eye on its own row
            yield return new Case { Name = "calibrate-fit", Make = () => new CalibrateForm(Path.Combine(Path.GetTempPath(), "airframexr_calibrate_demo_" + Guid.NewGuid().ToString("N"))) };   // the page calibrate.exe opens on: Fit the picture to your eyes
            yield return new Case { Name = "fov-manager-empty", Make = () => new FovManagerForm(Path.Combine(Path.GetTempPath(), "airframexr_fov_empty_" + Guid.NewGuid().ToString("N") + ".json"), quest3) };
            yield return new Case { Name = "find-headset", Make = () => new FindHeadsetForm(Tuple.Create("1234.client.alvr", "Apple Vision Pro", "192.168.100.123")) };
            yield return new Case { Name = "cable-missing", Make = () => new CableForm() };
            yield return new Case { Name = "chroma-step", Make = () => new ChromaStepForm() };
            yield return new Case { Name = "openxr-layers", Make = () => new LayersForm() };
            yield return new Case { Name = "suggestions", Make = () => new AdviceHost() };
            yield return new Case { Name = "firewall-rules", Make = () => new FirewallForm(DemoFirewall()) };
        }

        // harness/advice_check.py: every suggestion rule against synthetic wizard metrics and fixture logs; a clean setup must stay quiet
        static int AdviceSelfTest(string outDir)
        {
            var o = new StringBuilder(); int fails = 0;
            void check(string name, bool ok, string detail = "") { if (!ok) fails++; o.AppendLine($"CHECK {(ok ? "PASS" : "FAIL")} {name}" + (ok ? "" : ": " + detail)); }
            string Titles(List<Suggestion> l) => string.Join(" | ", l.Select(x => x.Sev + ": " + x.Title));
            var hs = new Handshake { Rates = new List<double> { 90, 100 } };
            // fixture logs: a noisy link, a laptop GPU in P8, a long arrival spread, four games
            var root = Path.Combine(Path.GetTempPath(), "advice_" + Guid.NewGuid().ToString("N"));
            string MakeLogs(string name, double encP95, double gap95, int over15, double spread, string pstate, double videoMhz, double gpuPct)
            {
                var d = Path.Combine(root, name, "logs", "debug", "20261019-120000"); Directory.CreateDirectory(d);
                var ev = new List<string>();
                for (int i = 0; i < 6; i++)
                {
                    ev.Add("{\"data\":{\"fps\":88.0,\"encode_ms_p95\":" + encP95.ToString(CultureInfo.InvariantCulture) + ",\"tracking_gap_ms\":{\"p95\":" + gap95.ToString(CultureInfo.InvariantCulture) + ",\"over_15ms\":" + over15 + ",\"n\":500}},\"event\":\"encoder_stats\"}");
                    ev.Add("{\"data\":{\"spread_ms\":" + spread.ToString(CultureInfo.InvariantCulture) + ",\"period_ms\":11.1,\"est_ms\":3.0},\"event\":\"phase_lock\"}");
                }
                ev.Add("{\"data\":{\"fps\":30.0,\"encode_ms_p95\":2.0},\"event\":\"encoder_stats\"}");   // a menu window: ignored
                File.WriteAllLines(Path.Combine(d, "host_events.jsonl"), ev);
                var gpu = new List<string> { "time,gpu_pct,mem_pct,enc_pct,dec_pct,graphics_mhz,memory_mhz,video_mhz,power_w,temp_c,pstate,vram_used_mb,clock_event_reasons" };
                for (int i = 0; i < 8; i++) gpu.Add($"12:00:0{i}.000,{gpuPct.ToString(CultureInfo.InvariantCulture)},10,5,0,300,810,{videoMhz.ToString(CultureInfo.InvariantCulture)},20,40,{pstate},900,0x1");
                File.WriteAllLines(Path.Combine(d, "gpu.csv"), gpu);
                return Path.Combine(root, name, "logs");
            }
            string MakeGames(string name, params string[] games)
            {
                var d = Path.Combine(root, name, "config", "games"); Directory.CreateDirectory(d);
                for (int i = 0; i < games.Length; i++) File.WriteAllText(Path.Combine(d, "g" + i + ".json"), games[i]);
                return d;
            }
            string Game(string exe, string name, string level, double fps, double rep, double hostRep, long start) =>
                "{\"exe\":\"" + exe + "\",\"name\":\"" + name + "\",\"sessions\":[{\"start\":" + start + ",\"headset\":\"Apple Vision Pro\",\"level\":\"" + level + "\",\"display_hz\":90,\"rendered_fps\":" + fps.ToString(CultureInfo.InvariantCulture) +
                ",\"headset_repeated_typ\":" + (rep / 100).ToString(CultureInfo.InvariantCulture) + ",\"headset_repeated_p90\":" + (rep / 50).ToString(CultureInfo.InvariantCulture) + ",\"host_repeats_typ\":" + (hostRep / 100).ToString(CultureInfo.InvariantCulture) +
                ",\"net\":{\"latency_ms\":60}}]}";
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var clean = Advice.From(new Results { EncodeP95 = 4, SceneRan = true, HeadSamplesPerS = 270, HeadGapP95Ms = 5, SameTsPct = 0.5 }, hs, 90, false, new LogFacts());
            check("a clean setup raises nothing above Info", clean.All(x => x.Sev == Sev.Info), Titles(clean));
            var none = Advice.From(null, null, 90, false, null);
            check("no measurement at all: an empty list, no crash", none.Count == 0, Titles(none));

            var slow = Advice.From(new Results { EncodeP95 = 9.0 }, hs, 90, false, new LogFacts());
            check("a slow encoder is flagged with its numbers", slow.Any(x => x.Sev == Sev.Warn && x.Title.Contains("encoder") && x.Measured.Contains("9") && x.Source.Contains("wizard")), Titles(slow));
            var wifi = Advice.From(new Results { EncodeP95 = 4, SceneRan = true, HeadSamplesPerS = 60, HeadGapP95Ms = 22 }, hs, 90, false, new LogFacts());
            check("bursty tracking from the scene test points at the Wi-Fi", wifi.Any(x => x.Sev == Sev.Warn && x.Title.Contains("bursts") && x.Source.Contains("scene test")), Titles(wifi));
            var same = Advice.From(new Results { EncodeP95 = 4, SceneRan = true, HeadSamplesPerS = 270, HeadGapP95Ms = 5, SameTsPct = 9 }, hs, 90, false, new LogFacts());
            check("repeated head samples are explained (known Vision Pro issue)", same.Any(x => x.Title.Contains("same head sample")), Titles(same));

            // logs: noisy link, long spread, laptop GPU, debug on, games
            var logs = MakeLogs("noisy", 9.5, 21, 40, 11.0, "P8", 555, 10);
            var games = MakeGames("noisy",
                Game("a.exe", "Game A", "off", 55, 12, 0, now - 400),
                Game("b.exe", "Game B", "half", 44, 0.4, 20, now - 300),
                Game("c.exe", "Game C", "half", 44, 0.5, 0.2, now - 200),
                Game("d.exe", "Game D", "third", 30, 7, 0.2, now - 100));
            var f = LogFacts.Read(logs, games);
            check("the newest debug session with a game streaming is read", f.Windows == 6 && Math.Abs(f.EncodeP95 - 9.5) < 0.01 && Math.Abs(f.SpreadMed - 11.0) < 0.01 && f.GpuSamples == 8, $"windows {f.Windows} enc {f.EncodeP95} spread {f.SpreadMed} gpu samples {f.GpuSamples}");
            check("the four newest games are read, newest first", f.Games.Count == 4 && f.Games[0].Name == "Game D", string.Join(",", f.Games.Select(g => g.Name)));
            bool off = false;
            var all = Advice.From(null, hs, 90, true, f, () => off = true);
            check("logs: the encoder headroom", all.Any(x => x.Title.Contains("encoder") && x.Source.Contains("logs")), Titles(all));
            check("logs: bursty tracking", all.Any(x => x.Title.Contains("bursts") && x.Source.Contains("logs")), Titles(all));
            check("logs: arrival margin 9 proposed for an 11 ms spread", all.Any(x => x.Title.Contains("try 9") && x.Apply != null && x.ApplyText.Contains("9 ms")), Titles(all));
            check("logs: the GPU in P8 gets the keepalive", all.Any(x => x.Title.Contains("low power state") && x.Apply != null), Titles(all));
            var dbg = all.FirstOrDefault(x => x.Title.Contains("Debug logging is on") && x.Apply != null); dbg?.Apply();   // by its title: other suggestions with a button come first
            check("debug logging: a button that turns it off", dbg != null && off, Titles(all));
            check("game A (55 fps raw, headset repeats 12 %): frame generation suggested", all.Any(x => x.Title.Contains("Game A") && x.Title.Contains("frame generation")), Titles(all));
            check("game B (host repeats 20 % at 1/2): a lower rate", all.Any(x => x.Title.Contains("Game B") && x.Do.Contains("lower rate")), Titles(all));
            check("every suggestion says what it measured and where from", all.All(x => x.Measured.Length > 0 && x.Source.Length > 0 && x.Title.Length > 0), "");
            check("warnings come first", all.Count > 1 && (int)all[0].Sev >= (int)all[all.Count - 1].Sev, Titles(all).Substring(0, Math.Min(120, Titles(all).Length)));

            var fine = LogFacts.Read(MakeLogs("fine", 4.0, 6, 3, 8.0, "P0", 1500, 55), MakeGames("fine"));
            var good = Advice.From(null, hs, 90, false, fine);
            check("a healthy session: margin default fits, nothing above Info", good.Any(x => x.Title.Contains("default 7 ms fits")) && good.All(x => x.Sev == Sev.Info), Titles(good));

            File.WriteAllText(Path.Combine(outDir, "advice.txt"), o.ToString());
            return fails == 0 ? 0 : 1;
        }

        static FwReport DemoFirewall()
        {
            var r = new FwReport { State = FirewallState.Allowed, Headline = "Windows Firewall lets this folder's alvr_host.exe in. 3 leftover rule(s) can be removed." };
            r.Rules.Add(new FwRule { Name = "{1}", DisplayName = "AirFrameXR (alvr_host)", Direction = "Inbound", Action = "Allow", Profile = "Any", Protocol = "Any", Enabled = true, InPlace = true, Status = "in place: lets this folder in", Program = @"C:\Users\Someone With A Long Name\Downloads\AirFrameXR-0.5\AirFrameXR\bin\alvr_host.exe" });
            r.Rules.Add(new FwRule { Name = "{2}", DisplayName = "alvr_host.exe", Direction = "Inbound", Action = "Allow", Profile = "Public", Protocol = "TCP", Enabled = true, Propose = true, Status = "redundant: the rule above already covers all protocols", Program = @"C:\x\alvr_host.exe" });
            r.Rules.Add(new FwRule { Name = "{3}", DisplayName = "alvr_host.exe", Direction = "Inbound", Action = "Allow", Profile = "Public", Protocol = "UDP", Enabled = true, Propose = true, Status = "stale: the file is gone (C:\\Old\\AirFrameXR\\bin\\alvr_host.exe)", Program = @"C:\Old\alvr_host.exe" });
            r.Rules.Add(new FwRule { Name = "{4}", DisplayName = "ALVR Streamer (other copy)", Direction = "Inbound", Action = "Allow", Profile = "Private, Public", Protocol = "Any", Enabled = true, Status = "another copy of alvr_host.exe (left alone)", Program = @"D:\ALVR\alvr_host.exe" });
            return r;
        }

        /// Text of every visible control, one line each, in tree order: what a person would read in the window.
        static void DumpControls(Control c, StringBuilder o, int depth)
        {
            if (!c.Visible) return;
            var t = (c is Label || c is ButtonBase || c is GroupBox) ? c.Text : "";
            string kind = c is CheckBox cb ? "check[" + (cb.Checked ? "x" : " ") + "]" : c is RadioButton ? "radio" : c is Button ? "button" : c is LinkLabel ? "link" : c is Label ? "label" : c is GroupBox ? "group" : c.GetType().Name;
            if (c is ToggleSwitch ts) o.AppendLine(new string(' ', depth) + "switch " + (c.AccessibleName ?? "") + " " + (ts.Checked ? "on" : "off"));
            else if (t.Length > 0) o.AppendLine(new string(' ', depth) + kind + " " + t.Replace("\r", "").Replace("\n", " / "));
            if (c is ListBox lb) foreach (var it in lb.Items) o.AppendLine(new string(' ', depth) + "item " + it);
            if (c is ListView lv) foreach (ListViewItem r in lv.Items) o.AppendLine(new string(' ', depth) + "row " + string.Join("|", r.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x => x.Text)));
            foreach (Control k in c.Controls) DumpControls(k, o, depth + 1);
        }

        /// harness/performance_check.py: the Game statistics window on the fixture records, as text.
        static int DumpPerformance(string game, string outDir)
        {
            var o = new StringBuilder();
            using (var f = new PerformanceForm(game))
            {
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.ShowInTaskbar = false; f.Opacity = 0;
                f.Show();
                DumpControls(f, o, 0);
            }
            File.WriteAllText(Path.Combine(outDir, "performance.txt"), o.ToString());
            return 0;
        }

        /// One JSON object per line: {"event":...,"data":{...}} goes to RuntimeForm.HostEvent (the function the live host drives); {"dump":"label"} writes the window's text
        /// under that label. The last state is always dumped, and a screenshot written.
        static int Replay(string file, string outDir)
        {
            var o = new StringBuilder();
            Ui.WorkAreaOverride = new Rectangle(0, 0, 3840, 2100);   // a big pretend screen: window sizes are the content's, never the builder's screen
            using (var f = new RuntimeForm(null))
            {
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.ShowInTaskbar = false; f.Opacity = 0;
                f.Show();   // no message loop: Shown (which would start the streamer) never runs
                void dump(string label) { o.AppendLine("== " + label); o.AppendLine("clientsize " + f.ClientSize.Width + "x" + f.ClientSize.Height + " at " + f.Location.X + "," + f.Location.Y); DumpControls(f, o, 0); }
                int n = 0;
                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
                    var d = global::AirFrameXR.Json.Parse(line);
                    n++;
                    if (d.TryGetValue("dump", out var lab)) { dump(Convert.ToString(lab)); foreach (var c in f.Sent) o.AppendLine("sent " + c); continue; }
                    if (d.TryGetValue("ui", out var ui)) { f.Sent.Clear(); f.UiAction(Convert.ToString(ui), d.TryGetValue("value", out var uv) ? Convert.ToString(uv) : ""); continue; }
                    if (!d.TryGetValue("event", out var ev)) continue;
                    f.HostEvent(Convert.ToString(ev), d.TryGetValue("data", out var x) ? x as Dictionary<string, object> : null);
                }
                dump("end");
                o.AppendLine("range height " + f.HeightMin.ToString(CultureInfo.InvariantCulture) + " " + f.HeightMax.ToString(CultureInfo.InvariantCulture));
                try { using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(Path.Combine(outDir, "replay.png"), ImageFormat.Png); } } catch { }
            }
            File.WriteAllText(Path.Combine(outDir, "replay.txt"), o.ToString());
            return 0;
        }

        // harness/center_check.py: a window that starts centred (CenterScreen) is centred on its screen's work area once its content has sized it
        static int CenterSelfTest(string outDir)
        {
            var o = new StringBuilder(); int fails = 0;
            foreach (var make in new Func<Form>[] { () => new RuntimeForm(null), () => new PerformanceForm("Cyberpunk2077.exe") })
                using (var f = make())
                {
                    f.Opacity = 0; f.ShowInTaskbar = false; f.Show();
                    var wa = Screen.FromControl(f).WorkingArea;
                    int dx = (f.Left + f.Width / 2) - (wa.Left + wa.Width / 2), dy = (f.Top + f.Height / 2) - (wa.Top + wa.Height / 2);
                    bool ok = Math.Abs(dx) <= 2 && (Math.Abs(dy) <= 2 || f.Height >= wa.Height - 2) && f.Left >= wa.Left && f.Top >= wa.Top && f.Right <= wa.Right && f.Bottom <= wa.Bottom;
                    if (!ok) fails++;
                    o.AppendLine($"CHECK {(ok ? "PASS" : "FAIL")} {f.GetType().Name}: window {f.Bounds}, work area {wa}, off centre ({dx},{dy})");
                }
            File.WriteAllText(Path.Combine(outDir, "center.txt"), o.ToString());
            return fails == 0 ? 0 : 1;
        }

        /// docs/PICTURE_EDGES_DESIGN.md sections 6 and 9 (owner's UI review 2026-11-05), checked on the real forms by control names, so the GUI work
        /// of steps A and B has a fixed target. The contract (AccessibleName): "Whole panel" (wizard radio), "Fit now" (wizard button), "Fit the picture
        /// to your eyes" (the Vision Pro card and the manager), "Picture edges" (the Vision Pro card in the launcher, the wizard calls the same card), "Retry"
        /// (calibrate.exe); the FOV restriction lives in Advanced video and performance settings as "Restrict the field of view" (or "What the game renders"),
        /// with its in-headset tool "Choose in the headset" (section 6b) on every headset. Gone from every window: "Calibrate now (30 min...)" and "Check with fovgrid". One key=value line per window
        /// and headset; the expected lines are in the Python checker. Written before the UI: most lines differ until steps A and B are built.
        static int FovUiPlanSelfTest(string outDir)
        {
            var lines = new List<string>();
            IEnumerable<Control> All(Control c) { foreach (Control k in c.Controls) { yield return k; foreach (var d in All(k)) yield return d; } }
            Control Named(Control root, params string[] names) => All(root).FirstOrDefault(c => names.Any(n => string.Equals(c.AccessibleName, n, StringComparison.OrdinalIgnoreCase)));
            bool Has(Control root, params string[] names) => Named(root, names) != null;
            bool On(Control root, params string[] names) { var c = Named(root, names); return c != null && c.Enabled; }
            bool Text(Control root, string part) => All(root).Any(c => (c.Text ?? "").IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
            int TypedBoxes(Control root) => All(root).OfType<TextBox>().Count(t => !string.Equals(t.AccessibleName, "Name of the new profile", StringComparison.OrdinalIgnoreCase));
            string B(bool v) => v ? "True" : "False";
            void Hidden(Form f) { f.Opacity = 0; f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000); f.Show(); Application.DoEvents(); }
            void SetHeadset(string name) { var st = new AppSettings(); if (name == null) global::AirFrameXR.Json.Remove(st.Root, "headset.name"); else global::AirFrameXR.Json.Set(st.Root, "headset.name", name); st.Save(); }
            var saved = new AppSettings().HeadsetName;
            try
            {
                foreach (var (tag, demoHs) in new[] { ("avp", (string)null), ("quest", "quest3") })
                {
                    WizardForm.DemoHeadset = demoHs;
                    using (var w = new WizardForm(null, true))
                    {
                        Hidden(w); w.ForTest(7); Application.DoEvents();
                        lines.Add($"wizard_{tag}: whole_panel={B(Has(w, "Whole panel"))} fit_now={B(On(w, "Fit now"))} ray_calibration={B(Text(w, "Calibrate now") || Text(w, "30 min"))} restriction={B(Has(w, "No restriction", "Restrict to a profile", "What the game renders"))}");
                    }
                    WizardForm.DemoHeadset = null;
                }
                foreach (var (tag, name) in new[] { ("avp", "Apple Vision Pro"), ("quest", "Meta Quest 3"), ("none", (string)null) })
                {
                    SetHeadset(name);
                    using (var m = new FovManagerForm(FovManagerForm.DemoStore(), tag == "quest" ? new[] { 1.376, 0.839, 0.966, 1.428 } : new[] { 1.732, 1.0, 1.192, 1.0 }))
                    {
                        Hidden(m);
                        lines.Add($"manager_{tag}: fit={B(On(m, "Fit the picture to your eyes"))} ray_calibration={B(Text(m, "Calibrate now") || Text(m, "30 min"))} fovgrid_button={B(Text(m, "Check with fovgrid"))} typed_values={B(TypedBoxes(m) >= 4)} per_eye={B(Text(m, "per eye"))}");
                    }
                    // the launcher: the Vision Pro calibration is its own card (the mini app), absent unless a Vision Pro was seen; the FOV restriction is not in the launcher
                    using (var l = new LauncherForm())
                    {
                        Hidden(l);
                        lines.Add($"launcher_{tag}: avp_card={B(Has(l, "Picture edges"))} fit={B(On(l, "Fit the picture to your eyes"))} restriction={B(Has(l, "Restrict the field of view", "What the game renders"))}");
                    }
                    // Advanced video and performance settings: the FOV restriction for every headset
                    using (var a = new AdvancedForm())
                    {
                        Hidden(a);
                        lines.Add($"advanced_{tag}: restriction={B(On(a, "Restrict the field of view", "What the game renders"))} headset_tool={B(On(a, "Choose in the headset"))} picture_edges={B(Has(a, "Picture edges"))}");
                    }
                }
                SetHeadset("Apple Vision Pro");
                // calibrate.exe in its demo mode (no streamer, no probe): the planned window opens on the fit page (Retry, the Space instruction); today's
                // demo result page draws the measured rays (its map control) and must be gone with the sweep
                using (var k = new CalibrateForm(Path.Combine(Path.GetTempPath(), "airframexr_calibrate_plan_" + Guid.NewGuid().ToString("N"))))
                {
                    Hidden(k);
                    var retry = Has(k, "Retry"); var space = Text(k, "SPACE");
                    k.ApplyDemoResult(); Application.DoEvents();
                    lines.Add($"calibrate: retry={B(retry)} fit_instructions={B(space)} ray_sweep={B(Has(k, "Your visible window with the measured rays"))}");
                }
            }
            finally { SetHeadset(string.IsNullOrEmpty(saved) ? null : saved); }
            File.WriteAllLines(Path.Combine(outDir, "fovuiplan.txt"), lines);
            return 0;
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string Arg(string k) { for (int i = 0; i + 1 < args.Length; i++) if (args[i] == k) return args[i + 1]; return null; }
            var outDir = Arg("--out") ?? Path.Combine(Paths.Dir, "uitest");
            Directory.CreateDirectory(outDir);
            var only = Arg("--only");
            if (args.Contains("--advice-selftest")) return AdviceSelfTest(outDir);
            if (args.Contains("--recorder-selftest")) return RecorderSelfTest(outDir);
            if (args.Contains("--center-selftest")) return CenterSelfTest(outDir);
            var replay = Arg("--replay");   // harness/streamer_events.py: feed host events to the streamer window and dump what it shows
            if (replay != null) return Replay(replay, outDir);
            var perfGame = Arg("--dump-performance");
            if (perfGame != null) return DumpPerformance(perfGame, outDir);
            var exportTo = Arg("--export-selftest");   // harness/export_logs.py: the export to a path with no dialog; lists the entries
            if (exportTo != null)
            {
                Log.Write("INFO", "export selftest");   // makes sure a general log exists
                var names = LogExport.Write(exportTo, Arg("--game"));
                File.WriteAllLines(Path.Combine(outDir, "export.txt"), names.Concat(new[] { "open_forms=" + Application.OpenForms.Count }));
                return 0;
            }
            if (Arg("--eye-choice-selftest") != null)   // harness/eye_choice_check.py: a per-eye size saved from Settings reaches both resolution keys, ALVR's negotiated cache and the density reference, and the FOV card reads the setting (not the stale cache)
            {
                var lines = new List<string>(); int bad = 0;
                void Check(bool ok, string what) { lines.Add("CHECK " + (ok ? "PASS" : "FAIL") + " " + what); if (!ok) bad++; }
                var s = new Dictionary<string, object>();
                global::AirFrameXR.Json.Set(s, "openvr_config.eye_resolution_width", 3552.0); global::AirFrameXR.Json.Set(s, "openvr_config.eye_resolution_height", 3200.0);
                global::AirFrameXR.PipelineModel.WriteStreamEye(s, 3552, 3200);
                var st = new AppSettings();   // this folder's own config, never saved here
                st.StreamFitWindow = false;
                var written = global::AirFrameXR.PipelineModel.SaveEyeChoice(s, st, 4096, 3680);
                Check(written[0] == 4096 && written[1] == 3680, "fit off: the size written is the size chosen (" + written[0] + "x" + written[1] + ")");
                foreach (var k in new[] { "transcoding_view_resolution", "emulated_headset_view_resolution" })
                    Check(global::AirFrameXR.Json.Str(s, "session_settings.video." + k + ".variant", "") == "Absolute" && (int)global::AirFrameXR.Json.Num(s, "session_settings.video." + k + ".Absolute.width", 0) == 4096 && (int)global::AirFrameXR.Json.Num(s, "session_settings.video." + k + ".Absolute.height.content", 0) == 3680, k + " = Absolute 4096x3680");
                Check((int)global::AirFrameXR.Json.Num(s, "openvr_config.eye_resolution_width", 0) == 4096 && (int)global::AirFrameXR.Json.Num(s, "openvr_config.eye_resolution_height", 0) == 3680, "ALVR's negotiated cache follows the save (the FOV card and the encoder tests read it)");
                Check(st.StreamReferenceEye != null && st.StreamReferenceEye[0] == 4096 && st.StreamReferenceEye[1] == 3680, "the density reference is the chosen size (the fit tick no longer writes an old size back)");
                var e = global::AirFrameXR.PipelineModel.StreamEyeExpected(s);
                Check(e != null && e[0] == 4096 && e[1] == 3680, "expected stream size = the setting (" + (e == null ? "-" : e[0] + "x" + e[1]) + ")");
                // the setting wins over a stale cache, floored to 32 on both axes like ALVR's align32
                var s2 = new Dictionary<string, object>();
                global::AirFrameXR.PipelineModel.WriteStreamEye(s2, 4030, 3690);
                global::AirFrameXR.Json.Set(s2, "openvr_config.eye_resolution_width", 3552.0); global::AirFrameXR.Json.Set(s2, "openvr_config.eye_resolution_height", 3200.0);
                e = global::AirFrameXR.PipelineModel.StreamEyeExpected(s2);
                Check(e != null && e[0] == 4000 && e[1] == 3680, "Absolute 4030x3690 with a stale cache 3552x3200 reads 4000x3680, floored to 32 like ALVR (" + (e == null ? "-" : e[0] + "x" + e[1]) + ")");
                // no Absolute setting (ALVR's Scale variant): the cache is all there is
                var s3 = new Dictionary<string, object>();
                global::AirFrameXR.Json.Set(s3, "session_settings.video.transcoding_view_resolution.variant", "Scale");
                global::AirFrameXR.Json.Set(s3, "openvr_config.eye_resolution_width", 3552.0); global::AirFrameXR.Json.Set(s3, "openvr_config.eye_resolution_height", 3200.0);
                e = global::AirFrameXR.PipelineModel.StreamEyeExpected(s3);
                Check(e != null && e[0] == 3552 && e[1] == 3200, "Scale variant: the negotiated cache is used (" + (e == null ? "-" : e[0] + "x" + e[1]) + ")");
                Check(global::AirFrameXR.PipelineModel.StreamEyeExpected(new Dictionary<string, object>()) == null, "nothing known: null");
                File.WriteAllLines(Path.Combine(outDir, "eyechoice.txt"), lines);
                return bad == 0 ? 0 : 1;
            }
            if (Arg("--encoding-selftest") != null)   // harness/encoding_radios_check.py: the Encoding radio buttons are one group (both could be checked before 1.0.1)
            {
                var panel = new SettingsPanel(new AppSettings(), Session.Load());
                var found = new List<RadioButton>();
                void Walk(Control c) { foreach (Control k in c.Controls) { if (k is RadioButton rb && (rb.Text.StartsWith("Foveated") || rb.Text.StartsWith("Full frame"))) found.Add(rb); Walk(k); } }
                Walk(panel);
                var fov = found.Find(r => r.Text.StartsWith("Foveated")); var full = found.Find(r => r.Text.StartsWith("Full frame"));
                var lines = new List<string> { "radios=" + found.Count };
                if (fov != null && full != null)
                {
                    fov.Checked = true; full.Checked = true; lines.Add($"full_then: fov={fov.Checked} full={full.Checked}");
                    fov.Checked = true; lines.Add($"fov_then: fov={fov.Checked} full={full.Checked}");
                }
                File.WriteAllLines(Path.Combine(outDir, "encoding.txt"), lines);
                return 0;
            }
            var pmCases = Arg("--pipeline-model-selftest");   // harness/pipeline_model_check.py: the C# pipeline model against the Python oracle, case by case
            if (pmCases != null)
            {
                var lines = new List<string>();
                var root = global::AirFrameXR.Json.Parse(File.ReadAllText(pmCases));
                var cases = root.TryGetValue("cases", out var co) ? co as object[] ?? new object[0] : new object[0];
                double[] Arr(Dictionary<string, object> d, string k) { return d.TryGetValue(k, out var v) && v is object[] a ? a.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray() : null; }
                int[] IArr(Dictionary<string, object> d, string k) { var a = Arr(d, k); return a == null ? null : a.Select(x => (int)x).ToArray(); }
                var ic = CultureInfo.InvariantCulture;
                foreach (var o in cases)
                {
                    if (!(o is Dictionary<string, object> c)) continue;
                    var i = new PipelineModel.Inputs
                    {
                        Declared = Arr(c, "declared"), Rect = Arr(c, "rect"), Eye = IArr(c, "eye"), ReferenceEye = IArr(c, "reference_eye"), ReferenceFov = Arr(c, "reference_fov"),
                        Fit = c.TryGetValue("fit", out var fv) && fv is bool fb && fb, Quality = Convert.ToDouble(c["quality"], ic),
                        Foveated = !(c.TryGetValue("foveated", out var fo) && fo is bool fob) || fob, Geometry = Arr(c, "geometry") ?? new[] { 0.45, 0.4, 0.4, 0.1, 4.0, 5.0 },
                        SplitMode = Convert.ToInt32(c["split_mode"], ic), Preset = Convert.ToInt32(c["preset"], ic), Tuning = Convert.ToString(c["tuning"]), Engines = Convert.ToInt32(c["engines"], ic),
                    };
                    var r = PipelineModel.Compute(i);
                    string X(int[] v) => v == null ? "-" : v[0] + "x" + v[1];
                    string B(double[] v) => v == null ? "-" : v[0].ToString("0.0", ic) + ".." + v[1].ToString("0.0", ic);
                    lines.Add($"{c["name"]}|fit_eye={X(r.FitEye)}|game={X(r.Game)}|encoded={X(r.Encoded)}|band_x={B(r.BandX)}|band_y={B(r.BandY)}|strips={r.Strips}|warnings={r.Warnings.Count}|info={r.Info.Count}|{string.Join(";", r.Warnings)}");
                }
                File.WriteAllLines(Path.Combine(outDir, "pipeline_model.txt"), lines);
                return 0;
            }
            if (Arg("--fov-profile-selftest") != null)   // harness/fov_profile_check.py: choosing a profile in the launcher's FOV card ticks the restriction and saves its tangents (into this folder's own config)
            {
                var lines = new List<string>();
                using (var f = new FovPanelTestForm(new[] { 1.7909, 1.0218, 1.2211, 1.0218 }, false, new[] { 3552, 3200 }, false, FovManagerForm.DemoStore()))
                {
                    f.Opacity = 0; f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000);
                    f.Show(); Application.DoEvents();
                    var pn = f.Panel;
                    lines.Add("profiles=" + pn.ProfileCount + " tick_enabled=" + pn.TickEnabled + " restricted=" + pn.Restricted);   // the Quest profile is wider than this box: not offered
                    pn.SelectForTest("d1", true); Application.DoEvents();
                    var st = new AppSettings();
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "override={0} outer={1:0.00} inner={2:0.00} upper={3:0.00} lower={4:0.00} profile={5}", st.FovOverride, st.FovOuter, st.FovInner, st.FovUpper, st.FovLower, global::AirFrameXR.Json.Str(st.Root, "video.fov_profile", "")));
                    pn.SelectForTest("d3", false); Application.DoEvents();
                    st = new AppSettings();
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "after_other: outer={0:0.00} inner={1:0.00} upper={2:0.00} lower={3:0.00} profile={4}", st.FovOuter, st.FovInner, st.FovUpper, st.FovLower, global::AirFrameXR.Json.Str(st.Root, "video.fov_profile", "")));
                }
                File.WriteAllLines(Path.Combine(outDir, "fovprofile.txt"), lines);
                return 0;
            }
            if (Arg("--fov-ui-plan-selftest") != null) return FovUiPlanSelfTest(outDir);   // harness/fov_ui_plan_check.py: the UI rules of docs/PICTURE_EDGES_DESIGN.md sections 6 and 9
            if (Arg("--fov-other-headset-selftest") != null)   // harness/fov_profile_check.py: an override made for the Vision Pro is not applied to a headset with another field of view
            {
                var lines = new List<string>();
                var avp = new[] { 1.7909, 1.0218, 1.2211, 1.0218 }; var quest = new[] { 1.07, 1.07, 0.99, 1.43 };
                var st0 = new AppSettings();
                global::AirFrameXR.Json.Set(st0.Root, "video.fov_override", true);
                var vals = new[] { 0.94, 0.93, 0.90, 0.76 }; var keys = new[] { "video.fov_outer", "video.fov_inner", "video.fov_upper", "video.fov_lower" };
                for (int k = 0; k < 4; k++) global::AirFrameXR.Json.Set(st0.Root, keys[k], vals[k]);
                global::AirFrameXR.Json.Set(st0.Root, "video.fov_for", avp.Select(x => (object)x).ToList());
                global::AirFrameXR.Json.Set(st0.Root, "video.fov_for_headset", "Apple Vision Pro");
                global::AirFrameXR.Json.Set(st0.Root, "headset.name", "Apple Vision Pro");
                st0.Save();
                var ceiling = new[] { 1.05, 0.80, 0.76, 0.87 };   // the AirFrameXR client's ceiling (docs/FOV_CLIENT_CEILING.md): another shape, the same headset
                var fitsAvp = new AppSettings().FovFits(avp); var fitsUnknown = new AppSettings().FovFits(null); var fitsScaled = new AppSettings().FovFits(avp.Select(x => x * 0.8).ToArray()); var fitsCeiling = new AppSettings().FovFits(ceiling);
                var st1 = new AppSettings(); global::AirFrameXR.Json.Set(st1.Root, "headset.name", "Meta Quest 3"); st1.Save();   // a Quest paired: another headset
                // an override saved by 1.1.x (no name) on the first client box, seen with the client's ceiling: the known transition fits; a Quest's box does not
                var st2 = new AppSettings(); global::AirFrameXR.Json.Set(st2.Root, "video.fov_for", new[] { 0.956, 0.946, 0.915, 0.772 }.Select(x => (object)x).ToList()); global::AirFrameXR.Json.Remove(st2.Root, "video.fov_for_headset"); st2.Save();
                var fitsMigrated = new AppSettings().FovFits(ceiling); var fitsMigratedQuest = new AppSettings().FovFits(quest);
                global::AirFrameXR.Json.Set(st2.Root, "video.fov_for_headset", "Apple Vision Pro"); st2.Save();
                lines.Add("fits_avp=" + fitsAvp + " fits_quest=" + new AppSettings().FovFits(quest) + " fits_unknown=" + fitsUnknown + " fits_avp_scaled=" + fitsScaled + " fits_same_name_new_shape=" + fitsCeiling + " fits_unnamed_first_box_to_ceiling=" + fitsMigrated + " fits_unnamed_first_box_on_quest=" + fitsMigratedQuest);   // the visionOS app's FoV Scale 0.8 is a uniform factor and the client's ceiling another shape: the override stays on the same headset
                using (var f = new FovPanelTestForm(quest, false, null, false, FovManagerForm.DemoStore()))
                {
                    f.Opacity = 0; f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000);
                    f.Show(); Application.DoEvents();
                    var pn = f.Panel;
                    lines.Add("profiles_on_quest=" + pn.ProfileCount + " tick_on_quest=" + pn.Restricted);
                    pn.SelectForTest("d1", true); Application.DoEvents();
                    var st = new AppSettings();
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "after_tick: override={0} lower={1:0.00} fits_quest={2}", st.FovOverride, st.FovLower, st.FovFits(quest)));
                }
                File.WriteAllLines(Path.Combine(outDir, "fovother.txt"), lines);
                return 0;
            }
            var applyOut = Arg("--fov-apply-to");   // harness/scenarios/e2e_fov_from_settings.json: writes the settings file exactly as choosing a profile in the launcher's FOV card does (UserFovStore.Apply on an empty file)
            if (applyOut != null)
            {
                double[] Nums(string k) => (Arg(k) ?? "").Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                var tan = Nums("--tan"); var client = Nums("--client");
                var st = new AppSettings(); st.Root.Clear();
                UserFovStore.Apply(new UserFov { Id = "e2e-profile", Name = "e2e", Tan = tan }, st, client, "Mock headset");
                global::AirFrameXR.Json.Save(applyOut, st.Root);
                return 0;
            }
            if (Arg("--fov-store-selftest") != null)   // harness/fov_store_check.py: the validator's red / yellow / valid cases and the profile store (a temp file, not the user's)
            {
                var lines = new List<string>();
                var quest = new[] { 1.376, 0.839, 0.966, 1.428 };   // the Quest 3's declared box (54 / 40 / 44 / 55 degrees)
                string Show(double[] v, FovUnit u, double[] decl) { var c = FovValidator.Check(v, u, decl); return c.Level + (double.IsNaN(c.AreaShare) ? "" : string.Format(CultureInfo.InvariantCulture, " area={0:0.00}", c.AreaShare)); }
                var nan = double.NaN;
                lines.Add("parse=" + string.Join("|", new[] { "0.96", " 1,05 ", "abc", "", "1e9x" }.Select(t => double.IsNaN(FovValidator.Parse(t)) ? "nan" : FovValidator.Parse(t).ToString(CultureInfo.InvariantCulture))));
                lines.Add("nan_red=" + Show(new[] { nan, 0.8, 0.7, 0.6 }, FovUnit.Tangent, null));
                lines.Add("zero_red=" + Show(new[] { 0.9, 0.0, 0.7, 0.6 }, FovUnit.Tangent, null));
                lines.Add("negative_red=" + Show(new[] { 0.9, -0.1, 0.7, 0.6 }, FovUnit.Tangent, null));
                lines.Add("deg90_red=" + Show(new[] { 90.0, 40, 40, 40 }, FovUnit.Degrees, null));
                lines.Add("over_declared_yellow=" + Show(new[] { 1.5, 0.8, 0.9, 1.4 }, FovUnit.Tangent, quest));
                lines.Add("avp_calibration_valid=" + Show(new[] { 0.97, 0.71, 0.69, 0.63 }, FovUnit.Tangent, null));
                lines.Add("quest_trim_valid=" + Show(new[] { 51.0, 40, 43, 54 }, FovUnit.Degrees, quest));
                lines.Add("quest_declared_valid=" + Show(quest, FovUnit.Tangent, quest));
                lines.Add("degrees_as_tangents_yellow=" + Show(new[] { 54.0, 40, 44, 55 }, FovUnit.Tangent, null));
                lines.Add("side_ratio_yellow=" + Show(new[] { 1.6, 0.5, 0.7, 0.7 }, FovUnit.Tangent, null));
                lines.Add("vertical_ratio_yellow=" + Show(new[] { 0.9, 0.8, 1.4, 0.5 }, FovUnit.Tangent, null));
                lines.Add("tiny_yellow=" + Show(new[] { 0.2, 0.2, 0.2, 0.2 }, FovUnit.Tangent, null));
                lines.Add("small_share_yellow=" + Show(new[] { 0.7, 0.5, 0.5, 0.7 }, FovUnit.Tangent, quest));
                var fcheck = FovValidator.Check(new[] { 0.97, 0.71, 0.69, 0.63 }, FovUnit.Tangent, null);
                lines.Add(string.Format(CultureInfo.InvariantCulture, "avp_total={0:0.0}x{1:0.0}", fcheck.TotalH, fcheck.TotalV));
                var tmp = Path.Combine(outDir, "user_fov_profiles_test.json");
                try { File.Delete(tmp); } catch { }
                lines.Add("empty=" + UserFovStore.Load(tmp).Count);
                var a = UserFovStore.Add(new UserFov { Id = "a", Name = "Mine", Headset = "Apple Headset", Source = "manual", Tan = new[] { 0.97, 0.71, 0.69, 0.63 }, Created = "2026-01-01T00:00:00" }, tmp);
                var b = UserFovStore.Add(new UserFov { Id = "b", Name = "mine", Source = "calibrated", Tan = new[] { 0.9, 0.7, 0.6, 0.6 }, Quality = 0.5, Created = "2026-01-02T00:00:00", Window = new List<double[]> { new[] { 0.9, 0.0 }, new[] { 0.0, 0.6 } } }, tmp);
                var all = UserFovStore.Load(tmp);
                lines.Add("after_add=" + string.Join(",", all.Select(x => x.Id + ":" + x.Name)) + " window_b=" + all.First(x => x.Id == "b").Window.Count + " quality_b=" + all.First(x => x.Id == "b").Quality.ToString(CultureInfo.InvariantCulture));
                lines.Add("rename=" + UserFovStore.Rename("a", "Renamed", tmp) + " renamed=" + UserFovStore.Load(tmp).First(x => x.Id == "a").Name);
                lines.Add("remove=" + UserFovStore.Remove("a", tmp) + " remove_again=" + UserFovStore.Remove("a", tmp) + " left=" + UserFovStore.Load(tmp).Count);
                File.WriteAllText(tmp, "{ not json");
                lines.Add("corrupt=" + UserFovStore.Load(tmp).Count);
                try { File.Delete(tmp); } catch { }
                // the card: what the Add button allows, and what an add stores
                using (var card = new FovCard(tmp, quest))
                {
                    card.TypeForTest("Degrees", "95", "40", "43", "54"); var red = card.CanAdd;
                    card.TypeForTest("Degrees", "51", "40", "43", "54"); var valid = card.CanAdd; var validMsg = card.Message.Length;
                    card.TypeForTest("Tangents", "1.3", "0.5", "0.9", "1.0"); var yellow = card.CanAdd; var yellowMsg = card.Message.Length > 0;
                    card.TypeForTest("Tangents", "1.0", "", "", ""); var partial = card.CanAdd;
                    card.TypeForTest("Degrees", "51", "40", "43", "54"); card.AddForTest();
                    lines.Add("card=red:" + red + " valid:" + valid + "/" + validMsg + " yellow:" + yellow + "/" + yellowMsg + " partial:" + partial);
                    lines.Add("card_added=" + card.Count + ":" + card.Selected?.Name + ":" + card.Selected?.Tan[0].ToString(CultureInfo.InvariantCulture) + ":" + card.CanAdd);
                }
                try { File.Delete(tmp); } catch { }
                // seeding from shipped profiles: the proposal for this headset is copied in once, a deleted one does not come back, a choice is a copy in the settings
                {
                    var pd = Path.Combine(outDir, "seed_profiles"); Directory.CreateDirectory(pd);
                    File.WriteAllText(Path.Combine(pd, "a.profile.json"), "{\"id\":\"t-avp\",\"display_name\":\"Test AVP (x)\",\"match\":{\"name_contains\":[\"Apple\"]},\"fov\":{\"visible_deg\":{\"outer\":44.1,\"inner\":35.4,\"upper\":34.6,\"lower\":32.2},\"margin_deg\":0,\"client_fov_scale\":0.8}}");
                    File.WriteAllText(Path.Combine(pd, "q.profile.json"), "{\"id\":\"t-q\",\"display_name\":\"Test Quest\",\"match\":{\"name_contains\":[\"Quest 3\"]},\"fov\":{\"visible_deg\":{\"outer\":51,\"inner\":40,\"upper\":43,\"lower\":54},\"margin_deg\":0,\"source\":\"starting-point\"}}");
                    var sp = Path.Combine(outDir, "user_fov_seed_test.json"); try { File.Delete(sp); } catch { }
                    string T(UserFov u) => string.Join("/", u.Tan.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)));
                    var n1 = UserFovStore.SeedFromShipped("Apple Headset", sp, pd); var u1 = UserFovStore.Load(sp).FirstOrDefault();
                    lines.Add("seed=" + n1 + " name=" + u1?.Name + " tan=" + (u1 == null ? "" : T(u1)));
                    var n2 = UserFovStore.SeedFromShipped("Apple Headset", sp, pd);
                    UserFovStore.Remove("std-t-avp", sp);
                    var n3 = UserFovStore.SeedFromShipped("Apple Headset", sp, pd);
                    lines.Add("seed_again=" + n2 + " after_delete=" + n3);
                    var n4 = UserFovStore.SeedFromShipped("Meta Quest 3", sp, pd); var u4 = UserFovStore.Load(sp).FirstOrDefault();
                    lines.Add("seed_quest=" + n4 + " name=" + u4?.Name + " tan=" + (u4 == null ? "" : T(u4)));
                    var stA = new AppSettings(); var av = new UserFov { Id = "std-t-avp", Tan = new[] { 0.97, 0.71, 0.69, 0.63 } };
                    UserFovStore.Apply(av, stA, new[] { 0.9, 0.8, 0.7, 0.6 }, "Apple Headset");
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "apply={0} {1:0.00}/{2:0.00}/{3:0.00}/{4:0.00} id={5} for={6}", stA.FovOverride, stA.FovOuter, stA.FovInner, stA.FovUpper, stA.FovLower, global::AirFrameXR.Json.Str(stA.Root, "video.fov_profile", ""), stA.FovForHeadset));
                    try { File.Delete(sp); Directory.Delete(pd, true); } catch { }
                }
                File.WriteAllLines(Path.Combine(outDir, "fovstore.txt"), lines);
                return 0;
            }
            var fwExe = Arg("--firewall");   // harness/firewall_check.py: print the firewall analysis of this exe and stop
            if (fwExe != null)
            {
                var rep = Firewall.Analyze(fwExe);
                File.WriteAllText(Path.Combine(outDir, "firewall.txt"), "state=" + rep.State + "\r\nclutter=" + rep.Clutter + "\r\n" + string.Join("\r\n", rep.Rules.Select(r => $"{r.Name}|{r.Direction}|{r.Action}|{r.Protocol}|{r.Profile}|{r.Enabled}|{(r.InPlace ? "INPLACE" : "")}|{(r.Propose ? "PROPOSE" : "")}|{r.Status}")));
                return 0;
            }
            var matrix = (Arg("--matrix") ?? "1920x1080@100,1920x1080@125,1920x1080@150,2560x1440@100,2560x1440@125,2560x1440@150,3840x2160@100,3840x2160@150,3840x2160@200").Split(',');
            int fails = 0, warns = 0, n = 0;
            foreach (var m in matrix)
            {
                var at = m.Split('@'); var wh = at[0].Split('x');
                int sw = int.Parse(wh[0]), sh = int.Parse(wh[1]), pct = int.Parse(at[1]);
                int dpi = 96 * pct / 100;
                // work area: the screen without the taskbar (48 px at 100 %)
                var work = new Size(sw, sh - 48 * pct / 100);
                foreach (var c in Cases())
                {
                    if (only != null && c.Name != only) continue;
                    Ui.DpiOverride = dpi;
                    var issues = new List<string>(); string size = "";
                    try
                    {
                        using (var f = c.Make())
                        {
                            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.ShowInTaskbar = false; f.Opacity = 0; f.MaximumSize = new Size(20000, 20000);   // Windows would clamp a window to the real screen: the pretend screens are bigger   // on the primary monitor (its dpi), invisible
                            f.Show();                       // Load: the framework scales to the real dpi; Shown is posted and never runs (no message loop)
                            var pre = f.Width + "x" + f.Height + " dpi " + f.DeviceDpi;
                            c.Prepare?.Invoke(f);
                            double k = dpi / (double)f.DeviceDpi;   // the framework scaled the window to its monitor's dpi at Load (native per-monitor mode): that is the baseline
                            Emulate(f, k, k);
                            Ui.WorkAreaOverride = new Rectangle(0, 0, work.Width, work.Height);   // the windows' own fitting code, against the pretend screen
                            Ui.Refit(f);
                            f.PerformLayout();
                            var tabs = Descend(f).OfType<TabControl>().FirstOrDefault();
                            int pages = tabs != null ? tabs.TabPages.Count : 1;
                            for (int t = 0; t < pages; t++)
                            {
                                if (tabs != null) { tabs.SelectedIndex = t; f.PerformLayout(); }
                                var tag = tabs != null ? "-tab" + t : "";
                                Audit(f, work, tag, issues);
                                size = f.Width + "x" + f.Height + " [before: " + pre + "]";
                                using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(Path.Combine(outDir, $"{c.Name}{tag}_{sw}x{sh}@{pct}.png"), ImageFormat.Png); }
                            }
                        }
                    }
                    catch (Exception e) { Ui.WorkAreaOverride = null; issues.Add("FAIL crashed: " + e.GetType().Name + ": " + e.Message); }
                    Ui.WorkAreaOverride = null;
                    n++;
                    fails += issues.Count(i => i.StartsWith("FAIL")); warns += issues.Count(i => i.StartsWith("WARN"));
                    Json.Add($"{{\"form\":{Q(c.Name)},\"screen\":\"{sw}x{sh}\",\"scale\":{pct},\"work\":\"{work.Width}x{work.Height}\",\"size\":{Q(size)},\"issues\":[{string.Join(",", issues.Select(Q))}]}}");
                    Application.DoEvents();
                }
            }
            File.WriteAllText(Path.Combine(outDir, "results.json"), "[\n" + string.Join(",\n", Json) + "\n]\n");
            Console.WriteLine($"uitest: {n} renders, {fails} failures, {warns} warnings");
            return 0;
        }

        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";

        static IEnumerable<Control> Descend(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in Descend(k)) yield return d; }
        }

        // what the framework does at a higher dpi: sizes grow by k (target / the scale it applied), fonts by kFont (target / the dpi they are drawn at)
        static void Emulate(Form f, double kFont, double k)
        {
            if (Math.Abs(k - 1.0) < 0.01 && Math.Abs(kFont - 1.0) < 0.01) return;
            var explicitFonts = new List<KeyValuePair<Control, Font>>();
            foreach (var c in Descend(f)) if (c.Parent != null && !ReferenceEquals(c.Font, c.Parent.Font)) explicitFonts.Add(new KeyValuePair<Control, Font>(c, c.Font));
            var baseFont = f.Font;
            f.SuspendLayout();
            f.Font = new Font(baseFont.FontFamily, (float)(baseFont.Size * kFont), baseFont.Style);
            foreach (var kv in explicitFonts) kv.Key.Font = new Font(kv.Value.FontFamily, (float)(kv.Value.Size * kFont), kv.Value.Style);
            f.Scale(new SizeF((float)k, (float)k));
            f.ResumeLayout(true);
        }

        static string Desc(Control c) { var s = new StringBuilder(c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? "" : ":" + c.Name)); var t = c.Text; if (!string.IsNullOrEmpty(t) && !(c is Form)) s.Append(" '" + (t.Length > 28 ? t.Substring(0, 28) + "..." : t).Replace("\n", " ") + "'"); return s.ToString(); }

        static void Audit(Form f, Size work, string tag, List<string> issues)
        {
            void add(string level, string msg) { var m = level + " " + msg + (tag.Length > 0 ? " (" + tag.TrimStart('-') + ")" : ""); if (!issues.Contains(m)) issues.Add(m); }
            // the scale is emulated by resizing a window built at the machine's own scale: a scrolling container keeps the extent it measured then (a sideways bar the real scale would not
            // show), so every one measures again before the audit
            foreach (var sc in Descend(f).OfType<ScrollableControl>().Where(x => x.AutoScroll).ToList()) { sc.AutoScroll = false; sc.AutoScroll = true; }
            f.PerformLayout();
            // 1. the window against the work area
            bool resizable = f.FormBorderStyle == FormBorderStyle.Sizable || f.FormBorderStyle == FormBorderStyle.SizableToolWindow;
            if (f.Width > work.Width || f.Height > work.Height)
                add(resizable && f.MinimumSize.Width <= work.Width && f.MinimumSize.Height <= work.Height ? "WARN" : "FAIL", $"window {f.Width}x{f.Height} does not fit the work area {work.Width}x{work.Height}" + (resizable ? $" (minimum {f.MinimumSize.Width}x{f.MinimumSize.Height})" : ""));
            foreach (var c in Descend(f).Where(x => x.Visible))
            {
                var p = c.Parent; if (p == null) continue;
                var scroll = p as ScrollableControl;
                bool scrolls = scroll != null && scroll.AutoScroll;
                // 2. clipped by the parent (a scrolling parent may overflow: that is what it is for, but a sideways scroll bar is a smell)
                if (!scrolls && c.Dock == DockStyle.None)
                {
                    var r = p.ClientRectangle;
                    if (c.Right > r.Right + 2 || c.Bottom > r.Bottom + 2 || c.Left < r.Left - 2 || c.Top < r.Top - 2)
                        add("FAIL", $"clipped: {Desc(c)} {c.Bounds} outside {p.GetType().Name} {r.Size}");
                }
                if (scrolls && scroll.HorizontalScroll.Visible && p == scroll)
                {
                    var wide = p.Controls.Cast<Control>().Where(k => k.Visible).OrderByDescending(k => k.Right).FirstOrDefault();   // who reaches furthest right
                    add("WARN", $"{Desc(p)} needs a horizontal scroll bar [min {scroll.AutoScrollMinSize}, display {scroll.DisplayRectangle.Size}, pref {p.PreferredSize}, vscroll {scroll.VerticalScroll.Visible}]" + (wide != null ? $" (client {scroll.ClientSize.Width}px, widest: {string.Join("; ", p.Controls.Cast<Control>().OrderByDescending(k => k.Right + k.Margin.Right).Take(3).Select(k => (k.Visible ? "" : "(hidden) ") + Desc(k) + " " + k.Width + "w right " + (k.Right + k.Margin.Right)))})" : ""));
                }
                // 3. text cut off
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
                if ((c is Button || c is CheckBox || c is RadioButton) && !c.AutoSize && !string.IsNullOrEmpty(c.Text))
                {
                    var w = TextRenderer.MeasureText(c.Text, c.Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding).Width;
                    if (w + 8 > c.Width) add("FAIL", $"text cut off: {Desc(c)} needs {w + 8}px, has {c.Width}");
                }
                if (c is Label lb && !lb.AutoSize && !string.IsNullOrEmpty(lb.Text))
                {
                    var one = TextRenderer.MeasureText(lb.Text, lb.Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding);
                    var need = one.Width <= lb.Width ? one : TextRenderer.MeasureText(lb.Text, lb.Font, new Size(lb.Width, 0), flags);
                    if (need.Height > lb.Height + 2) add("FAIL", $"text cut off: {Desc(lb)} needs {need.Height}px, has {lb.Height} (width {lb.Width}, text {one.Width}px, dock {lb.Dock}, font {lb.Font.SizeInPoints:0.0}pt)");
                }
                if (c is Label al && al.AutoSize && al.Right > f.ClientSize.Width + 2 && !scrolls) add("FAIL", $"label beyond the window: {Desc(al)}");
                if (c is ListView lv && lv.View == View.Details)
                {
                    int total = 0;
                    foreach (ColumnHeader h in lv.Columns)
                    {
                        total += h.Width;
                        var hw = TextRenderer.MeasureText(h.Text, lv.Font).Width + 2;
                        if (hw > h.Width) add("FAIL", $"list header cut off: {Desc(lv)} column '{h.Text}' needs {hw}px, has {h.Width}");
                    }
                    foreach (ListViewItem it in lv.Items)
                        for (int i = 0; i < it.SubItems.Count && i < lv.Columns.Count; i++)
                        {
                            var cw = TextRenderer.MeasureText(it.SubItems[i].Text, lv.Font).Width + 12;
                            if (cw > lv.Columns[i].Width + 4 && total + 8 < lv.ClientSize.Width) { add("WARN", $"list cell cut off: {Desc(lv)} column '{lv.Columns[i].Text}' '{it.SubItems[i].Text.Substring(0, Math.Min(24, it.SubItems[i].Text.Length))}' needs {cw}px, has {lv.Columns[i].Width}"); break; }
                        }
                    if (total > lv.ClientSize.Width + 4) add("WARN", $"{Desc(lv)} columns ({total}px) are wider than the list ({lv.ClientSize.Width}px): sideways scrolling");
                }
            }
            // 4. siblings that overlap (not docked, both visible)
            foreach (var p in new[] { (Control)f }.Concat(Descend(f)).Where(x => x.Controls.Count > 1 && !(x is FlowLayoutPanel) && !(x is TableLayoutPanel) && !(x is TabControl) && !(x is ListView)))
            {
                var kids = p.Controls.Cast<Control>().Where(k => k.Visible && k.Dock == DockStyle.None).ToList();
                for (int i = 0; i < kids.Count; i++)
                    for (int j = i + 1; j < kids.Count; j++)
                    {
                        var ov = Rectangle.Intersect(kids[i].Bounds, kids[j].Bounds);
                        if (ov.Width * ov.Height > 16) add("FAIL", $"overlap in {p.GetType().Name}: {Desc(kids[i])} and {Desc(kids[j])} ({ov.Width}x{ov.Height})");
                    }
            }
        }
    }
}
