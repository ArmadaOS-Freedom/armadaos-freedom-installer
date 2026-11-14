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

        BoardRecoveryExtraction? extraction = null;
        string? boardDirectory = _boardRecoveryDiscoveredDirectory;
        if (boardDirectory is null)
        {
            SetInternalInstallBackupStatus(M("Dynamic.RecoverySource.ReadingBoard"));
            AppendLocalizedLog(M("Dynamic.RecoverySource.ReadingBoard"));
            extraction = await BoardRecoveryBackup.ReadDirectoryAsync(
                client, parent, TemporaryWorkRoot, token);
            boardDirectory = extraction?.DirectoryPath;
        }

        if (boardDirectory is null)
        {
            AppendLocalizedLog(M("Dynamic.BootMaintenance.Repair.BoardMissingCapture"));
            return await InternalInstallBackup.CreateAsync(client, parent, progress, token);
        }

        InternalBackupSet? recovery = null;
        InternalBackupSet? snapshot = null;
        try
        {
            recovery = await InternalInstallBackup.OpenRecoveryAsync(client, boardDirectory, token: token);

            // Historical recovery is only original-epoch evidence, not current rollback state.
            snapshot = await InternalInstallBackup.CreateAsync(client, parent, progress, token);

            BoardRecoveryBackup.ValidateSourceForReuse(snapshot, recovery);
        }
        catch
        {
            snapshot?.Dispose();
            recovery?.Dispose();
            extraction?.Dispose();
            throw;
        }

        _internalInstallRecoverySource?.Dispose();
        _internalInstallRecoverySource = recovery;
        _boardRecoveryDiscoveredDirectory = boardDirectory;
        _boardRecoveryAttemptManifest = snapshot!.ManifestSha256;
        _bootMaintenanceOriginalDirectory = boardDirectory;
        extraction?.Retain();
        AppendLocalizedLog(M("Dynamic.RecoverySource.BoardReady", boardDirectory));
        return snapshot!;
    }
}
