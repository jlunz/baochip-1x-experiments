//
// Bao1x uDMA I2C master (PULP udma_i2c) — four instances at
// 0x50109000 + n*0x1000. i2c0 is the Dabao bus (PB11 SCL / PB12 SDA).
//
// Hardware reference: baochip-1x rtl/ips/udma/udma_i2c/rtl/*.sv,
// rtl/ips/incdir/udma_i2c_defines.sv; xous-core libs/bao1x-hal/src/udma/i2c.rs.
//
// The engine executes a 32-bit command stream fetched by the CMD uDMA
// channel (registers +0x20..0x28); payload moves through the TX/RX
// channels. Command opcode in bits [31:28]:
//   0x0 START     0x2 STOP      0x4 RD_ACK    0x6 RD_NACK
//   0x7 WRB (imm byte)          0x8 WR (byte from TX channel)
//   0x9 EOT       0xA WAIT      0xC RPT (arg = count for next cmd)
//   0xE CFG (arg = divider)     0x1 WAIT_EV
// STATUS (+0x30): bit0 busy, bit1 arbitration lost.
// ACK (+0x38): bit0 sticky NACK, cleared on read.
//
// The model executes the whole command list instantly on CMD kick. The
// first WRB after a START is the address+R/W byte; a NACK is latched if
// no peripheral is registered at that address.
//
// SPDX-License-Identifier: Apache-2.0
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.I2C;

namespace Antmicro.Renode.Peripherals.I2C.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xUdmaI2c : SimpleContainer<II2CPeripheral>, IDoubleWordPeripheral, IKnownSize
    {
        public Bao1xUdmaI2c(Machine machine) : base(machine)
        {
            Reset();
        }

        public override void Reset()
        {
            rxSaddr = rxSize = txSaddr = txSize = cmdSaddr = cmdSize = 0;
            divider = 0;
            nack = false;
            currentSlave = null;
            writeBuffer.Clear();
            isRead = false;
        }

        public uint ReadDoubleWord(long offset)
        {
            switch((Registers)offset)
            {
            case Registers.RxSaddr: return rxSaddr;
            case Registers.RxSize: return 0;         // drained instantly
            case Registers.TxSaddr: return txSaddr;
            case Registers.TxSize: return 0;
            case Registers.CmdSaddr: return cmdSaddr;
            case Registers.CmdSize: return 0;
            case Registers.Status: return 0;         // never busy, no arb loss
            case Registers.Ack:
            {
                var v = nack ? 1u : 0u;
                nack = false;                        // clear on read
                return v;
            }
            default:
                return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch((Registers)offset)
            {
            case Registers.RxSaddr: rxSaddr = value; break;
            case Registers.RxSize: rxSize = value & 0x1ffff; break;
            case Registers.RxCfg: break;             // RX drained on demand
            case Registers.TxSaddr: txSaddr = value; break;
            case Registers.TxSize: txSize = value & 0x1ffff; break;
            case Registers.TxCfg: break;             // TX fetched on demand
            case Registers.CmdSaddr: cmdSaddr = value; break;
            case Registers.CmdSize: cmdSize = value & 0x1ffff; break;
            case Registers.CmdCfg:
                if((value & CfgEnable) != 0)
                {
                    RunCommands();
                }
                break;
            case Registers.Setup:
                if((value & 1) != 0)
                {
                    Reset();
                }
                break;
            default:
                this.Log(LogLevel.Warning, "write 0x{0:X} to unhandled offset 0x{1:X}", value, offset);
                break;
            }
        }

        private void RunCommands()
        {
            var bus = machine.GetSystemBus(this);
            var words = (int)(cmdSize / 4);
            uint repeat = 1;
            var addressPhase = false;

            for(var i = 0; i < words; i++)
            {
                var cmd = bus.ReadDoubleWord(cmdSaddr + (ulong)(4 * i));
                var op = cmd >> 28;
                var arg = cmd & 0x0fffffff;
                var count = repeat;
                repeat = 1;

                switch(op)
                {
                case 0xE:               // CFG
                    divider = (ushort)arg;
                    break;
                case 0x0:               // START (also repeated start)
                    FlushWrite();
                    addressPhase = true;
                    break;
                case 0xC:               // RPT
                    repeat = arg;
                    break;
                case 0x7:               // WRB: immediate byte
                    for(var n = 0; n < count; n++)
                    {
                        if(addressPhase)
                        {
                            SelectSlave((byte)arg);
                            addressPhase = false;
                        }
                        else
                        {
                            writeBuffer.Add((byte)arg);
                        }
                    }
                    break;
                case 0x8:               // WR: bytes from the TX channel
                    for(var n = 0; n < count; n++)
                    {
                        if(txSize == 0)
                        {
                            this.Log(LogLevel.Warning, "WR with empty TX channel");
                            break;
                        }
                        writeBuffer.Add(bus.ReadByte(txSaddr));
                        txSaddr++;
                        txSize--;
                    }
                    break;
                case 0x4:               // RD_ACK
                case 0x6:               // RD_NACK
                    FlushWrite();
                    for(var n = 0; n < count; n++)
                    {
                        byte b = 0xff;
                        if(currentSlave != null)
                        {
                            var data = currentSlave.Read(1);
                            if(data.Length > 0)
                            {
                                b = data[0];
                            }
                        }
                        if(rxSize > 0)
                        {
                            bus.WriteByte(rxSaddr, b);
                            rxSaddr++;
                            rxSize--;
                        }
                    }
                    break;
                case 0x2:               // STOP
                    FlushWrite();
                    currentSlave?.FinishTransmission();
                    currentSlave = null;
                    break;
                case 0x9:               // EOT
                case 0xA:               // WAIT
                case 0x1:               // WAIT_EV
                    break;
                default:
                    this.Log(LogLevel.Warning, "unhandled I2C command 0x{0:X}", op);
                    break;
                }
            }
            cmdSize = 0;
        }

        private void SelectSlave(byte addressByte)
        {
            var address = addressByte >> 1;
            isRead = (addressByte & 1) != 0;
            if(!ChildCollection.TryGetValue(address, out currentSlave))
            {
                this.Log(LogLevel.Warning, "no I2C device at 0x{0:X2}; NACK", address);
                currentSlave = null;
                nack = true;
            }
        }

        private void FlushWrite()
        {
            if(writeBuffer.Count > 0 && currentSlave != null)
            {
                currentSlave.Write(writeBuffer.ToArray());
            }
            writeBuffer.Clear();
        }

        public long Size => 0x1000;

        private const uint CfgEnable = 1u << 4;

        private uint rxSaddr, rxSize, txSaddr, txSize, cmdSaddr, cmdSize;
        private ushort divider;
        private bool nack;
        private bool isRead;
        private II2CPeripheral currentSlave;
        private readonly List<byte> writeBuffer = new List<byte>();

        private enum Registers
        {
            RxSaddr = 0x00,
            RxSize = 0x04,
            RxCfg = 0x08,
            TxSaddr = 0x10,
            TxSize = 0x14,
            TxCfg = 0x18,
            CmdSaddr = 0x20,
            CmdSize = 0x24,
            CmdCfg = 0x28,
            Status = 0x30,
            Setup = 0x34,
            Ack = 0x38,
        }
    }
}
