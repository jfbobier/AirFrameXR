// The wizard's tests: each one is a TestStep with a state the checklist shows; they run in sequence, a failure does not stop the others.
// The measurements are made by alvr_host.exe modes that already exist (--bench, --benchmark-quality, --benchmark-network, --live) and
// collected into Results.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VisionALVR
{
    public enum TestState { Pending, Running, Passed, Warning, Failed, Skipped }

    public class TestFailed : Exception { public TestFailed(string m) : base(m) { } }
    public class TestSkipped : Exception { public TestSkipped(string m) : base(m) { } }

    public class TestStep
    {
        public string Name;
        public bool NeedsHeadset;
        public TestState State = TestState.Pending;
        public string Note = "";
        public Func<TestContext, TestStep, Task<string>> Run;   // returns the note; may call Warn(); throws TestFailed / TestSkipped
        public bool Warned;
        public void Warn() { Warned = true; }
    }

    public class TestContext
    {
        public Handshake HS = new Handshake();
        public Dictionary<string, object> Check;
        public Results R = new Results();
        public double Fps = 90;
        public bool HeadsetAvailable;
        public string NetSteps = "100,150,200,250,300,350,400,500";
        public int StepSecs = 6;
        public Action<string> Status = s => { };
        /// called when the encoder test is done: the wizard applies the interim proposal (so the later tests run at a configuration the encoder can sustain)
        public Action OnEncoderDone = () => { };
        public CancellationToken Ct;
    }

    public static class Tests
    {
        static string Fmt(Dictionary<string, object> d, string k, string f)
        {
            try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString(f, CultureInfo.InvariantCulture) : "-"; }
            catch { return "-"; }
        }

        static double Num(Dictionary<string, object> d, string k)
        {
            try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0; }
            catch { return 0; }
        }

        static Dictionary<string, object> Sub(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;

        /// Runs alvr_host.exe with `args`, handing its events to `on`; completes with the exit code (-1 on timeout / cancel).
        public static Task<int> Host(TestContext c, string args, Action<string, Dictionary<string, object>> on, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<int>();
            var h = new HostProcess();
            h.OnEvent += (n, d) => { try { on(n, d); } catch (Exception e) { Log.Write("ERROR", "test event handler: " + e.Message); } };
            h.OnExit += code => tcs.TrySetResult(code);
            var err = h.Start(args);
            if (err != null) { tcs.TrySetException(new TestFailed(err)); return tcs.Task; }
            var reg = c.Ct.Register(() => { h.Stop(2000); tcs.TrySetCanceled(); });
            Task.Delay(timeoutMs).ContinueWith(_ => { if (!tcs.Task.IsCompleted) { h.Stop(2000); tcs.TrySetResult(-1); } });
            return tcs.Task.ContinueWith(t => { reg.Dispose(); return t.Result; });
        }

        static string Base => $"--install-dir \"{Paths.Dir}\" ";

        public static List<TestStep> Build(TestContext c)
        {
            var steps = new List<TestStep>();

            steps.Add(new TestStep
            {
                Name = "System check (GPU, driver, hardware encoder)",
                Run = async (ctx, st) =>
                {
                    await Task.Yield();
                    var chk = ctx.Check;
                    var gpu = Sub(chk, "gpu");
                    ctx.R.GpuName = gpu != null && gpu.TryGetValue("name", out var n) ? n as string ?? "" : "";
                    ctx.R.GpuArch = gpu != null && gpu.TryGetValue("arch_name", out var a) ? a as string ?? "" : "";
                    ctx.R.Driver = gpu != null && gpu.TryGetValue("driver", out var d) ? d as string ?? "" : "";
                    ctx.R.VramMb = (int)Num(gpu, "vram_mb");
                    var ok = chk != null && chk.TryGetValue("ok", out var o) && o is bool ob && ob;
                    var overrideOn = chk != null && chk.TryGetValue("gpu_override", out var go) && go is bool gb && gb;
                    var supported = chk != null && chk.TryGetValue("gpu_supported", out var gs) && gs is bool gsb && gsb;
                    if (!ok) throw new TestFailed(chk != null && chk.TryGetValue("reason", out var r) ? r as string : "the system check did not run");
                    if (!supported && overrideOn) st.Warn();
                    return $"{ctx.R.GpuName} ({ctx.R.GpuArch}), driver {ctx.R.Driver}, {ctx.R.VramMb / 1024.0:0} GB" + (!supported && overrideOn ? " - not a supported GPU, allowed for testing" : "");
                },
            });

            steps.Add(new TestStep
            {
                Name = "Encoder speed at the chosen resolution and refresh rate",
                Run = async (ctx, st) =>
                {
                    string result = null; string error = null;
                    ctx.Status("Encoding test frames with the hardware encoder...");
                    await Host(ctx, Base + "--live --bench 300 --noise-temporal --encode-profile foveated", (n, d) =>
                    {
                        if (n == "setup_error") error = d != null && d.TryGetValue("message", out var m) ? m as string : "setup error";
                        if (n != "bench" || d == null) return;
                        var r = ctx.R;
                        r.EncodeP50 = Num(d, "p50_ms"); r.EncodeP95 = Num(d, "p95_ms"); r.BudgetMs = Num(d, "budget_ms");
                        r.EncodeFits = d.TryGetValue("fits", out var f) && f is bool fb && fb;
                        r.EncW = (int)Num(d, "width"); r.EncH = (int)Num(d, "height"); r.Engines = (int)Num(d, "nvenc_engines");
                        result = $"{r.EncW}x{r.EncH} encoded: p50 {r.EncodeP50:0.0} ms, p95 {r.EncodeP95:0.0} ms of {r.BudgetMs:0.0} ms per frame; {r.Engines} NVENC engine(s)";
                    }, 120000);
                    if (error != null) throw new TestFailed(error);
                    if (result == null) throw new TestFailed("the encoder test produced no result (see logs\\VisionALVR.log)");
                    if (!ctx.R.EncodeFits) { st.Warn(); result += " - too slow for this rate: the later tests and the proposal use a lower resolution or rate"; }
                    ctx.OnEncoderDone();
                    return result;
                },
            });

            steps.Add(new TestStep
            {
                Name = "Picture quality benchmark (about 1.5 minutes)",
                Run = async (ctx, st) =>
                {
                    string error = null; int n = 0; string line = null;
                    ctx.Status("Quality benchmark: starting...");
                    await Host(ctx, Base + "--benchmark-quality", (ev, d) =>
                    {
                        switch (ev)
                        {
                            case "bq_result": n++; ctx.Status($"Quality benchmark: {n} configurations measured..."); break;
                            case "bq_error":
                            case "setup_error": error = d != null && d.TryGetValue("message", out var m) ? m as string : ev; break;
                            case "bq_summary":
                                var rec = Sub(Sub(d, "proposals"), "recommended");
                                if (rec != null)
                                {
                                    ctx.R.QualityLabel = rec.TryGetValue("label", out var l) ? l as string ?? "" : "";
                                    ctx.R.QualityPsnr = Num(Sub(rec, "psnr"), "weighted");
                                    ctx.R.QualityEncodeP95 = Num(Sub(rec, "encode_ms"), "p95");
                                    line = $"recommended: {ctx.R.QualityLabel} ({ctx.R.QualityPsnr:0.0} dB, encode p95 {ctx.R.QualityEncodeP95:0.0} ms)";
                                }
                                var clk = Sub(d, "gpu_clocks");
                                if (clk != null && clk.TryGetValue("stable", out var s) && s is bool sb && !sb) { st.Warn(); line += " - the GPU clock changed during the test (laptop on battery?)"; }
                                break;
                        }
                    }, 360000);
                    if (error != null) throw new TestFailed(error);
                    return line ?? throw new TestFailed("the quality benchmark produced no summary");
                },
            });

            steps.Add(new TestStep
            {
                Name = "Network test with the headset",
                NeedsHeadset = true,
                Run = async (ctx, st) =>
                {
                    if (!ctx.HeadsetAvailable) throw new TestSkipped("needs the headset connected");
                    string error = null; int rec = 0; var lost = new List<double>(); string note = null;
                    ctx.Status("Network test: waiting for the headset (open the ALVR app)...");
                    await Host(ctx, Base + $"--benchmark-network {ctx.NetSteps} --bench-step-s {ctx.StepSecs} --connect-timeout 120 --seconds 900 --encode-profile foveated", (n, d) =>
                    {
                        switch (n)
                        {
                            case "client_connected": ctx.Status("Network test: headset connected, testing..."); break;
                            case "setup_error": error = d != null && d.TryGetValue("message", out var m) ? m as string : "setup error"; break;
                            case "bench_step_start": ctx.Status($"Network test: {d?["target_mbps"]} Mbps..."); break;
                            case "bench_step": lost.Add(Num(d, "packets_lost_per_s")); break;
                            case "bench_result":
                                rec = (int)Num(d, "recommended_mbps");
                                note = rec > 0 ? $"clean up to {rec} Mbps (the highest clean step minus 10%)" : "no step passed: " + (d != null && d.TryGetValue("note", out var nt) ? nt : "");
                                break;
                        }
                    }, 1200000);
                    if (error != null) throw new TestFailed(error);
                    if (note == null) throw new TestFailed("the headset did not connect or the test did not finish");
                    ctx.R.CleanMbps = rec;
                    ctx.R.LossPerS = lost.Count > 0 ? lost.Average() : -1;
                    if (rec <= 0) { st.Warn(); }
                    return note;
                },
            });

            steps.Add(new TestStep
            {
                Name = "Streaming test with the headset (latency, frames, decoder)",
                NeedsHeadset = true,
                Run = async (ctx, st) =>
                {
                    if (!ctx.HeadsetAvailable) throw new TestSkipped("needs the headset connected");
                    string error = null; bool connected = false;
                    double w = 0, total = 0, game = 0, enc = 0, net = 0, dec = 0, dq = 0, vq = 0, cf = 0, df = 0, mbps = 0;
                    void take(Dictionary<string, object> cl)
                    {
                        if (cl == null) return;
                        var s = Math.Max(1, Num(cl, "samples"));
                        w += s; total += s * Num(cl, "total_latency_ms"); game += s * Num(cl, "game_ms"); enc += s * Num(cl, "encode_ms"); net += s * Num(cl, "network_ms");
                        dec += s * Num(cl, "decode_ms"); dq += s * Num(cl, "decoder_queue_ms"); vq += s * Num(cl, "vsync_queue_ms");
                        cf += s * Num(cl, "client_fps"); df += s * Num(cl, "displayed_fps"); mbps += s * Num(cl, "bitrate_mbps");
                    }
                    ctx.Status("Streaming test: waiting for the headset (open the ALVR app)...");
                    await Host(ctx, Base + "--live --seconds 24 --connect-timeout 120", (n, d) =>
                    {
                        if (n == "client_connected") { connected = true; ctx.Status("Streaming test: measuring for 24 s..."); }
                        else if (n == "setup_error") error = d != null && d.TryGetValue("message", out var m) ? m as string : "setup error";
                        else if (n == "encoder_stats" || n == "status") take(Sub(d, "client"));
                    }, 240000);
                    if (error != null) throw new TestFailed(error);
                    if (!connected) throw new TestFailed("the headset did not connect");
                    if (w <= 0) throw new TestFailed("the headset sent no statistics");
                    var r = ctx.R;
                    r.TotalMs = total / w; r.GameMs = game / w; r.EncoderMs = enc / w; r.NetworkMs = net / w; r.DecodeMs = dec / w; r.DecoderQueueMs = dq / w; r.VsyncQueueMs = vq / w;
                    r.ClientFps = cf / w; r.DisplayedFps = df / w; r.StreamedMbps = mbps / w;
                    if (Reference.Latency(r.TotalMs) == Rating.Poor) st.Warn();
                    return $"latency {r.TotalMs:0.0} ms (encode {r.EncoderMs:0.0}, network {r.NetworkMs:0.0}, decode {r.DecodeMs:0.0}, headset queues {r.DecoderQueueMs + r.VsyncQueueMs:0.0}), {r.DisplayedFps:0} fps shown";
                },
            });

            steps.Add(new TestStep
            {
                Name = "Audio output",
                Run = async (ctx, st) =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command \"Get-CimInstance Win32_SoundDevice | ForEach-Object { $_.Name }\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    string text;
                    using (var p = System.Diagnostics.Process.Start(psi)) { text = await Task.Run(() => { var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(15000); return o; }); }
                    var names = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    var cable = names.FirstOrDefault(x => x.IndexOf("cable", StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0);
                    ctx.R.AudioNote = names.Count == 0 ? "no audio device found" : (cable != null ? $"{names.Count} audio device(s), virtual device: {cable}" : $"{names.Count} audio device(s), no virtual cable");
                    if (names.Count == 0) { st.Warn(); }
                    return ctx.R.AudioNote + " (game audio is captured from the Windows default output)";
                },
            });
            return steps;
        }

        /// Runs the steps in order; `changed` is called (from the worker) whenever a step's state or note changes.
        public static async Task RunAll(List<TestStep> steps, TestContext c, Action<TestStep> changed)
        {
            foreach (var st in steps)
            {
                if (c.Ct.IsCancellationRequested) { st.State = TestState.Skipped; st.Note = "cancelled"; changed(st); continue; }
                st.State = TestState.Running; st.Note = ""; changed(st);
                try
                {
                    st.Note = await st.Run(c, st);
                    st.State = st.Warned ? TestState.Warning : TestState.Passed;
                }
                catch (TestSkipped s) { st.State = TestState.Skipped; st.Note = s.Message; }
                catch (TaskCanceledException) { st.State = TestState.Skipped; st.Note = "cancelled"; }
                catch (OperationCanceledException) { st.State = TestState.Skipped; st.Note = "cancelled"; }
                catch (TestFailed f) { st.State = TestState.Failed; st.Note = f.Message; }
                catch (Exception e) { st.State = TestState.Failed; st.Note = e.Message; Log.Write("ERROR", $"test '{st.Name}': {e}"); }
                Log.Write(st.State == TestState.Failed ? "ERROR" : "INFO", $"wizard test '{st.Name}': {st.State} - {st.Note}");
                changed(st);
            }
        }
    }
}
