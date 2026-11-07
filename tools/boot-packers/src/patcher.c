/** @file
 * Multi-Boot Kernel Patcher entry point.
 *
 * Copyright (c) 2021-2026 The DuoWoa authors. All rights reserved.
 * Copyright (c) 2024-2026 The AlohaWoA authors. All rights reserved.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include "file_io.h"
#include "log.h"
#include "pack.h"

#include <errno.h>
#include <stdio.h>

static int packer_entry(int argc, char *argv[])
{
  log_info("Multi-Boot Kernel Patcher v2.0.0.0");
  log_info("Copyright (c) The DuoWoA & The AlohaWoA authors");
  if (argc != 2) {
    log_err("Usage: MultiBootKernelPatcher <Config File>");
    return -EINVAL;
  }
  return PackConfigFile(argv[1]);
}

#ifdef WINDOWS_UTF8_ENTRY
int wmain(int argc, wchar_t *argv[])
{
  return run_utf8_main(packer_entry, argc, argv);
}
#else
int main(int argc, char *argv[])
{
  return packer_entry(argc, argv);
}
#endif
