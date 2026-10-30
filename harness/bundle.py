#!/usr/bin/env python3
"""The bundle: the stock (release) package with the Black Box (the telemetry package) nested in it as blackbox\\ (blackbox_perf.exe in the root starts it), as a folder and a zip on the builder, public and personal.

    python3 harness/bundle.py            # builds both flavours (about 20 minutes), stashes them, assembles out\\bundle\\<stamp>-public and -personal, fetches both zips to build/bundle/

Order (the builder's outputs are shared between flavours, so each flavour is stashed before the next build): release build + public package -> stash; the same release build + personal
package -> stash; debug (telemetry) build + package -> stash; assemble. Nothing is deleted anywhere: every run uses new names. The last build left on the builder is the debug one, as the lab expects.
"""
import os, re, subprocess, sys, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

RUN = [sys.executable, str(lib.ROOT / "harness" / "run.py")]
VERSION = re.search(r'pub const VERSION: &str = "([^"]+)"', (lib.ROOT / "tools/host/src/vision.rs").read_text()).group(1)


def run(args, env=None):
    e = dict(os.environ); e.update(env or {})
    p = subprocess.run(RUN + args, capture_output=True, text=True, env=e)
    print(" ".join(args), "->", p.stdout.strip().splitlines()[-1] if p.stdout.strip() else "(no output)")
    return p


def ps_text(script_text, name, *a):
    f = lib.CACHE / name
    f.parent.mkdir(parents=True, exist_ok=True)
    f.write_text(script_text)
    r = subprocess.run(["bash", str(lib.BUILDER), "ps", str(f), *a], capture_output=True)
    return r.stdout.decode(errors="replace").replace("\x00", "")


def stash(name):
    out = ps_text('$r = $env:AIRFRAMEXR_ROOT\nrobocopy "$r\\out\\dist\\AirFrameXR" "$r\\out\\stash\\%s" /E /NFL /NDL /NJH /NJS /NP | Out-Null\nWrite-Host "stashed %s"\n' % (name, name), "stash.ps1")
    print(out.strip())


def need(p, what):
    if f"build package: ok" not in p.stdout:
        sys.exit(f"{what} failed:\n" + p.stdout[-800:])


TAG = sys.argv[sys.argv.index("--tag") + 1] if "--tag" in sys.argv else ""   # appended to the zip names (a candidate must not share its name with a published asset)
stamp = time.strftime("%Y%m%d-%H%M%S")
p = run(["build", "host", "shim", "gui", "bench", "package", "--release"]); need(p, "release build")
stash(f"public_{stamp}")
p = run(["build", "package", "--release"], {"AIRFRAMEXR_PERSONAL": "1"}); need(p, "personal package")
stash(f"personal_{stamp}")
p = run(["build", "host", "shim", "gui", "bench", "package"]); need(p, "debug build")
stash(f"debug_{stamp}")
dest = lib.ROOT / "build" / "bundle"
dest.mkdir(parents=True, exist_ok=True)
for kind, suffix in (("public", ""), ("personal", "-personal")):
    name = f"{stamp}-{kind}"
    zipname = f"AirFrameXR-{VERSION}-bundle{suffix}{'-' + TAG if TAG else ''}.zip"
    out = ps_text(open(lib.ROOT / "tools/remote/stage24_bundle.ps1").read(), "stage24_bundle.ps1", f"{kind}_{stamp}", f"debug_{stamp}", name, zipname)
    print(out.strip()[-400:])
    if "RESULT: OK" not in out:
        sys.exit("bundle failed")
    lib.b_get(f"out/bundle/{name}/{zipname}", dest / zipname)
    print("fetched", dest / zipname, round((dest / zipname).stat().st_size / 1048576, 1), "MB")
