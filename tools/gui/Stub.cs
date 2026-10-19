// VisionALVR.exe in the package root: the one file a user ever double-clicks. It starts bin\launcher.exe and exits at once (no window of its own), so the
// root holds a single file and the launcher can have its own bin\launcher.exe.config. Arguments are passed on. Not a launcher itself: nothing to configure.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

static class Stub
{
    [STAThread]
    static int Main(string[] args)
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var exe = Path.Combine(dir, "bin", "launcher.exe");
        // started from inside the zip viewer (Windows unpacks one file to a temp folder): the rest of the package is not there
        if (!File.Exists(exe))
        {
            var inZip = dir.IndexOf(".zip", StringComparison.OrdinalIgnoreCase) >= 0 || dir.IndexOf("\\Temp1_", StringComparison.OrdinalIgnoreCase) >= 0;
            MessageBox.Show(inZip ? "Extract the whole zip first (right-click it, Extract All), then start VisionALVR.exe from the extracted folder."
                                  : "VisionALVR cannot start: bin\\launcher.exe is missing. Unzip the whole VisionALVR folder, keeping its folders, and start VisionALVR.exe from it.",
                "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        try { Process.Start(new ProcessStartInfo(exe, string.Join(" ", args.Select(a => a.Contains(" ") ? "\"" + a + "\"" : a))) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false }); return 0; }
        catch (Exception e) { MessageBox.Show("VisionALVR cannot start: " + e.Message, "VisionALVR", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
