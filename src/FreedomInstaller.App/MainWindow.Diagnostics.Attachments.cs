using System;
using System.Collections.Generic;
using System.Threading;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

public partial class MainWindow
{
    private sealed record DiagnosticBootEvidence(
        InternalBackupSet Backup,
        ImportedPackage Package,
        InternalArtifactSet Artifacts);

    private DiagnosticBootEvidence? _diagnosticBootEvidence;
    private bool? _latestFailureAllowsAttachments;

    // First-install backup failures publish a per-attempt snapshot of completed ABL
    // captures taken directly from the capture callback, independent of
    // _internalInstallBackup and of package/artifact eligibility.
    private IReadOnlyList<DiagnosticBootFile>? _diagnosticAttemptBootEvidence;

    private void PublishDiagnosticBootEvidence(
        InternalBackupSet backup,
        ImportedPackage package,
        InternalArtifactSet artifacts)
    {
        var evidence = new DiagnosticBootEvidence(backup, package, artifacts);
        Volatile.Write(ref _diagnosticBootEvidence, evidence);
    }

    private void ResetDiagnosticBootEvidence()
    {
        Volatile.Write(ref _diagnosticBootEvidence, null);
        _latestFailureAllowsAttachments = null;
    }

    private bool HasDiagnosticBootEvidence()
    {
        if (!IsInternalInstallMode)
        {
            return false;
        }

        var evidence = Volatile.Read(ref _diagnosticBootEvidence);
        return evidence is not null
            && ReferenceEquals(evidence.Backup, _internalInstallBackup)
            && ReferenceEquals(evidence.Package, _internalInstallPackage)
            && ReferenceEquals(evidence.Artifacts, _internalInstallArtifacts);
    }

    private void NoteDiagnosticAttachmentFailure(UiMessage stage)
    {
        var allows = HasDiagnosticBootEvidence()
            && (string.Equals(stage.Key, "Dynamic.Diagnostics.Stage.Prepare", StringComparison.Ordinal)
                || string.Equals(stage.Key, "Dynamic.Diagnostics.Stage.Exit", StringComparison.Ordinal)
                || string.Equals(stage.Key, "Dynamic.Diagnostics.Stage.Execute", StringComparison.Ordinal));
        _latestFailureAllowsAttachments = allows;
    }

    private IReadOnlyList<DiagnosticBootFile> GetDiagnosticPageBootFiles()
    {
        var allows = _latestFailureAllowsAttachments;
        if (HasDiagnosticBootEvidence() && allows is not false)
        {
            return CollectBootFiles();
        }

        // 失败备份的尝试快照不依赖备份对象（返回 Android 后已被释放），
        // 也不依赖附件资格：这里只要求内部安装模式与已发布的非空快照。
        if (IsInternalInstallMode
            && _diagnosticAttemptBootEvidence is { Count: > 0 } attemptEvidence)
        {
            return attemptEvidence;
        }

        return Array.Empty<DiagnosticBootFile>();
    }
}
