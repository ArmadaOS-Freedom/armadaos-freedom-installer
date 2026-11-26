using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreedomInstaller.App;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Localization tests for the catalogs the installer actually ships. The four JSON files are embedded
/// in this test assembly under the same logical names as in the App assembly, so <see cref="UiText"/>
/// reads the real strings here instead of a copy that could drift. Nothing is written to disk, so no
/// test artifact directory is needed.
/// </summary>
public sealed class UiTextTests
{
    /// <summary>Logical resource names that must exist in the executing assembly, without culture suffix.</summary>
    private const string StaticChineseCatalog = "FreedomInstaller.Strings.Static.zh-CN.json";
    private const string StaticEnglishCatalog = "FreedomInstaller.Strings.Static.en.json";
    private const string DynamicChineseCatalog = "FreedomInstaller.Strings.Dynamic.zh-CN.json";
    private const string DynamicEnglishCatalog = "FreedomInstaller.Strings.Dynamic.en.json";

    /// <summary>Composite placeholders such as <c>{0}</c> or <c>{1:N0}</c>.</summary>
    private static readonly Regex PlaceholderPattern = new(
        @"\{(\d+)(?:,[^}]*)?(?::[^}]*)?\}",
        RegexOptions.CultureInvariant);

    [Fact]
    public void ScreenshotConnectTimeout_RawErrorIsMaskedForDisplayButKeptInRawFormat()
    {
        const string raw = "No EDL endpoint for serial TEST123 appeared within 90 seconds.";
        var message = new UiMessage("Dynamic.Global.Status.ConnectFailed", new TimeoutException(raw));
        var text = new UiText(UiText.ChineseLanguage);

        var chineseDisplay = text.FormatForDisplay(message);
        Assert.Contains("连接失败", chineseDisplay);
        Assert.Contains("90 秒", chineseDisplay);
        Assert.Contains("9008", chineseDisplay);
        Assert.DoesNotContain("TEST123", chineseDisplay);
        Assert.DoesNotContain(raw, chineseDisplay);
        Assert.Contains(raw, text.Format(message));

        text.SetLanguage(UiText.EnglishLanguage);
        var englishDisplay = text.FormatForDisplay(message);
        Assert.Contains("Connect failed", englishDisplay);
        Assert.Contains("90 seconds", englishDisplay);
        Assert.DoesNotContain(raw, englishDisplay);
        Assert.DoesNotContain("TEST123", englishDisplay);
        Assert.Contains(raw, text.Format(message));
    }

    [Fact]
    public void LinuxAlreadyInstalled_WarningExplainsWhyFactoryBackupIsUnavailable()
    {
        var chinese = new UiText(UiText.ChineseLanguage);
        var english = new UiText(UiText.EnglishLanguage);
        var message = new UiMessage("Dynamic.Global.Error.LinuxAlreadyInstalled");

        Assert.Contains("已有 Linux 分区", chinese.FormatForDisplay(message));
        Assert.Contains("原厂安卓的 9008 全局备份", chinese.FormatForDisplay(message));
        Assert.Contains("already has Linux installed", english.FormatForDisplay(message));
        Assert.Contains("Android 9008 backup", english.FormatForDisplay(message));
    }

    [Fact]
    public void NestedArgumentUiMessage_DisplayKeepsPathAndMasksUnknownProseButRawFormatPreservesIt()
    {
        const string unknownRawProse = "vendor driver stack trace 0xDEADBEEF should stay raw only";
        const string localPath = "C:\\Backups\\tablet-2024-06-01";
        var inner = new UiMessage("Dynamic.InternalInstall.Backup.Failed", unknownRawProse, localPath);
        var message = new UiMessage("Dynamic.Diagnostics.Failed", inner);
        var text = new UiText(UiText.ChineseLanguage);

        var chineseDisplay = text.FormatForDisplay(message);
        Assert.Contains(localPath, chineseDisplay);
        Assert.DoesNotContain(unknownRawProse, chineseDisplay);
        Assert.Contains(localPath, text.Format(message));
        Assert.Contains(unknownRawProse, text.Format(message));

        text.SetLanguage(UiText.EnglishLanguage);
        var englishDisplay = text.FormatForDisplay(message);
        Assert.Contains(localPath, englishDisplay);
        Assert.DoesNotContain(unknownRawProse, englishDisplay);
        Assert.Contains(localPath, text.Format(message));
        Assert.Contains(unknownRawProse, text.Format(message));
    }

