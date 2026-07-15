# bao1x-sbi: M-mode SBI shim for the Baochip-1x.
# Usage: make DTB=path/to/board.dtb [O=outdir]
CROSS   ?= riscv64-linux-gnu-
O       ?= ../../build/bao1x-sbi
DTB     ?= $(O)/bao1x-renode.dtb

CFLAGS  := -march=rv32imac_zicsr_zifencei -mabi=ilp32 -mcmodel=medany \
           -Os -g -Wall -Wextra -ffreestanding -fno-builtin -fno-pic \
           -fno-stack-protector
LDFLAGS := -nostdlib -Wl,--build-id=none -Wl,-T,bao1x-sbi.ld

OBJS := $(O)/entry.o $(O)/main.o $(O)/trap.o $(O)/console.o

all: $(O)/bao1x-sbi.elf $(O)/bao1x-sbi.bin

$(O):
	mkdir -p $@

$(O)/entry.o: entry.S bao1x.h $(DTB) | $(O)
	$(CROSS)gcc $(CFLAGS) -DDTB_PATH='"$(DTB)"' -c $< -o $@

$(O)/%.o: %.c bao1x.h | $(O)
	$(CROSS)gcc $(CFLAGS) -c $< -o $@

$(O)/bao1x-sbi.elf: $(OBJS) bao1x-sbi.ld
	$(CROSS)gcc $(CFLAGS) $(LDFLAGS) $(OBJS) -o $@
	$(CROSS)size $@

$(O)/bao1x-sbi.bin: $(O)/bao1x-sbi.elf
	$(CROSS)objcopy -O binary $< $@

clean:
	rm -rf $(O)

.PHONY: all clean
