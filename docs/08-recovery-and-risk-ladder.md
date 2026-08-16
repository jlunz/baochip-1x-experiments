# Recovery, and a risk-ordered way back onto hardware

Written after the 2026-08-07 session ended with an unresponsive board
(`07-board-incident-2026-08-07.md`). Two things go wrong in a bring-up like
this: you damage the board, or you lose the ability to tell what happened. The
first session managed the second. This document is the ordering that keeps both
from recurring, plus what changed in the tree to make each rung survivable.

Read `## Rung 0` before touching anything, including the spare.

---

## What is and is not recoverable on this silicon

Established by reading `sources/xous-core/bao1x-boot` and `libs/bao1x-hal`;
file references are to that tree.

**Hard limits — nothing in software gets past these.**

- **boot0 is immutable.** It is burned at the OSAT and sealed with IFR bits.
  `blobs/README.md` records that `ifr_0x280.bin` locks out write access to the
  boot0 partition *from user code*, so a running payload cannot corrupt it —
  but equally, nothing can repair it.
- **JTAG is fused off.** `boot1/src/secboot.rs:43` reads the IFR block at
  `0x60400180` and compares it against a reference value that encodes "Cortex-M7
  disabled, hardware JTAG debug disabled". There is no debug-port rescue on a
  production part.
- **One-way counters only count up.** Board type, bootwait, USB speed, paranoid
  mode, alt-boot and every revocation are OWC-encoded. Some wrap around a
  modulus and can be walked back to a value; PARANOID_MODE and the revocations
  cannot be undone in any useful sense.
- **DEVELOPER_MODE is a one-time transition.** Already burnt on the incident
  board. It does not prevent booting; it makes boot0 and boot1 run
  `erase_secrets` on every boot.

**Everything else is recoverable, because the vendor design is deliberately
hard to brick.**

- `bootwait` is enabled on these units, so boot1 never auto-boots the payload.
- **PROG + RESET forces the REPL regardless of bootwait** — hold PROG, press and
  release RESET while still holding PROG, wait 1 s, release. boot1 prints
  `Boot bypassed with keypress`.
- **boot1's REPL reads the UART whenever USB is not `Configured`**
  (`boot1/src/main.rs:326`). Broken USB does not block reflashing;
  `uf2send.py` over the probe UART is a complete recovery path.
- boot1 range-checks every UF2 write to the payload region
  (`usb/handlers.rs:249`), so flashing cannot damage the bootloader.

**The one trap in that design.** If boot1 ever fails to validate, boot0 falls
back to the LOADER/BAREMETAL region (`boot0/src/main.rs:325`) — which on this
board is *our* image. A hanging payload would then run with no REPL and no
bootwait. `AltBootCoding` selects that order deliberately; see rung 6.

## Why a silent board is not proof of damage

`libs/bao1x-hal/src/sigcheck.rs:892` — `die_no_std()` zeroizes the backup
registers, always-on RAM, key regions, SCE memory, IFRAM, **the USB device
controller's memory**, the BIO memory and all 2 MiB of SRAM; then emits 256
`'X'` characters **on the DUART at 0x40042000**; then hangs in an unrolled jump
loop. The Dabao routes no DUART net. A security abort on this board is
therefore completely silent and survives every reset, because the next boot
reaches the same check and dies the same way.

An aborting chip and an unpowered chip produce identical evidence over USB and
UART. Distinguishing them needs a multimeter, which is why rung 0 insists on
one.

Triggers that fire *before* boot0's console comes up (`boot0` switches output
from the DUART to UART2 at `platform/bao1x/bao1x.rs:227`) include the TRNG
stuck-value health check, the SHA-512 known-answer test, PLL bring-up in
`init_clock_asic_350mhz()`, and paranoid-mode voltage sensors. Several are
power- and clock-integrity checks: **a marginal supply trips them
deterministically and invisibly**, which fits a board whose USB had already
degraded to full-speed hours before it went quiet.

---

## The ladder

Each rung states what it proves, what it costs when it goes wrong, and what it
makes irreversible. Do not skip a rung to save time; every one of them exists
because the previous session's failure would have been caught there.

### Rung 0 — Triage. No writes. Zero risk.

**Do this on the *spare* first, while it is known-good**, and keep the output.
The incident is hard to interpret partly because no capture of a healthy board
exists to compare against.

