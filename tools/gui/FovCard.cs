// The 2D map of a field of view, shared by the calibration result page, the profile card of the setup wizard and the profile manager (docs/FOV_CALIBRATION.md).
// Tangent space throughout: x outward from the nose, y up; the origin is the view axis.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
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

    /// The profile card, shared by the profile manager window (and, later, the setup wizard): the user's profiles with their map, delete and rename, values typed by hand
    /// with the red / yellow check and a live map, the calibration and the fovgrid check. Everything is stored in config\user_fov_profiles.json (UserFovStore).
    public class FovCard : FlowLayoutPanel
    {
        readonly string storePath;
        readonly double[] declared;
        readonly AppSettings settings = new AppSettings();
        readonly ListBox list = new ListBox { Width = 360, Height = 150, IntegralHeight = false, FormattingEnabled = true, AccessibleName = "My field of view profiles" };
        readonly FovMap map = new FovMap { Size = new Size(350, 250), Margin = new Padding(12, 3, 3, 3) };
        readonly Label details, message, note;
        readonly Button delete = new Button { Text = "Delete", AutoSize = true }, rename = new Button { Text = "Rename...", AutoSize = true };
        readonly Button add = new Button { Text = "Add to my profiles", AutoSize = true, Enabled = false, Margin = new Padding(12, 2, 3, 3) };
        readonly Button calibrate = new Button { Text = "Calibrate now (30 min, needs the headset)...", AutoSize = true, Margin = new Padding(3, 8, 8, 3) };
        readonly Button import = new Button { Text = "Import a file...", AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
        readonly Button grid = new Button { Text = "Check with fovgrid...", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        readonly ComboBox unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, AccessibleName = "Unit of the typed values" };
        readonly TextBox[] box = new TextBox[4];
        readonly TextBox name = new TextBox { Width = 170, AccessibleName = "Name of the new profile" };
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };
        readonly Timer watch = new Timer { Interval = 1500 };
        Process calProc;
        bool loading;

        /// The profile highlighted in the list, null when none.
        public UserFov Selected => list.SelectedItem as UserFov;
        public event Action SelectionChanged;
        // the UI harness and the self-test
        public bool CanAdd => add.Enabled;
        public string Message => message.Text;
        public int Count => list.Items.Count;
        public void AddForTest() => AddTyped();

        /// storePath / declared: tests use their own file and box; null = the user's file and the headset's box as the host saved it.
        public FovCard(string storePath = null, double[] declared = null)
        {
            this.storePath = storePath;
            this.declared = declared ?? AppSettings.ReadClientFov();
            FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoSize = true; Margin = new Padding(0);
            var top = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            left.Controls.Add(Ui.Line("My profiles", Ui.Bold));
            list.Format += (s, e) => e.Value = Describe((UserFov)e.ListItem);
            list.SelectedIndexChanged += (s, e) => { if (!loading) { typedClear(); ShowSelected(); SelectionChanged?.Invoke(); } };
            left.Controls.Add(list);
            var lb = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            delete.Click += (s, e) => Delete(); rename.Click += (s, e) => Rename();
            lb.Controls.Add(delete); lb.Controls.Add(rename);
            left.Controls.Add(lb);
            top.Controls.Add(left); top.Controls.Add(map);
            Controls.Add(top);

            details = Ui.Line(""); details.ForeColor = Color.DimGray; Controls.Add(details); Ui.FitWidth(details, 700, asMaximum: true);
            message = Ui.Line(""); message.Font = Ui.Bold; Controls.Add(message); Ui.FitWidth(message, 700, asMaximum: true);

            var typed = new GroupBox { Text = "Enter values by hand", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3, 8, 3, 3), Padding = new Padding(8, 4, 8, 6) };
            var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
            var row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            unit.Items.AddRange(new object[] { "Tangents", "Degrees" }); unit.SelectedIndex = 0;
            unit.SelectedIndexChanged += (s, e) => Typed();
            row1.Controls.Add(new Label { Text = "Unit", AutoSize = true, Margin = new Padding(3, 6, 3, 0) }); row1.Controls.Add(unit);
            var names = new[] { "Outer", "Inner", "Upper", "Lower" };
            for (int k = 0; k < 4; k++)
            {
                box[k] = new TextBox { Width = 60, AccessibleName = names[k] };
                box[k].TextChanged += (s, e) => Typed();
                row1.Controls.Add(new Label { Text = names[k], AutoSize = true, Margin = new Padding(12, 6, 3, 0) }); row1.Controls.Add(box[k]);
                tips.SetToolTip(box[k], names[k] + ": " + (k == 0 ? "the side away from the nose" : k == 1 ? "the side towards the nose" : k == 2 ? "up" : "down") + ". Tangents (what the Advanced window and the profiles use) or degrees from the view axis, as chosen in Unit. Never more than the headset shows.");
            }
            inner.Controls.Add(row1);
            var row2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            row2.Controls.Add(new Label { Text = "Name", AutoSize = true, Margin = new Padding(3, 6, 3, 0) }); row2.Controls.Add(name); row2.Controls.Add(add);
            add.Click += (s, e) => AddTyped();
            inner.Controls.Add(row2);
            typed.Controls.Add(inner);
            Controls.Add(typed);

            var rowB = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            calibrate.Click += (s, e) => Calibrate(); grid.Click += (s, e) => CheckWithGrid();
            tips.SetToolTip(calibrate, "Measures the edges of what your headset shows with the eye calibration wizard (about 30 minutes, the headset on). The result is kept here as a new profile.");
            tips.SetToolTip(grid, "Starts the streamer if needed and draws the chosen rectangle on a degree grid in the headset (bench\\fovgrid.exe): the outline must sit just outside the picture you can see.");
            import.Click += (s, e) => ImportFile();
            tips.SetToolTip(import, "A calibration file someone shared, or the one fov_profile.exe wrote (a .profile.json with a measured field of view): it is added to your profiles.");
            rowB.Controls.Add(calibrate); rowB.Controls.Add(grid); rowB.Controls.Add(import);
            Controls.Add(rowB); Ui.FitWidth(rowB, 700, asMaximum: true);   // the three buttons wrap in a narrow window
            note = Ui.Line(""); note.ForeColor = Color.DimGray; Controls.Add(note); Ui.FitWidth(note, 700, asMaximum: true);

            watch.Tick += (s, e) => { if (calProc != null && calProc.HasExited) { calProc = null; watch.Stop(); Reload(null); } };
            Disposed += (s, e) => { watch.Dispose(); tips.Dispose(); };
            Reload(null);
            Typed();
        }

        static string Describe(UserFov u) => u.Name + "   " + string.Join(" / ", u.Tan.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)));

        /// Reads the store again; selectId (or the same profile as before, or the first) is highlighted.
        public void Reload(string selectId)
        {
            loading = true;
            var keep = selectId ?? Selected?.Id;
            list.Items.Clear();
            foreach (var u in UserFovStore.Load(storePath)) list.Items.Add(u);
            var pick = list.Items.Cast<UserFov>().FirstOrDefault(u => u.Id == keep) ?? (list.Items.Count > 0 ? (UserFov)list.Items[0] : null);
            if (pick != null) list.SelectedItem = pick;
            loading = false;
            delete.Enabled = rename.Enabled = list.Items.Count > 0;
            if (box != null && box.All(b => b == null || b.Text.Length == 0)) ShowSelected();
            SelectionChanged?.Invoke();
        }

        bool typedAny => box.Any(b => b != null && b.Text.Trim().Length > 0);
        void typedClear() { if (!typedAny) return; loading = true; foreach (var b in box) b.Text = ""; loading = false; }

        void ShowSelected()
        {
            var u = Selected;
            map.Declared = declared; map.Window = u?.Window; map.Rectangle = u?.Tan; map.RectColor = Ui.Accent;
            if (u == null) { details.Text = list.Items.Count == 0 ? "No profile yet: calibrate, or enter values below." : ""; message.Text = ""; Layout(null); return; }
            var c = FovValidator.Check(u.Tan, FovUnit.Tangent, declared);
            details.Text = Summary(c) + "\n" + (u.Calibrated ? "Calibrated" : "Entered by hand") + (u.Created.Length >= 10 ? " on " + u.Created.Substring(0, 10) : "") + (u.Headset.Length > 0 ? " for " + u.Headset : "") + ".";
            Layout(c);
        }

        static string Summary(FovCheck c)
        {
            string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            string D(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
            return $"Outer {F(c.Tan[0])}  Inner {F(c.Tan[1])}  Upper {F(c.Tan[2])}  Lower {F(c.Tan[3])} (tangents) = {D(c.Deg[0])} / {D(c.Deg[1])} / {D(c.Deg[2])} / {D(c.Deg[3])} degrees, {D(c.TotalH)} x {D(c.TotalV)} in total"
                + (double.IsNaN(c.AreaShare) ? "." : $"; the game renders {(100 * c.AreaShare).ToString("0", CultureInfo.InvariantCulture)} % of the pixels of the headset's own view.");
        }

        void Layout(FovCheck c)
        {
            message.Text = c == null ? "" : c.Summary;
            message.ForeColor = c == null ? Color.Black : c.Level == FovLevel.Invalid ? Color.Firebrick : c.Level == FovLevel.Strange ? Color.DarkGoldenrod : Color.SeaGreen;
        }

        // ---- values typed by hand ----------------------------------------------------------------------------------------------------------------------

        FovCheck TypedCheck() => FovValidator.Check(box.Select(b => FovValidator.Parse(b.Text)).ToArray(), unit.SelectedIndex == 1 ? FovUnit.Degrees : FovUnit.Tangent, declared);

        void Typed()
        {
            if (loading || box == null || box.Any(b => b == null)) return;
            if (!typedAny) { add.Enabled = false; ShowSelected(); return; }
            var c = TypedCheck();
            bool filled = box.All(b => b.Text.Trim().Length > 0);
            add.Enabled = filled && c.Usable;
            map.Declared = declared; map.Window = null;
            map.Rectangle = c.Tan.All(v => !double.IsNaN(v)) ? c.Tan : null;
            map.RectColor = c.Level == FovLevel.Invalid ? Color.Firebrick : c.Level == FovLevel.Strange ? Color.DarkGoldenrod : Ui.Accent;
            details.Text = c.Usable ? Summary(c) : "";
            Layout(c);
        }

        /// The UI harness: types values as a person would (unit "Tangents" or "Degrees").
        public void TypeForTest(string unitName, params string[] values)
        {
            unit.SelectedIndex = unitName == "Degrees" ? 1 : 0;
            loading = true; for (int k = 0; k < 4; k++) box[k].Text = values[k]; loading = false;
            if (name.Text.Length == 0) name.Text = "My values";
            Typed();
        }

        void AddTyped()
        {
            var c = TypedCheck();
            if (!c.Usable || !box.All(b => b.Text.Trim().Length > 0)) return;
            var u = UserFovStore.Add(new UserFov { Id = UserFovStore.NewId(), Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : "My values", Headset = settings.HeadsetName, Source = "manual", Tan = c.Tan.Select(v => Math.Round(v, 4)).ToArray() }, storePath);
            Log.Write("INFO", $"fov profiles: '{u.Name}' added by hand ({string.Join(" / ", u.Tan.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)))})");
            loading = true; foreach (var b in box) b.Text = ""; name.Text = ""; loading = false;
            add.Enabled = false;
            Reload(u.Id);
        }

        // ---- the list ------------------------------------------------------------------------------------------------------------------------------------

        void Delete()
        {
            var u = Selected; if (u == null) return;
            bool inUse = settings.FovOverride && Enumerable.Range(0, 4).All(k => Math.Abs(new[] { settings.FovOuter, settings.FovInner, settings.FovUpper, settings.FovLower }[k] - u.Tan[k]) < 0.006);
            var ask = $"Delete \"{u.Name}\"?" + (inUse ? "\n\nIt is the field of view in use now: the restriction is switched off (it needs a streamer restart to apply)." : "");
            if (MessageBox.Show(FindForm(), ask, "Field of view profiles", MessageBoxButtons.YesNo, inUse ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;
            UserFovStore.Remove(u.Id, storePath);
            if (inUse) { var st = new AppSettings(); Json.Remove(st.Root, "video.fov_override"); st.Save(); Log.Write("INFO", "fov profiles: the profile in use was deleted, restriction off"); }
            Log.Write("INFO", $"fov profiles: '{u.Name}' deleted");
            Reload(null);
        }

        void Rename()
        {
            var u = Selected; if (u == null) return;
            var n = Prompt(FindForm(), "Rename the profile", u.Name);
            if (string.IsNullOrWhiteSpace(n) || n.Trim() == u.Name) return;
            UserFovStore.Rename(u.Id, n, storePath);
            Reload(u.Id);
        }

        static string Prompt(Form owner, string title, string current)
        {
            using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Ui.Body, AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi, Padding = new Padding(10) })
            {
                var t = new TextBox { Text = current, Width = 280, Margin = new Padding(3, 3, 3, 8) };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; row.Controls.Add(ok); row.Controls.Add(cancel);
                col.Controls.Add(t); col.Controls.Add(row); f.Controls.Add(col); f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text.Trim() : null;
            }
        }

        /// "Import a file...": a profile file with a measured field of view (fov_profile.exe's, or one shared by another user) becomes one of the user's profiles.
        void ImportFile()
        {
            string path;
            using (var dlg = new OpenFileDialog { Title = "Import a calibration file", Filter = "AirFrameXR profile (*.json)|*.json|All files (*.*)|*.*", CheckFileExists = true })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            var list = new List<HmdProfile>();
            try { list = HmdProfiles.Parse(File.ReadAllText(path), Path.GetFileName(path)); } catch (Exception e) { Log.Write("WARN", "calibration file: " + e.Message); }
            var p = list.FirstOrDefault(x => x.HasFov);
            if (p == null) { MessageBox.Show(FindForm(), "This file has no measured field of view. Pick the .profile.json that fov_profile.exe wrote.", "Field of view profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var hs = settings.HeadsetName;
            if (p.NameContains.Length > 0 && hs.Length > 0 && !p.NameContains.Any(n => hs.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                && MessageBox.Show(FindForm(), $"This calibration was made for a headset named like \"{string.Join("\" / \"", p.NameContains)}\", yours is \"{hs}\". Add it anyway?", "Field of view profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var u = UserFovStore.Add(new UserFov { Id = UserFovStore.NewId(), Name = p.DisplayName.Split('(')[0].Trim(), Headset = p.NameContains.FirstOrDefault() ?? "", Source = "imported", Tan = p.FovTangents(), Quality = p.FovRenderQuality, ClientScale = p.FovClientScale, Note = p.FovNote }, storePath);
            Log.Write("INFO", $"fov profiles: '{u.Name}' imported from {Path.GetFileName(path)}");
            Reload(u.Id);
        }

        // ---- calibration and the grid ------------------------------------------------------------------------------------------------------------------

        void Calibrate()
        {
            var exe = Path.Combine(Paths.Bin, "calibrate.exe");
            if (!File.Exists(exe)) { MessageBox.Show(FindForm(), "bin\\calibrate.exe is missing from this package.", "Field of view profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (calProc != null && !calProc.HasExited) { note.Text = "The calibration is already open."; return; }
            try { calProc = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Paths.Bin }); watch.Start(); note.Text = "The calibration window is open; its result is added to the list when you apply or save it."; }
            catch (Exception e) { MessageBox.Show(FindForm(), "Could not start the calibration: " + e.Message, "Field of view profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        /// The rectangle to show on the grid: the typed values when they are complete and usable, else the selected profile.
        double[] GridTangents()
        {
            if (typedAny) { var c = TypedCheck(); return box.All(b => b.Text.Trim().Length > 0) && c.Usable ? c.Tan : null; }
            return Selected?.Tan;
        }

        void CheckWithGrid()
        {
            var t = GridTangents();
            if (t == null) { note.Text = "Select a profile, or type four usable values, first."; return; }
            var bat = Path.Combine(Paths.Dir, "bench", "fovgrid.bat");
            if (!File.Exists(bat)) { MessageBox.Show(FindForm(), "bench\\fovgrid.bat is missing from this package.", "Field of view profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (settings.FovOverride && MessageBox.Show(FindForm(), "The field of view restriction is on. The outline is only readable with it off (with it on, the game's view is this rectangle and the outline falls on the edge of the picture): untick it in the main window and restart the streamer first.\n\nShow the grid anyway?", "Field of view profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                if (Process.GetProcessesByName("streamer").Length == 0 && File.Exists(Paths.Runtime))
                    Process.Start(new ProcessStartInfo(Paths.Runtime) { WorkingDirectory = Paths.Bin, WindowStyle = ProcessWindowStyle.Minimized });
                var arg = string.Join(",", t.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)));
                Process.Start(new ProcessStartInfo(bat, arg) { WorkingDirectory = Path.GetDirectoryName(bat), UseShellExecute = true });
                note.Text = "Open the ALVR app on the headset: the grid starts in it once it is connected. The white outline is the rectangle " + arg + "; close the grid's console window when done.";
                Log.Write("INFO", "fov profiles: fovgrid with " + arg);
            }
            catch (Exception e) { MessageBox.Show(FindForm(), "Could not start the grid: " + e.Message, "Field of view profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