    [Fact]
    public void AlreadyLocalizedTypedFailure_InMaskedSlot_UsesItsOwnUiMessageInBothLanguages()
    {
        var unauthorized = new UiMessage("Dynamic.Global.Error.UnauthorizedAdb");
        var message = new UiMessage("Dynamic.Diagnostics.Failed", unauthorized);
        var text = new UiText(UiText.ChineseLanguage);

        var chineseDisplay = text.FormatForDisplay(message);
        var chineseUnauthorized = new UiText(UiText.ChineseLanguage).FormatForDisplay(unauthorized);
        Assert.Contains(chineseUnauthorized, chineseDisplay);

        text.SetLanguage(UiText.EnglishLanguage);
        var englishDisplay = text.FormatForDisplay(message);
        var englishUnauthorized = new UiText(UiText.EnglishLanguage).FormatForDisplay(unauthorized);
        Assert.Contains(englishUnauthorized, englishDisplay);
        Assert.NotEqual(chineseUnauthorized, englishUnauthorized);
    }

    [Fact]
    public void InternalInstallPreparationFailure_MessageIsLocalizedInBothLanguages()
    {
        var exception = Assert.Throws<InternalInstallWorkDiskSpaceException>(
            () => InternalInstallPreparationContext.RequireDirectExecutionSpaceAvailable(38408839168L, 14706802688L));
        var message = UiText.InternalInstallPreparationFailure(exception);

        var text = new UiText(UiText.ChineseLanguage);
        var chinese = text.Format(message);
        Assert.Contains("电脑的临时文件盘空间不足", chinese);
        Assert.Contains("13.69 GiB", chinese);
        Assert.Contains("35.78 GiB", chinese);
        Assert.DoesNotContain("insufficient-direct-execution-space", chinese);

        text.SetLanguage(UiText.EnglishLanguage);
        var english = text.Format(message);
        Assert.Contains("Not enough free space", english);
        Assert.Contains("13.69 GiB", english);
        Assert.Contains("35.78 GiB", english);
    }

    [Fact]
    public void WorkDiskSpaceFailure_InSharedStatusPreservesCapacityAndDiagnostic()
    {
        var exception = Assert.Throws<InternalInstallWorkDiskSpaceException>(
            () => InternalInstallPreparationContext.RequireDirectExecutionSpaceAvailable(38408839168L, 14706802688L));
        var message = new UiMessage("Dynamic.InternalInstall.Capacity.ImportFailed", exception);
        var text = new UiText(UiText.ChineseLanguage);

        Assert.Contains("13.69 GiB", text.FormatForDisplay(message));
        Assert.Contains("35.78 GiB", text.FormatForDisplay(message));
        Assert.DoesNotContain("insufficient-direct-execution-space", text.FormatForDisplay(message));
        Assert.Contains(exception.Message, text.Format(message));

        text.SetLanguage(UiText.EnglishLanguage);
        Assert.Contains("13.69 GiB", text.FormatForDisplay(message));
        Assert.Contains("35.78 GiB", text.FormatForDisplay(message));
    }

    [Theory]
    [InlineData(UiText.ChineseLanguage, "B 槽", "启动镜像")]
    [InlineData(UiText.EnglishLanguage, "slot B", "boot image")]
    public void InternalInstallPreparationFailure_BootImageLimitShowsAffectedSlotAndSizes(
        string language, string slot, string image)
    {
        const string detail = "packed raw image exceeds the slot loader budget (raw image actual bytes 67108864, MaxRawBytes limit 62914560, target slot 'b', source slot 'a')";
        var failure = UiText.InternalInstallPreparationFailure(new System.IO.InvalidDataException(detail));
        var message = new UiText(language).FormatForDisplay(failure);

        Assert.Contains(slot, message);
        Assert.Contains(image, message);
        Assert.Contains("64", message);
        Assert.Contains("60", message);
        Assert.DoesNotContain("MaxRawBytes", message);
    }

