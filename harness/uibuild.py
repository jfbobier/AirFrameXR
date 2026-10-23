"""Builds tools/gui (uitest.exe) on the builder and stages the fixtures next to it: shared by ui_matrix.py, streamer_events.py and export_logs.py.
build(rid) returns the compiler's error lines (empty = built); the exe is _tmp\\uitest\\run\\uitest.exe, run with cwd of the builder's clone."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

ROOT = lib.ROOT
SOURCES = "Common.cs Configure.cs SettingsPanel.cs Reference.cs TestRunner.cs Wizard.cs Profiles.cs Verdict.cs Advice.cs FindHeadset.cs AudioRoute.cs VisionALVR.cs Launcher.cs Performance.cs UiTest.cs"


def build(rid):
    for d in ("_tmp\\uitest\\src\\res", f"runs\\{rid}", "_tmp\\uitest\\run"):
        lib.b_run(f"if not exist {d} mkdir {d}")
    for f in sorted((ROOT / "tools/gui").glob("*.cs")) + [ROOT / "tools/gui/app.manifest"]:
        lib.b_put(f, f"_tmp/uitest/src/{f.name}")
    for f in ("visionalvr.ico", "logo_640.png", "chroma_passthrough.png"):
        lib.b_put(ROOT / "tools/gui/res" / f, f"_tmp/uitest/src/res/{f}")
    lib.b_put(ROOT / "harness/fixtures/ui_matrix/config", "_tmp/uitest/run/")
    lib.b_put(ROOT / "harness/fixtures/ui_matrix/DLSSMotionVectors", "_tmp/uitest/run/")   # the packaged DLL (a placeholder): the Logs window then offers Install
    lib.b_put(ROOT / "harness/fixtures/ui_matrix/logs", "_tmp/uitest/run/")   # a debug session of a noisy laptop: the results page then shows a full list of suggestions
    lib.b_put(ROOT / "tools/gui/app.exe.config", "_tmp/uitest/run/uitest.exe.config")   # native per-monitor DPI mode, like the shipped exes
    rc, home = lib.b_run("echo %USERPROFILE%")
    base = f"{home.strip().splitlines()[-1].strip()}\\{lib.ROOTNAME}"
    src = f"{base}\\_tmp\\uitest\\src"
    files = " ".join(f'"{src}\\{n}"' for n in SOURCES.split())
    fw = "C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319"
    refs = " ".join(f"/r:{fw}\\{r}.dll" for r in ("System", "System.Core", "System.Drawing", "System.Windows.Forms", "System.Web.Extensions", "System.IO.Compression", "System.IO.Compression.FileSystem"))
    csc = "C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\Roslyn\\csc.exe"
    bat = ("@echo off\r\n"
           f"\"{csc}\" /nologo /noconfig /target:winexe /platform:x64 /langversion:latest /nowarn:1701,1702 /main:VisionALVR.UiTest {refs} "
           f"\"/win32manifest:{src}\\app.manifest\" \"/resource:{src}\\res\\logo_640.png,logo_640.png\" \"/resource:{src}\\res\\chroma_passthrough.png,chroma_passthrough.png\" "
           f"\"/out:{base}\\_tmp\\uitest\\run\\uitest.exe\" {files}\r\n")
    lib.CACHE.mkdir(parents=True, exist_ok=True)
    (lib.CACHE / "uitest_build.bat").write_text(bat)
    lib.b_put(lib.CACHE / "uitest_build.bat", "_tmp/uitest/build.bat")
    rc, out = lib.b_run("cmd /c _tmp\\uitest\\build.bat", timeout=300)
    return [l for l in out.splitlines() if "error" in l.lower()]
