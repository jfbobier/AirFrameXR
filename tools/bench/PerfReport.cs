// The report of a Bench session (docs/TELEMETRY_PLAN.md, "The report"): reads one session folder, writes nothing but the report, never changes its input.
// Inputs (all optional, a missing one is INSUFFICIENT DATA for what needs it, never a guess):
//   host_frames.csv    the engine's per-frame record of what was sent (telemetry flavour): arrival, encode start, encode ms, hold, send time, kind of frame
//   client_graph.csv   the headset's per-frame report (the client-statistics patch): latency split, fps, bitrate
//   hardware_*.csv, logger_health.csv, session.json    from perfcore.dll's probe
// What it says about the stream is about SENT frames (the stream's cadence), not about how often the game rendered new content: a frame the generator
// made or sent again counts as sent. Percentiles are nearest-rank. The thresholds are engineering heuristics (named in each finding), not guarantees.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AirFrameXR.Perf
{
    public sealed class Finding
    {
        public string Id, Status, Title, Detail, Test;   // Status: OK | PROBLEM | WATCH | INSUFFICIENT
    }

    public sealed class WorstFrame
    {
        public long Frame; public double T, GapMs, QueueMs, EncodeMs, HoldMs; public string Class;
    }

    /// one line of the resource table: a metric with its average and its worst value (the worst is defined per row: the 99th percentile of a load, the lowest value of a clock or of free memory)
    public sealed class ResourceRow { public string Name, Average, Worst, Note; }

    /// GPU, CPU, memory and I/O, network or VR rendering: a status, a one-line reading, and the rows behind it
    public sealed class ResourceCategory
    {
        public string Name, Status = "OK", Summary = "";
        public readonly List<ResourceRow> Rows = new List<ResourceRow>();
    }

    /// One line of a time series chart: a value per second of the recording, drawn as a percentage of its limit so that different units share a scale (100 % = the limit or budget)
    public sealed class TimeSeries
    {
        public string Name, Unit, Fmt = "F1", LimitText;
        public double Limit;                 // the real value that is drawn as 100 %
        public double[] V;                   // real values, one per second of the window (NaN: no reading)
        public double Percent(int i) { return double.IsNaN(V[i]) || Limit <= 0 ? double.NaN : V[i] / Limit * 100.0; }
    }

    /// the charts of one category (at most three lines each)
    public sealed class SeriesGroup { public string Category; public readonly List<TimeSeries> Series = new List<TimeSeries>(); }

    public sealed class ReportData
    {
        public double OriginSeconds;                                     // seconds from the recording's start to the first second of the charts
        public double[] LateBudgetPercent;                               // late frames per second as a percentage of the 1 % budget (NaN: no frames), the strip above the charts
        public readonly List<SeriesGroup> Charts = new List<SeriesGroup>();
        public readonly List<ResourceCategory> Categories = new List<ResourceCategory>();
        public string VerdictStatus = "INSUFFICIENT", Verdict = "";
        public readonly List<Finding> Findings = new List<Finding>();
        public readonly List<WorstFrame> Worst = new List<WorstFrame>();
        public readonly List<string> Hotspots = new List<string>();   // the worst 10 s windows: "when did it go bad" for a long recording
        public readonly List<KeyValuePair<string, string>> Tiles = new List<KeyValuePair<string, string>>();
        public string Markdown = "";
    }

    public static partial class Report
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        public const double LateFactor = 1.5;     // a gap longer than this many periods is a late frame (the lab's rule, harness/README.md)
        public const double MaxLateShare = 0.01;  // plan A01
        public const double PauseMs = 500;        // a longer gap between sent frames is a pause, not a late frame
        public const double EncodeBudget = 0.8;   // encode p99 above this share of the period is a problem

        // ---- small helpers -----------------------------------------------------------------------------------------------
        sealed class Table
        {
            public readonly Dictionary<string, List<double>> Col = new Dictionary<string, List<double>>();
            public readonly Dictionary<string, List<string>> Raw = new Dictionary<string, List<string>>();
            public int Rows;
            public bool Has(string c) { return Col.ContainsKey(c); }
            public List<double> Get(string c) { List<double> v; return Col.TryGetValue(c, out v) ? v : new List<double>(); }
        }

        static Table Read(string path)
        {
            if (!File.Exists(path)) return null;
            string[] lines;
            try { using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var r = new StreamReader(fs)) lines = r.ReadToEnd().Split('\n'); }
            catch { return null; }
            if (lines.Length < 2) return null;
            var head = lines[0].TrimEnd('\r').Split(',');
            var t = new Table();
            foreach (var h in head) { t.Col[h] = new List<double>(); t.Raw[h] = new List<string>(); }
            for (int i = 1; i < lines.Length; i++)
            {
                var l = lines[i].TrimEnd('\r'); if (l.Length == 0) continue;
                var f = l.Split(',');
                if (f.Length != head.Length) continue;      // a torn last line of a file still being written
                for (int k = 0; k < head.Length; k++)
                {
                    double v; t.Col[head[k]].Add(double.TryParse(f[k], NumberStyles.Float, C, out v) ? v : double.NaN);
                    t.Raw[head[k]].Add(f[k]);
                }
                t.Rows++;
            }
            return t;
        }

        static double P(List<double> v, double p)     // nearest rank, NaN ignored
        {
            var s = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
            if (s.Count == 0) return double.NaN;
            int k = (int)Math.Ceiling(p * s.Count) - 1; return s[Math.Max(0, Math.Min(s.Count - 1, k))];
        }
        static double Max(List<double> v) { var s = v.Where(x => !double.IsNaN(x)).ToList(); return s.Count == 0 ? double.NaN : s.Max(); }
        static double Mean(List<double> v) { var s = v.Where(x => !double.IsNaN(x)).ToList(); return s.Count == 0 ? double.NaN : s.Average(); }
        static string N(double v, string f = "F1") { return double.IsNaN(v) ? "n/a" : v.ToString(f, C); }
        static string Pct(double v) { return double.IsNaN(v) ? "n/a" : (v * 100).ToString("F2", C) + " %"; }

        static double StartQpc(string dir) { return SessionNum(dir, "started_qpc_s"); }
        static double EndQpc(string dir) { return SessionNum(dir, "ended_qpc_s"); }
        static double SessionNum(string dir, string key)
        {
            try
            {
                var s = File.ReadAllText(Path.Combine(dir, "session.json"));
                int i = s.IndexOf("\"" + key + "\":"); if (i < 0) return double.NaN;
                i += key.Length + 3; int j = s.IndexOfAny(new[] { ',', '\n' }, i);
                double v; return double.TryParse(s.Substring(i, j - i).Trim(), NumberStyles.Float, C, out v) ? v : double.NaN;
            }
            catch { return double.NaN; }
        }

        static string Name(ulong bits)
        {
            var n = new List<string>();
            if ((bits & 0x2) != 0) n.Add("applications clocks setting"); if ((bits & 0x4) != 0) n.Add("software power cap"); if ((bits & 0x8) != 0) n.Add("hardware slowdown");
            if ((bits & 0x20) != 0) n.Add("software thermal slowdown"); if ((bits & 0x40) != 0) n.Add("hardware thermal slowdown"); if ((bits & 0x80) != 0) n.Add("hardware power brake");
            if ((bits & 0x10) != 0) n.Add("sync boost"); if ((bits & 0x100) != 0) n.Add("display clock setting");
            return string.Join(" + ", n);
        }

        // ---- the report ----------------------------------------------------------------------------------------------------
        public static ReportData Build(string dir, double warmupSeconds = 10)
        {
            var R = new ReportData();
            var hf = Read(Path.Combine(dir, "host_frames.csv"));
            var cg = Read(Path.Combine(dir, "client_graph.csv"));
            var gpu = Read(Path.Combine(dir, "hardware_gpu.csv"));
            var sys = Read(Path.Combine(dir, "hardware_system.csv"));
            var eng = Read(Path.Combine(dir, "hardware_engines.csv"));
            var proc = Read(Path.Combine(dir, "hardware_process.csv"));
            var health = Read(Path.Combine(dir, "logger_health.csv"));
            double t0 = StartQpc(dir);
            double from = double.IsNaN(t0) ? double.NaN : t0 + warmupSeconds;
            double to = EndQpc(dir);     // the engine's files can hold more than this recording (a whole streamer run): the window is cut here

            // ---- host frames in the window
            var rows = new List<int>();
            if (hf != null && hf.Has("t_send"))
            {
                var ts = hf.Get("t_send");
                var sl = hf.Has("slot") ? hf.Get("slot") : null;
                for (int i = 0; i < ts.Count; i++) if (!double.IsNaN(ts[i]) && (double.IsNaN(from) || ts[i] >= from) && (double.IsNaN(to) || ts[i] <= to) && (sl == null || sl[i] != -1)) rows.Add(i);   // the idle (green) frames are not the game's stream
                rows = rows.OrderBy(i => ts[i]).ToList();
                if (double.IsNaN(from) && rows.Count > 0) { double first = ts[rows[0]] + warmupSeconds; rows = rows.Where(i => ts[i] >= first).ToList(); }
            }
            bool haveHost = rows.Count >= 100;
            var gaps = new List<double>(); int pauses = 0; double longestPause = 0; double period = double.NaN; int late = 0, streak = 0, longest = 0; double wStart = double.NaN, wEnd = double.NaN;
            var lateIdx = new List<int>();     // index into rows of the frame that ended a late gap
            if (haveHost)
            {
                var t = hf.Get("t_send");
                // a gap over PauseMs is a pause (loading, a menu, another game starting), not a late frame: counted apart, out of the percentiles
                var gapRow = new List<int>();
                for (int k = 1; k < rows.Count; k++)
                {
                    double g = (t[rows[k]] - t[rows[k - 1]]) * 1000.0;
                    if (g > PauseMs) { pauses++; longestPause = Math.Max(longestPause, g); continue; }
                    gaps.Add(g); gapRow.Add(k);
                }
                period = P(gaps, 0.5);
                for (int k = 0; k < gaps.Count; k++)
                {
                    if (gaps[k] > LateFactor * period) { late++; streak++; longest = Math.Max(longest, streak); lateIdx.Add(gapRow[k]); } else streak = 0;
                }
                wStart = t[rows[0]]; wEnd = t[rows[rows.Count - 1]];
            }
            double lateShare = haveHost ? (double)late / Math.Max(1, gaps.Count) : double.NaN;

            // ---- per-stage times and the stage that made each late gap
            // The wait for the encoder is measured from the host's boundary (when it picks the frame) for EVERY frame: a generated or re-stamped frame has the arrival of its SOURCE frame in t_arrival
            // (game cadence, not lateness), and for a game frame the time from arrival to the boundary is the deliberate wait for the display clock. "Upstream" is the game's own arrival gap for a
            // game frame and the host's boundary slip for a generated or re-stamped one.
            List<double> queue = new List<double>(), qb = new List<double>(), enc = new List<double>(), hold = new List<double>(), tail = new List<double>();
            double qbMed = 0, eMed = 0, hMed = 0, tMed = 0;
            int app = 0, gen = 0, restamp = 0, green = 0;
            if (haveHost)
            {
                var ta = hf.Get("t_arrival"); var tb = hf.Get("t_boundary"); var tes = hf.Get("t_encode_start"); var em = hf.Get("encode_ms"); var hm = hf.Get("hold_ms"); var slot = hf.Get("slot"); var tsd = hf.Get("t_send");
                bool hasB = hf.Has("t_boundary") && hf.Has("t_encode_start");
                foreach (var i in rows)
                {
                    bool isApp = !hf.Has("slot") || slot[i] >= 0;
                    if (hf.Has("t_arrival") && hf.Has("t_encode_start") && isApp && ta[i] > 0) queue.Add((tes[i] - ta[i]) * 1000.0);
                    if (hasB) qb.Add((tes[i] - tb[i]) * 1000.0);
                    if (hf.Has("encode_ms")) enc.Add(em[i]);
                    if (hf.Has("hold_ms")) hold.Add(hm[i]);
                    if (hasB && hf.Has("encode_ms")) tail.Add((tsd[i] - tes[i]) * 1000.0 - em[i]);
                    if (hf.Has("slot")) { double sl = slot[i]; if (sl >= 0) app++; else if (sl == -2) gen++; else if (sl == -3) restamp++; else green++; }
                }
                qbMed = P(qb, 0.5); eMed = P(enc, 0.5); hMed = P(hold, 0.5); tMed = P(tail, 0.5);
                if (double.IsNaN(qbMed)) qbMed = 0; if (double.IsNaN(eMed)) eMed = 0; if (double.IsNaN(hMed)) hMed = 0; if (double.IsNaN(tMed)) tMed = 0;
            }
            var classCount = new Dictionary<string, int> { { "Upstream (the game or runtime delivered late)", 0 }, { "Waiting for the encoder (queue)", 0 }, { "Encode took long", 0 }, { "Release hold", 0 }, { "After the encode (sender, socket)", 0 }, { "Unattributed", 0 } };
            var keys = classCount.Keys.ToList();
            var worst = new List<WorstFrame>();
            if (haveHost)
            {
                var t = hf.Get("t_send"); var ta = hf.Get("t_arrival"); var tb = hf.Get("t_boundary"); var tes = hf.Get("t_encode_start"); var em = hf.Get("encode_ms"); var hm = hf.Get("hold_ms"); var fr = hf.Get("frame"); var slot = hf.Get("slot");
                bool hasB = hf.Has("t_boundary") && hf.Has("t_encode_start");
                foreach (var k in lateIdx)
                {
                    int i = rows[k], p = rows[k - 1];
                    double gap = (t[i] - t[p]) * 1000.0, over = gap - period;
                    bool appPair = !hf.Has("slot") || (slot[i] >= 0 && slot[p] >= 0);
                    double eu = 0;
                    if (appPair) { if (ta.Count > 0 && ta[i] > 0 && ta[p] > 0) eu = (ta[i] - ta[p]) * 1000.0 - period; }
                    else if (hf.Has("t_boundary")) eu = (tb[i] - tb[p]) * 1000.0 - period;
                    double eq = hasB ? (tes[i] - tb[i]) * 1000.0 - qbMed : 0;
                    double ee = em.Count > 0 ? em[i] - eMed : 0;
                    double eh = hm.Count > 0 ? hm[i] - hMed : 0;
                    double et = hasB && em.Count > 0 ? (t[i] - tes[i]) * 1000.0 - em[i] - tMed : 0;
                    var ex = new[] { eu, eq, ee, eh, et };
                    int best = 0; for (int x = 1; x < 5; x++) if (ex[x] > ex[best]) best = x;
                    int cls = ex[best] >= 0.25 * over ? best : 5;
                    classCount[keys[cls]]++;
                    worst.Add(new WorstFrame { Frame = fr.Count > i ? (long)fr[i] : -1, T = t[i], GapMs = gap, QueueMs = hasB ? (tes[i] - tb[i]) * 1000.0 : double.NaN, EncodeMs = em.Count > 0 ? em[i] : double.NaN, HoldMs = hm.Count > 0 ? hm[i] : double.NaN, Class = keys[cls] });
                }
                R.Worst.AddRange(worst.OrderByDescending(w => w.GapMs).Take(10));
            }

            double gpuP95 = double.NaN;
            if (gpu != null && gpu.Rows >= 3 && gpu.Has("gpu_util_pct")) { var gu = gpu.Get("gpu_util_pct"); var gq = gpu.Get("sample_qpc"); gpuP95 = P(Enumerable.Range(0, gu.Count).Where(i => (double.IsNaN(from) || gq[i] >= from) && (double.IsNaN(to) || gq[i] <= to)).Select(i => gu[i]).ToList(), 0.95); }

            // the worst 10 s windows of the recording: for a long one, WHEN it went bad
            if (haveHost)
            {
                var wt = hf.Get("t_send"); var wins = new Dictionary<int, int[]>();
                var gapAt = new List<double>(); for (int k = 1; k < rows.Count; k++) { double gg = (wt[rows[k]] - wt[rows[k - 1]]) * 1000.0; if (gg <= PauseMs) gapAt.Add(wt[rows[k]]); }
                for (int g = 0; g < gaps.Count && g < gapAt.Count; g++)
                {
                    int w = (int)((gapAt[g] - wStart) / 10.0); int[] c; if (!wins.TryGetValue(w, out c)) wins[w] = c = new int[2];
                    c[0]++; if (gaps[g] > LateFactor * period) c[1]++;
                }
                foreach (var kv in wins.Where(kv => kv.Value[0] >= 100 && kv.Value[1] > 0).OrderByDescending(kv => (double)kv.Value[1] / kv.Value[0]).Take(5).OrderBy(kv => kv.Key))
                    R.Hotspots.Add(TimeSpan.FromSeconds(kv.Key * 10.0 + (double.IsNaN(t0) ? 0 : wStart - t0)).ToString(@"h\:mm\:ss") + " in: " + Pct((double)kv.Value[1] / kv.Value[0]) + " late (" + kv.Value[1] + " of " + kv.Value[0] + " frames)");
            }

            // ---- findings
            Func<string, string, string, string, string, Finding> add = (id, st, title, detail, test) => { var f = new Finding { Id = id, Status = st, Title = title, Detail = detail, Test = test }; R.Findings.Add(f); return f; };

            bool probeOk = health != null && health.Rows >= 3;
            long werr = health != null && health.Has("write_errors") ? (long)(Max(health.Get("write_errors")) is double m1 && !double.IsNaN(m1) ? m1 : 0) : 0;
            long serr = health != null && health.Has("sample_errors") ? (long)(Max(health.Get("sample_errors")) is double m2 && !double.IsNaN(m2) ? m2 : 0) : 0;
            double nvmlStall = health != null ? Max(health.Get("max_tick_ms_gpu")) : double.NaN;

            if (!haveHost)
                add("T01", "INSUFFICIENT", "No stream record in this session", hf == null ? "host_frames.csv is missing: the streamer of this session was not the telemetry build, or debug logging was off." : "fewer than 100 sent frames after the " + N(warmupSeconds, "F0") + " s warm-up (" + rows.Count + ").", "Run the capture again with the Bench streamer and a game running for at least 30 s.");
            else
            {
                add("T01", "OK", "Stream record intact", rows.Count + " sent frames in " + N(wEnd - wStart, "F1") + " s (idle frames left out), nominal period " + N(period, "F2") + " ms (" + N(1000.0 / period, "F1") + " Hz)." + (pauses > 0 ? " " + pauses + " pause(s) over " + N(PauseMs, "F0") + " ms, longest " + N(longestPause, "F0") + " ms, not counted as late frames." : ""), "");

                // deadlines
                string cadence = "gap p50 " + N(P(gaps, 0.5), "F2") + ", p95 " + N(P(gaps, 0.95), "F2") + ", p99 " + N(P(gaps, 0.99), "F2") + ", max " + N(Max(gaps), "F2") + " ms; late frames " + late + " of " + gaps.Count + " (" + Pct(lateShare) + "), longest run " + longest + ".";
                if (lateShare <= MaxLateShare) add("T02", "OK", "Deadlines met", cadence + " Threshold: " + Pct(MaxLateShare) + " of gaps over " + N(LateFactor, "F1") + " periods.", "");
                else add("T02", "PROBLEM", "Late frames: " + Pct(lateShare), cadence + " Threshold: " + Pct(MaxLateShare) + ".", "Compare the stage that made the late frames (below), then change one thing and capture again.");

                // who made them
                if (late >= 5)
                {
                    var top = classCount.OrderByDescending(kv => kv.Value).First();
                    double share = (double)top.Value / late;
                    string parts = string.Join("; ", classCount.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => kv.Key + ": " + kv.Value));
                    string fgNote = (gen + restamp) > 0 && app == 0 ? " (frame generation was on: here upstream is the host's own display-clock slip, not the game)" : "";
                    string test = top.Key.StartsWith("Upstream") ? (!double.IsNaN(gpuP95) && gpuP95 >= 95 ? "The GPU was at " + N(gpuP95, "F0") + " % (p95) and the stream waited for the game: the game is GPU-bound. Lower its settings (DLSS quality, ray tracing, resolution), then record again." : "The stream waited for the game: lower its settings or cap its fps, then record again.") :
                                  top.Key.StartsWith("Waiting") ? "The encoder was busy when the frame arrived: try a faster preset or a lower resolution scale and capture again." :
                                  top.Key.StartsWith("Encode") ? "Try a faster preset, lower bitrate or resolution scale, and capture again." :
                                  top.Key.StartsWith("Release") ? "The deliberate hold before the send was long: check the pacing settings." :
                                  top.Key.StartsWith("After the encode") ? "The send itself was late after a finished encode: look at the network and the sender (a recording with the headset on Ethernet)." :
                                  "No stage stands out: the lateness is spread over the stages.";
                    add("T03", share >= 0.5 ? "PROBLEM" : "WATCH", "Late gaps: mostly " + top.Key.ToLowerInvariant(), parts + fgNote + ". Each late gap is attributed to the stage whose time exceeded its own median by the most (and by at least a quarter of the lateness).", test);
                }
                else add("T03", "OK", "Too few late frames to attribute", late + " late gap(s).", "");

            }

            // ---- the resources, in bottleneck order (GPU, CPU, memory and I/O, network, VR rendering): rows with an average and a worst value, a status per category
            var cx = new Ctx { Dir = dir, Hf = hf, Cg = cg, Gpu = gpu, Sys = sys, Eng = eng, Proc = proc, Rows = rows, HaveHost = haveHost, Period = period, LateShare = lateShare, Late = late, Gaps = gaps,
                Enc = enc, Qb = qb, From = from, To = to, App = app, Gen = gen, Restamp = restamp, Green = green, ClassCount = classCount, GpuP95 = gpuP95, Pauses = pauses, StartQpc = t0 };
            BuildCategories(R, cx);
            BuildSeries(R, cx, wStart, wEnd, t0);


            // ---- the recorder's own health
            if (probeOk)
            {
                if (werr > 0) add("T10", "PROBLEM", "The recorder lost data", werr + " write error(s): treat the hardware columns of this session as incomplete.", "");
                else if (nvmlStall > 500) add("T10", "WATCH", "NVML stalled for " + N(nvmlStall, "F0") + " ms", "The sensor thread was blocked once or more (the driver can do this); hardware rows have a gap there, nothing else waited for it. Sample errors: " + serr + ".", "");
                else add("T10", "OK", "Recorder healthy", "no write errors, NVML max tick " + N(nvmlStall, "F1") + " ms, probe CPU p95 " + N(P(health.Get("probe_process_cpu_pct"), 0.95), "F1") + " % of one core.", "");
            }
            else add("T10", "INSUFFICIENT", "No recorder health", "logger_health.csv is missing or too short.", "");

            add("T11", "OK", "How to read the counters", "Streamed fps is not game fps; the NIC figure is all adapters, not the stream; GPU util is a window average, not what one frame saw; a limiter flag says the limiter was on, not that it caused a late frame.", "");

            // ---- verdict: the first category, in bottleneck order, that has evidence; "smooth" when the stream is smooth whatever the sensors say
            if (!haveHost) { R.VerdictStatus = "INSUFFICIENT"; R.Verdict = "Not enough data: " + R.Findings.First().Detail; }
            else
            {
                var cause = R.Categories.Where(c => c.Name != "VR rendering").FirstOrDefault(c => c.Status == "PROBLEM");
                var closest = R.Categories.Where(c => c.Name != "VR rendering").FirstOrDefault(c => c.Status == "WATCH");
                bool lateIssue = lateShare > MaxLateShare;
                if (!lateIssue)
                {
                    R.VerdictStatus = closest != null ? "WATCH" : "OK";
                    R.Verdict = "Smooth: " + Pct(lateShare) + " late frames" + (closest != null ? ". Closest to a limit: " + closest.Name + " (" + closest.Summary + ")" : "");
                }
                else if (cause != null) { R.VerdictStatus = "PROBLEM"; R.Verdict = "Late frames " + Pct(lateShare) + ". " + cause.Name + ": " + cause.Summary; }
                else if (closest != null) { R.VerdictStatus = "PROBLEM"; R.Verdict = "Late frames " + Pct(lateShare) + ". No resource is saturated; closest: " + closest.Name + " (" + closest.Summary + ")"; }
                else
                {
                    var t3 = R.Findings.FirstOrDefault(f => f.Id == "T03");
                    R.VerdictStatus = "PROBLEM"; R.Verdict = "Late frames " + Pct(lateShare) + ". No resource stands out" + (t3 != null ? "; " + t3.Title.ToLowerInvariant() : "") + ".";
                }
            }

            // the recorder's own health outranks a good reading: hardware rows that are incomplete or have a gap are said first
            var t10 = R.Findings.FirstOrDefault(f => f.Id == "T10");
            if (t10 != null && t10.Status == "PROBLEM") { R.VerdictStatus = "PROBLEM"; R.Verdict = t10.Title + ": " + t10.Detail + " (" + R.Verdict + ")"; }
            else if (t10 != null && t10.Status == "WATCH" && R.VerdictStatus == "OK") { R.VerdictStatus = "WATCH"; R.Verdict += " (" + t10.Title + ": the hardware rows have a gap there)"; }

            R.Tiles.Add(new KeyValuePair<string, string>("Streamed fps", haveHost ? N(1000.0 / period, "F1") : "n/a"));
            R.Tiles.Add(new KeyValuePair<string, string>("Late frames", Pct(lateShare)));
            R.Tiles.Add(new KeyValuePair<string, string>("Encode p99", haveHost ? N(P(enc, 0.99), "F1") + " ms" : "n/a"));
            R.Tiles.Add(new KeyValuePair<string, string>("Worst gap", haveHost ? N(Max(gaps), "F1") + " ms" : "n/a"));
            R.Markdown = Render(R, dir, rows.Count, period, warmupSeconds, queue, qb, enc, hold, tail, gaps);
            return R;
        }

        static double Min(List<double> v) { var s = v.Where(x => !double.IsNaN(x)).ToList(); return s.Count == 0 ? double.NaN : s.Min(); }

        static string Render(ReportData R, string dir, int frames, double period, double warmup, List<double> queue, List<double> qb, List<double> enc, List<double> hold, List<double> tail, List<double> gaps)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# AirFrameXR Bench report");
            sb.AppendLine();
            sb.AppendLine("**" + R.VerdictStatus + "**: " + R.Verdict);
            sb.AppendLine();
            sb.AppendLine("Session: `" + Path.GetFileName(dir.TrimEnd('\\', '/')) + "`, warm-up excluded: " + warmup.ToString("F0", C) + " s. Percentiles: nearest rank. Thresholds are heuristics named in each finding.");
            sb.AppendLine();
            sb.AppendLine("## Resources, in bottleneck order");
            sb.AppendLine("The worst value is defined per row: the 99th percentile of a load, the lowest value of a clock or of free memory.");
            foreach (var c in R.Categories)
            {
                sb.AppendLine();
                sb.AppendLine("### " + c.Name + " - " + c.Status + (c.Summary.Length > 0 ? ": " + c.Summary : ""));
                if (c.Rows.Count > 0)
                {
                    sb.AppendLine("| metric | average | worst | note |");
                    sb.AppendLine("|---|---:|---:|---|");
                    foreach (var r in c.Rows) sb.AppendLine("| " + r.Name + " | " + r.Average + " | " + r.Worst + " | " + (r.Note ?? "") + " |");
                }
            }
            sb.AppendLine();
            sb.AppendLine("## Findings");
            foreach (var f in R.Findings)
            {
                sb.AppendLine("- **" + f.Id + " " + f.Status + "** " + f.Title);
                if (!string.IsNullOrEmpty(f.Detail)) sb.AppendLine("  - " + f.Detail);
                if (!string.IsNullOrEmpty(f.Test)) sb.AppendLine("  - Suggested test: " + f.Test);
            }
            if (frames > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Stages of the sent frames (ms)");
                sb.AppendLine("| stage | p50 | p95 | p99 | max |");
                sb.AppendLine("|---|---:|---:|---:|---:|");
                Action<string, List<double>> row = (n, v) => { if (v.Count > 0) sb.AppendLine("| " + n + " | " + N(P(v, 0.5), "F2") + " | " + N(P(v, 0.95), "F2") + " | " + N(P(v, 0.99), "F2") + " | " + N(Max(v), "F2") + " |"); };
                row("gap between sent frames", gaps);
                row("boundary to encode start (waiting for the encoder, all frames)", qb);
                row("encode", enc);
                row("release hold (deliberate)", hold);
                row("encode end to send (sender)", tail);
                row("arrival to encode start (game frames only; includes the wait for the display clock)", queue);
            }
            if (R.Hotspots.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Worst 10 s windows (time since the recording started)");
                foreach (var h in R.Hotspots) sb.AppendLine("- " + h);
            }
            if (R.Worst.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Worst late frames");
                sb.AppendLine("| frame | send time (QPC s) | gap ms | queue ms | encode ms | hold ms | stage |");
                sb.AppendLine("|---:|---:|---:|---:|---:|---:|---|");
                foreach (var w in R.Worst) sb.AppendLine("| " + w.Frame + " | " + w.T.ToString("F3", C) + " | " + N(w.GapMs, "F2") + " | " + N(w.QueueMs, "F2") + " | " + N(w.EncodeMs, "F2") + " | " + N(w.HoldMs, "F2") + " | " + w.Class + " |");
            }
            return sb.ToString();
        }
    }
}
