using System.Security;

namespace FreedomInstaller.Core;

public sealed record InstallerWorkDirectoryResult(string RootPath, long AvailableFreeBytes);

public sealed record InstallerWorkVolume(string RootPath, DriveType Type, bool IsReady, string FileSystem);

// Injectable platform description so the macOS discovery rules can be exercised on
// any test host without mounting volumes.
public sealed record InstallerWorkDirectoryPlatform(bool IsMacOS)
{
    public static InstallerWorkDirectoryPlatform Current { get; } = new(OperatingSystem.IsMacOS());
}

public static class InstallerWorkDirectory
{
    public const long MinimumWorkingReserveBytes = 1024L * 1024 * 1024;

    private static readonly HashSet<string> SupportedFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "NTFS", "ReFS", "exFAT", "ext4", "xfs", "btrfs", "apfs", "hfs"
    };

    // macOS mounts the system's own APFS volumes (Data, VM, Preboot, ...) here.
    // They share the boot container's free space with "/", whose user folders
    // are firmlinked to Data, so only "/" and external disks are candidates.
    private const string MacSystemVolumesRoot = "/System/Volumes";

    // macOS mounts external disks below /Volumes; every such root is "/Volumes/<name>".
    private const string MacVolumeMountsRoot = "/Volumes";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static InstallerWorkDirectoryResult Resolve(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return ResolveExplicit(explicitRoot.Trim(), InternalInstallPackageSnapshot.TryGetAvailableFreeBytes, ProbeDirectoryWritability);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var volumes = new List<InstallerWorkVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                bool ready = drive.IsReady;
                volumes.Add(new InstallerWorkVolume(drive.RootDirectory.FullName, drive.DriveType, ready, ready ? drive.DriveFormat : string.Empty));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
            }
        }

        return Resolve(explicitRoot, localAppData, volumes, InternalInstallPackageSnapshot.TryGetAvailableFreeBytes, ProbeDirectoryWritability);
    }

    public static InstallerWorkDirectoryResult Resolve(
        string? explicitRoot,
        string localAppData,
        IEnumerable<InstallerWorkVolume> volumes,
        Func<string, long?> readFree,
        Action<string> verifyWritable,
        InstallerWorkDirectoryPlatform? platform = null)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(readFree);
        ArgumentNullException.ThrowIfNull(verifyWritable);
        platform ??= InstallerWorkDirectoryPlatform.Current;

        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return ResolveExplicit(explicitRoot.Trim(), readFree, verifyWritable);

        var supportedVolumes = volumes
            .Where(v => v is not null && !string.IsNullOrWhiteSpace(v.RootPath) && v.IsReady && v.Type == DriveType.Fixed && SupportedFileSystems.Contains(v.FileSystem?.Trim() ?? string.Empty) && !IsMacSystemVolume(v.RootPath))
            .ToList();

        string? matchedLocalVolumeRoot = null;
        if (!string.IsNullOrWhiteSpace(localAppData) && Path.IsPathFullyQualified(localAppData))
        {
            string fullLocal = Path.GetFullPath(localAppData);
            matchedLocalVolumeRoot = supportedVolumes
                .Select(v => Path.GetFullPath(v.RootPath))
                .Where(root => IsSameOrDescendant(fullLocal, root))
                .OrderByDescending(root => root.Length)
                .FirstOrDefault();
        }

        bool IsMatchedLocalVolume(string rootPath) =>
            matchedLocalVolumeRoot is not null
            && string.Equals(Path.GetFullPath(rootPath), matchedLocalVolumeRoot, PathComparison);

        bool isMacOS = platform.IsMacOS;
        if (isMacOS)
        {
            // Only the volume holding LocalApplicationData and external disks mounted
            // directly below /Volumes are automatic candidates, so system containers
            // and unrelated host paths are never used.
            supportedVolumes = supportedVolumes
                .Where(v => IsNormalMacVolumeMount(v.RootPath) || IsMatchedLocalVolume(v.RootPath))
                .ToList();
        }

        var candidates = new List<(string WorkPath, long PreFree, bool IsLocal)>();
        var seenPaths = new HashSet<string>(PathComparer);

        foreach (var vol in supportedVolumes)
        {
            try
            {
                string volRoot = Path.GetFullPath(vol.RootPath);
                bool isLocalVol = IsMatchedLocalVolume(volRoot);
                string baseDir = isLocalVol ? Path.GetFullPath(localAppData) : volRoot;
                string workPath = Path.Combine(baseDir, "ArmadaOS Freedom", "Work");
                if (!seenPaths.Add(workPath))
                    continue;

                string? ancestor = FindExistingAncestor(workPath);
                if (ancestor is null)
                    continue;

                long? free = readFree(ancestor);
                if (free is >= MinimumWorkingReserveBytes)
                    candidates.Add((workPath, free.Value, isLocalVol));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
            {
            }
        }

        // macOS prefers the volume that actually holds LocalApplicationData, so the work
        // directory stays on the boot disk even when an external disk has more free space.
        // Every other platform keeps ordering by available free space.
        var ordered = candidates
            .OrderByDescending(c => isMacOS && c.IsLocal)
            .ThenByDescending(c => c.PreFree)
            .ThenBy(c => c.WorkPath, PathComparer)
            .ToList();

        Exception? lastError = null;
        foreach (var candidate in ordered)
        {
            try
            {
                verifyWritable(candidate.WorkPath);
                long? postFree = readFree(candidate.WorkPath);
                if (postFree is >= MinimumWorkingReserveBytes)
                    return new InstallerWorkDirectoryResult(candidate.WorkPath, postFree.Value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                lastError = ex;
            }
        }

        throw new IOException(
            $"No eligible writable fixed drive with at least {MinimumWorkingReserveBytes} bytes of available startup working reserve was found for the installer work directory.",
            lastError);
    }

    public static void ProbeDirectoryWritability(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Directory path must not be empty.", nameof(directoryPath));

        try
        {
            Directory.CreateDirectory(directoryPath);
            string probeFile = Path.Combine(directoryPath, $".freedom-work-probe-{Guid.NewGuid():N}.tmp");
            bool created = false;
            try
            {
                using var stream = new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
                created = true;
                stream.WriteByte(0x46);
                stream.Flush();
            }
            finally
            {
                if (created)
                {
                    try
                    {
                        File.Delete(probeFile);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                    {
                        throw new IOException($"Failed to clean up installer writability probe file '{probeFile}'.", ex);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            throw new IOException($"Installer work directory '{directoryPath}' is not writable.", ex);
        }
    }

    private static InstallerWorkDirectoryResult ResolveExplicit(string explicitPath, Func<string, long?> readFree, Action<string> verifyWritable)
    {
        if (!Path.IsPathFullyQualified(explicitPath))
            throw new IOException($"Explicit installer work directory '{explicitPath}' must be a fully qualified absolute path.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(explicitPath);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityException or NotSupportedException or PathTooLongException)
        {
            throw new IOException($"Explicit installer work directory '{explicitPath}' is invalid.", ex);
        }

        string? ancestor = FindExistingAncestor(fullPath)
            ?? throw new IOException($"Explicit installer work directory '{fullPath}' does not have an existing parent directory.");

        long? preFree = TryReadFreeOrThrow(readFree, ancestor, fullPath);
        if (preFree is null)
            throw new IOException($"Unable to determine available free space for explicit installer work directory '{fullPath}'.");
        if (preFree.Value < MinimumWorkingReserveBytes)
            throw new IOException($"Explicit installer work directory '{fullPath}' has {preFree.Value} bytes free; at least {MinimumWorkingReserveBytes} bytes are required for startup working reserve.");

        try
        {
            verifyWritable(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new IOException($"Explicit installer work directory '{fullPath}' is not writable.", ex);
        }

        long? postFree = TryReadFreeOrThrow(readFree, fullPath, fullPath);
        if (postFree is null)
            throw new IOException($"Unable to verify available free space after probing explicit installer work directory '{fullPath}'.");
        if (postFree.Value < MinimumWorkingReserveBytes)
            throw new IOException($"Explicit installer work directory '{fullPath}' has insufficient free space ({postFree.Value} bytes).");

        return new InstallerWorkDirectoryResult(fullPath, postFree.Value);
    }

    private static long? TryReadFreeOrThrow(Func<string, long?> readFree, string targetPath, string displayPath)
    {
        try
        {
            return readFree(targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new IOException($"Failed to query free space for explicit installer work directory '{displayPath}'.", ex);
        }
    }

    private static string? FindExistingAncestor(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    private static bool IsMacSystemVolume(string rootPath)
    {
        string root = rootPath.TrimEnd('/');
        return string.Equals(root, MacSystemVolumesRoot, StringComparison.Ordinal)
            || root.StartsWith(MacSystemVolumesRoot + "/", StringComparison.Ordinal);
    }

    private static bool IsNormalMacVolumeMount(string rootPath)
    {
        string root = rootPath.TrimEnd('/');
        if (!root.StartsWith(MacVolumeMountsRoot + "/", StringComparison.Ordinal))
            return false;

        string name = root[(MacVolumeMountsRoot.Length + 1)..];
        return name.Length > 0 && name != "." && name != ".." && name.IndexOf('/') < 0;
    }

    private static bool IsSameOrDescendant(string candidatePath, string rootPath)
    {
        string relative = Path.GetRelativePath(rootPath, candidatePath);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison));
    }
}
