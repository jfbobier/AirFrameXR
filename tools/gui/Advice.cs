// Suggestions for the Diagnose window and the wizard's results page, from docs/PERFORMANCE_KNOBS.md: each knob there names what should drive it; this reads
// those measurements (the wizard's tests of this run, and, when there are any, the logs: the newest debug session and the per-game records) and proposes
// the knob with the number next to it. The streamer measures and records, this only proposes: the user applies.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VisionALVR
{
    public enum Sev { Info, Hint, Warn }

    public class Suggestion
    {
        public Sev Sev;
        public string Title = "", Measured = "", Do = "", Source = "", Knob = "";
        public Action Apply; public string ApplyText;
    }

    /// What the logs say. Everything optional: no debug session, no games played, an empty report.
    public class LogFacts
    {
        public string Run = ""; public DateTime When;
        public int Windows;                                  // encoder_stats windows with the game streaming
        public double EncodeP95, TrackGapP95, TrackOver15Share, SpreadMed, SpreadP90, PeriodMs;
        public int PhaseSamples;
        public double VideoMhz, GpuPct; public string Pstate = ""; public int GpuSamples;
        public class Game { public string Name = "", Exe = "", Headset = "", Level = ""; public DateTime When; public double Fps, Hz, RepTyp, RepP90, HostRep, LatencyMs, MvFrames, MvUsed; public bool MvEnabled; }
        public List<Game> Games = new List<Game>();

        static double Num(Dictionary<string, object> d, string k, double def = double.NaN) { try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : def; } catch { return def; } }
        static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        static double Median(List<double> v) { if (v.Count == 0) return double.NaN; v.Sort(); return v[v.Count / 2]; }
        static double Pct(List<double> v, double q) { if (v.Count == 0) return double.NaN; v.Sort(); return v[(int)Math.Round((v.Count - 1) * q)]; }

        /// The newest debug session with a game streaming in it (at most the five newest are looked at), and the latest session of each recorded game.
        public static LogFacts Read(string logsDir, string gamesDir)
        {
            var f = new LogFacts();
            try
            {
                var debug = Path.Combine(logsDir, "debug");
                if (Directory.Exists(debug))
                    foreach (var dir in new DirectoryInfo(debug).GetDirectories().OrderByDescending(d => d.Name).Take(5))
                        if (f.ReadRun(dir.FullName)) { f.Run = dir.Name; f.When = dir.LastWriteTime; break; }
            }
            catch (Exception e) { Log.Write("WARN", "advice: debug logs: " + e.Message); }
            try
            {
                if (Directory.Exists(gamesDir))
                    foreach (var file in Directory.GetFiles(gamesDir, "*.json"))
                    {
                        var g = Json.Load(file);
                        if (!(g.TryGetValue("sessions", out var so) && so is object[] ss && ss.Length > 0 && ss[ss.Length - 1] is Dictionary<string, object> s)) continue;
                        var net = Obj(s, "net"); var mv = Obj(s, "mv");
                        var name = g.TryGetValue("name", out var n) && n is string ns && ns.Length > 0 ? ns : (g.TryGetValue("exe", out var e) ? Convert.ToString(e) : Path.GetFileNameWithoutExtension(file));
                        f.Games.Add(new Game
                        {
                            Name = name, Exe = g.TryGetValue("exe", out var ex) ? Convert.ToString(ex) : "", Headset = s.TryGetValue("headset", out var h) ? Convert.ToString(h) : "", Level = s.TryGetValue("level", out var l) ? Convert.ToString(l) : "",
                            When = DateTimeOffset.FromUnixTimeSeconds((long)Num(s, "start", 0)).LocalDateTime, Fps = Num(s, "rendered_fps"), Hz = Num(s, "display_hz"), RepTyp = Num(s, "headset_repeated_typ") * 100, RepP90 = Num(s, "headset_repeated_p90") * 100,
                            HostRep = Num(s, "host_repeats_typ") * 100, LatencyMs = Num(net, "latency_ms"), MvEnabled = mv != null && mv.TryGetValue("enabled", out var me) && me is bool mb && mb, MvFrames = Num(mv, "frames", 0), MvUsed = Num(mv, "used_eyes", 0),
                        });
                    }
                f.Games = f.Games.OrderByDescending(x => x.When).Take(4).ToList();
            }
            catch (Exception e) { Log.Write("WARN", "advice: game records: " + e.Message); }
            return f;
        }

        bool ReadRun(string dir)
        {
            var ev = Path.Combine(dir, "host_events.jsonl");
            if (!File.Exists(ev)) return false;
            var enc = new List<double>(); var gap = new List<double>(); var spread = new List<double>(); var periods = new List<double>();
            double over = 0, n = 0;
            foreach (var line in File.ReadLines(ev).Reverse().Take(4000))   // the end of a long session is what the setup looks like now
            {
                if (line.IndexOf("\"encoder_stats\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"phase_lock\"", StringComparison.Ordinal) < 0) continue;
                Dictionary<string, object> j; try { j = Json.Parse(line); } catch { continue; }
                var d = Obj(j, "data"); if (d == null) continue;
                var name = j.TryGetValue("event", out var en) ? Convert.ToString(en) : "";
                if (name == "encoder_stats")
                {
                    if (!(Num(d, "fps", 0) >= 50)) continue;   // a window of the game streaming (at the display rate, 60 Hz or more; menus and idle are 30 fps green frames)
                    var gp = Obj(Obj(d, "tracking_gap_ms") != null ? d : null, "tracking_gap_ms");
                    var p = Num(d, "encode_ms_p95"); if (!double.IsNaN(p) && p > 0) enc.Add(p);
                    if (gp != null) { var g95 = Num(gp, "p95"); if (!double.IsNaN(g95)) gap.Add(g95); over += Num(gp, "over_15ms", 0); n += Num(gp, "n", 0); }
                    Windows++;
                }
                else if (name == "phase_lock") { var s = Num(d, "spread_ms"); if (!double.IsNaN(s) && s > 0) spread.Add(s); var pm = Num(d, "period_ms"); if (!double.IsNaN(pm)) periods.Add(pm); }
            }
            if (Windows < 3) { Windows = 0; return false; }
            EncodeP95 = Median(enc); TrackGapP95 = Median(gap); TrackOver15Share = n > 0 ? over / n : double.NaN;
            PhaseSamples = spread.Count; SpreadMed = Median(spread); SpreadP90 = Pct(spread, 0.9); PeriodMs = Median(periods);
            var gpu = Path.Combine(dir, "gpu.csv");
            if (File.Exists(gpu))
            {
                var vid = new List<double>(); var util = new List<double>(); var ps = new Dictionary<string, int>();
                foreach (var l in File.ReadLines(gpu).Skip(1))
                {
                    var c = l.Split(',');
                    if (c.Length < 11) continue;
                    if (double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var u)) util.Add(u);
                    if (double.TryParse(c[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) vid.Add(v);
                    ps[c[10]] = ps.TryGetValue(c[10], out var k) ? k + 1 : 1;
                }
                GpuSamples = util.Count; GpuPct = Median(util); VideoMhz = Median(vid);
                Pstate = ps.Count > 0 ? ps.OrderByDescending(x => x.Value).First().Key : "";
            }
            return true;
        }
    }

    public static class Advice
    {
        static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

        /// r: the wizard's measurements of this run (null: none); facts: the logs; fps: the refresh rate in use. debugOn: the launcher's debug tick.
        public static List<Suggestion> From(Results r, Handshake hs, double fps, bool debugOn, LogFacts facts, Action turnDebugOff = null)
        {
            var s = new List<Suggestion>();
            facts = facts ?? new LogFacts();
            double period = fps > 0 ? 1000.0 / fps : 11.1;
            string logSrc = facts.Windows > 0 ? $"from your logs (debug session {facts.Run}, {facts.Windows} windows of streaming)" : "";

            // encoder headroom: the generator needs 1-3 ms per output on top of the encode (docs: GPU and encoder family)
            double enc = r != null && r.EncodeP95 > 0 ? r.EncodeP95 : double.NaN; string encSrc = "from the wizard's encoder test";
            if (!double.IsNaN(facts.EncodeP95) && (double.IsNaN(enc) || r == null)) { enc = facts.EncodeP95; encSrc = logSrc; }
            if (!double.IsNaN(enc) && enc >= 0.5 * period)
                s.Add(new Suggestion { Sev = enc >= 0.7 * period ? Sev.Warn : Sev.Hint, Knob = "NVENC preset / resolution", Source = encSrc,
                    Title = "The encoder leaves little room for frame generation",
                    Measured = $"encode p95 {F(enc)} ms of a {F(period)} ms period ({F(100 * enc / period, "0")} %); the generator adds about 1-3 ms per output",
                    Do = "keep the P2/P3 low-latency preset and foveated encoding; if you use frame generation, a lower resolution per eye buys the room" });

            // tracking spacing = Wi-Fi quality in one number
            double gapP95 = double.NaN; string gapSrc = "";
            if (r != null && r.SceneRan && r.HeadGapP95Ms > 0) { gapP95 = r.HeadGapP95Ms; gapSrc = "from the wizard's scene test"; }
            else if (!double.IsNaN(facts.TrackGapP95)) { gapP95 = facts.TrackGapP95; gapSrc = logSrc; }
            bool lowRate = r != null && r.SceneRan && fps > 0 && r.HeadSamplesPerS > 0 && r.HeadSamplesPerS < 0.8 * fps;
            bool burstLogs = !double.IsNaN(facts.TrackOver15Share) && facts.TrackOver15Share > 0.05;
            if ((!double.IsNaN(gapP95) && gapP95 > 15) || lowRate || burstLogs)
                s.Add(new Suggestion { Sev = Sev.Warn, Knob = "the wireless link", Source = gapSrc.Length > 0 ? gapSrc : logSrc,
                    Title = "The headset's tracking arrives in bursts",
                    Measured = (double.IsNaN(gapP95) ? "" : $"tracking gaps p95 {F(gapP95)} ms (a regular stream is under 12) ") + (burstLogs ? $"{F(100 * facts.TrackOver15Share, "0.0")} % of gaps over 15 ms " : "") + (lowRate ? $"{F(r.HeadSamplesPerS, "0")} samples/s against {F(fps, "0")} Hz" : ""),
                    Do = "this is the Wi-Fi: a 5 GHz or 6 GHz channel without neighbours (a neighbour's network cost one night its smoothness; channel 149 cured it), the PC on Ethernet, the router in the room; a noisy link also wants the larger arrival margin below" });
            if (r != null && r.SceneRan && r.SameTsPct >= 5)
                s.Add(new Suggestion { Sev = Sev.Hint, Knob = "the headset's tracking stamps", Source = "from the wizard's scene test",
                    Title = "Some frames carry the same head sample as the one before",
                    Measured = $"{F(r.SameTsPct, "0.0")} % of frames share the previous frame's tracking timestamp",
                    Do = "the headset skips them (shown fps a little under the host's). Known on the Vision Pro in heavy games: the game reads its pose in bursts; a fix needs a client change, nothing to set here" });

            // arrival margin: the knob the UI should own (logs only: the wizard does not see the phase lock)
            if (facts.PhaseSamples >= 5 && !double.IsNaN(facts.SpreadMed))
            {
                int proposal = (int)Math.Max(4, Math.Min(10, Math.Round(0.85 * facts.SpreadMed)));
                bool differs = Math.Abs(proposal - 7) >= 2;
                s.Add(new Suggestion { Sev = differs ? Sev.Hint : Sev.Info, Knob = "VISIONALVR_PHASE_MARGIN_MS", Source = logSrc,
                    Title = differs ? $"Arrival margin: try {proposal} ms instead of the default 7" : "Arrival margin: the default 7 ms fits your link",
                    Measured = $"frames arrive over a spread of {F(facts.SpreadMed)} ms (p90 {F(facts.SpreadP90)}) of a {F(facts.PeriodMs > 0 ? facts.PeriodMs : period)} ms period",
                    Do = differs ? $"the margin steers the earliest arrivals that far before the headset's tick; it should cover the spread so the p90 arrival lands before it. Set the environment variable VISIONALVR_PHASE_MARGIN_MS={proposal} before starting the streamer (not a launcher setting yet); it costs at most one period of latency, invisible until it crosses a tick"
                                    : "frames land before the headset's tick; nothing to change" });
            }

            // GPU power state: a laptop GPU that idles while it streams makes the encode slow
            if (facts.GpuSamples >= 5 && !double.IsNaN(facts.VideoMhz) && facts.VideoMhz > 0 && facts.VideoMhz < 900 && facts.GpuPct < 30)
                s.Add(new Suggestion { Sev = Sev.Warn, Knob = "--gpu-keepalive", Source = logSrc,
                    Title = "The GPU sits in a low power state while streaming",
                    Measured = $"power state {facts.Pstate}, video clock {F(facts.VideoMhz, "0")} MHz, GPU {F(facts.GpuPct, "0")} % busy (a laptop at P8 took 30 ms to encode what P0 does in 9)",
                    Do = "start the streamer's host with --gpu-keepalive 90 (a small load that keeps the GPU out of idle), or set the Windows power mode and the NVIDIA power management to maximum performance" });

            // the display rate in force
            if (hs != null && hs.Rates != null && hs.Rates.Count > 1)
                s.Add(new Suggestion { Sev = Sev.Info, Knob = "display refresh rate", Source = "from the headset's handshake",
                    Title = $"The headset can run at {string.Join(" / ", hs.Rates.Select(x => F(x, "0")))} Hz",
                    Measured = "the Vision Pro switches between 90 and 100 Hz as visionOS decides (100 under 50 Hz lighting); the generation caps move with it (30 fps at 90 Hz, 33 at 100)",
                    Do = "nothing to choose: if a game flips between clean and dirty with the room's lighting, that is why" });

            // debug logging costs send delay
            if (debugOn)
                s.Add(new Suggestion { Sev = Sev.Hint, Knob = "Debug logging", Source = "your settings",
                    Title = "Debug logging is on",
                    Measured = "it cost 4.6 ms of send delay per frame on the reference PC (encode p95 4.3 -> 9.0 ms); the debug sessions this page reads exist because it was on",
                    Do = "leave it on only while investigating, then turn it off", Apply = turnDebugOff, ApplyText = "Turn debug logging off" });

            // per game, from the records
            foreach (var g in facts.Games)
            {
                string who = $"{g.Name} ({g.When:yyyy-MM-dd})";
                string src = $"from your records, last session of {who} on {g.Headset}";
                bool gen = g.Level.Length > 0 && g.Level != "off";
                string lvl = g.Level == "third" ? "1/3" : g.Level == "half" ? "1/2" : g.Level == "two_thirds" ? "2/3" : "off";
                if (!gen && g.Hz > 0 && g.Fps > 0 && g.Fps < 0.9 * g.Hz && g.RepTyp > 5)
                    s.Add(new Suggestion { Sev = Sev.Hint, Knob = "Frame generation", Source = src, Title = $"{g.Name}: frame generation would probably help",
                        Measured = $"it ran at {F(g.Fps, "0")} fps on a {F(g.Hz, "0")} Hz panel and the headset repeated {F(g.RepTyp)} % of its refreshes",
                        Do = "tick Frame generation in the streamer window and leave Recommended on; some games look better raw (a 56 fps game was preferred to 2/3), so it is a taste, not a rule" });
                if (gen && g.HostRep >= 5)
                    s.Add(new Suggestion { Sev = Sev.Hint, Knob = "Frame generation rate", Source = src, Title = $"{g.Name}: the game could not keep up with its rate ({lvl})",
                        Measured = $"the streamer re-sent {F(g.HostRep)} % of its outputs because the game had not delivered (frozen content the headset counts as shown); the headset itself repeated {F(g.RepTyp)} %",
                        Do = "choose the next lower rate (1/3 under 1/2): the Recommended tick does it after one more run" });
                else if (gen && g.RepTyp >= 5)
                    s.Add(new Suggestion { Sev = Sev.Hint, Knob = "Arrival / Wi-Fi", Source = src, Title = $"{g.Name}: the headset still repeated {F(g.RepTyp)} % of its refreshes at {lvl}",
                        Measured = $"typical window {F(g.RepTyp)} %, heavy parts {F(g.RepP90)} %, host repeats {F(g.HostRep)} %",
                        Do = "the game is keeping up, so look at the link and the arrival margin above" });
                if (gen && g.MvEnabled && g.MvFrames <= 0)
                    s.Add(new Suggestion { Sev = Sev.Hint, Knob = "DLSS motion vectors", Source = src, Title = $"{g.Name}: no DLSS motion vectors reached the streamer",
                        Measured = "the tick was on and generation was on, but the game gave none; generated frames used optical flow instead",
                        Do = "if the game uses DLSS, install the DLL from the Logs window (it says whether it is there); a game without DLSS has none to give" });
                else if (gen && g.MvEnabled && g.MvUsed > 0)
                    s.Add(new Suggestion { Sev = Sev.Info, Knob = "DLSS motion vectors", Source = src, Title = $"{g.Name}: the game's motion vectors are flowing",
                        Measured = $"{F(g.MvUsed, "0")} eye passes were synthesised from them", Do = "nothing to do: exact motion, less ghosting of small fast objects" });
            }
            return s.OrderByDescending(x => (int)x.Sev).ToList();
        }

        /// The suggestions as controls for the results page.
        public static Control Panel(List<Suggestion> list)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var head = Ui.Line("Suggestions", Ui.Big); head.ForeColor = Ui.Accent; p.Controls.Add(head);
            var note = Ui.Line("From docs\\PERFORMANCE_KNOBS.md, with this PC's measurements: the wizard's tests of this run and, when there are any, your logs and game records. Each says what it measured; nothing is changed unless you press a button.");
            note.ForeColor = Color.DimGray; p.Controls.Add(note);
            if (list.Count == 0) { p.Controls.Add(Ui.Line("Nothing to suggest: no measurement points to a knob worth changing.")); return p; }
            foreach (var x in list)
            {
                var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 6, 0, 2), Padding = new Padding(8, 4, 8, 4), MinimumSize = new Size(780, 0), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };
                var t = Ui.Line((x.Sev == Sev.Warn ? "⚠  " : x.Sev == Sev.Info ? "✓  " : "•  ") + x.Title, Ui.Bold);
                t.ForeColor = x.Sev == Sev.Warn ? Color.Firebrick : x.Sev == Sev.Info ? Color.SeaGreen : Color.Black;
                box.Controls.Add(t);
                var m = Ui.Line("Measured: " + x.Measured); m.ForeColor = Color.DimGray; box.Controls.Add(m);
                box.Controls.Add(Ui.Line("What to do: " + x.Do));
                var src = Ui.Line(x.Source + (x.Knob.Length > 0 ? "   ·   knob: " + x.Knob : ""), new Font("Segoe UI", 8.5f)); src.ForeColor = Color.DimGray; box.Controls.Add(src);
                if (x.Apply != null)
                {
                    var b = new Button { Text = x.ApplyText ?? "Apply", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
                    var ap = x.Apply;
                    b.Click += (s, e) => { ap(); b.Enabled = false; b.Text = "Done"; };
                    box.Controls.Add(b);
                }
                p.Controls.Add(box);
            }
            return p;
        }
    }
}
