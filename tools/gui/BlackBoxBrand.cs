// The Black Box identity (branding/blackbox/, kit README): a flight-recorder look for the two Black Box programs, the recorder (blackbox_recorder.exe) and the analyzer
// (blackbox_analyzer.exe). Compiled only into those two (and the UI test). Palette: navy #031022, cyan #00D9F5, electric blue #087BFF, recording red #FF5154.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace AirFrameXR
{
    public static class BlackBoxBrand
    {
        public static readonly Color Navy = Color.FromArgb(0x03, 0x10, 0x22), Cyan = Color.FromArgb(0x00, 0xD9, 0xF5), Blue = Color.FromArgb(0x08, 0x7B, 0xFF), Red = Color.FromArgb(0xFF, 0x51, 0x54);
    }

    /// The header of a Black Box window: the app icon, "AirFrameXR BLACK BOX", the role, and a red REC lamp that lights while the recorder records. Native controls, so it is
    /// crisp at every scale and as small as the other windows' compact header. Sizes are design values at 96 dpi (the window's AutoScaleDimensions scale them).
    public class BlackBoxHeader : Panel
    {
        readonly Label rec = new Label { Text = "● REC", AutoSize = true, ForeColor = Color.FromArgb(0x5A, 0x2A, 0x32), Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0), Visible = true };
        bool recording;

        public BlackBoxHeader(string role, bool showRec = true)
        {
            Dock = DockStyle.Top; AutoSize = true; BackColor = BlackBoxBrand.Navy; Padding = new Padding(12, 8, 12, 8);
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, RowCount = 1, Dock = DockStyle.Top, BackColor = BlackBoxBrand.Navy };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var img = Ui.Resource("blackbox_128.png");
            var pic = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Margin = new Padding(0, 0, 12, 0), BackColor = BlackBoxBrand.Navy };
            AccessibleName = "AirFrameXR Black Box " + role;
            var words = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), BackColor = BlackBoxBrand.Navy };
            var line1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = BlackBoxBrand.Navy };
            line1.Controls.Add(new Segments(new Font("Segoe UI", 11f, FontStyle.Bold), new[] { "AirFrame", "XR", "   BLACK BOX " + role.ToUpperInvariant() }, new[] { Color.White, BlackBoxBrand.Cyan, Color.White }));
            var sub = new Label { Text = "PERFORMANCE FLIGHT RECORDER", AutoSize = true, ForeColor = Color.FromArgb(0x8F, 0xA9, 0xC8), Font = new Font("Segoe UI", 8f, FontStyle.Bold), Margin = new Padding(0, 1, 0, 0) };
            words.Controls.Add(line1); words.Controls.Add(sub);
            rec.Anchor = AnchorStyles.Right; rec.Margin = new Padding(8, 0, 0, 0);
            if (img != null) t.Controls.Add(pic, 0, 0); else t.ColumnStyles[0].Width = 0;
            t.Controls.Add(words, 1, 0); if (showRec) t.Controls.Add(rec, 2, 0);
            Controls.Add(t);
        }

        /// the REC lamp: dim while idle, red while recording
        public bool Recording
        {
            get { return recording; }
            set { recording = value; rec.ForeColor = value ? BlackBoxBrand.Red : Color.FromArgb(0x5A, 0x2A, 0x32); }
        }
    }

    /// One line of text in several colours, drawn piece by piece. Measured and drawn on the control's own device context, so the pieces fit at every scale
    /// (a Label per colour leaves a visible gap between the pieces).
    sealed class Segments : Control
    {
        readonly string[] text; readonly Color[] colors;
        const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left | TextFormatFlags.Top;
        public Segments(Font font, string[] text, Color[] colors)
        {
            Font = font; this.text = text; this.colors = colors; Margin = new Padding(0); BackColor = BlackBoxBrand.Navy; DoubleBuffered = true; AutoSize = true;
        }
        int Width1(Graphics g, string t) { return TextRenderer.MeasureText(g, t, Font, new Size(int.MaxValue, int.MaxValue), Flags).Width; }
        public override Size GetPreferredSize(Size proposedSize)
        {
            using (var g = CreateGraphics())
            {
                int w = 0; foreach (var t in text) w += Width1(g, t);
                return new Size(w + 2, TextRenderer.MeasureText(g, "Ag", Font, new Size(int.MaxValue, int.MaxValue), Flags).Height + 2);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            int x = 0;
            for (int i = 0; i < text.Length; i++) { TextRenderer.DrawText(e.Graphics, text[i], Font, new Point(x, 0), colors[i], BlackBoxBrand.Navy, Flags); x += Width1(e.Graphics, text[i]); }
        }
    }
}
