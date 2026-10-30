#!/usr/bin/env python3
"""Automated harness entry point.

  python3 harness/run.py setup [--clone-only <dir>]        # one-time builder setup (pinned ALVR + VDXR), then build everything
  python3 harness/run.py list
  python3 harness/run.py build [host|client|shim|probe|package|vdxr|nvenc ...]; test-install     # (re)build binaries on the builder
  python3 harness/run.py run  <scenario> [<scenario> ...]
  python3 harness/run.py all                               # every scenario, non-zero exit if any fail
  python3 harness/run.py report [results_dir]              # re-evaluate saved artifacts (no builder needed)
  python3 harness/run.py bench-quality [quick|full] [--qp on|off] [--eye WxH] [--mbps N]   # benchmark phase 2 headless on the builder + sanity checks
  python3 harness/run.py test-zip                          # unzip the package into a fresh folder and run from it (logs, debug, config)

Exit code: 0 all PASS, 1 any FAIL, 2 any BLOCKED (infrastructure), 3 usage.
Results: harness/results/<UTC>_<scenario>/{result.json, analysis.json, *_report.json, client.hevc, ...}
"""
import json, os, subprocess, sys, time
from datetime import datetime, timezone
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import lib

SCEN = lib.ROOT / "harness" / "scenarios"


def load(name):
    p = SCEN / f"{name}.json"
    if not p.exists():
        p = SCEN / "optional" / f"{name}.json"      # cross-network scenarios, not part of `all`
    if not p.exists():
        p = SCEN / "pacing" / f"{name}.json"        # pacing lab (`run.py pacing`), not part of `all`
    if not p.exists():
        p = SCEN / "pacing" / "exp" / f"{name}.json"   # experiments (named explicitly)
    if not p.exists():
        raise SystemExit(f"unknown scenario {name}; available: {', '.join(x.stem for x in sorted(SCEN.glob('*.json')))}")
    sc = json.load(open(p))
    sc.setdefault("name", name)
    return sc


def print_checks(checks):
    for c in checks:
        print(f"    [{'PASS' if c['ok'] else 'FAIL'}] {c['name']}: {c['value']}  (expected {c['expected']})")


def verdict(checks):
    return "PASS" if checks and all(c["ok"] for c in checks) else "FAIL"


def run_one(name):
    sc = load(name)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    outdir = lib.RESULTS / f"{stamp}_{name}"
    print(f"== {name}: {sc.get('description', '')}")
    result = {"scenario": name, "started": stamp, "outdir": str(outdir)}
    try:
        steps = lib.run_scenario(sc, outdir, log=print)
        marker = sc.get("blocked_if_app_log")   # an environment the scenario cannot measure in (e.g. a screen the OS does not let a program duplicate) is BLOCKED, not a failed product
        if marker and marker in ((outdir / "app.log").read_text(errors="replace") if (outdir / "app.log").exists() else ""):
            raise lib.Blocked(f"the app reports {marker!r}: this machine cannot run the scenario (see the app.log)")
        a = lib.analyse(sc, outdir)
        checks = lib.evaluate(sc, a)
        result.update(status=verdict(checks), steps=steps, checks=checks)
        (outdir / "analysis.json").write_text(json.dumps(a, indent=1, default=str))
    except lib.Blocked as e:
        result.update(status="BLOCKED", reason=str(e), checks=[])
        outdir.mkdir(parents=True, exist_ok=True)
    (outdir / "result.json").write_text(json.dumps(result, indent=1))
    latest = lib.RESULTS / "latest"
    try:
        if latest.is_symlink() or latest.exists():
            latest.unlink()
        latest.symlink_to(outdir.name)
    except OSError:
        pass
    print_checks(result["checks"])
    print(f"   -> {result['status']}" + (f" ({result['reason']})" if result.get("reason") else "") + f"   [{outdir}]")
    return result["status"]


