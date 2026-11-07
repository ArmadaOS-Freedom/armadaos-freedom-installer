using System;
using System.IO;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class InstallerWorkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupersededPreparationReleasesImagesButKeepsRecordsAndCurrentAttempt(bool started)
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")!, Guid.NewGuid().ToString("N"));
        var session = new InstallerWorkSession(root);
        string old = Path.Combine(session.DirectoryPath, "old"), current = Path.Combine(session.DirectoryPath, "current");
        Directory.CreateDirectory(old); Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(old, "board.img"), "large generated archive");
        File.WriteAllText(Path.Combine(old, "plan.json"), "receipt");
        File.WriteAllText(Path.Combine(current, "board.img"), "current candidate");
        session.DiscardPreparation(old, started);
        Assert.Equal(started, Directory.Exists(old));
        Assert.True(File.Exists(Path.Combine(current, "board.img")));
        if (!started) Assert.Equal("receipt", File.ReadAllText(Path.Combine(session.RecordsDirectory, "old", "plan.json")));
        Assert.Throws<IOException>(() => session.DiscardPreparation(root, false));
        Assert.Throws<IOException>(() => session.DiscardPreparation(session.DirectoryPath, false));
        session.Complete(false);
    }

    [Fact]
    public void PreparationCleanupRejectsLinkedAncestors()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")!, Guid.NewGuid().ToString("N"));
        var session = new InstallerWorkSession(root);
        string outside = Path.Combine(root, "backup"), child = Path.Combine(outside, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "boot.bin"), "original");
        string link = Path.Combine(session.DirectoryPath, "link");
        Directory.CreateSymbolicLink(link, outside);
        Assert.Throws<IOException>(() => session.DiscardPreparation(Path.Combine(link, "child"), false));
        Assert.True(File.Exists(Path.Combine(child, "boot.bin")));
        session.Complete(false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseCleansOnlyOwnedWorkAndKeepsReports(bool preserveRecovery)
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string backup = Path.Combine(root, "user-backup");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "boot_a.bin"), "original-backup");
        var first = new InstallerWorkSession(root);
        var other = new InstallerWorkSession(root);
        File.WriteAllText(Path.Combine(other.DirectoryPath, "busy.bin"), "other-session");
        string child = Path.Combine(first.DirectoryPath, "preparation");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "recovery.img"), "temporary-image");
        File.WriteAllText(Path.Combine(child, "verification.json"), "verified receipt");
        first.Complete(preserveRecovery);
        Assert.Equal(preserveRecovery, Directory.Exists(first.DirectoryPath));
        Assert.Equal("original-backup", File.ReadAllText(Path.Combine(backup, "boot_a.bin")));
        Assert.True(File.Exists(Path.Combine(other.DirectoryPath, "busy.bin")));
        if (!preserveRecovery)
        {
            Assert.Equal("verified receipt", File.ReadAllText(Path.Combine(first.RecordsDirectory, "preparation", "verification.json")));
            Assert.False(File.Exists(Path.Combine(first.RecordsDirectory, "preparation", "recovery.img")));
            first.Complete(false);
        }
        other.Complete(false);
    }
    [Fact]
    public void CleanupDoesNotFollowDirectoryLinks()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")!, Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(root, "selected-backup");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "boot.bin"), "keep");
        File.WriteAllText(Path.Combine(backup, "manifest.json"), "keep");
        var work = new InstallerWorkSession(root);
        Directory.CreateSymbolicLink(Path.Combine(work.DirectoryPath, "external"), backup);
        work.Complete(false);
        Assert.False(Directory.Exists(work.DirectoryPath));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(backup, "boot.bin")));
        Assert.False(Directory.Exists(Path.Combine(work.RecordsDirectory, "external")));
    }

}
