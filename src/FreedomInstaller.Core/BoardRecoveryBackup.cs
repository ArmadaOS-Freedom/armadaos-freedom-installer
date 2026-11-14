using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace FreedomInstaller.Core;

public static class BoardRecoveryBackup
{
    private const long ImageBytes = 2L << 30;
    private const int HeaderBytes = 65536;
    private const int ManifestLimit = 8 << 20;

    /// <summary>
    /// Discovers the board recovery material and returns a lease over the uniquely new extraction directory
    /// this call created below <paramref name="durableParent"/>. The caller must <see cref="BoardRecoveryExtraction.Retain"/>
    /// only after the material is validated and handed over; otherwise <see cref="BoardRecoveryExtraction.Dispose"/>
    /// removes exactly that new directory.
    /// </summary>
    public static async Task<BoardRecoveryExtraction?> ReadDirectoryAsync(
        EdlClient client, string durableParent, string temporaryParent, CancellationToken token = default)
    {
        RequireParent(durableParent);
        RequireParent(temporaryParent);
        var work = Path.Combine(Path.GetFullPath(temporaryParent), "board-recovery-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var probe = await client.ProbeRawGeometryAsync(
                0, !client.HasInitialSaharaProof, Path.Combine(work, "geometry.bin"), token).ConfigureAwait(false);
            var geometry = probe.Geometry;
            if (geometry.TotalSectors <= 128) throw new InvalidDataException("The device has no valid GPT window.");
            var primary = await client.ReadAsync(0, 0, 64, Path.Combine(work, "primary.bin"), token).ConfigureAwait(false);
            var secondary = await client.ReadAsync(0, geometry.TotalSectors - 64, 64, Path.Combine(work, "secondary.bin"), token).ConfigureAwait(false);
            var table = OriginalGptImporter.Parse(
                await File.ReadAllBytesAsync(primary.FilePath, token).ConfigureAwait(false),
                await File.ReadAllBytesAsync(secondary.FilePath, token).ConfigureAwait(false),
                geometry.SectorSize, checked(geometry.TotalSectors * (long)geometry.SectorSize));
            var partition = FindRecoveryPartition(
                table,
                "Multiple recovery partitions were found.",
                "The recovery partition has invalid identity or size.");
            if (partition is null) return null;
            var image = await client.ReadAsync(0, partition.StartSector, partition.SectorCount,
                Path.Combine(work, "recovery.img"), token).ConfigureAwait(false);
            if (image.Length != ImageBytes)
                throw new InvalidDataException("The board recovery capture is incomplete.");
            var directory = await ExtractAsync(
                image.FilePath, client.Toolchain.Model, partition.PartUuid, durableParent, token).ConfigureAwait(false);
            return new BoardRecoveryExtraction(directory, durableParent);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<InternalBackupSet?> ReadAsync(
        EdlClient client, InternalBackupSet current, string durableParent, string temporaryParent,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(current);
        if (!ReferenceEquals(client.Binding, current.Binding))
            throw new InvalidDataException("Recovery and current capture belong to different device sessions.");
        var table = current.Tables[0];
        var partition = FindRecoveryPartition(
            table,
            "Multiple ARMADA_BACKUP partitions were found.",
            "The board recovery partition has invalid identity or geometry.");
        if (partition is null) return null;
        RequireParent(durableParent);
        RequireParent(temporaryParent);
        var work = Path.Combine(Path.GetFullPath(temporaryParent), "board-recovery-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var path = Path.Combine(work, "recovery.img");
            var capture = await client.ReadAsync(0, partition.StartSector, partition.SectorCount, path, token).ConfigureAwait(false);
            if (capture.Length != ImageBytes)
                throw new InvalidDataException("The board recovery capture is incomplete.");
            var directory = await ExtractAsync(path, current.Model, partition.PartUuid, durableParent, token).ConfigureAwait(false);

            // The extraction is owned as soon as it exists: any failure before the successful handoff below
            // releases the opened backup locks first and then removes only this newly created directory.
            var extraction = new BoardRecoveryExtraction(directory, durableParent);
            InternalBackupSet? recovered = null;
            try
            {
                recovered = await InternalInstallBackup.OpenRecoveryAsync(client, extraction.DirectoryPath, token: token).ConfigureAwait(false);
                ValidateSource(current, recovered);
                extraction.Retain();
                var result = recovered;
                recovered = null;
                return result;
            }
            catch
            {
                recovered?.Dispose();
                extraction.Dispose();
                throw;
            }
        }
        finally
        {
            // This unique directory contains only the temporary capture created above.
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void ValidateSourceForReuse(InternalBackupSet current, InternalBackupSet recovery)
        => ValidateSource(current, recovery);

    internal static void ValidateSource(InternalBackupSet current, InternalBackupSet recovery)
    {
        if (!ReferenceEquals(current.Binding, recovery.Binding) || current.Model != recovery.Model
            || !string.Equals(current.HardwareId, recovery.HardwareId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recovery material belongs to another device.");
        for (var lun = 0; lun < 6; lun++)
        {
            var before = recovery.Tables[lun];
            var now = current.Tables[lun];
            if (before.SectorSize != now.SectorSize || before.DiskBytes != now.DiskBytes
                || !string.Equals(before.DiskGuid, now.DiskGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovery material belongs to a different storage layout.");
        }
        var original = recovery.Tables[0];
        var userdata = original.Partitions.SingleOrDefault(p => p.Name == "userdata");
        if (userdata is null || checked(userdata.StartSector + userdata.SectorCount) != checked(original.LastUsableSector + 1)
            || original.Partitions.Any(p => p.Name.StartsWith("ARMADA_", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Recovery material does not contain a factory userdata layout.");
        foreach (var before in original.Partitions)
        {
            var now = current.Tables[0].Partitions.SingleOrDefault(p => p.Number == before.Number);
            if (now is null || now.Name != before.Name
                || !string.Equals(now.PartUuid, before.PartUuid, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(now.TypeGuid, before.TypeGuid, StringComparison.OrdinalIgnoreCase)
                || now.StartSector != before.StartSector
                || (before.Name != "userdata" && now.SectorCount != before.SectorCount)
                || (before.Name == "userdata" && now.SectorCount > before.SectorCount))
                throw new InvalidDataException("Recovery material does not match the current vendor partitions.");
        }
    }

    /// <summary>
    /// Applies the shared recovery partition identity and geometry gate to either reader's parsed table.
    /// Returns null when the table carries no <c>ARMADA_BACKUP</c> partition.
    /// </summary>
    private static GptPartition? FindRecoveryPartition(
        GptTable table, string multipleMessage, string invalidMessage)
    {
        var matches = table.Partitions.Where(p =>
            string.Equals(p.Name, "ARMADA_BACKUP", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new InvalidDataException(multipleMessage);
        var partition = matches[0];
        if (partition.Name != "ARMADA_BACKUP"
            || !Guid.TryParse(partition.PartUuid, out var uuid) || uuid == Guid.Empty
            || !string.Equals(partition.TypeGuid, "0FC63DAF-8483-4772-8E79-3D69D8477DE4", StringComparison.OrdinalIgnoreCase)
            || partition.SectorCount != ImageBytes / table.SectorSize
            || partition.StartSector < table.FirstUsableSector
            || checked(partition.StartSector + partition.SectorCount) > checked(table.LastUsableSector + 1))
            throw new InvalidDataException(invalidMessage);
        return partition;
    }

    internal static Task<string> ExtractAsync(
        string imagePath, string model, string expectedPartitionUuid, string durableParent, CancellationToken token)
        => Task.Run(() => Extract(imagePath, model, expectedPartitionUuid, durableParent, token), token);

    private static string Extract(
        string imagePath, string model, string expectedPartitionUuid, string durableParent, CancellationToken token)
    {
        RequireParent(durableParent);
        using var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var recovery = InternalInstallArtifacts.VerifyBoardArchive(image, model, token);
        var owned = recovery.GetProperty("ownedPartitionUuids");
        if (!Guid.TryParse(expectedPartitionUuid, out var expectedUuid) || expectedUuid == Guid.Empty
            || owned.GetArrayLength() != 3
            || !Guid.TryParse(owned[2].GetString(), out var backupUuid) || backupUuid != expectedUuid)
            throw new InvalidDataException("The archive does not own the current recovery partition.");
        image.Position = 0;
        var headerBytes = new byte[HeaderBytes];
        image.ReadExactly(headerBytes);
        using var header = JsonDocument.Parse(headerBytes);
        if (header.RootElement.GetProperty("schemaVersion").GetInt32() == 3
            && Guid.Parse(header.RootElement.GetProperty("configPartitionUuid").GetString()!) != expectedUuid)
            throw new InvalidDataException("The archive launcher descriptor names a different recovery partition.");

        var entries = recovery.GetProperty("entries").EnumerateArray().ToDictionary(
            e => e.GetProperty("role").GetString()!, e => e.Clone(), StringComparer.Ordinal);
        var directory = Path.Combine(Path.GetFullPath(durableParent), "board-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        var manifestPath = Path.Combine(directory, "backup-manifest.json");
        try
        {
            image.Position = HeaderBytes;
            using (var gzip = new GZipStream(image, CompressionMode.Decompress, leaveOpen: true))
            using (var tar = new TarReader(gzip))
            {
                _ = tar.GetNextEntry() ?? throw new InvalidDataException("Missing recovery manifest.");
                foreach (var claim in entries)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = tar.GetNextEntry() ?? throw new InvalidDataException("Truncated recovery archive.");
                    var expected = claim.Value;
                    if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null
                        || entry.Name != expected.GetProperty("path").GetString()
                        || entry.Length != expected.GetProperty("length").GetInt64())
                        throw new InvalidDataException("Recovery entry differs from its verified manifest.");
                    if (claim.Key is "post-gpt-primary" or "post-gpt-backup" or "installation-record") continue;
                    if (BackupPartitionPolicy.IsExcluded(claim.Key)) continue;
                    if (claim.Key == "pc-backup-manifest" && entry.Length > ManifestLimit)
                        throw new InvalidDataException("The archived PC manifest is too large.");
                    var path = claim.Key == "pc-backup-manifest" ? manifestPath
                        : Path.Combine(directory, Guid.NewGuid().ToString("N") + ".capture");
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        entry.DataStream.CopyTo(output);
                        output.Flush(true);
                    }
                    staged.Add(claim.Key, path);
                }
            }
            var manifestBytes = File.ReadAllBytes(manifestPath);
            var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            if (!string.Equals(manifestHash, recovery.GetProperty("pcBackupManifestSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The archived PC manifest hash is inconsistent.");
            var manifestNode = JsonNode.Parse(manifestBytes) ?? throw new InvalidDataException("Missing PC manifest.");
            var manifestFiles = manifestNode["files"]?.AsArray() ?? throw new InvalidDataException("Missing backup file list.");
            var excluded = manifestFiles.Where(f => BackupPartitionPolicy.IsExcluded(f?["role"]?.GetValue<string>())).ToArray();
            foreach (var item in excluded) manifestFiles.Remove(item);
            if (excluded.Length != 0)
            {
                File.Move(manifestPath, Path.Combine(directory, "archive-source-manifest.json"));
                manifestBytes = Encoding.UTF8.GetBytes(manifestNode.ToJsonString());
                using var rewritten = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                rewritten.Write(manifestBytes);
                rewritten.Flush(true);
                manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            }
            using var manifest = JsonDocument.Parse(manifestBytes);
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { manifestPath, manifestPath + ".sha256", Path.Combine(directory, "archive-source-manifest.json") };
            var seenRoles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var role = file.GetProperty("role").GetString()!;
                if (BackupPartitionPolicy.IsExcluded(role) || !seenRoles.Add(role)
                    || !entries.TryGetValue(role, out var claim) || !staged.TryGetValue(role, out var source)
                    || file.GetProperty("length").GetInt64() != claim.GetProperty("length").GetInt64()
                    || !string.Equals(file.GetProperty("sha256").GetString(), claim.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The PC manifest does not match the archived recovery files.");
                var destination = RecoveryPath(directory, file.GetProperty("relativePath").GetString()!);
                if (!destinations.Add(destination)) throw new InvalidDataException("Duplicate recovery destination.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination, overwrite: false);
            }
            if (seenRoles.Count != staged.Count - 1)
                throw new InvalidDataException("The recovery archive contains unclaimed captures.");
            using (var sidecar = new FileStream(manifestPath + ".sha256", FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                sidecar.Write(Encoding.ASCII.GetBytes(manifestHash + "\n"));
                sidecar.Flush(true);
            }
            return directory;
        }
        catch
        {
            // This newly created directory contains only this failed extraction.
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static string RecoveryPath(string directory, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')
            || parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException("The recovery manifest contains an unsafe relative path.");
        var path = Path.GetFullPath(Path.Combine(directory, Path.Combine(parts)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The recovery path leaves its directory.");
        return path;
    }

    private static void RequireParent(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("An existing, regular recovery parent directory is required.");
    }
}

/// <summary>
/// Owns exactly one freshly extracted board recovery directory created by
/// <see cref="BoardRecoveryBackup.ReadDirectoryAsync"/> or <see cref="BoardRecoveryBackup.ReadAsync"/>.
/// <para>
/// The directory path is immutable and the instance is created only by the factory. <see cref="Retain"/> marks a
/// successful handoff, after which <see cref="Dispose"/> never deletes anything. Without that handoff,
/// <see cref="Dispose"/> removes only the verified root this factory created below the chosen parent: it never
/// follows a reparse-point root, never deletes outside that parent and never touches pre-existing, cached or
/// user-selected backups.
/// </para>
/// </summary>
public sealed class BoardRecoveryExtraction : IDisposable
{
    private const string DirectoryPrefix = "board-recovery-";
    private const int Owned = 0;
    private const int Retained = 1;
    private const int Released = 2;

    private readonly string _directoryPath;
    private readonly string _parentPath;
    private int _state;

    /// <summary>Factory-only constructor; the directory must already be the unique one this factory created.</summary>
    internal BoardRecoveryExtraction(string directoryPath, string parentPath)
    {
        _directoryPath = Path.GetFullPath(directoryPath);
        _parentPath = Path.GetFullPath(parentPath);
    }

    /// <summary>Absolute path of the uniquely new extraction directory this lease owns.</summary>
    public string DirectoryPath => _directoryPath;

    /// <summary>
    /// Marks the extraction as successfully handed over so <see cref="Dispose"/> keeps it durable. Idempotent and
    /// without effect after <see cref="Dispose"/>.
    /// </summary>
    public void Retain() => Interlocked.CompareExchange(ref _state, Retained, Owned);

    /// <summary>
    /// Releases the lease. An unretained owned extraction is deleted; a retained one is left untouched.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, Released) != Owned) return;
        DeleteOwnedDirectory();
    }

    private void DeleteOwnedDirectory()
    {
        try
        {
            var parent = Path.TrimEndingDirectorySeparator(_parentPath);
            var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
                ? parent
                : parent + Path.DirectorySeparatorChar;
            var name = Path.GetFileName(_directoryPath);
            if (!_directoryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name.Substring(DirectoryPrefix.Length), "N", out _)
                || !Directory.Exists(_directoryPath)
                || (File.GetAttributes(_directoryPath) & FileAttributes.ReparsePoint) != 0)
                return;
            Directory.Delete(_directoryPath, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
