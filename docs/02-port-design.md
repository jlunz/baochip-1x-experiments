# Linux port design for bao1x / Dabao

This is the decided architecture (approved plan). Evidence for every hardware claim is in
`01-hardware-dossier.md`.

## Software stack

```
boot0 → boot1 (vendor, untouched)
  └─> dev-signed UF2 payload flashed at RRAM 0x6006_0000, entered in M-mode:
      ┌──────────────────────────────────────────────────────────────────┐
      │ bao1x-sbi — tiny M-mode SBI shim (target: 4–8 KiB XIP, ~4 KiB    │
      │ SRAM). Responsibilities:                                         │
      │  • sanity clock/uart init (inherit boot1 state)                  │
      │  • SBI console (DBCN + legacy putchar) → DUART TX                │
      │  • rdtime/rdtimeh emulation (illegal-insn trap) → TICKTIMER      │
      │  • SBI TIME set_timer → TICKTIMER alarm (or TIMER0) → inject     │
      │    mip.STIP into S-mode                                          │
      │  • SBI SRST → watchdog reset                                     │
      │  • medeleg/mideleg: delegate everything delegable to S           │
      │  • load DTB address into a1, hartid 0 in a0, mret into kernel    │
      └──────────────────────────────────────────────────────────────────┘
      ┌──────────────────────────────────────────────────────────────────┐
      │ Linux (RV32, Sv32, UP, CONFIG_XIP_KERNEL)                        │
      │  text: XIP from RRAM;  .data/.bss/stacks: SRAM @ 0x6100_0000     │
      │  New drivers (DT-probed, upstream style):                        │
      │   drivers/irqchip/irq-vexriscv-intc.c    CSRs 0x9C0/0xDC0, 32 l. │
      │   drivers/irqchip/irq-baochip-irqarray.c cascaded 20×16 banks    │
      │   drivers/clocksource/timer-litex.c      TIMER0 clockevent +     │
      │                                          TICKTIMER clocksource   │
      │   drivers/tty/serial/baochip-duart.c     earlycon + TX console   │
      │   drivers/tty/serial/baochip-udma-uart.c real console (IFRAM DMA)│
      └──────────────────────────────────────────────────────────────────┘
      ┌──────────────────────────────────────────────────────────────────┐
      │ rootfs: XIP cramfs in RRAM (CONFIG_CRAMFS with direct physical   │
      │ mapping) — busybox (static, rv32 musl, built by Buildroot).      │
      │ Userspace text pages map straight to RRAM; only data/stack/heap  │
      │ consume SRAM.                                                    │
      └──────────────────────────────────────────────────────────────────┘
```

## Key decisions and their rationale

