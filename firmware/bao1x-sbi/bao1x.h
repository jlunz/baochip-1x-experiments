/*
 * bao1x-sbi: hardware definitions.
 * See docs/01-hardware-dossier.md for sources (RTL + xous-core SVDs).
 *
 * SPDX-License-Identifier: Apache-2.0
 */
#ifndef BAO1X_H
#define BAO1X_H

#include <stdint.h>

/* --- Board clocking ------------------------------------------------------
 * rdtime is emulated from mcycle, so the DT timebase-frequency equals the
 * CPU clock. TIMER0 (the sbi_set_timer backend) counts fclk, which is 2x
 * the CPU clock on silicon (boot1 leaves the Dabao at fclk=700MHz, CPU
 * 350MHz, perclk ~99.8MHz). Renode pins everything to 100MHz.
 */
#if defined(BOARD_dabao)
#define TIMER0_TICKS_MULT   2u      /* TIMER0 ticks per timebase tick */
#define UART2_CLKDIV        100u    /* ~99.8MHz perclk / 1Mbaud */
#define CPU_HZ              350000000u
#elif defined(BOARD_renode)
#define TIMER0_TICKS_MULT   1u
#define UART2_CLKDIV        100u    /* 100MHz model clock / 1Mbaud */
#define CPU_HZ              100000000u
#else
#error "build with BOARD=renode or BOARD=dabao"
#endif

/* SHIM_ONLY heartbeat period, in mcycle ticks (~1s). */
#define HEARTBEAT_CYCLES    ((uint64_t)CPU_HZ)

#define MMIO32(a)       (*(volatile uint32_t *)(a))

/* --- Memory map ---------------------------------------------------------- */
#define RRAM_BASE       0x60000000u
#define SRAM_BASE       0x61000000u
#define SRAM_SIZE       0x00200000u
/* Top 16KiB of SRAM is the shim's workspace (data/bss/M-stack); the DT
 * memory node ends below it. The DTB is copied just underneath, inside
 * kernel memory (the kernel memblock-reserves it). */
#define SHIM_RAM_SIZE   0x4000u
#define SHIM_RAM_BASE   (SRAM_BASE + SRAM_SIZE - SHIM_RAM_SIZE)   /* 0x611FC000 */
#define DTB_MAX_SIZE    0x4000u
#define DTB_DEST        (SHIM_RAM_BASE - DTB_MAX_SIZE)            /* 0x611F8000 */

#define KERNEL_ENTRY    0x60070000u

/* --- DUART: TX-only debug UART (shim diagnostics only) -------------------
 * The pad is not routed anywhere reachable on Dabao, and nothing guarantees
 * SFR_ETUC (the baud divider, reset value 0) has been programmed by the time
 * boot1 hands over. A zero divider means SFR_SR never clears, so every wait
 * on this peripheral must be bounded -- see SPIN_LIMIT. Losing debug output
 * is acceptable; wedging the boot on a debug pad is not.
 */
#define DUART_TXD       MMIO32(0x40042000u + 0x0)
#define DUART_CR        MMIO32(0x40042000u + 0x4)
#define DUART_SR        MMIO32(0x40042000u + 0x8)
#define DUART_ETUC      MMIO32(0x40042000u + 0xc)

/* Upper bound on any hardware poll loop. ~2M iterations is several ms even at
 * 350MHz -- orders of magnitude beyond a 1Mbaud character time -- so a healthy
 * peripheral never reaches it, and a wedged one cannot hang the boot. */
#define SPIN_LIMIT      2000000u

/* --- IOX (pin mux + GPIO) ------------------------------------------------
 * Per-port 16-bit words, port stride 4 bytes, PA=0 .. PF=5. The offsets are
 * cross-checked three ways: utralib's generated bao1x register file, the
 * vendor HAL (bao1x-hal src/iox.rs), and this port's own pinctrl driver
 * (linux/patches/v6.14/0015). AFSEL packs eight pins per word, hence the
 * two-level index.
 */
#define IOX_BASE        0x5012F000u
#define IOX_PORT_PC     2u
#define IOX_AFSEL(pin)  MMIO32(IOX_BASE + 0x000u + ((pin) / 16u) * 8u \
                                                 + (((pin) % 16u) / 8u) * 4u)
#define IOX_AFSEL_SHIFT(pin) (((pin) % 8u) * 2u)
#define IOX_AFSEL_GPIO  0u
#define IOX_OUT(port)   MMIO32(IOX_BASE + 0x130u + (port) * 4u)
#define IOX_OE(port)    MMIO32(IOX_BASE + 0x148u + (port) * 4u)
#define IOX_PU(port)    MMIO32(IOX_BASE + 0x160u + (port) * 4u)
#define IOX_SCHM(port)  MMIO32(IOX_BASE + 0x230u + (port) * 4u)

/* Dabao wires PC13 to the SE0 control of the EMS4000 USB switch, dual-purposed
 * as the "boot update" button input. boot1's boot() drives it low (USB forced
 * into SE0) and hands over expecting the next stage's USB stack to release it
 * -- README-baochip: "it is up to the next USB stack to de-assert this". This
 * port has no USB gadget yet, so the shim releases it itself; otherwise every
 * boot leaves the port disconnected and only a physical replug restores it.
 */
