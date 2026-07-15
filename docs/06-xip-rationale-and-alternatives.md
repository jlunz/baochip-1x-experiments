# Why XIP, and the ways around it (incl. adding hardware)

Rationale for running the kernel execute-in-place (XIP) from RRAM instead of a normal
RAM-loaded `Image`, the alternatives considered, and — since it comes up — a hardware analysis
of whether external RAM could escape the constraint. Evidence points at RTL/HAL paths so the
claims can be re-checked. See also `01-hardware-dossier.md` (§2 memory map, §9 board, §10 open
questions) and `02-port-design.md` (decided architecture).

## 1. The constraint that forces the decision

The SoC has **2 MiB internal SRAM** (`0x6100_0000`, 2 MiB) and **4 MiB nonvolatile RRAM**
(`0x6000_0000`, memory-mapped reads = XIP-capable; writes via the RRC). The Dabao board fits
**no external RAM** (dossier §9). So the kernel's read-only footprint has to live somewhere,
and the only "somewhere" with room is the RRAM.

Measured from the phase-3 `vmlinux` (`build/phase3/kernel/vmlinux`, `size`):

| segment       | size        | XIP places it in | a RAM-loaded `Image` places it in |
|---------------|-------------|------------------|-----------------------------------|
| text + rodata | ~1.55 MiB   | **flash (RRAM)** | **SRAM**                          |
| data          | ~112 KiB    | SRAM             | SRAM                              |
| bss           | ~56 KiB     | SRAM             | SRAM                              |

A conventional boot would drop ~1.55 MiB of text+rodata into the 2 MiB SRAM, leaving **~280 KiB**
for page cache, slab, page tables *and* all of userspace. That does not boot. XIP keeps that
1.55 MiB in flash and spends SRAM only on `.data`/`.bss` + runtime — which is what makes a real
userspace fit (proven: busybox shell, ~250K–580K free depending on driver set).

## 2. Why XIP was chosen (summary)

1. **SRAM budget** — see §1. This is the decisive reason.
2. **The RRAM is XIP-capable and directly mapped** — nonvolatile and directly executable, so
   copying it into scarce SRAM would only waste RAM.
3. **The signed-boot chain already lands the payload in flash, in place** — boot1 → dev-signed
   UF2 at RRAM `0x6006_0000` → SBI shim → S-mode kernel, all executing from RRAM.
4. **rodata is RAM-free under XIP** — read-only data (incl. `KALLSYMS`) stays in flash, so kernel
   symbols can be enabled for hardware debugging at zero SRAM cost.

**Cost accepted:** mainline RISC-V `XIP_KERNEL` was removed in Linux 7.1 (Nam Cao,
`9b3a2be84803`). We base on last-good v6.14 (predates `a44fb5722199` "runtime constant support",
which broke XIP by patching immediates in read-only kernel text) and forward-port. This port is
effectively the "real user" justifying XIP's continued existence (7.1.3 revival proven feasible;
see `03-status-and-resume.md`).

## 3. Ways around it — software only (no hardware change)

None of these let us drop XIP and still fit; they are recorded so they aren't re-litigated.

- **RAM-loaded `Image`** — the §1 table: doesn't fit. Dead end.
- **Compressed-in-flash, decompress to RAM** — same endpoint (text lands in SRAM) plus
  decompression scratch. Strictly worse.
- **Repurpose IFRAM0/1 (2×128 KiB) as a second RAM node** — on-chip, keeps XIP, but only 256 KiB,
  it's uDMA-only memory the I2C/SPI/USB/camera drivers already use as bounce buffers, and
  cacheability is questionable. Marginal headroom; does nothing about the XIP maintenance burden.
- **Shrink further / NOMMU** — already done aggressively (the 2 MiB recipe: `SECTION_SIZE_BITS`,
  `LOG_BUF_SHIFT`, `SYSFS=n`/`BLOCK=n`, `THREAD_SIZE_ORDER=0`). Diminishing returns.

**Conclusion:** with no hardware change, XIP stays — there is no software trick that keeps
~1.55 MiB of text out of SRAM without executing it in place from flash.

## 4. Ways around it — adding hardware

### 4.1 The QFC is an ESP32-style serial-memory-map engine (verified in RTL)

The Quad Flash Controller (QFC, control regs `0x4001_0000`; SoC decode `idx 1`
`0x4001_0000–0x4002_0000` in `RTL:rtl/asic_top/rtl/daric_cfg_pkg.sv`) is built on the SmartDV
`spi_flash_controller_iip` (`RTL:rtl/asic_top/include/qfc_inc.sv`). It is **not** a plain flash
reader — it is architecturally the same kind of block as the ESP32's external-memory controller:

- **Memory-mapped writes are supported**, not just reads: XIP write opcodes
  `cfg_xip_wr_opcode`, `cfg_xip_wr_opcode_ext`, `cfg_xip_wr_dummy_cycs` alongside the read-side
  `cfg_xip_rd_dummy_cycs` (`RTL:rtl/modules/core/rtl/qfc.sv` ~L380–385).
