using System.Reflection;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class RecoveryPartitionLifecycleTests
{
    private const long GiB = 1L << 30;
    private const string LinuxType = "0FC63DAF-8483-4772-8E79-3D69D8477DE4";
    private const string DiskGuid = "F3C013D0-899E-456B-901B-2B566E5A0001";
    private const string UserUuid = "F3C013D0-899E-456B-901B-2B566E5A0002";
    private const string BackupUuid = "F3C013D0-899E-456B-901B-2B566E5A0003";
    private const string VendorUuid = "F3C013D0-899E-456B-901B-2B566E5A0004";
    private const ulong BackupAttributes = 1UL << 60;

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void ReinstallReusesTailAndUninstallReturnsTheCompleteFactoryExtent(int sector)
    {
        var image = RetainedImage(sector);
        var table = Parse(image, sector);
        var backup = table.Partitions.Single(p => p.Name == "ARMADA_BACKUP");
        var userdata = table.Partitions.Single(p => p.Name == "userdata");
        var plan = InternalPartitionPlanner.Plan(table, 128, new PackageStorage(GiB, 8 * GiB, 2 * GiB, GiB));
        Assert.Same(userdata, plan.OriginalUserdata);
        Assert.Equal(userdata.StartSector + userdata.SectorCount, plan.OriginalUserdataEndSectorExclusive);
        Assert.Equal(backup.StartSector, plan.BackupStartSector);
        Assert.Equal(backup.SectorCount, plan.BackupSectorCount);
        Assert.Equal((userdata.SectorCount - plan.AndroidSectorCount - plan.AlignmentGapSectors) * sector, plan.LinuxBytes);

        var method = typeof(InternalInstallArtifacts).GetMethod("BuildGptRewrite", BindingFlags.Static | BindingFlags.NonPublic)!;
        var bootUuid = Guid.Parse("F3C013D0-899E-456B-901B-2B566E5A0011");
        var rootUuid = Guid.Parse("F3C013D0-899E-456B-901B-2B566E5A0012");
        var result = method.Invoke(null, new object[] {
            image.BuildPrimaryFragment(), image.BuildBackupFragment(), sector, 256 * GiB,
            table, plan, bootUuid, rootUuid, Guid.Parse(BackupUuid) })!;
        var post = (GptTable)result.GetType().GetField("PostTable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result)!;
        Assert.Equal(table.Partitions.Count + 2, post.Partitions.Count);
        var kept = post.Partitions.Single(p => p.Name == "ARMADA_BACKUP");
        Assert.Equal(backup.Number, kept.Number);
        Assert.Equal(backup.PartUuid, kept.PartUuid);
        Assert.Equal(backup.Attributes, kept.Attributes);
        Assert.Equal(backup.StartSector, kept.StartSector);
        Assert.Equal(backup.SectorCount, kept.SectorCount);
        var layout = new DiskLayout("edl:ufs:lun:0", sector, post.Partitions.Select(p =>
            new DiskPartition(p.Number, p.Name, p.StartSector, p.SectorCount, p.TypeGuid, p.PartUuid)).ToArray());
        var record = new LinuxInstallationLayout("edl:ufs:lun:0", UserUuid, bootUuid.ToString(), rootUuid.ToString(), table.LastUsableSector + 1)
            { BoardBackupPartUuid = BackupUuid };
        var restored = UninstallPlanner.Plan(layout, record);
        Assert.Equal(table.LastUsableSector + 1, restored.UserdataEndSectorExclusive);
        Assert.Equal((table.LastUsableSector + 1 - userdata.StartSector) * sector, restored.RestoredAndroidBytes);
        Assert.Equal(3, restored.RemovedPartitionNumbers.Count);
        Assert.Contains((long)backup.Number, restored.RemovedPartitionNumbers);

        var factory = RetainedImage(sector, factory: true);
        var again = InternalPartitionPlanner.Plan(Parse(factory, sector), 128, new PackageStorage(GiB, 8 * GiB, 2 * GiB, GiB));
        Assert.Equal(plan.BackupStartSector, again.BackupStartSector);
        Assert.Equal(plan.LinuxBytes, again.LinuxBytes);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("type")]
    [InlineData("gap")]
    [InlineData("unknown")]
    [InlineData("linux")]
    public void InvalidRetainedPartitionIsRejected(string fault)
    {
        var table = Parse(RetainedImage(4096, fault: fault), 4096);
        Assert.Throws<InvalidOperationException>(() => InternalPartitionPlanner.Plan(
            table, 128, new PackageStorage(GiB, 8 * GiB, 2 * GiB, GiB)));
    }

    private static GptTable Parse(GptImage image, int sector) =>
        OriginalGptImporter.Parse(image.BuildPrimaryFragment(), image.BuildBackupFragment(), sector, 256 * GiB);

    private static GptImage RetainedImage(int sector, bool factory = false, string? fault = null)
    {
        var sectors = 256 * GiB / sector;
        var end = sectors - 33;
        var alignment = (1L << 20) / sector;
        var backupEnd = end / alignment * alignment;
        var backupStart = backupEnd - 2 * GiB / sector;
        var start = GiB / sector + 40;
        var entries = new List<GptPartitionSpec>
        {
            new(1, "vendor", LinuxType, VendorUuid, 40, 128, 0UL),
            new(2, "userdata", LinuxType, UserUuid, start,
                (factory ? end : backupStart - (fault == "gap" ? 1 : 0)) - start, 0UL),
        };
        if (!factory)
            entries.Add(new GptPartitionSpec(17,
                fault == "unknown" ? "ARMADA_OTHER" : fault == "linux" ? "ARMADA_ROOT" : "ARMADA_BACKUP",
                fault == "type" ? "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" : LinuxType,
                BackupUuid, backupStart, 2 * GiB / sector - (fault == "size" ? 1 : 0), BackupAttributes));
        return new GptImage(sector, 256 * GiB, DiskGuid, 34, end - 1, entries.ToArray());
    }
}
