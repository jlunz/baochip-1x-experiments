/*
 * bao1x Renode platform smoke test (M-mode).
 *
 * Exercises exactly the mechanisms the Linux port depends on:
 *   1. DUART TX               (console path)
 *   2. VexRiscv external-irq CSRs 0xBC0/0xFC0 + IRQARRAY EV_SOFT  (irqchip path)
 *   3. TIMER0 one-shot interrupt                                   (clockevent path)
 *   4. TICKTIMER monotonic 64-bit count                            (clocksource path)
 *   5. uDMA UART2: clock ungate, DMA TX from IFRAM0, rx_char irq
 *      via IRQARRAY5 + PIO RX                                      (console path)
 *
 * Prints "ALL TESTS PASSED" on success; any failure prints "FAIL:" details.
 * The robot runner injects "K" on uart2 for the RX half of test 5.
 */

#include <stdint.h>

#define DUART_BASE     0x40042000u
#define DUART_TXD      (*(volatile uint32_t *)(DUART_BASE + 0x0))
#define DUART_CR       (*(volatile uint32_t *)(DUART_BASE + 0x4))
#define DUART_SR       (*(volatile uint32_t *)(DUART_BASE + 0x8))

#define IRQARRAY0_BASE 0xE0004000u   /* bank 0 -> ext irq bit 0 */
#define EV_SOFT        0x00
#define EV_STATUS      0x0c
#define EV_PENDING     0x10
#define EV_ENABLE      0x14
#define IRQARRAY0(reg) (*(volatile uint32_t *)(IRQARRAY0_BASE + (reg)))

#define TIMER0_BASE    0xE001C000u
#define TIMER0_LOAD    (*(volatile uint32_t *)(TIMER0_BASE + 0x00))
#define TIMER0_RELOAD  (*(volatile uint32_t *)(TIMER0_BASE + 0x04))
#define TIMER0_EN      (*(volatile uint32_t *)(TIMER0_BASE + 0x08))
#define TIMER0_EV_PENDING (*(volatile uint32_t *)(TIMER0_BASE + 0x18))
#define TIMER0_EV_ENABLE  (*(volatile uint32_t *)(TIMER0_BASE + 0x1c))

#define TICKTIMER_BASE 0xE001B000u
#define TICKTIMER_TIME1 (*(volatile uint32_t *)(TICKTIMER_BASE + 0x04))
#define TICKTIMER_TIME0 (*(volatile uint32_t *)(TICKTIMER_BASE + 0x08))

#define UDMA_CTRL_CG   (*(volatile uint32_t *)0x50100000u)
#define UART2_BASE     0x50103000u
#define UART2_TX_SADDR (*(volatile uint32_t *)(UART2_BASE + 0x10))
#define UART2_TX_SIZE  (*(volatile uint32_t *)(UART2_BASE + 0x14))
#define UART2_TX_CFG   (*(volatile uint32_t *)(UART2_BASE + 0x18))
#define UART2_SETUP    (*(volatile uint32_t *)(UART2_BASE + 0x24))
#define UART2_IRQ_EN   (*(volatile uint32_t *)(UART2_BASE + 0x2c))
#define UART2_VALID    (*(volatile uint32_t *)(UART2_BASE + 0x30))
#define UART2_DATA     (*(volatile uint32_t *)(UART2_BASE + 0x34))

#define IRQARRAY5_BASE 0xE0013000u   /* bank 5 -> ext irq bit 5, uDMA UART events */
#define IRQARRAY5(reg) (*(volatile uint32_t *)(IRQARRAY5_BASE + (reg)))

#define IFRAM0_BASE    0x50000000u

#define MIE_MEIE       (1u << 11)
#define MSTATUS_MIE    (1u << 3)
#define MCAUSE_M_EXT   0x8000000bu

/* VexRiscv ExternalInterruptArrayPlugin CSRs (machine side) */
#define csr_write_bc0(v) __asm__ volatile("csrw 0xbc0, %0" :: "r"(v))
#define csr_read_fc0() ({ uint32_t v; __asm__ volatile("csrr %0, 0xfc0" : "=r"(v)); v; })

static void putc_(char c)
{
    while (DUART_SR & 1) { }
    DUART_TXD = (uint32_t)c;
}

static void puts_(const char *s)
{
    while (*s) {
        if (*s == '\n')
            putc_('\r');
        putc_(*s++);
    }
}

static void puthex(uint32_t v)
{
    puts_("0x");
    for (int i = 28; i >= 0; i -= 4)
        putc_("0123456789abcdef"[(v >> i) & 0xf]);
}

static volatile uint32_t irq_count;
static volatile uint32_t last_mcause;
static volatile uint32_t last_pending;
static volatile uint32_t last_uart_pending;

void trap_handler(void)
{
    uint32_t mcause;
    __asm__ volatile("csrr %0, mcause" : "=r"(mcause));
    last_mcause = mcause;
    last_pending = csr_read_fc0();
    irq_count++;

    /* Clear whatever peripheral asserted us. */
    if (last_pending & (1u << 0))
        IRQARRAY0(EV_PENDING) = 0xffff;
    if (last_pending & (1u << 5)) {
        last_uart_pending = IRQARRAY5(EV_PENDING);
        IRQARRAY5(EV_PENDING) = 0xffff;
    }
    if (last_pending & (1u << 30))
        TIMER0_EV_PENDING = 1;
}

