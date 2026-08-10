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

/* Stage markers on the console UART (uart2_puts). Bring-up instrumentation:
 * with the DUART unrouted on Dabao this is the only channel that reaches a
 * human, and each marker pins down how far the shim got before it stopped.
 * The first is deliberately emitted *before* uart2_init(), reusing the
 * configuration boot1 leaves behind -- proven working, since boot1 printed
 * through it moments ago.
 */
#define mark(s) uart2_puts(s)

void main(void)
{
    mark("SBI:entry\r\n");
    duart_puts("\nbao1x-sbi: M-mode SBI shim\n");
    mark("SBI:duart-ok\r\n");
    uart2_init();
    mark("SBI:uart-init\r\n");

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
    mark("SBI:dtb-copied\r\n");

    /* Give traps a usable stack *before* touching any CSR. A CSR this silicon
     * does not implement traps as an illegal instruction, and with mscratch
     * still 0 (entry.S's "in M-mode" marker) trap_entry would hand the handler
     * sp = 0 and die on its first store -- the shim would simply vanish. With
     * this set, trap_handler can report the offending mepc instead. */
    csr_write(mscratch, (uint32_t)__mstack_top);

    /* Written one at a time with markers: any of these can be absent on a
     * given VexRiscv configuration, and Renode implements them all, so
     * emulation cannot tell us which. */
    csr_write(medeleg, MEDELEG_MASK);      mark("SBI:medeleg\r\n");
    csr_write(mideleg, MIDELEG_MASK);      mark("SBI:mideleg\r\n");
    csr_write(mie, MIE_MEIE);              mark("SBI:mie\r\n");
    __asm__ volatile("csrw 0xbc0, zero");  mark("SBI:extmask\r\n");

    /* Fresh S-state for the kernel. */
    csr_write(satp, 0);                    mark("SBI:satp\r\n");
    csr_write(stvec, 0);                   mark("SBI:stvec\r\n");
    csr_write(sscratch, 0);                mark("SBI:sscratch\r\n");
    csr_write(sie, 0);                     mark("SBI:sie\r\n");
    /* cycle/instret visible to S and U (no time CSR to enable).
     *
     * These two are probed rather than written outright: the Dabao's VexRiscv
     * does not implement them and traps (measured: mcause=2,
     * mtval=0x3063d073 = `csrwi mcounteren, 7`), while Renode does implement
     * them — and there the kernel *needs* the write, or its S-mode counter
     * reads trap and the boot wedges. Neither "always write" nor "never
     * write" works on both, so attempt it and accept a trap as "absent". */
    if (csr_write_probe(mcounteren, 0x7))
        mark("SBI:mcounteren\r\n");
    else
        mark("SBI:mcounteren-absent\r\n");
    if (csr_write_probe(scounteren, 0x7))
        mark("SBI:scounteren\r\n");
    else
        mark("SBI:scounteren-absent\r\n");

    mark("SBI:csr-done\r\n");
    duart_puts("bao1x-sbi: entering kernel at ");
    duart_puthex(KERNEL_ENTRY);
    duart_puts("\n");
    /* Same sign of life on the console UART: the DUART pad may not be
     * routed anywhere reachable on a given board. */
    static const char banner[] = "bao1x-sbi: jumping to kernel\r\n";
    uart2_tx((const uint8_t *)banner, sizeof(banner) - 1);

    /* mscratch already holds the M-stack (set before the CSR block above). */
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
