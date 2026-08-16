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

Every capture is written to build/hw/, because a baseline from a healthy board
is the thing whose absence made the incident hard to interpret.

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

# Substrings that prove a given stage ran, in boot order. Kept in step with the
# mark() calls in firmware/bao1x-sbi/main.c and the stage table in
# docs/05-hardware-bringup.md. boot0 is the important one: it runs before any
# board-specific code, so seeing it separates "chip executing" from everything
# else.
STAGE_MARKERS = [
    ("boot0", "boot0 up!"),
    ("boot0-console", "boot0 console up"),
    ("boot1", "Boot1 up!"),
    ("boot1-usb", "USB device ready"),
    ("bootwait", "Boot bypassed"),
    ("payload-validated", "Booting with key"),
    ("devmode-burn", "Developer key detected"),
    ("shim", "SBI:entry"),
    ("shim-duart", "SBI:duart-ok"),
    ("shim-uart", "SBI:uart-init"),
    ("shim-se0", "SBI:se0-released"),
    ("shim-dtb", "SBI:dtb-copied"),
    ("shim-csr-done", "SBI:csr-done"),
    ("shim-fatal", "SBI:FATAL"),
    ("shim-only", "SBI:shim-only"),
    ("shim-heartbeat", "SBI:alive"),
    ("shim-jump", "jumping to kernel"),
    ("kernel", "Linux version"),
    ("login", "login:"),
]

# Stages that mean boot1 is in charge and its REPL is reachable.
BOOT1_STAGES = {"boot1", "boot1-usb", "bootwait"}
BOOT0_STAGES = {"boot0", "boot0-console"}

POWER_REMEDIES = """\
a) no power / bad USB-C cable. Measure GND(13)->VBUS(40) ~5V and
   GND(13)->3V3(36) ~3.3V. This board has no LED, so a multimeter is the
   only way to know. Try another cable and a direct root port.
b) the console link to the board is broken. tools/uart-loopback.py proves
   the probe and the host, NOT the board's PB14 path -- re-check continuity
   at header pins 15/16.
c) a security abort in boot0. Its die() path prints only on the DUART,
   which this board does not route, then hangs forever; every reset repeats
   it. Indistinguishable from (a) over any signal available here."""


def printable_bytes(raw):
    """Count bytes that would render as text.

    Deliberately over raw bytes, not over a decoded str: decoding with
    errors="replace" turns every invalid byte into U+FFFD, and
    "�".isprintable() is True -- so a decoded count reports garbage from a
    baud mismatch as ~100% printable and the wrong-baud branch never fires.
    Same form as score() in tools/baud-scan.py.
    """
    return sum(1 for b in raw if 32 <= b < 127 or b in (9, 10, 13))


def check_usb():
    """Report presence and negotiated speed of the board on USB."""
    print("== 1. USB ==")
    sysfs = pathlib.Path("/sys/bus/usb/devices")
    if not sysfs.is_dir():
        print("  no /sys/bus/usb/devices -- run this on the machine the board")
        print("  is physically attached to, not inside a container or VM guest.")
        return None

    speed = None
    for dev in sorted(sysfs.glob("*")):
        try:
            vid = (dev / "idVendor").read_text().strip()
            pid = (dev / "idProduct").read_text().strip()
        except OSError:
            continue                      # interface node, not a device
        if f"{vid}:{pid}" != USB_ID:
            continue
        try:
            speed = (dev / "speed").read_text().strip()
        except OSError:
            speed = "unknown"
        print(f"  {USB_ID} present at {dev.name}: speed {speed} Mbps")
        break

    if speed == "480":
        print("  -> high speed: boot1's USB stack is running and healthy.")
    elif speed in ("12", "1.5"):
        print("  -> FULL/LOW speed. boot1 defaults to high speed, so this is")
        print("     either a chip that is not running boot1's USB stack, or a")
        print("     link too degraded to complete the high-speed chirp.")
        print("     Check dmesg for 'device descriptor read/64, error -71':")
        print("     that is the physical layer, not firmware. Try another")
        print("     cable and a direct root port before suspecting the board.")
        print("     If the board does reach the REPL and usb_speed reports")
        print("     'Full', that OWC is the cause -- but setting it back is an")
        print("     irreversible counter write, so read docs/08 rung 6 first.")
    else:
        print(f"  {USB_ID}: absent")
        print("  -> Either unpowered, held in SE0 by a payload that never")
        print("     released PC13 (only a physical unplug clears that -- the")
        print("     switch is powered from VBUS), or not executing.")
    return speed


