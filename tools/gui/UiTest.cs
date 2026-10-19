// uitest.exe (harness/ui_matrix.py): builds every window, dialog and wizard page of the GUI, pretends the display is each of a list of
// resolutions at a Windows scale, and audits the layout: does the window fit the work area, is any control clipped by its parent, do siblings
// overlap, is text cut off (buttons, labels, list headers and cells). Writes a PNG per case and results.json. Not shipped.
//
//   uitest.exe --out <dir> [--matrix 1920x1080@100,1920x1080@125,...] [--only <form name>]
//
// Emulation: the forms are built and shown (off screen) at the real display's dpi, which is what the framework scales them to; then everything
// is scaled by target/real (sizes, margins, fonts) and re-laid out. List columns are not scaled by the framework: the code gives them through
// Ui.Col, which follows Ui.DpiOverride, so a column that was left in raw pixels shows up as cut-off text. Close to the real thing, not the real
// thing: real Windows lays out from scratch at the new dpi; a final look on a real display at each scale is still worth it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace VisionALVR
{
    static class UiTest
    {
        class Case { public string Name; public Func<Form> Make; public Action<Form> Prepare; }
        static readonly List<string> Json = new List<string>();

        static IEnumerable<Case> Cases()
        {
            yield return new Case { Name = "launcher", Make = () => new LauncherForm() };
            yield return new Case { Name = "streamer", Make = () => new RuntimeForm(null), Prepare = f => ((RuntimeForm)f).ApplyDemoState() };
            yield return new Case { Name = "streamer-empty", Make = () => new RuntimeForm(null) };
            yield return new Case { Name = "performance", Make = () => new PerformanceForm("Cyberpunk2077.exe") };
            for (int p = 0; p <= 6; p++)
            {
                int page = p;
                yield return new Case { Name = "wizard-page" + page, Make = () => new WizardForm(null, true), Prepare = f => ((WizardForm)f).ForTest(page) };
            }
            yield return new Case { Name = "configure-advanced", Make = () => new ConfigureForm(null) };
            yield return new Case { Name = "find-headset", Make = () => new FindHeadsetForm(Tuple.Create("1234.client.alvr", "Apple Vision Pro", "192.168.100.123")) };
            yield return new Case { Name = "dll-offer", Make = () => new DllOfferForm("Cyberpunk 2077 (C) 2020 by CD Projekt RED - Phantom Liberty Edition") };
            yield return new Case { Name = "chroma-step", Make = () => new ChromaStepForm() };
            yield return new Case { Name = "openxr-layers", Make = () => new LayersForm() };
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string Arg(string k) { for (int i = 0; i + 1 < args.Length; i++) if (args[i] == k) return args[i + 1]; return null; }
            var outDir = Arg("--out") ?? Path.Combine(Paths.Dir, "uitest");
            Directory.CreateDirectory(outDir);
            var only = Arg("--only");
            var matrix = (Arg("--matrix") ?? "1920x1080@100,1920x1080@125,1920x1080@150,2560x1440@100,2560x1440@125,2560x1440@150,3840x2160@100,3840x2160@150,3840x2160@200").Split(',');
            int fails = 0, warns = 0, n = 0;
            foreach (var m in matrix)
            {
                var at = m.Split('@'); var wh = at[0].Split('x');
                int sw = int.Parse(wh[0]), sh = int.Parse(wh[1]), pct = int.Parse(at[1]);
                int dpi = 96 * pct / 100;
                // work area: the screen without the taskbar (48 px at 100 %)
                var work = new Size(sw, sh - 48 * pct / 100);
                foreach (var c in Cases())
                {
                    if (only != null && c.Name != only) continue;
                    Ui.DpiOverride = dpi;
                    var issues = new List<string>(); string size = "";
                    try
                    {
                        using (var f = c.Make())
                        {
                            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0); f.ShowInTaskbar = false; f.Opacity = 0; f.MaximumSize = new Size(20000, 20000);   // Windows would clamp a window to the real screen: the pretend screens are bigger   // on the primary monitor (its dpi), invisible
                            f.Show();                       // Load: the framework scales to the real dpi; Shown is posted and never runs (no message loop)
                            var pre = f.Width + "x" + f.Height + " dpi " + f.DeviceDpi;
                            c.Prepare?.Invoke(f);
                            double k = dpi / (double)f.DeviceDpi;   // the framework scaled the window to its monitor's dpi at Load (native per-monitor mode): that is the baseline
                            Emulate(f, k, k);
                            Ui.WorkAreaOverride = new Rectangle(0, 0, work.Width, work.Height);   // the windows' own fitting code, against the pretend screen
                            Ui.Refit(f);
                            f.PerformLayout();
                            var tabs = Descend(f).OfType<TabControl>().FirstOrDefault();
                            int pages = tabs != null ? tabs.TabPages.Count : 1;
                            for (int t = 0; t < pages; t++)
                            {
                                if (tabs != null) { tabs.SelectedIndex = t; f.PerformLayout(); }
                                var tag = tabs != null ? "-tab" + t : "";
                                Audit(f, work, tag, issues);
                                size = f.Width + "x" + f.Height + " [before: " + pre + "]";
                                using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(Path.Combine(outDir, $"{c.Name}{tag}_{sw}x{sh}@{pct}.png"), ImageFormat.Png); }
                            }
                        }
                    }
                    catch (Exception e) { Ui.WorkAreaOverride = null; issues.Add("FAIL crashed: " + e.GetType().Name + ": " + e.Message); }
                    Ui.WorkAreaOverride = null;
                    n++;
                    fails += issues.Count(i => i.StartsWith("FAIL")); warns += issues.Count(i => i.StartsWith("WARN"));
                    Json.Add($"{{\"form\":{Q(c.Name)},\"screen\":\"{sw}x{sh}\",\"scale\":{pct},\"work\":\"{work.Width}x{work.Height}\",\"size\":{Q(size)},\"issues\":[{string.Join(",", issues.Select(Q))}]}}");
                    Application.DoEvents();
                }
            }
            File.WriteAllText(Path.Combine(outDir, "results.json"), "[\n" + string.Join(",\n", Json) + "\n]\n");
            Console.WriteLine($"uitest: {n} renders, {fails} failures, {warns} warnings");
            return 0;
        }

        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";

        static IEnumerable<Control> Descend(Control c)
        {
            foreach (Control k in c.Controls) { yield return k; foreach (var d in Descend(k)) yield return d; }
        }

        // what the framework does at a higher dpi: sizes grow by k (target / the scale it applied), fonts by kFont (target / the dpi they are drawn at)
        static void Emulate(Form f, double kFont, double k)
        {
            if (Math.Abs(k - 1.0) < 0.01 && Math.Abs(kFont - 1.0) < 0.01) return;
            var explicitFonts = new List<KeyValuePair<Control, Font>>();
            foreach (var c in Descend(f)) if (c.Parent != null && !ReferenceEquals(c.Font, c.Parent.Font)) explicitFonts.Add(new KeyValuePair<Control, Font>(c, c.Font));
            var baseFont = f.Font;
            f.SuspendLayout();
            f.Font = new Font(baseFont.FontFamily, (float)(baseFont.Size * kFont), baseFont.Style);
            foreach (var kv in explicitFonts) kv.Key.Font = new Font(kv.Value.FontFamily, (float)(kv.Value.Size * kFont), kv.Value.Style);
            f.Scale(new SizeF((float)k, (float)k));
            f.ResumeLayout(true);
        }

        static string Desc(Control c) { var s = new StringBuilder(c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? "" : ":" + c.Name)); var t = c.Text; if (!string.IsNullOrEmpty(t) && !(c is Form)) s.Append(" '" + (t.Length > 28 ? t.Substring(0, 28) + "..." : t).Replace("\n", " ") + "'"); return s.ToString(); }

        static void Audit(Form f, Size work, string tag, List<string> issues)
        {
            void add(string level, string msg) { var m = level + " " + msg + (tag.Length > 0 ? " (" + tag.TrimStart('-') + ")" : ""); if (!issues.Contains(m)) issues.Add(m); }
            // 1. the window against the work area
            bool resizable = f.FormBorderStyle == FormBorderStyle.Sizable || f.FormBorderStyle == FormBorderStyle.SizableToolWindow;
            if (f.Width > work.Width || f.Height > work.Height)
                add(resizable && f.MinimumSize.Width <= work.Width && f.MinimumSize.Height <= work.Height ? "WARN" : "FAIL", $"window {f.Width}x{f.Height} does not fit the work area {work.Width}x{work.Height}" + (resizable ? $" (minimum {f.MinimumSize.Width}x{f.MinimumSize.Height})" : ""));
            foreach (var c in Descend(f).Where(x => x.Visible))
            {
                var p = c.Parent; if (p == null) continue;
                var scroll = p as ScrollableControl;
                bool scrolls = scroll != null && scroll.AutoScroll;
                // 2. clipped by the parent (a scrolling parent may overflow: that is what it is for, but a sideways scroll bar is a smell)
                if (!scrolls && c.Dock == DockStyle.None)
                {
                    var r = p.ClientRectangle;
                    if (c.Right > r.Right + 2 || c.Bottom > r.Bottom + 2 || c.Left < r.Left - 2 || c.Top < r.Top - 2)
                        add("FAIL", $"clipped: {Desc(c)} {c.Bounds} outside {p.GetType().Name} {r.Size}");
                }
                if (scrolls && scroll.HorizontalScroll.Visible && p == scroll) add("WARN", $"{Desc(p)} needs a horizontal scroll bar");
                // 3. text cut off
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
                if ((c is Button || c is CheckBox || c is RadioButton) && !c.AutoSize && !string.IsNullOrEmpty(c.Text))
                {
                    var w = TextRenderer.MeasureText(c.Text, c.Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding).Width;
                    if (w + 8 > c.Width) add("FAIL", $"text cut off: {Desc(c)} needs {w + 8}px, has {c.Width}");
                }
                if (c is Label lb && !lb.AutoSize && !string.IsNullOrEmpty(lb.Text))
                {
                    var one = TextRenderer.MeasureText(lb.Text, lb.Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding);
                    var need = one.Width <= lb.Width ? one : TextRenderer.MeasureText(lb.Text, lb.Font, new Size(lb.Width, 0), flags);
                    if (need.Height > lb.Height + 2) add("FAIL", $"text cut off: {Desc(lb)} needs {need.Height}px, has {lb.Height} (width {lb.Width}, text {one.Width}px, dock {lb.Dock}, font {lb.Font.SizeInPoints:0.0}pt)");
                }
                if (c is Label al && al.AutoSize && al.Right > f.ClientSize.Width + 2 && !scrolls) add("FAIL", $"label beyond the window: {Desc(al)}");
                if (c is ListView lv && lv.View == View.Details)
                {
                    int total = 0;
                    foreach (ColumnHeader h in lv.Columns)
                    {
                        total += h.Width;
                        var hw = TextRenderer.MeasureText(h.Text, lv.Font).Width + 2;
                        if (hw > h.Width) add("FAIL", $"list header cut off: {Desc(lv)} column '{h.Text}' needs {hw}px, has {h.Width}");
                    }
                    foreach (ListViewItem it in lv.Items)
                        for (int i = 0; i < it.SubItems.Count && i < lv.Columns.Count; i++)
                        {
                            var cw = TextRenderer.MeasureText(it.SubItems[i].Text, lv.Font).Width + 12;
                            if (cw > lv.Columns[i].Width + 4 && total + 8 < lv.ClientSize.Width) { add("WARN", $"list cell cut off: {Desc(lv)} column '{lv.Columns[i].Text}' '{it.SubItems[i].Text.Substring(0, Math.Min(24, it.SubItems[i].Text.Length))}' needs {cw}px, has {lv.Columns[i].Width}"); break; }
                        }
                    if (total > lv.ClientSize.Width + 4) add("WARN", $"{Desc(lv)} columns ({total}px) are wider than the list ({lv.ClientSize.Width}px): sideways scrolling");
                }
            }
            // 4. siblings that overlap (not docked, both visible)
            foreach (var p in new[] { (Control)f }.Concat(Descend(f)).Where(x => x.Controls.Count > 1 && !(x is FlowLayoutPanel) && !(x is TableLayoutPanel) && !(x is TabControl) && !(x is ListView)))
            {
                var kids = p.Controls.Cast<Control>().Where(k => k.Visible && k.Dock == DockStyle.None).ToList();
                for (int i = 0; i < kids.Count; i++)
                    for (int j = i + 1; j < kids.Count; j++)
                    {
                        var ov = Rectangle.Intersect(kids[i].Bounds, kids[j].Bounds);
                        if (ov.Width * ov.Height > 16) add("FAIL", $"overlap in {p.GetType().Name}: {Desc(kids[i])} and {Desc(kids[j])} ({ov.Width}x{ov.Height})");
                    }
            }
        }
    }
}
