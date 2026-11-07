using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;

namespace FreedomInstaller.Core.Tests;

// actual public AfterUpdate preparation/execution business integration over the
// existing legitimate signed/project/capture fixtures. The harness comes from the frozen
// EdlUninstallCurrentMenuTests composition (PrepareAsync prepares only; it never executes a plan
// and never issues device writes). All execution here runs against the simulated IB001 runner.
public sealed class BootMaintenanceAfterUpdateTests :
    IClassFixture<EdlClientToolchainFixture>,
    IClassFixture<EdlRawGblEnvelopeTests.SharedFixture>
{
    private readonly EdlClientToolchainFixture Toolchain;
    private readonly EdlRawGblEnvelopeTests.SharedFixture Shared;

    public BootMaintenanceAfterUpdateTests(
        EdlClientToolchainFixture toolchain,
        EdlRawGblEnvelopeTests.SharedFixture shared)
    {
        Toolchain = toolchain;
        Shared = shared;
    }

    // Hardcoded allowed write targets for a full AfterUpdate repair execution. The prepared plan
    // is never used to derive this whitelist; every observed write must additionally match exactly
    // one actual Prepared WillWrite region by LUN/start/count, and that region's role must be here.
    private static readonly string[] AllowedWriteRoles =
    {
        "boot_a", "boot_b",
        "recovery_a", "recovery_b",
        "vbmeta_system_a", "vbmeta_system_b",
        "vbmeta_a", "vbmeta_b",
        "efisp",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task After_TemplateB_NewStockA_RepairsAndReadsBackWholeWithoutPackage(bool qualcommSlot)
    {
        int nativeCallsBefore = Shared.Runner.Calls;
        using AfterUpdateRun run = await PrepareAfterUpdateAsync(qualcommSlot);
        Assert.True(Shared.Runner.Calls > nativeCallsBefore);

        BootMaintenancePrepared prepared = run.Backend.Prepared;
        Assert.Equal(MaintenanceMode.AfterUpdate, prepared.Mode);
        Assert.Equal("a", prepared.CurrentSlot);

        BootMaintenanceSlotSummary slotA = prepared.Slots.Single(s => s.Slot == "a");
        Assert.Equal(BootMaintenanceSourceKind.Project, slotA.SourceKind);
        Assert.Equal("a", slotA.SourceSlot);

        CurrentBootSlotFacts bootSlot = await CurrentBootSlot.ReadAsync(run.Fresh);
        Assert.Equal(qualcommSlot ? "qualcomm-gpt" : "aosp-misc", bootSlot.Source);

        BootMaintenanceRegionSummary targetA = prepared.Regions.Single(r => r.Role == "boot_a");
        EdlCapturedRegion region = run.Harness.Current.Files["boot_a"];
        string freshBootAFile = run.Fresh.Files["boot_a"].FilePath;
        Assert.True(targetA.WillWrite);
        Assert.Equal(region.Lun, targetA.Lun);
        Assert.Equal(region.StartSector, targetA.StartSector);
        Assert.Equal(region.SectorCount, targetA.SectorCount);
        Assert.Equal((long)region.SectorCount * region.SectorSize, targetA.Length);
        Assert.Equal(Sha256File(freshBootAFile), targetA.OriginalSha256, ignoreCase: true);
        Assert.False(string.Equals(Sha256File(freshBootAFile), targetA.CandidateSha256, StringComparison.OrdinalIgnoreCase));

        int writesBefore = run.Harness.Ib.Writes.Count;
        int resetsBefore = run.Harness.Ib.ResetCount;
        int readsBefore = run.Harness.Ib.Reads.Count;

        BootMaintenanceResult result = await run.Backend.ExecuteAsync(
            prepared.ConfirmationId, progress: null, cancellationToken: default);

        Assert.Equal(BootMaintenanceOutcome.Completed, result.Outcome);
        Assert.Equal(InternalInstallWriteState.AllVerified, result.WriteState);
        Assert.Contains("boot_a", result.VerifiedRegions);
        Assert.True(result.AwaitingAndroidAcceptance);

        List<IB001Write> newWrites = run.Harness.Ib.Writes.Skip(writesBefore).ToList();
        foreach (IB001Write write in newWrites)
        {
            BootMaintenanceRegionSummary match = Assert.Single(prepared.Regions.Where(r =>
                r.WillWrite &&
                r.Lun == write.Lun &&
                r.StartSector == write.StartSector &&
                r.SectorCount == write.SectorCount));
            Assert.Contains(match.Role, AllowedWriteRoles);
        }

        Assert.Contains(newWrites, w =>
            w.Lun == region.Lun &&
            w.StartSector == region.StartSector &&
            w.SectorCount == region.SectorCount);

        IB001Read readBackA = run.Harness.Ib.Reads.Skip(readsBefore).Last(r =>
            r.Lun == region.Lun &&
            r.StartSector == region.StartSector &&
            r.SectorCount == region.SectorCount);
        Assert.Equal(targetA.CandidateSha256, Sha256File(readBackA.FilePath), ignoreCase: true);

        Assert.Equal(resetsBefore + 1, run.Harness.Ib.ResetCount);
    }

    [Fact]
    public async Task After_PreparedThenCurrentBootChanges_RefusesBeforeAnyWriteReset()
    {
        using AfterUpdateRun run = await PrepareAfterUpdateAsync();

        int writesBefore = run.Harness.Ib.Writes.Count;
        int resetsBefore = run.Harness.Ib.ResetCount;

        EdlCapturedRegion region = run.Harness.Current.Files["boot_a"];
        EdlCapturedRegion freshBootA = run.Fresh.Files["boot_a"];

        byte[] sector = new byte[region.SectorSize];
        using (FileStream stream = File.OpenRead(freshBootA.FilePath))
        {
            int total = 0;
            while (total < sector.Length)
            {
                int readNow = await stream.ReadAsync(sector.AsMemory(total));
                Assert.True(readNow > 0);
                total += readNow;
            }
        }

        sector[0] ^= 0xFF;
        string tamperedPath = Path.Combine(run.Harness.CaseDirectory, "boot_a-sector0-flipped.bin");
        await File.WriteAllBytesAsync(tamperedPath, sector);
        run.Harness.Device.AddWriteOverlay(region.Lun, region.StartSector, 1, tamperedPath);

        BootMaintenanceResult result = await run.Backend.ExecuteAsync(
            run.Backend.Prepared.ConfirmationId, progress: null, cancellationToken: default);

        Assert.Equal(BootMaintenanceOutcome.Failed, result.Outcome);
        Assert.Equal(InternalInstallWriteState.NoneIssued, result.WriteState);
        Assert.Empty(result.VerifiedRegions);
        Assert.Equal(writesBefore, run.Harness.Ib.Writes.Count);
        Assert.Equal(resetsBefore, run.Harness.Ib.ResetCount);
    }

    [Fact]
    public async Task After_FromEdl_HistoricalAnchorDoesNotInventUnlockedForAosp()
    {
        var frozen = new EdlUninstallCurrentMenuTests(Toolchain, Shared);
        using EdlUninstallCurrentMenuTests.Harness h = await frozen.PrepareAsync();

        string evidenceDirectory = Path.Combine(h.CaseDirectory, "evidence-edl");
        Directory.CreateDirectory(evidenceDirectory);

        int writesBefore = h.Ib.Writes.Count;
        int resetsBefore = h.Ib.ResetCount;

        BootMaintenanceHistoricalIdentity history =
            await BootMaintenanceBackend.ReadHistoricalIdentityAsync(h.Current.DirectoryPath);
        Assert.Equal(BootloaderState.Unknown, history.Identity.Identity.BootloaderState);

        var sources = new BootMaintenanceSources(BootTools: Shared.Tools);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BootMaintenanceBackend.CreatePreparedFromEdlAsync(
                MaintenanceMode.AfterUpdate, history, h.Client, h.Current, sources, Shared.Runner,
                evidenceDirectory, progress: null, cancellationToken: default));

        Assert.StartsWith("requires-current-bootloader-state:", failure.Message);
        Assert.Equal(writesBefore, h.Ib.Writes.Count);
        Assert.Equal(resetsBefore, h.Ib.ResetCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairInstallation_ExistingAospRouteKeepsAblAndData(bool locked)
    {
        using var run = await PrepareAfterUpdateAsync(qualcommSlot: true, repairInstallation: true, locked: locked);
        Assert.DoesNotContain(run.Backend.Prepared.Regions, r => r.Role.StartsWith("abl_", StringComparison.Ordinal));
        Assert.True(run.Backend.Prepared.Regions.Single(r => r.Role == "boot_a").WillWrite);
        Assert.True(run.Backend.Prepared.Regions.Single(r => r.Role == "boot_b").WillWrite);
        int writesBefore = run.Harness.Ib.Writes.Count;
        var result = await run.Backend.ExecuteAsync(run.Backend.Prepared.ConfirmationId);
        Assert.Equal(BootMaintenanceOutcome.Completed, result.Outcome);
        Assert.Contains("boot_a", result.VerifiedRegions);
        Assert.Contains("boot_b", result.VerifiedRegions);
        foreach (var write in run.Harness.Ib.Writes.Skip(writesBefore))
            Assert.Contains(run.Backend.Prepared.Regions, r => AllowedWriteRoles.Contains(r.Role)
                && r.Lun == write.Lun && r.StartSector == write.StartSector && r.SectorCount == write.SectorCount);
    }

    private static async Task<InternalDeviceCheckResult> RunRealCheckAsync(EdlUninstallCurrentMenuTests.Harness h, bool locked = false)
    {
        string serial = h.Client.Binding.AndroidIdentity.Identity.Serial;
        var checkRunner = new DeviceCheckRunner(serial, new DeviceCheckClock())
        {
            FastbootCurrentSlot = "a",
            ReturnSlotSuffix = "_a",
            BootImmediatelyCompleteOnReturn = true,
            UnlockedReportValue = locked ? "no" : "yes",
        };
        InternalDeviceCheckResult check = await new InternalInstallDeviceCheck(
            AdbOrFastboot.AdbPath, AdbOrFastboot.FastbootPath, checkRunner, checkRunner.Clock)
            .CheckAsync(serial);
        Assert.True(check.Succeeded);
        Assert.Equal(locked ? BootloaderState.Locked : BootloaderState.Unlocked, check.Android!.Identity.BootloaderState);
        return check;
    }

    private async Task<AfterUpdateRun> PrepareAfterUpdateAsync(bool qualcommSlot = false, bool repairInstallation = false, bool locked = false)
    {
        var frozen = new EdlUninstallCurrentMenuTests(Toolchain, Shared);
        EdlUninstallCurrentMenuTests.Harness h = await frozen.PrepareAsync();

        EdlRawBootBuilderTests.Workspace? workspace = null;
        DirectAvbSourceTestFixture? aosp = null;
        BootMaintenanceBackend? backend = null;
        InternalBackupSet? fresh = null;
        try
        {
            if (qualcommSlot)
            {
                EdlCapturedRegion miscRegion = h.Current.Files["misc"];
                byte[] miscBytes = await File.ReadAllBytesAsync(miscRegion.FilePath);
                Assert.True(miscBytes.Length >= 2048 + 32);
                Array.Clear(miscBytes, 2048, 32);
                string syntheticMiscPath = Path.Combine(h.CaseDirectory, "misc-zero-aosp-control.bin");
                await File.WriteAllBytesAsync(syntheticMiscPath, miscBytes);
                h.Device.AddWriteOverlay(miscRegion.Lun, miscRegion.StartSector, miscRegion.SectorCount, syntheticMiscPath);
                EdlUninstallCurrentMenuTests.OverlayQualcommSlotGpt(h.Device, h.CaseDirectory, activeA: true);
            }

            workspace = new();
            EdlRawBootBuilderTests.SlotFixture freshSlot = EdlRawBootBuilderTests.CreateSlot(workspace, "a", 0x91);
            aosp = new(DirectAvbSourceKind.Aosp);
            VerifiedEdlFile newStockA = aosp.A.RewriteBootLeaf(freshSlot.CurrentBoot.Path);

            EdlCapturedRegion region = h.Current.Files["boot_a"];
            h.Device.AddWriteOverlay(region.Lun, region.StartSector, region.SectorCount, newStockA.Path);

            string afterCurrentDirectory = h.CaseDirectory + "/after-current";
            Directory.CreateDirectory(afterCurrentDirectory);
            fresh = await InternalInstallBackup.CreateAsync(h.Client, afterCurrentDirectory);

            InternalDeviceCheckResult check = await RunRealCheckAsync(h, locked);

            var sources = new BootMaintenanceSources(BootTools: Shared.Tools, RepairInstallation: repairInstallation);
            string evidenceDirectory = Path.Combine(h.CaseDirectory, "evidence");
            Directory.CreateDirectory(evidenceDirectory);

            backend = await BootMaintenanceBackend.CreatePreparedAsync(
                MaintenanceMode.AfterUpdate, check, h.Client, fresh, sources, Shared.Runner,
                evidenceDirectory, progress: null, cancellationToken: default);

            return new AfterUpdateRun(workspace, aosp, h, fresh, backend);
        }
        catch
        {
            // Reverse-order best-effort cleanup of whatever was actually allocated; the original
            // exception is preserved and never replaced by disposal failures.
            BestEffortDispose(backend);
            BestEffortDispose(fresh);
            BestEffortDispose(h);
            BestEffortDispose(aosp);
            BestEffortDispose(workspace);
            throw;
        }
    }

    private static void BestEffortDispose(IDisposable? resource)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            resource.Dispose();
        }
        catch
        {
            // Best effort only; the caller's original exception takes precedence.
        }
    }

    private static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class AfterUpdateRun : IDisposable
    {
        public AfterUpdateRun(
            EdlRawBootBuilderTests.Workspace workspace,
            DirectAvbSourceTestFixture aosp,
            EdlUninstallCurrentMenuTests.Harness harness,
            InternalBackupSet fresh,
            BootMaintenanceBackend backend)
        {
            Workspace = workspace;
            Aosp = aosp;
            Harness = harness;
            Fresh = fresh;
            Backend = backend;
        }

        public EdlRawBootBuilderTests.Workspace Workspace { get; }
        public DirectAvbSourceTestFixture Aosp { get; }
        public EdlUninstallCurrentMenuTests.Harness Harness { get; }
        public InternalBackupSet Fresh { get; }
        public BootMaintenanceBackend Backend { get; }

        public void Dispose()
        {
            // All five owned resources are disposed even if one throws. Order: backend, fresh,
            // harness, key fixture, workspace — workspace file claims stay alive the longest.
            BestEffortDispose(Backend);
            BestEffortDispose(Fresh);
            BestEffortDispose(Harness);
            BestEffortDispose(Aosp);
            BestEffortDispose(Workspace);
        }
    }
}
