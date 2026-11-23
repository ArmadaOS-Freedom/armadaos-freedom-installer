using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class InternalInstallBootOnlyInactiveBTests
    : IClassFixture<Gen5EdlToolchainFixture>, IClassFixture<EdlRawSharedInput>
{
    private readonly Gen5EdlToolchainFixture _g5;
    private readonly EdlRawSharedInput _raw;

    public InternalInstallBootOnlyInactiveBTests(
        Gen5EdlToolchainFixture g5,
        EdlRawSharedInput raw)
    {
        _g5 = g5;
        _raw = raw;
    }

    [Fact]
    public async Task BootOnlyInactiveB_UsesTrustedAAndEmptyOriginalBEvidence()
    {
        using var session = await DirectInstallReadyTestSession.CreateAsync(
            _g5.Toolchain,
            _raw,
            zeroSlot: "b",
            bootOnlyInactiveB: true);

        var prepared = session.Backend.Prepared;

        Assert.Equal("a", prepared.AvbSlots["b"].SourceSlot);
        Assert.True(prepared.AvbSlots["b"].NonbootableSystem);
        Assert.Equal("a", prepared.SlotMaterials["b"].Slot);
        Assert.Equal(
            session.Backup.Files["boot_b"].Sha256,
            prepared.CurrentBootSha256["b"],
            ignoreCase: true);
        Assert.NotEqual(
            prepared.CurrentBootSha256["a"],
            prepared.CurrentBootSha256["b"],
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(session.EdlRunner.Writes);

        var result = await session.Backend.ExecuteAsync(
            prepared.ConfirmationId,
            userdataEraseConfirmed: true);

        Assert.Equal(InternalInstallOutcome.InstallationCompleted, result.Outcome);
        Assert.Equal(InternalInstallWriteState.AllVerified, result.WriteState);
        Assert.Equal("b,a", string.Join(",", result.VerifiedSlots));
        Assert.Equal(1, session.EdlRunner.ResetCount);

        var bootB = session.Backup.Files["boot_b"];
        var bootBWrites = session.EdlRunner.Writes
            .Where(w => w.Lun == bootB.Lun
                     && w.StartSector == bootB.StartSector
                     && w.SectorCount == bootB.SectorCount)
            .ToList();
        Assert.Single(bootBWrites);
        Assert.NotEqual(bootB.Sha256, bootBWrites[0].Sha256, StringComparer.OrdinalIgnoreCase);

        foreach (var role in new[] { "vendor_boot_b", "init_boot_b", "abl_b", "xbl_config_b", "vbmeta_b", "vbmeta_system_b", "recovery_b" })
        {
            var region = session.Backup.Files[role];
            Assert.DoesNotContain(session.EdlRunner.Writes, w =>
                w.Lun == region.Lun
                && w.StartSector == region.StartSector
                && w.SectorCount == region.SectorCount);
        }
    }

    [Fact]
    public async Task StaleInactiveB_ChangedCompanionViaOverlay_FailsBeforeAnyWrite()
    {
        using var session = await DirectInstallReadyTestSession.CreateAsync(
            _g5.Toolchain,
            _raw,
            zeroSlot: "b",
            bootOnlyInactiveB: true);

        var prepared = session.Backend.Prepared;
        var region = session.Backup.Files["xbl_config_b"];
        var original = File.ReadAllBytes(region.FilePath);
        Assert.True(original.Length > 0);
        Assert.Equal(0, original[0]);

        var changed = (byte[])original.Clone();
        changed[0] = 1;
        var newPath = Path.Combine(session.RootDirectory, "changed-inactive-b.bin");
        File.WriteAllBytes(newPath, changed);

        session.Device.AddWriteOverlay(region.Lun, region.StartSector, region.SectorCount, newPath);

        var result = await session.Backend.ExecuteAsync(
            prepared.ConfirmationId,
            userdataEraseConfirmed: true);

        Assert.Equal(InternalInstallOutcome.Failed, result.Outcome);
        Assert.Equal(InternalInstallWriteState.NoneIssued, result.WriteState);
        Assert.Empty(session.EdlRunner.Writes);
        Assert.Equal(0, session.EdlRunner.ResetCount);
    }

    [Fact]
    public async Task BootOnlyInactiveB_RefusesKeptInactiveRecovery()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DirectInstallReadyTestSession.CreateAsync(
                _g5.Toolchain,
                _raw,
                zeroSlot: "b",
                bootOnlyInactiveB: true,
                keepInactiveRecovery: true));

        Assert.Contains("recovery_b", error.Message, StringComparison.Ordinal);
    }
}
