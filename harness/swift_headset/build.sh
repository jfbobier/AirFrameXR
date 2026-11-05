#!/bin/bash
# Builds the Swift headset (harness/swift_headset/README.md): the visionOS client's own Swift files from the client fork at a git ref,
# compiled for this OS against Apple-framework stand-ins, linked with the REAL client_core of that ref (the fork's ALVR submodule,
# Rust), built here with the harness's loopback patch (ALVR_BIND_IP) and the no-PipeWire patch (Linux only).
#
#   harness/swift_headset/build.sh [--ref airframexr-client] [--fork external/alvr-visionos-pr]
#   -> harness/cache/swift_headset/swift_headset (+ libalvr_client_core.so next to it)
#
# Needs: a Swift 6.x toolchain (SWIFTC, default swiftc-linux or swiftc), cargo, and the openvr headers of external/ALVR-v20.14.1
# (the session crate's build script reads them). Nothing in the fork checkout is changed (files are read with git show).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
REF=airframexr-client
FORK="$ROOT/external/alvr-visionos-pr"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --ref) REF="$2"; shift 2 ;;
        --fork) FORK="$(cd "$2" && pwd)"; shift 2 ;;
        *) echo "unknown option $1"; exit 2 ;;
    esac
done
SWIFTC="${SWIFTC:-$(command -v swiftc-linux || command -v swiftc)}"
CACHE="$ROOT/harness/cache/swift_headset"
SRC="$CACHE/src"
MOD="$CACHE/modules"
mkdir -p "$SRC/ALVRClient/Extensions" "$SRC/CAlvr" "$MOD"

# 1. the client's files at the ref
COMMIT=$(git -C "$FORK" rev-parse "$REF")
for f in ALVRClient/EventHandler.swift ALVRClient/FFR.swift ALVRClient/Settings.swift ALVRClient/Extensions/Codable.swift \
         ALVRClient/Extensions/Drawable.swift tools/protocol-harness/Shims/AppleShims.swift tools/protocol-harness/CAlvr/alvr_client_core.h \
         tools/protocol-harness/CAlvr/module.modulemap; do
    mkdir -p "$SRC/$(dirname "$f")"
    git -C "$FORK" show "$COMMIT:$f" > "$SRC/$f"
done
cp "$SRC/tools/protocol-harness/CAlvr/"* "$SRC/CAlvr/"
echo "client sources: $REF ($COMMIT)"

# 2. client_core of that ref (the submodule commit), built once per commit
CORE=$(git -C "$FORK" ls-tree "$COMMIT" ALVR | awk '{print $3}')
CORE_DIR="$CACHE/alvr-$CORE"
LIB="$CACHE/target/release/libalvr_client_core.so"
if [[ ! -f "$CORE_DIR/.built" ]]; then
    rm -rf "$CORE_DIR"; mkdir -p "$CORE_DIR"
    git -C "$FORK/ALVR" archive "$CORE" | tar -x -C "$CORE_DIR"
    (cd "$CORE_DIR" && patch -p1 -s < "$ROOT/tools/patches/loopback-client.patch" && patch -p1 -s < "$HERE/patches/no-pipewire.patch")
    rmdir "$CORE_DIR/openvr" 2>/dev/null || true
    cp -r "$ROOT/external/ALVR-v20.14.1/openvr" "$CORE_DIR/openvr"
    (cd "$CORE_DIR" && CARGO_TARGET_DIR="$CACHE/target" cargo build --release -p alvr_client_core) > "$CACHE/core_build.log" 2>&1 \
        || { tail -30 "$CACHE/core_build.log"; exit 1; }
    touch "$CORE_DIR/.built"
fi
# the C header from that client_core (the fork's own command, build_and_repack.sh:29), not the protocol harness's copy
(cd "$CORE_DIR/alvr/client_core" && "${CBINDGEN:-$(command -v cbindgen || echo ~/.cargo/bin/cbindgen)}" --config cbindgen.toml \
    --crate alvr_client_core --output "$SRC/CAlvr/alvr_client_core.h") 2>> "$CACHE/core_build.log"
echo "client_core: ALVR $CORE ($LIB)"

# 3. Apple stand-ins: one module (the protocol harness's AppleShims + ours) and a re-exporting module per framework name
COMMON=(-swift-version 5 -Onone -I "$SRC/CAlvr" -I "$MOD")
"$SWIFTC" "${COMMON[@]}" -parse-as-library -emit-module -emit-module-path "$MOD/AppleShims.swiftmodule" -module-name AppleShims \
    -emit-object -wmo -o "$MOD/AppleShims.o" "$SRC/tools/protocol-harness/Shims/AppleShims.swift" "$HERE/Shims/HeadsetShims.swift"
OBJS=("$MOD/AppleShims.o")
for fw in Metal VideoToolbox Combine AVKit Network UIKit QuartzCore simd CompositorServices Spatial; do
    echo "@_exported import AppleShims" > "$MOD/$fw.swift"
    "$SWIFTC" "${COMMON[@]}" -parse-as-library -emit-module -emit-module-path "$MOD/$fw.swiftmodule" -module-name "$fw" \
        -emit-object -o "$MOD/$fw.o" "$MOD/$fw.swift"
    OBJS+=("$MOD/$fw.o")
done

# 4. the headset: the client's files unchanged + the neighbours + the render-loop mirror
CLIENT=("$SRC/ALVRClient/EventHandler.swift" "$SRC/ALVRClient/FFR.swift" "$SRC/ALVRClient/Settings.swift"
        "$SRC/ALVRClient/Extensions/Codable.swift" "$SRC/ALVRClient/Extensions/Drawable.swift")
# the app sees the C API in every file through its bridging header: -import-module does the same here
"$SWIFTC" "${COMMON[@]}" -Xfrontend -import-module -Xfrontend AppleShims -g -module-name SwiftHeadset "${CLIENT[@]}" "$HERE/Headset/Neighbours.swift" "$HERE/Headset/main.swift" \
    "${OBJS[@]}" -L "$CACHE/target/release" -lalvr_client_core -Xlinker -rpath -Xlinker "$CACHE/target/release" -o "$CACHE/swift_headset"
echo "$REF $COMMIT core $CORE" > "$CACHE/swift_headset.version"
echo "built $CACHE/swift_headset ($REF)"
