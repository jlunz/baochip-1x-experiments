# Bringing up a new board without repeating 2026-08-07

`08-recovery-and-risk-ladder.md` establishes the *risk ordering*: which step
costs what, and which are irreversible. This document is the *procedure* — what
to do, in sequence, starting from a board still in its bag and a checkout that
cannot currently build an image at all.

It folds in three things the ladder did not have:

1. **A tripwire.** boot1's `audit` command reads out the exact one-way counters
   the incident analysis blames, and validates the flashed payload *without
   booting it*. Run before and after every rung, it turns the leading brick
   hypothesis from a post-mortem into a live check.
2. **A pre-flight.** The tree in its current state cannot produce a UF2 — not
   even the shim-only one. Every hour spent discovering that with a board on the
   bench is an hour of powered board and a temptation to improvise.
3. **Corrections** to the ladder's own instructions. They are folded back into
   `08` already, so it no longer lacks them; they are recorded at the end
   because the reasoning is what stops them being reintroduced.

**`## Run sheet` is the operative section** — numbered steps A1…H4, each with
what to check and when to stop. The lettered sections after it are the *why* for
each block; read them once, work from the run sheet.

Nothing here supersedes the ladder's ordering. Do not skip rungs.

---

## Decide the board allocation first

Three units, three roles, decided once and written down:

| Unit | Role | What may be done to it |
|---|---|---|
| **new board** | bring-up | the whole run sheet, including the irreversible blocks |
| **`J0BTA9`** | control | **block C only, forever** (`08` rungs 0–1). Never dev-signed, never flashed |
| **`VL7NR0`** | evidence | nothing. It is the vendor FA sample (`07` § What to try next) |

