#!/usr/bin/env python3
"""Send a command to the boot1 REPL and print what it says back.

boot1's management REPL lives on its USB CDC console (/dev/ttyACM0). Note that
this console disappears when boot1 jumps to a payload -- it asserts the USB SE0
pin on the way out -- so a write error right after 'boot' is expected, not a
failure.

Sends are paced one character at a time (default 5ms apart, --char-delay to
change it). A single unpaced port.write() of a whole command reliably drops
characters at 1000000 baud -- confirmed on hardware 2026-08-20: 'audit' came
back as 'audt' twice in a row, and longer strings lost more, in different
positions each time. This link has no flow control and boot1's REPL apparently
can't drain a burst that fast; sources/xous-core/bao1x-boot/uf2send.py already
worked around the same thing for its 'localecho off' send (see its comment),
but not for the uf2 block-transfer path itself, which has the same
vulnerability and is unfixed as of this writing since that file is vendored
and outside this repo's patch mechanism.

  tools/boot1-cmd.py                # just read, send nothing
  tools/boot1-cmd.py help
  tools/boot1-cmd.py 'bootwait check'
"""

import argparse
import sys
import time

import serial


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("command", nargs="*", help="command to send (omit to only listen)")
    ap.add_argument("--port", default="/dev/ttyACM0")
    ap.add_argument("--baud", type=int, default=115200)
    ap.add_argument("--wait", type=float, default=2.0, help="seconds to read after sending")
    ap.add_argument("--char-delay", type=float, default=5.0,
                     help="ms between characters when sending (0 disables pacing)")
    args = ap.parse_args()

    try:
        port = serial.Serial(args.port, args.baud, timeout=0.2)
    except Exception as e:
        print(f"cannot open {args.port}: {e}", file=sys.stderr)
        return 1

    # Drain anything already buffered so the reply below is unambiguous.
    time.sleep(0.2)
    stale = port.read(65536)
    if stale:
        sys.stdout.write("--- buffered before send ---\n")
        sys.stdout.write(stale.decode("utf-8", errors="replace"))
        sys.stdout.write("\n")

    def send(s: str) -> None:
        if args.char_delay <= 0:
            port.write(s.encode())
            port.flush()
            return
        for ch in s:
            port.write(ch.encode())
            port.flush()
            time.sleep(args.char_delay / 1000.0)

    if args.command:
        cmd = " ".join(args.command)
        sys.stdout.write(f"--- sending: {cmd!r} ---\n")
        send(cmd + "\r\n")
    else:
        # A bare newline nudges the REPL into printing its prompt.
        send("\r\n")

    deadline = time.time() + args.wait
    got = bytearray()
    while time.time() < deadline:
        chunk = port.read(4096)
        if chunk:
            got += chunk
            deadline = time.time() + 0.6  # extend while it is still talking
    sys.stdout.write("--- reply ---\n")
    sys.stdout.write(got.decode("utf-8", errors="replace") if got else "(silence)\n")
    sys.stdout.write("\n")
    port.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
