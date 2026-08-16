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
#
# SHIM_ONLY=1 builds the shim so it parks in a console heartbeat instead of
# entering Linux. That is rungs 2-3 of docs/08-recovery-and-risk-ladder.md:
# it exercises the whole flash + handover path without ever running the kernel.
set -euo pipefail

TOP="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$TOP/build/dabao"
XOUS="$TOP/sources/xous-core"
SIGN="$XOUS/target/release/xous-sign-image"
# Normalise early: the Makefile tests `ifeq ($(SHIM_ONLY),1)`, so anything that
# treats a bare "0" as on would disagree with it and silently build the wrong
# image. Only 1 turns it on.
case "${SHIM_ONLY:-}" in
1) SHIM_ONLY=1 ;;
"" | 0) SHIM_ONLY= ;;
*) echo "SHIM_ONLY must be 1 or 0 (got '$SHIM_ONLY')" >&2; exit 2 ;;
esac

KERNEL="$TOP/build/phase3/kernel/arch/riscv/boot/xipImage"
ROOTFS="$TOP/build/phase1/rootfs.cramfs"

SHIM_OFF=0            # source.bin starts at flash 0x60060300
KERNEL_OFF=$((0x60070000 - 0x60060300))
ROOTFS_OFF=$((0x60220000 - 0x60060300))
# rootfs slot ends where the writable "data" partition begins (see the DT)
LIMIT=$((0x60360000 - 0x60060300))

mkdir -p "$OUT"

[ -x "$SIGN" ] || { echo "xous-sign-image missing; cargo build --release -p xous-tools --bin xous-sign-image (in sources/xous-core)" >&2; exit 1; }

if [ -z "$SHIM_ONLY" ]; then
    [ -f "$KERNEL" ] || { echo "kernel missing; run emulation/renode/build-phase3.sh" >&2; exit 1; }
    [ -f "$ROOTFS" ] || { echo "rootfs missing; run emulation/qemu/build-rootfs.sh" >&2; exit 1; }
    # sources/linux is gitignored, so anything fixed only there is lost when
    # the working tree goes away. That has already happened once: three fixes
    # made after the first silicon session existed only in sources/linux and
    # had to be reconstructed. Refuse to build a kernel-bearing image from a
    # tree that no longer matches the committed series.
    #
    # Deliberately not gated on the shim-only path: that image contains no
    # kernel, and it is the recovery rung you reach for on a fresh machine
    # where sources/linux may not even have the series applied yet. Coupling
    # it to the kernel tree would defeat the point.
    "$TOP/tools/refresh-patches.sh" --check
fi

# --- Board DTB + shim -------------------------------------------------------
# dabao.dts lives in the kernel tree and uses cpp-style includes.
DTSDIR="$TOP/sources/linux/arch/riscv/boot/dts/baochip"
cpp -nostdinc -I "$DTSDIR" -undef -x assembler-with-cpp "$DTSDIR/dabao.dts" \
    | dtc -I dts -O dtb -i "$DTSDIR" -o "$OUT/dabao.dtb"
make -C "$TOP/firmware/bao1x-sbi" O="$OUT/sbi" BOARD=dabao DTB="$OUT/dabao.dtb" \
    ${SHIM_ONLY:+SHIM_ONLY=1}

# --- Assemble the flat payload (base = 0x60060300) --------------------------
# Slot boundaries are not sector-aligned (the 768-byte sig block shifts
# everything), so build strictly by truncate-to-offset + append.
SRC="$OUT/source.bin"
cp "$OUT/sbi/bao1x-sbi.bin" "$SRC"
SHIM_SIZE=$(stat -c%s "$SRC")
[ "$SHIM_SIZE" -le $KERNEL_OFF ] || { echo "shim overflows its slot" >&2; exit 1; }

if [ -n "$SHIM_ONLY" ]; then
    # The payload is the shim alone -- quick to sign and verify, and the
    # signature block it gets covers only the shim, so if boot1 is ever
    # rejected and boot0 falls back to this region it lands on the shim and
    # parks in the heartbeat rather than on a kernel.
    #
    # It does NOT clear a previously flashed kernel: boot1 programs only the
    # blocks a UF2 actually carries and never erases the region
    # (bao1x-boot/boot1/.../usb/handlers.rs), so old bytes stay at 0x60070000.
    # They are simply never reached, because this shim has no jump to them.
    #
    # Verify that -- a stale full-image build in the same output directory
    # would otherwise produce a "shim-only" file that does jump to the kernel.
    grep -q "SBI:shim-only" "$SRC" || {
        echo "SHIM_ONLY=1 but the shim carries no shim-only marker (stale build?)" >&2
        exit 1
    }
    echo "slot check OK (shim-only): shim $SHIM_SIZE bytes, no kernel entry"
else
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
fi

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
# Distinct names, because the two images are indistinguishable once copied
# onto the board and only one of them can reach Linux.
if [ -n "$SHIM_ONLY" ]; then
    UF2="$OUT/dabao-shim-only.uf2"; STALE="$OUT/dabao-linux.uf2"
else
    UF2="$OUT/dabao-linux.uf2";     STALE="$OUT/dabao-shim-only.uf2"
fi
# flash.bin/flash.uf2 have one name in both modes and were just overwritten, so
# the other mode's UF2 no longer corresponds to anything here. Leaving it would
# hand the operator a plausible-looking file that docs/05 tells them to flash --
# exactly the ambiguity the distinct names exist to prevent.
rm -f "$STALE"
python3 "$TOP/tools/mkuf2.py" "$OUT/flash.bin" "$UF2" --base 0x60060000
cmp "$OUT/flash.uf2" "$UF2" || { echo "UF2 mismatch vs vendor tool!" >&2; exit 1; }

echo "=== dabao image complete ==="
ls -la "$OUT/flash.bin" "$UF2"
md5sum "$UF2"     # record this in the bring-up log with the boot it produced
echo "flash: copy $(basename "$UF2") to the BAOCHIP mass-storage volume (boot1),"
echo "       or stream with sources/xous-core/bao1x-boot/uf2send.py over the boot1 console."
if [ -n "$SHIM_ONLY" ]; then
    echo "NOTE: shim-only image -- it will NOT boot Linux. Expect the SBI:* stage"
    echo "      markers, then 'SBI:shim-only' and a 1Hz 'SBI:alive' heartbeat."
fi
