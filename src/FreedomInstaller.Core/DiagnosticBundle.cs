using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

/// <summary>Single diagnostic boot file candidate supplied by a verified manifest.</summary>
/// <param name="Source">One of captured, stock, candidate.</param>
/// <param name="Role">A/B role name (abl_a, vbmeta_b, boot_a, ...).</param>
/// <param name="Path">Trusted local absolute path from a verified manifest.</param>
/// <param name="ExpectedBytes">Optional claimed size; must match if supplied.</param>
/// <param name="ExpectedSha256">Optional claimed SHA-256 as 64 hex characters.</param>
public sealed record DiagnosticBootFile(
    string Source,
    string Role,
    string Path,
    long? ExpectedBytes = null,
    string? ExpectedSha256 = null);

/// <summary>Inputs for creating a local-only diagnostic ZIP bundle.</summary>
public sealed record DiagnosticExportRequest(
    string Summary,
    string Log,
    IReadOnlyList<string> Redactions,
    IReadOnlyList<DiagnosticBootFile> BootFiles,
    bool IncludeAbl,
    bool IncludeLargeBoot)
{
    /// <summary>Optional host environment pairs; when supplied, written as environment.json.</summary>
    public IReadOnlyDictionary<string, string?>? HostEnvironment { get; init; }

    /// <summary>
    /// Opt-in 由用户明确要求暴露的「最近一次检查的目标」ADB 序列号。仅在导出时写入
    /// installer.log 与 summary.txt 的一行 <c>LastCheckedDeviceSerial=...</c>。必须通过
    /// 本地 ADB 序列号格式校验；非法/多行/端点形式将被忽略。通用脱敏对其它位置仍生效，
    /// 且本字段不加入 redactions 列表，因此此单一来源会原样出现。
    /// </summary>
    public string? LastCheckedDeviceSerial { get; init; }
}

/// <summary>
/// Builds a local diagnostic ZIP containing a redacted summary, log, manifest, and
/// caller-provided boot payloads. Never touches devices. Fails closed on aliasing
/// between sources and destination, on unsafe parent chains, and on evidence that
/// cannot be fully validated before publishing.
/// </summary>
public static class DiagnosticBundle
{
    private const long AblVbmetaCap = 16L * 1024 * 1024;
    private const long BootClassCap = 256L * 1024 * 1024;
    private const int CopyBufferSize = 64 * 1024;

    private static readonly HashSet<string> AllowedSources = new(StringComparer.Ordinal)
    {
        "captured", "stock", "candidate",
    };

    private static readonly HashSet<string> AblRoles = new(StringComparer.Ordinal)
    {
        "abl_a", "abl_b",
        "vbmeta_a", "vbmeta_b", "vbmeta_system_a", "vbmeta_system_b",
    };

    private static readonly HashSet<string> LargeBootRoles = new(StringComparer.Ordinal)
    {
        "boot_a", "boot_b",
        "vendor_boot_a", "vendor_boot_b",
        "init_boot_a", "init_boot_b",
    };

    private static readonly string[] AlwaysRequestedCapturedAblRoles = { "abl_a", "abl_b" };

    private static readonly UTF8Encoding Utf8NoBom =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const int MaxAdbSerialLength = 128;

    // 本地 ADB 序列号语法：ASCII 字母/数字与 '-','_','.'，非空，长度上限 128。
    // 明确拒绝 ':'（adb endpoint "host:port" 形式）、控制字符与多行。
    private static bool IsPlausibleLocalAdbSerial(string? serial)
    {
        if (string.IsNullOrEmpty(serial)) return false;
        if (serial.Length > MaxAdbSerialLength) return false;
        foreach (char c in serial)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                      || c == '-' || c == '_' || c == '.';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// 通用脱敏后，若用户明确提供了合法的最近检查目标序列号，则追加/前置一行
    /// <c>LastCheckedDeviceSerial=...</c>。该值经过独立语法校验，不进入 redactions，
    /// 每次文本条目最多出现一次。
    /// </summary>
    private static string AppendLastCheckedDeviceSerialIfRequested(string sanitizedText, string? serial)
    {
        if (!IsPlausibleLocalAdbSerial(serial)) return sanitizedText;
        string row = "LastCheckedDeviceSerial=" + serial + "\n";
        return row + sanitizedText;
    }

    /// <summary>
    /// Redacts supplied literal secrets and well-known sensitive shapes (Windows/UNC/Unix
    /// home paths, serial numbers, auth/password/token/query/header fields). Literals are
    /// scrubbed longest-first, case-insensitively, ignoring very short values.
    /// </summary>
    public static string Sanitize(string text, IReadOnlyList<string> redactions)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        string result = text;

        if (redactions is not null)
        {
            string[] literals = redactions
                .Where(r => !string.IsNullOrEmpty(r) && r.Length >= 4)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(r => r.Length)
                .ToArray();
            foreach (string literal in literals)
            {
                result = result.Replace(literal, "[redacted]", StringComparison.OrdinalIgnoreCase);
            }
        }

        result = RedactShapes(result);
        return result;
    }

