//
// Bao1x uDMA controller global registers at 0x50100000.
//
// Hardware reference: baochip-1x rtl/ips/udma/udma_core/rtl/common/udma_ctrl.sv
//   REG_CG  (0x00): bit N = 1 enables the clock of peripheral N
//                   (uart0..3 = bits 0..3, spim0..3 = 4..7, i2c0..3 = 8..11,
//                   sdio=12, i2s=13, cam=14, filter=15, scif=16, spis0/1=17/18,
//                   adc=19 — xous bao1x-api PeriphId). Reset: all gated off.
//   REG_CFG_EVT (0x04): four 8-bit event IDs routed to the DMA trigger
//                   channels — not in the interrupt path.
//   REG_RST (0x08): write bit = hold peripheral N in reset, write 0 = release.
//
// This is a state-holding stub: peripherals in emulation work regardless of
// gating (the models don't check it), but the register readbacks behave so
// driver init sequences can be validated. Gating enforcement can be added
// later by wiring peripherals to query this model.
//
// SPDX-License-Identifier: Apache-2.0
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xUdmaCtrl : BasicDoubleWordPeripheral, IKnownSize
    {
        public Bao1xUdmaCtrl(Machine machine) : base(machine)
        {
            DefineRegisters();
        }

        private void DefineRegisters()
        {
            Registers.ClockGate.Define32(this)
                .WithValueField(0, 20, name: "REG_CG", changeCallback: (_, value) =>
                    this.Log(LogLevel.Debug, "peripheral clock enables -> 0x{0:X5}", value));

            Registers.EventCfg.Define32(this)
                .WithValueField(0, 32, name: "REG_CFG_EVT");

            Registers.Reset.Define32(this)
                .WithValueField(0, 20, name: "REG_RST", changeCallback: (_, value) =>
                {
                    if(value != 0)
                    {
                        this.Log(LogLevel.Debug, "peripheral reset asserted: 0x{0:X5}", value);
                    }
                });
        }

        public long Size => 0x1000;

        private enum Registers
        {
            ClockGate = 0x00,
            EventCfg = 0x04,
            Reset = 0x08,
        }
    }
}
