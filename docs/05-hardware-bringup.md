# Dabao hardware bring-up guide (phase 4)

Everything below was prepared and validated in emulation on 2026-07-15; the
flash-ready artifact is reproducible with:

```sh
emulation/qemu/build-rootfs.sh          # rootfs (once)
emulation/renode/build-phase3.sh        # kernel + Renode validation build
tools/build-dabao-image.sh              # dabao DTB + shim + sign + UF2
```

Output: `build/dabao/dabao-linux.uf2` (UF2, base 0x60060000) and
`build/dabao/flash.bin` (raw signed image).

## ⚠ One-time irreversible step

The image is signed with the **public developer key** (xous-core
`devkey/dev.key`, `--function-code baremetal`). The first time boot1 runs any
developer-signed payload it **permanently burns DEVELOPER_MODE into the chip
and erases the factory-provisioned secrets**. This was approved for this
board on 2026-07-14. It affects only this unit's ability to run
factory-signed secure workloads; normal development is unaffected.

## What to connect

1. **USB-C to the host** — boot1 enumerates as a mass-storage volume named
   `BAOCHIP` (hold the update/BOOT button if the previous payload
   auto-boots; consult the Dabao quickstart).
2. **USB-UART adapter @ 1,000,000 baud 8n1** on the header:
   - board **PB14 = TX** (chip → adapter RX)
   - board **PB13 = RX** (chip ← adapter TX)
   - GND to GND. Pins are configured by boot1 (IOX AF1); Linux reuses them.

## Flashing

Either:
- copy `build/dabao/dabao-linux.uf2` onto the `BAOCHIP` volume and eject; or
- stream it over the boot1 serial console:
  `python3 sources/xous-core/bao1x-boot/uf2send.py build/dabao/dabao-linux.uf2 --port /dev/ttyUSB0`

Then reset the board.

## Expected console output (UART2, 1 Mbaud)

```
bao1x-sbi: jumping to kernel        <- shim (also on DUART, if routed)
...                                 <- printk backlog once ttyBAO0 registers
dabao login:                        <- root, empty password
```

The kernel banner and earlycon lines go to the **DUART** (TX-only debug pad;
may not be routed anywhere accessible) — the UART2 console starts printing
at `console [ttyBAO0] enabled`, which includes the buffered boot log
(CON_PRINTBUFFER), so nothing is lost even without DUART access. If the boot
hangs before that point, rebuild with `earlycon=sbi` in
`linux/dts/baochip/dabao.dts` bootargs: the SBI DBCN earlycon prints through
the shim onto UART2 from the first kernel instruction.

## Triage matrix

| Symptom | Meaning | Next step |
|---|---|---|
| boot1 rejects/ignores image | signature/format | check boot1 console output over its USB console; re-run `tools/build-dabao-image.sh` (it self-checks slots + UF2) |
| No shim banner on UART2 | boot1 didn't jump / shim crashed pre-console | verify DEVELOPER_MODE burn message on boot1 console; DUART pad if reachable |
| Shim banner, then silence | kernel didn't reach ttyBAO0 registration | switch bootargs to `earlycon=sbi console=hvc0` (pure shim console, no native drivers) and compare |
| Boots but `sleep 1` is wrong length | TIMER0_TICKS_MULT / timebase wrong | calibrate: time 100 sleeps against wall clock; adjust `firmware/bao1x-sbi/bao1x.h` (dabao: mcycle@350MHz, TIMER0@700MHz assumed) and/or DT timebase-frequency |
| Console garbled | UART divider vs real perclk | try divider 87..100 in bao1x.h `UART2_CLKDIV` (perclk is ~99.8MHz if boot1 defaults hold) |

## First-exercise-on-silicon list (untestable in Renode)

- **rdtime emulation** (illegal-instruction trap → mcycle): Renode's CPU
  implements rdtime natively, so the shim path runs for the first time on
  hardware — including from user mode (vDSO clock_gettime).
- mtval-on-illegal semantics (shim falls back to an MPRV=1 fetch of the
  opcode if mtval reads 0).
- TIMER0_TICKS_MULT=2 scaling (fclk=2×CPU clock assumption).
- `bao1x_uart` tx_empty vs. real shifter drain (Renode DMA is instant).
- RRAM XIP wait states / real boot speed; ed25519 verification time of the
  2.7MB image in boot1.
