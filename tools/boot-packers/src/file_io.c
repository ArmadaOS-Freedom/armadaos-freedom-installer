/** @file
 * UTF-8 aware file and path helpers for native Windows tools.
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

#include "file_io.h"

#include <errno.h>
#include <stdlib.h>
#include <string.h>

#ifndef _WIN32
#include <sys/stat.h>
#endif

#ifdef _WIN32
#include <direct.h>
#include <windows.h>

/* Strict UTF-8 -> UTF-16 conversion.  Returns NULL with errno=EILSEQ. */
static wchar_t *utf8_to_utf16(const char *path)
{
  if (path == NULL) {
    errno = EINVAL;
    return NULL;
  }
  int wideLength = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, NULL, 0);
  if (wideLength <= 0) {
    errno = EILSEQ;
    return NULL;
  }
  wchar_t *wide = malloc((size_t)wideLength * sizeof(wchar_t));
  if (wide == NULL)
    return NULL;
  if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide, wideLength) <= 0) {
    free(wide);
    errno = EILSEQ;
    return NULL;
  }
  return wide;
}

/* True if path already begins with the \\?\ extended-length prefix. */
static int has_extended_prefix(const wchar_t *path)
{
  return path[0] == L'\\' && path[1] == L'\\' && path[2] == L'?' && path[3] == L'\\';
}

/*
 * Build an extended-length absolute wide path from a normalized input.
 *
 * - Input separators must already be '\\'.
 * - Already-prefixed inputs are preserved verbatim.
 * - Drive absolute (C:...) becomes \\?\C:...
 * - UNC (\\server\share\...) becomes \\?\UNC\server\share\...
 * - Anything else is resolved with GetFullPathNameW (which handles
 *   relative, dot segments and drive-relative C:foo forms) against the
 *   current working directory, then prefixed.  If GetFullPathNameW
 *   returns an already-extended result it is preserved verbatim.
 *
 * Returns a heap string the caller must free, or NULL with errno set
 * (path resolution errors also report the Win32 error code).
 */
static wchar_t *make_extended_path(const wchar_t *path)
{
  if (path == NULL || path[0] == L'\0') {
    errno = EINVAL;
    return NULL;
  }
  if (has_extended_prefix(path)) {
    size_t length = wcslen(path) + 1;
    wchar_t *copy = malloc(length * sizeof(wchar_t));
    if (copy == NULL)
      return NULL;
    memcpy(copy, path, length * sizeof(wchar_t));
    return copy;
  }

  DWORD fullLength = GetFullPathNameW(path, 0, NULL, NULL);
  if (fullLength == 0) {
    fprintf(stderr, "Cannot resolve path: win32=%lu\n", (unsigned long)GetLastError());
    errno = EINVAL;
    return NULL;
  }
  wchar_t *absolute = malloc(((size_t)fullLength + 1) * sizeof(wchar_t));
  if (absolute == NULL)
    return NULL;
  DWORD written = GetFullPathNameW(path, fullLength + 1, absolute, NULL);
  if (written == 0 || written > fullLength) {
    DWORD error = written == 0 ? GetLastError() : ERROR_INSUFFICIENT_BUFFER;
    free(absolute);
    fprintf(stderr, "Cannot resolve path: win32=%lu\n", (unsigned long)error);
    errno = EINVAL;
    return NULL;
  }

  /* GetFullPathNameW may itself return an already-extended name when the
   * process cwd is long; do not add a second prefix. */
  if (has_extended_prefix(absolute))
    return absolute;

  const wchar_t *suffix = absolute;
  const wchar_t *prefix = NULL;
  if (absolute[0] == L'\\' && absolute[1] == L'\\') {
    prefix = L"\\\\?\\UNC";
    suffix = absolute + 1; /* keep leading backslash of \\server -> \UNC\... */
  } else {
    prefix = L"\\\\?\\";
  }

  size_t prefixLength = wcslen(prefix);
  size_t suffixLength = wcslen(suffix);
  wchar_t *result = malloc((prefixLength + suffixLength + 1) * sizeof(wchar_t));
  if (result == NULL) {
    free(absolute);
    return NULL;
  }
  memcpy(result, prefix, prefixLength * sizeof(wchar_t));
  memcpy(result + prefixLength, suffix, (suffixLength + 1) * sizeof(wchar_t));
  free(absolute);
  return result;
}

/* Convert UTF-8 to a normalized extended-length wide path. */
static wchar_t *utf8_to_extended_path(const char *path)
{
  wchar_t *wide = utf8_to_utf16(path);
  if (wide == NULL)
    return NULL;
  for (wchar_t *cursor = wide; *cursor != L'\0'; cursor++) {
    if (*cursor == L'/')
      *cursor = L'\\';
  }
  wchar_t *extended = make_extended_path(wide);
  int saved = errno;
  free(wide);
  errno = saved;
  return extended;
}

FILE *open_file_utf8(const char *path, const char *mode)
{
  if (path == NULL || path[0] == '\0' || mode == NULL || mode[0] == '\0') {
    errno = EINVAL;
    return NULL;
  }
  wchar_t *wideMode = utf8_to_utf16(mode);
  if (wideMode == NULL)
    return NULL;
  wchar_t *widePath = utf8_to_extended_path(path);
  if (widePath == NULL) {
    int saved = errno;
    free(wideMode);
    errno = saved;
    return NULL;
  }
  FILE *file = _wfopen(widePath, wideMode);
  int saved = errno;
  free(widePath);
  free(wideMode);
  errno = saved;
  return file;
}

int make_directory_utf8(const char *path)
{
  if (path == NULL || path[0] == '\0') {
    errno = EINVAL;
    return -1;
  }
  wchar_t *widePath = utf8_to_extended_path(path);
  if (widePath == NULL)
    return -1;
  int result = _wmkdir(widePath);
  int saved = errno;
  free(widePath);
  errno = saved;
  return result;
}

int run_utf8_main(int (*entry)(int, char **), int argc, wchar_t **argv)
{
  if (entry == NULL || argc < 0 || (argc > 0 && argv == NULL)) {
    errno = EINVAL;
    return -EINVAL;
  }
  char **narrow = calloc((size_t)argc + 1, sizeof(char *));
  if (narrow == NULL)
    return -ENOMEM;
  int status = -EILSEQ;
  for (int index = 0; index < argc; index++) {
    int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[index], -1, NULL, 0, NULL, NULL);
    if (length <= 0)
      goto cleanup;
    char *converted = malloc((size_t)length);
    if (converted == NULL) {
      status = -ENOMEM;
      goto cleanup;
    }
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[index], -1, converted, length, NULL, NULL) <= 0) {
      free(converted);
      goto cleanup;
    }
    narrow[index] = converted;
  }
  narrow[argc] = NULL;
  status = entry(argc, narrow);

cleanup:
  for (int index = 0; index < argc; index++)
    free(narrow[index]);
  free(narrow);
  return status;
}

#else /* !_WIN32 */

FILE *open_file_utf8(const char *path, const char *mode)
{
  if (path == NULL || path[0] == '\0' || mode == NULL || mode[0] == '\0') {
    errno = EINVAL;
    return NULL;
  }
  return fopen(path, mode);
}

int make_directory_utf8(const char *path)
{
  if (path == NULL || path[0] == '\0') {
    errno = EINVAL;
    return -1;
  }
  return mkdir(path, 0777);
}

#endif /* _WIN32 */