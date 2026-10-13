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
        /// the community profile in use (null: the generic path with its searches) and the bitrate it asks for; the tests then verify that configuration
        public HmdProfile Profile;
        public double ConfigMbps;
        /// the user's own ALVR / VisionALVR settings are used as they are (third option of the wizard): the tests verify them like a profile, nothing is searched or changed
        public bool Existing;
        public string ExistingEncodeProfile = "foveated";
        /// a fixed configuration to verify (a profile or the existing settings), as opposed to the generic path with its searches
        public bool Fixed => Profile != null || Existing;
        public string EncodeProfileArg => Profile != null ? (Profile.Foveated ? "foveated" : "full-split") : Existing ? ExistingEncodeProfile : "foveated";
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
                    if (!ok) throw new TestFailed(chk != null && chk.TryGetValue("reason", out var r) ? r as string : "the system check did not run");
                    var nvenc = chk.TryGetValue("nvenc_api", out var nv) ? nv as string ?? "" : "";
                    return $"{ctx.R.GpuName} ({ctx.R.GpuArch}), driver {ctx.R.Driver}, {ctx.R.VramMb / 1024.0:0} GB, NVENC API {nvenc}";
                },
            });

            steps.Add(new TestStep
            {
                Name = "Encoder speed at the chosen resolution and refresh rate",
                Run = async (ctx, st) =>
                {
                    string result = null; string error = null;
                    ctx.Status("Encoding test frames with the hardware encoder...");
                    await Host(ctx, Base + $"--live --bench 300 --noise-temporal --encode-profile {ctx.EncodeProfileArg}", (n, d) =>
                    {
                        if (n == "setup_error") error = d != null && d.TryGetValue("message", out var m) ? m as string : "setup error";
                        if (n != "bench" || d == null) return;
                        var r = ctx.R;
                        r.EncodeP50 = Num(d, "p50_ms"); r.EncodeP95 = Num(d, "p95_ms"); r.BudgetMs = Num(d, "budget_ms");
                        r.EncodeFits = d.TryGetValue("fits", out var f) && f is bool fb && fb;
                        r.EncW = (int)Num(d, "width"); r.EncH = (int)Num(d, "height"); r.Engines = (int)Num(d, "nvenc_engines");
                        r.SplitMode = (int)Num(d, "split_encode_mode"); r.Slices = Num(d, "slices_per_frame");
                        result = $"{r.EncW}x{r.EncH} encoded: p50 {r.EncodeP50:0.0} ms, p95 {r.EncodeP95:0.0} ms of {r.BudgetMs:0.0} ms per frame; {r.Engines} NVENC engine(s)";
                    }, 120000);
                    if (error != null) throw new TestFailed(error);
                    if (result == null) throw new TestFailed("the encoder test produced no result (see logs\\VisionALVR.log)");
                    if (!ctx.R.EncodeFits) { st.Warn(); result += ctx.Fixed ? " - too slow for this configuration: lower the resolution or the refresh rate in the setup" : " - too slow for this rate: the later tests and the proposal use a lower resolution or rate"; }
                    ctx.OnEncoderDone();
                    return result;
                },
            });

            steps.Add(new TestStep
            {
                Name = "Picture quality benchmark (about 1.5 minutes)",
                Run = async (ctx, st) =>
                {
                    if (ctx.Fixed) throw new TestSkipped(ctx.Existing ? "not needed: your configuration is used as it is" : "not needed: the profile gives the stream settings");
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
                    if (ctx.Fixed)
                    {
                        // one step at the configured bitrate: pass or fail with the reasons, no search for another bitrate
                        string perr = null, verdict = null; bool ok = false; var why = new List<string>(); var plost = new List<double>();
                        var mbps = Math.Max(30, (int)Math.Round(ctx.ConfigMbps));
                        ctx.Status($"Network check at {mbps} Mbps: waiting for the headset (open the ALVR app)...");
                        await Host(ctx, Base + $"--benchmark-network {mbps} --bench-step-s {Math.Max(ctx.StepSecs, 10)} --connect-timeout 120 --seconds 900 --encode-profile {ctx.EncodeProfileArg}", (n, d) =>
                        {
                            switch (n)
                            {
                                case "client_connected": ctx.Status($"Network check: headset connected, streaming {mbps} Mbps..."); break;
                                case "setup_error": perr = d != null && d.TryGetValue("message", out var m) ? m as string : "setup error"; break;
                                case "bench_step": plost.Add(Num(d, "packets_lost_per_s")); break;
                                case "bench_result":
                                    var steps0 = d != null && d.TryGetValue("steps", out var ss) ? ss as object[] : null;
                                    var s0 = steps0 != null && steps0.Length > 0 ? steps0[0] as Dictionary<string, object> : null;
                                    ok = s0 != null && s0.TryGetValue("ok", out var okv) && okv is bool okb && okb;
                                    if (s0 != null && s0.TryGetValue("why", out var wv) && wv is object[] wa) why.AddRange(wa.Select(x => x?.ToString() ?? ""));
                                    verdict = "done";
                                    break;
                            }
                        }, 600000);
                        if (perr != null) throw new TestFailed(perr);
                        if (verdict == null) throw new TestFailed("the headset did not connect or the test did not finish");
                        ctx.R.CleanMbps = ok ? mbps : 0;
                        ctx.R.LossPerS = plost.Count > 0 ? plost.Average() : -1;
                        if (!ok) { st.Warn(); return $"{mbps} Mbps did NOT run clean on your link: {string.Join(", ", why)}. Pick a lower network variant or bitrate in the setup (nothing was searched)."; }
                        return $"{mbps} Mbps ran clean (no packet loss, the headset kept its frame rate, the network latency did not rise)";
                    }
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
                Name = "Scene test: Littlest Tokyo through the real pipeline (head tracking, frames, latency)",
                NeedsHeadset = true,
                Run = async (ctx, st) =>
                {
                    if (!ctx.HeadsetAvailable) throw new TestSkipped("needs the headset connected");
                    var probe = System.IO.Path.Combine(Paths.Dir, "bench", "xr_probe.exe");
                    var scene = System.IO.Path.Combine(Paths.Dir, "bench", "littlest_tokyo.vab");
                    var runtime = System.IO.Path.Combine(Paths.Dir, "virtualdesktop-openxr.json");
                    if (!System.IO.File.Exists(probe) || !System.IO.File.Exists(scene) || !System.IO.File.Exists(runtime)) throw new TestSkipped("bench\\xr_probe.exe, bench\\littlest_tokyo.vab or the runtime is missing from this folder");
                    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "visionalvr_scene_test");
                    System.IO.Directory.CreateDirectory(tmp);
                    var poseCsv = System.IO.Path.Combine(tmp, "pose.csv");
                    try { System.IO.File.Delete(poseCsv); } catch { }
                    string error = null; bool connected = false, gameOn = false, gameDone = false;
                    DateTime gameStart = DateTime.MinValue;
                    double frames = 0, fpsW = 0, sameTs = 0, trkN = 0, gapP95 = 0, slices = 0, encMs = 0;
                    double w = 0, total = 0, game = 0, enc = 0, net = 0, dec = 0, dq = 0, vq = 0, cf = 0, df = 0, mbps = 0;
                    var host = new HostProcess();
                    host.OnEvent += (n, d) =>
                    {
                        try
                        {
                            if (n == "client_connected") connected = true;
                            else if (n == "setup_error") error = d != null && d.TryGetValue("message", out var m) ? m as string : "setup error";
                            else if (n == "game_started") { gameOn = true; gameStart = DateTime.UtcNow; }
                            else if (n == "game_ended") gameDone = true;
                            else if (n == "encoder_stats" && gameOn && !gameDone && (DateTime.UtcNow - gameStart).TotalSeconds > 5 && d != null)
                            {
                                // the first seconds (scene loading, the encoder settling) are left out
                                var f = Math.Max(1, Num(d, "frames"));
                                frames += f; fpsW += f * Num(d, "fps"); sameTs += Num(d, "same_ts_frames"); encMs += f * Num(d, "encode_ms_avg");
                                slices += f * Num(d, "slices_per_packet");
                                var gap = Sub(d, "tracking_gap_ms");
                                trkN += Num(gap, "n"); gapP95 = Math.Max(gapP95, Num(gap, "p95"));
                                var cl = Sub(d, "client");
                                if (cl != null)
                                {
                                    var sm = Math.Max(1, Num(cl, "samples"));
                                    w += sm; total += sm * Num(cl, "total_latency_ms"); game += sm * Num(cl, "game_ms"); enc += sm * Num(cl, "encode_ms"); net += sm * Num(cl, "network_ms");
                                    dec += sm * Num(cl, "decode_ms"); dq += sm * Num(cl, "decoder_queue_ms"); vq += sm * Num(cl, "vsync_queue_ms");
                                    cf += sm * Num(cl, "client_fps"); df += sm * Num(cl, "displayed_fps"); mbps += sm * Num(cl, "bitrate_mbps");
                                }
                            }
                        }
                        catch (Exception e) { Log.Write("ERROR", "scene test event handler: " + e.Message); }
                    };
                    var exited = new TaskCompletionSource<int>();
                    host.OnExit += code => exited.TrySetResult(code);
                    var herr = host.Start($"--install-dir \"{Paths.Dir}\" --shim --daemon --gui");
                    if (herr != null) throw new TestFailed(herr);
                    var reg = ctx.Ct.Register(() => { host.Stop(2000); exited.TrySetCanceled(); });
                    System.Diagnostics.Process app = null;
                    try
                    {
                        ctx.Status("Scene test: waiting for the headset (open the ALVR app)...");
                        var t0 = DateTime.UtcNow;
                        while (!connected && error == null && !exited.Task.IsCompleted && (DateTime.UtcNow - t0).TotalSeconds < 120) await Task.Delay(300, ctx.Ct);
                        if (error != null) throw new TestFailed(error);
                        if (!connected) throw new TestFailed("the headset did not connect");
                        var nFrames = (int)(ctx.Fps * 32);
                        var psi = new System.Diagnostics.ProcessStartInfo(probe, $"\"{System.IO.Path.Combine(tmp, "scene.log")}\" {nFrames} 60 \"{poseCsv}\"")
                        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = tmp };
                        psi.EnvironmentVariables["XR_RUNTIME_JSON"] = runtime;
                        psi.EnvironmentVariables["XR_PROBE_SCENE"] = scene;
                        try { app = System.Diagnostics.Process.Start(psi); } catch (Exception e) { throw new TestFailed("cannot start the scene app: " + e.Message); }
                        ctx.Status("Scene test: you are inside Littlest Tokyo. Look around slowly (left, right, up, down) for about 30 seconds...");
                        await Task.Run(() => { while (!app.HasExited && !ctx.Ct.IsCancellationRequested) System.Threading.Thread.Sleep(200); });
                        if (error != null) throw new TestFailed(error);
                    }
                    finally
                    {
                        try { if (app != null && !app.HasExited) app.Kill(); } catch { }
                        host.Stop(4000);
                        reg.Dispose();
                    }
                    ctx.Ct.ThrowIfCancellationRequested();
                    if (frames <= 0) throw new TestFailed("the scene app streamed no frames (see logs): the runtime may be registered for another folder or the scene app could not start");
                    var r = ctx.R;
                    r.SceneRan = true;
                    var secs = frames / Math.Max(1, fpsW / frames);
                    r.SceneAppFps = fpsW / frames;
                    r.SameTsPct = 100.0 * sameTs / frames;
                    r.HeadSamplesPerS = trkN / Math.Max(1, secs);
                    r.HeadGapP95Ms = gapP95;
                    r.SliceLive = slices / frames;
                    // the head pose as the scene app received it (headset -> host -> shim -> app): its range during the test
                    var yaw = new List<double>(); var xs = new List<double>(); var ys = new List<double>(); var zs = new List<double>();
                    try
                    {
                        foreach (var line in System.IO.File.ReadAllLines(poseCsv).Skip(1 + (int)ctx.Fps))   // the first second holds the start-up re-anchor
                        {
                            var c = line.Split(',');
                            if (c.Length < 5) continue;
                            yaw.Add(double.Parse(c[1], CultureInfo.InvariantCulture)); xs.Add(double.Parse(c[2], CultureInfo.InvariantCulture)); ys.Add(double.Parse(c[3], CultureInfo.InvariantCulture)); zs.Add(double.Parse(c[4], CultureInfo.InvariantCulture));
                        }
                    }
                    catch { }
                    if (yaw.Count == 0) throw new TestFailed("the scene app received no head pose from the headset");
                    // unwrap the yaw so a turn through +-180 is not counted as a jump
                    double acc = yaw[0], mn = acc, mx = acc;
                    for (int i = 1; i < yaw.Count; i++) { var dlt = yaw[i] - yaw[i - 1]; while (dlt > Math.PI) dlt -= 2 * Math.PI; while (dlt < -Math.PI) dlt += 2 * Math.PI; acc += dlt; mn = Math.Min(mn, acc); mx = Math.Max(mx, acc); }
                    r.HeadYawRangeDeg = (mx - mn) * 180 / Math.PI;
                    r.HeadMoveCm = 100 * Math.Max(xs.Max() - xs.Min(), Math.Max(ys.Max() - ys.Min(), zs.Max() - zs.Min()));
                    if (w > 0)
                    {
                        r.TotalMs = total / w; r.GameMs = game / w; r.EncoderMs = enc / w; r.NetworkMs = net / w; r.DecodeMs = dec / w; r.DecoderQueueMs = dq / w; r.VsyncQueueMs = vq / w;
                        r.ClientFps = cf / w; r.DisplayedFps = df / w; r.StreamedMbps = mbps / w;
                    }
                    var moved = r.HeadYawRangeDeg >= 5 || r.HeadMoveCm >= 3;
                    var problems = new List<string>();
                    if (r.HeadSamplesPerS < 0.8 * ctx.Fps) problems.Add($"head tracking arrives at only {r.HeadSamplesPerS:0}/s (expected about {ctx.Fps:0})");
                    if (!moved) problems.Add("no head movement was seen: look around during the test to verify tracking");
                    if (w > 0 && Reference.Latency(r.TotalMs, Targets.For(Math.Max(1, r.EncW), Math.Max(1, r.EncH), ctx.Fps)) == Rating.Poor) problems.Add("high latency");
                    if (problems.Count > 0) st.Warn();
                    return $"head pose reaches the scene ({r.HeadSamplesPerS:0}/s, gaps up to {r.HeadGapP95Ms:0} ms; turned {r.HeadYawRangeDeg:0} deg, moved {r.HeadMoveCm:0} cm); streamed {r.SceneAppFps:0} fps, {r.SameTsPct:0.#}% repeated head samples" +
                           (w > 0 ? $"; latency {r.TotalMs:0.0} ms (encode {r.EncoderMs:0.0}, network {r.NetworkMs:0.0}, decode {r.DecodeMs:0.0}, headset queues {r.DecoderQueueMs + r.VsyncQueueMs:0.0}), {r.DisplayedFps:0} fps shown" : "; the headset sent no statistics") +
                           (problems.Count > 0 ? " - " + string.Join("; ", problems) : "");
                },
            });

            steps.Add(new TestStep
            {
                Name = "Split encoding (one frame cut in strips, one per NVENC engine)",
                Run = async (ctx, st) =>
                {
                    await Task.Yield();
                    var wanted = ctx.Profile != null ? ctx.Profile.SplitEncode > 0 && !ctx.Profile.Foveated : ctx.Existing && ctx.ExistingEncodeProfile == "full-split";
                    if (!wanted) throw new TestSkipped(!ctx.Fixed || ctx.Existing ? "not used by this configuration (foveated encoding encodes a smaller frame in one piece)" : "this profile does not use it");
                    var r = ctx.R;
                    var benchOn = r.Engines >= 2 && r.Slices >= 1.5;
                    var live = r.SceneRan ? $"; the live stream carried {r.SliceLive:0.#} strips per frame" : "";
                    if (benchOn && (!r.SceneRan || r.SliceLive >= 1.5)) return $"engaged: {r.Slices:0.#} strips per frame on {r.Engines} NVENC engines (encoder test){live}";
                    st.Warn();
                    return $"NOT engaged: {r.Engines} NVENC engine(s), {r.Slices:0.#} strip per frame in the encoder test{live}. This GPU encodes the whole frame in one piece: expect a longer encode time, so lower the resolution or refresh rate if the encoder test was slow.";
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
