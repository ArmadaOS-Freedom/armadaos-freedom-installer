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

    [Fact]
    public void InitialPublicationWritesTheCompleteSelectedPathWithoutLeavingATemporaryFile()
    {
        string task = NewTaskDirectory();
        try
        {
            string settings = Path.Combine(task, "settings");
            string work = Path.Combine(task, "drive-a");
            string resolved = OfflineUpdateRecordsDirectory.Resolve(settings, work);
            string expected = Path.GetFullPath(Path.Combine(work, "records", "offline-upgrades"));
            Assert.Equal(expected, resolved);
            Assert.Equal(expected, File.ReadAllText(Path.Combine(settings, "offline-records-root.txt")));
            Assert.Equal(new[] { "offline-records-root.txt" },
                Directory.GetFiles(settings, "offline-records-root.txt*").Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(task, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentCallersAllReturnTheSinglePublishedIndex()
    {
        string task = NewTaskDirectory();
        try
        {
            string settings = Path.Combine(task, "settings");
            string[] works = { Path.Combine(task, "drive-a"), Path.Combine(task, "drive-b") };
            using var gate = new ManualResetEventSlim(false);
            Task<string>[] calls = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                gate.Wait();
                return OfflineUpdateRecordsDirectory.Resolve(settings, works[i % works.Length]);
            })).ToArray();
            gate.Set();
            string[] resolved = await Task.WhenAll(calls);

            string published = File.ReadAllText(Path.Combine(settings, "offline-records-root.txt"));
            Assert.All(resolved, path => Assert.Equal(published, path));
            Assert.Contains(published, works.Select(w => Path.GetFullPath(Path.Combine(w, "records", "offline-upgrades"))));
            Assert.True(Directory.Exists(published));
            Assert.Equal(new[] { "offline-records-root.txt" },
                Directory.GetFiles(settings, "offline-records-root.txt*").Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(task, recursive: true); }
    }

    [Fact]
    public void ExistingInvalidIndexIsPreservedAndRejected()
    {
        string task = NewTaskDirectory();
        try
        {
            string settings = Path.Combine(task, "settings");
            Directory.CreateDirectory(settings);
            string index = Path.Combine(settings, "offline-records-root.txt");
            File.WriteAllText(index, "records/offline-upgrades");
            Assert.Throws<IOException>(() =>
                OfflineUpdateRecordsDirectory.Resolve(settings, Path.Combine(task, "drive-a")));
            Assert.Equal("records/offline-upgrades", File.ReadAllText(index));
            Assert.Equal(new[] { "offline-records-root.txt" },
                Directory.GetFiles(settings, "offline-records-root.txt*").Select(Path.GetFileName).ToArray());
            Assert.False(Directory.Exists(Path.Combine(task, "drive-a")));
        }
        finally { Directory.Delete(task, recursive: true); }
    }

    [Fact]
    public void PublishFailureRemovesItsTemporaryFileAndKeepsTheOccupiedPath()
    {
        string task = NewTaskDirectory();
        try
        {
            string settings = Path.Combine(task, "settings");
            string index = Path.Combine(settings, "offline-records-root.txt");
            Directory.CreateDirectory(index);
            Exception? failure = Record.Exception(() =>
                OfflineUpdateRecordsDirectory.Resolve(settings, Path.Combine(task, "drive-a")));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.True(Directory.Exists(index));
            Assert.Empty(Directory.GetFiles(settings));
        }
        finally { Directory.Delete(task, recursive: true); }
    }

    private static string NewTaskDirectory()
    {
        string task = Path.Combine(Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
            ?? Path.GetTempPath(), "records-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(task);
        return task;
    }
}
