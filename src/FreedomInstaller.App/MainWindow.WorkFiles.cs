using System;
using System.IO;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

public partial class MainWindow
{
    private InstallerWorkSession? _workSession;
    private InstallerWorkDirectoryResult? _workDirectory;
    private string? _manualWorkRoot = Environment.GetEnvironmentVariable("FREEDOM_WORK_ROOT");
    private bool _workDirectoryReady;

    private string TemporaryWorkRoot
    {
        get
        {
            if (_workSession is not null) return _workSession.DirectoryPath;
            if (!_workDirectoryReady || _workDirectory is null)
                throw new InvalidOperationException("Choose a writable temporary work directory in environment preparation first.");
            return (_workSession = new InstallerWorkSession(_workDirectory.RootPath)).DirectoryPath;
        }
    }

    private async System.Threading.Tasks.Task CheckTemporaryWorkDirectoryAsync(
        System.Threading.CancellationToken cancellationToken)
    {
        _workDirectoryReady = false;
        SetText(EnvironmentWorkStatusText, "Dynamic.Environment.Checking");
        SetEnvironmentIndicator(EnvironmentWorkIndicator, null);
        // An active session must keep its files on the original volume.
        var requestedRoot = _workSession is null ? _manualWorkRoot : _workDirectory?.RootPath;
        try
        {
            var result = await System.Threading.Tasks.Task.Run(
                () => InstallerWorkDirectory.Resolve(requestedRoot), cancellationToken);
            if (_closed) return;
            cancellationToken.ThrowIfCancellationRequested();
            _workDirectory = result;
            _workDirectoryReady = true;
            SetText(EnvironmentWorkPathText, "Dynamic.Environment.WorkPath", result.RootPath);
            SetText(EnvironmentWorkStatusText, "Dynamic.Environment.WorkSpace",
                result.AvailableFreeBytes / (1024d * 1024 * 1024));
            SetEnvironmentIndicator(EnvironmentWorkIndicator, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            if (_closed) return;
            SetText(EnvironmentWorkPathText, string.IsNullOrWhiteSpace(requestedRoot)
                ? M("Dynamic.Environment.WorkUnselected")
                : M("Dynamic.Environment.WorkPath", requestedRoot));
            SetText(EnvironmentWorkStatusText, "Dynamic.Environment.WorkFailed", exception.Message);
            SetEnvironmentIndicator(EnvironmentWorkIndicator, false);
        }
    }

    private bool CanChangeTemporaryWorkDirectory =>
        _ready && !_closed && !_busy && !_environmentDriverInstalling && _workSession is null;

    private async void OnChooseTemporaryWorkDirectory(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!CanChangeTemporaryWorkDirectory) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = T("Static.Environment.WorkChooseTitle")
        };
        if (dialog.ShowDialog(this) != true) return;
        _manualWorkRoot = Path.Combine(dialog.FolderName, "ArmadaOS Freedom", "Work");
        await RecheckTemporaryWorkDirectoryAsync();
    }

    private async void OnAutoTemporaryWorkDirectory(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!CanChangeTemporaryWorkDirectory) return;
        _manualWorkRoot = null;
        await RecheckTemporaryWorkDirectoryAsync();
    }

    private System.Threading.Tasks.Task RecheckTemporaryWorkDirectoryAsync() =>
        RunBusyAsync(async token =>
        {
            await CheckTemporaryWorkDirectoryAsync(token);
            if (!_closed) RenderEnvironmentStatus();
        });

    private void RefreshTemporaryWorkDirectoryActions()
    {
        EnvironmentWorkChooseButton.IsEnabled = CanChangeTemporaryWorkDirectory;
        EnvironmentWorkAutoButton.IsEnabled = CanChangeTemporaryWorkDirectory;
        EnvironmentWorkLockedText.Visibility = _workSession is null
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    private void SaveWorkLogOnClose() => PersistWorkLogIfActive();

    /// <summary>
    /// 工作会话存在时把当前日志写进会话目录。追加一行后立即调用，因此窗口不关闭也能看到最新日志；
    /// 会话尚未建立时什么也不做，绝不声称更早的日志行已经保存。
    /// </summary>
    private void PersistWorkLogIfActive()
    {
        if (_workSession is null) return;
        InstallerLog.TryPersist(
            _workSession.DirectoryPath,
            System.Linq.Enumerable.Select(_localizedLogRows, row => row.Row.Text));
    }

    private void CleanWorkFilesOnClose(bool preserveRecovery)
    {
        if (_workSession is null || preserveRecovery || _internalInstallOperationActive
            || _userdataUninstallOperationActive || _bootMaintenanceOperationActive) return;
        try
        {
            // Closing an idle window releases file leases only; it sends no device command.
            _userdataUninstallBackend?.Dispose();
            _userdataUninstallMenu?.Dispose();
            _userdataUninstallOriginal?.Dispose();
            _bootMaintenanceBackend?.Dispose();
            _internalInstallBackend?.Dispose();
            _internalInstallAssets?.Dispose();
            _internalInstallNativeTools?.Dispose();
            _internalInstallArtifacts?.Dispose();
            _internalInstallPackage?.Dispose();
            _internalInstallBackup?.Dispose();
            _internalInstallRecoverySource?.Dispose();
            _internalInstallEdlTools?.Dispose();
            _workSession.Complete(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Keep the owned directory when a lease cannot be released or a receipt cannot be saved.
            System.Diagnostics.Trace.WriteLine("Installer work cleanup: " + exception.Message);
        }
    }
}
