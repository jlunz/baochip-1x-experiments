/*
 * bao1x-sbi: M-mode trap handling — the SBI surface Linux talks to.
 *
 * The chip has no time CSR, no CLINT and no PLIC (MTIP/MSIP are tied to 0
 * in silicon), so:
 *   - rdtime/rdtimeh trap here as illegal instructions and are emulated
 *     from the 64-bit mcycle counter (timebase-frequency in the DT must
 *     equal the core clock).
 *   - sbi_set_timer arms TIMER0 (LiteX down-counter, machine ext-irq
 *     line 30) and the resulting M-external interrupt sets mip.STIP,
 *     which mideleg hands to the kernel.
 *   - console = SBI DBCN (+ legacy putchar/getchar) on uDMA UART2.
 *
 * SPDX-License-Identifier: Apache-2.0
 */
#include "bao1x.h"

/* Frame slot for register xN (N >= 1); frame[1] holds the trapped sp. */
#define REG(frame, n) ((frame)[(n) - 1])

#define CAUSE_ILLEGAL       2
#define CAUSE_ECALL_S       9
#define CAUSE_M_EXT         0x8000000bu

#define SBI_SUCCESS         0
#define SBI_ERR_NOT_SUPPORTED (-2)

#define SBI_EXT_BASE        0x10
#define SBI_EXT_TIME        0x54494D45
#define SBI_EXT_IPI         0x00735049
#define SBI_EXT_RFENCE      0x52464E43
#define SBI_EXT_HSM         0x48534D
#define SBI_EXT_SRST        0x53525354
#define SBI_EXT_DBCN        0x4442434E
#define SBI_LEGACY_SET_TIMER 0x00
#define SBI_LEGACY_PUTCHAR  0x01
#define SBI_LEGACY_GETCHAR  0x02

uint64_t read_mcycle64(void)
{
    uint32_t hi, lo, hi2;
    do {
        __asm__ volatile("csrr %0, mcycleh" : "=r"(hi));
        __asm__ volatile("csrr %0, mcycle"  : "=r"(lo));
        __asm__ volatile("csrr %0, mcycleh" : "=r"(hi2));
    } while (hi != hi2);
    return ((uint64_t)hi << 32) | lo;
}

static void mask_timer_line(void)
{
    uint32_t m;
    __asm__ volatile("csrr %0, 0xbc0" : "=r"(m));
    m &= ~(1u << TIMER0_IRQ_LINE);
    __asm__ volatile("csrw 0xbc0, %0" :: "r"(m));
}

static void unmask_timer_line(void)
{
    uint32_t m;
    __asm__ volatile("csrr %0, 0xbc0" : "=r"(m));
    m |= 1u << TIMER0_IRQ_LINE;
    __asm__ volatile("csrw 0xbc0, %0" :: "r"(m));
}

static void set_timer(uint64_t target)
{
    uint64_t now = read_mcycle64();

    csr_clear(mip, MIP_STIP);
    TIMER0_EN = 0;
    TIMER0_EV_PENDING = 1;

    if (target <= now) {
        csr_set(mip, MIP_STIP);
        return;
    }
    uint64_t delta = (target - now) * TIMER0_TICKS_MULT;
    if (delta > 0xffffffffu)
        delta = 0xffffffffu;
    TIMER0_RELOAD = 0;
    TIMER0_LOAD = (uint32_t)delta;
    TIMER0_EV_ENABLE = 1;
    TIMER0_EN = 1;
    unmask_timer_line();
}

static void handle_m_external(void)
{
    uint32_t pending;
    __asm__ volatile("csrr %0, 0xfc0" : "=r"(pending));

    if (pending & (1u << TIMER0_IRQ_LINE)) {
        TIMER0_EN = 0;
        TIMER0_EV_PENDING = 1;
        mask_timer_line();
        csr_set(mip, MIP_STIP);
        return;
    }
    /* Only the timer line is ever unmasked on the machine side. */
    duart_puts("bao1x-sbi: unexpected M-ext pending ");
    duart_puthex(pending);
    duart_puts("\n");
    __asm__ volatile("csrw 0xbc0, zero");
}

/* Load a 16-bit unit from an S-mode virtual address by borrowing the
 * S-mode translation via mstatus.MPRV (MPP is still S from the trap). */
static uint32_t load_s_u16(uint32_t va)
{
    uint32_t v;
    __asm__ volatile(
        "li   t0, %2\n"
        "csrs mstatus, t0\n"
        "lhu  %0, 0(%1)\n"
        "csrc mstatus, t0\n"
        : "=r"(v) : "r"(va), "i"(1 << 17) : "t0");
    return v;
}

static void forward_to_s(uint32_t *frame, uint32_t cause, uint32_t tval)
{
    (void)frame;
    uint32_t mepc = csr_read(mepc);
    uint32_t mstatus = csr_read(mstatus);
    uint32_t stvec = csr_read(stvec);
    uint32_t old_sstatus = csr_read(sstatus);

    csr_write(scause, cause);
    csr_write(stval, tval);
    csr_write(sepc, mepc);

    /* sstatus.SPP <- previous privilege, SPIE <- old SIE, SIE <- 0 */
    uint32_t new_sstatus = old_sstatus & ~((1u << 8) | (1u << 5) | (1u << 1));
    if (mstatus & MSTATUS_MPP_S)
        new_sstatus |= 1u << 8;
    if (old_sstatus & (1u << 1))
        new_sstatus |= 1u << 5;
    csr_write(sstatus, new_sstatus);

    csr_write(mepc, stvec & ~3u);
    /* Re-enter in S-mode regardless of where the trap came from. */
    uint32_t ms = csr_read(mstatus);
    ms &= ~(3u << 11);
    ms |= MSTATUS_MPP_S;
    csr_write(mstatus, ms);
}

