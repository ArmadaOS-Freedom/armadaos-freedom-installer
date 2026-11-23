using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace FreedomInstaller.Core;

internal static class InternalInstallLauncherDefaults
{
    private const long TwoGiB = 2L * 1024L * 1024L * 1024L;
    private const long RegionSize = 65536L;
    private const long SlotSize = 4096L;
    private const long SlotCount = 3L;
    private const int RecoveryCompareOffset = 12288;

    internal static GptPartition? FindInstalledBackupPartition(bool usesArmadaBoot, GptTable original)
    {
        if (!usesArmadaBoot) return null;
        return InternalPartitionPlanner.FindInstalledLinuxLayout(original)?.Backup;
    }

    internal static LauncherDefaultsPlan? PlanFromReads(
        EdlReadEvidence headerRead,
        EdlReadEvidence tailRead,
        Guid expectedPartition,
        string model,
        string evidenceDirectory,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (headerRead is null) throw new ArgumentNullException(nameof(headerRead));
        if (tailRead is null) throw new ArgumentNullException(nameof(tailRead));
        if (string.IsNullOrEmpty(model)) throw new ArgumentException("model", nameof(model));
        if (string.IsNullOrEmpty(evidenceDirectory)) throw new ArgumentException("evidenceDirectory", nameof(evidenceDirectory));

        ValidateGeometry(headerRead, nameof(headerRead));
        ValidateGeometry(tailRead, nameof(tailRead));

        if (headerRead.Length != RegionSize) throw new InvalidOperationException("Header length is not 65536.");
        if (tailRead.Length != RegionSize) throw new InvalidOperationException("Tail length is not 65536.");

        if (headerRead.Geometry.Lun != 0 || tailRead.Geometry.Lun != 0)
            throw new InvalidOperationException("Reads must be on LUN 0.");

        int sectorSize = headerRead.Geometry.SectorSize;
        if (tailRead.Geometry.SectorSize != sectorSize)
            throw new InvalidOperationException("Header and tail sector sizes differ.");

        long expectedTailStart = checked(headerRead.StartSector + TwoGiB / sectorSize - RegionSize / sectorSize);
        if (tailRead.StartSector != expectedTailStart)
            throw new InvalidOperationException(
                $"Tail start {tailRead.StartSector} != expected {expectedTailStart} (header start {headerRead.StartSector}).");

        byte[] headerBytes = File.ReadAllBytes(headerRead.FilePath);
        ct.ThrowIfCancellationRequested();
        if (headerBytes.Length != RegionSize) throw new InvalidOperationException("Header file length mismatch.");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(headerBytes)), headerRead.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Header file SHA256 does not match evidence.");

        byte[] before = File.ReadAllBytes(tailRead.FilePath);
        ct.ThrowIfCancellationRequested();
        if (before.Length != RegionSize) throw new InvalidOperationException("Tail file length mismatch.");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(before)), tailRead.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tail file SHA256 does not match evidence.");

        InternalInstallArtifacts.BoardArchiveHeader header = InternalInstallArtifacts.ParseBoardArchiveHeader(headerBytes, model);
        if (header.SchemaVersion == 2)
            return null;
        if (header.SchemaVersion != 3)
            throw new InvalidDataException(
                $"Retained ARMADA_BACKUP schema {header.SchemaVersion} is not supported; a compatible schema-3 backup is required before ARMADA_BOOT reinstall.");
        if (header.ConfigPartitionUuid != expectedPartition)
            throw new InvalidDataException("The recovery header does not belong to the target partition.");

        byte[] after = BootSettingsRegion.PrepareLinuxDefault(
            before,
            expectedPartition,
            header.InstallationId,
            model,
            checked(RegionSize + header.CompressedBytes));

        if (!after.AsSpan(RecoveryCompareOffset).SequenceEqual(before.AsSpan(RecoveryCompareOffset)))
            throw new InvalidOperationException("Recovery mutable records were modified.");

        Directory.CreateDirectory(evidenceDirectory);

        var writes = new List<InternalArtifactRegion>();
        int sectorsPerSlot = checked((int)(SlotSize / sectorSize));

        for (int slot = 0; slot < SlotCount; slot++)
        {
            int offset = slot * (int)SlotSize;
            ReadOnlySpan<byte> src = after.AsSpan(offset, (int)SlotSize);
            if (src.SequenceEqual(before.AsSpan(offset, (int)SlotSize)))
                continue;

            byte[] slotBytes = src.ToArray();
            string sha = Convert.ToHexString(SHA256.HashData(slotBytes));
            string outPath = Path.Combine(evidenceDirectory, $"launcher-defaults-slot{slot}.bin");

            using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ct.ThrowIfCancellationRequested();
                fs.Write(slotBytes, 0, slotBytes.Length);
                fs.Flush(flushToDisk: true);
            }

            long slotStart = checked(tailRead.StartSector + (long)slot * SlotSize / sectorSize);

            writes.Add(new InternalArtifactRegion(
                Role: $"launcher-defaults-slot{slot}",
                Lun: 0,
                StartSector: slotStart,
                SectorCount: sectorsPerSlot,
                SectorSize: sectorSize,
                FilePath: outPath,
                Length: SlotSize,
                Sha256: sha));
        }

        string afterSha = Convert.ToHexString(SHA256.HashData(after));
        return new LauncherDefaultsPlan(writes.AsReadOnly(), headerRead, tailRead, afterSha);
    }

    private static void ValidateGeometry(EdlReadEvidence r, string name)
    {
        int ss = r.Geometry.SectorSize;
        if (ss != 512 && ss != 4096)
            throw new InvalidOperationException($"{name}: sector size {ss} not permitted.");
        if (RegionSize % ss != 0)
            throw new InvalidOperationException($"{name}: region not aligned to sector size.");
        if (r.SectorCount * ss != r.Length)
            throw new InvalidOperationException($"{name}: sector count * sector size != length.");
        if (r.Length != RegionSize)
            throw new InvalidOperationException($"{name}: length is not 65536.");
        if (r.Geometry.TotalSectors < r.StartSector + r.SectorCount)
            throw new InvalidOperationException($"{name}: read exceeds geometry.");
    }
}

internal sealed record LauncherDefaultsPlan(
    IReadOnlyList<InternalArtifactRegion> Writes,
    EdlReadEvidence BeforeHeader,
    EdlReadEvidence BeforeTail,
    string AfterTailSha256);