def build(what):
    # flavour: `--release` drops the per-frame telemetry (cargo feature `telemetry`, C++ AIRFRAMEXR_TELEMETRY); the default build keeps it
    release = "--release" in (what or [])
    what = [w for w in (what or []) if w != "--release"]
    lib.b_run("if not exist _tmp mkdir _tmp")
    flav = lib.CACHE / "flavour.txt"
    lib.CACHE.mkdir(parents=True, exist_ok=True)
    flav.write_text("release" if release else "debug")
    lib.b_put(flav, "_tmp/flavour.txt")
    print("flavour:", "release (no telemetry)" if release else "debug (telemetry)")
    stages = {"host": ("tools/host/Cargo.toml", "tools/host/src/main.rs", "_tmp/host", "tools/remote/stage8_host.ps1"),
              "client": ("tools/mock_client/Cargo.toml", "tools/mock_client/src/main.rs", "_tmp/mock_client", "tools/remote/stage7_mockclient.ps1")}
    if any(w in ("client", "host") for w in (what or ["client", "host"])):
        lib.b_run("if not exist _tmp mkdir _tmp")
        for pn in ("loopback-client.patch", "client-stats-log.patch", "client-caps-log.patch", "audio-trace.patch", "audio-process-loopback.patch", "client-audio-device.patch"):
            lib.b_put(lib.ROOT / "tools/patches" / pn, f"_tmp/{pn}")
        r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage9_patch.ps1"], cwd=lib.ROOT, capture_output=True)
        o = r.stdout.decode(errors="replace").replace("\x00", "")
        patched = r.returncode == 0 and "patch:" in o and "DOES NOT APPLY" not in o and "missing in _tmp" not in o
        print("patch:", "ok" if patched else "FAILED " + o[-300:])
        if not patched:
            return 2
    for w in what or ["client", "host"]:
        if w == "vdxr":      # VDXR with the small OVRShim patches (tools/remote/stage15_vdxr.ps1)
            r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage15_vdxr.ps1"], cwd=lib.ROOT, capture_output=True)
            o = r.stdout.decode(errors="replace").replace("\x00", "")
            print(f"build vdxr: {'ok' if 'RESULT: OK' in o else 'FAILED'}")
            if "RESULT: OK" not in o:
                print(o[-1200:])
                return 2
            continue
        if w == "shim":      # OVRShim: fork of VDXR's OVRNull (ovrshim/), built next to it in the builder's VDXR checkout
            lib.b_run("if not exist _tmp\\ovrshim mkdir _tmp\\ovrshim")
            for f in ("driver.cpp", "ipc.h", "ipc_win.h", "constantsbuffer.h", "layerconstants.h", "render_size.h", "ReprojectVS.hlsl", "ReprojectPS.hlsl", "LayerVS.hlsl", "LayerPS.hlsl", "LayerCubePS.hlsl"):
                lib.b_put(lib.ROOT / "ovrshim" / f, f"_tmp/ovrshim/{f}")
            r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage11_ovrshim.ps1"], cwd=lib.ROOT, capture_output=True)
            o = r.stdout.decode(errors="replace").replace("\x00", "")
            print(f"build shim: {'ok' if 'RESULT: OK' in o else 'FAILED'}")
            if "RESULT: OK" not in o:
                print(o[-1500:])
                return 2
            continue
        if w == "ballast":   # the stability harness' VRAM / RAM ballast (tools/stability/vram_ballast.cpp)
            lib.b_run("if not exist _tmp mkdir _tmp")
            lib.b_put(lib.ROOT / "tools/stability/vram_ballast.cpp", "_tmp/vram_ballast.cpp")
            r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage21_ballast.ps1"], cwd=lib.ROOT, capture_output=True)
            o = r.stdout.decode(errors="replace").replace("\x00", "")
            print(f"build ballast: {'ok' if 'RESULT: OK' in o else 'FAILED'}")
            if "RESULT: OK" not in o:
                print(o[-1500:])
                return 2
            continue
        if w == "bench":     # the Bench flavour's own binaries: perfcore.dll, perfreport.exe, perfprobe.exe, blackbox_recorder.exe and blackbox_analyzer.exe (tools/bench/, tools/gui/BenchRecorder.cs + PerfAnalyze.cs, docs/TELEMETRY_PLAN.md)
            lib.b_run("if not exist _tmp\\bench mkdir _tmp\\bench")
            for src in sorted((lib.ROOT / "tools/bench").glob("*.cs")):
                lib.b_put(src, f"_tmp/bench/{src.name}")
            lib.b_run("if not exist _tmp\\bench\\gui\\res mkdir _tmp\\bench\\gui\\res")
            for g in ("Common.cs", "PipelineModel.cs", "AirFrameXR.cs", "FindHeadset.cs", "AudioRoute.cs", "BenchRecorder.cs", "PerfAnalyze.cs", "BlackBoxBrand.cs", "BlackBoxChart.cs", "Stub.cs", "app.stub.manifest", "app.manifest", "app.exe.config"):
                lib.b_put(lib.ROOT / "tools/gui" / g, f"_tmp/bench/gui/{g}")
            for g in ("airframexr.ico", "blackbox-windows.ico", "blackbox_128.png", "logo_640.png", "chroma_passthrough.png"):
                lib.b_put(lib.ROOT / "tools/gui/res" / g, f"_tmp/bench/gui/res/{g}")
            r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage23_bench.ps1"], cwd=lib.ROOT, capture_output=True)
            o = r.stdout.decode(errors="replace").replace("\x00", "")
            print(f"build bench: {'ok' if 'RESULT: OK' in o else 'FAILED'}")
            if "RESULT: OK" not in o:
                print(o[-3000:])
                return 2
            continue
        if w == "gui":       # AirFrameXR.exe + configure.exe (C# WinForms, .NET Framework 4.8)
            lib.b_run("if not exist _tmp\\gui\\res mkdir _tmp\\gui\\res")
            for src in sorted((lib.ROOT / "tools/gui").glob("*.cs")) + [lib.ROOT / "tools/gui/app.manifest", lib.ROOT / "tools/gui/app.stub.manifest", lib.ROOT / "tools/gui/app.exe.config"]:
                lib.b_put(src, f"_tmp/gui/{src.name}")
            for f in ("airframexr.ico", "logo_640.png", "chroma_passthrough.png"):
                lib.b_put(lib.ROOT / "tools/gui/res" / f, f"_tmp/gui/res/{f}")
            r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage16_gui.ps1"], cwd=lib.ROOT, capture_output=True)
            o = r.stdout.decode(errors="replace").replace("\x00", "")
            print(f"build gui: {'ok' if 'RESULT: OK' in o else 'FAILED'}")
            if "RESULT: OK" not in o:
                print(o[-3000:])
                return 2
            continue
        if w == "package":   # out\\AirFrameXR: the portable folder a user unzips (+ out\\AirFrameXR-<version>.zip)
            lib.b_run("if not exist _tmp\\install mkdir _tmp\\install")
            for f in ("register_openxr_runtime.ps1", "session.default.json", "README.txt", "fovgrid.bat"):
                lib.b_put(lib.ROOT / "tools/install" / f, f"_tmp/install/{f}")
            # personal build (AIRFRAMEXR_PERSONAL=1, values from private/personal_config.json, never published): other session defaults, a ready
            # config\airframexr.json and a zip suffix. A generic build overwrites these with empty ones so a stale personal file cannot leak.
            pers = lib.ROOT / "private/personal_config.json"
            out_dir = lib.ROOT / "build/personal"
            out_dir.mkdir(parents=True, exist_ok=True)
            vis, ver = "{}", ""
            if os.environ.get("AIRFRAMEXR_PERSONAL") == "1":
                if not pers.exists():
                    print("package: AIRFRAMEXR_PERSONAL=1 but private/personal_config.json is missing (copy tools/install/personal_config.example.json)")
                    return 2
                pc = json.loads(pers.read_text())
                sess = json.loads((lib.ROOT / "tools/install/session.default.json").read_text())
                for k, v in pc.get("session", {}).items():
                    d = sess
                    ks = k.split(".")
                    for kk in ks[:-1]:
                        d = d[kk]
                    if ks[-1] not in d:
                        print(f"package: personal session key {k} does not exist in session.default.json")
                        return 2
                    d[ks[-1]] = v
                def merge(dst, src):
                    for k, v in src.items():
                        if isinstance(v, dict) and isinstance(dst.get(k), dict):
                            merge(dst[k], v)
                        else:
                            dst[k] = v
                merge(sess, pc.get("session_merge", {}))   # e.g. the paired headset (client_connections)
                (out_dir / "session.default.json").write_text(json.dumps(sess, indent=2))
                lib.b_put(out_dir / "session.default.json", "_tmp/install/session.default.json")
                vis = json.dumps(pc.get("airframexr", pc.get("visionalvr", {})), indent=2)   # "visionalvr": the key before the 1.0 rename (a personal_config.json written then still works)
                ver = pc.get("version_suffix", "-personal")
                print(f"package: personal build ({ver}) from {pers.name}")
            extra = []
            if os.environ.get("AIRFRAMEXR_PERSONAL") == "1":
                extra = json.loads(pers.read_text()).get("extra_files", [])   # build-PC paths copied next to AirFrameXR.exe (personal builds only)
            (out_dir / "personal_files.txt").write_text("\n".join(extra))
            lib.b_put(out_dir / "personal_files.txt", "_tmp/install/personal_files.txt")
            (out_dir / "airframexr.personal.json").write_text(vis)
            (out_dir / "version_suffix.txt").write_text(ver)
            lib.b_put(out_dir / "airframexr.personal.json", "_tmp/install/airframexr.personal.json")
            lib.b_put(out_dir / "version_suffix.txt", "_tmp/install/version_suffix.txt")
            import re
            m = re.search(r'pub const VERSION: &str = "([^"]+)"', (lib.ROOT / "tools/host/src/vision.rs").read_text())
            (out_dir / "version.txt").write_text(m.group(1) if m else "")
            lib.b_run("if not exist _tmp\\install\\profiles mkdir _tmp\\install\\profiles")
            for pf in sorted((lib.ROOT / "profiles").glob("*.json")):   # shipped as profiles\\*.json: the template and the Vision Pro profile (users add theirs to config\\profiles)
                lib.b_put(pf, f"_tmp/install/profiles/{pf.name}")
            lib.b_put(out_dir / "version.txt", "_tmp/install/version.txt")   # the zip is named after the host's VERSION (vision.rs)
            # licences shipped with the binaries: the notices, the project licence (once chosen) and the upstream texts
            lib.b_run("if not exist _tmp\\install\\licenses mkdir _tmp\\install\\licenses")
            lib.b_put(lib.ROOT / "THIRD_PARTY_NOTICES.md", "_tmp/install/THIRD_PARTY_NOTICES.md")
            if (lib.ROOT / "LICENSE").exists():
                lib.b_put(lib.ROOT / "LICENSE", "_tmp/install/LICENSE.txt")
            lic = sorted((lib.ROOT / "third_party/licenses").glob("*.txt"))
            for f in lic:
                lib.b_put(f, f"_tmp/install/licenses/{f.name}")
            # the package ships exactly these: the builder's staging folder keeps files the repo dropped (its guard refuses deletes; the
            # extractor's GPL text survived its removal that way, 0.6.1)
            (lib.CACHE / "licenses.txt").write_text("\n".join(f.name for f in lic))
            lib.b_put(lib.CACHE / "licenses.txt", "_tmp/install/licenses.txt")
            r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage13_package.ps1"], cwd=lib.ROOT, capture_output=True)
            o = r.stdout.decode(errors="replace").replace("\x00", "")
            print(f"build package: {'ok' if 'RESULT: OK' in o else 'FAILED'}")
            if "RESULT: OK" not in o:
                print(o[-1200:])
                return 2
            continue
        if w == "mirrordesktop":   # the desktop mirror, an OpenXR app: tools/mirrordesktop/*.cpp|h compiled on the builder
            lib.b_run("if not exist tools\\mirrordesktop mkdir tools\\mirrordesktop")
            for f in sorted((lib.ROOT / "tools/mirrordesktop").glob("*")):
                if f.suffix in (".cpp", ".h"):
                    lib.b_put(f, f"tools/mirrordesktop/{f.name}")
            r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage20_mirrordesktop.ps1"], cwd=lib.ROOT, capture_output=True)
            o = r.stdout.decode(errors="replace").replace("\x00", "")
            ok = "RESULT: OK" in o
            print(f"build mirrordesktop: {'ok' if ok else 'FAILED'}")
            if not ok:
                print(o[-1500:])
                return 2
            continue
        if w == "probe":     # the OpenXR test app (+ the benchmark scene renderer it can show, and the scene file)
            lib.b_put(lib.ROOT / "tools/probe/xr_probe.cpp", "_tmp/xr_probe.cpp")
            lib.b_put(lib.ROOT / "tools/probe/xr_modsim.cpp", "_tmp/xr_modsim.cpp")   # the mod-loop mimic (e2e_modsim_*)
            lib.b_run("if not exist nvenc\\hostlib mkdir nvenc\\hostlib")
            lib.b_put(lib.ROOT / "nvenc/hostlib/bench_scene.h", "nvenc/hostlib/bench_scene.h")
            lib.b_run("if not exist third_party\\stb mkdir third_party\\stb")
            lib.b_put(lib.ROOT / "third_party/stb/stb_image.h", "third_party/stb/stb_image.h")
            vab = lib.ROOT / "build/bench/littlest_tokyo.vab"
            if vab.exists():
                lib.b_run("if not exist bench mkdir bench")
                lib.b_put(vab, "bench/littlest_tokyo.vab")
                lib.b_put(lib.ROOT / "tools/bench_scene/sakura_breeze.mp3", "bench/sakura_breeze.mp3")   # the scene's music (xr_probe plays it)
            r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage12_probe_build.ps1"], cwd=lib.ROOT, capture_output=True)
            o = r.stdout.decode(errors="replace").replace("\x00", "")
            ok = "RESULT: OK" in o
            print(f"build probe: {'ok' if ok else 'FAILED'}")
            if not ok:
                print(o[-1200:])
                return 2
            continue
        if w == "nvenc":
            subprocess.run(["bash", str(lib.BUILDER), "put", "nvenc/.", "nvenc/"], cwd=lib.ROOT)
            r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage6_nvenc.ps1"], cwd=lib.ROOT)
            continue
        cargo, src, dst, ps = stages[w]
        lib.b_run(f"if not exist {dst.replace('/', chr(92))}\\src mkdir {dst.replace('/', chr(92))}\\src")
        lib.b_put(lib.ROOT / cargo, f"{dst}/Cargo.toml")
        lib.b_put(lib.ROOT / src, f"{dst}/src/main.rs")
        if w == "host":
            lib.b_put(lib.ROOT / "tools/host/build.rs", f"{dst}/build.rs")
            for rs in sorted((lib.ROOT / "tools/host/src").glob("*.rs")):   # every module, not just main.rs
                if rs.name != "main.rs":
                    lib.b_put(rs, f"{dst}/src/{rs.name}")
            subprocess.run(["bash", str(lib.BUILDER), "put", "nvenc/.", "nvenc/"], cwd=lib.ROOT, capture_output=True)
            lib.b_run("if not exist ovrshim mkdir ovrshim")
            subprocess.run(["bash", str(lib.BUILDER), "put", "ovrshim/.", "ovrshim/"], cwd=lib.ROOT, capture_output=True)
            # stb_image (bench.cpp decodes the benchmark scene's textures) and the scene itself (tools/bench_scene/build.sh)
            lib.b_run("if not exist third_party\\stb mkdir third_party\\stb")
            lib.b_put(lib.ROOT / "third_party/stb/stb_image.h", "third_party/stb/stb_image.h")
            vab = lib.ROOT / "build/bench/littlest_tokyo.vab"
            if vab.exists():
                lib.b_run("if not exist bench mkdir bench")
                lib.b_put(vab, "bench/littlest_tokyo.vab")
                lib.b_put(lib.ROOT / "tools/bench_scene/sakura_breeze.mp3", "bench/sakura_breeze.mp3")   # the scene's music (xr_probe plays it)
        r = subprocess.run(["bash", str(lib.BUILDER), "ps", ps], cwd=lib.ROOT, capture_output=True)
        out = r.stdout.decode(errors="replace").replace("\x00", "")
        print(f"build {w}: {'ok' if 'Finished' in out else 'FAILED'}")
        if "Finished" not in out:
            print(out[-1500:])
            return 2
    return 0


