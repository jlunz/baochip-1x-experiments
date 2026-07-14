//
// Bao1x TIMER0 — LiteX 32-bit down-counting timer, one-shot or periodic.
// Base 0xE001C000, external interrupt array bit 30.
//
// Register layout from xous-core utralib/bao1x/core.svd (this LiteX revision
// has UPDATE_VALUE/VALUE which older models, e.g. betrusted's, lack):
//   0x00 LOAD, 0x04 RELOAD, 0x08 EN, 0x0c UPDATE_VALUE, 0x10 VALUE,
//   0x14 EV_STATUS, 0x18 EV_PENDING (w1c), 0x1c EV_ENABLE
// Semantics (LiteX timer core): while EN=1 counter counts LOAD (once) down
// to 0; on zero it raises the `zero` event and reloads from RELOAD
// (RELOAD=0 -> one-shot). VALUE is latched by writing UPDATE_VALUE.
//
// Structure adapted from xous-core emulation/peripherals/LiteX_Timer_32.cs
// (Antmicro, MIT).
//
// SPDX-License-Identifier: MIT
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xLiteXTimer : BasicDoubleWordPeripheral, IKnownSize
    {
        public Bao1xLiteXTimer(Machine machine, long frequency) : base(machine)
        {
            IRQ = new GPIO();
            SupervisorIRQ = new GPIO();
            innerTimer = new LimitTimer(machine.ClockSource, (ulong)frequency, this, "bao1x_timer0",
                                        eventEnabled: true, autoUpdate: true);
            innerTimer.LimitReached += delegate
            {
                irqPending.Value = true;
                UpdateInterrupts();
                if(reloadValue == 0)
                {
                    innerTimer.Enabled = false;   // one-shot
                }
                else
                {
                    innerTimer.Limit = reloadValue;
                }
            };
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
            innerTimer.Reset();
            loadValue = 0;
            reloadValue = 0;
            latchedValue = 0;
            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            var active = irqPending.Value && irqEnabled.Value;
            IRQ.Set(active);
            SupervisorIRQ.Set(active);
        }

        private void DefineRegisters()
        {
            Registers.Load.Define32(this)
                .WithValueField(0, 32, name: "LOAD",
                    valueProviderCallback: _ => loadValue,
                    writeCallback: (_, value) => loadValue = (uint)value);

            Registers.Reload.Define32(this)
                .WithValueField(0, 32, name: "RELOAD",
                    valueProviderCallback: _ => reloadValue,
                    writeCallback: (_, value) => reloadValue = (uint)value);

            Registers.Enable.Define32(this)
                .WithFlag(0, name: "EN",
                    valueProviderCallback: _ => innerTimer.Enabled,
                    writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            // LiteX semantics: EN 0->1 loads the counter from LOAD
                            innerTimer.Limit = loadValue != 0 ? loadValue : reloadValue;
                            innerTimer.ResetValue();
                        }
                        innerTimer.Enabled = value;
                    });

            Registers.UpdateValue.Define32(this)
                .WithFlag(0, FieldMode.Write, name: "UPDATE_VALUE", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        // Down-counter: remaining ticks until zero
                        latchedValue = (uint)(innerTimer.Limit - innerTimer.Value);
                    }
                });

            Registers.Value.Define32(this)
                .WithValueField(0, 32, FieldMode.Read, name: "VALUE",
                    valueProviderCallback: _ => latchedValue);

            Registers.EventStatus.Define32(this)
                .WithFlag(0, FieldMode.Read, name: "EV_STATUS",
                    valueProviderCallback: _ => innerTimer.Value == 0);

            Registers.EventPending.Define32(this)
                .WithFlag(0, out irqPending, FieldMode.Read | FieldMode.WriteOneToClear,
                    name: "EV_PENDING", changeCallback: (_, __) => UpdateInterrupts());

            Registers.EventEnable.Define32(this)
                .WithFlag(0, out irqEnabled, name: "EV_ENABLE",
                    changeCallback: (_, __) => UpdateInterrupts());
        }

        public GPIO IRQ { get; }
        public GPIO SupervisorIRQ { get; }
        public long Size => 0x20;

        private readonly LimitTimer innerTimer;
        private IFlagRegisterField irqEnabled;
        private IFlagRegisterField irqPending;
        private uint loadValue;
        private uint reloadValue;
        private uint latchedValue;

        private enum Registers
        {
            Load = 0x00,
            Reload = 0x04,
            Enable = 0x08,
            UpdateValue = 0x0c,
            Value = 0x10,
            EventStatus = 0x14,
            EventPending = 0x18,
            EventEnable = 0x1c,
        }
    }
}
