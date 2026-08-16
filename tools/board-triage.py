#!/usr/bin/env python3
"""Decide, mechanically, whether a Dabao is alive -- before touching its flash.

This is rung 0 of docs/08-recovery-and-risk-ladder.md and it writes nothing to
the board. It answers the question the 2026-08-07 session never managed to
answer (docs/07-board-incident-2026-08-07.md): is the chip powered and
executing, or is the fault upstream of the firmware?

Three independent signals, in the order that makes each one interpretable:

  1. USB. Whether the device appears at all, and at which speed. On this board
     "full speed" is a symptom, not a setting: boot1 brings its port up at high
     speed unless the UsbDefaultSpeed one-way counter says otherwise, so a
     full-speed device that never answers a descriptor read means the port is
     electrically connected to something that is not running boot1's USB stack.
  2. Serial. Reset the board with a capture already running and look for the
     boot0 and boot1 banners. This is the decisive test: boot0 prints before
     any board-specific logic, so its banner means the chip is executing.
  3. Baud. If nothing arrives at 1 Mbaud, the console may simply have moved --
     a mis-started PLL shifts the bit rate without stopping the CPU. Defer to
     tools/baud-scan.py, which resets once per candidate rate.

What it deliberately does not do is conclude "the board is dead". A silent
board on this design is also what a security abort looks like: boot0's die()
path zeroizes state and then prints only on the DUART, which the Dabao does
not route, so an aborting chip and an unpowered chip are indistinguishable
over these three signals. That distinction needs a multimeter -- see the
report this prints.

  tools/board-triage.py --port /dev/serial/by-id/usb-Raspberry_Pi_Debug_Probe...-if01
  tools/board-triage.py --port ... --no-reset      # observe only, no RST_N
"""

import argparse
import pathlib
import re
import subprocess
import sys
import time

import serial

REPO = pathlib.Path(__file__).resolve().parent.parent

# boot1 enumerates as this; it is the vendor's Baochip-1x USB ID.
USB_ID = "1d50:6196"

# Substrings that prove a given stage ran. boot0 is the important one: it runs
# before any board-specific code, so seeing it separates "chip executing" from
# everything else.
STAGE_MARKERS = [
    ("boot0", "boot0 up!"),
    ("boot0-console", "boot0 console up"),
    ("boot1", "Boot1 up!"),
    ("boot1-usb", "USB device ready"),
    ("bootwait", "Boot bypassed"),
    ("payload-validated", "Booting with key"),
    ("shim", "SBI:entry"),
    ("shim-se0", "SBI:se0-released"),
    ("shim-done", "SBI:csr-done"),
    ("kernel", "Linux version"),
]


def sh(cmd):
    try:
        return subprocess.run(cmd, capture_output=True, text=True,
                              timeout=10).stdout
    except (OSError, subprocess.SubprocessError):
        return ""


def check_usb():
    """Report presence and negotiated speed of the board on USB."""
    print("== 1. USB ==")
    lsusb = sh(["lsusb"])
    if not lsusb:
        print("  lsusb unavailable -- run this on the machine the board is")
        print("  physically attached to, not inside a container or VM guest.")
        return None

    present = USB_ID in lsusb
    print(f"  {USB_ID} present: {'yes' if present else 'no'}")

    speed = None
    # /sys is authoritative for the negotiated speed; lsusb -t needs root on
    # some distros and its output format moves around between versions.
    for dev in sorted(pathlib.Path("/sys/bus/usb/devices").glob("*")):
        try:
            vid = (dev / "idVendor").read_text().strip()
            pid = (dev / "idProduct").read_text().strip()
        except OSError:
            continue
        if f"{vid}:{pid}" != USB_ID:
            continue
        try:
            speed = (dev / "speed").read_text().strip()
        except OSError:
            speed = "unknown"
        print(f"  {dev.name}: speed {speed} Mbps")

    if present and speed == "480":
        print("  -> high speed: boot1's USB stack is running and healthy.")
    elif present and speed in ("12", "1.5"):
        print("  -> FULL/LOW speed. boot1 defaults to high speed, so this is")
        print("     either a chip that is not running boot1's USB stack, or a")
        print("     link too degraded to complete the high-speed chirp.")
        print("     Check dmesg for 'device descriptor read/64, error -71':")
        print("     that is the physical layer, not firmware. Try another")
        print("     cable and a direct root port before suspecting the board.")
    elif not present:
        print("  -> absent. Either unpowered, held in SE0 by a previous")
        print("     payload (only a physical unplug clears that -- the switch")
        print("     is powered from VBUS), or not executing.")
    return speed