Keeping `J0BTA9` permanently read-only is the cheapest possible move on the
control-group gap `07` flags (*"it is not known to have been through the
DEVELOPER_MODE transition … If it has not, the comparison is uncontrolled
exactly where it matters most"*). Rung 1's `audit` **settles** that question at
zero irreversible cost, because `audit.rs:264–265` checks slot 85 and prints
`== IN DEVELOPER MODE ==` when it is non-zero.

It settles it either way, but read `07`'s sentence carefully before drawing the
conclusion: the gap it flags is `J0BTA9` **not** being confirmed never-dev-moded.
So the banner's *absence* is the good outcome, and its *presence* is the bad
one, not the reverse:

- **No dev-mode banner** — `J0BTA9` is the never-dev-moded reference `07`
  wanted, the gap is closed, block C is done.
- **Banner present** — `J0BTA9` has already been through the transition `07`
  worried about, exactly like `VL7NR0`. The gap stays open, every comparison in
  `07`'s measurement table remains uncontrolled, and the only never-dev-moded
  baseline that will ever exist is the new board's own D5, captured before
  block F. That is one more reason D5 is not optional.

Record which answer you got, and do not prejudge it. The temptation later will
be to "just try it on the control". That trade is bad either way — a
never-modified reference is worth more than a third bring-up unit.

## Run sheet

Work top to bottom. Every step has a **Check** that must hold before the next
one, and most have a **Stop if** — when that fires, stop, capture, and diagnose;
do not improvise forward. The rationale for each block is the like-lettered
phase further down.

Set once, in every shell:

```sh
PROBE=/dev/serial/by-id/usb-Raspberry_Pi_Debug_Probe...-if01   # not ttyACMn
```

Where a step below says "in the REPL", the tool is `tools/boot1-cmd.py` — and
its defaults are wrong for this bench. It defaults to `/dev/ttyACM0` at 115200,
which is boot1's *USB CDC* console; the probe UART needs both flags, every time:

```sh
tools/boot1-cmd.py --port $PROBE --baud 1000000                  # listen only
tools/boot1-cmd.py --port $PROBE --baud 1000000 audit
tools/boot1-cmd.py --port $PROBE --baud 1000000 bootwait check
```

With no command it sends only a bare `\r\n` — enough to nudge out a prompt,
nothing that writes a counter — which is how to confirm you have the right
port before typing anything that does.

**It does not save its output**, unlike `board-triage.py`. Any step below that
says a REPL transcript must be "saved" or "captured" means wrap the call in
`script`, the same way G3 does for the interactive session:

```sh
script -c "tools/boot1-cmd.py --port $PROBE --baud 1000000 audit" build/hw/c4-audit.log
```

Also do not trust its own `--wait` to hold a window open: `boot1-cmd.py`
re-arms its deadline to only 0.6 s past the *last* byte received, not past
the start of the call, so once continuous output starts (the ~1 Hz heartbeat
included) the process exits within a second or two of it, not after the
number of seconds passed to `--wait`. Watch the terminal and stop it by hand
(`Ctrl-C`, inside the `script` wrapper) once you've seen what the step asks
for, rather than trusting `--wait N` to keep the port held for `N` seconds.

### A — Host pre-flight (no board involved, nothing to break)

**A1.** `./setup/install-tools.sh` — *Check:* `riscv64-linux-gnu-gcc --version`
and `tools/renode/renode --version` both answer.

**A2.** `./setup/fetch-sources.sh` — *Check:* `sources/linux`, `buildroot`,
`xous-core`, `baochip-1x`, `opensbi`, `cramfs-tools` all exist.

**A3.** Apply the kernel series:
```sh
git -C sources/linux checkout -b bao1x-xip-fixes v6.14
git -C sources/linux am "$PWD"/linux/patches/v6.14/*.patch   # absolute: -C moves the cwd
```
*Check:* 22 commits on top of `v6.14`, and
`sources/linux/arch/riscv/boot/dts/baochip/dabao.dts` now exists — without it
**no** image builds, shim-only included. *Stop if:* `am` conflicts; the series is
against `v6.14` exactly.

**A4.** `(cd sources/xous-core && cargo build --release -p xous-tools --bin xous-sign-image)`
— *Check:* `sources/xous-core/target/release/xous-sign-image` is executable.

**A5.** `./emulation/qemu/build-rootfs.sh` then
`./emulation/renode/build-phase3.sh` — *Check:* `build/phase1/rootfs.cramfs` and
`build/phase3/kernel/arch/riscv/boot/xipImage` exist.

**A6.** `tools/refresh-patches.sh --check` — *Check:* passes. *Stop if:* it
reports drift; export the tree with `tools/refresh-patches.sh` and commit before
building anything.

**A7.** `./emulation/renode/tests/run-smoke.sh` and
`tools/renode/renode-test emulation/renode/tests/linux.robot --results-dir build/phase3/results`
— *Check:* both GREEN. This is the last regression gate that costs nothing.

**A8.** `tools/build-dabao-image.sh` — *Check:* `slot check OK`, UF2 matches the
vendor tool, **md5 recorded in the log with the date**.

**A9.** `SHIM_ONLY=1 tools/build-dabao-image.sh` — *Check:* `slot check OK
(shim-only)` and md5 recorded. Note this **deletes** `dabao-linux.uf2`; the two
never coexist, by design. Build shim-only last so it is what is on disk when the
board arrives.

**A10.** `git checkout -b hw/flashed-<md5-prefix>` for the shim-only image —
*Check:* branch name matches the md5 you are about to flash. This convention is
why the 2026-08-07 timeline was reconstructable at all.

### B — Bench setup (board still unplugged)

**B1.** Install the udev rule **on the host**, not in a container:
```sh
sudo cp setup/99-baochip.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
```
*Check:* survives a replug — the rule sets `MODE="0666"` only, not group
ownership, so check the mode bits, not that group ownership becomes
`dialout`.

**B2.** Wire the probe: header **pin 15 = PB14 = board TX**, **pin 16 = PB13 =
board RX**, GND to GND. 1 Mbaud 8n1.

**B3.** `tools/uart-loopback.py $PROBE 1000000` with probe TX shorted to probe
RX — *Check:* `PASS`. *Stop if:* not PASS; the fault is on the host side and
nothing measured on the board would mean anything. Remember this proves the
probe and host **only**, never the board's PB14 path.

**B4.** Decide the reset method now. `tools/board-reset.py` hardcodes an ESPHome
host (`192.168.42.38`, switch `GPIO Switch 15`). *Check:* `tools/board-reset.py
--state` reports `running`. *If not:* use `tools/board-triage.py --no-reset` and
press RESET by hand — do not discover this with the board powered.

**B5.** Multimeter on the bench, a second known-good USB-C **data** cable, and a
**direct root port** identified. Never a hub.

**B6.** Start the capture:
```sh
tools/hw-console.py --port $PROBE:1000000
```
*Check:* logs appearing under `build/hw/`. An event nobody recorded is
unobservable afterwards.

**One process may hold `$PROBE`, and only one.** Nothing in `tools/` opens a
serial device with `exclusive=True` — `hw-console.py:55`, `board-triage.py:168`,
`boot1-cmd.py:30` and `uf2send.py:216` are all plain `serial.Serial(...)`. Two
readers on the same port therefore do not fail loudly; they *split the byte
stream between them, at random*, which is worse than either alone.
`board-triage.py` would under-count and can print `SILENT` on a live board;
`uf2send.py` would miss its `Wrote 256 to 0x…` acknowledgements and burn through
`RETRY_LIMIT` (5 blocks) into an abort.

`hw-console.py` is the default holder. Hand the port over, and take it back:

- **Release it** for every step that runs another tool — C2, C4, D3, D5, E1, E2,
  F2, G2. Nothing is lost by doing so: `board-triage.py` writes its own
  capture (`save_capture`) and `uf2send.py` reports per block.
- **Hold it** across the button presses and the observation windows — C3, D4,
  F3, F4 — which is where the unrepeatable events are.
- **F5, G6 and H4 also press PROG + RESET**, ahead of the `audit` they each
  read. Keep `hw-console.py` running through the button press itself — it is
  a reset, and its banners are worth having in the standing capture — then
  release only for the `audit` call that follows (wrapped in `script`, per the
  note above), and restart `hw-console.py` once that returns.
- **F2 is both at once**, and it is the one boot you cannot repeat. Send and
  capture from the same process, in `script` so the transcript survives
  `boot1-cmd.py`'s own early exit (see the note above):
  ```sh
  script -c "tools/boot1-cmd.py --port $PROBE --baud 1000000 boot" build/hw/f2-boot.log
  ```
  Watch it, stop it by hand (`Ctrl-C`) once `SBI:alive` has repeated a few
  times, then restart `hw-console.py`.
- **G3–G5 and H2–H4 are one interactive session** and hold the port for their
  whole duration; G3 says how to record it.

If boot1's USB CDC console has enumerated, capture *that* port concurrently
instead — a different device, no contention, and the pairing `hw-console.py`'s
own docstring shows.

### C — Baseline the control board `J0BTA9` (read-only, once, then retire it)

**C1.** Plug in `J0BTA9`, capture already running.

**C2.** `tools/board-triage.py --port $PROBE` — *Check:* `ALIVE. boot1 is
running.` Keep the capture file.

**C3.** PROG + RESET into the REPL — hold PROG, press and release RESET while
still holding PROG, wait 1 s, release. *Check:* the REPL prompt is reachable.
The banner it prints depends on whether bootwait is enabled on this unit
(`main.rs:190–194` — the two are mutually exclusive): `Boot bypassed because
bootwait was enabled` if it is, `Boot bypassed with keypress: …` only if it is
not. `08` says bootwait is enabled on these units, so expect the first form,
not the second — reaching the prompt at all is the actual check.

**C4.** In the REPL: `audit`, via the `script` wrapper above so it is actually
saved (`boot1-cmd.py` itself writes nothing to disk). *Check:* transcript
saved and **committed** (`build/` is gitignored, the transcript is not). Then
read one line off it: whether `== IN DEVELOPER MODE ==` appears. Absent, this
is the never-dev-moded baseline `07` wanted; present, the control-group gap
stays open and the new board's D5 is the only clean baseline you will get.
**Record which** — that is the whole point of the step.

**C5.** Unplug `J0BTA9` and put it away. Nothing further, ever.

### D — Baseline the new board (read-only, zero risk)

**D1.** Plug the new board into the direct root port, capture running.

**D2.** Multimeter: GND(13)→VBUS(40) ≈5 V, GND(13)→3V3(36) ≈3.3 V. *Check:*
both present. *Stop if:* absent or low — the fault is power, not firmware, and
nothing else you do will be interpretable.

**D3.** `tools/board-triage.py --port $PROBE` — *Check:* `ALIVE. boot1 is
running.` *Stop if:* `SILENT` — work its printed remedies (cable, root port,
continuity at 15/16), then `tools/baud-scan.py --port $PROBE`. Do not reach for
flash.

**D4.** PROG + RESET into the REPL. *Check:* the REPL prompt is reachable —
see C3 for which banner to actually expect; do not treat `Boot bypassed with
keypress` as the pass condition if D5 is about to find bootwait `Enable`.

**D5.** Capture, in this order (via the `script` wrapper — `boot1-cmd.py`
does not save its own output):
```
audit
usb_speed            # bare form reads
bootwait check       # 'check', NOT bare -- bare is a help error
```
*Check:* saved as this board's baseline, **and `bootwait` reads `Enable`** —
that value is what makes block E reversible; see the gate below. *Stop if:*
`Paranoid mode: a/b` shows `a != b`, or `Possible attack attempts` is non-zero
and unexplained.

Note what the revocation table says, but do **not** stop on a `revoked` here.
This is the read that establishes what normal looks like *on this unit*, and
`audit.rs:111` prints `revoked` for any slot whose counter is non-zero **or
unreadable** (`unwrap_or(1)`), across four key slots for each of three stages —
so a factory-revoked or never-provisioned slot reads `revoked` legitimately.
What matters is a later diff, not the absolute value; see `## Stop rules`.

**D6.** Record the serial number `audit` prints, and label the board physically.

**Gate for D:** banners seen, REPL reachable, `audit` captured, both paranoid
halves equal, and **`bootwait` reads `Enable`**. Everything so far is
reversible; nothing has been written.

**If `bootwait` reads `Disable`, stop and decide before E.** `main.rs:184`
auto-boots the payload when bootwait is disabled and no PROG key is held, ahead
of the keypress check. So once E1 has landed a valid dev-signed image, *any*
reset — F4's, `board-triage.py`'s default reset, an accidental one, a replug —
crosses into F unattended, and E's "last fully reversible point" stops being
true. `08` says bootwait is enabled on these units; that is an observation about
the two boards in hand, not a property of a new one, which is why D5 reads it
rather than assuming it. The fix is `bootwait enable` — a one-way counter write,
rung 6, and the one deliberate exception to `## Never` worth making. Make it
here, before flashing, or accept that E and F are a single irreversible step.

### E — Flash the shim-only image, do not boot it (reversible)

**E1.**
```sh
python3 sources/xous-core/bao1x-boot/uf2send.py \
    build/dabao/dabao-shim-only.uf2 --port $PROBE
```
UART, not mass storage: mass-storage success is silent, and USB is the
unreliable path here. *Check:* uf2send reports all blocks accepted.

**E2.** `audit` again. *Check:* **`Next stage: key 3/3 (dev ) -> …`** — the
image landed and validates, with no fuse touched. *Stop if:* `Next stage did not
validate` → reflash. Do not issue `boot`.

**E3.** Diff this `audit` against D5. *Check:* identical apart from the
next-stage line. Nothing between D5 and E2 resets the board, so even
`auto-audit limit:` should not have moved here — unlike at F5.

**Gate for E:** payload validates, counters unchanged. **Last fully reversible
point — but only if `bootwait` reads `Enable` (see the D gate), and only while
the REPL loop is left alone.** Two other exits from that loop also fall
through to `boot()` regardless of bootwait: pressing PROG a second time while
in the REPL (`main.rs:284`, `new_key.is_some() && current_key.is_none()`
breaks the loop), and unplugging USB *after* it has enumerated to
`Configured` (`main.rs:351–356` breaks on disconnect). So between E2 and F1,
leave the board alone — don't fidget with PROG, and if USB has come up, don't
unplug it. `§ E` has the detail on the USB case.

### F — First dev-signed boot (irreversible: burns DEVELOPER_MODE)

**F1.** Confirm something is recording `$PROBE` and nothing will touch RESET —
for this step that means the `script`-wrapped `boot1-cmd.py boot` form from
the port-ownership note above, so the process that sends `boot` is the one
recording what comes back; stop it by hand once you've seen the heartbeat
repeat, don't rely on its own `--wait` timer. **Do not reset
between issuing `boot` and the first `SBI:` marker** — that whole window writes
the counter array, on each of the first half-dozen dev-signed boots. The window
opens at `boot`, not at the `Booting with key` line: from the second dev-signed
boot on, the first counter write happens *before* that line is ever printed
(§ F).

**F2.** Issue `boot`. *Check, in order:*
```
Booting with key 3/3(dev )
Developer key detected, ensuring secrets are erased
SBI:entry / SBI:duart-ok / SBI:uart-init
SBI:se0-released
SBI:dtb-copied / … / SBI:csr-done
SBI:shim-only -- not entering kernel
SBI:alive                      <- ~1 Hz, forever
```
*Stop if:* any marker missing, or `SBI:FATAL` — the last marker seen pins down
where it stopped (`05` § Shim stage markers).

**F3.** *Check:* the heartbeat is ~1 Hz. If it is much faster, that is the
mcycle-stall fallback (`main.c:144`): a sign of life, but the cycle counter is
not advancing and the timer assumptions need revisiting before Linux.

**F4.** **SE0 test.** `tools/board-reset.py` (or the RESET button) with **no
physical replug**. *Check:* USB comes back on its own. That confirms the fix for
the single most annoying failure mode of the previous session.

**F5.** PROG + RESET back to the REPL, `audit`. *Check:* diff against E2. Two
groups of line are expected to move, and neither is a finding:

- the `DEVELOPER_MODE` consequences — `== IN DEVELOPER MODE ==`, `Erase proof:
  erased`, `Collateral erased`, and the `** System did not meet minimum
  requirements for security **` footer;
- `auto-audit limit:` — that line is `EARLY_BOOT_COUNT` (slot 47), which
  `early_audit()` bumps on every boot1 start (power-up *or* reset) for the
  chip's first three, regardless of anything done here, and those same boots
  dump an unprompted full `audit` into the capture. F4's reset counts toward
  that limit; F2 does not — `boot` jumps straight to the shim without
  restarting boot1, so `early_audit()` never runs for it. On a board still
  under the limit, F4's reset (and F5's own PROG + RESET) will have moved it.

