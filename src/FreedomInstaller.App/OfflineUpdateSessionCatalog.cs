using System.IO;
using FreedomInstaller.Core;

namespace FreedomInstaller.App
{
    /// <summary>A record file that could not be read as a session of the connected device.</summary>
    internal sealed record OfflineDamagedSessionRecord(string RecordName, string Reason);

    internal sealed record OfflineSessionRecordScan(
        IReadOnlyList<OfflineUpdateSession> Sessions,
        IReadOnlyList<OfflineDamagedSessionRecord> Damaged);

    /// <summary>
    /// Enumerates the device-bound session records one file at a time.
    ///
    /// The strict Core lookup refuses the whole set when a single record is
    /// damaged. The panel must not: one unrelated damaged file may neither hide
    /// the other saved sessions nor discard an already validated package. A
    /// damaged record is therefore reported with its name and reason and
    /// skipped. Nothing is rewritten or deleted here, so the damaged file stays
    /// byte-identical for inspection.
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
            if (!Directory.Exists(sessionsDir))
                return new OfflineSessionRecordScan(sessions, damaged);

            string[] files;
            try
            {
                files = Directory.GetFiles(sessionsDir, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                damaged.Add(new OfflineDamagedSessionRecord(sessionsDir, ex.Message));
                return new OfflineSessionRecordScan(sessions, damaged);
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
    }
}
