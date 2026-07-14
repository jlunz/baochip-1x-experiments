//
// Bao1x TICKTIMER — 64-bit millisecond-resolution free-running counter with
// msleep alarm. Base 0xE001B000, external interrupt array bit 20.
//
// Register layout from xous-core utralib/bao1x/core.svd:
//   0x00 CONTROL (bit0 = reset counter, bit1 = pause)
//   0x04 TIME1 / 0x08 TIME0            (64-bit tick count, ms)
//   0x0c MSLEEP_TARGET1 / 0x10 MSLEEP_TARGET0
//   0x14 EV_STATUS, 0x18 EV_PENDING (w1c), 0x1c EV_ENABLE (alarm event)
//   0x20 CLOCKS_PER_TICK (hw divider from the source clock; default 800000)
//
// Adapted from xous-core emulation/peripherals/ticktimer.cs (Antmicro, MIT);
// bao1x adds CLOCKS_PER_TICK, modelled as a plain store (Renode ticks in ms
// regardless).
//
// SPDX-License-Identifier: MIT
//
using System.Threading;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xTickTimer : BasicDoubleWordPeripheral, IKnownSize
    {
        public Bao1xTickTimer(Machine machine, ulong periodInMs = 1) : base(machine)
        {
            machine.ClockSource.AddClockEntry(new ClockEntry(periodInMs, 1000, OnTick, this, "bao1x_ticktimer"));
            IRQ = new GPIO();
            SupervisorIRQ = new GPIO();
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
            tickValue = 0;
            paused = false;
            msleepTarget = ulong.MaxValue;
            RegistersCollection.Reset();
        }

        private void OnTick()
        {
            if(!paused)
            {
                Interlocked.Increment(ref tickValue);
                if((ulong)tickValue >= msleepTarget && irqEnabled.Value)
                {
                    irqPending.Value = true;
                }
                UpdateInterrupts();
            }
        }

        private void UpdateInterrupts()
        {
            var active = irqPending.Value && irqEnabled.Value;
            IRQ.Set(active);
            SupervisorIRQ.Set(active);
        }

        private void DefineRegisters()
        {
            Registers.Control.Define32(this)
                .WithFlag(0, FieldMode.Write, name: "RESET", writeCallback: (_, value) =>
                {
                    if(value) { tickValue = 0; }
                })
                .WithFlag(1, name: "PAUSE",
                    valueProviderCallback: _ => paused,
                    writeCallback: (_, value) => paused = value);

            Registers.Time1.Define32(this)
                .WithValueField(0, 32, FieldMode.Read, name: "TIME1",
                    valueProviderCallback: _ => (uint)(Interlocked.Read(ref tickValue) >> 32));

            Registers.Time0.Define32(this)
                .WithValueField(0, 32, FieldMode.Read, name: "TIME0",
                    valueProviderCallback: _ => (uint)Interlocked.Read(ref tickValue));

            Registers.MsleepTarget1.Define32(this)
                .WithValueField(0, 32, name: "MSLEEP_TARGET1",
                    valueProviderCallback: _ => (uint)(msleepTarget >> 32),
                    writeCallback: (_, value) =>
                        msleepTarget = (msleepTarget & 0x0000_0000_FFFF_FFFF) | ((ulong)value << 32));

            Registers.MsleepTarget0.Define32(this)
                .WithValueField(0, 32, name: "MSLEEP_TARGET0",
                    valueProviderCallback: _ => (uint)msleepTarget,
                    writeCallback: (_, value) =>
                        msleepTarget = (msleepTarget & 0xFFFF_FFFF_0000_0000) | value);

            Registers.EventStatus.Define32(this)
                .WithFlag(0, FieldMode.Read, name: "EV_STATUS",
                    valueProviderCallback: _ => (ulong)Interlocked.Read(ref tickValue) >= msleepTarget);

            Registers.EventPending.Define32(this)
                .WithFlag(0, out irqPending, FieldMode.Read | FieldMode.WriteOneToClear,
                    name: "EV_PENDING", changeCallback: (_, __) => UpdateInterrupts());

            Registers.EventEnable.Define32(this)
                .WithFlag(0, out irqEnabled, name: "EV_ENABLE",
                    changeCallback: (_, __) => UpdateInterrupts());

            Registers.ClocksPerTick.Define32(this, 800000)
                .WithValueField(0, 32, name: "CLOCKS_PER_TICK");
        }

        public GPIO IRQ { get; }
        public GPIO SupervisorIRQ { get; }
        public long Size => 0x24;

        private long tickValue;
        private bool paused;
        private ulong msleepTarget;
        private IFlagRegisterField irqEnabled;
        private IFlagRegisterField irqPending;

        private enum Registers
        {
            Control = 0x00,
            Time1 = 0x04,
            Time0 = 0x08,
            MsleepTarget1 = 0x0c,
            MsleepTarget0 = 0x10,
            EventStatus = 0x14,
            EventPending = 0x18,
            EventEnable = 0x1c,
            ClocksPerTick = 0x20,
        }
    }
}
