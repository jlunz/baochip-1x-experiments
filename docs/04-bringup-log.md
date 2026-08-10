# Bring-up log (lab notebook)

Newest entries at the bottom. Every experiment, measurement, and surprise goes here.

## 2026-07-14 — Phase 0: research + repo setup

- Researched chip from primary sources (RTL repo, xous-core, docs site). Full findings in
  `01-hardware-dossier.md`. Highlights that shaped the design:
  - VexRiscv built with `CsrPluginConfig.linuxFull` + Sv32 `MmuPlugin` → S-mode Linux viable.
  - `timerInterrupt`/`softwareInterrupt` tied to 0 in RTL → no MTIP/MSIP ever; all timing
    via external-irq lines 20 (TICKTIMER) / 30 (TIMER0) and custom CSRs 0xBC0/0xFC0/0x9C0/0xDC0.
  - Payload entered in M-mode at RRAM 0x6006_0000 after ed25519 check; dev key is public;
    first dev-signed boot burns DEVELOPER_MODE + erases factory secrets (user approved).
  - ~3.47 MiB XIP flash window + 2 MiB SRAM → XIP kernel + XIP cramfs architecture.
- Upstream status check: RISC-V XIP_KERNEL broken since commit `a44fb5722199` (~10 months),
  removal queued by Nam Cao ("revert is possible if someone shows up complaining").
  → Phase 1 must find the last-good tag empirically; candidates: v6.7/v6.8 era (post-restore
  per riscv.org news, pre-breakage).
- No existing Linux port found for bao1x/cramium (searched July 2026).
- Repo initialized; plan approved by user (see 02-port-design.md).

Environment: Fedora host (kernel 7.1.3-100.fc43), no cross toolchain installed yet.

### XIP archaeology (kernel base selection)

- Breaking commit `a44fb5722199` = "riscv: Add runtime constant support" (Charlie Jenkins):
  runtime constants patch kernel *text* at boot — impossible when text is in ROM. Explains
  the breakage class, and the eventual fix (fall back to plain loads under `XIP_KERNEL`).
- GitHub compare API: `a44fb5722199` is **in v6.15, not in v6.14** → **v6.14 = last mainline
  release with working XIP**. Chosen as the phase 1–4 base tag; forward-port to HEAD is
  phase 6 (includes reverting the 7.1-era removal + the runtime-const fix).
- v6.14 `arch/riscv/Kconfig`: `XIP_KERNEL` depends on `MMU && SPARSEMEM && NONPORTABLE`
  — no 64-bit restriction, so rv32+XIP is Kconfig-legal out of the box.
- QEMU phase-1 scaffolding: OpenSBI `fw_jump` (rv32, jump target = pflash base 0x2000_0000)
  boots the xipImage placed in `-drive if=pflash`; added opensbi to fetch-sources.sh.
  (The real board never uses OpenSBI — see 02-port-design.md.)

## 2026-07-14 (cont.) — Phase 2: Renode platform GREEN

- `bao1x.repl` + custom C# models (DUART, IRQARRAY, LiteX timer with bao1x layout,
  TickTimer). M-mode smoke test passes on first full run: DUART TX, EV_SOFT →
  machine-external irq via CSRs 0xBC0/0xFC0, w1c semantics, TIMER0 one-shot on ext-irq 30,
  TICKTIMER monotonicity. `emulation/renode/tests/run-smoke.sh` is the regression gate.
- Renode facts learned: `path add $ORIGIN` needed before `i @peripherals/*.cs`;
  VexRiscv model maps GPIO 0-31 → machine ext-irq array, 1000-1031 → supervisor array
  (hardware feeds one line into both → all models expose IRQ + SupervisorIRQ).

## 2026-07-14 (cont.) — Phase 1: rv32 XIP kernel boots to login on QEMU

v6.14 + tinyconfig fragment. **Five real kernel bugs found and fixed** (series in
`linux/patches/v6.14/`, all verified by GDB against the live target):

1. **BSS never cleared on XIP** (`head.S`): the clear loop is `#ifndef CONFIG_XIP_KERNEL`
   and nothing else zeroes .bss → stale-RAM nondeterminism (manifested as garbage
   `riscv_isa` bitmap → spurious Zkr → illegal `seed` CSR access in `random_init_early`).
