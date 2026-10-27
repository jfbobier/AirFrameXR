// configure.exe as an autoconfigure wizard (docs/CONFIGURE_WIZARD.md):
//   0 gate (NVIDIA GPU + NVENC API 12.2) - 1 headset - 2 handshake and what we learned - 3 tests with a checklist - 4 how the setup performs - 5 proposed configuration.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AirFrameXR
{
    public class WizardForm : DpiForm
    {
        readonly AppSettings settings = new AppSettings();
        Dictionary<string, object> session = Session.Load();
        readonly Dictionary<string, object> check;
        readonly bool demo;
        Discovery discovery;
        LinkLabel usbHint; bool seenOnNetwork;   // the Quest-over-USB link: shown only when discovery found nothing
        HostProcess handshakeHost;
        CancellationTokenSource testCts;
        readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14) };
        readonly Panel stepsPanel = new Panel { Dock = DockStyle.Left, Width = 230, BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9), Padding = new Padding(12) };
        readonly Button back = new Button { Text = "< Back", AutoSize = true, MinimumSize = new Size(90, 30) };
        readonly Button next = new Button { Text = "Next >", AutoSize = true, MinimumSize = new Size(110, 30), Font = Ui.Bold };
                readonly Label[] stepLabels = new Label[4];
        Control wizHeader;
        // four steps the user sees; the pages behind them: PC check + headset + handshake / choice of setup / tests / verdict + configuration
        static readonly string[] StepNames = { "1  Headset", "2  Setup", "3  Test", "4  Result" };
        static int Group(int page) => page <= 2 ? 0 : page == 3 ? 1 : page == 4 ? 2 : 3;
        int step;
        // state
        Handshake hs = new Handshake();
        TestContext ctx;
        List<TestStep> steps;
        Proposal proposal;
        HmdProfile profile;          // the profile that matches the handshake (null: generic proposal from the capabilities and the searches)
        HmdProfile importedProfile;  // a profile the user imported on the handshake page: it wins over the automatic match
        string variantId = "";       // the network variant chosen for it
        ComboBox variantBox;
        SettingsPanel settingsPanel;
        Tuple<bool, string> chromaChoice;   // the chroma-key answer (on, rrggbb), written once the profile or the proposal is, so that neither overwrites it
        bool autoconfig;
        // headset page
        ListView found = new ListView { View = View.Details, FullRowSelect = true, Height = 200, Width = 780, HideSelection = false, MultiSelect = false };
        readonly Dictionary<string, ListViewItem> rows = new Dictionary<string, ListViewItem>();
        readonly Label discStatus = Ui.Line("");
        string chosenHost = "", chosenIp = "", chosenName = "";
        // handshake page
        ComboBox rateBox; NumericUpDown scaleBox; CheckBox foveationBox; Label hsStatus; FlowLayoutPanel hsDetails, modePanel; Button hsRetry, hsSkip, hsGo;
        RadioButton modeProfile, modeManual, modeExisting; HmdProfile matchedProfile;
        ProgressBar hsBar; bool hsReal, hsDone;   // hsDone: the handshake of the paired headset is in hand (Back from the next page does not redo it)
        // tests page
        ListView testList; Label testStatus; Button testCancel;
        // unattended run for the harness (harness/wizard_e2e.py): --auto --headset-ip X --headset-host H --report file
        readonly string[] cmd = Environment.GetCommandLineArgs();
        bool auto => cmd.Contains("--auto");
        // --diagnose: the same handshake, tests and verdict on the settings as they are (no choice of setup, no configuration page); Close at the end
        bool diagnose => cmd.Contains("--diagnose");
        string Arg(string k) { for (int i = 1; i + 1 < cmd.Length; i++) if (cmd[i] == k) return cmd[i + 1]; return null; }

        public WizardForm(Dictionary<string, object> check, bool demo)
        {
            this.check = check;
            this.demo = demo;
            Text = cmd.Contains("--diagnose") ? "AirFrameXR \u2013 Diagnose" : "AirFrameXR \u2013 Setup";
            Icon = Ui.AppIcon();
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96F, 96F);   // the pixel sizes below are designed at 96 dpi; without this nothing scales and only the fonts grow
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            BackColor = Color.White;
            Width = 1080; Height = 900;   // design values at 96 dpi: scaled at load, then kept inside the work area
            MinimumSize = new Size(980, 760);
            Ui.FitToWorkArea(this);
            for (int i = 0; i < 4; i++)
            {
                stepLabels[i] = new Label { Text = StepNames[i], AutoSize = false, Height = 30, Dock = DockStyle.Top, Font = Ui.Body, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
            }
            for (int i = 3; i >= 0; i--) stepsPanel.Controls.Add(stepLabels[i]);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(8), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(new Label(), 1, 0);
            bottom.Controls.Add(back, 2, 0);
            bottom.Controls.Add(next, 3, 0);
            back.Click += (s, e) => GoBack();
            next.Click += (s, e) => GoNext();
            Controls.Add(content);
            Controls.Add(stepsPanel);
            Controls.Add(bottom);
            wizHeader = Ui.CompactHeader(cmd.Contains("--diagnose") ? "Diagnose" : "Setup");   // the logo greets on the first page only
            Controls.Add(wizHeader);
            FormClosing += (s, e) => { discovery?.Dispose(); handshakeHost?.Stop(1000); testCts?.Cancel(); };
            Shown += (s, e) => Start();
            var shot = Ui.ScreenshotPath();
            if (shot != null) Ui.ScreenshotWhenShown(this, 3500);
        }

        /// uitest.exe: one page of the demo wizard, without the discovery and the tests.
        public void ForTest(int page) { if (demo) LoadDemo(); GoTo(page); }

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
            if (wizHeader != null) wizHeader.Visible = s <= 1;
            discovery?.Dispose(); discovery = null;
            content.Controls.Clear();
            int g = Group(s), shown = 0;
            for (int i = 0; i < 4; i++)
            {
                stepLabels[i].Visible = !(diagnose && i == 1);   // diagnosing keeps the setup as it is: no Setup step
                if (!stepLabels[i].Visible) continue;
                shown++;
                stepLabels[i].Font = i == g ? Ui.Bold : Ui.Body;
                stepLabels[i].ForeColor = i == g ? Ui.Accent : (i < g ? Color.SeaGreen : Color.DimGray);
                stepLabels[i].Text = (i < g ? "\u2713  " : "") + (diagnose && i > 1 ? StepNames[i].Replace((i + 1).ToString(), i.ToString()) : StepNames[i]);
            }
            back.Enabled = s > 1; back.Visible = true; next.Visible = true; next.Text = "Next >"; next.Enabled = true;
            Control page;
            switch (s)
            {
                case 0: page = PageGate(); back.Visible = false; next.Text = "Exit"; break;
                case 1: page = PageHeadset(); break;
                case 2: page = PageHandshake(); next.Visible = false; break;
                case 3:
                    page = PageMode(); next.Visible = false;
                    if (diagnose) BeginInvoke((Action)(() => { modeExisting.Checked = true; ContinueToTests(); }));
                    break;
                case 4: page = PageTests(); next.Enabled = false; break;
                case 5: page = PageResults(); break;
                default: page = PageConfig(); next.Text = "Save and start AirFrameXR"; break;
            }
            Ui.LateScale(page, this);   // built after the window loaded: scaled by hand
            page.Dock = DockStyle.Fill;
            content.Controls.Add(page);
            if (diagnose && s == 5) next.Text = "Close";
            if (auto) BeginInvoke((Action)AutoDrive);
        }

        void GoBack()
        {
            if (step == 4) { testCts?.Cancel(); }
            GoTo(Math.Max(1, step - 1));
        }

        void GoNext()
        {
            switch (step)
            {
                case 0: Close(); break;
                case 1:
                    if (string.IsNullOrEmpty(chosenIp)) { MessageBox.Show(this, "Select a headset first (open the ALVR app on the headset), or add its IP address.", Text); return; }
                    Session.Pair(session, chosenHost, chosenName, chosenIp);
                    Json.Save(Paths.Session, session);
                    settings.HeadsetHost = chosenHost; settings.HeadsetName = chosenName; settings.HeadsetIp = chosenIp;
                    settings.Save();
                    Log.Write("INFO", $"paired headset {chosenName} ({chosenHost}) at {chosenIp}");
                    hsDone = false;
                    GoTo(2);
                    break;
                case 4: GoTo(5); break;
                case 5: if (diagnose) Close(); else GoTo(6); break;
                case 6:
                    if (SaveConfig() && !demo) { try { System.Diagnostics.Process.Start(Paths.Runtime); } catch { } Close(); }
                    break;
            }
        }

        static Label H(string text) { var l = Ui.Line(text, Ui.Big); l.ForeColor = Ui.Accent; return l; }

        // ------------------------------------------------------------------------------------------------------------- 0 gate
        Control PageGate()
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            p.Controls.Add(H("This PC cannot run AirFrameXR"));
            var gpu = check != null && check.TryGetValue("gpu", out var g) ? g as Dictionary<string, object> : null;
            var reason = check != null && check.TryGetValue("reason", out var r) ? r as string : "the system check did not run (alvr_host.exe missing?)";
            p.Controls.Add(Ui.Line(reason));
            if (gpu != null) p.Controls.Add(Ui.Line($"Detected: {(gpu.TryGetValue("name", out var n) ? n : "?")}, driver {(gpu.TryGetValue("driver", out var d) ? d : "?")}"));
            p.Controls.Add(Ui.Line("AirFrameXR streams with NVIDIA's hardware video encoder (NVENC): it needs an NVIDIA GPU and a driver with NVENC API 12.2 or newer (any recent driver; update it from nvidia.com if this fails)."));
            return p;
        }

        // ------------------------------------------------------------------------------------------------------------- 1 headset
        Control PageHeadset()
        {
            found = new ListView { View = View.Details, FullRowSelect = true, Height = 200, Width = 780, HideSelection = false, MultiSelect = false };
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            p.Controls.Add(H("Your headset"));
            p.Controls.Add(Ui.Line("Open the ALVR app on the headset (same network as this PC). It appears below within a few seconds. A headset you paired before is listed at once."));
            found.Columns.Clear(); found.Items.Clear(); rows.Clear();
            Ui.Col(found, "Name (hostname)", 300);
            Ui.Col(found, "IP address", 140);
            Ui.Col(found, "Status", 320);
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
            add.Click += (s, e) => { if (System.Net.IPAddress.TryParse(ipBox.Text.Trim(), out _)) AddRow("", ipBox.Text.Trim(), "Headset", "added by IP address", true); };
            manual.Controls.Add(Ui.Line("Headset with a fixed IP not listed?")); manual.Controls.Add(ipBox); manual.Controls.Add(add);
            p.Controls.Add(manual);
            p.Controls.Add(discStatus);
            // Windows Firewall: rules are per executable path; without an allow rule the headset is not found and the stream may not connect
            var fwLine = new FlowLayoutPanel { AutoSize = true };
            var fwLabel = Ui.Line("Checking the Windows Firewall...");
            var fwFix = new Button { Text = "Allow AirFrameXR through the firewall (asks for administrator)", AutoSize = true, Visible = false };
            fwLine.Controls.Add(fwLabel); fwLine.Controls.Add(fwFix);
            p.Controls.Add(fwLine);
            void fwCheck()
            {
                if (demo) { fwLabel.Text = "Windows Firewall: alvr_host.exe is allowed."; fwLabel.ForeColor = Color.SeaGreen; return; }
                Task.Run(() => Firewall.Check(Paths.Host)).ContinueWith(t => BeginInvoke((Action)(() =>
                {
                    var st = t.Result;
                    fwFix.Visible = st == FirewallState.Blocked || st == FirewallState.Missing;
                    fwLabel.Text = st == FirewallState.Allowed ? "Windows Firewall: alvr_host.exe in this folder is allowed."
                        : st == FirewallState.Blocked ? "Windows Firewall BLOCKS alvr_host.exe in this folder (a dismissed Windows prompt does that): the headset will not be found and the stream will not connect."
                        : st == FirewallState.Missing ? "Windows Firewall has no rule for alvr_host.exe in this folder: discovery will find nothing until it is allowed (the rule belongs to this exact folder)."
                        : "Windows Firewall: state unknown (this Windows language is not recognised).";
                    fwLabel.ForeColor = st == FirewallState.Allowed ? Color.SeaGreen : st == FirewallState.Unknown ? Color.DimGray : Color.Firebrick;
                })));
            }
            fwFix.Click += (s, e) => { Firewall.Allow(Paths.Host); fwCheck(); StartDiscovery(); };
            fwCheck();
            usbHint = QuestUsb.Hint(() =>
            {
                if (!QuestUsb.Ask(this)) return;
                AddRow("", QuestUsb.Address, "Meta Quest (USB)", "address entered for USB (ADBForwarder)", false);
                foreach (ListViewItem it in found.Items) it.Selected = ((string[])it.Tag)[1] == QuestUsb.Address;
            });
            p.Controls.Add(usbHint);
            // the headsets we already know: paired in session.json, saved in airframexr.json (they must be in the list AND selected)
            var hsPaired = Session.Headset(session);
            if (hsPaired != null) AddRow(hsPaired.Item1, hsPaired.Item3, hsPaired.Item2, "paired before", true);
            if (!string.IsNullOrEmpty(settings.HeadsetIp)) AddRow(settings.HeadsetHost, settings.HeadsetIp, string.IsNullOrEmpty(settings.HeadsetName) ? "Apple Vision Pro" : settings.HeadsetName, "saved address", true);
            if (demo && found.Items.Count == 0) AddRow("1234.client.alvr", "192.168.1.50", "Apple Vision Pro", "paired before - reachable", false);
            if (found.Items.Count > 0) found.Items[0].Selected = true;
            if (!demo) { StartDiscovery(); QuestUsb.Arm(usbHint, () => seenOnNetwork); }
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
                Ui.FitColumns(found);
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
                seenOnNetwork = true; usbHint.Visible = false;
                var name = f.Hostname.EndsWith(".client.alvr") ? "Apple Vision Pro" : f.Hostname.EndsWith(".client") ? "Headset (Quest or other ALVR client)" : f.Hostname;
                AddRow(f.Hostname, f.Ip, name, f.Compatible ? $"detected on the network (ALVR protocol {f.Protocol})" : $"INCOMPATIBLE ({f.Protocol}, streamer {f.ServerProtocol})", false);
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
            hsStatus = Ui.Line($"{chosenName} is detected at {chosenIp}; connecting... open the ALVR app on the headset and leave it on its start screen.");
            p.Controls.Add(hsStatus);
            hsBar = new ProgressBar { Style = ProgressBarStyle.Marquee, Width = 500, Height = 14 };
            p.Controls.Add(hsBar);
            var bar = new FlowLayoutPanel { AutoSize = true };
            hsRetry = new Button { Text = "Try again", AutoSize = true, Visible = false };
            hsRetry.Click += (s, e) => BeginHandshake();
            hsSkip = new Button { Text = "Continue without the headset (default values)", AutoSize = true, Visible = false };
            hsSkip.Click += (s, e) => { hs = new Handshake { DisplayName = chosenName, Hostname = chosenHost, Ip = chosenIp }; hsReal = false; ShowHandshakeResult(false); };
            bar.Controls.Add(hsRetry); bar.Controls.Add(hsSkip);
            p.Controls.Add(bar);
            hsDetails = new FlowLayoutPanel { AutoSize = true, Visible = false, WrapContents = false, FlowDirection = FlowDirection.TopDown };
            p.Controls.Add(hsDetails);
            hsGo = new Button { Text = "Yes, this is my headset: continue >", AutoSize = true, Font = Ui.Big, Padding = new Padding(10), Visible = false, Margin = new Padding(0, 12, 0, 0) };
            hsGo.Click += (s, e) =>
            {
                // Chroma-key passthrough is optional and off by default. ALVR has no way to ask a client whether it accepts the key colour the streamer sends (the client never reports its
                // settings back), and the Vision Pro's App Store app has a fixed key colour and range: so every headset but the Vision Pro is offered it, the Vision Pro keeps it off.
                if (!hs.IsVisionPro && !demo && !auto && !settings.ChromaAsked)
                    using (var dlg = new ChromaStepForm())
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                        {
                            chromaChoice = Tuple.Create(dlg.Transparent, dlg.Rgb);
                            settings.ChromaAsked = true; settings.Save();
                            Log.Write("INFO", $"wizard: chroma-key passthrough {(dlg.Transparent ? "on, key #" + dlg.Rgb : "off")}");
                        }
                GoTo(3);
            };
            p.Controls.Add(hsGo);
            if (demo) { hsReal = true; ShowHandshakeResult(true); } else if (hsDone) ShowHandshakeResult(hsReal); else BeginHandshake();
            return p;
        }

        void BeginHandshake()
        {
            hsRetry.Visible = false; hsSkip.Visible = false; hsBar.Visible = true;
            hsStatus.Text = $"{chosenName} is detected at {chosenIp}; connecting... open the ALVR app on the headset and leave it on its start screen.";
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
                    if (hsStatus.ForeColor != Color.Firebrick) { hsStatus.Text = "The headset did not answer. Is the ALVR app open on it, on the same network (and is AirFrameXR allowed through the firewall)?"; hsStatus.ForeColor = Color.DarkOrange; }
                    hsRetry.Visible = true; hsSkip.Visible = true;
                    return;
                }
                hs = Handshake.From(caps);
                session = Session.Load();
                var h = Session.Headset(session);
                hs.Hostname = h != null ? h.Item1 : chosenHost; hs.Ip = chosenIp;
                hs.DisplayName = h != null && h.Item2 != null && h.Item2.Length > 0 ? h.Item2 : chosenName;
                hsReal = true;
                ShowHandshakeResult(true);
                if (auto)
                {
                    // --auto-mode profile (default) | manual | existing: the three options of the next page
                    GoTo(3);
                    if (Arg("--auto-mode") == "manual") modeManual.Checked = true;
                    else if (Arg("--auto-mode") == "existing") modeExisting.Checked = true;
                    ContinueToTests();
                }
            }));
            discovery?.Dispose();
            var err = local.Start(Base() + "--live --seconds 3 --connect-timeout 90");
            if (err != null) { hsStatus.Text = err; hsStatus.ForeColor = Color.Firebrick; hsBar.Visible = false; hsRetry.Visible = true; }
        }

        static string Base() => $"--install-dir \"{Paths.Dir}\" ";

        void ShowHandshakeResult(bool real)
        {
            hsDone = true;
            hsGo.Text = real ? "Yes, this is my headset: continue >" : "Continue with the defaults >";
            hsBar.Visible = false; hsRetry.Visible = false; hsSkip.Visible = false;
            hsStatus.Text = real ? $"Connected to {hs.DisplayName}. This is what it told us:" : "No headset data: the defaults of a Vision Pro are used, and the tests that need the headset are skipped.";
            hsStatus.ForeColor = real ? Color.SeaGreen : Color.DarkOrange;
            hsDetails.Controls.Clear();
            var g = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
            void row(string k, string v) { g.Controls.Add(Ui.Line(k, Ui.Bold)); g.Controls.Add(Ui.Line(v)); }
            row("Headset", $"{hs.DisplayName}   ({hs.Hostname})   vendor: {hs.Vendor}");
            row("Panel resolution per eye", $"{hs.PanelW} x {hs.PanelH}");
            row("Refresh rates", string.Join(", ", hs.Rates.Select(x => x.ToString("0.#", CultureInfo.InvariantCulture))) + " Hz");
            row("Foveated encoding", hs.Foveation ? "supported" : "not advertised");
            row("Video codec", $"HEVC{(hs.EncHigh ? " (high profile)" : "")}{(hs.Enc10 ? ", 10-bit" : ", 8-bit")}{(hs.EncAv1 ? "; the headset also decodes AV1 (AirFrameXR encodes HEVC)" : "")}");
            row("Colour", $"{(hs.PreferFull ? "full range" : "limited range")}, encoding gamma {hs.Gamma:0.00}{(hs.PreferHdr ? ", HDR preferred" : "")}");
            row("Microphone", hs.MicRate > 0 ? $"{hs.MicRate} Hz" : "none advertised");
            Ui.LateScale(g, this);
            hsDetails.Controls.Add(g);
            hsDetails.Visible = true;
            hsGo.Visible = true;
            hsGo.Enabled = true;
        }

        // ------------------------------------------------------------------------------------------------------------- 3 setup choice
        Control PageMode()
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            p.Controls.Add(H("Choose a setup"));
            var real = hsReal;
            matchedProfile = importedProfile ?? (real ? HmdProfiles.Match(hs, HmdProfiles.LoadAll()) : null);
            // the three ways to a configuration; the tests (next page) then check it on this PC
            p.Controls.Add(Ui.Line("How do you want to configure the stream?", Ui.Big));
            var modes = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(820, 0) };
            modeProfile = new RadioButton { Appearance = Appearance.Button, Text = "Community profile", AutoSize = true, Font = Ui.Body, Padding = new Padding(8) };
            modeManual = new RadioButton { Appearance = Appearance.Button, Text = "Manual configuration (pre-filled)", AutoSize = true, Font = Ui.Body, Padding = new Padding(8) };
            modeExisting = new RadioButton { Appearance = Appearance.Button, Text = "I have an existing ALVR configuration", AutoSize = true, Font = Ui.Body, Padding = new Padding(8) };
            // the recommended way is the one choice shown: the matching community profile, else the pre-filled values; the others sit under "Other ways to set up".
            // The buttons live in two containers, so they are made exclusive by hand.
            var rec = matchedProfile != null ? modeProfile : modeManual;
            rec.Text = "Recommended: " + (matchedProfile != null ? "community profile for your headset" : "manual configuration, pre-filled from your headset");
            var radios = new[] { modeProfile, modeManual, modeExisting };
            foreach (var r0 in radios)
            {
                var r = r0;
                r.CheckedChanged += (s, e) =>
                {
                    r.Font = r.Checked ? Ui.Bold : Ui.Body;
                    if (r.Checked) foreach (var o in radios) if (o != r && o.Checked) o.Checked = false;
                    if (radios.Any(o => o.Checked)) RenderMode(real);
                };
            }
            modes.Controls.Add(rec);
            p.Controls.Add(modes);
            var others = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(820, 0) };
            foreach (var r in radios) if (r != rec) others.Controls.Add(r);
            p.Controls.Add(Ui.Expander("Other ways to set up", others));
            modePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
            p.Controls.Add(modePanel);
            hsGo = new Button { Text = "Run the tests >", AutoSize = true, Font = Ui.Big, Padding = new Padding(10), Margin = new Padding(0, 12, 0, 0) };
            hsGo.Click += (s, e) => ContinueToTests();
            p.Controls.Add(hsGo);
            rec.Checked = true;   // renders the option (the buttons' handlers need modePanel and hsGo)
            return p;
        }

        /// The lower part of the handshake page for the chosen option: the profile (matched or imported) with the network it is for, or the pre-filled values.
        void RenderMode(bool real)
        {
            RenderModeInner(real);
            foreach (Control c in modePanel.Controls) Ui.LateScale(c, this);   // rebuilt after the window loaded
        }

        void RenderModeInner(bool real)
        {
            modePanel.Controls.Clear();
            variantBox = null; rateBox = null; scaleBox = null; foveationBox = null;
            if (modeProfile.Checked)
            {
                profile = matchedProfile;
                if (profile != null)
                {
                    modePanel.Controls.Add(Ui.Line($"{profile.DisplayName}{(profile.Author.Length > 0 ? ", by " + profile.Author : "")} ({profile.Source}): a ready configuration for this headset. The tests check it on this PC; nothing is searched.", Ui.Bold));
                    foreach (var n in profile.Notes) modePanel.Controls.Add(Ui.Line("Note: " + n));
                    foreach (var v in profile.Verified) modePanel.Controls.Add(Ui.Line("Verified: " + v));
                    var net = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                    variantBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
                    foreach (var v in profile.Variants) variantBox.Items.Add($"{v.Label}: {v.Mbps:0} Mbps");
                    variantBox.SelectedIndex = Math.Max(0, profile.Variants.FindIndex(x => x.Id == profile.Variant(null).Id));
                    net.Controls.Add(Ui.Line("Your network:", Ui.Bold)); net.Controls.Add(variantBox);
                    modePanel.Controls.Add(net);
                    modePanel.Controls.Add(Ui.Line("The bitrate depends on it: pick what describes your Wi-Fi (you can change the value at the end)."));
                }
                else modePanel.Controls.Add(Ui.Line("No profile matches this headset yet. Import one shared by another user (see docs\\HMD_PROFILES.md, the AirFrameXR Discord), or choose the manual configuration."));
                var import = new Button { Text = profile != null ? "Import another profile..." : "Import a profile...", AutoSize = true };
                import.Click += (s, e) => ImportProfile(real);
                modePanel.Controls.Add(import);
                hsGo.Enabled = profile != null;
            }
            else if (modeExisting.Checked)
            {
                profile = null;
                session = Session.Load();
                const string V = "session_settings.video.";
                var mbps = Json.Num(session, V + "bitrate.mode.ConstantMbps", 0);
                modePanel.Controls.Add(Ui.Line("Your current settings (config\\session.json and airframexr.json) are used exactly as they are: the tests check them on this PC and your link, and nothing is changed or searched."));
                modePanel.Controls.Add(Ui.Line($"Now: {(int)Json.Num(session, V + "preferred_fps", 90)} Hz, {(mbps > 0 ? mbps.ToString("0", CultureInfo.InvariantCulture) + " Mbps" : "adaptive bitrate")}, encoding '{settings.EncodeProfile}'. At the end you can still change anything."));
                var imp = new Button { Text = "Import my working ALVR session.json...", AutoSize = true };
                imp.Click += (s, e) => { if (AlvrImport.Run(this)) RenderMode(real); };
                modePanel.Controls.Add(imp);
                hsGo.Enabled = true;
            }
            else
            {
                profile = null;
                modePanel.Controls.Add(Ui.Line("Values proposed from what the headset reported. Change what you like: the tests measure this PC and your link, and the proposal at the end adjusts what does not hold."));
                var g = new TableLayoutPanel { ColumnCount = 3, AutoSize = true };
                void row(string k, string v, Control edit) { g.Controls.Add(Ui.Line(k, Ui.Bold)); g.Controls.Add(edit); g.Controls.Add(Ui.Line(v)); }
                rateBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
                foreach (var r in hs.Rates) rateBox.Items.Add(r.ToString("0.#", CultureInfo.InvariantCulture));
                // a headset does not always list every rate it can run (a Quest 3 may leave 120 out of what it reports): the usual ones stay selectable, with a warning
                foreach (var r in new double[] { 60, 72, 80, 90, 96, 100, 120, 144 })
                    if (!hs.Rates.Any(x => Math.Abs(x - r) < 0.6)) rateBox.Items.Add(r.ToString("0.#", CultureInfo.InvariantCulture));
                rateBox.SelectedIndex = Math.Max(0, hs.Rates.Count - 1);
                var listed = "the headset supports " + string.Join(", ", hs.Rates.Select(x => x.ToString("0.#", CultureInfo.InvariantCulture))) + " Hz";
                var rateInfo = Ui.Line(listed);
                rateBox.SelectedIndexChanged += (s, e) =>
                {
                    var chosen = double.Parse((string)rateBox.SelectedItem, CultureInfo.InvariantCulture);
                    var isListed = hs.Rates.Any(x => Math.Abs(x - chosen) < 0.6);
                    rateInfo.Text = isListed ? listed : listed + ". " + (string)rateBox.SelectedItem + " Hz is not among them: the headset may refuse it, then pick a listed one";
                    rateInfo.ForeColor = isListed ? SystemColors.ControlText : Color.DarkOrange;
                };
                g.Controls.Add(Ui.Line("Refresh rate", Ui.Bold)); g.Controls.Add(rateBox); g.Controls.Add(rateInfo);
                scaleBox = new NumericUpDown { Minimum = 40, Maximum = 100, Increment = 5, Value = 100, Width = 70 };
                row("Resolution scale (%)", "100% = the panel's own resolution; lower renders and streams less", scaleBox);
                foveationBox = new CheckBox { Text = "use it", Checked = hs.Foveation, Enabled = hs.Foveation, AutoSize = true };
                row("Foveated encoding", hs.Foveation ? "fewer pixels at the edges; full frame needs a stronger link and GPU" : "not advertised by the headset", foveationBox);
                modePanel.Controls.Add(g);
                hsGo.Enabled = true;
            }
        }

        void ContinueToTests()
        {
            autoconfig = !(modeExisting != null && modeExisting.Checked); ApplyOverrides(); GoTo(4);
        }

        /// "Import profile...": pick a profile file (shared by another user), show what ALVR says about this headset next to what the profile expects,
        /// warn about every mismatch (never refuse: a Quest 2 profile usually suits a Quest 3), and install it in config\\profiles.
        void ImportProfile(bool real)
        {
            string path;
            using (var dlg = new OpenFileDialog { Title = "Import a headset profile", Filter = "AirFrameXR profile (*.json)|*.json|All files (*.*)|*.*", CheckFileExists = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            var p = HmdProfiles.Read(path, hs, out var warnings, out var error);
            if (p == null) { MessageBox.Show(this, error, "Import profile", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var fovLine = !p.HasFov ? "" : "A measured field of view is in the profile: Advanced picture and FOV settings offers it with one button (off until you press it)."
                + (p.FovClientScale > 0 ? " Set FoV Scale to " + p.FovClientScale.ToString("0.##", CultureInfo.InvariantCulture) + " in the headset's app as well." : "") + "\n";
            var text = $"Profile: {p.DisplayName}{(p.Author.Length > 0 ? " by " + p.Author : "")}\n{p.EyeW} x {p.EyeH} per eye at {p.Fps:0} fps, {p.Codec.ToUpperInvariant()} {p.BitDepth}-bit, " +
                       $"{(p.Foveated ? "foveated" : "full frame")}, split encode {p.SplitEncode}, {p.Variant(null).Mbps:0} Mbps, {p.SessionOverrides.Count} ALVR setting(s)\n{fovLine}\n" +
                       $"Your headset, as ALVR reports it:\n{HmdProfiles.Describe(hs)}\n\n";
            if (warnings.Count > 0)
            {
                text += "Possible mismatch (the profile may not suit this headset, you can still use it):\n - " + string.Join("\n - ", warnings) + "\n\nImport it anyway?";
                if (MessageBox.Show(this, text, "Import profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }
            else
            {
                text += "This headset matches the profile. Import it?";
                if (MessageBox.Show(this, text, "Import profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            try { var dst = HmdProfiles.Install(path, p); Log.Write("INFO", $"profile '{p.Id}' imported to {dst} ({warnings.Count} warning(s))"); }
            catch (Exception e) { MessageBox.Show(this, "Cannot copy the profile into config\\profiles: " + e.Message, "Import profile", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            importedProfile = p;
            GoTo(3);
        }

        void ApplyOverrides()
        {
            if (profile != null)
            {
                // a profile fixes the stream; the rate is the one of the headset's closest to the profile's
                ctxFps = profile.Fps > 0 && hs.Rates.Count > 0 ? hs.Rates.OrderBy(x => Math.Abs(x - profile.Fps)).First() : (profile.Fps > 0 ? profile.Fps : 90);
                ctxScale = 1.0;
                ctxFoveation = profile.Foveated && hs.Foveation;
            }
            else if (modeExisting != null && modeExisting.Checked)
            {
                ctxFps = Json.Num(session, "session_settings.video.preferred_fps", hs.Rates.Count > 0 ? hs.Rates.Max() : 90);
                ctxScale = 1.0; ctxFoveation = settings.EncodeProfile != "full-split";
            }
            else
            {
                if (rateBox != null && rateBox.SelectedItem != null) ctxFps = double.Parse((string)rateBox.SelectedItem, CultureInfo.InvariantCulture);
                ctxScale = scaleBox != null ? (double)scaleBox.Value / 100.0 : 1.0;
                ctxFoveation = foveationBox == null || foveationBox.Checked;
            }
            variantId = profile != null && variantBox != null && variantBox.SelectedIndex >= 0 ? profile.Variants[variantBox.SelectedIndex].Id : "";
        }

        double ctxFps = 90, ctxScale = 1.0; bool ctxFoveation = true;

        // ------------------------------------------------------------------------------------------------------------- 3 tests
        Control PageTests()
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            p.Controls.Add(H("Testing your setup"));
            p.Controls.Add(Ui.Line("Each test is ticked off when it is done. This takes a few minutes; the headset ones need the ALVR app open on it."));
            testList = new ListView { View = View.Details, FullRowSelect = true, Width = 790, Height = 300, HeaderStyle = ColumnHeaderStyle.None, ShowItemToolTips = true };
            Ui.Col(testList, "Test", 330); Ui.Col(testList, "Result", 440);
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

        void ApplyChromaChoice()
        {
            if (chromaChoice == null) return;
            var s = Session.Load();
            Chroma.Apply(s, settings, chromaChoice.Item1, chromaChoice.Item2);
            try { Json.Save(Paths.Session, s); settings.Save(); session = s; } catch (Exception e) { Log.Write("WARN", "chroma choice not saved: " + e.Message); }
        }

        void StartTests()
        {
            // the candidate configuration (the headset's panel, the chosen rate) goes to disk first: the encoder tests read it
            var panel = new SettingsPanel(settings, session);
            panel.Load();
            ctx = new TestContext { HS = hs, Check = check, Fps = ctxFps, HeadsetAvailable = hs.FromHeadset };
            ctx.R.TestFps = ctxFps;
            ctx.AskHeard = () =>
            {
                bool? heard = null;
                if (auto) return null;   // the unattended harness run has nobody to ask
                try { Invoke((Action)(() => heard = MessageBox.Show(this, "The scene played music (Sakura Breeze) while you were inside it.\n\nDid you hear it in the headset?", "AirFrameXR \u2013 audio test", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)); } catch { }
                return heard;
            };
            ctx.AskCable = () => { try { Invoke((Action)(() => { using (var f = new CableForm()) f.ShowDialog(this); })); } catch { } };
            var existing = modeExisting != null && modeExisting.Checked;
            if (existing)
            {
                // the user's own configuration: nothing is written, the tests read it as it is
                ctx.Existing = true; ctx.ExistingEncodeProfile = settings.EncodeProfile;
                ctx.ConfigMbps = Json.Num(session, "session_settings.video.bitrate.mode.ConstantMbps", 0);
                if (ctx.ConfigMbps <= 0) ctx.ConfigMbps = 150;
            }
            else
            {
                Proposal cand;
                if (profile != null)
                {
                    cand = Proposal.FromProfile(hs, profile, variantId, ctxFps, ctxScale, ctxFoveation);
                    ctx.Profile = profile; ctx.ConfigMbps = cand.BitrateMbps;
                }
                else
                {
                    cand = Proposal.Build(hs, new Results(), ctxFps);
                    cand.EyeW = (int)(hs.PanelW * ctxScale) / 32 * 32; cand.EyeH = (int)(hs.PanelH * ctxScale); cand.Foveated = ctxFoveation;
                }
                panel.Prefill(cand);
                if (!panel.Save(out var err) && err != null) { testStatus.Text = err; testStatus.ForeColor = Color.Firebrick; }
                else HmdProfiles.ApplySessionOverrides(profile);
                ApplyChromaChoice();
            }
            steps = demo ? DemoSteps() : Tests.Build(ctx);
            foreach (var st in steps) testList.Items.Add(new ListViewItem(new[] { Glyph(st.State) + "  " + st.Name, "" }));
            Ui.FitColumns(testList);
            if (demo) { FinishTests(); return; }
            testCts = new CancellationTokenSource();
            ctx.Ct = testCts.Token;
            ctx.OnEncoderDone = () => Invoke((Action)(() =>
            {
                if (ctx.Fixed) return;   // a profile or the existing settings are verified as it is: the later tests run at the same configuration, nothing is lowered silently
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
            Ui.FitColumns(testList);
        }

        void FinishTests()
        {
            if (step != 4) return;
            testCancel.Enabled = false;
            for (int i = 0; i < steps.Count; i++) UpdateRow(steps[i]);
            var failed = steps.Count(x => x.State == TestState.Failed);
            testStatus.Text = failed == 0 ? "Done." : $"Done: {failed} test(s) failed (details in logs\\AirFrameXR.log); the proposal uses defaults where a measurement is missing.";
            testStatus.ForeColor = failed == 0 ? Color.SeaGreen : Color.DarkOrange;
            proposal = profile != null ? Proposal.FromProfile(hs, profile, variantId, ctxFps, ctxScale, ctxFoveation) : Proposal.Build(hs, ctx.R, ctxFps);
            next.Enabled = true;
            if (auto) GoNext();
        }

        // ------------------------------------------------------------------------------------------------------------- 4 results
        Control PageResults()
        {
            var p = Verdict.Build(hs, ctx, profile, proposal, ctxFps, ctxScale, ctxFoveation);
            p.Controls.Add(H("How your setup performs"));
            p.Controls.SetChildIndex(p.Controls[p.Controls.Count - 1], 0);
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
                : "Your current settings (from config\\session.json and airframexr.json). Change anything you like."));
            session = Session.Load();
            settingsPanel = new SettingsPanel(settings, session) { Dock = DockStyle.Fill };
            settingsPanel.Load();
            if (autoconfig && proposal != null) settingsPanel.Prefill(proposal);
            p.Controls.Add(settingsPanel);
            // share what you ended up with: a profile file with everything ALVR declared about the headset, these settings and the verdict's numbers
            p.RowCount = 4;
            p.RowStyles.Clear();
            p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var exp = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(640, 0) };   // wraps inside the page
            var authorBox = new TextBox { Width = 130, Text = "" };
            var netBox = new TextBox { Width = 230, Text = "" };
            var export = new Button { Text = "Export profile...", AutoSize = true };
            export.Click += (s, e) => ExportProfile(authorBox.Text.Trim(), netBox.Text.Trim());
            exp.Controls.Add(export);
            exp.Controls.Add(Ui.Line("your name:")); exp.Controls.Add(authorBox);
            exp.Controls.Add(Ui.Line("your network (e.g. Wi-Fi 6E 6 GHz, same room):")); exp.Controls.Add(netBox);
            p.Controls.Add(exp);
            return p;
        }

        /// Saves the settings, then writes a shareable profile (see docs/HMD_PROFILES.md) next to the user's choice of file name.
        void ExportProfile(string author, string networkLabel)
        {
            string err = null;
            if (settingsPanel == null || !settingsPanel.Save(out err)) { MessageBox.Show(this, err ?? "Cannot save the settings first.", "Export profile"); return; }
            var data = HmdProfiles.BuildExport(hs, ctx?.R, settings, author, networkLabel);
            using (var dlg = new SaveFileDialog { Title = "Export this configuration as a profile", Filter = "AirFrameXR profile (*.json)|*.json", FileName = ((string)data["id"]) + ".profile.json", OverwritePrompt = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { Json.Save(dlg.FileName, data); }
                catch (Exception e) { MessageBox.Show(this, "Cannot write the file: " + e.Message, "Export profile"); return; }
                Log.Write("INFO", $"profile exported to {dlg.FileName}");
                MessageBox.Show(this, $"Saved: {dlg.FileName}\n\nShare this file (for example on the AirFrameXR Discord). Whoever imports it sees what ALVR declared about your headset and is warned if theirs differs.", "Export profile");
            }
        }

        bool SaveConfig()
        {
            if (settingsPanel == null) return false;
            if (!settingsPanel.Save(out var err)) { MessageBox.Show(this, err, Text); return false; }
            if (autoconfig) HmdProfiles.ApplySessionOverrides(profile);
            return true;
        }

        void AutoDrive()
        {
            if (step == 5) { var t = new System.Windows.Forms.Timer { Interval = 600 }; t.Tick += (s, e) => { t.Stop(); GoNext(); }; t.Start(); }
            else if (step == 6)
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
                    ["eye"] = new object[] { proposal.EyeW, proposal.EyeH }, ["fps"] = proposal.Fps, ["bitrate_mbps"] = proposal.BitrateMbps, ["foveated"] = proposal.Foveated,
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
            if (Arg("--demo-headset") == "quest3")
                hs = new Handshake { FromHeadset = true, DisplayName = "Oculus Quest3", Hostname = "quest3.client.alvr_", Ip = "192.168.1.30", PanelW = 2064, PanelH = 2208, Rates = new List<double> { 72, 80, 90, 120 }, MicRate = 48000, EncAv1 = true, Gamma = 2.2 };
            chosenHost = hs.Hostname; chosenIp = hs.Ip; chosenName = hs.DisplayName; ctxFps = hs.Rates.Contains(90) ? 90 : hs.Rates.Max();
            ctx = new TestContext { HS = hs, Check = check, HeadsetAvailable = true };
            ctx.R = new Results
            {
                GpuName = "NVIDIA GeForce RTX 5090", GpuArch = "Blackwell", Driver = "617.42", VramMb = 32768, EncodeP50 = 3.1, EncodeP95 = 3.6, BudgetMs = 11.1, EncodeFits = true, Engines = 3, EncW = 4224, EncH = 1664,
                CleanMbps = 270, LossPerS = 0.02, TotalMs = 52, NetworkMs = 9.5, DecodeMs = 6.2, EncoderMs = 4.1, DecoderQueueMs = 8, VsyncQueueMs = 10, DisplayedFps = 89, ClientFps = 90, TestFps = 90,
            };
            proposal = Proposal.Build(hs, ctx.R, 90);
            autoconfig = true; hsReal = true;
        }

        List<TestStep> DemoSteps()
        {
            var l = new List<TestStep>();
            var states = new[] { (TestState.Passed, "NVIDIA GeForce RTX 5090 (Blackwell), driver 617.42, 32 GB, NVENC API 13.0"), (TestState.Passed, "3552x3200 encoded: p50 3.1 ms, p95 3.6 ms of 11.1 ms per frame; 3 NVENC engine(s)"),
                (TestState.Passed, "recommended: P3 foveated (47.1 dB, encode p95 3.6 ms)"), (TestState.Passed, "clean up to 270 Mbps (the highest clean step minus 10%)"),
                (TestState.Warning, "head pose reaches the scene (90/s; turned 62 deg); streamed 90 fps, 1% repeated head samples; latency 52.0 ms, 89 fps shown"),
                (TestState.Passed, "engaged: 3 strips per frame on 3 NVENC engines"), (TestState.Running, "") };
            var names = new[] { "System check (GPU, driver, hardware encoder)", "Encoder speed at the chosen resolution and refresh rate", "Picture quality benchmark (about 1.5 minutes)", "Network test with the headset",
                "Scene test: Littlest Tokyo through the real pipeline (head tracking, frames, latency)", "Split encoding (one frame cut in strips, one per NVENC engine)", "Audio output" };
            for (int i = 0; i < names.Length; i++) l.Add(new TestStep { Name = names[i], State = states[i].Item1, Note = states[i].Item2 });
            return l;
        }
    }
}
