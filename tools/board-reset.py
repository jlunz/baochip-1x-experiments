#!/usr/bin/env python3
"""Pulse the Dabao RUN/RST_N line via an ESPHome switch.

An ESP32 GPIO is wired to header pin 30 (RUN / RST_N, active low): high = run,
low = held in reset. Driving it from the ESPHome REST API removes the human
from the bring-up loop -- every reflash/boot cycle otherwise needs a button
press, and the shim's failure mode (hang with USB down) makes a reset the only
way back to boot1.

  tools/board-reset.py                 # pulse reset
  tools/board-reset.py --hold 0.5      # longer assertion
  tools/board-reset.py --state         # just report the line

The switch's ON state is the *released* (high, running) state; this script
always leaves the line high so the board is never left parked in reset.
"""

import argparse
import json
import sys
import time
import urllib.request

DEFAULT_HOST = "192.168.42.38"
# ESPHome addresses entities by *display name*, URL-encoded -- not the slug
# that appears in the SSE "id" field (that 404s).
SWITCH = "GPIO%20Switch%2015"


def post(host: str, action: str, timeout: float = 5.0) -> int:
    url = f"http://{host}/switch/{SWITCH}/{action}"
    # An explicit zero-length body is required; without Content-Length the
    # device answers 411.
    req = urllib.request.Request(url, data=b"", method="POST")
    req.add_header("Content-Length", "0")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status


def read_state(host: str, timeout: float = 6.0):
    """Scrape the SSE stream for this switch's current state."""
    url = f"http://{host}/events"
    deadline = time.time() + timeout
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            for raw in r:
                if time.time() > deadline:
                    break
                line = raw.decode("utf-8", errors="replace").strip()
                if not line.startswith("data:"):
                    continue
                try:
                    obj = json.loads(line[5:].strip())
                except ValueError:
                    continue
                if obj.get("id") == "switch-gpio_switch_15":
                    return obj.get("state")
    except Exception:
        pass
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default=DEFAULT_HOST)
    ap.add_argument("--hold", type=float, default=0.2, help="seconds to hold reset low")
    ap.add_argument("--state", action="store_true", help="report line state and exit")
    args = ap.parse_args()

    if args.state:
        st = read_state(args.host)
        print(f"RUN/RST_N switch = {st} ({'running' if st == 'ON' else 'held in reset' if st else 'unknown'})")
        return 0 if st else 1

    try:
        post(args.host, "turn_off")          # assert reset (line low)
        time.sleep(args.hold)
        post(args.host, "turn_on")           # release (line high)
    except Exception as e:
        print(f"reset failed: {e}", file=sys.stderr)
        return 1

    st = read_state(args.host)
    print(f"reset pulsed ({args.hold}s low); line now {st or 'unknown'}")
    # Leaving the line low would look exactly like a dead board, so say so loudly.
    if st is not None and st != "ON":
        print("WARNING: line is NOT high - board is parked in reset!", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
