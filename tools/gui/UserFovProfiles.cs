// The user's own field-of-view profiles and the check of four numbers typed by hand (docs/FOV_CALIBRATION.md, docs/HMD_PROFILES.md).
//   config\user_fov_profiles.json: every calibration the user kept and every valid set of numbers entered by hand, one file apart from the settings so an
//   upgrade (which replaces the shipped files, not config\) keeps them. The values are tangents per edge (outer = away from the nose, inner, upper, lower),
//   the same four numbers as video.fov_outer / inner / upper / lower.
//   FovValidator: red = the numbers cannot be a field of view, yellow = they can but look strange, green = nothing to say.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AirFrameXR
{
    public class UserFov
    {
        public string Id, Name, Headset = "", Source = "manual", Created = "";
        /// outer, inner, upper, lower (tangents)
        public double[] Tan = new double[4];
        /// the game render quality that goes with it (0..1, video.fov_render_quality)
        public double Quality;
        /// the measured window of a calibration (tangent space, x outward from the nose, y up); empty for numbers typed by hand
        public List<double[]> Window = new List<double[]>();
        public bool Calibrated => Source == "calibrated";

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = Id, ["name"] = Name, ["headset"] = Headset, ["source"] = Source, ["created"] = Created,
                ["tan"] = Tan.Select(v => (object)Math.Round(v, 4)).ToList(),
                ["render_quality"] = Math.Round(Quality, 2),
            };
            if (Window.Count > 0) d["window"] = Window.Select(p => (object)new List<object> { Math.Round(p[0], 4), Math.Round(p[1], 4) }).ToList();
            return d;
        }

        public static UserFov FromJson(Dictionary<string, object> d)
        {
            try
            {
                var t = Json.Get(d, "tan") as System.Collections.IList;
                if (t == null || t.Count < 4) return null;
                var u = new UserFov
                {
                    Id = Json.Str(d, "id", ""), Name = Json.Str(d, "name", ""), Headset = Json.Str(d, "headset", ""), Source = Json.Str(d, "source", "manual"),
                    Created = Json.Str(d, "created", ""), Quality = Math.Max(0, Math.Min(1, Json.Num(d, "render_quality", 0))),
                    Tan = Enumerable.Range(0, 4).Select(i => Convert.ToDouble(t[i], CultureInfo.InvariantCulture)).ToArray(),
                };
                if (u.Id.Length == 0 || u.Tan.Any(v => double.IsNaN(v) || !(v > 0))) return null;
                if (u.Name.Length == 0) u.Name = u.Id;
                if (Json.Get(d, "window") is System.Collections.IList w)
                    foreach (var p in w.OfType<System.Collections.IList>().Where(p => p.Count >= 2))
                        u.Window.Add(new[] { Convert.ToDouble(p[0], CultureInfo.InvariantCulture), Convert.ToDouble(p[1], CultureInfo.InvariantCulture) });
                return u;
            }
            catch { return null; }
        }
    }

    public static class UserFovStore
    {
        public static string FilePath => Path.Combine(Paths.Config, "user_fov_profiles.json");

        /// Newest first. A file that cannot be read gives an empty list (Json.Load logs it) and is never overwritten by a read.
        public static List<UserFov> Load(string path = null)
        {
            var root = Json.Load(path ?? FilePath);
            var list = Json.Get(root, "profiles") as System.Collections.IList;
            var all = new List<UserFov>();
            if (list != null) foreach (var o in list.OfType<Dictionary<string, object>>()) { var u = UserFov.FromJson(o); if (u != null) all.Add(u); }
            return all.OrderByDescending(u => u.Created, StringComparer.Ordinal).ToList();
        }

        public static void Save(IEnumerable<UserFov> all, string path = null)
        {
            var root = new Dictionary<string, object> { ["version"] = 1.0, ["profiles"] = all.Select(u => (object)u.ToJson()).ToList() };
            Json.Save(path ?? FilePath, root);
        }

        /// Adds the profile (an id that exists is replaced, a name that exists gets " (2)", " (3)"...). Returns the stored profile.
        public static UserFov Add(UserFov p, string path = null)
        {
            var all = Load(path);
            all.RemoveAll(x => x.Id == p.Id);
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Profile";
            var baseName = p.Name.Trim(); int n = 1;
            while (all.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))) p.Name = baseName + " (" + (++n) + ")";
            if (p.Created.Length == 0) p.Created = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            all.Insert(0, p);
            Save(all, path);
            return p;
        }

        public static bool Remove(string id, string path = null)
        {
            var all = Load(path);
            if (all.RemoveAll(x => x.Id == id) == 0) return false;
            Save(all, path);
            return true;
        }

        public static bool Rename(string id, string name, string path = null)
        {
            var all = Load(path); var p = all.FirstOrDefault(x => x.Id == id);
            if (p == null || string.IsNullOrWhiteSpace(name)) return false;
            p.Name = name.Trim(); Save(all, path);
            return true;
        }

        public static string NewId() => "u" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        /// A calibration result (FovFitResult.Profile's rectangle and window) as a stored profile.
        public static UserFov FromCalibration(FovFitResult r, string headset, string name = null)
        {
            var u = new UserFov
            {
                Id = NewId(), Headset = headset ?? "", Source = "calibrated", Quality = 0,
                Name = name ?? ("Calibration " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Tan = new[] { r.Rectangle.Outer, r.Rectangle.Inner, r.Rectangle.Upper, r.Rectangle.Lower },
            };
            foreach (var w in r.Window) u.Window.Add(new[] { w.X, w.Y });
            return u;
        }
    }

    public enum FovLevel { Valid, Strange, Invalid }
    public enum FovUnit { Tangent, Degrees }

    public class FovCheck
    {
        public FovLevel Level = FovLevel.Valid;
        public List<string> Messages = new List<string>();
        /// the four values as tangents (NaN where they could not be read), and as degrees from the view axis
        public double[] Tan = { double.NaN, double.NaN, double.NaN, double.NaN };
        public double[] Deg = { double.NaN, double.NaN, double.NaN, double.NaN };
        public double TotalH = double.NaN, TotalV = double.NaN;
        /// the rectangle's area over the headset's declared one (what the game renders, before the quality slider); NaN = no declared box known
        public double AreaShare = double.NaN;
        public bool Usable => Level != FovLevel.Invalid;
        public string Summary => Messages.Count == 0 ? "" : string.Join(" ", Messages);
        void Add(FovLevel l, string m) { if (l > Level) Level = l; Messages.Add(m); }

        public void Red(string m) => Add(FovLevel.Invalid, m);
        public void Yellow(string m) => Add(FovLevel.Strange, m);
    }

    public static class FovValidator
    {
        static readonly string[] Names = { "Outer", "Inner", "Upper", "Lower" };
        // Thresholds for "valid but strange". They are judgement, not measurement: the values we have are the Vision Pro fit (outer / inner 1.24, upper / lower 1.13) and the
        // Quest 3's declared box (outer / inner 1.35, upper / lower 0.80). Tune them when more calibrations are in.
        const double MaxSideRatio = 1.8, MaxVertRatio = 2.0, MinTotalDeg = 40, MaxTotalDeg = 130, MinAreaShare = 0.5, LooksLikeDegrees = 3.0, OverDeclaredTolerance = 1.005;

        /// text -> number (the invariant point or the local comma); NaN when it is not one
        public static double Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return double.NaN;
            s = s.Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsInfinity(v) ? v : double.NaN;
        }

        /// values: outer, inner, upper, lower, as typed (NaN = not a number); declared: the headset's own tangents in the same order, or null when no headset was seen.
        public static FovCheck Check(double[] values, FovUnit unit, double[] declared)
        {
            var c = new FovCheck();
            for (int k = 0; k < 4; k++)
            {
                var v = values[k];
                if (double.IsNaN(v)) { c.Red(Names[k] + " is not a number."); continue; }
                if (!(v > 0)) { c.Red(Names[k] + " must be above 0."); continue; }
                if (unit == FovUnit.Degrees)
                {
                    if (v >= 90) { c.Red(Names[k] + " " + F(v) + "° is not possible: an edge must be under 90° (its tangent does not exist)."); continue; }
                    c.Deg[k] = v; c.Tan[k] = Math.Tan(v * Math.PI / 180);
                }
                else { c.Tan[k] = v; c.Deg[k] = Math.Atan(v) * 180 / Math.PI; }
            }
            if (c.Level == FovLevel.Invalid) return c;

            if (declared != null && declared.Length == 4)
                for (int k = 0; k < 4; k++)
                    if (c.Tan[k] > declared[k] * OverDeclaredTolerance)
                        c.Red(Names[k] + " " + F(c.Tan[k]) + " is more than the headset shows (" + F(declared[k]) + ", " + F(Math.Atan(declared[k]) * 180 / Math.PI) + "°): a game cannot be given more than that.");
            c.TotalH = c.Deg[0] + c.Deg[1]; c.TotalV = c.Deg[2] + c.Deg[3];
            if (declared != null && declared.Length == 4) c.AreaShare = (c.Tan[0] + c.Tan[1]) * (c.Tan[2] + c.Tan[3]) / ((declared[0] + declared[1]) * (declared[2] + declared[3]));
            if (c.Level == FovLevel.Invalid) return c;

            if (unit == FovUnit.Tangent && c.Tan.Any(t => t > LooksLikeDegrees)) c.Yellow("A value above " + F(LooksLikeDegrees) + " is a tangent of more than 71°: did you type degrees?");
            if (Ratio(c.Tan[0], c.Tan[1]) > MaxSideRatio) c.Yellow("Outer and inner differ a lot (" + F(c.Tan[0]) + " against " + F(c.Tan[1]) + "); the nose side is usually the smaller, but not by that much.");
            if (Ratio(c.Tan[2], c.Tan[3]) > MaxVertRatio) c.Yellow("Upper and lower differ a lot (" + F(c.Tan[2]) + " against " + F(c.Tan[3]) + ").");
            if (c.TotalH < MinTotalDeg || c.TotalV < MinTotalDeg) c.Yellow("A total of " + c.TotalH.ToString("0", CultureInfo.InvariantCulture) + " x " + c.TotalV.ToString("0", CultureInfo.InvariantCulture) + "° is a very small window.");
            if (c.TotalH > MaxTotalDeg || c.TotalV > MaxTotalDeg) c.Yellow("A total of " + c.TotalH.ToString("0", CultureInfo.InvariantCulture) + " x " + c.TotalV.ToString("0", CultureInfo.InvariantCulture) + "° is wider than any headset shows.");
            if (!double.IsNaN(c.AreaShare) && c.AreaShare < MinAreaShare) c.Yellow("This is " + (100 * c.AreaShare).ToString("0", CultureInfo.InvariantCulture) + " % of what the headset shows: expect black at the edges.");
            return c;
        }

        static double Ratio(double a, double b) => Math.Max(a, b) / Math.Min(a, b);
        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
