using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace FreedomInstaller.App;

internal record AndroidDriverFiles(string DirectoryPath, string GoogleInfPath, string OplusInfPath);

internal static class BundledAndroidDriver
{
    private const string ResourceName = "FreedomInstaller.AndroidUsbDrivers.zip";
    private static readonly (string Path, long Bytes, string Sha256)[] Pins = {
        ("google/android_winusb.inf", 6957, "CD1EBE134E3ACE2B0C20B16CE62CA8369B1C80F497D6FA584F38DFBFC0A65ADE"),
        ("google/androidwinusba64.cat", 12825, "F099CDF89446B006ECDEC9B81B79A6BC1F5EF6FB014578DAA88D01FE5AADD90A"),
        ("google/amd64/WdfCoInstaller01009.dll", 1721576, "18487B4FF94EDCCC98ED59D9FCA662D4A1331C5F1E14DF8DB3093256DD9F1C3E"),
        ("google/amd64/winusbcoinstaller2.dll", 1002728, "981A17EFFDDBC20377512DDAEC9F22C2B7067E17A3E2A8CCF82BB7BB7B2420B6"),
        ("oplus/android_winusb.inf", 46886, "1A8F6E5D8D7CDE3A7E3E06F6696DA6963B59AAF55D96A70422EB0825DD6F30B9"),
        ("oplus/androidwinusba64.cat", 39509, "45AB641B65C3DE7B4C12840DB0E44FB3CC832C1743FD7416A5420B5A8C66036F"),
        ("oplus/amd64/WdfCoInstaller01009.dll", 1741728, "C0C372AA37DB208C2831DE6E2707A814F656A61994742240D5103F4EDA258F18"),
        ("oplus/amd64/WinUSBCoInstaller2.dll", 1022872, "43970391CC04CCB626F10B4BC8454AC55042E88498C8CF6F672253E1FA198AA9")
    };
    private static readonly string[] Directories = { "", "google", "google/amd64", "oplus", "oplus/amd64" };
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static AndroidDriverFiles EnsureAvailable()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The bundled Android USB driver archive is missing.");
        return ExtractArchive(stream, Path.Combine(BundledTools.GetCacheRoot(), "drivers"));
    }

    internal static AndroidDriverFiles ExtractArchive(Stream stream, string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var pin = Pins.FirstOrDefault(p => p.Path == entry.FullName);
            if (pin.Path is null || !entries.Add(entry.FullName) || entry.Length != pin.Bytes)
                throw new InvalidDataException("Android driver archive has missing, duplicate or unexpected entries.");
            using var input = entry.Open();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Android driver file hash differs: {pin.Path}.");
        }
        if (entries.Count != Pins.Length) throw new InvalidDataException("Android driver archive is incomplete.");

        var root = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(root);
        RejectLink(root);
        var target = Path.Combine(root, "android-usb-" + Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant());
        var files = Files(target);
        if (CacheValid(files)) return files;
        var staging = Path.Combine(root, ".staging-android-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var dir in Directories) Directory.CreateDirectory(Path.Combine(staging, dir));
            foreach (var pin in Pins)
            {
                using var input = zip.GetEntry(pin.Path)!.Open();
                using var output = new FileStream(Path.Combine(staging, pin.Path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            ValidateFiles(Files(staging));
            if (Directory.Exists(target))
            {
                RejectLink(target);
                // Validation rejected this hash-keyed cache. Remove only its expected regular files;
                // unexpected entries or links require manual repair, never recursive traversal.
                ValidateLayout(target, allowMissing: true);
                foreach (var pin in Pins)
                {
                    var path = Path.Combine(target, pin.Path);
                    if (File.Exists(path)) File.Delete(path);
                }
                foreach (var dir in Directories.Reverse())
                {
                    var path = Path.Combine(target, dir);
                    if (Directory.Exists(path)) Directory.Delete(path, false);
                }
            }
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        ValidateFiles(files);
        return files;
    }

    internal static void ValidateFiles(AndroidDriverFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.GetFullPath(files.DirectoryPath);
        var expected = Files(root);
        if (!PathComparer.Equals(Path.GetFullPath(files.GoogleInfPath), expected.GoogleInfPath)
            || !PathComparer.Equals(Path.GetFullPath(files.OplusInfPath), expected.OplusInfPath))
            throw new InvalidDataException("Android driver INF paths must belong to the verified bundle.");
        ValidateLayout(root);
        foreach (var pin in Pins)
        {
            var path = Path.Combine(root, pin.Path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != pin.Bytes
                || !string.Equals(Convert.ToHexString(SHA256.HashData(input)), pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Android driver cache file differs: {pin.Path}.");
        }
    }

    private static void ValidateLayout(string root, bool allowMissing = false)
    {
        foreach (var relative in Directories)
        {
            var dir = Path.Combine(root, relative);
            if (!Directory.Exists(dir))
            {
                if (allowMissing && !File.Exists(dir)) continue;
                throw new InvalidDataException("Android driver cache directory is missing.");
            }
            RejectLink(dir);
            var expected = new HashSet<string>(PathComparer);
            foreach (var name in Pins.Select(p => p.Path).Concat(Directories.Where(d => d.Length != 0)))
            {
                var parent = Path.GetDirectoryName(name)?.Replace('\\', '/') ?? "";
                if (parent == relative) expected.Add(Path.GetFileName(name));
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                RejectLink(path);
                if (!expected.Remove(Path.GetFileName(path)))
                    throw new InvalidDataException("Android driver cache contains unexpected entries.");
            }
            if (expected.Count != 0 && !allowMissing)
                throw new InvalidDataException("Android driver cache is incomplete.");
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Android driver cache must not contain links.");
    }

    private static bool CacheValid(AndroidDriverFiles files)
    {
        if (!Directory.Exists(files.DirectoryPath)) return false;
        try { ValidateFiles(files); return true; }
        catch (InvalidDataException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static AndroidDriverFiles Files(string root) =>
        new(root, Path.Combine(root, "google", "android_winusb.inf"), Path.Combine(root, "oplus", "android_winusb.inf"));
}