def bench_quality(plan, qp=None, eye=None, mbps=None):
    """Runs the quality sweep (tools/remote/stage17_bench_quality.ps1) and checks that it measured something real: the
    reconstructed frame was used and matches the input's colour conversion, quality orders sensibly, proposals exist."""
    rid = f"bq_{plan}" + (f"_qp{qp}" if qp else "") + (f"_{eye}" if eye else "") + (f"_{mbps}mbps" if mbps else "")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    outdir = lib.RESULTS / (f"{stamp}_bench_quality_{plan}" + (f"_qp{qp}" if qp else "") + (f"_{eye}" if eye else "") + (f"_{mbps}mbps" if mbps else ""))
    outdir.mkdir(parents=True, exist_ok=True)
    lib.b_run(f"if not exist runs\\{rid} mkdir runs\\{rid}")
    sess = lib.ROOT / "harness/fixtures/session.user.json"
    if eye:   # --eye WxH: the base eye size of the sweep (the fixture is the Vision Pro's 3552x3200; 2064x2208 = Meta Quest 3, lighter for a laptop GPU)
        w, h = (int(x) for x in eye.lower().split("x"))
        d = json.load(open(sess))
        for k in ("eye_resolution", "target_eye_resolution"):
            d["openvr_config"][k + "_width"], d["openvr_config"][k + "_height"] = w, h
        sess = lib.CACHE / f"session_bq_{w}x{h}.json"
        sess.parent.mkdir(parents=True, exist_ok=True)
        json.dump(d, open(sess, "w"), indent=1)
    lib.b_put(sess, f"runs/{rid}/session.json")
    frames = ["-Frames", "30", "-Warmup", "10", "-Every", "10"] if plan == "quick" else []
    r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage17_bench_quality.ps1", "-Label", rid, "-Plan", plan, *frames, *(["-QpMap", qp] if qp else []), *(["-Mbps", str(mbps)] if mbps else [])],
                       cwd=lib.ROOT, capture_output=True, timeout=900)
    o = r.stdout.decode(errors="replace").replace("\x00", "")
    for f in ("stdout.jsonl", "stderr.txt", "bq.json", "preview.png"):
        lib.b_get(f"runs/{rid}/{f}", outdir / f)
    events = []
    for line in (outdir / "stdout.jsonl").read_text(errors="replace").splitlines() if (outdir / "stdout.jsonl").exists() else []:
        try:
            events.append(json.loads(line))
        except ValueError:
            pass
    res = [e["data"] for e in events if e.get("event") == "bq_result"]
    summ = next((e["data"] for e in events if e.get("event") == "bq_summary"), None)
    ok = [x for x in res if x["ok"]]
    by = {x["label"]: x for x in ok}
    def psnr(label, k="weighted"):
        return by.get(label, {}).get("psnr", {}).get(k)
    p1, p5 = by.get("100% strong foveation P1 AQ on split auto"), by.get("100% strong foveation P5 AQ on split auto")
    checks = [
        ("ran", "exit 0" in o and summ is not None, o.strip().splitlines()[-2:] if o.strip() else "no output"),
        ("all_configs_ok_or_size_limit", all(x["ok"] or "INVALID_PARAM" in x["error"] for x in res), [x["label"] for x in res if not x["ok"]]),
        ("recon_used", bool(ok) and all(x["recon"] == 1 for x in ok), sorted({x["recon_format"] for x in ok})),
        ("recon_matches_input", bool(ok) and all(abs(x["y_fit"][0] - 1) < 0.05 and abs(x["y_fit"][1]) < 0.05 for x in ok), sorted({round(x["y_fit"][0], 3) for x in ok})),
        ("codec_psnr_plausible", bool(ok) and all(30 < x["psnr"]["codec"] < 70 for x in ok), [round(x["psnr"]["codec"], 1) for x in ok]),
        ("slower_preset_not_worse", p1 is not None and p5 is not None and p5["psnr"]["codec"] >= p1["psnr"]["codec"] - 0.2,
         p1 and p5 and (round(p1["psnr"]["codec"], 2), round(p5["psnr"]["codec"], 2))),
        ("encode_time_by_preset", p1 is not None and p5 is not None and p5["encode_ms"]["p50"] > p1["encode_ms"]["p50"],
         p1 and p5 and (round(p1["encode_ms"]["p50"], 1), round(p5["encode_ms"]["p50"], 1))),
    ]
    if plan != "quick":
        checks += [
            ("foveation_costs_periphery", (psnr("100% no foveation P1 AQ on split auto", "periphery") or 0) > (psnr("100% mild foveation P1 AQ on split auto", "periphery") or 99) > (psnr("100% strong foveation P1 AQ on split auto", "periphery") or 99),
             [psnr(f"100% {f} foveation P1 AQ on split auto", "periphery") for f in ("no", "mild", "strong")]),
            ("lower_resolution_costs_center", (psnr("100% mild foveation P1 AQ on split auto", "center") or 0) > (psnr("80% mild foveation P1 AQ on split auto", "center") or 99),
             [psnr(f"{s} mild foveation P1 AQ on split auto", "center") for s in ("100%", "80%")]),
            ("proposals", summ is not None and all(summ["proposals"].get(k) for k in ("lowest_latency", "recommended", "best_quality")),
             summ and {k: (summ["proposals"][k] or {}).get("label") for k in ("lowest_latency", "recommended", "best_quality")}),
            ("within_time", summ is not None and summ["seconds"] < 180, summ and round(summ["seconds"])),
        ]
    for name, passed, detail in checks:
        print(f"  {'PASS' if passed else 'FAIL'}  {name}: {detail}")
    verdict = "PASS" if all(c[1] for c in checks) else "FAIL"
    json.dump({"plan": plan, "verdict": verdict, "checks": [{"name": n, "pass": bool(p), "detail": str(d)} for n, p, d in checks]},
              open(outdir / "result.json", "w"), indent=1)
    print(f"bench-quality {plan}: {verdict} ({outdir})")
    return 0 if verdict == "PASS" else 1


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 3
    cmd = argv[1]
    if cmd == "list":
        for p in sorted(SCEN.glob("*.json")):
            print(f"{p.stem:28s} {json.load(open(p)).get('description', '')}")
        return 0
    if cmd == "build":
        return build(argv[2:])
    if cmd == "test-install":      # installer logic against a throwaway HKCU key (never touches HKLM or the real runtime)
        lib.b_put(lib.ROOT / "tools/install/Test-Install.ps1", "_tmp/install/Test-Install.ps1")
        rc, out = lib.b_run('powershell -NoProfile -ExecutionPolicy Bypass -File _tmp\\install\\Test-Install.ps1', timeout=300)
        for line in out.splitlines():
            if line.startswith(("CHECK", "RESULT")) or "rror" in line:
                print(line)
        return 0 if "RESULT: PASS" in out else 1
    if cmd == "setup":             # fresh builder: tools check, pinned sources (deps.lock.json), prerequisites, then every build
        lib.b_run("if not exist _tmp mkdir _tmp")
        lib.b_put(lib.ROOT / "deps.lock.json", "_tmp/deps.lock.json")
        extra = ["-CloneOnly", "-Src", argv[3]] if len(argv) > 3 and argv[2] == "--clone-only" else []
        r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/setup_builder.ps1", *extra], cwd=lib.ROOT, capture_output=True, timeout=7200)
        out = r.stdout.decode(errors="replace").replace("\x00", "")
        for line in out.splitlines():
            if line.startswith(("SETUP", "RESULT")):
                print(line.rstrip())
        if "RESULT: OK" not in out:
            return 2
        return 0 if extra else build(["nvenc", "vdxr", "client", "host", "shim", "probe", "mirrordesktop", "gui", "package"])
    if cmd == "test-zip":          # the shipped zip, unzipped into a fresh folder and run from there (no registry)
        r = subprocess.run(["bash", str(lib.BUILDER), "ps", "tools/remote/stage18_zip_test.ps1"], cwd=lib.ROOT, capture_output=True, timeout=600)
        out = r.stdout.decode(errors="replace").replace("\x00", "")
        for line in out.splitlines():
            if line.startswith(("CHECK", "RESULT", "folder")):
                print(line.rstrip())
        return 0 if "RESULT: PASS" in out else 1
    if cmd == "bench-quality":     # alvr_host --benchmark-quality on the builder (no headset), then checks on its results
        qp = argv[argv.index("--qp") + 1] if "--qp" in argv else None   # on | off: the foveal QP delta map (default: the engine's, on)
        eye = argv[argv.index("--eye") + 1] if "--eye" in argv else None
        mbps = argv[argv.index("--mbps") + 1] if "--mbps" in argv else None
        return bench_quality(argv[2] if len(argv) > 2 and not argv[2].startswith("--") else "quick", qp, eye, mbps)
    if cmd == "pacing":            # the pacing lab: every scenarios/pacing/*.json (or the named ones), then one KPI table
        names = argv[2:] or [p.stem for p in sorted((SCEN / "pacing").glob("*.json"))]
        rows = []
        for n in names:
            run_one(n)
            d = lib.RESULTS / "latest"
            a = json.load(open(d / "analysis.json")) if (d / "analysis.json").exists() else {}
            r = json.load(open(d / "result.json"))
            dk, pp, ak = a.get("display") or {}, a.get("pipeline") or {}, a.get("app_timing") or {}
            dw = dk.get("dwell_ms") or {}
            nan = float("nan")
            rows.append([n, r["status"], f'{(ak.get("fps") or 0):.0f}', f'{dk.get("repeat_pct", nan):.1f}', f'{dk.get("skip_pct", nan):.1f}',
                         f'{dw.get("p05", nan):.1f}', f'{dw.get("p50", nan):.1f}', f'{dw.get("p95", nan):.1f}',
                         str(pp.get("slots_empty")), str(pp.get("superseded")), str(pp.get("encoder_overruns")), str(pp.get("send_late")),
                         f'{((pp.get("shim") or {}).get("app_frame_ms") or 0):.1f}', str((pp.get("shim") or {}).get("releases_late")),
                         f'{(dk.get("pose_age_ms") or {}).get("p50", nan):.0f}/{(dk.get("pose_age_ms") or {}).get("p95", nan):.0f}/{(dk.get("pose_age_ms") or {}).get("max", nan):.0f}', str(dk.get("same_ts_shown"))])
        hdr = ["scenario", "status", "app_fps", "repeat%", "skip%", "dwell_p05", "dwell_p50", "dwell_p95", "empty", "superseded", "overruns", "send_late", "app_env_ms", "rel_late", "age_p50/p95/max", "same_ts"]
        w = [max(len(h), *(len(r[i]) for r in rows)) for i, h in enumerate(hdr)]
        print("\n== pacing lab (reference SteamVR + ALVR on the headset: repeat 3.4 %, dwell 9-11 ms)")
        print("  ".join(h.ljust(w[i]) for i, h in enumerate(hdr)))
        for r in rows:
            print("  ".join(c.ljust(w[i]) for i, c in enumerate(r)))
        return 0 if all(r[1] == "PASS" for r in rows) else 1
    if cmd == "report":
        d = Path(argv[2]) if len(argv) > 2 else lib.RESULTS / "latest"
        r = json.load(open(d / "result.json"))
        sc = load(r["scenario"])
        a = json.load(open(d / "analysis.json"))
        checks = lib.evaluate(sc, a)
        print_checks(checks)
        print(verdict(checks))
        return 0 if verdict(checks) == "PASS" else 1
    names = [p.stem for p in sorted(SCEN.glob("*.json"))] if cmd == "all" else argv[2:] if cmd == "run" else None
    if not names:
        print(__doc__)
        return 3
    statuses = {n: run_one(n) for n in names}
    print("\n== summary")
    for n, s in statuses.items():
        print(f"  {s:8s} {n}")
    vals = set(statuses.values())
    return 2 if "BLOCKED" in vals else 1 if "FAIL" in vals else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
