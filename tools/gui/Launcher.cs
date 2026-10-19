// bin\launcher.exe: the front door, started by the stub VisionALVR.exe (the only file in the package root). One card per tool (what it does, a Launch button); every tool is its own exe so experts can run only what they
// need. Experts can skip this window and start the exes directly.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VisionALVR
{
    /// A whole-card button: a title, one line of text, a chevron. Click, Enter or Space.
    public class Card : Panel
    {
        public Card() { TabStop = true; DoubleBuffered = true; Cursor = Cursors.Hand; }
        public void PerformClick() => OnClick(EventArgs.Empty);
        protected override bool IsInputKey(Keys k) => k == Keys.Enter || k == Keys.Space || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { PerformClick(); e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
        protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(2, 2, Width - 5, Height - 5)); }
    }

    public class LauncherForm : DpiForm
    {
        class Tool { public string Title, What, Exe, Args = ""; public bool StopsStreamer, Primary; }
        static readonly Tool[] Tools = {
            new Tool { Title = "Streamer", Exe = @"bin\streamer.exe", Primary = true, What = "Start streaming to your headset." },
            new Tool { Title = "Game history", Exe = @"bin\performance.exe", What = "How your games ran, and the motion-vector DLL of each." },
            new Tool { Title = "Something feels wrong", Exe = @"bin\configure.exe", Args = "--diagnose", StopsStreamer = true, What = "Check your setup and get suggestions." },
            new Tool { Title = "Set up a headset", Exe = @"bin\configure.exe", StopsStreamer = true, What = "Pair a headset and choose the settings." },
        };
        static readonly Tool Advanced = new Tool { Title = "Advanced (full ALVR settings)", Exe = @"bin\configure.exe", Args = "--advanced", StopsStreamer = true };

        readonly FlowLayoutPanel sys = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 8, 0, 0), Padding = new Padding(10), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9), MinimumSize = new Size(700, 0) };
        FwReport firewall;           // filled in the background (PowerShell takes a few seconds)
        readonly bool demoReady;

        /// demoReady: the screenshot / harness state: everything in place, nothing checked.
        public LauncherForm(bool demoReady = false)
        {
            this.demoReady = demoReady;
            Text = "VisionALVR \u2013 Launcher";
            Icon = Ui.AppIcon();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            BackColor = Color.White;

            var body = new TableLayoutPanel { Dock = DockStyle.None, Location = Point.Empty, AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 4, 12, 12) };
            foreach (var t in Tools) body.Controls.Add(MakeCard(t));
            body.Controls.Add(sys);
            // quiet links: the full ALVR settings, and the help
            var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            links.Controls.Add(Link("Advanced (full ALVR settings)...", () => Launch(Advanced, Path.Combine(Paths.Dir, Advanced.Exe))));
            links.Controls.Add(Link("Help", () => { var f = Path.Combine(Paths.Docs, "README.txt"); if (File.Exists(f)) Process.Start(f); else MessageBox.Show(this, "docs\\README.txt is missing from this folder.", "VisionALVR"); }));
            body.Controls.Add(links);
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            scroller.Controls.Add(body);
            Controls.Add(scroller);
            var hdr = Ui.CompactHeader("Launcher");
            Controls.Add(hdr);
            Ui.AutoFitWindow(this, hdr, body);
            RefreshSystem();
            if (!demoReady) Shown += (s, e) => System.Threading.Tasks.Task.Run(() => Firewall.Analyze(Paths.Host)).ContinueWith(t => { try { BeginInvoke((Action)(() => { firewall = t.Result; RefreshSystem(); })); } catch { } });
            Ui.ScreenshotWhenShown(this, 800);
        }

        static Button Link(string text, Action act)
        {
            var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Ui.Accent, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 14, 0), Padding = new Padding(0), BackColor = Color.Transparent };
            b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xE6, 0xEA, 0xF2);
            b.Click += (s, e) => act();
            return b;
        }

        Control MakeCard(Tool t)
        {
            var exe = Path.Combine(Paths.Dir, t.Exe);
            var card = new Card { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 10, 12, 10), Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9), MinimumSize = new Size(700, 0), AccessibleName = t.Title + ". " + t.What, AccessibleRole = AccessibleRole.PushButton, Enabled = demoReady || File.Exists(exe) };
            var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
            text.Controls.Add(new Label { Text = t.Title, AutoSize = true, Font = t.Primary ? new Font(Ui.Big.FontFamily, 13f, FontStyle.Bold) : Ui.Big, ForeColor = t.Primary ? Ui.Accent : Color.Black, Margin = new Padding(0, 0, 0, 2) });
            text.Controls.Add(new Label { Text = t.What, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(620, 0), Margin = new Padding(0) });
            var chevron = new Label { Text = "\u203A", AutoSize = true, Font = new Font(Ui.Big.FontFamily, 20f), ForeColor = Color.DimGray, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.Controls.Add(text, 0, 0); grid.Controls.Add(chevron, 1, 0);
            card.Controls.Add(grid);
            card.Click += (s, e) => Launch(t, exe);
            // the labels are in the way of the card's click: forward them; a hover colour on the whole card
            Action<Control> wire = null;
            wire = c => { foreach (Control k in c.Controls) { k.Click += (s, e) => card.PerformClick(); k.Cursor = Cursors.Hand; k.MouseEnter += (s, e) => card.BackColor = Color.FromArgb(0xE6, 0xEA, 0xF2); k.MouseLeave += (s, e) => card.BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9); wire(k); } };
            wire(card);
            card.MouseEnter += (s, e) => card.BackColor = Color.FromArgb(0xE6, 0xEA, 0xF2); card.MouseLeave += (s, e) => card.BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9);
            return card;
        }

        // What needs attention, and only that. The OpenXR runtime games use (VisionALVR, or the one that was active before: the same button goes back), OpenXR
        // layers that may take the controllers, and the firewall (read in the background). Everything else lives in Something feels wrong.
        void RefreshSystem()
        {
            sys.SuspendLayout();
            sys.Controls.Clear();
            var lines = new System.Collections.Generic.List<Control>();
            bool ours = demoReady || OpenXr.IsOurs();
            if (ours)
            {
                var prev = demoReady ? "" : OpenXr.Previous();
                lines.Add(Row("\u2713", "Your VR games use VisionALVR.", Color.SeaGreen, prev.Length > 0 ? new[] { ("Switch back to " + OpenXr.Name(prev), (Action)SwitchBack) } : new (string, Action)[0]));
            }
            else
                lines.Add(Row("\u26A0", "Your VR games are set to use " + OpenXr.Name(OpenXr.ActiveRuntime()) + ", not VisionALVR.", Color.Firebrick, File.Exists(Paths.Manifest) ? new[] { ("Use VisionALVR", (Action)UseVisionAlvr) } : new (string, Action)[0]));
            var risky = demoReady ? new System.Collections.Generic.List<XrLayer>() : XrLayers.ActiveImplicit().Where(l => l.MayCaptureInput).ToList();
            if (risky.Count > 0)
                lines.Add(Row("\u26A0", risky.Count == 1 ? "An OpenXR layer may take your controllers before the game sees them." : risky.Count + " OpenXR layers may take your controllers before the game sees them.", Color.Firebrick, new[] { ("Review", (Action)(() => { using (var f = new LayersForm()) f.ShowDialog(this); RefreshSystem(); })) }));
            if (firewall != null && (firewall.State == FirewallState.Missing || firewall.State == FirewallState.Blocked))
                lines.Add(Row("\u26A0", firewall.State == FirewallState.Blocked ? "Windows Firewall blocks the streamer: the headset cannot connect." : "Windows Firewall has no rule for the streamer: the headset will not be found.", Color.Firebrick, new[] { ("Fix", (Action)(() => { using (var f = new FirewallForm()) f.ShowDialog(this); firewall = null; Shown_Recheck(); })) }));
            bool allGood = lines.Count == 1 && ours;
            sys.Controls.Add(new Label { Text = allGood ? "Ready" : "Needs attention", AutoSize = true, Font = Ui.Big, ForeColor = allGood ? Color.SeaGreen : Color.Firebrick, Margin = new Padding(0, 0, 0, 4) });
            foreach (var l in lines) sys.Controls.Add(l);
            sys.ResumeLayout();
        }

        void Shown_Recheck() { System.Threading.Tasks.Task.Run(() => Firewall.Analyze(Paths.Host)).ContinueWith(t => { try { BeginInvoke((Action)(() => { firewall = t.Result; RefreshSystem(); })); } catch { } }); }

        Control Row(string icon, string text, Color c, (string label, Action act)[] buttons)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 2), MaximumSize = new Size(660, 0) };
            row.Controls.Add(new Label { Text = icon, AutoSize = true, ForeColor = c, Font = Ui.Bold, Margin = new Padding(0, 6, 6, 0) });
            row.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 10, 0), MaximumSize = new Size(480, 0) });
            foreach (var b in buttons) { var bt = new Button { Text = b.label, AutoSize = true, Margin = new Padding(0, 0, 6, 0) }; var act = b.act; bt.Click += (s, e) => act(); row.Controls.Add(bt); }
            return row;
        }

        void UseVisionAlvr()
        {
            if (!OpenXr.Register(this)) MessageBox.Show(this, "The switch did not take effect (administrator permission declined?).", "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshSystem();
        }

        void SwitchBack()
        {
            var back = XrLayers.DisabledByUs();
            OpenXr.Unregister();
            if (back.Count > 0 && MessageBox.Show(this, "Also switch back on the OpenXR layers VisionALVR turned off (" + string.Join(", ", back.Select(l => l.Name)) + ")?", "VisionALVR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                XrLayers.Set(back, true);
            RefreshSystem();
        }

        // The launcher steps aside while a tool is open and comes back when the last one is closed (the wizard hands over to the streamer: the launcher
        // waits for every tool, not for the one it started).
        static readonly string[] ToolProcesses = { "streamer", "configure", "performance" };
        readonly Timer watch = new Timer { Interval = 500 };
        Process launched;
        static bool AnyToolOpen() => ToolProcesses.Any(n => Process.GetProcessesByName(n).Length > 0);

        void Launch(Tool t, string exe, string extraArgs = "")
        {
            if (t.StopsStreamer && Process.GetProcessesByName("streamer").Length > 0)
            {
                // the tool needs the streamer's network port: offer to close it rather than send the user away
                if (MessageBox.Show(this, "The streamer is open and uses the same network port. Close it and continue?", "VisionALVR", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                foreach (var p in Process.GetProcessesByName("streamer")) { try { p.CloseMainWindow(); p.WaitForExit(10000); } catch { } }
                if (Process.GetProcessesByName("streamer").Length > 0) { MessageBox.Show(this, "The streamer did not close. Close its window and try again.", "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            }
            try
            {
                launched = Process.Start(exe, (t.Args + " " + extraArgs).Trim());
                Log.Write("INFO", "launcher: started " + t.Exe);
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not start " + Path.GetFileName(t.Exe) + ": " + ex.Message, "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            FlowLog("hide: launched " + t.Title);
            Hide();
            watch.Tick -= WatchTick; watch.Tick += WatchTick; watch.Start();
        }

        void WatchTick(object s, EventArgs e)
        {
            if ((launched != null && !launched.HasExited) || AnyToolOpen()) return;
            watch.Stop();
            RefreshSystem();      // the tool may have switched the runtime or the layers
            Show(); Activate();
            FlowLog("show: every tool closed");
            if (testFlow) { var c = new Timer { Interval = 2500 }; c.Tick += (s2, e2) => { FlowLog("sample"); Close(); }; c.Start(); }   // the test ends once the launcher is back
        }

        // test harness (harness/launcher_flow.py): VISIONALVR_TEST_FLOW_LOG = a file that gets one line per state change and sample
        static readonly string FlowLogPath = Environment.GetEnvironmentVariable("VISIONALVR_TEST_FLOW_LOG");
        void FlowLog(string what)
        {
            if (string.IsNullOrEmpty(FlowLogPath)) return;
            try { File.AppendAllText(FlowLogPath, $"{DateTime.Now:HH:mm:ss.fff} visible={(Visible ? 1 : 0)} tools={ToolProcesses.Sum(n => Process.GetProcessesByName(n).Length)} {what}\r\n"); } catch { }
        }

        /// --test-flow "<tool title>": after the window is up, launches that tool as a click would, with --screenshot so the tool closes itself, and samples the state.
        public void StartTestFlowPublic(string title) => StartTestFlow(title);
        bool testFlow;
        void StartTestFlow(string title)
        {
            testFlow = true;
            var t = Tools.First(x => x.Title == title);
            var sampler = new Timer { Interval = 400 }; sampler.Tick += (s, e) => FlowLog("sample"); sampler.Start();
            var go = new Timer { Interval = 1500 };
            go.Tick += (s, e) =>
            {
                go.Stop();
                FlowLog("before launch");
                var shot = Path.Combine(Path.GetTempPath(), "visionalvr_flow_" + title.Replace(' ', '_') + ".png");
                Launch(t, Path.Combine(Paths.Dir, t.Exe), "--screenshot \"" + shot + "\"");
            };
            go.Start();
            var end = new Timer { Interval = 45000 }; end.Tick += (s, e) => { FlowLog("end"); Close(); }; end.Start();   // however it goes, the test ends
        }

        [STAThread]
        static void Main()
        {
            Log.Component = "launcher";
            bool first;
            var one = new System.Threading.Mutex(true, "VisionALVR_Launcher", out first);
            if (!first) { Log.Write("INFO", "launcher: already running, this start ends"); return; }   // a second double-click: the launcher (or the tool it stepped aside for) is already there
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var args = Environment.GetCommandLineArgs();
            var f = new LauncherForm();
            var i = Array.IndexOf(args, "--test-flow");
            if (i >= 0 && i + 1 < args.Length) f.Shown += (s, e) => f.StartTestFlowPublic(args[i + 1]);
            try { Application.Run(f); }
            catch (Exception e) { Log.Write("ERROR", "launcher failed: " + e); throw; }
        }
    }
}
