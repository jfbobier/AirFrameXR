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
    public class RuntimeForm : Form
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
        readonly Label stats = Ui.Line("Waiting for the first statistics (every 10 s)...");
        // display controls: (label, min, max, default, settings getter/setter), values x100 on the track bars
        class Knob { public string Name; public TrackBar Bar; public Label Value; public double Def; public Func<double> Get; public Action<double> Set; }
        readonly List<Knob> knobs = new List<Knob>();
        readonly CheckBox debug = new CheckBox { Text = "Debug logging (files in logs\\debug\\)", AutoSize = true, Font = Ui.Body };
        readonly ToggleSwitch frameGen = new ToggleSwitch { Margin = new Padding(3, 6, 10, 3) };
        readonly Label footer = Ui.Line("", new Font("Segoe UI", 8.5f));
        string pairedName = "", pairedIp = "";
        bool closing;
        int restarts;
        string setupError;   // the streamer's `setup_error` message: a problem to fix, restarting it would not help

        public RuntimeForm(Dictionary<string, object> check, bool autoFind = false)
        {
            this.check = check;
            this.autoFind = autoFind;
            Text = "VisionALVR";
            Icon = Ui.AppIcon();
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 4, 12, 12) };
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

            var box = new GroupBox { Text = "Streaming (updated every 10 s)", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Font = Ui.Bold };
            stats.Font = new Font("Consolas", 9.5f);
            box.Controls.Add(stats);
            stats.Location = new Point(10, 22);
            body.Controls.Add(box);

            // Position and picture, two knobs per row so the window fits a laptop screen. Games with the action on the floor by default (Moss,
            // Titan Isles) assume a standing user; the headset's floor estimate is unreliable, so the streamer hands games the eye height set here
            // (above the floor) when you enter VR and keeps your own vertical movement on top of it. Everything is applied live.
            var live = new GroupBox { Text = "Position and picture (applied live)", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8), Font = Ui.Bold };
            var lt = new TableLayoutPanel { AutoSize = true, ColumnCount = 6, Location = new Point(8, 20), Font = Ui.Body };
            if (!settings.HasDisplay) { settings.Gamma = AppSettings.DefaultGamma; settings.Save(); } // the streamer reads it at start
            AddKnob(lt, "Eye height, m", 0, 220, AppSettings.DefaultHeight, () => settings.Height, v => settings.Height = v,
                    "Your eye height above the floor, as games see it: about 1.30 m seated, 1.6-1.7 m standing; 0 = the headset's own floor estimate. Games that place you from the floor use it; moving it re-anchors at your current head position.");
            AddKnob(lt, "Gamma (>1 brighter)", 50, 200, AppSettings.DefaultGamma, () => settings.Gamma, v => settings.Gamma = v);
            AddKnob(lt, "Brightness", -30, 30, 0, () => settings.Brightness, v => settings.Brightness = v);
            AddKnob(lt, "Contrast", -30, 30, 0, () => settings.Contrast, v => settings.Contrast = v);
            AddKnob(lt, "Saturation", -50, 50, 0, () => settings.Saturation, v => settings.Saturation = v, "Below 0: less vivid.");
            AddKnob(lt, "Sharpening", 0, 100, 0, () => settings.Sharpening, v => settings.Sharpening = v);
            var reset = new Button { Text = "Defaults", AutoSize = true };
            reset.Click += (s, e) => { foreach (var k in knobs) k.Bar.Value = (int)Math.Round(k.Def * 100); };
            tips.SetToolTip(reset, "Eye height 1.30 m, gamma 1.2, the rest neutral.");
            lt.Controls.Add(reset);
            lt.SetColumnSpan(reset, 2);
            lt.Controls.Add(debug);
            lt.SetColumnSpan(debug, 4);
            var fgRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            fgRow.Controls.Add(frameGen);
            var fgText = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            fgText.Controls.Add(new Label { Text = "Frame generation", AutoSize = true, Font = Ui.Bold, Margin = new Padding(0, 4, 0, 0) });
            fgText.Controls.Add(new Label { Text = "The game renders at half the display rate and the streamer fills the frame in between. For games that cannot hold 70-75 fps; applies at once.", AutoSize = true, Font = Ui.Body, ForeColor = Color.DimGray, MaximumSize = new Size(560, 0), Margin = new Padding(0) });
            fgRow.Controls.Add(fgText);
            lt.Controls.Add(fgRow);
            lt.SetColumnSpan(fgRow, 6);
            live.Controls.Add(lt);
            body.Controls.Add(live);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var logs = new Button { Text = "Open logs folder", AutoSize = true };
            logs.Click += (s, e) => { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", Paths.Logs); };
            var conf = new Button { Text = "Configure... (stops streaming)", AutoSize = true };
            conf.Click += (s, e) => OpenConfigure();
            buttons.Controls.Add(logs);
            buttons.Controls.Add(conf);
            body.Controls.Add(buttons);
            footer.ForeColor = Color.Gray;
            body.Controls.Add(footer);

            Controls.Add(body);
            Controls.Add(Ui.CompactHeader("Streamer for ALVR headsets (Vision Pro, Quest, ...)"));


            debug.Checked = settings.Debug;
            debug.CheckedChanged += (s, e) =>
            {
                host.Send("debug " + (debug.Checked ? "on" : "off"));
                settings.Debug = debug.Checked;
                settings.Save();
            };
            frameGen.Checked = settings.FrameGen;
            frameGen.CheckedChanged += (s, e) =>
            {
                host.Send("frame_gen " + (frameGen.Checked ? "on" : "off"));   // the running streamer switches at the next boundary; the setting is read at start too
                settings.FrameGen = frameGen.Checked;
                settings.Save();
                Log.Write("INFO", "frame generation " + (frameGen.Checked ? "on" : "off"));
            };

            host.OnEvent += (name, data) => BeginInvoke((Action)(() => HostEvent(name, data)));
            host.OnExit += code => BeginInvoke((Action)(() => HostExited(code)));
            Shown += (s, e) =>
            {
                StartHost();
                CheckFirewall();
                if (autoFind) { var t = new Timer { Interval = 3000 }; t.Tick += (a, b) => { t.Stop(); FindHeadset(true); }; t.Start(); }
            };
            Ui.ScreenshotWhenShown(this, 12000);
            FormClosing += (s, e) => { closing = true; footer.Text = "Stopping the streamer..."; Refresh(); host.Stop(); Log.Write("INFO", "VisionALVR.exe closed"); };
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
                               Bar = new TrackBar { Minimum = min, Maximum = max, TickFrequency = (max - min) / 6, SmallChange = 5, LargeChange = 10, Width = 170, AutoSize = false, Height = 32 } };
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

        void SendDisplay()
        {
            string f(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            host.Send("gamma " + f(settings.Gamma));
            host.Send("height " + f(settings.Height));
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
                    break;
                case "game_ended":
                    gameRunning = false;
                    game.Text = $"Game: none (last: {S(d, "exe")}, {F(D(d, "seconds") / 60, "0.0")} min)";
                    break;
                case "status":
                    var c = d != null && d.TryGetValue("client", out var cc) ? cc as Dictionary<string, object> : null;
                    stats.Text =
                        $"Game fps {F(D(d, "app_fps"), "0.0"),6}    streamed fps {F(D(d, "stream_fps"), "0.0"),6}    headset shows {F(double.IsNaN(D(c, "displayed_fps")) ? D(c, "client_fps") : D(c, "displayed_fps"), "0.0"),6} fps\n" +
                        $"Bitrate  {F(D(d, "mbps"), "0"),5} Mbps   frames not streamed {S(d, "frames_not_streamed"),4}   packets lost {S(d, "packets_lost"),4}\n" +
                        $"Latency  total {F(D(c, "total_latency_ms"), "0.0"),5} ms = encode {F(D(c, "encode_ms"), "0.0")} + network {F(D(c, "network_ms"), "0.0")} + decode {F(D(c, "decode_ms"), "0.0")} + other";
                    break;
                case "encode_error":
                    footer.Text = "Encode error: " + S(d, "message");
                    break;
            }
        }

        void OpenConfigure()
        {
            if (MessageBox.Show(this, "configure.exe needs the network port the streamer uses: streaming stops while it is open. Continue?",
                    "VisionALVR", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            closing = true;
            host.Stop();
            Process.Start(Paths.Configure);
            Close();
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
                Log.Write("INFO", $"VisionALVR.exe {Version.Text} start ({Paths.Dir})");
                if (!File.Exists(Paths.Host))
                {
                    MessageBox.Show("alvr_host.exe was not found next to VisionALVR.exe. Unzip the whole VisionALVR folder.", "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                                            "Register this folder now? (asks for administrator rights; unregister_openxr_runtime.bat puts the previous one back)",
                                            "VisionALVR", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (q == DialogResult.Yes && !OpenXr.Register(null))
                        MessageBox.Show("Registration did not take effect. Run register_openxr_runtime.bat from this folder.", "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
