// perfreport.exe: the report of a Bench session folder from the command line (lab fixtures, support, CI). Reads the folder, writes report.md next to its inputs.
//   perfreport.exe <session dir> [--warmup <seconds>] [--stdout]
using System;
using System.IO;
using AirFrameXR.Perf;

static class PerfReportCli
{
    static int Main(string[] a)
    {
        if (a.Length < 1) { Console.WriteLine("usage: perfreport <session dir> [--warmup <seconds>] [--stdout]"); return 2; }
        double warm = 10;
        for (int i = 1; i < a.Length - 1; i++) if (a[i] == "--warmup") warm = double.Parse(a[i + 1], System.Globalization.CultureInfo.InvariantCulture);
        if (!Directory.Exists(a[0])) { Console.WriteLine("no such folder: " + a[0]); return 2; }
        var r = Report.Build(a[0], warm);
        if (Array.IndexOf(a, "--stdout") >= 0) Console.Write(r.Markdown);
        else File.WriteAllText(Path.Combine(a[0], "report.md"), r.Markdown, new System.Text.UTF8Encoding(false));
        Console.WriteLine(r.VerdictStatus + ": " + r.Verdict);
        foreach (var f in r.Findings) Console.WriteLine("  " + f.Id + " " + f.Status + " " + f.Title);
        return r.VerdictStatus == "PROBLEM" ? 1 : 0;
    }
}