Everything else must be unchanged: **paranoid halves still equal**, `Possible
attack attempts` unchanged, revocation table unchanged.

**This diff cannot catch a differential move on 65/67 from F2 itself.** boot0
compares them at `bao1x.rs:62`, *before any console exists*, and only on a
reset — F2 is not one. If F2's `boot` had somehow desynced the pair, the first
thing that would notice is boot0 on F5's own reset, which is silent and does
not return to the REPL `audit` is read from. See `## The tripwire: audit`
below for how far the live-catch claim actually reaches.

**Gate for F:** the whole flash-and-handover path is proven on silicon, without
Linux ever executing.

### G — Kernel to a shell, read-only rootfs

**G1.** `tools/build-dabao-image.sh` — *Check:* md5 recorded against the boot it
will produce, and `git checkout -b hw/flashed-<md5-prefix>` for it, the same
convention as A10. This is the image that runs Linux; it is the one whose
timeline you will actually want to reconstruct.

**G2.** `uf2send.py build/dabao/dabao-linux.uf2 --port $PROBE`, then `audit`.
*Check:* `Next stage: key 3/3 (dev )` again, before booting.

**G3.** From here on you need an interactive session, not `boot1-cmd.py` — G5
and H want a shell. pyserial ships one and is already a dependency; wrap it in
`script` so the session is still recorded, since `miniterm` does not log:
```sh
script -c "python3 -m serial.tools.miniterm $PROBE 1000000" build/hw/g-linux.log
```
(Ctrl-] leaves miniterm.) `hw-console.py` must be off `$PROBE` for all of G3–G5.
Type `boot` in that session and stay in it. *Check:* `bao1x-sbi: jumping to
kernel`, then `[ 0.000000] Linux version …` via `earlycon=sbi` on the same wire.

