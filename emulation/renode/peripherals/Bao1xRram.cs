//
// Bao1x RRAM write path: RRC controller (0x40000000) + a writable RRAM
// window with line-buffer semantics.
//
// Hardware reference: xous-core libs/bao1x-hal/src/rram.rs, utralib rrc
// registers. Writes to the RRAM array go through a 32-byte line buffer:
// software stores the 8 data words to the target (mapped) address, sets
// RRC CR = 2 (write-cmd mode, plus a security-mode field 0xFC00), then
// stores magic 0x5200 (load buffer) and 0x9528 (write buffer) to the
// line address, and restores CR = 0.
//
// Renode cannot intercept stores to executable MappedMemory, so the
// platform keeps the XIP portion of the RRAM (boot/shim/kernel/rootfs)
// as MappedMemory and models only the writable data window
// (0x60360000..0x60400000) with this peripheral:
//   Bao1xRrc      — the control registers; tracks the CR mode.
//   Bao1xRramData — the data window; stages stores into a line buffer
//                   and commits it on the 0x9528 magic when the RRC is
//                   in write-cmd mode.
//
// SPDX-License-Identifier: Apache-2.0
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xRrc : IDoubleWordPeripheral, IKnownSize
    {
        public Bao1xRrc(Machine machine)
        {
        }

        public void Reset()
        {
            ControlRegister = 0;
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case 0x00: return ControlRegister;
            case 0x08: return 0;    // SFR_RRCSR: status, never busy
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
            case 0x00:
                ControlRegister = value;
                break;
            case 0x10:
                this.Log(LogLevel.Warning, "SFR_RRCAR write 0x{0:X} (self-destruct register!)", value);
                break;
            }
        }

        public bool InWriteCmdMode => (ControlRegister & 0x2) != 0;

        public uint ControlRegister { get; private set; }
        public long Size => 0x1000;
    }

    public class Bao1xRramData : IDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize
    {
        public Bao1xRramData(Machine machine, Bao1xRrc rrc, uint size = 0xA0000)
        {
            this.rrc = rrc;
            data = new byte[size];
            lineBuffer = new uint[LineWords];
            Reset();
        }

        public void Reset()
        {
            // Nonvolatile: contents survive reset; fresh model = erased.
            lineAddress = ~0u;
        }

        public byte ReadByte(long offset)
        {
            return data[offset];
        }

        public ushort ReadWord(long offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        public uint ReadDoubleWord(long offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        public void WriteByte(long offset, byte value)
        {
            this.Log(LogLevel.Warning, "sub-word RRAM store at 0x{0:X} ignored", offset);
        }

        public void WriteWord(long offset, ushort value)
        {
            this.Log(LogLevel.Warning, "sub-word RRAM store at 0x{0:X} ignored", offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(rrc.InWriteCmdMode)
            {
                if(value == MagicLoadBuffer)
                {
                    return;     // buffer already staged
                }
                if(value == MagicWriteBuffer)
                {
                    Commit((uint)offset & ~(LineBytes - 1));
                    return;
                }
                this.Log(LogLevel.Warning, "unexpected store 0x{0:X} in write-cmd mode", value);
                return;
            }
            // Data phase: stage into the line buffer.
            var line = (uint)offset & ~(LineBytes - 1);
            if(line != lineAddress)
            {
                // The real buffer is loaded from the array on a new line.
                lineAddress = line;
                for(var i = 0; i < LineWords; i++)
                {
                    lineBuffer[i] = ReadDoubleWord(line + 4 * i);
                }
            }
            lineBuffer[((uint)offset & (LineBytes - 1)) / 4] = value;
        }

        private void Commit(uint line)
        {
            if(line != lineAddress)
            {
                this.Log(LogLevel.Warning,
                         "commit at 0x{0:X} but staged line is 0x{1:X}", line, lineAddress);
                return;
            }
            for(var i = 0; i < LineWords; i++)
            {
                var v = lineBuffer[i];
                data[line + 4 * i] = (byte)v;
                data[line + 4 * i + 1] = (byte)(v >> 8);
                data[line + 4 * i + 2] = (byte)(v >> 16);
                data[line + 4 * i + 3] = (byte)(v >> 24);
            }
        }

        public long Size => data.Length;

        private const uint LineBytes = 32;
        private const int LineWords = 8;
        private const uint MagicLoadBuffer = 0x5200;
        private const uint MagicWriteBuffer = 0x9528;

        private readonly Bao1xRrc rrc;
        private readonly byte[] data;
        private readonly uint[] lineBuffer;
        private uint lineAddress;
    }
}
