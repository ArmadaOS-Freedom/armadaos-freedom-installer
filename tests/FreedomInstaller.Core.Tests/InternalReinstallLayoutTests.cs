using System;
using System.IO;
using System.Linq;
using Xunit;

namespace FreedomInstaller.Core.Tests
{
    /// <summary>
    /// Real saved GPT fixtures for the recognized installed ARMADA_BOOT/ARMADA_ROOT/ARMADA_BACKUP layout.
    /// The task-specific remote run sets both environment paths; ordinary runs without the fixture return
    /// without asserting. No test fabricates a recognized installed layout or touches a device.
    /// </summary>
    public class InternalReinstallLayoutTests
    {
        private const long DiskBytes = 123871232L * 4096;
        private const int SectorSize = 4096;
        private const long MiB = 1L << 20;
        private const long GiB = 1L << 30;

        [Theory]
        [InlineData(32L)]
        [InlineData(64L)]
        [InlineData(128L)]
        public void RealGptLayout_PlannerRespectsInvariants(long androidGiB)
        {
            using RealReinstallFixture? fixture = RealReinstallFixture.TryLoad();
            if (fixture is null)
            {
                return;
            }

            GptTable original = fixture.Original;
            GptPartition userdata = original.Partitions.Single(p => p.Name == "userdata");
            Assert.Equal(5972392L, userdata.StartSector);
            Assert.Equal(16777216L, userdata.SectorCount);

            GptPartition installedBoot = original.Partitions.Single(p => p.Name == "ARMADA_BOOT");
            GptPartition installedRoot = original.Partitions.Single(p => p.Name == "ARMADA_ROOT");
            GptPartition backupPart = original.Partitions.Single(p => p.Name == "ARMADA_BACKUP");
            Assert.Equal(22749696L, installedBoot.StartSector);
            Assert.Equal(23011840L, installedRoot.StartSector);
            Assert.Equal(123346688L, backupPart.StartSector);
            Assert.Equal(524288L, backupPart.SectorCount);

            long alignmentSectors = MiB / SectorSize;
            long androidEnd = userdata.StartSector + userdata.SectorCount;
            long bootStart = AlignUp(androidEnd, alignmentSectors);
            Assert.Equal(installedBoot.StartSector, bootStart);
            Assert.Equal(installedBoot.StartSector + installedBoot.SectorCount, installedRoot.StartSector);
            Assert.Equal(installedRoot.StartSector + installedRoot.SectorCount, backupPart.StartSector);

            var storage = new PackageStorage(1L << 30, 8L << 30, 2L << 30, 1L << 30);

            AndroidAllocationRange range = InternalPartitionPlanner.GetAndroidRange(original, storage);
            Assert.Equal(16L, range.MinimumGiB);
            Assert.InRange(androidGiB, range.MinimumGiB, range.MaximumGiB);
            Assert.InRange(64L, range.MinimumGiB, range.MaximumGiB);

            InternalAllocationPlan plan = InternalPartitionPlanner.Plan(original, androidGiB, storage);

            Assert.Equal(userdata.StartSector, plan.OriginalUserdata.StartSector);
            Assert.Equal(userdata.SectorCount, plan.OriginalUserdata.SectorCount);
            Assert.Equal(userdata.PartUuid, plan.OriginalUserdata.PartUuid);
            Assert.Equal(androidGiB * GiB, plan.AndroidBytes);
            Assert.Equal(androidGiB * GiB / SectorSize, plan.AndroidSectorCount);
            Assert.Equal(userdata.StartSector + plan.AndroidSectorCount, plan.AndroidEndSectorExclusive);

            Assert.Equal(backupPart.StartSector, plan.BackupStartSector);
            Assert.Equal(backupPart.SectorCount, plan.BackupSectorCount);
            Assert.Equal(2L * GiB, plan.BackupBytes);
            Assert.Equal(backupPart.StartSector + backupPart.SectorCount, plan.BackupEndSectorExclusive);

            Assert.Equal(AlignUp(plan.AndroidEndSectorExclusive, alignmentSectors), plan.LinuxBootStartSector);
            Assert.Equal(plan.LinuxBootStartSector + plan.LinuxBootSectorCount, plan.LinuxRootStartSector);
            Assert.Equal(plan.LinuxRootStartSector + plan.LinuxRootSectorCount, plan.BackupStartSector);

            if (androidGiB == 64L)
            {
                Assert.Equal(installedBoot.StartSector, plan.LinuxBootStartSector);
                Assert.Equal(installedRoot.StartSector, plan.LinuxRootStartSector);
                Assert.Equal(installedRoot.SectorCount, plan.LinuxRootSectorCount);
            }
            else
            {
                long deltaSectors = (androidGiB - 64L) * GiB / SectorSize;
                Assert.Equal(installedRoot.SectorCount - deltaSectors, plan.LinuxRootSectorCount);
            }

            InternalAllocationPlan smaller = InternalPartitionPlanner.Plan(original, 32L, storage);
            InternalAllocationPlan larger = InternalPartitionPlanner.Plan(original, 128L, storage);
            Assert.True(smaller.AndroidSectorCount <= plan.AndroidSectorCount);
            Assert.True(plan.AndroidSectorCount <= larger.AndroidSectorCount);
            Assert.True(smaller.LinuxRootSectorCount >= plan.LinuxRootSectorCount);
            Assert.True(plan.LinuxRootSectorCount >= larger.LinuxRootSectorCount);
            Assert.Equal(plan.BackupStartSector, smaller.BackupStartSector);
            Assert.Equal(plan.BackupStartSector, larger.BackupStartSector);
            Assert.Equal(plan.BackupSectorCount, smaller.BackupSectorCount);
            Assert.Equal(plan.BackupSectorCount, larger.BackupSectorCount);
        }

