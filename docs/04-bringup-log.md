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
