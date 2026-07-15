#!/bin/sh
# Buildroot post-build hook: cross-compile the small test tools that the
# bao1x rootfs carries (busybox lacks GPIO chardev support and the kernel
# has no sysfs).
set -eu

BOARD_DIR="$(dirname "$0")"
CC="$HOST_DIR/bin/riscv32-linux-gcc"

"$CC" -static -Os -o "$TARGET_DIR/usr/bin/gpio-tool" "$BOARD_DIR/gpio-tool.c"