static int failures;

static void check(int cond, const char *what)
{
    if (cond) {
        puts_("ok: ");
    } else {
        failures++;
        puts_("FAIL: ");
    }
    puts_(what);
    puts_("\n");
}

int main(void)
{
    DUART_CR = 1;
    puts_("\nbao1x renode smoke test\n");

    /* --- 2. irq array + custom CSRs ------------------------------------ */
    uint32_t mie = MIE_MEIE;
    __asm__ volatile("csrs mie, %0" :: "r"(mie));
    csr_write_bc0((1u << 0) | (1u << 30));    /* unmask ext-irq bits 0 and 30 */
    IRQARRAY0(EV_ENABLE) = 0xffff;
    __asm__ volatile("csrs mstatus, %0" :: "r"(MSTATUS_MIE));

    irq_count = 0;
    IRQARRAY0(EV_SOFT) = 1;                   /* software-trigger bank 0 slot 0 */
    for (volatile int i = 0; i < 1000 && irq_count == 0; i++) { }

    check(irq_count == 1, "EV_SOFT raised exactly one machine external irq");
    check(last_mcause == MCAUSE_M_EXT, "mcause is machine-external");
    check((last_pending & 1) != 0, "CSR 0xFC0 showed ext-irq bit 0 pending");
    check((csr_read_fc0() & 1) == 0, "pending cleared after EV_PENDING w1c");

    /* --- 3. TIMER0 one-shot --------------------------------------------- */
    irq_count = 0;
    TIMER0_EV_ENABLE = 1;
    TIMER0_RELOAD = 0;
    TIMER0_LOAD = 10000;                      /* 100us at 100MHz model clock */
    TIMER0_EN = 1;
    for (volatile int i = 0; i < 2000000 && irq_count == 0; i++) { }

    check(irq_count == 1, "TIMER0 one-shot fired");
    check((last_pending & (1u << 30)) != 0, "TIMER0 pending was ext-irq bit 30");

    /* --- 4. TICKTIMER monotonicity -------------------------------------- */
    uint32_t t0 = TICKTIMER_TIME0;
    for (volatile int i = 0; i < 2000000; i++) { }
    uint32_t t1 = TICKTIMER_TIME0;
    check(t1 > t0, "TICKTIMER advances");

    /* --- 5. uDMA UART2 --------------------------------------------------- */
    UDMA_CTRL_CG = (1u << 2);                 /* ungate uart2 */
    check(UDMA_CTRL_CG == (1u << 2), "UDMA_CTRL clock gate reads back");

    /* 1 Mbaud from a 100MHz perclk, 8n1, TX+RX enabled, rx irq mode */
    UART2_SETUP = (100u << 16) | (1u << 9) | (1u << 8) | (3u << 1);
    UART2_IRQ_EN = 1;                         /* rx_char event enable */
    csr_write_bc0((1u << 0) | (1u << 5) | (1u << 30));  /* + ext-irq bit 5 */
    IRQARRAY5(EV_ENABLE) = 0xffff;

    /* DMA TX: string staged in IFRAM0, like the real driver will do. */
    volatile char *tx = (volatile char *)IFRAM0_BASE;
    const char *msg = "UART2-TX-OK\r\n";
    uint32_t len = 0;
    while (msg[len]) {
        tx[len] = msg[len];
        len++;
    }
    irq_count = 0;
    last_uart_pending = 0;
    UART2_TX_SADDR = IFRAM0_BASE;
    UART2_TX_SIZE = len;
    UART2_TX_CFG = (1u << 4);                 /* kick */
    for (volatile int i = 0; i < 1000 && irq_count == 0; i++) { }

    check(UART2_TX_SIZE == 0, "TX_SIZE readback drained to 0");
    check(irq_count == 1, "TX done event raised ext-irq bit 5");
    check((last_uart_pending & (1u << 9)) != 0, "IRQARRAY5 slot 9 (uart2 tx) pending");

    /* PIO RX: the robot runner injects 'K' on uart2 after seeing the TX. */
    irq_count = 0;
    last_uart_pending = 0;
    for (volatile int i = 0; i < 20000000 && irq_count == 0; i++) { }
    check(irq_count == 1, "rx_char event raised ext-irq bit 5");
    check((last_uart_pending & (1u << 10)) != 0, "IRQARRAY5 slot 10 (uart2 rx_char) pending");
    check(UART2_VALID == 1, "VALID flags the PIO rx byte");
    check(UART2_DATA == 'K', "DATA delivers the injected byte");
    check(UART2_VALID == 0, "VALID clears after DATA read");

    if (failures == 0)
        puts_("ALL TESTS PASSED\n");
    else {
        puts_("FAILURES: ");
        puthex(failures);
        puts_("\n");
    }
    return 0;
}