        [Theory]
        [InlineData(32L)]
        [InlineData(64L)]
        [InlineData(128L)]
        public void RealGptLayout_RewritePreservesEverythingExceptThePlannedFields(long androidGiB)
        {
            using RealReinstallFixture? fixture = RealReinstallFixture.TryLoad();
            if (fixture is null)
            {
                return;
            }

            GptTable original = fixture.Original;
            var storage = new PackageStorage(1L << 30, 8L << 30, 2L << 30, 1L << 30);
            InternalAllocationPlan plan = InternalPartitionPlanner.Plan(original, androidGiB, storage);

            GptPartition userdata = original.Partitions.Single(p => p.Name == "userdata");
            GptPartition installedBoot = original.Partitions.Single(p => p.Name == "ARMADA_BOOT");
            GptPartition installedRoot = original.Partitions.Single(p => p.Name == "ARMADA_ROOT");
            GptPartition backupPart = original.Partitions.Single(p => p.Name == "ARMADA_BACKUP");

            var uuids = InternalInstallArtifacts.CreatePartitionUuids(original);
            Assert.Equal(Guid.Parse(installedBoot.PartUuid), uuids.Boot);
            Assert.Equal(Guid.Parse(installedRoot.PartUuid), uuids.Root);
            Assert.Equal(Guid.Parse(backupPart.PartUuid), uuids.Backup);

            var rewrite = InternalInstallArtifacts.BuildGptRewrite(
                fixture.Primary, fixture.Backup, SectorSize, DiskBytes,
                original, plan, uuids.Boot, uuids.Root, uuids.Backup);

            var plannedUserdata = rewrite.PostTable.Partitions.Single(p => p.Name == "userdata");
            Assert.Equal(userdata.Number, plannedUserdata.Number);
            Assert.Equal(userdata.Name, plannedUserdata.Name);
            Assert.Equal(userdata.TypeGuid, plannedUserdata.TypeGuid);
            Assert.Equal(userdata.PartUuid, plannedUserdata.PartUuid);
            Assert.Equal(userdata.Attributes, plannedUserdata.Attributes);
            Assert.Equal(userdata.StartSector, plannedUserdata.StartSector);
            Assert.Equal(plan.AndroidSectorCount, plannedUserdata.SectorCount);

            var plannedBoot = rewrite.PostTable.Partitions.Single(p => p.Name == "ARMADA_BOOT");
            var plannedRoot = rewrite.PostTable.Partitions.Single(p => p.Name == "ARMADA_ROOT");
            var plannedBackup = rewrite.PostTable.Partitions.Single(p => p.Name == "ARMADA_BACKUP");
            Assert.Equal(installedBoot.Number, plannedBoot.Number);
            Assert.Equal(installedBoot.PartUuid, plannedBoot.PartUuid);
            Assert.Equal(installedBoot.Attributes, plannedBoot.Attributes);
            Assert.Equal(plan.LinuxBootStartSector, plannedBoot.StartSector);
            Assert.Equal(plan.LinuxBootSectorCount, plannedBoot.SectorCount);
            Assert.Equal(installedRoot.Number, plannedRoot.Number);
            Assert.Equal(installedRoot.PartUuid, plannedRoot.PartUuid);
            Assert.Equal(installedRoot.Attributes, plannedRoot.Attributes);
            Assert.Equal(plan.LinuxRootStartSector, plannedRoot.StartSector);
            Assert.Equal(plan.LinuxRootSectorCount, plannedRoot.SectorCount);

            Assert.Equal(backupPart.Number, plannedBackup.Number);
            Assert.Equal(backupPart.Name, plannedBackup.Name);
            Assert.Equal(backupPart.TypeGuid, plannedBackup.TypeGuid);
            Assert.Equal(backupPart.PartUuid, plannedBackup.PartUuid);
            Assert.Equal(backupPart.Attributes, plannedBackup.Attributes);
            Assert.Equal(backupPart.StartSector, plannedBackup.StartSector);
            Assert.Equal(backupPart.SectorCount, plannedBackup.SectorCount);

            foreach (GptPartition before in original.Partitions)
            {
                if (before.Number == userdata.Number || before.Number == installedBoot.Number
                    || before.Number == installedRoot.Number)
                {
                    continue;
                }

                GptPartition after = rewrite.PostTable.Partitions.Single(p => p.Number == before.Number);
                Assert.Equal(before.Name, after.Name);
                Assert.Equal(before.TypeGuid, after.TypeGuid);
                Assert.Equal(before.PartUuid, after.PartUuid);
                Assert.Equal(before.Attributes, after.Attributes);
                Assert.Equal(before.StartSector, after.StartSector);
                Assert.Equal(before.SectorCount, after.SectorCount);
            }

            long backupStart = backupPart.StartSector;
            long backupEnd = backupPart.StartSector + backupPart.SectorCount;
            foreach (var region in rewrite.WriteRegions)
            {
                long regionEnd = region.StartSector + region.SectorCount;
                Assert.True(
                    regionEnd <= backupStart || region.StartSector >= backupEnd,
                    $"write region '{region.Role}' overlaps the existing backup extent");
            }

            if (androidGiB == 64L)
            {
                Assert.True(rewrite.PostPrimaryFragment.AsSpan().SequenceEqual(fixture.Primary));
                Assert.True(rewrite.PostBackupFragment.AsSpan().SequenceEqual(fixture.Backup));
            }
            else
            {
                Assert.False(rewrite.PostPrimaryFragment.AsSpan().SequenceEqual(fixture.Primary));
                Assert.False(rewrite.PostBackupFragment.AsSpan().SequenceEqual(fixture.Backup));
            }
        }

