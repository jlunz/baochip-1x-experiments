#!/usr/bin/env python3
"""Multi-port serial capture for Dabao hardware bring-up.

Logs one or more serial ports to timestamped files and tees them to stdout with
a per-port prefix. Ports may come and go -- boot1's USB CDC console disappears
the moment it jumps to our payload -- so a missing or vanishing port is normal
and the reader keeps retrying rather than exiting.

  tools/hw-console.py --port /dev/ttyACM1:1000000 --port /dev/ttyACM0:115200

Logs land in build/hw/<name>-<stamp>.log (raw bytes, no translation) alongside
a .txt transcript with timestamps. Ctrl-C stops all readers.
"""

import argparse
import os
import pathlib
import sys
import threading
import time

import serial

REPO = pathlib.Path(__file__).resolve().parent.parent
STAMP = time.strftime("%Y%m%d-%H%M%S")

stop = threading.Event()


def reader(dev: str, baud: int, outdir: pathlib.Path, prefix: str) -> None:
    name = os.path.basename(dev)
    raw_path = outdir / f"{name}-{STAMP}.log"
    txt_path = outdir / f"{name}-{STAMP}.txt"
    raw = open(raw_path, "ab", buffering=0)
    txt = open(txt_path, "a", buffering=1)
    txt.write(f"### capture {dev} @ {baud} started {time.strftime('%H:%M:%S')}\n")

    port = None
    line = bytearray()
    was_open = False

    def flush_line(reason: str = "") -> None:
        if not line:
            return
        text = line.decode("utf-8", errors="replace").rstrip("\r\n")
        ts = time.strftime("%H:%M:%S")
        txt.write(f"[{ts}] {text}\n")
        sys.stdout.write(f"{prefix} {text}\n")
        sys.stdout.flush()
        line.clear()

    while not stop.is_set():
        if port is None:
            try:
                port = serial.Serial(dev, baud, timeout=0.2)
                if not was_open:
                    sys.stdout.write(f"{prefix} <<< connected {dev} @ {baud} >>>\n")
                    sys.stdout.flush()
                was_open = True
            except Exception:
                # Port absent (board resetting, boot1 handed off, not plugged in
                # yet). Not an error -- wait for it to come back.
                if was_open:
                    flush_line()
                    ts = time.strftime("%H:%M:%S")
                    txt.write(f"[{ts}] ### port vanished\n")
                    sys.stdout.write(f"{prefix} <<< port vanished >>>\n")
                    sys.stdout.flush()
                    was_open = False
                stop.wait(0.5)
                continue

        try:
            chunk = port.read(4096)
        except Exception:
            try:
                port.close()
            except Exception:
                pass
            port = None
            continue

        if not chunk:
            # Idle: flush a partial line so a prompt with no trailing newline
            # (e.g. "dabao login: ") still shows up promptly.
            if line:
                flush_line()
            continue

        raw.write(chunk)
        for b in chunk:
            if b == 0x0A:
                flush_line()
            else:
                line.append(b)

    flush_line()
    txt.write(f"### capture ended {time.strftime('%H:%M:%S')}\n")
    raw.close()
    txt.close()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--port",
        action="append",
        required=True,
        metavar="DEV[:BAUD]",
        help="serial port to capture; baud defaults to 1000000",
    )
    ap.add_argument("--outdir", default=str(REPO / "build" / "hw"))
    ap.add_argument("--seconds", type=float, default=0, help="stop after N seconds")
    args = ap.parse_args()

    outdir = pathlib.Path(args.outdir)
    outdir.mkdir(parents=True, exist_ok=True)

    threads = []
    for spec in args.port:
        dev, _, baud = spec.partition(":")
        baud = int(baud) if baud else 1000000
        prefix = f"[{os.path.basename(dev)}]"
        t = threading.Thread(target=reader, args=(dev, baud, outdir, prefix), daemon=True)
        t.start()
        threads.append(t)

    print(f"### logging to {outdir} (stamp {STAMP}); Ctrl-C to stop", file=sys.stderr)
    try:
        if args.seconds:
            stop.wait(args.seconds)
        else:
            while True:
                stop.wait(3600)
    except KeyboardInterrupt:
        pass
    stop.set()
    for t in threads:
        t.join(timeout=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
