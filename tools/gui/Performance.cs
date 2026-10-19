// performance.exe: the games that have run (left) and what was stored for the selected one (right): the last session as tiles, the recent
// sessions, the motion-vector DLL (install / uninstall) and an export of the logs. Data: config\games\<exe>.json written by the streamer.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

namespace VisionALVR
{
    public class PerformanceForm : DpiForm
    {
        readonly AppSettings settings = new AppSettings();
        readonly ListBox games = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, Font = Ui.Body };
        readonly FlowLayoutPanel right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 8, 12, 8), BackColor = Color.White };
        readonly List<string> stems = new List<string>();   // parallel to the list box rows
        readonly Dictionary<string, Dictionary<string, object>> data = new Dictionary<string, Dictionary<string, object>>();

        public PerformanceForm(string select)
        {
            Text = "VisionALVR - logs and performance";
            Icon = Ui.AppIcon();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Width = 1180; Height = 780;   // design values at 96 dpi
            MinimumSize = new Size(760, 520);
            Ui.FitToWorkArea(this);

            var autoDel = new CheckBox { Text = "Delete the records of games that are no longer installed", AutoSize = true, Checked = settings.AutoDeleteGameLogs, Margin = new Padding(3, 6, 3, 6) };
            autoDel.CheckedChanged += (s, e) => { settings.AutoDeleteGameLogs = autoDel.Checked; settings.Save(); if (autoDel.Checked) { Scan(); Fill(null); } };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 0, 10, 4), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            var open = new Button { Text = "Open logs folder", AutoSize = true };
            open.Click += (s, e) => { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", Paths.Logs); };
            bottom.Controls.Add(autoDel);
            bottom.Controls.Add(open);

            var left = new Panel { Dock = DockStyle.Left, Width = 300, Padding = new Padding(8), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            left.Controls.Add(games);
            games.BackColor = left.BackColor;
            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(bottom);
            Controls.Add(Ui.CompactHeader("Logs and performance"));
            games.SelectedIndexChanged += (s, e) => Render();
            if (settings.AutoDeleteGameLogs) Scan();
            Fill(select);
            Load += (s, e) => { ActiveControl = games; right.AutoScrollPosition = Point.Empty; };   // focus on the list of games: a control lower in the right pane would scroll it
            Ui.ScreenshotWhenShown(this, 1500);
        }

        static string S(Dictionary<string, object> d, string k, string def = "") => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;
        static double D(Dictionary<string, object> d, string k) { try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN; } catch { return double.NaN; } }
        static Dictionary<string, object> O(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        static string N(double v, string fmt = "0.0") => double.IsNaN(v) ? "-" : v.ToString(fmt, CultureInfo.InvariantCulture);

        // a record whose game has recorded paths and none of them exists any more: the game was uninstalled
        void Scan()
        {
            foreach (var f in Games.Files().ToList())
            {
                var g = Json.Load(f);
                if (g.TryGetValue("paths", out var p) && p is object[] arr && arr.Length > 0 && !arr.Any(o => File.Exists(o as string ?? "")))
                {
                    try { File.Delete(f); Log.Write("INFO", "logs: deleted the record of an uninstalled game: " + Path.GetFileName(f)); } catch { }
                }
            }
        }

        void Fill(string select)
        {
            games.Items.Clear(); stems.Clear(); data.Clear();
            var rows = new List<(string stem, string label, long when)>();
            foreach (var f in Games.Files())
            {
                var g = Json.Load(f);
                var stem = Path.GetFileNameWithoutExtension(f);
                data[stem] = g;
                var name = S(g, "name"); if (name.Length == 0) name = S(g, "exe", stem);
                long when = 0;
                if (g.TryGetValue("sessions", out var so) && so is object[] ss && ss.Length > 0 && ss[ss.Length - 1] is Dictionary<string, object> last) when = (long)(D(last, "updated") is double u && !double.IsNaN(u) ? u : 0);
                rows.Add((stem, name, when));
            }
            foreach (var r in rows.OrderByDescending(r => r.when).ThenBy(r => r.label)) { stems.Add(r.stem); games.Items.Add(r.label); }
            if (stems.Count == 0) { Render(); return; }
            var i = select != null ? stems.IndexOf(Games.Stem(select)) : -1;
            games.SelectedIndex = i >= 0 ? i : 0;
        }

        void Render()
        {
            right.SuspendLayout();
            right.Controls.Clear();
            if (games.SelectedIndex < 0 || games.SelectedIndex >= stems.Count)
            {
                right.Controls.Add(Ui.Line("No game has been recorded yet. Play one with the streamer running (a run of 20 seconds or more is recorded when the game ends).", Ui.Body));
                right.ResumeLayout(); return;
            }
            var stem = stems[games.SelectedIndex];
            var g = data[stem];
            var exe = S(g, "exe", stem);
            right.Controls.Add(new Label { Text = games.SelectedItem.ToString(), AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Margin = new Padding(3, 0, 3, 0) });
            var paths = g.TryGetValue("paths", out var po) && po is object[] pa ? pa.Select(x => x as string).Where(x => !string.IsNullOrEmpty(x)).ToArray() : new string[0];
            right.Controls.Add(new Label { Text = exe + (paths.Length > 0 ? "   " + paths[0] : ""), AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), 0), Margin = new Padding(3, 0, 3, 8) });

            var sessions = g.TryGetValue("sessions", out var so) && so is object[] sa ? sa.OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>>();
            if (sessions.Count == 0)
                right.Controls.Add(Ui.Line("No session stored for this game yet (migrated from an older version: the next run fills this in).", Ui.Body));
            else
            {
                var last = sessions[sessions.Count - 1];
                right.Controls.Add(new Label { Text = $"Last session: {When(last)}, {N(D(last, "seconds") / 60)} min on {S(last, "headset")}, {Mode(S(last, "level"))}", AutoSize = true, Font = Ui.Bold, Margin = new Padding(3, 0, 3, 4) });
                right.Controls.Add(Tiles(last));
                right.Controls.Add(new Label { Text = $"Sessions (the last {sessions.Count}, newest first)", AutoSize = true, Font = Ui.Bold, Margin = new Padding(3, 8, 3, 4) });
                right.Controls.Add(SessionList(sessions));
            }
            right.Controls.Add(DllBox(stem, g));
            var tools = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var export = new Button { Text = "Export log...", AutoSize = true };
            export.Click += (s, e) => Export(stem);
            var del = new Button { Text = "Forget this game", AutoSize = true };
            del.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Delete the stored record of " + games.SelectedItem + "? (The game itself is not touched.)", "VisionALVR", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                try { File.Delete(Games.FileOf(exe)); } catch { }
                Fill(null);
            };
            tools.Controls.Add(export);
            tools.Controls.Add(del);
            right.Controls.Add(tools);
            foreach (Control c in right.Controls) Ui.LateScale(c, this);   // rebuilt after the window loaded; widths above are design units
            right.ResumeLayout();
            right.AutoScrollPosition = Point.Empty;   // a rebuilt pane starts at its top, whatever the last one was scrolled to
        }

        static string Mode(string level) => level == "off" ? "generation off" : level == "third" ? "generation 1/3" : level == "half" ? "generation 1/2" : level == "two_thirds" ? "generation 2/3" : level;
        static string When(Dictionary<string, object> ses)
        {
            var t = D(ses, "start");
            return double.IsNaN(t) ? "?" : DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }

        Control Tiles(Dictionary<string, object> s)
        {
            var net = O(s, "net"); var mv = O(s, "mv");
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), 0), Margin = new Padding(0) };
            var rep = D(s, "headset_repeated_typ") * 100; var hostRep = D(s, "host_repeats_typ") * 100;
            f.Controls.Add(Ui.MakeTile("Game", N(D(s, "rendered_fps")) + " fps", "slowest tenth " + N(D(s, "rendered_fps_low"))));
            f.Controls.Add(Ui.MakeTile("Streamed", N(D(s, "streamed_fps")) + " fps", "display " + N(D(s, "display_hz"), "0") + " Hz"));
            if (net != null) f.Controls.Add(Ui.MakeTile("Headset shows", N(D(net, "displayed_fps")) + " fps", "repeated periods " + N(rep) + " %", rep >= 5 ? Color.Firebrick : (Color?)null));
            f.Controls.Add(Ui.MakeTile("Host repeats", N(hostRep) + " %", "of its outputs"));
            if (net != null)
            {
                f.Controls.Add(Ui.MakeTile("Latency", N(D(net, "latency_ms"), "0") + " ms", $"p95 {N(D(net, "latency_p95_ms"), "0")}; enc {N(D(net, "encode_ms"), "0")} + net {N(D(net, "network_ms"), "0")} + dec {N(D(net, "decode_ms"), "0")}"));
                f.Controls.Add(Ui.MakeTile("Bitrate", N(D(net, "bitrate_mbps"), "0") + " Mbps", "sent " + N(D(net, "throughput_mbps"), "0")));
                var lost = D(net, "packets_lost");
                f.Controls.Add(Ui.MakeTile("Packets lost", N(lost, "0"), "during the session", lost > 0 ? Color.Firebrick : (Color?)null));
            }
            if (mv != null)
            {
                var frames = D(mv, "frames");
                var on = mv.TryGetValue("enabled", out var eo) && eo is bool b && b;
                var gen = S(s, "level") != "off";
                f.Controls.Add(Ui.MakeTile("DLSS vectors", !gen ? "not used" : frames > 0 ? "working" : (on ? "not seen" : "off"), !gen ? "generation was off" : frames > 0 ? N(frames, "0") + " frames" : (on ? "no vectors from the game" : "ticked off")));
            }
            return f;
        }

        Control SessionList(List<Dictionary<string, object>> sessions)
        {
            var lv = new ListView { View = View.Details, FullRowSelect = true, GridLines = false, Height = 150, Width = Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = new Padding(3) };
            string[] cols = { "Date", "Headset", "Gen.", "Min", "Game", "Shown", "Repeat %", "Lat. ms", "Vectors" };
            int[] w = { 125, 125, 55, 45, 55, 60, 75, 65, 70 };
            for (int i = 0; i < cols.Length; i++) Ui.Col(lv, cols[i], w[i]);
            right.Resize += (s, e) => lv.Width = Math.Max(400, right.ClientSize.Width - 40);
            foreach (var s in Enumerable.Reverse(sessions))
            {
                var net = O(s, "net"); var mv = O(s, "mv");
                lv.Items.Add(new ListViewItem(new[] { When(s), S(s, "headset"), Mode(S(s, "level")).Replace("generation ", ""), N(D(s, "seconds") / 60), N(D(s, "rendered_fps")), net != null ? N(D(net, "displayed_fps")) : "-",
                    N(D(s, "headset_repeated_typ") * 100), net != null ? N(D(net, "latency_ms"), "0") : "-",
                    mv != null ? (D(mv, "frames") > 0 ? "working" : "-") : "-" }));
            }
            Ui.FitColumns(lv);
            return lv;
        }

        Control DllBox(string stem, Dictionary<string, object> g)
        {
            var box = new GroupBox { Text = "DLSS motion vectors", AutoSize = true, Font = Ui.Bold, Padding = new Padding(8), Margin = new Padding(3, 8, 3, 0) };
            var flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Font = Ui.Body };
            var dir = MvDll.GameDir(g);
            var st = MvDll.Check(dir, stem, out var file);
            string text = st == MvDll.State.NoMaster ? "This release does not include the DLL (DLSSMotionVectors\\dxgi.dll)."
                : st == MvDll.State.NoFolder ? "The game's folder is not known (or the game is gone): run the game once with the streamer."
                : st == MvDll.State.Installed ? "Installed: " + file + " (identical to this release's copy)."
                : st == MvDll.State.OlderCopy ? "Installed by VisionALVR but different from this release's copy (an older version): reinstall to update."
                : st == MvDll.State.OtherDll ? "A dxgi.dll that is not ours is in the game folder; install will use version.dll."
                : "Not installed in " + dir + ".";
            flow.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(Math.Max(400, Ui.Design(right, right.ClientSize.Width - 80)), 0), Margin = new Padding(0, 2, 0, 4) });
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            bool can = st != MvDll.State.NoMaster && st != MvDll.State.NoFolder;
            var install = new Button { Text = st == MvDll.State.Installed ? "Reinstall" : "Install", AutoSize = true, Enabled = can };
            install.Click += (s, e) => { MessageBox.Show(this, MvDll.Install(dir, stem, out _), "VisionALVR"); Render(); };
            var remove = new Button { Text = "Uninstall", AutoSize = true, Enabled = can && MvDll.Installed(stem) != null };
            remove.Click += (s, e) => { MessageBox.Show(this, MvDll.Uninstall(dir, stem), "VisionALVR"); Render(); };
            row.Controls.Add(install);
            row.Controls.Add(remove);
            flow.Controls.Add(row);
            box.Controls.Add(flow);
            return box;
        }

        // one zip with what a bug report needs: this game's record, the general log, the newest debug run, the DLL install record
        void Export(string stem)
        {
            using (var dlg = new SaveFileDialog { Filter = "Zip|*.zip", FileName = $"VisionALVR-{stem}-{DateTime.Now:yyyyMMdd-HHmm}.zip" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                    using (var z = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create))
                    {
                        Add(z, Games.FileOf(stem), "game/" + stem + ".json");
                        Add(z, Path.Combine(Paths.Logs, "VisionALVR.log"), "VisionALVR.log");
                        Add(z, MvDll.Record, "mv_dll.json");
                        var dbg = Path.Combine(Paths.Logs, "debug");
                        if (Directory.Exists(dbg))
                        {
                            var newest = new DirectoryInfo(dbg).GetDirectories().OrderByDescending(d => d.LastWriteTime).FirstOrDefault();
                            if (newest != null) foreach (var f in newest.GetFiles()) Add(z, f.FullName, "debug/" + newest.Name + "/" + f.Name);
                        }
                        var info = z.CreateEntry("info.txt");
                        using (var w = new StreamWriter(info.Open())) w.WriteLine($"VisionALVR {Version.Text}\n{DateTime.Now:s}\n{Environment.OSVersion}");
                    }
                    Log.Write("INFO", "logs exported to " + dlg.FileName);
                    Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\"");
                }
                catch (Exception ex) { MessageBox.Show(this, "Export failed: " + ex.Message, "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        static void Add(ZipArchive z, string file, string name)
        {
            if (!File.Exists(file)) return;
            // the logs are being written by the streamer: read with sharing
            using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = z.CreateEntry(name).Open()) src.CopyTo(dst);
        }

        [STAThread]
        static void Main()
        {
            Log.Component = "performance";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var a = Environment.GetCommandLineArgs();
            Application.Run(new PerformanceForm(a.Length > 2 && a[1] == "--game" ? a[2] : null));
        }
    }
}
