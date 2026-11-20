#!/bin/bash
# clang-cl syntax check (no codegen, no link) of VDXR source files (our fork, docs/UPSTREAM.md) against the MSVC + Windows SDK headers
# of tools/wsl/setup.sh. The NuGet packages (Detours, GameInput), the openvr hand-simulation sample, the build-generated commit.h and
# the compiled shader headers are stubbed: a check of our edits, not of those. Submodules used: fmt, OpenXR-MixedReality, Vulkan-SDK,
# cJSON (`git submodule update --init --depth 1 external/<name>` in the VDXR checkout), LibOVR and OpenXR-SDK (taken from
# external/VDXR when the checkout lacks them).
# Usage: tools/wsl/check_vdxr.sh [vdxr checkout, default external/VDXR] [file.cpp ...] (default: the files the fork changes)
R=$(cd "$(dirname "$0")/../.." && pwd); D=$R/external/_wslcheck
V=$(cd "${1:-$R/external/VDXR}" && pwd); shift
FILES=("$@"); [ ${#FILES[@]} -gt 0 ] || FILES=(frame.cpp d3d11_native.cpp session.cpp instance.cpp)
sub() { for b in "$V/external/$1" "$R/external/VDXR/external/$1"; do [ -n "$(ls -A "$b" 2>/dev/null)" ] && { echo "$b"; return; }; done; echo "MISSING-$1"; }
for m in fmt OpenXR-MixedReality Vulkan-SDK cJSON LibOVR OpenXR-SDK; do
  [[ "$(sub $m)" == MISSING-* ]] && { echo "submodule external/$m is empty: git -C $V submodule update --init --depth 1 external/$m"; exit 2; }
done
S=$(mktemp -d)
for n in FullScreenQuadVS ResolveMultisampledDepthPS AlphaBlendingCS UpscalingCS SharpeningCS; do
  printf '#pragma once\nconst unsigned char g_%s[] = {0, 1, 2, 3};\n' "$n" > "$S/$n.h"
done
cat > "$S/detours.h" <<'EOF'
#pragma once
inline LONG DetourTransactionBegin() { return 0; }
inline LONG DetourUpdateThread(HANDLE) { return 0; }
inline LONG DetourAttach(PVOID*, PVOID) { return 0; }
inline LONG DetourDetach(PVOID*, PVOID) { return 0; }
inline LONG DetourTransactionCommit() { return 0; }
inline BOOL DetourRestoreAfterWith() { return TRUE; }
EOF
printf '#pragma once\n' > "$S/GameInput.h"
printf '#pragma once\nclass MyHandSimulation { public: template <class... A> void ComputeSkeletonTransforms(A&&...) {} };\n' > "$S/hand_simulation.h"
printf 'const char* RuntimeCommitHash = "check";\n' > "$S/commit.h"
W="/imsvc $D/win/msvc /imsvc $D/win/ucrt /imsvc $D/win/um /imsvc $D/win/shared /imsvc $D/win/winrt /imsvc $D/win/cppwinrt"
MR=$(sub OpenXR-MixedReality); OS=$(sub OpenXR-SDK); LO=$(sub LibOVR)
I="/I$S /I$V/virtualdesktop-openxr /I$OS/include /I$OS/src/common /I$MR/shared /I$MR/shared/XrUtility /I$MR/shared/SampleShared
   /I$LO/Include /I$LO/Include/Extras /I$LO/Shim /I$(sub Vulkan-SDK)/include /I$V/external/OpenGL /I$(sub fmt)/include /I$(sub cJSON) /I$D/wil/include"
rc=0
for f in "${FILES[@]}"; do
  echo "== $f"
  out=$(clang-cl /std:c++17 /EHsc -fsyntax-only /DRUNTIME_NAMESPACE=virtualdesktop_openxr /DSTANDALONE_RUNTIME /DNDEBUG /D_WINDOWS /D_USRDLL \
        /D_CRT_SECURE_NO_WARNINGS /DNOMINMAX /DUNICODE /D_UNICODE -Wno-everything $I $W "$V/virtualdesktop-openxr/$f" 2>&1) || rc=1
  grep -E "error" <<<"$out" | head -15
done
rm -rf "$S"
exit $rc
