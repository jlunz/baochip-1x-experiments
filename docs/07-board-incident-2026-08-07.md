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

> **Update 2026-08-15.** A source review against upstream `xous-core` narrows the
> hang window using the current measurement, establishes that a silent `die()`
> fits every observation, and closes out recoverability — see
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
- The board recovered from boots #1–#4 without issue. Only after boot #5 — the first boot where the shim completed and actually `mret`'d into the **Linux kernel** — did it stop responding. That is a correlation, not a demonstrated cause; the kernel got no further than its first instruction as far as we can tell, since it printed nothing.
- ~~The USB degradation (high-speed → full-speed, `-71`) began at ~14:15, hours *before* the final failure.~~ **Retracted — see corrections.**

## Corrections

Two claims above were wrong *(2026-08-10)*, and one piece of upstream
documentation turns out to be right *(2026-08-15)*.

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
| :60 | `hardened_get2(PARANOID_MODE, PARANOID_MODE_DUPE).unwrap()` — panics on a failed read |
| :62 | `if paranoid1 != paranoid2 { die(); }` |
| :67, :77 | `paranoid_mode()` |
| :99 | DARIC CGU commit (`SFR_CGUSET = 0x32`) |
| :105 | DUART `SFR_ETUC = 34` — **boundary: a `die()` past this line emits one `X`** |
| :123 | `assert!(statics_in_rom.version == STATICS_IN_ROM_VERSION, "Can't find valid statics table")` — silent panic |
| :142 | `init_clock_asic_350mhz()` — **boundary: the 14 mA draw puts the hang at or before here** |

Everything *after* `:142` is excluded by the current measurement: `Csprng::new()`
and its TRNG stuck-value check (:148), `reset_sensors()` (:154), the SHA-512 KAT
(:201–211), and `setup_tx()` (:224).

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
this document: it rules out bootloader corruption, which is the first cause
anyone will reach for. The bootloader on this board is byte-identical to the one
that shipped, and boot0 itself is in immutable ROM burnt at OSAT — it cannot be
overwritten by any host-side action taken here.

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

### There is no software recovery path

Verified in source, and this closes the question rather than deferring it:

- **boot0 samples no external input before it stops.** Its only IOX use is
  configuring PB13/PB14 inside `setup_tx`/`setup_rx` (`debug.rs:147–152`), called
  at `:218`/`:224` — *after* the hang point. No button, strap, or pin is read.
- **boot0's REPL is not in the shipped build**: `unsafe-dev` is absent from
  `default` (`boot0/Cargo.toml:39`).
- **Hardware JTAG is fused off.** `boot1/src/secboot.rs:43–49` checks IFR at
  `0x60400180` against a reference value meaning the Cortex-M7 and hardware JTAG
  debug are both disabled.
- **boot0 is immutable**, burnt at OSAT, and per the section above it was never
  written to from here anyway.

Everything drivable — boot1's REPL, PROG+RESET, `uf2send.py` over the console
UART — lives *downstream* of where the chip stops. There is no key combination,
serial break, or timing trick that reaches it. Recovery on this bench is not
possible; the remaining path is the vendor.

### Two open items, both unresolved

**Control-group gap.** `J0BTA9` is the control for every measurement in the table
above, but it is not known to have been through the DEVELOPER_MODE transition —
the one irreversible change made to `VL7NR0`. If it has not, the comparison is
uncontrolled exactly where it matters most. Worth establishing before leaning on
the table further. Burning dev mode on `J0BTA9` to close the gap would spend the
only working board and is **not** recommended.

**Unverified hypothesis: a torn one-way-counter write.** `VL7NR0` was reset
mid-boot several times (14:08, 21:02, 21:22), and every dev-mode boot writes RRAM
via `hardened_erase_policy`; `die()` additionally increments `POSSIBLE_ATTACKS`.
A reset landing inside an increment to one half of a duplicated pair would leave
`paranoid1 != paranoid2`, and `bao1x.rs:62` turns that into a permanent, silent,
pre-console `die()` on every subsequent boot. This fits every measured number and
is the only mechanism found so far that distinguishes this board from the
control. **It is a hypothesis, not a finding** — confirming it needs a read of
the OWC array, which requires code we can no longer run.

## What to try next (2026-08-15)

Ordered by information per unit of effort. Steps 1–3 are the last things this
bench can contribute; step 4 is the actual path forward.

1. **Long unpowered soak.** Everything on 08-10 was `RST_N`, the RESET button, or
   a replug; nothing has cleared the always-on domain. De-power fully for ≥30 min,
   then plug in with the analyzer armed on PB14. Low probability, near-zero cost,
   needs no new gear.
2. **Probe the DUART TX pad — the one decisive measurement left.** `die_no_std()`
   writes one `X` to TXD *before* its unbounded poll, so even a wedged DUART emits
   a single character. A character ⇒ `die()` fired ⇒ this is a security abort, not
   a hardware hang, and `POSSIBLE_ATTACKS` has been incremented. Flat ⇒ a plain
   hang, or a `die()` before the DUART is enabled at `:105`. Check the package
   first: BOOTCHAIN says WLCSP for standard parts, so "not physically reachable"
   is a legitimate outcome.
3. **Current trace over the first 500 ms after `RST_N` release, both boards.** The
   good board should show a step as the PLL engages, ahead of its 244.2 ms first
   byte. No step on the dead board confirms the hang is at or before `:142` and
   cuts the candidate table roughly in half. The bench power meter will do if it
   logs fast enough; otherwise a shunt and a scope.
4. **Escalate to Baochip.** Concrete asks: can they read the OWC array off this
   die (`POSSIBLE_ATTACKS`, `PARANOID_MODE`/`_DUPE`, `DEVELOPER_MODE`) and the
   ACRAM data slots; is there a known pre-console failure mode in boot0
   `v0.10.0`, particularly after repeated `hardened_erase_policy` runs following
   the dev-mode transition; is `PARANOID_MODE` / `PARANOID_MODE_DUPE`
   desynchronization a known hazard, given `bao1x.rs:62` makes any divergence a
   permanent silent brick; and is RMA appropriate.
5. **Protect `J0BTA9`.** Do not reset it mid-boot while it is running a
   dev-signed image — every such boot writes RRAM via `hardened_erase_policy`.
   And do not flash it with the current tree: the three fixes recorded in the
   bring-up log as done were made in gitignored trees and are **still missing**
   (re-verified 2026-08-15) — `linux/patches/v6.14/0013-*.patch:112` still has
   the unbounded `DUART_SR_BUSY` spin, `0022-*.patch:309–310` still has
   `earlycon` + `stdout-path = &duart`, and `firmware/bao1x-sbi/` still has no
   SE0/IOX code at all. Redo them first, and refresh `linux/patches/` from
   `sources/linux` as part of the build so kernel-side work stops evaporating.

## Stash — deferred, not done

Parked deliberately. Nothing below has been attempted; no conclusion in this
document depends on it.

### Rebuild the flashed image for hash-level proof

The build host offered to stash current work and rebuild md5
`06412a3b80311684e5f8db22949acb32` from source, to confirm at hash level that the
image actually flashed carried the unbounded `duart_puts()` spin. Deferred as
low value — the boot #1 hang is already explained by the captured UART log, and
the failure under investigation is upstream of anything the payload can reach.
