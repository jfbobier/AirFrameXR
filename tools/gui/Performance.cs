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
            Text = "VisionALVR \u2013 Game statistics";
            Icon = Ui.AppIcon();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Ui.Body;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Width = 1180; Height = 780;   // design values at 96 dpi
            MinimumSize = new Size(760, 520);
            Ui.FitToWorkArea(this);

            // troubleshooting and the one preference, folded away: most people never open them
            var debug = new CheckBox { Text = "Record debug logs at every start (costs about 4.6 ms per frame: turn it off when you are done)", AutoSize = true, Checked = settings.Debug, Margin = new Padding(3, 4, 3, 2) };
            debug.CheckedChanged += (s, e) => { settings.Debug = debug.Checked; settings.Save(); Log.Write("INFO", "debug logging " + (debug.Checked ? "on" : "off") + " (from the logs window)"); };
            var autoDel = new CheckBox { Text = "Delete the records of games that are no longer installed", AutoSize = true, Checked = settings.AutoDeleteGameLogs, Margin = new Padding(3, 2, 3, 2) };
            autoDel.CheckedChanged += (s, e) => { settings.AutoDeleteGameLogs = autoDel.Checked; settings.Save(); if (autoDel.Checked) { Scan(); Fill(null); } };
            var open = new Button { Text = "Open the logs folder", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            open.Click += (s, e) => { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", Paths.Logs); };
            var trouble = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            trouble.Controls.Add(debug); trouble.Controls.Add(autoDel); trouble.Controls.Add(open);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 2, 10, 4), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            bottom.Controls.Add(Ui.Expander("Troubleshooting and settings", trouble));

            var left = new Panel { Dock = DockStyle.Left, Width = 300, Padding = new Padding(8), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            left.Controls.Add(games);
            games.BackColor = left.BackColor;
            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(bottom);
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
            foreach (var f in Games.Files().Where(f => !Games.IsHiddenFile(f)).ToList())
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
            foreach (var f in Games.Files().Where(f => !Games.IsHiddenFile(f)))
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
                var lmv = O(last, "mv");
                var vk = VectorsLine.Of(S(last, "level"), lmv != null && lmv.TryGetValue("enabled", out var le) && le is bool lb && lb, lmv == null ? 0 : D(lmv, "frames"), MvDll.Check(MvDll.GameDir(g), stem, out _), false);
                if (vk == VectorsLine.Kind.Blind)   // the same wording and function as the streamer's Frame generation component
                    right.Controls.Add(new Label { Text = VectorsLine.Text(vk, 0), AutoSize = true, ForeColor = VectorsLine.ColorOf(vk), Margin = new Padding(3, 0, 3, 4), MaximumSize = new Size(Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), 0) });
                right.Controls.Add(new Label { Text = $"Sessions (the last {sessions.Count}, newest first)", AutoSize = true, Font = Ui.Bold, Margin = new Padding(3, 8, 3, 4) });
                right.Controls.Add(SessionList(sessions));
            }
            right.Controls.Add(DllBox(stem, g));
            var tools = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var export = new Button { Text = "Export log...", AutoSize = true };
            var exported = new Label { AutoSize = true, Visible = false, Margin = new Padding(3, 4, 3, 0), MaximumSize = new Size(Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), 0) };
            var reveal = new LinkLabel { Text = "Open folder", AutoSize = true, Visible = false, Margin = new Padding(3, 4, 3, 0) };
            reveal.LinkClicked += (s, e) => LogExport.Reveal(reveal.Tag as string);
            export.Click += (s, e) => LogExport.Run(this, stem, exported, reveal);
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
            var result = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            result.Controls.Add(exported); result.Controls.Add(reveal);
            right.Controls.Add(result);
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
            int maxW = Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40));
            var main = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(maxW, 0), Margin = new Padding(0) };
            var more = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(maxW, 0), Margin = new Padding(0) };
            var rep = D(s, "headset_repeated_typ") * 100; var hostRep = D(s, "host_repeats_typ") * 100;
            // the four numbers that answer "how did it go"; the rest is one click away
            main.Controls.Add(Ui.MakeTile("Game", N(D(s, "rendered_fps")) + " fps", "slowest tenth " + N(D(s, "rendered_fps_low"))));
            if (net != null) main.Controls.Add(Ui.MakeTile("Headset shows", N(D(net, "displayed_fps")) + " fps", "repeated periods " + N(rep) + " %", rep >= 5 ? Color.Firebrick : (Color?)null));
            if (net != null) {
                var bad = Latency.Absurd(D(net, "latency_ms"), D(net, "latency_outliers"), D(net, "latency_samples")) || Latency.Absurd(D(net, "latency_p95_ms"));
                var lt = Ui.MakeTile("Latency", bad ? "n/a" : N(D(net, "latency_ms"), "0") + " ms", bad ? "not measured" : $"p95 {N(D(net, "latency_p95_ms"), "0")}; enc {N(D(net, "encode_ms"), "0")} + net {N(D(net, "network_ms"), "0")} + dec {N(D(net, "decode_ms"), "0")}");
                if (bad) lt.Set("n/a", "not measured", null, Latency.Why);
                main.Controls.Add(lt);
            }
            if (mv != null)
            {
                var frames = D(mv, "frames");
                var on = mv.TryGetValue("enabled", out var eo) && eo is bool b && b;
                var gen = S(s, "level") != "off";
                main.Controls.Add(Ui.MakeTile("DLSS vectors", !gen ? "not used" : frames > 0 ? "working" : (on ? "not seen" : "off"), !gen ? "generation was off" : frames > 0 ? N(frames, "0") + " frames" : (on ? "no vectors from the game" : "ticked off")));
            }
            more.Controls.Add(Ui.MakeTile("Streamed", N(D(s, "streamed_fps")) + " fps", "display " + N(D(s, "display_hz"), "0") + " Hz"));
            more.Controls.Add(Ui.MakeTile("Host repeats", N(hostRep) + " %", "of its outputs"));
            if (net != null)
            {
                more.Controls.Add(Ui.MakeTile("Bitrate", N(D(net, "bitrate_mbps"), "0") + " Mbps", "sent " + N(D(net, "throughput_mbps"), "0")));
                var lost = D(net, "packets_lost");
                more.Controls.Add(Ui.MakeTile("Packets lost", N(lost, "0"), "during the session", lost > 0 ? Color.Firebrick : (Color?)null));
            }
            var all = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            all.Controls.Add(main);
            if (net != null)
            {
                // ALVR's own stats line, to tell a slow encoder or decoder from a slow network: streamer latency = encoder + transport + decoder
                double enc = D(net, "encode_ms"), tr = D(net, "network_ms"), dec = D(net, "decode_ms");
                var st = Latency.Streamer(enc, tr, dec, D(net, "latency_outliers"), D(net, "latency_samples"));
                all.Controls.Add(new Label { AutoSize = true, Font = Ui.Body, Margin = new Padding(3, 0, 3, 4),
                    Text = st == null ? "Streamer latency: n/a" : $"Streamer latency {N(st.Value, "0")} ms = encoder {N(enc, "0")} + transport {N(tr, "0")} + decoder {N(dec, "0")}" });
            } all.Controls.Add(Ui.Expander("More numbers", more));
            return all;
        }

        Control SessionList(List<Dictionary<string, object>> sessions)
        {
            var lv = new ListView { View = View.Details, FullRowSelect = true, GridLines = false, Height = 150, Width = Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = new Padding(3) };
            string[] cols = { "Date", "Headset", "Generation", "Minutes", "Game fps", "Shown fps", "Repeated %", "Latency ms", "Enc+net+dec ms", "Vectors" };
            int[] w = { 125, 125, 55, 45, 55, 60, 75, 65, 90, 70 };
            for (int i = 0; i < cols.Length; i++) Ui.Col(lv, cols[i], w[i]);
            right.Resize += (s, e) => lv.Width = Math.Max(400, right.ClientSize.Width - 40);
            foreach (var s in Enumerable.Reverse(sessions))
            {
                var net = O(s, "net"); var mv = O(s, "mv");
                lv.Items.Add(new ListViewItem(new[] { When(s), S(s, "headset"), Mode(S(s, "level")).Replace("generation ", ""), N(D(s, "seconds") / 60), N(D(s, "rendered_fps")), net != null ? N(D(net, "displayed_fps")) : "-",
                    N(D(s, "headset_repeated_typ") * 100), net != null ? Latency.Text(D(net, "latency_ms"), D(net, "latency_outliers"), D(net, "latency_samples")) : "-",
                    net != null ? (Latency.Streamer(D(net, "encode_ms"), D(net, "network_ms"), D(net, "decode_ms"), D(net, "latency_outliers"), D(net, "latency_samples")) is double sl ? N(sl, "0") : "n/a") : "-",
                    mv != null ? (D(mv, "frames") > 0 ? "working" : "-") : "-" }));
            }
            Ui.FitColumns(lv);
            return lv;
        }

        // one line and at most one button: the state of the motion-vector DLL for this game and the action that fits it
        Control DllBox(string stem, Dictionary<string, object> g)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(3, 10, 3, 0), MaximumSize = new Size(Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), 0) };
            var dir = MvDll.GameDir(g);
            var st = MvDll.Check(dir, stem, out var file);
            string text; string action = null; Action act = null;
            switch (st)
            {
                case MvDll.State.NoMaster: text = "not available in this release"; break;
                case MvDll.State.NoFolder: text = "the game's folder is not known yet: run the game once with the streamer"; break;
                case MvDll.State.Installed: text = "installed"; action = "Remove"; act = () => MessageBox.Show(this, MvDll.Uninstall(dir, stem), "VisionALVR"); break;
                case MvDll.State.OlderCopy: text = "installed, but an older copy than this release's"; action = "Update"; act = () => MessageBox.Show(this, MvDll.Install(dir, stem, out _), "VisionALVR"); break;
                case MvDll.State.OtherDll: text = "another dxgi.dll is in the game folder (it is left alone)"; action = "Install as version.dll"; act = () => MessageBox.Show(this, MvDll.Install(dir, stem, out _), "VisionALVR"); break;
                default: text = "not installed"; action = "Install"; act = () => MessageBox.Show(this, MvDll.Install(dir, stem, out _), "VisionALVR"); break;
            }
            row.Controls.Add(new Label { Text = "DLSS motion vectors:", AutoSize = true, Font = Ui.Bold, Margin = new Padding(0, 6, 4, 0) });
            row.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
            if (action != null) { var b = new Button { Text = action, AutoSize = true, Margin = new Padding(0) }; b.Click += (s, e) => { act(); Render(); }; row.Controls.Add(b); }
            return row;
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
