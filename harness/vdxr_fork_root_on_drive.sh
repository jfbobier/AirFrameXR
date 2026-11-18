#!/bin/bash
# One-time, before harness/vdxr_fork_preflight.sh: put this worktree's builder root on another drive of the builder (the 3080 Ti's 1 TB
# USB drive, D:) behind a junction %USERPROFILE%\<root> -> <drive>:\<root>. Every tool keeps using %USERPROFILE%\<root> unchanged
# (builder.sh, and the absolute paths lib.py writes into run scripts and XR_RUNTIME_JSON).
# Creates <drive>:\<root> and the junction; refuses when %USERPROFILE%\<root> already exists as a folder (it never deletes anything),
# and does nothing when the junction is already there. Then checks that scp writes through the junction.
# Usage: [AIRFRAMEXR_PROFILE=5090] harness/vdxr_fork_root_on_drive.sh [drive letter, default D]
cd "$(dirname "$0")/.." || exit 2
drive="${1:-D}"
[[ "$drive" =~ ^[A-Za-z]$ ]] || { echo "drive letter expected, got: $drive"; exit 2; }
root=$(python3 -c "import sys; sys.path.insert(0, 'harness'); import lib; print(lib.ROOTNAME)") || exit 2
[[ "$root" =~ ^[A-Za-z0-9._-]+$ && "$root" != "." ]] || { echo "unexpected root name: $root"; exit 2; }
echo "== builder profile: ${AIRFRAMEXR_PROFILE:-default}: %USERPROFILE%\\$root -> ${drive}:\\$root"

# Root '.' so builder.sh's "mkdir the root if missing" does not create the C: folder this script replaces by a junction.
ps=$(sed -e "s/__ROOT__/$root/g" -e "s/__DRIVE__/$drive/g" <<'EOF' | tr '\n' ' '
$r = Join-Path $env:USERPROFILE '__ROOT__'; $t = '__DRIVE__:\__ROOT__';
if (-not (Test-Path '__DRIVE__:\')) { 'no __DRIVE__: drive'; exit 1 }
$v = Get-Volume -DriveLetter __DRIVE__; '__DRIVE__: ' + $v.FileSystem + ', ' + $v.DriveType + ', free GB ' + [math]::Round($v.SizeRemaining / 1GB, 1);
$i = Get-Item $r -Force -ErrorAction SilentlyContinue;
if ($i -and $i.LinkType -eq 'Junction') { if (($i.Target -join ' ') -like ('*' + $t)) { 'already a junction to ' + $t; exit 0 } else { 'refused: junction to ' + ($i.Target -join ' '); exit 1 } }
if ($i -and (Get-ChildItem $r -Force | Select-Object -First 1)) { 'refused: ' + $r + ' is a folder with files in it'; exit 1 }
if ($i) { 'EXISTS-EMPTY'; exit 1 }
New-Item -ItemType Directory -Force $t | Out-Null; New-Item -ItemType Junction -Path $r -Target $t | Out-Null;
'junction ' + $r + ' -> ' + $t
EOF
)
out=$(AIRFRAMEXR_BUILDER_ROOT=. bash tools/builder.sh run "powershell -NoProfile -Command \"$ps\"" 2>&1 | tr -d '\r\000')
echo "$out"
if grep -q EXISTS-EMPTY <<<"$out"; then
  echo "%USERPROFILE%\\$root exists and is empty (an earlier builder.sh call created it). Remove it (rd without /s only removes an empty"
  echo "folder), then run this again:  bash tools/builder.sh --allow-destructive run \"rd %USERPROFILE%\\$root\""
  exit 1
fi
grep -qE '^(junction|already a junction)' <<<"$out" || exit 1

# scp writes through the junction (Win32-OpenSSH's sftp server must follow it, or builder.sh put / ps would land nowhere)
probe="$(mktemp)"; echo "root on ${drive}:" > "$probe"
bash tools/builder.sh run "if not exist _tmp mkdir _tmp" >/dev/null 2>&1
bash tools/builder.sh put "$probe" _tmp/root_on_drive.txt; rm -f "$probe"
bash tools/builder.sh run "if exist ${drive}:\\$root\\_tmp\\root_on_drive.txt (echo scp through the junction: ok) else (echo scp through the junction: FAILED)" 2>&1 | tr -d '\r\000' | tail -1
