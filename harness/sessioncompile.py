"""The streamer's own session compile (tools/gui SessionCompiler) for the harness: the scenarios start the host directly, so without this the session they run is the fixture
as it is, never what the launcher compiles at a real start (stream fit, bitrate sizing, foveation from the picture edges, pairing).

compile_session(golden, settings, views) lays out root\\{bin,config,profiles} on the builder, runs uitest.exe --session-compile-selftest (the real SessionCompiler, not a
reimplementation) and returns (compiled session dict, the log line it wrote, the airframexr.json it left: fov_render_quality is derived into it).

  golden    the golden master (a session.json-shaped dict, what the windows would have saved in config\\session.base.json)
  settings  config\\airframexr.json (headset.name "Apple Headset" makes the picture-edges foveation and the bitrate sizing run: they are Vision Pro only)
  views     the headset's declared tangents [outer, inner, upper, lower] (config\\airframexr_views.json), or None
"""
import json, sys, tempfile, time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib, uibuild

ROOT = lib.ROOT
_built = {"rid": None}


def _ensure_built():
    if _built["rid"] is None:
        rid = f"sc_{time.strftime('%H%M%S')}"
        errs = uibuild.build(rid)
        if errs:
            raise lib.Blocked("uitest.exe does not build, the session compile is not available: " + "; ".join(errs[:3]))
        _built["rid"] = rid
    return _built["rid"]


def compile_session(golden, settings, views=None):
    rid = _ensure_built()
    name = f"compile_{time.strftime('%H%M%S')}_{int(time.time() * 1000) % 100000}"
    rel = f"runs\\{rid}\\{name}"
    loc = Path(tempfile.mkdtemp(prefix="sc_"))
    (loc / "config").mkdir()
    (loc / "config" / "session.base.json").write_text(json.dumps(golden, indent=1))
    (loc / "config" / "airframexr.json").write_text(json.dumps(settings, indent=1))
    lib.b_run(f"mkdir {rel}\\config {rel}\\bin {rel}\\profiles")
    for f in ("session.base.json", "airframexr.json"):
        lib.b_put(loc / "config" / f, f"runs/{rid}/{name}/config/{f}")
    if views:
        (loc / "config" / "airframexr_views.json").write_text(json.dumps({"fov_tan": list(views) * 2}))
        lib.b_put(loc / "config" / "airframexr_views.json", f"runs/{rid}/{name}/config/airframexr_views.json")
    for p in sorted((ROOT / "profiles").glob("apple-vision-pro*.profile.json")):   # the rate map the foveation and the bitrate sizing read
        lib.b_put(p, f"runs/{rid}/{name}/profiles/{p.name}")
    lib.b_run(f"copy /y _tmp\\uitest\\run\\uitest.exe {rel}\\bin\\uitest.exe")
    lib.b_run(f"{rel}\\bin\\uitest.exe --session-compile-selftest --out {rel}")
    out = loc / "out_session.json"
    if not lib.b_get(f"runs/{rid}/{name}/config/session.json", out):
        raise lib.Blocked("the session compile wrote no session.json on the builder")
    line = ""
    if lib.b_get(f"runs/{rid}/{name}/session_compile.txt", loc / "line.txt"):
        line = (loc / "line.txt").read_text(errors="replace").strip()
    st_out = {}
    if lib.b_get(f"runs/{rid}/{name}/config/airframexr.json", loc / "st_out.json"):
        st_out = json.loads((loc / "st_out.json").read_text())
    return json.loads(out.read_text()), line, st_out
