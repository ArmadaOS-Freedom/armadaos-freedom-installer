using System;
using System.Collections.Generic;
using Xunit;
using FreedomInstaller.Core;

namespace FreedomInstaller.Core.Tests;

public class AndroidDataErasePlanTests
{
    private static readonly string UserdataGuid = Guid.NewGuid().ToString("D");
    private static readonly string UserdataType = Guid.NewGuid().ToString("D");
    private static readonly string MetadataGuid = Guid.NewGuid().ToString("D");
    private static readonly string MetadataType = Guid.NewGuid().ToString("D");
    private static readonly string BackupGuid = Guid.NewGuid().ToString("D");
    private static readonly string BackupType = Guid.NewGuid().ToString("D");
    private static readonly string DiskGuid = Guid.NewGuid().ToString("D");

    private static GptPartition Userdata(int number, long start, long count) =>
        new GptPartition(number, "userdata", UserdataType, UserdataGuid, start, count, 0UL);

    private static GptPartition Metadata(int number, long start, long count) =>
        new GptPartition(number, "metadata", MetadataType, MetadataGuid, start, count, 0UL);

    private static GptPartition Backup(long start, long count) =>
        new GptPartition(3, "ARMADA_BACKUP", BackupType, BackupGuid, start, count, 0UL);

    private static GptTable Table(long usable, List<GptPartition> parts, int sectorSize = 512) =>
        new GptTable(null, sectorSize, usable * sectorSize, DiskGuid, 34, usable - 34, parts, "fp-" + sectorSize + "-" + usable);

    private static GptTable Table600(int sectorSize, bool addSecondMetadata, bool moveMetadata, long userdataCount = 600) =>
        TableFor(sectorSize, userdataCount, addSecondMetadata, moveMetadata);

    private static GptTable TableFor(int sectorSize, long userdataCount, bool addSecondMetadata, bool moveMetadata)
    {
        long metaStart = moveMetadata ? 101 : 100;
        var parts = new List<GptPartition>
        {
            new GptPartition(1, "userdata", UserdataType, UserdataGuid, 400, userdataCount, 0UL),
            new GptPartition(2, "metadata", MetadataType, MetadataGuid, metaStart, 50, 0UL),
            new GptPartition(3, "ARMADA_BACKUP", BackupType, BackupGuid, 1000, 100, 0UL),
        };
        if (addSecondMetadata)
        {
            parts.Add(new GptPartition(4, "Metadata", MetadataType, Guid.NewGuid().ToString("D"), 1100, 10, 0UL));
        }
        return Table(2000, parts, sectorSize);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void ShrinkThenGrow_DecreaseAndIncrease(int sectorSize)
    {
        GptTable current = TableFor(sectorSize, 600, false, false);
        GptTable shrunk = TableFor(sectorSize, 400, false, false);
        AndroidDataErasePlan plan = AndroidDataErasePlan.Prepare(current, shrunk);
        Assert.Equal(400L, plan.Userdata.SectorCount);
        Assert.Equal(100L, plan.Metadata.StartSector);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void GrowTo600_ReachesBackupAt1000_NoOverlap(int sectorSize)
    {
        GptTable shrunk = TableFor(sectorSize, 400, false, false);
        GptTable grown = TableFor(sectorSize, 600, false, false);
        AndroidDataErasePlan plan = AndroidDataErasePlan.Prepare(shrunk, grown);
        Assert.Equal(600L, plan.Userdata.SectorCount);
        Assert.Equal(1000L, plan.Userdata.StartSector + plan.Userdata.SectorCount);
    }

    [Fact]
    public void MissingPostMetadata_Throws()
    {
        GptTable current = TableFor(512, 600, false, false);
        var parts = new List<GptPartition> { new GptPartition(1, "userdata", UserdataType, UserdataGuid, 400, 600, 0UL) };
        GptTable post = Table(2000, parts);
        Assert.ThrowsAny<Exception>(() => AndroidDataErasePlan.Prepare(current, post));
    }

    [Fact]
    public void SecondMetadataAmbiguous_Throws()
    {
        GptTable current = TableFor(512, 600, false, false);
        GptTable post = TableFor(512, 600, true, false);
        Assert.ThrowsAny<Exception>(() => AndroidDataErasePlan.Prepare(current, post));
    }

    [Fact]
    public void MetadataMovedOneSector_Throws()
    {
        GptTable current = TableFor(512, 600, false, false);
        GptTable post = TableFor(512, 600, false, true);
        Assert.ThrowsAny<Exception>(() => AndroidDataErasePlan.Prepare(current, post));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void UserdataIncTo601_IntersectsBackup_Throws(int sectorSize)
    {
        GptTable current = TableFor(sectorSize, 400, false, false);
        GptTable post = TableFor(sectorSize, 601, false, false);
        Assert.ThrowsAny<Exception>(() => AndroidDataErasePlan.Prepare(current, post));
    }

    [Fact]
    public void TailPastUIntMax_WithinValidHugeWindow_Throws()
    {
        long tail = (long)uint.MaxValue + 10L;
        var parts = new List<GptPartition>
        {
            new GptPartition(1, "userdata", UserdataType, UserdataGuid, 34, tail, 0UL),
            new GptPartition(2, "metadata", MetadataType, MetadataGuid, tail + 34, 50, 0UL),
        };
        GptTable current = Table((long)uint.MaxValue * 2L, parts);
        GptTable post = Table((long)uint.MaxValue * 2L, new List<GptPartition>
        {
            new GptPartition(1, "userdata", UserdataType, UserdataGuid, 34, 400, 0UL),
            new GptPartition(2, "metadata", MetadataType, MetadataGuid, tail + 34, 50, 0UL),
        });
        Assert.ThrowsAny<Exception>(() => AndroidDataErasePlan.Prepare(current, post));
    }
}