    [Fact]
    public void MaintenanceResourceFailure_UsesItsTypedCauseInChinese()
    {
        var exception = new MaintenanceException(MaintenanceErrorCode.AssetHashMismatch, "sha256 check failed for the reviewed asset");
        var message = new UiMessage("Dynamic.ToolsStatus.PrepareFailed", exception);
        var text = new UiText(UiText.ChineseLanguage);

        Assert.Contains("校验", text.FormatForDisplay(message));
        Assert.DoesNotContain("sha256 check failed", text.FormatForDisplay(message));
        Assert.Contains(exception.Message, text.Format(message));
    }

    [Fact]
    public void InternalInstallPreparationFailure_NearThresholdRoundsConservatively()
    {
        const long gib = 1073741824L;
        const long required = 36L * gib;
        var exception = Assert.Throws<InternalInstallWorkDiskSpaceException>(
            () => InternalInstallPreparationContext.RequireDirectExecutionSpaceAvailable(
                required,
                required - (4L * 1024 * 1024)));
        var message = UiText.InternalInstallPreparationFailure(exception);

        var english = new UiText(UiText.EnglishLanguage).Format(message);
        Assert.Contains("35.99 GiB", english);
        Assert.Contains("36.00 GiB", english);
    }

    [Fact]
    public void InternalInstallPreparationFailure_UnexpectedExceptionHasNoRawEnglish()
    {
        var message = UiText.InternalInstallPreparationFailure(
            new InvalidOperationException("raw english detail"));

        var chinese = new UiText(UiText.ChineseLanguage).Format(message);
        Assert.DoesNotContain("raw english detail", chinese);
        Assert.Contains("安装日志", chinese);

        var english = new UiText(UiText.EnglishLanguage).Format(message);
        Assert.DoesNotContain("raw english detail", english);
        Assert.Contains("install log", english);
    }

    [Theory]
    [InlineData("blocked-locked-g4-first-install-firmware: TB322FC with locked bootloader and ZUXOS 1.5.10.358 cannot be safely installed.", BootloaderState.Locked)]
    [InlineData("requires-compatible-bootloader: TB323FU requires a compatible bootloader.", BootloaderState.Locked)]
    [InlineData("requires-compatible-bootloader: TB710FU requires a compatible bootloader.", BootloaderState.Locked)]
    [InlineData("gbl-boot-chain-key-mismatch: TB322FC boot chain key does not match.", BootloaderState.Locked)]
    [InlineData("gbl-runtime-region-unknown: TB323FU runtime region is unknown.", BootloaderState.Locked)]
    public void InternalInstallPreparationFailure_LockedBootloaderCompatibilityIsLocalized(string detail, BootloaderState state)
    {
        var exception = new InvalidOperationException(detail);
        var message = UiText.InternalInstallPreparationFailure(exception, state);

        var chinese = new UiText(UiText.ChineseLanguage).Format(message);
        Assert.Contains("免解锁方案检查不通过，请解锁 BL。", chinese);
        Assert.Contains(detail, chinese);

        var english = new UiText(UiText.EnglishLanguage).Format(message);
        Assert.Contains("The check for installation with a locked bootloader failed. Please unlock the bootloader.", english);
        Assert.Contains(detail, english);
    }

    [Theory]
    [InlineData("gbl-boot-chain-key-mismatch: some detail", BootloaderState.Unknown, "Dynamic.InternalInstall.Prepared.GblBootChainKeyMismatch")]
    [InlineData("gbl-boot-chain-key-mismatch: some detail", BootloaderState.Unlocked, "Dynamic.InternalInstall.Prepared.GblBootChainKeyMismatch")]
    [InlineData("gbl-runtime-region-unknown: some detail", BootloaderState.Unknown, "Dynamic.InternalInstall.Prepared.GblRuntimeRegionUnknown")]
    [InlineData("gbl-runtime-region-unknown: some detail", BootloaderState.Unlocked, "Dynamic.InternalInstall.Prepared.GblRuntimeRegionUnknown")]
    public void InternalInstallPreparationFailure_GblCodesPreserveSpecificMessage(string detail, BootloaderState state, string expectedKey)
    {
        var exception = new InvalidOperationException(detail);
        var message = UiText.InternalInstallPreparationFailure(exception, state);

        Assert.Equal(expectedKey, message.Key);

        var english = new UiText(UiText.EnglishLanguage).Format(message);
        Assert.DoesNotContain("Please unlock the bootloader", english);
    }

