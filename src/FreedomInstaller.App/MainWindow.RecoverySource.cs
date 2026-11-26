using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

public partial class MainWindow
{
    private InternalBackupSet? _internalInstallRecoverySource;
    private BoardRecoveryExtraction? _internalInstallTemporaryRecoveryExtraction;
    private string? _boardRecoveryAttemptManifest;
    private string? _boardRecoveryDiscoveredDirectory;
    private bool _userdataUninstallUseBoardBackup = true;

    private string? SelectedUserdataUninstallPcBackup => _userdataUninstallUseBoardBackup ? null : _bootMaintenanceOriginalDirectory;

    private string?[] SharedBackupProtectedSourceDirectories => IsModernUserdataUninstallMode
        ? new[] { SelectedUserdataUninstallPcBackup }
        : new[] { _bootMaintenanceOriginalDirectory, _bootMaintenanceProjectDirectory };

    private void ClearUserdataUninstallRecoverySource()
    {
        _internalInstallRecoverySource?.Dispose();
        _internalInstallRecoverySource = null;
        _boardRecoveryAttemptManifest = null;
    }

    private static string DefaultRecoveryOutputDirectory()
    {
        var directory = Path.Combine(BundledTools.GetCacheRoot(), "Recovery");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task CheckUninstallBoardRecoveryEdlAsync()
    {
        var parent = _internalInstallBackupParent;
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            try
            {
                parent = DefaultRecoveryOutputDirectory();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 缓存目录不可写：在本流程的状态里报告失败，保留会话，不让异常逃出 async void 处理程序（同 Mac）。
                SetInternalInstallCheckStatus(M("Dynamic.RecoverySource.Failed", error));
                AppendLocalizedLog(M("Dynamic.RecoverySource.Failed", error.Message));
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(parent)) return;
        ApplyInternalInstallBackupParent(parent);
        _internalInstallOperationActive = true;
        try
        {
            await RunBusyAsync(async token =>
            {
                try
                {
                    var discovery = new WindowsEdlEndpointDiscovery();
                    var client = _internalInstallEdlClient;
                    if (client is null)
                    {
                        _boardRecoveryDiscoveredDirectory = null;
                        using var detector = await LoadGlobalToolsAsync(EdlDeviceProfiles.FindByModel("TB322FC")!, token);
                        var hardware = await GlobalEdlDetection.DetectIdentityAsync(detector.ClientPath, _runner!, discovery, null, token);
                        var profile = hardware.Profile;
                        if (DeviceModelRoutes.IsWriteRoutePending(profile.Model))
                            throw new LocalizedInvalidOperationException(M("Dynamic.Model.RoutePendingInEdl", UiText.ModelName(profile.Model)));
                        var tools = await LoadGlobalToolsAsync(profile, token);
                        try
                        {
                            var entry = GlobalEdlBinding.Attach(profile, discovery);
                            client = new EdlClient(tools, entry, _runner!, discovery, hardware);
                            _internalInstallEdlTools = tools;
                            _internalInstallEdlEntry = entry;
                            _internalInstallEdlClient = client;
                            _internalInstallEdlEntryAttempted = true;
                        }
                        catch { tools.Dispose(); throw; }
                    }
                    // 修复模式不使用 PC 原厂来源：始终优先板内恢复材料，再由自动回滚捕获建立来源。
                    var activeExplicitDirectory = IsModernUserdataUninstallMode
                        ? SelectedUserdataUninstallPcBackup
                        : _bootMaintenanceOriginalDirectory;
                    if (!IsRepairInstallationMode && !string.IsNullOrWhiteSpace(activeExplicitDirectory))
                    {
                        var chosen = UninstallBackupDirectorySelection.Resolve(activeExplicitDirectory);
                        if (chosen.Kind != UninstallBackupDirectorySelection.SelectionKind.Selected || chosen.DirectoryPath is null)
                            throw new LocalizedInvalidOperationException(M("Dynamic.RecoverySource.NeedSpecificOriginal"));
                        await BindUninstallRecoveryIdentityAsync(client, chosen.DirectoryPath, token);
                        _bootMaintenanceOriginalDirectory = chosen.DirectoryPath;
                        AppendLocalizedLog(M("Dynamic.RecoverySource.PcReady", chosen.DirectoryPath));
                        return;
                    }
                    AppendLocalizedLog(M("Dynamic.RecoverySource.ReadingBoard"));
                    string? source = _boardRecoveryDiscoveredDirectory;
                    var connected = false;
                    if (source is not null)
                    {
                        try
                        {
                            await BindUninstallRecoveryIdentityAsync(client, source, token);
                            connected = true;
                        }
                        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or FormatException or InvalidDataException)
                        {
                            source = null;
                            _boardRecoveryDiscoveredDirectory = null;
                            AppendLocalizedLog(M("Dynamic.RecoverySource.BoardInvalid", error.Message));
                        }
                    }
                    if (!connected)
                    {
                        BoardRecoveryExtraction? extraction = null;
                        try
                        {
                            // Uninstall always performs a fresh real board read; never reuse a retained PC copy
                            // left behind by a previous failed attempt.
                            extraction = await BoardRecoveryBackup.ReadDirectoryAsync(
                                client,
                                IsModernUserdataUninstallMode ? TemporaryWorkRoot : parent,
                                TemporaryWorkRoot,
                                token,
                                allowRetainedCopy: !IsModernUserdataUninstallMode);
                            if (extraction is not null)
                            {
                                await BindUninstallRecoveryIdentityAsync(client, extraction.DirectoryPath, token);
                                if (IsModernUserdataUninstallMode)
                                {
                                    AdoptTemporaryRecoveryExtraction(extraction);
                                    extraction = null;
                                }
                                else
                                {
                                    extraction.Retain();
                                }
                                source = extraction?.DirectoryPath ?? _internalInstallTemporaryRecoveryExtraction?.DirectoryPath;
                            }
                            else
                            {
                                source = null;
                            }
                            _boardRecoveryDiscoveredDirectory = source;
                        }
                        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or FormatException or InvalidDataException)
                        {
                            source = null;
                            _boardRecoveryDiscoveredDirectory = null;
                            AppendLocalizedLog(M("Dynamic.RecoverySource.BoardInvalid", error.Message));
                        }
                        finally
                        {
                            // Only a successful bind retained the extraction; every failure or cancellation has
                            // already released the backup locks inside the bind helper before this lease removes
                            // exactly the directory this call created.
                            extraction?.Dispose();
                        }
                    }
                    if (source is null)
                    {
                        // 修复模式下绝不弹原厂目录选择器；板内恢复材料缺失时必须清晰失败，
                        // 不允许回退到 PC 备份或编造身份。
                        if (IsRepairInstallationMode)
                        {
                            throw new LocalizedInvalidOperationException(M("Dynamic.BootMaintenance.Repair.NeedAndroidIdentity"));
                        }

                        var selected = activeExplicitDirectory
                            ?? PickExistingDirectory(null, "Dynamic.RecoverySource.PickOriginal");
                        if (selected is null) throw new LocalizedInvalidOperationException(M("Dynamic.RecoverySource.NeedOriginal"));
                        var selection = UninstallBackupDirectorySelection.Resolve(selected);
                        source = selection.Kind == UninstallBackupDirectorySelection.SelectionKind.Selected
                            ? selection.DirectoryPath : throw new LocalizedInvalidOperationException(M("Dynamic.RecoverySource.NeedSpecificOriginal"));
                        await BindUninstallRecoveryIdentityAsync(client, source!, token);
                        _bootMaintenanceOriginalDirectory = source;
                        if (IsModernUserdataUninstallMode)
                        {
                            _userdataUninstallUseBoardBackup = false;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    SetInternalInstallCheckStatus(M("Dynamic.RecoverySource.Failed", error));
                    AppendLocalizedLog(M("Dynamic.RecoverySource.Failed", error.Message));
                }
            });
        }
        finally
        {
            _internalInstallOperationActive = false;
            RefreshInternalInstallActions();
            RefreshBootMaintenanceSharedActions();
        }
    }

    private async Task BindUninstallRecoveryIdentityAsync(EdlClient client, string source, CancellationToken token)
    {
        var history = await BootMaintenanceBackend.ReadHistoricalIdentityAsync(source, token);
        if (history.Profile.ProfileId != client.Toolchain.ProfileId)
            throw new LocalizedInvalidOperationException(M("Dynamic.RecoverySource.WrongDevice"));
        var savedEntry = await new EdlEntry(_transport!.AdbPath, _runner!, new WindowsEdlEndpointDiscovery())
            .WaitForPresentAsync(history.Identity, history.Profile, token);
        var probeOutput = Path.Combine(TemporaryWorkRoot,
            $"uninstall-recovery-{Guid.NewGuid():N}", "probe.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(probeOutput)!);
        var rebound = await client.WithSavedIdentityAsync(savedEntry, probeOutput, token);
        using var verified = await InternalInstallBackup.OpenRecoveryAsync(rebound, source, token: token);
        _internalInstallEdlClient = rebound;
        _internalInstallEdlEntry = savedEntry;
        _bootMaintenanceHistoricalIdentity = history;
        // 板内恢复材料绑定成功后发布自动原厂来源：修复回滚捕获将据此建立真实来源。
        if (IsRepairInstallationMode)
        {
            _bootMaintenanceOriginalDirectory = source;
        }
        SetInternalInstallCheckStatus(M("Dynamic.RecoverySource.Connected", UiText.ModelName(history.Model)));
    }

    private async Task<InternalBackupSet> GetBoardFirstRecoverySourceAsync(
        InternalBackupSet current, string durableParent, CancellationToken token)
    {
        if (IsModernUserdataUninstallMode && !_userdataUninstallUseBoardBackup)
        {
            var selectedPc = SelectedUserdataUninstallPcBackup;
            if (string.IsNullOrWhiteSpace(selectedPc))
                throw new LocalizedInvalidOperationException(M("Dynamic.RecoverySource.NeedOriginal"));
            var selection = UninstallBackupDirectorySelection.Resolve(selectedPc);
            if (selection.Kind != UninstallBackupDirectorySelection.SelectionKind.Selected || selection.DirectoryPath is null)
                throw new LocalizedInvalidOperationException(M("Dynamic.RecoverySource.NeedSpecificOriginal"));
            if (_internalInstallRecoverySource is { } explicitCached
                && string.Equals(explicitCached.DirectoryPath, selection.DirectoryPath, StringComparison.OrdinalIgnoreCase))
            {
                BoardRecoveryBackup.ValidateSourceForReuse(current, explicitCached);
                return explicitCached;
            }
            var explicitSource = await InternalInstallBackup.OpenRecoveryAsync(
                _internalInstallEdlClient!, selection.DirectoryPath, token: token);
            try { BoardRecoveryBackup.ValidateSourceForReuse(current, explicitSource); }
            catch { explicitSource.Dispose(); throw; }
            _internalInstallRecoverySource?.Dispose();
            _internalInstallRecoverySource = explicitSource;
            AppendLocalizedLog(M("Dynamic.RecoverySource.PcReady", explicitSource.DirectoryPath));
            return explicitSource;
        }
        if (_internalInstallRecoverySource is { } cached)
        {
            try
            {
                BoardRecoveryBackup.ValidateSourceForReuse(current, cached);
                return cached;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or FormatException or InvalidDataException)
            {
                // Auto cached lease is stale: discard it without touching the actual user directory,
                // clear discovery state and fall through to a fresh read.
                cached.Dispose();
                _internalInstallRecoverySource = null;
                _boardRecoveryDiscoveredDirectory = null;
                _boardRecoveryAttemptManifest = null;
                AppendLocalizedLog(M("Dynamic.RawValue", $"{error.GetType().FullName}: {error.Message}"));
            }
        }
        var client = _internalInstallEdlClient ?? throw new InvalidOperationException("No active EDL connection.");
        if (_boardRecoveryDiscoveredDirectory is { } discovered)
        {
            try
            {
                var discoveredBackup = await InternalInstallBackup.OpenRecoveryAsync(client, discovered, token: token);
                try { BoardRecoveryBackup.ValidateSourceForReuse(current, discoveredBackup); }
                catch { discoveredBackup.Dispose(); throw; }
                _internalInstallRecoverySource = discoveredBackup;
                _boardRecoveryAttemptManifest = current.ManifestSha256;
                AppendLocalizedLog(M("Dynamic.RecoverySource.BoardReady", discoveredBackup.DirectoryPath));
                return discoveredBackup;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or FormatException or InvalidDataException)
            {
                _boardRecoveryDiscoveredDirectory = null;
                _boardRecoveryAttemptManifest = null;
                AppendLocalizedLog(M("Dynamic.RecoverySource.BoardInvalid", error.Message));
            }
        }
        if (_boardRecoveryAttemptManifest != current.ManifestSha256)
        {
            AppendLocalizedLog(M("Dynamic.RecoverySource.ReadingBoard"));
            try
            {
                var board = await BoardRecoveryBackup.ReadAsync(
                    client,
                    current,
                    IsModernUserdataUninstallMode ? TemporaryWorkRoot : durableParent,
                    TemporaryWorkRoot,
                    token,
                    allowRetainedCopy: !IsModernUserdataUninstallMode,
                    adoptTemporaryExtraction: IsModernUserdataUninstallMode
                        ? AdoptTemporaryRecoveryExtraction
                        : null);
                if (board is not null)
                {
                    _internalInstallRecoverySource = board;
                    _boardRecoveryAttemptManifest = current.ManifestSha256;
                    AppendLocalizedLog(M("Dynamic.RecoverySource.BoardReady", board.DirectoryPath));
                    return board;
                }
                AppendLocalizedLog(M("Dynamic.RecoverySource.BoardMissing"));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or FormatException or InvalidDataException)
            {
                AppendLocalizedLog(M("Dynamic.RecoverySource.BoardInvalid", error.Message));
            }
        }

        var selected = IsModernUserdataUninstallMode ? null : _bootMaintenanceOriginalDirectory;
        if (string.IsNullOrWhiteSpace(selected))
            selected = PickExistingDirectory(null, "Dynamic.RecoverySource.PickOriginal");
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(selected))
            throw new LocalizedInvalidOperationException(M("Dynamic.RecoverySource.NeedOriginal"));
        var resolved = UninstallBackupDirectorySelection.Resolve(selected);
        if (resolved.Kind != UninstallBackupDirectorySelection.SelectionKind.Selected || resolved.DirectoryPath is null)
            throw new LocalizedInvalidOperationException(M("Dynamic.RecoverySource.NeedSpecificOriginal"));
        var recovery = await InternalInstallBackup.OpenRecoveryAsync(client, resolved.DirectoryPath, token: token);
        try { BoardRecoveryBackup.ValidateSourceForReuse(current, recovery); }
        catch { recovery.Dispose(); throw; }
        _internalInstallRecoverySource = recovery;
        if (IsModernUserdataUninstallMode)
        {
            _bootMaintenanceOriginalDirectory = recovery.DirectoryPath;
            _userdataUninstallUseBoardBackup = false;
        }
        AppendLocalizedLog(M("Dynamic.RecoverySource.PcReady", recovery.DirectoryPath));
        return recovery;
    }

    private void AdoptTemporaryRecoveryExtraction(BoardRecoveryExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        // The failed lease still backs _internalInstallRecoverySource, so release that reference before
        // dropping the old temporary lease but never discard a lease that a valid plan still uses.
        var previousExtraction = _internalInstallTemporaryRecoveryExtraction;
        _internalInstallTemporaryRecoveryExtraction = extraction;
        if (previousExtraction is not null && !ReferenceEquals(previousExtraction, extraction))
        {
            _internalInstallRecoverySource?.Dispose();
            _internalInstallRecoverySource = null;
            previousExtraction.Dispose();
        }
    }

    private void ReleaseTemporaryRecoveryLease()
    {
        _internalInstallTemporaryRecoveryExtraction?.Dispose();
        _internalInstallTemporaryRecoveryExtraction = null;
        _boardRecoveryAttemptManifest = null;
        _boardRecoveryDiscoveredDirectory = null;
    }
}
