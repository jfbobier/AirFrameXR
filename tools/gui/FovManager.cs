// The field-of-view profile manager: bin\configure.exe --fov (docs/FOV_CALIBRATION.md). The same card as the setup wizard's field-of-view step.
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AirFrameXR
{
    public class FovManagerForm : DpiForm
    {
        readonly FovCard card;

        /// storePath / declared: the UI harness uses its own profile file and headset box.
        public FovManagerForm(string storePath = null, double[] declared = null)
        {
            Text = "AirFrameXR – Field of view profiles";
            Icon = Ui.AppIcon();
            Font = Ui.Body;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Width = 860; Height = 760;   // design values at 96 dpi
            MinimumSize = new Size(640, 520);
            Ui.FitToWorkArea(this);
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14, 10, 14, 10) };
            var head = Ui.Line("Your field-of-view profiles", Ui.Big); head.ForeColor = Ui.Accent;
            var intro = Ui.Line("Every calibration you keep and every set of values you enter is stored here, apart from the settings, so an upgrade does not lose them. Choose which one the games get in the main window (FOV restriction). A profile applies after a streamer restart.");
            intro.ForeColor = Color.DimGray; Ui.FitWidth(intro, 760, asMaximum: true);
            card = new FovCard(storePath, declared) { Margin = new Padding(0, 6, 0, 0) };
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top };
            col.Controls.Add(head); col.Controls.Add(intro); col.Controls.Add(card);
            scroller.Controls.Add(col);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(10), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
            var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 30) };
            close.Click += (s, e) => Close();
            bar.Controls.Add(close);
            Controls.Add(scroller); Controls.Add(bar);
            Ui.ScreenshotWhenShown(this, 1500);
        }

        /// The UI harness: three profiles (a calibration with its window, two typed ones) in the harness's own file; typed values for the red / yellow states.
        public static string DemoStore()
        {
            var path = Path.Combine(Path.GetTempPath(), "airframexr_fov_demo_" + Guid.NewGuid().ToString("N") + ".json");
            var win = new System.Collections.Generic.List<double[]>();
            for (int i = 0; i < 24; i++)
            {
                double a = i * 15 * Math.PI / 180, cx = Math.Cos(a), cy = Math.Sin(a);
                double reach = Math.Sqrt(1 / (Math.Pow(cx / (cx >= 0 ? 0.95 : 0.72), 2) + Math.Pow(cy / (cy >= 0 ? 0.74 : 0.66), 2)));
                win.Add(new[] { reach * cx, reach * cy });
            }
            UserFovStore.Add(new UserFov { Id = "d1", Name = "Calibration 2026-11-02 21:10", Headset = "Apple Headset", Source = "calibrated", Created = "2026-11-02T21:10:00", Tan = new[] { 0.97, 0.71, 0.69, 0.63 }, Window = win }, path);
            UserFovStore.Add(new UserFov { Id = "d2", Name = "Quest 3, about 10 % less", Headset = "Meta Quest 3", Source = "manual", Created = "2026-11-01T10:00:00", Tan = new[] { 1.2349, 0.8391, 0.9325, 1.3764 } }, path);
            UserFovStore.Add(new UserFov { Id = "d3", Name = "Tight", Headset = "Apple Headset", Source = "manual", Created = "2026-10-30T09:00:00", Tan = new[] { 0.9, 0.65, 0.6, 0.55 } }, path);
            return path;
        }

        public FovCard Card => card;
    }
}
