//
// Bao1x uDMA UART (PULP udma_uart) — one of four instances at
// 0x50101000 + n*0x1000 (uart2 = 0x50103000 is the Dabao console).
//
// Hardware reference: baochip-1x rtl/ips/udma/udma_uart/rtl/udma_uart_reg_if.sv
// and udma_uart_rx/tx.sv; xous-core libs/bao1x-hal/src/udma/uart.rs.
//
// Programming model (as the Linux driver will use it):
//   TX is DMA-only: software writes TX_SADDR/TX_SIZE (buffer must be in
//     IFRAM on real hardware), sets TX_CFG.EN, polls TX_SIZE readback
//     (bytes left) or STATUS.TX_BUSY, gets a "tx done" event.
//   RX has a PIO path: when SETUP.RX_POLLING_EN or IRQ_EN.RX is set,
//     received chars land in DATA with VALID.0 as the ready flag (reading
//     DATA clears it); each char raises the "rx_char" event when IRQ_EN.RX.
//     The DMA RX path (RX_SADDR/RX_SIZE/RX_CFG) is modeled too.
//
// Events fan out to IRQARRAY5 slots (uartN base = 4*N): +0 rx (DMA done),
// +1 tx (DMA done), +2 rx_char, +3 err — exposed here as four GPIO outputs
// to be wired in the .repl. Events are pulses; the irq array latches them.
//
// Simplifications: DMA transfers complete instantly (STATUS busy always
// reads 0, TX_SIZE readback drops straight to 0); baud divider is stored
// but does not pace anything; TX continuous mode is not supported (logged).
// The UDMA_CTRL clock gate is modeled separately and not enforced here.
//
// SPDX-License-Identifier: Apache-2.0
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.UART;

