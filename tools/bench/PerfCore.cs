// perfcore.dll: the measurement library of the Bench flavour (docs/TELEMETRY_PLAN.md). Managed, no admin, no SDK: NVML from the NVIDIA driver's nvml.dll,
// Windows counters from System.Diagnostics. Never touches the streamer: it only reads the machine and writes a session folder.
//   hardware_gpu.csv      NVML, one row per tick            (L0 1 Hz, L1 5 Hz)
//   hardware_system.csv   CPU, RAM, faults, NIC, disk       (L0 1 Hz, L1 5 Hz)
//   hardware_pcie.csv     NVML PCIe throughput              (L1 1 Hz, own thread: the call blocks ~30 ms)
//   hardware_engines.csv  GPU Engine counters per process   (L1 1 Hz, only the named processes: ~0.25 ms per instance)
//   capabilities.csv, session.json, state_events.csv, logger_health.csv
// A reading that is unavailable is an EMPTY field, never 0. Every source has its own thread (NVML can stall for a second): a stall shows in
// logger_health.csv as that thread's max tick, and nothing else waits for it. Times are QPC seconds, the same clock as the host's CSVs (logs::qpc_at).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using System.Threading;

[assembly: AssemblyVersion("0.1.0.0")]

namespace AirFrameXR.Perf
{
    public enum Level { L0, L1 }

    public sealed class ProbeOptions
    {
        public string Dir;                       // the session folder (created)
        public Level Level = Level.L1;
        public string[] ProcessNames = new string[0];   // processes whose GPU engines are read (L1), e.g. the game and alvr_host
        public string SettingsDir;               // folder with airframexr.json / session.json to hash and watch (optional)
        public int GpuIndex;
    }

    public static class Clock
    {
        public static readonly double Frequency = Stopwatch.Frequency;
        public static double Now() { return Stopwatch.GetTimestamp() / Frequency; }
        public static string S(double t) { return t.ToString("F6", CultureInfo.InvariantCulture); }
    }

