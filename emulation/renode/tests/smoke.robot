*** Settings ***
Documentation     bao1x Renode platform smoke test: DUART, custom irq CSRs,
...               IRQARRAY EV_SOFT, TIMER0 clockevent path, TICKTIMER.
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
    Create Terminal Tester    sysbus.duart
    Start Emulation
    Wait For Line On Uart    ALL TESTS PASSED    timeout=20
    Should Not Be On Uart    FAIL                timeout=1
