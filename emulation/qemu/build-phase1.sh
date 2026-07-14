#!/usr/bin/env bash
# Phase 1: build the rv32 XIP kernel (v6.14) + OpenSBI fw_jump for QEMU -M virt.
# Artifacts land in build/phase1/.
set -euo pipefail

TOP="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="$TOP/sources"
OUT="$TOP/build/phase1"
CROSS=riscv64-linux-gnu-
JOBS=$(nproc)
KERNEL_TAG=v6.14

mkdir -p "$OUT"

# --- Kernel tree is shallow-cloned at the last-good-XIP tag ----------------
ACTUAL=$(git -C "$SRC/linux" describe --tags --always)
if [ "$ACTUAL" != "$KERNEL_TAG" ]; then
    echo "WARNING: sources/linux is at $ACTUAL, expected $KERNEL_TAG" >&2
fi

# --- Kernel: tinyconfig + phase-1 fragment --------------------------------
cd "$SRC/linux"
make ARCH=riscv CROSS_COMPILE=$CROSS O="$OUT/kernel" tinyconfig
# ARCH must be exported or merge_config validates the fragment against x86
ARCH=riscv CROSS_COMPILE=$CROSS ./scripts/kconfig/merge_config.sh -O "$OUT/kernel" \
    "$OUT/kernel/.config" "$TOP/linux/configs/phase1-qemu-virt-xip.config"
make ARCH=riscv CROSS_COMPILE=$CROSS O="$OUT/kernel" olddefconfig
make ARCH=riscv CROSS_COMPILE=$CROSS O="$OUT/kernel" -j"$JOBS" xipImage
ls -la "$OUT/kernel/arch/riscv/boot/xipImage"

# --- OpenSBI fw_jump: M-mode shim for QEMU only ---------------------------
# Jumps straight to the XIP kernel in pflash0 (0x20000000).
if [ ! -f "$OUT/opensbi/platform/generic/firmware/fw_jump.bin" ]; then
    make -C "$SRC/opensbi" O="$OUT/opensbi" \
        CROSS_COMPILE=$CROSS PLATFORM=generic PLATFORM_RISCV_XLEN=32 \
        FW_JUMP_ADDR=0x20000000 FW_JUMP_FDT_ADDR=0x80180000 -j"$JOBS"
fi
ls -la "$OUT/opensbi/platform/generic/firmware/fw_jump.bin"

echo "=== phase 1 build complete ==="
