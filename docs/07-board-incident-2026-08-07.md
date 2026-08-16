# Dabao board: everything done to it, up to it going unresponsive

Session 2026-08-07. Times are wall-clock from the UART capture logs in
`build/hw/*.txt`. Deliberately kept in `build/` (gitignored) — not committed.

Board: Dabao, boot0/boot1 `v0.10.0-61-g5397e1b48` (`bao2-0`), serial `VL7NR0`.

A second Dabao, serial **`J0BTA9`**, has been in use since 2026-08-07 22:42 and
remains healthy. It serves as the control for every measurement below.

> **Update 2026-08-10.** Sections marked *(2026-08-10)* were added after a
> follow-up bench session with a logic analyzer and a USB power meter. The
> original "Open question" about whether the board is powered has been
> **answered** — see [Post-incident measurements](#post-incident-measurements-2026-08-10).

> **Update 2026-08-15.** A review against upstream `xous-core`, the Dabao board
> files and the bao1x RTL narrows the hang window, establishes that a silent
> `die()` fits every observation, identifies a partition-adjacency hazard in our
> own port, and closes out recoverability — see
> [Source review](#source-review-2026-08-15) and
> [What to try next](#what-to-try-next-2026-08-15). No hardware was touched.

## Timeline

| Time | Event |
|---|---|
| 13:47 | First contact. Enumerates cleanly, **USB high-speed (480 Mbps)**, CDC console + `BAOCHIP` mass storage. boot0/boot1 banners on UART2 @ 1 Mbaud. `CPU @ 350MHz`, board type Dabao, `bootwait` enabled, clock skipping off. Board fully healthy. |
| 13:57 | Flash #1 — `dabao-linux.uf2` md5 `76f057ea`, copied to the `BAOCHIP` volume. |
| 13:59 | **Boot #1.** `Stopping USB… / Booting with key 3/3(dev ) / Developer key detected, ensuring secrets are erased` → **DEVELOPER_MODE burnt (irreversible, pre-approved)**. Then silence (later diagnosed: `duart_puts()` spinning on an unrouted DUART). |
| 14:08 | Physical RESET by user. **Board recovers completely** — boot0/boot1 up, USB fine. |
| 14:12 | **Boot #2** (same image, cold cache, to test I-cache staleness). Same hang. |
| ~14:15 | User unplugs/replugs; board ends up on a **hub chain** (`1-1.1.1`). Host log shows `-32`/`-71`, `attempt power cycle`, **full-speed**. *(See correction below — this pattern is normal, not a degradation.)* |
| 14:15–20:50 | Board absent/unusable. Extended remote debugging. |
| 20:55 | Board reappears on a **direct root port** (`1-8`), **480 Mbps**, healthy again. Flash #2 — md5 `47304c1b` (bounded waits + stage markers). |
| 20:55 | **Boot #3.** Markers through `SBI:dtb-copied`, then hang (later: `mcounteren`). |
| 21:02 | First ESPHome `RST_N` reset — works, board recovers. |
| 21:03 | Flash #3 — md5 `2d650de9` (per-CSR markers + M-mode trap reporter). |
| 21:03 | **Boot #4.** `SBI:FATAL M-mode trap mcause=0x2 mepc=0x600604f0 mtval=0x3063d073` (`csrwi mcounteren, 7`). |
| ~21:05 | A `cp` to a **stale mount** returned `Input/output error` (mount invalidated by an earlier reset). Believed to have written nothing to the board; boot1 validated normally afterwards. |
| 21:22 | `RST_N` reset — works, board recovers, boot1 up. |
| 21:25 | Flash #4 — md5 `06412a3b` (`csr_write_probe`). |
| **21:25:25** | **Boot #5 — last successful communication with the board.** Shim runs to completion: `…SBI:csr-done` → `bao1x-sbi: jumping to kernel`. Kernel prints nothing. |
| 21:30 → | **Never responds again.** |

## After 21:25 — what was tried, and the result

- `RST_N` via ESPHome, holds of 0.3 s / 0.5 s / 3.0 s — switch state verified ON→OFF→ON each time. No UART output.
- **Physical RESET button** — no UART output. (Rules out the ESPHome line.)
- **PROG + RESET** — no UART output.
- **Baud scan**, 14 rates from 9600 to 2 M, one reset each — **zero bytes at every rate**.
- USB: fails on a **hub port and a direct root port** alike, always **full-speed**, `device descriptor read/64, error -71`. Never enumerates.
- **UART loopback test PASSED** (probe TX shorted to probe RX): 29/29 bytes byte-exact at 1 Mbaud → the probe, its USB path, both leads and the host software are all good. *(2026-08-10: loopback re-verified clean up to 2 Mbaud, and the **good board's console was captured through the same rig**, which is the stronger proof — the chain is known-good end to end, not just in loopback.)*
- Console leads re-seated and re-checked against the pinout (pin 15 = PB14, pin 16 = PB13).
- ~~**Never measured:** VBUS (pin 40) and 3V3 (pin 36).~~ **Measured 2026-08-10** — see below. The board has **no LED**, which is why this went unconfirmed for so long.
- **Never changed:** the USB cable. *(Still true, but no longer load-bearing: the failure reproduces with the USB path removed entirely — see the logic-analyzer result.)*

## Facts worth noting (no causal claim)

- **Every one of the 5 boots left the USB SE0 pin asserted.** boot1's `boot` drives PC13 low and leaves it that way, expecting the next stage to release it (`README-baochip`: *"it is up to the next USB stack to de-assert this"*); our payload never did. The EMS4000 switch is powered from **VBUS**, so `RST_N` cannot clear it — only a physical unplug can. This matches the observed pattern exactly: resets never restored USB, physical replugs did (twice). A fix exists (`SBI:se0-released`, md5 `105b5045`) but **has never run on hardware**.
- **`hardened_erase_policy` ran on all 5 boots** — `Developer key detected, ensuring secrets are erased` was printed each time. Whether repeated erase cycles matter is unknown to me.
- The board recovered from boots #1–#4 without issue. Only after boot #5 — the first boot where the shim completed and actually `mret`'d into the **Linux kernel** — did it stop responding. That is a correlation, not a demonstrated cause. How far the kernel actually got is **unknown**: with `stdout-path = &duart` and a DUART that wedges, it prints nothing whether it stopped at its first instruction or reached userspace (correction 6).
- ~~The USB degradation (high-speed → full-speed, `-71`) began at ~14:15, hours *before* the final failure.~~ **Retracted — see corrections.**

## Corrections

Items 1–2 date from 2026-08-10; items 3–6 from the 2026-08-15 source and
board-file review.

**1. The `-32` / `-71` / full-speed / `attempt power cycle` sequence is not a
degradation — it is this board's normal boot pattern.** The good board (`J0BTA9`)
produces the identical sequence on every healthy plug-in: full-speed attach →
`-32` on the descriptor read → hub power cycle → high-speed success. So its
appearance at ~14:15 is not a precursor to anything. The meaningful distinction
is that the failed board **never escapes that cycle**, not that it enters it.

**2. Full-speed on the dead board is a consequence, not a cause.** An
unconfigured USB block presents as full-speed by default; high-speed requires
firmware to program MAXSPEED and complete the chirp handshake. "Full-speed only"
therefore just restates "firmware never ran." An earlier theory that the
`UsbDefaultSpeed` one-way counter was involved is **withdrawn** — nothing ever
wrote that OWC.

**3. The `X`-on-death emitter is real, and its absence proves nothing.**
`BOOTCHAIN.md:24` says a failed check zeroizes volatile state, prints *"a series
of `X` on the DUART"*, and hangs. That is accurate. The emitter is `die_no_std()`
at `libs/bao1x-hal/src/sigcheck.rs:892` — in the crate boot0 calls, not in
`boot0/src` itself, which is why a grep confined to `boot0/src` misses it.

It is useless as a diagnostic on this board regardless: the `X`s go out the
**DUART, which Dabao does not route**, and the emitter's own status poll is
unbounded, so it parks on the first character. See
[Source review](#source-review-2026-08-15).

**Signature rejection is ruled out** on ordering grounds: boot0's failure message
(`Sigcheck err: {:?}`, `main.rs:367`) prints *after* the console is brought up,
and this board never brings the console up.

**4. `RST_N` and `PB1` are adjacent pins, not the same pin.** The bring-up guide
and the lab notebook both called header pin 30 `GPIO_PB1`; both are now
corrected. The board files show two distinct nets on two distinct balls:

| Header pin | Net | SoC ball | Symbol pin name |
|---|---|---|---|
| 29 | `/PB1` | A9 | `PB1` |
| 30 | `/RST_N` | H5 | `AORSTn` |

So the pin we have been pulsing is the **always-on-domain reset**, not a GPIO —
they simply sit next to each other, which is presumably how the two got merged.
Worse for recovery, **`XRSTn` (ball E5) — the chip's main external reset — is
bonded but unrouted**, so `AORSTn` and a power cycle are the only resets Dabao
exposes.

Pin *numbering* in the guide is fine, and worth recording because it looks wrong
at first glance: Dabao has **two 16-pin headers = 32 positions, numbered 1–16 and
25–40** (17–24 do not exist). That gap is why pin 36 and pin 40 are legitimate on
a 32-pin board. Every pin reference in `docs/05` checks out against the PCB —
15 = `/PB14`, 16 = `/PB13`, 30 = `/RST_N`, 36 = `+3.3V`, 40 = `VBUS` — which also
confirms the 2026-08-10 measurement labels.

**5. "Hardware JTAG is fused off" was asserted on insufficient evidence.** That
claim (added here on 2026-08-15) rested on the comment above `secboot.rs:43`.
boot1 only compares IFR `0x180..0x18F`; the JTAG-disable byte (`0x3a`) in the
factory blobs sits at `0x19F` and `0x28F` and is **not** covered — and
`ifr_0x280.bin` versus `ifr_0x280_jtag_disa.bin` differ in exactly that one byte,
so JTAG lockout is a per-lot factory choice, not a given. The *conclusion* holds
for a stronger reason: the board exposes no JTAG pins at all. See
[Source review](#source-review-2026-08-15).

**6. Silence bounds nothing about how far the kernel got.** The flashed image has
`stdout-path = &duart` with a bare `earlycon`, and the DUART earlycon is compiled
in (`CONFIG_SERIAL_BAO1X=y`) with an unbounded `DUART_SR_BUSY` poll. On this
silicon that poll wedges. So the kernel prints nothing whether it stopped at its
first instruction or ran to userspace — the console is the thing that fails. The
expected outcome is still that it stopped at the first `printk`, but that now
rests on the code path, not on the absence of output.

## Post-incident measurements (2026-08-10)

Bench setup changed to a logic analyzer on all relevant pins (no ESPHome).

| Measurement | Good board (`J0BTA9`) | Dead board (`VL7NR0`) |
|---|---|---|
| 3V3 (pin 36) | high | **high** |
| RUN / `RST_N` | high | **high** |
| VBUS current | ~38 mA | **14 mA** |
| PB14 (console Tx) after reset | `boot0 console up` decodes **244.2 ms** after `RST_N` release (cursors 119.481 → 363.678 ms) | **flat for 2.0 s** — 25 M samples @ 12.5 MHz, not one edge |

Two conclusions follow.

**The board is powered and almost certainly executing.** 14 mA is an order of
magnitude above leakage. This closes the original open question: it is not a
dead rail and not a board held in reset.

**It hangs; it does not loop.** One USB attach on plug-in, then zero events in
60 s unattended, port left `not attached`. A reset loop or paranoid-mode reboot
cycle would produce repeated attach events. It does not.

### Where the hang is

PB14 never toggling means execution stops **before `bao1x.rs:228`**
(`crate::println!("boot0 console up")`) — the first byte boot0 ever emits.

Candidate list *(revised 2026-08-15)*, in source order, all in
`boot0/src/platform/bao1x/bao1x.rs`:

| `bao1x.rs` | Gate |
|---|---|
| :45–47, :71 | `ro_trng.get_raw()` — raw TRNG read, no health check, **no timeout** |
| :57–:75 | `bollard!(die, 4)` canaries |
| :62 | `if paranoid1 != paranoid2 { die(); }` |
| :67, :77 | `paranoid_mode()` |
| :99 | DARIC CGU commit (`SFR_CGUSET = 0x32`) |
| :105 | DUART `SFR_ETUC = 34` — **boundary: a `die()` past this line emits one `X`** |
| :123 | `assert!(statics_in_rom.version == STATICS_IN_ROM_VERSION, "Can't find valid statics table")` — silent panic |
| :142 | `init_clock_asic_350mhz()` — **boundary: the 14 mA draw puts the hang at or before here** |

Everything *after* `:142` is excluded by the current measurement: `Csprng::new()`
and its TRNG stuck-value check (:148), `reset_sensors()` (:154), the SHA-512 KAT
(:201–211), and `setup_tx()` (:224).

An earlier version of this table also listed the `.unwrap()` on `:60`. It cannot
fire: `MAX_ONEWAY_COUNTERS = 8192/32 = 256` (`acram.rs:24`) and the two offsets
are compile-time constants (65, 67) below it.

Of the rows that remain, only `:62` is decided by persistent state — the canaries
fire only under glitching, and the rest are plain hangs. See
[the `:62` analysis](#bao1xrs62-is-the-only-state-dependent-way-to-die-before-the-pll).

## What was flashed (verified against the build host)

Only **`build/dabao/dabao-linux.uf2`** was ever written to the board — **4 times**,
matching the four flashes in the timeline, last at 21:25:15 with md5
`06412a3b80311684e5f8db22949acb32`.

The payload is a **dev-signed Baremetal image** (`--function-code baremetal`,
signed with `devkey/dev.key`):

| Address | Contents |
|---|---|
| `0x60060000` | signature block, 768 B, opens with `jal x0,+768` |
| `0x60060300` | SBI shim |
| `0x60070000` | XIP kernel |
| `0x60220000` | cramfs rootfs |
| `0x60360000` | limit |

**No boot0 or boot1 image was ever written. No `xtask` was run. No one-way
counter was ever written directly.** This matters more than anything else in
this document: it rules out bootloader corruption *by any host-side action*,
which is the first cause anyone will reach for. The bootloader on this board is
byte-identical to the one that shipped, and boot0 is write-protected against user
code by an IFR block burnt at OSAT (`blobs/ifr_0x280.bin`).

Two precisions, neither of which changes the conclusion. boot0 lives in
write-protected RRAM, not mask ROM — the distinction matters only to someone
later reasoning about rewriting it. And "no one-way counter was written
*directly*" is about host-side flashing; it does not cover what the *running
payload* could have reached, which is the subject of
[the adjacency hazard](#the-partition-adjacency-hazard-new-and-live).

`build-dabao-image.sh`'s own header warns that the first boot of a
developer-signed image **irreversibly burns `DEVELOPER_MODE` and erases factory
secrets**. This chip has been through that transition (boot #1, 13:59), and
`hardened_erase_policy` then re-ran on all five boots.

> **Loose end:** the timeline cites md5 `105b5045` for the never-flashed
> `SBI:se0-released` fix, while the build host's records show a second image
> `04a1e7f2…` built at 21:28:01 and never flashed. These may be different
> artifacts or one may be misrecorded; worth reconciling if it ever matters.
> Neither reached the board, so it does not affect any conclusion here.

## Source review (2026-08-15)

Desk work only — upstream `xous-core` read at default branch `dev`, HEAD
`f7d8c7e`, cross-checked against the measurements above. Nothing was flashed,
powered, or probed.

### `die()` is not ruled out

`die_no_std()` (`libs/bao1x-hal/src/sigcheck.rs:892`) zeroizes BUREG, AORAM, the
key regions, SCE_MEM, IFRAM0/1, UDC_MEM, BIO_MEM and all 2 MiB of SRAM, then:

```
// emit a loop out of DUART to indicate successful death
"li t0, 0x40042000",   // print 'X' (0x58)
"sw t1, 0x0(t0)",
"lw t3, 0x8(t0)",      // check SR
"bne x0, t3, 21b",     // wait for 0   <- unbounded
...
"j  22b",              // x17, park forever
```

Two things follow. The `X`s go to the **DUART, which this board does not route**,
and the poll is **unbounded** — the same defect class already confirmed fatal on
this silicon in our own shim (bring-up log, Bug 1). So a `die()` on a Dabao is
completely invisible and looks identical to a hardware hang: silent, permanent,
no reset loop, low current. That is the whole observed signature.

Signature rejection is ruled out on ordering grounds (see corrections), but that
does not rule out `die()`. It has many pre-console triggers with nothing to do
with signature checking — the `bollard!` canaries, the `paranoid1 != paranoid2`
check, and the `.unwrap()` panic paths in the table above. Any of them produces
exactly what was measured.

### The current measurement narrows the window

`FREQ_OSC_MHZ = 48` (`libs/bao1x-api/src/clocks.rs:3`): pre-PLL the core runs at
48 MHz, and `init_clock_asic_350mhz()` (`bao1x.rs:142`) takes it to 350 MHz CPU /
700 MHz fclk. The good board draws 38 mA sitting in boot1's REPL at that speed;
the dead board draws 14 mA. **A core spinning at 350 MHz cannot read 14 mA**, so
the hang is at or before `:142`.

Limit of the inference: it establishes *where in the sequence*, not *which of two
states*. A gate that hung and a `die_no_std()` that already parked both sit at
48 MHz and both read the same. Nothing measured so far separates them.

### There is no way in, and no way out

Each of these is a separate closed door. Together they leave nothing on this
bench.

- **boot0 samples no external input before it stops.** Its only IOX use is
  configuring PB13/PB14 inside `setup_tx`/`setup_rx` (`debug.rs:147–152`), called
  at `:218`/`:224` — *after* the hang point. No button, strap, or pin is read, so
  no key combination, serial break, or timing trick exists.
- **boot0's REPL is not in the shipped build**: `unsafe-dev` is absent from
  `default` (`boot0/Cargo.toml:39`).
- **boot0 cannot be rewritten** — write-protected at OSAT, and never written from
  here (see above).
- **The board exposes no JTAG.** The die has TAPs — `jtagvex` (VexRiscv debug, on
  dedicated pads `PAD_JTCK`/`JTMS`/`JTDI`/`JTDO`/`JTRST`) and `jtagrrc[0:1]` (RRAM
  controller) — but the Dabao board files contain no JTAG net and no unconnected
  JTAG ball; the only JTAG strings in the schematic are annotation blocks
  describing the probe-station scan mapping. The RRC TAPs are additionally gated
  by `cmstest` in RTL (`pad_frame_arm.sv:233–235`,
  `jtagrrc[0].tms = cmstest & patestpi4`), i.e. wafer-probe mode only.
- **The board exposes no hard reset either.** `/RST_N` lands on `AORSTn`; `XRSTn`
  is unrouted (correction 4). Power cycling has already been tried.
- **The one observable is unreachable.** `die_no_std()` writes a single `X` to the
  DUART *before* its unbounded poll, so even a wedged DUART emits one character —
  but ball D3 is `unconnected-(U1B-DUART-PadD3)`, bonded to the package and routed
  nowhere. Under a WLCSP that is not probeable without die access.
- **One-way counters only count up.** Even knowing which counter is wrong,
  re-syncing it requires executing code — exactly what is unavailable.

Everything drivable — boot1's REPL, PROG+RESET, `uf2send.py` over the console
UART — lives *downstream* of where the chip stops. **Recovery on this bench is not
possible.** The only remaining path is vendor failure analysis: Baochip's CP flow
re-enables the RRC TAPs under `cmstest`, which is the sole mechanism that can read
or rewrite RRAM and the one-way counters on a packaged part.

### `bao1x.rs:62` is the only state-dependent way to die before the PLL

Combining the two results above: the hang is at or before `:142`, and within that
window the only paths reaching `die()` are the `bollard!` canaries (`:57`–`:75`)
and `paranoid1 != paranoid2` (`:62`). The canaries fire only if the program
counter is glitched into them. **So `:62` is the one gate in the pre-PLL window
where persistent non-volatile state, rather than a transient event, decides the
outcome** — and a `die()` is the only mechanism found that reproduces the whole
measured signature at once: silent, permanent, no reset loop, low current, on a
board that routes no DUART.

The check is exact equality: `hardened_get2` sums five reads of each counter, so
any divergence at all — by one — is a permanent, silent, pre-console brick.

**Ruled out: reset timing during `paranoid enable`.** That command
(`repl.rs:505–506`) makes two separate, non-atomic `inc()` calls, and a reset
between them desyncs the pair unrecoverably. It is a real footgun worth knowing
about, but it is not what happened here:

1. Those two lines are the **only** writers of either counter in the whole tree.
2. Issuing the command needs boot1's REPL, which did not exist in the failure
   window (21:25:25 → ~21:30, shim and kernel running).
3. boot0 evaluates `:62` on every boot, so `65 == 67` was demonstrably true as
   late as boot #5 at 21:25:25.

### The partition-adjacency hazard (new, and live)

The one writer available in the failure window is an errant RRAM write from the
kernel. The geometry is exact, and the margin is zero:

```
data partition  0x60360000 .. 0x603DA000   0x7A000 = exactly 122 x 4 KiB
OWC array       0x603DA000 .. 0x603DC000   256 slots x 32 B   (acram.rs:20)
  slot 65  PARANOID_MODE       0x603DA820
  slot 66  POSSIBLE_ATTACKS    0x603DA840
  slot 67  PARANOID_MODE_DUPE  0x603DA860
```

Our writable JFFS2 partition **ends on the first byte of the one-way counter
array**, and the MTD device maps `<0x60000000 0x400000>` straight through it to
the IFR base. `mtd.erasesize = SZ_4K` and 4096/32 = 128, so **one erase block past
the partition end covers slots 0–127 — including all three above.** The partition
is an exact multiple of the erase size, so this takes a genuine off-by-one rather
than rounding. The page is plausibly writable from the payload: `inc()` is a raw
`write_volatile` with no ACL check, slots 128–255 are reserved "for user
applications", and `seal_boot1_keys()` concedes the coreuser mechanism only stops
an arbitrary-*read* primitive, not executing code.

As a cause this is unproven, and the evidence cuts against its most likely form. A
uniform write across that block increments 65 and 67 **equally** — they stay equal
and both go non-zero, which passes `:62` and instead triggers `:66
paranoid_mode()`, whose `SFR_VDMASK1 = 0` is commented *"makes the chip reset on
glitch detect"*. That predicts a **reset loop**, which the 08-10 measurement
specifically excludes; and a boot passing both gates would reach the console and
print. Only the *differential* case fits — the write terminating between slot 65
and slot 67, a 64-byte window inside a 4 KiB block. That matches the data exactly
but needs a specific landing spot.

Net: the leading hypothesis, not a finding, and cheap for the vendor to settle.
It is also a **live hazard for `J0BTA9`** regardless of what killed `VL7NR0`.

### Control-group gap

`J0BTA9` is the control for every measurement in the table above, but it is not
known to have been through the DEVELOPER_MODE transition — the one irreversible
change made to `VL7NR0`. If it has not, the comparison is uncontrolled exactly
where it matters most. Worth establishing before leaning on the table further.
Burning dev mode on `J0BTA9` to close the gap would spend the only working board
and is **not** recommended.

## What to try next (2026-08-15)

Steps 1–2 are the last things this bench can contribute and both are low-yield;
step 3 is the actual path forward. The previous plan's DUART-probe step is
**withdrawn** — ball D3 is unrouted under the package.

1. **Long unpowered soak.** Everything on 08-10 was `RST_N`, the RESET button, or
   a replug, and `AORSTn` is the only reset the board exposes. De-power fully for
   ≥30 min, then plug in with the analyzer armed on PB14. Low probability,
   near-zero cost, needs no new gear.
2. **Current trace over the first 500 ms after `RST_N` release, both boards.** The
   good board should show a step as the PLL engages, ahead of its 244.2 ms first
   byte. No step on the dead board confirms the hang is at or before `:142`. The
   bench power meter will do if it logs fast enough; otherwise a shunt and a scope.
3. **Escalate to Baochip.** The highest-value single ask is a read of one-way
   counter slots **65 (`PARANOID_MODE`), 66 (`POSSIBLE_ATTACKS`) and 67
   (`PARANOID_MODE_DUPE`)** off this die — that one measurement decides the leading
   hypothesis. If 65 ≠ 67, that is the answer. If they are equal and zero, the
   whole one-way-counter family is dead and the remaining candidates are the
   non-`die()` hangs in the same window: the raw TRNG reads at `:45`/`:71`, the CGU
   commit at `:99`, or the statics `assert!` at `:123`. Also worth asking: the rest
   of the OWC array and the ACRAM data slots; whether `ifr_0x280` or
   `ifr_0x280_jtag_disa` was burnt on this lot; whether there is a known
   pre-console failure mode in boot0 `v0.10.0`; and whether RMA applies.
4. **Before flashing `J0BTA9` — fix the port first.**
   - **Put a guard band below `0x603DA000`.** Shrink `data_part` and drop the MTD
     device's `reg` so that nothing in our port can address the one-way counter
     array at all.
   - Redo the three fixes the bring-up log records as done but which were made in
     gitignored trees and are **still missing** (re-verified 2026-08-15):
     `linux/patches/v6.14/0013-*.patch:112` still has the unbounded
     `DUART_SR_BUSY` spin, `0022-*.patch:309–310` still has `earlycon` +
     `stdout-path = &duart`, and `firmware/bao1x-sbi/` still has no SE0/IOX code.
   - Refresh `linux/patches/` from `sources/linux` as part of the build, so
     kernel-side work stops evaporating between sessions.
   - Do not reset the board mid-boot while it runs a dev-signed image — every such
     boot writes RRAM via `hardened_erase_policy`.

## Stash — deferred, not done

Parked deliberately. Nothing below has been attempted; no conclusion in this
document depends on it.

### Rebuild the flashed image for hash-level proof

The build host offered to stash current work and rebuild md5
`06412a3b80311684e5f8db22949acb32` from source, to confirm at hash level that the
image actually flashed carried the unbounded `duart_puts()` spin. Deferred as
low value — the boot #1 hang is already explained by the captured UART log, and
the failure under investigation is upstream of anything the payload can reach.

## Later observation (2026-08-16)

A reset now brings up a **USB device at full speed** that still never completes
enumeration. Recorded here because it changes what the evidence rules out, not
because it resolves anything:

- boot1 brings its port up at **high** speed unless the `UsbDefaultSpeed`
  one-way counter says otherwise (`boot1/src/main.rs:239`). A full-speed device
  is therefore *not* boot1's USB stack in its default configuration.
- The port being electrically present at all means SE0 is no longer asserted —
  consistent with the reset returning PC13 to an input, as boot1's
  `setup_dabao_boot_pin()` would.
- Something answering at full speed but failing `device descriptor read/64`
  with `-71` is the same physical-layer signature seen from ~14:15 onward, four
  hours before the failure.

The analysis of what can and cannot be recovered from this state, and what the
firmware could and could not have damaged, is in
`08-recovery-and-risk-ladder.md`. Short version: the boot chain in RRAM is
almost certainly intact — the kernel hung in its DUART earlycon before the MTD
driver ever probed — see
[Post-incident measurements](#post-incident-measurements-2026-08-10) for the
VBUS/3V3 readings that already settled whether the board was powered.