```sh
tools/board-triage.py --port /dev/serial/by-id/usb-Raspberry_Pi_Debug_Probe...-if01
```

It reports USB presence and negotiated speed, resets the board with a capture
already running, and looks for the boot0/boot1 banners. It writes nothing.

Also, in this order:

1. **Full unplug ≥10 s, a different known-good USB-C data cable, a direct root
   port** — not a hub. Both previous recoveries were physical replugs. The SE0
   switch is powered from VBUS, so `RST_N` cannot clear it.
2. **Multimeter**: GND (pin 13) → VBUS (pin 40) ≈5 V, → 3V3 (pin 36) ≈3.3 V.
   The board has no LED; this is the only way to know it is powered. Absent or
   low 3V3 ends the investigation — the fault is power, not firmware.
3. **Verify the console link *to the board***. `tools/uart-loopback.py` shorts
   probe TX to probe RX: it proves the probe, its cable and the host software,
   and says nothing about the board's PB14 path. Re-check continuity at header
   pins 15/16.
4. If nothing arrives at 1 Mbaud, `tools/baud-scan.py` — a mis-started PLL moves
   the bit rate without stopping the CPU.

**Pass:** boot0 and boot1 banners after a reset. **Then and only then** move on.

### Rung 1 — boot1 REPL, read-only commands. No writes. Near-zero risk.

PROG + RESET into the REPL, then use the *query* forms only:

```
help
audit                 # dumps identity and configuration
usb_speed             # bare: prints the current setting
bootwait              # bare: prints the current setting
```

**The bare form reads; the form with an argument increments a one-way counter.**
`boot1/src/repl.rs:1096` shows `usb_speed full` looping `inc_coded` until the
modulus matches. Do not pass arguments here.

**Proves:** the chip executes, the REPL is reachable, and the flash path is
open — which is the whole recovery story. If the board reaches this rung it is
not bricked, whatever else is wrong.

### Rung 2 — Flash the shim-only image, do not boot it. Reversible.

```sh
SHIM_ONLY=1 tools/build-dabao-image.sh
python3 sources/xous-core/bao1x-boot/uf2send.py \
    build/dabao/dabao-shim-only.uf2 --port $PROBE
```

`SHIM_ONLY=1` builds a payload containing **only** the shim — no kernel, no
rootfs. Two reasons that matters: it is quick to sign and verify, and it leaves
nothing in the payload region for boot0's fallback path to land on.

Writing the payload region is reversible: write it again. **No fuse is burnt
until the image is actually booted.** boot1 validates the signature *before*
`hardened_erase_policy` runs (`secboot.rs`), so a rejected image leaves the
fuses untouched.

**Pass:** boot1 accepts the image. Record the md5 the build prints, against the
boot it produces — the incident timeline is only reconstructable because those
were recorded.

### Rung 3 — First boot of a dev-signed image. **Irreversible: burns DEVELOPER_MODE.**

On a fresh spare this is the one-way step. It affects only that unit's ability
to run factory-signed secure workloads; normal development is unaffected. It
cannot be undone, and once the developer key is revoked it can never be
re-entered.

Issue `boot` on the REPL. Expected on UART2 at 1 Mbaud:

```
Booting with key 3/3(dev )
Developer key detected, ensuring secrets are erased
SBI:entry / SBI:duart-ok / SBI:uart-init
SBI:se0-released
SBI:dtb-copied / SBI:medeleg ... SBI:mcounteren-absent / SBI:csr-done
SBI:shim-only -- not entering kernel
SBI:alive                          <- once a second, forever
```

**Proves, without ever executing Linux:** signing, UF2 packaging, slot layout,
the boot1→shim handover, every bounded busy-wait, the CSR probe, the console,
and the new SE0 release. A board in this state is always recoverable: nothing
here writes RRAM, and PROG+RESET returns to the REPL.

**If `SBI:se0-released` appears and USB comes back after a plain reset** — no
physical replug — the SE0 fix is confirmed, and the single most annoying
failure mode of the previous session is gone.

### Rung 4 — Kernel to a shell, read-only rootfs. Hangs are recoverable.

```sh
tools/build-dabao-image.sh          # full image
```

The kernel now takes `earlycon=sbi` with `stdout-path = &uart2`, so its first
instruction prints through the shim onto the wire this board actually routes.
The previous image used `earlycon` on the DUART, whose transmit poll was
unbounded — that is what silently parked the CPU on boot #5.

