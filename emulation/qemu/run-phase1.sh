#!/usr/bin/env bash
# Phase 1: boot the rv32 XIP kernel on QEMU -M virt.
#   pflash0 (0x20000000, 32M): xipImage   — kernel text executes in place here
#   pflash1 (0x22000000, 32M): cramfs     — XIP rootfs (userspace text in flash)
#   RAM: constrained to emulate the bao1x budget (default 16M; tighten with -m)
set -euo pipefail

TOP="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$TOP/build/phase1"
MEM="${MEM:-16M}"

# pflash drives must be exactly 32M
for img in kernel-flash rootfs-flash; do
    [ -f "$OUT/$img.img" ] || truncate -s 32M "$OUT/$img.img"
done
dd if="$OUT/kernel/arch/riscv/boot/xipImage" of="$OUT/kernel-flash.img" conv=notrunc status=none
# Without a rootfs image yet, boot still proves XIP up to the mount panic.
[ -f "$OUT/rootfs.cramfs" ] && \
    dd if="$OUT/rootfs.cramfs" of="$OUT/rootfs-flash.img" conv=notrunc status=none

# Kernel cmdline is built in (CONFIG_CMDLINE_FORCE): QEMU's -append needs -kernel.
exec qemu-system-riscv32 -M virt -smp 1 -m "$MEM" -nographic \
    -bios "$OUT/opensbi/platform/generic/firmware/fw_jump.bin" \
    -drive if=pflash,unit=0,format=raw,file="$OUT/kernel-flash.img" \
    -drive if=pflash,unit=1,format=raw,file="$OUT/rootfs-flash.img" \
    "$@"
