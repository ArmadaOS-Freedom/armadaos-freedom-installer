using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FreedomInstaller.App;
using FreedomInstaller.Core;

namespace FreedomInstaller.Mac;

/// <summary>
/// The 9008 global backup / restore page of the macOS window: the port of the WPF
/// <c>MainWindow.GlobalRecovery.cs</c> and <c>MainWindow.GlobalRecovery.Execution.cs</c> pair. One page with two
/// views (backup / restore) sharing one connection, progress and log. Opening the page never touches the device;
/// every device step starts from an explicit click. Core makes every device decision, exactly as on Windows; this
/// file only keeps the session state, gates the buttons and renders. Device calls run on a worker thread because
/// the macOS 9008 discovery reads ioreg synchronously.
/// </summary>
public partial class MainWindow
{
    private enum GlobalRecoveryMode
    {
        Backup,
        Restore,
    }

    private const string GlobalToolchainFolder = ".armada-installer-tools";

    private GlobalRecoveryMode _globalRecoveryMode = GlobalRecoveryMode.Backup;
    private bool _globalOperationActive;
    private EdlToolchain? _globalEdlTools;
    private EdlEntryResult? _globalEdlEntry;
    private EdlClient? _globalEdlClient;
    private EdlDeviceProfile? _globalProfile;
    private bool _globalEntryAttempted;
    private PowerSample? _globalPower;
    private string? _globalOutputParent;
    private string? _globalRestoreSource;
    private GlobalEdlBackupSet? _globalRestoreSet;
    private GlobalEdlRestorePlan? _globalRestorePlan;
    private bool _globalRestoreWriteAttempted;
    private bool _globalRestoreUncertain;
    private bool _globalRestoreSucceeded;
    private bool _globalResetAttempted;
    private bool _globalResetUnknown;
    private bool _globalProgressActive;
    private string? _globalProgressStage;
    private DateTime _globalLastProgressAt = DateTime.MinValue;
    private string? _globalSessionDirectory;
    private bool _globalRestoreEnvironmentReady;

    /// <summary>The tablet is (or may be) in 9008 for this page, or a write or reset outcome is still unresolved.</summary>
    private bool HasGlobalRecoveryProtectedState =>
        _globalOperationActive
        || _globalEdlClient is not null
        || _globalEdlEntry is not null
        || _globalEntryAttempted
        || _globalResetAttempted
        || _globalResetUnknown
        || _globalRestoreUncertain
        || _globalRestoreWriteAttempted;

    private static string MacCacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreedomInstaller");

    // ================================================================ navigation

