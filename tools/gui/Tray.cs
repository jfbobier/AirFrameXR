// tray.exe: sits in the notification area, watches for the paired headset on the network and starts the streamer when it shows up.
// Discovery is the engine's own (`alvr_host --discover`, ALVR's mDNS + UDP 9943 code, via Common.cs Discovery): nothing new on the network side.
// While streamer.exe runs the discovery child is stopped (the streamer owns UDP 9943 and does its own discovery); when the streamer closes,
// watching resumes after a short cooldown so a headset that is still advertising does not restart a streamer the user just closed.
// Only the paired headset (config\session.base.json) triggers a start; other ALVR clients on the network are ignored.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AirFrameXR;
using Microsoft.Win32;

static class TrayProgram
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "AirFrameXR tray";
    static readonly int CooldownSeconds = int.TryParse(Environment.GetEnvironmentVariable("AIRFRAMEXR_TRAY_COOLDOWN"), out var c) ? c : 60;   // the harness shortens it

    [STAThread]
    static void Main()
    {
        using (new System.Threading.Mutex(true, "AirFrameXR_Tray", out bool first))
        {
            if (!first) return;
            Application.EnableVisualStyles();
            Application.Run(new TrayContext());
        }
    }

    class TrayContext : ApplicationContext
    {
        readonly NotifyIcon icon = new NotifyIcon { Icon = Ui.AppIcon(), Visible = true };
        readonly Timer timer = new Timer { Interval = 500 };
        readonly ToolStripMenuItem status = new ToolStripMenuItem { Enabled = false };
        readonly ToolStripMenuItem pause = new ToolStripMenuItem("Pause watching") { CheckOnClick = true };
        readonly ToolStripMenuItem autostart = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        Discovery discovery;
        bool streamerWasRunning;
        DateTime resumeAt = DateTime.MinValue;

        /// This folder's streamer.exe only (another copy, e.g. a test folder, does not count). A process whose path cannot be read counts as running: idle is the safe side.
        static bool StreamerRunning()
        {
            foreach (var p in Process.GetProcessesByName("streamer"))
                using (p)
                {
                    try { if (string.Equals(p.MainModule.FileName, Paths.Runtime, StringComparison.OrdinalIgnoreCase)) return true; }
                    catch { return true; }
                }
            return false;
        }

        public TrayContext()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Start streamer now", null, (s, e) => StartStreamer());
            menu.Items.Add("Open launcher", null, (s, e) => OpenLauncher());
            menu.Items.Add(pause);
            autostart.Checked = AutostartOn();
            autostart.CheckedChanged += (s, e) => SetAutostart(autostart.Checked);
            menu.Items.Add(autostart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += (s, e) => OpenLauncher();
            pause.CheckedChanged += (s, e) => Tick();
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Tick();
        }

        void Tick()
        {
            if (StreamerRunning())
            {
                StopWatching();
                streamerWasRunning = true;
                Say("Streamer running (not watching)");
                return;
            }
            if (streamerWasRunning) { streamerWasRunning = false; resumeAt = DateTime.Now.AddSeconds(CooldownSeconds); }
            if (pause.Checked) { StopWatching(); Say("Paused"); return; }
            if (DateTime.Now < resumeAt) { StopWatching(); Say($"Streamer closed, watching again in {(int)(resumeAt - DateTime.Now).TotalSeconds + 1} s"); return; }
            var paired = Session.Headset(Session.Load());
            if (paired == null) { StopWatching(); Say("No headset paired: pair it in Settings"); return; }
            if (discovery == null) StartWatching(paired.Item1, paired.Item3);
        }

        void StartWatching(string pairedHostname, string pairedIp)
        {
            var d = new Discovery();
            d.OnFound += f =>
            {
                // the pairing is by hostname (the key) or, when the key is empty (a headset paired by its address, as the Settings window does for the Vision Pro), by IP
                var mine = (pairedHostname.Length > 0 && string.Equals(f.Hostname, pairedHostname, StringComparison.OrdinalIgnoreCase)) || (pairedIp.Length > 0 && f.Ip == pairedIp);
                if (!f.Compatible || !mine) { Log.Write("INFO", $"tray: headset {f.Hostname} at {f.Ip} seen via {f.Via}, ignored (compatible={f.Compatible}, paired: '{pairedHostname}' / {pairedIp})"); return; }
                Log.Write("INFO", $"tray: headset {f.Hostname} seen at {f.Ip} via {f.Via}: starting the streamer");
                BeginInvoke(StartStreamer);
            };
            d.OnError += m => Log.Write("WARN", "tray: discovery: " + m);
            var err = d.Start();
            if (err != null) { Say("Cannot watch: " + err); d.Dispose(); return; }
            discovery = d;
            Say("Watching for the headset");
        }

        void StopWatching() { discovery?.Dispose(); discovery = null; }

        void BeginInvoke(Action a) { try { if (icon.ContextMenuStrip.IsHandleCreated) icon.ContextMenuStrip.BeginInvoke(a); else a(); } catch { } }

        void StartStreamer()
        {
            if (StreamerRunning()) return;
            StopWatching();   // before the launch: the streamer's host needs UDP 9943
            try { Process.Start(new ProcessStartInfo(Paths.Runtime) { WorkingDirectory = Paths.Bin, UseShellExecute = false }); streamerWasRunning = true; Say("Streamer started"); }
            catch (Exception e) { Say("Cannot start the streamer: " + e.Message); Log.Write("ERROR", "tray: " + e.Message); }
        }

        static void OpenLauncher()
        {
            var exe = Path.Combine(Paths.Bin, "launcher.exe");
            try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Paths.Bin, UseShellExecute = false }); } catch { }
        }

        void Say(string s)
        {
            status.Text = s;
            icon.Text = ("AirFrameXR: " + s).Substring(0, Math.Min(63, ("AirFrameXR: " + s).Length));   // the notification text limit
        }

        static bool AutostartOn() { try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue(RunName) != null; } catch { return false; } }

        static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\""); else k.DeleteValue(RunName, false);
            }
            catch (Exception e) { Log.Write("WARN", "tray: autostart: " + e.Message); }
        }

        protected override void ExitThreadCore() { timer.Stop(); StopWatching(); icon.Visible = false; icon.Dispose(); base.ExitThreadCore(); }
    }
}
