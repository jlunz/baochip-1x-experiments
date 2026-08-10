# Status & how to resume this port

> Living document. Update the tables here whenever a phase changes state.
> Chronological detail and evidence live in `04-bringup-log.md` (lab notebook);
> this file is the *entry point for a future session/agent* picking up the work.

Last updated: **2026-08-07** (phase 4 started on real hardware: image validates
and DEVELOPER_MODE is burnt; shim hangs pre-console, fix built but unproven —
board stopped responding mid-session, see 04-bringup-log).

## Phase status

| Phase | State | Gate artifact / evidence |
|---|---|---|
| 0 — repo, docs, environment | ✅ done | `setup/*.sh` reproduce the host |
| 1 — rv32 XIP feasibility (QEMU virt) | ✅ done | busybox shell in a **2 MiB** RAM window; `emulation/qemu/*.sh` |
| 2 — Renode bao1x platform | ✅ done | `emulation/renode/tests/run-smoke.sh` GREEN |
| 3 — SBI shim + Linux in Renode | ✅ done | `tests/linux.robot` GREEN: login on ttyBAO0, native irqchip/serial stack, `sleep 1` returns |
| 4 — hardware bring-up on Dabao | 🟡 **in progress on silicon** | image validates on-chip (`Booting with key 3/3(dev )`), DEVELOPER_MODE burnt, CPU 350MHz confirmed; shim silent after the jump — bounded-wait + stage-marker fix built and Renode-clean, **not yet proven on hardware** |
| 5 — driver expansion | 🟡 nearly done | pinctrl/GPIO ✅, I2C ✅, SPI ✅, RRAM-MTD+JFFS2 ✅ (all robot-tested); SD n/a on Dabao (no slot); USB-UDC not started (needs hardware to validate) |
| 6 — upstream packaging | 🟡 nearly done | 22 patches in review order (SoC/dts/defconfig/MAINTAINERS included, dtbs_check clean); mainline-HEAD forward-port outstanding |

## What exists and how to rebuild it

All commands run from the repo root; artifacts land in `build/` (gitignored).

```sh
./setup/install-tools.sh            # host deps (dnf) + Renode 1.16.1 + pip deps
./setup/fetch-sources.sh            # linux v6.14 (shallow), buildroot, xous-core, baochip-1x, opensbi, cramfs-tools
# Kernel patches: apply linux/patches/v6.14/*.patch onto tag v6.14
#   (sources/linux already carries them on branch bao1x-xip-fixes)

./emulation/qemu/build-rootfs.sh    # rv32 musl static busybox + gpio-tool/i2c-tool -> XIP cramfs
./emulation/qemu/build-phase1.sh    # phase-1 QEMU kernel (generic XIP proof)
MEM=6M ./emulation/qemu/run-phase1.sh   # boots to shell in a 2MiB window

./emulation/renode/build-phase3.sh  # bao1x kernel + DTB + bao1x-sbi shim (BOARD=renode)
tools/renode/renode-test emulation/renode/tests/linux.robot --results-dir build/phase3/results
./emulation/renode/tests/run-smoke.sh   # bare-metal platform smoke test

./tools/build-dabao-image.sh        # dabao DTB + shim (BOARD=dabao) + sign + UF2
```

## Architecture in one paragraph

boot0→boot1 (vendor, untouched) jump in M-mode to the 768-byte signature
block at RRAM 0x60060000 whose first word jumps to **bao1x-sbi**
(`firmware/bao1x-sbi`, ~4 KiB) at 0x60060300. The shim provides the SBI the
stock kernel expects on this CLINT-less, time-CSR-less chip (DBCN console on
uDMA UART2, `set_timer` via TIMER0→`mip.STIP`, rdtime emulation from mcycle,
delegation) and mret's into the XIP kernel at **0x60070000** (v6.14 + the
patch series). Kernel .data/.bss live in the 2 MiB SRAM at 0x61000000 (top
16 KiB = shim workspace, DTB copied to 0x611F8000); root is XIP cramfs on
mtd-rom at **0x60220000** (usable RRAM ends 0x603DA000). Native drivers:
`irq-bao1x-intc` (CSRs 0x9C0/0xDC0) → `irq-bao1x-irqarray` banks →
`bao1x_uart` console, `pinctrl-bao1x` (IOX), `i2c-bao1x`; DUART earlycon.

## Key numbers / invariants (do not regress)