    /// <summary>Opens the page in one view; a protected session keeps the view it belongs to.</summary>
    private void ShowGlobalRecoveryPage(GlobalRecoveryMode mode)
    {
        if (mode == GlobalRecoveryMode.Backup && !_environmentReady)
        {
            return;
        }

        if (HasGlobalRecoveryProtectedState && _globalRecoveryMode != mode)
        {
            return;
        }

        _globalRecoveryMode = mode;
        ShowView(GlobalRecoveryPage);
        ApplyGlobalRecoveryModeView();
        RefreshGlobalRecoveryActions();
        RenderGlobalConnection();
        RenderGlobalPower();
        if (mode == GlobalRecoveryMode.Restore && !_globalRestoreEnvironmentReady)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Error.RestoreEnvironmentRequired");
        }
        GlobalRecoveryPage.ScrollToHome();
    }

    private void OnOpenGlobalRestoreFromEnvironment(object? sender, RoutedEventArgs e)
    {
        if (!EnvironmentPage.IsVisible || _environmentReady || _busy || HasLiveDeviceSession || !EnvironmentGlobalRestoreButton.IsEnabled)
        {
            return;
        }

        ShowGlobalRecoveryPage(GlobalRecoveryMode.Restore);
    }

    private void ApplyGlobalRecoveryModeView()
    {
        var backup = _globalRecoveryMode == GlobalRecoveryMode.Backup;
        GlobalBackupTitleText.IsVisible = backup;
        GlobalRestoreTitleText.IsVisible = !backup;
        GlobalBackupLeadText.IsVisible = backup;
        GlobalRestoreLeadText.IsVisible = !backup;
        GlobalOutputSection.IsVisible = backup;
        GlobalBackupCard.IsVisible = backup;
        GlobalRestoreCard.IsVisible = !backup;
        GlobalAndroidConnectionGuide.IsVisible = backup;
        GlobalAutoDetectText.IsVisible = backup;
        GlobalPowerText.IsVisible = backup;
        GlobalManualEdlGuide.IsVisible = !backup;
        GlobalConnectButton[!ContentControl.ContentProperty] = GlobalConnectButton.GetResourceObservable(backup ? "Static.Global.Connect" : "Static.Global.RestoreConnect").ToBinding();
        GlobalBackHomeButton[!ContentControl.ContentProperty] = GlobalBackHomeButton.GetResourceObservable(_environmentReady ? "Static.Global.BackHome" : "Static.Environment.Title").ToBinding();
    }

    private void OnGlobalBackHome(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (HasGlobalRecoveryProtectedState)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Status.BlockedBySession");
            return;
        }

        ShowView(_environmentReady ? HomePage : EnvironmentPage);
        RefreshActions();
    }

    /// <summary>Releases managed objects on close; user backups and evidence are never deleted.</summary>
    private void DisposeGlobalRecoveryOnClose()
    {
        if (_globalOperationActive)
        {
            return;
        }

        DisposeGlobalRestorePreparation();
        _globalRestoreUncertain = false;
        _globalResetAttempted = false;
        _globalResetUnknown = false;
        _globalEntryAttempted = false;
        _globalEdlEntry = null;
        _globalEdlClient = null;
        _globalProfile = null;
        _globalPower = null;
        _globalEdlTools?.Dispose();
        _globalEdlTools = null;
        _globalProgressActive = false;
    }

    // ================================================================ availability

    private void RefreshGlobalRecoveryActions()
    {
        var busy = _globalOperationActive || _busy;
        var frozen = _globalRestoreUncertain || _globalResetUnknown;
        var writesAttempted = _globalRestoreWriteAttempted;
        var backupView = _globalRecoveryMode == GlobalRecoveryMode.Backup;
        var restoreView = _globalRecoveryMode == GlobalRecoveryMode.Restore;

        GlobalBrowseBackupButton.IsEnabled = !busy && !frozen && !writesAttempted;
        GlobalBrowseRestoreButton.IsEnabled = !busy && !frozen && !writesAttempted;

        var proof = _globalEdlClient?.HasInitialSaharaProof == true;
        var idleConnect = _globalEdlClient is null && !_globalEntryAttempted;
        var reprobeConnect = _globalEdlClient is not null && !proof;
        var restoreHostReady = !restoreView || _globalRestoreEnvironmentReady;
        GlobalConnectButton.IsEnabled = !busy && !frozen && !writesAttempted
            && (idleConnect || reprobeConnect) && restoreHostReady;

        GlobalBackupButton.IsEnabled = backupView && !busy && !frozen && !writesAttempted && proof;

        GlobalPrepareRestoreButton.IsEnabled = restoreView && !busy && !frozen && !writesAttempted
            && _globalRestoreEnvironmentReady
            && !string.IsNullOrWhiteSpace(_globalRestoreSource);

        GlobalRestoreConfirmCheck.IsEnabled = restoreView && !busy && !frozen && !writesAttempted
            && _globalRestorePlan is not null;

        GlobalRestoreButton.IsEnabled = restoreView && !busy && !frozen && !writesAttempted
            && _globalRestorePlan is not null
            && proof
            && GlobalRestoreConfirmCheck.IsChecked == true;

        GlobalRestartButton.IsEnabled = !busy && !frozen
            && !_globalResetAttempted
            && proof
            && (!writesAttempted || _globalRestoreSucceeded);

        GlobalBackHomeButton.IsEnabled = !busy && !HasGlobalRecoveryProtectedState;
    }

    // ================================================================ local choices

    private void OnGlobalRestoreConsentChanged(object? sender, RoutedEventArgs e)
    {
        if (!_globalOperationActive)
        {
            RefreshGlobalRecoveryActions();
        }
    }

    private async void OnGlobalBrowseBackup(object? sender, RoutedEventArgs e) => await PickGlobalOutputAsync();

    /// <summary>Picks the parent folder for backups (and restore evidence); only invalidates a local preparation.</summary>
    private async Task PickGlobalOutputAsync()
    {
        if (_busy || _globalOperationActive || _globalRestoreUncertain || _globalResetUnknown
            || _globalRestoreWriteAttempted)
        {
            return;
        }

        var chosen = await PickExistingDirectoryAsync("Dynamic.Global.Dialog.PickOutput");
        if (chosen is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_globalRestoreSource)
            && !IsGlobalOutputOutsideSource(chosen, _globalRestoreSource!))
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Error.OutputInsideRestoreSource");
            return;
        }

        InvalidateGlobalRestorePreparation();
        _globalOutputParent = chosen;
        GlobalBackupDirectoryBox.Text = chosen;
        SetText(GlobalProgressStatusText, "Dynamic.Global.Status.OutputSelected");
        RefreshGlobalRecoveryActions();
    }

    private async void OnGlobalBrowseRestore(object? sender, RoutedEventArgs e)
    {
        if (_globalRecoveryMode != GlobalRecoveryMode.Restore
            || _busy || _globalOperationActive || _globalRestoreUncertain || _globalResetUnknown
            || _globalRestoreWriteAttempted)
        {
            return;
        }

        var chosen = await PickExistingDirectoryAsync("Dynamic.Global.Dialog.PickRestore");
        if (chosen is null)
        {
            return;
        }

        _globalRestoreSource = chosen;
        GlobalRestoreDirectoryBox.Text = chosen;
        InvalidateGlobalRestorePreparation();
        SetText(GlobalRestoreSummaryText, "Dynamic.Global.Restore.NotPrepared");
        SetText(GlobalProgressStatusText, "Dynamic.Global.Status.RestoreSourceSelected");
        RefreshGlobalRecoveryActions();
    }

    private async Task<string?> PickExistingDirectoryAsync(string titleKey)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T(titleKey),
            AllowMultiple = false,
        });
        var path = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// The output parent may be a sibling or an ancestor of the restore source, never the source itself or a folder
    /// inside it, so evidence is never written into the user's backup tree. Same rule as the WPF helper; macOS
    /// volumes are case-insensitive by default, so the comparison is too.
    /// </summary>
    private static bool IsGlobalOutputOutsideSource(string parent, string source)
    {
        try
        {
            var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            if (normalizedParent.Length == 0 || normalizedSource.Length == 0
                || string.Equals(normalizedParent, normalizedSource, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !normalizedParent.StartsWith(
                normalizedSource + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private void InvalidateGlobalRestorePreparation()
    {
        DisposeGlobalRestorePreparation();
        GlobalRestoreConfirmCheck.IsChecked = false;
    }

    private void DisposeGlobalRestorePreparation()
    {
        _globalRestorePlan?.Dispose();
        _globalRestorePlan = null;
        _globalRestoreSet?.Dispose();
        _globalRestoreSet = null;
        _globalRestoreWriteAttempted = false;
        _globalRestoreSucceeded = false;
    }

    private void DisposeGlobalRestorePlanOnly()
    {
        _globalRestorePlan?.Dispose();
        _globalRestorePlan = null;
        GlobalRestoreConfirmCheck.IsChecked = false;
    }

    // ================================================================ rendering

    private void AppendGlobalLog(UiMessage message)
    {
        var row = _l.AppendLogRow(GlobalLogList, message, MaxLogRows);
        row.FontSize = 12;
        row.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        row.Margin = new Avalonia.Thickness(0, 0, 0, 4);
        row[!TextBlock.ForegroundProperty] = row.GetResourceObservable("MutedBrush").ToBinding();

        GlobalLogEmptyText.IsVisible = false;
        GlobalLogScrollViewer.ScrollToEnd();
    }

    private void RenderGlobalConnection()
    {
        if (_globalEdlClient is null && !_globalEntryAttempted && !_globalResetUnknown && !_globalRestoreUncertain)
        {
            SetText(GlobalConnectionText, "Dynamic.Global.Connection.NotConnected");
            return;
        }

        // Only the initial Sahara proof may say "verified"; an unknown reset or an uncertain write hides old proof.
        if (_globalEdlClient?.HasInitialSaharaProof != true || _globalResetUnknown || _globalRestoreUncertain)
        {
            SetText(GlobalConnectionText, "Dynamic.Global.Connection.Unverified");
            return;
        }

        var model = _globalProfile?.Model ?? _globalEdlEntry?.OfflineIdentity?.Model ?? UnknownValue;
        var bus = _globalEdlEntry?.BusSerialHex ?? UnknownValue;
        SetText(GlobalConnectionText, "Dynamic.Global.Connection.Verified", UiText.ModelName(model), bus);
    }

    private void RenderGlobalPower()
    {
        var sample = _globalPower;
        if (sample is null)
        {
            SetText(GlobalPowerText, "Dynamic.InternalInstall.Power.Unknown");
            return;
        }

        object percent = sample.Percentage is int value
            ? string.Format(CultureInfo.CurrentCulture, "{0}%", value)
            : M("Dynamic.InternalInstall.Power.PercentUnknown");
        object external = sample.ExternalPower switch
        {
            true => M("Dynamic.UninstallFlow.Common.Yes"),
            false => M("Dynamic.UninstallFlow.Common.No"),
            _ => M("Dynamic.InternalInstall.Power.ExternalUnknown"),
        };
        object time = sample.CapturedAt == default
            ? M("Dynamic.InternalInstall.Power.TimeUnknown")
            : sample.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        SetText(GlobalPowerText, "Dynamic.InternalInstall.Power.Sample", percent, external, time);
    }

    private string FormatGlobalPowerForDialog()
    {
        var sample = _globalPower;
        if (sample is null)
        {
            return T("Dynamic.InternalInstall.Power.Unknown");
        }

        object percent = sample.Percentage is int value
            ? string.Format(CultureInfo.CurrentCulture, "{0}%", value)
            : T("Dynamic.InternalInstall.Power.PercentUnknown");
        var external = sample.ExternalPower switch
        {
            true => T("Dynamic.UninstallFlow.Common.Yes"),
            false => T("Dynamic.UninstallFlow.Common.No"),
            _ => T("Dynamic.InternalInstall.Power.ExternalUnknown"),
        };
        var time = sample.CapturedAt == default
            ? T("Dynamic.InternalInstall.Power.TimeUnknown")
            : sample.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        return T("Dynamic.InternalInstall.Power.Sample", percent, external, time);
    }

    // ================================================================ progress

    private IProgress<GlobalEdlProgress> BeginGlobalProgress()
    {
        _globalProgressActive = true;
        _globalProgressStage = null;
        _globalLastProgressAt = DateTime.MinValue;
        GlobalProgressBar.IsIndeterminate = true;
        GlobalProgressBar.Maximum = 100;
        GlobalProgressBar.Value = 0;
        return new Progress<GlobalEdlProgress>(RenderGlobalProgress);
    }

    private void EndGlobalProgress()
    {
        _globalProgressActive = false;
        _globalProgressStage = null;
        GlobalProgressBar.IsIndeterminate = false;
    }

    private void RenderGlobalProgress(GlobalEdlProgress progress)
    {
        if (!_globalProgressActive || progress is null)
        {
            return;
        }

        var stageMessage = M("Dynamic.Global.Stage." + NormalizeGlobalStage(progress.Stage));
        var stageChanged = !string.Equals(_globalProgressStage, progress.Stage, StringComparison.Ordinal);
        var now = DateTime.UtcNow;

        // A stage change renders and logs at once; plain progress refreshes at most once a second.
        if (!stageChanged && (now - _globalLastProgressAt) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _globalLastProgressAt = now;
        _globalProgressStage = progress.Stage;

        object role = string.IsNullOrWhiteSpace(progress.Role) ? M("Dynamic.Global.Progress.NoRole") : progress.Role!;
        object lun = progress.Lun is int index
            ? index.ToString(CultureInfo.InvariantCulture)
            : M("Dynamic.Global.Progress.NoLun");
        SetText(
            GlobalProgressStatusText,
            "Dynamic.Global.Progress.Status",
            stageMessage,
            role,
            lun,
            FormatGlobalBytes(progress.CompletedBytes),
            progress.TotalBytes > 0 ? FormatGlobalBytes(progress.TotalBytes) : M("Dynamic.Global.Progress.UnknownTotal"));

        if (progress.TotalBytes > 0)
        {
            GlobalProgressBar.IsIndeterminate = false;
            GlobalProgressBar.Maximum = progress.TotalBytes;
            GlobalProgressBar.Value = Math.Min(progress.CompletedBytes, progress.TotalBytes);
        }
        else
        {
            GlobalProgressBar.IsIndeterminate = true;
        }

        if (stageChanged)
        {
            AppendGlobalLog(M("Dynamic.Global.Log.Stage", stageMessage));
        }
    }

    private void MarkGlobalProgressComplete()
    {
        GlobalProgressBar.IsIndeterminate = false;
        GlobalProgressBar.Maximum = 100;
        GlobalProgressBar.Value = 100;
    }

    private static string NormalizeGlobalStage(string stage) => stage switch
    {
        "probe" => "Probe",
        "read-gpt" => "ReadGpt",
        "read-partition" => "ReadPartition",
        "compress" => "Compress",
        "verify-compression" => "VerifyCompression",
        "verify-backup" => "VerifyBackup",
        "prepare-restore" => "PrepareRestore",
        "write-partition" => "WritePartition",
        "readback-partition" => "ReadbackPartition",
        "write-gpt" => "WriteGpt",
        "readback-gpt" => "ReadbackGpt",
        "initialize-android" => "InitializeAndroid",
        "completed" => "Completed",
        _ => "Unknown",
    };

    private static string FormatGlobalBytes(long bytes)
    {
        const double GiB = 1024d * 1024d * 1024d;
        const double MiB = 1024d * 1024d;
        const double KiB = 1024d;
        if (bytes >= GiB)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.##} GiB", bytes / GiB);
        }

        if (bytes >= MiB)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.##} MiB", bytes / MiB);
        }

        return bytes >= KiB
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.##} KiB", bytes / KiB)
            : string.Format(CultureInfo.CurrentCulture, "{0} B", bytes);
    }

    private static string FormatGlobalGiB(long bytes) =>
        string.Format(CultureInfo.CurrentCulture, "{0:0.##} GiB", bytes / (1024d * 1024d * 1024d));

    // ================================================================ session directory

    /// <summary>This connection's own tool and probe folder under the cache; a new connection gets a new folder.</summary>
    private string GetGlobalSessionDirectory()
    {
        _globalSessionDirectory ??= Path.Combine(MacCacheRoot, "global-edl-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_globalSessionDirectory);
        return _globalSessionDirectory;
    }

    private string NewGlobalProbePath() =>
        Path.Combine(GetGlobalSessionDirectory(), "probe-lun0-" + Guid.NewGuid().ToString("N") + ".bin");

    // ================================================================ connect

    private async void OnGlobalConnect(object? sender, RoutedEventArgs e)
    {
        if (_busy || _globalOperationActive || _globalRestoreUncertain || _globalResetUnknown
            || _globalRestoreWriteAttempted)
        {
            return;
        }

        if (_globalRecoveryMode == GlobalRecoveryMode.Restore && !_globalRestoreEnvironmentReady)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Error.RestoreEnvironmentRequired");
            return;
        }

        // An entry left no client: never resend the Android command on our own.
        if (_globalEdlTools is not null && _globalEdlClient is null)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Status.BlockedBySession");
            return;
        }

        if (_globalEdlClient is not null)
        {
            if (_globalProfile is null)
            {
                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.NeedVerifiedConnection");
                return;
            }

            var profile = _globalProfile;
            await RunGlobalOperationAsync(async token =>
            {
                try
                {
                    var verified = await EnsureGlobalConnectionAsync(profile, ExpectedGlobalBusSerial(), token);
                    RenderGlobalConnection();
                    SetText(GlobalProgressStatusText,
                        verified ? "Dynamic.Global.Status.Connected" : "Dynamic.Global.Status.NeedVerifiedConnection");
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    RenderGlobalConnection();
                    SetText(GlobalProgressStatusText, "Dynamic.Global.Status.ConnectFailed", exception.Message);
                }
            });
            return;
        }

        // The Android route reboots into 9008; an existing 9008 endpoint is not rebooted.
        if (_globalRecoveryMode == GlobalRecoveryMode.Backup
            && !await MessageDialog.AskAsync(
                this,
                T("Dynamic.Global.Dialog.Title"),
                T("Dynamic.Global.Dialog.ConnectWarning"),
                T("Dynamic.Mac.Dialog.Yes"),
                T("Dynamic.Mac.Dialog.No")))
        {
            return;
        }

        await RunGlobalOperationAsync(async token =>
        {
            try
            {
                EdlDeviceProfile? expectedProfile = null;
                string? expectedBus = null;
                if (_globalRecoveryMode == GlobalRecoveryMode.Restore)
                {
                    if (_globalRestoreSet is null && !string.IsNullOrWhiteSpace(_globalRestoreSource))
                    {
                        // Before opening the backup, read-only confirm a present 9008: a missing endpoint
                        // takes priority over the backup and must never open it.
                        if (!await HasPresentGlobalEdlAsync(Discovery, token))
                        {
                            throw new InvalidOperationException(T("Dynamic.Global.Error.ManualEdlRequired"));
                        }

                        SetText(GlobalProgressStatusText, "Dynamic.Global.Status.VerifyingRestoreSource");
                        var source = _globalRestoreSource!;
                        var openProgress = new Progress<GlobalEdlProgress>(RenderGlobalProgress);
                        _globalRestoreSet = await Task.Run(() => GlobalEdlBackup.OpenAsync(source, openProgress, token), token);
                    }

                    var verifiedIdentity = _globalRestoreSet?.Identity;
                    if (verifiedIdentity is not null)
                    {
                        // A restore the app would refuse later must not reboot the tablet into 9008 first: the same
                        // write-route gate as the restore preparation runs before any connection.
                        if (DeviceModelRoutes.IsWriteRoutePending(verifiedIdentity.Model))
                        {
                            throw new InvalidOperationException(
                                T("Dynamic.Global.Error.RestoreRoutePending", UiText.ModelName(verifiedIdentity.Model)));
                        }

                        expectedProfile = EdlDeviceProfiles.FindById(verifiedIdentity.ProfileId);
                        if (expectedProfile is null || !expectedProfile.IsComplete)
                        {
                            throw new InvalidOperationException(
                                T("Dynamic.Global.Error.RestoreProfileUnsupported", UiText.ModelName(verifiedIdentity.Model)));
                        }

                        expectedBus = verifiedIdentity.BusSerialHex;
                    }
                }

                await ConnectGlobalCoreAsync(expectedProfile, expectedBus, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                RenderGlobalConnection();
                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.ConnectFailed", exception.Message);
            }
        });
    }

    /// <summary>One outer operation: busy gate, progress lifetime and a final refresh, like the WPF try/finally.</summary>
    private async Task RunGlobalOperationAsync(Func<CancellationToken, Task> work)
    {
        _globalOperationActive = true;
        BeginGlobalProgress();
        try
        {
            await RunBusyAsync(work);
        }
        catch (OperationCanceledException)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Status.Canceled");
        }
        finally
        {
            EndGlobalProgress();
            _globalOperationActive = false;
            RefreshGlobalRecoveryActions();
            RefreshActions();
        }
    }

    private string? ExpectedGlobalBusSerial() =>
        _globalRecoveryMode == GlobalRecoveryMode.Restore ? _globalRestoreSet?.Identity.BusSerialHex : null;

    /// <summary>Reuses the verified session; a different profile or bus serial is refused, never a silent switch.</summary>
    private async Task<bool> EnsureGlobalConnectionAsync(
        EdlDeviceProfile profile,
        string? expectedBusSerialHex,
        CancellationToken token)
    {
        if (_globalEdlClient is null)
        {
            await ConnectGlobalCoreAsync(profile, expectedBusSerialHex, token);
            return _globalEdlClient?.HasInitialSaharaProof == true;
        }

        if (!string.Equals(_globalProfile?.ProfileId, profile.ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(T("Dynamic.Global.Error.ConnectionProfileMismatch"));
        }

        if (expectedBusSerialHex is not null
            && !string.Equals(_globalEdlEntry?.BusSerialHex, expectedBusSerialHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                T("Dynamic.Global.Error.ConnectionBusMismatch", _globalEdlEntry?.BusSerialHex ?? UnknownValue));
        }

        // A missing proof allows one explicit probe on the current session folder, never a new entry.
        var client = _globalEdlClient;
        if (!client.HasInitialSaharaProof)
        {
            var probe = NewGlobalProbePath();
            await Task.Run(() => client.ProbeRawGeometryAsync(0, !client.HasInitialSaharaProof, probe, token), token);
        }

        return client.HasInitialSaharaProof;
    }

    /// <summary>One connection: bind the single present 9008 endpoint and identify it, else reboot the single authorized Android device once.</summary>
    private async Task ConnectGlobalCoreAsync(
        EdlDeviceProfile? expectedProfile,
        string? expectedBusSerialHex,
        CancellationToken token)
    {
        if (_globalEdlTools is not null && _globalEdlClient is null)
        {
            throw new InvalidOperationException(T("Dynamic.Global.Status.BlockedBySession"));
        }

        token.ThrowIfCancellationRequested();

        var anyPresentEdl = await HasPresentGlobalEdlAsync(Discovery, token);

        if (!anyPresentEdl && _globalRecoveryMode == GlobalRecoveryMode.Restore)
        {
            // A restore only connects when a 9008 endpoint is already present: never reboot Android into 9008.
            throw new InvalidOperationException(T("Dynamic.Global.Error.ManualEdlRequired"));
        }

        if (anyPresentEdl)
        {
            // A real 9008 endpoint: identify it read-only first (shared client only, no Firehose, no probe).
            SetText(GlobalProgressStatusText, "Dynamic.Global.Status.BindingExistingEdl");

            EdlHardwareIdentity detectedHardware;
            using (var identificationTools = await LoadGlobalToolsAsync(GlobalIdentificationProfile(), token))
            {
                var clientPath = identificationTools.ClientPath;
                detectedHardware = await Task.Run(
                    () => GlobalEdlDetection.DetectIdentityAsync(clientPath, _runner, Discovery, expectedBusSerialHex, token),
                    token);
            }

            var detectedProfile = detectedHardware.Profile;
            if (expectedProfile is not null
                && !string.Equals(expectedProfile.ProfileId, detectedProfile.ProfileId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(T("Dynamic.Global.Error.RestoreProfileMismatch"));
            }

            var entry = await Task.Run(() => GlobalEdlBinding.Attach(detectedProfile, Discovery, expectedBusSerialHex), token);
            var tools = await LoadGlobalToolsAsync(detectedProfile, token);
            _globalProfile = detectedProfile;
            _globalEdlTools = tools;
            _globalEntryAttempted = true;
            _globalEdlEntry = entry;
            _globalPower = null;

            var client = new EdlClient(tools, entry, _runner, Discovery, detectedHardware);
            _globalEdlClient = client;
            var probe = NewGlobalProbePath();
            await Task.Run(() => client.ProbeRawGeometryAsync(0, !client.HasInitialSaharaProof, probe, token), token);

            RenderGlobalConnection();
            RenderGlobalPower();
            AppendGlobalLog(M("Dynamic.Global.Log.ConnectedEdl", UiText.ModelName(detectedProfile.Model), entry.BusSerialHex));
            SetText(GlobalProgressStatusText, "Dynamic.Global.Status.Connected");
            return;
        }

        var transport = Transport;
        SetText(GlobalProgressStatusText, "Dynamic.Global.Status.ReadingAndroid");
        var devices = await transport.ListDevicesAsync(token);
        if (devices.Count == 0)
        {
            throw new InvalidOperationException(T("Dynamic.Global.Error.NoAdbDevice"));
        }

        if (devices.Count > 1)
        {
            throw new InvalidOperationException(T("Dynamic.Global.Error.MultipleAdbDevices"));
        }

        var device = devices[0];
        if (device.ConnectionState != ConnectionState.Authorized)
        {
            throw new InvalidOperationException(T("Dynamic.Global.Error.UnauthorizedAdb"));
        }

        var identity = await transport.ReadAdbIdentityAsync(device.Serial, token);
        var resolved = EdlDeviceProfiles.FindByModel(identity.Model);
        if (resolved is null || !resolved.IsComplete)
        {
            throw new InvalidOperationException(
                T("Dynamic.Global.Error.RestoreProfileUnsupported", UiText.ModelName(identity.Model)));
        }

        var profile = resolved;
        if (expectedProfile is not null
            && !string.Equals(expectedProfile.ProfileId, profile.ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(T("Dynamic.Global.Error.RestoreProfileMismatch"));
        }

        var android = await new MaintenanceEntry(transport.AdbPath, transport.FastbootPath, _runner)
            .ReadAndroidAsync(identity, token);

        // With a restore source selected the identity is checked before any reboot: never reboot the wrong tablet.
        if (expectedBusSerialHex is not null && !AndroidSocMatchesBus(android.SocSerial, expectedBusSerialHex))
        {
            throw new InvalidOperationException(
                T("Dynamic.Global.Error.SocSerialMismatch", android.SocSerial, expectedBusSerialHex));
        }

        _globalPower = await CapturePowerAsync(transport.AdbPath, identity.Serial, token);
        RenderGlobalPower();

        var pendingTools = await LoadGlobalToolsAsync(profile, token);
        _globalProfile = profile;
        _globalEdlTools = pendingTools;
        _globalEntryAttempted = true;

        SetText(GlobalProgressStatusText, "Dynamic.Global.Status.EnteringEdl");
        var entered = await Task.Run(
            () => new EdlEntry(transport.AdbPath, _runner, Discovery).EnterAsync(android, profile, token),
            token);

        if (expectedBusSerialHex is not null
            && !string.Equals(entered.BusSerialHex, expectedBusSerialHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                T("Dynamic.Global.Error.EntryBusMismatch", entered.BusSerialHex, expectedBusSerialHex));
        }

        _globalEdlEntry = entered;
        var enteredClient = new EdlClient(_globalEdlTools, entered, _runner, Discovery);
        _globalEdlClient = enteredClient;
        var enteredProbe = NewGlobalProbePath();
        await Task.Run(
            () => enteredClient.ProbeRawGeometryAsync(0, !enteredClient.HasInitialSaharaProof, enteredProbe, token),
            token);

        RenderGlobalConnection();
        RenderGlobalPower();
        AppendGlobalLog(M("Dynamic.Global.Log.Connected", UiText.ModelName(profile.Model), entered.BusSerialHex));
        SetText(GlobalProgressStatusText, "Dynamic.Global.Status.Connected");
    }

    /// <summary>Read-only presence check: enumerate on a worker thread and match only the existing VID/PID.</summary>
    private static Task<bool> HasPresentGlobalEdlAsync(IEdlEndpointDiscovery discovery, CancellationToken token) =>
        Task.Run(
            () =>
            {
                token.ThrowIfCancellationRequested();
                return discovery.ReadPresent()
                    .Any(endpoint => endpoint is not null && IsGlobalEdlInstance(endpoint.DeviceInstanceId));
            },
            token);

    private static bool IsGlobalEdlInstance(string? deviceInstanceId) =>
        !string.IsNullOrEmpty(deviceInstanceId)
        && deviceInstanceId.Contains("VID_05C6", StringComparison.OrdinalIgnoreCase)
        && deviceInstanceId.Contains("PID_9008", StringComparison.OrdinalIgnoreCase);

    private static bool AndroidSocMatchesBus(string socSerial, string expectedBusSerialHex) =>
        !string.IsNullOrWhiteSpace(socSerial)
        && uint.TryParse(socSerial, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && string.Equals(value.ToString("X8", CultureInfo.InvariantCulture), expectedBusSerialHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The toolchain used only to read the 9008 identity before the model is known. Every model's archive carries
    /// the same pinned client, so any available one will do; the WPF window always uses TB322FC's, which every
    /// release embeds. A Mac build may instead take its tools from an external directory holding only some models,
    /// so the first complete profile whose archive is present is used. The detected model's own archive is loaded
    /// afterwards for everything model-specific.
    /// </summary>
    private EdlDeviceProfile GlobalIdentificationProfile()
    {
        foreach (var model in EdlDeviceProfiles.Models)
        {
            var profile = EdlDeviceProfiles.FindByModel(model);
            if (profile is { IsComplete: true } && !string.IsNullOrWhiteSpace(profile.ArchiveLogicalName)
                && MacTools.Exists(profile.ArchiveLogicalName!))
            {
                return profile;
            }
        }

        throw new InvalidOperationException(T("Dynamic.Global.Error.ToolsMissing"));
    }

    private async Task<EdlToolchain> LoadGlobalToolsAsync(EdlDeviceProfile profile, CancellationToken token)
    {
        if (!profile.IsComplete || string.IsNullOrWhiteSpace(profile.ArchiveLogicalName))
        {
            throw new InvalidOperationException(T("Dynamic.Global.Error.ProfileIncomplete"));
        }

        EdlToolchain tools;
        await using (var archive = MacTools.Open(profile.ArchiveLogicalName!))
        {
            if (archive is null)
            {
                throw new InvalidOperationException(T("Dynamic.Global.Error.ToolsMissing"));
            }

            tools = await EdlToolchain.LoadAsync(archive, Path.Combine(MacCacheRoot, GlobalToolchainFolder), token);
        }

        if (!ReferenceEquals(tools.Profile, profile))
        {
            tools.Dispose();
            throw new InvalidOperationException(T("Dynamic.Global.Error.ToolsProfileMismatch"));
        }

        return tools;
    }

    // ================================================================ read-only backup

    private async void OnGlobalBackupAction(object? sender, RoutedEventArgs e)
    {
        if (_busy || _globalOperationActive || _globalRestoreUncertain || _globalResetUnknown
            || _globalRestoreWriteAttempted || _globalRecoveryMode != GlobalRecoveryMode.Backup)
        {
            return;
        }

        if (_globalEdlClient?.HasInitialSaharaProof != true)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Status.NeedVerifiedConnection");
            return;
        }

        // A verified connection is enough: without a folder yet, ask once; cancelling keeps every state.
        if (string.IsNullOrWhiteSpace(_globalOutputParent))
        {
            await PickGlobalOutputAsync();
            if (string.IsNullOrWhiteSpace(_globalOutputParent))
            {
                return;
            }
        }

        InvalidateGlobalRestorePreparation();
        SetText(GlobalRestoreSummaryText, "Dynamic.Global.Restore.NotPrepared");

        var client = _globalEdlClient!;
        var parent = _globalOutputParent!;
        await RunGlobalOperationAsync(async token =>
        {
            try
            {
                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.BackupRunning");
                var progress = new Progress<GlobalEdlProgress>(RenderGlobalProgress);
                var set = await Task.Run(() => GlobalEdlBackup.CreateAsync(client, parent, progress, token), token);
                MarkGlobalProgressComplete();
                try
                {
                    SetText(
                        GlobalBackupSummaryText,
                        "Dynamic.Global.Backup.Summary",
                        set.DirectoryPath,
                        set.PartitionCount,
                        FormatGlobalBytes(set.RawBytes),
                        FormatGlobalBytes(set.CompressedBytes));
                    AppendGlobalLog(M("Dynamic.Global.Log.BackupComplete", set.PartitionCount, FormatGlobalBytes(set.RawBytes)));
                    SetText(GlobalProgressStatusText, "Dynamic.Global.Status.BackupComplete");
                }
                finally
                {
                    // Only the managed handles are released; the backup files stay.
                    set.Dispose();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                SetText(GlobalProgressStatusText, GlobalBackupFailureMessage(exception));
                AppendGlobalLog(M("Dynamic.Global.Log.BackupFailed", exception.ToString()));
            }
        });
    }

    private static UiMessage GlobalBackupFailureMessage(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        const string virtualAbPrefix = "global-backup-android-update-unconfirmed:";
        const string linuxSuffix = "; this is not the original Android layout.";

        if (message.StartsWith(virtualAbPrefix, StringComparison.Ordinal))
        {
            return M("Dynamic.Global.Error.AndroidUpdateUnconfirmed");
        }

        var linuxPartitionName = message.StartsWith("LUN", StringComparison.Ordinal)
            && message.Contains(" carries the Linux partition name '", StringComparison.Ordinal)
            && message.EndsWith(linuxSuffix, StringComparison.Ordinal);
        var linuxTypeGuid = message.StartsWith("Partition '", StringComparison.Ordinal)
            && message.Contains(" uses the Linux type GUID ", StringComparison.Ordinal)
            && message.EndsWith(linuxSuffix, StringComparison.Ordinal);
        return linuxPartitionName || linuxTypeGuid
            ? M("Dynamic.Global.Error.LinuxAlreadyInstalled")
            : M("Dynamic.Global.Status.BackupFailed", exception.Message);
    }

    // ================================================================ read-only restore preparation

    private async void OnGlobalPrepareRestoreAction(object? sender, RoutedEventArgs e)
    {
        if (_busy || _globalOperationActive || _globalRestoreUncertain || _globalResetUnknown
            || _globalRestoreWriteAttempted || _globalRecoveryMode != GlobalRecoveryMode.Restore)
        {
            return;
        }

        if (!_globalRestoreEnvironmentReady)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Error.RestoreEnvironmentRequired");
            return;
        }

        if (string.IsNullOrWhiteSpace(_globalRestoreSource))
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Error.RestoreSourceRequired");
            return;
        }

        var source = _globalRestoreSource!;
        await RunGlobalOperationAsync(async token =>
        {
            try
            {
                InvalidateGlobalRestorePreparation();
                var progress = new Progress<GlobalEdlProgress>(RenderGlobalProgress);

                // Operation records live under the app work root, never inside the chosen backup source.
                var recordsParent = Path.Combine(MacCacheRoot, "records");
                if (!IsGlobalOutputOutsideSource(recordsParent, source))
                {
                    throw new InvalidOperationException(T("Dynamic.Global.Error.RestoreRecordsInsideSource"));
                }

                if (!Directory.Exists(recordsParent))
                {
                    Directory.CreateDirectory(recordsParent);
                }

                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.VerifyingRestoreSource");
                var set = await Task.Run(() => GlobalEdlBackup.OpenAsync(source, progress, token), token);
                _globalRestoreSet = set;
                var identity = set.Identity;

                // Models whose restore write route is still closed (TB710FU) stop before any connection.
                if (DeviceModelRoutes.IsWriteRoutePending(identity.Model))
                {
                    throw new InvalidOperationException(
                        T("Dynamic.Global.Error.RestoreRoutePending", UiText.ModelName(identity.Model)));
                }

                var profile = _globalEdlClient is not null ? _globalProfile : EdlDeviceProfiles.FindByModel(identity.Model);
                if (profile is null || !profile.IsComplete)
                {
                    throw new InvalidOperationException(
                        T("Dynamic.Global.Error.RestoreProfileUnsupported", UiText.ModelName(identity.Model)));
                }

                if (!string.Equals(profile.ProfileId, identity.ProfileId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(T("Dynamic.Global.Error.RestoreProfileMismatch"));
                }

                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.ConnectingForRestore");
                if (!await EnsureGlobalConnectionAsync(profile, identity.BusSerialHex, token))
                {
                    throw new InvalidOperationException(T("Dynamic.Global.Status.NeedVerifiedConnection"));
                }

                if (!string.Equals(_globalProfile?.ProfileId, identity.ProfileId, StringComparison.Ordinal)
                    || !string.Equals(_globalEdlEntry?.BusSerialHex, identity.BusSerialHex, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        T("Dynamic.Global.Error.ConnectionBusMismatch", _globalEdlEntry?.BusSerialHex ?? UnknownValue));
                }

                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.PreparingRestore");
                var client = _globalEdlClient!;
                var plan = await Task.Run(() => GlobalEdlRestore.PrepareAsync(client, set, recordsParent, progress, token), token);
                _globalRestorePlan = plan;
                GlobalRestoreConfirmCheck.IsChecked = false;

                SetText(
                    GlobalRestoreSummaryText,
                    "Dynamic.Global.Restore.Summary",
                    UiText.ModelName(plan.Identity.Model),
                    plan.Identity.BusSerialHex,
                    plan.RegionCount,
                    FormatGlobalBytes(plan.WriteBytes),
                    FormatGlobalGiB(plan.AndroidBytes),
                    plan.EvidenceDirectory);
                AppendGlobalLog(M("Dynamic.Global.Log.RestorePrepared", plan.RegionCount, FormatGlobalBytes(plan.WriteBytes)));
                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.RestorePrepared");
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                DisposeGlobalRestorePlanOnly();
                SetText(GlobalRestoreSummaryText, "Dynamic.Global.Restore.NotPrepared");
                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.RestorePrepareFailed", exception.Message);
            }
        });
    }

    // ================================================================ the single restore write

    private async void OnGlobalRestoreAction(object? sender, RoutedEventArgs e)
    {
        if (_busy || _globalOperationActive || _globalRestoreUncertain || _globalResetUnknown
            || _globalRestoreWriteAttempted || _globalRecoveryMode != GlobalRecoveryMode.Restore)
        {
            return;
        }

        var plan = _globalRestorePlan;
        if (plan is null || GlobalRestoreConfirmCheck.IsChecked != true || _globalEdlClient?.HasInitialSaharaProof != true)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Status.NeedVerifiedConnection");
            return;
        }

        if (!await MessageDialog.AskAsync(
                this,
                T("Dynamic.Global.Dialog.Title"),
                T("Dynamic.Global.Dialog.ConfirmRestore",
                    UiText.ModelName(plan.Identity.Model),
                    plan.Identity.BusSerialHex,
                    plan.RegionCount,
                    FormatGlobalBytes(plan.WriteBytes),
                    FormatGlobalGiB(plan.AndroidBytes),
                    plan.EvidenceDirectory,
                    FormatGlobalPowerForDialog()),
                T("Dynamic.Mac.Dialog.Yes"),
                T("Dynamic.Mac.Dialog.No")))
        {
            return;
        }

        _globalOperationActive = true;
        BeginGlobalProgress();
        try
        {
            await RunBusyAsync(async token =>
            {
                // Marked before the call: any later exception means a write may have started.
                _globalRestoreWriteAttempted = true;
                try
                {
                    SetText(GlobalProgressStatusText, "Dynamic.Global.Status.RestoreRunning");
                    var progress = new Progress<GlobalEdlProgress>(RenderGlobalProgress);
                    var result = await Task.Run(
                        () => GlobalEdlRestore.ExecuteAsync(plan, plan.ConfirmationId, progress, token),
                        token);

                    if (result.Success)
                    {
                        MarkGlobalProgressComplete();
                        _globalRestoreSucceeded = true;
                        SetText(
                            GlobalRestoreSummaryText,
                            "Dynamic.Global.Restore.Succeeded",
                            UiText.ModelName(plan.Identity.Model),
                            plan.Identity.BusSerialHex,
                            plan.RegionCount,
                            FormatGlobalBytes(plan.WriteBytes),
                            plan.EvidenceDirectory);
                        AppendGlobalLog(M("Dynamic.Global.Log.RestoreSucceeded", result.VerifiedRegions.Count, plan.EvidenceDirectory));
                        SetText(GlobalProgressStatusText, "Dynamic.Global.Status.RestoreSucceeded");
                        return;
                    }

                    if (result.AnyWriteStarted)
                    {
                        MarkGlobalRestoreUncertain(result.Error);
                        return;
                    }

                    // Clearly nothing was written: the attempt is cleared and a new preparation is allowed.
                    _globalRestoreWriteAttempted = false;
                    DisposeGlobalRestorePlanOnly();
                    SetText(GlobalRestoreSummaryText, "Dynamic.Global.Restore.NotPrepared");
                    SetText(GlobalProgressStatusText, "Dynamic.Global.Status.RestoreNotStarted",
                        result.Error ?? T("Dynamic.Global.Error.Unknown"));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // The execute call was made: any exception counts as "a write may have started", no retry.
                    MarkGlobalRestoreUncertain(exception.Message);
                }
            });
        }
        finally
        {
            EndGlobalProgress();
            _globalOperationActive = false;
            RefreshGlobalRecoveryActions();
            RefreshActions();
        }
    }

    private void MarkGlobalRestoreUncertain(string? detail)
    {
        _globalRestoreUncertain = true;
        _globalRestoreWriteAttempted = true;
        var text = string.IsNullOrWhiteSpace(detail) ? T("Dynamic.Global.Error.Unknown") : detail!;
        AppendGlobalLog(M("Dynamic.Global.Log.RestoreUncertain", text));
        SetText(GlobalProgressStatusText, "Dynamic.Global.Status.RestoreUncertain", text);
        // The old Sahara proof no longer describes the device: show the unverified state right away.
        RenderGlobalConnection();
    }

    // ================================================================ explicit restart

    private async void OnGlobalRestart(object? sender, RoutedEventArgs e)
    {
        if (_busy || _globalOperationActive || _globalResetAttempted || _globalResetUnknown || _globalRestoreUncertain)
        {
            return;
        }

        if (_globalRestoreWriteAttempted && !_globalRestoreSucceeded)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Status.BlockedBySession");
            return;
        }

        if (_globalEdlClient?.HasInitialSaharaProof != true)
        {
            SetText(GlobalProgressStatusText, "Dynamic.Global.Status.NeedVerifiedConnection");
            return;
        }

        var client = _globalEdlClient;
        _globalOperationActive = true;
        BeginGlobalProgress();
        try
        {
            await RunBusyAsync(async token =>
            {
                // One reset only: marked before the call.
                _globalResetAttempted = true;
                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.Resetting");

                EdlResetEvidence evidence;
                try
                {
                    evidence = await Task.Run(() => client.ResetAsync(token), token);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _globalResetUnknown = true;
                    AppendGlobalLog(M("Dynamic.Global.Log.ResetUnknown", exception.Message));
                    SetText(GlobalProgressStatusText, "Dynamic.Global.Status.ResetUnknown", exception.Message);
                    return;
                }

                if (!evidence.CommandAccepted)
                {
                    _globalResetUnknown = true;
                    SetText(GlobalProgressStatusText, "Dynamic.Global.Status.ResetUnknown", T("Dynamic.Global.Error.ResetNotAccepted"));
                    return;
                }

                ReleaseGlobalSessionAfterReset();
                AppendGlobalLog(M("Dynamic.Global.Log.ResetSent"));
                SetText(GlobalProgressStatusText, "Dynamic.Global.Status.ResetSent");
            });
        }
        finally
        {
            EndGlobalProgress();
            _globalOperationActive = false;
            RenderGlobalConnection();
            RenderGlobalPower();
            RefreshGlobalRecoveryActions();
            RefreshActions();
        }
    }

    /// <summary>After an accepted reset only this page's session is released; backups and evidence stay.</summary>
    private void ReleaseGlobalSessionAfterReset()
    {
        _globalEdlClient = null;
        _globalEdlEntry = null;
        _globalProfile = null;
        _globalEntryAttempted = false;
        _globalEdlTools?.Dispose();
        _globalEdlTools = null;
        _globalRestorePlan?.Dispose();
        _globalRestorePlan = null;
        _globalRestoreSet?.Dispose();
        _globalRestoreSet = null;
        _globalResetAttempted = false;
        _globalResetUnknown = false;
        _globalRestoreWriteAttempted = false;
        _globalRestoreUncertain = false;
        _globalRestoreSucceeded = false;
        _globalSessionDirectory = null;
        GlobalRestoreConfirmCheck.IsChecked = false;
        RenderGlobalConnection();
        RenderGlobalPower();
    }
}
