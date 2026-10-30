// Prototype of the L0/L1 system probe (docs/TELEMETRY_PLAN.md): what NVML and the Windows counters give on this PC, and what they cost.
// No admin, no SDK: nvml.dll comes with the NVIDIA driver, the counters with Windows.
//   perfprobe.exe survey                 every NVML function we might use: return code, value, cost per call; the PDH categories and their cost
//   perfprobe.exe run <hz> <seconds> [out.csv] [jitter]   sample both at <hz>, write one CSV, report the probe's own CPU time; "jitter" also
//                                                         runs a 10 ms sleeper (like the host's sched probe) so on/off runs can be compared
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

static class Nvml
{
    const string Dll = "nvml.dll";
    [DllImport(Dll)] public static extern int nvmlInit_v2();
    [DllImport(Dll)] public static extern int nvmlShutdown();
    [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr h);
    [DllImport(Dll)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr h, out Util u);
    [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(IntPtr h, int type, out uint mhz);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPowerUsage(IntPtr h, out uint mw);
    [DllImport(Dll)] public static extern int nvmlDeviceGetTemperature(IntPtr h, int sensor, out uint c);
    [DllImport(Dll)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr h, out Mem m);
    [DllImport(Dll)] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr h, out ulong r);
    [DllImport(Dll)] public static extern int nvmlDeviceGetEncoderUtilization(IntPtr h, out uint util, out uint periodUs);
    [DllImport(Dll)] public static extern int nvmlDeviceGetDecoderUtilization(IntPtr h, out uint util, out uint periodUs);
    [DllImport(Dll)] public static extern int nvmlDeviceGetEncoderStats(IntPtr h, out uint sessions, out uint fps, out uint latencyUs);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPcieThroughput(IntPtr h, int counter, out uint kbps);
    [DllImport(Dll)] public static extern int nvmlDeviceGetPerformanceState(IntPtr h, out int p);
    [DllImport(Dll)] public static extern int nvmlDeviceGetFanSpeed(IntPtr h, out uint pct);
    [DllImport(Dll)] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr h, out uint mw);
    [DllImport(Dll)] public static extern int nvmlDeviceGetProcessUtilization(IntPtr h, [Out] ProcUtil[] s, ref uint count, ulong lastSeen);
    [DllImport(Dll)] public static extern int nvmlDeviceGetName(IntPtr h, [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder n, uint len);
    [DllImport(Dll)] public static extern int nvmlSystemGetDriverVersion([MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder n, uint len);
    [StructLayout(LayoutKind.Sequential)] public struct Util { public uint gpu, memory; }
    [StructLayout(LayoutKind.Sequential)] public struct Mem { public ulong total, free, used; }
    [StructLayout(LayoutKind.Sequential)] public struct ProcUtil { public uint pid; public ulong timeStamp; public uint smUtil, memUtil, encUtil, decUtil; }
}

class Probe
{
    static IntPtr dev;
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    static string Rc(int rc) { return rc == 0 ? "ok" : rc == 3 ? "NOT_SUPPORTED" : rc == 4 ? "NO_PERMISSION" : rc == 15 ? "NOT_FOUND" : "err" + rc; }


    static double Cost(Action a, int n = 200)
    {
        a();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) a();
        return sw.Elapsed.TotalMilliseconds * 1000.0 / n;
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "survey";
        int rc = Nvml.nvmlInit_v2();
        if (rc != 0) { Console.WriteLine("nvmlInit failed " + rc); }
        else
        {
            Nvml.nvmlDeviceGetHandleByIndex_v2(0, out dev);
            var sb = new System.Text.StringBuilder(96);
            Nvml.nvmlDeviceGetName(dev, sb, 96);
            var dv = new System.Text.StringBuilder(32);
            Nvml.nvmlSystemGetDriverVersion(dv, 32);
            Console.WriteLine("gpu " + sb + "  driver " + dv);
        }
        if (mode == "survey") Survey();
        else if (mode == "nvmlmax") NvmlMax(double.Parse(args[1], C));
        else if (mode == "run") Run(double.Parse(args[1], C), double.Parse(args[2], C), args.Length > 3 && !args[3].StartsWith("-") ? args[3] : null, args.Contains("-jitter"), args.Contains("-nopcie"), args.Contains("-noeng"), args.Where(x => x.StartsWith("-names=")).SelectMany(x => x.Substring(7).Split(',')).ToArray());
        return 0;
    }

    static void Survey()
    {
        Console.WriteLine("\n== NVML (rc, value, cost us/call over 200 calls) ==");
        uint u1 = 0, u2 = 0, u3 = 0; ulong ul = 0; int pi = 0;
        Nvml.Util ut = default(Nvml.Util); Nvml.Mem mem = default(Nvml.Mem);
        Row("utilization gpu/mem", () => { var r = Nvml.nvmlDeviceGetUtilizationRates(dev, out ut); return r; }, () => { Nvml.nvmlDeviceGetUtilizationRates(dev, out ut); return ut.gpu + "/" + ut.memory + " %"; });
        foreach (var t in new[] { new { n = "clock graphics", i = 0 }, new { n = "clock sm", i = 1 }, new { n = "clock mem", i = 2 }, new { n = "clock video", i = 3 } })
        { var ti = t.i; Row(t.n, () => Nvml.nvmlDeviceGetClockInfo(dev, ti, out u1), () => u1 + " MHz"); }
        Row("power", () => Nvml.nvmlDeviceGetPowerUsage(dev, out u1), () => (u1 / 1000.0).ToString("F1", C) + " W");
        Row("power limit", () => Nvml.nvmlDeviceGetEnforcedPowerLimit(dev, out u1), () => (u1 / 1000.0).ToString("F1", C) + " W");
        Row("temperature", () => Nvml.nvmlDeviceGetTemperature(dev, 0, out u1), () => u1 + " C");
        Row("memory", () => Nvml.nvmlDeviceGetMemoryInfo(dev, out mem), () => (mem.used >> 20) + "/" + (mem.total >> 20) + " MiB");
        Row("throttle reasons", () => Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(dev, out ul), () => "0x" + ul.ToString("x"));
        Row("performance state", () => Nvml.nvmlDeviceGetPerformanceState(dev, out pi), () => "P" + pi);
        Row("fan", () => Nvml.nvmlDeviceGetFanSpeed(dev, out u1), () => u1 + " %");
        Row("encoder util", () => Nvml.nvmlDeviceGetEncoderUtilization(dev, out u1, out u2), () => u1 + " % over " + u2 + " us");
        Row("decoder util", () => Nvml.nvmlDeviceGetDecoderUtilization(dev, out u1, out u2), () => u1 + " % over " + u2 + " us");
        Row("encoder stats", () => Nvml.nvmlDeviceGetEncoderStats(dev, out u1, out u2, out u3), () => u1 + " sessions, " + u2 + " fps, " + u3 + " us");
        Row("pcie tx", () => Nvml.nvmlDeviceGetPcieThroughput(dev, 0, out u1), () => u1 + " KB/s");
        Row("pcie rx", () => Nvml.nvmlDeviceGetPcieThroughput(dev, 1, out u1), () => u1 + " KB/s");
        var ps = new Nvml.ProcUtil[64];
        Row("process utilization", () => { uint n = 0; int r = Nvml.nvmlDeviceGetProcessUtilization(dev, null, ref n, 0); if (r != 7) return r; n += 8; return Nvml.nvmlDeviceGetProcessUtilization(dev, ps, ref n, 0); }, () => { uint n = 64; int r = Nvml.nvmlDeviceGetProcessUtilization(dev, ps, ref n, 0); return n + " samples: " + string.Join(", ", ps.Take((int)Math.Min(n, 4)).Select(p => p.pid + " sm" + p.smUtil + " enc" + p.encUtil)); });

        Console.WriteLine("\n== Windows counters (PDH through System.Diagnostics) ==");
        foreach (var cat in new[] { "Processor Information", "Memory", "Network Interface", "PhysicalDisk", "Process", "GPU Engine", "GPU Adapter Memory", "GPU Process Memory", "GPU Local Adapter Memory" })
        {
            try
            {
                var pcc = new PerformanceCounterCategory(cat);
                var inst = pcc.CategoryType == PerformanceCounterCategoryType.MultiInstance ? pcc.GetInstanceNames() : new string[0];
                var ctrs = (inst.Length > 0 ? pcc.GetCounters(inst[0]) : pcc.GetCounters()).Select(c => c.CounterName).ToArray();
                Console.WriteLine(cat + ": " + inst.Length + " instances, counters: " + string.Join(" | ", ctrs.Take(8)));
            }
            catch (Exception e) { Console.WriteLine(cat + ": unavailable (" + e.GetType().Name + ")"); }
        }
        try
        {
            var inst = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();
            var types = inst.Select(i => { int k = i.IndexOf("engtype_"); return k < 0 ? "?" : i.Substring(k); }).GroupBy(x => x).OrderBy(g => g.Key);
            Console.WriteLine("GPU Engine instances by engine type:");
            foreach (var g in types) Console.WriteLine("  " + g.Key + ": " + g.Count());
            var enc = inst.Where(i => i.Contains("engtype_VideoEncode")).ToArray();
            Console.WriteLine("VideoEncode instances (pid, engine): " + string.Join("; ", enc.Take(6).Select(i => i.Substring(0, i.IndexOf("_luid")))));
        }
        catch (Exception e) { Console.WriteLine("GPU Engine: " + e.Message); }
        Console.WriteLine("\nsweep cost (create all counters once, then ms per read of every GPU Engine utilization counter):");
        try
        {
            var inst = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();
            var sw = Stopwatch.StartNew();
            var cs = inst.Select(i => new PerformanceCounter("GPU Engine", "Utilization Percentage", i, true)).ToArray();
            Console.WriteLine("  create " + cs.Length + " counters: " + sw.ElapsedMilliseconds + " ms");
            foreach (var c in cs) c.NextValue();
            Thread.Sleep(1000);
            sw.Restart();
            float tot = 0; foreach (var c in cs) tot += c.NextValue();
            Console.WriteLine("  one full read: " + sw.Elapsed.TotalMilliseconds.ToString("F2", C) + " ms (sum " + tot.ToString("F1", C) + " %)");
        }
        catch (Exception e) { Console.WriteLine("  failed: " + e.Message); }
    }

    static void Row(string name, Func<int> call, Func<string> show)
    {
        int rc = call();
        string val = rc == 0 ? show() : "";
        string cost = rc == 0 ? Cost(() => call()).ToString("F1", C) : "-";
        Console.WriteLine(name.PadRight(22) + Rc(rc).PadRight(14) + val.PadRight(52) + cost + " us");
    }

    // which NVML call stalls: every call at 10 Hz, worst time and number of calls over 50 ms per call
    static void NvmlMax(double seconds)
    {
        uint a = 0, b = 0, c = 0; ulong th; int pi; Nvml.Util ut; Nvml.Mem mem;
        var calls = new List<KeyValuePair<string, Func<int>>>
        {
            new KeyValuePair<string, Func<int>>("utilization", () => Nvml.nvmlDeviceGetUtilizationRates(dev, out ut)),
            new KeyValuePair<string, Func<int>>("clock gfx", () => Nvml.nvmlDeviceGetClockInfo(dev, 0, out a)),
            new KeyValuePair<string, Func<int>>("clock mem", () => Nvml.nvmlDeviceGetClockInfo(dev, 2, out a)),
            new KeyValuePair<string, Func<int>>("power", () => Nvml.nvmlDeviceGetPowerUsage(dev, out a)),
            new KeyValuePair<string, Func<int>>("temperature", () => Nvml.nvmlDeviceGetTemperature(dev, 0, out a)),
            new KeyValuePair<string, Func<int>>("memory", () => Nvml.nvmlDeviceGetMemoryInfo(dev, out mem)),
            new KeyValuePair<string, Func<int>>("throttle", () => Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(dev, out th)),
            new KeyValuePair<string, Func<int>>("pstate", () => Nvml.nvmlDeviceGetPerformanceState(dev, out pi)),
            new KeyValuePair<string, Func<int>>("encoder util", () => Nvml.nvmlDeviceGetEncoderUtilization(dev, out a, out b)),
            new KeyValuePair<string, Func<int>>("decoder util", () => Nvml.nvmlDeviceGetDecoderUtilization(dev, out a, out b)),
            new KeyValuePair<string, Func<int>>("encoder stats", () => Nvml.nvmlDeviceGetEncoderStats(dev, out a, out b, out c)),
        };
        var worst = new double[calls.Count]; var slow = new int[calls.Count]; var stamps = new List<string>();
        var wall = Stopwatch.StartNew();
        while (wall.Elapsed.TotalSeconds < seconds)
        {
            for (int i = 0; i < calls.Count; i++)
            {
                var sw = Stopwatch.StartNew(); calls[i].Value(); double ms = sw.Elapsed.TotalMilliseconds;
                if (ms > worst[i]) worst[i] = ms;
                if (ms > 50) { slow[i]++; stamps.Add(calls[i].Key + " " + ms.ToString("F0", C) + " ms at " + wall.Elapsed.TotalSeconds.ToString("F1", C) + " s"); }
            }
            Thread.Sleep(100);
        }
        for (int i = 0; i < calls.Count; i++) Console.WriteLine(calls[i].Key.PadRight(16) + "worst " + worst[i].ToString("F1", C).PadLeft(8) + " ms, over 50 ms: " + slow[i]);
        Console.WriteLine(string.Join("\n", stamps.Take(20)));
    }

    // the sampling loop: NVML fields at <hz>, Windows counters at <hz>, one CSV row per tick; a failed call leaves its field blank
    static void Run(double hz, double seconds, string outPath, bool jitter, bool noPcie, bool noEng, string[] names)
    {
        var self = Process.GetCurrentProcess();
        var cpu0 = self.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var lat = new List<double>();
        Thread sleeper = null;
        bool stop = false;
        if (jitter)
        {
            sleeper = new Thread(() =>
            {
                while (!stop)
                {
                    var sw = Stopwatch.StartNew();
                    Thread.Sleep(10);
                    lock (lat) lat.Add(sw.Elapsed.TotalMilliseconds - 10.0);
                }
            }) { IsBackground = true };
            sleeper.Start();
        }
        // nvmlDeviceGetPcieThroughput blocks ~30 ms per call (it measures over a window): its own 1 Hz thread, the tick reads the last value
        uint pcieTx = 0, pcieRx = 0; bool pcieOk = false;
        var pcie = new Thread(() => { while (!stop) { uint x, y; if (Nvml.nvmlDeviceGetPcieThroughput(dev, 0, out x) == 0 && Nvml.nvmlDeviceGetPcieThroughput(dev, 1, out y) == 0) { pcieTx = x; pcieRx = y; pcieOk = true; } Thread.Sleep(1000); } }) { IsBackground = true };
        if (!noPcie) pcie.Start();
        var cpuC = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
        var memC = new PerformanceCounter("Memory", "Available MBytes");
        var faultC = new PerformanceCounter("Memory", "Page Faults/sec");
        var nicNames = new PerformanceCounterCategory("Network Interface").GetInstanceNames();
        var nic = nicNames.Select(n => new PerformanceCounter("Network Interface", "Bytes Total/sec", n)).ToArray();
        var pids = names.SelectMany(nm => Process.GetProcessesByName(nm)).Select(p => "pid_" + p.Id + "_").ToArray();
        var gpuInst = new PerformanceCounterCategory("GPU Engine").GetInstanceNames().Where(i => !noEng && (i.Contains("engtype_VideoEncode") || i.Contains("engtype_VideoDecode") || ((i.Contains("engtype_3D") || i.Contains("engtype_Copy")) && pids.Any(p => i.StartsWith(p))))).ToArray();
        var gpuC = gpuInst.Select(i => new PerformanceCounter("GPU Engine", "Utilization Percentage", i, true)).ToArray();
        foreach (var c in nic) c.NextValue(); foreach (var c in gpuC) c.NextValue(); cpuC.NextValue(); faultC.NextValue();

        var w = outPath == null ? TextWriter.Null : new StreamWriter(outPath);
        w.WriteLine("t_s,gpu_util,mem_util,gfx_mhz,mem_mhz,power_w,temp_c,vram_used_mib,throttle,enc_util,enc_sessions,enc_fps,enc_latency_us,pcie_tx_kbps,pcie_rx_kbps,cpu_pct,ram_avail_mib,faults_s,nic_bytes_s,eng_3d_pct,eng_copy_pct,eng_enc_pct,eng_dec_pct,ms_nvml,ms_win,ms_eng");
        var tick = new List<double>();
        double period = 1000.0 / hz;
        long n = 0;
        while (wall.Elapsed.TotalSeconds < seconds)
        {
            var sw = Stopwatch.StartNew();
            var f = new List<string> { wall.Elapsed.TotalSeconds.ToString("F3", C) };
            Nvml.Util ut; Nvml.Mem mem; uint a, b, c2; ulong th;
            double t0 = sw.Elapsed.TotalMilliseconds;
            if (Nvml.nvmlDeviceGetUtilizationRates(dev, out ut) == 0) { f.Add(ut.gpu.ToString()); f.Add(ut.memory.ToString()); } else { f.Add(""); f.Add(""); }
            f.Add(Nvml.nvmlDeviceGetClockInfo(dev, 0, out a) == 0 ? a.ToString() : "");
            f.Add(Nvml.nvmlDeviceGetClockInfo(dev, 2, out a) == 0 ? a.ToString() : "");
            f.Add(Nvml.nvmlDeviceGetPowerUsage(dev, out a) == 0 ? (a / 1000.0).ToString("F1", C) : "");
            f.Add(Nvml.nvmlDeviceGetTemperature(dev, 0, out a) == 0 ? a.ToString() : "");
            f.Add(Nvml.nvmlDeviceGetMemoryInfo(dev, out mem) == 0 ? (mem.used >> 20).ToString() : "");
            f.Add(Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(dev, out th) == 0 ? "0x" + th.ToString("x") : "");
            f.Add(Nvml.nvmlDeviceGetEncoderUtilization(dev, out a, out b) == 0 ? a.ToString() : "");
            if (Nvml.nvmlDeviceGetEncoderStats(dev, out a, out b, out c2) == 0) { f.Add(a.ToString()); f.Add(b.ToString()); f.Add(c2.ToString()); } else { f.Add(""); f.Add(""); f.Add(""); }
            f.Add(pcieOk ? pcieTx.ToString() : ""); f.Add(pcieOk ? pcieRx.ToString() : "");
            double t1 = sw.Elapsed.TotalMilliseconds;
            f.Add(cpuC.NextValue().ToString("F1", C));
            f.Add(memC.NextValue().ToString("F0", C));
            f.Add(faultC.NextValue().ToString("F0", C));
            f.Add(nic.Sum(x => (double)x.NextValue()).ToString("F0", C));
            double t2 = sw.Elapsed.TotalMilliseconds;
            double e3 = 0, ec = 0, ee = 0, ed = 0;
            for (int i = 0; i < gpuC.Length; i++)
            {
                float v = gpuC[i].NextValue(); string nme = gpuInst[i];
                if (nme.Contains("engtype_3D")) e3 += v; else if (nme.Contains("engtype_Copy")) ec += v; else if (nme.Contains("engtype_VideoEncode")) ee += v; else ed += v;
            }
            f.Add(e3.ToString("F1", C)); f.Add(ec.ToString("F1", C)); f.Add(ee.ToString("F1", C)); f.Add(ed.ToString("F1", C));
            double t3 = sw.Elapsed.TotalMilliseconds;
            f.Add((t1 - t0).ToString("F2", C)); f.Add((t2 - t1).ToString("F2", C)); f.Add((t3 - t2).ToString("F2", C));
            w.WriteLine(string.Join(",", f)); w.Flush();
            tick.Add(sw.Elapsed.TotalMilliseconds);
            n++;
            double sleep = n * period - wall.Elapsed.TotalMilliseconds;
            if (sleep > 0) Thread.Sleep((int)sleep);
        }
        stop = true;
        w.Flush(); w.Dispose();
        self.Refresh();
        double cpuMs = (self.TotalProcessorTime - cpu0).TotalMilliseconds;
        tick.Sort();
        Console.WriteLine("samples " + tick.Count + " at " + hz + " Hz over " + wall.Elapsed.TotalSeconds.ToString("F1", C) + " s; instances read per tick: " + gpuC.Length + " GPU Engine, " + nic.Length + " NIC");
        Console.WriteLine("tick duration ms: p50 " + tick[tick.Count / 2].ToString("F2", C) + "  p99 " + tick[Math.Min(tick.Count - 1, tick.Count * 99 / 100)].ToString("F2", C) + "  max " + tick[tick.Count - 1].ToString("F2", C));
        Console.WriteLine("probe process CPU: " + cpuMs.ToString("F0", C) + " ms over " + wall.Elapsed.TotalSeconds.ToString("F1", C) + " s = " + (cpuMs / wall.Elapsed.TotalMilliseconds * 100).ToString("F2", C) + " % of one core");
        if (jitter)
        {
            List<double> l; lock (lat) l = lat.OrderBy(x => x).ToList();
            Console.WriteLine("10 ms sleeper lateness ms (n " + l.Count + "): p50 " + l[l.Count / 2].ToString("F2", C) + "  p99 " + l[l.Count * 99 / 100].ToString("F2", C) + "  max " + l[l.Count - 1].ToString("F2", C) + "  over 2 ms " + l.Count(x => x > 2));
        }
    }
}
