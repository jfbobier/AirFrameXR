// perfprobe.exe: command line over perfcore.dll (lab, support, and the first test of the library without any window).
//   perfprobe.exe capture <session dir> <L0|L1> <seconds> [process names, comma separated] [settings dir]
using System;
using AirFrameXR.Perf;

static class PerfProbeCli
{
    static int Main(string[] a)
    {
        if (a.Length < 4 || a[0] != "capture") { Console.WriteLine("usage: perfprobe capture <session dir> <L0|L1> <seconds> [processes] [settings dir]"); return 2; }
        var o = new ProbeOptions
        {
            Dir = a[1], Level = a[2] == "L0" ? Level.L0 : Level.L1,
            ProcessNames = a.Length > 4 && a[4].Length > 0 ? a[4].Split(',') : new string[0],
            SettingsDir = a.Length > 5 ? a[5] : null
        };
        double seconds = double.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture);
        using (var p = new Probe(o))
        {
            p.Start();
            Console.WriteLine("session " + p.SessionId + " in " + o.Dir + " (" + o.Level + ", " + seconds + " s)");
            System.Threading.Thread.Sleep((int)(seconds * 1000));
        }
        Console.WriteLine("done");
        return 0;
    }
}
