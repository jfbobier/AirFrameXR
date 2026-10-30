// The Black Box time series (docs/BENCH_UI.md): one small chart per category on a shared time axis, every line drawn as a percentage of its own limit (100 % = the limit or the budget:
// the frame period, 100 % load, all the cores, all the RAM, the late-frame budget, the display rate), so a GPU load, a network latency in ms and a late-frame share can sit on one scale.
// Custom drawn, no chart library. Hovering shows the real values at that second in every chart (the cursor is shared). Compiled only into the analyzer (and the UI test).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using AirFrameXR.Perf;

namespace AirFrameXR
{
    public sealed class TimelineChart : Control
    {
        static readonly Color[] Palette = { BlackBoxBrand.Cyan, BlackBoxBrand.Blue, Color.FromArgb(0xF5, 0xA6, 0x23) };
        const double YMax = 125;                       // the axis runs to 125 % of the limit: a line above 100 % is over it
        readonly List<TimeSeries> series; readonly double origin; readonly int n; readonly double[] lateStrip; readonly string title;
        static readonly List<TimelineChart> All = new List<TimelineChart>();
        int cursor = -1;

        /// lateStrip: the late-frame share per second against its budget (only the first chart of a report shows it, as a strip above the lines)
        public TimelineChart(string title, List<TimeSeries> series, double originSeconds, double[] lateStrip)
        {
            this.title = title; this.series = series; origin = originSeconds; this.lateStrip = lateStrip; n = series.Max(s => s.V.Length);
            DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.White; Margin = new Padding(0, 2, 0, 4); Cursor = Cursors.Cross;
            AccessibleName = title + " time series"; All.Add(this);
            Disposed += (s, e) => All.Remove(this);
        }

        public int LegendHeight { get { return Math.Max(14, Font.Height + 2); } }
        Rectangle Plot { get { int top = LegendHeight + (lateStrip != null ? 10 : 0) + 2; return new Rectangle(34, top, Math.Max(10, Width - 34 - 6), Math.Max(10, Height - top - 16)); } }

