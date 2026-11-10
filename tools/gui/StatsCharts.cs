// The drawings of the Game statistics window (performance.exe): the frame's way from the game to the headset as a left-to-right strip with the latency split under it,
// and small trend charts over the stored sessions. Custom drawn with GDI+ (no chart library); every size is a 96-dpi design value times DeviceDpi / 96, so they follow the
// display scale (the window scales them itself: they are not touched by Ui.LateScale, see Tag "noscale").
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace AirFrameXR
{
    public class Stage { public string Title, Value, Note; public Color? Alarm; }
    public class LatencyParts { public double Total, Enc, Net, Dec; public bool Bad; }

    /// GAME -> GENERATION -> ENCODE -> NETWORK -> DECODE -> HEADSET: a box per stage (title, the number, a note), arrows between, wrapped onto more rows when the page is narrow;
    /// under it the latency as one bar split into encoder, network, decoder and the rest (the game's own time and its queues).
    public sealed class PipelineStrip : Control
    {
        public List<Stage> Stages = new List<Stage>();
        public LatencyParts Latency;
        static readonly Color BoxBack = Color.FromArgb(0xF3, 0xF5, 0xF9);
        // fonts are made from the control's own (inherited) font, so they follow the window's scale like every other text
        readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        Font Sized(float pt, FontStyle st = FontStyle.Regular) { var key = pt + "/" + st + "/" + Font.Size; if (!fonts.TryGetValue(key, out var f)) fonts[key] = f = new Font("Segoe UI", pt * Font.Size / 9.5f, st); return f; }
        Font TitleFont => Sized(8.5f, FontStyle.Bold);
        Font ValueFont => Sized(14f, FontStyle.Bold);
        Font NoteFont => Sized(8.5f);
        Font TextFont => Sized(9f);
        readonly ToolTip tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
        int tipBox = -1;

        public PipelineStrip() { DoubleBuffered = true; ResizeRedraw = true; Margin = new Padding(3, 0, 3, 4); Tag = "noscale"; }
        float K => Ui.Px(this, 1000) / 1000f;   // the window's scale (the test harness emulates others)
        float MinBox => 104 * K;
        float Gap => 22 * K;
        float BoxH => 104 * K;
        float RowGap => 10 * K;
        int Cols => Math.Max(1, Math.Min(Math.Max(1, Stages.Count), (int)((Width + Gap) / (MinBox + Gap))));
        int Rows => Stages.Count == 0 ? 0 : (Stages.Count + Cols - 1) / Cols;
        float BoxW => (Width - (Cols - 1) * Gap) / Cols;
        RectangleF BoxRect(int i) { int r = i / Cols, c = i % Cols; return new RectangleF(c * (BoxW + Gap), r * (BoxH + RowGap), BoxW, BoxH); }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            int h = (int)Math.Ceiling(Rows * (BoxH + RowGap) + (Latency != null ? 56 * K : 0));
            if (Height != h && h > 0) Height = h;
        }
        public void Rebuild() { int h = (int)Math.Ceiling(Rows * (BoxH + RowGap) + (Latency != null ? 56 * K : 0)); if (h > 0) Height = h; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(BackColor);
            using (var arrow = new Pen(Color.FromArgb(0x9A, 0xA3, 0xB5), 2 * K) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var back = new SolidBrush(BoxBack))
                for (int i = 0; i < Stages.Count; i++)
                {
                    var b = BoxRect(i); var st = Stages[i];
                    g.FillRectangle(back, b);
                    float pad = 8 * K;
                    TextRenderer.DrawText(g, st.Title.ToUpperInvariant(), TitleFont, new Rectangle((int)(b.X + pad), (int)(b.Y + 6 * K), (int)(b.Width - 2 * pad), (int)(16 * K)), Color.DimGray, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, st.Value, ValueFont, new Rectangle((int)(b.X + pad), (int)(b.Y + 22 * K), (int)(b.Width - 2 * pad), (int)(28 * K)), st.Alarm ?? Color.Black, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, st.Note ?? "", NoteFont, new Rectangle((int)(b.X + pad), (int)(b.Y + 52 * K), (int)(b.Width - 2 * pad), (int)(48 * K)), Color.DimGray, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                    if ((i + 1) % Cols != 0 && i + 1 < Stages.Count)   // an arrow to the next box of the row
                    {
                        float x0 = b.Right + 6 * K, x1 = b.Right + Gap - 6 * K, y = b.Y + BoxH / 2;
                        g.DrawLine(arrow, x0, y, x1, y); g.DrawLine(arrow, x1 - 5 * K, y - 5 * K, x1, y); g.DrawLine(arrow, x1 - 5 * K, y + 5 * K, x1, y);
                    }
                }
            if (Latency == null) return;
            float y0 = Rows * (BoxH + RowGap) + 2 * K;
            if (Latency.Bad)
            {
                TextRenderer.DrawText(g, "Latency: n/a (not measured)", TextFont, new Point(0, (int)y0), Color.DimGray, TextFormatFlags.NoPadding);
                return;
            }
            double rest = Math.Max(0, Latency.Total - Latency.Enc - Latency.Net - Latency.Dec);
            TextRenderer.DrawText(g, "Latency " + Latency.Total.ToString("0", CultureInfo.InvariantCulture) + " ms = encode " + Latency.Enc.ToString("0", CultureInfo.InvariantCulture) + " + network " + Latency.Net.ToString("0", CultureInfo.InvariantCulture) + " + decode " + Latency.Dec.ToString("0", CultureInfo.InvariantCulture) + " + game and queues " + rest.ToString("0", CultureInfo.InvariantCulture), TextFont, new Point(0, (int)y0), Color.Black, TextFormatFlags.NoPadding);
            var parts = new[] { ("encode", Latency.Enc, Color.FromArgb(0x1E, 0x6B, 0xF0)), ("network", Latency.Net, Color.FromArgb(0x5B, 0x9B, 0xF5)), ("decode", Latency.Dec, Color.FromArgb(0x9C, 0xC2, 0xF9)), ("game and queues", rest, Color.FromArgb(0xC9, 0xCE, 0xD8)) };
            float bx = 0, by = y0 + 22 * K, bh = 22 * K, total = (float)Math.Max(1e-6, parts.Sum(p => p.Item2));
            foreach (var p in parts)
            {
                float w = (float)(p.Item2 / total) * Width;
                if (w <= 0) continue;
                using (var br = new SolidBrush(p.Item3)) g.FillRectangle(br, bx, by, w, bh);
                var txt = $"{p.Item1} {p.Item2.ToString("0", CultureInfo.InvariantCulture)}";
                var size = TextRenderer.MeasureText(txt, NoteFont);
                if (size.Width + 6 * K < w) TextRenderer.DrawText(g, txt, NoteFont, new Rectangle((int)bx, (int)by, (int)w, (int)bh), p.Item3.GetBrightness() < 0.6f ? Color.White : Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                bx += w;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = -1;
            for (int i = 0; i < Stages.Count; i++) if (BoxRect(i).Contains(e.Location)) hit = i;
            if (hit == tipBox) return;
            tipBox = hit;
            tip.SetToolTip(this, hit < 0 ? "" : Stages[hit].Title + ": " + Stages[hit].Value + (string.IsNullOrEmpty(Stages[hit].Note) ? "" : ". " + Stages[hit].Note));
        }
    }

    public class TrendSeries { public string Name; public Color Color; public double[] V; }

    /// One small chart over the stored sessions of a game, oldest on the left: a line per series, the last value written next to its point, the values of the session under the
    /// pointer in the title row. The axis starts at zero (an fps, a latency and a share are read against zero).
    public sealed class TrendChart : Control
    {
        public string Title, Unit;
        public List<TrendSeries> Series = new List<TrendSeries>();
        public string[] Labels = new string[0];   // per session: "2026-11-10 12:03  x2"
        public Func<string, string> AxisLabel = l => l.Length >= 10 ? l.Substring(5, 5) : l;   // what is written under the first and the last point (the date of a session; a timeline sets the time)
        int hover = -1;
        readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        Font Sized(float pt, FontStyle st = FontStyle.Regular) { var key = pt + "/" + st + "/" + Font.Size; if (!fonts.TryGetValue(key, out var f)) fonts[key] = f = new Font("Segoe UI", pt * Font.Size / 9.5f, st); return f; }
        Font TitleFont => Sized(9f, FontStyle.Bold);
        Font SmallFont => Sized(8.5f);
        public TrendChart() { DoubleBuffered = true; ResizeRedraw = true; Margin = new Padding(0, 0, 8, 0); Tag = "noscale"; }
        float K => Ui.Px(this, 1000) / 1000f;   // the window's scale (the test harness emulates others)
        RectangleF Plot => new RectangleF(40 * K, 48 * K, Math.Max(10, Width - 40 * K - 14 * K), Math.Max(10, Height - 48 * K - 20 * K));
        int N => Series.Count == 0 ? 0 : Series.Max(s => s.V.Length);
        float X(int i) => N <= 1 ? Plot.X + Plot.Width / 2 : Plot.X + i * Plot.Width / (N - 1);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.FromArgb(0xF3, 0xF5, 0xF9));
            TextRenderer.DrawText(g, Title, TitleFont, new Point((int)(10 * K), (int)(6 * K)), Color.Black, TextFormatFlags.NoPadding);
            // the second row: the legend, or the values of the session under the pointer
            if (hover >= 0 && hover < N)
            {
                var vals = string.Join("   ", Series.Where(s => hover < s.V.Length && !double.IsNaN(s.V[hover])).Select(s => s.Name + " " + s.V[hover].ToString("0.#", CultureInfo.InvariantCulture) + Unit));
                TextRenderer.DrawText(g, (hover < Labels.Length ? Labels[hover] + ":  " : "") + vals, SmallFont, new Rectangle((int)(10 * K), (int)(24 * K), Width - (int)(20 * K), (int)(20 * K)), Color.Black, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            else
            {
                float lx = 10 * K;
                foreach (var s in Series)
                {
                    using (var b = new SolidBrush(s.Color)) g.FillRectangle(b, lx, 28 * K, 9 * K, 9 * K);
                    var sz = TextRenderer.MeasureText(s.Name, SmallFont);
                    TextRenderer.DrawText(g, s.Name, SmallFont, new Point((int)(lx + 13 * K), (int)(25 * K)), Color.DimGray, TextFormatFlags.NoPadding);
                    lx += 13 * K + sz.Width + 12 * K;
                }
            }
            var all = Series.SelectMany(s => s.V).Where(v => !double.IsNaN(v)).ToList();
            if (all.Count == 0) { TextRenderer.DrawText(g, "no data", SmallFont, new Point((int)Plot.X, (int)(Plot.Y + 10 * K)), Color.DimGray, TextFormatFlags.NoPadding); return; }
            double top = Nice(all.Max() * 1.1);
            var p = Plot;
            using (var grid = new Pen(Color.FromArgb(0xDD, 0xE1, 0xE8), 1))
                for (int i = 0; i <= 2; i++)
                {
                    float y = p.Bottom - i * p.Height / 2;
                    g.DrawLine(grid, p.X, y, p.Right, y);
                    TextRenderer.DrawText(g, (top * i / 2).ToString("0.#", CultureInfo.InvariantCulture), SmallFont, new Rectangle(0, (int)(y - 8 * K), (int)(p.X - 4 * K), (int)(16 * K)), Color.DimGray, TextFormatFlags.Right | TextFormatFlags.NoPadding);
                }
            if (Labels.Length > 0 && N > 1)   // the first and the last session's date under the plot
            {
                Func<string, string> D = AxisLabel;
                TextRenderer.DrawText(g, D(Labels[0]), SmallFont, new Point((int)p.X, (int)(p.Bottom + 3 * K)), Color.DimGray, TextFormatFlags.NoPadding);
                var last = D(Labels[Math.Min(Labels.Length, N) - 1]);
                TextRenderer.DrawText(g, last, SmallFont, new Point((int)(p.Right - TextRenderer.MeasureText(last, SmallFont).Width + 4 * K), (int)(p.Bottom + 3 * K)), Color.DimGray, TextFormatFlags.NoPadding);
            }
            foreach (var s in Series)
            {
                var pts = new List<PointF>(); int lastIdx = -1;
                for (int i = 0; i < s.V.Length; i++)
                {
                    if (double.IsNaN(s.V[i])) { Flush(g, s.Color, pts); continue; }
                    pts.Add(new PointF(X(i), p.Bottom - (float)(s.V[i] / top) * p.Height)); lastIdx = i;
                }
                Flush(g, s.Color, pts);
                if (lastIdx >= 0)
                {
                    var v = s.V[lastIdx].ToString("0.#", CultureInfo.InvariantCulture);
                    var sz = TextRenderer.MeasureText(v, SmallFont);
                    float lx = Math.Min(X(lastIdx) + 5 * K, Width - sz.Width - 2 * K), ly = p.Bottom - (float)(s.V[lastIdx] / top) * p.Height - sz.Height - 2 * K;
                    TextRenderer.DrawText(g, v, SmallFont, new Point((int)lx, (int)Math.Max(p.Y - 4 * K, ly)), s.Color, TextFormatFlags.NoPadding);
                }
            }
            if (hover >= 0 && hover < N) using (var hp = new Pen(Color.FromArgb(0x9A, 0xA3, 0xB5), 1) { DashStyle = DashStyle.Dash }) g.DrawLine(hp, X(hover), p.Y, X(hover), p.Bottom);
        }

        void Flush(Graphics g, Color c, List<PointF> pts)
        {
            if (pts.Count == 0) return;
            using (var pen = new Pen(c, 2 * K) { LineJoin = LineJoin.Round }) using (var br = new SolidBrush(c))
            {
                if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                foreach (var q in pts) g.FillEllipse(br, q.X - 3 * K, q.Y - 3 * K, 6 * K, 6 * K);
            }
            pts.Clear();
        }

        static double Nice(double v)
        {
            if (v <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v))), f = v / mag;
            return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * mag;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            if (N > 0) { double best = double.MaxValue; for (int i = 0; i < N; i++) { var d = Math.Abs(X(i) - e.X); if (d < best) { best = d; h = i; } } }
            if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }
    }

    /// The last run of a game in the general log (logs\AirFrameXR.log): what the host wrote every 10 s or 30 s while it ran. Only what is logged: the game's and the stream's fps and
    /// the generation lag (when frame generation is on), the GPU memory in use. Nothing is recorded for this: it reads the log that is already there.
    public static class LogTimeline
    {
        /// at most `max` points: the mean of each bucket, at the bucket's last time
        public static void Downsample(List<DateTime> t, List<double> v, int max, out List<DateTime> ot, out List<double> ov)
        {
            ot = new List<DateTime>(); ov = new List<double>();
            if (t.Count <= max) { ot.AddRange(t); ov.AddRange(v); return; }
            double per = t.Count / (double)max;
            for (int b = 0; b < max; b++)
            {
                int i0 = (int)Math.Floor(b * per), i1 = Math.Min(t.Count, (int)Math.Floor((b + 1) * per)); if (i1 <= i0) continue;
                double sum = 0; for (int i = i0; i < i1; i++) sum += v[i];
                ot.Add(t[i1 - 1]); ov.Add(sum / (i1 - i0));
            }
        }
        public class Run
        {
            public string Exe; public DateTime Start, End; public bool Ended;
            public List<DateTime> PerfT = new List<DateTime>(); public List<double> App = new List<double>(), Stream = new List<double>(), Lag = new List<double>();
            public List<DateTime> GpuT = new List<DateTime>(); public List<double> GpuMb = new List<double>();
            public List<DateTime> ArrT = new List<DateTime>(); public List<double> Arr = new List<double>(); public double ArrTarget = double.NaN;   // phase lock: how long before the render tick the frames arrive
        }
        static readonly System.Text.RegularExpressions.Regex Head = new System.Text.RegularExpressions.Regex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\.\d+ \[host\] INFO  (game started|game ended): (.+?)(?: after .*)?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);   // a name may have spaces (Dead Space.exe)
        static readonly System.Text.RegularExpressions.Regex Perf = new System.Text.RegularExpressions.Regex(@"frame generation perf: app ([\d.]+) fps.*? stream ([\d.]+) fps.*? lag ([\d.]+) ms", System.Text.RegularExpressions.RegexOptions.Compiled);
        static readonly System.Text.RegularExpressions.Regex Arrival = new System.Text.RegularExpressions.Regex(@"phase lock: arrivals (-?[\d.]+) ms before the render tick \(target ([\d.]+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);
        static readonly System.Text.RegularExpressions.Regex Gpu = new System.Text.RegularExpressions.Regex(@"GPU (\d+) of \d+ MB used", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static Run Last(string logFile, string exe)
        {
            try
            {
                if (!System.IO.File.Exists(logFile) || string.IsNullOrEmpty(exe)) return null;
                Run cur = null, last = null;
                using (var fs = new System.IO.FileStream(logFile, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var rd = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8))
                {
                    string line; DateTime lastT = DateTime.MinValue;
                    while ((line = rd.ReadLine()) != null)
                    {
                        if (line.Length < 24) continue;
                        DateTime t; bool okT = DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
                        if (!okT) continue;
                        lastT = t;
                        var m = Head.Match(line);
                        if (m.Success && string.Equals(m.Groups[3].Value, exe, StringComparison.OrdinalIgnoreCase))
                        {
                            if (m.Groups[2].Value == "game started") { cur = new Run { Exe = exe, Start = t }; last = cur; }
                            else if (cur != null) { cur.End = t; cur.Ended = true; cur = null; }
                            continue;
                        }
                        if (cur == null) continue;
                        var p = Perf.Match(line);
                        if (p.Success)
                        {
                            cur.PerfT.Add(t); cur.App.Add(double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture)); cur.Stream.Add(double.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture)); cur.Lag.Add(double.Parse(p.Groups[3].Value, CultureInfo.InvariantCulture));
                            continue;
                        }
                        var g = Gpu.Match(line);
                        if (g.Success) { cur.GpuT.Add(t); cur.GpuMb.Add(double.Parse(g.Groups[1].Value, CultureInfo.InvariantCulture)); continue; }
                        var a = Arrival.Match(line);
                        if (a.Success) { cur.ArrT.Add(t); cur.Arr.Add(double.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture)); cur.ArrTarget = double.Parse(a.Groups[2].Value, CultureInfo.InvariantCulture); }
                    }
                    if (last != null && !last.Ended) last.End = lastT;
                }
                return last;
            }
            catch { return null; }
        }
    }
}
