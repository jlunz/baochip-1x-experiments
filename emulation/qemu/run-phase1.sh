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
dd if="$OUT/kernel/arch/riscv/boot/xipImage" of="$OUT/kernel-flash.img" bs=4096 seek=112 conv=notrunc status=none  # entry at +0x70000, like bao1x
# Without a rootfs image yet, boot still proves XIP up to the mount panic.
[ -f "$OUT/rootfs.cramfs" ] && \
    dd if="$OUT/rootfs.cramfs" of="$OUT/rootfs-flash.img" conv=notrunc status=none

# Patch the generated DTB: the kernel must not CFI-probe the flash it
# executes from (probe commands switch pflash out of read-array mode and the
# next instruction fetch returns garbage). mtd-rom keeps it XIP-mappable.
qemu-system-riscv32 -M virt,dumpdtb="$OUT/virt.dtb" -smp 1 -m "$MEM" -nographic \
    -bios "$OUT/opensbi/platform/generic/firmware/fw_jump.bin" \
    -drive if=pflash,unit=0,format=raw,file="$OUT/kernel-flash.img" \
    -drive if=pflash,unit=1,format=raw,file="$OUT/rootfs-flash.img" >/dev/null 2>&1
dtc -I dtb -O dts "$OUT/virt.dtb" -o "$OUT/virt.dts" 2>/dev/null
# Also split the two banks into separate nodes: one DT node with two reg
# ranges makes physmap concatenate them, and mtd_concat cannot mtd_point()
# (no direct mapping -> no cramfs XIP).
python3 - "$OUT/virt.dts" <<'EOF'
import re, sys
p = sys.argv[1]
s = open(p).read()
old = re.search(r'\tflash@20000000 \{.*?\n\t\};\n', s, re.S).group(0)
new = ('\tflash@20000000 {\n\t\tbank-width = <0x04>;\n'
       '\t\treg = <0x00 0x20000000 0x00 0x2000000>;\n'
       '\t\tcompatible = "mtd-rom";\n\t};\n\n'
       '\tflash@22000000 {\n\t\tbank-width = <0x04>;\n'
       '\t\treg = <0x00 0x22000000 0x00 0x2000000>;\n'
       '\t\tcompatible = "mtd-rom";\n\t};\n')
open(p, 'w').write(s.replace(old, new))
EOF
dtc -I dts -O dtb "$OUT/virt.dts" -o "$OUT/virt-patched.dtb" 2>/dev/null

# Kernel cmdline is built in (CONFIG_CMDLINE_FORCE): QEMU's -append needs -kernel.
exec qemu-system-riscv32 -M virt -smp 1 -m "$MEM" -nographic \
    -bios "$OUT/opensbi/platform/generic/firmware/fw_jump.bin" \
    -dtb "$OUT/virt-patched.dtb" \
    -drive if=pflash,unit=0,format=raw,file="$OUT/kernel-flash.img" \
    -drive if=pflash,unit=1,format=raw,file="$OUT/rootfs-flash.img" \
    "$@"
