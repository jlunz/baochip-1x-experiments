/* bao1x smoke test: text in RRAM, data/bss/stack in SRAM (VMA==LMA; the
 * emulator's LoadELF writes each section where it lives, no copy loop). */
OUTPUT_ARCH(riscv)
ENTRY(_start)

MEMORY
{
    RRAM (rx)  : ORIGIN = 0x60000000, LENGTH = 4M
    SRAM (rwx) : ORIGIN = 0x61000000, LENGTH = 2M
}

SECTIONS
{
    .text : {
        KEEP(*(.text.start))
        *(.text*)
        *(.rodata*)
    } > RRAM

    .data : {
        *(.data*)
        *(.sdata*)
    } > SRAM

    .bss (NOLOAD) : {
        __bss_start = .;
        *(.bss*)
        *(.sbss*)
        *(COMMON)
        __bss_end = .;
    } > SRAM

    __stack_top = ORIGIN(SRAM) + LENGTH(SRAM);
}
