#!/usr/bin/env bash
# Phase 4: build the flash-ready, dev-signed Linux UF2 for the Dabao board.
#
# Payload layout in RRAM (boot1 jumps to 0x60060000 in M-mode):
#   0x60060000  768-byte signature block (jal x0,+768 / ed25519ph, dev key)
#   0x60060300  bao1x-sbi shim (BOARD=dabao, embeds dabao.dtb)
#   0x60070000  XIP kernel (v6.14 + linux/patches, bao1x-xip.config)
#   0x60220000  XIP cramfs rootfs
#   0x603DA000  end of usable RRAM (vendor layout)
#
# WARNING (user-approved): the first boot of a developer-signed image
# irreversibly burns DEVELOPER_MODE on the chip and erases factory secrets.
set -euo pipefail

TOP="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$TOP/build/dabao"
XOUS="$TOP/sources/xous-core"
SIGN="$XOUS/target/release/xous-sign-image"

KERNEL="$TOP/build/phase3/kernel/arch/riscv/boot/xipImage"
ROOTFS="$TOP/build/phase1/rootfs.cramfs"

SHIM_OFF=0            # source.bin starts at flash 0x60060300
KERNEL_OFF=$((0x60070000 - 0x60060300))
ROOTFS_OFF=$((0x60220000 - 0x60060300))
# rootfs slot ends where the writable "data" partition begins (see the DT)
LIMIT=$((0x60360000 - 0x60060300))

mkdir -p "$OUT"

[ -f "$KERNEL" ] || { echo "kernel missing; run emulation/renode/build-phase3.sh" >&2; exit 1; }
[ -f "$ROOTFS" ] || { echo "rootfs missing; run emulation/qemu/build-rootfs.sh" >&2; exit 1; }
[ -x "$SIGN" ] || { echo "xous-sign-image missing; cargo build --release -p xous-tools --bin xous-sign-image (in sources/xous-core)" >&2; exit 1; }

# --- Board DTB + shim -------------------------------------------------------
# dabao.dts lives in the kernel tree and uses cpp-style includes.
DTSDIR="$TOP/sources/linux/arch/riscv/boot/dts/baochip"
cpp -nostdinc -I "$DTSDIR" -undef -x assembler-with-cpp "$DTSDIR/dabao.dts" \
    | dtc -I dts -O dtb -i "$DTSDIR" -o "$OUT/dabao.dtb"
make -C "$TOP/firmware/bao1x-sbi" O="$OUT/sbi" BOARD=dabao DTB="$OUT/dabao.dtb"

# --- Assemble the flat payload (base = 0x60060300) --------------------------
# Slot boundaries are not sector-aligned (the 768-byte sig block shifts
# everything), so build strictly by truncate-to-offset + append.
SRC="$OUT/source.bin"
cp "$OUT/sbi/bao1x-sbi.bin" "$SRC"
SHIM_SIZE=$(stat -c%s "$SRC")
[ "$SHIM_SIZE" -le $KERNEL_OFF ] || { echo "shim overflows its slot" >&2; exit 1; }
truncate -s $KERNEL_OFF "$SRC"
cat "$KERNEL" >> "$SRC"
TOTAL=$(stat -c%s "$SRC")
[ "$TOTAL" -le $ROOTFS_OFF ] || { echo "kernel overflows its slot" >&2; exit 1; }
truncate -s $ROOTFS_OFF "$SRC"
cat "$ROOTFS" >> "$SRC"
TOTAL=$(stat -c%s "$SRC")
[ "$TOTAL" -le $LIMIT ] || { echo "image exceeds usable RRAM" >&2; exit 1; }

# Paranoia: verify each slot before signing.
python3 - "$SRC" "$OUT/sbi/bao1x-sbi.bin" "$KERNEL" "$ROOTFS" $KERNEL_OFF $ROOTFS_OFF <<'EOF'
import sys
src, shim, kernel, rootfs = (open(f, 'rb').read() for f in sys.argv[1:5])
koff, roff = int(sys.argv[5]), int(sys.argv[6])
assert src[:len(shim)] == shim, "shim slot corrupt"
assert src[koff:koff+len(kernel)] == kernel, "kernel slot corrupt"
assert src[roff:roff+len(rootfs)] == rootfs, "rootfs slot corrupt"
print("slot check OK: shim %d, kernel %d @0x%x, rootfs %d @0x%x"
      % (len(shim), len(kernel), koff, len(rootfs), roff))
EOF

# --- Sign (developer key; sig block prepended -> image at 0x60060000) -------
# anti-rollback comes from signing/anti-rollback.hjson via the xous-core cwd.
(cd "$XOUS" && "$SIGN" --bao1x --with-jump \
    --loader-image "$SRC" \
    --loader-key devkey/dev.key \
    --loader-output "$OUT/flash.bin" \
    --sig-length 768 \
    --function-code baremetal \
    --min-xous-ver v0.9.0 \
    --git-describe v0.1.0-0-g0000000)   # shallow clone: git describe has no tags

# --- UF2 ---------------------------------------------------------------------
# xous-sign-image already emits flash.uf2; regenerate independently with
# mkuf2.py and require both to agree (cross-checks tool and layout).
python3 "$TOP/tools/mkuf2.py" "$OUT/flash.bin" "$OUT/dabao-linux.uf2" --base 0x60060000
cmp "$OUT/flash.uf2" "$OUT/dabao-linux.uf2" || { echo "UF2 mismatch vs vendor tool!" >&2; exit 1; }

echo "=== dabao image complete ==="
ls -la "$OUT/flash.bin" "$OUT/dabao-linux.uf2"
echo "flash: copy dabao-linux.uf2 to the BAOCHIP mass-storage volume (boot1),"
echo "       or stream with sources/xous-core/bao1x-boot/uf2send.py over the boot1 console."