static void handle_illegal(uint32_t *frame)
{
    uint32_t insn = csr_read(mtval);
    uint32_t mepc = csr_read(mepc);

    if (insn == 0) {
        insn = load_s_u16(mepc);
        if ((insn & 3) == 3)
            insn |= load_s_u16(mepc + 2) << 16;
    }

    /* csrr rd, time / timeh  (csrrs rd, 0xc01/0xc81, x0) */
    if ((insn & 0xfff07fffu) == 0xc0102073u ||
        (insn & 0xfff07fffu) == 0xc8102073u) {
        uint32_t rd = (insn >> 7) & 0x1f;
        uint64_t t = read_mcycle64();
        uint32_t v = (insn & 0x08000000u) ? (uint32_t)(t >> 32) : (uint32_t)t;
        if (rd != 0)
            REG(frame, rd) = v;
        csr_write(mepc, mepc + 4);
        return;
    }

    forward_to_s(frame, CAUSE_ILLEGAL, insn);
}

static void sbi_dispatch(uint32_t *frame)
{
    uint32_t eid = REG(frame, 17);          /* a7 */
    uint32_t fid = REG(frame, 16);          /* a6 */
    uint32_t a0 = REG(frame, 10);
    uint32_t a1 = REG(frame, 11);
    uint32_t a2 = REG(frame, 12);
    int32_t err = SBI_SUCCESS;
    uint32_t val = 0;

    switch (eid) {
    case SBI_EXT_BASE:
        switch (fid) {
        case 0: val = 0x02000000; break;         /* spec v2.0 */
        case 1: val = 0xba0; break;              /* impl id (unregistered) */
        case 2: val = 1; break;                  /* impl version */
        case 3:                                  /* probe_extension */
            val = (a0 == SBI_EXT_TIME || a0 == SBI_EXT_DBCN ||
                   a0 == SBI_EXT_SRST || a0 == SBI_EXT_BASE) ? 1 : 0;
            break;
        case 4: case 5: case 6: val = 0; break;  /* mvendorid/marchid/mimpid */
        default: err = SBI_ERR_NOT_SUPPORTED;
        }
        break;

    case SBI_EXT_TIME:
        if (fid == 0)
            set_timer(((uint64_t)a1 << 32) | a0);
        else
            err = SBI_ERR_NOT_SUPPORTED;
        break;

    case SBI_LEGACY_SET_TIMER:
        set_timer(((uint64_t)a1 << 32) | a0);
        break;

    case SBI_EXT_DBCN:
        switch (fid) {
        case 0:                                  /* console_write */
            uart2_tx((const uint8_t *)a1, a0);   /* a2:a1 = 64-bit PA */
            val = a0;
            break;
        case 1: {                                /* console_read */
            uint8_t *dst = (uint8_t *)a1;
            uint32_t n = 0;
            int c;
            while (n < a0 && (c = uart2_rx_byte()) >= 0)
                dst[n++] = (uint8_t)c;
            val = n;
            break;
        }
        case 2: {                                /* console_write_byte */
            uint8_t b = (uint8_t)a0;
            uart2_tx(&b, 1);
            break;
        }
        default: err = SBI_ERR_NOT_SUPPORTED;
        }
        break;

    case SBI_LEGACY_PUTCHAR: {
        uint8_t b = (uint8_t)a0;
        uart2_tx(&b, 1);
        break;
    }
    case SBI_LEGACY_GETCHAR:
        /* Legacy calls return the value in a0. */
        REG(frame, 10) = (uint32_t)uart2_rx_byte();
        csr_write(mepc, csr_read(mepc) + 4);
        return;

    case SBI_EXT_SRST:
        duart_puts("bao1x-sbi: system reset requested; parking\n");
        for (;;)
            __asm__ volatile("wfi");

    case SBI_EXT_IPI:
    case SBI_EXT_RFENCE:
    case SBI_EXT_HSM:
    default:
        err = SBI_ERR_NOT_SUPPORTED;
    }

    REG(frame, 10) = (uint32_t)err;
    REG(frame, 11) = val;
    csr_write(mepc, csr_read(mepc) + 4);

    (void)a2;
}

void trap_handler(uint32_t *frame)
{
    uint32_t cause = csr_read(mcause);

    switch (cause) {
    case CAUSE_ECALL_S:
        sbi_dispatch(frame);
        return;
    case CAUSE_ILLEGAL:
        handle_illegal(frame);
        return;
    case CAUSE_M_EXT:
        handle_m_external();
        return;
    default:
        duart_puts("bao1x-sbi: unhandled trap mcause=");
        duart_puthex(cause);
        duart_puts(" mepc=");
        duart_puthex(csr_read(mepc));
        duart_puts(" mtval=");
        duart_puthex(csr_read(mtval));
        duart_puts("\n");
        for (;;)
            __asm__ volatile("wfi");
    }
}