2. **Trampoline maps RAM instead of flash on rv32** (`init.c`): !64BIT path lacks the XIP
   special case → first post-satp fetch executes data bytes → illegal-instruction loop.
3. **swapper_pg_dir never gets the kernel mapping on rv32** (`init.c`): `IS_ENABLED(64BIT)`
   guard → text unmapped after `setup_vm_final` → recursive fetch faults on stvec.
4. **Linear mapping inconsistent on rv32-XIP** (`init.c`+`page.h`): `va_pa_offset` ignored
   the in-flash image window ( +4MiB error in every linear `__pa`, satp pointed past
   swapper_pg_dir); `is_linear_mapping` claimed text VAs; RAM below `PHYS_RAM_BASE`
   stayed in memblock and its linear VAs collided with kernel text, pre-filling PGD
   entries so the text superpage never installed (caught via HW watchpoint on the PGD).
5. **`flush_icache_pte` oopses on memmap-less PFNs** (`cacheflush.c`): cramfs-XIP maps
   userspace text straight to flash PFNs via `vmf_insert_mixed`; `pte_page()` on those
   is bogus. Guard with `pfn_valid()`. (Strong evidence riscv cramfs-XIP was never run.)

QEMU-side lessons (in `emulation/qemu/run-phase1.sh`):
- rv32 XIP needs PMD (4MiB) alignment for `PHYS_RAM_BASE` (BUG_ON in setup_vm) —
  bao1x bases 0x60000000/0x61000000 are naturally aligned.
- Never CFI-probe the flash you execute from (pflash leaves read-array mode → garbage
  fetches). DTB is patched: `cfi-flash` → `mtd-rom`, and the two banks split into two
  nodes because mtd_concat can't `mtd_point()` (needed by cramfs direct mapping).
- `merge_config.sh` silently validates against x86 without `ARCH=riscv` in env.
- tinyconfig needs `CONFIG_MISC_FILESYSTEMS=y` for CRAMFS to be selectable.

**Result: `dabao login:` prompt.** XIP kernel (~1.9MiB text in flash), XIP cramfs
(1.15MiB) with busybox, 12MiB RAM ceiling for now (2MiB target comes after the bao1x
port itself). One remaining phase-1 item: buildroot silently fell back to ilp32d
(double-float) because the defconfig used a wrong symbol (`BR2_riscv_custom=y` is the
choice symbol); soft-float rootfs rebuild running — rcS SIGILL on `fsd` will disappear
with it.

## 2026-07-15 — Phase 1 complete: shell in 2 MiB RAM

Soft-float rootfs verified: `busybox` now reads `soft-float ABI` (readelf flags 0x1,
RVC), rcS runs to completion — the `fsd` SIGILL is gone. Full boot at 12MiB:
MemTotal 10496K, ~2.2MiB used after boot with syslogd/klogd/crond running.

Then the real gate: `MEM=6M` gives the kernel a 2MiB RAM window (0x80400000 +
0x200000), exactly the bao1x SRAM budget. Three successive walls, each measured, each
fixed at the root:

1. **SPARSEMEM memmap panic.** `__populate_section_memmap: Failed to allocate 1048576
   bytes` — rv32 `SECTION_SIZE_BITS=27` means one 128MiB section costs a 1MiB memmap,
   half the machine. XIP_KERNEL *requires* SPARSEMEM, so this hits every small-RAM rv32
   XIP target. → kernel patch 0007: 4MiB sections on 32-bit (the classic-SPARSEMEM
   minimum, `MAX_PAGE_ORDER + PAGE_SHIFT`); worst-case static cost 8KiB.
2. **printk ring buffer: 528KiB.** Default `LOG_BUF_SHIFT=17` allocates 128K of data
   + 400K(!) of per-record metadata. → `CONFIG_LOG_BUF_SHIFT=12` in the fragment
   (rwdata dropped 469K→82K, reserved 824K→312K).
3. **Slab: 1.5MiB unreclaimable.** Ranked via a temporary SLUB_DEBUG boot at MEM=8M:
   `kernfs_node_cache` 570K + its inodes/dentries/kobject names (sysfs allocates a
   node per kobject, eagerly) and ~170K of block-layer bio/biovec mempools nothing
   uses (root is CRAMFS_MTD, no block device anywhere). → `CONFIG_SYSFS=n`,
   `CONFIG_BLOCK=n` (devtmpfs provides /dev without sysfs).
