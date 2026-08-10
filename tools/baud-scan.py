#!/usr/bin/env python3
"""Hunt for boot0/boot1 output at an unknown baud rate.

If the SoC comes up on a different reference clock than boot0 expects (dead or
mis-starting 48MHz crystal, wrong PLL), the console keeps transmitting but the
bit rate moves. Everything then looks dead at the nominal 1Mbaud even though
the chip is executing fine -- and USB fails at the same time, for the same
reason. This resets the board once per candidate rate and scores what comes
back, so a shifted clock shows up as readable text at some other baud.

  tools/baud-scan.py --port /dev/serial/by-id/...-if01
"""

import argparse
import json
import sys
import time
import urllib.request

import serial

CANDIDATES = [
    1000000,  # nominal
    500000, 2000000, 250000, 750000, 1500000,
    921600, 460800, 230400, 115200, 57600, 38400, 19200, 9600,
]

RESET_HOST = "192.168.42.38"
SWITCH = "GPIO%20Switch%2015"


def reset_board(host: str, hold: float = 0.3) -> None:
    for action in ("turn_off", "turn_on"):
        req = urllib.request.Request(
            f"http://{host}/switch/{SWITCH}/{action}", data=b"", method="POST")
        req.add_header("Content-Length", "0")
        try:
            urllib.request.urlopen(req, timeout=5)
        except Exception as e:
            print(f"  (reset {action} failed: {e})", file=sys.stderr)
        if action == "turn_off":
            time.sleep(hold)


def score(data: bytes):
    """Printable ratio plus a bonus for strings boot0/boot1 actually emit."""
    if not data:
        return 0.0, ""
    printable = sum(1 for b in data if 32 <= b < 127 or b in (10, 13))
    ratio = printable / len(data)
    text = data.decode("utf-8", errors="replace")
    bonus = 0.0
    for needle in ("boot0", "boot1", "Baochip", "console", "up!", "CPU", "SBI"):
        if needle in text:
            bonus += 1.0
    return ratio + bonus, text


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", required=True)
    ap.add_argument("--host", default=RESET_HOST)
    ap.add_argument("--listen", type=float, default=3.0)
    ap.add_argument("--bauds", default="")
    args = ap.parse_args()

    bauds = [int(b) for b in args.bauds.split(",")] if args.bauds else CANDIDATES
    results = []

    for baud in bauds:
        try:
            port = serial.Serial(args.port, baud, timeout=0.2)
        except Exception as e:
            print(f"{baud:>8}: cannot open ({e})")
            continue
        port.reset_input_buffer()
        reset_board(args.host)

        got = bytearray()
        deadline = time.time() + args.listen
        while time.time() < deadline:
            chunk = port.read(4096)
            if chunk:
                got += chunk
        port.close()

        s, text = score(bytes(got))
        results.append((s, baud, bytes(got)))
        preview = text.replace("\r", "").replace("\n", " | ")[:90]
        print(f"{baud:>8}: {len(got):>5} bytes  score={s:5.2f}  {preview}")

    print("\n=== best ===")
    for s, baud, data in sorted(results, reverse=True)[:3]:
        if not data:
            continue
        print(f"{baud} (score {s:.2f}): {data[:200]!r}")
    if not any(d for _, _, d in results):
        print("nothing received at any rate -- the chip is not transmitting, or")
        print("the receive path (probe, leads, wiring) is broken. Run uart-loopback.py.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
