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

    public sealed class ReportData
    {
        public string VerdictStatus = "INSUFFICIENT", Verdict = "";
        public readonly List<Finding> Findings = new List<Finding>();
        public readonly List<WorstFrame> Worst = new List<WorstFrame>();
        public readonly List<KeyValuePair<string, string>> Tiles = new List<KeyValuePair<string, string>>();
        public string Markdown = "";
    }

    public static class Report
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

        static double StartQpc(string dir)
        {
            try
            {
                var s = File.ReadAllText(Path.Combine(dir, "session.json"));
                int i = s.IndexOf("\"started_qpc_s\":"); if (i < 0) return double.NaN;
                i += 16; int j = s.IndexOfAny(new[] { ',', '\n' }, i);
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
            var health = Read(Path.Combine(dir, "logger_health.csv"));
            double t0 = StartQpc(dir);
            double from = double.IsNaN(t0) ? double.NaN : t0 + warmupSeconds;

            // ---- host frames in the window
            var rows = new List<int>();
            if (hf != null && hf.Has("t_send"))
            {
                var ts = hf.Get("t_send");
                var sl = hf.Has("slot") ? hf.Get("slot") : null;
                for (int i = 0; i < ts.Count; i++) if (!double.IsNaN(ts[i]) && (double.IsNaN(from) || ts[i] >= from) && (sl == null || sl[i] != -1)) rows.Add(i);   // the idle (green) frames are not the game's stream
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
            List<double> queue = new List<double>(), enc = new List<double>(), hold = new List<double>(), arrGap = new List<double>();
            double qMed = 0, eMed = 0, hMed = 0;
            int app = 0, gen = 0, restamp = 0, green = 0;
            if (haveHost)
            {
                var ta = hf.Get("t_arrival"); var tes = hf.Get("t_encode_start"); var em = hf.Get("encode_ms"); var hm = hf.Get("hold_ms"); var slot = hf.Get("slot");
                foreach (var i in rows)
                {
                    if (hf.Has("t_arrival") && hf.Has("t_encode_start") && ta[i] > 0) queue.Add((tes[i] - ta[i]) * 1000.0);
                    if (hf.Has("encode_ms")) enc.Add(em[i]);
                    if (hf.Has("hold_ms")) hold.Add(hm[i]);
                    if (hf.Has("slot")) { double s = slot[i]; if (s >= 0) app++; else if (s == -2) gen++; else if (s == -3) restamp++; else green++; }
                }
                qMed = P(queue, 0.5); eMed = P(enc, 0.5); hMed = P(hold, 0.5);
                if (double.IsNaN(qMed)) qMed = 0; if (double.IsNaN(eMed)) eMed = 0; if (double.IsNaN(hMed)) hMed = 0;
            }
            var classCount = new Dictionary<string, int> { { "Upstream (the game or runtime delivered late)", 0 }, { "Waiting for the encoder (queue)", 0 }, { "Encode took long", 0 }, { "Release hold", 0 }, { "After the send (sender, socket, unattributed)", 0 } };
            var keys = classCount.Keys.ToList();
            var worst = new List<WorstFrame>();
            if (haveHost)
            {
                var t = hf.Get("t_send"); var ta = hf.Get("t_arrival"); var tes = hf.Get("t_encode_start"); var em = hf.Get("encode_ms"); var hm = hf.Get("hold_ms"); var fr = hf.Get("frame");
                foreach (var k in lateIdx)
                {
                    int i = rows[k], p = rows[k - 1];
                    double gap = (t[i] - t[p]) * 1000.0, over = gap - period;
                    double eu = (ta.Count > 0 && ta[i] > 0 && ta[p] > 0) ? (ta[i] - ta[p]) * 1000.0 - period : 0;
                    double eq = (ta.Count > 0 && ta[i] > 0) ? (tes[i] - ta[i]) * 1000.0 - qMed : 0;
                    double ee = em.Count > 0 ? em[i] - eMed : 0;
                    double eh = hm.Count > 0 ? hm[i] - hMed : 0;
                    var ex = new[] { eu, eq, ee, eh };
                    int best = 0; for (int x = 1; x < 4; x++) if (ex[x] > ex[best]) best = x;
                    int cls = ex[best] >= 0.25 * over ? best : 4;
                    classCount[keys[cls]]++;
                    worst.Add(new WorstFrame { Frame = fr.Count > i ? (long)fr[i] : -1, T = t[i], GapMs = gap, QueueMs = ta.Count > 0 && ta[i] > 0 ? (tes[i] - ta[i]) * 1000.0 : double.NaN, EncodeMs = em.Count > 0 ? em[i] : double.NaN, HoldMs = hm.Count > 0 ? hm[i] : double.NaN, Class = keys[cls] });
                }
                R.Worst.AddRange(worst.OrderByDescending(w => w.GapMs).Take(10));
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
                    string test = top.Key.StartsWith("Upstream") ? "Lower the game's settings or cap its fps, then capture again: the stream waited for the game." :
                                  top.Key.StartsWith("Waiting") ? "The encoder was busy when the frame arrived: try a faster preset or a lower resolution scale and capture again." :
                                  top.Key.StartsWith("Encode") ? "Try a faster preset, lower bitrate or resolution scale, and capture again." :
                                  top.Key.StartsWith("Release") ? "The deliberate hold before the send was long: check the pacing settings." :
                                  "The host sent late after the encode: look at the network and the sender (a capture with the headset on Ethernet).";
                    add("T03", share >= 0.5 ? "PROBLEM" : "WATCH", "Late gaps: mostly " + top.Key.ToLowerInvariant(), parts + ". Each late gap is attributed to the stage whose time exceeded its own median by the most (and by at least a quarter of the lateness).", test);
                }
                else add("T03", "OK", "Too few late frames to attribute", late + " late gap(s).", "");

                // encoder budget
                if (enc.Count > 0)
                {
                    double e99 = P(enc, 0.99), e50 = P(enc, 0.5);
                    string d = "encode ms p50 " + N(e50, "F2") + ", p95 " + N(P(enc, 0.95), "F2") + ", p99 " + N(e99, "F2") + ", max " + N(Max(enc), "F2") + " (period " + N(period, "F2") + " ms, budget " + N(EncodeBudget * 100, "F0") + " % of it).";
                    if (e99 > EncodeBudget * period) add("T04", "PROBLEM", "The encoder uses most of the frame period", d, "Try a faster NVENC preset or a lower resolution scale.");
                    else add("T04", "OK", "Encode fits the period", d, "");
                }

                // generated / repeated content (context, never a verdict)
                if (hf.Has("slot") && rows.Count > 0)
                    add("T05", "OK", "What the stream carried", "game frames " + app + " (" + Pct((double)app / rows.Count) + "), generated " + gen + ", re-stamped " + restamp + ", idle " + green + ". These are STREAMED frames: this does not say how often the game rendered new content.", "");
            }

            // ---- client
            if (cg != null && cg.Rows >= 100 && cg.Has("total_ms"))
            {
                var tr = cg.Get("t_recv");
                var idx = Enumerable.Range(0, cg.Rows).Where(i => double.IsNaN(from) || tr[i] >= from).ToList();
                Func<string, List<double>> g = c => idx.Select(i => cg.Get(c)[i]).ToList();
                double dec95 = P(g("decode_ms"), 0.95), net95 = P(g("network_ms"), 0.95), q95 = P(g("decoder_queue_ms"), 0.95), tot95 = P(g("total_ms"), 0.95);
                string d = "pipeline latency p50 " + N(P(g("total_ms"), 0.5)) + " / p95 " + N(tot95) + " ms; encode p95 " + N(P(g("encode_ms"), 0.95)) + ", network p95 " + N(net95) + ", decode p95 " + N(dec95) + ", decoder queue p95 " + N(q95) + ", vsync queue p95 " + N(P(g("vsync_queue_ms"), 0.95)) + " ms; headset fps median " + N(P(g("client_fps"), 0.5)) + ", bitrate mean " + N(Mean(g("bitrate_mbps"))) + " Mbps (headset's own statistics).";
                bool bad = !double.IsNaN(period) && (dec95 > period || net95 > 2 * period || q95 > period);
                add("T06", bad ? "WATCH" : "OK", bad ? "The headset side is slow at the tail" : "Headset side keeps up", d, bad ? "Decode over one period or network over two: check the bitrate, the Wi-Fi band and the headset's load." : "");
            }
            else add("T06", "INSUFFICIENT", "No headset report", cg == null ? "client_graph.csv is missing (no headset connected during the capture, or the telemetry build was not used)." : "fewer than 100 headset reports.", "");

            // ---- hardware context
            if (gpu != null && gpu.Rows >= 3)
            {
                var gi = Enumerable.Range(0, gpu.Rows).Where(i => double.IsNaN(from) || gpu.Get("sample_qpc")[i] >= from).ToList();
                if (gi.Count < 3) gi = Enumerable.Range(0, gpu.Rows).ToList();
                Func<string, List<double>> G = c => gi.Select(i => gpu.Get(c)[i]).ToList();
                var thr = gpu.Raw["throttle_reasons"]; int active = 0, counted = 0; ulong seen = 0;
                foreach (var i in gi) { var s = thr[i]; if (s.StartsWith("0x")) { ulong b; if (ulong.TryParse(s.Substring(2), NumberStyles.HexNumber, C, out b)) { counted++; ulong lim = b & ~0x1UL & ~0x4000000000000000UL; if ((lim & ~0x1UL) != 0) { active++; seen |= lim; } } } }
                double clkMax = Max(G("core_clock_mhz")), clkMin = G("core_clock_mhz").Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Min();
                string d = "GPU util p50 " + N(P(G("gpu_util_pct"), 0.5), "F0") + " / p95 " + N(P(G("gpu_util_pct"), 0.95), "F0") + " %; core clock " + N(clkMin, "F0") + "-" + N(clkMax, "F0") + " MHz (p50 " + N(P(G("core_clock_mhz"), 0.5), "F0") + "); power max " + N(Max(G("board_power_w"))) + " W; temp max " + N(Max(G("temp_c")), "F0") + " C; VRAM max " + N(Max(G("vram_used_bytes")) / 1073741824.0, "F1") + " of " + N(Max(G("vram_total_bytes")) / 1073741824.0, "F0") + " GiB; NVENC util p95 " + N(P(G("nvenc_util_pct"), 0.95), "F0") + " %, sessions max " + N(Max(G("nvenc_sessions")), "F0") + ".";
                double activeShare = counted > 0 ? (double)active / counted : double.NaN;
                if (counted > 0 && activeShare >= 0.10)
                    add("T07", "WATCH", "A GPU limiter was active " + Pct(activeShare) + " of the time", d + " Limiter: " + Name(seen) + ". A 1 Hz sensor says the limiter was on, not that it caused a late frame.", "Capture again with the limiter removed (power or cooling) to see whether the late frames follow it.");
                else add("T07", "OK", "No sustained GPU limiter", d, "");
            }
            else add("T07", "INSUFFICIENT", "No GPU sensor data", "hardware_gpu.csv is missing or empty (NVML not available).", "");

            if (sys != null && sys.Rows >= 3)
            {
                Func<string, List<double>> S = c => sys.Get(c);
                add("T08", "OK", "System context", "CPU total p50 " + N(P(S("cpu_total_pct"), 0.5), "F0") + " / p95 " + N(P(S("cpu_total_pct"), 0.95), "F0") + " %; RAM available min " + N(Min(S("ram_available_bytes")) / 1073741824.0, "F1") + " GiB; page faults p95 " + N(P(S("page_faults_per_s"), 0.95), "F0") + "/s; NIC (all adapters, not the stream's own traffic) peak " + N(Max(S("nic_bytes_per_s")) * 8 / 1e6, "F0") + " Mbps; disk busy p95 " + N(P(S("disk_busy_pct"), 0.95), "F0") + " %.", "");
            }
            if (eng != null && eng.Rows > 0 && eng.Has("util_pct"))
            {
                var by = new Dictionary<string, List<double>>();
                for (int i = 0; i < eng.Rows; i++) { string key = eng.Raw["process"][i] + " " + eng.Raw["engine"][i]; List<double> l; if (!by.TryGetValue(key, out l)) by[key] = l = new List<double>(); l.Add(eng.Get("util_pct")[i]); }
                var show = by.Where(kv => Mean(kv.Value) >= 0.5).OrderByDescending(kv => Mean(kv.Value)).Take(8).Select(kv => kv.Key + " mean " + N(Mean(kv.Value)) + " / p95 " + N(P(kv.Value, 0.95)) + " %");
                add("T09", "OK", "GPU engines per process", show.Any() ? string.Join("; ", show) : "no engine above 0.5 % mean for the named processes.", "");
            }

            // ---- the recorder's own health
            if (probeOk)
            {
                if (werr > 0) add("T10", "PROBLEM", "The recorder lost data", werr + " write error(s): treat the hardware columns of this session as incomplete.", "");
                else if (nvmlStall > 500) add("T10", "WATCH", "NVML stalled for " + N(nvmlStall, "F0") + " ms", "The sensor thread was blocked once or more (the driver can do this); hardware rows have a gap there, nothing else waited for it. Sample errors: " + serr + ".", "");
                else add("T10", "OK", "Recorder healthy", "no write errors, NVML max tick " + N(nvmlStall, "F1") + " ms, probe CPU p95 " + N(P(health.Get("probe_process_cpu_pct"), 0.95), "F1") + " % of one core.", "");
            }
            else add("T10", "INSUFFICIENT", "No recorder health", "logger_health.csv is missing or too short.", "");

            add("T11", "OK", "How to read the counters", "Streamed fps is not game fps; the NIC figure is all adapters, not the stream; GPU util is a window average, not what one frame saw; a limiter flag says the limiter was on, not that it caused a late frame.", "");

            // ---- verdict
            var prob = R.Findings.FirstOrDefault(f => f.Status == "PROBLEM");
            var watch = R.Findings.FirstOrDefault(f => f.Status == "WATCH");
            if (!haveHost) { R.VerdictStatus = "INSUFFICIENT"; R.Verdict = "Not enough data: " + R.Findings.First().Detail; }
            else if (prob != null) { R.VerdictStatus = "PROBLEM"; R.Verdict = prob.Title + (R.Findings.Any(f => f.Id == "T03" && f.Status != "OK") ? " - " + R.Findings.First(f => f.Id == "T03").Title : ""); }
            else if (watch != null) { R.VerdictStatus = "WATCH"; R.Verdict = "Smooth enough (" + Pct(lateShare) + " late frames), one thing to watch: " + watch.Title; }
            else { R.VerdictStatus = "OK"; R.Verdict = "Smooth: " + Pct(lateShare) + " late frames, encode p99 " + N(P(enc, 0.99), "F1") + " ms"; }

            R.Tiles.Add(new KeyValuePair<string, string>("Streamed fps", haveHost ? N(1000.0 / period, "F1") : "n/a"));
            R.Tiles.Add(new KeyValuePair<string, string>("Late frames", Pct(lateShare)));
            R.Tiles.Add(new KeyValuePair<string, string>("Encode p99", haveHost ? N(P(enc, 0.99), "F1") + " ms" : "n/a"));
            R.Tiles.Add(new KeyValuePair<string, string>("Worst gap", haveHost ? N(Max(gaps), "F1") + " ms" : "n/a"));
            R.Markdown = Render(R, dir, rows.Count, period, warmupSeconds, queue, enc, hold, gaps);
            return R;
        }

        static double Min(List<double> v) { var s = v.Where(x => !double.IsNaN(x)).ToList(); return s.Count == 0 ? double.NaN : s.Min(); }

        static string Render(ReportData R, string dir, int frames, double period, double warmup, List<double> queue, List<double> enc, List<double> hold, List<double> gaps)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# AirFrameXR Bench report");
            sb.AppendLine();
            sb.AppendLine("**" + R.VerdictStatus + "**: " + R.Verdict);
            sb.AppendLine();
            sb.AppendLine("Session: `" + Path.GetFileName(dir.TrimEnd('\\', '/')) + "`, warm-up excluded: " + warmup.ToString("F0", C) + " s. Percentiles: nearest rank. Thresholds are heuristics named in each finding.");
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
                row("arrival to encode start (queue age)", queue);
                row("encode", enc);
                row("release hold (deliberate)", hold);
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
