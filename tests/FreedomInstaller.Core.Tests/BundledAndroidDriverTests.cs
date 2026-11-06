using System.IO.Compression;
using FreedomInstaller.App;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class BundledAndroidDriverTests
{
    private static byte[] Archive() => File.ReadAllBytes(
        Environment.GetEnvironmentVariable("ANDROID_DRIVER_TEST_ARCHIVE")
        ?? throw new InvalidOperationException("ANDROID_DRIVER_TEST_ARCHIVE must provide the real build archive."));

    [Fact]
    public void SignedFilesExtractAndTamperedCacheIsRepaired()
    {
        using var scratch = new TempPackageDirectory();
        var bytes = Archive();
        var files = BundledAndroidDriver.ExtractArchive(new MemoryStream(bytes), scratch.Path);
        BundledAndroidDriver.ValidateFiles(files);
        Assert.Equal(8, Directory.GetFiles(files.DirectoryPath, "*", SearchOption.AllDirectories).Length);
        var original = File.ReadAllBytes(files.OplusInfPath);
        File.WriteAllText(files.OplusInfPath, "modified INF");
        Assert.Throws<InvalidDataException>(() => BundledAndroidDriver.ValidateFiles(files));
        var repaired = BundledAndroidDriver.ExtractArchive(new MemoryStream(bytes), scratch.Path);
        Assert.Equal(original, File.ReadAllBytes(repaired.OplusInfPath));
        Assert.Single(Directory.GetDirectories(scratch.Path));
        Assert.Throws<InvalidDataException>(() =>
            BundledAndroidDriver.ValidateFiles(repaired with { GoogleInfPath = Path.Combine(scratch.Path, "untrusted.inf") }));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    [InlineData("corrupt")]
    public void InvalidArchivesAreRejectedBeforePublishingCache(string change)
    {
        using var scratch = new TempPackageDirectory();
        using var original = new ZipArchive(new MemoryStream(Archive()));
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var item in original.Entries)
            {
                if (change == "missing" && item.FullName == "google/androidwinusba64.cat") continue;
                using var input = item.Open();
                using var data = new MemoryStream();
                input.CopyTo(data);
                var bytes = data.ToArray();
                if (change == "corrupt" && item.FullName == "google/android_winusb.inf") bytes[0] ^= 1;
                using var output = zip.CreateEntry(item.FullName).Open();
                output.Write(bytes);
            }
            if (change is "duplicate" or "traversal")
            {
                using var output = zip.CreateEntry(change == "duplicate" ? "google/android_winusb.inf" : "../outside.inf").Open();
                output.WriteByte(1);
            }
        }
        buffer.Position = 0;
        Assert.Throws<InvalidDataException>(() => BundledAndroidDriver.ExtractArchive(buffer, scratch.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FirstPackageCancellationRebootOrFailureStopsSequence(int outcome)
    {
        var files = new AndroidDriverFiles("bundle", "google.inf", "oplus.inf");
        var calls = new List<string>();
        var result = await WindowsDriverInstaller.InstallAndroidSequenceAsync(files, inf =>
        {
            calls.Add(inf);
            return Task.FromResult(new DriverInstallResult((DriverInstallOutcome)outcome, null, null));
        });
        Assert.Equal((DriverInstallOutcome)outcome, result.Outcome);
        Assert.Equal(["google.inf"], calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SecondPackageResultIsPreserved(int outcome)
    {
        var calls = new List<string>();
        var result = await WindowsDriverInstaller.InstallAndroidSequenceAsync(
            new AndroidDriverFiles("bundle", "google.inf", "oplus.inf"), inf =>
            {
                calls.Add(inf);
                return Task.FromResult(new DriverInstallResult(
                    calls.Count == 1 ? DriverInstallOutcome.Completed : (DriverInstallOutcome)outcome, null, null));
            });
        Assert.Equal(["google.inf", "oplus.inf"], calls);
        Assert.Equal((DriverInstallOutcome)outcome, result.Outcome);
    }
}