    static class F
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        public static string N(double v, string fmt) { return v.ToString(fmt, C); }
        public static string N(uint v) { return v.ToString(C); }
        public static string N(ulong v) { return v.ToString(C); }
        public static string N(long v) { return v.ToString(C); }
        public static string Q(string s) { return s == null ? "" : (s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s); }
    }

    /// <summary>One CSV file: header on creation, rows buffered, flushed by the health thread once a second (never per row).</summary>
    sealed class CsvOut
    {
        readonly StreamWriter w; readonly object gate = new object();
        public long Rows; public long Errors;
        public CsvOut(string path, string header) { w = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 1 << 16); w.NewLine = "\n"; w.WriteLine(header); }
        public void Row(string row) { try { lock (gate) { w.WriteLine(row); Rows++; } } catch { Errors++; } }
        public void Flush() { try { lock (gate) w.Flush(); } catch { Errors++; } }
        public void Close() { try { lock (gate) { w.Flush(); w.Dispose(); } } catch { Errors++; } }
    }

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
        [DllImport(Dll)] public static extern int nvmlDeviceGetName(IntPtr h, [MarshalAs(UnmanagedType.LPStr)] StringBuilder n, uint len);
        [DllImport(Dll)] public static extern int nvmlSystemGetDriverVersion([MarshalAs(UnmanagedType.LPStr)] StringBuilder n, uint len);
        [DllImport(Dll)] public static extern int nvmlSystemGetNVMLVersion([MarshalAs(UnmanagedType.LPStr)] StringBuilder n, uint len);
        [StructLayout(LayoutKind.Sequential)] public struct Util { public uint gpu, memory; }
        [StructLayout(LayoutKind.Sequential)] public struct Mem { public ulong total, free, used; }
    }

    /// <summary>The probe: start it with a session folder, stop it when the capture ends. Everything fails open: a missing DLL or counter leaves blank fields and a reason in capabilities.csv.</summary>
    public sealed class Probe : IDisposable
    {
        public const int SchemaVersion = 1;
        readonly ProbeOptions o;
        readonly List<Thread> threads = new List<Thread>();
        volatile bool stop;
        CsvOut gpuCsv, sysCsv, pcieCsv, engCsv, capCsv, stateCsv, healthCsv;
        IntPtr dev; bool nvmlOk; string nvmlVersion = "", driver = "", gpuName = "";
        readonly Dictionary<string, double> maxTickMs = new Dictionary<string, double>();
        readonly object tickGate = new object();
        public string SessionDir { get { return o.Dir; } }
        /// the newest reading of each live value (for a window's tiles): empty until the first sample, empty when the reading is unavailable
        public volatile string LiveGpuUtil = "", LiveClock = "", LivePower = "", LiveTemp = "", LiveNvenc = "", LiveLimiter = "";
        public long RowsGpu { get { return gpuCsv == null ? 0 : gpuCsv.Rows; } }
        public long LostRows { get { long e = 0; foreach (var c in new[] { gpuCsv, sysCsv, pcieCsv, engCsv, stateCsv }) if (c != null) e += c.Errors; return e; } }
        public readonly string SessionId = Guid.NewGuid().ToString();
        double startedQpc; DateTime startedUtc;

        public Probe(ProbeOptions options) { o = options; }

        public void Start()
        {
            Directory.CreateDirectory(o.Dir);
            startedQpc = Clock.Now(); startedUtc = DateTime.UtcNow;
            capCsv = new CsvOut(Path.Combine(o.Dir, "capabilities.csv"), "provider,capability,supported,measurement_quality,reason,provider_version");
            stateCsv = new CsvOut(Path.Combine(o.Dir, "state_events.csv"), "event_qpc,event_type,old_value,new_value,scope,config_hash,reason");
            healthCsv = new CsvOut(Path.Combine(o.Dir, "logger_health.csv"), "sample_qpc,rows_gpu,rows_system,rows_pcie,rows_engines,write_errors,sample_errors,max_tick_ms_gpu,max_tick_ms_system,max_tick_ms_pcie,max_tick_ms_engines,probe_process_cpu_pct");
            InitNvml();
            bool l1 = o.Level == Level.L1;
            double hz = l1 ? 5.0 : 1.0;
            gpuCsv = new CsvOut(Path.Combine(o.Dir, "hardware_gpu.csv"), "sample_qpc,gpu_util_pct,memory_util_pct,core_clock_mhz,memory_clock_mhz,board_power_w,temp_c,vram_used_bytes,vram_total_bytes,pstate,throttle_reasons,nvenc_util_pct,nvdec_util_pct,nvenc_sessions,nvenc_avg_fps,nvenc_avg_latency_us,tick_us");
            sysCsv = new CsvOut(Path.Combine(o.Dir, "hardware_system.csv"), "sample_qpc,cpu_total_pct,ram_available_bytes,page_faults_per_s,nic_bytes_per_s,disk_busy_pct,tick_us");
            Spawn("gpu", () => GpuLoop(hz));
            Spawn("system", () => SystemLoop(hz));
            if (l1)
            {
                pcieCsv = new CsvOut(Path.Combine(o.Dir, "hardware_pcie.csv"), "sample_qpc,pcie_tx_kbps,pcie_rx_kbps,call_ms");
                Spawn("pcie", PcieLoop);
                engCsv = new CsvOut(Path.Combine(o.Dir, "hardware_engines.csv"), "sample_qpc,pid,process,engine,util_pct");
                if (o.ProcessNames.Length > 0) Spawn("engines", EngineLoop);
                else Cap("windows", "gpu_engine_per_process", false, "exact", "no process names given", "");
            }
            if (!string.IsNullOrEmpty(o.SettingsDir)) Spawn("state", StateLoop);
            Spawn("health", HealthLoop);
            WriteSession();
        }

        public void Stop()
        {
            stop = true;
            foreach (var t in threads) t.Join(2500);     // a thread stuck in an NVML call is abandoned (background): never wait for it longer
            foreach (var c in new[] { gpuCsv, sysCsv, pcieCsv, engCsv, capCsv, stateCsv, healthCsv }) if (c != null) c.Close();
            WriteSession(true);
            try { if (nvmlOk) Nvml.nvmlShutdown(); } catch { }
        }

        public void Dispose() { if (!stop) Stop(); }

        void Spawn(string name, ThreadStart body)
        {
            lock (maxTickMs) maxTickMs[name] = 0;
            var t = new Thread(() => { try { body(); } catch (Exception e) { Cap("probe", name + "_thread", false, "exact", "stopped: " + e.GetType().Name + " " + e.Message, ""); } }) { IsBackground = true, Name = "perf_" + name };
            threads.Add(t); t.Start();
        }

        void Tick(string name, double ms) { lock (tickGate) { if (ms > maxTickMs[name]) maxTickMs[name] = ms; } }
        double TakeMax(string name) { lock (tickGate) { double v = maxTickMs[name]; maxTickMs[name] = 0; return v; } }
        void Cap(string provider, string cap, bool ok, string quality, string reason, string version)
        { capCsv.Row(string.Join(",", F.Q(provider), F.Q(cap), ok ? "1" : "0", quality, F.Q(reason), F.Q(version))); }

        long sampleErrors;
        string Nv(int rc, Func<string> value) { if (rc == 0) return value(); Interlocked.Increment(ref sampleErrors); return ""; }

        // ---- NVML -------------------------------------------------------------------------------------------------------------
        void InitNvml()
        {
            try
            {
                int rc = Nvml.nvmlInit_v2();
                if (rc != 0) { Cap("nvml", "init", false, "exact", "nvmlInit returned " + rc, ""); return; }
                rc = Nvml.nvmlDeviceGetHandleByIndex_v2((uint)o.GpuIndex, out dev);
                if (rc != 0) { Cap("nvml", "device", false, "exact", "no device " + o.GpuIndex + " (rc " + rc + ")", ""); return; }
                var sb = new StringBuilder(96); Nvml.nvmlDeviceGetName(dev, sb, 96); gpuName = sb.ToString();
                var dv = new StringBuilder(32); Nvml.nvmlSystemGetDriverVersion(dv, 32); driver = dv.ToString();
                var nv = new StringBuilder(32); Nvml.nvmlSystemGetNVMLVersion(nv, 32); nvmlVersion = nv.ToString();
                nvmlOk = true;
                CapProbe("gpu_utilization", () => { Nvml.Util u; return Nvml.nvmlDeviceGetUtilizationRates(dev, out u); });
                CapProbe("clocks", () => { uint a; return Nvml.nvmlDeviceGetClockInfo(dev, 0, out a); });
                CapProbe("power", () => { uint a; return Nvml.nvmlDeviceGetPowerUsage(dev, out a); });
                CapProbe("temperature", () => { uint a; return Nvml.nvmlDeviceGetTemperature(dev, 0, out a); });
                CapProbe("memory", () => { Nvml.Mem m; return Nvml.nvmlDeviceGetMemoryInfo(dev, out m); });
                CapProbe("clock_event_reasons", () => { ulong r; return Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(dev, out r); });
                CapProbe("encoder_utilization", () => { uint a, b; return Nvml.nvmlDeviceGetEncoderUtilization(dev, out a, out b); });
                CapProbe("decoder_utilization", () => { uint a, b; return Nvml.nvmlDeviceGetDecoderUtilization(dev, out a, out b); });
                CapProbe("encoder_stats", () => { uint a, b, c; return Nvml.nvmlDeviceGetEncoderStats(dev, out a, out b, out c); });
                CapProbe("performance_state", () => { int p; return Nvml.nvmlDeviceGetPerformanceState(dev, out p); });
                if (o.Level == Level.L1) CapProbe("pcie_throughput", () => { uint a; return Nvml.nvmlDeviceGetPcieThroughput(dev, 0, out a); }, "blocks ~30 ms per call: own 1 Hz thread");
            }
            catch (DllNotFoundException) { Cap("nvml", "init", false, "exact", "nvml.dll not found (NVIDIA driver missing?)", ""); }
            catch (Exception e) { Cap("nvml", "init", false, "exact", e.GetType().Name + ": " + e.Message, ""); }
        }

        void CapProbe(string cap, Func<int> call, string note = "")
        {
            int rc; try { rc = call(); } catch (Exception e) { Cap("nvml", cap, false, "exact", e.GetType().Name, nvmlVersion); return; }
            Cap("nvml", cap, rc == 0, "sampled", rc == 0 ? note : (rc == 3 ? "not supported by this GPU/driver" : "NVML error " + rc), nvmlVersion);
        }

        void GpuLoop(double hz)
        {
            if (!nvmlOk) return;
            double period = 1.0 / hz; long n = 0; var wall = Stopwatch.StartNew();
            while (!stop)
            {
                var sw = Stopwatch.StartNew(); double t = Clock.Now();
                Nvml.Util u; Nvml.Mem m; uint a, b, c; ulong th; int ps;
                var f = new List<string>(17) { Clock.S(t) };
                int rc = Nvml.nvmlDeviceGetUtilizationRates(dev, out u);
                f.Add(Nv(rc, () => F.N(u.gpu))); f.Add(Nv(rc, () => F.N(u.memory)));
                f.Add(Nv(Nvml.nvmlDeviceGetClockInfo(dev, 0, out a), () => F.N(a)));
                f.Add(Nv(Nvml.nvmlDeviceGetClockInfo(dev, 2, out a), () => F.N(a)));
                f.Add(Nv(Nvml.nvmlDeviceGetPowerUsage(dev, out a), () => F.N(a / 1000.0, "F1")));
                f.Add(Nv(Nvml.nvmlDeviceGetTemperature(dev, 0, out a), () => F.N(a)));
                rc = Nvml.nvmlDeviceGetMemoryInfo(dev, out m);
                f.Add(Nv(rc, () => F.N(m.used))); f.Add(Nv(rc, () => F.N(m.total)));
                f.Add(Nv(Nvml.nvmlDeviceGetPerformanceState(dev, out ps), () => "P" + ps));
                f.Add(Nv(Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(dev, out th), () => "0x" + th.ToString("x")));
                f.Add(Nv(Nvml.nvmlDeviceGetEncoderUtilization(dev, out a, out b), () => F.N(a)));
                f.Add(Nv(Nvml.nvmlDeviceGetDecoderUtilization(dev, out a, out b), () => F.N(a)));
                rc = Nvml.nvmlDeviceGetEncoderStats(dev, out a, out b, out c);
                f.Add(Nv(rc, () => F.N(a))); f.Add(Nv(rc, () => F.N(b))); f.Add(Nv(rc, () => F.N(c)));
                double ms = sw.Elapsed.TotalMilliseconds;
                f.Add(F.N((long)(ms * 1000)));
                gpuCsv.Row(string.Join(",", f)); Tick("gpu", ms);
                LiveGpuUtil = f[1]; LiveClock = f[3]; LivePower = f[5]; LiveTemp = f[6]; LiveNvenc = f[11]; LiveLimiter = f[10];
                Pace(ref n, period, wall);
            }
        }

        void PcieLoop()
        {
            if (!nvmlOk) return;
            while (!stop)
            {
                var sw = Stopwatch.StartNew(); double t = Clock.Now(); uint tx, rx;
                int r1 = Nvml.nvmlDeviceGetPcieThroughput(dev, 0, out tx), r2 = Nvml.nvmlDeviceGetPcieThroughput(dev, 1, out rx);
                double ms = sw.Elapsed.TotalMilliseconds;
                pcieCsv.Row(Clock.S(t) + "," + Nv(r1, () => F.N(tx)) + "," + Nv(r2, () => F.N(rx)) + "," + F.N(ms, "F1")); Tick("pcie", ms);
                SleepStop(1000);
            }
        }

        // ---- Windows counters ----------------------------------------------------------------------------------------------------
        void SystemLoop(double hz)
        {
            PerformanceCounter cpu = null, ram = null, faults = null, disk = null; PerformanceCounter[] nic = new PerformanceCounter[0];
            cpu = Make("Processor Information", "% Processor Utility", "_Total", "cpu_total");
            if (cpu == null) cpu = Make("Processor", "% Processor Time", "_Total", "cpu_total");
            ram = Make("Memory", "Available Bytes", null, "ram_available");
            faults = Make("Memory", "Page Faults/sec", null, "page_faults");
            disk = Make("PhysicalDisk", "% Disk Time", "_Total", "disk_busy");
            try { nic = new PerformanceCounterCategory("Network Interface").GetInstanceNames().Select(n => new PerformanceCounter("Network Interface", "Bytes Total/sec", n)).ToArray(); Cap("windows", "nic_bytes", true, "sampled", "all adapters summed (not the stream's own traffic)", ""); }
            catch (Exception e) { Cap("windows", "nic_bytes", false, "exact", e.GetType().Name, ""); }
            Prime(cpu, ram, faults, disk); foreach (var c in nic) Prime(c);
            SleepStop(200);
            double period = 1.0 / hz; long n = 0; var wall = Stopwatch.StartNew();
            while (!stop)
            {
                var sw = Stopwatch.StartNew(); double t = Clock.Now();
                string cpuV = Read(cpu, "F1"), ramV = Read(ram, "F0"), faultV = Read(faults, "F0"), diskV = Read(disk, "F1");
                string nicV = ""; if (nic.Length > 0) { double s = 0; bool ok = true; foreach (var c in nic) { try { s += c.NextValue(); } catch { ok = false; } } nicV = ok ? F.N(s, "F0") : ""; if (!ok) Interlocked.Increment(ref sampleErrors); }
                double ms = sw.Elapsed.TotalMilliseconds;
                sysCsv.Row(string.Join(",", Clock.S(t), cpuV, ramV, faultV, nicV, diskV, F.N((long)(ms * 1000)))); Tick("system", ms);
                Pace(ref n, period, wall);
            }
        }

        PerformanceCounter Make(string cat, string name, string inst, string cap)
        {
            try { var c = inst == null ? new PerformanceCounter(cat, name, true) : new PerformanceCounter(cat, name, inst, true); Cap("windows", cap, true, "sampled", cat + " / " + name, ""); return c; }
            catch (Exception e) { Cap("windows", cap, false, "exact", cat + " / " + name + ": " + e.GetType().Name, ""); return null; }
        }
        static void Prime(params PerformanceCounter[] cs) { foreach (var c in cs) { try { if (c != null) c.NextValue(); } catch { } } }
        string Read(PerformanceCounter c, string fmt) { if (c == null) return ""; try { return F.N(c.NextValue(), fmt); } catch { Interlocked.Increment(ref sampleErrors); return ""; } }

        // GPU Engine counters: only the named processes, instance list refreshed every 10 s (processes come and go)
        void EngineLoop()
        {
            var counters = new Dictionary<string, PerformanceCounter>();
            double nextRefresh = 0; var names = o.ProcessNames;
            bool capDone = false;
            while (!stop)
            {
                var sw = Stopwatch.StartNew(); double t = Clock.Now();
                try
                {
                    if (t >= nextRefresh)
                    {
                        nextRefresh = t + 10;
                        var pids = new Dictionary<int, string>();
                        foreach (var nm in names) foreach (var p in Process.GetProcessesByName(nm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? nm.Substring(0, nm.Length - 4) : nm)) pids[p.Id] = p.ProcessName;
                        var inst = new PerformanceCounterCategory("GPU Engine").GetInstanceNames().Where(i => { int k = i.IndexOf("_luid"); return k > 4 && pids.ContainsKey(PidOf(i)); }).ToArray();
                        foreach (var gone in counters.Keys.Except(inst).ToList()) { counters[gone].Dispose(); counters.Remove(gone); }
                        foreach (var i in inst.Except(counters.Keys).ToList()) { var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", i, true); c.NextValue(); counters[i] = c; }
                        if (!capDone) { Cap("windows", "gpu_engine_per_process", true, "sampled", "instances for " + string.Join("+", names), ""); capDone = true; }
                        lastPids = pids;
                    }
                    var sums = new Dictionary<string, double>();
                    foreach (var kv in counters)
                    {
                        float v; try { v = kv.Value.NextValue(); } catch { Interlocked.Increment(ref sampleErrors); continue; }
                        string key = PidOf(kv.Key) + "|" + EngineOf(kv.Key);
                        double s; sums.TryGetValue(key, out s); sums[key] = s + v;
                    }
                    foreach (var kv in sums)
                    {
                        var parts = kv.Key.Split('|'); int pid = int.Parse(parts[0], CultureInfo.InvariantCulture); string pn; lastPids.TryGetValue(pid, out pn);
                        engCsv.Row(Clock.S(t) + "," + pid + "," + F.Q(pn) + "," + parts[1] + "," + F.N(kv.Value, "F1"));
                    }
                }
                catch (Exception) { Interlocked.Increment(ref sampleErrors); }
                Tick("engines", sw.Elapsed.TotalMilliseconds);
                SleepStop(1000);
            }
            foreach (var c in counters.Values) c.Dispose();
        }
        Dictionary<int, string> lastPids = new Dictionary<int, string>();
        static int PidOf(string inst) { try { int a = inst.IndexOf("pid_") + 4, b = inst.IndexOf('_', a); return int.Parse(inst.Substring(a, b - a), CultureInfo.InvariantCulture); } catch { return -1; } }
        static string EngineOf(string inst) { int k = inst.IndexOf("engtype_"); return k < 0 ? "?" : inst.Substring(k + 8); }

        // ---- settings changes, health, session ------------------------------------------------------------------------------
        void StateLoop()
        {
            var last = new Dictionary<string, string>();
            while (!stop)
            {
                foreach (var f in new[] { "airframexr.json", "session.json" })
                {
                    string h = HashFile(Path.Combine(o.SettingsDir, f));
                    string old; bool had = last.TryGetValue(f, out old);
                    if (!had || old != h) { stateCsv.Row(string.Join(",", Clock.S(Clock.Now()), "config_changed", had ? old : "", h, f, h, had ? "file content changed during the capture" : "at start")); last[f] = h; }
                }
                SleepStop(1000);
            }
        }

        static string HashFile(string path)
        {
            try { using (var sha = SHA256.Create()) using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").Substring(0, 16).ToLowerInvariant(); }
            catch { return "absent"; }
        }

        void HealthLoop()
        {
            var self = Process.GetCurrentProcess(); var last = self.TotalProcessorTime; var lastT = Stopwatch.StartNew();
            while (!stop)
            {
                SleepStop(1000);
                self.Refresh(); var cpuNow = self.TotalProcessorTime;
                double cpu = (cpuNow - last).TotalMilliseconds / Math.Max(1.0, lastT.Elapsed.TotalMilliseconds) * 100.0;   // % of one core
                last = cpuNow; lastT.Restart();
                long werr = 0; foreach (var c in new[] { gpuCsv, sysCsv, pcieCsv, engCsv, stateCsv }) if (c != null) { c.Flush(); werr += c.Errors; }
                Func<CsvOut, string> rows = c => c == null ? "" : F.N(c.Rows);
                Func<string, string> mx = n => { lock (tickGate) { if (!maxTickMs.ContainsKey(n)) return ""; } return F.N(TakeMax(n), "F1"); };
                healthCsv.Row(string.Join(",", Clock.S(Clock.Now()), rows(gpuCsv), rows(sysCsv), rows(pcieCsv), rows(engCsv), F.N(werr), F.N(Interlocked.Read(ref sampleErrors)), mx("gpu"), mx("system"), mx("pcie"), mx("engines"), F.N(cpu, "F2")));
                healthCsv.Flush();
            }
        }

        void WriteSession(bool ended = false)
        {
            string flavour = "unknown";
            try { flavour = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flavour.txt")).Trim(); } catch { }
            var sb = new StringBuilder("{\n");
            Action<string, string, bool> kv = (k, v, num) => sb.Append("  \"").Append(k).Append("\": ").Append(num ? v : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"").Append(",\n");
            kv("schema_version", SchemaVersion.ToString(), true);
            kv("session_id", SessionId, false);
            kv("started_utc", startedUtc.ToString("o", CultureInfo.InvariantCulture), false);
            if (ended) { kv("ended_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), false); kv("ended_qpc_s", Clock.S(Clock.Now()), true); }
            kv("clock_domain", "pc_qpc_seconds (same as the host's CSVs)", false);
            kv("qpc_frequency_hz", F.N((long)Clock.Frequency), true);
            kv("started_qpc_s", Clock.S(startedQpc), true);
            kv("level", o.Level.ToString(), false);
            kv("perfcore_version", typeof(Probe).Assembly.GetName().Version.ToString(), false);
            kv("flavour_file", flavour, false);
            kv("os", Environment.OSVersion.VersionString, false);
            kv("logical_cpus", Environment.ProcessorCount.ToString(), true);
            kv("gpu", gpuName, false); kv("gpu_driver", driver, false); kv("nvml_version", nvmlVersion, false);
            kv("processes", string.Join("+", o.ProcessNames), false);
            if (!string.IsNullOrEmpty(o.SettingsDir)) { kv("airframexr_json_hash", HashFile(Path.Combine(o.SettingsDir, "airframexr.json")), false); kv("session_json_hash", HashFile(Path.Combine(o.SettingsDir, "session.json")), false); }
            kv("privacy", "no pixels, no pose, no packet payloads, no user paths", false);
            sb.Append("  \"complete\": ").Append(ended ? "true" : "false").Append("\n}\n");
            try { File.WriteAllText(Path.Combine(o.Dir, "session.json"), sb.ToString(), new UTF8Encoding(false)); } catch { }
        }

        void Pace(ref long n, double period, Stopwatch wall)
        {
            n++;
            double wait = n * period - wall.Elapsed.TotalSeconds;
            if (wait > 0) SleepStop((int)(wait * 1000));
            else if (wait < -period) { wall.Restart(); n = 0; }   // fell behind (an NVML stall): skip, do not burst; the gap stays visible in the CSV
        }
        void SleepStop(int ms) { for (int left = ms; left > 0 && !stop; left -= 50) Thread.Sleep(Math.Min(50, left)); }
    }
}
