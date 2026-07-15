/*
 * bao1x-sbi linker script.
 * Text/rodata/DTB execute in place from RRAM. The payload slot starts at
 * 0x60060000 with the 768-byte boot1 signature block, whose first word is
 * a `jal x0, +768` — so code starts at 0x60060300 (the same address is
 * used in Renode, which just loads the ELF and starts at its entry).
 * Data/bss/stack live in the top 16KiB of SRAM, which the DT memory node
 * excludes.
 *
 * SPDX-License-Identifier: Apache-2.0
 */
OUTPUT_ARCH(riscv)
ENTRY(_start)

MEMORY
{
    ROM (rx)  : ORIGIN = 0x60060300, LENGTH = 0xFD00
    RAM (rwx) : ORIGIN = 0x611FC000, LENGTH = 0x4000
}

SECTIONS
{
    .text : {
        KEEP(*(.text.entry))
        *(.text .text.*)
    } > ROM

    .rodata : {
        *(.rodata .rodata.*)
        *(.srodata .srodata.*)
        . = ALIGN(8);
        *(.rodata.dtb)
    } > ROM

    .data : ALIGN(4) {
        __data_start = .;
        *(.data .data.*)
        *(.sdata .sdata.*)
        . = ALIGN(4);
        __data_end = .;
    } > RAM AT > ROM
    __data_lma = LOADADDR(.data);

    .bss : ALIGN(4) {
        __bss_start = .;
        *(.bss .bss.*)
        *(.sbss .sbss.*)
        *(COMMON)
        . = ALIGN(4);
        __bss_end = .;
    } > RAM

    /* Everything left in RAM is M-stack. */
    __mstack_top = ORIGIN(RAM) + LENGTH(RAM);

    /DISCARD/ : { *(.eh_frame .riscv.attributes .comment) }
}
