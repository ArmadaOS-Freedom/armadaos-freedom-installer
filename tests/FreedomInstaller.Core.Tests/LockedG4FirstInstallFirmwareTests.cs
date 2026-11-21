using System;
using Xunit;
using FreedomInstaller.Core;

namespace FreedomInstaller.Core.Tests;

public sealed class InternalInstallPreparationContextLockedG4FirstInstallFirmwareTests
{
    private const string ExpectedRejectCode = "blocked-locked-g4-first-install-firmware";

    [Theory]
    [InlineData("TB322FC", "TB322FC_CN_OPEN_USER_Q00041.1_W_ZUXOS_1.5.10.358_ST_260910", BootloaderState.Locked, true)]
    [InlineData("TB322FC", "ZUXOS_1.5.10.358_260910_PRC", BootloaderState.Locked, true)]
    [InlineData("TB322FC", "ZUXOS_1.5.10.3580_260910_PRC", BootloaderState.Locked, false)]
    [InlineData("TB322FC", "ZUXOS_1.5.10.259_260910_PRC", BootloaderState.Locked, false)]
    [InlineData("TB322FC", "TB322FC_CN_OPEN_USER_Q00041.1_W_ZUXOS_1.5.10.358_ST_260910", BootloaderState.Unlocked, false)]
    [InlineData("TB323FU", "TB323FU_CN_OPEN_USER_Q00041.1_W_ZUXOS_1.5.10.358_ST_260910", BootloaderState.Locked, false)]
    public void RejectLockedG4FirstInstallFirmware_MatchesExpected(
        string model,
        string displayId,
        BootloaderState lockState,
        bool shouldReject)
    {
        var firmware = new FirmwareSnapshot(
            DisplayId: displayId,
            BuildFingerprint: null,
            AndroidRelease: null,
            SecurityPatch: null,
            AndroidReportedBootloaderState: BootloaderState.Unknown);

        bool effectiveReject = shouldReject && !Tb322fcAbl188Assets.IsBundled;

        if (effectiveReject)
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                InternalInstallPreparationContext.RejectLockedG4FirstInstallFirmware(
                    model,
                    lockState,
                    firmware));
            Assert.Contains(ExpectedRejectCode, ex.Message);
        }
        else
        {
            InternalInstallPreparationContext.RejectLockedG4FirstInstallFirmware(
                model,
                lockState,
                firmware);
        }
    }
}
