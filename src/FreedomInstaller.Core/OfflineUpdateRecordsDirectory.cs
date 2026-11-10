namespace FreedomInstaller.Core;

public static class OfflineUpdateRecordsDirectory
{
    private const string IndexName = "offline-records-root.txt";

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
    // final name, so a reader never observes a partial index and the first published root stays pinned.
    private static void Publish(string settingsRoot, string index, string selected)
    {
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
                // Another caller published first; its index wins.
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