    /// <summary>
    /// Exports the diagnostic bundle to <paramref name="destination"/>. All validation,
    /// including aliasing checks against unselected/invalid sources, happens before any
    /// write. On any post-open failure, the temporary archive is deleted and an existing
    /// destination is left intact.
    /// </summary>
    public static async Task ExportAsync(
        string destination,
        DiagnosticExportRequest request,
        CancellationToken token = default)
    {
        if (destination is null) throw new ArgumentNullException(nameof(destination));
        if (request is null) throw new ArgumentNullException(nameof(request));
        token.ThrowIfCancellationRequested();

        string destinationFull = Path.GetFullPath(destination);
        string? destinationDirectory = Path.GetDirectoryName(destinationFull);
        if (string.IsNullOrEmpty(destinationDirectory))
            throw new ArgumentException("Destination must have a parent directory.", nameof(destination));
        if (!Directory.Exists(destinationDirectory))
            throw new DirectoryNotFoundException("Destination directory does not exist.");

        string resolvedDirectory = ResolveDirectory(destinationDirectory, 32);
        string effectiveDestination = Path.Combine(resolvedDirectory, Path.GetFileName(destinationFull));
        if (string.IsNullOrEmpty(Path.GetFileName(effectiveDestination)))
            throw new ArgumentException("Destination must have a file name.", nameof(destination));
        var destinationInfo = new FileInfo(effectiveDestination);
        if (destinationInfo.LinkTarget is not null)
            throw new InvalidOperationException("Diagnostic export refused: destination is a symbolic link.");

        IReadOnlyList<string> redactions = request.Redactions ?? Array.Empty<string>();
        IReadOnlyList<DiagnosticBootFile> supplied = request.BootFiles ?? Array.Empty<DiagnosticBootFile>();

        // Fail closed: if ANY supplied source path (regardless of validity, role, or
        // selection) aliases the destination, refuse before any side effect.
        EnsureNoAlias(supplied, effectiveDestination);

        List<PlannedFile> planned = PlanFiles(effectiveDestination, supplied, request.IncludeAbl, request.IncludeLargeBoot);

        string tempPath = Path.Combine(
            resolvedDirectory,
            "." + Path.GetFileName(effectiveDestination) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        bool published = false;
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, useAsync: true))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                token.ThrowIfCancellationRequested();
                WriteEntry(
                    zip,
                    "summary.txt",
                    Utf8NoBom.GetBytes(AppendLastCheckedDeviceSerialIfRequested(
                        Sanitize(request.Summary ?? string.Empty, redactions),
                        request.LastCheckedDeviceSerial)));

                token.ThrowIfCancellationRequested();
                WriteEntry(
                    zip,
                    "installer.log",
                    Utf8NoBom.GetBytes(AppendLastCheckedDeviceSerialIfRequested(
                        Sanitize(request.Log ?? string.Empty, redactions),
                        request.LastCheckedDeviceSerial)));

                if (request.HostEnvironment is not null)
                {
                    token.ThrowIfCancellationRequested();
                    var environment = new SortedDictionary<string, string?>(StringComparer.Ordinal);
                    foreach (var pair in request.HostEnvironment)
                    {
                        environment[pair.Key] = pair.Value is null ? null : Sanitize(pair.Value, redactions);
                    }
                    WriteEntry(zip, "environment.json", JsonSerializer.SerializeToUtf8Bytes(environment, JsonOptions));
                }

                var copiedRecords = new List<object>();
                foreach (PlannedFile plan in planned)
                {
                    token.ThrowIfCancellationRequested();
                    object record = await CopyAndRecordAsync(zip, plan, token).ConfigureAwait(false);
                    copiedRecords.Add(record);
                }

