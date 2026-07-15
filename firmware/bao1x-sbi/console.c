/*
 * bao1x-sbi: console back-ends.
 *   DUART  — TX-only debug UART: shim banner/diagnostics.
 *   UART2  — the Dabao console (PB13/PB14 @ 1 Mbaud): SBI DBCN + legacy
 *            console. TX is DMA-only on this IP, bounced through the tail
 *            of IFRAM0; RX uses the PIO VALID/DATA path in polling mode.
 *
 * SPDX-License-Identifier: Apache-2.0
 */
#include "bao1x.h"

void duart_puts(const char *s)
{
    DUART_CR = 1;
    while (*s) {
        if (*s == '\n') {
            while (DUART_SR & 1) { }
            DUART_TXD = '\r';
        }
        while (DUART_SR & 1) { }
        DUART_TXD = (uint32_t)*s++;
    }
}

void duart_puthex(uint32_t v)
{
    char buf[11];
    buf[0] = '0';
    buf[1] = 'x';
    for (int i = 0; i < 8; i++)
        buf[2 + i] = "0123456789abcdef"[(v >> (28 - 4 * i)) & 0xf];
    buf[10] = '\0';
    duart_puts(buf);
}

void uart2_init(void)
{
    UDMA_CTRL_CG |= UDMA_CG_UART2;
    /* 8n1, TX+RX enabled, PIO RX (polled — the kernel hvc console polls
     * through SBI; no events, IRQARRAY5 stays quiet for Linux to own). */
    UART2_IRQ_EN = 0;
    UART2_SETUP = UART_SETUP_DIV(100) | UART_SETUP_RXEN | UART_SETUP_TXEN |
                  UART_SETUP_RXPOLL | UART_SETUP_8BIT;
}

void uart2_tx(const uint8_t *buf, uint32_t len)
{
    while (len) {
        uint32_t chunk = len < TX_BOUNCE_LEN ? len : TX_BOUNCE_LEN;
        volatile uint8_t *dst = (volatile uint8_t *)TX_BOUNCE;
        for (uint32_t i = 0; i < chunk; i++)
            dst[i] = buf[i];
        UART2_TX_SADDR = TX_BOUNCE;
        UART2_TX_SIZE = chunk;
        UART2_TX_CFG = UART_CFG_EN;
        while (UART2_TX_SIZE != 0 || (UART2_STATUS & 1)) { }
        buf += chunk;
        len -= chunk;
    }
}

int uart2_rx_byte(void)
{
    if (!(UART2_VALID & 1))
        return -1;
    return (int)(UART2_DATA & 0xff);
}