4. **Fragmentation kills order-1 stacks.** With 724K free — all as single 4K pages,
   mobility grouping disabled (zone < pageblock) — `kthreadd` OOMs on an 8K stack;
   every later fork() would too. VMAP_STACK is 64-bit-only on riscv *for cause*
   (rv32 still lazy-faults vmalloc PGD entries, and a lazy fault can't be taken on
   the stack the trap handler pushes to — same reason x86_32 never got it). → kernel
   patch 0008: allow `THREAD_SIZE_ORDER` selection without VMAP_STACK; fragment sets
   4K stacks + keeps IRQ_STACKS (default y) + `SCHED_STACK_END_CHECK` as a canary
   while we qualify the depth.

**Result: interactive root shell at 2048K.** `Memory: 1568K/2048K available (976K
kernel code [in flash], 73K rwdata, 169K rodata, 94K init, 56K bss, 260K reserved)`;
after boot with all three demo daemons still running: MemTotal 1816K, MemFree ~250K.
The bao1x system won't run those daemons, and the SBI shim reserves only a few KiB,
so the budget closes with margin. Kernel series is now 8 patches (re-exported to
linux/patches/v6.14/). KALLSYMS costs only flash (rodata is XIP) — worth enabling
for hardware bring-up debugging later.

## 2026-07-15 (cont.) — Phase 3: Linux boots on the bao1x platform in Renode

First full-stack boot, and it worked on (nearly) the first attempt: **`dabao login:`
on uDMA UART2** with the exact bao1x memory map — shim @0x60060000, XIP kernel
@0x60070000, XIP cramfs rootfs @0x60220000 (all inside the vendor's usable-RRAM
limit 0x603DA000), kernel RAM = SRAM @0x61000000 (2MiB minus 16KiB shim workspace).

Strategy that made attempt 1 driverless: the kernel uses only stock code —
`HVC_RISCV_SBI` console + `earlycon=sbi` (DBCN), `RISCV_TIMER` via SBI set_timer,
physmap/mtd-rom/cramfs root. Everything bao1x-specific lives in **bao1x-sbi**
(firmware/bao1x-sbi, ~3KiB M-mode shim): DBCN/legacy console routed to UART2
(DMA TX bounced through IFRAM0 tail, PIO RX), sbi_set_timer armed on TIMER0
(machine ext-irq 30) with the M-ext trap setting mip.STIP (mideleg-delegated),
rdtime/rdtimeh emulated from mcycle on illegal-instruction traps, non-rdtime
illegals forwarded to stvec by hand, medeleg for the rest.

Renode findings:
- Renode's VexRiscv implements the `time` CSR natively (silicon traps it!) and
  needs a time provider: parked a CLINT at 0xF0010000 (no DT node, nothing on
  silicon decodes it) at 100MHz to match `PerformanceInMips: 100` (= mcycle
  rate) and DT `timebase-frequency`. The shim's rdtime emulation is therefore
  UNTESTED in Renode — first exercised on hardware (phase-4 watch item).
- `riscv: base ISA extensions` came up empty: v6.14 with
  `CONFIG_RISCV_ISA_FALLBACK=n` ignores `riscv,isa`; added `riscv,isa-base` +
  `riscv,isa-extensions` to the DT (the modern canonical properties).
- `mount: mounting sysfs on /sys failed` in rcS — expected (SYSFS=n); cosmetic.

Memory: 1560K/2032K available, boots clean with the 4K-stack + no-sysfs +
no-block + LOG_BUF=4K recipe from phase 1. No allocation failures.

## 2026-07-15 (cont.) — Phase 3b: native driver stack GREEN in Renode

Replaced the SBI-console bring-up vehicle with the port's real drivers — all
passing linux.robot on the first run:

- `irq-bao1x-intc` (kernel patch 0009): the 32-line VexRiscv external interrupt
  array as a chained irqchip on riscv-intc hwirq 9 (S-ext), mask CSR 0x9C0,
  pending CSR 0xDC0, level-type children. First S-mode exercise of Renode's
  supervisor CSR array — works.
