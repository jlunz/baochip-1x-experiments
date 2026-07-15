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