#define SE0_PORT        IOX_PORT_PC
#define SE0_PIN         13u
#define SE0_PIN_INDEX   (SE0_PORT * 16u + SE0_PIN)

/* --- uDMA ----------------------------------------------------------------- */
#define UDMA_CTRL_CG    MMIO32(0x50100000u + 0x0)
#define UDMA_CG_UART2   (1u << 2)
#define UDMA_CG_I2C0    (1u << 8)
#define UDMA_CG_SPIM0   (1u << 4)

#define UART2_BASE      0x50103000u
#define UART2_TX_SADDR  MMIO32(UART2_BASE + 0x10)
#define UART2_TX_SIZE   MMIO32(UART2_BASE + 0x14)
#define UART2_TX_CFG    MMIO32(UART2_BASE + 0x18)
#define UART2_STATUS    MMIO32(UART2_BASE + 0x20)
#define UART2_SETUP     MMIO32(UART2_BASE + 0x24)
#define UART2_IRQ_EN    MMIO32(UART2_BASE + 0x2c)
#define UART2_VALID     MMIO32(UART2_BASE + 0x30)
#define UART2_DATA      MMIO32(UART2_BASE + 0x34)

#define UART_SETUP_PARITY   (1u << 0)
#define UART_SETUP_8BIT     (3u << 1)
#define UART_SETUP_RXPOLL   (1u << 4)
#define UART_SETUP_TXEN     (1u << 8)
#define UART_SETUP_RXEN     (1u << 9)
#define UART_SETUP_DIV(d)   ((uint32_t)(d) << 16)

#define UART_CFG_EN         (1u << 4)
/* boot1 enqueues every uDMA transfer with the backpressure bit set
 * (bao1x-hal udma_enqueue: CFG_EN | CFG_BACKPRESSURE). That is the only
 * uDMA TX sequence proven on silicon, so match it exactly. */
#define UART_CFG_BACKPRESSURE (1u << 7)

/* TX bounce buffer: tail of IFRAM0 (uDMA can only read IFRAM on silicon).
 * The kernel's future uart driver allocates from the IFRAM0 head; the shim
 * only transmits via DBCN, which stops mattering once a real console runs. */
#define TX_BOUNCE       0x5001FF00u
#define TX_BOUNCE_LEN   0x100u

/* --- TIMER0 (LiteX 32-bit down-counter), ext-irq array line 30 ----------- */
#define TIMER0_BASE     0xE001C000u
#define TIMER0_LOAD     MMIO32(TIMER0_BASE + 0x00)
#define TIMER0_RELOAD   MMIO32(TIMER0_BASE + 0x04)
#define TIMER0_EN       MMIO32(TIMER0_BASE + 0x08)
#define TIMER0_EV_PENDING MMIO32(TIMER0_BASE + 0x18)
#define TIMER0_EV_ENABLE  MMIO32(TIMER0_BASE + 0x1c)
#define TIMER0_IRQ_LINE 30

/* --- VexRiscv external interrupt array CSRs ------------------------------ */
#define CSR_MMASK       0xBC0   /* machine mask */
#define CSR_MPENDING    0xFC0   /* machine pending (read-only) */

/* --- Standard CSR bits ---------------------------------------------------- */
#define MSTATUS_MPP_S   (1u << 11)
#define MSTATUS_MPIE    (1u << 7)
#define MIP_STIP        (1u << 5)
#define MIE_MEIE        (1u << 11)

#define csr_read(csr) ({ uint32_t v; \
    __asm__ volatile("csrr %0, " #csr : "=r"(v)); v; })
#define csr_write(csr, v) \
    __asm__ volatile("csrw " #csr ", %0" :: "rK"((uint32_t)(v)))
#define csr_set(csr, v) \
    __asm__ volatile("csrs " #csr ", %0" :: "rK"((uint32_t)(v)))
#define csr_clear(csr, v) \
    __asm__ volatile("csrc " #csr ", %0" :: "rK"((uint32_t)(v)))

/* console.c */
void duart_puts(const char *s);
void duart_puthex(uint32_t v);
void uart2_init(void);
void uart2_tx(const uint8_t *buf, uint32_t len);
void uart2_puts(const char *s);
void uart2_puthex(uint32_t v);
int uart2_rx_byte(void);

/* board.c */
void se0_release(void);

/* trap.c */
void trap_handler(uint32_t *frame);
uint64_t read_mcycle64(void);
extern volatile int m_probe_active;
extern volatile int m_probe_faulted;

/* Write a CSR that may not be implemented on this core. Evaluates to 1 if the
 * write took effect, 0 if it trapped as an illegal instruction. Requires
 * mscratch to already hold the M-stack, so traps are survivable. */
#define csr_write_probe(csr, v) ({      \
    m_probe_faulted = 0;                \
    m_probe_active = 1;                 \
    csr_write(csr, v);                  \
    m_probe_active = 0;                 \
    !m_probe_faulted;                   \
})

#endif
