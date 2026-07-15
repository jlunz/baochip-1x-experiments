/*
 * Minimal I2C chardev tool for the bao1x rootfs (no i2c-tools package).
 *
 *   i2c-tool read <addr> <reg> [n]   write reg pointer, read n bytes (1-8)
 *   i2c-tool scan                    probe addresses 0x08..0x77
 *
 * SPDX-License-Identifier: MIT
 */
#include <fcntl.h>
#include <linux/i2c.h>
#include <linux/i2c-dev.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

static const char *bus = "/dev/i2c-0";

int main(int argc, char **argv)
{
	int fd = open(bus, O_RDWR);

	if (fd < 0) {
		perror(bus);
		return 1;
	}

	if (argc >= 2 && !strcmp(argv[1], "scan")) {
		int found = 0;

		for (int a = 0x08; a <= 0x77; a++) {
			struct i2c_msg msg = {
				.addr = a, .flags = I2C_M_RD, .len = 1,
				.buf = (unsigned char[]){0},
			};
			struct i2c_rdwr_ioctl_data d = { .msgs = &msg,
							 .nmsgs = 1 };
			if (ioctl(fd, I2C_RDWR, &d) >= 0) {
				printf("found 0x%02x\n", a);
				found++;
			}
		}
		printf("%d device(s)\n", found);
		return 0;
	}

	if (argc >= 4 && !strcmp(argv[1], "read")) {
		unsigned char reg = strtoul(argv[3], NULL, 0);
		unsigned char buf[8] = {0};
		int n = argc > 4 ? atoi(argv[4]) : 1;
		struct i2c_msg msgs[2] = {
			{ .addr = strtoul(argv[2], NULL, 0), .flags = 0,
			  .len = 1, .buf = &reg },
			{ .addr = strtoul(argv[2], NULL, 0), .flags = I2C_M_RD,
			  .len = n > 8 ? 8 : n, .buf = buf },
		};
		struct i2c_rdwr_ioctl_data d = { .msgs = msgs, .nmsgs = 2 };

		if (ioctl(fd, I2C_RDWR, &d) < 0) {
			perror("I2C_RDWR");
			return 1;
		}
		printf("reg 0x%02x:", reg);
		for (int i = 0; i < msgs[1].len; i++)
			printf(" %02x", buf[i]);
		printf("\n");
		return 0;
	}

	fprintf(stderr, "usage: i2c-tool scan | i2c-tool read <addr> <reg> [n]\n");
	return 2;
}
