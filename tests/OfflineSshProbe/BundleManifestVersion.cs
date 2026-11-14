using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace OfflineSshProbe;

/// <summary>
/// Reads the <c>version</c> declared by the root <c>manifest.json</c> of the
/// local updater bundle without extracting it. The probe binds the coordinator
/// to the bundle bytes it uploads, so it must read that same file instead of
/// trusting the tablet or a hardcoded version.
/// </summary>
internal static class BundleManifestVersion
{
    private const string ManifestName = "manifest.json";
    private const int MaxBundleBytes = 256 * 1024 * 1024;
    private const int MaxMembers = 4096;
    private const int MaxMemberBytes = 128 * 1024 * 1024;
    private const int MaxManifestBytes = 4 * 1024 * 1024;

    internal static string Read(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        byte[] manifest;
        try
        {
            using var file = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > MaxBundleBytes)
            {
                throw new InvalidOperationException(
                    "The offline updater bundle '" + bundlePath + "' is empty or exceeds its size limit.");
            }

            using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
            using var tar = new TarReader(gzip, leaveOpen: true);
            manifest = ReadManifestMember(tar, bundlePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidOperationException(
                "The offline updater bundle '" + bundlePath + "' is not a readable tar.gz archive.", ex);
        }

        try
        {
            using var document = JsonDocument.Parse(manifest);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String ||
                version.GetString() is not { } text ||
                !IsDottedDecimalVersion(text))
            {
                throw new InvalidOperationException(
                    "The offline updater bundle '" + bundlePath +
                    "' manifest.json has no valid dotted decimal version.");
            }

            return text;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The offline updater bundle '" + bundlePath + "' manifest.json is not valid JSON.", ex);
        }
    }

    private static byte[] ReadManifestMember(TarReader tar, string bundlePath)
    {
        byte[]? manifest = null;
        var members = 0;
        while (tar.GetNextEntry() is { } entry)
        {
            if (++members > MaxMembers)
            {
                throw new InvalidOperationException(
                    "The offline updater bundle '" + bundlePath + "' has too many members.");
            }

            if (entry.Length > MaxMemberBytes)
            {
                throw new InvalidOperationException(
                    "The offline updater bundle '" + bundlePath + "' has a member that exceeds its size limit.");
            }

            if (!string.Equals(entry.Name, ManifestName, StringComparison.Ordinal))
                continue;

            if (manifest is not null)
            {
                throw new InvalidOperationException(
                    "The offline updater bundle '" + bundlePath + "' has more than one manifest.json.");
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                throw new InvalidOperationException(
                    "The offline updater bundle '" + bundlePath + "' manifest.json is not a regular file.");
            }

            if (entry.Length is <= 0 or > MaxManifestBytes)
            {
                throw new InvalidOperationException(
                    "The offline updater bundle '" + bundlePath + "' manifest.json size is out of bounds.");
            }

            var data = entry.DataStream
                ?? throw new InvalidOperationException(
                    "The offline updater bundle '" + bundlePath + "' manifest.json has no data.");

            var buffer = new byte[(int)entry.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = data.Read(buffer, read, buffer.Length - read);
                if (count == 0)
                {
                    throw new InvalidOperationException(
                        "The offline updater bundle '" + bundlePath + "' manifest.json is truncated.");
                }

                read += count;
            }

            manifest = buffer;
        }

        return manifest
            ?? throw new InvalidOperationException(
                "The offline updater bundle '" + bundlePath + "' has no manifest.json.");
    }

    private static bool IsDottedDecimalVersion(string value)
    {
        foreach (var segment in value.Split('.'))
        {
            if (segment.Length == 0 ||
                !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return true;
    }
}
