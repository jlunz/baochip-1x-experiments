/*
 * bao1x-sbi: M-mode entry and trap vector.
 *
 * boot1 enters at _start in M-mode with nothing set up (plain `jr` from
 * sigcheck). The shim runs forever as the machine-mode SBI backend: after
 * mret to the kernel it only ever runs again through mtvec.
 *
 * Trap frame layout (allocated on the M-stack): 31 words, x1..x31 at
 * offset (n-1)*4. x2 (sp) is the pre-trap value recovered from mscratch.
 *
 * SPDX-License-Identifier: Apache-2.0
 */

#define FRAME_SIZE (31 * 4)

    .section .text.entry, "ax"
    .globl _start
_start:
    /* No relocations to worry about: linked at the XIP address. */
    la      sp, __mstack_top
    csrw    mscratch, zero          /* 0 = "currently in M-mode" convention */

    /* .data/.bss live in SRAM; load .data image from RRAM, clear .bss. */
    la      t0, __data_lma
    la      t1, __data_start
    la      t2, __data_end
1:  bgeu    t1, t2, 2f
    lw      t3, 0(t0)
    sw      t3, 0(t1)
    addi    t0, t0, 4
    addi    t1, t1, 4
    j       1b
2:  la      t1, __bss_start
    la      t2, __bss_end
3:  bgeu    t1, t2, 4f
    sw      zero, 0(t1)
    addi    t1, t1, 4
    j       3b
4:
    la      t0, trap_entry
    csrw    mtvec, t0
    call    main                    /* never returns */
5:  wfi
    j       5b

    .align 2
trap_entry:
    /* Swap sp with mscratch. Traps only arrive from S/U mode (M-mode shim
     * code never faults by construction), so mscratch always holds the
     * M-stack pointer here. */
    csrrw   sp, mscratch, sp
    addi    sp, sp, -FRAME_SIZE
    sw      x1,   0(sp)
    /* x2: the trapped sp currently sits in mscratch; store it below. */
    sw      x3,   8(sp)
    sw      x4,  12(sp)
    sw      x5,  16(sp)
    sw      x6,  20(sp)
    sw      x7,  24(sp)
    sw      x8,  28(sp)
    sw      x9,  32(sp)
    sw      x10, 36(sp)
    sw      x11, 40(sp)
    sw      x12, 44(sp)
    sw      x13, 48(sp)
    sw      x14, 52(sp)
    sw      x15, 56(sp)
    sw      x16, 60(sp)
    sw      x17, 64(sp)
    sw      x18, 68(sp)
    sw      x19, 72(sp)
    sw      x20, 76(sp)
    sw      x21, 80(sp)
    sw      x22, 84(sp)
    sw      x23, 88(sp)
    sw      x24, 92(sp)
    sw      x25, 96(sp)
    sw      x26, 100(sp)
    sw      x27, 104(sp)
    sw      x28, 108(sp)
    sw      x29, 112(sp)
    sw      x30, 116(sp)
    sw      x31, 120(sp)
    csrr    t0, mscratch
    sw      t0,   4(sp)             /* trapped sp */

    mv      a0, sp
    call    trap_handler

    lw      t0,   4(sp)
    csrw    mscratch, t0            /* will be swapped back below */
    lw      x1,   0(sp)
    lw      x3,   8(sp)
    lw      x4,  12(sp)
    lw      x5,  16(sp)
    lw      x6,  20(sp)
    lw      x7,  24(sp)
    lw      x8,  28(sp)
    lw      x9,  32(sp)
    lw      x10, 36(sp)
    lw      x11, 40(sp)
    lw      x12, 44(sp)
    lw      x13, 48(sp)
    lw      x14, 52(sp)
    lw      x15, 56(sp)
    lw      x16, 60(sp)
    lw      x17, 64(sp)
    lw      x18, 68(sp)
    lw      x19, 72(sp)
    lw      x20, 76(sp)
    lw      x21, 80(sp)
    lw      x22, 84(sp)
    lw      x23, 88(sp)
    lw      x24, 92(sp)
    lw      x25, 96(sp)
    lw      x26, 100(sp)
    lw      x27, 104(sp)
    lw      x28, 108(sp)
    lw      x29, 112(sp)
    lw      x30, 116(sp)
    lw      x31, 120(sp)
    addi    sp, sp, FRAME_SIZE
    csrrw   sp, mscratch, sp        /* restore trapped sp; mscratch = M-stack */
    mret

    /* Embedded device tree, copied to DTB_DEST before mret. */
    .section .rodata.dtb, "a"
    .globl _dtb_start, _dtb_end
    .align 3
_dtb_start:
    .incbin DTB_PATH
_dtb_end:
