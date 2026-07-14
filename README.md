# Mainline Linux on Baochip-1x / Dabao

Port of mainline Linux to the [Baochip-1x](https://baochip.com/) SoC (VexRiscv RV32-IMAC,
Sv32 MMU, 2 MiB SRAM + 4 MiB RRAM) on the [Dabao evaluation
board](https://www.crowdsupply.com/baochip/dabao).

No Linux port exists for this chip; the vendor OS is [Xous](https://github.com/betrusted-io/xous-core).
This repo contains the research, design, firmware, kernel patches, emulation platform, and
build glue to change that — with all kernel work kept upstream-quality.

## Layout

| Path         | Contents                                                          |
|--------------|-------------------------------------------------------------------|
| `docs/`      | Hardware dossier, port design, emulation notes, bring-up log      |
| `setup/`     | Reproducible environment setup (`install-tools.sh`, `fetch-sources.sh`) |
| `emulation/` | Renode platform for bao1x (`.repl`, peripheral models, run scripts) |
| `firmware/`  | `bao1x-sbi`: tiny M-mode SBI shim that boots the S-mode kernel    |
| `linux/`     | Kernel patch series, defconfig, device trees                      |
| `buildroot/` | Buildroot external tree for the Dabao board (rootfs, images)      |
| `tools/`     | Image signing / UF2 packaging for the boot1 bootloader            |
| `sources/`   | (gitignored) upstream trees fetched by `setup/fetch-sources.sh`   |

## Quick start

```sh
./setup/install-tools.sh    # dnf-installs toolchains, QEMU, Renode, etc.
./setup/fetch-sources.sh    # clones linux, buildroot, xous-core, baochip-1x into sources/
```

Then see `docs/00-overview.md` for the plan and current status.

## Status

- [x] Phase 0 — research, repo, environment
- [ ] Phase 1 — rv32 XIP Linux feasibility on QEMU `virt`
- [ ] Phase 2 — Renode platform for bao1x
- [ ] Phase 3 — SBI shim + Linux boot in Renode
- [ ] Phase 4 — hardware bring-up on Dabao
- [ ] Phase 5 — drivers: pinctrl/GPIO, I2C/SPI, RRAM MTD, SD, USB gadget
- [ ] Phase 6 — upstream-ready patch series
