// OpenComposite (OpenVR -> OpenXR, GPLv3, https://gitlab.com/znixian/OpenOVR) as the launcher uses it: its two vrclient DLLs ship in the package, unmodified, in
// opencomposite\bin\ with the licence texts and the corresponding source (opencomposite\SOURCE.txt); we do not link to them. The launcher only points the
// system-wide OpenVR runtime path (%LOCALAPPDATA%\openvr\openvrpaths.vrpath) at that folder, or back at SteamVR; the output goes to the active OpenXR runtime, which is
// AirFrameXR. Reversible: the previous runtime entry is kept and restored.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AirFrameXR
{
    public static class OpenComposite
    {
        /// The OpenVR runtime folder we own (bin\vrclient.dll, bin\vrclient_x64.dll). Tests point these at a temporary folder.
        public static string Root = Path.Combine(Paths.Dir, "opencomposite");
        public static string VrPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
        static string Previous => Path.Combine(Paths.Config, "opencomposite_previous.json");
        static string Dll32 => Path.Combine(Root, "bin", "vrclient.dll");
        static string Dll64 => Path.Combine(Root, "bin", "vrclient_x64.dll");

        /// The DLLs of the package are there (a developer folder, or a package without them, has no OpenComposite to switch to).
        public static bool Installed => File.Exists(Dll32) && File.Exists(Dll64) && new FileInfo(Dll32).Length > 0 && new FileInfo(Dll64).Length > 0;

        /// The OpenVR runtime folders registered on this PC (SteamVR's, or ours); empty when there is none.
        public static string[] Runtimes()
        {
            var r = Json.Load(VrPath);
            return r.TryGetValue("runtime", out var o) && o is object[] a ? a.Select(x => Convert.ToString(x)).ToArray() : new string[0];
        }

        static string Norm(string p) => (p ?? "").Trim().TrimEnd('\\', '/').Replace('/', '\\').ToLowerInvariant();
        public static bool Active => Runtimes().Any(p => Norm(p) == Norm(Root));

        /// OpenVR games now load OpenComposite. The runtime entry that was there is kept (once) for Disable.
        public static string Enable()
        {
            try
            {
                if (!Installed) return "This folder has no OpenComposite (opencomposite\\bin\\ is missing).";
                var root = Json.Load(VrPath);
                var current = Runtimes();
                if (!current.Any(p => Norm(p) == Norm(Root)))
                {
                    Json.Save(Previous, new Dictionary<string, object> { ["runtime"] = current.Cast<object>().ToArray() });
                    if (File.Exists(VrPath) && !File.Exists(VrPath + ".airframexr.bak")) File.Copy(VrPath, VrPath + ".airframexr.bak");
                }
                root["runtime"] = new object[] { Root };
                if (!root.ContainsKey("version")) root["version"] = 1;
                if (!root.ContainsKey("jsonid")) root["jsonid"] = "vrpathreg";
                Json.Save(VrPath, root);
                Log.Write("INFO", "OpenVR runtime set to OpenComposite (" + Root + ")");
                return null;
            }
            catch (Exception e) { Log.Write("WARN", "OpenComposite switch failed: " + e.Message); return "Could not change the OpenVR runtime: " + e.Message; }
        }

        /// OpenVR games load what was registered before (SteamVR). Without a record, SteamVR's usual folder; when there is none either, a message.
        public static string Disable()
        {
            try
            {
                var prev = Json.Load(Previous);
                string[] back = prev.TryGetValue("runtime", out var o) && o is object[] a ? a.Select(x => Convert.ToString(x)).Where(p => Norm(p) != Norm(Root)).ToArray() : new string[0];
                if (back.Length == 0)
                {
                    var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "SteamVR");
                    if (Directory.Exists(steam)) back = new[] { steam };
                }
                if (back.Length == 0) return "No earlier OpenVR runtime to go back to: start SteamVR once from Steam and it registers itself.";
                var root = Json.Load(VrPath);
                root["runtime"] = back.Cast<object>().ToArray();
                Json.Save(VrPath, root);
                Log.Write("INFO", "OpenVR runtime set back to " + string.Join("; ", back));
                return null;
            }
            catch (Exception e) { Log.Write("WARN", "OpenComposite switch back failed: " + e.Message); return "Could not change the OpenVR runtime: " + e.Message; }
        }
    }
}
