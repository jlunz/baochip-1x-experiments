#!/usr/bin/env bash
# Phase 3: build the bao1x XIP kernel, DTB, and bao1x-sbi shim for Renode.
# Rootfs: reuses the phase-1 XIP cramfs (emulation/qemu/build-rootfs.sh).
set -euo pipefail

TOP="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="$TOP/sources"
OUT="$TOP/build/phase3"
CROSS=riscv64-linux-gnu-
JOBS=$(nproc)

mkdir -p "$OUT"

# --- Kernel: tinyconfig + bao1x fragment -----------------------------------
cd "$SRC/linux"
make ARCH=riscv CROSS_COMPILE=$CROSS O="$OUT/kernel" tinyconfig
ARCH=riscv CROSS_COMPILE=$CROSS ./scripts/kconfig/merge_config.sh -O "$OUT/kernel" \
    "$OUT/kernel/.config" "$TOP/linux/configs/bao1x-xip.config"
make ARCH=riscv CROSS_COMPILE=$CROSS O="$OUT/kernel" olddefconfig
make ARCH=riscv CROSS_COMPILE=$CROSS O="$OUT/kernel" -j"$JOBS" xipImage
ls -la "$OUT/kernel/arch/riscv/boot/xipImage"

# --- DTB --------------------------------------------------------------------
# bao1x.dtsi is canonical in the kernel tree (patch "riscv: add Baochip
# bao1x SoC and Dabao board support"); the Renode board dts includes it.
dtc -I dts -O dtb -i "$SRC/linux/arch/riscv/boot/dts/baochip" \
    -o "$OUT/bao1x-renode.dtb" "$TOP/linux/dts/baochip/bao1x-renode.dts"

# --- bao1x-sbi shim (embeds the DTB) ----------------------------------------
make -C "$TOP/firmware/bao1x-sbi" O="$OUT/sbi" BOARD=renode DTB="$OUT/bao1x-renode.dtb"

# --- Rootfs -----------------------------------------------------------------
if [ ! -f "$TOP/build/phase1/rootfs.cramfs" ]; then
    echo "rootfs missing; run emulation/qemu/build-rootfs.sh first" >&2
    exit 1
fi

echo "=== phase 3 build complete ==="
echo "  shim:   $OUT/sbi/bao1x-sbi.elf   @ 0x60060000"
echo "  kernel: $OUT/kernel/arch/riscv/boot/xipImage @ 0x60070000"
echo "  rootfs: $TOP/build/phase1/rootfs.cramfs @ 0x60220000"
