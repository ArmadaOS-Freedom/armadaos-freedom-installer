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

    /// <summary>
    /// 工作会话建立之前产生的日志行。界面只显示最近 200 行，这里不设上限：
    /// 会话一旦绑定就把它们按原顺序排进追加队列，随后清空，之后只排新产生的行。
    /// </summary>
    private readonly System.Collections.Generic.List<string> _pendingWorkLogLines = new();

    private string TemporaryWorkRoot
    {
        get
        {
            if (_workSession is not null) return _workSession.DirectoryPath;
            if (!_workDirectoryReady || _workDirectory is null)
                throw new InvalidOperationException("Choose a writable temporary work directory in environment preparation first.");
            var session = _workSession = new InstallerWorkSession(_workDirectory.RootPath);
            if (_pendingWorkLogLines.Count > 0)
            {
                // 绑定会话时补写启动阶段的行；这一份批次只入队一次，之后不再重复。
                InstallerLog.EnqueuePersist(session.DirectoryPath, _pendingWorkLogLines.ToArray());
                _pendingWorkLogLines.Clear();
            }

            return session.DirectoryPath;
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

    private void SaveWorkLogOnClose()
    {
        // Wait for every queued append batch. The file already holds the whole session, so closing
        // must not write the 200-row UI snapshot again: that would duplicate or overwrite the log.
        InstallerLog.Flush();
    }

    /// <summary>
    /// 追加一行刚产生的日志。会话尚未建立时先留在本窗口的待写列表里（不设上限）；
    /// 会话已在时只把这一行排进队列，绝不重写整场日志。
    /// </summary>
    private void AppendWorkLogLine(string line)
    {
        if (_workSession is null)
        {
            _pendingWorkLogLines.Add(line);
            return;
        }

        InstallerLog.EnqueuePersist(_workSession.DirectoryPath, new[] { line });
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
