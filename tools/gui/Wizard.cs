// configure.exe as an autoconfigure wizard (docs/CONFIGURE_WIZARD.md):
//   0 gate (NVIDIA RTX 40 or newer) - 1 headset - 2 handshake and what we learned - 3 tests with a checklist - 4 how the setup performs - 5 proposed configuration.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VisionALVR
{
    public class WizardForm : Form
    {
        readonly AppSettings settings = new AppSettings();
        Dictionary<string, object> session = Session.Load();
        readonly Dictionary<string, object> check;
        readonly bool demo;
        Discovery discovery;
        HostProcess handshakeHost;
        CancellationTokenSource testCts;
        readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14) };
        readonly Panel stepsPanel = new Panel { Dock = DockStyle.Left, Width = 230, BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9), Padding = new Padding(12) };
        readonly Button back = new Button { Text = "< Back", AutoSize = true, MinimumSize = new Size(90, 30) };
        readonly Button next = new Button { Text = "Next >", AutoSize = true, MinimumSize = new Size(110, 30), Font = Ui.Bold };
        readonly Button advanced = new Button { Text = "Advanced (old tabs)", AutoSize = true };
        readonly Label[] stepLabels = new Label[6];
        static readonly string[] StepNames = { "0  Your PC", "1  Headset", "2  What we learned", "3  Tests", "4  How you perform", "5  Configuration" };
        int step;
        // state
        Handshake hs = new Handshake();
        TestContext ctx;
        List<TestStep> steps;
        Proposal proposal;
        SettingsPanel settingsPanel;
        bool autoconfig;
        // headset page
        ListView found = new ListView { View = View.Details, FullRowSelect = true, Height = 200, Width = 780, HideSelection = false, MultiSelect = false };
        readonly Dictionary<string, ListViewItem> rows = new Dictionary<string, ListViewItem>();
        readonly Label discStatus = Ui.Line("");
        string chosenHost = "", chosenIp = "", chosenName = "";
        // handshake page
        ComboBox rateBox; NumericUpDown scaleBox; CheckBox foveationBox; Label hsStatus; FlowLayoutPanel hsDetails; Button hsRetry, hsSkip, hsAuto, hsManual;
        ProgressBar hsBar;
        // tests page
        ListView testList; Label testStatus; Button testCancel;
        bool testsDone;
        // unattended run for the harness (harness/wizard_e2e.py): --auto --headset-ip X --headset-host H --report file
        readonly string[] cmd = Environment.GetCommandLineArgs();
        bool auto => cmd.Contains("--auto");
        string Arg(string k) { for (int i = 1; i + 1 < cmd.Length; i++) if (cmd[i] == k) return cmd[i + 1]; return null; }

        public WizardForm(Dictionary<string, object> check, bool demo)
        {
            this.check = check;
            this.demo = demo;
            Text = "VisionALVR - setup";
            Icon = Ui.AppIcon();
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            BackColor = Color.White;
            Width = 1080;
            Height = 900;
            MinimumSize = new Size(980, 760);
            for (int i = 0; i < 6; i++)
            {
                stepLabels[i] = new Label { Text = StepNames[i], AutoSize = false, Width = 200, Height = 30, Dock = DockStyle.Top, Font = Ui.Body, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
            }
            for (int i = 5; i >= 0; i--) stepsPanel.Controls.Add(stepLabels[i]);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 52, ColumnCount = 4, Padding = new Padding(8), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(advanced, 0, 0);
            bottom.Controls.Add(new Label(), 1, 0);
            bottom.Controls.Add(back, 2, 0);
            bottom.Controls.Add(next, 3, 0);
            back.Click += (s, e) => GoBack();
            next.Click += (s, e) => GoNext();
            advanced.Click += (s, e) => { Hide(); new ConfigureForm(check).ShowDialog(); Close(); };
            Controls.Add(content);
            Controls.Add(stepsPanel);
            Controls.Add(bottom);
            Controls.Add(CompactHeader());
            FormClosing += (s, e) => { discovery?.Dispose(); handshakeHost?.Stop(1000); testCts?.Cancel(); };
            Shown += (s, e) => Start();
            var shot = Ui.ScreenshotPath();
            if (shot != null) Ui.ScreenshotWhenShown(this, 3500);
        }

        static Control CompactHeader()
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, BackColor = Color.White, Padding = new Padding(12, 6, 12, 2), WrapContents = false };
            var logo = Ui.Resource("logo_640.png");
            if (logo != null) p.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Width = 180, Height = 76, Margin = new Padding(0) });
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(16, 10, 0, 0) };
            col.Controls.Add(new Label { Text = $"Setup   ·   version {Version.Text}", AutoSize = true, Font = Ui.Body, ForeColor = Color.DimGray });
            col.Controls.Add(new Label { Text = "ALPHA SOFTWARE - use at your own risk. For testing only.", AutoSize = true, Font = Ui.Bold, ForeColor = Color.White, BackColor = Color.FromArgb(0xC6, 0x28, 0x28), Padding = new Padding(6, 3, 6, 3), Margin = new Padding(0, 6, 0, 0) });
            p.Controls.Add(col);
            return p;
        }

        // ------------------------------------------------------------------------------------------------------------- navigation
        void Start()
        {
            var args = Environment.GetCommandLineArgs();
            int page = -1;
            for (int i = 1; i + 1 < args.Length; i++) if (args[i] == "--page") int.TryParse(args[i + 1], out page);
            if (demo) LoadDemo();
            var ok = check != null && check.TryGetValue("ok", out var o) && o is bool b && b;
            if (!ok && !demo) { GoTo(0); return; }
            if (auto && Arg("--headset-ip") != null)
            {
                chosenIp = Arg("--headset-ip"); chosenHost = Arg("--headset-host") ?? ""; chosenName = "Apple Vision Pro";
                step = 1; GoNext();
                return;
            }
            GoTo(page >= 1 ? page : 1);
        }

        void GoTo(int s)
        {
            step = s;
            discovery?.Dispose(); discovery = null;
            content.Controls.Clear();
            for (int i = 0; i < 6; i++)
            {
                stepLabels[i].Font = i == s ? Ui.Bold : Ui.Body;
                stepLabels[i].ForeColor = i == s ? Ui.Accent : (i < s ? Color.SeaGreen : Color.DimGray);
                stepLabels[i].Text = (i < s ? "✓  " : "") + StepNames[i];
            }
            back.Enabled = s > 1; back.Visible = true; next.Visible = true; next.Text = "Next >"; next.Enabled = true;
            Control page;
            switch (s)
            {
                case 0: page = PageGate(); back.Visible = false; next.Text = "Exit"; break;
                case 1: page = PageHeadset(); break;
                case 2: page = PageHandshake(); next.Visible = false; break;
                case 3: page = PageTests(); next.Enabled = false; break;
                case 4: page = PageResults(); break;
                default: page = PageConfig(); next.Text = "Save and start VisionALVR"; break;
            }
            page.Dock = DockStyle.Fill;
            content.Controls.Add(page);
            if (auto) BeginInvoke((Action)AutoDrive);
        }

        void GoBack()
        {
            if (step == 3) { testCts?.Cancel(); }
            if (step == 5 && !autoconfig) { GoTo(2); return; }
            GoTo(Math.Max(1, step - 1));
        }

        void GoNext()
        {
            switch (step)
            {
                case 0: Close(); break;
                case 1:
                    if (string.IsNullOrEmpty(chosenIp)) { MessageBox.Show(this, "Select a headset first (open the ALVR app on the Vision Pro), or add its IP address.", Text); return; }
                    Session.Pair(session, chosenHost, chosenName, chosenIp);
                    Json.Save(Paths.Session, session);
                    settings.HeadsetHost = chosenHost; settings.HeadsetName = chosenName; settings.HeadsetIp = chosenIp;
                    settings.Save();
                    Log.Write("INFO", $"paired headset {chosenName} ({chosenHost}) at {chosenIp}");
                    GoTo(2);
                    break;
                case 3: GoTo(4); break;
                case 4: GoTo(5); break;
                case 5:
                    if (SaveConfig() && !demo) { try { System.Diagnostics.Process.Start(Paths.Runtime); } catch { } Close(); }
                    break;
            }
        }

        static Label H(string text) { var l = Ui.Line(text, Ui.Big); l.ForeColor = Ui.Accent; return l; }

        // ------------------------------------------------------------------------------------------------------------- 0 gate
        Control PageGate()
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            p.Controls.Add(H("This PC cannot run VisionALVR"));
            var gpu = check != null && check.TryGetValue("gpu", out var g) ? g as Dictionary<string, object> : null;
            var reason = check != null && check.TryGetValue("reason", out var r) ? r as string : "the system check did not run (alvr_host.exe missing?)";
            p.Controls.Add(Ui.Line(reason));
            if (gpu != null) p.Controls.Add(Ui.Line($"Detected: {(gpu.TryGetValue("name", out var n) ? n : "?")}, driver {(gpu.TryGetValue("driver", out var d) ? d : "?")}"));
            p.Controls.Add(Ui.Line("VisionALVR streams with NVIDIA's hardware video encoder (NVENC) on GeForce RTX 40 series GPUs and newer (Ada / Blackwell)."));
            return p;
        }

        // ------------------------------------------------------------------------------------------------------------- 1 headset
        Control PageHeadset()
        {
            found = new ListView { View = View.Details, FullRowSelect = true, Height = 200, Width = 780, HideSelection = false, MultiSelect = false };
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            p.Controls.Add(H("Your headset"));
            p.Controls.Add(Ui.Line("Open the ALVR app on the Vision Pro (same network as this PC). It appears below within a few seconds. A headset you paired before is listed at once."));
            found.Columns.Clear(); found.Items.Clear(); rows.Clear();
            found.Columns.Add("Name (hostname)", 300);
            found.Columns.Add("IP address", 140);
            found.Columns.Add("Status", 320);
            found.SelectedIndexChanged += (s, e) =>
            {
                if (found.SelectedItems.Count == 0) { chosenHost = chosenIp = chosenName = ""; return; }
                var t = (string[])found.SelectedItems[0].Tag;
                chosenHost = t[0]; chosenIp = t[1]; chosenName = t[2];
            };
            found.DoubleClick += (s, e) => GoNext();
            p.Controls.Add(found);
            var manual = new FlowLayoutPanel { AutoSize = true };
            var ipBox = new TextBox { Width = 140 };
            var add = new Button { Text = "Add this IP address", AutoSize = true };
            add.Click += (s, e) => { if (System.Net.IPAddress.TryParse(ipBox.Text.Trim(), out _)) AddRow("", ipBox.Text.Trim(), "Apple Vision Pro", "added by IP address", true); };
            manual.Controls.Add(Ui.Line("Headset with a fixed IP not listed?")); manual.Controls.Add(ipBox); manual.Controls.Add(add);
            p.Controls.Add(manual);
            p.Controls.Add(discStatus);
            // the headsets we already know: paired in session.json, saved in visionalvr.json (they must be in the list AND selected)
            var hsPaired = Session.Headset(session);
            if (hsPaired != null) AddRow(hsPaired.Item1, hsPaired.Item3, hsPaired.Item2, "paired before", true);
            if (!string.IsNullOrEmpty(settings.HeadsetIp)) AddRow(settings.HeadsetHost, settings.HeadsetIp, string.IsNullOrEmpty(settings.HeadsetName) ? "Apple Vision Pro" : settings.HeadsetName, "saved address", true);
            if (demo && found.Items.Count == 0) AddRow("1234.client.alvr", "192.168.1.50", "Apple Vision Pro", "paired before - reachable", false);
            if (found.Items.Count > 0) found.Items[0].Selected = true;
            if (!demo) StartDiscovery();
            return p;
        }

        void AddRow(string host, string ip, string name, string status, bool known)
        {
            if (string.IsNullOrEmpty(ip)) return;
            // one row per headset: merge by hostname, else by address
            ListViewItem item = null;
            foreach (var kv in rows) { var t = (string[])kv.Value.Tag; if ((host.Length > 0 && t[0] == host) || t[1] == ip) { item = kv.Value; break; } }
            if (item == null)
            {
                item = new ListViewItem(new[] { host.Length > 0 ? $"{name} ({host})" : name, ip, status }) { Tag = new[] { host, ip, name } };
                found.Items.Add(item);
                rows[host + "|" + ip] = item;
            }
            else
            {
                var t = (string[])item.Tag;
                var h2 = host.Length > 0 ? host : t[0]; var ip2 = ip; var nm = string.IsNullOrEmpty(name) ? t[2] : name;
                item.Tag = new[] { h2, ip2, nm };
                item.SubItems[0].Text = h2.Length > 0 ? $"{nm} ({h2})" : nm;
                item.SubItems[1].Text = ip2;
                item.SubItems[2].Text = status;
            }
            if (found.SelectedItems.Count == 0) { item.Selected = true; }
            if (known) PingRow(item, ip);
        }

        void PingRow(ListViewItem item, string ip)
        {
            Task.Run(() =>
            {
                string res;
                try { using (var pg = new Ping()) res = pg.Send(ip, 1500).Status == IPStatus.Success ? "reachable" : "no answer to ping (it may still be found by the ALVR app)"; }
                catch { res = "not checked"; }
                try { BeginInvoke((Action)(() => { if (!item.ListView?.IsDisposed ?? false) item.SubItems[2].Text = item.SubItems[2].Text.Split('-')[0].Trim() + " - " + res; })); } catch { }
            });
        }

        void StartDiscovery()
        {
            discovery?.Dispose();
            discovery = new Discovery();
            discovery.OnFound += f => BeginInvoke((Action)(() =>
            {
                if (step != 1) return;
                var name = f.Hostname.EndsWith(".client.alvr") ? "Apple Vision Pro" : f.Hostname;
                AddRow(f.Hostname, f.Ip, name, f.Compatible ? $"found on the network (ALVR protocol {f.Protocol})" : $"INCOMPATIBLE ({f.Protocol}, streamer {f.ServerProtocol})", false);
            }));
            discovery.OnError += m => BeginInvoke((Action)(() => { discStatus.Text = "Discovery failed: " + m; discStatus.ForeColor = Color.Firebrick; }));
            var err = discovery.Start();
            discStatus.Text = err ?? "Looking for headsets (Bonjour/mDNS and UDP 9943)... If Windows asks, allow alvr_host.exe on private networks.";
            discStatus.ForeColor = err == null ? Color.DimGray : Color.Firebrick;
        }

        // ------------------------------------------------------------------------------------------------------------- 2 handshake
        Control PageHandshake()
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            p.Controls.Add(H("What we learned from your headset"));
            hsStatus = Ui.Line($"Connecting to {chosenName} at {chosenIp}... open the ALVR app on the headset and leave it on its start screen.");
            p.Controls.Add(hsStatus);
            hsBar = new ProgressBar { Style = ProgressBarStyle.Marquee, Width = 500, Height = 14 };
            p.Controls.Add(hsBar);
            var bar = new FlowLayoutPanel { AutoSize = true };
            hsRetry = new Button { Text = "Try again", AutoSize = true, Visible = false };
            hsRetry.Click += (s, e) => BeginHandshake();
            hsSkip = new Button { Text = "Continue without the headset (default Vision Pro values)", AutoSize = true, Visible = false };
            hsSkip.Click += (s, e) => { hs = new Handshake { DisplayName = chosenName, Hostname = chosenHost, Ip = chosenIp }; ShowHandshakeResult(false); };
            bar.Controls.Add(hsRetry); bar.Controls.Add(hsSkip);
            p.Controls.Add(bar);
            hsDetails = new FlowLayoutPanel { AutoSize = true, Visible = false, WrapContents = false };
            p.Controls.Add(hsDetails);
            hsAuto = new Button { Text = "Autoconfigure (recommended)", AutoSize = true, Font = Ui.Big, Padding = new Padding(10), Visible = false };
            hsAuto.Click += (s, e) => { autoconfig = true; ApplyOverrides(); GoTo(3); };
            hsManual = new Button { Text = "Manual configuration / update my existing settings", AutoSize = true, Visible = false };
            hsManual.Click += (s, e) => { autoconfig = false; ApplyOverrides(); GoTo(5); };
            var choices = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
            choices.Controls.Add(hsAuto); choices.Controls.Add(hsManual);
            p.Controls.Add(choices);
            if (demo) ShowHandshakeResult(true); else BeginHandshake();
            return p;
        }

        void BeginHandshake()
        {
            hsRetry.Visible = false; hsSkip.Visible = false; hsBar.Visible = true;
            hsStatus.Text = $"Connecting to {chosenName} at {chosenIp}... open the ALVR app on the headset and leave it on its start screen.";
            hsStatus.ForeColor = Color.Black;
            handshakeHost?.Stop(1000);
            handshakeHost = new HostProcess();
            Dictionary<string, object> caps = null;
            var local = handshakeHost;
            local.OnEvent += (n, d) => BeginInvoke((Action)(() =>
            {
                if (local != handshakeHost) return;
                if (n == "client_capabilities") caps = d;
                else if (n == "setup_error") { hsStatus.Text = "Cannot connect: " + (d != null && d.TryGetValue("message", out var m) ? m : "setup error"); hsStatus.ForeColor = Color.Firebrick; }
            }));
            local.OnExit += code => BeginInvoke((Action)(() =>
            {
                if (local != handshakeHost || step != 2) return;
                hsBar.Visible = false;
                if (caps == null)
                {
                    if (hsStatus.ForeColor != Color.Firebrick) { hsStatus.Text = "The headset did not answer. Is the ALVR app open on it, on the same network (and is VisionALVR allowed through the firewall)?"; hsStatus.ForeColor = Color.DarkOrange; }
                    hsRetry.Visible = true; hsSkip.Visible = true;
                    return;
                }
                hs = Handshake.From(caps);
                session = Session.Load();
                var h = Session.Headset(session);
                hs.Hostname = h != null ? h.Item1 : chosenHost; hs.Ip = chosenIp;
                hs.DisplayName = h != null && h.Item2 != null && h.Item2.Length > 0 ? h.Item2 : chosenName;
                ShowHandshakeResult(true);
                if (auto) hsAuto.PerformClick();
            }));
            discovery?.Dispose();
            var err = local.Start(Base() + "--live --seconds 3 --connect-timeout 90");
            if (err != null) { hsStatus.Text = err; hsStatus.ForeColor = Color.Firebrick; hsBar.Visible = false; hsRetry.Visible = true; }
        }

        static string Base() => $"--install-dir \"{Paths.Dir}\" ";

        void ShowHandshakeResult(bool real)
        {
            hsBar.Visible = false; hsRetry.Visible = false; hsSkip.Visible = false;
            hsStatus.Text = real ? $"Connected to {hs.DisplayName}. This is what it told us, and what the setup will be based on:" : "No headset data: the defaults of a Vision Pro are used, and the tests that need the headset are skipped.";
            hsStatus.ForeColor = real ? Color.SeaGreen : Color.DarkOrange;
            hsDetails.Controls.Clear();
            var g = new TableLayoutPanel { ColumnCount = 3, AutoSize = true };
            void row(string k, string v, Control edit = null)
            {
                g.Controls.Add(Ui.Line(k, Ui.Bold));
                if (edit == null) { var t = Ui.Line(v); g.Controls.Add(t); g.SetColumnSpan(t, 2); return; }
                g.Controls.Add(edit);
                g.Controls.Add(Ui.Line(v));
            }
            row("Headset", $"{hs.DisplayName}   ({hs.Hostname})   vendor: {hs.Vendor}");
            row("Profile", hs.IsVisionPro ? "Apple Vision Pro: tuned defaults" : "no tuned profile for this headset: it may work, it is not supported");
            row("Panel resolution per eye", $"{hs.PanelW} x {hs.PanelH}", null);
            rateBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
            foreach (var r in hs.Rates) rateBox.Items.Add(r.ToString("0.#", CultureInfo.InvariantCulture));
            rateBox.SelectedIndex = Math.Max(0, hs.Rates.Count - 1);
            row("Refresh rate to use", "the headset supports " + string.Join(", ", hs.Rates.Select(x => x.ToString("0.#", CultureInfo.InvariantCulture))) + " Hz", rateBox);
            scaleBox = new NumericUpDown { Minimum = 40, Maximum = 100, Increment = 5, Value = 100, Width = 70 };
            row("Resolution scale (%)", "100% = the panel's own resolution; lower renders and streams less", scaleBox);
            foveationBox = new CheckBox { Text = "use it", Checked = hs.Foveation, Enabled = hs.Foveation, AutoSize = true };
            row("Foveated encoding", hs.Foveation ? "supported by the headset" : "not advertised by the headset", foveationBox);
            row("Video codec", $"HEVC{(hs.EncHigh ? " (high profile)" : "")}{(hs.Enc10 ? ", 10-bit" : ", 8-bit")}{(hs.EncAv1 ? "; the headset also decodes AV1 (VisionALVR encodes HEVC)" : "")}");
            row("Colour", $"{(hs.PreferFull ? "full range" : "limited range")}, encoding gamma {hs.Gamma:0.00}{(hs.PreferHdr ? ", HDR preferred" : "")}");
            row("Microphone", hs.MicRate > 0 ? $"{hs.MicRate} Hz" : "none advertised");
            hsDetails.Controls.Add(g);
            hsDetails.Visible = true;
            hsAuto.Visible = true; hsManual.Visible = true;
        }

        void ApplyOverrides()
        {
            if (rateBox != null && rateBox.SelectedItem != null) ctxFps = double.Parse((string)rateBox.SelectedItem, CultureInfo.InvariantCulture);
            ctxScale = scaleBox != null ? (double)scaleBox.Value / 100.0 : 1.0;
            ctxFoveation = foveationBox == null || foveationBox.Checked;
        }

        double ctxFps = 90, ctxScale = 1.0; bool ctxFoveation = true;

        // ------------------------------------------------------------------------------------------------------------- 3 tests
        Control PageTests()
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            p.Controls.Add(H("Testing your setup"));
            p.Controls.Add(Ui.Line("Each test is ticked off when it is done. This takes a few minutes; the headset ones need the ALVR app open on it."));
            testList = new ListView { View = View.Details, FullRowSelect = true, Width = 790, Height = 300, HeaderStyle = ColumnHeaderStyle.None, ShowItemToolTips = true };
            testList.Columns.Add("Test", 330); testList.Columns.Add("Result", 440);
            p.Controls.Add(testList);
            testStatus = Ui.Line("");
            p.Controls.Add(testStatus);
            testCancel = new Button { Text = "Cancel the tests", AutoSize = true };
            testCancel.Click += (s, e) => { testCts?.Cancel(); testCancel.Enabled = false; };
            p.Controls.Add(testCancel);
            StartTests();
            return p;
        }

        static string Glyph(TestState s)
        {
            switch (s)
            {
                case TestState.Passed: return "☑";   // ballot box with check
                case TestState.Warning: return "⚠";
                case TestState.Failed: return "☒";   // ballot box with X
                case TestState.Skipped: return "➖";
                case TestState.Running: return "▶";
                default: return "☐";                 // empty ballot box
            }
        }

        void StartTests()
        {
            testsDone = false;
            // the candidate configuration (the headset's panel, the chosen rate) goes to disk first: the encoder tests read it
            var panel = new SettingsPanel(settings, session);
            panel.Load();
            ctx = new TestContext { HS = hs, Check = check, Fps = ctxFps, HeadsetAvailable = hs.FromHeadset };
            ctx.R.TestFps = ctxFps;
            var cand = Proposal.Build(hs, new Results(), ctxFps);
            cand.EyeW = (int)(hs.PanelW * ctxScale) / 32 * 32; cand.EyeH = (int)(hs.PanelH * ctxScale); cand.Foveated = ctxFoveation;
            panel.Prefill(cand);
            if (!panel.Save(out var err) && err != null) { testStatus.Text = err; testStatus.ForeColor = Color.Firebrick; }
            steps = demo ? DemoSteps() : Tests.Build(ctx);
            foreach (var st in steps) testList.Items.Add(new ListViewItem(new[] { Glyph(st.State) + "  " + st.Name, "" }));
            if (demo) { FinishTests(); return; }
            testCts = new CancellationTokenSource();
            ctx.Ct = testCts.Token;
            ctx.OnEncoderDone = () => Invoke((Action)(() =>
            {
                // the encoder test measured the candidate: the tests that follow run at the interim proposal (lower resolution / rate if it could not keep up)
                var interim = Proposal.Build(hs, ctx.R, ctxFps);
                interim.Foveated = ctxFoveation;
                if (interim.EyeW > (int)(hs.PanelW * ctxScale) / 32 * 32) interim.EyeW = (int)(hs.PanelW * ctxScale) / 32 * 32;
                var pn = new SettingsPanel(settings, session);
                pn.Load();
                pn.Prefill(interim);
                if (pn.Save(out var e2)) Log.Write("INFO", $"wizard: tests continue at {interim.EyeW}x{interim.EyeH} @ {interim.Fps} Hz");
            }));
            ctx.Status = s => BeginInvoke((Action)(() => { testStatus.Text = s; testStatus.ForeColor = Color.DimGray; }));
            Task.Run(async () =>
            {
                await Tests.RunAll(steps, ctx, st => BeginInvoke((Action)(() => UpdateRow(st))));
                BeginInvoke((Action)FinishTests);
            });
        }

        void UpdateRow(TestStep st)
        {
            var i = steps.IndexOf(st);
            if (i < 0 || i >= testList.Items.Count) return;
            var it = testList.Items[i];
            it.SubItems[0].Text = Glyph(st.State) + "  " + st.Name;
            it.SubItems[1].Text = st.Note;
            it.ToolTipText = st.Name + "\n" + st.Note;
            it.ForeColor = st.State == TestState.Failed ? Color.Firebrick : st.State == TestState.Warning ? Color.DarkOrange : st.State == TestState.Passed ? Color.SeaGreen : st.State == TestState.Skipped ? Color.Gray : Color.Black;
            if (st.State == TestState.Running) testList.EnsureVisible(i);
        }

        void FinishTests()
        {
            if (step != 3) return;
            testsDone = true;
            testCancel.Enabled = false;
            for (int i = 0; i < steps.Count; i++) UpdateRow(steps[i]);
            var failed = steps.Count(x => x.State == TestState.Failed);
            testStatus.Text = failed == 0 ? "Done." : $"Done: {failed} test(s) failed (details in logs\\VisionALVR.log); the proposal uses defaults where a measurement is missing.";
            testStatus.ForeColor = failed == 0 ? Color.SeaGreen : Color.DarkOrange;
            proposal = Proposal.Build(hs, ctx.R, ctxFps);
            next.Enabled = true;
            if (auto) GoNext();
        }

        // ------------------------------------------------------------------------------------------------------------- 4 results
        Control PageResults()
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            p.Controls.Add(H("How your setup performs"));
            var r = ctx?.R ?? new Results();
            var list = new ListView { View = View.Details, FullRowSelect = true, Width = 790, Height = 330, ShowItemToolTips = true };
            list.Columns.Add("Measurement", 230); list.Columns.Add("Your value", 200); list.Columns.Add("Reference (provisional)", 250); list.Columns.Add("Verdict", 90);
            var bad = new List<string>();
            void add(string name, string val, string refText, Rating rt, string advice = null)
            {
                var it = new ListViewItem(new[] { name, val, refText, rt == Rating.Unknown ? "not measured" : rt.ToString() });
                it.ForeColor = rt == Rating.Good ? Color.SeaGreen : rt == Rating.Ok ? Color.DarkOrange : rt == Rating.Poor ? Color.Firebrick : Color.Gray;
                list.Items.Add(it);
                if (rt == Rating.Poor && advice != null) bad.Add(advice);
            }
            var budget = r.BudgetMs > 0 ? r.BudgetMs : 1000.0 / Math.Max(1, ctxFps);
            add("Encoder time per frame (p95)", r.EncodeP95 > 0 ? $"{r.EncodeP95:0.0} ms of {budget:0.0}" : "-", $"under {Reference.Get("encode_good") * 100:0}% good, {Reference.Get("encode_ok") * 100:0}% ok of the frame time", Reference.EncodeBudget(r.EncodeP95, budget),
                "The encoder is close to or over the frame time: the proposal lowers the resolution or the refresh rate.");
            add("Clean network bitrate", r.CleanMbps > 0 ? $"{r.CleanMbps:0} Mbps" : "-", $"{Reference.Get("bitrate_good_mbps"):0}+ good, {Reference.Get("bitrate_ok_mbps"):0}+ ok", Reference.Bitrate(r.CleanMbps),
                "The link carries little: use a Wi-Fi 6E (6 GHz) router in access-point mode close to the headset, and put the PC on Ethernet.");
            add("Packets lost per second", r.LossPerS >= 0 && r.CleanMbps > 0 ? $"{r.LossPerS:0.00}" : "-", "0 good, up to 1 ok", Reference.Loss(r.CleanMbps > 0 ? r.LossPerS : -1), "The wireless link drops packets: move closer, use the 6 GHz band, avoid other traffic on that access point.");
            add("Total latency (ALVR's own)", r.TotalMs > 0 ? $"{r.TotalMs:0.0} ms" : "-", $"under {Reference.Get("latency_good_ms"):0} good, {Reference.Get("latency_ok_ms"):0} ok", Reference.Latency(r.TotalMs));
            add("   network", r.NetworkMs > 0 ? $"{r.NetworkMs:0.0} ms" : "-", $"under {Reference.Get("network_good_ms"):0} good", Reference.Network(r.NetworkMs));
            add("   headset decoder", r.DecodeMs > 0 ? $"{r.DecodeMs:0.0} ms" : "-", $"under {Reference.Get("decode_good_ms"):0} good", Reference.Decode(r.DecodeMs));
            add("   encoder (ALVR's view)", r.EncoderMs > 0 ? $"{r.EncoderMs:0.0} ms" : "-", "", Rating.Unknown);
            add("   headset queues", r.DecoderQueueMs + r.VsyncQueueMs > 0 ? $"{r.DecoderQueueMs + r.VsyncQueueMs:0.0} ms" : "-", "", Rating.Unknown);
            add("Frames shown / refresh rate", r.DisplayedFps > 0 ? $"{r.DisplayedFps:0} of {ctxFps:0} fps" : "-", $"{Reference.Get("fps_good_share") * 100:0}%+ good", Reference.FpsShare(r.DisplayedFps > 0 ? r.DisplayedFps / ctxFps : 0));
            add("GPU", string.IsNullOrEmpty(r.GpuName) ? "-" : r.GpuName, $"{r.Engines} hardware encoder engine(s), {r.VramMb / 1024.0:0} GB", Rating.Unknown);
            p.Controls.Add(list);
            var good = list.Items.Cast<ListViewItem>().Count(x => x.SubItems[3].Text == "Good");
            var poor = list.Items.Cast<ListViewItem>().Count(x => x.SubItems[3].Text == "Poor");
            var okc = list.Items.Cast<ListViewItem>().Count(x => x.SubItems[3].Text == "Ok");
            var verdict = poor > 0 ? "Limited: some parts are below the reference (see below)" : (okc > 0 ? "Good: the setup works, a few values are only acceptable" : (good > 0 ? "Excellent: everything measured is within the reference" : "Not enough measurements to judge"));
            p.Controls.Add(Ui.Line(verdict, Ui.Big));
            foreach (var a in bad.Distinct()) p.Controls.Add(Ui.Line("• " + a));
            var note = Ui.Line("The reference values are provisional: they will come from our lab comparisons with stock ALVR + SteamVR.");
            note.ForeColor = Color.DimGray;
            p.Controls.Add(note);
            return p;
        }

        // ------------------------------------------------------------------------------------------------------------- 5 configuration
        Control PageConfig()
        {
            var p = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };
            p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            p.Controls.Add(H("Your configuration"));
            p.Controls.Add(Ui.Line(autoconfig && proposal != null
                ? "Proposed from your headset and the tests, with the reason next to each value. Change anything you like."
                : "Your current settings (from config\\session.json and visionalvr.json). Change anything you like."));
            session = Session.Load();
            settingsPanel = new SettingsPanel(settings, session) { Dock = DockStyle.Fill };
            settingsPanel.Load();
            if (autoconfig && proposal != null) settingsPanel.Prefill(proposal);
            p.Controls.Add(settingsPanel);
            return p;
        }

        bool SaveConfig()
        {
            if (settingsPanel == null) return false;
            if (!settingsPanel.Save(out var err)) { MessageBox.Show(this, err, Text); return false; }
            return true;
        }

        void AutoDrive()
        {
            if (step == 4) { var t = new System.Windows.Forms.Timer { Interval = 600 }; t.Tick += (s, e) => { t.Stop(); GoNext(); }; t.Start(); }
            else if (step == 5)
            {
                var t = new System.Windows.Forms.Timer { Interval = 600 };
                t.Tick += (s, e) => { t.Stop(); SaveConfig(); WriteReport(); Close(); };
                t.Start();
            }
            else if (step == 0) { WriteReport(); Close(); }
        }

        void WriteReport()
        {
            var path = Arg("--report");
            if (path == null) return;
            var r = ctx?.R ?? new Results();
            var d = new Dictionary<string, object>
            {
                ["handshake"] = new Dictionary<string, object> { ["display_name"] = hs.DisplayName, ["hostname"] = hs.Hostname, ["panel"] = new object[] { hs.PanelW, hs.PanelH }, ["rates"] = hs.Rates.Cast<object>().ToArray(), ["foveation"] = hs.Foveation, ["gamma"] = hs.Gamma, ["from_headset"] = hs.FromHeadset },
                ["results"] = new Dictionary<string, object>
                {
                    ["gpu"] = r.GpuName, ["encode_p50"] = r.EncodeP50, ["encode_p95"] = r.EncodeP95, ["budget_ms"] = r.BudgetMs, ["encode_fits"] = r.EncodeFits, ["enc_size"] = new object[] { r.EncW, r.EncH },
                    ["quality"] = r.QualityLabel, ["clean_mbps"] = r.CleanMbps, ["loss_per_s"] = r.LossPerS, ["total_ms"] = r.TotalMs, ["network_ms"] = r.NetworkMs, ["decode_ms"] = r.DecodeMs, ["displayed_fps"] = r.DisplayedFps, ["audio"] = r.AudioNote,
                },
                ["tests"] = (steps ?? new List<TestStep>()).Select(x => (object)new Dictionary<string, object> { ["name"] = x.Name, ["state"] = x.State.ToString(), ["note"] = x.Note }).ToArray(),
                ["proposal"] = proposal == null ? null : new Dictionary<string, object>
                {
                    ["eye"] = new object[] { proposal.EyeW, proposal.EyeH }, ["fps"] = proposal.Fps, ["bitrate_mbps"] = proposal.BitrateMbps, ["foveated"] = proposal.Foveated, ["latency"] = proposal.LatencyMode,
                    ["why_resolution"] = proposal.Why("resolution"), ["why_bitrate"] = proposal.Why("bitrate"), ["why_fps"] = proposal.Why("fps"),
                },
            };
            try { Json.Save(path, d); } catch (Exception e) { Log.Write("ERROR", "report: " + e.Message); }
        }

        // ------------------------------------------------------------------------------------------------------------- demo data (screenshots, no headset)
        void LoadDemo()
        {
            hs = new Handshake
            {
                FromHeadset = true, DisplayName = "Apple Vision Pro", Hostname = "1234.client.alvr", Ip = "192.168.1.50", PanelW = 3552, PanelH = 3200,
                Rates = new List<double> { 90, 96, 100 }, MicRate = 48000, Gamma = 1.0,
            };
            chosenHost = hs.Hostname; chosenIp = hs.Ip; chosenName = hs.DisplayName; ctxFps = 90;
            ctx = new TestContext { HS = hs, Check = check, HeadsetAvailable = true };
            ctx.R = new Results
            {
                GpuName = "NVIDIA GeForce RTX 5090", GpuArch = "Blackwell", Driver = "617.42", VramMb = 32768, EncodeP50 = 3.1, EncodeP95 = 3.6, BudgetMs = 11.1, EncodeFits = true, Engines = 3,
                CleanMbps = 270, LossPerS = 0.02, TotalMs = 52, NetworkMs = 9.5, DecodeMs = 6.2, EncoderMs = 4.1, DecoderQueueMs = 8, VsyncQueueMs = 10, DisplayedFps = 89, ClientFps = 90, TestFps = 90,
            };
            proposal = Proposal.Build(hs, ctx.R, 90);
            autoconfig = true;
        }

        List<TestStep> DemoSteps()
        {
            var l = new List<TestStep>();
            var states = new[] { (TestState.Passed, "NVIDIA GeForce RTX 5090 (Blackwell), driver 617.42, 32 GB"), (TestState.Passed, "3552x3200 encoded: p50 3.1 ms, p95 3.6 ms of 11.1 ms per frame; 3 NVENC engine(s)"),
                (TestState.Passed, "recommended: P3 foveated (47.1 dB, encode p95 3.6 ms)"), (TestState.Passed, "clean up to 270 Mbps (the highest clean step minus 10%)"),
                (TestState.Warning, "latency 52.0 ms (encode 4.1, network 9.5, decode 6.2, headset queues 18.0), 89 fps shown"), (TestState.Running, "") };
            var names = new[] { "System check (GPU, driver, hardware encoder)", "Encoder speed at the chosen resolution and refresh rate", "Picture quality benchmark (about 1.5 minutes)", "Network test with the headset",
                "Streaming test with the headset (latency, frames, decoder)", "Audio output" };
            for (int i = 0; i < names.Length; i++) l.Add(new TestStep { Name = names[i], State = states[i].Item1, Note = states[i].Item2 });
            return l;
        }
    }
}
