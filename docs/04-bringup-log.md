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
