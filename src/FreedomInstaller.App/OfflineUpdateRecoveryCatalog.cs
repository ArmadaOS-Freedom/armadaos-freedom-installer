using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FreedomInstaller.Core;

namespace FreedomInstaller.App
{
    /// <summary>A recovery bundle directory that could not be reused as this transaction's bundle.</summary>
    internal sealed record OfflineSkippedRecoveryBundle(string DirectoryPath, string Reason);

    internal sealed record OfflineRecoveryBundleScan(
        SavedOfflineUpdateRecovery? Recovery,
        IReadOnlyList<OfflineSkippedRecoveryBundle> Skipped);

    /// <summary>
    /// Looks for an already exported recovery bundle of this exact
    /// transaction, package and model.
    ///
    /// Each candidate directory is validated on its own. A leftover bundle
    /// from another transaction, another package or a damaged write must
    /// neither throw away a later valid bundle nor be deleted: it is reported
    /// with its reason and left byte-identical for inspection, exactly as the
    /// session records are. A cancelled token still cancels the whole scan.
    /// </summary>
    internal static class OfflineUpdateRecoveryCatalog
    {
        internal const string AcknowledgementFileName = "recovery-ack.json";

        internal static OfflineRecoveryBundleScan Scan(
            string sessionRoot,
            OfflineUpdateRecoveryContext expected,
            string packageDescriptorSha256,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var skipped = new List<OfflineSkippedRecoveryBundle>();
            string root;
            try
            {
                root = Path.GetFullPath(sessionRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                skipped.Add(new OfflineSkippedRecoveryBundle(sessionRoot, ex.Message));
                return new OfflineRecoveryBundleScan(null, skipped);
            }

            if (!Directory.Exists(root))
                return new OfflineRecoveryBundleScan(null, skipped);

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new OfflineSkippedRecoveryBundle(root, ex.Message));
                return new OfflineRecoveryBundleScan(null, skipped);
            }

            // Deterministic order keeps the reported candidates stable; at
            // most one directory can validate against this exact context.
            Array.Sort(directories, StringComparer.Ordinal);
            foreach (string directory in directories)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(Path.Combine(directory, AcknowledgementFileName)))
                    continue;

                try
                {
                    var loaded = PackageImporter.LoadOfflineUpdateRecovery(
                        directory, expected, packageDescriptorSha256, token);
                    return new OfflineRecoveryBundleScan(loaded, skipped);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new OfflineSkippedRecoveryBundle(directory, ex.Message));
                }
            }

            return new OfflineRecoveryBundleScan(null, skipped);
        }
    }
}
