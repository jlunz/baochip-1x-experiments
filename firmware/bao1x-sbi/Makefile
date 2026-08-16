# bao1x-sbi: M-mode SBI shim for the Baochip-1x.
# Usage: make BOARD=renode|dabao DTB=path/to/board.dtb [O=outdir] [SHIM_ONLY=1]
#
# SHIM_ONLY=1 builds a shim that runs every stage and then parks in a console
# heartbeat instead of entering the kernel. It is the bring-up rung that proves
# the handover on a board without ever executing Linux -- see
# docs/08-recovery-and-risk-ladder.md.
CROSS   ?= riscv64-linux-gnu-
O       ?= ../../build/bao1x-sbi
BOARD   ?= renode
DTB     ?= $(O)/bao1x-renode.dtb
SHIM_ONLY ?=

CFLAGS  := -march=rv32imac_zicsr_zifencei -mabi=ilp32 -mcmodel=medany \
           -Os -g -Wall -Wextra -ffreestanding -fno-builtin -fno-pic \
           -fno-stack-protector -DBOARD_$(BOARD)
ifeq ($(SHIM_ONLY),1)
CFLAGS  += -DSHIM_ONLY
endif
LDFLAGS := -nostdlib -Wl,--build-id=none -Wl,-T,bao1x-sbi.ld

OBJS := $(O)/entry.o $(O)/main.o $(O)/trap.o $(O)/console.o $(O)/board.o

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
