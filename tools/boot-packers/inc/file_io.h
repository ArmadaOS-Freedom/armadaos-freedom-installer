// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include <stdio.h>
#ifdef _WIN32
#include <wchar.h>
#endif

// UTF-8 paths; Windows uses extended absolute UTF-16 paths without registry changes.
FILE *open_file_utf8(const char *path, const char *mode);
int make_directory_utf8(const char *path);
#ifdef _WIN32
int run_utf8_main(int (*entry)(int, char **), int argc, wchar_t **argv);
#endif
