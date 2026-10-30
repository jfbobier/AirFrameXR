// AirFrameXR.exe in the package root: the one file a user ever double-clicks. It starts bin\launcher.exe and exits at once (no window of its own), so the
// root holds a single file and the launcher can have its own bin\launcher.exe.config. Arguments are passed on. Not a launcher itself: nothing to configure.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

static class Stub
{
#if BLACKBOX
    // blackbox_perf.exe in the package root (the bundle): starts blackbox\bin\launcher.exe, the Black Box's own two-card launcher
    const string Rel = @"blackbox\bin\launcher.exe", Title = "AirFrameXR Black Box", Self = "blackbox_perf.exe", Folder = "blackbox";
#else
    const string Rel = @"bin\launcher.exe", Title = "AirFrameXR", Self = "AirFrameXR.exe", Folder = "bin";
#endif
    [STAThread]
    static int Main(string[] args)
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var exe = Path.Combine(dir, Rel);
        // started from inside the zip viewer (Windows unpacks one file to a temp folder): the rest of the package is not there
        if (!File.Exists(exe))
        {
            var inZip = dir.IndexOf(".zip", StringComparison.OrdinalIgnoreCase) >= 0 || dir.IndexOf("\\Temp1_", StringComparison.OrdinalIgnoreCase) >= 0;
            MessageBox.Show(inZip ? "Extract the whole zip first (right-click it, Extract All), then start " + Self + " from the extracted folder."
                                  : Title + " cannot start: " + Rel + " is missing. Unzip the whole AirFrameXR folder, keeping its folders, and start " + Self + " from it.",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        try { Process.Start(new ProcessStartInfo(exe, string.Join(" ", args.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a))) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false }); return 0; }
        catch (Exception e) { MessageBox.Show(Title + " cannot start: " + e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
