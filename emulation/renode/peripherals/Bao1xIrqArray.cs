//
// Bao1x IRQARRAY bank — LiteX EventManager-style interrupt aggregator.
// 20 instances at 0xE0004000 + bank*0x1000, each collecting up to 16 event
// sources onto one line of the VexRiscv 32-bit external interrupt array.
//
// Hardware reference: xous-core utralib/bao1x/core.svd (IRQARRAY0..19).
// Registers: EV_SOFT (w: trigger from software), EV_EDGE_TRIGGERED,
//            EV_POLARITY, EV_STATUS (raw levels), EV_PENDING (w1c),
//            EV_ENABLE.
//
// The physical line feeds both the machine (CSR 0xFC0) and supervisor
// (CSR 0xDC0) pending arrays; Renode models those as separate GPIO ranges,
// so this peripheral exposes two identical outputs:
//   IRQ           -> cpu@<bank>        (machine array bit)
//   SupervisorIRQ -> cpu@<1000+bank>   (supervisor array bit)
//
// Simplifications vs RTL: sources are treated as edge events latched into
// EV_PENDING (EV_EDGE_TRIGGERED/EV_POLARITY are stored but not interpreted;
// uDMA-style event pulses are what we model). Revisit against Verilator if a
// driver depends on level semantics.
//
// SPDX-License-Identifier: Apache-2.0
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.IRQControllers.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xIrqArray : BasicDoubleWordPeripheral, IKnownSize, IGPIOReceiver
    {
        public Bao1xIrqArray(Machine machine) : base(machine)
        {
            IRQ = new GPIO();
            SupervisorIRQ = new GPIO();
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
            status = 0;
            pending = 0;
            enable = 0;
            Update();
        }

        // Event source inputs (bank slots 0..15), driven by peripheral models.
        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number > 15)
            {
                this.Log(LogLevel.Error, "IRQARRAY slot {0} out of range", number);
                return;
            }
            var bit = 1u << number;
            if(value)
            {
                status |= bit;
                pending |= bit;      // latch on rising edge
            }
            else
            {
                status &= ~bit;
            }
            Update();
        }

        private void Update()
        {
            var active = (pending & enable) != 0;
            IRQ.Set(active);
            SupervisorIRQ.Set(active);
        }

        private void DefineRegisters()
        {
            Registers.EvSoft.Define32(this)
                .WithValueField(0, 16, FieldMode.Write, name: "EV_SOFT", writeCallback: (_, value) =>
                {
                    pending |= (uint)value;   // software-triggered events
                    Update();
                });

            Registers.EvEdgeTriggered.Define32(this)
                .WithValueField(0, 16, name: "EV_EDGE_TRIGGERED");

            Registers.EvPolarity.Define32(this)
                .WithValueField(0, 16, name: "EV_POLARITY");

            Registers.EvStatus.Define32(this)
                .WithValueField(0, 16, FieldMode.Read, name: "EV_STATUS",
                                valueProviderCallback: _ => status);

            Registers.EvPending.Define32(this)
                .WithValueField(0, 16, name: "EV_PENDING",
                    valueProviderCallback: _ => pending,
                    writeCallback: (_, value) =>
                    {
                        pending &= ~(uint)value;   // write-1-to-clear
                        Update();
                    });

            Registers.EvEnable.Define32(this)
                .WithValueField(0, 16, name: "EV_ENABLE",
                    valueProviderCallback: _ => enable,
                    writeCallback: (_, value) =>
                    {
                        enable = (uint)value;
                        Update();
                    });
        }

        public GPIO IRQ { get; }
        public GPIO SupervisorIRQ { get; }
        public long Size => 0x1000;

        private uint status;
        private uint pending;
        private uint enable;

        private enum Registers
        {
            EvSoft = 0x00,
            EvEdgeTriggered = 0x04,
            EvPolarity = 0x08,
            EvStatus = 0x0c,
            EvPending = 0x10,
            EvEnable = 0x14,
        }
    }
}