- `irq-bao1x-irqarray` (0009): IRQARRAY banks as chained edge-type domains
  (EV_PENDING w1c ack, EV_ENABLE mask). DT has bank 5 (uDMA UARTs) so far.
- `bao1x_uart` (0010): uDMA UART serial+console "ttyBAO0". DMA-only TX bounced
  through a private IFRAM0 slice (DT reg entry "txram" @0x50010000, kfifo →
  memcpy_toio → TX_SADDR/SIZE/CFG kick, "tx done" irq advances); PIO RX via
  VALID/DATA on the "rx char" irq (slots 9/10 of irqarray5). Console writes
  synchronous. PORT_BAO1X = 124.
- `bao1x_duart` (0010): DUART earlycon — works with zero setup, the earliest
  sign of life for hardware bring-up (`earlycon` + stdout-path=&duart).

Boot flow now: DUART earlycon → ttyBAO0 console handover → interactive login,
`sleep 1` returns (SBI timer unchanged). hvc0/DBCN remains built as fallback.
Deferred by choice: native TIMER0/TICKTIMER drivers — SBI set_timer + rdtime
is the standard RISC-V mechanism and measured fine; a TICKTIMER clocksource
is a phase-5 optimization (saves the rdtime trap per clock read on silicon).
Watch item for phase 4: tx_empty()/tx-done event vs. real shifter drain
(STATUS.busy) — indistinguishable in Renode's instant DMA.

## 2026-07-15 (cont.) — Phase 4 prep: flash-ready signed UF2 for Dabao

Archaeology (all from xous-core, cited in the code): boot1 runs
`init_clock_asic(700MHz)` on Dabao before the payload — the shim inherits
fclk=700MHz, CPU/mcycle=350MHz, perclk≈99.8MHz (targets 100MHz), SRAM trims
and console pinmux (PB13/PB14 AF1) already done: zero platform init needed.
Payload format: 768-byte `SignatureInFlash` at 0x60060000 (first word =
`jal x0,+768`, ed25519ph over SealedFields‖pad‖image, FunctionCode
Baremetal=6, magic "yumyBao3"), code entry at 0x60060300 — shim relinked
there (Renode robot re-run: still GREEN). TIMER0/TICKTIMER count fclk →
`TIMER0_TICKS_MULT=2` on dabao (timebase = mcycle = 350MHz).

Pipeline `tools/build-dabao-image.sh`: dabao.dts (timebase 350MHz) → shim
BOARD=dabao → truncate/append assembly (slots are NOT sector aligned; a dd
bs=4096 seek rounding bug ate the kernel — caught by the new in-script slot
verifier) → `xous-sign-image --bao1x` (vendor signer, built with dnf cargo;
needs --git-describe on shallow clones) → UF2 via tools/mkuf2.py, verified
byte-identical to the signer's own UF2 output (family 0xa7d76373).
Result: build/dabao/dabao-linux.uf2, 10736 blocks, 0x60060000..0x602ff000
(usable-RRAM limit 0x603da000 respected, ~0.9MB headroom).

Silicon-only checks resolved from RTL: VexRiscv D$ is write-through (no
`withWriteBack` in GenCramSoC DataCacheConfig) → IFRAM DMA bounce needs no
cache maintenance. Shim banner now mirrored to UART2 (DUART pad may be
unrouted). Flashing steps + triage matrix: docs/05-hardware-bringup.md.
Board work now needs the user (flash + irreversible DEVELOPER_MODE burn).

## 2026-07-15 (cont.) — Phase 5a: IOX pinctrl/GPIO GREEN in Renode

Kernel patches 0013-0015: `gpiolib: cdev: use kvzalloc` (opening
/dev/gpiochipN needs an order-2 alloc for the embedded 32-entry lineinfo
kfifo — failed from fragmentation alone on the 2MiB system, OOM-killed the
shell; vmalloc fallback fixes it), dt-binding + `pinctrl-bao1x` (96 pins as
single-pin groups "PA0".."PF15", functions gpio/af1/af2/af3, generic
function/groups DT parsing, gpio-ranges auto-mux; pull-up/schmitt/slew
pinconf; the 8 IOX interrupt slots deferred). Renode: Bao1xIox model
(AFSEL/OUT/OE/PU/IN + pad readback (OE&OUT)|(~OE&ext); inputs injected with
`iox OnGPIO <n> <bool>`; numbering port*16+pin). Rootfs gains `gpio-tool`
(cdev v2 ioctls; busybox has none, no sysfs) via buildroot post-build hook.
linux.robot: drives PC0 out (register bits asserted), reads injected PC1
both states. DT: iox node + uart2 pinctrl-0 (PB13/14 af1, same as boot1).
Lesson (again): every /dev/gpiochip open costs >8KiB contiguous before the
kvzalloc patch — on 2MiB systems watch every order>0 allocation.