        [Fact]
        public void RealGptLayout_EffectiveFactoryWindowMatchesPartitionSum()
        {
            using RealReinstallFixture? fixture = RealReinstallFixture.TryLoad();
            if (fixture is null)
            {
                return;
            }

            var layout = InternalPartitionPlanner.FindInstalledLinuxLayout(fixture.Original);
            Assert.NotNull(layout);

            GptPartition userdata = fixture.Original.Partitions.Single(p => p.Name == "userdata");
            long alignmentSectors = MiB / SectorSize;
            long userdataEnd = userdata.StartSector + userdata.SectorCount;
            long bootEnd = layout!.Value.Boot.StartSector + layout.Value.Boot.SectorCount;
            long rootEnd = layout.Value.Root.StartSector + layout.Value.Root.SectorCount;
            long backupEnd = layout.Value.Backup.StartSector + layout.Value.Backup.SectorCount;

            Assert.Equal(bootEnd, layout.Value.Root.StartSector);
            Assert.Equal(rootEnd, layout.Value.Backup.StartSector);
            Assert.Equal(AlignUp(userdataEnd, alignmentSectors), layout.Value.Boot.StartSector);
            Assert.Equal(layout.Value.Backup.StartSector, rootEnd);
            Assert.Equal(2L * GiB, layout.Value.Backup.SectorCount * (long)SectorSize);
            Assert.True(backupEnd > layout.Value.Backup.StartSector);
        }

        [Fact]
        public void RealGptLayout_GetAndroidRangeSupportsTheCurrentAndroidValue()
        {
            using RealReinstallFixture? fixture = RealReinstallFixture.TryLoad();
            if (fixture is null)
            {
                return;
            }

            var storage = new PackageStorage(1L << 30, 8L << 30, 2L << 30, 1L << 30);
            AndroidAllocationRange range = InternalPartitionPlanner.GetAndroidRange(fixture.Original, storage);

            Assert.Equal(16L, range.MinimumGiB);
            Assert.True(range.MaximumGiB >= 64L);
            Assert.InRange(64L, range.MinimumGiB, range.MaximumGiB);
            _ = InternalPartitionPlanner.Plan(fixture.Original, range.MinimumGiB, storage);
            _ = InternalPartitionPlanner.Plan(fixture.Original, range.MaximumGiB, storage);
        }

        private static long AlignUp(long value, long alignment)
        {
            long remainder = value % alignment;
            return remainder == 0 ? value : value + (alignment - remainder);
        }

        private sealed class RealReinstallFixture : IDisposable
        {
            internal byte[] Primary = Array.Empty<byte>();
            internal byte[] Backup = Array.Empty<byte>();
            internal GptTable Original = null!;

            internal static RealReinstallFixture? TryLoad()
            {
                string? primaryPath = Environment.GetEnvironmentVariable("FREEDOM_REINSTALL_GPT_PRIMARY");
                string? backupPath = Environment.GetEnvironmentVariable("FREEDOM_REINSTALL_GPT_BACKUP");

                if (primaryPath is null && backupPath is null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(primaryPath) || string.IsNullOrWhiteSpace(backupPath))
                {
                    throw new InvalidOperationException(
                        "Both FREEDOM_REINSTALL_GPT_PRIMARY and FREEDOM_REINSTALL_GPT_BACKUP must be set together.");
                }

                var fixture = new RealReinstallFixture
                {
                    Primary = File.ReadAllBytes(primaryPath),
                    Backup = File.ReadAllBytes(backupPath),
                };
                fixture.Original = OriginalGptImporter.Parse(fixture.Primary, fixture.Backup, SectorSize, DiskBytes);
                return fixture;
            }

            public void Dispose()
            {
            }
        }
    }
}