- 2 MiB RAM recipe (all four required): `SECTION_SIZE_BITS=22` (patch 0007),
  `LOG_BUF_SHIFT=12`, `SYSFS=n` + `BLOCK=n`, `THREAD_SIZE_ORDER=0` +
  `IRQ_STACKS` (patch 0008). Watch **every order>0 allocation** (see the
  gpiolib-cdev kvzalloc patch 0013 for what happens otherwise).
- Renode clocking: `PerformanceInMips=100` = mcycle = CLINT timeProvider =
  TIMER0 = DT timebase = 100 MHz. One knob, keep them equal.
- Dabao clocking (from boot1): fclk 700 MHz, CPU/mcycle/timebase 350 MHz,
  TIMER0/TICKTIMER on fclk → shim `TIMER0_TICKS_MULT=2`, perclk ≈ 99.8 MHz.
- Shim currently ungates uDMA clocks (uart2, i2c0, spim0) — a kernel UDMA_CTRL
  clk driver is future work; add new peripherals to `uart2_init()` until then.

## Immediate next steps

1. **Phase 4 (in progress)**: DEVELOPER_MODE is already burnt; the image
   validates on-chip. The open item is the shim hanging before any console
   output — flash the instrumented build (bounded `SPIN_LIMIT` waits + `SBI:*`
   stage markers) and read the last marker. Prime suspect is `duart_puts()`
   spinning on an unrouted DUART whose `SFR_ETUC` divider is 0. Flash over the
   **console UART**, not USB (`uf2send.py` into boot1's REPL); PROG+RESET is
   the guaranteed way back to boot1. See `05-hardware-bringup.md`.
   *Blocked at end of 2026-08-07: board silent on both USB and UART after a
   replug — loopback-test the probe path, check power LED, try another cable
   and a direct root-hub port before suspecting the board.*
2. **Corigine USB UDC** (0x50200000, port from xous
   `libs/bao1x-hal/src/usb/`) — the last phase-5 driver; needs real
   hardware to validate meaningfully, so do it after/with phase 4.
   Smaller items: UDMA_CTRL clk driver (retire the shim's ungating),
   uart driver migration from clock-frequency to clocks (like i2c/spi),
   IOX gpio-irq support (8 INTCR slots).
3. **Phase 6 (remaining)**: forward-port the series onto mainline HEAD
   (XIP_KERNEL is being removed there — the cover letter argues this port
   as the user justifying revival) + cover letter. The `maintainers:`/
   MAINTAINERS identity needs the user's review before any submission
   (currently their +claude address; no Signed-off-by by deliberate
   choice — the user adds it).

## Gotchas for a future agent (hard-won)

- `merge_config.sh` needs `ARCH=riscv` in the environment or it validates
  against x86 and silently drops everything.
- Never `pkill -f` a pattern that matches your own wrapper shell.
- Renode: software breakpoints don't work on flash-executed code (use hbreak);
  `$ORIGIN` needs `path add $ORIGIN`; two terminal testers ⇒ every keyword
  needs `testerId=`; `ReadDoubleWord` prints zero-padded (`0x00000001`).
- buildroot ABI changes need `rm -rf build/buildroot-phase1`; correct rv32
  symbols are `BR2_riscv_custom` + `BR2_RISCV_ISA_RVM/RVA/RVC` + ILP32.
- The kernel tree in `sources/linux` is a shallow clone: `git describe`
  fails ⇒ pass `--git-describe` to xous-sign-image.
- QEMU pflash must never be CFI-probed while executing from it (mtd-rom).
- The bao1x IRQARRAY base addresses follow LiteX *alphabetical* allocation
  (0,1,10..19,2..9) — do not "fix" the ordering in bao1x.repl.
- **Emulation cannot test busy-waits.** Renode's DUART completes instantly and
  its DMA is synchronous, so every `while (STATUS & bit)` in the shim is
  effectively untested until silicon. Bound them all (`SPIN_LIMIT`): losing
  debug output beats wedging the boot. This cost a whole hardware session.
- Hardware serial: address ports via `/dev/serial/by-id/`, never `ttyACMn`
  (the debug probe renumbers when the board's USB drops); install
  `setup/99-baochip.rules` **on the host** or permissions reset on every board
  reset. The dev host is a QEMU VM with the board passed through — if the
  *host's* `lsusb` lacks `1d50:6196`, it is not a passthrough problem.
- Keep a UART capture running *across* any user-performed reset/replug;
  otherwise the event is unobservable after the fact.