def save_capture(raw):
    out = REPO / "build" / "hw"
    out.mkdir(parents=True, exist_ok=True)
    path = out / f"triage-{time.strftime('%Y%m%d-%H%M%S')}.log"
    path.write_bytes(bytes(raw))
    return path


def check_serial(port, baud, hold, do_reset, window):
    """Capture across a reset and report which boot stages announced.

    Returns None if the port could not be opened, otherwise the list of stage
    names seen -- which is empty both for "no bytes" and for "bytes that
    matched nothing", so the byte count is returned alongside it.
    """
    print("\n== 2. Serial ==")
    try:
        ser = serial.Serial(port, baud, timeout=0.2)
    except Exception as e:                # noqa: BLE001 - report and continue
        print(f"  cannot open {port}: {e}")
        print("  loopback-test the probe first: tools/uart-loopback.py")
        return None, 0

    raw = bytearray()
    reset = None
    with ser:
        ser.reset_input_buffer()
        if do_reset:
            # Launched without waiting: board-reset.py holds the line, then
            # polls ESPHome for several seconds, and boot0 prints within
            # milliseconds of release. Blocking on it first would mean the
            # banners have to survive in the tty buffer until we get back --
            # which is the difference between "capture already running" and
            # a false SILENT verdict on a healthy board.
            print(f"  pulsing RST_N (hold {hold}s), capturing throughout ...")
            reset = subprocess.Popen(
                [sys.executable, str(REPO / "tools" / "board-reset.py"),
                 "--hold", str(hold)],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        else:
            print(f"  no reset requested -- press RESET within {window:.0f}s")

        deadline = time.time() + window + (hold if do_reset else 0)
        while time.time() < deadline:
            raw += ser.read(4096)

    if reset is not None:
        try:
            _, err = reset.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            reset.kill()
            err = "timed out"
        if reset.returncode:
            print("  NOTE: RST_N pulse failed, so this capture may cover no")
            print("        reset at all. Press RESET by hand and re-run.")
            print("        " + (err or "").strip().splitlines()[-1:][0]
                  if (err or "").strip() else "")

    text = raw.decode("utf-8", "replace")
    printable = printable_bytes(raw)
    print(f"  captured {len(raw)} bytes "
          f"({printable} printable) over {window:.0f}s at {baud} baud")
    if raw:
        print(f"  saved: {save_capture(raw).relative_to(REPO)}")

    if not raw:
        print("  -> SILENT. Nothing at all, not even framing errors.")
        return [], 0

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
    return seen, len(raw)


def verdict(usb_speed, stages, nbytes):
    print("\n== Verdict ==")
    if stages is None:
        print("  INCONCLUSIVE: the console was never opened. Prove the probe")
        print("  path with tools/uart-loopback.py, then re-run.")
        return 2

    seen = set(stages)
    if seen & BOOT0_STAGES or seen & BOOT1_STAGES:
        # Reaching either stage settles the only question that matters here.
        # boot0 without boot1 is worth calling out separately; boot1 without
        # boot0 just means the capture started late, which --no-reset makes
        # routine.
        if seen & BOOT1_STAGES:
            print("  ALIVE. boot1 is running. The board is recoverable from")
            print("  its REPL over this same UART -- USB is not needed for")
            print("  flashing. Proceed to rung 1 of the ladder.")
            if not seen & BOOT0_STAGES:
                print("  (No boot0 banner, which is expected when the capture")
                print("   started after it had already scrolled past.)")
            return 0
        print("  PARTIALLY ALIVE: boot0 ran, boot1 did not announce. boot0")
        print("  falls back to the payload region when boot1 fails to")
        print("  validate, and on this board that region holds our image.")
        print("  Do not reflash blind; see rung 1 of the ladder.")
        return 1

    if stages:
        print("  ALIVE but unrecognised stage set -- the chip is transmitting.")
        return 1

    if nbytes:
        # Bytes arrived and matched nothing. Whatever else is true, the wire is
        # not silent, so none of the power/cable triage below applies.
        print(f"  TRANSMITTING but unrecognised: {nbytes} bytes arrived and")
        print("  matched no known banner. The chip is driving the line, so")
        print("  this is not a power or wiring fault. Most likely the console")
        print("  moved: run tools/baud-scan.py, which resets once per rate.")
        return 1

    print("  SILENT. The chip is not talking on this wire. Three causes are")
    print("  indistinguishable from here, in decreasing order of likelihood:")
    for line in POWER_REMEDIES.splitlines():
        print("    " + line)
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
    stages, nbytes = check_serial(args.port, args.baud, args.hold,
                                  not args.no_reset, args.window)
    return verdict(usb_speed, stages, nbytes)


if __name__ == "__main__":
    sys.exit(main())
