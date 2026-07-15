/*
 * Minimal GPIO chardev (v2 uAPI) tool for the bao1x rootfs — busybox has
 * no gpioset/gpioget and the kernel is built without sysfs.
 *
 *   gpio-tool set <line> <0|1>   drive a line (value persists on release)
 *   gpio-tool get <line>         read a line, prints "line <n> = <v>"
 *
 * SPDX-License-Identifier: MIT
 */
#include <fcntl.h>
#include <linux/gpio.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

static const char *chip = "/dev/gpiochip0";

int main(int argc, char **argv)
{
	struct gpio_v2_line_request req;
	struct gpio_v2_line_values vals;
	int fd, line, value;

	if (argc < 3)
		goto usage;
	line = atoi(argv[2]);

	fd = open(chip, O_RDWR);
	if (fd < 0) {
		perror(chip);
		return 1;
	}

	memset(&req, 0, sizeof(req));
	req.offsets[0] = line;
	req.num_lines = 1;
	strncpy(req.consumer, "gpio-tool", sizeof(req.consumer) - 1);

	if (!strcmp(argv[1], "set") && argc == 4) {
		value = atoi(argv[3]);
		req.config.flags = GPIO_V2_LINE_FLAG_OUTPUT;
		req.config.num_attrs = 1;
		req.config.attrs[0].attr.id = GPIO_V2_LINE_ATTR_ID_OUTPUT_VALUES;
		req.config.attrs[0].attr.values = value ? 1 : 0;
		req.config.attrs[0].mask = 1;
		if (ioctl(fd, GPIO_V2_GET_LINE_IOCTL, &req) < 0) {
			perror("GPIO_V2_GET_LINE (output)");
			return 1;
		}
		printf("line %d <= %d\n", line, value);
		return 0;
	}

	if (!strcmp(argv[1], "get") && argc == 3) {
		req.config.flags = GPIO_V2_LINE_FLAG_INPUT;
		if (ioctl(fd, GPIO_V2_GET_LINE_IOCTL, &req) < 0) {
			perror("GPIO_V2_GET_LINE (input)");
			return 1;
		}
		memset(&vals, 0, sizeof(vals));
		vals.mask = 1;
		if (ioctl(req.fd, GPIO_V2_LINE_GET_VALUES_IOCTL, &vals) < 0) {
			perror("GPIO_V2_LINE_GET_VALUES");
			return 1;
		}
		printf("line %d = %d\n", line, (int)(vals.bits & 1));
		return 0;
	}

usage:
	fprintf(stderr, "usage: gpio-tool set <line> <0|1> | gpio-tool get <line>\n");
	return 2;
}
