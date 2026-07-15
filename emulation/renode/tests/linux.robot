*** Settings ***
Documentation     Phase 3: Linux boots to an interactive busybox shell on the
...               bao1x Renode platform (bao1x-sbi shim + XIP kernel + XIP
...               cramfs rootfs; console = SBI DBCN hvc on uDMA UART2).
Suite Setup       Setup
Suite Teardown    Teardown
Test Teardown     Test Teardown
Resource          ${RENODEKEYWORDS}

*** Test Cases ***
Linux Should Boot To Shell
    Execute Command    include @${CURDIR}/../bao1x-linux.resc
    ${duart}=          Create Terminal Tester    sysbus.duart
    ${uart2}=          Create Terminal Tester    sysbus.uart2
    Start Emulation
    # Shim banner and kernel earlycon (bao1x_duart) on the debug UART...
    Wait For Line On Uart    bao1x-sbi: entering kernel    timeout=10   testerId=${duart}
    Wait For Line On Uart    Linux version                 timeout=30   testerId=${duart}
    # ...then the native uDMA UART console (ttyBAO0) takes over on uart2.
    Wait For Line On Uart    VFS: Mounted root             timeout=120  testerId=${uart2}
    Wait For Prompt On Uart  dabao login:                  timeout=120  testerId=${uart2}
    Write Line To Uart       root                          testerId=${uart2}
    Wait For Prompt On Uart  \#                            timeout=60   testerId=${uart2}
    Write Line To Uart       uname -a && free              testerId=${uart2}
    Wait For Line On Uart    riscv32                       timeout=30   testerId=${uart2}
    Wait For Line On Uart    Mem:                          timeout=30   testerId=${uart2}
    # Timer sanity: sleep must return (clockevent -> TIMER0 -> STIP path).
    Write Line To Uart       sleep 1 && echo TIMER_OK      testerId=${uart2}
    Wait For Line On Uart    TIMER_OK                      timeout=30   testerId=${uart2}
