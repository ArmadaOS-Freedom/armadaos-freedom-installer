using System.IO;
using FreedomInstaller.Core;

namespace FreedomInstaller.App
{
    /// <summary>A record file that could not be read as a session of the connected device.</summary>
    internal sealed record OfflineDamagedSessionRecord(string RecordName, string Reason);

    internal sealed record OfflineSessionRecordScan(
        IReadOnlyList<OfflineUpdateSession> Sessions,
        IReadOnlyList<OfflineDamagedSessionRecord> Damaged,
        string? UnavailableReason = null);

    /// <summary>
    /// Enumerates the device-bound session records one file at a time.
    ///
    /// The strict Core lookup refuses the whole set when a single record is
    /// damaged. The panel must not: one unrelated damaged file may neither hide
    /// the other saved sessions nor discard an already validated package. A
    /// damaged record is therefore reported with its name and reason and
    /// skipped. Nothing is rewritten or deleted here, so the damaged file stays
    /// byte-identical for inspection.
    ///
    /// A store that cannot be enumerated at all is reported separately from an
    /// empty one: an access-denied, non-directory or otherwise unreadable
    /// location is <see cref="OfflineSessionRecordScan.UnavailableReason"/>,
    /// never "no saved sessions". Only a genuinely missing store directory under
    /// an existing records root is an empty install.
    /// </summary>
    internal static class OfflineUpdateSessionCatalog
    {
        internal const int MaxRecordFileBytes = 64 * 1024;

        internal static OfflineSessionRecordScan Load(
            string recordsRoot,
            string? deviceModel,
            string? recoveryPartUuid,
            string? packageDescriptorSha256)
        {
            var sessions = new List<OfflineUpdateSession>();
            var damaged = new List<OfflineDamagedSessionRecord>();
            string sessionsDir = Path.Combine(recordsRoot, "offline-update-sessions");

            // Directory.Exists answers false both for a missing directory and
            // for an access-denied or non-directory path, so it cannot separate
            // an empty install from a store nobody can read. The enumeration is
            // attempted and its failure classified instead: a store entry that
            // was never created under a records root that still exists is an
            // empty install, everything else leaves the location unavailable.
            string[] files;
            try
            {
                files = Directory.GetFiles(sessionsDir, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return IsMissingStore(sessionsDir, recordsRoot)
                    ? new OfflineSessionRecordScan(sessions, damaged)
                    : new OfflineSessionRecordScan(sessions, damaged, ex.Message);
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (!name.EndsWith(".json", StringComparison.Ordinal) || name.Length <= 5)
                    continue;

                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || info.LinkTarget != null ||
                        (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    {
                        damaged.Add(new OfflineDamagedSessionRecord(name, "record is not a regular file"));
                        continue;
                    }

                    if (info.Length <= 0 || info.Length > MaxRecordFileBytes)
                    {
                        damaged.Add(new OfflineDamagedSessionRecord(name, $"record size out of range: {info.Length}"));
                        continue;
                    }

                    var record = PackageImporter.LoadOfflineUpdateSession(recordsRoot, name[..^5]);
                    // Only the sessions of the connected device (and, while a
                    // package is imported, of that exact package) are targets.
                    if (!string.Equals(record.DeviceModel, deviceModel, StringComparison.Ordinal) ||
                        !string.Equals(record.RecoveryPartUuid, recoveryPartUuid, StringComparison.Ordinal))
                        continue;
                    if (!string.IsNullOrEmpty(packageDescriptorSha256) &&
                        !string.Equals(record.PackageDescriptorSha256, packageDescriptorSha256, StringComparison.Ordinal))
                        continue;

                    sessions.Add(record);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    damaged.Add(new OfflineDamagedSessionRecord(name, ex.Message));
                }
            }

            sessions.Sort((a, b) => string.CompareOrdinal(a.SessionId, b.SessionId));
            return new OfflineSessionRecordScan(sessions, damaged);
        }

        /// <summary>
        /// Selects the selector entry after a readable rescan. A record that
        /// was selected before survives only when the refreshed store still
        /// holds the exact same value record: session id, device model,
        /// recovery part and package descriptor all matching. A vanished or
        /// changed record leaves the selector empty even when exactly one
        /// other record is present, because selecting it would silently
        /// retarget the operator's session. Without a prior selection the
        /// ordinary rule applies: one record is selected, several are not.
        /// </summary>
        internal static OfflineUpdateSession? SelectAfterRefresh(
            OfflineUpdateSession? previous,
            IReadOnlyList<OfflineUpdateSession> sessions)
        {
            if (previous != null)
            {
                foreach (OfflineUpdateSession candidate in sessions)
                {
                    // Record equality covers every binding field, and the
                    // refreshed instance is what the selector must hold.
                    if (candidate == previous)
                        return candidate;
                }

                return null;
            }

            return sessions.Count == 1 ? sessions[0] : null;
        }

        /// <summary>
        /// True only when the session store was never created: its entry does
        /// not exist while the records root that holds it still does. The entry
        /// is asked for through attributes, because
        /// <see cref="Directory.Exists"/> answers false for a denied or
        /// non-directory entry as well.
        /// </summary>
        private static bool IsMissingStore(string sessionsDir, string recordsRoot)
        {
            try
            {
                _ = File.GetAttributes(sessionsDir);
                // The entry exists: a file, a link or a directory that could not
                // be enumerated. All of those are unreadable, not empty.
                return false;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return IsExistingDirectory(recordsRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Whether the records root is still a directory, asked through
        /// attributes rather than <see cref="Directory.Exists"/> so a vanished
        /// or unreadable root is not mistaken for "the store was never made".
        /// </summary>
        private static bool IsExistingDirectory(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
