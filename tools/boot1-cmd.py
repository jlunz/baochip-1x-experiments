#!/usr/bin/env python3
"""Send a command to the boot1 REPL and print what it says back.

boot1's management REPL lives on its USB CDC console (/dev/ttyACM0). Note that
this console disappears when boot1 jumps to a payload -- it asserts the USB SE0
pin on the way out -- so a write error right after 'boot' is expected, not a
failure.

Sends always go one character at a time (write+flush per char, never one bulk
write of the whole command) -- a single unpaced port.write() of a whole
command reliably drops characters at 1000000 baud, confirmed on hardware
2026-08-20: 'audit' came back as 'audt' twice in a row, and longer strings
lost more, in different positions each time. This link has no flow control
and boot1's REPL apparently can't drain a burst that fast.

--char-delay (default 0) adds an *extra* sleep between characters on top of
that. 0 does not mean "send it all at once" -- calibration on hardware the
same day found the per-write()/flush() syscall and USB round-trip overhead
alone (no explicit sleep, ~170us/char measured) is already enough to be
reliable (15/15 clean trials on a realistic ~683-char block); an explicit
5ms/char, the first fix applied, was correct but needlessly ~30x slower and
would have made an 11584-block kernel transfer take ~11 hours instead of
~20 minutes. Only raise --char-delay if a specific link proves less reliable
than this one. sources/xous-core/bao1x-boot/uf2send.py's block-transfer path
had the identical bug and is patched the same way, locally
(xous-core-local-patches/, since that file is vendored and outside this
repo's own patch mechanism).

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
    ap.add_argument("--char-delay", type=float, default=0.0,
                     help="extra ms between characters when sending, on top of "
                          "the natural per-write overhead (see module docstring)")
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
        for ch in s:
            port.write(ch.encode())
            port.flush()
            if args.char_delay > 0:
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
