// launcher.exe: the front door. One card per tool (what it does, a Launch button); every tool is its own exe so experts can run only what they
// need. Experts can skip this window and start the exes directly.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VisionALVR
{
    public class LauncherForm : Form
    {
        class Tool { public string Title, What, Exe, Args = ""; public bool StopsStreamer; }
        static readonly Tool[] Tools = {
            new Tool { Title = "Streamer", Exe = "VisionALVR.exe",
                What = "Starts streaming to your headset and shows the live state: headset, game, frame rate and latency. Brightness, eye height and frame generation are set here, applied live." },
            new Tool { Title = "Logs and performance", Exe = "performance.exe",
                What = "The games you played: frame rates, latency, network and frame-generation results of each session, the DLSS motion-vector DLL of each game (install / uninstall), and a one-click export of the logs." },
            new Tool { Title = "Diagnose", Exe = "configure.exe", Args = "--diagnose", StopsStreamer = true,
                What = "Checks your setup as it is, without changing anything: the encoder, the network, the latency and the head tracking, then tells you what is below target and why. Run it when something feels wrong. The streamer must be closed." },
            new Tool { Title = "Setup wizard", Exe = "configure.exe", StopsStreamer = true,
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
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 4, 12, 12) };
            foreach (var t in Tools) body.Controls.Add(Card(t));
            Controls.Add(body);
            Controls.Add(Ui.CompactHeader("Launcher"));
            Ui.ScreenshotWhenShown(this, 800);
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

        void Launch(Tool t, string exe)
        {
            if (t.StopsStreamer && Process.GetProcessesByName("VisionALVR").Length > 0)
            {
                MessageBox.Show(this, t.Title + " needs the network port the streamer uses. Close the streamer window first.", "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { Process.Start(exe, t.Args); Log.Write("INFO", "launcher: started " + t.Exe); }
            catch (Exception ex) { MessageBox.Show(this, "Could not start " + t.Exe + ": " + ex.Message, "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        [STAThread]
        static void Main()
        {
            Log.Component = "launcher";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
        }
    }
}
