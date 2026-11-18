using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FreedomInstaller.Core;
using Microsoft.Win32;

namespace FreedomInstaller.App;

public partial class MainWindow
{
    private const string DiagnosticIssuesUrl = "https://github.com/ArmadaOS-Freedom/armadaos-freedom-installer/issues";

    private static readonly string[] DiagnosticBootRoleNames =
    {
        "abl_a", "abl_b", "vbmeta_a", "vbmeta_b", "vbmeta_system_a", "vbmeta_system_b",
        "boot_a", "boot_b", "vendor_boot_a", "vendor_boot_b", "init_boot_a", "init_boot_b",
    };

    private bool _diagnosticExporting;
    private bool _showingDiagnostics;
    private string? _diagnosticDestinationPath;
    private IReadOnlyList<DiagnosticBootFile> _diagnosticBootSnapshot = Array.Empty<DiagnosticBootFile>();
    private bool _diagnosticAblAvailable;
    private bool _diagnosticBootAvailable;

    // Full failure context kept for the diagnostic summary only. The UI keeps showing
    // the first line as before; the summary redaction reads this nullable field so a
    // multi-line reason is not truncated to what the label displays.
    private string? _diagnosticLastFailureFullDetail;

    /// <summary>记录最近一次诊断失败；面板在首次失败后可见，导出失败不清空此内容。</summary>
    private void RecordDiagnosticFailure(UiMessage stage, string? detail)
    {
        _diagnosticLastFailureFullDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;
        var firstLine = FirstLine(detail);
        if (DiagnosticFailureText is not null)
            SetText(DiagnosticFailureText, "Dynamic.Diagnostics.Failure", stage, firstLine);
        if (DiagnosticFailurePanel is not null)
            DiagnosticFailurePanel.Visibility = _showingDiagnostics ? Visibility.Collapsed : Visibility.Visible;
        NoteDiagnosticAttachmentFailure(stage);
    }

