#!/bin/bash
# Before the VDXR fork lab (docs/PLAN_VDXR_FORK_LAB.md): is the builder up, how much disk is free, what this root holds, and whether
# the fork commits the lab builds are on GitHub (the builder fetches them from there). Changes nothing on the builder except
# creating the (empty) root folder, as every builder.sh call does.
# Usage: [AIRFRAMEXR_PROFILE=5090] harness/vdxr_fork_preflight.sh
cd "$(dirname "$0")/.." || exit 2
B="bash tools/builder.sh"
root=$(python3 -c "import sys; sys.path.insert(0, 'harness'); import lib; print(lib.ROOTNAME)" 2>/dev/null)
echo "== builder profile: ${AIRFRAMEXR_PROFILE:-default}, root %USERPROFILE%\\$root"
$B run "echo reachable" 2>&1 | tr -d '\r\000' | tail -1 | grep -q reachable || { echo "builder unreachable"; exit 2; }
echo "reachable"
$B run "powershell -NoProfile -Command \"'C: free GB ' + [math]::Round((Get-PSDrive C).Free / 1GB, 1); if (Test-Path D:\\) { 'D: free GB ' + [math]::Round((Get-PSDrive D).Free / 1GB, 1) } else { 'no D: drive' }\"" 2>&1 | tr -d '\r\000'
$B run "if exist src\\VDXR\\.git (git -C src\\VDXR rev-parse HEAD) else (echo no VDXR checkout in this root: python3 harness/run.py setup)" 2>&1 | tr -d '\r\000' | tail -1
$B run "if exist out\\vdxr-shim\\LibOVRRT64_1.dll (echo shim built) else (echo no shim build in this root yet)" 2>&1 | tr -d '\r\000' | tail -1
$B run "if exist out\\vdxr-shim-base\\LibOVRRT64_1.dll (echo base runtime present: out\\vdxr-shim-base) else (echo no base runtime yet: plan step B0)" 2>&1 | tr -d '\r\000' | tail -1
echo "== fork branches on GitHub (the builder fetches the pinned commits from there)"
git ls-remote https://github.com/jfbobier/VirtualDesktop-OpenXR.git airframexr airframexr-slices
echo "pinned: $(python3 -c "import json; print(json.load(open('deps.lock.json'))['builder_sources']['VDXR']['full_hash'])")"
