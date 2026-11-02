// The calibration maths and the profile file (docs/FOV_CALIBRATION.md), shared by calibrate.exe (the launcher's tool) and fov_profile.exe (the console version in the
// calibration bundle). Input: sweep CSVs written by xr_probe's sweep mode (fov_sweep_<name>_<headset>_run<N>.csv). Output: a profile file with a measured field of view
// that Import profile... / Import a calibration file... accept.
//
// The visible window is the outline of the points where the dot vanished, per direction, taking the largest over every run, both eyes and both gaze passes (the game is
// played with the eyes forward and roaming, and the override is one mirrored rectangle for both eyes). The rectangle is the smallest one that still covers the requested
// share of that outline. Tangent space throughout: x outward from the nose, y up.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace AirFrameXR
{
    public record struct Rect(double Outer, double Inner, double Upper, double Lower)
    {
        public double Area => (Outer + Inner) * (Upper + Lower);
    }

    public class FovFitResult
    {
        public Rect Rectangle, Box;
        public double Coverage;                 // 0..1, exact share of the window the rectangle covers
        public int Runs, Steps;
        public string Author, Headset, FileName;
        public List<string> Warnings = new List<string>();
        public Dictionary<string, object> Profile;
        public string Json => Pretty(new JavaScriptSerializer().Serialize(Profile));
        public double SavedVsBox => 1 - Rectangle.Area / Box.Area;

        static string Pretty(string json)
        {
            var sb = new System.Text.StringBuilder(); int depth = 0; bool str = false;
            foreach (char c in json)
            {
                if (c == '"' && (sb.Length == 0 || sb[sb.Length - 1] != '\\')) str = !str;
                if (str) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[': sb.Append(c).Append('\n').Append(' ', 2 * ++depth); break;
                    case '}': case ']': sb.Append('\n').Append(' ', 2 * --depth).Append(c); break;
                    case ',': sb.Append(c).Append('\n').Append(' ', 2 * depth); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('\n').ToString();
        }
    }

    public static class FovFit
    {
        record struct Pt(double X, double Y);
        class Row { public int Eye; public string Fit, Gaze; public double Theta, Ecc; }

        const double MaxEccDeg = 60;      // anything beyond is a stray click, not an edge
        const double StepTan = 0.005;     // resolution of the search, about 0.3 degree

        static readonly (string Key, string[] Names, string Display)[] Known =
        {
            ("visionpro", new[] { "Apple Headset", "Apple Vision Pro" }, "Apple Vision Pro"),
            ("quest3s", new[] { "Quest 3S" }, "Meta Quest 3S"),
            ("quest3", new[] { "Quest 3" }, "Meta Quest 3"),
            ("questpro", new[] { "Quest Pro" }, "Meta Quest Pro"),
            ("quest2", new[] { "Quest 2" }, "Meta Quest 2"),
            ("pico", new[] { "Pico" }, "Pico"),
        };

        /// Reads the sweep files and fits the rectangle. null + problem: the data cannot give a trustworthy window (nothing is written then).
        /// match: the headset name fragment the profile applies to (null: from the files' headset label, for the headsets the tool knows); author: the wearer (null: from the file name).
        public static FovFitResult Fit(IList<string> files, double coveragePct, string match, string author, out string problem)
        {
            problem = null;
            var rows = files.SelectMany(ReadRows).ToList();
            var r = new FovFitResult { Runs = files.Count, Steps = rows.Count };
            var parts = Path.GetFileNameWithoutExtension(files[0]).Split('_');
            r.Author = author ?? (parts.Length == 5 && parts[1] == "sweep" ? parts[2] : "user");
            string fit = rows.Select(x => x.Fit).FirstOrDefault() ?? "headset";
            int cut = fit.LastIndexOf('_');
            r.Headset = cut > 0 && fit.Skip(cut + 1).All(char.IsDigit) ? fit.Substring(0, cut) : fit;
            if (!Check(rows, files.Count, r.Warnings, out problem)) return null;

            var window = Outline(rows);
            r.Box = BoundingBox(window);
            r.Rectangle = SmallestRect(window, coveragePct / 100);
            r.Coverage = Clipped(window, r.Rectangle) / Area(window);
            var rect = r.Rectangle;
            var deg = new[] { rect.Outer, rect.Inner, rect.Upper, rect.Lower }.Select(t => Math.Round(Math.Atan(t) * 180 / Math.PI, 2)).ToArray();

            var key = r.Headset.ToLowerInvariant().Replace(" ", "");
            var known = Known.Where(k => key.StartsWith(k.Key)).Select(k => ((string[] Names, string Display)?)(k.Names, k.Display)).FirstOrDefault();
            var names = match != null ? new[] { match } : known?.Names ?? new[] { r.Headset };
            var display = match ?? known?.Display ?? r.Headset;
            if (match == null && known == null) r.Warnings.Add($"\"{r.Headset}\" is not a headset this tool knows: the profile applies to a headset whose name contains that text.");

            r.FileName = Slug($"airframexr_fov_{r.Author}_{r.Headset}") + ".profile.json";
            r.Profile = new Dictionary<string, object>
            {
                ["schema"] = 1,
                ["id"] = Slug($"fov-{r.Headset}-{r.Author}"),
                ["display_name"] = $"{display} field of view ({r.Author}'s calibration)",
                ["author"] = r.Author,
                ["match"] = new Dictionary<string, object> { ["name_contains"] = names },
                ["fov"] = new Dictionary<string, object>
                {
                    ["visible_deg"] = new Dictionary<string, object> { ["outer"] = deg[0], ["inner"] = deg[1], ["upper"] = deg[2], ["lower"] = deg[3] },
                    ["margin_deg"] = 0,
                    ["render_quality"] = 0,
                    ["note"] = $"Measured by {r.Author} on {display}: {r.Runs} run(s), {r.Steps} steps, {DateTime.Now:yyyy-MM-dd}. The smallest rectangle covering {coveragePct:0.#} % of the visible window "
                             + $"({100 * r.Coverage:0.0} % exactly); the window's own bounding box is {r.Box.Outer:0.00} / {r.Box.Inner:0.00} / {r.Box.Upper:0.00} / {r.Box.Lower:0.00}. Tangents outer / inner / upper / lower: "
                             + $"{rect.Outer:0.00} / {rect.Inner:0.00} / {rect.Upper:0.00} / {rect.Lower:0.00}. Check the corners in a game; if black shows at an edge, calibrate again at a higher coverage.",
                },
            };
            return r;
        }

        public static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '-').ToArray());

        static IEnumerable<Row> ReadRows(string path)
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2) yield break;
            var h = lines[0].Split(',').Select((n, i) => (n.Trim(), i)).ToDictionary(x => x.Item1, x => x.Item2);
            foreach (var key in new[] { "eye", "fit", "gaze", "theta_deg", "ecc_deg", "timeout" })
                if (!h.ContainsKey(key)) throw new InvalidDataException($"{Path.GetFileName(path)} is not a sweep file (no column \"{key}\").");
            foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
            {
                var c = line.Split(',');
                double ecc = double.Parse(c[h["ecc_deg"]], CultureInfo.InvariantCulture);
                if (c[h["timeout"]] == "1" || ecc > MaxEccDeg || ecc <= 0) continue;   // no click, or a stray one
                yield return new Row
                {
                    Eye = int.Parse(c[h["eye"]]), Fit = c[h["fit"]], Gaze = c[h["gaze"]],
                    Theta = double.Parse(c[h["theta_deg"]], CultureInfo.InvariantCulture), Ecc = ecc,
                };
            }
        }

        /// Stops on data that cannot give a trustworthy window; warns on data that can but may be rough.
        static bool Check(List<Row> rows, int files, List<string> warnings, out string problem)
        {
            problem = null;
            foreach (var eye in new[] { 0, 1 })
            {
                int rays = rows.Where(r => r.Eye == eye).Select(r => r.Theta).Distinct().Count();
                if (rays < 20) { problem = $"only {rays} of 24 directions have a click for the {(eye == 0 ? "left" : "right")} eye: run the calibration again, all the way to the end."; return false; }
            }
            var gazes = rows.Select(r => r.Gaze).Distinct().ToList();
            if (gazes.Count < 2) warnings.Add($"Only the \"{gazes.FirstOrDefault()}\" pass is present. Both passes (eyes following, then eyes fixed) matter: a single pass tends to give a window that is too small.");
            if (files < 2) warnings.Add("One run only. Two or three runs (headset off and on between them) show how precise the result is.");
            // two clicks for the same direction and eye that are far apart mean one of them was early or late
            var wide = rows.GroupBy(r => (r.Eye, r.Theta, r.Gaze)).Where(g => g.Max(r => r.Ecc) - g.Min(r => r.Ecc) > 6).ToList();
            if (wide.Count > 4) warnings.Add($"{wide.Count} directions differ by more than 6 degrees between runs. The largest value is kept; the result is on the generous side.");
            return true;
        }

        // ---------------------------------------------------------------- geometry

        /// The visible window: for each direction the largest distance seen over all runs, eyes and gaze passes, as a polygon in tangent space.
        static List<Pt> Outline(List<Row> rows) =>
            rows.GroupBy(r => Math.Round(r.Theta, 1)).OrderBy(g => g.Key)
                .Select(g => { double a = g.Key * Math.PI / 180, t = Math.Tan(g.Max(r => r.Ecc) * Math.PI / 180); return new Pt(t * Math.Cos(a), t * Math.Sin(a)); })
                .ToList();

        static double Area(IReadOnlyList<Pt> poly)
        {
            double s = 0;
            for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; s += a.X * b.Y - b.X * a.Y; }
            return Math.Abs(s) / 2;
        }

        /// Sutherland-Hodgman against the four sides of the rectangle: exact area of window and rectangle together.
        static double Clipped(List<Pt> poly, Rect r)
        {
            Func<Pt, double>[] sides = { p => r.Outer - p.X, p => p.X + r.Inner, p => r.Upper - p.Y, p => p.Y + r.Lower };   // >= 0 inside
            IReadOnlyList<Pt> cur = poly;
            foreach (var dist in sides)
            {
                var next = new List<Pt>();
                for (int i = 0; i < cur.Count; i++)
                {
                    Pt a = cur[i], b = cur[(i + 1) % cur.Count];
                    double da = dist(a), db = dist(b);
                    if (da >= 0) next.Add(a);
                    if ((da >= 0) != (db >= 0)) { double t = da / (da - db); next.Add(new Pt(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y))); }
                }
                cur = next;
                if (cur.Count < 3) return 0;
            }
            return Area(cur);
        }

        static double CoveredShare(List<Pt> window, Rect r) => Clipped(window, r) / Area(window);

        static Rect BoundingBox(List<Pt> w) => new Rect(w.Max(p => p.X), -w.Min(p => p.X), w.Max(p => p.Y), -w.Min(p => p.Y));

        /// Starts from the bounding box (100 %) and keeps pulling in whichever edge costs the least coverage per step, while the target still holds.
        static Rect SmallestRect(List<Pt> window, double target)
        {
            var r = BoundingBox(window);
            while (true)
            {
                Rect? best = null; double bestCover = -1;
                foreach (var step in new Func<Rect, Rect>[]
                {
                    x => x with { Outer = x.Outer - StepTan }, x => x with { Inner = x.Inner - StepTan },
                    x => x with { Upper = x.Upper - StepTan }, x => x with { Lower = x.Lower - StepTan },
                })
                {
                    var c = step(r);
                    if (c.Outer <= 0.1 || c.Inner <= 0.1 || c.Upper <= 0.1 || c.Lower <= 0.1) continue;
                    double cov = CoveredShare(window, c);
                    if (cov >= target && cov > bestCover) { best = c; bestCover = cov; }
                }
                if (best is null) return r;
                r = best.Value;
            }
        }
    }
}