Root is XIP cramfs, mounted **read-only**. Nothing writes RRAM at this rung.

**Recovery from a hang:** PROG + RESET. That keeps working as long as bootwait
stays enabled, which is why rung 6 exists and is last.

**Abort if:** the kernel prints nothing at all. That means early output is
still not reaching the wire; go back and prove the console at rung 3 rather
than iterating on kernel builds blind.

### Rung 5 — Writable RRAM: JFFS2 on /data. **Highest risk of permanent damage.**

This is the first time Linux *writes* the array the boot chain lives in.

Mitigation now in the tree: `bao1x-rram.c` refuses any write or erase below
offset `0x60000` — the immutable first stage and the signed second stage —
returning `-EROFS` and logging the offending offset, **without consulting the
partition table**. A device tree is not a safe place to keep the only copy of
that constraint, and the driver maps the whole 4 MiB array.

Before mounting anything, prove the write path by hand at a known-safe offset
inside the data partition, and prove the guard by aiming a write at offset 0
and confirming it is refused. Only then mount JFFS2.

**If this rung goes wrong on the real chip anyway, the board is gone** — that is
the whole reason it is last, and the reason to do it on the spare.

### Rung 6 — One-way counters. Irreversible, and some are traps.

| Command | Effect | Verdict |
|---|---|---|
| `usb_speed high` | forces high-speed enumeration | safe, use if triage found the OWC set to `full` |
| `bootwait` (arg form) | toggles auto-boot | **disabling removes your automatic recovery window**; PROG still works, but do not disable while the payload can hang |
| `altboot` | makes boot0 prefer the payload region over boot1 | **never on a board you care about.** boot0 jumps straight into the payload — a hanging payload then leaves no REPL and no bootwait |
| `paranoid` | aggressive glitch detectors, hardware auto-reset | **never.** False positives are documented by the vendor, and in paranoid mode `apply_attack_policy` wipes secrets and dies once `POSSIBLE_ATTACKS` passes a threshold |

### Never

`self_destruct`, `publock`, `lockdown`, `rand_collateral`, and any write to the
IFR region. These are vendor test and lifecycle commands; they do what their
names say.

---

## What changed in the tree for this

Three fixes described in `04-bringup-log.md` as done were never in the repo:
they were made in `sources/linux` and `build/`, both gitignored, and vanished
with the container. The committed tree was byte-identical to the image that
was on the board when it stopped responding.

| Change | Where | Rung it protects |
|---|---|---|
| Shim releases the USB SE0 pin (PC13) before handover | `firmware/bao1x-sbi/board.c` | 3 — USB survives a hang; no physical replug needed |
| `SHIM_ONLY=1` build: run every stage, park in a heartbeat, never enter Linux | `firmware/bao1x-sbi/main.c`, `Makefile`, `tools/build-dabao-image.sh` | 3 — proves the handover with no kernel present |
| Kernel DUART earlycon poll bounded to 10 ms | `linux/patches/v6.14/0013` | 4 — the bug that hung boot #5 |
| `earlycon=sbi`, `stdout-path = &uart2` | `linux/patches/v6.14/0022` | 4 — early output lands on the routed wire |
| RRAM driver refuses writes below the boot chain | `linux/patches/v6.14/0021` | 5 — a wrong offset can no longer be permanent |
| `tools/board-triage.py` | new | 0 |
| `tools/refresh-patches.sh`, wired into the image build | new | all — kernel fixes can no longer be lost in a gitignored tree |

The DUART binding text was corrected too: it claimed the device "needs no clock
or pin configuration", which silicon disproved — the divider resets to zero and
the pad need not be routed at all.

## Standing rules earned the hard way

- **Every unbounded `while (STATUS & bit)` is untested code until silicon runs
  it.** Renode drains the DUART instantly, completes DMA synchronously and
  implements every CSR. This has now cost three bugs and one hardware session.
- **Keep a capture running across every reset and replug.** An event nobody was
  recording is unobservable afterwards.
- **Record the image md5 against the boot it produced.** The build script prints
  it for this reason.
- **Anything fixed only in `sources/linux` is not fixed.** Run
  `tools/refresh-patches.sh`; the image build refuses to proceed otherwise.
