// fov_profile.exe: the console version of the calibration (calibration bundle, docs/FOV_CALIBRATION.md): the sweep CSVs of a folder into one importable profile file.
//   fov_profile.exe [folder | file.csv ...]   [--match "Quest 3"]   [--coverage 99]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AirFrameXR;

static class FovProfileTool
{
    static int Main(string[] args)
    {
        try
        {
            string match = null; double coverage = 99; var inputs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--match" && i + 1 < args.Length) match = args[++i];
                else if (args[i] == "--coverage" && i + 1 < args.Length) coverage = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else inputs.Add(args[i]);
            }
            if (inputs.Count == 0) inputs.Add(AppDomain.CurrentDomain.BaseDirectory);
            var files = inputs.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "fov_sweep_*.csv") : new[] { p }).Distinct().OrderBy(f => f).ToList();
            if (files.Count == 0) { Console.WriteLine("  No fov_sweep_*.csv found next to this program. Run run_sweep.bat first."); return 1; }

            var r = FovFit.Fit(files, coverage, match, null, out var problem);
            if (r == null) { Console.WriteLine("\n  Problem: " + problem); return 1; }
            Console.WriteLine($"\n  {r.Runs} run file(s), {r.Steps} usable steps ({r.Headset}, {r.Author}).");
            foreach (var w in r.Warnings) Console.WriteLine("  Warning: " + w);
            var outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0])), r.FileName);
            File.WriteAllText(outPath, r.Json);
            var t = r.Rectangle;
            Console.WriteLine($"\n  Field of view for the games (outer / inner / upper / lower): {t.Outer:0.00} / {t.Inner:0.00} / {t.Upper:0.00} / {t.Lower:0.00}");
            Console.WriteLine($"  Covers {100 * r.Coverage:0.0} % of what you see; {100 * r.SavedVsBox:0.0} % less picture than the window's bounding box.");
            Console.WriteLine($"\n  Profile written:\n    {outPath}\n");
            Console.WriteLine("  To use it: AirFrameXR > Advanced picture and FOV settings > \"Import a calibration file...\".");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("\n  Something went wrong: " + e.Message); return 1; }
    }
}
