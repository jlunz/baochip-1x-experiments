#!/usr/bin/env bash
# Fetch all upstream source trees into sources/ (gitignored).
# Idempotent: skips anything already cloned.
set -euo pipefail

TOP="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$TOP/sources"
mkdir -p "$SRC"

clone() { # url dir [extra git-clone args...]
    local url="$1" dir="$2"; shift 2
    if [ -d "$SRC/$dir/.git" ]; then
        echo "sources/$dir already cloned — skipping"
    else
        git clone "$@" "$url" "$SRC/$dir"
    fi
}

# Mainline kernel. Blob-less partial clone: full history for tag archaeology
# (finding the last-good XIP tag) without the multi-GB blob download.
clone https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git linux \
    --filter=blob:none

# Buildroot (rootfs + rv32 musl toolchain). Track latest LTS branch.
clone https://gitlab.com/buildroot.org/buildroot.git buildroot --depth 1

# Vendor OS: HAL references, generated SVDs, signing/UF2 tooling, Renode models.
clone https://github.com/betrusted-io/xous-core.git xous-core --depth 1

# The chip RTL: ground truth for registers and interrupt wiring.
clone https://github.com/baochip/baochip-1x.git baochip-1x --depth 1

# OpenSBI: only used as M-mode scaffolding for the QEMU rv32 XIP feasibility
# test (fw_jump into pflash). The real board uses firmware/bao1x-sbi instead.
clone https://github.com/riscv-software-src/opensbi.git opensbi --depth 1

# Nicolas Pitre's cramfs-tools: mkcramfs with XIP direct-pointer support
# (mainline cramfs XIP, v4.15+). Used to build the XIP rootfs images.
clone https://github.com/npitre/cramfs-tools.git cramfs-tools --depth 1

echo "=== All sources present under sources/ ==="
