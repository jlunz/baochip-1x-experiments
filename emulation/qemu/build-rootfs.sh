#!/usr/bin/env bash
# Phase 1 rootfs: buildroot (rv32 musl static busybox) -> XIP cramfs image.
# Long: first run builds the cross toolchain (~30-60 min). Re-runs are fast.
set -euo pipefail

TOP="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="$TOP/sources"
OUT="$TOP/build/phase1"
BROUT="$TOP/build/buildroot-phase1"
mkdir -p "$OUT" "$BROUT"

# --- Buildroot: rootfs tarball --------------------------------------------
make -C "$SRC/buildroot" O="$BROUT" defconfig \
    BR2_DEFCONFIG="$TOP/buildroot/configs/phase1_qemu_rv32_defconfig"
make -C "$SRC/buildroot" O="$BROUT" -j"$(nproc)"

# --- cramfs-tools: mkcramfs with XIP direct pointers -----------------------
if [ ! -x "$OUT/mkcramfs" ]; then
    make -C "$SRC/cramfs-tools" mkcramfs
    cp "$SRC/cramfs-tools/mkcramfs" "$OUT/mkcramfs"
fi

# --- Assemble XIP cramfs ----------------------------------------------------
ROOTDIR="$BROUT/rootfs-xip"
rm -rf "$ROOTDIR" && mkdir -p "$ROOTDIR"
tar -xf "$BROUT/images/rootfs.tar" -C "$ROOTDIR"
# Mark executables for XIP (uncompressed, page-aligned direct pointers):
# cramfs XIPs files with the sticky bit set (S_ISVTX) when using -X? — see
# cramfs-tools README; adjusted after first inspection. Baseline: busybox.
"$OUT/mkcramfs" -X "$ROOTDIR" "$OUT/rootfs.cramfs" || \
"$OUT/mkcramfs" "$ROOTDIR" "$OUT/rootfs.cramfs"
ls -la "$OUT/rootfs.cramfs"
