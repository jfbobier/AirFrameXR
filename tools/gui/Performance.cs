// performance.exe: the games that have run (left) and what was stored for the selected one (right): the last session as tiles, the recent
// sessions, the removal of a motion-vector DLL an earlier release installed, and an export of the logs. Data: config\games\<exe>.json written by the streamer.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

namespace AirFrameXR
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
            Text = "AirFrameXR \u2013 Game statistics";
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
                right.Controls.Add(new Label { Text = $"Sessions (the last {sessions.Count}, newest first)", AutoSize = true, Font = Ui.Bold, Margin = new Padding(3, 8, 3, 4) });
                right.Controls.Add(SessionList(sessions));
            }
            if (MvDll.Installed(stem) != null) right.Controls.Add(DllBox(stem));
            var tools = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var export = new Button { Text = "Export log...", AutoSize = true };
            var exported = new Label { AutoSize = true, Visible = false, Margin = new Padding(3, 4, 3, 0), MaximumSize = new Size(Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), 0) };
            var reveal = new LinkLabel { Text = "Open folder", AutoSize = true, Visible = false, Margin = new Padding(3, 4, 3, 0) };
            reveal.LinkClicked += (s, e) => LogExport.Reveal(reveal.Tag as string);
            export.Click += (s, e) => LogExport.Run(this, stem, exported, reveal);
            var del = new Button { Text = "Forget this game", AutoSize = true };
            del.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "Delete the stored record of " + games.SelectedItem + "? (The game itself is not touched.)", "AirFrameXR", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
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
            var net = O(s, "net");
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
            var pipe = O(s, "pipeline");
            if (pipe != null)
            {
                all.Controls.Add(new Label { Text = "Frame pipeline (what happened to each frame, stage by stage)", AutoSize = true, Font = Ui.Bold, Margin = new Padding(3, 6, 3, 2) });
                all.Controls.Add(PipelineRow(pipe, net, maxW, rep));
            }
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

        static int[] I2(Dictionary<string, object> d, string k)
        {
            try { return d != null && d.TryGetValue(k, out var v) && v is object[] a && a.Length >= 2 ? new[] { Convert.ToInt32(a[0], CultureInfo.InvariantCulture), Convert.ToInt32(a[1], CultureInfo.InvariantCulture) } : null; } catch { return null; }
        }
        static string X(int[] v) => v == null ? "-" : v[0] + " \u00D7 " + v[1];

        /// the record's `pipeline` block (written by the streamer at the end of a run): one tile per stage, in the order the frame goes through them
        Control PipelineRow(Dictionary<string, object> p, Dictionary<string, object> net, int maxW, double repeatedPct)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(maxW, 0), Margin = new Padding(0) };
            var q = D(p, "render_quality");
            row.Controls.Add(Ui.MakeTile("Game", X(I2(p, "game_render")), "per eye" + (double.IsNaN(q) ? "" : ", quality " + N(q * 100, "0") + " %")));
            var eye = I2(p, "stream_eye");
            row.Controls.Add(Ui.MakeTile("Frame", eye == null ? "-" : X(new[] { 2 * eye[0], eye[1] }), "stream over the headset's box"));
            var fg = O(p, "frame_gen");
            bool fgOn = fg != null && fg.TryGetValue("on", out var fo) && fo is bool fb && fb;
            row.Controls.Add(Ui.MakeTile("Generation", fgOn ? "on" : "off", fgOn ? "flow " + X(I2(fg, "flow_size")) + ", " + N(D(fg, "flow_ms")) + " ms" : ""));
            bool fov = p.TryGetValue("foveated", out var fv) && fv is bool fvb && fvb;
            var bx = p.TryGetValue("band_x", out var bxo) && bxo is object[] bxa && bxa.Length >= 2 ? bxa : null;
            row.Controls.Add(Ui.MakeTile("Foveation", fov ? X(I2(p, "encoded")) : "full frame", fov && bx != null ? "band x " + N(Math.Abs(Convert.ToDouble(bx[0], CultureInfo.InvariantCulture)), "0") + ".." + N(Convert.ToDouble(bx[1], CultureInfo.InvariantCulture), "0") + "\u00B0" : ""));
            var nv = O(p, "nvenc");
            double seen = D(nv, "slices_seen"), want = D(nv, "expected_strips");
            bool bad = !double.IsNaN(seen) && !double.IsNaN(want) && want > 0 && seen + 0.05 < want;
            var preset = D(nv, "preset");
            row.Controls.Add(Ui.MakeTile("Encoder", N(D(nv, "encode_p50_ms")) + " ms", $"p95 {N(D(nv, "encode_p95_ms"))} ms, {N(seen)} strip(s) seen, {N(want, "0")} expected" + (double.IsNaN(preset) ? "" : ", P" + N(preset, "0")), bad ? Color.Firebrick : (Color?)null));
            row.Controls.Add(Ui.MakeTile("Network", net != null ? N(D(net, "bitrate_mbps"), "0") + " Mbps" : "-", ""));
            row.Controls.Add(Ui.MakeTile("Headset", net != null ? "decode " + N(D(net, "decode_ms"), "0") + " ms" : "-", "repeated " + N(repeatedPct) + " %"));
            if (!bad) return row;
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            col.Controls.Add(row);
            var eng = D(nv, "engines");
            col.Controls.Add(new Label { Text = $"Split encoding did not engage: {N(seen)} strip(s) for {N(want, "0")} expected ({N(eng, "0")} NVENC engines).", AutoSize = true, ForeColor = Color.Firebrick, Font = Ui.Body, Margin = new Padding(3, 0, 3, 4), MaximumSize = new Size(maxW, 0) });
            return col;
        }

        Control SessionList(List<Dictionary<string, object>> sessions)
        {
            var lv = new ListView { View = View.Details, FullRowSelect = true, GridLines = false, Height = 150, Width = Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = new Padding(3) };
            string[] cols = { "Date", "Headset", "Gen.", "Min", "Game fps", "Shown fps", "Repeat %", "Lat. ms", "E+N+D ms" };   // short headers: the nine fit the page without a sideways bar (Gen. = frame generation level, Repeat = repeated periods, E+N+D = encoder + network + decoder)
            int[] w = { 125, 125, 55, 45, 55, 60, 75, 65, 90 };
            for (int i = 0; i < cols.Length; i++) Ui.Col(lv, cols[i], w[i]);
            Ui.FitWidth(lv, 1600);   // the page's width (and again when the window changes), not the width the page had when this list was built
            foreach (var s in Enumerable.Reverse(sessions))
            {
                var net = O(s, "net");
                lv.Items.Add(new ListViewItem(new[] { When(s), S(s, "headset"), Mode(S(s, "level")).Replace("generation ", ""), N(D(s, "seconds") / 60), N(D(s, "rendered_fps")), net != null ? N(D(net, "displayed_fps")) : "-",
                    N(D(s, "headset_repeated_typ") * 100), net != null ? Latency.Text(D(net, "latency_ms"), D(net, "latency_outliers"), D(net, "latency_samples")) : "-",
                    net != null ? (Latency.Streamer(D(net, "encode_ms"), D(net, "network_ms"), D(net, "decode_ms"), D(net, "latency_outliers"), D(net, "latency_samples")) is double sl ? N(sl, "0") : "n/a") : "-" }));
            }
            Ui.FitColumns(lv);
            return lv;
        }

        // only for a game where an earlier release installed the motion-vector DLL: one line and the button that removes AirFrameXR's copy
        Control DllBox(string stem)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(3, 10, 3, 0), MaximumSize = new Size(Math.Max(400, Ui.Design(right, right.ClientSize.Width - 40)), 0) };
            row.Controls.Add(new Label { Text = "DLSS motion-vector DLL:", AutoSize = true, Font = Ui.Bold, Margin = new Padding(0, 6, 4, 0) });
            row.Controls.Add(new Label { Text = MvDll.Installed(stem) + " installed by an earlier release, no longer used", AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
            var b = new Button { Text = "Remove", AutoSize = true, Margin = new Padding(0) };
            b.Click += (s, e) => { MessageBox.Show(this, MvDll.Uninstall(stem), "AirFrameXR"); Render(); };
            row.Controls.Add(b);
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
