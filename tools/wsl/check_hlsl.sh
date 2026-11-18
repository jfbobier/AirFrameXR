#!/bin/bash
# Compiles OVRShim's shaders with the Windows SDK's fxc.exe through WSL interop (shader model 5.0, /D HLSL, as OVRNull.vcxproj does)
# and prints each constant buffer's size and the offset of its last field from fxc's reflection, which driver.cpp's static_asserts
# pin. Needs the Windows SDK on the Windows side and WSL interop on. Usage: tools/wsl/check_hlsl.sh
R=$(cd "$(dirname "$0")/../.." && pwd)
FXC=$(ls "/mnt/c/Program Files (x86)/Windows Kits/10/bin/"*/x64/fxc.exe 2>/dev/null | sort -V | tail -1)
[ -x "$FXC" ] || { echo "fxc.exe not found under C:\\Program Files (x86)\\Windows Kits\\10\\bin (or WSL interop is off)"; exit 2; }
T=$(mktemp -d)
cp "$R"/ovrshim/*.hlsl "$R"/ovrshim/constantsbuffer.h "$R"/ovrshim/layerconstants.h "$R/external/VDXR/OVRNull/utils.hlsli" "$T/"
cd "$T" || exit 2
rc=0
for f in *.hlsl; do
  case "$f" in *VS.hlsl) prof=vs_5_0 ;; *) prof=ps_5_0 ;; esac
  out=$("$FXC" /nologo /T "$prof" /E main /D HLSL /Fh "${f%.hlsl}.h" /Vn "k_${f%.hlsl}" "$f" 2>&1 | tr -d '\r')
  if [ -s "${f%.hlsl}.h" ] && ! grep -qi "error" <<<"$out"; then
    echo "== $f ($prof): ok; $(grep -E '^//   } cb;' "${f%.hlsl}.h" | sed 's#^//   ##; s#  *# #g')"
  else
    echo "== $f ($prof): FAILED"; echo "$out" | grep -i "error" | head -8; rc=1
  fi
done
rm -rf "$T"
exit $rc
