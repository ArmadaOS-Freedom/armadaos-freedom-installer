using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

/// <summary>
/// One localizable message: a catalog key plus a snapshot of its arguments.
/// Arguments may themselves be <see cref="UiMessage"/> values, so text such as
/// «serial · state» keeps switching language as a whole.
/// </summary>
internal sealed record UiMessage(string Key, params object?[] Arguments);

/// <summary>
/// Thrown when a preparation step fails with text that is already localized in the UI catalog.
/// Carries the <see cref="UiMessage"/> so the failure mapper reproduces the same key instead of
/// falling back to the generic message.
/// </summary>
internal sealed class LocalizedInvalidOperationException : InvalidOperationException
{
    public LocalizedInvalidOperationException(UiMessage message, string? diagnostic = null, Exception? innerException = null)
        : base(diagnostic ?? message.Key, innerException)
    {
        UserMessage = message;
    }

    public UiMessage UserMessage { get; }
}

/// <summary>
/// Localized UI text. Reads the embedded catalogs <c>FreedomInstaller.Strings.Static.&lt;lang&gt;.json</c>
/// and <c>FreedomInstaller.Strings.Dynamic.&lt;lang&gt;.json</c> from the executing assembly and depends on
/// the BCL only (no WPF types), so it can be linked into the test assembly and exercised on Linux.
/// <para>
/// Rules kept deliberately simple: an unknown key renders the key itself, a missing English entry falls
/// back to the Chinese catalog, and a missing or broken catalog never throws — the UI keeps working with
/// whatever text resolves instead of failing to start.
/// </para>
/// </summary>
internal sealed class UiText
{
    /// <summary>Chinese (Simplified) is the product default and the fallback catalog.</summary>
    internal const string ChineseLanguage = "zh-CN";

    /// <summary>Default language used when no preference is stored or the stored value is unknown.</summary>
    internal const string DefaultLanguage = ChineseLanguage;

    /// <summary>The only non-default UI language.</summary>
    internal const string EnglishLanguage = "en";

    /// <summary>Catalog key prefixes; every catalog entry carries one of them.</summary>
    internal const string StaticPrefix = "Static.";

    /// <summary>Catalog key prefix for runtime messages (status lines, log lines, summaries).</summary>
    internal const string DynamicPrefix = "Dynamic.";

    /// <summary>Logical resource name prefix: <c>FreedomInstaller.Strings.&lt;FileName&gt;.json</c>.</summary>
    internal const string ResourceNamePrefix = "FreedomInstaller.Strings.";

    /// <summary>Depth limit for nested <see cref="UiMessage"/> arguments; a self-referencing message cannot hang the UI.</summary>
    private const int MaxFormatDepth = 8;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Dictionary<string, string> _defaultStrings;

    private readonly Dictionary<string, string> _englishStrings;

    private string _language;

    internal UiText(string language)
        : this(language, LoadCatalog(ChineseLanguage), LoadCatalog(EnglishLanguage))
    {
    }

    /// <summary>
    /// Catalog-injecting constructor used by tests. The production constructor above always loads the
    /// embedded catalogs; this overload only exists so formatting can be tested without adding entries
    /// to the shipped catalogs.
    /// </summary>
    internal UiText(string language, Dictionary<string, string> defaultStrings, Dictionary<string, string> englishStrings)
    {
        _defaultStrings = defaultStrings ?? throw new ArgumentNullException(nameof(defaultStrings));
        _englishStrings = englishStrings ?? throw new ArgumentNullException(nameof(englishStrings));
        _language = NormalizeLanguage(language);
    }

    /// <summary>Currently selected language: <c>zh-CN</c> or <c>en</c>.</summary>
    internal string Language => _language;

    /// <summary>True when the English catalog is active.</summary>
    internal bool IsEnglish => string.Equals(_language, EnglishLanguage, StringComparison.Ordinal);