**G4.** *Stop if:* the kernel prints **nothing at all**. Early output is still
not reaching the wire; go back to F and prove the console rather than iterating
on kernel builds blind.

**G5.** In the same session — *Check:* `dabao login:`, root with empty password,
and `sleep 1` takes about a second (timebase sanity).

**G6.** PROG + RESET, `audit`, diff. *Check:* counters unmoved. Root was mounted
read-only; nothing should have written RRAM.

### H — Writable RRAM, last and highest risk

**H1.** In Renode first: exercise the driver guard with a deliberately bad
partition offset. *Check:* `-EROFS` and the offending offset logged. Offset 0 of
the `data` partition proves nothing — it lands at `0x360000`.

**H2.** The board should already be running G1's image from G3–G5 — H1 is the
Renode-only exercise with the deliberately bad offset, not something to
reflash onto hardware. From the still-open G3 shell, write **by hand at a
known-safe offset inside `data`** before mounting anything. If the G3 session
was closed, reopen it the same way (§ port ownership above): `uf2send.py`
needs `$PROBE` released from any interactive session before it can flash, and
the G3 shell needs it back afterward. *Check:* the write reads back.

**H3.** Mount JFFS2 on `/data`. *Check:* survives a reset with contents intact.

**H4.** PROG + RESET, `audit`, diff. *Check:* **paranoid halves still equal.**
*Stop if:* they differ — the board is one boot from a silent permanent brick.
Note the limit of this check: reaching it already required a reset, and
`a != b` is exactly the condition boot0 checks *before* that reset can reach
this REPL (`bao1x.rs:62`). If H3 actually desynced the pair, this step does
not run at all — the board goes silent instead, and that silence, on a board
that was alive going into H3, **is** the signal. Either way: do not reset
again, capture everything, and treat it as a vendor FA sample.

