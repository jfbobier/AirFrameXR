// blackbox_analyzer.exe: the analysis half of the Black Box (docs/BENCH_UI.md). The Black Box Recorder only records; this window lists the recordings in
// logs\performance\, analyzes one on demand (nothing is analyzed until you click), replays old reports, exports, deletes. It also opens a recording exported from
// another PC (folder or zip). It refuses to analyze while a recording is running (the analysis would compete with the game for the CPU).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;
using AirFrameXR.Perf;

namespace AirFrameXR
{
    public class AnalyzeForm : DpiForm
    {
        readonly Label banner = new Label { AutoSize = true, Font = Ui.Body, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 8), Text = "Reads recordings made by the Black Box Recorder. Nothing here changes your settings." };
        readonly ListBox list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, Font = Ui.Body };
        readonly List<string> dirs = new List<string>();
        readonly FlowLayoutPanel right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 8, 12, 8), BackColor = Color.White };
        readonly Label title = new Label { AutoSize = true, Font = Ui.Big, Margin = new Padding(0, 0, 0, 4) };
        readonly Label meta = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 6) };
        readonly Label notice = new Label { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        readonly Button btnAnalyze = new Button { Text = "Analyze this recording", AutoSize = true, Font = Ui.Bold }, btnReport = new Button { Text = "Open report.md", AutoSize = true },
                        btnFolder = new Button { Text = "Open folder", AutoSize = true }, btnExport = new Button { Text = "Export...", AutoSize = true }, btnDelete = new Button { Text = "Delete...", AutoSize = true };
        readonly Label verdict = new Label { AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Margin = new Padding(0, 6, 0, 6) };
        readonly Label resTitle = new Label { Text = "Resources, in bottleneck order (each chart: every line as a percentage of its limit; hover for the real values)", Font = Ui.Bold, AutoSize = true, Margin = new Padding(0, 6, 0, 2) };
        readonly TableLayoutPanel resTable = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Margin = new Padding(0), Dock = DockStyle.None };   // metric, average, worst, and a filler that takes the rest of the width (the numbers sit next to their names)
        readonly Label findTitle = new Label { Text = "Details", Font = Ui.Bold, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        readonly FlowLayoutPanel findings = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        readonly Label hotTitle = new Label { Text = "Worst 10-second windows", Font = Ui.Bold, AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
        readonly Label hot = new Label { AutoSize = true, Margin = new Padding(12, 0, 0, 0) };
        readonly Label worstTitle = new Label { Text = "Worst late frames", Font = Ui.Bold, AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        readonly ListView worst = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, Height = 170, Width = 100, HideSelection = false };
        readonly FlowLayoutPanel result = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0), Visible = false };
        readonly FlowLayoutPanel detail = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0), Visible = false };
        string current; bool demo;

        public static string RecordingsDir => Path.Combine(Paths.Logs, "performance");

        public AnalyzeForm(string select = null, string openPath = null)
        {
            Text = "AirFrameXR Black Box \u2013 Analyzer";
            Icon = Ui.AppIcon();
            Font = Ui.Body;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Width = 1180; Height = 800; MinimumSize = new Size(820, 560);
            Ui.FitToWorkArea(this);

            var left = new Panel { Dock = DockStyle.Left, Width = 320, Padding = new Padding(8), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            var leftTitle = new Label { Text = "Recordings", Dock = DockStyle.Top, Font = Ui.Bold, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
            var open = new Button { Text = "Open a recording (folder or zip)...", Dock = DockStyle.Bottom, AutoSize = true };
            var clean = new Button { Text = "Delete old recordings...", Dock = DockStyle.Bottom, AutoSize = true };
            left.Controls.Add(list); left.Controls.Add(clean); left.Controls.Add(open); left.Controls.Add(leftTitle);
            list.BackColor = left.BackColor;
            list.SelectedIndexChanged += (s, e) => { if (list.SelectedIndex >= 0 && list.SelectedIndex < dirs.Count) Select(dirs[list.SelectedIndex]); };
            open.Click += (s, e) => OpenOther();
            clean.Click += (s, e) => CleanUp();

            var btns = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            btns.Controls.AddRange(new Control[] { btnAnalyze, btnReport, btnFolder, btnExport, btnDelete });
            worst.Columns.Add("frame"); worst.Columns.Add("sent at (QPC s)"); worst.Columns.Add("gap ms"); worst.Columns.Add("queue ms"); worst.Columns.Add("encode ms"); worst.Columns.Add("hold ms"); worst.Columns.Add("stage");
            result.Controls.Add(verdict); result.Controls.Add(resTitle); result.Controls.Add(resTable); result.Controls.Add(findTitle); result.Controls.Add(findings); result.Controls.Add(hotTitle); result.Controls.Add(hot); result.Controls.Add(worstTitle); result.Controls.Add(worst);
            detail.Controls.Add(title); detail.Controls.Add(meta); detail.Controls.Add(btns); detail.Controls.Add(notice); detail.Controls.Add(result);
            right.Controls.Add(banner); right.Controls.Add(detail);
            Controls.Add(right); Controls.Add(left); Controls.Add(new BlackBoxHeader("Analyzer", false));
            right.SizeChanged += (s, e) => FitWidth(); Load += (s, e) => FitWidth();
            btnAnalyze.Click += (s, e) => Analyze();
            btnReport.Click += (s, e) => { var f = Path.Combine(current ?? "", "report.md"); if (File.Exists(f)) Process.Start("notepad.exe", "\"" + f + "\""); };
            btnFolder.Click += (s, e) => { if (Directory.Exists(current)) Process.Start("explorer.exe", "\"" + current + "\""); };
            btnExport.Click += (s, e) => Export();
            btnDelete.Click += (s, e) => DeleteCurrent();
            Load += (s, e) => { if (demo) return; if (openPath != null) LoadOther(openPath); else { Fill(select); } };
            Ui.ScreenshotWhenShown(this, 1500);
        }

        int Wd() { return Math.Max(Ui.Px(this, 280), right.ClientSize.Width - right.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - Ui.Px(this, 4)); }
        void FitWidth()
        {
            int w = Wd();
            worst.Width = w; verdict.MaximumSize = new Size(w, 0); notice.MaximumSize = new Size(w, 0); hot.MaximumSize = new Size(w - 12, 0); meta.MaximumSize = new Size(w, 0); banner.MaximumSize = new Size(w, 0);
            foreach (Control c in findings.Controls) if (c is Label) c.MaximumSize = new Size(w - ((Label)c).Margin.Left, 0);
            foreach (Control c in resTable.Controls) { if (c is Label && c.Tag as string == "wide") c.MaximumSize = new Size(w - 6, 0); if (c.Tag as string == "chart") c.Width = Math.Max(Ui.Px(this, 280), w - 6); }
            ClampColumns(worst);
        }
        static void ClampColumns(ListView lv)
        {
            Ui.FitColumns(lv);
            int total = lv.Columns.Cast<ColumnHeader>().Sum(c => c.Width), room = lv.ClientSize.Width - 2;
            if (total > room && lv.Columns.Count > 0) { var last = lv.Columns[lv.Columns.Count - 1]; last.Width = Math.Max(60, last.Width - (total - room)); }
        }

        // ---- the recordings ---------------------------------------------------------------------------------------------------
        static long SizeOf(string dir) { try { return Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length); } catch { return 0; } }
        static string Mb(long b) { return (b / 1048576.0).ToString(b >= 10L << 20 ? "F0" : "F1", CultureInfo.InvariantCulture) + " MB"; }
        static bool StreamerRunning() { try { return Process.GetProcessesByName("blackbox_recorder").Length > 0; } catch { return false; } }
        static bool Incomplete(string dir) { try { var s = File.ReadAllText(Path.Combine(dir, "session.json")); return s.Contains("\"complete\": false"); } catch { return false; } }
        static bool RecordingNow() { try { return StreamerRunning() && Directory.Exists(RecordingsDir) && Directory.GetDirectories(RecordingsDir).Any(Incomplete); } catch { return false; } }

        void Fill(string select)
        {
            dirs.Clear(); list.Items.Clear();
            try
            {
                if (Directory.Exists(RecordingsDir))
                    foreach (var d in Directory.GetDirectories(RecordingsDir).OrderByDescending(x => Path.GetFileName(x)))
                    {
                        if (!File.Exists(Path.Combine(d, "session.json"))) continue;
                        dirs.Add(d);
                        string st = ""; try { var a = Json.Load(Path.Combine(d, "analysis.json")); st = Json.Str(a, "status", ""); } catch { }
                        list.Items.Add((st.Length > 0 ? "[" + st + "]" : "[not analyzed]") + "  " + Path.GetFileName(d));
                    }
            }
            catch { }
            if (dirs.Count == 0) { list.Items.Add("(no recording yet)"); detail.Visible = false; return; }
            int i = select != null ? dirs.FindIndex(d => string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(select).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) : 0;
            list.SelectedIndex = i >= 0 ? i : 0;
        }

        void Select(string dir)
        {
            current = dir; detail.Visible = true; result.Visible = false; notice.Text = "";
            var m = Json.Load(Path.Combine(dir, "bench.json"));
            title.Text = Json.Str(m, "scene", Path.GetFileName(dir));
            string game = Json.Str(m, "game", ""), by = Json.Str(m, "stopped_by", "");
            double secs = 0; try { secs = Convert.ToDouble(Json.Get(m, "seconds"), CultureInfo.InvariantCulture); } catch { }
            meta.Text = Path.GetFileName(dir) + "   ·   " + (secs > 0 ? TimeSpan.FromSeconds(secs).ToString(@"h\:mm\:ss") : "length unknown") + (game.Length > 0 ? "   ·   " + game : "") + (by.Length > 0 ? "   ·   stopped by: " + by : "") + "   ·   " + Mb(SizeOf(dir)) + (Json.Bool(m, "full_session", false) ? "   ·   full session" : "");
            bool busy = RecordingNow(), inc = Incomplete(dir);
            btnAnalyze.Enabled = !busy && !inc;
            if (inc) { notice.Text = "This recording is still being written."; notice.ForeColor = Color.DarkOrange; }
            else if (busy) { notice.Text = "A recording is running: analysis waits until it is stopped (it would compete with the game for the CPU)."; notice.ForeColor = Color.DarkOrange; }
            btnReport.Enabled = File.Exists(Path.Combine(dir, "report.md"));
            if (File.Exists(Path.Combine(dir, "report.md")) && !inc) ShowReport(Build(dir, false));   // an old recording that was analyzed: replay its report, no click needed
        }

        static double Warmup(string dir) { try { return Convert.ToDouble(Json.Get(Json.Load(Path.Combine(dir, "bench.json")), "warmup_s"), CultureInfo.InvariantCulture); } catch { return 10; } }

        ReportData Build(string dir, bool save)
        {
            ReportData r;
            try { r = Report.Build(dir, Warmup(dir)); } catch (Exception e) { r = new ReportData { VerdictStatus = "INSUFFICIENT", Verdict = "The report could not be built: " + e.Message }; }
            if (save)
            {
                try
                {
                    File.WriteAllText(Path.Combine(dir, "report.md"), r.Markdown, new System.Text.UTF8Encoding(false));
                    Json.Save(Path.Combine(dir, "analysis.json"), new Dictionary<string, object> { { "status", r.VerdictStatus }, { "verdict", r.Verdict }, { "at", DateTime.Now.ToString("s", CultureInfo.InvariantCulture) } });
                }
                catch { }
            }
            return r;
        }

        void Analyze()
        {
            if (current == null || demo) return;
            if (RecordingNow()) { notice.Text = "A recording is running: analysis waits."; notice.ForeColor = Color.DarkOrange; return; }
            var dir = current; btnAnalyze.Enabled = false; notice.Text = "Analyzing ..."; notice.ForeColor = Color.DimGray; Application.DoEvents();
            var r = Build(dir, true);
            var sel = list.SelectedIndex; Fill(dir);
            ShowReport(r); btnReport.Enabled = true; btnAnalyze.Enabled = true; notice.Text = "";
        }

        static Color StatusColor(string s) { return s == "OK" ? Color.SeaGreen : s == "PROBLEM" ? Color.Firebrick : s == "WATCH" ? Color.DarkOrange : Color.DimGray; }
        static string Nn(double v) { return double.IsNaN(v) ? "n/a" : v.ToString("F1", CultureInfo.InvariantCulture); }

        void ShowReport(ReportData rep)
        {
            result.Visible = true;
            verdict.Text = rep.VerdictStatus + ": " + rep.Verdict; verdict.ForeColor = StatusColor(rep.VerdictStatus);
            FillResources(rep);
            findings.SuspendLayout(); findings.Controls.Clear();
            foreach (var f in rep.Findings.Where(f => f.Id != "T11" && f.Id != "T01").OrderBy(f => f.Status == "PROBLEM" ? 0 : f.Status == "WATCH" ? 1 : f.Status == "INSUFFICIENT" ? 2 : 3))
            {
                findings.Controls.Add(new Label { Text = f.Status + "   " + f.Title, AutoSize = true, Font = Ui.Bold, ForeColor = StatusColor(f.Status), Margin = new Padding(0, 6, 0, 0), MaximumSize = new Size(Wd(), 0) });
                if (!string.IsNullOrEmpty(f.Detail)) findings.Controls.Add(new Label { Text = f.Detail, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 0, 0, 0), MaximumSize = new Size(Wd() - 12, 0) });
                if (!string.IsNullOrEmpty(f.Test) && f.Status != "OK") findings.Controls.Add(new Label { Text = "Try: " + f.Test, AutoSize = true, Margin = new Padding(12, 0, 0, 0), MaximumSize = new Size(Wd() - 12, 0) });
            }
            findings.ResumeLayout();
            hotTitle.Visible = hot.Visible = rep.Hotspots.Count > 0; hot.Text = string.Join("\n", rep.Hotspots);
            worstTitle.Visible = worst.Visible = rep.Worst.Count > 0;
            worst.Items.Clear();
            foreach (var w in rep.Worst) worst.Items.Add(new ListViewItem(new[] { w.Frame.ToString(CultureInfo.InvariantCulture), w.T.ToString("F3", CultureInfo.InvariantCulture), w.GapMs.ToString("F1", CultureInfo.InvariantCulture), Nn(w.QueueMs), Nn(w.EncodeMs), Nn(w.HoldMs), w.Class }));
            FitWidth();
        }

        // the resource table: a header row per category (status colour + the one-line reading), then a row per metric with its average and its worst value, and its note, if any, as a dim line under it
        void FillResources(ReportData rep)
        {
            resTable.SuspendLayout(); resTable.Controls.Clear(); resTable.RowStyles.Clear(); resTable.RowCount = 0;
            resTitle.Visible = resTable.Visible = rep.Categories.Count > 0;
            if (rep.Categories.Count == 0) { resTable.ResumeLayout(); return; }
            resTable.ColumnStyles.Clear();
            resTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); resTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); resTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); resTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            Action<Control, int, int, int> put = (c, col, r, span) => { resTable.Controls.Add(c, col, r); if (span > 1) resTable.SetColumnSpan(c, span); };
            foreach (var cat in rep.Categories)
            {
                var head = new Label { Text = cat.Name + "   " + cat.Status + (cat.Summary.Length > 0 ? "   " + cat.Summary : ""), AutoSize = true, Font = Ui.Bold, ForeColor = StatusColor(cat.Status), Margin = new Padding(0, 10, 0, 2), Tag = "wide", MaximumSize = new Size(Wd() - 6, 0) };
                put(head, 0, row++, 4);
                if (cat.Rows.Count > 0)
                {
                    put(new Label { Text = "metric", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 0, 24, 2) }, 0, row, 1);
                    put(new Label { Text = "average", AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 36, 2) }, 1, row, 1);
                    put(new Label { Text = "worst", AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 0, 2) }, 2, row++, 1);
                }
                foreach (var r in cat.Rows)
                {
                    put(new Label { Text = r.Name, AutoSize = true, Margin = new Padding(12, 1, 24, 0) }, 0, row, 1);
                    put(new Label { Text = r.Average, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 1, 36, 0) }, 1, row, 1);
                    put(new Label { Text = r.Worst, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 1, 0, 0) }, 2, row++, 1);
                    if (!string.IsNullOrEmpty(r.Note)) put(new Label { Text = r.Note, AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(24, 0, 0, 0), Tag = "wide", MaximumSize = new Size(Wd() - 30, 0) }, 0, row++, 4);
                }
                var grp = rep.Charts.FirstOrDefault(x => x.Category == cat.Name);
                if (grp != null)
                {
                    var chart = new TimelineChart(cat.Name, grp.Series, rep.OriginSeconds, rep.Charts.IndexOf(grp) == 0 ? rep.LateBudgetPercent : null) { Tag = "chart", Font = Ui.Body };
                    chart.Height = Ui.Px(this, rep.Charts.IndexOf(grp) == 0 ? 120 : 104); chart.Width = Math.Max(Ui.Px(this, 280), Wd() - 6);
                    put(chart, 0, row++, 4);
                }
            }
            resTable.ResumeLayout();
        }

        // ---- export, delete, other recordings ---------------------------------------------------------------------------------
        void Export()
        {
            if (current == null || !Directory.Exists(current)) return;
            using (var sfd = new SaveFileDialog { Filter = "Zip|*.zip", FileName = Path.GetFileName(current) + ".zip" })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (File.Exists(sfd.FileName)) File.Delete(sfd.FileName);
                    using (var z = ZipFile.Open(sfd.FileName, ZipArchiveMode.Create))
                        foreach (var f in Directory.GetFiles(current).Where(f => !f.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) || new FileInfo(f).Length < 200L * 1024 * 1024))
                            z.CreateEntryFromFile(f, Path.GetFileName(f));
                    notice.Text = "Saved to " + sfd.FileName; notice.ForeColor = Color.SeaGreen;
                }
                catch (Exception e) { notice.Text = "Export failed: " + e.Message; notice.ForeColor = Color.Firebrick; }
            }
        }

        void DeleteCurrent()
        {
            if (current == null || demo || !Directory.Exists(current)) return;
            if (Incomplete(current) && StreamerRunning()) { notice.Text = "This recording is still being written."; notice.ForeColor = Color.DarkOrange; return; }
            if (MessageBox.Show(this, "Delete the recording " + Path.GetFileName(current) + " (" + Mb(SizeOf(current)) + ")?\nThis cannot be undone.", "AirFrameXR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { Directory.Delete(current, true); current = null; Fill(null); }
            catch (Exception e) { notice.Text = "Could not delete: " + e.Message; notice.ForeColor = Color.Firebrick; }
        }

        void CleanUp()
        {
            if (demo) return;
            using (var f = new DpiForm { Text = "Delete old recordings", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Font = Ui.Body })
            {
                var days = new NumericUpDown { Minimum = 0, Maximum = 3650, Value = 30, Width = 70 };
                var info = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 8, 3, 8) };
                Action upd = () => { var old = Old((int)days.Value); info.Text = old.Count + " recording(s), " + Mb(old.Sum(d => SizeOf(d))) + (old.Count > 0 ? ":\n" + string.Join("\n", old.Take(6).Select(Path.GetFileName)) + (old.Count > 6 ? "\n..." : "") : ""); };
                days.ValueChanged += (s, e) => upd(); upd();
                var ok = new Button { Text = "Delete them", DialogResult = DialogResult.OK, AutoSize = true }; var no = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var row = new FlowLayoutPanel { AutoSize = true }; row.Controls.Add(new Label { Text = "Older than", AutoSize = true, Margin = new Padding(3, 6, 3, 0) }); row.Controls.Add(days); row.Controls.Add(new Label { Text = "days", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
                var btn = new FlowLayoutPanel { AutoSize = true }; btn.Controls.Add(ok); btn.Controls.Add(no);
                var col = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) }; col.Controls.Add(row); col.Controls.Add(info); col.Controls.Add(btn);
                f.Controls.Add(col); f.AcceptButton = ok; f.CancelButton = no;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                foreach (var d in Old((int)days.Value)) { try { Directory.Delete(d, true); } catch { } }
                Fill(null);
            }
        }
        static List<string> Old(int days)
        {
            var cut = DateTime.Now.AddDays(-days);
            try { return Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Where(d => Directory.GetCreationTime(d) < cut && !Incomplete(d)).ToList() : new List<string>(); } catch { return new List<string>(); }
        }

        void OpenOther()
        {
            using (var f = new OpenFileDialog { Title = "Open an exported recording (zip), or cancel and pick a folder", Filter = "Recording (zip)|*.zip|Any file|*.*", CheckFileExists = true })
            {
                if (f.ShowDialog(this) == DialogResult.OK) { LoadOther(f.FileName); return; }
            }
            using (var d = new FolderBrowserDialog { Description = "A recording folder (it contains session.json and host_frames.csv)" })
                if (d.ShowDialog(this) == DialogResult.OK) LoadOther(d.SelectedPath);
        }

        // a recording from another PC: a folder is read where it is; a zip is unpacked flat into a temp folder (names with a path are refused)
        void LoadOther(string path)
        {
            try
            {
                string dir = path;
                if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    dir = Path.Combine(Path.GetTempPath(), "AirFrameXR-analyze", Path.GetFileNameWithoutExtension(path) + "_" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(dir);
                    using (var z = ZipFile.OpenRead(path))
                        foreach (var e in z.Entries)
                        {
                            if (e.Name.Length == 0 || e.FullName != e.Name) continue;   // flat files only
                            e.ExtractToFile(Path.Combine(dir, e.Name), true);
                        }
                }
                if (!Directory.Exists(dir) || !File.Exists(Path.Combine(dir, "session.json"))) { notice.Text = "That is not a recording (no session.json)."; notice.ForeColor = Color.Firebrick; detail.Visible = true; return; }
                Fill(null); Select(dir); list.ClearSelected();
            }
            catch (Exception e) { detail.Visible = true; notice.Text = "Could not open it: " + e.Message; notice.ForeColor = Color.Firebrick; }
        }

        // ---- the UI matrix: fixed content ------------------------------------------------------------------------------------------
        public void ApplyDemoState(string st)
        {
            demo = true; dirs.Clear(); list.Items.Clear();
            if (st == "empty") { list.Items.Add("(no recording yet)"); detail.Visible = false; return; }
            list.Items.Add("[PROBLEM]  20261030-210512_Lies of P corridor"); list.Items.Add("[OK]  20261030-203340_Lies of P corridor"); list.Items.Add("[not analyzed]  20261029-224103_Cyberpunk market");
            detail.Visible = true;
            title.Text = st == "long" ? "A very long scene name that somebody typed to describe the corridor with the fog and the three enemies behind the second door" : "Lies of P corridor";
            meta.Text = "20261030-210512_Lies of P corridor   ·   1:32:08   ·   LiesofP-Win64-Shipping.exe   ·   stopped by: game closed   ·   412 MB   ·   full session";
            if (st == "unanalyzed") { result.Visible = false; notice.Text = ""; return; }
            if (st == "busy") { result.Visible = false; btnAnalyze.Enabled = false; notice.Text = "A recording is running: analysis waits until it is stopped (it would compete with the game for the CPU)."; notice.ForeColor = Color.DarkOrange; return; }
            var rep = new ReportData();
            if (st == "smooth") { DemoCategories(rep, false); rep.VerdictStatus = "OK"; rep.Verdict = "Smooth: 0.31 % late frames, encode p99 4.2 ms"; rep.Findings.Add(new Finding { Id = "T02", Status = "OK", Title = "Deadlines met", Detail = "gap p50 11.09, p95 11.25, p99 11.40, max 24.10 ms; late frames 21 of 6900 (0.31 %), longest run 1." }); }
            else if (st == "insufficient") { rep.VerdictStatus = "INSUFFICIENT"; rep.Verdict = "Not enough data: host_frames.csv is missing: the streamer of this session was not the telemetry build, or debug logging was off."; rep.Findings.Add(new Finding { Id = "T01", Status = "INSUFFICIENT", Title = "No stream record in this session", Detail = "host_frames.csv is missing.", Test = "Record again with the Black Box Recorder and a game running for at least 30 s." }); }
            else
            {
                rep.VerdictStatus = "PROBLEM"; rep.Verdict = "Late frames 2.98 %. GPU: the encoder uses 105 % of the frame period at the 99th percentile (median 77 %); the GPU itself was at 54 % on average; frame generation was on";
                rep.Findings.Add(new Finding { Id = "T02", Status = "PROBLEM", Title = "Late frames: 4.20 %", Detail = "gap p50 11.09, p95 22.20, p99 33.30, max 61.0 ms; late frames 290 of 6900 (4.20 %), longest run 3. Threshold: 1.00 %.", Test = "Compare the stage that made the late frames, then change one thing and record again." });
                rep.Findings.Add(new Finding { Id = "T03", Status = "PROBLEM", Title = "Late gaps: mostly upstream (the game or runtime delivered late)", Detail = "Upstream: 231; Waiting for the encoder (queue): 40; Encode took long: 19.", Test = "The GPU was at 98 % (p95) and the stream waited for the game: the game is GPU-bound. Lower its settings (DLSS quality, ray tracing, resolution), then record again." });
                rep.Findings.Add(new Finding { Id = "T07", Status = "WATCH", Title = "A GPU limiter was active 38.00 % of the time", Detail = "GPU util p50 97 / p95 100 %; core clock 1410-1905 MHz; power max 231 W. Limiter: software power cap.", Test = "Record again with the limiter removed to see whether the late frames follow it." });
                rep.Findings.Add(new Finding { Id = "T04", Status = "OK", Title = "Encode fits the period", Detail = "encode ms p50 3.9, p95 7.4, p99 12.1." });
                DemoCategories(rep, true);
                rep.Hotspots.Add("0:41:30 in: 14.20 % late (128 of 900 frames)"); rep.Hotspots.Add("1:07:10 in: 9.80 % late (88 of 900 frames)");
                for (int i = 0; i < 6; i++) rep.Worst.Add(new WorstFrame { Frame = 2103 + i * 311, T = 11852.123 + i * 3.7, GapMs = 33.3 - i, QueueMs = 1.2 + i, EncodeMs = 14.2 - i, HoldMs = 0.5, Class = "Upstream (the game or runtime delivered late)" });
            }
            ShowReport(rep);
        }

        static void DemoCategories(ReportData rep, bool bad)
        {
            Func<string, string, string, string, ResourceRow> R = (n, a, w, note) => new ResourceRow { Name = n, Average = a, Worst = w, Note = note };
            var g = new ResourceCategory { Name = "GPU", Status = bad ? "PROBLEM" : "OK", Summary = bad ? "the encoder uses 105 % of the frame period at the 99th percentile (median 77 %); the GPU itself was at 54 % on average; frame generation was on" : "GPU 54 % on average, encode window 41 % at the median" };
            g.Rows.Add(R("GPU utilisation", "54 %", bad ? "59 %" : "62 %", null)); g.Rows.Add(R("Game: 3D engine", "41 %", "45 %", "ACBlackFlag")); g.Rows.Add(R("NVENC engines (summed)", "188 %", "190 %", "3 engine instances: can pass 100 %"));
            g.Rows.Add(R("Encode time per frame", bad ? "8.4 ms" : "4.1 ms", bad ? "10.5 ms" : "5.0 ms", null)); g.Rows.Add(R("Core clock", "2812 MHz", "2797 MHz", null)); g.Rows.Add(R("Clock limiter active", "0.0 % of samples", "-", "other clock-event bits 0x400 set: not a documented limiter, not counted"));
            var c = new ResourceCategory { Name = "CPU", Status = "OK", Summary = "CPU 18 % on average, 32 % at p95" };
            c.Rows.Add(R("CPU total", "18 %", "36 %", "16 logical CPUs")); c.Rows.Add(R("Game process", "3.1 cores", "4.0 cores", "ACBlackFlag (1.0 = one whole core)"));
            var m = new ResourceCategory { Name = "Memory and I/O", Status = "OK", Summary = "48 GiB free at the lowest, disk 3 % busy at p95" };
            m.Rows.Add(R("RAM available", "49.6 GiB", "48.6 GiB", null)); m.Rows.Add(R("Disk read latency", "0.4 ms", "1.9 ms", null));
            var n = new ResourceCategory { Name = "Network", Status = "OK", Summary = "network 8.0 ms on average, 13 ms at p99, 0 packets lost" };
            n.Rows.Add(R("Network latency (headset-reported)", "8.0 ms", "13.1 ms", null)); n.Rows.Add(R("Stream bitrate (headset's statistics)", "216 Mbps", "262 Mbps", null));
            var v = new ResourceCategory { Name = "VR rendering", Status = bad ? "PROBLEM" : "OK", Summary = bad ? "late frames 2.98 %: the symptom (the categories above say where it comes from)" : "late frames 0.31 %, game 44.7 fps, headset 99.3 fps" };
            v.Rows.Add(R("Game fps", "44.7", "45.0", "average over the run; worst = the slowest tenth")); v.Rows.Add(R("Late frames", bad ? "2.98 %" : "0.31 %", bad ? "worst 10 s: 3.81 %" : "-", null));
            rep.Categories.Add(g); rep.Categories.Add(c); rep.Categories.Add(m); rep.Categories.Add(n); rep.Categories.Add(v);
            // demo series: 15 minutes, one value a second
            int N = 900; var rnd = new Random(5); rep.OriginSeconds = 10;
            Func<double, double, double, double, double[]> wave = (mid, amp, per, noise) => Enumerable.Range(0, N).Select(i => mid + amp * Math.Sin(i / per) + (rnd.NextDouble() - 0.5) * noise).ToArray();
            Func<string, string, double[], double, string, string, TimeSeries> ts = (nm, un, vals, lim, lt, fm) => new TimeSeries { Name = nm, Unit = un, V = vals, Limit = lim, LimitText = lt, Fmt = fm };
            rep.LateBudgetPercent = Enumerable.Range(0, N).Select(i => (i > 300 && i < 360) || (i > 640 && i < 700) ? 150 + rnd.NextDouble() * 250 : (rnd.NextDouble() < 0.05 ? 100 : 0.0)).ToArray();
            var gg = new SeriesGroup { Category = "GPU" }; gg.Series.Add(ts("GPU utilisation", "%", wave(54, 6, 40, 4), 100, "100 %", "F0")); gg.Series.Add(ts("Encode time per frame", "ms", wave(bad ? 8.4 : 4.1, 1.2, 25, 0.8), 10, "10.0 ms = the frame period", "F1")); gg.Series.Add(ts("Game: 3D engine", "%", wave(41, 5, 55, 4), 100, "100 %", "F0"));
            var cc = new SeriesGroup { Category = "CPU" }; cc.Series.Add(ts("CPU total", "%", wave(18, 7, 30, 6), 100, "100 %", "F0")); cc.Series.Add(ts("Game process", "cores", wave(3.1, 0.6, 35, 0.3), 16, "16 cores = all of them", "F1"));
            var mc = new SeriesGroup { Category = "Memory and I/O" }; mc.Series.Add(ts("RAM used", "GiB", wave(14.5, 1.0, 120, 0.3), 64, "64 GiB = all of it", "F1")); mc.Series.Add(ts("Disk busy", "%", wave(3, 2, 45, 3), 100, "100 %", "F0"));
            var nc = new SeriesGroup { Category = "Network" }; nc.Series.Add(ts("Network latency", "ms", wave(8, 2, 22, 2), 10, "10.0 ms = the frame period", "F1")); nc.Series.Add(ts("Stream bitrate", "Mbps", wave(215, 20, 50, 15), 432, "432 Mbps = the recording's peak", "F0"));
            var vc = new SeriesGroup { Category = "VR rendering" }; vc.Series.Add(ts("Late frames", "%", rep.LateBudgetPercent.Select(x => x * 0.01).ToArray(), 1, "1 % of the frames = the budget", "F1")); vc.Series.Add(ts("Streamed fps", "fps", wave(99.5, 0.5, 20, 1), 100, "100 fps = the display rate", "F0")); vc.Series.Add(ts("Headset shows", "fps", wave(99, 0.8, 30, 1.5), 100, "100 fps = the display rate", "F0"));
            rep.Charts.Add(gg); rep.Charts.Add(cc); rep.Charts.Add(mc); rep.Charts.Add(nc); rep.Charts.Add(vc);
        }

        [STAThread]
        static void Main()
        {
            Log.Component = "blackbox_analyzer";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var a = Environment.GetCommandLineArgs();
            string sel = null, open = null;
            for (int i = 1; i < a.Length - 1; i++) { if (a[i] == "--select") sel = a[i + 1]; if (a[i] == "--open") open = a[i + 1]; }
            Application.Run(new AnalyzeForm(sel, open));
        }
    }
}
