//
// Bao1x IOX — pin mux + GPIO controller at 0x5012F000.
//
// Hardware reference: baochip-1x rtl/modules/ifsub/rtl/iox.sv, register
// map from utralib bao1x_peri.svd. 6 ports (PA..PF) x 16 pins:
//   0x000..0x02c  AFSEL   2 bits/pin, two registers per port (AF0=GPIO)
//   0x100..0x11c  INTCR   8 slots {wkupen,inten,mode[1:0],pinsel} (stored)
//   0x120         INTFR   interrupt flags (stub, reads 0)
//   0x130..0x144  GPIOOUT per-port output value
//   0x148..0x15c  GPIOOE  per-port output enable
//   0x160..0x174  GPIOPU  per-port pull-up enable (stored)
//   0x178..0x18c  GPIOIN  per-port input; reads (OE&OUT) | (~OE&external)
//   0x200         PIOSEL, 0x230.. schmitt/slew/drive (stored)
//
// GPIO numbering for Renode connections: port*16 + pin (PA0=0 .. PF15=95).
// Outputs drive Connections[n] when OE is set; external inputs arrive via
// OnGPIO(n, value). Pin muxing is stored but not enforced (peripherals in
// this platform model their pins independently).
//
// SPDX-License-Identifier: Apache-2.0
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous.Baochip
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class Bao1xIox : IDoubleWordPeripheral, IKnownSize, IGPIOReceiver, INumberedGPIOOutput
    {
        public Bao1xIox(Machine machine)
        {
            this.machine = machine;
            var connections = new Dictionary<int, IGPIO>();
            for(var i = 0; i < NrPins; i++)
            {
                connections[i] = new GPIO();
            }
            Connections = connections;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(afsel, 0, afsel.Length);
            Array.Clear(intcr, 0, intcr.Length);
            Array.Clear(gpioOut, 0, gpioOut.Length);
            Array.Clear(gpioOe, 0, gpioOe.Length);
            Array.Clear(gpioPu, 0, gpioPu.Length);
            Array.Clear(extIn, 0, extIn.Length);
            Array.Clear(misc, 0, misc.Length);
        }

        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= NrPins)
            {
                this.Log(LogLevel.Error, "pin {0} out of range", number);
                return;
            }
            var port = number / 16;
            var bit = 1u << (number % 16);
            if(value)
            {
                extIn[port] |= bit;
            }
            else
            {
                extIn[port] &= ~bit;
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= 0x000 && offset < 0x030)
            {
                return afsel[offset / 4];
            }
            if(offset >= 0x100 && offset < 0x120)
            {
                return intcr[(offset - 0x100) / 4];
            }
            if(offset == 0x120)
            {
                return 0;   // INTFR: no interrupt slots modeled yet
            }
            if(offset >= 0x130 && offset < 0x148)
            {
                return gpioOut[(offset - 0x130) / 4];
            }
            if(offset >= 0x148 && offset < 0x160)
            {
                return gpioOe[(offset - 0x148) / 4];
            }
            if(offset >= 0x160 && offset < 0x178)
            {
                return gpioPu[(offset - 0x160) / 4];
            }
            if(offset >= 0x178 && offset < 0x190)
            {
                var port = (int)(offset - 0x178) / 4;
                // Pad readback: driven value where output-enabled, external
                // input elsewhere.
                return (gpioOe[port] & gpioOut[port]) | (~gpioOe[port] & extIn[port]);
            }
            if(offset >= 0x200 && offset < Size)
            {
                return misc[(offset - 0x200) / 4];
            }
            this.Log(LogLevel.Warning, "read from unhandled offset 0x{0:X}", offset);
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= 0x000 && offset < 0x030)
            {
                afsel[offset / 4] = value;
                return;
            }
            if(offset >= 0x100 && offset < 0x120)
            {
                intcr[(offset - 0x100) / 4] = value;
                return;
            }
            if(offset == 0x120)
            {
                return;     // INTFR w1c: nothing pends yet
            }
            if(offset >= 0x130 && offset < 0x148)
            {
                var port = (int)(offset - 0x130) / 4;
                gpioOut[port] = value & 0xffff;
                UpdatePort(port);
                return;
            }
            if(offset >= 0x148 && offset < 0x160)
            {
                var port = (int)(offset - 0x148) / 4;
                gpioOe[port] = value & 0xffff;
                UpdatePort(port);
                return;
            }
            if(offset >= 0x160 && offset < 0x178)
            {
                gpioPu[(offset - 0x160) / 4] = value & 0xffff;
                return;
            }
            if(offset >= 0x200 && offset < Size)
            {
                misc[(offset - 0x200) / 4] = value;
                return;
            }
            this.Log(LogLevel.Warning, "write 0x{0:X} to unhandled offset 0x{1:X}", value, offset);
        }

        private void UpdatePort(int port)
        {
            for(var pin = 0; pin < 16; pin++)
            {
                var driven = (gpioOe[port] & (1u << pin)) != 0 &&
                             (gpioOut[port] & (1u << pin)) != 0;
                Connections[port * 16 + pin].Set(driven);
            }
        }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }
        public long Size => 0x300;

        private const int NrPins = 96;

        private readonly Machine machine;
        private readonly uint[] afsel = new uint[12];
        private readonly uint[] intcr = new uint[8];
        private readonly uint[] gpioOut = new uint[6];
        private readonly uint[] gpioOe = new uint[6];
        private readonly uint[] gpioPu = new uint[6];
        private readonly uint[] extIn = new uint[6];
        private readonly uint[] misc = new uint[0x100 / 4];
    }
}
