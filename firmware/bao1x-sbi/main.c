/*
 * bao1x-sbi: boot path. Runs once in M-mode, sets up delegation and the
 * S-mode world, then mret's into the XIP kernel at 0x60070000.
 *
 * SPDX-License-Identifier: Apache-2.0
 */
#include "bao1x.h"

extern const uint8_t _dtb_start[], _dtb_end[];
extern uint8_t __mstack_top[];

/* Exceptions delegated to S-mode. Kept in M: ecall-from-S (SBI) and
 * illegal instruction (rdtime emulation; others are forwarded by hand). */
#define MEDELEG_MASK ( \
    (1u << 0)  /* insn addr misaligned */ | \
    (1u << 1)  /* insn access fault    */ | \
    (1u << 3)  /* breakpoint           */ | \
    (1u << 4)  /* load addr misaligned */ | \
    (1u << 5)  /* load access fault    */ | \
    (1u << 6)  /* store addr misaligned*/ | \
    (1u << 7)  /* store access fault   */ | \
    (1u << 8)  /* ecall from U         */ | \
    (1u << 12) /* insn page fault      */ | \
    (1u << 13) /* load page fault      */ | \
    (1u << 15) /* store page fault     */ )

/* Interrupts delegated to S-mode: SSIP, STIP, SEIP. */
#define MIDELEG_MASK ((1u << 1) | (1u << 5) | (1u << 9))

void main(void)
{
    duart_puts("\nbao1x-sbi: M-mode SBI shim\n");
    uart2_init();

    /* DTB: RRAM -> top of kernel RAM (the kernel memblock-reserves it;
     * XIP setup_vm cannot read a DTB that is outside RAM). */
    uint32_t dtb_size = (uint32_t)(_dtb_end - _dtb_start);
    if (dtb_size > DTB_MAX_SIZE) {
        duart_puts("bao1x-sbi: DTB too large\n");
        for (;;)
            __asm__ volatile("wfi");
    }
    const uint32_t *src = (const uint32_t *)_dtb_start;
    uint32_t *dst = (uint32_t *)DTB_DEST;
    for (uint32_t i = 0; i < (dtb_size + 3) / 4; i++)
        dst[i] = src[i];

    csr_write(medeleg, MEDELEG_MASK);
    csr_write(mideleg, MIDELEG_MASK);
    csr_write(mie, MIE_MEIE);
    __asm__ volatile("csrw 0xbc0, zero");   /* mask the whole M ext array */

    /* Fresh S-state for the kernel. */
    csr_write(satp, 0);
    csr_write(stvec, 0);
    csr_write(sscratch, 0);
    csr_write(sie, 0);
    /* cycle/instret visible to S and U (no time CSR to enable). */
    csr_write(mcounteren, 0x7);
    csr_write(scounteren, 0x7);

    duart_puts("bao1x-sbi: entering kernel at ");
    duart_puthex(KERNEL_ENTRY);
    duart_puts("\n");
    /* Same sign of life on the console UART: the DUART pad may not be
     * routed anywhere reachable on a given board. */
    static const char banner[] = "bao1x-sbi: jumping to kernel\r\n";
    uart2_tx((const uint8_t *)banner, sizeof(banner) - 1);

    /* From here on the shim only runs via mtvec; give traps the M-stack. */
    csr_write(mscratch, (uint32_t)__mstack_top);

    uint32_t ms = csr_read(mstatus);
    ms &= ~((3u << 11) | MSTATUS_MPIE);     /* MPP=0, MPIE=0 */
    ms |= MSTATUS_MPP_S;                    /* MPP=S */
    csr_write(mstatus, ms);
    csr_write(mepc, KERNEL_ENTRY);

    register uint32_t a0 __asm__("a0") = 0;             /* hartid */
    register uint32_t a1 __asm__("a1") = DTB_DEST;      /* dtb PA */
    __asm__ volatile("fence.i\n\tmret" :: "r"(a0), "r"(a1));
    __builtin_unreachable();
}