## The tripwire: `audit`

`boot1/src/audit.rs` `audit()`, reachable from the REPL as `audit`, prints —
among identity and version fields — exactly this:

| Line it prints | Slot | Why it is the thing to watch |
|---|---|---|
| `Paranoid mode: <a>/<b>` | 65 / 67 | `PARANOID_MODE` / `PARANOID_MODE_DUPE`. boot0 compares these for exact equality at `bao1x.rs:62` **before any console exists**. `a != b` is a permanent, silent, unrecoverable brick on the next boot |
| `Possible attack attempts: <n>` | 66 | `POSSIBLE_ATTACKS`. Rising = the glitch detectors are firing |
| `PQ required: <a>/<b>` | 19 / 44 | `REQUIRE_PQ` / `REQUIRE_PQ_DUPE`. Must stay `0/0`. `validate_image` enforces the pair for **every** stage (`sigcheck.rs:312`) and our images carry no PQ signature. Only `require-pq confirm` sets it, and it cannot be undone — see `## Never` |
| `First-try boot partition is:` | — | `AltBootCoding`. Must stay on boot1 (`08` rung 6) |
| `Revocations: <table>` | 116/120/124 | key revocations, four slots per stage — the **main** array only (`audit.rs:101`: *"only checks the main array, not the duplicate array"*), so the dupes at 68/72/76 never appear here. Any `revoked` that was `enabled` is unrecoverable; a slot that read `revoked` in the D5 baseline is not news |
| `Next stage: key 3/3 (dev ) -> …` | — | **validates the flashed payload without booting it** (abbreviated here; the actual line also carries a ` pq …` field between the tag and the target) |
| `Boot0:` / `Boot1:` | — | the vendor chain still validates |

The three slots the incident document asks the vendor to read off the dead die —
65, 66, 67 — are defined at `libs/bao1x-api/src/offsets/common.rs:182,188,191`.
On a live board they are one command away.

**The procedure is therefore: capture `audit` verbatim before touching the
board, and again after every rung. Diff them.** This catches a differential
move on 65/67 only when nothing resets the board between the moving rung and
the `audit` that reads it — which is true for D5→E2 (E3 relies on exactly
this) but false everywhere the run sheet reads `PROG + RESET, audit` (F5, G6,
H4): boot0's own equality check on 65/67 (`bao1x.rs:62`) runs on that very
reset, *before* any console exists, so a rung that desynced the pair is caught
by a silent brick on the reset, not by the diff that was meant to precede it.
Where a reset intervenes, the diff still proves everything else in this table
— revocations, `PQ required`, `Next stage` — because those are read, not
compared for equality pre-console; only 65/67 have this gap. Treat every
`PROG + RESET` step's diff as "confirms the board came back and printed
`audit`" first, and "confirms 65/67 didn't move" only incidentally.

Two precisions, so nobody later records this as more than it is:

- `audit` is **not** bit-for-bit read-only. `detect_stepping()`
  (`audit.rs:46`) writes the RRC security-mode register to test whether bit 12
  is clearable, then restores it three times over. That is a volatile
  peripheral CSR — no fuse, no RRAM, no counter — and it is the vendor's own
  auto-audit path, which runs unprompted on the first three boots of every chip.