    /// <summary>
    /// Every catalog key with its value in the current language (English falls back to Chinese per key),
    /// ordered by key. Used to fill the window resources that the XAML reads with <c>DynamicResource</c>.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, string>> Strings
    {
        get
        {
            var keys = new List<string>(_defaultStrings.Keys);
            foreach (var key in _englishStrings.Keys)
            {
                if (!_defaultStrings.ContainsKey(key))
                {
                    keys.Add(key);
                }
            }

            keys.Sort(StringComparer.Ordinal);

            var pairs = new List<KeyValuePair<string, string>>(keys.Count);
            foreach (var key in keys)
            {
                pairs.Add(new KeyValuePair<string, string>(key, ResolveFormat(key)));
            }

            return pairs;
        }
    }

    /// <summary>Unknown languages are not supported and resolve to <see cref="DefaultLanguage"/>.</summary>
    internal static string NormalizeLanguage(string? language) =>
        string.Equals((language ?? string.Empty).Trim(), EnglishLanguage, StringComparison.OrdinalIgnoreCase)
            ? EnglishLanguage
            : DefaultLanguage;

    internal void SetLanguage(string language) => _language = NormalizeLanguage(language);

    /// <summary>Wraps a device model code so its visible name follows the catalog.</summary>
    internal static UiMessage ModelName(string? code)
    {
        var normalized = (code ?? string.Empty).Trim();
        var key = normalized.ToUpperInvariant() switch
        {
            "TB322FC" => "Static.ModelName.Tb322fc",
            "TB323FU" => "Static.ModelName.Tb323fu",
            "TB710FU" => "Static.ModelName.Tb710fu",
            _ => null,
        };

        return key is null
            ? new UiMessage(DynamicPrefix + "RawValue", string.IsNullOrWhiteSpace(normalized) ? "\u2014" : code)
            : new UiMessage(key);
    }

    /// <summary>
    /// Resolves <paramref name="key"/> and applies <paramref name="args"/> with invariant formatting.
    /// An unknown key returns the key; invalid placeholders return the unformatted catalog text.
    /// </summary>
    internal string Get(string key, params object?[] args) =>
        string.IsNullOrEmpty(key) ? string.Empty : FormatKey(key, args, 0, false);

    /// <summary>
    /// Resolves <paramref name="key"/> for the visible UI surface. In display mode
    /// <see cref="DisplayErrorArgumentMask(string)"/> marks arguments that carry diagnostic
    /// values; those are rendered as a localized semantic message instead of being shown verbatim.
    /// </summary>
    internal string GetForDisplay(string key, params object?[] args) =>
        string.IsNullOrEmpty(key) ? string.Empty : FormatKey(key, args, 0, true);

    internal static UiMessage InternalInstallExecutionFailure(InternalInstallResult result) =>
        DisplayError((object?)result.WorkDiskSpaceFailure ?? result.Diagnostic ?? result.ErrorCode);