    [Fact]
    public void InternalInstallPreparationFailure_PlainIoExceptionUsesGenericMessage()
    {
        var exception = new IOException("disk fell over");
        var message = UiText.InternalInstallPreparationFailure(exception, BootloaderState.Unlocked);

        Assert.Equal("Dynamic.InternalInstall.Prepared.Failed", message.Key);

        var english = new UiText(UiText.EnglishLanguage).Format(message);
        Assert.DoesNotContain("Please unlock the bootloader", english);
        Assert.DoesNotContain("免解锁方案检查不通过", new UiText(UiText.ChineseLanguage).Format(message));
        Assert.DoesNotContain("disk fell over", english);
    }

    [Theory]
    [InlineData(BootloaderState.Unlocked)]
    [InlineData(BootloaderState.Unknown)]
    [InlineData(null)]
    public void InternalInstallPreparationFailure_RequiresCompatibleBootloaderStaysGeneric(BootloaderState? state)
    {
        var exception = new InvalidOperationException("requires-compatible-bootloader: some future detail");
        var message = UiText.InternalInstallPreparationFailure(exception, state);

        Assert.Equal("Dynamic.InternalInstall.Prepared.Failed", message.Key);

        var english = new UiText(UiText.EnglishLanguage).Format(message);
        Assert.DoesNotContain("Please unlock the bootloader", english);
        Assert.DoesNotContain("requires-compatible-bootloader", english);
    }

    [Fact]
    public void InternalInstallPreparationFailure_UnknownFreeSpaceIsLocalized()
    {
        var exception = Assert.Throws<InternalInstallWorkDiskSpaceException>(
            () => InternalInstallPreparationContext.RequireDirectExecutionSpaceAvailable(38408839168L, null));
        var message = UiText.InternalInstallPreparationFailure(exception);

        var chinese = new UiText(UiText.ChineseLanguage).Format(message);
        Assert.Contains("无法读取电脑临时文件盘的剩余空间", chinese);
        Assert.Contains("35.78 GiB", chinese);
        Assert.Contains("请检查磁盘是否可用", chinese);
    }

    [Fact]
    public void InternalInstallPreparationFailure_LocalizedInvalidOperationMapsToItsKey()
    {
        var expected = new UiMessage("Dynamic.InternalInstall.Prepared.NeedInputs");
        var message = UiText.InternalInstallPreparationFailure(
            new LocalizedInvalidOperationException(expected));

        Assert.Equal("Dynamic.InternalInstall.Prepared.NeedInputs", message.Key);
        Assert.Empty(message.Arguments);
        var chinese = new UiText(UiText.ChineseLanguage).Format(message);
        Assert.Contains("请确认已分配空间、校验系统包，并保持平板的 9008 连接", chinese);
    }

    [Fact]
    public void EmbeddedCatalogs_ResolveAuthorNameInBothLanguagesAndDefaultToChinese()
    {
        // The real catalogs must be readable from this assembly, otherwise every assertion below
        // would silently compare catalog keys against each other.
        var chinese = new UiText("zh-CN");
        Assert.Equal(UiText.ChineseLanguage, chinese.Language);
        Assert.Equal("单翼", chinese.Get("Static.AuthorName"));
        Assert.Equal("已授权", chinese.Get("Dynamic.ConnectionState.Authorized"));
        Assert.Equal("ArmadaOS Freedom 安装助手 9.8.7", chinese.Get("Static.Title", "9.8.7"));

        var english = new UiText("en");
        Assert.Equal(UiText.EnglishLanguage, english.Language);
        Assert.Equal("danyi", english.Get("Static.AuthorName"));
        Assert.Equal("Authorized", english.Get("Dynamic.ConnectionState.Authorized"));
        Assert.Equal("ArmadaOS Freedom Installer 9.8.7", english.Get("Static.Title", "9.8.7"));

        var unknown = new UiText("fr-FR");
        Assert.Equal(UiText.DefaultLanguage, unknown.Language);
        Assert.Equal("单翼", unknown.Get("Static.AuthorName"));
    }

