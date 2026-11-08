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
        public string Id, Name, Headset = "", Source = "manual", Created = "", Note = "";
        /// the headset app's own FoV scale recommended with it (0 = none), e.g. 0.8 for the Vision Pro's window: the stream then covers less angle at the same pixel density
        public double ClientScale;
        /// outer, inner, upper, lower (tangents)
        public double[] Tan = new double[4];
        /// schema 2: each eye's own four tangents when the eyes were fitted apart (null = the eyes are mirrored); Tan stays the per-edge union, which is what a reader of schema 1 and the streamer (until the per-eye message exists) use
        public double[] TanLeft, TanRight;
        public bool PerEye => TanLeft != null && TanRight != null;
        /// schema 2: how the numbers were found: quickfit | restriction | sweep | slider | manual ("" for a file written before)
        public string Method = "";
        /// schema 2: the light seal size the wearer named ("" = not asked / not known) and the panel (outer, inner, upper, lower of the left eye, then the right eye's) the numbers were measured against (null = not recorded)
        public string LightSeal = "";
        public double[] DeclaredAtMeasure;
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
            if (Note.Length > 0) d["note"] = Note;
            if (ClientScale > 0) d["client_scale"] = Math.Round(ClientScale, 2);
            if (PerEye) { d["tan_left"] = TanLeft.Select(v => (object)Math.Round(v, 4)).ToList(); d["tan_right"] = TanRight.Select(v => (object)Math.Round(v, 4)).ToList(); }
            if (Method.Length > 0) d["method"] = Method;
            if (LightSeal.Length > 0) d["light_seal"] = LightSeal;
            if (DeclaredAtMeasure != null) d["declared_at_measure"] = DeclaredAtMeasure.Select(v => (object)Math.Round(v, 4)).ToList();
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
                    Created = Json.Str(d, "created", ""), Note = Json.Str(d, "note", ""), ClientScale = Math.Max(0, Json.Num(d, "client_scale", 0)), Quality = Math.Max(0, Math.Min(1, Json.Num(d, "render_quality", 0))),
                    Tan = Enumerable.Range(0, 4).Select(i => Convert.ToDouble(t[i], CultureInfo.InvariantCulture)).ToArray(),
                };
                u.Method = Json.Str(d, "method", "");
                u.LightSeal = Json.Str(d, "light_seal", "");
                if (Json.Get(d, "declared_at_measure") is System.Collections.IList dm && dm.Count >= 8) u.DeclaredAtMeasure = Enumerable.Range(0, 8).Select(i => Convert.ToDouble(dm[i], CultureInfo.InvariantCulture)).ToArray();
                if (Json.Get(d, "tan_left") is System.Collections.IList tl && tl.Count >= 4 && Json.Get(d, "tan_right") is System.Collections.IList tr && tr.Count >= 4)
                {
                    double[] Four(System.Collections.IList l) => Enumerable.Range(0, 4).Select(i => Convert.ToDouble(l[i], CultureInfo.InvariantCulture)).ToArray();
                    var left = Four(tl); var right = Four(tr);
                    if (left.Concat(right).All(v => !double.IsNaN(v) && v > 0)) { u.TanLeft = left; u.TanRight = right; }
                }
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

        public static void Save(IEnumerable<UserFov> all, string path = null) => Save(all, Seeded(path), path);

        static void Save(IEnumerable<UserFov> all, List<string> seeded, string path)
        {
            var root = new Dictionary<string, object> { ["version"] = 2.0, ["profiles"] = all.Select(u => (object)u.ToJson()).ToList() };
            if (seeded.Count > 0) root["seeded"] = seeded.Select(x => (object)x).ToList();
            Json.Save(path ?? FilePath, root);
        }

        /// The ids of the shipped profiles already copied in: a profile the user deleted is not copied again.
        public static List<string> Seeded(string path = null)
        {
            var l = Json.Get(Json.Load(path ?? FilePath), "seeded") as System.Collections.IList;
            return l == null ? new List<string>() : l.OfType<string>().ToList();
        }

        /// The shipped profiles (profiles\*.profile.json) that carry a measured field of view and fit the connected headset by name (the Vision Pro's standard calibration) are copied
        /// into the user's file once, so the list has something to choose from and every choice is one of the user's own (deletable). Returns how many were copied.
        public static int SeedFromShipped(string headsetName, string path = null, string profilesDir = null)
        {
            headsetName = headsetName ?? "";
            int added = 0;
            try
            {
                var dir = profilesDir ?? Path.Combine(Paths.Dir, "profiles");
                if (!Directory.Exists(dir)) return 0;
                var all = Load(path); var seeded = Seeded(path);
                foreach (var f in Directory.GetFiles(dir, "*.profile.json").OrderBy(x => x))
                {
                    var d = Json.Load(f);
                    var id = Json.Str(d, "id", "");
                    var fov = Json.Get(d, "fov") as Dictionary<string, object>;
                    var vis = fov == null ? null : Json.Get(fov, "visible_deg") as Dictionary<string, object>;
                    var names = (Json.Get(d, "match.name_contains") as System.Collections.IList)?.OfType<string>().ToList();
                    if (id.Length == 0 || vis == null || names == null || seeded.Contains(id)) continue;
                    // the Vision Pro's "AVP optimized" window is offered to everybody (a user can pick it before a headset was ever seen); the other shipped windows follow the headset's name
                    if (id != "apple-vision-pro" && !names.Any(n => headsetName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    var margin = Math.Max(0, Math.Min(15, Json.Num(fov, "margin_deg", 3)));
                    var scale = Json.Num(fov, "client_fov_scale", 0);
                    var tan = new[] { "outer", "inner", "upper", "lower" }.Select(k => Math.Round(Math.Tan((Json.Num(vis, k, 0) + margin) * Math.PI / 180), 2)).ToArray();
                    if (tan.Any(v => !(v > 0))) continue;
                    var standard = Json.Str(fov, "source", "standard");
                    all.RemoveAll(x => x.Id == "std-" + id);
                    all.Add(new UserFov
                    {
                        Id = "std-" + id, Name = Json.Str(fov, "name", "") is string given && given.Length > 0 ? given : standard == "author" ? "AVP optimized" : Json.Str(d, "display_name", id).Split('(')[0].Trim() + (standard == "starting-point" ? " (starting point, not measured)" : " (standard calibration)"), Headset = names[0], Source = standard,
                        Created = "2026-01-01T00:00:00", Tan = tan, Quality = Math.Max(0, Math.Min(1, Json.Num(fov, "render_quality", 0))), ClientScale = scale >= 0.2 && scale <= 1.6 && Math.Abs(scale - 1) > 0.001 ? scale : 0, Note = Json.Str(fov, "note", ""),
                    });
                    seeded.Add(id); added++;
                }
                if (added > 0) Save(all.OrderByDescending(u => u.Created, StringComparer.Ordinal), seeded, path);
            }
            catch (Exception e) { Log.Write("WARN", "fov profiles: seeding failed: " + e.Message); }
            return added;
        }

        /// A restriction saved before the store existed (tangents in airframexr.json, no profile in the list): kept as "My earlier values" so nothing is lost. True when one was added.
        public static bool KeepCurrentValues(AppSettings st, string path = null)
        {
            var cur = new[] { st.FovOuter, st.FovInner, st.FovUpper, st.FovLower };
            if (!st.FovOverride || cur.Any(v => !(v > 0))) return false;
            var all = Load(path);
            if (all.Any(u => Enumerable.Range(0, 4).All(k => Math.Abs(u.Tan[k] - cur[k]) < 0.006))) return false;
            Add(new UserFov { Id = NewId(), Name = "My earlier values", Headset = st.FovForHeadset.Length > 0 ? st.FovForHeadset : st.HeadsetName, Source = "manual", Tan = cur.Select(v => Math.Round(v, 4)).ToArray(), Quality = st.FovRenderQuality }, path);
            return true;
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

        /// Writes the profile into the settings the streamer reads (video.fov_override on, the four tangents never above the headset's own, which profile it was, the headset it was made for):
        /// the profile list itself is never read by the streamer, a choice is a copy. The caller saves. client = the headset's tangents (null = not seen yet).
        public static void Apply(UserFov u, AppSettings st, double[] client, string headset)
        {
            var keys = new[] { "video.fov_outer", "video.fov_inner", "video.fov_upper", "video.fov_lower" };
            // one box for both eyes, mirrored (Tan is the larger value of the two eyes when the tool measured them apart): the engine tells the game the same box in both eyes
            for (int k = 0; k < 4; k++) Json.Set(st.Root, keys[k], Math.Round(client != null ? Math.Min(u.Tan[k], client[k]) : u.Tan[k], 4));
            Json.Remove(st.Root, "video.fov_override_right");
            Json.Set(st.Root, "video.fov_override", true);
            Json.Set(st.Root, "video.fov_profile", u.Id);
            if (client != null) Json.Set(st.Root, "video.fov_for", client.Select(x => (object)Math.Round(x, 4)).ToList());   // the headset these values were made for (they are absolute tangents)
            if (!string.IsNullOrEmpty(headset)) Json.Set(st.Root, "video.fov_for_headset", headset);
        }

        /// The UI harness and the README screenshots: three made-up profiles (a calibration with its window, two typed ones) in a temporary file, never a real person's.
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

        public static string NewId() => "u" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        /// A box the probe's tool scenes produced (one per eye: outer, inner, upper, lower tangents) as a stored profile: method "quickfit" (the black-bar fit) or "restriction" (what the game renders).
        /// Tan is the per-edge union of the two eyes, which is what schema 1 readers and the streamer's mirrored fields take; TanLeft / TanRight carry the eyes apart.
        public static UserFov FromTool(string method, double[][] box, double[][] panel, string headset, string seal)
        {
            var u = new UserFov
            {
                Id = NewId(), Headset = headset ?? "", Source = method == "quickfit" ? "calibrated" : "manual", Method = method, LightSeal = seal ?? "", Quality = 0,
                Name = (method == "quickfit" ? "Fit " : "Restriction ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                TanLeft = box[0].Select(v => Math.Round(v, 4)).ToArray(), TanRight = box[1].Select(v => Math.Round(v, 4)).ToArray(),
            };
            u.Tan = Enumerable.Range(0, 4).Select(k => Math.Round(Math.Max(box[0][k], box[1][k]), 4)).ToArray();
            if (panel != null) u.DeclaredAtMeasure = panel[0].Concat(panel[1]).ToArray();
            return u;
        }

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

            c.TotalH = c.Deg[0] + c.Deg[1]; c.TotalV = c.Deg[2] + c.Deg[3];
            if (declared != null && declared.Length == 4)
            {
                // Above what the headset app declared at its last connection: not an error. A calibration measures the wearer, not the app; the streamer gives
                // a game at most the declared box per edge (vision::fov_scale_from), so the extra is unused until the app declares more. The Vision Pro apps
                // 1.2.0 to 1.3.1 declared a fixed box (about 1.04 / 0.79 / 0.76 / 0.87) that cut wearers who see more; 1.3.2 declares the whole panel.
                var over = Enumerable.Range(0, 4).Where(k => c.Tan[k] > declared[k] * OverDeclaredTolerance).ToList();
                if (over.Count > 0)
                    c.Yellow(string.Join(", ", over.Select(k => Names[k] + " " + F(c.Tan[k]))) + (over.Count == 1 ? " is" : " are") + " more than the headset app declared at its last connection ("
                             + string.Join(", ", over.Select(k => F(declared[k]) + " = " + F(Math.Atan(declared[k]) * 180 / Math.PI) + "°")) + "). Games get at most that until the app declares more:"
                             + " with an older AirFrameXR Vision Pro app (1.2.0 to 1.3.1) update it to 1.3.2 or later and connect once; the values are kept as they are.");
                // the share of the declared box a game renders: each edge counts at most the declared one (what the streamer gives)
                double E(int k) => Math.Min(c.Tan[k], declared[k]);
                c.AreaShare = (E(0) + E(1)) * (E(2) + E(3)) / ((declared[0] + declared[1]) * (declared[2] + declared[3]));
            }

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
