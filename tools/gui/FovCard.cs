// The 2D map of a field of view, shared by the calibration result page, the profile card of the setup wizard and the profile manager (docs/FOV_CALIBRATION.md).
// Tangent space throughout: x outward from the nose, y up; the origin is the view axis.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AirFrameXR
{
    public class FovMap : Control
    {
        /// outer, inner, upper, lower (tangents) of the rectangle the game would be told; null = none drawn
        public double[] Rectangle { get => rect; set { rect = value; Invalidate(); } }
        /// the headset's own box (what it declares), drawn as a dashed outline; null = none known
        public double[] Declared { get => declared; set { declared = value; Invalidate(); } }
        /// the measured window of a calibration, with its rays; empty = none
        public IList<double[]> Window { get => window; set { window = value ?? new List<double[]>(); Invalidate(); } }
        /// the rectangle's colour: the accent, or red / amber when the numbers are invalid / strange
        public Color RectColor { get => rectColor; set { rectColor = value; Invalidate(); } }

        double[] rect, declared; IList<double[]> window = new List<double[]>(); Color rectColor = Ui.Accent;

        public FovMap() { DoubleBuffered = true; BackColor = Color.White; Size = new Size(520, 340); AccessibleName = "Field of view map"; }

        /// The calibration result page's picture: the rectangle, the measured window and its rays.
        public static FovMap FromResult(FovFitResult r) => new FovMap
        {
            Rectangle = new[] { r.Rectangle.Outer, r.Rectangle.Inner, r.Rectangle.Upper, r.Rectangle.Lower },
            Window = r.Window.Select(w => new[] { w.X, w.Y }).ToList(),
            AccessibleName = "Your visible window with the measured rays",
        };

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // the extent: every shape that is drawn, at least the unit square so an empty map still shows its axes
            double ex0 = -0.5, ex1 = 0.5, ey0 = -0.5, ey1 = 0.5;
            void Grow(double x0, double x1, double y0, double y1) { ex0 = Math.Min(ex0, x0); ex1 = Math.Max(ex1, x1); ey0 = Math.Min(ey0, y0); ey1 = Math.Max(ey1, y1); }
            if (rect != null && Ok(rect)) Grow(-rect[1], rect[0], -rect[3], rect[2]);
            if (declared != null && Ok(declared)) Grow(-declared[1], declared[0], -declared[3], declared[2]);
            foreach (var w in window) Grow(w[0], w[0], w[1], w[1]);
            float x0f = (float)ex0, x1f = (float)ex1, y0f = (float)ey0, y1f = (float)ey1;
            float pad = 0.06f * Math.Max(x1f - x0f, y1f - y0f); x0f -= pad; x1f += pad; y0f -= pad; y1f += pad;
            float k = Math.Min((Width - 2) / (x1f - x0f), (Height - 2) / (y1f - y0f));
            float ox = (Width - k * (x1f - x0f)) / 2, oy = (Height - k * (y1f - y0f)) / 2;
            Func<float, float, PointF> map = (x, y) => new PointF(ox + (x - x0f) * k, oy + (y1f - y) * k);
            var c = map(0, 0);
            using (var axis = new Pen(Color.Gainsboro)) { g.DrawLine(axis, map(x0f, 0), map(x1f, 0)); g.DrawLine(axis, map(0, y0f), map(0, y1f)); }
            if (declared != null && Ok(declared))
            {
                var a = map(-(float)declared[1], (float)declared[2]); var b = map((float)declared[0], -(float)declared[3]);
                using (var d = new Pen(Color.Silver, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) g.DrawRectangle(d, a.X, a.Y, b.X - a.X, b.Y - a.Y);
            }
            if (window.Count > 0)
            {
                using (var ray = new Pen(Color.Silver, 1f))
                using (var dot = new SolidBrush(Color.DimGray))
                    foreach (var w in window) { var q = map((float)w[0], (float)w[1]); g.DrawLine(ray, c, q); g.FillEllipse(dot, q.X - 3, q.Y - 3, 6, 6); }
                using (var outline = new Pen(Color.DimGray, 1.5f)) g.DrawPolygon(outline, window.Select(w => map((float)w[0], (float)w[1])).ToArray());
            }
            if (rect != null && Ok(rect))
            {
                var a = map(-(float)rect[1], (float)rect[2]); var b = map((float)rect[0], -(float)rect[3]);
                using (var p = new Pen(rectColor, 2.5f)) g.DrawRectangle(p, a.X, a.Y, b.X - a.X, b.Y - a.Y);
            }
            using (var f = new Font(Font.FontFamily, 8.5f))
            {
                g.DrawString("nose", f, Brushes.DimGray, 4, c.Y + 2); g.DrawString("outer", f, Brushes.DimGray, Width - 38, c.Y + 2);
                g.DrawString("up", f, Brushes.DimGray, c.X + 4, 2); g.DrawString("down", f, Brushes.DimGray, c.X + 4, Height - 16);
            }
        }

        static bool Ok(double[] v) => v.Length >= 4 && v.Take(4).All(x => !double.IsNaN(x) && !double.IsInfinity(x) && x > 0);
    }
}
