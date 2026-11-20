#!/bin/bash
# Before the VDXR fork lab (docs/PLAN_VDXR_FORK_LAB.md): is the builder up, how much disk is free, where this root lives and what it
# holds, and whether the fork commits the lab builds are on GitHub (the builder fetches them from there). Changes nothing on the
# builder: the first checks run from %USERPROFILE% (root '.'), and the root's contents are only looked at when the root exists, so
# a missing root is not created on C: before harness/vdxr_fork_root_on_drive.sh can put it on D:.
# Usage: [AIRFRAMEXR_PROFILE=5090] harness/vdxr_fork_preflight.sh
cd "$(dirname "$0")/.." || exit 2
B="bash tools/builder.sh"
H="env AIRFRAMEXR_BUILDER_ROOT=. bash tools/builder.sh"   # runs in %USERPROFILE%, creates nothing
root=$(python3 -c "import sys; sys.path.insert(0, 'harness'); import lib; print(lib.ROOTNAME)" 2>/dev/null)
echo "== builder profile: ${AIRFRAMEXR_PROFILE:-default}, root %USERPROFILE%\\$root"
$H run "echo reachable" 2>&1 | tr -d '\r\000' | tail -1 | grep -q reachable || { echo "builder unreachable"; exit 2; }
echo "reachable"
$H run "powershell -NoProfile -Command \"'C: free GB ' + [math]::Round((Get-PSDrive C).Free / 1GB, 1); if (Test-Path D:\\) { 'D: ' + (Get-Volume -DriveLetter D).FileSystem + ', free GB ' + [math]::Round((Get-PSDrive D).Free / 1GB, 1) } else { 'no D: drive' }\"" 2>&1 | tr -d '\r\000'
where=$($H run "powershell -NoProfile -Command \"\$i = Get-Item (Join-Path \$env:USERPROFILE '$root') -Force -ErrorAction SilentlyContinue; if (-not \$i) { 'root does not exist yet' } elseif (\$i.LinkType -eq 'Junction') { 'root is a junction to ' + (\$i.Target -join ' ') } else { 'root is a folder on C:' }\"" 2>&1 | tr -d '\r\000' | tail -1)
echo "$where"
if [[ "$where" == "root does not exist yet" ]]; then
  echo "next: harness/vdxr_fork_root_on_drive.sh (root on D:), or any builder.sh call creates it on C:; then python3 harness/run.py setup"
else
  $B run "if exist src\\VDXR\\.git (git -C src\\VDXR rev-parse HEAD) else (echo no VDXR checkout in this root: python3 harness/run.py setup)" 2>&1 | tr -d '\r\000' | tail -1
  $B run "if exist out\\vdxr-shim\\LibOVRRT64_1.dll (echo shim built) else (echo no shim build in this root yet)" 2>&1 | tr -d '\r\000' | tail -1
  $B run "if exist out\\vdxr-shim-base\\LibOVRRT64_1.dll (echo base runtime present: out\\vdxr-shim-base) else (echo no base runtime yet: plan step B0)" 2>&1 | tr -d '\r\000' | tail -1
fi
echo "== fork branches on GitHub (the builder fetches the pinned commits from there)"
git ls-remote https://github.com/jfbobier/VirtualDesktop-OpenXR.git airframexr airframexr-slices
echo "pinned: $(python3 -c "import json; print(json.load(open('deps.lock.json'))['builder_sources']['VDXR']['full_hash'])")"