## 2026-07-15 (cont.) — Phase 5b: uDMA I2C GREEN in Renode

Kernel patches 0016/0017: dt-binding + `i2c-bao1x` — the udma_i2c command
engine (opcodes in [31:28]: START/WRB/WR/RD_ACK/RD_NACK/STOP/RPT/CFG/EOT,
from rtl/ips/incdir/udma_i2c_defines.sv) driven via a per-bus IFRAM slice
split into cmd/TX/RX bounce buffers. Polled xfer: CMD_SIZE drains + STATUS
busy clears; ACK reg (+0x38) = sticky clear-on-read NACK → -ENXIO; SETUP
reset recovers a wedged engine. Divider = perclk/(4·f) per vendor HAL.
perclk is now a proper fixed-clock DT node (boards define it; i2c uses
`clocks=`); the uart driver still uses clock-frequency — migrate in phase 6.
Dabao bus: i2c0 @0x50109000, PB11 SCL / PB12 SDA AF1 (pinctrl node, SDA+SCL
pull-up). Shim ungates i2c0 (bit 8) alongside uart2 until a UDMA_CTRL clk
driver exists. Renode: Bao1xUdmaI2c interprets the command stream against
SimpleContainer I2C slaves; TMP103 at 0x48. Rootfs: i2c-tool (I2C_RDWR).
linux.robot: `i2c-tool scan` finds 0x48, temperature register reads back.
Driver bug found by the test: pinconf group-set was missing in pinctrl-bao1x
(bias-pull-up on the i2c0 group failed the whole map) — folded into 0015.

## 2026-07-15 (cont.) — Phase 5c: uDMA SPI GREEN; rootfs slimmed (no daemons)

Kernel patches 0018/0019: dt-binding + `spi-bao1x` (command-stream master,
same IFRAM-bounce architecture as I2C; CS lives in the stream → whole
spi_message compiled into one command list, transfer_one_message, polled).
Renode Bao1xUdmaSpim interprets CFG/SOT/SEND_CMD/TX/RX/FULL_DUPL/EOT
against container SPI slaves; Micron MT25Q on CS0. Verified through the
kernel's own JEDEC probe: /proc/mtd gains "spi0.0" (mtd1), 256-byte read OK.
Two lessons: (1) adding SPI+MTD_SPI_NOR pushed the system into boot OOM —
the buildroot demo daemons (syslogd/klogd/crond/network, ~500K RSS) had no
business on a 2MiB machine; replaced via rootfs overlay with a minimal
inittab (getty only) → 428K free after login with ALL drivers loaded.
(2) don't assert on early printk lines: the 4K ring overwrites them before
the console registers — assert on /proc/mtd instead. Series at 19 patches.
Dabao UF2 rebuilt (kernel+rootfs+shim; shim now ungates uart2+i2c0+spim0).

## 2026-07-15 (cont.) — Phase 5d: RRAM MTD (writable storage) GREEN

Kernel patches 0020/0021: dt-binding + `bao1x-rram` MTD driver. Reads and
mtd_point() straight from the mapping (cramfs root now mounts through this
driver instead of physmap/mtd-rom — dropped from the bao1x config); writes
via the RRC 32-byte line-buffer sequence from xous rram.rs (data words to
the mapped address → CR=2|0xFC00 → 0x5200/0x9528 magics to the line →
CR=0xFC00), per-line with IRQs off, VexRiscv D$ flush (.word 0x500F) after.
Erase emulated (0xFF); only DT partitions exposed → boot chain + secrets
area (0x3DA000+) unreachable. New layout: rootfs @0x220000 (1.25MB slot),
**data @0x360000..0x3DA000 (488K writable)** — image builder limit updated.
Renode: stores to executable MappedMemory can't be intercepted, so the
platform splits the RRAM at 0x360000 — XIP part stays MappedMemory, the
writable window is Bao1xRramData (line-buffer semantics keyed off the
Bao1xRrc CR state). linux.robot: dd write + readback on the data partition
via the RRC path. Robot gotchas: Renode's keyword server parses any
`name=value` argument as named (escaping doesn't help — avoid '=' in
patterns) and Write Line To Uart's echo check breaks on wrapped lines
(waitForEcho=false for long commands).

