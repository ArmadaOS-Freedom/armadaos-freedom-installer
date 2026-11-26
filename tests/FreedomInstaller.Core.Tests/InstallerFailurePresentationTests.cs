using FreedomInstaller.App;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class InstallerFailurePresentationTests
{
    [Theory]
    [InlineData(true, "备份保存位置", "selected backup location")]
    [InlineData(false, "平板预留的系统恢复备份区", "tablet's recovery partition")]
    public void BackupSpaceFailure_IdentifiesTheAffectedStorage(bool hostBackup, string chineseStorage, string englishStorage)
    {
        const long available = 1073741824;
        const long required = 2684354560;
        Exception error = hostBackup
            ? new BackupDestinationSpaceException(available, required, "Technical backup failure")
            : new BoardRecoverySpaceException(required, available);
        var message = UiText.DisplayError(error);

        var chinese = new UiText(UiText.ChineseLanguage).FormatForDisplay(message);
        var english = new UiText(UiText.EnglishLanguage).FormatForDisplay(message);

        Assert.Contains(chineseStorage, chinese);
        Assert.Contains(englishStorage, english, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.00 GiB", chinese);
        Assert.Contains("2.50 GiB", chinese);
        Assert.DoesNotContain(error.Message, chinese);
    }

    [Fact]
    public void PreparationBanner_KeepsDiskSpaceReasonAfterLanguageSwitch()
    {
        var exception = Assert.Throws<InternalInstallWorkDiskSpaceException>(() =>
            InternalInstallPreparationContext.RequireDirectExecutionSpaceAvailable(38408839168L, 14706802688L));
        var reason = UiText.InternalInstallPreparationFailure(exception);
        var banner = new UiMessage("Dynamic.Diagnostics.Failure",
            new UiMessage("Dynamic.Diagnostics.Stage.Prepare"), reason);
        var text = new UiText(UiText.ChineseLanguage);

        var chinese = text.FormatForDisplay(banner);
        Assert.Contains("空间不足", chinese);
        Assert.Contains("13.69 GiB", chinese);
        Assert.Contains("35.78 GiB", chinese);
        Assert.DoesNotContain("insufficient-direct-execution-space", chinese);

        text.SetLanguage(UiText.EnglishLanguage);

        var english = text.FormatForDisplay(banner);
        Assert.Contains("free space", english, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13.69 GiB", english);
        Assert.Contains("35.78 GiB", english);
        Assert.DoesNotContain("insufficient-direct-execution-space", english);
        Assert.DoesNotContain("空间不足", english);
        Assert.Contains("insufficient-direct-execution-space", InstallerLog.DescribeFailure(exception));
    }
}