    [Fact]
    public void CatalogPairs_ShipIdenticalKeySetsWithNonEmptyText()
    {
        AssertCatalogPair(StaticChineseCatalog, StaticEnglishCatalog, minimumKeys: 50);
        AssertCatalogPair(DynamicChineseCatalog, DynamicEnglishCatalog, minimumKeys: 100);
    }

    [Fact]
    public void EveryShippedFormat_RendersWithEnoughDummyArgumentsInBothLanguages()
    {
        var chinese = new UiText(UiText.ChineseLanguage);
        var english = new UiText(UiText.EnglishLanguage);

        AssertAllFormatsRender(StaticChineseCatalog, chinese);
        AssertAllFormatsRender(StaticEnglishCatalog, english);
        AssertAllFormatsRender(DynamicChineseCatalog, chinese);
        AssertAllFormatsRender(DynamicEnglishCatalog, english);
    }

    [Fact]
    public void RetainedNestedMessage_RerendersNestedLabelAfterSetLanguage()
    {
        var text = new UiText(UiText.ChineseLanguage);

        // «serial · state»: the state is itself a catalog message, so switching language must
        // re-render both halves of the already-built message.
        var message = new UiMessage(
            "Dynamic.DeviceEntry",
            "FIXTURE001",
            new UiMessage("Dynamic.ConnectionState.Authorized"));

        Assert.Equal("FIXTURE001 \u00B7 已授权", text.Format(message));

        // 免解锁安装方式只作为第二段追加在主摘要之后；语言切换时两段都必须重渲染。
        var withMode = new UiMessage(
            "Dynamic.InternalInstall.Execution.WithFailureDetails",
            message,
            new UiMessage("Dynamic.InternalInstall.Prepared.GblInstallMode"));

        Assert.Equal("安装方式：免解锁安装", text.Get("Dynamic.InternalInstall.Prepared.GblInstallMode"));
        Assert.Equal(
            "FIXTURE001 \u00B7 已授权\n\n安装方式：免解锁安装",
            text.Format(withMode));

        text.SetLanguage(UiText.EnglishLanguage);

        Assert.Equal(UiText.EnglishLanguage, text.Language);
        Assert.Equal("FIXTURE001 \u00B7 Authorized", text.Format(message));
        Assert.Equal(
            "Installation method: without unlocking the bootloader",
            text.Get("Dynamic.InternalInstall.Prepared.GblInstallMode"));
        Assert.Equal(
            "FIXTURE001 \u00B7 Authorized\n\nInstallation method: without unlocking the bootloader",
            text.Format(withMode));
    }

