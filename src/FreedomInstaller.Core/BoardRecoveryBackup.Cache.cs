using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

/// <summary>
/// The small, bounded provenance record written inside a successfully retained board recovery directory.
/// <para>
/// It is never authority by itself: the next session still reads the live 64 KiB archive header, requires the
/// same model, recovery partition UUID and header hash, requires the saved recovery manifest to hash to the live
/// header's <c>recoveryManifestSha256</c>, and requires that manifest's <c>pcBackupManifestSha256</c> to match the
/// retained PC manifest. Only then is the retained directory handed to <see cref="InternalInstallBackup.OpenRecoveryAsync"/>,
/// which re-hashes every payload file and revalidates the device identity before <see cref="BoardRecoveryBackup.ValidateSource"/>
/// checks the current layout.
/// </para>
/// </summary>
internal sealed record BoardRecoveryCacheReceipt(
    string Model,
    string PartitionUuid,
    string HeaderSha256,
    string RecoveryManifestSha256,
    string PcManifestSha256);

/// <summary>
/// The result of one verified board archive extraction: the new directory plus the exact bounded bytes and
/// hashes a cache proof needs. The live header hash, the recovery manifest hash and the archive's own PC
/// manifest hash are all derived from the verified archive, never from a caller claim.
/// </summary>
internal sealed record BoardRecoveryMaterial(
    string Directory,
    string Model,
    string PartitionUuid,
    string HeaderSha256,
    string RecoveryManifestSha256,
    string PcManifestSha256,
    byte[] RecoveryManifestPayload);

public static partial class BoardRecoveryBackup
{
    private const string CacheReceiptFileName = "board-recovery-cache.json";
    private const string CacheManifestFileName = "board-recovery-cache.manifest";
    private const string CacheDirectoryPrefix = "board-recovery-";
    private const string CachePcManifestFileName = "backup-manifest.json";
    private const string CachePcManifestSourceName = "archive-source-manifest.json";
    private const int CacheReceiptMaxBytes = 4096;
    private const int CacheHeaderBytes = 65536;

    // ---------------------------------------------------------------- proof creation

