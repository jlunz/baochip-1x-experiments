#!/usr/bin/env python3
"""Drive the Dabao PROG line via an ESPHome switch, and do PROG+RESET.

An ESP32 GPIO (14) is wired to the board's PROG/EN pin, active low --
`get_key()` samples this pin and treats low as `KeyPress::Select`, which
makes boot1 stay in the REPL regardless of `bootwait` (docs/04-bringup-log.md,
"PROG = guaranteed safe-mode"). Same relay convention as board-reset.py's
RST_N line: the switch's ON state is *released* (high, not pressed); this
script always leaves the line high when it's not actively holding PROG down,
so the board is never left parked with PROG asserted by accident.

  tools/board-prog.py state              # just report the line
  tools/board-prog.py release            # make sure PROG is not held (safe default)
  tools/board-prog.py prog-reset         # the actual C3/D4/F5/G6/H2/H4 sequence:
                                          #   hold PROG, pulse RESET, wait 1s, release PROG

prog-reset shells out to board-reset.py for the RESET pulse itself, so both
lines always go through the one script that owns each of them.
"""

import argparse
import json
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

DEFAULT_HOST = "192.168.42.38"
SWITCH = "GPIO%20Switch%2014"
REPO = Path(__file__).resolve().parent.parent


def post(host: str, action: str, timeout: float = 5.0) -> int:
    url = f"http://{host}/switch/{SWITCH}/{action}"
    req = urllib.request.Request(url, data=b"", method="POST")
    req.add_header("Content-Length", "0")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status


def read_state(host: str, timeout: float = 6.0):
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
                if obj.get("id") == "switch-gpio_switch_14":
                    return obj.get("state")
    except Exception:
        pass
    return None


def assert_prog(host: str) -> None:
    post(host, "turn_off")  # drive low = PROG held


def release_prog(host: str) -> None:
    post(host, "turn_on")  # drive high = PROG released


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("action", nargs="?", default="state",
                     choices=["state", "assert", "release", "prog-reset"])
    ap.add_argument("--host", default=DEFAULT_HOST)
    ap.add_argument("--reset-hold", type=float, default=0.2,
                     help="seconds RST_N is held low during prog-reset (passed to board-reset.py --hold)")
    ap.add_argument("--settle", type=float, default=1.0,
                     help="seconds to hold PROG after RESET releases, before releasing PROG "
                          "(the run sheet's 'wait 1s')")
    args = ap.parse_args()

    if args.action == "state":
        st = read_state(args.host)
        print(f"PROG switch = {st} ({'released' if st == 'ON' else 'HELD (asserted)' if st else 'unknown'})")
        return 0 if st else 1

    if args.action == "release":
        release_prog(args.host)
        st = read_state(args.host)
        print(f"PROG released; line now {st or 'unknown'}")
        return 0 if st == "ON" else 1

    if args.action == "assert":
        assert_prog(args.host)
        st = read_state(args.host)
        print(f"PROG held; line now {st or 'unknown'}")
        if st is not None and st != "OFF":
            print("WARNING: line is not low -- PROG is NOT actually held!", file=sys.stderr)
            return 1
        return 0

    if args.action == "prog-reset":
        print("holding PROG...")
        assert_prog(args.host)
        st = read_state(args.host)
        if st != "OFF":
            print(f"ERROR: PROG line reads {st!r}, not held -- aborting before touching RESET", file=sys.stderr)
            return 1

        print(f"pulsing RESET (hold {args.reset_hold}s)...")
        r = subprocess.run(
            [sys.executable, str(REPO / "tools" / "board-reset.py"), "--hold", str(args.reset_hold)],
            capture_output=True, text=True,
        )
        print(r.stdout, end="")
        if r.stderr:
            print(r.stderr, end="", file=sys.stderr)
        if r.returncode:
            print("ERROR: RESET pulse failed -- releasing PROG and aborting", file=sys.stderr)
            release_prog(args.host)
            return 1

        print(f"holding PROG for {args.settle}s after RESET release...")
        time.sleep(args.settle)

        print("releasing PROG...")
        release_prog(args.host)
        st = read_state(args.host)
        print(f"PROG released; line now {st or 'unknown'}")
        if st is not None and st != "ON":
            print("WARNING: line is NOT high -- PROG may still be held!", file=sys.stderr)
            return 1
        return 0

    return 1


if __name__ == "__main__":
    sys.exit(main())
