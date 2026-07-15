# Status & how to resume this port

> Living document. Update the tables here whenever a phase changes state.
> Chronological detail and evidence live in `04-bringup-log.md` (lab notebook);
> this file is the *entry point for a future session/agent* picking up the work.

Last updated: **2026-07-15** (after phase 5a; phase 5b I2C in verification).

## Phase status

| Phase | State | Gate artifact / evidence |
|---|---|---|
| 0 — repo, docs, environment | ✅ done | `setup/*.sh` reproduce the host |
| 1 — rv32 XIP feasibility (QEMU virt) | ✅ done | busybox shell in a **2 MiB** RAM window; `emulation/qemu/*.sh` |
| 2 — Renode bao1x platform | ✅ done | `emulation/renode/tests/run-smoke.sh` GREEN |
| 3 — SBI shim + Linux in Renode | ✅ done | `tests/linux.robot` GREEN: login on ttyBAO0, native irqchip/serial stack, `sleep 1` returns |
| 4 — hardware bring-up on Dabao | 🟡 **prepared, blocked on user** | `build/dabao/dabao-linux.uf2` (signed, verified); flashing guide `05-hardware-bringup.md`; needs the physical board |
| 5 — driver expansion | 🟡 in progress | 5a pinctrl/GPIO ✅ (robot-tested); 5b I2C written, in verification; SPI/RRAM-MTD/SD/USB-UDC not started |
| 6 — upstream packaging | 🟡 partial | 16-patch series exports clean; dt-bindings validate; MAINTAINERS entry + mainline-HEAD rebase outstanding |

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
- Shim currently ungates uDMA clocks (uart2, i2c0) — a kernel UDMA_CTRL
  clk driver is future work; add new peripherals to `uart2_init()` until then.

## Immediate next steps

1. **Phase 5b (in flight)**: finish uDMA I2C verification in Renode
   (`linux.robot` I2C section: TMP103 at 0x48 via i2c-tool). Then commit as
   kernel patches (binding + driver) like the previous drivers.
2. **Phase 4 (user)**: flash `build/dabao/dabao-linux.uf2` per
   `05-hardware-bringup.md` (⚠ irreversible DEVELOPER_MODE burn, approved
   2026-07-14). First-on-silicon watch list is in that file (rdtime
   emulation, timer scaling, UART divider).
3. **Phase 5c+**: UDMA SPIM driver (same pattern as I2C; regs 0x50105000+),
   RRAM MTD write via RRC @0x40000000 (xous `rram.rs` is the reference;
   read-while-write hazards vs XIP!), SD via UDMA SDIO (0x5010d000), then
   the Corigine USB UDC (0x50200000, port from xous
   `libs/bao1x-hal/src/usb/`) — the big one.
4. **Phase 6**: MAINTAINERS entry, reorder series (bindings before drivers),
   rebase/forward-port onto mainline HEAD (XIP revival argument), cover
   letter. The `maintainers:` fields in the dt-bindings need the user's
   review before any submission (currently their +claude address, no
   Signed-off-by anywhere by deliberate choice — the user adds it).

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
