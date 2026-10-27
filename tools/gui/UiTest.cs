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
    static class UiTestProfiles
    {
        public static HmdProfile WithMeasuredFov() => HmdProfiles.Parse("{\"id\":\"apple-vision-pro-test\",\"display_name\":\"Apple Vision Pro (ALVR visionOS client)\",\"match\":{\"name_contains\":[\"Apple\"]},\"stream\":{\"eye_resolution\":[3552,3200]},\"network\":{\"default\":\"a\",\"variants\":[{\"id\":\"a\",\"label\":\"a\",\"bitrate_mbps\":300}]},\"fov\":{\"visible_deg\":{\"outer\":40.3,\"inner\":40.0,\"upper\":39.0,\"lower\":34.4},\"margin_deg\":3,\"render_quality\":0.5,\"note\":\"test\"}}", "test")[0];
    }

    static class UiTest
    {
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
            yield return new Case { Name = "advanced-switches-fov-profile", Make = () => new AdvancedForm(new[] { 1.7909, 1.0218, 1.2211, 1.0218 }, false, new[] { 3552, 3200 }, UiTestProfiles.WithMeasuredFov()) };   // the profile button, override off
            yield return new Case { Name = "advanced-switches-fov-quality", Make = () => new AdvancedForm(new[] { 1.7909, 1.0218, 1.2211, 1.0218 }, true, new[] { 3552, 3200 }) };   // the same, restricted, with the render quality row
            yield return new Case { Name = "advanced-switches-fov", Make = () => new AdvancedForm(new[] { 1.7909, 1.0218, 1.2211, 1.0218 }, true) };   // a pretend Vision Pro, the override ticked
            yield return new Case { Name = "performance", Make = () => new PerformanceForm("Cyberpunk2077.exe") };
            for (int p = 0; p <= 6; p++)
            {
                int page = p;
                yield return new Case { Name = "wizard-page" + page, Make = () => new WizardForm(null, true), Prepare = f => ((WizardForm)f).ForTest(page) };
            }
            yield return new Case { Name = "configure-advanced", Make = () => new ConfigureForm(null) };
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
            var dbg = all.FirstOrDefault(x => x.Apply != null); dbg?.Apply();
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
            if (Arg("--fov-profile-selftest") != null)   // harness/fov_profile_check.py: the profile button fills the fields and the slider and saves (into this folder's own config)
            {
                var lines = new List<string>();
                using (var f = new AdvancedForm(new[] { 1.7909, 1.0218, 1.2211, 1.0218 }, false, new[] { 3552, 3200 }, UiTestProfiles.WithMeasuredFov()))
                {
                    f.Opacity = 0; f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000);
                    f.Show(); Application.DoEvents();
                    Button b = null;
                    void Find(Control c) { foreach (Control k in c.Controls) { if (k is Button x && x.AccessibleName == "Use the measured values") b = x; Find(k); } }
                    Find(f);
                    lines.Add("button=" + (b != null) + " visible=" + (b != null && b.Visible));
                    if (b != null) { lines.Add("text=" + b.Text); b.PerformClick(); Application.DoEvents(); }
                    var st = new AppSettings();
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "override={0} outer={1:0.00} inner={2:0.00} upper={3:0.00} lower={4:0.00} quality={5:0.00}", st.FovOverride, st.FovOuter, st.FovInner, st.FovUpper, st.FovLower, st.FovRenderQuality));
                }
                File.WriteAllLines(Path.Combine(outDir, "fovprofile.txt"), lines);
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
                if (scrolls && scroll.HorizontalScroll.Visible && p == scroll) add("WARN", $"{Desc(p)} needs a horizontal scroll bar");
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
