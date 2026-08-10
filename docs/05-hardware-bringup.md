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

## Host setup (do this first)

Install the udev rule **on the host** — udev does not run inside a toolbox,
and without it the device nodes come back `0660 root:dialout` (or
`nobody:nobody` in a container) after *every* board reset:

```sh
sudo cp setup/99-baochip.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
```

Always address serial ports by their stable path, never `ttyACMn`: when the
board's USB drops, everything else renumbers.

```sh
ls /dev/serial/by-id/          # boot1 console = ...Baochip-1x...-if00
                               # debug probe UART = ...Debug_Probe...-if01
```

## Flashing

Three routes; the serial one is the most robust and is how the board is
normally programmed (the third-party dabao-sdk flashes this way too).

1. **Over the console UART — no USB data needed** (only USB power).
   boot1's REPL reads the UART whenever USB is not `Configured`
   (`boot1/src/main.rs:318`), so this works even when enumeration is broken:
   ```sh
   PROBE=/dev/serial/by-id/usb-Raspberry_Pi_Debug_Probe...-if01
   python3 sources/xous-core/bao1x-boot/uf2send.py \
       build/dabao/dabao-linux.uf2 --port $PROBE
   ```
2. **Mass storage**: copy `build/dabao/dabao-linux.uf2` onto the `BAOCHIP`
   volume and `sync`. boot1 sniffs the sector writes and programs RRAM as
   they arrive; **success is silent** (the log line in `usb/handlers.rs` is
   commented out), so no console output does not mean it failed.
3. `uf2` REPL command directly (base64 blocks) — what uf2send.py drives.

`bootwait` ships enabled, so boot1 does **not** auto-boot: issue `boot` on the
REPL afterwards. Note `boot` asserts the USB SE0 pin and leaves it asserted
for the next stage, so boot1's USB console dies at handoff — from that moment
the PB14/PB13 UART is the only channel (boot1's own messages fall back to it
automatically, see below).

## Recovery / safe-mode

- **PROG button = guaranteed boot1 REPL**, regardless of `bootwait`:
  hold PROG, press+release RESET (keep holding PROG), wait 1 s, release PROG.
  boot1 prints `Boot bypassed with keypress`.
- **`RST_N` is on the header** (`GPIO_PB1`, Pico-form-factor RUN, physical
  pin 30). Wire a DTR-capable USB-serial adapter to it for software-controlled
  reset and hands-free iteration.
- Flashing can never brick the bootloader: boot1 range-checks every UF2 write
  to the payload region (`usb/handlers.rs:249`).
- The DEVELOPER_MODE burn is gated on a **valid signature** — `secboot.rs`
  runs `validate_image` before `hardened_erase_policy`, so a bad image gives
  `Image did not validate` with the fuses untouched.

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
| No shim banner on UART2 | boot1 didn't jump / shim crashed pre-console | check the `SBI:*` stage markers (below); verify DEVELOPER_MODE burn message |
| Nothing at all on the UART, no boot0 banner | the chip is not executing — this is *upstream of firmware* | loopback-test the probe first (`tools/uart-loopback.py`), then power: different cable, direct root-hub port (not a hub chain) |
| Board absent from the **host's** `lsusb` | not a VM/passthrough issue | power/cable; note a running payload with no USB driver also presents nothing |
| Shim banner, then silence | kernel didn't reach ttyBAO0 registration | switch bootargs to `earlycon=sbi console=hvc0` (pure shim console, no native drivers) and compare |
| Boots but `sleep 1` is wrong length | TIMER0_TICKS_MULT / timebase wrong | calibrate: time 100 sleeps against wall clock; adjust `firmware/bao1x-sbi/bao1x.h` (dabao: mcycle@350MHz, TIMER0@700MHz assumed) and/or DT timebase-frequency |
| Console garbled | UART divider vs real perclk | try divider 87..100 in bao1x.h `UART2_CLKDIV` (perclk is ~99.8MHz if boot1 defaults hold) |

## Shim stage markers

The shim emits these on UART2 as it advances; the last one seen pins down
where it stopped. `SBI:entry` is deliberately sent *before* `uart2_init()`, so
it rides the UART setup boot1 leaves behind (proven working — boot1 printed
through it moments earlier).

| Marker | Reached |
|---|---|
| `SBI:entry` | shim entered, boot1's UART config usable |
| `SBI:duart-ok` | survived `duart_puts()` (see the DUART hazard below) |
| `SBI:uart-init` | UART2 reconfigured by the shim |
| `SBI:dtb-copied` | DTB relocated to `DTB_DEST` |
| `SBI:csr-done` | delegation + S-mode CSRs set |
| `bao1x-sbi: jumping to kernel` | about to `mret` into the kernel |

**DUART hazard.** The Dabao pinout exposes no DUART pins, and `SFR_ETUC`
(baud divider, offset 0xc) has reset value 0 — with a zero divider `SFR_SR`
never clears, so any unbounded `while (DUART_SR & 1)` wedges the boot on a
peripheral the board does not even bring out. Every hardware poll in the shim
is now bounded by `SPIN_LIMIT`. Renode's DUART model completes instantly, so
emulation cannot catch this class of bug: **treat every new busy-wait as
untested until silicon says otherwise.**

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
