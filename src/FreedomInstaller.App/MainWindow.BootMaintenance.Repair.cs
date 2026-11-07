using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

public partial class MainWindow
{
    private async Task<InternalBackupSet> CaptureRepairCurrentAsync(
        EdlClient client,
        string parent,
        IProgress<InternalBackupProgress> progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new InvalidDataException("Repair capture parent directory is unavailable.");

        string? boardDirectory = _boardRecoveryDiscoveredDirectory;
        if (boardDirectory is null)
        {
            SetInternalInstallBackupStatus(M("Dynamic.RecoverySource.ReadingBoard"));
            AppendLocalizedLog(M("Dynamic.RecoverySource.ReadingBoard"));
            boardDirectory = await BoardRecoveryBackup.ReadDirectoryAsync(
                client, parent, TemporaryWorkRoot, token);
        }

        if (boardDirectory is null)
        {
            AppendLocalizedLog(M("Dynamic.BootMaintenance.Repair.BoardMissingCapture"));
            return await InternalInstallBackup.CreateAsync(client, parent, progress, token);
        }

        var recovery = await InternalInstallBackup.OpenRecoveryAsync(client, boardDirectory, token: token);
        InternalBackupSet? snapshot = null;
        try
        {
            // Historical recovery is only original-epoch evidence, not current rollback state.
            snapshot = await InternalInstallBackup.CreateAsync(client, parent, progress, token);

            BoardRecoveryBackup.ValidateSourceForReuse(snapshot, recovery);

            _internalInstallRecoverySource?.Dispose();
            _internalInstallRecoverySource = recovery;
            _boardRecoveryDiscoveredDirectory = boardDirectory;
            _boardRecoveryAttemptManifest = snapshot.ManifestSha256;
            _bootMaintenanceOriginalDirectory = boardDirectory;
            AppendLocalizedLog(M("Dynamic.RecoverySource.BoardReady", boardDirectory));
            return snapshot;
        }
        catch
        {
            snapshot?.Dispose();
            recovery.Dispose();
            throw;
        }
    }
}
