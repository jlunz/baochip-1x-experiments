//
// Bao1x DUART — TX-only debug UART at 0x40042000.
// Hardware reference: baochip-1x rtl/modules/core/rtl/duart.sv
// Registers: SFR_TXD (w), SFR_CR (bit0 = tx enable), SFR_SR (bit0 = tx busy),
//            SFR_ETUC (baud divisor vs perclk; irrelevant in emulation).
//
// SPDX-License-Identifier: Apache-2.0
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.UART;

namespace Antmicro.Renode.Peripherals.UART.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xDuart : BasicDoubleWordPeripheral, IUART, IKnownSize
    {
        public Bao1xDuart(Machine machine) : base(machine)
        {
            DefineRegisters();
        }

        private void DefineRegisters()
        {
            Registers.Txd.Define32(this)
                .WithValueField(0, 8, FieldMode.Write, name: "SFR_TXD", writeCallback: (_, value) =>
                {
                    if(txEnabled.Value)
                    {
                        CharReceived?.Invoke((byte)value);
                    }
                });

            Registers.Cr.Define32(this, 0x1)
                .WithFlag(0, out txEnabled, name: "SFR_CR_TXEN");

            Registers.Sr.Define32(this)
                .WithFlag(0, FieldMode.Read, name: "SFR_SR_TXBUSY",
                          valueProviderCallback: _ => false); // TX completes instantly

            Registers.Etuc.Define32(this)
                .WithValueField(0, 16, name: "SFR_ETUC");
        }

        // IUART: RX direction does not exist in hardware.
        public void WriteChar(byte value)
        {
            this.Log(LogLevel.Warning, "DUART is TX-only; dropping host byte 0x{0:X2}", value);
        }

        public event Action<byte> CharReceived;

        public uint BaudRate => 1000000;
        public Bits StopBits => Bits.One;
        public Parity ParityBit => Parity.None;
        public long Size => 0x10;

        private IFlagRegisterField txEnabled;

        private enum Registers
        {
            Txd = 0x00,
            Cr = 0x04,
            Sr = 0x08,
            Etuc = 0x0c,
        }
    }
}