| Decision | Rationale | Rejected alternative |
|---|---|---|
| S-mode MMU Linux | Sv32 proven by Xous; fork()/protection; mainline-friendly | NOMMU/M-mode Linux (mainline forbids M-mode+MMU; NOMMU is strictly worse here) |
| Tiny custom SBI shim | No CLINT → OpenSBI needs a custom platform anyway, and its generic build costs 50–200 KiB of our 2 MiB SRAM; the shim does the needed 10% in a few KiB | OpenSBI fw_payload (revisit if SBI surface grows) |
| Native S-mode timer driver (TIMER0 clockevent) | Timer irq arrives as ext-irq 30, maskable at S-level directly — no M-mode round trip per tick | SBI set_timer as the only clockevent path (kept as fallback; shim implements it regardless) |
| XIP kernel from RRAM | 2 MiB RAM cannot hold kernel text + userspace | Kernel-in-SRAM (doesn't fit); compression (must decompress *into* RAM) |
| XIP cramfs userspace | Same argument for userspace text | initramfs (eats SRAM for every byte of text) |
| Renode as primary emulator | Declarative platform, existing VexRiscv custom-CSR + Sv32 support, reusable xous C# models; fast iteration | QEMU custom machine (more work, C); Verilator (ground truth but ~1000× slower) |
| Kernel base = last-good-XIP tag, then forward-port | RISC-V XIP is currently broken upstream (since commit `a44fb5722199`) and queued for removal; fighting two wars at once on day 1 is dumb | Basing on HEAD immediately |

## Memory budget (working numbers, to be measured in phases 1/3)

| Item | Budget |
|---|---|
| Kernel .data + .bss + percpu | ≤ 400 KiB |
| Page tables, mem_map, slab floor | ≤ 300 KiB |
| Network stack | omitted entirely (no NIC) |
| Userspace (busybox sh data/stack/heap, VFS caches) | remainder ≈ 1.2 MiB |
| SBI shim resident | ≤ 8 KiB (top of SRAM, mPMP-less: protected only by not being mapped) |

Kernel config strategy: start from `tinyconfig`, add back: MMU, printk, serial, DT, cramfs,
proc/sysfs (evaluate), ELF binfmt. `CONFIG_SLUB_TINY`, no SMP, no modules (XIP), no swap,
`CONFIG_XIP_KERNEL=y`, `CONFIG_STRICT_KERNEL_RWX` interactions checked.

## RRAM flash plan (3.47 MiB payload window @ 0x6006_0000)

| Region | Size (target) | Contents |
|---|---|---|
| `0x6006_0000` | 64 KiB | sig block + bao1x-sbi + DTB |
| `0x6007_0000` | ~2.1 MiB | XIP kernel image (`CONFIG_XIP_PHYS_ADDR`) |
| tail | ~1.3 MiB | cramfs rootfs (XIP), 4 KiB-aligned |

Boundaries are provisional; the UF2 packer in `tools/` owns the layout and emits one image.

## Device tree sketch

```dts
/ {
  compatible = "baochip,dabao", "baochip,bao1x";
  cpus { cpu@0 { compatible = "baochip,bao1x-vexriscv", "riscv";
                 riscv,isa-base = "rv32imac"; mmu-type = "riscv,sv32";
                 timebase-frequency = <...>;   /* rdtime emu rate: 1 kHz or better */
                 interrupt-controller (riscv,cpu-intc); } }
  memory@61000000 { reg = <0x61000000 0x200000>; };
  soc {
    intc: interrupt-controller { compatible = "baochip,bao1x-intc";  /* CSR-based, 32 */ }
    irqarray@e0004000 { compatible = "baochip,bao1x-irqarray"; ... cascaded banks }
    timer@e001c000   { compatible = "litex,timer0"; interrupts-extended = <&intc 30>; }
    ticktimer@e001b000 { compatible = "baochip,bao1x-ticktimer"; /* clocksource */ }
    duart@40042000   { compatible = "baochip,bao1x-duart"; /* console, TX-only */ }
    uart@50101000    { compatible = "baochip,bao1x-udma-uart"; /* real console */ }
    flash@60000000   { compatible = "mtd-rom"; /* + RRC for writes, phase 5 */ }
  };
};
```

## Verification strategy

1. **Phase 1 (QEMU virt/rv32)**: proves XIP+rv32+cramfs-XIP+2 MiB generically. Exit: busybox
   shell, `free` output recorded.
2. **Phase 2 (Renode)**: platform fidelity tests — bare-metal ELF exercises DUART, TIMER0
   irq, IRQARRAY soft-triggered irqs (`EV_SOFT`!), CSR mask/pending semantics.
3. **Phase 3 (Renode)**: full boot to shell; `sleep 1` wall-clock sanity; irq counters.
4. **Phase 4 (hardware)**: same image + DUART earlycon; measure boot time, RAM, timer drift.
5. Regression: scripted Renode boot-to-shell run (`emulation/tests/`) kept green.

## Risk register

| # | Risk | Mitigation / resolution phase |
|---|------|-------------------------------|
| 1 | rv32+XIP needs real kernel fixes | Phase 1 exposes immediately; pin last-good tag; forward-port later |
| 2 | 2 MiB ceiling too tight for shell | Measured in phase 1/3; trim harder; last resort: header-wired SPI PSRAM as swap (baosec precedent) |
| 3 | VexRiscv MMU corner cases (A/D bits) | LiteX-Linux precedent on identical plugin; watch in phase 3 |
| 4 | Renode fidelity (custom CSRs + Sv32, uDMA) | betrusted precedent; Verilator as arbiter |
| 5 | RRAM XIP wait-states / write-while-execute | RTL + `xous:rram.rs`; phases 4–5 |
| 6 | boot1 payload size cap below ~3.4 MiB | Check `validate_image`; fallback: chainload trampoline |
| 7 | XIP removed upstream before phase 6 | Removal is revertible per maintainer; our port is the justifying user; series includes the revert |
