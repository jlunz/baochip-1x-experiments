#!/usr/bin/env bash
# Install all host tools needed for the bao1x Linux port (Fedora).
# Idempotent: safe to re-run. Everything this project needs on the host is listed here.
set -euo pipefail

echo "=== bao1x port: host tool installation (Fedora) ==="

# --- Kernel + firmware cross-build ---------------------------------------
# Fedora's riscv64 cross-gcc builds RV32 kernel/firmware fine (bare-metal,
# -march=rv32imac_zicsr_zifencei -mabi=ilp32); RV32 *userspace* toolchain is
# built by Buildroot, not installed here.
KERNEL_BUILD_DEPS=(
    gcc-riscv64-linux-gnu binutils-riscv64-linux-gnu
    gcc gcc-c++ make git flex bison bc perl
    openssl-devel elfutils-libelf-devel ncurses-devel
    dwarves # pahole, only needed if BTF ever gets enabled
)

# --- Buildroot host dependencies ------------------------------------------
BUILDROOT_DEPS=(
    which sed binutils diffutils gzip bzip2 tar cpio unzip rsync file wget
    findutils patch perl-ExtUtils-MakeMaker perl-Thread-Queue perl-FindBin
    perl-English perl-IPC-Cmd
)

# --- Emulation / verification ---------------------------------------------
EMU_DEPS=(
    qemu-system-riscv    # phase 1: generic rv32 XIP feasibility on -M virt
    dtc                  # device tree compiler
    verilator            # optional: full-chip RTL sim ground truth
    python3 python3-pip  # uf2/signing tooling
)

sudo dnf install -y "${KERNEL_BUILD_DEPS[@]}" "${BUILDROOT_DEPS[@]}" "${EMU_DEPS[@]}"

# --- Renode (not packaged in Fedora) ---------------------------------------
# Portable build into tools/renode; pinned version for reproducibility.
RENODE_VERSION=1.16.1
RENODE_DIR="$(dirname "$0")/../tools/renode"
if [ ! -x "$RENODE_DIR/renode" ]; then
    echo "=== Installing Renode $RENODE_VERSION (portable) into tools/renode ==="
    mkdir -p "$RENODE_DIR"
    curl -L "https://github.com/renode/renode/releases/download/v${RENODE_VERSION}/renode-${RENODE_VERSION}.linux-portable.tar.gz" \
        | tar xz -C "$RENODE_DIR" --strip-components=1
else
    echo "Renode already present in tools/renode — skipping"
fi
"$RENODE_DIR/renode" --version || true

echo "=== Done. Next: setup/fetch-sources.sh ==="