    private static byte[] BuildCacheReceipt(BoardRecoveryMaterial material)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteString("model", material.Model);
            json.WriteString("partitionUuid", material.PartitionUuid);
            json.WriteString("headerSha256", material.HeaderSha256);
            json.WriteString("recoveryManifestSha256", material.RecoveryManifestSha256);
            json.WriteString("pcManifestSha256", material.PcManifestSha256);
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Writes the two bounded cache files of a validated handoff. A failure only means future sessions fall back
    /// to the full archive capture and extraction; the retained directory and every payload file stay untouched,
    /// and no I/O failure escapes this method.
    /// <para>
    /// Each file is created exclusively, and ownership is recorded immediately after that open succeeds, so a
    /// failure during the write or flush still removes only the file this call created. A pre-existing marker is
    /// never opened, truncated or deleted: its collision leaves both markers byte-for-byte as they were.
    /// </para>
    /// </summary>
    internal static void TryWriteCacheProof(string directory, byte[] receipt, byte[] manifest)
    {
        string receiptPath = Path.Combine(directory, CacheReceiptFileName);
        string manifestPath = Path.Combine(directory, CacheManifestFileName);

        bool manifestOwned = false;
        bool receiptOwned = false;

        try
        {
            using (var stream = CreateBoundedProofFile(manifestPath))
            {
                manifestOwned = true;
                stream.Write(manifest, 0, manifest.Length);
                stream.Flush(flushToDisk: true);
            }

            using (var stream = CreateBoundedProofFile(receiptPath))
            {
                receiptOwned = true;
                stream.Write(receipt, 0, receipt.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteOwnedProof(manifestPath, manifestOwned);
            TryDeleteOwnedProof(receiptPath, receiptOwned);
        }
    }

    private static FileStream CreateBoundedProofFile(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096);

    /// <summary>Removes one proof file only when this call created it; a pre-existing marker is never touched.</summary>
    private static void TryDeleteOwnedProof(string path, bool owned)
    {
        if (!owned) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ cache borrowing

    /// <summary>
    /// Tries to hand back a retained backup set for <paramref name="current"/> without reading the 2 GiB archive
    /// again. Every failed proof, hash, identity or layout check simply moves on; the pre-existing directories are
    /// only ever read, never rewritten or deleted.
    /// </summary>
    private static async Task<InternalBackupSet?> TryBorrowRecoveryAsync(
        EdlClient client,
        InternalBackupSet current,
        GptPartition partition,
        string durableParent,
        string workDirectory,
        CancellationToken token)
    {
        var candidates = CollectCacheCandidates(durableParent, current.Model, partition.PartUuid);
        if (candidates.Count == 0) return null;

        var live = await ReadLiveHeaderAsync(
            client, partition.StartSector, current.Tables[0].SectorSize, workDirectory, token).ConfigureAwait(false);

        foreach (var (directory, receipt) in candidates)
        {
            token.ThrowIfCancellationRequested();

            if (!TryBindLiveHeader(live.Bytes, live.Sha256, receipt, current.Model, out var header)) continue;
            if (!ValidateCachedProof(directory, receipt, header)) continue;

            InternalBackupSet? recovered = null;
            try
            {
                recovered = await InternalInstallBackup
                    .OpenRecoveryAsync(client, directory, token: token).ConfigureAwait(false);
                ValidateSource(current, recovered);
                var result = recovered;
                recovered = null;
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                recovered?.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// The lease counterpart of <see cref="TryBorrowRecoveryAsync"/>: the returned lease owns nothing, is born
    /// retained and can never delete the borrowed directory, even when the caller disposes it without a handoff.
    /// </summary>
    private static async Task<BoardRecoveryExtraction?> TryBorrowDirectoryAsync(
        EdlClient client,
        string model,
        string hardwareId,
        GptPartition partition,
        GptTable liveLun0,
        string durableParent,
        string workDirectory,
        CancellationToken token)
    {
        var candidates = CollectCacheCandidates(durableParent, model, partition.PartUuid);
        if (candidates.Count == 0) return null;

        var live = await ReadLiveHeaderAsync(
            client, partition.StartSector, liveLun0.SectorSize, workDirectory, token).ConfigureAwait(false);

        foreach (var (directory, receipt) in candidates)
        {
            token.ThrowIfCancellationRequested();

            if (!TryBindLiveHeader(live.Bytes, live.Sha256, receipt, model, out var header)) continue;
            if (!ValidateCachedProof(directory, receipt, header)) continue;

            InternalBackupSet? recovered = null;
            try
            {
                recovered = await InternalInstallBackup
                    .OpenRecoveryAsync(client, directory, token: token).ConfigureAwait(false);
                ValidateLun0Layout(model, hardwareId, liveLun0, recovered);
                return BoardRecoveryExtraction.Borrowed(directory, durableParent);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // A candidate that fails any step is ignored; the next one or the full capture takes over.
            }
            finally
            {
                recovered?.Dispose();
            }
        }

        return null;
    }

    private static async Task<(byte[] Bytes, string Sha256)> ReadLiveHeaderAsync(
        EdlClient client,
        long archiveStartSector,
        int sectorSize,
        string workDirectory,
        CancellationToken token)
    {
        int sectors = sectorSize <= 0 ? 0 : CacheHeaderBytes / sectorSize;
        if (sectors <= 0 || checked(sectors * sectorSize) != CacheHeaderBytes)
        {
            throw new InvalidDataException(
                $"A {sectorSize}-byte sector cannot carry the fixed {CacheHeaderBytes}-byte board header.");
        }

        string path = Path.Combine(
            Path.GetFullPath(workDirectory), "board-live-header-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            var evidence = await client
                .ReadAsync(0, archiveStartSector, sectors, path, token).ConfigureAwait(false);
            if (evidence.Length != CacheHeaderBytes)
            {
                throw new InvalidDataException(
                    $"The live board header is {evidence.Length} bytes, not the fixed {CacheHeaderBytes}.");
            }

            byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (bytes.Length != CacheHeaderBytes)
            {
                throw new InvalidDataException(
                    $"The live board header is {bytes.Length} bytes, not the fixed {CacheHeaderBytes}.");
            }

            return (bytes, Sha256Text.Hex(bytes).ToUpperInvariant());
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Parses the live header with the same pure verifier the full archive check uses and binds it to the receipt.
    /// The returned header is only a candidate: the cached recovery manifest and PC manifest must still descend
    /// from it and the cached directory must still pass the full recovery open.
    /// </summary>
    private static bool TryBindLiveHeader(
        byte[] liveHeader,
        string liveHeaderSha256,
        BoardRecoveryCacheReceipt receipt,
        string model,
        out InternalInstallArtifacts.BoardArchiveHeader header)
    {
        header = null!;

        if (!string.Equals(receipt.Model, model, StringComparison.Ordinal)
            || !IsSha256Hex(receipt.HeaderSha256)
            || !string.Equals(receipt.HeaderSha256, liveHeaderSha256, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(receipt.PartitionUuid, out var receiptUuid))
        {
            return false;
        }

        InternalInstallArtifacts.BoardArchiveHeader parsed;
        try
        {
            parsed = InternalInstallArtifacts.ParseBoardArchiveHeader(liveHeader, model);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (parsed.SchemaVersion != 3 || parsed.ConfigPartitionUuid != receiptUuid) return false;

        header = parsed;
        return true;
    }

    /// <summary>
    /// The provenance chain from the live header down to the retained PC manifest:
    /// live header → cached recovery manifest bytes → <c>pcBackupManifestSha256</c> → cached PC manifest.
    /// A forged PC manifest with a matching sidecar changes the last hash and is rejected here.
    /// </summary>
    private static bool ValidateCachedProof(
        string directory,
        BoardRecoveryCacheReceipt receipt,
        InternalInstallArtifacts.BoardArchiveHeader header)
    {
        if (!string.Equals(
                receipt.RecoveryManifestSha256,
                header.RecoveryManifestSha256,
                StringComparison.OrdinalIgnoreCase)
            || !IsSha256Hex(receipt.RecoveryManifestSha256)
            || !IsSha256Hex(receipt.PcManifestSha256))
        {
            return false;
        }

        string manifestPath = Path.Combine(directory, CacheManifestFileName);
        if (!TryReadBoundedFile(manifestPath, CacheHeaderBytes, out byte[] manifest))
        {
            return false;
        }

        if (manifest.Length != CacheHeaderBytes
            || !string.Equals(
                Sha256Text.Hex(manifest).ToUpperInvariant(),
                receipt.RecoveryManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(manifest);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || !schemaVersion.TryGetInt32(out int version) || version != 2
                || !string.Equals(ReadString(root, "kind"), "armada-board-recovery", StringComparison.Ordinal)
                || !string.Equals(
                    ReadString(root, "installationId"), header.InstallationIdText, StringComparison.Ordinal)
                || !string.Equals(ReadString(root, "planSha256"), header.PlanSha256, StringComparison.Ordinal)
                || !string.Equals(
                    ReadString(root, "pcBackupManifestSha256"),
                    receipt.PcManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return ValidateCachedPcManifest(directory, receipt.PcManifestSha256);
    }

    private static bool ValidateCachedPcManifest(string directory, string archivePcManifestSha256)
    {
        string manifestPath = Path.Combine(directory, CachePcManifestFileName);
        string sourcePath = Path.Combine(directory, CachePcManifestSourceName);

        try
        {
            byte[] actual = File.ReadAllBytes(manifestPath);
            string actualSha256 = Sha256Text.Hex(actual).ToUpperInvariant();

            if (File.Exists(sourcePath))
            {
                byte[] source = File.ReadAllBytes(sourcePath);
                if (!string.Equals(
                        Sha256Text.Hex(source).ToUpperInvariant(),
                        archivePcManifestSha256,
                        StringComparison.OrdinalIgnoreCase)
                    || !actual.AsSpan().SequenceEqual(RewritePcManifestBytes(source)))
                {
                    return false;
                }
            }
            else if (!string.Equals(
                         actualSha256, archivePcManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ReadSidecar(manifestPath + ".sha256", out string sidecar)
                   && string.Equals(sidecar, actualSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Reapplies the production exclusion filter to an archived PC manifest, exactly as extraction did.</summary>
    private static byte[] RewritePcManifestBytes(byte[] source)
    {
        var node = JsonNode.Parse(source) ?? throw new InvalidDataException("Missing PC manifest.");
        var files = node["files"]?.AsArray() ?? throw new InvalidDataException("Missing backup file list.");
        var excluded = new List<JsonNode?>();
        foreach (var file in files)
        {
            if (BackupPartitionPolicy.IsExcluded(file?["role"]?.GetValue<string>()))
            {
                excluded.Add(file);
            }
        }

        foreach (var file in excluded) files.Remove(file);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    // -------------------------------------------------------------- receipt and candidates

    private static List<(string Directory, BoardRecoveryCacheReceipt Receipt)> CollectCacheCandidates(
        string durableParent, string model, string partitionUuid)
    {
        var candidates = new List<(string, BoardRecoveryCacheReceipt)>();
        if (!Guid.TryParse(partitionUuid, out var expectedUuid)) return candidates;

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(
                         durableParent, CacheDirectoryPrefix + "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(directory);
                if (name.Length != CacheDirectoryPrefix.Length + 32
                    || !Guid.TryParseExact(
                        name.Substring(CacheDirectoryPrefix.Length), "N", out _))
                {
                    continue;
                }

                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;

                string receiptPath = Path.Combine(directory, CacheReceiptFileName);
                if (!TryReadCacheReceipt(receiptPath, out var receipt)) continue;

                if (!string.Equals(receipt.Model, model, StringComparison.Ordinal)
                    || !Guid.TryParse(receipt.PartitionUuid, out var receiptUuid)
                    || receiptUuid != expectedUuid)
                {
                    continue;
                }

                candidates.Add((directory, receipt));
            }
        }
        catch (IOException)
        {
            return candidates;
        }
        catch (UnauthorizedAccessException)
        {
            return candidates;
        }

        candidates.Sort((left, right) => string.CompareOrdinal(left.Item1, right.Item1));
        return candidates;
    }

    private static bool TryReadCacheReceipt(string path, out BoardRecoveryCacheReceipt receipt)
    {
        receipt = null!;

        if (!TryReadBoundedFile(path, CacheReceiptMaxBytes, out byte[] bytes)) return false;

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            // A syntactically valid receipt that carries the wrong JSON kind for schemaVersion is still an
            // invalid receipt: TryGetInt32 would throw InvalidOperationException out of this optional-cache
            // probe, so the kind is rejected here exactly like any other failed receipt check.
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out int version) || version != 1)
            {
                return false;
            }

            string model = ReadString(root, "model");
            string partitionUuid = ReadString(root, "partitionUuid");
            string headerSha256 = ReadString(root, "headerSha256");
            string recoveryManifestSha256 = ReadString(root, "recoveryManifestSha256");
            string pcManifestSha256 = ReadString(root, "pcManifestSha256");

            if (model.Length == 0
                || !Guid.TryParse(partitionUuid, out _)
                || !IsSha256Hex(headerSha256)
                || !IsSha256Hex(recoveryManifestSha256)
                || !IsSha256Hex(pcManifestSha256))
            {
                return false;
            }

            receipt = new BoardRecoveryCacheReceipt(
                model, partitionUuid, headerSha256, recoveryManifestSha256, pcManifestSha256);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------- small helpers

    private static bool TryReadBoundedFile(string path, int maximumBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes
                || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
            return bytes.Length <= maximumBytes;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadSidecar(string path, out string value)
    {
        value = string.Empty;
        try
        {
            if (!File.Exists(path)) return false;
            value = File.ReadAllText(path, Encoding.ASCII).Trim();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSha256Hex(string? value) => Sha256Text.IsHex(value);
}