                token.ThrowIfCancellationRequested();
                byte[] manifestBytes = BuildManifest(request, redactions, supplied, planned, copiedRecords);
                WriteEntry(zip, "manifest.json", manifestBytes);
            }

            token.ThrowIfCancellationRequested();
            File.Move(tempPath, effectiveDestination, overwrite: true);
            published = true;
        }
        finally
        {
            if (!published) TryDelete(tempPath);
        }
    }

    private sealed record PlannedFile(DiagnosticBootFile File, long Cap);

    private static void EnsureNoAlias(IReadOnlyList<DiagnosticBootFile> supplied, string destinationFull)
    {
        string destinationDirectory = ResolveDirectory(
            Path.GetDirectoryName(destinationFull) ?? destinationFull, 32);
        string destinationLeaf = Path.GetFileName(destinationFull);
        foreach (DiagnosticBootFile? file in supplied)
        {
            if (file is null || string.IsNullOrEmpty(file.Path)) continue;
            string full;
            try { full = Path.GetFullPath(file.Path); }
            catch { continue; }
            string sourceDirectory = ResolveDirectory(
                Path.GetDirectoryName(full) ?? full, 32);
            string sourceLeaf = Path.GetFileName(full) ?? string.Empty;
            string resolvedSource = string.IsNullOrEmpty(sourceLeaf)
                ? sourceDirectory
                : Path.Combine(sourceDirectory, sourceLeaf);
            if (string.Equals(resolvedSource, Path.Combine(destinationDirectory, destinationLeaf), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Diagnostic export refused: a source path aliases the destination.");
        }
    }

    private static string ResolveDirectory(string fullPath, int budget)
    {
        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent)) return fullPath;
        string resolvedParent = ResolveDirectory(parent, budget);
        string candidate = Path.Combine(resolvedParent, Path.GetFileName(fullPath));
        var info = new DirectoryInfo(candidate);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0) return candidate;
        if (budget == 0) throw new IOException("Diagnostic export refused: directory link depth exceeded.");
        FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: false);
        if (target is null || string.IsNullOrEmpty(target.FullName))
            throw new IOException("Diagnostic export refused: unresolvable directory link.");
        return ResolveDirectory(target.FullName, budget - 1);
    }

    private static List<PlannedFile> PlanFiles(
        string destinationFull,
        IReadOnlyList<DiagnosticBootFile> supplied,
        bool includeAbl,
        bool includeLargeBoot)
    {
        var planned = new List<PlannedFile>();
        var seen = new HashSet<(string, string)>();
        foreach (DiagnosticBootFile? file in supplied)
        {
            if (file is null) continue;
            string? source = file.Source;
            string? role = file.Role;
            string? path = file.Path;
            if (source is null || role is null || path is null) continue;
            if (!AllowedSources.Contains(source)) continue;

            long cap;
            if (AblRoles.Contains(role))
            {
                if (!includeAbl) continue;
                cap = AblVbmetaCap;
            }
            else if (LargeBootRoles.Contains(role))
            {
                if (!includeLargeBoot) continue;
                cap = BootClassCap;
            }
            else
            {
                continue; // unrelated role
            }

            if (!seen.Add((source, role))) continue;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { continue; }
            if (string.Equals(full, destinationFull, StringComparison.OrdinalIgnoreCase)) continue;

            planned.Add(new PlannedFile(file with { Path = full }, cap));
        }
        return planned;
    }

    private static async Task<object> CopyAndRecordAsync(ZipArchive zip, PlannedFile plan, CancellationToken token)
    {
        DiagnosticBootFile file = plan.File;
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = file.Source,
            ["role"] = file.Role,
            ["capBytes"] = plan.Cap,
            ["expectedBytes"] = file.ExpectedBytes,
            ["expectedSha256"] = NormalizeExpectedHashOrNull(file.ExpectedSha256),
        };

        FileStream? input = null;
        try
        {
            token.ThrowIfCancellationRequested();

            if (!File.Exists(file.Path))
            {
                record["status"] = "missing";
                record["reason"] = "not-found";
                return record;
            }

            // Open first with FileShare.Read; verify metadata on the same handle.
            try
            {
                input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
            }
            catch (FileNotFoundException) { record["status"] = "missing"; record["reason"] = "not-found"; return record; }
            catch (DirectoryNotFoundException) { record["status"] = "missing"; record["reason"] = "not-found"; return record; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { record["status"] = "unreadable"; record["reason"] = "open-failed"; return record; }

            // Reparse checks on open handle's resolved full path.
            string resolved = input.Name;
            if (!IsPathSafe(resolved))
            {
                record["status"] = "rejected";
                record["reason"] = "reparse-or-unsafe-path";
                await input.DisposeAsync().ConfigureAwait(false);
                return record;
            }

            long length = input.Length;
            record["actualBytes"] = length;

            if (length > plan.Cap)
            {
                record["status"] = "skipped";
                record["reason"] = "size-cap-exceeded";
                await input.DisposeAsync().ConfigureAwait(false);
                return record;
            }
            if (file.ExpectedBytes is { } expBytes && expBytes != length)
            {
                record["status"] = "rejected";
                record["reason"] = "size-mismatch";
                await input.DisposeAsync().ConfigureAwait(false);
                return record;
            }

            string? normalizedExpected = NormalizeExpectedHashOrNull(file.ExpectedSha256);
            bool hashExpected = !string.IsNullOrEmpty(file.ExpectedSha256);

            // Hash the opened handle (nothing published yet).
            string computedHash;
            try
            {
                computedHash = await ComputeHashAsync(input, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                record["status"] = "unreadable";
                record["reason"] = "read-failed";
                await input.DisposeAsync().ConfigureAwait(false);
                return record;
            }
            record["computedSha256"] = computedHash;

            if (hashExpected)
            {
                if (normalizedExpected is null)
                {
                    record["status"] = "rejected";
                    record["reason"] = "malformed-expected-sha256";
                    await input.DisposeAsync().ConfigureAwait(false);
                    return record;
                }
                if (!string.Equals(normalizedExpected, computedHash, StringComparison.Ordinal))
                {
                    record["status"] = "rejected";
                    record["reason"] = "sha256-mismatch";
                    await input.DisposeAsync().ConfigureAwait(false);
                    return record;
                }
            }

            // All checks passed. Rewind and stream to a single new zip entry. From here on,
            // any failure aborts the whole export so a partial entry cannot publish.
            input.Position = 0;
            string entryName = "boot/" + SanitizeEntrySegment(file.Source) + "/" + SanitizeEntrySegment(file.Role) + ".bin";
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            long written;
            using (Stream outStream = entry.Open())
            {
                written = await CopyAndHashAsync(input, outStream, token).ConfigureAwait(false);
            }
            if (written != length)
                throw new IOException("copy-length-mismatch");

            // Verify again post-copy against the same handle's reported length.
            if (input.Length != length)
                throw new IOException("source-mutated-during-copy");

            record["archivePath"] = entryName;
            record["status"] = "copied";
            return record;
        }
        finally
        {
            if (input is not null) await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeHashAsync(FileStream stream, CancellationToken token)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            sha.AppendData(buffer, 0, read);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<long> CopyAndHashAsync(FileStream input, Stream output, CancellationToken token)
    {
        byte[] buffer = new byte[CopyBufferSize];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            total += read;
        }
        return total;
    }

    private static bool IsPathSafe(string fullPath)
    {
        try
        {
            // Leaf must not be a reparse point.
            var leaf = new FileInfo(fullPath);
            if ((leaf.Attributes & FileAttributes.ReparsePoint) != 0) return false;

            string? dir = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(dir))
            {
                var di = new DirectoryInfo(dir);
                if (!di.Exists) return false;
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                string? parent = Path.GetDirectoryName(dir);
                if (parent is null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) break;
                dir = parent;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeExpectedHashOrNull(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length != 64) return null;
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return null;
        }
        return value.ToLowerInvariant();
    }

    private static byte[] BuildManifest(
        DiagnosticExportRequest request,
        IReadOnlyList<string> redactions,
        IReadOnlyList<DiagnosticBootFile> supplied,
        List<PlannedFile> planned,
        List<object> copiedRecords)
    {
        var suppliedMeta = new List<object>();
        foreach (DiagnosticBootFile? f in supplied)
        {
            if (f is null) continue;
            bool validSource = f.Source is not null && AllowedSources.Contains(f.Source);
            bool validRole = (f.Role is not null) && (AblRoles.Contains(f.Role) || LargeBootRoles.Contains(f.Role));
            bool validHash = !string.IsNullOrEmpty(f.ExpectedSha256) && NormalizeExpectedHashOrNull(f.ExpectedSha256) is not null;
            bool malformedHash = !string.IsNullOrEmpty(f.ExpectedSha256) && !validHash;
            if (!validSource || !validRole) continue; // don't serialize metadata from invalid entries
            suppliedMeta.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["source"] = f.Source,
                ["role"] = f.Role,
                ["expectedBytes"] = f.ExpectedBytes,
                ["expectedSha256"] = validHash ? NormalizeExpectedHashOrNull(f.ExpectedSha256) : null,
                ["hashState"] = malformedHash ? "malformed" : (validHash ? "present" : "none"),
            });
        }

        // Requested-but-absent roles listed as missing.
        var provided = new HashSet<(string, string)>();
        foreach (object r in copiedRecords)
        {
            if (r is Dictionary<string, object?> d &&
                d.TryGetValue("source", out object? s) && s is string src &&
                d.TryGetValue("role", out object? ro) && ro is string role)
            {
                provided.Add((src, role));
            }
        }

        var missing = new List<object>();
        foreach (string role in AlwaysRequestedCapturedAblRoles)
        {
            if (!provided.Contains(("captured", role)))
            {
                missing.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["source"] = "captured",
                    ["role"] = role,
                    ["reason"] = "not-provided",
                });
            }
        }
        if (request.IncludeLargeBoot)
        {
            foreach (string role in LargeBootRoles.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!provided.Contains(("captured", role)))
                {
                    missing.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["source"] = "captured",
                        ["role"] = role,
                        ["reason"] = "not-provided",
                    });
                }
            }
        }

        var manifest = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = "freedominstaller.diagnostics.manifest.v1",
            ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["localOnly"] = true,
            ["deviceOperations"] = "none",
            ["includeAbl"] = request.IncludeAbl,
            ["includeLargeBoot"] = request.IncludeLargeBoot,
            ["redactions"] = redactions?.Count ?? 0,
            ["suppliedBootFiles"] = suppliedMeta,
            ["copiedFiles"] = copiedRecords,
            ["missingRoles"] = missing,
        };
        return JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream s = entry.Open();
        s.Write(content, 0, content.Length);
    }

    private static string SanitizeEntrySegment(string value)
    {
        if (string.IsNullOrEmpty(value)) return "unknown";
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        }
        return sb.ToString();
    }

    private static string RedactShapes(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        string result = input;

        // Windows home/user paths (e.g. C:\Users\alice\...).
        result = RedactWithBoundaries(result, @"[A-Za-z]:\\Users\\[^\\\s""'<>|]+", "[redacted]");
        // UNC paths.
        result = RedactWithBoundaries(result, @"\\\\[^\\\s""'<>|]+\\[^\\\s""'<>|]+", "[redacted]");
        // Unix home paths.
        result = RedactWithBoundaries(result, @"/home/[^/\s""'<>|]+(?:/[^\s""'<>|]*)?", "[redacted]");
        result = RedactWithBoundaries(result, @"/Users/[^/\s""'<>|]+(?:/[^\s""'<>|]*)?", "[redacted]");

        // Windows USB PnP instance IDs: redact only the trailing instance component,
        // keeping the hardware VID/PID/interface prefix.
        result = RedactWithBoundaries(result,
            @"(?i)\b(USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&[A-Z0-9_]+)*\\)[A-Z0-9_&.\-]+",
            "$1[redacted]");

        // Serial numbers.
        result = RedactWithBoundaries(result,
            @"(?i)\b(?:serial(?:\s*(?:number|no\.?))?|设备序列号)\b(?:\s*[:=#]\s*|\s+)([A-Za-z0-9\-]+)",
            "serial=[redacted]");

        // Auth / password / token / query / header fields.
        result = RedactWithBoundaries(result, @"(?i)\b(Authorization)\s*:\s*[^\r\n]+", "$1: [redacted]");
        result = RedactWithBoundaries(result,
            """(?i)(?<prefix>(?<keyQuote>["']?)\b(?:password|passwd|pwd|token|access_token|api_key|apikey|secret|auth|authorization)\b\k<keyQuote>\s*[:=]\s*)(?:(?<dq>")(?:\\[\s\S]|[^"\\])*"|(?<sq>')(?:\\[\s\S]|[^'\\])*'|[^\s&;"']+)""",
            "${prefix}${dq}${sq}[redacted]${dq}${sq}");

        return result;
    }

    private static string RedactWithBoundaries(string input, string pattern, string replacement)
    {
        // Compile inline to keep imports minimal and avoid a static Regex cache.
        var regex = new System.Text.RegularExpressions.Regex(pattern,
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(150));
        return regex.Replace(input, replacement);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
