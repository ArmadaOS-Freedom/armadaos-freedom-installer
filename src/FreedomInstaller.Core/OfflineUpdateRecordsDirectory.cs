namespace FreedomInstaller.Core;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

public static class OfflineUpdateRecordsDirectory
{
    private const string IndexName = "offline-records-root.txt";
    private const string MutexPrefix = "FreedomInstaller.OfflineUpdateRecordsDirectory.";
    private static readonly TimeSpan PublicationTimeout = TimeSpan.FromSeconds(30);

    public static string Resolve(string settingsRoot, string workRoot)
    {
        Directory.CreateDirectory(settingsRoot);
        string index = Path.Combine(settingsRoot, IndexName);
        if (!File.Exists(index))
        {
            string selected = Path.GetFullPath(Path.Combine(workRoot, "records", "offline-upgrades"));
            Directory.CreateDirectory(selected);
            Publish(settingsRoot, index, selected);
        }
        string root = File.ReadAllText(index);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new IOException("The saved offline update records location is invalid.");
        // Never replace an unavailable records location with an empty session list on another drive.
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Offline update records are unavailable: {root}");
        return Path.GetFullPath(root);
    }

    // The index is only ever published by renaming a fully written, closed temporary file onto its
    // final name, so a reader never observes a partial index and no caller renames over an existing
    // index. Rename alone is not enough to pin one root: two callers that both see a missing index
    // would each rename a candidate of their own, and the loser would hand back a root that the
    // index no longer names. Publication therefore runs under a named mutex that every caller of
    // one settings root shares across processes, and the winner's index is re-read after the race.
    private static void Publish(string settingsRoot, string index, string selected)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName(settingsRoot));
        bool owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(PublicationTimeout);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died while publishing. The mutex is ours now and the index is
                // either absent or complete, because it is only ever replaced by an atomic rename.
                owned = true;
            }
            // Another caller published first, either while we waited or before we got the mutex;
            // its index stays pinned and is what Resolve returns.
            if (File.Exists(index))
                return;
            if (!owned)
                throw new IOException("Timed out waiting to publish the offline update records location.");

            string temporary = Path.Combine(settingsRoot, IndexName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                    writer.Write(selected);
                try
                {
                    File.Move(temporary, index, overwrite: false);
                }
                catch (IOException) when (File.Exists(index))
                {
                    // A publisher outside this protocol won the race; keep its index.
                }
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    // Named mutexes are machine-wide on Unix and session-wide on Windows, so the name carries the
    // current user: publication stays inside one user's security context instead of depending on
    // access to a kernel object created by another account.
    private static string MutexName(string settingsRoot)
    {
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settingsRoot));
        // Windows paths are case-insensitive, so every spelling of one settings root must map to a
        // single mutex name; Unix paths are not, so there the spelling stays part of the identity.
        if (OperatingSystem.IsWindows())
            canonical = canonical.ToUpperInvariant();
        string key = CurrentUserScope() + "|" + canonical + "|" + IndexName;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return MutexPrefix + Convert.ToHexString(digest.AsSpan(0, 16));
    }

    private static string CurrentUserScope()
    {
        if (OperatingSystem.IsWindows())
            return WindowsUserScope();
        return "unix:" + Environment.UserName;
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsUserScope()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return "win:" + (identity.User?.Value ?? Environment.UserName);
    }
}
