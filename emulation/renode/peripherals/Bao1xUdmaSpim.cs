//
// Bao1x uDMA SPI master (PULP udma_spim) — four instances at
// 0x50105000 + n*0x1000.
//
// Hardware reference: baochip-1x rtl/ips/udma/udma_spim/rtl/*.sv,
// rtl/ips/incdir/udma_spim_defines.sv; xous-core libs/bao1x-hal/src/udma/spim.rs.
//
// Same architecture as the uDMA I2C: a CMD channel (+0x20..0x28) streams
// 32-bit commands, payload moves through TX/RX channels. Opcode in
// [31:28]:
//   0x0 CFG (pol<<9|pha<<8|div)   0x1 SOT (arg = chip select 0-3)
//   0x2 SEND_CMD                  0x4 DUMMY
//   0x5 WAIT                      0x6 TX_DATA   0x7 RX_DATA
//   0x8 RPT       0x9 EOT         0xA RPT_END   0xB RX_CHECK
//   0xC FULL_DUPL 0xD SETUP_UCA   0xE SETUP_UCS
// TX/RX_DATA/FULL_DUPL: mode<<27 | endian<<26 | wpx<<21 | (bits-1)<<16
// | (len-1) where len counts words (we model 8-bit words only).
//
// Slaves register at their chip-select index. Commands run instantly on
// the CMD kick; STATUS reads idle.
//
// SPDX-License-Identifier: Apache-2.0
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.SPI.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xUdmaSpim : SimpleContainer<ISPIPeripheral>, IDoubleWordPeripheral, IKnownSize
    {
        public Bao1xUdmaSpim(Machine machine) : base(machine)
        {
            Reset();
        }

        public override void Reset()
        {
            rxSaddr = rxSize = txSaddr = txSize = cmdSaddr = cmdSize = 0;
            selected = null;
        }

        public uint ReadDoubleWord(long offset)
        {
            switch((Registers)offset)
            {
            case Registers.RxSaddr: return rxSaddr;
            case Registers.TxSaddr: return txSaddr;
            case Registers.CmdSaddr: return cmdSaddr;
            case Registers.RxSize:
            case Registers.TxSize:
            case Registers.CmdSize: return 0;    // drained instantly
            case Registers.Status: return 0;     // idle
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch((Registers)offset)
            {
            case Registers.RxSaddr: rxSaddr = value; break;
            case Registers.RxSize: rxSize = value & 0x1ffff; break;
            case Registers.RxCfg: break;
            case Registers.TxSaddr: txSaddr = value; break;
            case Registers.TxSize: txSize = value & 0x1ffff; break;
            case Registers.TxCfg: break;
            case Registers.CmdSaddr: cmdSaddr = value; break;
            case Registers.CmdSize: cmdSize = value & 0x1ffff; break;
            case Registers.CmdCfg:
                if((value & CfgEnable) != 0)
                {
                    RunCommands();
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

            for(var i = 0; i < words; i++)
            {
                var cmd = bus.ReadDoubleWord(cmdSaddr + (ulong)(4 * i));
                var op = cmd >> 28;
                var count = repeat;
                repeat = 1;

                switch(op)
                {
                case 0x0:       // CFG: pol/pha/div — timing, ignored
                    break;
                case 0x1:       // SOT: assert chip select
                    if(!ChildCollection.TryGetValue((int)(cmd & 0x3), out selected))
                    {
                        this.Log(LogLevel.Warning, "no SPI device at CS{0}", cmd & 0x3);
                        selected = null;
                    }
                    break;
                case 0x2:       // SEND_CMD: up to 16 bits, left-aligned value
                {
                    var bits = ((cmd >> 16) & 0x1f) + 1;
                    var val = cmd & 0xffff;
                    for(var shift = (int)bits - 8; shift >= 0; shift -= 8)
                    {
                        selected?.Transmit((byte)(val >> shift));
                    }
                    break;
                }
                case 0x6:       // TX_DATA: bytes from the TX channel
                {
                    var len = (cmd & 0xffff) + 1;
                    for(var n = 0u; n < len * count && txSize > 0; n++)
                    {
                        selected?.Transmit(bus.ReadByte(txSaddr));
                        txSaddr++;
                        txSize--;
                    }
                    break;
                }
                case 0x7:       // RX_DATA: bytes to the RX channel
                {
                    var len = (cmd & 0xffff) + 1;
                    for(var n = 0u; n < len * count && rxSize > 0; n++)
                    {
                        var b = selected != null ? selected.Transmit(0xff) : (byte)0xff;
                        bus.WriteByte(rxSaddr, b);
                        rxSaddr++;
                        rxSize--;
                    }
                    break;
                }
                case 0xC:       // FULL_DUPL: simultaneous TX/RX
                {
                    var len = (cmd & 0xffff) + 1;
                    for(var n = 0u; n < len && txSize > 0; n++)
                    {
                        var b = selected != null ? selected.Transmit(bus.ReadByte(txSaddr))
                                                 : (byte)0xff;
                        txSaddr++;
                        txSize--;
                        if(rxSize > 0)
                        {
                            bus.WriteByte(rxSaddr, b);
                            rxSaddr++;
                            rxSize--;
                        }
                    }
                    break;
                }
                case 0x8:       // RPT
                    repeat = cmd & 0xffff;
                    break;
                case 0x9:       // EOT: deassert CS
                    selected?.FinishTransmission();
                    selected = null;
                    break;
                case 0x4:       // DUMMY
                case 0x5:       // WAIT
                case 0xA:       // RPT_END
                    break;
                default:
                    this.Log(LogLevel.Warning, "unhandled SPIM command 0x{0:X}", op);
                    break;
                }
            }
            cmdSize = 0;
        }

        public long Size => 0x1000;

        private const uint CfgEnable = 1u << 4;

        private uint rxSaddr, rxSize, txSaddr, txSize, cmdSaddr, cmdSize;
        private ISPIPeripheral selected;

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
        }
    }
}
