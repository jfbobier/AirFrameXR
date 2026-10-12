// Headset profiles (docs/HMD_PROFILES.md): one small JSON file per headset, shared by users among themselves (Discord), never downloaded by the
// program. The handshake identifies the headset, a matching profile is the starting configuration of the configure wizard (the wizard then verifies
// it on this PC instead of searching for one). Files are read from <install>\profiles (shipped examples) and <install>\config\profiles (the user's).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VisionALVR
{
    public class NetworkVariant { public string Id = "", Label = ""; public double Mbps; }

    public class HmdProfile
    {
        public string Id = "", DisplayName = "", Source = "";
        public string[] NameContains = new string[0];
        public int[] ViewRes;                       // default_view_resolution to match, null = any
        public double[] RatesInclude = new double[0];
        public int EyeW = 3552, EyeH = 3200, BitDepth = 10, SplitEncode = 3;
        public double Fps = 90, Gamma = 1.0;
        public string Codec = "hevc", Protocol = "udp";
        public bool FullRange = true, Foveated;
        public string DefaultVariant = "";
        public List<NetworkVariant> Variants = new List<NetworkVariant>();
        public List<string> Notes = new List<string>(), Verified = new List<string>();
        public string Author = "";
        /// ALVR session settings the profile sets (dotted paths into config\session.json -> value), applied when the wizard saves the configuration
        public Dictionary<string, object> SessionOverrides = new Dictionary<string, object>();

        public NetworkVariant Variant(string id)
        {
            var v = Variants.FirstOrDefault(x => x.Id == (string.IsNullOrEmpty(id) ? DefaultVariant : id));
            return v ?? Variants.FirstOrDefault() ?? new NetworkVariant { Id = "default", Label = "default", Mbps = 150 };
        }
    }

    public static class HmdProfiles
    {
        public static string ShippedDir => Path.Combine(Paths.Dir, "profiles");
        public static string UserDir => Path.Combine(Paths.Config, "profiles");

        static double D(object o) { try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; } }
        static string S(Dictionary<string, object> d, string k, string def = "") => d != null && d.TryGetValue(k, out var v) && v is string s ? s : def;
        static bool B(Dictionary<string, object> d, string k, bool def) => d != null && d.TryGetValue(k, out var v) && v is bool b ? b : def;
        static Dictionary<string, object> O(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        static object[] A(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is object[] a ? a : new object[0];

        /// Parses one profile file; unknown keys are ignored, an entry without id or stream values is skipped (never throws).
        public static List<HmdProfile> Parse(string json, string source)
        {
            var list = new List<HmdProfile>();
            Dictionary<string, object> root;
            try { root = Json.Parse(json); } catch (Exception e) { Log.Write("WARN", $"profile {source}: {e.Message}"); return list; }
            // a file is one profile, or {"profiles": [...]}
            var items = root.ContainsKey("profiles") ? A(root, "profiles") : new object[] { root };
            foreach (var item in items)
            {
                var e = item as Dictionary<string, object>;
                var st = O(e, "stream");
                if (e == null || st == null || S(e, "id") == "") continue;
                if (S(e, "id") == "my-headset-example") continue;   // the template
                var p = new HmdProfile { Id = S(e, "id"), DisplayName = S(e, "display_name", S(e, "id")), Source = source, Author = S(e, "author") };
                var so = O(e, "session_overrides");
                if (so != null) foreach (var kv in so) p.SessionOverrides[kv.Key] = kv.Value;
                var m = O(e, "match");
                p.NameContains = A(m, "name_contains").OfType<string>().ToArray();
                var vr = A(m, "default_view_resolution");
                if (vr.Length >= 2) p.ViewRes = new[] { (int)D(vr[0]), (int)D(vr[1]) };
                p.RatesInclude = A(m, "supported_refresh_rates_include").Select(D).ToArray();
                var er = A(st, "eye_resolution");
                if (er.Length >= 2) { p.EyeW = (int)D(er[0]); p.EyeH = (int)D(er[1]); }
                p.Fps = D(st.TryGetValue("fps", out var f) ? f : 90); if (p.Fps < 1) p.Fps = 90;
                p.Codec = S(st, "codec", "hevc"); p.Protocol = S(st, "protocol", "udp");
                p.BitDepth = st.TryGetValue("bit_depth", out var bd) ? (int)D(bd) : 10;
                p.SplitEncode = st.TryGetValue("split_encode", out var se) ? (int)D(se) : 3;
                p.Gamma = st.TryGetValue("encoding_gamma", out var g) ? D(g) : 1.0;
                p.FullRange = B(st, "full_range", true); p.Foveated = B(st, "foveated", false);
                var net = O(e, "network");
                p.DefaultVariant = S(net, "default");
                foreach (var v in A(net, "variants").OfType<Dictionary<string, object>>())
                    p.Variants.Add(new NetworkVariant { Id = S(v, "id"), Label = S(v, "label"), Mbps = v.TryGetValue("bitrate_mbps", out var mb) ? D(mb) : 0 });
                p.Notes = A(e, "notes").OfType<string>().ToList();
                foreach (var v in A(e, "verified").OfType<Dictionary<string, object>>())
                    p.Verified.Add($"{S(v, "gpu")}, {S(v, "client")}, {S(v, "date")}: {S(v, "result")}");
                if (p.Variants.Count == 0) continue;
                list.Add(p);
            }
            return list;
        }

        /// The shipped examples, then the user's own files: a user's profile replaces a shipped one with the same id, new ids come first.
        public static List<HmdProfile> LoadAll()
        {
            var all = new List<HmdProfile>();
            foreach (var dir in new[] { ShippedDir, UserDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        foreach (var p in Parse(File.ReadAllText(f), $"{(dir == UserDir ? "config\\profiles" : "profiles")}\\{Path.GetFileName(f)}"))
                        {
                            var i = all.FindIndex(x => x.Id == p.Id);
                            if (i >= 0) all[i] = p; else if (dir == UserDir) all.Insert(0, p); else all.Add(p);
                        }
                    }
                    catch (Exception e) { Log.Write("WARN", $"profile {f}: {e.Message}"); }
                }
            }
            return all;
        }

        /// Writes the profile's session settings into config\session.json (after the wizard's own values, so a profile can refine them). False: nothing to do or it failed.
        public static bool ApplySessionOverrides(HmdProfile p)
        {
            if (p == null || p.SessionOverrides.Count == 0) return false;
            try
            {
                var s = Session.Load();
                foreach (var kv in p.SessionOverrides) Json.Set(s, kv.Key, kv.Value);
                Json.Save(Paths.Session, s);
                Log.Write("INFO", $"profile '{p.Id}': {p.SessionOverrides.Count} session setting(s) applied");
                return true;
            }
            catch (Exception e) { Log.Write("WARN", $"profile '{p.Id}': session settings not applied: {e.Message}"); return false; }
        }

        /// The most specific profile whose match fields all hold for this handshake (null: none, the wizard falls back to the capabilities alone).
        public static HmdProfile Match(Handshake hs, List<HmdProfile> all)
        {
            HmdProfile best = null; int bestScore = 0;
            foreach (var p in all)
            {
                int score = 0;
                if (p.NameContains.Length > 0)
                {
                    if (!p.NameContains.Any(n => hs.DisplayName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    score += 2;
                }
                if (p.ViewRes != null)
                {
                    if (hs.PanelW != p.ViewRes[0] || hs.PanelH != p.ViewRes[1]) continue;
                    score += 2;
                }
                if (p.RatesInclude.Length > 0)
                {
                    if (!p.RatesInclude.All(r => hs.Rates.Any(x => Math.Abs(x - r) < 0.6))) continue;
                    score += 1;
                }
                if (score > bestScore) { best = p; bestScore = score; }
            }
            return best;
        }
    }
}
