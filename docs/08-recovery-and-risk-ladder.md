# Recovery, and a risk-ordered way back onto hardware

Written after the 2026-08-07 session ended with an unresponsive board
(`07-board-incident-2026-08-07.md`). Two things go wrong in a bring-up like
this: you damage the board, or you lose the ability to tell what happened. The
first session managed the second. This document is the ordering that keeps both
from recurring, plus what changed in the tree to make each rung survivable.

Read `## Rung 0` before touching anything, including the spare.

This document is the risk *ordering* — what each step costs and what it makes
irreversible. `09-new-board-bringup-plan.md` is the executable procedure built
on it: the host-side pre-flight, a step-by-step run sheet, and the board
allocation to decide first.

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
  release RESET while still holding PROG, wait 1 s, release. Which banner
  boot1 prints depends on that same bootwait state (`main.rs:190–194`, and the
  two are mutually exclusive): `Boot bypassed because bootwait was enabled` on
  these units (bootwait enabled, previous line), `Boot bypassed with
  keypress: …` only on a unit with bootwait disabled. Reaching the prompt is
  the actual guarantee; don't wait on a specific banner text. **One
  exception:** `main.rs:184` short-circuits
  to `try_boot()` when the `warm_boot` backup flag is set, *ahead of* both the
  bootwait check and the keypress check. The flag is OS-managed, nothing in this
  port writes it, and `AORSTn` clears it by hardware design
  (`bao1x-api/src/lib.rs:73–77`) — but a payload that sets it and then hangs has
  removed the recovery window entirely. **Never set `warm_boot` from the
  payload.**
- **boot1's REPL reads the UART whenever USB is not `Configured`**
  (`boot1/src/main.rs:326`). Broken USB does not block reflashing;
  `uf2send.py` over the probe UART is a complete recovery path. The converse
  holds too and is easy to trip over: the guard is `USB_CONNECTED`
  (`main.rs:304`), which latches once USB reaches `Configured` and prints
  `Console moved to USB serial` — after that line the probe UART is no longer
  read, and the CDC console is where the REPL lives.
- boot1 range-checks every UF2 write to the payload region, so flashing cannot
  damage the bootloader. The two flash paths use different bounds: the REPL
  `uf2` command that `uf2send.py` drives is half-open, `BAREMETAL_START` up to
  but not including `HW_RERAM_MEM + RRAM_STORAGE_LEN` (`repl.rs:182–184`), while
  the USB mass-storage handler is inclusive at the top (`usb/handlers.rs:249`).
  The one-way counter array starts at exactly that top bound (`acram.rs:20`), so
  the UART path cannot address it at all. See `09` § E.

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

### Rung 1 — boot1 REPL, query commands. No persistent writes. Near-zero risk.

PROG + RESET into the REPL, then use the *query* forms only:

```
help
audit                 # the important one -- see below
usb_speed             # bare: prints the current setting
bootwait check        # 'check', not bare: the bare form is a help error
```

**Which form reads is per-command; check before typing.** `usb_speed` bare
reads (`repl.rs:1098`) and `usb_speed full` loops `inc_coded` until the modulus
matches (`repl.rs:1117`). `bootwait`, though, requires exactly one argument and
returns a help error otherwise (`repl.rs:456`), so the read is `bootwait check`
(`repl.rs:472`) and `toggle`/`enable`/`disable` are the counter writers. Never
guess the form: guessing wrong on a command that takes an argument is a
one-way counter write.

**`audit` is the one to capture, and it does more than dump identity.** It
prints, alongside board type, stepping, serial and UUID:

| Line | Slot | Why it matters |
|---|---|---|
| `Paranoid mode: <a>/<b>` | 65 / 67 | `PARANOID_MODE` / `PARANOID_MODE_DUPE`. boot0 compares these for exact equality at `bao1x.rs:62` **before any console exists**; `a != b` is a permanent, silent, pre-console brick on the next boot |
| `Possible attack attempts: <n>` | 66 | `POSSIBLE_ATTACKS`. Rising means the glitch detectors are firing |
| `First-try boot partition is:` | — | `AltBootCoding`; must stay on boot1 (rung 6) |
| `Revocations:` table | 116/120/124 | the **main** array only — `audit.rs:101` says so, so the dupes at 68/72/76 are never printed. Any `enabled` → `revoked` transition is unrecoverable; a slot already reading `revoked` in the baseline is not one |
| `PQ required: <a>/<b>` | 19 / 44 | `REQUIRE_PQ` / `REQUIRE_PQ_DUPE`, enforced for every stage at `sigcheck.rs:312`. Must stay `0/0` — our images carry no PQ signature |
| `Boot0:` / `Boot1:` / `Next stage:` | — | whether each stage validates — including, after rung 2, the payload just flashed, **without booting it** |

The three slots the incident document asks the vendor to read off the dead die —
65, 66, 67 — are defined at `bao1x-api/src/offsets/common.rs:182,188,191`. On a
live board they are one command away. **Capture this output before anything else and re-run it
after every rung**; a differential move on 65/67 is then caught while the board
still boots, instead of on the reset that never comes back — **but only when
no reset separates the rung from the `audit` that reads it.** boot0 itself
compares 65 and 67 for equality (`bao1x.rs:62`), *before any console exists*,
on every reset — so wherever a rung is followed by "PROG + RESET, then
`audit`" (rungs 3 onward), a desync is caught by a silent brick on that reset,
not by the diff. The diff only truly catches 65/67 in place across a rung that
does not reset the board in between; see `09` § `## The tripwire: audit` for
the detail.

