// What the frame pipeline will do with the current settings, before a game runs: the stream's per-eye size (the "fit" keeps the pixel density the
// user chose over whatever box the headset declares), the game's render size (floor = the stream's density over the rectangle the game is told,
// ceiling = the reference size when the fit is on), the encoded frame (ALVR's foveated layout, the exact maths of FFR.cpp), where the
// foveation's full-density band sits in degrees, the NVENC strips to expect (the split rules of the Video Codec SDK guide, section 8.14) and the
// warnings to show. A port of harness/fov_stream_geometry.py `model()`; harness/pipeline_model_check.py compares the two over a grid.
// Pure maths: the only file access is in FromSettings. docs/FOV_PIPELINE_OPTIM.md section 2b.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AirFrameXR
{
    public static class PipelineModel
    {
        public const int MaxEyeWidth = 4096, MaxEyeHeight = 8192;   // what NVENC takes: 8192 across side by side, 8192 high (Nvenc.CheckEye says the same)
        public const int SplitMinRowsHevc = 2112, SplitMinRowsAv1 = 2048;

        public class Inputs
        {
            /// the box the headset declares, tangents per edge (outer, inner, upper, lower), the two eyes averaged (AppSettings.ReadClientFov)
            public double[] Declared;
            /// the rectangle the game is told (the override's tangents), null = the declared box
            public double[] Rect;
            /// the stream's per-eye size in the session (openvr_config.eye_resolution_*)
            public int[] Eye;
            /// the density reference: the eye size the user chose and the box the headset declared then (video.stream_reference_eye / _fov); null = none saved
            public int[] ReferenceEye;
            public double[] ReferenceFov;
            /// video.stream_fit_window: size the stream from the reference density over the declared box
            public bool Fit;
            /// the render quality slider 0..1
            public double Quality;
            public bool Foveated = true;
            /// centre size x, y, centre shift x, y, edge ratio x, y (session foveated_encoding.content)
            public double[] Geometry = { 0.45, 0.4, 0.4, 0.1, 4.0, 5.0 };
            /// NVENC split mode: 0 auto, 1 auto-forced, 2 / 3 / 4 strips, 15 off
            public int SplitMode = 3;
            public int Preset = 3;
            public string Tuning = "ll";
            /// the GPU's NVENC engine count; -1 = not known yet
            public int Engines = -1;
        }

        public class Result
        {
            public int[] FitEye, Floor, Ceiling, Game, Frame, Encoded;
            public double[] Rect;
            public bool Foveated;
            /// the full-density band in degrees: x = outer (negative) .. inner, y = down (negative) .. up; null without foveation
            public double[] BandX, BandY;
            /// centre size x, y, shift x, y that keep the reference frame's band on this frame; null without a fit reference or foveation
            public double[] Reanchored;
            /// strips NVENC is expected to use (0 = engines unknown) and why
            public int Strips; public string StripsWhy = "";
            public List<string> Warnings = new List<string>();
            public List<string> Info = new List<string>();
        }

        /// ALVR floors an Absolute size to 32 (server_core connection.rs align32)
        public static int Align32Floor(double v) => (int)Math.Floor(v / 32.0) * 32;

        /// ovrshim/render_size.h EvenAtLeast64 (std::lround: half away from zero)
        public static int EvenAtLeast64(double v) => Math.Max(64, ((int)Math.Round(v, MidpointRounding.AwayFromZero) + 1) / 2 * 2);

        /// ovrshim/render_size.h GameRenderSize: quality 0 = the floor, 1 = the ceiling, even and at least 64
        public static int[] GameRenderSize(double[] floor, double[] ceiling, double q)
        {
            q = Math.Max(0, Math.Min(1, q));
            return new[] { EvenAtLeast64(floor[0] + (ceiling[0] - floor[0]) * q), EvenAtLeast64(floor[1] + (ceiling[1] - floor[1]) * q) };
        }

        /// ALVR's FFR.cpp CalculateFoveationVars, float arithmetic where the C++ uses float (the rounding decides whether the optimized size lands on a
        /// multiple of 32 or one step above it): optimized eye width / height (aligned to 32), the aligned centre size and shift.
        public static void FfrVars(int eyeW, int eyeH, double csX, double csY, double shX, double shY, double erX, double erY,
                                   out int optW, out int optH, out float centerX, out float centerY, out float shiftX, out float shiftY)
        {
            float tw = eyeW, th = eyeH;
            float cx = (float)csX, cy = (float)csY, sx = (float)shX, sy = (float)shY, rx = (float)erX, ry = (float)erY;
            float ex = (float)(tw - (float)(cx * tw)), ey = (float)(th - (float)(cy * th));
            float cxa = (float)(1.0 - Math.Ceiling((double)ex / ((double)rx * 2.0)) * ((double)rx * 2.0) / (double)tw);
            float cya = (float)(1.0 - Math.Ceiling((double)ey / ((double)ry * 2.0)) * ((double)ry * 2.0) / (double)th);
            float exa = (float)(tw - (float)(cxa * tw)), eya = (float)(th - (float)(cya * th));
            float sxa = (float)(Math.Ceiling((double)(float)(sx * exa) / ((double)rx * 2.0)) * ((double)rx * 2.0) / (double)exa);
            float sya = (float)(Math.Ceiling((double)(float)(sy * eya) / ((double)ry * 2.0)) * ((double)ry * 2.0) / (double)eya);
            float fsx = (float)((double)cxa + (1.0 - (double)cxa) / (double)rx);
            float fsy = (float)((double)cya + (1.0 - (double)cya) / (double)ry);
            float ow = (float)(fsx * tw), oh = (float)(fsy * th);
            optW = (int)Math.Ceiling((double)(float)(ow / 32f)) * 32;
            optH = (int)Math.Ceiling((double)(float)(oh / 32f)) * 32;
            centerX = cxa; centerY = cya; shiftX = sxa; shiftY = sya;
        }

        /// the full-density band of ALVR's compress shader in source eye uv (0 = the outer edge): [start, start + cs]
        public static double[] BandSource(double cs, double sh) { var start = (1.0 - cs) * (1.0 + sh) / 2.0; return new[] { start, start + cs }; }

        /// centre size and shift for a wanted band [start, start + width]
        public static double[] BandParams(double start, double width) => new[] { width, 2.0 * start / (1.0 - width) - 1.0 };

        /// strips NVENC is expected to encode for a frame of this height (SDK 13.0 guide 8.14), and why
        public static int SplitStrips(int height, int mode, int engines, int preset, string tuning, string codec, out string why)
        {
            if (engines < 2) { why = "one NVENC engine: no split"; return 1; }
            if (mode == 15) { why = "split mode off"; return 1; }
            if (mode == 2 || mode == 3 || mode == 4)
            {
                var n = Math.Min(mode, engines);
                why = $"forced {mode}-strip mode" + (n < mode ? $" capped by {engines} engines" : "");
                return n;
            }
            if (mode == 1) { why = "auto-forced: the driver chooses the strip count"; return Math.Min(engines, 3); }
            var min = codec == "av1" ? SplitMinRowsAv1 : SplitMinRowsHevc;
            if (height < min) { why = $"auto mode splits only frames at least {min} high ({codec.ToUpperInvariant()}); this one is {height}"; return 1; }
            if ((tuning == "hq" && preset > 2) || ((tuning == "ll" || tuning == "ull") && preset > 4)) { why = $"auto mode does not split with P{preset} {tuning}: P1-P2 high quality or P1-P4 low latency only"; return 1; }
            why = "auto mode: height and preset allow it, the driver chooses the strip count";
            return Math.Min(engines, 3);
        }

        static double Deg(double t) => Math.Atan(t) * 180.0 / Math.PI;
        static double R1(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

        /// the stream's per-eye size the fit gives over `declared` at the reference density (floored to 32, not capped)
        public static int[] FitEye(int[] referenceEye, double[] referenceFov, double[] declared)
        {
            double fw = referenceFov[0] + referenceFov[1], fh = referenceFov[2] + referenceFov[3];
            // the ratio first, plus a hair, so the same box gives back the same size (3200 x 1.66 / 1.66 lands under 3200 in floating point); the oracle does the same
            return new[] { Align32Floor(referenceEye[0] * ((declared[0] + declared[1]) / fw) + 1e-6), Align32Floor(referenceEye[1] * ((declared[2] + declared[3]) / fh) + 1e-6) };
        }

        public static Result Compute(Inputs i)
        {
            var r = new Result();
            var d = i.Declared;
            double[] dspan = { d[0] + d[1], d[2] + d[3] };
            var rect = (i.Rect ?? d).Select((v, k) => Math.Min(v, d[k])).ToArray();
            double[] rspan = { rect[0] + rect[1], rect[2] + rect[3] };
            r.Rect = rect;
            bool useFit = i.Fit && i.ReferenceEye != null && i.ReferenceFov != null;
            int[] fitEye;
            if (useFit)
            {
                var raw = FitEye(i.ReferenceEye, i.ReferenceFov, d);
                if (raw[0] > MaxEyeWidth || raw[1] > MaxEyeHeight)
                    r.Warnings.Add($"The stream's {raw[0]} x {raw[1]} per eye exceeds what NVENC encodes ({2 * MaxEyeWidth} across side by side, {MaxEyeHeight} high): capped.");
                fitEye = new[] { Math.Min(raw[0], MaxEyeWidth), Math.Min(raw[1], MaxEyeHeight) };
            }
            else
            {
                fitEye = new[] { i.Eye[0], i.Eye[1] };
                if (fitEye[0] > MaxEyeWidth || fitEye[1] > MaxEyeHeight)
                    r.Warnings.Add($"The stream's {fitEye[0]} x {fitEye[1]} per eye exceeds what NVENC encodes ({2 * MaxEyeWidth} across side by side, {MaxEyeHeight} high).");
            }
            double wr = rspan[0] / dspan[0], hr = rspan[1] / dspan[1];
            double[] floor = { fitEye[0] * wr, fitEye[1] * hr };
            double[] ceiling = useFit ? new double[] { Math.Max(i.ReferenceEye[0], fitEye[0]), Math.Max(i.ReferenceEye[1], fitEye[1]) } : new double[] { fitEye[0], fitEye[1] };
            r.FitEye = fitEye;
            r.Floor = new[] { EvenAtLeast64(floor[0]), EvenAtLeast64(floor[1]) };
            r.Ceiling = new[] { (int)ceiling[0], (int)ceiling[1] };
            r.Game = GameRenderSize(floor, ceiling, i.Quality);
            r.Frame = new[] { 2 * fitEye[0], fitEye[1] };
            r.Foveated = i.Foveated;
            var g = i.Geometry;
            if (i.Foveated)
            {
                FfrVars(fitEye[0], fitEye[1], g[0], g[1], g[2], g[3], g[4], g[5], out var ow, out var oh, out var cxa, out var cya, out var sxa, out var sya);
                r.Encoded = new[] { 2 * ow, oh };
                var bx = BandSource(cxa, sxa); var by = BandSource(cya, sya);
                r.BandX = new[] { R1(Deg(-d[0] + bx[0] * dspan[0])), R1(Deg(-d[0] + bx[1] * dspan[0])) };
                r.BandY = new[] { R1(Deg(d[2] - by[1] * dspan[1])), R1(Deg(d[2] - by[0] * dspan[1])) };
                if (useFit)
                {
                    var f = i.ReferenceFov; double[] fspan = { f[0] + f[1], f[2] + f[3] };
                    FfrVars(i.ReferenceEye[0], i.ReferenceEye[1], g[0], g[1], g[2], g[3], g[4], g[5], out _, out _, out var rcx, out var rcy, out var rsx, out var rsy);
                    var rbx = BandSource(rcx, rsx); var rby = BandSource(rcy, rsy);
                    double[] tx = { -f[0] + rbx[0] * fspan[0], -f[0] + rbx[1] * fspan[0] };          // tan, outer .. inner
                    double[] ty = { f[2] - rby[0] * fspan[1], f[2] - rby[1] * fspan[1] };           // tan, up .. down
                    double ux0 = Math.Max(0.0, (tx[0] + d[0]) / dspan[0]), ux1 = Math.Min(1.0, (tx[1] + d[0]) / dspan[0]);
                    double uy0 = Math.Max(0.0, (d[2] - ty[0]) / dspan[1]), uy1 = Math.Min(1.0, (d[2] - ty[1]) / dspan[1]);
                    var px = BandParams(ux0, ux1 - ux0); var py = BandParams(uy0, uy1 - uy0);
                    r.Reanchored = new[] { Math.Round(px[0], 3), Math.Round(py[0], 3), Math.Round(px[1], 3), Math.Round(py[1], 3) };
                }
            }
            else r.Encoded = new[] { 2 * fitEye[0], fitEye[1] };
            if (i.Engines < 0)
            {
                r.Strips = 0; r.StripsWhy = "the GPU's NVENC engine count is not known yet";
                r.Info.Add("The GPU's NVENC engine count is not known yet: run the streamer once.");
            }
            else
            {
                r.Strips = SplitStrips(r.Encoded[1], i.SplitMode, i.Engines, i.Preset, i.Tuning ?? "ll", "hevc", out var why);
                r.StripsWhy = why;
                if (i.Engines >= 2 && r.Strips < Math.Min(i.Engines, 3))
                    r.Warnings.Add($"Split encoding: {r.Strips} strip(s) expected ({why}). A forced mode splits at any size.");
            }
            if (i.Quality > 0 && r.Game[0] == r.Floor[0] && r.Game[1] == r.Floor[1] && ceiling[0] <= floor[0] + 1e-9 && ceiling[1] <= floor[1] + 1e-9)
                r.Warnings.Add("The render quality slider has no room: the game's size and its ceiling are the same (tick the stream fit or set a larger resolution).");
            return r;
        }

        /// One line for the Advanced window and the logs: "Game renders W x H per eye -> frame ... -> encoded ... -> N NVENC strip(s) (why)".
        public static string Summary(Result r)
        {
            string X(int[] v) => v == null ? "-" : v[0] + " × " + v[1];
            var strips = r.Strips > 0 ? $"{r.Strips} NVENC strip{(r.Strips > 1 ? "s" : "")} ({r.StripsWhy})" : "NVENC strips: " + r.StripsWhy;
            return $"Game renders {X(r.Game)} per eye -> frame {X(r.Frame)} -> encoded {X(r.Encoded)} ({(r.Foveated ? "foveated" : "full frame")}) -> {strips}.";
        }

        public static string BandText(Result r)
        {
            if (!r.Foveated || r.BandX == null) return "";
            var c = CultureInfo.InvariantCulture;
            return $"Foveation keeps full detail from {Math.Abs(r.BandX[0]).ToString("0.0", c)}° outward to {r.BandX[1].ToString("0.0", c)}° toward the nose, and from {Math.Abs(r.BandY[0]).ToString("0.0", c)}° down to {r.BandY[1].ToString("0.0", c)}° up; the rest of the frame is compressed.";
        }

        // ---- foveation from Apple's rate map (docs/PICTURE_EDGES_DESIGN.md section 5b, step D) --------------------------------------------------
        // A port of harness/fov_stream_geometry.py (load_ratemap .. density_eye); harness/ratemap_check.py compares the two. Settings only: the result goes to session.json
        // (ApplyGeometry) through the foveation parameters the host already reads. Nothing calls ApplyGeometry yet: the A/B of the zero-loss geometry on the 5090 decides.

        /// Apple's rasterization rate map: the panel's frustum P (outer, inner, upper, lower tangents), the full-rate density in px per tangent unit per axis, and one table
        /// per axis of [degrees from the eye's axis, rate] (x: negative = outer; y: negative = down).
        public class RateMap { public double[] P, Density; public double[][] X, Y; }

        public static class RateMapFoveation
        {
            /// STEP 0 SWITCH (docs/PICTURE_EDGES_DESIGN.md section 11): true = the client declares the frustum upside down, so the vertical table is read mirrored (up becomes down).
            /// Default false = the documented orientation (50 up / 45 down). Settled by the client's `[proj]` log line; this is the only place the vertical orientation lives.
            public static bool VerticalFlipped = false;

            static double[][] Table(object o) => ((object[])o).Select(r => ((object[])r).Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray()).ToArray();

            /// the headset profile's `ratemap` block (profiles/apple-vision-pro.profile.json); null when absent
            public static RateMap FromProfile(Dictionary<string, object> profile)
            {
                if (profile == null || !profile.TryGetValue("ratemap", out var ro) || !(ro is Dictionary<string, object> rm)) return null;
                var p = ((object[])rm["frustum_tan"]).Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
                var lp = ((object[])rm["logical_px"]).Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
                return new RateMap { P = p, Density = new[] { lp[0] / (p[0] + p[1]), lp[1] / (p[2] + p[3]) }, X = Table(rm["x"]), Y = Table(rm["y"]) };
            }

            public static RateMap Load(string path) { try { return FromProfile(Json.Load(path)); } catch { return null; } }

            /// the rate at `d` degrees: linear between samples, the end value beyond them (never zero)
            public static double RateAt(double[][] tbl, double d)
            {
                if (d <= tbl[0][0]) return tbl[0][1];
                if (d >= tbl[tbl.Length - 1][0]) return tbl[tbl.Length - 1][1];
                for (int k = 0; k + 1 < tbl.Length; k++)
                    if (tbl[k][0] <= d && d <= tbl[k + 1][0]) return tbl[k][1] + (tbl[k + 1][1] - tbl[k][1]) * (d - tbl[k][0]) / (tbl[k + 1][0] - tbl[k][0]);
                return tbl[tbl.Length - 1][1];
            }

            /// the rate on an axis (0 = x, 1 = y) at `d` degrees, with the step-0 switch applied to the vertical one
            public static double Rate(RateMap rm, int axis, double d) => axis == 0 ? RateAt(rm.X, d) : RateAt(rm.Y, VerticalFlipped ? -d : d);

            /// ALVR's compress shader, output -> source mapping of one axis (harness/lib.py _fov_f)
            public static double FovF(double e, double cs, double sh, double er)
            {
                double c0 = (1.0 - cs) / 2.0, c1 = (er - 1.0) * c0 * (sh + 1.0) / er, c2 = (er - 1.0) * cs + 1.0;
                double lo = c0 * (sh + 1.0) / c2, hi = c0 * (sh - 1.0) / c2 + 1.0, center = e * c2 / er + c1;
                double d2 = e * c2, d3 = (e - 1.0) * c2 + 1.0;
                if (e < lo) { var g1 = e / lo; return g1 * center + (1.0 - g1) * d2; }
                if (e > hi) { var g2 = (1.0 - e) / (1.0 - hi); return g2 * center + (1.0 - g2) * d3; }
                return center;
            }

            static double AxisDeg(double s, double loT, double hiT, int axis) => Deg(axis == 0 ? -loT + s * (loT + hiT) : hiT - s * (loT + hiT));

            /// worst over-compression on one axis (r / max(1, m / rate)); cs, sh are ALVR's aligned values. loT / hiT: x outer / inner, y down / up tangents
            public static double AxisLoss(double cs, double sh, double er, double loT, double hiT, int axis, RateMap rm, double m, int n, out double atDeg, out double rAt, out double allowAt)
            {
                double fs = cs + (1.0 - cs) / er;
                var es = new double[n]; var src = new double[n];
                for (int i = 0; i < n; i++) { es[i] = i / (double)(n - 1); src[i] = FovF(es[i], cs, sh, er); }
                double worst = 0; atDeg = 0; rAt = 0; allowAt = 0;
                for (int i = 0; i < n; i++)
                {
                    int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
                    double r = (src[b] - src[a]) / (es[b] - es[a]) / fs;
                    double d = AxisDeg(src[i], loT, hiT, axis);
                    double allow = Math.Max(1.0, m / Math.Max(1e-6, Rate(rm, axis, d)));
                    if (r / allow > worst) { worst = r / allow; atDeg = Math.Round(d, 1); rAt = Math.Round(r, 2); allowAt = Math.Round(allow, 2); }
                }
                return worst;
            }

            /// the interval where rate >= contour, clipped to the view, in source eye uv (x from the outer edge, y from the top)
            static void BandUv(double[] view, int axis, RateMap rm, double contour, out double u0, out double u1, out double d0, out double d1)
            {
                double loT = axis == 0 ? view[0] : view[3], hiT = axis == 0 ? view[1] : view[2];
                double loD = -Deg(loT), hiD = Deg(hiT);
                double first = double.NaN, last = double.NaN, near = 0, nearAbs = double.MaxValue;
                for (int i = 0; i <= 2000; i++)
                {
                    double d = loD + (hiD - loD) * i / 2000;
                    if (Math.Abs(d) < nearAbs) { nearAbs = Math.Abs(d); near = d; }
                    if (Rate(rm, axis, d) >= contour) { if (double.IsNaN(first)) first = d; last = d; }
                }
                if (double.IsNaN(first)) { first = last = near; }
                double a = Math.Tan(first * Math.PI / 180.0), b = Math.Tan(last * Math.PI / 180.0), span = loT + hiT;
                d0 = first; d1 = last;
                if (axis == 0) { u0 = (a + loT) / span; u1 = (b + loT) / span; }
                else { u0 = (hiT - b) / span; u1 = (hiT - a) / span; }
            }

            /// rules 1-2 of section 5b: band = rate >= contour clipped to the view, er = (1 / rate at the edge that allows more + 1) / 2 in [1, 10]
            /// (cs x, cs y, shift x, shift y, edge ratio x, edge ratio y)
            public static double[] RuleGeometry(double[] view, RateMap rm, double contour = 0.5)
            {
                var o = new double[6];
                for (int axis = 0; axis < 2; axis++)
                {
                    BandUv(view, axis, rm, contour, out var u0, out var u1, out _, out _);
                    var bp = BandParams(u0, u1 - u0);
                    double loT = axis == 0 ? view[0] : view[3], hiT = axis == 0 ? view[1] : view[2];
                    double edge = Math.Max(Rate(rm, axis, -Deg(loT)), Rate(rm, axis, Deg(hiT)));
                    o[axis] = bp[0]; o[2 + axis] = bp[1]; o[4 + axis] = Math.Min(10.0, Math.Max(1.0, (1.0 / edge + 1.0) / 2.0));
                }
                return o;
            }

            static double Worst(double size, double cs, double sh, double er, double loT, double hiT, int axis, RateMap rm, double m, out int opt)
            {
                FfrVars((int)size, (int)size, cs, cs, sh, sh, er, er, out var ow, out _, out var cx, out _, out var sx, out _);
                opt = ow;
                return AxisLoss(cx, sx, er, loT, hiT, axis, rm, m, 801, out _, out _, out _);
            }

            /// rule 3 of section 5b, exact: per axis, over band contours 0.30 .. 0.95, the largest er (bisection) whose worst over-compression after ALVR's alignment is <= 1,
            /// keeping the contour with the fewest encoded pixels on that axis. eye = the per-eye stream size; returns cs x, cs y, shift x, shift y, er x, er y
            public static double[] ZeroLoss(double[] view, int[] eye, RateMap rm, double m = 1.0)
            {
                var best = new double[2][];
                for (int axis = 0; axis < 2; axis++)
                {
                    double loT = axis == 0 ? view[0] : view[3], hiT = axis == 0 ? view[1] : view[2];
                    double size = eye[axis];
                    double[] cand = null; int candPx = 0;
                    for (int k = 30; k < 96; k += 5)
                    {
                        BandUv(view, axis, rm, k / 100.0, out var u0, out var u1, out _, out _);
                        var bp = BandParams(u0, u1 - u0);
                        double cs = bp[0], sh = bp[1];
                        if (Worst(size, cs, sh, 1.0, loT, hiT, axis, rm, m, out _) > 1.0 + 1e-6) continue;
                        double a = 1.0, b = 10.0;
                        if (Worst(size, cs, sh, b, loT, hiT, axis, rm, m, out _) <= 1.0 + 1e-6) a = b;
                        else for (int it = 0; it < 22; it++) { double mid = (a + b) / 2; if (Worst(size, cs, sh, mid, loT, hiT, axis, rm, m, out _) <= 1.0 + 1e-6) a = mid; else b = mid; }
                        Worst(size, cs, sh, a, loT, hiT, axis, rm, m, out var px);
                        if (cand == null || px < candPx) { cand = new[] { cs, sh, a }; candPx = px; }
                    }
                    best[axis] = cand ?? new[] { 0.45, 0.0, 1.0 };   // nothing under the map even without compression: cannot happen with a map <= 1 everywhere, kept safe
                }
                return new[] { best[0][0], best[1][0], best[0][1], best[1][1], best[0][2], best[1][2] };
            }

            /// section 5 density rule: eye = min(target density x the view's span per axis, NVENC's cap, what the encode test fits), 32-aligned (floor)
            public static int[] DensityEye(double[] view, RateMap rm, double fraction = 1.0, int[] fits = null)
            {
                double rawX = rm.Density[0] * fraction * (view[0] + view[1]), rawY = rm.Density[1] * fraction * (view[2] + view[3]);
                double limX = fits != null ? Math.Min(MaxEyeWidth, fits[0]) : MaxEyeWidth, limY = fits != null ? Math.Min(MaxEyeHeight, fits[1]) : MaxEyeHeight;
                return new[] { Align32Floor(Math.Min(rawX, limX)), Align32Floor(Math.Min(rawY, limY)) };
            }

            /// writes the six foveation numbers where the host reads them (session_settings.video.foveated_encoding.content.* and ALVR's cache openvr_config.foveation_*),
            /// rounded to 3 decimals as the scenarios carry them
            public static void ApplyGeometry(Dictionary<string, object> session, double[] g)
            {
                string[] keys = { "center_size_x", "center_size_y", "center_shift_x", "center_shift_y", "edge_ratio_x", "edge_ratio_y" };
                for (int k = 0; k < 6; k++)
                {
                    var v = Math.Round(g[k], 3);
                    Json.Set(session, "session_settings.video.foveated_encoding.content." + keys[k], v);
                    Json.Set(session, "openvr_config.foveation_" + keys[k], v);
                }
            }
        }

        /// The step D wiring (docs/PICTURE_EDGES_DESIGN.md sections 5b and 9; owner 2026-11-06): the foveation of the stream is computed from the picture edges V (the Vision Pro
        /// card's profile, or the whole panel the headset declares = "Apple's default") and Apple's rasterization map, over the stream size already in session.json. It compresses
        /// nothing below what the map keeps at that angle (the zero-loss geometry), so it never costs picture, and it takes the place of the static numbers of the profile. Only the
        /// foveation numbers are written: the stream size stays the user's choice (the density rule of section 5 is not applied).
        public static class PictureEdgesFoveation
        {
            public static string ProfilePath => Path.Combine(Paths.Dir, "profiles", "apple-vision-pro.profile.json");

            static double[] Four(object o) => (o as object[])?.Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();

            /// V as one box (outer, inner, upper, lower): the per-edge union of the two eyes of video.headset_view (four numbers, or {left, right}), never wider than the
            /// panel the headset declares (the client declares the smaller of the two); absent = the whole panel; no headset seen yet = Apple's frustum from the map
            public static double[] View(AppSettings st, RateMap rm)
            {
                var panel = AppSettings.ReadClientFov() ?? rm.P;
                var t = Json.Get(st.Root, "video.headset_view");
                double[] a = null, b = null;
                if (t is Dictionary<string, object> d) { a = Four(d.ContainsKey("left") ? d["left"] : null); b = Four(d.ContainsKey("right") ? d["right"] : null); }
                else a = b = Four(t);
                if (a == null || a.Length != 4) return panel;
                if (b == null || b.Length != 4) b = a;
                var v = new double[4];
                for (int k = 0; k < 4; k++) v[k] = Math.Min(Math.Max(a[k], b[k]), panel[k]);
                return v.All(x => x > 0.05) ? v : panel;
            }

            static readonly string[] Keys = { "center_size_x", "center_size_y", "center_shift_x", "center_shift_y", "edge_ratio_x", "edge_ratio_y" };

            /// Computes the foveation for the stream size in `session` and writes it there. Vision Pro only (the map is Apple's). `explicit`: the user just chose a picture-edges
            /// profile, finished a fit, or is setting up (clears the hand-edit flag); false: a passive recompute (the streamer starting) that leaves hand-edited numbers alone.
            /// Returns what it wrote, one line, or null when it wrote nothing. The caller saves the session (and the settings when the flag changed).
            public static string Apply(AppSettings st, Dictionary<string, object> session, bool explicitChoice, RateMap rm = null)
            {
                if (st.Kind != HeadsetKind.VisionPro) return null;
                rm = rm ?? RateMapFoveation.Load(ProfilePath);
                if (rm == null) return null;
                if (st.FoveationManual) { if (!explicitChoice) return null; st.FoveationManual = false; }
                var eye = StreamEyeExpected(session);
                if (eye == null || eye[0] < 256 || eye[1] < 256) return null;
                var view = View(st, rm);
                var g = RateMapFoveation.ZeroLoss(view, eye, rm);
                RateMapFoveation.ApplyGeometry(session, g);
                var c = CultureInfo.InvariantCulture;
                return $"foveation from the picture edges {string.Join(" / ", view.Select(x => x.ToString("0.00", c)))} and Apple's map, over {eye[0]} x {eye[1]} per eye: centre {g[0].ToString("0.000", c)} x {g[1].ToString("0.000", c)}, shift {g[2].ToString("0.000", c)} / {g[3].ToString("0.000", c)}, edge ratio {g[4].ToString("0.000", c)} / {g[5].ToString("0.000", c)}";
            }

            /// Apply on the files: loads the session, writes it back when something changed, logs. Never throws (a failure is a warning in the log).
            public static string ApplyToFiles(AppSettings st, bool explicitChoice)
            {
                try
                {
                    var flag = st.FoveationManual;
                    var s = Session.Load();
                    var line = Apply(st, s, explicitChoice);
                    if (line == null) return null;
                    Json.Save(Paths.Session, s);
                    if (flag != st.FoveationManual) st.Save();
                    Log.Write("INFO", line);
                    return line;
                }
                catch (Exception e) { Log.Write("WARN", "foveation from the picture edges not written: " + e.Message); return null; }
            }
        }

        // ---- the files -------------------------------------------------------------------------------------------------------------------------
        /// encoder facts from the session and the settings: foveated, its geometry, split mode, preset, engines (-1 = unknown)
        public static void ReadEncoder(AppSettings st, Dictionary<string, object> session, Inputs i)
        {
            const string F = "session_settings.video.foveated_encoding.";
            bool full = st.EncodeProfile == "full-split";
            i.Foveated = !full && Json.Bool(session, F + "enabled", true);
            i.Geometry = new[] { Json.Num(session, F + "content.center_size_x", 0.45), Json.Num(session, F + "content.center_size_y", 0.4), Json.Num(session, F + "content.center_shift_x", 0.4),
                Json.Num(session, F + "content.center_shift_y", 0.1), Json.Num(session, F + "content.edge_ratio_x", 4.0), Json.Num(session, F + "content.edge_ratio_y", 5.0) };
            i.SplitMode = full || st.SplitEncode ? 3 : 15;
            var p = Json.Str(session, "session_settings.video.encoder_config.nvenc.quality_preset.variant", "P3");
            i.Preset = p.Length == 2 && p[0] == 'P' && char.IsDigit(p[1]) ? p[1] - '0' : 3;
            i.Tuning = "ll";   // nvidia_profile.rs: low latency
            i.Engines = ReadEngines();
        }

        /// config\airframexr_gpu.json, written by the streamer at its encoder's creation: {"name","driver","nvenc_engines"}; -1 when absent
        public static int ReadEngines()
        {
            try
            {
                var p = Path.Combine(Paths.Config, "airframexr_gpu.json");
                if (!File.Exists(p)) return -1;
                var v = Json.Num(Json.Load(p), "nvenc_engines", -1);
                return v >= 0 ? (int)v : -1;
            }
            catch { return -1; }
        }

        /// the stream's per-eye size from the session (openvr_config, the negotiated cache the host reads too), null when unknown
        public static int[] StreamEye(Dictionary<string, object> session)
        {
            int w = (int)Json.Num(session, "openvr_config.eye_resolution_width", 0), h = (int)Json.Num(session, "openvr_config.eye_resolution_height", 0);
            return w > 0 && h > 0 ? new[] { w, h } : null;
        }

        /// the stream's per-eye size as the next connection negotiates it: the Absolute transcoding size floored to 32 on both axes (server_core align32), else ALVR's
        /// negotiated cache from the last connection. The setting first: the cache is stale between a save and the next connection (a user saw the old size in the FOV card, 2026-11-05)
        public static int[] StreamEyeExpected(Dictionary<string, object> session)
        {
            var e = SessionEye(session);
            return e != null ? new[] { e[0] / 32 * 32, e[1] / 32 * 32 } : StreamEye(session);
        }

        /// The per-eye size the user chose (the wizard's settings panel, the Settings window): it becomes the density reference of the stream fit (stream_reference_eye, with the
        /// box the headset declared), the session gets the fit size over that box when the fit is on and the size itself otherwise, in both resolution keys and ALVR's negotiated
        /// cache (what the FOV card and the encoder tests read before the next connection). The caller saves the settings and the session. Returns the size written.
        public static int[] SaveEyeChoice(Dictionary<string, object> session, AppSettings settings, int w, int h)
        {
            settings.StreamReferenceEye = new[] { w, h };
            var clientFov = AppSettings.ReadClientFov();
            if (clientFov != null) settings.StreamReferenceFov = clientFov;
            int sw = w, sh = h;
            if (settings.StreamFitWindow && clientFov != null && settings.StreamReferenceFov != null)
            {
                var fe = FitEye(new[] { w, h }, settings.StreamReferenceFov, clientFov);
                sw = Math.Min(fe[0], MaxEyeWidth); sh = Math.Min(fe[1], MaxEyeHeight);
                settings.StreamFitEye = new[] { sw, sh };
            }
            WriteStreamEye(session, sw, sh);
            return new[] { sw, sh };
        }

        /// the session's transcoding size as the Settings panel writes it (Absolute), null when not Absolute
        public static int[] SessionEye(Dictionary<string, object> session)
        {
            const string V = "session_settings.video.transcoding_view_resolution.";
            if (Json.Str(session, V + "variant", "") != "Absolute") return null;
            int w = (int)Json.Num(session, V + "Absolute.width", 0), h = (int)Json.Num(session, V + "Absolute.height.content", 0);
            return w > 0 && h > 0 ? new[] { w, h } : null;
        }

        /// writes the stream's per-eye size everywhere the Settings panel does (both resolution keys, Absolute, and ALVR's negotiated cache)
        public static void WriteStreamEye(Dictionary<string, object> session, int w, int h)
        {
            foreach (var k in new[] { "session_settings.video.transcoding_view_resolution", "session_settings.video.emulated_headset_view_resolution" })
            {
                Json.Set(session, k + ".variant", "Absolute");
                Json.Set(session, k + ".Absolute.width", w);
                Json.Set(session, k + ".Absolute.height.set", true);
                Json.Set(session, k + ".Absolute.height.content", h);
            }
            Json.Set(session, "openvr_config.eye_resolution_width", w);
            Json.Set(session, "openvr_config.eye_resolution_height", h);
            Json.Set(session, "openvr_config.target_eye_resolution_width", w);
            Json.Set(session, "openvr_config.target_eye_resolution_height", h);
        }

        /// the inputs from the files: the headset's declared box (null = not known yet), the override, the session, the settings
        public static Inputs FromSettings(AppSettings st, Dictionary<string, object> session)
        {
            var declared = AppSettings.ReadClientFov();
            if (declared == null) return null;
            var i = new Inputs { Declared = declared, Eye = StreamEyeExpected(session) ?? new[] { 3552, 3200 }, ReferenceEye = st.StreamReferenceEye, ReferenceFov = st.StreamReferenceFov, Fit = st.StreamFitWindow, Quality = st.FovRenderQuality };
            if (st.FovOverride && st.FovFits(declared))
            {
                var saved = new[] { st.FovOuter, st.FovInner, st.FovUpper, st.FovLower };
                if (saved.All(v => v > 0)) i.Rect = saved.Select((v, k) => Math.Abs(v - declared[k]) < 0.005 ? declared[k] : Math.Min(v, declared[k])).ToArray();
            }
            ReadEncoder(st, session, i);
            return i;
        }
    }
}
