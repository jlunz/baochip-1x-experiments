#!/usr/bin/env python3
"""Loopback test for the debug-probe UART path.

Proves the host software, USB path, probe firmware and jumper wires are all
good, independently of the board. Short the probe's TX to its RX (on a
Raspberry Pi Debug Probe: the orange and yellow leads touched together) and
run this: whatever is sent should come straight back.

  PASS -> probe path is fine; silence from the board is the board's silence.
  FAIL -> the fault is on our side, not the target.
"""

import sys
import time

import serial

PORT = sys.argv[1] if len(sys.argv) > 1 else "/dev/ttyACM0"
BAUD = int(sys.argv[2]) if len(sys.argv) > 2 else 1000000

try:
    port = serial.Serial(PORT, BAUD, timeout=0.5)
except Exception as e:
    print(f"cannot open {PORT}: {e}")
    sys.exit(2)

time.sleep(0.2)
port.reset_input_buffer()

probe = b"BAOCHIP-LOOPBACK-0123456789\r\n"
port.write(probe)
port.flush()

got = bytearray()
deadline = time.time() + 2.0
while time.time() < deadline and len(got) < len(probe):
    chunk = port.read(256)
    if chunk:
        got += chunk
        deadline = time.time() + 0.3
port.close()

print(f"sent {len(probe)} bytes @ {BAUD} on {PORT}")
print(f"got  {len(got)} bytes: {bytes(got)!r}")
if probe.rstrip() in bytes(got):
    print("\nPASS - probe path is good. Silence from the board is the board's.")
    sys.exit(0)
elif got:
    print("\nPARTIAL - bytes returned but corrupted: check baud rate or wire quality.")
    sys.exit(1)
else:
    print("\nFAIL - nothing came back. Either TX/RX are not shorted, or the")
    print("probe path itself is broken (wrong device node, dead probe, bad lead).")
    sys.exit(1)
