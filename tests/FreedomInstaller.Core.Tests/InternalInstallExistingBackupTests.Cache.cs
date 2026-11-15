using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// ISSUE38 cross-session reuse: once a board recovery handoff was fully validated and retained, a later session
/// that proves the same device, the same recovery partition identity and the same live 64 KiB archive header may
/// borrow that already extracted directory instead of reading the 2 GiB USB capture again and extracting it a
/// second time.
/// <para>
/// Every case prepares one real 2 GiB board archive through the production factories, retains one extraction and
/// then opens a fresh <see cref="EdlClient"/> over a freshly captured set. The assertions are only about what a
/// session does: which sector ranges it reads, which directory it returns and which files still exist afterwards.
/// Apart from the receipt-kind and proof-collision cases, which corrupt one marker on purpose, cache markers are
/// never inspected and no file-name pattern is banned.
/// </para>
/// </summary>
public sealed partial class InternalInstallExistingBackupTests
{
    private const long BoardHeaderBytes = 65536;
    private const int ChainAlignmentSectors = 256;
    private const string LinuxDataGuid = "0FC63DAF-8483-4772-8E79-3D69D8477DE4";

    [Fact]
    public async Task ReadAgain_second_session_borrows_the_retained_directory_without_a_new_archive_capture()
    {
        const string Name =
            nameof(ReadAgain_second_session_borrows_the_retained_directory_without_a_new_archive_capture);
        RetainedRecoveryMaterial material = await CreateRetainedRecoveryMaterialAsync(Name);
        string directory;
        string manifestPath;
        string manifestSha256;

        RetainedSession first = await CreateRetainedSessionAsync(material, Name + "-first");
        using (first.Current)
        {
            string scratch = _fixture.NewCaseDirectory(Name + "-first-scratch");
            using (InternalBackupSet? recovered = await BoardRecoveryBackup.ReadAsync(
                       first.Client, first.Current, material.DurableParent, scratch))
            {
                Assert.NotNull(recovered);
                InternalBackupSet retained = recovered!;
                directory = retained.DirectoryPath;
                manifestPath = retained.ManifestPath;
                manifestSha256 = retained.ManifestSha256;
                Assert.Equal(51, retained.Files.Count);
                Assert.True(Directory.Exists(directory));
                Assert.Equal(manifestSha256, HashFile(manifestPath), StringComparer.OrdinalIgnoreCase);

                // The first session still takes the whole 2 GiB capture once.
                Assert.Contains(first.Runner.Reads, read =>
                    read.Lun == 0 && read.StartSector == first.ArchiveStart && read.SectorCount == first.ArchiveSectors);
            }
        }

        Assert.Single(Directory.GetDirectories(material.DurableParent, "board-recovery-*"));

        RetainedSession second = await CreateRetainedSessionAsync(material, Name + "-second");
        using (second.Current)
        {
            string scratch = _fixture.NewCaseDirectory(Name + "-second-scratch");
            using (InternalBackupSet? borrowed = await BoardRecoveryBackup.ReadAsync(
                       second.Client, second.Current, material.DurableParent, scratch))
            {
                Assert.NotNull(borrowed);
                InternalBackupSet borrowedSet = borrowed!;
                Assert.Equal(directory, borrowedSet.DirectoryPath, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(manifestPath, borrowedSet.ManifestPath, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(manifestSha256, borrowedSet.ManifestSha256, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(51, borrowedSet.Files.Count);

                // The second session proves the archive with a 64 KiB header read, never with the whole capture.
                AssertRetainedArchiveWasNotCapturedAgain(second.Runner, second.ArchiveStart, second.ArchiveSectors);
                Assert.Empty(second.Runner.Writes);
                Assert.Equal(0, second.Runner.ResetCount);
            }
        }

        // Disposing the borrowed set releases only its locks: the retained directory and its files stay.
        Assert.Single(Directory.GetDirectories(material.DurableParent, "board-recovery-*"));
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(Path.Combine(directory, "misc.bin")));
        Assert.Equal(manifestSha256, HashFile(manifestPath), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadDirectoryAgain_second_lease_borrows_the_retained_directory_and_dispose_keeps_it()
    {
        const string Name =
            nameof(ReadDirectoryAgain_second_lease_borrows_the_retained_directory_and_dispose_keeps_it);
        RetainedRecoveryMaterial material = await CreateRetainedRecoveryMaterialAsync(Name);
        string directory;

        RetainedSession first = await CreateRetainedSessionAsync(material, Name + "-first");
        using (first.Current)
        {
            string scratch = _fixture.NewCaseDirectory(Name + "-first-scratch");
            using (BoardRecoveryExtraction lease = (await BoardRecoveryBackup.ReadDirectoryAsync(
                       first.Client, material.DurableParent, scratch))!)
            {
                directory = lease.DirectoryPath;
                Assert.True(
                    directory.StartsWith(material.DurableParent, StringComparison.OrdinalIgnoreCase),
                    "the extraction must live below the chosen durable parent");
                lease.Retain();
            }

            Assert.True(Directory.Exists(directory));
            Assert.Contains(first.Runner.Reads, read =>
                read.Lun == 0 && read.StartSector == first.ArchiveStart && read.SectorCount == first.ArchiveSectors);
        }

        string manifestPath = Path.Combine(directory, ManifestFileName);
        string manifestSha256 = HashFile(manifestPath);
        RetainedSession second = await CreateRetainedSessionAsync(material, Name + "-second");
        using (second.Current)
        {
            string scratch = _fixture.NewCaseDirectory(Name + "-second-scratch");
            using (BoardRecoveryExtraction borrowed = (await BoardRecoveryBackup.ReadDirectoryAsync(
                       second.Client, material.DurableParent, scratch))!)
            {
                Assert.Equal(directory, borrowed.DirectoryPath, StringComparer.OrdinalIgnoreCase);
                AssertRetainedArchiveWasNotCapturedAgain(second.Runner, second.ArchiveStart, second.ArchiveSectors);

                // This lease is never retained; disposing it must still leave the pre-existing directory alone.
            }
        }

        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(manifestPath));
        Assert.Equal(manifestSha256, HashFile(manifestPath), StringComparer.OrdinalIgnoreCase);
        Assert.Single(Directory.GetDirectories(material.DurableParent, "board-recovery-*"));
        Assert.Empty(second.Runner.Writes);
        Assert.Equal(0, second.Runner.ResetCount);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    public async Task InvalidCachedMaterial_is_never_reported_as_the_reusable_backup(string variant)
    {
        string name = nameof(InvalidCachedMaterial_is_never_reported_as_the_reusable_backup) + "-" + variant;
        RetainedRecoveryMaterial material = await CreateRetainedRecoveryMaterialAsync(name);
        string directory;
        string corruptPayload;

        RetainedSession first = await CreateRetainedSessionAsync(material, name + "-first");
        using (first.Current)
        {
            string scratch = _fixture.NewCaseDirectory(name + "-first-scratch");
            using (InternalBackupSet? recovered = await BoardRecoveryBackup.ReadAsync(
                       first.Client, first.Current, material.DurableParent, scratch))
            {
                Assert.NotNull(recovered);
                directory = recovered!.DirectoryPath;
                corruptPayload = Path.Combine(directory, "misc.bin");
            }
        }

        if (string.Equals(variant, "corrupt", StringComparison.Ordinal))
        {
            FlipByte(corruptPayload, 0);
        }
        else
        {
            File.Delete(Path.Combine(directory, ManifestFileName));
        }

        string corruptPayloadSha256 = HashFile(corruptPayload);
        RetainedSession second = await CreateRetainedSessionAsync(material, name + "-second");
        InternalBackupSet? reused = null;
        Exception? error = null;
        using (second.Current)
        {
            string scratch = _fixture.NewCaseDirectory(name + "-second-scratch");
            try
            {
                reused = await BoardRecoveryBackup.ReadAsync(
                    second.Client, second.Current, material.DurableParent, scratch);
            }
            catch (Exception exception)
            {
                error = exception;
            }
        }

        using (reused)
        {
            if (reused is null)
            {
                // A refusal is the first allowed outcome: the cache is simply not trusted.
                Assert.NotNull(error);
                Assert.IsNotType<NotImplementedException>(error);
            }
            else
            {
                // A full read and a fresh extraction are the other allowed outcome; the invalid cache is never it.
                Assert.NotEqual(directory, reused.DirectoryPath, StringComparer.OrdinalIgnoreCase);
                Assert.False(
                    reused.ManifestPath.StartsWith(
                        directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "the invalid cached directory must never be handed back as the reusable backup");
                Assert.True(File.Exists(reused.ManifestPath));
            }
        }

        // Neither outcome may delete or rewrite the pre-existing, invalid cached material.
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(corruptPayload));
        Assert.Equal(corruptPayloadSha256, HashFile(corruptPayload), StringComparer.OrdinalIgnoreCase);
        if (string.Equals(variant, "corrupt", StringComparison.Ordinal))
        {
            Assert.True(File.Exists(Path.Combine(directory, ManifestFileName)));
        }
        else
        {
            Assert.False(File.Exists(Path.Combine(directory, ManifestFileName)));
        }

        Assert.Empty(second.Runner.Writes);
        Assert.Equal(0, second.Runner.ResetCount);
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("partUuid")]
    [InlineData("header")]
    [InlineData("pcManifest")]
    public async Task ChangedDeviceIdentityOrLayoutOrLiveHeader_never_reuses_the_retained_material(string variant)
    {
        string name = nameof(ChangedDeviceIdentityOrLayoutOrLiveHeader_never_reuses_the_retained_material) + "-" + variant;
        RetainedRecoveryMaterial material = await CreateRetainedRecoveryMaterialAsync(name);
        string directory;
        string manifestSha256;

        RetainedSession first = await CreateRetainedSessionAsync(material, name + "-first");
        using (first.Current)
        {
            string scratch = _fixture.NewCaseDirectory(name + "-first-scratch");
            using (InternalBackupSet? recovered = await BoardRecoveryBackup.ReadAsync(
                       first.Client, first.Current, material.DurableParent, scratch))
            {
                Assert.NotNull(recovered);
                InternalBackupSet retained = recovered!;
                directory = retained.DirectoryPath;
                manifestSha256 = retained.ManifestSha256;
            }
        }

        string serial = string.Equals(variant, "serial", StringComparison.Ordinal)
            ? "OTHER_DEVICE"
            : "SN0123456789";
        string expectedManifestSha256 = manifestSha256;
        if (string.Equals(variant, "pcManifest", StringComparison.Ordinal))
        {
            // The cached manifest and its sidecar stay internally consistent; only the live archive's own
            // pcBackupManifestSha256 still names the original, so this must never be borrowed again.
            expectedManifestSha256 = TamperCachedPcManifest(directory);
        }

        string? changedBackupUuid = string.Equals(variant, "partUuid", StringComparison.Ordinal)
            ? Guid.NewGuid().ToString()
            : null;
        if (string.Equals(variant, "header", StringComparison.Ordinal))
        {
            // A different live header can never match the retained metadata, and it also breaks the archive.
            WriteHeaderBytes(material.ArchivePath, (byte)0x20);
        }

        RetainedSession second = await CreateRetainedSessionAsync(
            material, name + "-second", changedBackupUuid, serial);
        InternalBackupSet? reused = null;
        Exception? error = null;
        using (second.Current)
        {
            string scratch = _fixture.NewCaseDirectory(name + "-second-scratch");
            try
            {
                reused = await BoardRecoveryBackup.ReadAsync(
                    second.Client, second.Current, material.DurableParent, scratch);
            }
            catch (Exception exception)
            {
                error = exception;
            }
        }

        using (reused)
        {
            if (reused is null)
            {
                Assert.NotNull(error);
                Assert.IsNotType<NotImplementedException>(error);
            }
            else
            {
                Assert.NotEqual(directory, reused.DirectoryPath, StringComparer.OrdinalIgnoreCase);
                if (!string.Equals(variant, "pcManifest", StringComparison.Ordinal))
                {
                    // A fresh extraction carries the same archive manifest, so only the directory proves that
                    // the tampered cache was not borrowed in that case.
                    Assert.NotEqual(manifestSha256, reused.ManifestSha256, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        Assert.True(Directory.Exists(directory));
        Assert.Equal(
            expectedManifestSha256,
            HashFile(Path.Combine(directory, ManifestFileName)),
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(second.Runner.Writes);
        Assert.Equal(0, second.Runner.ResetCount);
    }

    // --------------------------------------------------------------------------- cache fixtures

    /// <summary>
    /// One real 2 GiB board archive prepared through the production factories, plus the factory table it was
    /// built from and the durable parent a later session extracts below.
    /// </summary>
    private sealed record RetainedRecoveryMaterial(
        IReadOnlyDictionary<string, VerifiedEdlFile> Sources,
        GptTable FactoryTable,
        string ArchivePath,
        string BackupUuid,
        string DurableParent);

    /// <summary>One fresh device session: its own runner, client, capture, retained layout and archive extent.</summary>
    private sealed record RetainedSession(
        IB001FakeEdlRunner Runner,
        EdlClient Client,
        InternalBackupSet Current,
        long ArchiveStart,
        long ArchiveSectors);

    private async Task<RetainedRecoveryMaterial> CreateRetainedRecoveryMaterialAsync(string name)
    {
        string sourceDirectory = _fixture.NewCaseDirectory(name + "-sources");
        IReadOnlyDictionary<string, VerifiedEdlFile> sources = CreateDirectSources(sourceDirectory);

        IB001SyntheticDevice device = IB001SyntheticDevice.CreateDirect(
            _fixture.NewCaseDirectory(name + "-factory-backing"), sources);
        EdlClient client = NewClient(new IB001FakeEdlRunner(device), NewBinding());

        GptTable factoryTable;
        string archivePath;
        string backupUuid;
        using (InternalBackupSet factory = await InternalInstallBackup.CreateAsync(
                   client, _fixture.NewCaseDirectory(name + "-factory-capture")))
        using (InternalArtifactSet artifacts = await InternalInstallArtifacts.PrepareAsync(
                   factory,
                   48,
                   new PackageStorage(1L << 30, 8L << 30, 4L << 30, 1L << 30),
                   _fixture.NewCaseDirectory(name + "-artifacts")))
        {
            factoryTable = factory.Tables[0];
            archivePath = artifacts.BoardSummary.FilePath;
            backupUuid = artifacts.BackupPartition.PartUuid;
        }

        Assert.Equal(BoardArchiveBytes, new FileInfo(archivePath).Length);
        return new RetainedRecoveryMaterial(
            sources, factoryTable, archivePath, backupUuid, _fixture.NewCaseDirectory(name + "-durable"));
    }

    private async Task<RetainedSession> CreateRetainedSessionAsync(
        RetainedRecoveryMaterial material,
        string name,
        string? backupUuid = null,
        string serial = "SN0123456789")
    {
        (IB001SyntheticDevice device, long archiveStart, long archiveSectors) = CreateRetainedDevice(
            material,
            _fixture.NewCaseDirectory(name + "-backing"),
            _fixture.NewCaseDirectory(name + "-overlay"),
            backupUuid ?? material.BackupUuid);
        IB001FakeEdlRunner runner = new(device);
        runner.EmitGeometryOnSingleSectorRead = true;
        EdlClient client = NewClient(runner, NewBinding(serial: serial));
        InternalBackupSet current = await InternalInstallBackup.CreateAsync(
            client, _fixture.NewCaseDirectory(name + "-capture"));
        return new RetainedSession(runner, client, current, archiveStart, archiveSectors);
    }

    /// <summary>
    /// Builds the current-device layout the production reader meets after a retained install: the factory table
    /// with a shrunk userdata and one appended <c>ARMADA_BACKUP</c> entry that already holds the archive bytes.
    /// </summary>
    private static (IB001SyntheticDevice Device, long ArchiveStart, long ArchiveSectors) CreateRetainedDevice(
        RetainedRecoveryMaterial material,
        string backing,
        string overlayDirectory,
        string backupUuid)
    {
        GptTable factory = material.FactoryTable;
        IB001SyntheticDevice device = IB001SyntheticDevice.CreateDirect(backing, material.Sources);

        long archiveSectors = BoardArchiveBytes / factory.SectorSize;
        long retainedEnd = (factory.LastUsableSector + 1) / ChainAlignmentSectors * ChainAlignmentSectors;
        long retainedStart = retainedEnd - archiveSectors;
        if (retainedStart <= factory.FirstUsableSector)
        {
            throw new InvalidOperationException("The synthetic LUN0 layout is too small for the retained archive.");
        }

        GptPartitionSpec[] specs = factory.Partitions
            .Select(partition => new GptPartitionSpec(
                partition.Number,
                partition.Name,
                partition.TypeGuid,
                partition.PartUuid,
                partition.StartSector,
                partition.Name == "userdata" ? retainedStart - partition.StartSector : partition.SectorCount,
                partition.Attributes))
            .Append(new GptPartitionSpec(
                factory.Partitions.Max(partition => partition.Number) + 1,
                "ARMADA_BACKUP",
                LinuxDataGuid,
                backupUuid,
                retainedStart,
                archiveSectors,
                1UL << 60))
            .ToArray();

        GptImage retainedImage = new(
            factory.SectorSize,
            factory.DiskBytes,
            factory.DiskGuid,
            factory.FirstUsableSector,
            factory.LastUsableSector,
            specs);

        long captureBytes = 64 * factory.SectorSize;
        byte[] primary = new byte[captureBytes];
        byte[] secondary = new byte[captureBytes];
        byte[] head = retainedImage.BuildPrimaryFragment();
        byte[] tail = retainedImage.BuildBackupFragment();
        Array.Copy(head, 0, primary, 0, head.Length);
        Array.Copy(tail, 0, secondary, secondary.Length - tail.Length, tail.Length);

        string primaryPath = Path.Combine(overlayDirectory, "retained-primary.bin");
        string secondaryPath = Path.Combine(overlayDirectory, "retained-secondary.bin");
        File.WriteAllBytes(primaryPath, primary);
        File.WriteAllBytes(secondaryPath, secondary);
        device.AddWriteOverlay(0, 0, 64, primaryPath);
        device.AddWriteOverlay(0, device.TotalSectors(0) - 64, 64, secondaryPath);
        device.AddWriteOverlay(0, retainedStart, archiveSectors, material.ArchivePath);
        return (device, retainedStart, archiveSectors);
    }

    private static void AssertRetainedArchiveWasNotCapturedAgain(
        IB001FakeEdlRunner runner, long archiveStart, long archiveSectors)
    {
        Assert.DoesNotContain(runner.Reads, read => read.Lun == 0 && read.SectorCount == archiveSectors);
        Assert.InRange(ArchiveBytesRead(runner, archiveStart, archiveSectors), 1L, BoardHeaderBytes);
    }

    private static long ArchiveBytesRead(IB001FakeEdlRunner runner, long archiveStart, long archiveSectors) =>
        runner.Reads
            .Where(read => read.Lun == 0
                && read.StartSector >= archiveStart
                && read.StartSector < archiveStart + archiveSectors)
            .Sum(read => (long)read.SectorCount * IB001SyntheticDevice.SectorSize);

    /// <summary>
    /// Rewrites the cached PC manifest and its sidecar so the directory stays internally self-consistent: every
    /// role, length and hash still describes the same payload, only the entry order changes. Only the archive's
    /// own <c>pcBackupManifestSha256</c> can notice this, which is exactly the provenance this case checks.
    /// </summary>
    private static string TamperCachedPcManifest(string directory)
    {
        string path = Path.Combine(directory, ManifestFileName);
        JsonObject root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        JsonArray files = root["files"]!.AsArray();
        JsonNode first = files[0]!.DeepClone();
        files.RemoveAt(0);
        files.Add(first);

        byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        File.WriteAllBytes(
            path + SidecarSuffix,
            Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)) + "\n"));
        return HashFile(path);
    }

    private static void WriteHeaderBytes(string archivePath, byte value)
    {
        byte[] header = new byte[BoardHeaderBytes];
        Array.Fill(header, value);
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = 0;
        stream.Write(header, 0, header.Length);
        stream.Flush(true);
    }

    // ------------------------------------------------------------------- proof-write ownership

    /// <summary>
    /// ISSUE38 review fix: a proof write that meets a pre-existing marker must leave that marker byte-for-byte
    /// untouched, and must not leave the companion file this call already created behind.
    /// </summary>
    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public void TryWriteCacheProof_collision_preserves_the_marker_and_leaves_no_new_companion(string variant)
    {
        string name = nameof(TryWriteCacheProof_collision_preserves_the_marker_and_leaves_no_new_companion)
            + "-" + variant;
        string directory = _fixture.NewCaseDirectory(name);
        string receiptPath = Path.Combine(directory, "board-recovery-cache.json");
        string manifestPath = Path.Combine(directory, "board-recovery-cache.manifest");

        // The manifest is written first, so "first" collides on the manifest and "second" only on the receipt.
        bool collidesOnManifest = string.Equals(variant, "first", StringComparison.Ordinal);
        string preexistingPath = collidesOnManifest ? manifestPath : receiptPath;
        string newlyCreatedPath = collidesOnManifest ? receiptPath : manifestPath;
        byte[] preexisting = Encoding.UTF8.GetBytes("pre-existing " + variant + " proof marker");
        File.WriteAllBytes(preexistingPath, preexisting);

        BoardRecoveryBackup.TryWriteCacheProof(
            directory,
            Encoding.UTF8.GetBytes("new receipt bytes"),
            Encoding.UTF8.GetBytes("new manifest bytes"));

        Assert.True(
            preexisting.AsSpan().SequenceEqual(File.ReadAllBytes(preexistingPath)),
            "the pre-existing proof marker must stay byte-for-byte unchanged");
        Assert.False(
            File.Exists(newlyCreatedPath),
            "the proof write must not leave behind a file it created before the collision");
        Assert.Single(Directory.GetFiles(directory));
    }

    // --------------------------------------------- receipt schemaVersion of a non-numeric JSON kind

    /// <summary>
    /// PR49 review, P2: the receipt reader asked <c>schemaVersion.TryGetInt32</c> before looking at its
    /// <c>ValueKind</c>, so a syntactically valid receipt carrying <c>"1"</c>, <c>null</c>, <c>true</c>,
    /// <c>[]</c> or <c>{}</c> threw <see cref="InvalidOperationException"/> straight out of the optional cache
    /// probe and out of the public reader, for an archive that was fully intact. Each kind below must instead
    /// leave the receipt unreadable-but-ignored, keep every pre-existing cache file byte-for-byte, and let the
    /// session read and extract the whole board archive exactly as if no cache existed.
    /// </summary>
    private static readonly string[] InvalidReceiptSchemaVersionKinds =
        { "string", "null", "boolean", "array", "object" };

    [Fact]
    public async Task InvalidReceiptSchemaVersionKind_ReadAgain_ignores_the_cache_and_reads_the_whole_archive()
    {
        const string Name =
            nameof(InvalidReceiptSchemaVersionKind_ReadAgain_ignores_the_cache_and_reads_the_whole_archive);
        RetainedRecoveryMaterial material = await CreateRetainedRecoveryMaterialAsync(Name);
        string invalidDirectory = await RetainCacheWithReadAsync(material, Name + "-first");
        string receiptPath = Path.Combine(invalidDirectory, "board-recovery-cache.json");
        Assert.True(File.Exists(receiptPath), "the retained first session must leave a cache receipt behind");

        byte[] validReceipt = File.ReadAllBytes(receiptPath);
        string cachedManifestPath = Path.Combine(invalidDirectory, ManifestFileName);
        string cachedManifestSha256 = HashFile(cachedManifestPath);
        int cachedFileCount = Directory.GetFiles(invalidDirectory, "*", SearchOption.AllDirectories).Length;

        foreach (string kind in InvalidReceiptSchemaVersionKinds)
        {
            byte[] tampered = RewriteReceiptSchemaVersion(validReceipt, kind);
            File.WriteAllBytes(receiptPath, tampered);

            RetainedSession second = await CreateRetainedSessionAsync(material, Name + "-" + kind);
            InternalBackupSet? reused = null;
            Exception? error = null;
            try
            {
                using (second.Current)
                {
                    string scratch = _fixture.NewCaseDirectory(Name + "-" + kind + "-scratch");
                    try
                    {
                        reused = await BoardRecoveryBackup.ReadAsync(
                            second.Client, second.Current, material.DurableParent, scratch);
                    }
                    catch (Exception exception)
                    {
                        error = exception;
                    }
                }

                Assert.True(
                    error is null,
                    $"{kind}: an optional cache marker must never fail the read of an intact archive: {error}");
                Assert.NotNull(reused);
                InternalBackupSet fallback = reused!;
                Assert.NotEqual(invalidDirectory, fallback.DirectoryPath, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(51, fallback.Files.Count);
                Assert.Equal(
                    HashFile(fallback.ManifestPath), fallback.ManifestSha256, StringComparer.OrdinalIgnoreCase);
                Assert.True(File.Exists(Path.Combine(fallback.DirectoryPath, "misc.bin")));

                // The fallback is the full capture the first session already had to take once.
                Assert.Contains(second.Runner.Reads, read =>
                    read.Lun == 0 && read.StartSector == second.ArchiveStart && read.SectorCount == second.ArchiveSectors);
                Assert.Empty(second.Runner.Writes);
                Assert.Equal(0, second.Runner.ResetCount);

                string[] fresh = FreshExtractions(material.DurableParent, invalidDirectory);
                Assert.True(
                    fresh.Length == 1,
                    $"{kind}: exactly one fresh fallback extraction was expected, found {fresh.Length}");
            }
            finally
            {
                reused?.Dispose();
                DeleteExtractionsExcept(material.DurableParent, invalidDirectory);
            }

            AssertUnchangedInvalidCache(
                kind, invalidDirectory, receiptPath, tampered, cachedManifestPath, cachedManifestSha256,
                cachedFileCount);
        }
    }

    [Fact]
    public async Task InvalidReceiptSchemaVersionKind_ReadDirectoryAgain_ignores_the_cache_and_reads_the_whole_archive()
    {
        const string Name =
            nameof(InvalidReceiptSchemaVersionKind_ReadDirectoryAgain_ignores_the_cache_and_reads_the_whole_archive);
        RetainedRecoveryMaterial material = await CreateRetainedRecoveryMaterialAsync(Name);
        string invalidDirectory = await RetainCacheWithDirectoryAsync(material, Name + "-first");
        string receiptPath = Path.Combine(invalidDirectory, "board-recovery-cache.json");
        Assert.True(File.Exists(receiptPath), "the retained first lease must leave a cache receipt behind");

        byte[] validReceipt = File.ReadAllBytes(receiptPath);
        string cachedManifestPath = Path.Combine(invalidDirectory, ManifestFileName);
        string cachedManifestSha256 = HashFile(cachedManifestPath);
        int cachedFileCount = Directory.GetFiles(invalidDirectory, "*", SearchOption.AllDirectories).Length;

        foreach (string kind in InvalidReceiptSchemaVersionKinds)
        {
            byte[] tampered = RewriteReceiptSchemaVersion(validReceipt, kind);
            File.WriteAllBytes(receiptPath, tampered);

            RetainedSession second = await CreateRetainedSessionAsync(material, Name + "-" + kind);
            BoardRecoveryExtraction? lease = null;
            Exception? error = null;
            using (second.Current)
            {
                string scratch = _fixture.NewCaseDirectory(Name + "-" + kind + "-scratch");
                try
                {
                    lease = await BoardRecoveryBackup.ReadDirectoryAsync(
                        second.Client, material.DurableParent, scratch);
                }
                catch (Exception exception)
                {
                    error = exception;
                }
            }

            // The outer finally deletes this case's own fresh fixtures on any failure; the inner finally only
            // disposes the lease, so the assertions right after it still see exactly what that dispose did.
            try
            {
                try
                {
                    Assert.True(
                        error is null,
                        $"{kind}: an optional cache marker must never fail the discovery of an intact archive: {error}");
                    Assert.NotNull(lease);
                    BoardRecoveryExtraction extracted = lease!;
                    Assert.NotEqual(invalidDirectory, extracted.DirectoryPath, StringComparer.OrdinalIgnoreCase);
                    Assert.True(File.Exists(Path.Combine(extracted.DirectoryPath, ManifestFileName)));
                    Assert.True(File.Exists(Path.Combine(extracted.DirectoryPath, "misc.bin")));

                    Assert.Contains(second.Runner.Reads, read =>
                        read.Lun == 0 && read.StartSector == second.ArchiveStart
                        && read.SectorCount == second.ArchiveSectors);
                    Assert.Empty(second.Runner.Writes);
                    Assert.Equal(0, second.Runner.ResetCount);

                    string[] fresh = FreshExtractions(material.DurableParent, invalidDirectory);
                    Assert.True(
                        fresh.Length == 1,
                        $"{kind}: exactly one fresh fallback extraction was expected, found {fresh.Length}");
                }
                finally
                {
                    // This lease was never retained, so disposing it removes only the directory it created.
                    lease?.Dispose();
                }

                // Checked before any manual cleanup: only the unretained dispose may have removed the fresh
                // extraction, so a dispose that failed to delete it can never hide behind the delete below.
                string[] remaining = Directory.GetDirectories(material.DurableParent, "board-recovery-*");
                Assert.True(
                    remaining.Length == 1
                        && string.Equals(remaining[0], invalidDirectory, StringComparison.OrdinalIgnoreCase),
                    $"{kind}: the unretained lease must remove only its own extraction and keep the invalid cache");
                AssertUnchangedInvalidCache(
                    kind, invalidDirectory, receiptPath, tampered, cachedManifestPath, cachedManifestSha256,
                    cachedFileCount);
            }
            finally
            {
                DeleteExtractionsExcept(material.DurableParent, invalidDirectory);
            }
        }
    }

    // ------------------------------------------------------------------ receipt-kind fixtures

    private async Task<string> RetainCacheWithReadAsync(RetainedRecoveryMaterial material, string name)
    {
        RetainedSession session = await CreateRetainedSessionAsync(material, name);
        using (session.Current)
        {
            string scratch = _fixture.NewCaseDirectory(name + "-scratch");
            using InternalBackupSet? retained = await BoardRecoveryBackup.ReadAsync(
                session.Client, session.Current, material.DurableParent, scratch);
            Assert.NotNull(retained);
            Assert.Equal(51, retained!.Files.Count);
            return retained.DirectoryPath;
        }
    }

    private async Task<string> RetainCacheWithDirectoryAsync(RetainedRecoveryMaterial material, string name)
    {
        RetainedSession session = await CreateRetainedSessionAsync(material, name);
        using (session.Current)
        {
            BoardRecoveryExtraction lease = (await BoardRecoveryBackup.ReadDirectoryAsync(
                session.Client, material.DurableParent, _fixture.NewCaseDirectory(name + "-scratch")))!;
            Assert.NotNull(lease);
            using (lease)
            {
                lease.Retain();
                return lease.DirectoryPath;
            }
        }
    }

    /// <summary>
    /// Rewrites only the JSON kind of <c>schemaVersion</c> in an otherwise genuine receipt: model, recovery
    /// partition identity and all three hashes stay exactly as the first session wrote them, so the kind guard
    /// is the only check that can reject this marker.
    /// </summary>
    private static byte[] RewriteReceiptSchemaVersion(byte[] receipt, string kind)
    {
        JsonObject root = JsonNode.Parse(receipt)!.AsObject();
        JsonNode? replacement = kind switch
        {
            "string" => JsonValue.Create("1"),
            "null" => null,
            "boolean" => JsonValue.Create(true),
            "array" => new JsonArray(JsonValue.Create(1)),
            "object" => new JsonObject { ["value"] = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown schemaVersion kind."),
        };
        root["schemaVersion"] = replacement;
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string[] FreshExtractions(string durableParent, string keep) =>
        Directory.GetDirectories(durableParent, "board-recovery-*")
            .Where(directory => !string.Equals(directory, keep, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>Removes only the fresh extractions this case created, so the next kind meets one candidate again.</summary>
    private static void DeleteExtractionsExcept(string durableParent, string keep)
    {
        foreach (string directory in FreshExtractions(durableParent, keep))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertUnchangedInvalidCache(
        string kind,
        string directory,
        string receiptPath,
        byte[] tampered,
        string manifestPath,
        string manifestSha256,
        int fileCount)
    {
        Assert.True(Directory.Exists(directory), $"{kind}: the invalid cache directory must not be deleted");
        Assert.True(File.Exists(receiptPath), $"{kind}: the invalid receipt must not be deleted");
        Assert.True(
            tampered.AsSpan().SequenceEqual(File.ReadAllBytes(receiptPath)),
            $"{kind}: the invalid receipt must stay byte-for-byte unchanged");
        Assert.Equal(manifestSha256, HashFile(manifestPath), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fileCount, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
    }
}
