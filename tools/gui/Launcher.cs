// VisionALVR.exe (the launcher): the only file in the package root, the front door. One card per tool (what it does, a Launch button); every tool is its own exe so experts can run only what they
// need. Experts can skip this window and start the exes directly.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VisionALVR
{
    public class LauncherForm : Form
    {
        class Tool { public string Title, What, Exe, Args = ""; public bool StopsStreamer; }
        static readonly Tool[] Tools = {
            new Tool { Title = "Streamer", Exe = @"bin\streamer.exe",
                What = "Starts streaming to your headset and shows the live state: headset, game, frame rate and latency. Brightness, eye height and frame generation are set here, applied live." },
            new Tool { Title = "Logs and performance", Exe = @"bin\performance.exe",
                What = "The games you played: frame rates, latency, network and frame-generation results of each session, the DLSS motion-vector DLL of each game (install / uninstall), and a one-click export of the logs." },
            new Tool { Title = "Diagnose", Exe = @"bin\configure.exe", Args = "--diagnose", StopsStreamer = true,
                What = "Checks your setup as it is, without changing anything: the encoder, the network, the latency and the head tracking, then tells you what is below target and why. Run it when something feels wrong. The streamer must be closed." },
            new Tool { Title = "Setup wizard", Exe = @"bin\configure.exe", StopsStreamer = true,
                What = "Pairs your headset, tests the connection and proposes the settings that suit your PC and headset. Run it once, and again when the setup changes. The streamer must be closed." },
        };

        public LauncherForm()
        {
            Text = "VisionALVR - launcher";
            Icon = Ui.AppIcon();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            BackColor = Color.White;

            var body = new TableLayoutPanel { Dock = DockStyle.None, Location = Point.Empty, AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 4, 12, 12) };
            foreach (var t in Tools) body.Controls.Add(Card(t));
            body.Controls.Add(RuntimeRow());
            var help = new Button { Text = "Help (README)", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            help.Click += (s2, e2) => { var f = Path.Combine(Paths.Docs, "README.txt"); if (File.Exists(f)) Process.Start(f); else MessageBox.Show(this, "docs\\README.txt is missing from this folder.", "VisionALVR"); };
            body.Controls.Add(help);
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            scroller.Controls.Add(body);
            Controls.Add(scroller);
            var hdr = Ui.CompactHeader("Launcher");
            Controls.Add(hdr);
            Ui.AutoFitWindow(this, hdr, body);
            Ui.ScreenshotWhenShown(this, 800);
        }

        // The OpenXR runtime games use: shows the current one and switches to the other (ours <-> the one that was active before), reversed after each
        // click. Going back also puts back the OpenXR layers VisionALVR switched off.
        readonly Label runtimeLabel = new Label { AutoSize = true, Font = Ui.Body, MaximumSize = new Size(520, 0), Margin = new Padding(0, 6, 12, 0) };
        readonly Button runtimeBtn = new Button { AutoSize = true, MinimumSize = new Size(150, 30) };
        readonly Button layersBtn = new Button { Text = "OpenXR layers...", AutoSize = true, MinimumSize = new Size(120, 30), Margin = new Padding(3, 0, 0, 0) };

        Control RuntimeRow()
        {
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), Padding = new Padding(10), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            text.Controls.Add(new Label { Text = "OpenXR runtime", AutoSize = true, Font = Ui.Big, Margin = new Padding(0, 0, 0, 2) });
            text.Controls.Add(runtimeLabel);
            var btns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
            btns.Controls.Add(runtimeBtn); btns.Controls.Add(layersBtn);
            row.Controls.Add(text); row.Controls.Add(btns);
            runtimeBtn.Click += (s, e) => ToggleRuntime();
            layersBtn.Click += (s, e) => { using (var f = new LayersForm()) f.ShowDialog(this); };
            RefreshRuntime();
            return row;
        }

        void RefreshRuntime()
        {
            var cur = OpenXr.ActiveRuntime();
            if (OpenXr.IsOurs())
            {
                var prev = OpenXr.Previous();
                runtimeLabel.Text = "Games use: VisionALVR (this folder).";
                runtimeBtn.Text = prev.Length > 0 ? "Switch back to " + OpenXr.Name(prev) : "Switch back";
                runtimeBtn.Enabled = true;
            }
            else
            {
                runtimeLabel.Text = "Games use: " + OpenXr.Name(cur) + ". VisionALVR is not the active runtime: the streamer will not get the games.";
                runtimeBtn.Text = "Switch to VisionALVR";
                runtimeBtn.Enabled = File.Exists(Paths.Manifest);
            }
        }

        void ToggleRuntime()
        {
            if (OpenXr.IsOurs())
            {
                var back = XrLayers.DisabledByUs();
                OpenXr.Unregister();
                if (back.Count > 0 && MessageBox.Show(this, "Also switch back on the OpenXR layers VisionALVR turned off (" + string.Join(", ", back.Select(l => l.Name)) + ")?", "VisionALVR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    XrLayers.Set(back, true);
            }
            else
            {
                if (!OpenXr.Register(this)) MessageBox.Show(this, "The switch did not take effect (administrator permission declined?).", "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                var risky = XrLayers.ActiveImplicit().Where(l => l.MayCaptureInput).ToList();
                if (risky.Count > 0 && MessageBox.Show(this, "These OpenXR layers load into every game and may capture the controllers before VisionALVR sees them:\n\n" + string.Join("\n", risky.Select(l => "  " + l.Name)) + "\n\nReview them now?", "VisionALVR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    using (var f = new LayersForm()) f.ShowDialog(this);
            }
            RefreshRuntime();
        }

        Control Card(Tool t)
        {
            var exe = Path.Combine(Paths.Dir, t.Exe);
            var card = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), Padding = new Padding(10), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            text.Controls.Add(new Label { Text = t.Title, AutoSize = true, Font = Ui.Big, Margin = new Padding(0, 0, 0, 2) });
            text.Controls.Add(new Label { Text = t.What, AutoSize = true, Font = Ui.Body, ForeColor = Color.DimGray, MaximumSize = new Size(520, 0), Margin = new Padding(0) });
            var go = new Button { Text = "Launch", AutoSize = true, Font = Ui.Bold, MinimumSize = new Size(100, 32), Anchor = AnchorStyles.Right, Margin = new Padding(16, 0, 0, 0), Enabled = File.Exists(exe) };
            go.Click += (s, e) => Launch(t, exe);
            card.Controls.Add(text);
            card.Controls.Add(go);
            return card;
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
                MessageBox.Show(this, t.Title + " needs the network port the streamer uses. Close the streamer window first.", "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
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
            RefreshRuntime();     // the tool may have switched the runtime or the layers
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var args = Environment.GetCommandLineArgs();
            var f = new LauncherForm();
            var i = Array.IndexOf(args, "--test-flow");
            if (i >= 0 && i + 1 < args.Length) f.Shown += (s, e) => f.StartTestFlowPublic(args[i + 1]);
            Application.Run(f);
        }
    }
}
