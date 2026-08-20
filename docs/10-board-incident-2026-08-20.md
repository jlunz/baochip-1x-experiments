# Dabao board `J0BTA9`: kernel panic on first Linux boot, then SILENT

Session 2026-08-20. Times are wall-clock from the UART capture logs in
`docs/serial_traces/*.log` (committed, unlike `07`'s `build/hw/*.txt` — see
that document's own note on why this session changed the convention).

Board: Dabao, boot0/boot1 `v0.10.0-61-g5397e1b48` (`bao2-0`), serial
`J0BTA9`. **This is the board `07` describes as "in use since 2026-08-07
22:42 and remains healthy," serving as the control for every measurement in
that document.** It is no longer either. `09`'s board-allocation table
assigned it "block C only, forever... Never dev-signed, never flashed" —
that plan was deliberately overridden this session (see
`docs/04-bringup-log.md`, "Deliberate deviation" entry), with the
consequence recorded here.

## Timeline

| Time | Event |
|---|---|
| ~10:20 | Bench setup: probe wired, `RST_N` on ESPHome GPIO 15 confirmed. |
| ~10:43 | **C4**: `audit` — no `== IN DEVELOPER MODE ==`. Confirmed never-dev-moded, closing `07`'s control-group gap. `Next stage: key 2/2 (beta) -> 60060000` — still vendor firmware. |
| ~10:56 | **D5**: `audit` clean, `bootwait check` → `Enable`. Board's `boot1` command hint list found to lack `require-pq` and `usb_speed`, present in the currently-fetched `xous-core` HEAD — this board's `boot1` predates both, and likely predates that HEAD's "fix the unsigned boot header vulnerability" (PR #945) too. |
| 11:03–11:05 | **E1/E2/E3**: `dabao-shim-only.uf2` (md5 `9d0a6d0a…`) flashed over UART, 27/27 blocks. `Next stage: key 3/3 (dev ) -> 60060000`. Diff against D5: only that one line changed. |
| 11:07:09 | **F2 — first dev-signed boot.** `boot` issued. Full marker sequence to `SBI:shim-only -- not entering kernel`. `SBI:alive` heartbeat confirmed subsequently, steady 1.000s apart. |
| ~11:41 | **F4** (SE0 test, USB-C reconnected for this check only): reset via `board-reset.py`, no physical replug — USB re-enumerated within ~500ms. Pass. |
| 11:43:08 | **F5**: `audit` (over native CDC, console had moved there) — `== IN DEVELOPER MODE ==` present, `Erase proof: erased`, security footer. Diff against E2: exactly those three lines, nothing else. |
| 11:44–12:14 | **G1/G2**: full image rebuilt (md5 `13a5ca7f…`, matching A8). USB-C disconnected again. Flashed 11584 blocks over UART — **11584/11584 successful, 1 retry total**. Post-flash `audit`: `Next stage: key 3/3 (dev ) -> 60060000`. |
| ~12:16:5x | **G3.** `boot` issued a second time (this board's second-ever dev-signed boot) via `tools/boot1-cmd.py`. First reply chunk read only `boot\nStopping USB...` before `boot1-cmd.py`'s own 0.6s-since-last-byte deadline cut the read off; **this specific reply was not saved to a file**, only observed live — a gap in capture discipline, noted rather than papered over. |
| 12:17:09.294–.327 | **Passive `tio` capture running throughout** (`docs/serial_traces/20260820_121709_g3-g4-kernel-boot.log`) picked up the continuation cleanly: `SBI:dtb-copied` through `SBI:csr-done`, `bao1x-sbi: jumping to kernel`, full Linux banner, memory zone setup, kernel command line — then, at **12:17:09.316**, `Oops - illegal instruction [#1]`, ending at **12:17:09.327** with `Kernel panic - not syncing: Fatal exception in interrupt`. Elapsed from `SBI:dtb-copied` to panic: **32 milliseconds.** This is the first time this kernel build has ever run far enough on real hardware to reach `time_init()` — every earlier attempt, this session and `07`'s, hung earlier (DUART/earlycon, before today's fixes). |
| 12:18:0x | `board-reset.py` pulsed to recover to the REPL. **Silence.** |
| 12:18:39 | `boot1-cmd.py` bare nudge. **Silence.** |
| 12:18:52 | Passive `tio` listen, 8s window. **Silence — zero bytes, not even the boot0 banner.** |
| ~12:19 | `board-triage.py`, own independent reset+6s capture: **SILENT — 0 bytes, not even framing errors.** USB (`1d50:6196`) absent. |
| ~13:xx | User measured GND(13)→VBUS(40) and GND(13)→3V3(36), both present. Separately measured ~24mA on a PPK2 — active-execution current, not leakage. |
| 14:06:52 | Built `tools/board-prog.py` (PROG wired to a second ESPHome GPIO, 14). `prog-reset` sequence run — hold PROG, pulse RESET, wait 1s, release PROG, all state-verified via the switch's own reads. **0 bytes over a 12s window.** |
| 14:07:33 | `prog-reset` repeated with output streams properly separated this time (the first run's terminal output had been visually interleaved with the capture, though the capture file itself was unaffected). **0 bytes over a 20s window.** |
| ~14:2x | **Full power cycle** — both VBUS and USB-C removed entirely, via PPK2 as the source, then reapplied. `board-triage.py` against the freshly-powered board: **still SILENT, 0 bytes over 6s.** |

## After 12:18 — what was tried, and the result

- `board-reset.py`, plain `RST_N` pulse (default 0.2s hold) — no output, twice independently (once directly, once via `board-triage.py`'s own reset).
- **PROG + RESET**, via the newly-built `board-prog.py`, run twice with independently verified correct assert/release of the PROG line both times — no output, over both a 12s and a 20s window.
- **Full power cycle** (not just a reset line — both supply rails removed and reapplied) — no output.
- Power measured directly: VBUS and 3V3 both present. Current draw ~24mA on a PPK2 — ruled out a dead rail or a chip held in analog reset.
- Continuity of the probe-to-board UART link was **not** independently re-checked with a meter, but is strongly implied intact: the kernel boot transcript captured moments before the silence began is itself clean, multi-kilobyte data over the exact same physical link, and nobody touched the wiring in the intervening seconds.
- USB: `1d50:6196` absent throughout every check after the panic.

Three recovery vectors of increasing strength (soft reset, PROG-forced REPL
entry, full cold power cycle) all produced the identical result. Nothing
tried distinguishes "boot0 hung and would eventually recover" from
"permanent."

## Facts worth noting (no causal claim)

- **The panic happened 32ms after `SBI:dtb-copied`, on the shim's second
  dev-signed boot of this board's life, and the very first time this kernel
  build reached `time_init()` on real hardware.** All of `07`'s five boots
  (a different board, `VL7NR0`) and this session's own F2 boot (shim-only,
  never entering the kernel) predate anything reaching this code path.
- **A normal Linux kernel panic does not write to RRAM, fuses, or one-way
  counters.** `Kernel panic - not syncing` halts the CPU (spin or `wfi`); it
  has no documented interaction with `bao1x.rs:62`'s pre-console equality
  check or any `acram.rs` counter. The panic and the silence are temporally
  adjacent, not causally connected by anything found in source so far — the
  same caution `07` applies to its own boot-then-hang correlation.
- **`A7`'s `linux.robot` Renode gate passed clean the same morning**
  (`docs/04-bringup-log.md`, block A entry, this session). If this panic
  does not reproduce under Renode, it is a second instance of the exact
  blind-spot pattern `07` already established once (2026-08-07 log, "two
  more Renode blind spots"). Not yet tested — see
  [What to try next](#what-to-try-next).
- **This board's `boot1` predates `require-pq`/`usb_speed`** relative to the
  `xous-core` HEAD fetched this session, and by extension likely predates
  that HEAD's PR #945 ("fix the unsigned boot header vulnerability").
  Recorded at D5 (`docs/04-bringup-log.md`), before any of this happened.
  Not established to be relevant to the panic or the silence — boot0/boot1
  never re-enter the picture after `bao1x-sbi: jumping to kernel` on a
  normal boot — but the age of this board's firmware relative to upstream is
  now a standing fact worth keeping in mind for anything found later.

## Source review: the panic

`Oops - illegal instruction [#1]` at `epc: get_cycles64+0x0/0x12` — faults
on the function's very first instruction. Call chain from the trace:
`start_kernel → time_init → timer_probe → riscv_timer_init_dt →
sched_clock_register → get_cycles64`.

`get_cycles64()` (`arch/riscv/include/asm/timex.h:66-77`) on a 32-bit,
non-M-mode build (this port: `CONFIG_64BIT` unset, S-mode Linux per the
hardware dossier) reduces to a loop of `get_cycles_hi()` /
`get_cycles()` — `csr_read(CSR_TIMEH)` and `csr_read(CSR_TIME)`, i.e. a raw,
unconditional `csrr` of CSRs `0xC81` / `0xC01`. **The trap's own `badaddr`
field reads `c81027f3`** — `0xc81` is exactly `CSR_TIMEH`. The call site
(`drivers/clocksource/timer-riscv.c:176`,
`sched_clock_register(riscv_sched_clock, 64, riscv_timebase)`, with
`riscv_sched_clock` a thin wrapper over `get_cycles64()`) has no probing, no
fallback, no SBI-TIME-extension check — this is stock upstream
`riscv_timer_init_common()`, untouched by this project's own patch series.

This project's own firmware has already documented needing exactly this
kind of probing, for a related but distinct CSR. `firmware/bao1x-sbi/main.c`
(comment, lines 95-99):

> "cycle/instret visible to S and U (no time CSR to enable)... the Dabao's
> VexRiscv does not implement them and traps (measured: mcause=2,
> mtval=0x3063d073 = `csrwi mcounteren, 7`)... Neither 'always write' nor
> 'never write' works on both [this silicon and Renode], so attempt it and
> accept a trap as 'absent'."

That `mtval=0x3063d073` is the same trap `07`'s Boot #4 hit
(`SBI:FATAL M-mode trap mcause=0x2 mepc=0x600604f0 mtval=0x3063d073`,
2026-08-07 21:03) — already known, already worked around, in the shim. The
shim's own boot markers this session show the same absence again:
`SBI:mcounteren-absent`, `SBI:scounteren-absent`, both printed cleanly
before `SBI:csr-done`, exactly as designed.

**The gap is that the shim's workaround covers `mcounteren`/`scounteren`
(the S/U-mode counter-access permission CSRs), not `time`/`timeh`
themselves.** The shim's own comment asserts "no time CSR to enable" —
read naturally as "nothing to do here," not as "this CSR pair doesn't
exist." Today's evidence says otherwise: on this VexRiscv configuration,
`time`/`timeh` are not implemented as readable CSRs at all (from any
privilege level, on the evidence available — the trap is on the read
itself, not gated by a counteren bit that was left clear), and nothing
upstream in `riscv_timer_init_common()` expects that to be possible on a
system that advertises the SBI TIME extension (which this shim does — `SBI
TIME extension detected` is in every boot's banner, this session included).
Upstream Linux's assumption that `time`/`timeh` are always directly
readable from S-mode when SBI TIME is present does not hold on this
silicon.

**Not yet done:** confirming this reproduces (or doesn't) in Renode, and
identifying the actual fix — either trap-and-emulate `time`/`timeh` reads in
the SBI shim's trap handler (consistent with the SBI TIME extension
contract), or patch the kernel's timer driver to probe before registering
`get_cycles64` as the sched_clock source, the same shape of fix already
applied to `mcounteren`/`scounteren` in the shim.

## What the silence means — not established, best-supported explanation

`docs/09`'s own tripwire section (`## The tripwire: audit`) already
describes the one gap in its entire diff-based safety story: **a
`Paranoid mode: a/b` desync (`bao1x.rs:62`) is checked by boot0 before any
console exists, on every reset — so if a rung desyncs the pair, the first
symptom is a silent brick on the very next reset, not a diff that catches
it.** That is worded as a hypothetical limitation when it was written
earlier this session. It now describes, as closely as anything else
considered, what has actually been observed:

- Silent — no boot0 banner, ever, on any of five independent
  reset/power-cycle attempts.
- Permanent across everything triable from this bench, including a full
  power cycle, which rules out any explanation resting on volatile state
  (a hung loop, a stuck peripheral, a latched-but-clearable glitch flag).
- Consistent with power good and non-trivial current draw — the chip is
  doing *something*, just never reaching a byte of console output, which
  fits a `die()`-shaped pre-console halt far better than a dead rail.

This is circumstantial, not proven, and the panic that preceded it does not
supply a demonstrated mechanism (see above — ordinary kernel panics do not
write RRAM). No one-way counter can be read without the console that is
missing, which is the same closed loop `07`'s own "There is no way in, and
no way out" section already worked through for a different board reaching
the same state. Everything in that section — boot0 samples no external
input before it stops, boot0's REPL isn't in this build, boot0 can't be
rewritten, no JTAG is exposed, no hard reset exists beyond what's already
been tried, the one DUART observable is unrouted on Dabao — applies here
unchanged.

## What to try next

1. **Reproduce the panic in Renode.** If it doesn't reproduce, that is
   itself worth recording precisely — a third documented instance of
   emulation not catching a real-hardware timer/CSR gap, following the two
   `07` already logged.
2. **Fix the `time`/`timeh` gap** (SBI-side trap-and-emulate, or a kernel
   probe) before this kernel build is ever booted again, on any board.
   Whatever caused the silence, there is no reason to hand a second board
   the same kernel panic to find out.
3. **Long unpowered soak**, matching `07`'s own step 1 for the same
   situation — de-power for longer than the few minutes tried here
   (hours, not the PPK2's immediate off/on), on the chance this is a
   slower-clearing latch than a plain power cycle catches. Low probability,
   near-zero cost, given `07` already tried a shorter version on a
   different board with no result.
4. **If a second board is used next** (the "other fine board" mentioned
   earlier this session, not yet brought into this run sheet): do not
   repeat G3 with the current kernel build. Confirm item 2 first.
5. **Escalation**, if this board's state needs to be known with certainty
   rather than inferred: the same ask `07` already made and never got an
   answer to — a read of one-way counter slots 65/66/67 off the die itself,
   which is the one measurement that would turn "best-supported
   explanation" into a fact.