    /// <summary>
    /// Maps a raw or untyped value flowing to the visible surface onto a localized semantic message.
    /// Already-localized messages are passed through; null or blank values become a neutral fallback.
    /// </summary>
    internal static UiMessage DisplayError(object? value)
    {
        if (value is LocalizedInvalidOperationException localized)
            value = localized.UserMessage;
        if (value is UiMessage message)
        {
            if (message.Key == "Dynamic.RawValue" || message.Key == "Dynamic.OfflineUpdate.Value")
                return new UiMessage("Dynamic.ErrorDetail.Generic");
            return message;
        }

        if (value is InternalInstallWorkDiskSpaceException space)
        {
            return WorkDiskSpaceFailure(space);
        }

        if (value is BackupDestinationSpaceException backupSpace)
        {
            return new UiMessage("Dynamic.ErrorDetail.BackupDestinationSpace",
                RoundGiBDown(backupSpace.AvailableBytes), RoundGiBUp(backupSpace.RequiredBytes));
        }

        if (value is BoardRecoverySpaceException boardSpace)
        {
            return new UiMessage("Dynamic.ErrorDetail.BoardRecoverySpace",
                RoundGiBDown(boardSpace.AvailableBytes), RoundGiBUp(boardSpace.RequiredBytes));
        }

        if (value is InternalInstallLinuxSpaceException linuxSpace)
        {
            return new UiMessage(
                linuxSpace.AndroidGiB > linuxSpace.MinimumAndroidGiB
                    ? "Dynamic.ErrorDetail.LinuxSpaceAdjustAllocation"
                    : "Dynamic.ErrorDetail.LinuxSpaceTooSmall",
                RoundGiBDown(linuxSpace.AvailableLinuxBytes),
                RoundGiBUp(linuxSpace.RequiredLinuxBytes),
                linuxSpace.MinimumAndroidGiB);
        }

        if (value is MaintenanceException maintenance)
        {
            return new UiMessage("Dynamic.ErrorDetail.Maintenance" + (maintenance.Code switch
            {
                MaintenanceErrorCode.UnsupportedProfile => "Unsupported",
                MaintenanceErrorCode.ModelMismatch => "ModelMismatch",
                MaintenanceErrorCode.BuildMismatch => "BuildMismatch",
                MaintenanceErrorCode.MissingAsset => "MissingAsset",
                MaintenanceErrorCode.AssetSizeMismatch or MaintenanceErrorCode.AssetHashMismatch => "Integrity",
                MaintenanceErrorCode.UnsafeEnvironment => "WorkDirectory",
                MaintenanceErrorCode.HandshakeMismatch => "HandshakeMismatch",
                _ => "InvalidBundle",
            }));
        }

        if (value is InternalStorageReadException storage)
        {
            return new UiMessage(storage.Failure == InternalStorageReadFailure.Unavailable
                ? "Dynamic.ErrorDetail.StorageUnavailable"
                : "Dynamic.ErrorDetail.StorageInvalid");
        }

        var text = value is Exception exception ? exception.Message : value as string;
        if (!string.IsNullOrEmpty(text))
        {
            var match = Regex.Match(text, @"\ANo EDL endpoint for serial \S+ appeared within ([0-9]+(?:[.,][0-9]+)?) seconds\.\z");
            if (match.Success)
                return new UiMessage("Dynamic.ErrorDetail.EdlTimeout", match.Groups[1].Value);
            if (BootImageSizeFailure(text) is { } bootImageSize)
                return bootImageSize;
            if (text.StartsWith("requires-compatible-bootloader:", StringComparison.Ordinal) || text.StartsWith("blocked-locked-g4-first-install-firmware:", StringComparison.Ordinal))
                return new UiMessage("Dynamic.ErrorDetail.Compatibility");
        }
        var key = value switch
        {
            TimeoutException => "Timeout",
            UnauthorizedAccessException or System.Security.SecurityException => "Security",
            FileNotFoundException or DirectoryNotFoundException => "FileNotFound",
            IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027)
                || (!OperatingSystem.IsWindows() && io.HResult == 28) => "DiskFull",
            OfflineUpdateRemoteException => "OfflineRequestRejected",
            InvalidDataException or JsonException => "InvalidData",
            NotSupportedException => "NotSupported",
            OperationCanceledException => "Canceled",
            null => "Unknown",
            _ => "Generic",
        };
        return new UiMessage("Dynamic.ErrorDetail." + key);
    }

    private static readonly (string Pattern, string Key)[] PackageReasonPatterns =
    {
        (@"\Aartifact \S+ 声明的文件缺失：(.+)。\z", "MissingFile"),
        (@"\A(?:安装包元数据缺失|合集缺少)[:：]?\s*(\S+)。\z", "MissingFile"),
        (@"\A(?:(?:CI 下载包|合集)缺少(?:签名声明的)?分卷|分卷文件缺失)：(?:.*[/\\])?([^/\\]+)。\z", "MissingVolume"),
        (@"\A(?:CI 下载包缺少签名文件|缺少升级包描述文件或签名|升级包描述文件与签名必须同时存在)：.+。\z", "MissingSignature"),
        (@"\A分卷数量不匹配：实际为 ([0-9]+)，描述文件要求 ([0-9]+)。\z", "VolumeCount"),
        (@"\Aartifact \S+ 的文件长度 ([0-9]+) 与声明的 ([0-9]+) 不一致：(.+)。\z", "ArtifactSize"),
        (@"\A分卷 (.+) 大小不匹配：实际为 ([0-9]+)，期望 ([0-9]+)。\z", "VolumeSize"),
        (@"\A(?:artifact \S+ 的 SHA256 与声明不一致：|升级包条目内容哈希不匹配：)(.+)。\z", "FileChecksum"),
        (@"\A分卷 (.+) 的 SHA-256 校验失败。\z", "VolumeChecksum"),
        (@"\A升级包整体 SHA-256 校验失败。\z", "ArchiveChecksum"),
        (@"\A分卷合并后的整体 SHA-256 或字节数校验失败。\z", "ArchiveChecksum"),
        (@"\A安装包的 deviceModel (\S+) 与期望型号 (\S+) 不一致，拒绝使用。\z", "WrongModel"),
        (@"\A合集不包含请求的 deviceModel (\S+)，不做缺省型号推断。\z", "MissingModel"),
        (@"\A安装包没有声明安装目标 (\S+)，拒绝安装到该目标。\z", "WrongTarget"),
        (@"\Apackage\.json 没有声明 installTargets，旧版安装包只允许安装到设备内置存储。\z", "WrongTarget"),
        (@"\A(?:合集|升级包描述文件)版本 (\S+) 与(?:所选安装包|合集)版本 (\S+) 不一致，拒绝使用。\z", "VersionMismatch"),
        (@"\A(?:合集包含加密条目，当前不支持带密码的归档|升级包条目已加密|升级包内不允许存在加密项)：(.+)。\z", "EncryptedEntry"),
        (@"\A(?:合集包含重复条目（忽略大小写）|升级包包含重复的 ZIP 条目|升级包包含重复条目)：(.+)。\z", "DuplicateEntry"),
    };

    private static UiMessage? PackageImportIssue(PackageValidationIssue? issue) => issue?.Kind switch
    {
        PackageValidationIssueKind.MissingFile => new UiMessage("Dynamic.Package.Issue.MissingFile", issue.FileName),
        PackageValidationIssueKind.DiskFull => new UiMessage("Dynamic.ErrorDetail.DiskFull"),
        PackageValidationIssueKind.AccessDenied => new UiMessage("Dynamic.ErrorDetail.Security"),
        _ => null,
    };

    private static UiMessage PackageReason(string reason)
    {
        const string importPrefix = "导入安装包时发生错误：";
        const string readPrefix = "读取安装包时发生错误：";
        var wrapped = reason.StartsWith(importPrefix, StringComparison.Ordinal)
            || reason.StartsWith(readPrefix, StringComparison.Ordinal);
        if (wrapped)
        {
            var prefix = reason.StartsWith(importPrefix, StringComparison.Ordinal) ? importPrefix : readPrefix;
            reason = reason[prefix.Length..];
        }

        foreach (var (pattern, key) in PackageReasonPatterns)
        {
            var match = Regex.Match(reason, pattern, RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            var arguments = new object?[match.Groups.Count - 1];
            for (var index = 0; index < arguments.Length; index++)
                arguments[index] = match.Groups[index + 1].Value;
            return new UiMessage("Dynamic.Package.Issue." + key, arguments);
        }

        if (reason.Contains("signature", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("签名校验", StringComparison.Ordinal))
            return new UiMessage("Dynamic.Package.Issue.Signature");
        if (reason.Contains("SHA256", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("SHA-256", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("哈希不匹配", StringComparison.Ordinal))
            return new UiMessage("Dynamic.Package.Issue.Integrity");
        if (reason.Contains("分卷", StringComparison.Ordinal))
            return new UiMessage("Dynamic.Package.Issue.Volumes");
        if (reason.StartsWith("工作目录", StringComparison.Ordinal))
            return new UiMessage("Dynamic.ErrorDetail.MaintenanceWorkDirectory");
        if (reason.Contains("清单", StringComparison.Ordinal) || reason.Contains("package.json", StringComparison.Ordinal)
            || reason.Contains("bundle.json", StringComparison.Ordinal) || reason.Contains("schemaVersion", StringComparison.Ordinal))
            return new UiMessage("Dynamic.Package.Issue.Manifest");
        return new UiMessage(wrapped ? "Dynamic.Package.Issue.Unreadable" : "Dynamic.Package.Issue.Unrecognized");
    }

    private static UiMessage? BootImageSizeFailure(string detail)
    {
        var match = Regex.Match(detail,
            @"\Apacked raw image exceeds the slot loader budget \(raw image actual bytes ([0-9]+), MaxRawBytes limit ([0-9]+)(?:, target slot '([ab])', source slot '[ab]')?\)\z",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = Regex.Match(detail,
                @"\A(?:BSS-expanded Linux payload|the BSS-expanded Linux payload|the packed TF payload) ([0-9]+) exceeds the slot loader budget ([0-9]+)\z",
                RegexOptions.CultureInvariant);
        }
        if (!match.Success
            || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var actual)
            || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
        {
            return null;
        }

        const decimal bytesPerMiB = 1048576m;
        var actualMiB = actual / bytesPerMiB;
        var limitMiB = limit / bytesPerMiB;
        return match.Groups[3].Success
            ? new UiMessage("Dynamic.ErrorDetail.BootImageSlotLimit", match.Groups[3].Value.ToUpperInvariant(), actualMiB, limitMiB, actual, limit)
            : new UiMessage("Dynamic.ErrorDetail.BootImageLimit", actualMiB, limitMiB, actual, limit);
    }

    /// <summary>
    /// Uses install-specific compatibility messages where possible, then the shared error mapper.
    /// </summary>
    internal static UiMessage InternalInstallPreparationFailure(Exception exception, BootloaderState? lockState = null)
    {
        if (exception is LocalizedInvalidOperationException localized)
        {
            return localized.UserMessage;
        }

        if (BootloaderCompatibilityFailure(exception, lockState) is { } compatibility)
        {
            return compatibility;
        }

        if (exception is InvalidOperationException problem)
        {
            if (problem.Message.StartsWith("requires-complete-backup-with-efisp:", StringComparison.Ordinal))
            {
                return new UiMessage("Dynamic.InternalInstall.Prepared.CompleteBackupWithEfispRequired");
            }

            if (problem.Message.StartsWith("gbl-boot-chain-key-mismatch:", StringComparison.Ordinal))
            {
                return new UiMessage("Dynamic.InternalInstall.Prepared.GblBootChainKeyMismatch");
            }

            if (problem.Message.StartsWith("gbl-runtime-region-unknown:", StringComparison.Ordinal))
            {
                return new UiMessage("Dynamic.InternalInstall.Prepared.GblRuntimeRegionUnknown");
            }
        }

        var failure = DisplayError(exception);
        return failure.Key is "Dynamic.ErrorDetail.Generic" or "Dynamic.ErrorDetail.Compatibility"
            ? new UiMessage("Dynamic.InternalInstall.Prepared.Failed")
            : failure;
    }

    /// <summary>
    /// Maps a bootloader-compatibility rejection to the dedicated message when the device is
    /// confirmed locked, keeping the original diagnostic as detail. Returns null otherwise.
    /// </summary>
    internal static UiMessage? BootloaderCompatibilityFailure(Exception exception, BootloaderState? lockState = null)
    {
        if (exception is not InvalidOperationException problem)
        {
            return null;
        }

        if (problem.Message.StartsWith("blocked-locked-g4-first-install-firmware:", StringComparison.Ordinal))
        {
            return new UiMessage("Dynamic.InternalInstall.Prepared.CompatibilityRequired", problem.Message);
        }

        if (lockState == BootloaderState.Locked
            && (problem.Message.StartsWith("requires-compatible-bootloader:", StringComparison.Ordinal)
                || problem.Message.StartsWith("gbl-boot-chain-key-mismatch:", StringComparison.Ordinal)
                || problem.Message.StartsWith("gbl-runtime-region-unknown:", StringComparison.Ordinal)))
        {
            return new UiMessage("Dynamic.InternalInstall.Prepared.CompatibilityRequired", problem.Message);
        }

        return null;
    }

    private const decimal BytesPerGiB = 1073741824m;

    private static UiMessage WorkDiskSpaceFailure(InternalInstallWorkDiskSpaceException space) =>
        space.AvailableBytes is long available
            ? new UiMessage(
                "Dynamic.InternalInstall.Prepared.InsufficientDiskSpaceKnown",
                RoundGiBDown(available),
                RoundGiBUp(space.RequiredBytes))
            : new UiMessage(
                "Dynamic.InternalInstall.Prepared.InsufficientDiskSpaceUnknown",
                RoundGiBUp(space.RequiredBytes));

    private static decimal RoundGiBDown(long bytes) =>
        decimal.Floor(((decimal)bytes / BytesPerGiB) * 100m) / 100m;

    private static decimal RoundGiBUp(long bytes) =>
        decimal.Ceiling(((decimal)bytes / BytesPerGiB) * 100m) / 100m;

    /// <summary>Resolves a message, including nested <see cref="UiMessage"/> arguments.</summary>
    internal string Format(UiMessage message) =>
        message is null ? string.Empty : FormatKey(message.Key, message.Arguments, 0, false);

    /// <summary>
    /// Resolves a message for the visible UI surface: diagnostic-value positions are replaced with
    /// localized semantic text, while paths, numbers, model names and IDs pass through unchanged.
    /// </summary>
    internal string FormatForDisplay(UiMessage message) =>
        message is null ? string.Empty : FormatKey(message.Key, message.Arguments, 0, true);

    private string FormatKey(string key, object?[]? args, int depth, bool display)
    {
        var format = ResolveFormat(key);
        if (args is null || args.Length == 0)
        {
            return format;
        }

        var mask = DisplayErrorArgumentMask(key);
        var resolved = new object?[args.Length];
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (index < 32 && (mask & (1 << index)) != 0)
            {
                if (argument is PackageValidationResult validation)
                {
                    if (display && PackageImportIssue(validation.UserIssue) is { } issue)
                    {
                        argument = issue;
                    }
                    else if (display)
                    {
                        var reasons = new List<string>();
                        foreach (var reason in validation.Reasons)
                        {
                            var detail = PackageReason(reason);
                            var rendered = FormatKey(detail.Key, detail.Arguments, depth + 1, display);
                            if (!reasons.Contains(rendered))
                                reasons.Add(rendered);
                        }
                        argument = reasons.Count > 0
                            ? string.Join(ResolveFormat("Dynamic.Package.ReasonSeparator"), reasons)
                            : ResolveFormat("Dynamic.Package.Issue.Unrecognized");
                    }
                    else
                    {
                        argument = string.Join(" | ", validation.Reasons);
                    }
                }
                else if (display)
                {
                    argument = DisplayError(argument);
                }
                else if (argument is Exception exception)
                {
                    argument = exception.Message;
                }
            }

            resolved[index] = ResolveArgument(argument, depth, display);
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, resolved);
        }
        catch (FormatException)
        {
            // A catalog value with a malformed placeholder must not reach the UI as an exception.
            return format;
        }
    }

    private object? ResolveArgument(object? argument, int depth, bool display)
    {
        if (argument is UiMessage message)
        {
            if (depth < MaxFormatDepth)
            {
                return FormatKey(message.Key, message.Arguments, depth + 1, display);
            }

            return display ? ResolveFormat("Dynamic.ErrorDetail.Generic") : message;
        }

        return argument;
    }

    private static int DisplayErrorArgumentMask(string key) => key switch
    {
        "Dynamic.Diagnostics.Failed" or
        "Dynamic.Global.Status.ConnectFailed" or
        "Dynamic.Global.Status.RestorePrepareFailed" or
        "Dynamic.Global.Status.RestoreNotStarted" or
        "Dynamic.Global.Status.BackupFailed" or
        "Dynamic.Global.Status.RestoreUncertain" or
        "Dynamic.Global.Status.ResetUnknown" or
        "Dynamic.InternalInstall.Capacity.StatusWorkRootFailed" or
        "Dynamic.InternalInstall.Capacity.ImportFailed" or
        "Dynamic.InternalInstall.Capacity.Invalid" or
        "Dynamic.InternalInstall.Capacity.RangeFailed" or
        "Dynamic.InternalInstall.Capacity.PlanFailed" or
        "Dynamic.InternalInstall.Capacity.PackageCleanupFailed" or
        "Dynamic.InternalInstall.Capacity.ArtifactCleanupFailed" or
        "Dynamic.InternalInstall.Prepared.ReleaseFailed" or
        "Dynamic.InternalInstall.Prepared.CompatibilityRequired" or
        "Dynamic.InternalInstall.Execution.Acceptance.Failed" or
        "Dynamic.InternalInstall.Execution.PrewriteFailed" or
        "Dynamic.InternalInstall.Check.Failed" or
        "Dynamic.InternalInstall.Check.ReturnUnconfirmed" or
        "Dynamic.InternalInstall.Backup.ExistingEnvironmentWorkspaceUnavailable" or
        "Dynamic.InternalInstall.Backup.Failed" or
        "Dynamic.InternalInstall.Backup.ExistingFailed" or
        "Dynamic.InternalInstall.Exit.ProofFailed" or
        "Dynamic.InternalInstall.Exit.ResetUnknown" or
        "Dynamic.InternalInstall.Exit.ReturnUnconfirmed" or
        "Dynamic.InternalInstall.Exit.Mismatch" or
        "Dynamic.Capacity.NoteRangeFailed" or
        "Dynamic.Capacity.NotePlanFailed" or
        "Dynamic.ToolsStatus.PrepareFailed" or
        "Dynamic.DeviceHint.IdentityFailed" or
        "Dynamic.Layout.Unavailable" or
        "Dynamic.Layout.InvalidResponse" or
        "Dynamic.Layout.ReadFailed" or
        "Dynamic.Uninstall.StatusPlanFailed" or
        "Dynamic.Uninstall.NoteRecordFailed" or
        "Dynamic.UninstallFlow.Maintenance.StatusAndroidReadFailed" or
        "Dynamic.UninstallFlow.Maintenance.WorkRootCreateFailed" or
        "Dynamic.UninstallFlow.Maintenance.StatusBootFailed" or
        "Dynamic.UninstallFlow.Maintenance.StatusStartIncomplete" or
        "Dynamic.UninstallFlow.Maintenance.StatusPortsFailed" or
        "Dynamic.UninstallFlow.Maintenance.StatusConnectFailed" or
        "Dynamic.Package.StatusCleanupFailed" or
        "Dynamic.Progress.Failed" or
        "Dynamic.UninstallFlow.Input.RecoveryFailed" or
        "Dynamic.UninstallFlow.Input.Failed" or
        "Dynamic.UninstallFlow.Inspect.Invalid" or
        "Dynamic.UninstallFlow.Transaction.StatusQueryFailed" or
        "Dynamic.UninstallFlow.Transaction.StatusError" or
        "Dynamic.UninstallFlow.Run.FailedBeforeWrite" or
        "Dynamic.UninstallFlow.Run.FailedAfterWrite" or
        "Dynamic.UninstallFlow.Run.Uncertain" or
        "Dynamic.UninstallFlow.Verify.Failed" or
        "Dynamic.UninstallFlow.Edl.Failed" or
        "Dynamic.UninstallFlow.EdlRestore.PrepareFailed" or
        "Dynamic.UninstallFlow.EdlRestore.Run.Uncertain" or
        "Dynamic.UninstallFlow.EdlRestore.Run.ResetUnconfirmed" or
        "Dynamic.UninstallFlow.EdlVerify.Failed" or
        "Dynamic.UninstallFlow.EdlVerify.Pending" or
        "Dynamic.UserdataUninstall.Status.PrepareFailureDetail" or
        "Dynamic.RecoverySource.BoardInvalid" or
        "Dynamic.RecoverySource.Failed" or
        "Dynamic.Environment.WorkFailed" or
        "Dynamic.OfflineUpdate.StateFailed" or
        "Dynamic.OfflineUpdate.ErrorReceiptFlagBlocked" or
        "Dynamic.OfflineUpdate.ErrorHostKeyRejected" or
        "Dynamic.OfflineUpdate.ErrorPasswordAuthRejected" or
        "Dynamic.OfflineUpdate.ErrorUpdateComponentsMissing" or
        "Dynamic.OfflineUpdate.ErrorSudoUnavailable" or
        "Dynamic.OfflineUpdate.ErrorConnectTimeout" or
        "Dynamic.OfflineUpdate.ErrorConnectFailed" or
        "Dynamic.OfflineUpdate.ErrorUsbConnection" or
        "Dynamic.OfflineUpdate.ErrorSavedSessionsUnavailable" or
        "Dynamic.OfflineUpdate.StateSavedSessionsUnavailable" or
        "Dynamic.OfflineUpdate.PairingCopyFailed" or
        "Dynamic.OfflineUpdate.StateCompletionFailed" or
        "Dynamic.OfflineUpdate.StateCompletionMismatch" or
        "Dynamic.OfflineUpdate.StateCompletionUnverified" or
        "Dynamic.OfflineUpdate.ErrorBootstrapFailed" or
        "Dynamic.OfflineUpdate.StatePreflightFailed" or
        "Dynamic.OfflineUpdate.ErrorPreflightFailed" => 1,
        "Dynamic.Diagnostics.Failure" or
        "Dynamic.Package.StatusRejectedForModel" or
        "Dynamic.UninstallFlow.Transaction.StatusFailed" or
        "Dynamic.UninstallFlow.Transaction.StatusRecoveryFailed" or
        "Dynamic.UninstallFlow.EdlRestore.Run.FailedAfterWrite" or
        "Dynamic.UninstallFlow.EdlRestore.Run.FailedBeforeWrite" or
        "Dynamic.UserdataUninstall.Status.SourceInvalid" => 2,
        "Dynamic.InternalInstall.Exit.ResetUnknownUnconfirmed" => 3,
        "Dynamic.OfflineUpdate.StateDamagedSessionRecord" or
        "Dynamic.UserdataUninstall.Runtime.StockRestoreFailed" => 4,
        "Dynamic.InternalInstall.Execution.FailureDetails" => 8,
        _ => 0,
    };

    private string ResolveFormat(string key)
    {
        if (IsEnglish && _englishStrings.TryGetValue(key, out var english) && !string.IsNullOrEmpty(english))
        {
            return english;
        }

        if (_defaultStrings.TryGetValue(key, out var chinese) && !string.IsNullOrEmpty(chinese))
        {
            return chinese;
        }

        return key;
    }

    /// <summary>
    /// Loads both catalogs for <paramref name="language"/>. Static text is expected in every build;
    /// the Dynamic catalogs are optional and are simply absent until they ship.
    /// </summary>
    private static Dictionary<string, string> LoadCatalog(string language)
    {
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        ReadCatalogFile(catalog, StaticPrefix, language);
        ReadCatalogFile(catalog, DynamicPrefix, language);
        return catalog;
    }

    private static void ReadCatalogFile(Dictionary<string, string> catalog, string category, string language)
    {
        var resourceName = ResourceNamePrefix + category + language + ".json";
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return;
            }

            using var document = JsonDocument.Parse(stream, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                catalog[NormalizeKey(property.Name, category)] = value;
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException)
        {
            // A missing or damaged catalog only costs localized text, never the window itself.
        }
    }

    /// <summary>Accepts both prefixed keys and bare keys inside a catalog file of the given category.</summary>
    private static string NormalizeKey(string key, string category) =>
        key.StartsWith(StaticPrefix, StringComparison.Ordinal) || key.StartsWith(DynamicPrefix, StringComparison.Ordinal)
            ? key
            : category + key;
}
