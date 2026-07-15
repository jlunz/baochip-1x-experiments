*** Settings ***
Documentation     bao1x Renode platform smoke test: DUART, custom irq CSRs,
...               IRQARRAY EV_SOFT, TIMER0 clockevent path, TICKTIMER,
...               uDMA UART2 (DMA TX from IFRAM + rx_char irq + PIO RX).
Suite Setup       Setup
Suite Teardown    Teardown
Test Teardown     Test Teardown
Resource          ${RENODEKEYWORDS}

*** Variables ***
${SMOKE_ELF}      ${CURDIR}/../../../build/renode-smoke/smoke.elf

*** Test Cases ***
Baremetal Smoke Test Should Pass
    Execute Command    $bin=@${SMOKE_ELF}
    Execute Command    include @${CURDIR}/../bao1x.resc
    ${duart}=          Create Terminal Tester    sysbus.duart
    ${uart2}=          Create Terminal Tester    sysbus.uart2
    Start Emulation
    # Test 5 first half: the DMA TX message arrives on uart2 ...
    Wait For Line On Uart    UART2-TX-OK    timeout=20    testerId=${uart2}
    # ... then the test blocks until we inject the PIO RX byte.
    Send Key To Uart    0x4B    testerId=${uart2}
    Wait For Line On Uart    ALL TESTS PASSED    timeout=20    testerId=${duart}
    Should Not Be On Uart    FAIL                timeout=1     testerId=${duart}
