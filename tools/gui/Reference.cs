// What the headset advertised (Handshake), how a measurement is rated (Reference) and the configuration proposed from both (Proposal).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VisionALVR
{
    /// ALVR's VideoStreamingCapabilities as the headset sent them at the handshake (host event `client_capabilities`).
    public class Handshake
    {
        public string DisplayName = "", Hostname = "", Ip = "";
        public int PanelW = 3552, PanelH = 3200;
        public List<double> Rates = new List<double> { 90 };
        public int MicRate = 0;
        public bool Foveation = true, EncHigh = true, Enc10 = true, EncAv1 = false, Prefer10 = true, PreferFull = true, PreferHdr = false;
        public double Gamma = 1.0;
        public bool FromHeadset;   // false: the defaults of a Vision Pro (nothing was received)
        public HeadsetProfile Profile => HeadsetProfile.From(this);

        static double D(object o) { try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; } }
        static bool B(Dictionary<string, object> d, string k, bool def) => d.TryGetValue(k, out var v) && v is bool b ? b : def;

        public static Handshake From(Dictionary<string, object> caps)
        {
            var h = new Handshake { FromHeadset = true };
            if (caps == null) return h;
            if (caps.TryGetValue("default_view_resolution", out var r) && r is object[] a && a.Length >= 2) { h.PanelW = (int)D(a[0]); h.PanelH = (int)D(a[1]); }
            if (caps.TryGetValue("supported_refresh_rates", out var rr) && rr is object[] ra && ra.Length > 0)
                h.Rates = ra.Select(D).Where(x => x > 1).Select(x => Math.Round(x, 1)).Distinct().OrderBy(x => x).ToList();
            h.MicRate = caps.TryGetValue("microphone_sample_rate", out var m) ? (int)D(m) : 0;
            h.Foveation = B(caps, "supports_foveated_encoding", true);
            h.EncHigh = B(caps, "encoder_high_profile", true);
            h.Enc10 = B(caps, "encoder_10_bits", true);
            h.EncAv1 = B(caps, "encoder_av1", false);
            h.Prefer10 = B(caps, "prefer_10bit", true);
            h.PreferFull = B(caps, "prefer_full_range", true);
            h.PreferHdr = B(caps, "prefer_hdr", false);
            h.Gamma = caps.TryGetValue("preferred_encoding_gamma", out var g) ? D(g) : 1.0;
            return h;
        }

        /// Headsets we have tuned: only the Vision Pro (the ALVR visionOS client names itself so).
        public bool IsVisionPro => DisplayName.IndexOf("Vision", StringComparison.OrdinalIgnoreCase) >= 0 || Hostname.EndsWith(".client.alvr");
        public string Vendor => IsVisionPro ? "Apple" : (DisplayName.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0 ? "Meta" : "unknown");
    }

    /// Per-headset rules of thumb (ALVR's docs and settings guide, forum guidance; see docs/CONFIGURE_WIZARD.md for the sources).
    public class HeadsetProfile
    {
        public string Family = "generic";            // avp | quest3 | quest2 | generic
        public string Label = "unknown headset";
        public string CodecAdvice = "HEVC (H.265), 10-bit";
        public string WifiAdvice = "5 GHz or 6 GHz Wi-Fi, a wide channel, the PC on Ethernet";
        public bool Tuned;

        public static HeadsetProfile From(Handshake h)
        {
            var n = (h.DisplayName ?? "") + " " + (h.Hostname ?? "");
            if (h.IsVisionPro)
                return new HeadsetProfile
                {
                    Family = "avp", Label = "Apple Vision Pro", Tuned = true,
                    CodecAdvice = h.EncAv1 ? "HEVC 10-bit. Your headset also decodes AV1 (newer chip): VisionALVR encodes HEVC today. H.264 is never recommended."
                                           : "HEVC 10-bit (strongly recommended on the M2 Vision Pro; it has no AV1 decoder; H.264 is never recommended)",
                    WifiAdvice = "the first Vision Pro (M2) has Wi-Fi 6 on 5 GHz only (no 6 GHz): a Wi-Fi 6 router on 5 GHz with a 160 MHz channel, close to the headset, the PC on Ethernet; newer models can also use 6 GHz",
                };
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"Quest\s*3", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return new HeadsetProfile
                {
                    Family = "quest3", Label = "Meta Quest 3",
                    CodecAdvice = "HEVC 10-bit (AV1 also works on the Quest 3 with an RTX 40 GPU: VisionALVR encodes HEVC today; H.264 needs 3-4x the bitrate)",
                    WifiAdvice = "Wi-Fi 6E on the 6 GHz band, close to the headset, the PC on Ethernet",
                };
            if (n.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0)
                return new HeadsetProfile
                {
                    Family = "quest2", Label = "Meta Quest 2 / Pro",
                    CodecAdvice = "HEVC 10-bit (no AV1 on this headset; H.264 needs 3-4x the bitrate)",
                    WifiAdvice = "Wi-Fi 6 or 5 GHz Wi-Fi 5 with an 80-160 MHz channel, close to the headset, the PC on Ethernet",
                };
            return new HeadsetProfile { Label = string.IsNullOrEmpty(h.DisplayName) ? "unknown headset" : h.DisplayName };
        }
    }

    /// The numbers a setup is judged against: rules of thumb scaled to the stream being sent.
    ///  bitrate: bits per encoded pixel per frame. Forum guidance of about 200 Mbps for 2 x 4K at 90 Hz (and ALVR's own 100-150 Mbps HEVC for a
    ///  Quest-class stream) both come to about 0.13; 0.18 is the comfortable value. Foveated encoding is measured on the encoded size (what is sent).
    ///  encode time: 5-10 ms is the usual budget; network about 10 ms; decode 5-8 ms at high bitrates.
    public class Targets
    {
        public const double MinBpp = 0.13, GoodBpp = 0.18;
        public double MinMbps, GoodMbps, FrameMs;
        public double EncodeGoodMs = 6, EncodeOkMs = 10, NetworkGoodMs = 10, NetworkOkMs = 20, DecodeGoodMs = 8, DecodeOkMs = 14, LatencyGoodMs = 50, LatencyOkMs = 80;

        public static Targets For(int encW, int encH, double fps)
        {
            var t = new Targets { FrameMs = 1000.0 / Math.Max(1, fps) };
            var pix = (double)Math.Max(1, encW) * Math.Max(1, encH) * Math.Max(1, fps);
            t.MinMbps = MinBpp * pix / 1e6;
            t.GoodMbps = GoodBpp * pix / 1e6;
            return t;
        }
    }

    public enum Rating { Unknown, Good, Ok, Poor }

    /// Rules of thumb that do not depend on the stream (the ones that do are in Targets). `bench\reference.json` next to configure.exe overrides any of them.
    public static class Reference
    {
        static readonly Dictionary<string, double> Defaults = new Dictionary<string, double>
        {
            ["encode_good"] = 0.70, ["encode_ok"] = 0.90,         // encode p95 as a share of the frame time (with the 5-10 ms rule, Targets)
            ["loss_ok_per_s"] = 1.0,
            ["fps_good_share"] = 0.97, ["fps_ok_share"] = 0.90,    // displayed fps / refresh rate
        };
        static Dictionary<string, double> table;

        public static double Get(string k)
        {
            if (table == null)
            {
                table = new Dictionary<string, double>(Defaults);
                try
                {
                    var f = System.IO.Path.Combine(Paths.Dir, "bench", "reference.json");
                    if (System.IO.File.Exists(f))
                        foreach (var kv in Json.Load(f)) table[kv.Key] = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture);
                }
                catch { }
            }
            return table.TryGetValue(k, out var v) ? v : 0;
        }

        public static Rating EncodeBudget(double p95, double frameMs, Targets t)
        {
            if (p95 <= 0 || frameMs <= 0) return Rating.Unknown;
            var byTime = p95 < Get("encode_good") * frameMs ? Rating.Good : p95 < Get("encode_ok") * frameMs ? Rating.Ok : Rating.Poor;   // share of the frame time
            var byRule = p95 <= t.EncodeGoodMs ? Rating.Good : p95 <= t.EncodeOkMs ? Rating.Ok : Rating.Poor;                              // the 5-10 ms rule of thumb
            return (Rating)Math.Max((int)byTime, (int)byRule);
        }
        public static Rating Bitrate(double clean, Targets t) => clean <= 0 ? Rating.Unknown : clean >= t.GoodMbps ? Rating.Good : clean >= t.MinMbps ? Rating.Ok : Rating.Poor;
        public static Rating Loss(double perS) => perS < 0 ? Rating.Unknown : perS <= 0.01 ? Rating.Good : perS <= Get("loss_ok_per_s") ? Rating.Ok : Rating.Poor;
        public static Rating Latency(double ms, Targets t) => ms <= 0 ? Rating.Unknown : ms <= t.LatencyGoodMs ? Rating.Good : ms <= t.LatencyOkMs ? Rating.Ok : Rating.Poor;
        public static Rating Decode(double ms, Targets t) => ms <= 0 ? Rating.Unknown : ms <= t.DecodeGoodMs ? Rating.Good : ms <= t.DecodeOkMs ? Rating.Ok : Rating.Poor;
        public static Rating Network(double ms, Targets t) => ms <= 0 ? Rating.Unknown : ms <= t.NetworkGoodMs ? Rating.Good : ms <= t.NetworkOkMs ? Rating.Ok : Rating.Poor;
        public static Rating FpsShare(double share) => share <= 0 ? Rating.Unknown : share >= Get("fps_good_share") ? Rating.Good : share >= Get("fps_ok_share") ? Rating.Ok : Rating.Poor;
    }

    /// What the tests measured (filled by the steps in TestRunner.cs; 0 = not measured).
    public class Results
    {
        public string GpuName = "", GpuArch = "", Driver = "";
        public int VramMb;
        public double EncodeP50, EncodeP95, BudgetMs; public bool EncodeFits; public int EncW, EncH, Engines;
        public string QualityLabel = ""; public double QualityEncodeP95, QualityPsnr;
        public double CleanMbps, LossPerS;
        public double TotalMs, GameMs, EncoderMs, NetworkMs, DecodeMs, DecoderQueueMs, VsyncQueueMs, ClientFps, DisplayedFps, StreamedMbps;
        public string AudioNote = "";
        public double TestFps;   // the refresh rate the tests ran at
        public int SplitMode;    // the encoder test's split_encode_mode and the strips per frame it measured (>= 2 means split encoding engaged)
        public double Slices;
    }

    /// The configuration proposed from the handshake and the tests, with a short reason for each value.
    public class Proposal
    {
        public int EyeW, EyeH;
        public double Fps, BitrateMbps;
        public bool Foveated = true, Audio = true;
        readonly Dictionary<string, string> why = new Dictionary<string, string>();
        public string Why(string k) => why.TryGetValue(k, out var s) ? s : "";

        static int Align32(double w) => Math.Max(256, (int)(w / 32) * 32);

        /// The configuration of a community profile (profiles\\hmd_profiles.json) for one network variant: nothing is searched or scaled, the tests
        /// verify it. `scale` shrinks the resolution (the wizard's scale box), `fps` and `foveated` are the user's own choices on the previous page.
        public static Proposal FromProfile(Handshake hs, HmdProfile prof, string variantId, double fps, double scale, bool foveated)
        {
            var v = prof.Variant(variantId);
            // what a profile leaves out is the headset's / ALVR's own default: the handshake's panel resolution, a rate the headset offers (90 if it can, else its highest)
            int bw = prof.EyeW > 0 ? prof.EyeW : hs.PanelW, bh = prof.EyeH > 0 ? prof.EyeH : hs.PanelH;
            double pfps = prof.Fps > 0 ? prof.Fps : (hs.Rates.Any(x => Math.Abs(x - 90) < 0.6) ? 90 : (hs.Rates.Count > 0 ? hs.Rates.Max() : 90));
            var p = new Proposal { EyeW = Align32(bw * scale), EyeH = Align32(bh * scale), Fps = fps > 0 ? fps : pfps, BitrateMbps = v.Mbps, Foveated = foveated, Audio = true };
            var src = $"profile '{prof.DisplayName}'";
            p.why["fps"] = $"{p.Fps:0} Hz: " + (Math.Abs(p.Fps - pfps) < 0.5 ? $"the {src}" : $"your choice (the {src} uses {pfps:0} Hz)");
            p.why["resolution"] = prof.EyeW > 0
                ? (scale >= 0.999 ? $"{p.EyeW} x {p.EyeH} per eye: the {src}" : $"{p.EyeW} x {p.EyeH}: {scale * 100:0}% of the {src} ({prof.EyeW} x {prof.EyeH})")
                : $"{p.EyeW} x {p.EyeH} per eye: the headset's own default (the {src} leaves the resolution to ALVR)" + (scale >= 0.999 ? "" : $", scaled to {scale * 100:0}%");
            p.why["bitrate"] = $"{p.BitrateMbps:0} Mbps: the {src}, network '{v.Label}'. The tests check this value on your link; nothing is searched.";
            p.why["foveation"] = (foveated ? "foveated encoding (your choice)" : "full frame, no foveation") + $"; the {src} " + (prof.Foveated ? "uses foveation" : "uses the full frame");
            p.why["codec"] = $"{prof.Codec.ToUpperInvariant()}" + (prof.BitDepth > 0 ? $", {prof.BitDepth}-bit" : "") + (prof.Gamma > 0 ? $", gamma {prof.Gamma:0.0#}" : "") + $" (the {src}; what it leaves out stays at ALVR's default)";
            p.why["audio"] = "game audio to the headset";
            p.Profile = prof;
            return p;
        }

        public HmdProfile Profile;

        public static Proposal Build(Handshake hs, Results r, double chosenFps)
        {
            var p = new Proposal();
            // refresh: the one the tests ran at, unless the encoder could not keep up with it: then the highest supported rate that it can
            var fps = chosenFps > 0 ? chosenFps : (hs.Rates.Count > 0 ? hs.Rates.Max() : 90);
            p.Fps = fps;
            p.why["fps"] = $"{fps:0} Hz: " + (hs.FromHeadset ? "the rate you chose among the headset's " + string.Join("/", hs.Rates.Select(x => x.ToString("0"))) : "default");
            if (r.EncodeP95 > 0 && r.BudgetMs > 0 && !r.EncodeFits)
            {
                var lower = hs.Rates.Where(x => x < fps && r.EncodeP95 < Reference.Get("encode_ok") * 1000.0 / x).OrderByDescending(x => x).ToList();
                if (lower.Count > 0)
                {
                    p.Fps = lower[0];
                    p.why["fps"] = $"{p.Fps:0} Hz: the encoder needs {r.EncodeP95:0.0} ms per frame, more than {fps:0} Hz allows";
                }
            }
            // resolution: the panel's, scaled down only when the encoder cannot sustain it at this rate
            double sw = hs.PanelW, sh = hs.PanelH, share = 1;
            var budget = 1000.0 / p.Fps;
            if (r.EncodeP95 > 0 && r.EncodeP95 > Reference.Get("encode_ok") * budget)
            {
                share = Math.Sqrt(Reference.Get("encode_ok") * budget / r.EncodeP95);   // encode time follows the pixel count
                share = Math.Max(0.4, Math.Min(1.0, share));
            }
            p.EyeW = Align32(sw * share);
            p.EyeH = Align32(sh * share);
            p.why["resolution"] = share >= 0.999
                ? $"the headset's panel resolution ({hs.PanelW} x {hs.PanelH} per eye): the encoder keeps up ({r.EncodeP95:0.0} ms of {budget:0.0} ms)"
                : $"scaled to {share * 100:0}% of the panel: the encoder needs {r.EncodeP95:0.0} ms at full size, the frame time is {budget:0.0} ms (estimate: encode time follows the pixel count)";
            // bitrate: twice the rule-of-thumb target for the stream that is sent (the comfortable value with headroom), within what the link carried cleanly
            var ew = r.EncW > 0 ? r.EncW : (int)(p.EyeW * 2 * (hs.Foveation ? 0.35 : 1.0));
            var eh = r.EncH > 0 ? r.EncH : (int)(p.EyeH * (hs.Foveation ? 0.52 : 1.0));
            var tg = Targets.For(ew, eh, p.Fps);
            var want = Math.Max(60, Math.Round(2 * tg.GoodMbps / 10) * 10);
            if (r.CleanMbps > 0)
            {
                p.BitrateMbps = Math.Min(want, Math.Max(30, r.CleanMbps));
                p.why["bitrate"] = r.CleanMbps >= want
                    ? $"{p.BitrateMbps:0} Mbps = about twice the rule-of-thumb target ({tg.GoodMbps:0} Mbps for {ew}x{eh} at {p.Fps:0} Hz); your link carried {r.CleanMbps:0} Mbps cleanly, so there is room to raise it if you see compression artifacts"
                    : $"{p.BitrateMbps:0} Mbps: all your link carried cleanly in the network test (the rule-of-thumb target is {tg.GoodMbps:0} Mbps for {ew}x{eh} at {p.Fps:0} Hz)";
            }
            else { p.BitrateMbps = want; p.why["bitrate"] = $"{want:0} Mbps = about twice the rule-of-thumb target ({tg.GoodMbps:0} Mbps for {ew}x{eh} at {p.Fps:0} Hz); the network test did not run or no step passed"; }
            p.Foveated = hs.Foveation;
            p.why["codec"] = hs.Profile.CodecAdvice;
            p.why["foveation"] = hs.Foveation ? "the headset supports ALVR's foveated encoding: fewer pixels at the edges, same sharpness where you look" : "the headset did not advertise foveated encoding: full frame (needs a strong link)";
            p.Audio = true;
            p.why["audio"] = hs.MicRate > 0 ? $"game audio to the headset (it advertised a {hs.MicRate} Hz microphone path)" : "game audio to the headset";
            return p;
        }
    }
}
