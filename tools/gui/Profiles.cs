// Headset profiles (docs/HMD_PROFILES.md): one small JSON file per headset, shared by users among themselves (Discord), never downloaded by the
// program. The handshake identifies the headset, a matching profile is the starting configuration of the configure wizard (the wizard then verifies
// it on this PC instead of searching for one). Files are read from <install>\profiles (shipped examples) and <install>\config\profiles (the user's).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AirFrameXR
{
    public class NetworkVariant { public string Id = "", Label = ""; public double Mbps; }

    public class HmdProfile
    {
        public string Id = "", DisplayName = "", Source = "";
        public string[] NameContains = new string[0];
        public int[] ViewRes;                       // default_view_resolution to match, null = any
        public double[] RatesInclude = new double[0];
        /// 0 = not given: ALVR's / the headset's own default (the handshake's panel resolution, its preferred gamma, a rate it supports)
        public int EyeW, EyeH, BitDepth, SplitEncode;
        public double Fps, Gamma;
        public List<string> Sources = new List<string>();
        public string Codec = "hevc", Protocol = "udp";
        public bool FullRange = true, Foveated;
        public string DefaultVariant = "";
        public List<NetworkVariant> Variants = new List<NetworkVariant>();
        public List<string> Notes = new List<string>(), Verified = new List<string>();
        public string Author = "";
        /// ALVR session settings the profile sets (dotted paths into config\session.json -> value), applied when the wizard saves the configuration
        public Dictionary<string, object> SessionOverrides = new Dictionary<string, object>();
        /// A measured visible window (docs/AVP_FOV.md): the angle in degrees from the view axis to the edge of what the headset shows (outer, inner, upper, lower), the margin to add
        /// (head and eye position, fit, the width of the measuring stroke), and the render quality (0..1) suggested with it. null = the profile has none. Nothing applies it by itself:
        /// the Advanced window offers it as a button.
        public double[] FovVisibleDeg;
        public double FovMarginDeg = 3, FovRenderQuality;
        /// the headset app's own FoV scale this profile is meant to be used with (0 = none to recommend): a uniform scale of what the headset declares, so the stream covers less angle
        public double FovClientScale;
        public string FovNote = "";
        public bool HasFov => FovVisibleDeg != null;

        /// The tangents the game is told for the measured window plus the margin (outer, inner, upper, lower), two decimals like the Advanced window's fields.
        public double[] FovTangents() => FovVisibleDeg.Select(d => Math.Round(Math.Tan((d + FovMarginDeg) * Math.PI / 180), 2)).ToArray();

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
                p.Fps = D(st.TryGetValue("fps", out var f) ? f : 0);
                p.Codec = S(st, "codec", "hevc"); p.Protocol = S(st, "protocol", "udp");
                p.BitDepth = st.TryGetValue("bit_depth", out var bd) ? (int)D(bd) : 0;
                p.SplitEncode = st.TryGetValue("split_encode", out var se) ? (int)D(se) : 0;
                p.Gamma = st.TryGetValue("encoding_gamma", out var g) ? D(g) : 0;
                p.FullRange = B(st, "full_range", true); p.Foveated = B(st, "foveated", false);
                var net = O(e, "network");
                p.DefaultVariant = S(net, "default");
                foreach (var v in A(net, "variants").OfType<Dictionary<string, object>>())
                    p.Variants.Add(new NetworkVariant { Id = S(v, "id"), Label = S(v, "label"), Mbps = v.TryGetValue("bitrate_mbps", out var mb) ? D(mb) : 0 });
                p.Notes = A(e, "notes").OfType<string>().ToList();
                p.Sources = A(e, "sources").OfType<string>().ToList();
                foreach (var v in A(e, "verified").OfType<Dictionary<string, object>>())
                    p.Verified.Add($"{S(v, "gpu")}, {S(v, "client")}, {S(v, "date")}: {S(v, "result")}");
                var fv = O(e, "fov"); var vis = O(fv, "visible_deg");
                if (vis != null && new[] { "outer", "inner", "upper", "lower" }.All(k => D(vis.TryGetValue(k, out var x) ? x : 0) > 0))
                {
                    p.FovVisibleDeg = new[] { "outer", "inner", "upper", "lower" }.Select(k => D(vis[k])).ToArray();
                    if (fv.TryGetValue("margin_deg", out var mg)) p.FovMarginDeg = Math.Max(0, Math.Min(15, D(mg)));
                    if (fv.TryGetValue("render_quality", out var rq)) p.FovRenderQuality = Math.Max(0, Math.Min(1, D(rq)));
                    if (fv.TryGetValue("client_fov_scale", out var cs)) { var c = D(cs); p.FovClientScale = c >= 0.2 && c <= 1.6 && Math.Abs(c - 1) > 0.001 ? c : 0; }
                    p.FovNote = S(fv, "note");
                }
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

        /// The profile with a measured field of view whose name fields match the headset's name (the saved one: "Apple Vision Pro", or ALVR's "Apple Headset"); null when none has.
        public static HmdProfile WithFovFor(string headsetName)
        {
            if (string.IsNullOrEmpty(headsetName)) return null;
            return LoadAll().FirstOrDefault(p => p.HasFov && p.NameContains.Any(n => headsetName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        /// The profile's measured field of view becomes the default the games are told (video.fov_override on with the profile's tangents, never above the
        /// headset's own when they are known, the profile's render quality, the record of the headset they were made for): what the Advanced window's button does,
        /// done by the wizard when it applies a profile that has one (docs/AVP_FOV.md: the 99 % rectangle since 1.2.0). False: no field of view in the profile or it failed.
        public static bool ApplyFovDefault(HmdProfile p, string headsetName)
        {
            if (p == null || !p.HasFov) return false;
            try
            {
                var st = new AppSettings();
                var client = AppSettings.ReadClientFov();
                var t = p.FovTangents();
                var keys = new[] { "video.fov_outer", "video.fov_inner", "video.fov_upper", "video.fov_lower" };
                for (int k = 0; k < 4; k++) Json.Set(st.Root, keys[k], Math.Round(client != null ? Math.Min(t[k], client[k]) : t[k], 4));
                Json.Set(st.Root, "video.fov_override", true);
                Json.Set(st.Root, "video.fov_render_quality", Math.Round(p.FovRenderQuality, 2));
                if (client != null) Json.Set(st.Root, "video.fov_for", client.Select(x => (object)Math.Round(x, 4)).ToList());
                if (!string.IsNullOrEmpty(headsetName)) Json.Set(st.Root, "video.fov_for_headset", headsetName);
                st.Save();
                Log.Write("INFO", $"profile '{p.Id}': field of view {string.Join(" / ", t.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)))} applied as the default the games are told (render quality {p.FovRenderQuality:0.##}); Advanced picture and FOV settings changes it");
                return true;
            }
            catch (Exception e) { Log.Write("WARN", $"profile '{p.Id}': field of view not applied: {e.Message}"); return false; }
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

        /// What ALVR said about the headset, as the import dialog shows it next to the profile's expectations.
        public static string Describe(Handshake hs) =>
            $"{hs.DisplayName} ({hs.Hostname}): panel {hs.PanelW} x {hs.PanelH} per eye, refresh rates {string.Join("/", hs.Rates.Select(x => x.ToString("0.#", CultureInfo.InvariantCulture)))} Hz, " +
            $"foveated encoding {(hs.Foveation ? "yes" : "no")}, 10-bit {(hs.Enc10 ? "yes" : "no")}, preferred gamma {hs.Gamma:0.0#}";

        /// Why this profile may not suit this headset (empty: it matches). Only warnings: copying a profile from a Quest 2 to a Quest 3 is usually fine,
        /// the user decides.
        public static List<string> Compatibility(HmdProfile p, Handshake hs)
        {
            var w = new List<string>();
            if (p.NameContains.Length > 0 && !p.NameContains.Any(n => hs.DisplayName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                w.Add($"the profile is for a headset named like \"{string.Join("\" / \"", p.NameContains)}\", this one reports \"{hs.DisplayName}\"");
            if (p.ViewRes != null && (hs.PanelW != p.ViewRes[0] || hs.PanelH != p.ViewRes[1]))
                w.Add($"the profile expects a {p.ViewRes[0]} x {p.ViewRes[1]} panel, this headset reports {hs.PanelW} x {hs.PanelH}");
            var missing = p.RatesInclude.Where(r => !hs.Rates.Any(x => Math.Abs(x - r) < 0.6)).ToList();
            if (missing.Count > 0) w.Add($"the profile expects the refresh rate(s) {string.Join(", ", missing.Select(x => x.ToString("0.#", CultureInfo.InvariantCulture)))} Hz, which this headset does not offer");
            if (p.Fps > 0 && !hs.Rates.Any(x => Math.Abs(x - p.Fps) < 0.6)) w.Add($"the profile streams at {p.Fps:0} fps, which this headset does not list among its refresh rates");
            if (p.EyeW > 0 && (p.EyeW > hs.PanelW * 1.25 || p.EyeH > hs.PanelH * 1.25)) w.Add($"the profile's {p.EyeW} x {p.EyeH} stream is larger than this headset's panel");
            if (p.BitDepth >= 10 && !hs.Enc10) w.Add("the profile uses 10-bit video and this headset did not advertise 10-bit decoding");
            if (p.Foveated && !hs.Foveation) w.Add("the profile uses foveated encoding and this headset did not advertise it");
            return w;
        }

        /// Reads a profile file the user picked: the profile (null + error when it is not usable) and the compatibility warnings for this headset.
        public static HmdProfile Read(string path, Handshake hs, out List<string> warnings, out string error)
        {
            warnings = new List<string>(); error = null;
            try
            {
                var list = Parse(File.ReadAllText(path), Path.GetFileName(path));
                if (list.Count == 0) { error = "this file is not a AirFrameXR headset profile (it needs an id, a stream section and at least one network variant; see profiles\\template.profile.json)"; return null; }
                warnings = Compatibility(list[0], hs);
                return list[0];
            }
            catch (Exception e) { error = e.Message; return null; }
        }

        /// Copies the profile file into config\profiles (named after its id), so the wizard finds it next time. Returns the new path.
        public static string Install(string path, HmdProfile p)
        {
            Directory.CreateDirectory(UserDir);
            var safe = new string(p.Id.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
            var dst = Path.Combine(UserDir, safe + ".profile.json");
            File.Copy(path, dst, true);
            return dst;
        }

        // ---------------------------------------------------------------------------------------------------------------- export
        static readonly string[] ExportSkip = { "client_connections", "openvr_config", "server_version", "gui_collapsed", "session_settings.extra.capture", "session_settings.extra.steamvr_launcher", "game_audio.content.device", "audio.microphone.content.devices" };
        static readonly string[] StreamKeys = { "session_settings.video.bitrate.mode", "session_settings.video.preferred_fps", "session_settings.video.transcoding_view_resolution", "session_settings.video.emulated_headset_view_resolution",
                                                "session_settings.video.foveated_encoding.enabled", "session_settings.connection.stream_protocol.variant" };

        static void Flatten(object o, string path, Dictionary<string, object> into)
        {
            if (o is Dictionary<string, object> d) { foreach (var kv in d) Flatten(kv.Value, path.Length == 0 ? kv.Key : path + "." + kv.Key, into); }
            else into[path] = o;
        }

        /// The ALVR settings that differ from the shipped defaults (config\session.default.json), minus machine-specific ones and the ones the stream/network
        /// sections already carry: what this user tuned, as dotted path -> value.
        public static Dictionary<string, object> TunedSettings(Dictionary<string, object> session, Dictionary<string, object> defaults)
        {
            var a = new Dictionary<string, object>(); var b = new Dictionary<string, object>();
            Flatten(session, "", a); Flatten(defaults, "", b);
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var res = new Dictionary<string, object>();
            foreach (var kv in a.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (ExportSkip.Any(x => kv.Key.Contains(x)) || StreamKeys.Any(x => kv.Key.StartsWith(x))) continue;
                if (b.TryGetValue(kv.Key, out var dv) && ser.Serialize(dv) == ser.Serialize(kv.Value)) continue;
                res[kv.Key] = kv.Value;
            }
            return res;
        }

        /// A shareable profile from what is configured now (the saved session.json / airframexr.json), what ALVR declared about the headset at the handshake and the
        /// numbers of the tests that ran. `author` and `networkLabel` are the user's words.
        public static Dictionary<string, object> BuildExport(Handshake hs, Results r, AppSettings settings, string author, string networkLabel)
        {
            var s = Session.Load();
            double N(string k, double d) => Json.Num(s, k, d);
            const string V = "session_settings.video.";
            int w = (int)N(V + "transcoding_view_resolution.Absolute.width", 3552), h = (int)N(V + "transcoding_view_resolution.Absolute.height.content", 3200);
            double fps = N(V + "preferred_fps", 90), mbps = N(V + "bitrate.mode.ConstantMbps", 150);
            bool fov = Json.Bool(s, V + "foveated_encoding.enabled", false);
            var proto = Json.Str(s, "session_settings.connection.stream_protocol.variant", "Udp").ToLowerInvariant();
            var slug = new string((hs.DisplayName.Length > 0 ? hs.DisplayName : "headset").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            var rates = hs.Rates.Select(x => (object)x).ToList();
            var verified = new List<object>();
            if (r != null && (r.DisplayedFps > 0 || r.EncodeP95 > 0))
            {
                var bits = new List<string>();
                if (r.DisplayedFps > 0) bits.Add($"{r.DisplayedFps:0} of {fps:0} fps shown");
                if (r.TotalMs > 0) bits.Add($"total latency {r.TotalMs:0} ms (ALVR's measure: encode {r.EncoderMs:0}, network {r.NetworkMs:0}, decode {r.DecodeMs:0})");
                if (r.EncodeP95 > 0) bits.Add($"encode p95 {r.EncodeP95:0.0} ms for {r.EncW}x{r.EncH}");
                if (r.LossPerS >= 0 && r.CleanMbps > 0) bits.Add($"{r.LossPerS:0.0} packets lost per second at {mbps:0} Mbps");
                if (r.Slices > 0) bits.Add(r.Engines >= 2 && r.Slices >= 1.5 ? $"split encoding engaged ({r.Slices:0.#} strips on {r.Engines} engines)" : $"split encoding not engaged ({r.Engines} engine(s))");
                verified.Add(new Dictionary<string, object> {
                    ["gpu"] = string.IsNullOrEmpty(r.GpuName) ? "" : $"{r.GpuName}, driver {r.Driver}", ["client"] = $"{hs.DisplayName} ({hs.Hostname})",
                    ["date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["result"] = string.Join("; ", bits) });
            }
            var export = new Dictionary<string, object>
            {
                ["_comment"] = "AirFrameXR headset profile exported by the setup wizard. Share this file; to use it, Import profile... in the wizard or drop it into config\\profiles. See docs/HMD_PROFILES.md.",
                ["schema"] = 1,
                ["id"] = slug + (author.Length > 0 ? "-" + new string(author.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-') : ""),
                ["display_name"] = hs.DisplayName,
                ["author"] = author,
                ["match"] = new Dictionary<string, object> { ["name_contains"] = new List<object> { hs.DisplayName }, ["default_view_resolution"] = new List<object> { hs.PanelW, hs.PanelH }, ["supported_refresh_rates_include"] = rates },
                ["stream"] = new Dictionary<string, object> {
                    ["eye_resolution"] = new List<object> { w, h }, ["fps"] = fps, ["codec"] = "hevc", ["bit_depth"] = Json.Bool(s, V + "encoder_config.use_10bit", hs.Enc10) ? 10 : 8,
                    ["full_range"] = hs.PreferFull, ["encoding_gamma"] = hs.Gamma, ["foveated"] = fov, ["split_encode"] = !fov && settings.EncodeProfile == "full-split" ? 3 : 0, ["protocol"] = proto },
                ["network"] = new Dictionary<string, object> { ["default"] = "mine", ["variants"] = new List<object> { new Dictionary<string, object> { ["id"] = "mine", ["label"] = networkLabel.Length > 0 ? networkLabel : "the network this was tested on", ["bitrate_mbps"] = mbps } } },
                ["session_overrides"] = TunedSettings(s, Json.Load(Paths.SessionDefault)),
                ["notes"] = new List<object>(),
                ["verified"] = verified,
                ["exported_by"] = new Dictionary<string, object> {
                    ["airframexr"] = Version.Number, ["date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["alvr_declared"] = new Dictionary<string, object> {
                        ["display_name"] = hs.DisplayName, ["hostname"] = hs.Hostname, ["panel"] = new List<object> { hs.PanelW, hs.PanelH }, ["refresh_rates"] = rates,
                        ["foveated_encoding"] = hs.Foveation, ["encoder_10_bits"] = hs.Enc10, ["encoder_high_profile"] = hs.EncHigh, ["encoder_av1"] = hs.EncAv1,
                        ["prefer_10bit"] = hs.Prefer10, ["prefer_full_range"] = hs.PreferFull, ["prefer_hdr"] = hs.PreferHdr, ["preferred_encoding_gamma"] = hs.Gamma, ["microphone_sample_rate"] = hs.MicRate } },
            };
            return export;
        }

        /// The most specific profile whose match fields all hold for this handshake (null: none, the wizard falls back to the capabilities alone).
        public static HmdProfile Match(Handshake hs, List<HmdProfile> all)
        {
            HmdProfile best = null; double bestScore = 0;
            foreach (var p in all)
            {
                double score = 0;
                if (p.NameContains.Length > 0)
                {
                    var hit = p.NameContains.Where(n => hs.DisplayName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).OrderByDescending(n => n.Length).FirstOrDefault();
                    if (hit == null) continue;
                    score += 2 + hit.Length / 100.0;   // the more specific name wins ("Quest 3S" over "Quest 3")
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