- The REPL's `audit` calls `audit()` directly; only boot1's automatic
  `early_audit()` increments `EARLY_BOOT_COUNT` (slot 47), and that happens on
  every boot1 start — power-up or reset alike — regardless of anything done
  here, for the first three such starts of the chip's life. F5 relies on this:
  F4's reset is what moves it, not a power-up.

---

## A — why the pre-flight exists

None of it needs a board, and all of it fails loudly if left until one is on the
bench.

| Missing | Consequence |
|---|---|
| `sources/linux` absent | `build-dabao-image.sh:63` reads `dabao.dts` from the kernel tree, **on both paths** — so even `SHIM_ONLY=1` fails |
| `xous-sign-image` not built | `build-dabao-image.sh:43` exits before anything is assembled |
| `build/dabao/` empty | no artifact, no md5, nothing to compare a reflash against |

The two UF2s cannot coexist on disk: each build deletes the other
(`build-dabao-image.sh:143`), deliberately, because they are indistinguishable
once copied onto the board. Hence A9 last, and a rebuild at G1.

## B — why the bench comes before the board

The udev rule must be installed **on the host**; udev does not run inside a
toolbox, and without it device nodes come back wrong after *every* board reset.
The loopback test proves the probe, its cable and the host stack — and says
nothing whatever about the board's PB14 path, which is why B3 passing and the
board being silent are compatible. The multimeter is not optional: the board has
no LED, so 3V3 and VBUS are the only power indication that exists, and their
absence is what made the incident uninterpretable for three days.

## C–D — why baseline both boards

`J0BTA9` is the *candidate* never-dev-moded reference — candidate, because `07`
records that nobody knows whether it has been through the transition, and C4 is
the step that decides it rather than assuming it. The new board gives the
"before" against which every later `audit` is diffed, and that one is a clean
baseline however C4 turns out, provided it is taken before block F. `07` is hard
to write precisely because neither existed. Both cost nothing irreversible.

## E — why the UART, and why flashing is safe

Over the probe UART, not mass storage: boot1's REPL reads the UART whenever USB
is not `Configured` (`boot1/src/main.rs:329`, the `else` branch draining
`UART_RX`), so this path does not depend on the USB stack that has been
unreliable throughout — and mass-storage success is *silent*, which is a bad
property for a step you need to be sure about.

The guard is precisely `USB_CONNECTED` (`main.rs:304`), which latches true the
first time USB reaches `Configured` and announces itself with `USB is
connected!` / `Console moved to USB serial`. That inverts the assumption above:
if those lines appear, the probe UART is **no longer being read**, and E1 will
sit there accepting nothing. Either work the CDC console instead — that is what
`boot1-cmd.py`'s defaults are for — or bring the board up with USB providing
power only. Given `07`, the UART-only case is the likely one, but check for the
line rather than assuming it.

Flashing cannot reach the bootloader or the dangerous counters — and the two
flash paths have *different* bounds, with the UART one being the tighter.

`uf2send.py` streams `uf2 <base64>` commands into boot1's REPL, which
range-checks each record at `boot1/src/repl.rs:182–184`:

```
record.address() >= BAREMETAL_START
    && record.address() < HW_RERAM_MEM + RRAM_STORAGE_LEN
```

= `0x60060000` up to but **not including** `0x603DA000`; out-of-range blocks are
ignored with `Invalid write address` (`repl.rs:196`). The one-way counter array
begins at exactly `0x603DA000` (`acram.rs:20`), so on this path the first
address that could touch it is the first address the check rejects.

The USB mass-storage handler is the looser of the two: `handlers.rs:249` matches
`START_RANGE..=STORAGE_END_ADDR`, **inclusive**, so there one 256-byte block
addressed at `0x603DA000` would land on counter slots 0–7 (`ONEWAY_LEN` is 32
bytes, `acram.rs:22`). Those slots are documented unallocated
(`offsets/common.rs:121`) and our layout never addresses them anyway (`LIMIT`
stops at `0x60360000`) — so it is not a live hazard, but it is one more reason
E1 says UART rather than mass storage. Slots 65/66/67 are out of reach of
either path.

**Then run `audit` again.** `Next stage: key 3/3 (dev ) -> …` proves the image
landed and validates, with no fuse touched: boot1 validates *before*
`hardened_erase_policy` runs. `Next stage did not validate` here means reflash,
not boot.

**Still reversible at this point. Nothing has been burnt** — provided `bootwait`
is enabled. With it disabled, `main.rs:184` boots this payload on the next
reset, with nobody typing `boot`. That is why the D gate reads it.

## F — what the irreversible boot actually costs

`boot` burns `DEVELOPER_MODE` and erases the factory secrets. It affects only
this unit's ability to run factory-signed secure workloads.

What actually happens on each such boot, from `sigcheck.rs:678–790`:

- key slots are erased **only if not already erased** (`:733`, *"to avoid
  stressing the RRAM array"*), so the bulk RRAM write is a first-boot event;
- but `DEVELOPER_MODE` (slot 85) is **incremented on every call to
  `erase_secrets` until it reaches 15** (`:783`) — and after the first boot
  there are *three* such calls per boot, not one: `secboot.rs:33` (`dev1 != 0`),
  `:38` (`dev2 != 0`), and `:91` via `hardened_erase_policy`. `hardened_get`
  returns the counter summed five times, so both halves read non-zero the moment
  the counter is.

Counting it through: boot 1 leaves the counter at 1, then boots 2–6 leave it at
4, 7, 10, 13, 15. **It saturates on about the sixth dev-signed boot, not the
fifteenth** — do not read "15" as a boot count.

Two operational rules follow, both sharper than `07`'s:

- **Do not reset the board between issuing `boot` and the shim's first `SBI:`
  marker** — for the first half-dozen dev-signed boots, not just the first.
- **The window opens at `boot`, not at the `Booting with key 3/3(dev )` line.**
  On boot 1 the erase runs after `validate_image` (`secboot.rs:63`) and so after
  that line prints. From boot 2 on, the `:33`/`:38` calls run *before*
  validation, so the boot's first counter write precedes any "Booting" output.
  What you will see on the wire while it happens is `Key range at …: n/n keys
  confirmed erased` (`sigcheck.rs:756`).

The SE0 test at F4 is worth doing explicitly rather than noticing in passing:
everything after this rung assumes recovery does not need hands on the board.
Recovery from anything in F is PROG + RESET; nothing here writes RRAM beyond
what this section already describes — the `DEVELOPER_MODE` counter increment
on every boot, and the bulk key-slot erase once, on the first.

## G — why the console must be proven first

Root is XIP cramfs, mounted read-only; nothing writes RRAM. Early output goes
`earlycon=sbi` with `stdout-path = &uart2`, onto the wire this board actually
routes — the DUART earlycon with its unbounded poll is what silently parked
boot #5. So a kernel that prints *nothing* means the console, not the kernel:
go back to F and prove it there rather than iterating on kernel builds blind.

## H — writable RRAM (JFFS2 on /data)

Last, and the first time Linux writes the array the boot chain lives in. Read
`08` rung 5 in full before starting; in particular, offset 0 of the `data`
partition lands at `0x360000` and proves nothing about the driver's guard.
Exercise the guard in Renode with a deliberately bad partition offset first.

Current geometry (patch `0022`): the MTD device's `reg` stops at `0x3da000` and
`data` ends one erase block short of it, so the counter array is outside
anything MTD can address — but that is a device-tree property, which is why
`bao1x-rram.c` also refuses writes below `0x60000` in the driver itself.

## Never

`altboot`, `paranoid`, `require-pq`, `self_destruct`, `publock`, `lockdown`,
`rand_collateral`, any write to IFR, and the **writing** argument forms of
`bootwait` (`toggle` / `enable` / `disable`), `usb_speed` (`full` / `high`) and
`boardtype` (`dabao` / `baosec` / `oem`).

The *read* forms are safe: `bootwait check`, bare `usb_speed`, bare
`boardtype`. D5 uses the first two; bare `boardtype` is safe on the same
pattern but nothing in the run sheet needs it. Which form reads is per-command
and not guessable — see correction 1. The one deliberate exception to this
list is `bootwait enable`, and only in the case the D gate describes.

**`require-pq` is the most dangerous command on the list, and until correction 6
neither document named it.** `repl.rs:724–739`: `require-pq confirm` increments
`REQUIRE_PQ` (slot 19) and `REQUIRE_PQ_DUPE` (44), and its own help string says
*"cannot be undone!"*. `validate_image` reads that pair (`sigcheck.rs:312–313`)
when checking **every** stage, and our images carry no PQ signature — `audit`
reports `No PQ sig`. Setting it permanently rejects our payload, and plausibly
the vendor chain with it. It sits in the REPL's advertised command list, one
typo away from `reset`.

One addition to the ladder's list: **never set the `warm_boot` backup flag from
the payload.** `boot1/src/main.rs:184` auto-boots on `warm_boot` *before* it
considers either bootwait or the PROG keypress — so a payload that sets it and
then hangs has removed the recovery window entirely. Our port never touches
BUREG, and `AORSTn` clears the flag by hardware design
(`bao1x-api/src/lib.rs:73–77`), so this is a rule to keep, not a live problem.

## Stop rules

Stop, capture, and write it down before doing anything else, if:

- `Paranoid mode: a/b` shows `a != b` — the next boot is a silent permanent
  brick. Do not reset. Do not boot.
- `Possible attack attempts` increases across a rung.
- any revocation flips from `enabled` to `revoked` — the flip is the signal, not
  the absolute value; D5 records what this board reads when nothing is wrong.
- `PQ required` moves off `0/0` — nothing we can sign will validate again.
- `audit` reports `Boot1 did not validate` — boot0 will fall back into the
  payload region, which on this board is our image.
- the board goes silent and a **measured** 3V3/VBUS are good. That is the exact
  signature of `07`, and the next action is the multimeter and a cable swap, not
  another flash.

## Corrections to `08-recovery-and-risk-ladder.md`

Found while checking it against `sources/xous-core` for this plan, and folded
back into `08` (rungs 1, 2, 4 and 6, and the hard-limits list). Corrections
7–9 are a second pass, checking this document (and, where they duplicated the
same claim, `05` and `08`) against the actual run sheet rather than against
the source alone — the first six were citation and read/write errors; these
three are places the *logic* of a Check or a claimed guarantee didn't survive
contact with the sequence of steps around it. Kept here as the record of what
changed and why.

**1. Rung 1's read/write rule is wrong for `bootwait`.** The ladder says *"The
bare form reads; the form with an argument increments a one-way counter"* and
lists bare `bootwait` as a query. `repl.rs:456–459` requires exactly one
argument and returns a help error otherwise, so bare `bootwait` reads nothing.
The read form is **`bootwait check`** (`repl.rs:472`); `toggle`, `enable` and
`disable` are the counter writers. `usb_speed` bare does read
(`repl.rs:1098`), so the rule is right for that one. The failure mode is
harmless — a help message, not a write — but as written the ladder leaves no
way to read the bootwait state at all.

**2. "PROG + RESET forces the REPL regardless of bootwait" has an exception.**
`main.rs:184` short-circuits to `try_boot()` when the `warm_boot` backup flag is
set, ahead of both the bootwait check and the keypress check. Harmless today —
nothing in our port writes that flag, and `AORSTn` clears it — but the claim as
stated is unconditional, and it is the claim the whole recovery story rests on.

**3. The `audit` command is under-sold.** `08` describes it as *"dumps identity
and configuration"*. It reads the three counters the incident blames and
validates the payload without booting it. That is the cheapest risk reduction
available in the entire ladder, and it belongs in rung 1's gate, not in a list.

**4. The UF2 range check was cited from the wrong path.** `08`'s hard-limits
list pointed at `usb/handlers.rs:249` — the USB mass-storage handler — to
justify a claim about `uf2send.py`, which does not go that way. The REPL `uf2`
command it actually drives checks `repl.rs:182–184`, and its bound is *half-open*
where the mass-storage one is inclusive. The conclusion survives and in fact
gets stronger on the UART path, since the counter array starts at exactly the
excluded address, but the citation supported it from the wrong side.

**5. Rung 1's revocation row listed a slot `audit` never prints.** It gave
`68/116/…`. `audit.rs:100–118` reads the **main** array only, and says so in a
comment — 116–119, 120–123, 124–127. Slot 68 is `LOADER_REVOCATION_DUPE_OFFSET`,
which never appears in the output. The row also now says that the *transition*
is the finding, not the value: `audit.rs:111` prints `revoked` for any slot that
is non-zero **or unreadable**, so a factory-revoked slot reads `revoked` in a
perfectly healthy baseline.

**6. `require-pq` was missing from rung 6's `Never` list entirely.** It is a
permanent, unrecoverable counter write (`repl.rs:724–739`), enforced against
every stage at `sigcheck.rs:312–313`, and our images carry no PQ signature. Of
everything reachable from the REPL by a single mistyped word it has the worst
outcome, and it was the one command neither document named. `audit` reports the
pair as `PQ required: <a>/<b>`, which is now a row in rung 1's table too.

**7. "boot1 prints `Boot bypassed with keypress`" is only ever true on a unit
with bootwait disabled.** `main.rs:190–194` prints one of two messages and the
two conditions are mutually exclusive: `Boot bypassed because bootwait was
enabled` when `boot_wait == Enable`, `Boot bypassed with keypress: …` only in
the `else if` covering a keypress with bootwait `Disable`. Both `08` rung 0
and `05`'s Recovery section quoted the keypress banner as *the* thing PROG +
RESET proves — on the boards this repo has actually touched, which `08` itself
says have bootwait enabled, that banner can never appear. The guarantee PROG +
RESET actually gives is reaching the prompt at all; the banner text is a
function of bootwait, not of PROG.

**8. Rung 1's "a differential move on 65/67 is then caught while the board
still boots" held only for the case with no reset in between.** boot0 compares
65 and 67 for exact equality at `bao1x.rs:62`, *before any console exists*, on
every reset. Wherever the ladder's own sequence is "do X, then PROG + RESET,
then `audit`" — which is most of rungs 3 onward — a rung that desynced the
pair is caught by a silent, console-less brick on that very reset, not by the
`audit` diff that was supposed to precede it. The diff still catches
everything else in the table (it's read, not compared for equality
pre-console); 65/67 is the one row where the claim needs the "no reset
intervened" qualifier.

**9. Rung 3's "nothing here writes RRAM" contradicted the rung's own title.**
Rung 3 is "First boot of a dev-signed image. **Irreversible: burns
DEVELOPER_MODE.**", and its own body says burning it "cannot be undone" — yet
the "Proves" paragraph claimed the boot writes no RRAM. It does:
`DEVELOPER_MODE` (slot 85) increments on every dev-signed boot
(`sigcheck.rs:783`) and the key slots get a bulk erase on the first
(`:733`). What PROG + RESET recovers after this rung is *access to the REPL*,
not the fuse state — the two were being conflated.
