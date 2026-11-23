using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

/// <summary>
/// Two-partition controlled erase of LUN0 <c>userdata</c> and <c>metadata</c> bound to a verified pre-table and
/// an already computed post-table. <see cref="Prepare"/> is pure: it reads no disk and mutates nothing, so a
/// refused plan is decided before the first erase command. <see cref="ExecuteAsync"/> re-proves the live GPT
/// against the post table before touching a sector, then erases exactly the planned extents and samples zero
/// readback at each extent's start and end.
/// </summary>
internal sealed class AndroidDataErasePlan
{
    private const int CaptureSectors = 64;
    private const int ReadbackWindowBytes = 4096;
    private const string UserdataName = "userdata";
    private const string MetadataName = "metadata";
    private const int PreviewLun = 0;

    private readonly GptTable postTable;
    private readonly long userdataStart;
    private readonly long userdataCount;
    private readonly long metadataStart;
    private readonly long metadataCount;

    private AndroidDataErasePlan(
        GptPartition userdata,
        GptPartition metadata,
        GptTable postTable,
        long userdataStart,
        long userdataCount,
        long metadataStart,
        long metadataCount)
    {
        Userdata = userdata;
        Metadata = metadata;
        this.postTable = postTable;
        this.userdataStart = userdataStart;
        this.userdataCount = userdataCount;
        this.metadataStart = metadataStart;
        this.metadataCount = metadataCount;
    }

    /// <summary>Post-table userdata entry; the current entry may only grow or shrink after its start.</summary>
    internal GptPartition Userdata { get; }

    /// <summary>Post-table metadata entry; it must be byte-for-byte the same partition current.</summary>
    internal GptPartition Metadata { get; }

    /// <summary>
    /// Builds the plan from two already CRC-verified tables. Rejects missing, case-ambiguous or duplicate
    /// partition names, geometry or usable-window drift, any identity/type/number/attribute change of the
    /// userdata entry and any change at all to the metadata entry, malformed UUIDs, non-positive or out-of-window
    /// post extents and overlap between post extents and any other declared post partition.
    /// </summary>
    internal static AndroidDataErasePlan Prepare(GptTable current, GptTable post)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        if (post is null)
        {
            throw new ArgumentNullException(nameof(post));
        }

        RequireGeometry(current, post);
        RequireUsableWindow(current);
        RequireUsableWindow(post);
        if (current.FirstUsableSector != post.FirstUsableSector || current.LastUsableSector != post.LastUsableSector)
        {
            throw new InvalidOperationException("The GPT tables do not share the same usable window.");
        }
        RequireDiskGuid(current, post);

        GptPartition currentUserdata = RequireUniqueNamed(current, UserdataName);
        GptPartition postUserdata = RequireUniqueNamed(post, UserdataName);
        GptPartition currentMetadata = RequireUniqueNamed(current, MetadataName);
        GptPartition postMetadata = RequireUniqueNamed(post, MetadataName);

        RequireUuid(currentUserdata.TypeGuid, "the current userdata type GUID");
        RequireUuid(currentUserdata.PartUuid, "the current userdata partition GUID");
        RequireUuid(postUserdata.TypeGuid, "the post userdata type GUID");
        RequireUuid(postUserdata.PartUuid, "the post userdata partition GUID");
        RequireUuid(currentMetadata.TypeGuid, "the current metadata type GUID");
        RequireUuid(currentMetadata.PartUuid, "the current metadata partition GUID");
        RequireUuid(postMetadata.TypeGuid, "the post metadata type GUID");
        RequireUuid(postMetadata.PartUuid, "the post metadata partition GUID");

        if (currentUserdata.Number != postUserdata.Number
            || !string.Equals(currentUserdata.TypeGuid, postUserdata.TypeGuid, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentUserdata.PartUuid, postUserdata.PartUuid, StringComparison.OrdinalIgnoreCase)
            || currentUserdata.Attributes != postUserdata.Attributes
            || currentUserdata.StartSector != postUserdata.StartSector)
        {
            throw new InvalidOperationException("The userdata entry identity or start sector changed.");
        }

