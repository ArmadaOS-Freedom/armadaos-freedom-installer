namespace FreedomInstaller.Core;

public static class OfflineUpdateRecordsDirectory
{
    public static string Resolve(string settingsRoot, string workRoot)
    {
        Directory.CreateDirectory(settingsRoot);
        string index = Path.Combine(settingsRoot, "offline-records-root.txt");
        if (!File.Exists(index))
        {
            string selected = Path.GetFullPath(Path.Combine(workRoot, "records", "offline-upgrades"));
            Directory.CreateDirectory(selected);
            try
            {
                using var stream = new FileStream(index, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(selected);
            }
            catch (IOException) when (File.Exists(index)) { }
        }
        string root = File.ReadAllText(index);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new IOException("The saved offline update records location is invalid.");
        // Never replace an unavailable records location with an empty session list on another drive.
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Offline update records are unavailable: {root}");
        return Path.GetFullPath(root);
    }
}