- **Full AXI slave with a write channel** (`AW`/`W`/`B` + `AR`/`R`, `qfc.sv` ~L406–434) → the CPU
  bus can issue ordinary reads *and* writes to the mapped window; the QFC serialises them.
- **Transparent AES** in the path (`qfc_aes` / `qfc_socbus_aes`), like ESP32 flash/PSRAM
  encryption. SVD: `CR_AESKEY_*`, `CR_AESENA`.
- **Configurable XIP** engine: SVD `CR_XIP_ADDRMODE/OPCODE/WIDTH/SSEL/DUMCYC/CFG`; two chip
  selects (SS0/SS1).
- **Validated against real external RAM in simulation**: the RTL testbench instantiates a
  Winbond **W959D8NFYA HyperRAM** on the QFC pins under `` `ifdef HYPERRAM ``
  (`RTL:rtl/asic_top/testbench/daric_rv32_tb.sv` ~L185–197; the `` `ifdef QSPI `` alternative is a
  W25Q128 NOR). HyperRAM is byte-addressable read/write random-access memory.

So the silicon **can** map external read/write RAM into the address space. This is the same
mechanism the user has seen on ESP32.

### 4.2 ESP32 comparison

| aspect                          | ESP32 / ESP32-S3                              | bao1x QFC                                        |
|---------------------------------|-----------------------------------------------|--------------------------------------------------|
| ext serial memory into address space | SPI0 + cache-MMU, 64 KB pages (flash + PSRAM) | QFC AXI slave maps external device as XIP window  |
| read-map (XIP)                  | yes                                           | yes (used today for 16 MiB SPI NOR)              |
| **write-map (RAM semantics)**   | yes (PSRAM writable)                          | **yes — XIP write opcodes + AXI write channel**  |
| transparent encryption          | flash/PSRAM AES                               | `qfc_aes` in same path                           |
| validated ext-RAM part          | ESP-PSRAM / Octal PSRAM                        | Winbond HyperRAM (RTL testbench)                 |
| **cache in front of it**        | **dedicated external-memory cache** (makes PSRAM usable; S3 can even run code from it) | **ITCM/DTCM tightly-coupled SRAM**, not a set-assoc cache over the QFC window |

The architectures line up almost exactly. The one consequential difference is the **cache**.

### 4.3 The two catches that keep XIP in the picture anyway

1. **Dabao doesn't fit the RAM part.** The board populates the QFC with **16 MiB external SPI
   NOR flash** (`SPINOR_LEN`, `sources/xous-core/libs/bao1x-hal/src/board/dabao.rs:122`), driven
   with flash semantics (read-map + command program/erase). The HyperRAM is a testbench-only
   option. Using it as RAM needs a **HyperRAM/PSRAM device physically added on the QFC bus**
   (second chip-select on the flash pins, or a board respin) — not a config flag.
2. **No big cache over the window.** On ESP32 the dedicated external-memory cache is what makes
   serial PSRAM fast enough to be main RAM. The bao1x RV32 (VexRiscv) instead leans on **ITCM**
   (4×32k×18) + **DTCM** (2×8k×36) tightly-coupled SRAM macros
   (`RTL:rtl/asic_top/rtl/soc_top.sv` ~L269–270) plus a modest **write-through** D-cache. The QFC
   data window does not appear in the RV32 local decode table (reached over the AXI fabric), and
   its caching behaviour is unverified. Serial HyperRAM is slower than the internal RRAM we
   already XIP from, and RRAM read wait-states are themselves still an open XIP-perf question
   (dossier §10 item 3).

### 4.4 Other hardware options (weaker)

- **External QSPI NOR as a second XIP window** — offloads rootfs/rodata off internal RRAM;
  frees flash, not SRAM. Doesn't touch the squeeze.
- **A chip variant with more SRAM / in-package PSRAM** — dissolves the whole problem, but that's
  a silicon change, not a board add; out of scope for this port.
- **Parallel/HyperRAM EMC** — the SoC has no parallel external-memory controller, only the serial
  QFC/SPIM. So a "fast RAM bus" means new silicon, not a solder job.

## 5. Bottom line

- **No hardware change:** XIP is effectively forced. The only lever is repurposing ~256 KiB of
  IFRAM for a little headroom; you cannot drop XIP and still boot.
- **The one hardware add that changes the equation** is external **HyperRAM/PSRAM mapped as RAM
  via the QFC** — and, crucially, the controller genuinely supports it (memory-mapped writes +
  HyperRAM-validated), so this is not hand-waving. It is the same trick as ESP32.
- **But** without an ESP32-style cache in front, external serial RAM here behaves as a **slower
  second memory zone**, not a transparent RAM upgrade. The sensible Linux design is a two-zone /
  NUMA-ish split: hot working set + stacks + kernel `.data` in the fast 2 MiB SRAM, cold/anonymous
  userspace pages in HyperRAM.
- Therefore added RAM cleanly solves the **headroom** problem but does **not** cleanly retire the
  XIP **maintenance burden** — you'd still execute kernel text in place from fast RRAM. And on
  Dabao specifically you'd first have to physically add the part.