    private void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        ShowDiagnosticPage();
    }

    private void ShowDiagnosticPage()
    {
        _showingDiagnostics = true;
        _diagnosticDestinationPath = null;
        _diagnosticAblAvailable = false;
        _diagnosticBootAvailable = false;
        _diagnosticBootSnapshot = GetDiagnosticPageBootFiles();
        _diagnosticAblAvailable = HasAblBootRole(_diagnosticBootSnapshot);
        _diagnosticBootAvailable = HasBootRole(_diagnosticBootSnapshot);

        if (DiagnosticIncludeAblBox is not null)
        {
            DiagnosticIncludeAblBox.IsChecked = _diagnosticAblAvailable;
            DiagnosticIncludeAblBox.IsEnabled = _diagnosticAblAvailable;
            DiagnosticIncludeAblBox.Visibility = _diagnosticAblAvailable ? Visibility.Visible : Visibility.Collapsed;
        }
        if (DiagnosticIncludeLargeBootBox is not null)
        {
            DiagnosticIncludeLargeBootBox.IsChecked = _diagnosticBootAvailable;
            DiagnosticIncludeLargeBootBox.IsEnabled = _diagnosticBootAvailable;
            DiagnosticIncludeLargeBootBox.Visibility = _diagnosticBootAvailable ? Visibility.Visible : Visibility.Collapsed;
        }

        bool anyAttachment = _diagnosticAblAvailable || _diagnosticBootAvailable;
        if (DiagnosticNoAttachmentsText is not null)
            DiagnosticNoAttachmentsText.Visibility = anyAttachment ? Visibility.Collapsed : Visibility.Visible;

        if (DiagnosticDestinationText is not null)
        {
            if (string.IsNullOrWhiteSpace(_diagnosticDestinationPath))
            {
                SetText(DiagnosticDestinationText, "Dynamic.Diagnostics.DestinationPlaceholder");
                DiagnosticDestinationText.Visibility = Visibility.Visible;
            }
            else
            {
                SetText(DiagnosticDestinationText, "Dynamic.Diagnostics.DestinationSelected", _diagnosticDestinationPath);
                DiagnosticDestinationText.Visibility = Visibility.Visible;
            }
        }
        if (DiagnosticConfirmButton is not null)
            DiagnosticConfirmButton.IsEnabled = !_diagnosticExporting && !string.IsNullOrWhiteSpace(_diagnosticDestinationPath);
        if (DiagnosticExportResultText is not null)
        {
            DiagnosticExportResultText.Visibility = Visibility.Collapsed;
            DiagnosticExportResultText.ClearValue(TextBlock.TextProperty);
        }
        if (DiagnosticJoinQqButton is not null)
            DiagnosticJoinQqButton.ToolTip = QqGroupInviteUrl;
        if (DiagnosticIssuesButton is not null)
            DiagnosticIssuesButton.ToolTip = DiagnosticIssuesUrl;

        ApplyFunctionView();
    }

    private void OnOpenDiagnosticIssues(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.Diagnostics.Issues", DiagnosticIssuesUrl);

    private void OnCloseDiagnosticPage(object sender, RoutedEventArgs e)
    {
        if (_diagnosticExporting) return;
        _showingDiagnostics = false;
        ApplyFunctionView();
    }

    private void OnChooseDiagnosticDestination(object sender, RoutedEventArgs e)
    {
        if (_diagnosticExporting || !_showingDiagnostics) return;

        var dlg = new SaveFileDialog
        {
            Title = T("Dynamic.Diagnostics.SaveTitle"),
            Filter = "ZIP (*.zip)|*.zip",
            FileName = "freedom-diagnostics-" + Guid.NewGuid().ToString("N") + ".zip",
            AddExtension = true,
            DefaultExt = ".zip",
            OverwritePrompt = true,
        };
        if (!string.IsNullOrWhiteSpace(_diagnosticDestinationPath))
        {
            dlg.InitialDirectory = Path.GetDirectoryName(_diagnosticDestinationPath);
            dlg.FileName = Path.GetFileName(_diagnosticDestinationPath);
        }

        if (dlg.ShowDialog(this) != true)
            return;

        _diagnosticDestinationPath = dlg.FileName;
        if (DiagnosticDestinationText is not null)
        {
            SetText(DiagnosticDestinationText, "Dynamic.Diagnostics.DestinationSelected", _diagnosticDestinationPath);
            DiagnosticDestinationText.Visibility = Visibility.Visible;
        }
        if (DiagnosticConfirmButton is not null)
            DiagnosticConfirmButton.IsEnabled = !_diagnosticExporting;
    }

    private async void OnConfirmDiagnosticExport(object sender, RoutedEventArgs e)
    {
        if (_diagnosticExporting || _closed || !_showingDiagnostics)
            return;

        string? destination = _diagnosticDestinationPath;
        if (string.IsNullOrWhiteSpace(destination))
            return;

        _diagnosticExporting = true;
        if (DiagnosticConfirmButton is not null)
            DiagnosticConfirmButton.IsEnabled = false;
        if (DiagnosticExportButton is not null)
            DiagnosticExportButton.IsEnabled = false;

        try
        {
            string? sessionDir = _workSession?.DirectoryPath;
            string? workRoot = _workDirectory?.RootPath ?? _manualWorkRoot;

            var currentSnapshot = GetDiagnosticPageBootFiles();
            var bootFiles = _diagnosticBootSnapshot
                .Where(f => currentSnapshot.Contains(f))
                .ToList();
            bool includeAbl = HasAblBootRole(bootFiles) && DiagnosticIncludeAblBox?.IsChecked == true;
            bool includeLargeBoot = HasBootRole(bootFiles) && DiagnosticIncludeLargeBootBox?.IsChecked == true;

            string logFallback = _pendingWorkLogLines.Count > 0
                ? string.Join(Environment.NewLine, _pendingWorkLogLines)
                : BuildUiLogFallback();

            var redactions = new List<string>();
            void AddRedaction(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) redactions.Add(value.Trim());
            }
            AddRedaction(sessionDir);
            AddRedaction(workRoot);
            AddRedaction(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            AddRedaction(_internalInstallDeviceCheck?.FastbootSerial);
            AddRedaction(_internalInstallDeviceCheck?.Android?.SocSerial);
            AddRedaction(_internalInstallDeviceCheck?.Android?.Identity?.Serial);

            var summary = BuildDiagnosticSummary(includeAbl, includeLargeBoot);
            var hostEnvironment = BuildDiagnosticHostEnvironment();

            var request = await Task.Run(() =>
            {
                string logText = logFallback;
                if (sessionDir is not null)
                {
                    try
                    {
                        InstallerLog.Flush();
                        var logPath = Path.Combine(sessionDir, InstallerLog.FileName);
                        var onDisk = ReadShared(logPath);
                        if (!string.IsNullOrWhiteSpace(onDisk))
                            logText = onDisk;
                    }
                    catch
                    {
                        logText = logFallback;
                    }
                }

                if (string.IsNullOrWhiteSpace(logText))
                    logText = "FreedomInstaller diagnostics" + Environment.NewLine;

                var destDir = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(destDir);

                return new DiagnosticExportRequest(
                    summary,
                    logText,
                    redactions,
                    bootFiles,
                    includeAbl,
                    includeLargeBoot)
                {
                    HostEnvironment = hostEnvironment,
                };
            }).ConfigureAwait(true);

            await Task.Run(() => DiagnosticBundle.ExportAsync(destination, request, CancellationToken.None)).ConfigureAwait(true);

            if (DiagnosticExportResultText is not null)
            {
                SetText(DiagnosticExportResultText, "Dynamic.Diagnostics.Saved", destination);
                DiagnosticExportResultText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            if (DiagnosticExportResultText is not null)
            {
                SetText(DiagnosticExportResultText, "Dynamic.Diagnostics.Failed", FirstLine(ex.Message));
                DiagnosticExportResultText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _diagnosticExporting = false;
            if (DiagnosticExportButton is not null)
                DiagnosticExportButton.IsEnabled = true;
            if (DiagnosticConfirmButton is not null)
                DiagnosticConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(_diagnosticDestinationPath);
        }
    }

    private static bool HasAblBootRole(IReadOnlyList<DiagnosticBootFile> files)
    {
        return files.Any(f =>
            f.Role.StartsWith("abl_", StringComparison.Ordinal) ||
            f.Role.StartsWith("vbmeta_", StringComparison.Ordinal));
    }

    private static bool HasBootRole(IReadOnlyList<DiagnosticBootFile> files)
    {
        return files.Any(f =>
            f.Role.StartsWith("boot_", StringComparison.Ordinal) ||
            f.Role.StartsWith("vendor_boot_", StringComparison.Ordinal) ||
            f.Role.StartsWith("init_boot_", StringComparison.Ordinal));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private string BuildUiLogFallback()
    {
        try
        {
            var rows = _localizedLogRows;
            if (rows is null || rows.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            foreach (var entry in rows)
            {
                var text = entry.Row?.Text;
                if (!string.IsNullOrEmpty(text)) builder.AppendLine(text);
            }
            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private string BuildDiagnosticSummary(bool includeAbl, bool includeLargeBoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FreedomInstaller diagnostics");
        sb.AppendLine("Mode: " + (IsInternalInstallMode ? "internal-install" : "standard"));

        var installerVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString();
        if (!string.IsNullOrWhiteSpace(installerVersion)) sb.AppendLine("InstallerVersion: " + installerVersion);
        if (SelectedOperationText?.Text is { Length: > 0 } op) sb.AppendLine("SelectedOperation: " + op);

        var model = _internalInstallDeviceCheck?.Android?.Identity?.Model;
        if (!string.IsNullOrWhiteSpace(model)) sb.AppendLine("Model: " + model);
        var bootloader = _internalInstallDeviceCheck?.Android?.Identity?.BootloaderState;
        if (bootloader is not null) sb.AppendLine("Bootloader: " + bootloader);
        var slot = _internalInstallDeviceCheck?.CurrentSlot;
        if (!string.IsNullOrWhiteSpace(slot)) sb.AppendLine("CurrentSlot: " + slot);
        var version = _internalInstallPackage?.Validation?.Version;
        if (!string.IsNullOrWhiteSpace(version)) sb.AppendLine("PackageVersion: " + version);
        var fp = _internalInstallPackage?.Validation?.ManifestFingerprint;
        if (!string.IsNullOrWhiteSpace(fp)) sb.AppendLine("ManifestFingerprint: " + fp);
        var pkgId = _internalInstallPackage?.Validation?.PackageId;
        if (!string.IsNullOrWhiteSpace(pkgId)) sb.AppendLine("PackageId: " + pkgId);
        var failedStage = _internalInstallDeviceCheck?.FailedStage;
        if (failedStage is not null) sb.AppendLine("FailedStage: " + failedStage);
        sb.AppendLine("IncludeAbl: " + includeAbl);
        sb.AppendLine("IncludeLargeBoot: " + includeLargeBoot);
        if (HasInternalInstallExecutionStarted) sb.AppendLine("ExecutionStarted: true");

        if (IsInternalInstallMode) sb.AppendLine("InternalInstallStep: " + _internalInstallStep);

        if (DiagnosticFailureText?.Text is { Length: > 0 } latestFailure) sb.AppendLine("LatestFailure: " + latestFailure);
        if (!string.IsNullOrWhiteSpace(_diagnosticLastFailureFullDetail))
            sb.AppendLine("LatestFailureDetail: " + _diagnosticLastFailureFullDetail);
        sb.AppendLine("QQGroup: " + AboutQqGroupNumber);
        sb.AppendLine("QQGroupInvite: " + QqGroupInviteUrl);
        sb.AppendLine("IssuesUrl: " + DiagnosticIssuesUrl);

        if (HasInternalInstallLiveEdlState) sb.AppendLine("LiveEdlState: true");
        if (InternalInstallExitMaintenanceStatusText?.Text is { Length: > 0 } maintenance) sb.AppendLine("ExitMaintenanceStatus: " + maintenance);

        var fastbootLock = _internalInstallDeviceCheck?.FastbootLockState;
        if (fastbootLock is not null) sb.AppendLine("FastbootLockState: " + fastbootLock);

        if (_internalInstallDeviceCheck?.OtaState is { } ota) sb.AppendLine("OtaState: " + ota);
        if (_internalInstallDeviceCheck?.AndroidReturn is { } otaReturn) sb.AppendLine("OtaReturn: " + otaReturn);

        var backup = _internalInstallBackup;
        if (backup is not null)
        {
            if (!string.IsNullOrWhiteSpace(backup.Model)) sb.AppendLine("BackupModel: " + backup.Model);
            if (!string.IsNullOrWhiteSpace(backup.AndroidBuild)) sb.AppendLine("BackupAndroidBuild: " + backup.AndroidBuild);
            if (!string.IsNullOrWhiteSpace(backup.ProfileId)) sb.AppendLine("BackupProfileId: " + backup.ProfileId);
            if (!string.IsNullOrWhiteSpace(backup.ManifestSha256)) sb.AppendLine("BackupManifestSha256: " + backup.ManifestSha256);
            if (backup.Tables is not null)
            {
                foreach (var pair in backup.Tables)
                {
                    var table = pair.Value;
                    sb.AppendLine("Table: LUN " + pair.Key.ToString(CultureInfo.InvariantCulture) + " SectorSize=" + table.SectorSize.ToString(CultureInfo.InvariantCulture));
                    if (table.Partitions is null) continue;
                    foreach (var part in table.Partitions)
                    {
                        sb.AppendLine("  Partition: " + part.Name
                            + " StartSector=" + part.StartSector.ToString(CultureInfo.InvariantCulture)
                            + " SectorCount=" + part.SectorCount.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        if (_internalInstallAllocation is { } allocation)
        {
            if (allocation.AndroidBytes is { } androidBytes) sb.AppendLine("AllocationAndroidBytes: " + androidBytes.ToString(CultureInfo.InvariantCulture));
            if (allocation.LinuxBytes is { } linuxBytes) sb.AppendLine("AllocationLinuxBytes: " + linuxBytes.ToString(CultureInfo.InvariantCulture));
        }

        AppendWorkflowDiagnosticSummary(sb);

        return sb.ToString();
    }

    private IReadOnlyList<DiagnosticBootFile> CollectBootFiles()
    {
        var result = new List<DiagnosticBootFile>();
        try
        {
            var currentSet = _internalInstallBackup;
            var stockSet = _internalInstallRecoverySource;
            if (currentSet?.Files is not null)
                AddBootFilesFrom(result, currentSet.Files, "captured");
            if (stockSet?.Files is not null)
                AddBootFilesFrom(result, stockSet.Files, "stock");
        }
        catch
        {
        }
        return result;
    }

    private static void AddBootFilesFrom(List<DiagnosticBootFile> target, IReadOnlyDictionary<string, EdlCapturedRegion> files, string region)
    {
        foreach (var name in DiagnosticBootRoleNames)
        {
            if (!files.TryGetValue(name, out var info) || info is null) continue;
            if (string.IsNullOrWhiteSpace(info.FilePath)) continue;
            long? bytes = info.Length > 0 ? info.Length : null;
            string? sha = string.IsNullOrWhiteSpace(info.Sha256) ? null : info.Sha256;
            target.Add(new DiagnosticBootFile(region, name, info.FilePath, bytes, sha));
        }
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var idx = text.IndexOfAny(new[] { '\r', '\n' });
        var line = idx >= 0 ? text.Substring(0, idx) : text;
        line = line.Trim();
        if (line.Length > 200) line = line.Substring(0, 200);
        return line;
    }
}