        static string Clock(double sec) { var t = TimeSpan.FromSeconds(Math.Max(0, sec)); return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(BackColor);
            var plot = Plot; var small = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1.5f));
            // legend: a colour dot and the name with what 100 % is
            int x = 0;
            for (int k = 0; k < series.Count; k++)
            {
                using (var b = new SolidBrush(Palette[k % Palette.Length])) g.FillEllipse(b, x, 3, 8, 8);
                string text = series[k].Name + " (100 % = " + series[k].LimitText + ")";
                g.DrawString(text, small, Brushes.DimGray, x + 11, 0); x += 11 + (int)g.MeasureString(text, small).Width + 12;
            }
            if (lateStrip != null)
            {
                int sy = LegendHeight + 2; g.DrawString("late", small, Brushes.DimGray, 0, sy - 2);
                for (int i = 0; i < n && i < lateStrip.Length; i++)
                {
                    double v = lateStrip[i]; if (double.IsNaN(v) || v <= 0) continue;
                    float x0 = plot.Left + (float)i / n * plot.Width, w = Math.Max(1f, (float)plot.Width / n);
                    int a = (int)Math.Min(255, 90 + v / 300.0 * 165);   // darker when further over the budget
                    using (var b = new SolidBrush(Color.FromArgb(a, BlackBoxBrand.Red))) g.FillRectangle(b, x0, sy, w, 8);
                }
            }
            // grid: 0, 50 and 100 % (the limit, dashed)
            foreach (var pct in new[] { 0.0, 50.0, 100.0 })
            {
                float y = plot.Bottom - (float)(pct / YMax) * plot.Height;
                using (var p = new Pen(pct == 100 ? Color.FromArgb(150, BlackBoxBrand.Red) : Color.FromArgb(225, 230, 238)) { DashStyle = pct == 100 ? DashStyle.Dash : DashStyle.Solid }) g.DrawLine(p, plot.Left, y, plot.Right, y);
                g.DrawString(pct.ToString("F0", CultureInfo.InvariantCulture), small, Brushes.DimGray, 0, y - small.Height / 2f);
            }
            // lines
            for (int k = 0; k < series.Count; k++)
            {
                var s = series[k]; var pts = new List<PointF>();
                using (var p = new Pen(Palette[k % Palette.Length], 1.6f))
                {
                    for (int i = 0; i < s.V.Length; i++)
                    {
                        double pc = s.Percent(i);
                        if (double.IsNaN(pc)) { if (pts.Count > 1) g.DrawLines(p, pts.ToArray()); pts.Clear(); continue; }
                        pts.Add(new PointF(plot.Left + (n > 1 ? (float)i / (n - 1) : 0) * plot.Width, plot.Bottom - (float)(Math.Min(pc, YMax) / YMax) * plot.Height));
                    }
                    if (pts.Count > 1) g.DrawLines(p, pts.ToArray());
                }
            }
            // time axis
            int ticks = Math.Max(2, Math.Min(8, plot.Width / 90));
            for (int t = 0; t <= ticks; t++)
            {
                double sec = origin + (double)t / ticks * (n - 1); float tx = plot.Left + (float)t / ticks * plot.Width;
                var label = Clock(sec); var sz = g.MeasureString(label, small);
                g.DrawString(label, small, Brushes.DimGray, Math.Min(Math.Max(0, tx - sz.Width / 2), Width - sz.Width), plot.Bottom + 1);
            }
            // the shared cursor and its values
            if (cursor >= 0 && cursor < n)
            {
                float cx = plot.Left + (n > 1 ? (float)cursor / (n - 1) : 0) * plot.Width;
                using (var p = new Pen(Color.FromArgb(160, 60, 70, 90))) g.DrawLine(p, cx, plot.Top, cx, plot.Bottom);
                var lines = new List<string> { Clock(origin + cursor) + " into the recording" };
                foreach (var s in series) lines.Add(s.Name + ": " + (cursor < s.V.Length && !double.IsNaN(s.V[cursor]) ? s.V[cursor].ToString(s.Fmt, CultureInfo.InvariantCulture) + " " + s.Unit : "no reading"));
                if (lateStrip != null && cursor < lateStrip.Length && !double.IsNaN(lateStrip[cursor])) lines.Add("late frames: " + (lateStrip[cursor] * MaxLate / 100.0).ToString("F1", CultureInfo.InvariantCulture) + " % of this second's gaps");
                string text = string.Join("\n", lines); var sz = g.MeasureString(text, small);
                float bx = cx + 10 + sz.Width > Width ? cx - 10 - sz.Width - 8 : cx + 10, by = plot.Top + 2;
                using (var b = new SolidBrush(Color.FromArgb(235, 255, 255, 255))) g.FillRectangle(b, bx, by, sz.Width + 8, sz.Height + 4);
                using (var p = new Pen(Color.FromArgb(200, 200, 208))) g.DrawRectangle(p, bx, by, sz.Width + 8, sz.Height + 4);
                g.DrawString(text, small, Brushes.Black, bx + 4, by + 2);
            }
            small.Dispose();
        }
        const double MaxLate = Report.MaxLateShare * 100.0;

        void SetCursor(int c) { if (cursor == c) return; cursor = c; Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            var plot = Plot; int c = n > 1 ? (int)Math.Round((double)(e.X - plot.Left) / plot.Width * (n - 1)) : 0;
            c = (e.X < plot.Left || e.X > plot.Right) ? -1 : Math.Max(0, Math.Min(n - 1, c));
            foreach (var ch in All.ToArray()) if (ch.n == n || c < 0) ch.SetCursor(c);   // every chart of this report shows the same second
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { foreach (var ch in All.ToArray()) ch.SetCursor(-1); base.OnMouseLeave(e); }
    }
}
