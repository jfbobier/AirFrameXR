#!/bin/bash
# What upstream VirtualDesktop-OpenXR (mbucchia) has that our fork does not (docs/UPSTREAM.md, "Taking upstream changes into the VDXR fork"):
# the commits on upstream main since the last merge (deps.lock.json builder_sources.VDXR.upstream_hash) and upstream's open pull requests.
# Read only (a fetch into external/VDXR). Usage: tools/vdxr_upstream_check.sh
R=$(cd "$(dirname "$0")/.." && pwd); V=$R/external/VDXR
[ -d "$V/.git" ] || [ -f "$V/.git" ] || { echo "no VDXR checkout in $V"; exit 2; }
up=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['builder_sources']['VDXR']['upstream_hash'])" "$R/deps.lock.json") || exit 2
git -C "$V" remote get-url upstream >/dev/null 2>&1 || git -C "$V" remote add upstream https://github.com/mbucchia/VirtualDesktop-OpenXR.git
git -C "$V" fetch -q upstream main || { echo "fetch failed"; exit 2; }
n=$(git -C "$V" rev-list --count "$up"..upstream/main)
echo "== upstream main: $n commit(s) since the last merge (${up:0:7})"
git -C "$V" log --oneline --date=short --format="%h %ad %s" "$up"..upstream/main
echo "== upstream's open pull requests (not merged upstream: a cherry-pick with -x, see docs/UPSTREAM.md)"
gh pr list --repo mbucchia/VirtualDesktop-OpenXR --state open --limit 30 2>/dev/null || echo "(gh not available: https://github.com/mbucchia/VirtualDesktop-OpenXR/pulls)"