def reset_board(hold):
    print(f"  pulsing RST_N (hold {hold}s) ...")
    r = subprocess.run([sys.executable, str(REPO / "tools" / "board-reset.py"),
                        "--hold", str(hold)], capture_output=True, text=True)
    if r.returncode != 0:
        print("  reset failed; press RESET by hand now "
              "(capture is already running)")
        print("  " + (r.stderr.strip().splitlines() or [""])[-1])
    return r.returncode == 0


def check_serial(port, baud, hold, do_reset, window):
    """Capture across a reset and report which boot stages announced."""
    print("\n== 2. Serial ==")
    try:
        ser = serial.Serial(port, baud, timeout=0.2)
    except Exception as e:            # noqa: BLE001 - report and continue
        print(f"  cannot open {port}: {e}")
        print("  loopback-test the probe first: tools/uart-loopback.py")
        return None

    with ser:
        ser.reset_input_buffer()
        if do_reset:
            reset_board(hold)
        else:
            print(f"  no reset requested -- press RESET within {window:.0f}s")

        raw = bytearray()
        deadline = time.time() + window
        while time.time() < deadline:
            raw += ser.read(4096)

    text = raw.decode("utf-8", "replace")
    printable = sum(c.isprintable() or c in "\r\n\t" for c in text)
    print(f"  captured {len(raw)} bytes "
          f"({printable} printable) over {window:.0f}s at {baud} baud")

    if not raw:
        print("  -> SILENT. Nothing at all, not even framing errors.")
        return []

    seen = [name for name, marker in STAGE_MARKERS if marker in text]
    if seen:
        print("  stages seen: " + ", ".join(seen))
    else:
        print("  bytes arrived but no known banner matched.")
        if printable < len(raw) // 2:
            print("  mostly unprintable -> wrong baud rate, not wrong content.")
            print("  run tools/baud-scan.py to find the real rate.")
        print("  --- first 400 chars ---")
        print("  " + re.sub(r"\n", "\n  ", text[:400]))
    return seen


def verdict(usb_speed, stages):
    print("\n== Verdict ==")
    if stages is None:
        print("  INCONCLUSIVE: the console was never opened. Prove the probe")
        print("  path with tools/uart-loopback.py, then re-run.")
        return 2

    if "boot0" in stages or "boot0-console" in stages:
        if "boot1" in stages:
            print("  ALIVE. boot0 and boot1 both ran. The board is recoverable")
            print("  from the boot1 REPL over this same UART -- USB is not")
            print("  needed for flashing. Proceed to rung 1 of the ladder.")
            return 0
        print("  PARTIALLY ALIVE: boot0 ran, boot1 did not announce. boot0")
        print("  falls back to the payload region when boot1 fails to")
        print("  validate, and on this board that region holds our image.")
        print("  Do not reflash blind; see rung 1 of the ladder.")
        return 1

    if stages:
        print("  ALIVE but unrecognised output -- the chip is transmitting.")
        return 1

    print("  SILENT. The chip is not talking on this wire. Three causes are")
    print("  indistinguishable from here, in decreasing order of likelihood:")
    print("    a) no power / bad USB-C cable. Measure GND(13)->VBUS(40) ~5V")
    print("       and GND(13)->3V3(36) ~3.3V. This board has no LED, so a")
    print("       multimeter is the only way to know. Try another cable.")
    print("    b) the console link to the board is broken. uart-loopback.py")
    print("       proves the probe and the host, NOT the board's PB14 path --")
    print("       re-check continuity at header pins 15/16.")
    print("    c) a security abort in boot0. Its die() path prints only on the")
    print("       DUART, which this board does not route, then hangs forever;")
    print("       every reset repeats it. Indistinguishable from (a) over any")
    print("       signal available here.")
    if usb_speed in ("12", "1.5"):
        print("  The full-speed USB device seen above is consistent with (a)")
        print("  and (c) alike: the port is connected, nothing is answering.")
    return 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", required=True,
                    help="probe UART, e.g. /dev/serial/by-id/...-if01")
    ap.add_argument("--baud", type=int, default=1000000)
    ap.add_argument("--hold", type=float, default=0.5,
                    help="RST_N assertion time in seconds")
    ap.add_argument("--window", type=float, default=6.0,
                    help="seconds to capture after the reset")
    ap.add_argument("--no-reset", action="store_true",
                    help="do not drive RST_N; reset by hand instead")
    args = ap.parse_args()

    print("=== Dabao triage (read-only: nothing is written to the board) ===\n")
    usb_speed = check_usb()
    stages = check_serial(args.port, args.baud, args.hold,
                          not args.no_reset, args.window)
    return verdict(usb_speed, stages)


if __name__ == "__main__":
    sys.exit(main())