        if (currentMetadata.Number != postMetadata.Number
            || !string.Equals(currentMetadata.Name, postMetadata.Name, StringComparison.Ordinal)
            || !string.Equals(currentMetadata.TypeGuid, postMetadata.TypeGuid, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentMetadata.PartUuid, postMetadata.PartUuid, StringComparison.OrdinalIgnoreCase)
            || currentMetadata.Attributes != postMetadata.Attributes
            || currentMetadata.StartSector != postMetadata.StartSector
            || currentMetadata.SectorCount != postMetadata.SectorCount)
        {
            throw new InvalidOperationException("The metadata entry moved or changed.");
        }

        RequireExtent(postTable: post, postUserdata, "userdata");
        RequireExtent(postTable: post, postMetadata, "metadata");

        RequireDisjoint(postUserdata, postMetadata, "userdata", "metadata");
        RequireNoOverlap(post, postUserdata, "userdata");
        RequireNoOverlap(post, postMetadata, "metadata");

        return new AndroidDataErasePlan(
            postUserdata,
            postMetadata,
            post,
            postUserdata.StartSector,
            postUserdata.SectorCount,
            postMetadata.StartSector,
            postMetadata.SectorCount);
    }

    /// <summary>
    /// Reads the live LUN0 GPT primary and backup, requires it to equal the post table (identity, geometry and
    /// usable bounds) and only then erases userdata and metadata in that order, verifying sampled zero readback
    /// at each erased extent. Any mismatch or readback failure throws and the remaining steps are never run.
    /// </summary>
    internal async Task ExecuteAsync(
        EdlClient client,
        string evidenceDirectory,
        Action<string> report,
        CancellationToken token)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (report is null)
        {
            throw new ArgumentNullException(nameof(report));
        }

        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            throw new InvalidOperationException("The evidence directory is blank.");
        }

        string directory = Path.Combine(
            evidenceDirectory,
            "android-erase-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("The erase evidence directory could not be created.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("The erase evidence directory could not be created.", exception);
        }

        try
        {
            token.ThrowIfCancellationRequested();

            report("android-erase: verifying the live LUN0 GPT against the post table");
            await VerifyLiveGptAsync(client, directory, token).ConfigureAwait(false);

            await EraseOneAsync(
                client,
                directory,
                report,
                "userdata",
                userdataStart,
                userdataCount,
                token).ConfigureAwait(false);

            await EraseOneAsync(
                client,
                directory,
                report,
                "metadata",
                metadataStart,
                metadataCount,
                token).ConfigureAwait(false);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private async Task VerifyLiveGptAsync(EdlClient client, string directory, CancellationToken token)
    {
        long totalSectors = CheckedCeilingDivide(postTable.DiskBytes, postTable.SectorSize, "The disk sector count overflows.");
        if (totalSectors < CaptureSectors)
        {
            throw new InvalidOperationException("The disk is too small to hold a GPT.");
        }

        string primaryPath = Path.Combine(directory, "live-gpt-primary.bin");
        string backupPath = Path.Combine(directory, "live-gpt-backup.bin");

        try
        {
            EdlReadEvidence primaryEvidence = await ReadCaptureAsync(
                client, primaryPath, 0, token).ConfigureAwait(false);
            EdlReadEvidence backupEvidence = await ReadCaptureAsync(
                client, backupPath, totalSectors - CaptureSectors, token).ConfigureAwait(false);

            if (primaryEvidence is null || backupEvidence is null)
            {
                throw new InvalidOperationException("The live LUN0 GPT read returned no evidence.");
            }

            if (primaryEvidence.Geometry.Lun != PreviewLun
                || primaryEvidence.Geometry.SectorSize != postTable.SectorSize
                || primaryEvidence.StartSector != 0
                || primaryEvidence.SectorCount != CaptureSectors)
            {
                throw new InvalidOperationException("The live GPT primary read acknowledged a different extent.");
            }

            if (backupEvidence.Geometry.Lun != PreviewLun
                || backupEvidence.Geometry.SectorSize != postTable.SectorSize
                || backupEvidence.StartSector != totalSectors - CaptureSectors
                || backupEvidence.SectorCount != CaptureSectors)
            {
                throw new InvalidOperationException("The live GPT backup read acknowledged a different extent.");
            }

            byte[] primaryBytes = File.ReadAllBytes(primaryPath);
            byte[] backupBytes = File.ReadAllBytes(backupPath);

            GptTable live = OriginalGptImporter.Parse(
                primaryBytes, backupBytes, postTable.SectorSize, postTable.DiskBytes);

            RequireUsableWindow(live);
            if (live.FirstUsableSector != postTable.FirstUsableSector
                || live.LastUsableSector != postTable.LastUsableSector)
            {
                throw new InvalidDataException("The live LUN0 usable window differs from the post table.");
            }

            InternalInstallPreparationContext.RequireSameTable(postTable, live, "the live LUN0 GPT");
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("The live LUN0 GPT could not be read for verification.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException("The live LUN0 GPT could not be read for verification.", exception);
        }
    }

    private async Task<EdlReadEvidence> ReadCaptureAsync(
        EdlClient client, string path, long startSector, CancellationToken token)
    {
        return await client
            .ReadAsync(PreviewLun, startSector, CaptureSectors, path, token)
            .ConfigureAwait(false);
    }

    private async Task EraseOneAsync(
        EdlClient client,
        string directory,
        Action<string> report,
        string name,
        long startSector,
        long sectorCount,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        report("android-erase: erasing " + name + " at sector " + startSector + " count " + sectorCount);

        EdlEraseEvidence evidence = await client
            .EraseAsync(PreviewLun, startSector, sectorCount, token)
            .ConfigureAwait(false);

        if (evidence is null)
        {
            throw new InvalidOperationException("The " + name + " erase returned no evidence.");
        }

        if (evidence.Geometry.Lun != PreviewLun
            || evidence.Geometry.SectorSize != postTable.SectorSize
            || evidence.StartSector != startSector
            || evidence.SectorCount != sectorCount)
        {
            throw new InvalidOperationException("The " + name + " erase acknowledged a different extent.");
        }

        report("android-erase: " + name + " erase acknowledged, verifying zero readback samples");

        long endSector = CheckedExtentEnd(startSector, sectorCount, name);
        string startPath = Path.Combine(directory, name + "-start-readback.bin");
        string endPath = Path.Combine(directory, name + "-end-readback.bin");

        try
        {
            await VerifyZeroWindowAsync(client, startPath, startSector, name, "start", token).ConfigureAwait(false);

            if (endSector == startSector)
            {
                report("android-erase: " + name + " verified zero at start sector " + startSector);
                return;
            }

            await VerifyZeroWindowAsync(client, endPath, endSector, name, "end", token).ConfigureAwait(false);
            report("android-erase: " + name + " verified zero samples at sectors " + startSector + " and " + endSector);
        }
        catch
        {
            report("android-erase: " + name + " readback did not verify as zero");
            throw;
        }
    }

    private async Task VerifyZeroWindowAsync(
        EdlClient client,
        string path,
        long startSector,
        string name,
        string label,
        CancellationToken token)
    {
        EdlReadEvidence evidence = await client
            .ReadAsync(PreviewLun, startSector, 1, path, token)
            .ConfigureAwait(false);

        if (evidence is null)
        {
            throw new InvalidOperationException("The " + name + " " + label + " readback returned no evidence.");
        }

        if (evidence.Geometry.Lun != PreviewLun
            || evidence.Geometry.SectorSize != postTable.SectorSize
            || evidence.StartSector != startSector
            || evidence.SectorCount != 1)
        {
            throw new InvalidOperationException("The " + name + " " + label + " readback acknowledged a different extent.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        int window = bytes.Length < ReadbackWindowBytes ? bytes.Length : ReadbackWindowBytes;
        if (window <= 0)
        {
            throw new InvalidOperationException("The " + name + " " + label + " readback window is empty.");
        }

        for (int index = 0; index < window; index++)
        {
            if (bytes[index] != 0)
            {
                throw new InvalidOperationException(
                    "The " + name + " " + label + " readback window is not zero.");
            }
        }
    }

    private static void RequireGeometry(GptTable current, GptTable post)
    {
        if (current.SectorSize != post.SectorSize
            || current.DiskBytes != post.DiskBytes)
        {
            throw new InvalidOperationException("The two tables describe different disk geometry.");
        }

        if (current.SectorSize != 512 && current.SectorSize != 4096)
        {
            throw new InvalidOperationException("The sector size is not supported.");
        }

        if (current.DiskBytes <= 0
            || current.DiskBytes % current.SectorSize != 0)
        {
            throw new InvalidOperationException("The disk size is not sector aligned.");
        }

        if (current.DeviceId is not null && post.DeviceId is not null
            && !string.Equals(current.DeviceId, post.DeviceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The two tables name different devices.");
        }
    }

    private static void RequireUsableWindow(GptTable table)
    {
        if (table.FirstUsableSector < 0
            || table.LastUsableSector < table.FirstUsableSector
            || table.SectorSize <= 0
            || table.DiskBytes <= 0)
        {
            throw new InvalidOperationException("The GPT usable window is not valid.");
        }

        long lastSector = CheckedCeilingDivide(table.DiskBytes, table.SectorSize, "The disk sector count overflows.") - 1;
        if (table.LastUsableSector > lastSector)
        {
            throw new InvalidOperationException("The GPT usable window extends past the disk.");
        }
    }

    private static void RequireDiskGuid(GptTable current, GptTable post)
    {
        RequireUuid(current.DiskGuid, "the current disk GUID");
        RequireUuid(post.DiskGuid, "the post disk GUID");
        if (!string.Equals(current.DiskGuid, post.DiskGuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The disk GUID changed between the two tables.");
        }
    }

    private static GptPartition RequireUniqueNamed(GptTable table, string name)
    {
        GptPartition? match = null;
        foreach (GptPartition partition in table.Partitions)
        {
            if (!string.Equals(partition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidOperationException("The partition name '" + name + "' is duplicated.");
            }

            if (!string.Equals(partition.Name, name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The partition name '" + name + "' is case-ambiguous.");
            }

            match = partition;
        }

        if (match is null)
        {
            throw new InvalidOperationException("The partition '" + name + "' is missing.");
        }

        return match;
    }

    private static void RequireUuid(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Guid.TryParseExact(value, "D", out Guid parsed)
            || parsed == Guid.Empty)
        {
            throw new InvalidOperationException(description + " is not a valid UUID.");
        }
    }

    private static void RequireExtent(GptTable postTable, GptPartition partition, string name)
    {
        if (partition.SectorCount <= 0)
        {
            throw new InvalidOperationException("The post " + name + " extent is not positive.");
        }

        long end = CheckedExtentEnd(partition.StartSector, partition.SectorCount, name);
        if (partition.StartSector < postTable.FirstUsableSector || end > postTable.LastUsableSector)
        {
            throw new InvalidOperationException("The post " + name + " extent leaves the usable window.");
        }

        long last = CheckedExtentEnd(partition.StartSector, partition.SectorCount, name);
        if (partition.StartSector > uint.MaxValue
            || partition.SectorCount > uint.MaxValue
            || last > uint.MaxValue)
        {
            throw new InvalidOperationException("The post " + name + " extent does not fit UInt32.");
        }
    }

    private static long CheckedExtentEnd(long start, long count, string name)
    {
        if (start < 0 || count <= 0)
        {
            throw new InvalidOperationException("The " + name + " extent is not positive.");
        }

        long last;
        try
        {
            last = checked(start + count - 1);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("The " + name + " extent overflows.", exception);
        }

        return last;
    }

    private static void RequireDisjoint(GptPartition left, GptPartition right, string leftName, string rightName)
    {
        long leftEnd = CheckedExtentEnd(left.StartSector, left.SectorCount, leftName);
        long rightEnd = CheckedExtentEnd(right.StartSector, right.SectorCount, rightName);
        if (left.StartSector <= rightEnd && right.StartSector <= leftEnd)
        {
            throw new InvalidOperationException("The " + leftName + " and " + rightName + " extents overlap.");
        }
    }

    private static void RequireNoOverlap(GptTable post, GptPartition target, string name)
    {
        long targetEnd = CheckedExtentEnd(target.StartSector, target.SectorCount, name);
        foreach (GptPartition other in post.Partitions)
        {
            if (other.Number == target.Number)
            {
                continue;
            }

            if (other.SectorCount <= 0)
            {
                continue;
            }

            long otherEnd = CheckedExtentEnd(other.StartSector, other.SectorCount, "the overlapping entry");
            if (target.StartSector <= otherEnd && other.StartSector <= targetEnd)
            {
                throw new InvalidOperationException(
                    "The post " + name + " extent overlaps partition " + other.Number + ".");
            }
        }
    }

    private static long CheckedCeilingDivide(long bytes, int sectorSize, string message)
    {
        if (bytes <= 0 || sectorSize <= 0)
        {
            throw new InvalidOperationException(message);
        }

        long whole;
        try
        {
            whole = checked(bytes / sectorSize);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(message, exception);
        }

        return bytes % sectorSize == 0 ? whole : checked(whole + 1);
    }

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
