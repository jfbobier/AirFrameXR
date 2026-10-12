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

    public enum Rating { Unknown, Good, Ok, Poor }

    /// Provisional reference values (the final ones come from the lab comparisons with stock ALVR + SteamVR, see docs/CONFIGURE_WIZARD.md).
    /// `bench\reference.json` next to configure.exe overrides any of them (same key names).
    public static class Reference
    {
        static readonly Dictionary<string, double> Defaults = new Dictionary<string, double>
        {
            ["encode_good"] = 0.70, ["encode_ok"] = 0.90,         // encode p95 as a share of the frame time
            ["bitrate_good_mbps"] = 200, ["bitrate_ok_mbps"] = 100,
            ["loss_ok_per_s"] = 1.0,
            ["latency_good_ms"] = 45, ["latency_ok_ms"] = 70,      // ALVR's total pipeline latency
            ["decode_good_ms"] = 8, ["decode_ok_ms"] = 14,
            ["network_good_ms"] = 12, ["network_ok_ms"] = 25,
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

        public static Rating EncodeBudget(double p95, double budget) => budget <= 0 || p95 <= 0 ? Rating.Unknown : p95 < Get("encode_good") * budget ? Rating.Good : p95 < Get("encode_ok") * budget ? Rating.Ok : Rating.Poor;
        public static Rating Bitrate(double clean) => clean <= 0 ? Rating.Unknown : clean >= Get("bitrate_good_mbps") ? Rating.Good : clean >= Get("bitrate_ok_mbps") ? Rating.Ok : Rating.Poor;
        public static Rating Loss(double perS) => perS < 0 ? Rating.Unknown : perS <= 0.01 ? Rating.Good : perS <= Get("loss_ok_per_s") ? Rating.Ok : Rating.Poor;
        public static Rating Latency(double ms) => ms <= 0 ? Rating.Unknown : ms <= Get("latency_good_ms") ? Rating.Good : ms <= Get("latency_ok_ms") ? Rating.Ok : Rating.Poor;
        public static Rating Decode(double ms) => ms <= 0 ? Rating.Unknown : ms <= Get("decode_good_ms") ? Rating.Good : ms <= Get("decode_ok_ms") ? Rating.Ok : Rating.Poor;
        public static Rating Network(double ms) => ms <= 0 ? Rating.Unknown : ms <= Get("network_good_ms") ? Rating.Good : ms <= Get("network_ok_ms") ? Rating.Ok : Rating.Poor;
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
    }

    /// The configuration proposed from the handshake and the tests, with a short reason for each value.
    public class Proposal
    {
        public int EyeW, EyeH;
        public double Fps, BitrateMbps;
        public bool Foveated = true, Audio = true;
        public string LatencyMode = "low";
        readonly Dictionary<string, string> why = new Dictionary<string, string>();
        public string Why(string k) => why.TryGetValue(k, out var s) ? s : "";

        static int Align32(double w) => Math.Max(256, (int)(w / 32) * 32);

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
            // bitrate: the network test's clean step, else a safe default
            if (r.CleanMbps > 0) { p.BitrateMbps = Math.Max(30, Math.Min(1000, r.CleanMbps)); p.why["bitrate"] = $"{r.CleanMbps:0} Mbps: the highest rate your link carried cleanly in the network test, minus a 10% margin"; }
            else { p.BitrateMbps = 150; p.why["bitrate"] = "150 Mbps: a safe default (the network test did not run or no step passed)"; }
            p.Foveated = hs.Foveation;
            p.why["foveation"] = hs.Foveation ? "the headset supports ALVR's foveated encoding: fewer pixels at the edges, same sharpness where you look" : "the headset did not advertise foveated encoding: full frame (needs a strong link)";
            p.LatencyMode = "low";
            p.why["latency"] = "no frame buffer, the newest frame sent at once; switch to smooth for fewer repeated pictures on uneven games";
            p.Audio = true;
            p.why["audio"] = hs.MicRate > 0 ? $"game audio to the headset (it advertised a {hs.MicRate} Hz microphone path)" : "game audio to the headset";
            return p;
        }
    }
}
