#!/usr/bin/env python3
"""Wrap a flat binary in UF2 blocks for the bao1x boot1 flasher.

boot1 accepts UF2 either as a file copied to the "BAOCHIP" mass-storage
volume or streamed over its serial console (xous-core
bao1x-boot/uf2send.py). Family ID from bao1x-api::BAOCHIP_1X_UF2_FAMILY;
blocks carry absolute RRAM addresses, 256-byte payloads.
"""
import argparse
import struct

MAGIC0 = 0x0A324655
MAGIC1 = 0x9E5D5157
MAGIC_END = 0x0AB16F30
FLAG_FAMILY_ID = 0x00002000
BAOCHIP_1X_FAMILY = 0xA7D76373
PAYLOAD = 256


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("binary", help="flat input image")
    ap.add_argument("output", help="output .uf2")
    ap.add_argument("--base", type=lambda x: int(x, 0), required=True,
                    help="flash address of the first byte (e.g. 0x60060000)")
    args = ap.parse_args()

    data = open(args.binary, "rb").read()
    nblocks = (len(data) + PAYLOAD - 1) // PAYLOAD
    with open(args.output, "wb") as out:
        for i in range(nblocks):
            chunk = data[i * PAYLOAD:(i + 1) * PAYLOAD]
            block = struct.pack(
                "<IIIIIIII", MAGIC0, MAGIC1, FLAG_FAMILY_ID,
                args.base + i * PAYLOAD, len(chunk), i, nblocks,
                BAOCHIP_1X_FAMILY)
            block += chunk.ljust(476, b"\x00")
            block += struct.pack("<I", MAGIC_END)
            assert len(block) == 512
            out.write(block)
    print(f"{args.output}: {nblocks} blocks, "
          f"0x{args.base:08x}..0x{args.base + len(data):08x}")


if __name__ == "__main__":
    main()
