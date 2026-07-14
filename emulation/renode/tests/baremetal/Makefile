CROSS   ?= riscv64-linux-gnu-
OUT     ?= ../../../../build/renode-smoke
CFLAGS  := -march=rv32imac_zicsr_zifencei -mabi=ilp32 -O2 -g \
           -ffreestanding -nostdlib -Wall -Wextra

all: $(OUT)/smoke.elf

$(OUT)/smoke.elf: crt0.S main.c bao1x.ld | $(OUT)
	$(CROSS)gcc $(CFLAGS) -T bao1x.ld -o $@ crt0.S main.c

$(OUT):
	mkdir -p $@

clean:
	rm -f $(OUT)/smoke.elf

.PHONY: all clean
