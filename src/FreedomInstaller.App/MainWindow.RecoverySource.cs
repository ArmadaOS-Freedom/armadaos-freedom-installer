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
    private string? _boardRecoveryAttemptManifest;
    private string? _boardRecoveryDiscoveredDirectory;

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
            parent = DefaultRecoveryOutputDirectory();
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
                        var profile = await GlobalEdlDetection.DetectAsync(detector.ClientPath, _runner!, discovery, null, token);
                        if (DeviceModelRoutes.IsWriteRoutePending(profile.Model))
                            throw new InvalidOperationException(T("Dynamic.Model.RoutePendingInEdl", UiText.ModelName(profile.Model)));
                        var tools = await LoadGlobalToolsAsync(profile, token);
                        try
                        {
                            var entry = GlobalEdlBinding.Attach(profile, discovery);
                            client = new EdlClient(tools, entry, _runner!, discovery);
                            _internalInstallEdlTools = tools;
                            _internalInstallEdlEntry = entry;
                            _internalInstallEdlClient = client;
                            _internalInstallEdlEntryAttempted = true;
                        }
                        catch { tools.Dispose(); throw; }
                    }
                    if (!string.IsNullOrWhiteSpace(_bootMaintenanceOriginalDirectory))
                    {
                        var chosen = UninstallBackupDirectorySelection.Resolve(_bootMaintenanceOriginalDirectory);
                        if (chosen.Kind != UninstallBackupDirectorySelection.SelectionKind.Selected || chosen.DirectoryPath is null)
                            throw new InvalidOperationException(T("Dynamic.RecoverySource.NeedSpecificOriginal"));
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
                        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or FormatException)
                        {
                            source = null;
                            _boardRecoveryDiscoveredDirectory = null;
                            AppendLocalizedLog(M("Dynamic.RecoverySource.BoardInvalid", error.Message));
                        }
                    }
                    if (!connected)
                    {
                        try
                        {
                            source = await BoardRecoveryBackup.ReadDirectoryAsync(client, parent, TemporaryWorkRoot, token);
                            if (source is not null)
                                await BindUninstallRecoveryIdentityAsync(client, source, token);
                            _boardRecoveryDiscoveredDirectory = source;
                        }
                        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or FormatException)
                        {
                            source = null;
                            _boardRecoveryDiscoveredDirectory = null;
                            AppendLocalizedLog(M("Dynamic.RecoverySource.BoardInvalid", error.Message));
                        }
                    }
                    if (source is null)
                    {
                        var selected = _bootMaintenanceOriginalDirectory
                            ?? PickExistingDirectory(null, "Dynamic.RecoverySource.PickOriginal");
                        if (selected is null) throw new InvalidOperationException(T("Dynamic.RecoverySource.NeedOriginal"));
                        var selection = UninstallBackupDirectorySelection.Resolve(selected);
                        source = selection.Kind == UninstallBackupDirectorySelection.SelectionKind.Selected
                            ? selection.DirectoryPath : throw new InvalidOperationException(T("Dynamic.RecoverySource.NeedSpecificOriginal"));
                        await BindUninstallRecoveryIdentityAsync(client, source!, token);
                        _bootMaintenanceOriginalDirectory = source;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    SetInternalInstallCheckStatus(M("Dynamic.RecoverySource.Failed", error.Message));
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
            throw new InvalidDataException(T("Dynamic.RecoverySource.WrongDevice"));
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
        SetInternalInstallCheckStatus(M("Dynamic.RecoverySource.Connected", UiText.ModelName(history.Model)));
    }

    private async Task<InternalBackupSet> GetBoardFirstRecoverySourceAsync(
        InternalBackupSet current, string durableParent, CancellationToken token)
    {
        if (IsModernUserdataUninstallMode && !string.IsNullOrWhiteSpace(_bootMaintenanceOriginalDirectory))
        {
            var selection = UninstallBackupDirectorySelection.Resolve(_bootMaintenanceOriginalDirectory);
            if (selection.Kind != UninstallBackupDirectorySelection.SelectionKind.Selected || selection.DirectoryPath is null)
                throw new InvalidOperationException(T("Dynamic.RecoverySource.NeedSpecificOriginal"));
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
            BoardRecoveryBackup.ValidateSourceForReuse(current, cached);
            return cached;
        }
        var client = _internalInstallEdlClient ?? throw new InvalidOperationException("No active EDL connection.");
        if (_boardRecoveryDiscoveredDirectory is { } discovered)
        {
            _boardRecoveryAttemptManifest = current.ManifestSha256;
            try
            {
                var discoveredBackup = await InternalInstallBackup.OpenRecoveryAsync(client, discovered, token: token);
                try { BoardRecoveryBackup.ValidateSourceForReuse(current, discoveredBackup); }
                catch { discoveredBackup.Dispose(); throw; }
                _internalInstallRecoverySource = discoveredBackup;
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
            _boardRecoveryAttemptManifest = current.ManifestSha256;
            AppendLocalizedLog(M("Dynamic.RecoverySource.ReadingBoard"));
            try
            {
                var board = await BoardRecoveryBackup.ReadAsync(client, current, durableParent, TemporaryWorkRoot, token);
                if (board is not null)
                {
                    _internalInstallRecoverySource = board;
                    AppendLocalizedLog(M("Dynamic.RecoverySource.BoardReady", board.DirectoryPath));
                    return board;
                }
                AppendLocalizedLog(M("Dynamic.RecoverySource.BoardMissing"));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or FormatException)
            {
                AppendLocalizedLog(M("Dynamic.RecoverySource.BoardInvalid", error.Message));
            }
        }

        var selected = _bootMaintenanceOriginalDirectory;
        if (string.IsNullOrWhiteSpace(selected))
            selected = PickExistingDirectory(null, "Dynamic.RecoverySource.PickOriginal");
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(selected))
            throw new InvalidOperationException(T("Dynamic.RecoverySource.NeedOriginal"));
        var resolved = UninstallBackupDirectorySelection.Resolve(selected);
        if (resolved.Kind != UninstallBackupDirectorySelection.SelectionKind.Selected || resolved.DirectoryPath is null)
            throw new InvalidOperationException(T("Dynamic.RecoverySource.NeedSpecificOriginal"));
        var recovery = await InternalInstallBackup.OpenRecoveryAsync(client, resolved.DirectoryPath, token: token);
        try { BoardRecoveryBackup.ValidateSourceForReuse(current, recovery); }
        catch { recovery.Dispose(); throw; }
        _internalInstallRecoverySource = recovery;
        AppendLocalizedLog(M("Dynamic.RecoverySource.PcReady", recovery.DirectoryPath));
        return recovery;
    }
}
