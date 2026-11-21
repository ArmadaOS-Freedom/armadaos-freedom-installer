using System;
using System.Threading;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class FirstInstallAblCompatibilityTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public void RejectsIncompatibleAblForFirstInstall(string slot)
    {
        byte[] raw = new byte[16];
        string model = "TB323FU";
        string firmware = "test-firmware-114";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            InternalInstallBackup.RequireFirstInstallAblCompatibility(
                raw,
                model,
                firmware,
                BootloaderState.Locked,
                slot,
                CancellationToken.None));
        var message = ex.Message;

        Assert.StartsWith("requires-compatible-bootloader:", message, StringComparison.Ordinal);
        Assert.Contains("firmware=test-firmware-114", message, StringComparison.Ordinal);
        Assert.Contains("model=TB323FU", message, StringComparison.Ordinal);
        Assert.Contains("lock=Locked", message, StringComparison.Ordinal);
        Assert.Contains("slot=" + slot, message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowsWhenCancelled()
    {
        byte[] raw = new byte[16];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            InternalInstallBackup.RequireFirstInstallAblCompatibility(
                raw,
                "TB323FU",
                "test-firmware-114",
                BootloaderState.Locked,
                "a",
                cts.Token));
    }
}