## 2026-07-15 (cont.) — Phase 5d': JFFS2 /data GREEN; SDIO ruled out for Dabao

JFFS2 mounts on the RRAM "data" partition with full persistence
(write→umount→remount→readback in linux.robot). Two 2MiB-lessons:
- **CONFIG_JFFS2_ZLIB (default y) OOM-panics the kernel at boot** — the zlib
  deflate workspace alone is ~270K. Disabled via JFFS2_COMPRESSION_OPTIONS;
  rtime compression stays (nearly free).
- jffs2 refuses media with garbage and zero valid nodes ("cowardly
  refusing") — the robot erases the raw-test block back to 0xFF first.
Renode: data window now initializes to 0xFF (erased convention — zeros made
jffs2 GC grind for 30+ min through the slow peripheral path) and the Renode
board DT shrinks the data partition to 128K (mount scans every byte; full
488K stays on hardware). SDIO: the Dabao routes no SD slot (xous dabao
board file has no SD pins; SDDC is device-mode, UDMA SDIO is for baosec) —
SD support is out of scope for this board, revisit if hardware appears.

## 2026-07-15 (cont.) — Phase 6: series packaged for review (22 patches)

New final patch "riscv: add Baochip bao1x SoC and Dabao board support":
ARCH_BAOCHIP in Kconfig.socs, `arch/riscv/boot/dts/baochip/{bao1x.dtsi,
dabao.dts}` (now the canonical DT — repo keeps only bao1x-renode.dts, built
against the kernel copy via dtc -i / cpp), `baochip_dabao_defconfig`
(savedefconfig of the proven 2MiB config), CPU compatible registered in
riscv/cpus.yaml (dtbs_check clean), MAINTAINERS entry (maintainer identity
to be confirmed by the user before submission). Series rebased into review
order: 8 riscv XIP/mm fixes → gpiolib fix → binding+driver pairs (irqchip,
serial, pinctrl, i2c, spi, mtd) → SoC support. Full robot suite re-run
GREEN after the reorder; UF2 rebuilt. Remaining phase-6 item: forward-port
onto mainline HEAD (where XIP_KERNEL is being removed — the cover letter
argues this port as the XIP user); best done after hardware confirms the
port, alongside the Corigine USB UDC (the last phase-5 driver, which needs
real hardware to validate meaningfully).

## 2026-08-07 — Phase 4: first silicon. Image validates; shim silent

First execution of this port on real hardware. Board: Dabao, boot0/boot1
`v0.10.0-61-g5397e1b48` (`bao2-0`). Console = Raspberry Pi Debug Probe on
PB14/PB13 @ 1Mbaud (probe UART is the `-if01` interface; **orange = probe TX
→ PB13, yellow = probe RX → PB14**, black = GND — the probe's own labels are
the reverse of what one might assume).

### Confirmed on silicon (previously inferred from xous source)

- **CPU @ 350MHz** — printed by boot1 itself (`boot1 udma console up, CPU @
  350MHz!`). This is the DT `timebase-frequency` and the basis of the shim's
  `TIMER0_TICKS_MULT=2`. Board type reads back `Dabao`; `skipping check` =
  false (clock skipping would have wrecked timer calibration).
- **Signing/UF2/slot layout are correct**: boot1 accepted the image with
  `Booting with key 3/3(dev )`. `tools/build-dabao-image.sh` output needs no
  changes.
- **DEVELOPER_MODE burn is gated on a valid signature.** `secboot.rs` runs
  `validate_image` *before* `hardened_erase_policy`, so a corrupt image
  yields `Image did not validate` with the fuses untouched. Burn message:
  `Developer key detected, ensuring secrets are erased`.
- **The board recovers from resets indefinitely** — `bootwait` keeps boot1 in
  charge after the burn.

### The failure

boot1 validates, burns, jumps — and the shim emits nothing. Deterministic.

Eliminated by evidence, not argument:
- *I-cache staleness at the handoff* (`jump_to` does no `fence.i`, and the
  first attempt flashed and booted in one power cycle): booting the same
  image from a cold reset failed identically.
- *Wrong jump target*: the signature block's first word is `0x3000006f` =
  `jal x0, +0x300` → `0x60060300`, exactly the shim's ELF entry, and the
  bytes there match `bao1x-sbi.bin`.
- *Wrong register offsets*: UART2 base/TX offsets verified against
  `bao1x_peri.svd`.

**Prime suspect: `duart_puts()` spins forever.** It is the first statement in
`main()` and waits `while (DUART_SR & 1)` unbounded. `SFR_ETUC` (the DUART
baud divider, offset 0xc) has reset value 0 and the shim never programs it; a
zero divider means the shifter never drains, wedging on the second character.
The Dabao pinout exposes **no DUART pins at all**, so this is a wait on a
peripheral the board does not even bring out. Renode's DUART model completes
instantly, which is why nine phases of emulation never touched it. Second
candidate: the unbounded uDMA TX-completion wait in `uart2_tx()` (docs/05 had
already flagged "Renode DMA is instant" as untestable in emulation).

Fix built and Renode-regression-clean (not yet proven on hardware — the board
stopped responding before it could be flashed): every hardware poll bounded by
`SPIN_LIMIT`; uDMA TX enqueued as `CFG_EN | CFG_BACKPRESSURE` to match the
only sequence proven on this silicon (bao1x-hal `udma_enqueue`); stage markers
`SBI:entry` / `duart-ok` / `uart-init` / `dtb-copied` / `csr-done`, the first
emitted *before* `uart2_init()` so it rides boot1's known-good UART setup.

### Flashing without USB (the standard path for this board)

boot1's REPL reads `UART_RX` whenever USB is not `Configured`
(`boot1/src/main.rs:318`), so the whole bootloader is drivable over PB14/PB13
— which is also how the third-party dabao-sdk flashes (`bao_flash` over
serial). `sources/xous-core/bao1x-boot/uf2send.py` streams base64 UF2 blocks
into the `uf2` REPL command. USB is needed only for power.

**PROG = guaranteed safe-mode.** `get_key()` on Dabao samples a boot pin; low
→ `KeyPress::Select` → boot1 prints `Boot bypassed with keypress` and stays in
the REPL regardless of `bootwait`. Sequence: hold PROG, press+release RESET
(still holding), wait 1s, release PROG.

**`RST_N` is on the header** (`GPIO_PB1`, the Pico-form-factor RUN position,
physical pin 30). Wiring a DTR-capable adapter to it gives software-controlled
reset and removes the human from the iteration loop — worth doing.

### Host-side traps (cost real time)

- The dev host is a **QEMU VM** (Silverblue) with the board passed through,
  and Claude runs in a toolbox inside it. Device nodes arrive as
  `nobody:nobody 0660`; `setup/99-baochip.rules` (installed on the *host*,
  udev does not run in the toolbox) makes them 0666. Without it the node
  reverts on **every** board reset.
- **Device numbers are not stable.** When the board's USB drops, the Debug
  Probe renumbers (`ttyACM1`→`ttyACM0`). Always use
  `/dev/serial/by-id/...` paths.
- `pkill -f <pattern>` matching the wrapper shell kills the tool's own
  session (already in docs/03; it bit again here and lost a capture).
- Blind spot to avoid repeating: leaving no capture running across a
  user-performed replug means the event is unobservable afterwards.

### Open at end of session

After an unplug/replug the board went silent on **both** USB (absent from the
*physical host's* `lsusb`, so upstream of the VM) and the UART (no boot0
banner, which is the real anomaly — boot0 prints before any of boot1's logic).
Two independent paths failing together points at power/connection rather than
firmware; note also that our flashing can only ever touch the payload region,
since boot1 range-checks every UF2 write (`usb/handlers.rs:249`), and boot1
was reached cleanly twice *after* the burn. Next: loopback-test the probe path
(`tools/uart-loopback.py`) to rule our own measurement chain in or out, check
for a power LED, then try a different cable and a direct root-hub port.

## 2026-08-07 (cont.) — shim runs end-to-end on silicon; two more Renode blind spots

Hands-free loop established: an ESPHome-controlled GPIO drives RUN/RST_N
(header pin 30), so reset is a REST call — `tools/board-reset.py`. ESPHome
addresses entities by **display name**, URL-encoded (`GPIO%20Switch%2015`);
the slug from the SSE `id` field 404s, and POST needs an explicit
zero-length body or the device answers 411.

### Bug 1: `duart_puts()` wedged the shim (confirmed + fixed)

`SBI:duart-ok` now appears, so bounding the poll was the fix. The Dabao
schematic settles the root cause beyond inference: **there is no DUART net
anywhere on the board** (nor any LED, and only one USB-C, two buttons, one
EMS4000 are populated — the BOM is the truth, the schematic carries
alternates). `SFR_ETUC` reset value 0 ⇒ SFR_SR never clears.

### Bug 2: `mcounteren`/`scounteren` do not exist on this VexRiscv

The new M-mode trap reporter caught it exactly:

    SBI:FATAL M-mode trap mcause=0x00000002 mepc=0x600604f0 mtval=0x3063d073

`0x3063d073` decodes as `csrwi mcounteren, 7` (csr=0x306, funct3=101
CSRRWI, uimm=7) — illegal instruction. This was invisible before because
`entry.S` parks mscratch at 0 as its "in M-mode" marker, so `trap_entry`
handed the handler sp=0 and it died on its first store. **Fix: set mscratch
to the M-stack before touching any CSR**, and report M-mode traps.

Important wrong turn: simply deleting the two writes **broke Renode** —
Renode *does* implement counter access control, and without the write the
kernel's S-mode counter reads trap and boot wedges. Neither "always write"
nor "never write" is correct across both. The shim now *probes*:
`csr_write_probe()` sets a flag making an M-mode illegal instruction skip
(mepc += 4) and report absence. Silicon prints `SBI:mcounteren-absent`,
Renode takes the write. CSR instructions are never compressed, so mepc+4 is
always right.

Shim now completes: `SBI:csr-done` → `bao1x-sbi: jumping to kernel`.

### Bug 3 (same class): the kernel's DUART earlycon has the identical hang

`drivers/tty/serial/bao1x_duart.c` spun unbounded on `DUART_SR_BUSY`, and
its comment asserted the DUART "works with no clock or pin setup at all" —
which silicon disproves. Bounded to `DUART_TX_TIMEOUT_US` (10 ms) and the
comment corrected. Also switched `dabao.dts` to `earlycon=sbi` with
`stdout-path = &uart2`: the SBI console reaches UART2 through the shim, so
early and late output land on the same wire that this board actually routes.
Renode stays green through all of it.

**Lesson, now costing three bugs: every unbounded `while (STATUS & bit)` is
untested code until silicon runs it.** Renode's DUART drains instantly, its
DMA is synchronous, and it implements every CSR.

### Host-side: USB passthrough does not survive a reset

Each RST_N pulse makes the board detach and re-attach as a *new* USB device
(host device number climbed 30 → 62 → 65 across resets). A VM passthrough
bound to a bus/port address cannot follow that, so the guest loses mass
storage *and* the USB console on every reset, while the *physical host*
enumerates it fine. Worse, because the host reaches `Configured`, boot1 sets
USB_CONNECTED and **stops reading the UART REPL** — so the serial fallback is
unavailable too. Fix: pass through by vendor:product (`1d50:6196`) with
`startupPolicy='optional'` so the VM re-attaches automatically.

### Open

After the kernel started (21:25) the board stopped responding to RST_N
entirely — a verified ON→OFF→ON toggle with holds up to 3 s produced no boot0
banner, where identical pulses worked at 21:02 and 21:22 while the board sat
in boot1 or the hung shim. Distinguish "reset line not reaching the chip"
from "board unpowered" with the physical RESET button and a multimeter on
pin 36 (3V3) / pin 40 (VBUS) against GND. The kernel itself has not yet
printed anything; with `earlycon=sbi` now in place its first output should
arrive over the shim's DBCN on UART2, so the next boot is the real test.