Two precisions, so this is not recorded as more than it is. `audit` is not
bit-for-bit read-only: `detect_stepping()` (`audit.rs:46`) writes the RRC
security-mode register to test whether bit 12 is clearable, then restores it —
a volatile peripheral CSR, no fuse, no RRAM, no counter, and the vendor's own
auto-audit path runs it unprompted on the first three boots of every chip. And
the REPL's `audit` calls `audit()` directly; only boot1's automatic
`early_audit()` increments `EARLY_BOOT_COUNT`, which happens on every boot1
start — power-up or reset alike — regardless.

**Proves:** the chip executes, the REPL is reachable, and the flash path is
open — which is the whole recovery story. If the board reaches this rung it is
not bricked, whatever else is wrong.

**Gate:** banners, REPL reachable, `Paranoid mode` with both halves equal, and
no unexpected revocation.

### Rung 2 — Flash the shim-only image, do not boot it. Reversible.

```sh
SHIM_ONLY=1 tools/build-dabao-image.sh
python3 sources/xous-core/bao1x-boot/uf2send.py \
    build/dabao/dabao-shim-only.uf2 --port $PROBE
```

`SHIM_ONLY=1` builds a payload containing **only** the shim — no kernel, no
rootfs. It is quick to sign and verify, and the signature block it gets covers
only the shim, so if boot1 is ever rejected and boot0 falls back to this region
it lands on the shim and parks rather than on a kernel.

It does **not** erase a previously flashed kernel. boot1 programs only the
blocks a UF2 actually carries and never erases the region
(`boot1/.../usb/handlers.rs`), so old bytes stay at 0x60070000 — they are
simply never reached, because this shim contains no jump to them.

Writing the payload region is reversible: write it again. **No fuse is burnt
until the image is actually booted.** boot1 validates the signature *before*
`hardened_erase_policy` runs (`secboot.rs`), so a rejected image leaves the
fuses untouched.

**Pass:** boot1 accepts the image, and `audit` then reports `Next stage: key
3/3 (dev ) -> …`. That is the cheapest check in the whole ladder: it proves the
image landed and validates with no fuse touched, because boot1 validates
*before* `hardened_erase_policy` runs. `Next stage did not validate` here means
reflash, not `boot`. Record the md5 the build prints, against the boot it
produces — the incident timeline is only reconstructable because those were
recorded.

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
and the new SE0 release. A board in this state is always recoverable —
PROG+RESET returns to the REPL — but "recoverable" is not "RRAM-clean": this
is the rung's whole point. `boot` erases the key slots (first dev-signed boot
only) and increments `DEVELOPER_MODE` (every dev-signed boot, saturating
around the sixth); see `09` § F for the exact accounting. What PROG+RESET
recovers is *access*, not the fuse state.

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

**Recovery from a hang:** PROG + RESET. The keypress path does not depend on
bootwait — what bootwait buys is the *automatic* window, with no key pressed at
all. What removes the recovery entirely is `altboot` or a payload that sets
`warm_boot`, which is why rung 6 exists and is last.

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
inside the data partition. Then mount JFFS2.

**What the guard does and does not cover.** It catches a *mis-specified
partition table* — the partition layer translates partition-relative offsets
into whole-device offsets before the driver sees them, so a writable partition
wrongly placed over the boot chain hits the check. It is not reachable from
userspace with the current device tree: `CONFIG_MTD_PARTITIONED_MASTER` is not
set, so the master device is never exposed and the lowest partition starts at
0x220000. Writing offset 0 of a partition therefore proves nothing — offset 0
of `data` lands at 0x360000 and succeeds, offset 0 of `rootfs` is refused by
its read-only flag and never reaches the check. Do not record that as the guard
being verified. Exercise it in Renode with a deliberately bad partition offset
if you want it proven.

**If this rung goes wrong on the real chip anyway, the board is gone** — that is
the whole reason it is last, and the reason to do it on the spare.

### Rung 6 — One-way counters. Irreversible, and some are traps.

| Command | Effect | Verdict |
|---|---|---|
| `usb_speed high` | forces high-speed enumeration | the safest of these, but still a counter write — only if the REPL's bare `usb_speed` actually reports `Full`. A full-speed device that never answers is almost always the physical layer instead |
| `bootwait` (arg form) | toggles auto-boot | **disabling removes your automatic recovery window**; PROG still works, but do not disable while the payload can hang |
| `altboot` | makes boot0 prefer the payload region over boot1 | **never on a board you care about.** boot0 jumps straight into the payload — a hanging payload then leaves no REPL and no bootwait |
| `paranoid` | aggressive glitch detectors, hardware auto-reset | **never.** False positives are documented by the vendor, and in paranoid mode `apply_attack_policy` wipes secrets and dies once `POSSIBLE_ATTACKS` passes a threshold |

### Never

`require-pq`, `self_destruct`, `publock`, `lockdown`, `rand_collateral`, and any
write to the IFR region. These are vendor test and lifecycle commands; they do
what their names say.

`require-pq confirm` deserves singling out: it increments `REQUIRE_PQ` and
`REQUIRE_PQ_DUPE` (`repl.rs:724–739`), its own help says *"cannot be undone!"*,
and `validate_image` then demands a PQ signature from every stage
(`sigcheck.rs:312–313`). Our images have none. It is in the REPL's advertised
command list, one typo from `reset`.

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
| Kernel DUART earlycon poll bounded by a latched spin count | `linux/patches/v6.14/0013` | 4 — the bug that hung boot #5 |
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
