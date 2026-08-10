# Dabao board: everything done to it, up to it going unresponsive

Session 2026-08-07. Times are wall-clock from the UART capture logs in
`build/hw/*.txt`. Deliberately kept in `build/` (gitignored) — not committed.

Board: Dabao, boot0/boot1 `v0.10.0-61-g5397e1b48` (`bao2-0`), serial `VL7NR0`.

## Timeline

| Time | Event |
|---|---|
| 13:47 | First contact. Enumerates cleanly, **USB high-speed (480 Mbps)**, CDC console + `BAOCHIP` mass storage. boot0/boot1 banners on UART2 @ 1 Mbaud. `CPU @ 350MHz`, board type Dabao, `bootwait` enabled, clock skipping off. Board fully healthy. |
| 13:57 | Flash #1 — `dabao-linux.uf2` md5 `76f057ea`, copied to the `BAOCHIP` volume. |
| 13:59 | **Boot #1.** `Stopping USB… / Booting with key 3/3(dev ) / Developer key detected, ensuring secrets are erased` → **DEVELOPER_MODE burnt (irreversible, pre-approved)**. Then silence (later diagnosed: `duart_puts()` spinning on an unrouted DUART). |
| 14:08 | Physical RESET by user. **Board recovers completely** — boot0/boot1 up, USB fine. |
| 14:12 | **Boot #2** (same image, cold cache, to test I-cache staleness). Same hang. |
| ~14:15 | User unplugs/replugs; board ends up on a **hub chain** (`1-1.1.1`). USB enumeration starts failing: `-32`/`-71`, `attempt power cycle`, **full-speed only**. |
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
- **UART loopback test PASSED** (probe TX shorted to probe RX): 29/29 bytes byte-exact at 1 Mbaud → the probe, its USB path, both leads and the host software are all good.
- Console leads re-seated and re-checked against the pinout (pin 15 = PB14, pin 16 = PB13).
- **Never measured:** VBUS (pin 40) and 3V3 (pin 36). The board has **no LED**, so power was never confirmed either way.
- **Never changed:** the USB cable.

## Facts worth noting (no causal claim)

- **Every one of the 5 boots left the USB SE0 pin asserted.** boot1's `boot` drives PC13 low and leaves it that way, expecting the next stage to release it (`README-baochip`: *"it is up to the next USB stack to de-assert this"*); our payload never did. The EMS4000 switch is powered from **VBUS**, so `RST_N` cannot clear it — only a physical unplug can. This matches the observed pattern exactly: resets never restored USB, physical replugs did (twice). A fix exists (`SBI:se0-released`, md5 `105b5045`) but **has never run on hardware**.
- **`hardened_erase_policy` ran on all 5 boots** — `Developer key detected, ensuring secrets are erased` was printed each time. Whether repeated erase cycles matter is unknown to me.
- The board recovered from boots #1–#4 without issue. Only after boot #5 — the first boot where the shim completed and actually `mret`'d into the **Linux kernel** — did it stop responding. That is a correlation, not a demonstrated cause; the kernel got no further than its first instruction as far as we can tell, since it printed nothing.
- The USB degradation (high-speed → full-speed, `-71`) began at ~14:15, hours *before* the final failure, and cleared twice on replug.

## Open question

Whether the board is powered and executing at all is **undetermined**. The decisive
unmade measurement is GND (pin 13) to **VBUS (pin 40, ~5 V)** and **3V3 (pin 36,
~3.3 V)**; and the untried variable is a **different USB cable**.
