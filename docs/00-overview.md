# Project overview: mainline Linux on Baochip-1x (Dabao)

## Goal

Run mainline Linux on the Baochip-1x ("bao1x") SoC on the Dabao evaluation board, and keep
every kernel-side change organized as an upstream-quality patch series (submission to
mailing lists deliberately deferred).

## Why it's plausible at all

The bao1x is a microcontroller-class chip, but an unusual one:

- Its VexRiscv core was generated with `CsrPluginConfig.linuxFull` — full M/S/U privilege
  modes — and a hardware-refilled **Sv32 MMU**. The vendor OS (Xous) already runs in S-mode
  with paging on this silicon, so the Linux-critical CPU features are proven.
- 4 MiB of memory-mapped, execute-in-place RRAM means the kernel text never has to occupy
  RAM (`CONFIG_XIP_KERNEL`), and userspace text can be XIP'd too (cramfs direct mapping).
  That is what makes 2 MiB of SRAM survivable.
- The interrupt scheme (VexRiscv `ExternalInterruptArrayPlugin` + LiteX event banks) is the
  same family used by linux-on-litex-vexriscv, for which working driver patterns exist.

## The hard constraints (see 01-hardware-dossier.md for evidence)

| Constraint | Consequence |
|---|---|
| 2 MiB SRAM is the only RAM | XIP kernel + XIP userspace mandatory; tinyconfig discipline |
| No CLINT/PLIC; MTIP/MSIP tied off | Custom irqchip drivers via VexRiscv CSRs; timer is a memory-mapped LiteX timer; SBI shim injects STIP |
| No `time` CSR | rdtime emulated in M-mode shim (TICKTIMER-backed) |
| Boot only via signed images through boot1 | Reuse dev key + UF2 tooling from xous-core |
| RISC-V XIP_KERNEL queued for removal upstream | Base on last-good tag, then forward-port fixes; this port is the "real user" that justifies keeping XIP |

## Phases

0. **Repo/docs/environment** — this commit series.
1. **Generic feasibility (QEMU rv32 virt)** — prove rv32 + XIP + cramfs-XIP + 2 MiB
   before writing any bao1x-specific code.
2. **Renode platform** — `bao1x.repl` + peripheral models; fast dev loop pre-hardware.
3. **SBI shim + Linux in Renode** — the core port: shim, DT, irqchip ×2, timer, serial.
4. **Hardware bring-up** — UF2 flash via boot1, console on PB14/PB13 @ 1 Mbaud.
5. **Drivers** — IOX pinctrl/GPIO, UDMA I2C/SPI, RRAM MTD + SD, Corigine USB gadget.
6. **Upstream-ready series** — rebased on mainline HEAD, checkpatch/dt-schema clean.

The full approved plan (including risk register) is mirrored in `docs/02-port-design.md`.

## Ground rules

- Everything installed on the host has a corresponding line in `setup/install-tools.sh`.
- Every experiment and measurement goes in `docs/04-bringup-log.md` (lab notebook).
- One logical step per git commit.
- Upstream trees live in `sources/` (gitignored); our changes to them live in `linux/patches/`
  et al. as `format-patch` series so the repo stays small and the provenance obvious.

## Primary references

- Chip docs: https://baochip.github.io/baochip-1x/ ("Coder's guide to the Baochip 1x")
- RTL (source of truth): https://github.com/baochip/baochip-1x
- Vendor OS + HAL + tooling: https://github.com/betrusted-io/xous-core
- Board: https://www.crowdsupply.com/baochip/dabao
- Out-of-tree app template (packaging/signing reference): https://github.com/bunnie/dabao-console
- linux-on-litex-vexriscv (driver prior art): https://github.com/litex-hub/linux-on-litex-vexriscv
