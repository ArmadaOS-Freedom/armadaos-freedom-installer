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

        return Array.Empty<DiagnosticBootFile>();
    }
}
