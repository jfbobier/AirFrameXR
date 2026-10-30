// The resource table of the Black Box report (docs/BENCH_UI.md): GPU, CPU, memory and I/O, network, VR rendering, in the order a bottleneck is looked for. Each row has an average and a
// worst value (the 99th percentile of a load, the lowest value of a clock or of free memory); each category a status and a one-line reading. A category is a PROBLEM only when the stream
// really had late frames AND this resource shows the evidence for them; a resource near a limit while the stream is smooth is only a WATCH. Percentiles: nearest rank.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AirFrameXR.Perf
{
    internal sealed class Ctx
    {
        public string Dir; public object Hf, Cg, Gpu, Sys, Eng, Proc;
        public List<int> Rows; public bool HaveHost; public double Period, LateShare, From, To, GpuP95, StartQpc = double.NaN; public int Late, App, Gen, Restamp, Green, Pauses;
        public List<double> Gaps, Enc, Qb; public Dictionary<string, int> ClassCount;
    }

    public static partial class Report
    {
        static string F1(double v) { return N(v, "F1"); }
        static string F0(double v) { return N(v, "F0"); }

        // a table's column restricted to the recording window (the time column says which rows)
        static List<double> Win(Table t, string col, string timeCol, double from, double to)
        {
            var o = new List<double>(); if (t == null || !t.Has(col) || !t.Has(timeCol)) return o;
            var v = t.Get(col); var tm = t.Get(timeCol);
            for (int i = 0; i < v.Count; i++) if (!double.IsNaN(v[i]) && (double.IsNaN(from) || tm[i] >= from) && (double.IsNaN(to) || tm[i] <= to)) o.Add(v[i]);
            return o;
        }

        // rows of a (sample, process, engine) table for one process and engine, in the window
        static List<double> WinBy(Table t, string process, string engine, string col, string timeCol, double from, double to, out double instances)
        {
            instances = 0; var o = new List<double>(); if (t == null || !t.Has(col) || !t.Has("process") || !t.Has(timeCol)) return o;
            var v = t.Get(col); var tm = t.Get(timeCol); var pr = t.Raw["process"]; var en = t.Has("engine") ? t.Raw["engine"] : null; var ins = t.Has("instances") ? t.Get("instances") : null;
            for (int i = 0; i < v.Count; i++)
            {
                if (pr[i] != process || (engine != null && en != null && en[i] != engine)) continue;
                if ((!double.IsNaN(from) && tm[i] < from) || (!double.IsNaN(to) && tm[i] > to) || double.IsNaN(v[i])) continue;
                o.Add(v[i]); if (ins != null && ins[i] > instances) instances = ins[i];
            }
            return o;
        }

        // one row: the average, and the worst (p99 of a load, p1 of a "low is bad" value)
        static ResourceRow RowOf(string name, string unit, List<double> v, bool highIsBad, string fmt = "F1", string note = null, double scale = 1.0)
        {
            if (v == null || v.Count == 0) return new ResourceRow { Name = name, Average = "n/a", Worst = "n/a", Note = note ?? "not recorded" };
            Func<double, string> f = x => N(x * scale, fmt) + (unit.Length > 0 ? " " + unit : "");
            return new ResourceRow { Name = name, Average = f(Mean(v)), Worst = f(highIsBad ? P(v, 0.99) : P(v, 0.01)), Note = note };
        }
        static ResourceRow InfoRow(string name, string average, string worst, string note = null) { return new ResourceRow { Name = name, Average = average, Worst = worst, Note = note }; }

        static string Max2(string a, string b) { return string.CompareOrdinal(a, b) >= 0 ? a : b; }


        static void BuildCategories(ReportData R, Ctx c)
        {
            var hf = (Table)c.Hf; var cg = (Table)c.Cg; var gpu = (Table)c.Gpu; var sys = (Table)c.Sys; var eng = (Table)c.Eng; var proc = (Table)c.Proc;
            double from = c.From, to = c.To, period = c.Period;
            bool lateIssue = c.HaveHost && c.LateShare > MaxLateShare;

            // the processes: the streamer's host and the game (the other name in the session)
            string host = "alvr_host", game = null;
            try
            {
                var sj = File.ReadAllText(Path.Combine(c.Dir, "session.json")); var m = Regex.Match(sj, "\"processes\": \"([^\"]*)\"");
                if (m.Success) game = m.Groups[1].Value.Split('+').FirstOrDefault(x => x.Length > 0 && !string.Equals(x, host, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            if (game == null && eng != null && eng.Has("process")) game = eng.Raw["process"].Distinct().FirstOrDefault(x => x != host);
            if (game == null && proc != null && proc.Has("process")) game = proc.Raw["process"].Distinct().FirstOrDefault(x => x != host);

            // the streamer's own record of the run (host_events.jsonl): game fps, the stage costs of the pipeline, the status lines every 10 s
            double gameFps = double.NaN, gameFpsLow = double.NaN, streamedFps = double.NaN, composeMs = double.NaN, flowMs = double.NaN, lost = double.NaN, headsetShows = double.NaN, repeatedP90 = double.NaN;
            string fgLevel = "";
            var appFps = new List<double>(); var clientFps = new List<double>(); var streamFps = new List<double>();
            // the 10 s status lines inside the recording window (after the warm-up), by their unix time against the session's UTC start and end
            double uFrom = double.NaN, uTo = double.NaN;
            try
            {
                var sj = File.ReadAllText(Path.Combine(c.Dir, "session.json"));
                Func<string, double> utc = k => { var m = Regex.Match(sj, "\"" + k + "\": \"([^\"]+)\""); DateTime d; return m.Success && DateTime.TryParse(m.Groups[1].Value, C, DateTimeStyles.RoundtripKind, out d) ? (d.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds : double.NaN; };
                double us = utc("started_utc"), ue = utc("ended_utc");
                if (!double.IsNaN(us) && !double.IsNaN(from) && !double.IsNaN(c.StartQpc)) uFrom = us + (from - c.StartQpc); else if (!double.IsNaN(us)) uFrom = us + 10;
                uTo = ue;
            }
            catch { }
            try
            {
                var evp = Path.Combine(c.Dir, "host_events.jsonl");
                if (File.Exists(evp))
                    foreach (var line in File.ReadLines(evp))
                    {
                        if (Regex.IsMatch(line, "\"event\":\\s*\"game_run\""))
                        {
                            gameFps = Num(line, "rendered_fps"); gameFpsLow = Num(line, "rendered_fps_low"); streamedFps = Num(line, "streamed_fps"); composeMs = Num(line, "compose_ms"); flowMs = Num(line, "flow_ms");
                            lost = Num(line, "packets_lost"); headsetShows = Num(line, "displayed_fps"); repeatedP90 = Num(line, "headset_repeated_p90");
                            var lv = Regex.Match(line, "\"level\":\\s*\"([^\"]*)\""); if (lv.Success) fgLevel = lv.Groups[1].Value;
                        }
                        else if (Regex.IsMatch(line, "\"event\":\\s*\"status\""))
                        {
                            double ux = Num(line, "unix_s");
                            if (!double.IsNaN(ux) && ((!double.IsNaN(uFrom) && ux < uFrom) || (!double.IsNaN(uTo) && ux > uTo))) continue;
                            double a = Num(line, "app_fps"), s = Num(line, "stream_fps"), d = Num(line, "displayed_fps");
                            if (!double.IsNaN(a) && a > 0) appFps.Add(a); if (!double.IsNaN(s) && s > 0) streamFps.Add(s); if (!double.IsNaN(d) && d > 0) clientFps.Add(d);
                        }
                    }
            }
            catch { }

            // ---------------------------------------------------------------- GPU
            var G = new ResourceCategory { Name = "GPU" };
            var util = Win(gpu, "gpu_util_pct", "sample_qpc", from, to);
            double ins;
            var game3d = game != null ? WinBy(eng, game, "3D", "util_pct", "sample_qpc", from, to, out ins) : new List<double>();
            var host3d = WinBy(eng, host, "3D", "util_pct", "sample_qpc", from, to, out ins);
            var ofa = WinBy(eng, host, "OFA_0", "util_pct", "sample_qpc", from, to, out ins);
            double nvencInst; var nvencEng = WinBy(eng, host, "VideoEncode", "util_pct", "sample_qpc", from, to, out nvencInst);
            var nvencUtil = Win(gpu, "nvenc_util_pct", "sample_qpc", from, to);
            var clk = Win(gpu, "core_clock_mhz", "sample_qpc", from, to);
            var pwr = Win(gpu, "board_power_w", "sample_qpc", from, to);
            var tmp = Win(gpu, "temp_c", "sample_qpc", from, to);
            var vram = Win(gpu, "vram_used_bytes", "sample_qpc", from, to);
            var vramTotal = Win(gpu, "vram_total_bytes", "sample_qpc", from, to);
            G.Rows.Add(RowOf("GPU utilisation", "%", util, true, "F0"));
            if (game3d.Count > 0) G.Rows.Add(RowOf("Game: 3D engine", "%", game3d, true, "F0", game));
            if (host3d.Count > 0) G.Rows.Add(RowOf("Streamer: 3D engine (compose, frame generation)", "%", host3d, true, "F0"));
            if (ofa.Count > 0 && Mean(ofa) >= 0.5) G.Rows.Add(RowOf("Frame generation: optical flow engine", "%", ofa, true, "F0"));
            if (nvencUtil.Count > 0) G.Rows.Add(RowOf("NVENC utilisation (driver)", "%", nvencUtil, true, "F0"));
            if (nvencEng.Count > 0) G.Rows.Add(RowOf("NVENC engines (summed)", "%", nvencEng, true, "F0", nvencInst > 1 ? N(nvencInst, "F0") + " engine instances: can pass 100 %" : null));
            if (c.Enc.Count > 0 && !double.IsNaN(period))
            {
                G.Rows.Add(RowOf("Encode time per frame", "ms", c.Enc, true, "F1"));
                G.Rows.Add(RowOf("Encode window used (encode / frame period)", "%", c.Enc, true, "F0", null, 100.0 / period));
            }
            if (!double.IsNaN(composeMs)) G.Rows.Add(InfoRow("Compose pass", F1(composeMs) + " ms", "-", "the streamer's own record of the run"));
            if (!double.IsNaN(flowMs)) G.Rows.Add(InfoRow("Frame generation: optical flow", F1(flowMs) + " ms", "-", "the streamer's own record of the run"));
            G.Rows.Add(RowOf("Core clock", "MHz", clk, false, "F0"));
            G.Rows.Add(RowOf("Board power", "W", pwr, true, "F0"));
            G.Rows.Add(RowOf("Temperature", "C", tmp, true, "F0"));
            if (vram.Count > 0) G.Rows.Add(RowOf("VRAM used", "GiB", vram, true, "F1", vramTotal.Count > 0 ? "of " + N(Max(vramTotal) / 1073741824.0, "F0") + " GiB" : null, 1.0 / 1073741824.0));
            // the limiter: only the documented clock-event bits count; any other bit is named, never counted (the RTX 5090's driver keeps 0x400 set at all times)
            int active = 0, counted = 0; ulong seen = 0, unknown = 0;
            if (gpu != null && gpu.Has("throttle_reasons") && gpu.Has("sample_qpc"))
            {
                var tq = gpu.Get("sample_qpc"); var thr = gpu.Raw["throttle_reasons"];
                for (int i = 0; i < thr.Count; i++)
                {
                    if ((!double.IsNaN(from) && tq[i] < from) || (!double.IsNaN(to) && tq[i] > to) || !thr[i].StartsWith("0x")) continue;
                    ulong b; if (!ulong.TryParse(thr[i].Substring(2), NumberStyles.HexNumber, C, out b)) continue;
                    counted++; ulong lim = b & 0x1FEUL; if (lim != 0) { active++; seen |= lim; } unknown |= b & ~0x1FFUL;
                }
            }
            double limShare = counted > 0 ? (double)active / counted : double.NaN;
            if (counted > 0) G.Rows.Add(InfoRow("Clock limiter active", Pct(limShare) + " of samples", "-", active > 0 ? Name(seen) : (unknown != 0 ? "other clock-event bits 0x" + unknown.ToString("x") + " set: not a documented limiter, not counted" : "none")));
            double e99 = c.Enc.Count > 0 ? P(c.Enc, 0.99) : double.NaN, e50 = c.Enc.Count > 0 ? P(c.Enc, 0.5) : double.NaN;
            bool encHigh = !double.IsNaN(e99) && !double.IsNaN(period) && e99 > EncodeBudget * period;
            double g3 = game3d.Count > 0 ? P(game3d, 0.99) : double.NaN, u95 = util.Count > 0 ? P(util, 0.95) : double.NaN;
            bool gpuBusy = (!double.IsNaN(g3) && g3 >= 95) || (!double.IsNaN(u95) && u95 >= 97);
            if (util.Count == 0 && c.Enc.Count == 0) { G.Status = "INSUFFICIENT"; G.Summary = "no GPU sensor data (NVML not available)"; }
            else if (encHigh)
            {
                G.Status = lateIssue ? "PROBLEM" : "WATCH";
                G.Summary = "the encoder uses " + F0(e99 / period * 100) + " % of the frame period at the 99th percentile (median " + F0(e50 / period * 100) + " %)" + (!double.IsNaN(u95) ? "; the GPU itself was at " + F0(Mean(util)) + " % on average" : "")
                    + (c.Gen > 0 ? "; frame generation was on" : "");
            }
            else if (gpuBusy) { G.Status = lateIssue ? "PROBLEM" : "WATCH"; G.Summary = "the GPU is saturated" + (!double.IsNaN(g3) ? " (the game's 3D engine reaches " + F0(g3) + " % at p99)" : " (" + F0(u95) + " % at p95)"); }
            else if (!double.IsNaN(limShare) && limShare >= 0.10) { G.Status = "WATCH"; G.Summary = "a clock limiter was active " + Pct(limShare) + " of the time (" + Name(seen) + ")"; }
            else { G.Status = "OK"; G.Summary = "GPU " + F0(Mean(util)) + " % on average, encode window " + (c.Enc.Count > 0 ? F0(e50 / period * 100) + " % at the median" : "n/a"); }
            R.Categories.Add(G);

            // ---------------------------------------------------------------- CPU
            var C2 = new ResourceCategory { Name = "CPU" };
            var cpu = Win(sys, "cpu_total_pct", "sample_qpc", from, to);
            double logical = 0; try { var sj = File.ReadAllText(Path.Combine(c.Dir, "session.json")); var m = Regex.Match(sj, "\"logical_cpus\": (\\d+)"); if (m.Success) logical = double.Parse(m.Groups[1].Value, C); } catch { }
            var gameCpu = game != null ? WinBy(proc, game, null, "cpu_cores", "sample_qpc", from, to, out ins) : new List<double>();
            var hostCpu = WinBy(proc, host, null, "cpu_cores", "sample_qpc", from, to, out ins);
            C2.Rows.Add(RowOf("CPU total", "%", cpu, true, "F0", logical > 0 ? N(logical, "F0") + " logical CPUs" : null));
            if (gameCpu.Count > 0) C2.Rows.Add(RowOf("Game process", "cores", gameCpu, true, "F1", game + " (1.0 = one whole core)"));
            if (hostCpu.Count > 0) C2.Rows.Add(RowOf("Streamer (alvr_host)", "cores", hostCpu, true, "F1"));
            double cpu95 = cpu.Count > 0 ? P(cpu, 0.95) : double.NaN;
            if (cpu.Count == 0) { C2.Status = "INSUFFICIENT"; C2.Summary = "no CPU counters"; }
            else if (cpu95 >= 90) { C2.Status = lateIssue ? "PROBLEM" : "WATCH"; C2.Summary = "the CPU is saturated (" + F0(cpu95) + " % at p95)"; }
            else if (cpu95 >= 80) { C2.Status = "WATCH"; C2.Summary = "the CPU is busy (" + F0(cpu95) + " % at p95)"; }
            else
            {
                C2.Status = "OK"; C2.Summary = "CPU " + F0(Mean(cpu)) + " % on average, " + F0(cpu95) + " % at p95" + (lateIssue && !gpuBusy && !encHigh ? "; a game bound on one thread can be slow with a low total, and no per-thread view is recorded here (a Deep trace shows it)" : "");
            }
            R.Categories.Add(C2);

            // ---------------------------------------------------------------- Memory and I/O
            var M = new ResourceCategory { Name = "Memory and I/O" };
            var ram = Win(sys, "ram_available_bytes", "sample_qpc", from, to);
            var faults = Win(sys, "page_faults_per_s", "sample_qpc", from, to);
            var hard = Win(sys, "pages_input_per_s", "sample_qpc", from, to);
            var diskBusy = Win(sys, "disk_busy_pct", "sample_qpc", from, to);
            var diskRead = Win(sys, "disk_read_bytes_per_s", "sample_qpc", from, to);
            var diskLat = Win(sys, "disk_read_latency_ms", "sample_qpc", from, to);
            var gameMem = game != null ? WinBy(proc, game, null, "private_bytes", "sample_qpc", from, to, out ins) : new List<double>();
            var gameIo = game != null ? WinBy(proc, game, null, "io_read_bytes_per_s", "sample_qpc", from, to, out ins) : new List<double>();
            M.Rows.Add(RowOf("RAM available", "GiB", ram, false, "F1", null, 1.0 / 1073741824.0));
            if (gameMem.Count > 0) M.Rows.Add(RowOf("Game private memory", "GiB", gameMem, true, "F1", game, 1.0 / 1073741824.0));
            M.Rows.Add(RowOf("Page faults (mostly soft: normal)", "/s", faults, true, "F0"));
            if (hard.Count > 0) M.Rows.Add(RowOf("Hard faults (pages read from disk)", "/s", hard, true, "F0"));
            M.Rows.Add(RowOf("Disk busy", "%", diskBusy, true, "F0"));
            if (diskRead.Count > 0) M.Rows.Add(RowOf("Disk read", "MB/s", diskRead, true, "F1", null, 1.0 / 1048576.0));
            if (diskLat.Count > 0) M.Rows.Add(RowOf("Disk read latency", "ms", diskLat, true, "F1"));
            if (gameIo.Count > 0) M.Rows.Add(RowOf("Game file reads", "MB/s", gameIo, true, "F1", game, 1.0 / 1048576.0));
            double ramMin = ram.Count > 0 ? Min(ram) / 1073741824.0 : double.NaN, lat99 = diskLat.Count > 0 ? P(diskLat, 0.99) : double.NaN, hard99 = hard.Count > 0 ? P(hard, 0.99) : double.NaN, busy95 = diskBusy.Count > 0 ? P(diskBusy, 0.95) : double.NaN;
            if (ram.Count == 0 && diskBusy.Count == 0) { M.Status = "INSUFFICIENT"; M.Summary = "no memory or disk counters"; }
            else if (lateIssue && (ramMin < 1 || lat99 > 50 || hard99 > 2000)) { M.Status = "PROBLEM"; M.Summary = ramMin < 1 ? "memory is nearly exhausted (" + F1(ramMin) + " GiB free at the lowest)" : "the disk is slow (read latency " + F0(lat99) + " ms at p99, hard faults " + F0(hard99) + "/s)"; }
            else if (ramMin < 4 || busy95 >= 80 || lat99 > 20 || hard99 > 500) { M.Status = "WATCH"; M.Summary = ramMin < 4 ? "free memory got low (" + F1(ramMin) + " GiB)" : "the disk is busy (" + F0(busy95) + " % at p95)"; }
            else { M.Status = "OK"; M.Summary = F0(ramMin) + " GiB free at the lowest" + (!double.IsNaN(busy95) ? ", disk " + F0(busy95) + " % busy at p95" : ""); }
            R.Categories.Add(M);

            // ---------------------------------------------------------------- Network
            var N2 = new ResourceCategory { Name = "Network" };
            var nic = Win(sys, "nic_bytes_per_s", "sample_qpc", from, to);
            var bitrate = Win(cg, "bitrate_mbps", "t_recv", from, to);
            var netMs = Win(cg, "network_ms", "t_recv", from, to);
            if (nic.Count > 0) N2.Rows.Add(RowOf("Network throughput (all adapters)", "Mbps", nic, true, "F0", "not the stream alone", 8.0 / 1e6));
            if (bitrate.Count > 0) N2.Rows.Add(RowOf("Stream bitrate (headset's statistics)", "Mbps", bitrate, true, "F0"));
            if (netMs.Count > 0) N2.Rows.Add(RowOf("Network latency (headset-reported)", "ms", netMs, true, "F1"));
            if (!double.IsNaN(lost)) N2.Rows.Add(InfoRow("Packets lost", F0(lost), "-", "over the run"));
            double net95 = netMs.Count > 0 ? P(netMs, 0.95) : double.NaN, net99 = netMs.Count > 0 ? P(netMs, 0.99) : double.NaN;
            if (netMs.Count == 0) { N2.Status = "INSUFFICIENT"; N2.Summary = "no headset report (client_graph.csv)"; }
            else if (lateIssue && (net99 > 3 * period || lost > 100)) { N2.Status = "PROBLEM"; N2.Summary = "network latency reaches " + F0(net99) + " ms at p99" + (lost > 0 ? ", " + F0(lost) + " packets lost" : ""); }
            else if (net95 > 1.5 * period || lost > 0) { N2.Status = "WATCH"; N2.Summary = (net95 > 1.5 * period ? "network latency " + F0(net95) + " ms at p95" : "") + (lost > 0 ? (net95 > 1.5 * period ? ", " : "") + F0(lost) + " packets lost" : ""); }
            else { N2.Status = "OK"; N2.Summary = "network " + F1(Mean(netMs)) + " ms on average, " + F0(net99) + " ms at p99, " + (double.IsNaN(lost) ? "loss unknown" : F0(lost) + " packets lost"); }
            R.Categories.Add(N2);

            // ---------------------------------------------------------------- VR rendering: what reaches the eyes
            var V = new ResourceCategory { Name = "VR rendering" };
            if (!double.IsNaN(gameFps)) V.Rows.Add(InfoRow("Game fps", F1(gameFps), double.IsNaN(gameFpsLow) ? "-" : F1(gameFpsLow), "average over the run; worst = the slowest tenth"));
            else if (appFps.Count > 0) V.Rows.Add(RowOf("Game fps (10 s windows)", "", appFps, false, "F1"));
            if (!double.IsNaN(streamedFps)) V.Rows.Add(InfoRow("Streamed fps", F1(streamedFps), streamFps.Count > 0 ? F1(P(streamFps, 0.01)) : "-", "frames sent"));
            if (!double.IsNaN(headsetShows)) V.Rows.Add(InfoRow("Headset shows fps", F1(headsetShows), clientFps.Count > 0 ? F1(P(clientFps, 0.01)) : "-", !double.IsNaN(repeatedP90) ? "repeated frames " + Pct(repeatedP90) + " (slowest tenth)" : null));
            if (c.Gaps.Count > 0) V.Rows.Add(RowOf("Gap between sent frames", "ms", c.Gaps, true, "F1", "nominal " + F1(period) + " ms"));
            V.Rows.Add(InfoRow("Late frames", Pct(c.LateShare), R.Hotspots.Count > 0 ? "worst 10 s: " + HotShare(R) : "-", c.Late + " gaps over " + N(LateFactor, "F1") + " periods"));
            if (c.Gen + c.Restamp > 0) V.Rows.Add(InfoRow("Frames made by frame generation", Pct((double)c.Gen / Math.Max(1, c.Rows.Count)), "-", (fgLevel.Length > 0 ? "level " + fgLevel + "; " : "") + "real frames re-stamped " + Pct((double)c.Restamp / Math.Max(1, c.Rows.Count))));
            var lat = Win(cg, "total_ms", "t_recv", from, to); var dec = Win(cg, "decode_ms", "t_recv", from, to); var dq = Win(cg, "decoder_queue_ms", "t_recv", from, to); var vq = Win(cg, "vsync_queue_ms", "t_recv", from, to);
            if (lat.Count > 0) V.Rows.Add(RowOf("Headset pipeline latency", "ms", lat, true, "F1", "tracking sent to frame displayed"));
            if (dec.Count > 0) V.Rows.Add(RowOf("Headset decode", "ms", dec, true, "F1"));
            if (dq.Count > 0) V.Rows.Add(RowOf("Decoder queue", "ms", dq, true, "F1"));
            if (vq.Count > 0) V.Rows.Add(RowOf("Vsync queue", "ms", vq, true, "F1"));
            double dec95 = dec.Count > 0 ? P(dec, 0.95) : double.NaN, dq95 = dq.Count > 0 ? P(dq, 0.95) : double.NaN;
            bool headsetSlow = !double.IsNaN(period) && ((!double.IsNaN(dec95) && dec95 > period) || (!double.IsNaN(dq95) && dq95 > period));
            if (!c.HaveHost) { V.Status = "INSUFFICIENT"; V.Summary = "no stream record"; }
            else if (lateIssue) { V.Status = "PROBLEM"; V.Summary = "late frames " + Pct(c.LateShare) + ": the symptom (the categories above say where it comes from)"; }
            else if (headsetSlow) { V.Status = "WATCH"; V.Summary = "the headset is slow at the tail (decode p95 " + F1(dec95) + " ms, decoder queue p95 " + F1(dq95) + " ms)"; }
            else { V.Status = "OK"; V.Summary = "late frames " + Pct(c.LateShare) + (!double.IsNaN(gameFps) ? ", game " + F1(gameFps) + " fps, headset " + (double.IsNaN(headsetShows) ? "n/a" : F1(headsetShows)) + " fps" : ""); }
            R.Categories.Add(V);
        }


        // ---------------------------------------------------------------- the time series: one chart per category, every line as a percentage of its limit, one value per second
        static double[] BinMean(List<double> t, List<double> v, double origin, int n)
        {
            var sum = new double[n]; var cnt = new int[n]; var o = new double[n];
            for (int i = 0; i < t.Count && i < v.Count; i++) { if (double.IsNaN(v[i])) continue; int b = (int)Math.Floor(t[i] - origin); if (b < 0 || b >= n) continue; sum[b] += v[i]; cnt[b]++; }
            for (int b = 0; b < n; b++) o[b] = cnt[b] > 0 ? sum[b] / cnt[b] : double.NaN;
            return o;
        }
        static double[] Scale(double[] v, double k) { return v.Select(x => double.IsNaN(x) ? x : x * k).ToArray(); }

        // a column of a table with its time column, restricted to rows whose time is in range (for the binning above)
        static void Col(Table t, string col, string timeCol, out List<double> tm, out List<double> v)
        {
            tm = new List<double>(); v = new List<double>(); if (t == null || !t.Has(col) || !t.Has(timeCol)) return;
            tm = t.Get(timeCol); v = t.Get(col);
        }

        static void BuildSeries(ReportData R, Ctx c, double wStart, double wEnd, double t0)
        {
            if (!c.HaveHost || double.IsNaN(wStart) || double.IsNaN(wEnd) || wEnd - wStart < 5) return;
            var hf = (Table)c.Hf; var cg = (Table)c.Cg; var gpu = (Table)c.Gpu; var sys = (Table)c.Sys; var eng = (Table)c.Eng; var proc = (Table)c.Proc;
            int n = (int)Math.Min(20000, Math.Ceiling(wEnd - wStart) + 1);
            R.OriginSeconds = double.IsNaN(t0) ? 0 : wStart - t0;
            double period = c.Period;
            List<double> tm, v;

            // late frames per second against the 1 % budget, and the streamed frames per second, from the host's own record
            var ts = hf.Get("t_send"); var enc = hf.Get("encode_ms");
            var late = new double[n]; var tot = new double[n]; var fps = new double[n]; var encT = new List<double>(); var encV = new List<double>();
            for (int k = 0; k < c.Rows.Count; k++)
            {
                int i = c.Rows[k]; int b = (int)Math.Floor(ts[i] - wStart); if (b < 0 || b >= n) continue;
                fps[b]++; if (hf.Has("encode_ms")) { encT.Add(ts[i]); encV.Add(enc[i]); }
                if (k == 0) continue;
                double gp = (ts[i] - ts[c.Rows[k - 1]]) * 1000.0; if (gp > PauseMs) continue;
                tot[b]++; if (gp > LateFactor * period) late[b]++;
            }
            R.LateBudgetPercent = Enumerable.Range(0, n).Select(b => tot[b] > 0 ? late[b] / tot[b] / MaxLateShare * 100.0 : double.NaN).ToArray();

            Func<string, string, double[], double, string, string, object, TimeSeries> mk = (name, unit, vals, limit, limitText, fmt, _) => new TimeSeries { Name = name, Unit = unit, V = vals, Limit = limit, LimitText = limitText, Fmt = fmt };
            Func<TimeSeries, bool> has = x => x.V.Any(y => !double.IsNaN(y));

            // game and host process names, as the table builder found them
            string host = "alvr_host", game = null;
            try { var sj = File.ReadAllText(Path.Combine(c.Dir, "session.json")); var m = Regex.Match(sj, "\"processes\": \"([^\"]*)\""); if (m.Success) game = m.Groups[1].Value.Split('+').FirstOrDefault(x => x.Length > 0 && !string.Equals(x, host, StringComparison.OrdinalIgnoreCase)); } catch { }
            double logical = 0, ramTotal = 0;
            try { var sj = File.ReadAllText(Path.Combine(c.Dir, "session.json")); var m = Regex.Match(sj, "\"logical_cpus\": (\\d+)"); if (m.Success) logical = double.Parse(m.Groups[1].Value, C); var m2 = Regex.Match(sj, "\"ram_total_bytes\": (\\d+)"); if (m2.Success) ramTotal = double.Parse(m2.Groups[1].Value, C); } catch { }

            // GPU
            var gg = new SeriesGroup { Category = "GPU" };
            Col(gpu, "gpu_util_pct", "sample_qpc", out tm, out v); gg.Series.Add(mk("GPU utilisation", "%", BinMean(tm, v, wStart, n), 100, "100 %", "F0", null));
            gg.Series.Add(mk("Encode time per frame", "ms", BinMean(encT, encV, wStart, n), period, N(period, "F1") + " ms = the frame period", "F1", null));
            if (game != null) { tm = new List<double>(); v = new List<double>(); if (eng != null && eng.Has("process") && eng.Has("engine")) { var tq = eng.Get("sample_qpc"); var uv = eng.Get("util_pct"); for (int i = 0; i < uv.Count; i++) if (eng.Raw["process"][i] == game && eng.Raw["engine"][i] == "3D") { tm.Add(tq[i]); v.Add(uv[i]); } } gg.Series.Add(mk("Game: 3D engine", "%", BinMean(tm, v, wStart, n), 100, "100 %", "F0", null)); }
            gg.Series.RemoveAll(x => !has(x)); if (gg.Series.Count > 0) R.Charts.Add(gg);
            // CPU
            var cp = new SeriesGroup { Category = "CPU" };
            Col(sys, "cpu_total_pct", "sample_qpc", out tm, out v); cp.Series.Add(mk("CPU total", "%", BinMean(tm, v, wStart, n), 100, "100 %", "F0", null));
            if (game != null && proc != null && proc.Has("process") && logical > 0) { tm = new List<double>(); v = new List<double>(); var tq = proc.Get("sample_qpc"); var cv = proc.Get("cpu_cores"); for (int i = 0; i < cv.Count; i++) if (proc.Raw["process"][i] == game) { tm.Add(tq[i]); v.Add(cv[i]); } cp.Series.Add(mk("Game process", "cores", BinMean(tm, v, wStart, n), logical, N(logical, "F0") + " cores = all of them", "F1", null)); }
            cp.Series.RemoveAll(x => !has(x)); if (cp.Series.Count > 0) R.Charts.Add(cp);
            // Memory and I/O
            var mm = new SeriesGroup { Category = "Memory and I/O" };
            if (ramTotal > 0) { Col(sys, "ram_available_bytes", "sample_qpc", out tm, out v); mm.Series.Add(mk("RAM used", "GiB", Scale(BinMean(tm, v.Select(x => ramTotal - x).ToList(), wStart, n), 1.0 / 1073741824.0), ramTotal / 1073741824.0, N(ramTotal / 1073741824.0, "F0") + " GiB = all of it", "F1", null)); }
            Col(sys, "disk_busy_pct", "sample_qpc", out tm, out v); mm.Series.Add(mk("Disk busy", "%", BinMean(tm, v, wStart, n), 100, "100 %", "F0", null));
            mm.Series.RemoveAll(x => !has(x)); if (mm.Series.Count > 0) R.Charts.Add(mm);
            // Network
            var nw = new SeriesGroup { Category = "Network" };
            Col(cg, "network_ms", "t_recv", out tm, out v); nw.Series.Add(mk("Network latency", "ms", BinMean(tm, v, wStart, n), period, N(period, "F1") + " ms = the frame period", "F1", null));
            Col(cg, "bitrate_mbps", "t_recv", out tm, out v); var br = BinMean(tm, v, wStart, n); double brMax = br.Where(x => !double.IsNaN(x)).DefaultIfEmpty(0).Max();
            if (brMax > 0) nw.Series.Add(mk("Stream bitrate", "Mbps", br, brMax, N(brMax, "F0") + " Mbps = the recording's peak", "F0", null));
            nw.Series.RemoveAll(x => !has(x)); if (nw.Series.Count > 0) R.Charts.Add(nw);
            // VR rendering
            var vr = new SeriesGroup { Category = "VR rendering" };
            vr.Series.Add(mk("Late frames", "%", Scale(R.LateBudgetPercent, MaxLateShare), MaxLateShare * 100, "1 % of the frames = the budget", "F1", null));
            double hz = 1000.0 / period;
            vr.Series.Add(mk("Streamed fps", "fps", fps, hz, N(hz, "F0") + " fps = the display rate", "F0", null));
            Col(cg, "client_fps", "t_recv", out tm, out v); vr.Series.Add(mk("Headset shows", "fps", BinMean(tm, v, wStart, n), hz, N(hz, "F0") + " fps = the display rate", "F0", null));
            vr.Series.RemoveAll(x => !has(x)); if (vr.Series.Count > 0) R.Charts.Add(vr);
        }

        static string HotShare(ReportData R) { var h = R.Hotspots.OrderByDescending(x => { var m = Regex.Match(x, @"in: ([\d.]+) %"); return m.Success ? double.Parse(m.Groups[1].Value, C) : 0; }).First(); var mm = Regex.Match(h, @"in: ([\d.]+) %"); return mm.Success ? mm.Groups[1].Value + " %" : "-"; }

        // a number in one jsonl line: "key": 12.3 (or an integer); NaN when absent
        static double Num(string line, string key)
        {
            var m = Regex.Match(line, "\"" + Regex.Escape(key) + "\": ?(-?[\\d.]+(?:[eE][-+]?\\d+)?)"); double v;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, C, out v) ? v : double.NaN;
        }
    }
}
