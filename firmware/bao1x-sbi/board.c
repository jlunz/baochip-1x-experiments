/*
 * bao1x-sbi: board-specific handover fixups.
 *
 * Everything here exists because boot1 hands the next stage a machine that is
 * only half-configured on purpose, and the vendor OS finishes the job in code
 * this port does not have yet.
 *
 * SPDX-License-Identifier: Apache-2.0
 */
#include "bao1x.h"

/*
 * Release the USB SE0 hold on Dabao.
 *
 * boot1 shuts its USB stack down and drives PC13 low before jumping
 * (bao1x-boot/boot1/src/main.rs, boot()), which parks the EMS4000 switch in
 * SE0 so the next stage re-enumerates cleanly with its own USB stack. This
 * port has no USB gadget driver, so nothing ever de-asserted it: every boot so
 * far ended with the port held disconnected, and because the switch sits on
 * VBUS a reset does not clear it -- only a physical replug does. That cost a
 * hardware session (docs/07-board-incident-2026-08-07.md).
 *
 * The sequence mirrors boot1's own setup_dabao_boot_pin(), which is the state
 * boot1 leaves the pin in whenever it wants USB connected: GPIO function,
 * driven high first, pull-up and schmitt trigger on, then the driver released
 * so the pin ends up a pulled-up input. Driving high before releasing means
 * the pin is never left floating in between.
 *
 * Renode models no USB switch and there is no PC13 net to drive, so this is
 * Dabao-only -- and deliberately so: it must not perturb the platform the
 * regression tests run on.
 */
void se0_release(void)
{
#if defined(BOARD_dabao)
    const uint32_t bit = 1u << SE0_PIN;

    /* Pin function back to plain GPIO (boot1 already leaves it there; make it
     * explicit so this does not depend on the handover state). */
    IOX_AFSEL(SE0_PIN_INDEX) =
        (IOX_AFSEL(SE0_PIN_INDEX) & ~(0x3u << IOX_AFSEL_SHIFT(SE0_PIN_INDEX))) |
        (IOX_AFSEL_GPIO << IOX_AFSEL_SHIFT(SE0_PIN_INDEX));

    IOX_OUT(SE0_PORT)  |= bit;      /* drive high: de-assert SE0        */
    IOX_PU(SE0_PORT)   |= bit;      /* pull-up, so releasing holds high */
    IOX_SCHM(SE0_PORT) |= bit;      /* schmitt trigger, as boot1 sets   */
    IOX_OE(SE0_PORT)   &= ~bit;     /* release the driver -> input      */

    uart2_puts("SBI:se0-released\r\n");
#endif
}