namespace Antmicro.Renode.Peripherals.UART.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xUdmaUart : BasicDoubleWordPeripheral, IUART, IKnownSize
    {
        public Bao1xUdmaUart(Machine machine, uint clockFrequency = 100000000) : base(machine)
        {
            this.clockFrequency = clockFrequency;
            RxDmaIRQ = new GPIO();
            TxDmaIRQ = new GPIO();
            RxCharIRQ = new GPIO();
            ErrIRQ = new GPIO();
            rxQueue = new Queue<byte>();
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
            lock(rxQueue)
            {
                rxQueue.Clear();
            }
            rxDmaAddress = 0;
            rxDmaBytesLeft = 0;
        }

        // Host -> device: a character arrives on the wire.
        public void WriteChar(byte value)
        {
            lock(rxQueue)
            {
                if(!rxEnabled.Value)
                {
                    this.Log(LogLevel.Warning, "RX disabled (SETUP.EN_RX=0); dropping 0x{0:X2}", value);
                    return;
                }
                if(rxPollingEnabled.Value || rxIrqEnabled.Value)
                {
                    // PIO path: VALID/DATA register pair.
                    rxQueue.Enqueue(value);
                    if(rxIrqEnabled.Value)
                    {
                        Pulse(RxCharIRQ);
                    }
                }
                else if(rxDmaEnabled.Value && rxDmaBytesLeft > 0)
                {
                    machine.GetSystemBus(this).WriteByte(rxDmaAddress, value);
                    rxDmaAddress++;
                    rxDmaBytesLeft--;
                    if(rxDmaBytesLeft == 0)
                    {
                        rxDmaEnabled.Value = rxContinuous.Value;
                        if(rxContinuous.Value)
                        {
                            rxDmaAddress = rxDmaStartAddress;
                            rxDmaBytesLeft = rxDmaSize;
                        }
                        Pulse(RxDmaIRQ);
                    }
                }
                else
                {
                    this.Log(LogLevel.Warning, "no RX consumer (PIO off, DMA idle); dropping 0x{0:X2}", value);
                }
            }
        }

        private void DoDmaTx()
        {
            var count = (int)txDmaSize;
            if(count == 0)
            {
                return;
            }
            if(txContinuous.Value)
            {
                this.Log(LogLevel.Error, "TX continuous mode is not modeled; sending once");
            }
            var data = machine.GetSystemBus(this).ReadBytes(txDmaStartAddress, count);
            foreach(var b in data)
            {
                CharReceived?.Invoke(b);
            }
            txDmaBytesLeft = 0;   // instant completion
            Pulse(TxDmaIRQ);
        }

        private void Pulse(GPIO irq)
        {
            // uDMA events are single-cycle pulses; the IRQARRAY latches the edge.
            irq.Set(true);
            irq.Set(false);
        }

        private void DefineRegisters()
        {
            Registers.RxSaddr.Define32(this)
                .WithValueField(0, 32, name: "RX_SADDR",
                    valueProviderCallback: _ => rxDmaAddress,
                    writeCallback: (_, value) =>
                    {
                        rxDmaStartAddress = (uint)value;
                        rxDmaAddress = (uint)value;
                    });

            Registers.RxSize.Define32(this)
                .WithValueField(0, 17, name: "RX_SIZE",
                    valueProviderCallback: _ => rxDmaBytesLeft,
                    writeCallback: (_, value) =>
                    {
                        rxDmaSize = (uint)value;
                        rxDmaBytesLeft = (uint)value;
                    });

            Registers.RxCfg.Define32(this)
                .WithFlag(0, out rxContinuous, name: "RX_CFG_CONTINUOUS")
                .WithReservedBits(1, 3)
                .WithFlag(4, out rxDmaEnabled, name: "RX_CFG_EN")
                .WithReservedBits(5, 1)
                .WithFlag(6, FieldMode.Write, name: "RX_CFG_CLR", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        rxDmaEnabled.Value = false;
                        rxDmaBytesLeft = 0;
                    }
                });

            Registers.TxSaddr.Define32(this)
                .WithValueField(0, 32, name: "TX_SADDR",
                    valueProviderCallback: _ => txDmaStartAddress,
                    writeCallback: (_, value) => txDmaStartAddress = (uint)value);

            Registers.TxSize.Define32(this)
                .WithValueField(0, 17, name: "TX_SIZE",
                    valueProviderCallback: _ => txDmaBytesLeft,   // bytes left; 0 when done
                    writeCallback: (_, value) =>
                    {
                        txDmaSize = (uint)value;
                        txDmaBytesLeft = (uint)value;
                    });

            Registers.TxCfg.Define32(this)
                .WithFlag(0, out txContinuous, name: "TX_CFG_CONTINUOUS")
                .WithReservedBits(1, 3)
                .WithFlag(4, name: "TX_CFG_EN",
                    valueProviderCallback: _ => false,   // instant completion
                    writeCallback: (_, value) =>
                    {
                        if(value && txEnabled.Value)
                        {
                            DoDmaTx();
                        }
                        else if(value)
                        {
                            this.Log(LogLevel.Warning, "TX DMA kicked with SETUP.EN_TX=0; ignored");
                        }
                    })
                .WithReservedBits(5, 1)
                .WithFlag(6, FieldMode.Write, name: "TX_CFG_CLR", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        txDmaBytesLeft = 0;
                    }
                });

            Registers.Status.Define32(this)
                .WithFlag(0, FieldMode.Read, name: "STATUS_TX_BUSY", valueProviderCallback: _ => false)
                .WithFlag(1, FieldMode.Read, name: "STATUS_RX_BUSY", valueProviderCallback: _ => false);

            Registers.Setup.Define32(this)
                .WithFlag(0, out parityEnabled, name: "SETUP_PARITY_ENA")
                .WithValueField(1, 2, out bitLength, name: "SETUP_BIT_LENGTH")
                .WithFlag(3, out stopBits, name: "SETUP_STOP_BITS")
                .WithFlag(4, out rxPollingEnabled, name: "SETUP_RX_POLLING_EN")
                .WithFlag(5, FieldMode.Write, name: "SETUP_RX_CLEAN_FIFO", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        lock(rxQueue)
                        {
                            rxQueue.Clear();
                        }
                    }
                })
                .WithReservedBits(6, 2)
                .WithFlag(8, out txEnabled, name: "SETUP_TX_ENA")
                .WithFlag(9, out rxEnabled, name: "SETUP_RX_ENA")
                .WithReservedBits(10, 6)
                .WithValueField(16, 16, out clockDivider, name: "SETUP_CLKDIV");

            Registers.Error.Define32(this)
                .WithFlag(0, FieldMode.Read, name: "ERROR_OVERFLOW", valueProviderCallback: _ => false)
                .WithFlag(1, FieldMode.Read, name: "ERROR_PARITY", valueProviderCallback: _ => false);

            Registers.IrqEn.Define32(this)
                .WithFlag(0, out rxIrqEnabled, name: "IRQ_EN_RX")
                .WithFlag(1, out errIrqEnabled, name: "IRQ_EN_ERR");

            Registers.Valid.Define32(this)
                .WithFlag(0, FieldMode.Read, name: "VALID_RX",
                    valueProviderCallback: _ =>
                    {
                        lock(rxQueue)
                        {
                            return rxQueue.Count > 0;
                        }
                    });

            Registers.Data.Define32(this)
                .WithValueField(0, 8, FieldMode.Read, name: "DATA",
                    valueProviderCallback: _ =>
                    {
                        lock(rxQueue)
                        {
                            return rxQueue.Count > 0 ? rxQueue.Dequeue() : (byte)0;
                        }
                    });
        }

        public event Action<byte> CharReceived;

        public GPIO RxDmaIRQ { get; }
        public GPIO TxDmaIRQ { get; }
        public GPIO RxCharIRQ { get; }
        public GPIO ErrIRQ { get; }

        public uint BaudRate
        {
            get
            {
                var div = (uint)clockDivider.Value;
                return div == 0 ? 1000000 : clockFrequency / div;
            }
        }
        public Bits StopBits => stopBits.Value ? Bits.Two : Bits.One;
        public Parity ParityBit => parityEnabled.Value ? Parity.Even : Parity.None;
        public long Size => 0x1000;

        private readonly uint clockFrequency;
        private readonly Queue<byte> rxQueue;

        private uint rxDmaStartAddress;
        private uint rxDmaAddress;
        private uint rxDmaSize;
        private uint rxDmaBytesLeft;
        private uint txDmaStartAddress;
        private uint txDmaSize;
        private uint txDmaBytesLeft;

        private IFlagRegisterField rxContinuous;
        private IFlagRegisterField rxDmaEnabled;
        private IFlagRegisterField txContinuous;
        private IFlagRegisterField parityEnabled;
        private IValueRegisterField bitLength;
        private IFlagRegisterField stopBits;
        private IFlagRegisterField rxPollingEnabled;
        private IFlagRegisterField txEnabled;
        private IFlagRegisterField rxEnabled;
        private IFlagRegisterField rxIrqEnabled;
        private IFlagRegisterField errIrqEnabled;
        private IValueRegisterField clockDivider;

        private enum Registers
        {
            RxSaddr = 0x00,
            RxSize = 0x04,
            RxCfg = 0x08,
            TxSaddr = 0x10,
            TxSize = 0x14,
            TxCfg = 0x18,
            Status = 0x20,
            Setup = 0x24,
            Error = 0x28,
            IrqEn = 0x2c,
            Valid = 0x30,
            Data = 0x34,
        }
    }
}