    /// <summary>
    /// 免解锁安装准备阶段的专用提示与阶段名都不带格式参数，必须直接渲染出真实文案，
    /// 不能回落成键名或残留格式花括号。
    /// </summary>
    [Theory]
    [InlineData(UiText.ChineseLanguage)]
    [InlineData(UiText.EnglishLanguage)]
    public void DynamicCatalog_RendersNewGblMessagesWithoutArguments(string language)
    {
        var text = new UiText(language);
        var keys = new[]
        {
            "Dynamic.InternalInstall.Prepared.CompleteBackupWithEfispRequired",
            "Dynamic.InternalInstall.Prepared.GblBootChainKeyMismatch",
            "Dynamic.InternalInstall.Prepared.GblRuntimeRegionUnknown",
            "Dynamic.InternalInstall.Prepared.GblInstallMode",
            "Dynamic.InternalInstall.Execution.Stage.WritingGblRuntime",
            "Dynamic.InternalInstall.Execution.Stage.WritingAbl",
            "Dynamic.InternalInstall.Execution.Stage.WritingGblAbl",
        };

        foreach (var key in keys)
        {
            var rendered = text.Get(key);

            Assert.False(string.IsNullOrWhiteSpace(rendered), $"{language}: '{key}' rendered empty.");
            Assert.NotEqual(key, rendered);
            Assert.DoesNotContain("{", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("}", rendered, StringComparison.Ordinal);
        }

        var replacementMode = text.Format(new UiMessage(
            "Dynamic.InternalInstall.Prepared.GblAblReplacementInstallMode",
            "a, b",
            "—"));
        Assert.Contains("a, b", replacementMode, StringComparison.Ordinal);
        Assert.Contains("088", replacementMode, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", replacementMode, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Dynamic.InternalInstall.Prepared.GblAblReplacementInstallMode",
            replacementMode,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The board recovery image wording must render real values in both languages and keep the
    /// board-phase transfer labels distinct from the generic ones and from their own keys.
    /// </summary>
    [Theory]
    [InlineData(UiText.ChineseLanguage)]
    [InlineData(UiText.EnglishLanguage)]
    public void DynamicCatalog_RendersBoardRecoveryImageValuesAndDistinctBoardPhases(string language)
    {
        var text = new UiText(language);

        var log = text.Format(new UiMessage(
            "Dynamic.InternalInstall.Prepared.RecoveryImageLog",
            "115.3 MiB",
            "2 GiB"));

        Assert.Contains("115.3 MiB", log);
        Assert.Contains("2 GiB", log);
        Assert.DoesNotContain("{0}", log);
        Assert.DoesNotContain("{1}", log);
        Assert.DoesNotContain("Dynamic.InternalInstall.Prepared.RecoveryImageLog", log);

        var writing = text.Get("Dynamic.InternalInstall.Execution.BoardIoPhase.Writing");
        var readingBack = text.Get("Dynamic.InternalInstall.Execution.BoardIoPhase.ReadingBack");
        Assert.NotEqual(writing, readingBack);
        Assert.NotEqual("Dynamic.InternalInstall.Execution.BoardIoPhase.Writing", writing);
        Assert.NotEqual("Dynamic.InternalInstall.Execution.BoardIoPhase.ReadingBack", readingBack);

        var expand = text.Get("Dynamic.InternalInstall.Log.Expand");
        var collapse = text.Get("Dynamic.InternalInstall.Log.Collapse");
        Assert.NotEqual(expand, collapse);
        Assert.NotEqual("Dynamic.InternalInstall.Log.Expand", expand);
        Assert.NotEqual("Dynamic.InternalInstall.Log.Collapse", collapse);
    }

    private static void AssertCatalogPair(string chineseResourceName, string englishResourceName, int minimumKeys)
    {
        var chinese = ReadCatalog(chineseResourceName);
        var english = ReadCatalog(englishResourceName);

        Assert.True(
            chinese.Count >= minimumKeys,
            $"{chineseResourceName} carries {chinese.Count} keys but at least {minimumKeys} are expected.");
        Assert.True(
            english.Count >= minimumKeys,
            $"{englishResourceName} carries {english.Count} keys but at least {minimumKeys} are expected.");

        Assert.Equal(
            chinese.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            english.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());

        Assert.All(
            chinese,
            pair => Assert.False(
                string.IsNullOrWhiteSpace(pair.Value),
                $"{chineseResourceName}: '{pair.Key}' is empty."));
        Assert.All(
            english,
            pair => Assert.False(
                string.IsNullOrWhiteSpace(pair.Value),
                $"{englishResourceName}: '{pair.Key}' is empty."));
    }

    private static void AssertAllFormatsRender(string resourceName, UiText text)
    {
        var catalog = ReadCatalog(resourceName);
        Assert.NotEmpty(catalog);

        foreach (var pair in catalog)
        {
            var arguments = DummyArguments(pair.Value);
            var rendered = text.Get(pair.Key, arguments);

            Assert.False(
                string.IsNullOrEmpty(rendered),
                $"{resourceName}: '{pair.Key}' rendered empty.");
            Assert.DoesNotContain("{", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("}", rendered, StringComparison.Ordinal);
        }
    }

    /// <summary>One dummy argument per placeholder index, so every composite format is fully applied.</summary>
    private static object?[] DummyArguments(string format)
    {
        var highestIndex = -1;
        foreach (Match match in PlaceholderPattern.Matches(format))
        {
            highestIndex = Math.Max(
                highestIndex,
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        if (highestIndex < 0)
        {
            return Array.Empty<object?>();
        }

        var arguments = new object?[highestIndex + 1];
        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = "ARG" + index.ToString(CultureInfo.InvariantCulture);
        }

        return arguments;
    }

    private static Dictionary<string, string> ReadCatalog(string resourceName)
    {
        var stream = typeof(UiText).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);

        using var document = JsonDocument.Parse(stream!);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
            catalog[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return catalog;
    }
}
