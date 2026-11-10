using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateRecordsDirectoryTests
{
    [Fact]
    public void ChangingWorkDriveKeepsExistingRecordsAndMissingDriveDoesNotResetThem()
    {
        string task = Path.Combine(Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
            ?? Path.GetTempPath(), "records-" + Guid.NewGuid().ToString("N"));
        try
        {
            string settings = Path.Combine(task, "settings");
            string first = OfflineUpdateRecordsDirectory.Resolve(settings, Path.Combine(task, "drive-a"));
            File.WriteAllText(Path.Combine(first, "receipt"), "saved");
            string second = OfflineUpdateRecordsDirectory.Resolve(settings, Path.Combine(task, "drive-b"));
            Assert.Equal(first, second);
            Assert.Equal("saved", File.ReadAllText(Path.Combine(second, "receipt")));
            Directory.Move(first, first + "-unmounted");
            Assert.Throws<DirectoryNotFoundException>(() =>
                OfflineUpdateRecordsDirectory.Resolve(settings, Path.Combine(task, "drive-b")));
            Assert.Equal(first, File.ReadAllText(Path.Combine(settings, "offline-records-root.txt")));
            Assert.False(Directory.Exists(Path.Combine(task, "drive-b")));
        }
        finally { Directory.Delete(task, recursive: true); }
    }
}
