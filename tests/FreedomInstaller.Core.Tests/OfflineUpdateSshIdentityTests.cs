#nullable enable
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateSshIdentityTests : IDisposable
{
    private const string Wire = "AAAAC3NzaC1lZDI1NTE5AAAAIAABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4f";
    private const string Canonical = "ssh-ed25519 " + Wire;
    private static readonly byte[] Placeholder = Encoding.ASCII.GetBytes("SYNTHETIC TEST PLACEHOLDER; NOT A CREDENTIAL; bounded fake private content.\n");
    private readonly string _directory;
    private readonly string _keygen;

    public OfflineUpdateSshIdentityTests()
    {
        string root = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT") ?? throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be set.");
        Assert.True(Path.IsPathFullyQualified(root));
        _directory = Path.Combine(root, $"identity {Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _keygen = Path.Combine(_directory, "fake ssh-keygen placeholder.exe");
        File.WriteAllText(_keygen, "validation placeholder only\n", new UTF8Encoding(false));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string NewDirectory() { string path = Path.Combine(_directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static OfflineUpdateSshKey ExpectedKey() => OfflineUpdateSshEnrollment.ParsePublicKey(Canonical);
    private static string Private(string dir) => Path.Combine(dir, "installer_ed25519");

    [Fact]
    public async Task GenerateThenReuse_UsesExactArgumentsAndPreservesPrivateBytes()
    {
        string dir = NewDirectory();
        var runner = new FakeRunner();
        OfflineUpdateSshIdentity first = await OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, _keygen);
        byte[] before = File.ReadAllBytes(first.IdentityFile);
        Assert.Equal(2, runner.Calls);
        Assert.Equal(ExpectedKey(), first.PublicKey);
        Assert.Equal(Canonical + " armada-offline-installer", File.ReadAllText(first.IdentityFile + ".pub").TrimEnd());
        OfflineUpdateSshIdentity second = await OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, _keygen);
        Assert.Equal(3, runner.Calls);
        Assert.Equal(before, File.ReadAllBytes(first.IdentityFile));
        Assert.Equal(first, second);
        if (OperatingSystem.IsWindows())
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var acl = new FileInfo(first.IdentityFile).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            var rules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier));
            Assert.Equal(1, rules.Count);
            Assert.Equal(identity.User, ((FileSystemAccessRule)rules[0]).IdentityReference);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, new DirectoryInfo(dir).UnixFileMode);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(first.IdentityFile));
        }
        Assert.All(runner.Commands, c => Assert.Equal(TimeSpan.FromSeconds(20), c.Timeout));
    }

    [Fact]
    public async Task NewIdentityDirectoryAndPrivateKeyBecomeOwnedByTheCurrentUser()
    {
        string dir = Path.Combine(_directory, "new-identity");
        var result = await OfflineUpdateSshIdentityStore.EnsureAsync(dir, new FakeRunner(), _keygen);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            Assert.Equal(identity.User, new DirectoryInfo(dir).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
            Assert.Equal(identity.User, new FileInfo(result.IdentityFile).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        }
        Assert.Equal(result, await OfflineUpdateSshIdentityStore.EnsureAsync(dir, new FakeRunner(), _keygen));
    }

    [Fact]
    public async Task DerivedOutputIsAuthoritative_AndMalformedOutputPreservesPrivate()
    {
        string dir = NewDirectory();
        File.WriteAllBytes(Private(dir), Placeholder);
        File.WriteAllText(Private(dir) + ".pub", "stale unauthorized public data\n");
        var runner = new FakeRunner { Comment = "authoritative comment" };
        OfflineUpdateSshIdentity result = await OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, _keygen);
        Assert.Equal(ExpectedKey(), result.PublicKey);
        Assert.Equal("stale unauthorized public data\n", File.ReadAllText(Private(dir) + ".pub"));
        runner.Output = Canonical + " comment\r\nsecond line\r\n";
        await Assert.ThrowsAnyAsync<IOException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, _keygen));
        Assert.Equal(Placeholder, File.ReadAllBytes(Private(dir)));
    }

    [Fact]
    public async Task OrphanPublicAndInvalidPrivate_StopBeforeDeriveOrOverwrite()
    {
        string orphan = NewDirectory();
        File.WriteAllText(Private(orphan) + ".pub", "orphan");
        var runner = new FakeRunner();
        await Assert.ThrowsAnyAsync<IOException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(orphan, runner, _keygen));
        Assert.Equal(0, runner.Calls);
        Assert.Equal("orphan", File.ReadAllText(Private(orphan) + ".pub"));
        foreach (byte[] content in new[] { Array.Empty<byte>(), new byte[65537] })
        {
            string dir = NewDirectory();
            File.WriteAllBytes(Private(dir), content);
            await Assert.ThrowsAnyAsync<IOException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, _keygen));
        }
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task DirectoryFileCollisions_RefuseBeforeRun()
    {
        var runner = new FakeRunner();
        foreach (string name in new[] { "installer_ed25519", "installer_ed25519.pub", ".identity.lock" })
        {
            string dir = NewDirectory();
            Directory.CreateDirectory(Path.Combine(dir, name));
            await Assert.ThrowsAnyAsync<IOException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, _keygen));
        }
        Assert.Equal(0, runner.Calls);
    }

    [LinuxFact]
    public async Task LinksIncludingDanglingDirectory_RefuseBeforeRun()
    {
        var runner = new FakeRunner();
        string target = NewDirectory();
        const string payload = "harmless target payload\n";
        File.WriteAllText(Path.Combine(target, "target"), payload);
        string danglingDir = Path.Combine(_directory, "dangling-dir");
        Directory.CreateSymbolicLink(danglingDir, Path.Combine(_directory, "missing"));
        await Assert.ThrowsAnyAsync<IOException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(danglingDir, runner, _keygen));
        foreach (string name in new[] { "installer_ed25519", "installer_ed25519.pub", ".identity.lock" })
        {
            foreach (bool dangling in new[] { false, true })
            {
                string dir = NewDirectory();
                File.CreateSymbolicLink(Path.Combine(dir, name), dangling ? Path.Combine(dir, "missing") : Path.Combine(target, "target"));
                await Assert.ThrowsAnyAsync<IOException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, _keygen));
            }
        }
        string linked = Path.Combine(_directory, "linked-dir");
        Directory.CreateSymbolicLink(linked, target);
        await Assert.ThrowsAnyAsync<IOException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(linked, runner, _keygen));
        Assert.Equal(0, runner.Calls);
        Assert.Equal(payload, File.ReadAllText(Path.Combine(target, "target")));
    }

    [Fact]
    public async Task ValidationCancellationTimeoutAndLock_ContainSideEffects()
    {
        var runner = new FakeRunner();
        await Assert.ThrowsAsync<ArgumentException>(() => OfflineUpdateSshIdentityStore.EnsureAsync("relative", runner, _keygen));
        await Assert.ThrowsAsync<ArgumentException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(NewDirectory(), runner, "relative-keygen"));
        await Assert.ThrowsAsync<ArgumentException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(Path.Combine(_directory, "bad%path"), runner, _keygen));
        Assert.Equal(0, runner.Calls);
        string cancelledDir = Path.Combine(_directory, "not-created");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(cancelledDir, runner, _keygen, cts.Token));
        }
        Assert.False(Directory.Exists(cancelledDir));
        string dir = NewDirectory();
        using (var cts = new CancellationTokenSource())
        {
            runner.AfterGenerate = cts.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, _keygen, cts.Token));
        }
        Assert.Equal(1, runner.Calls);
        Assert.True(File.Exists(Private(dir)));
        runner.AfterGenerate = null;
        await OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, _keygen);
        Assert.Equal(2, runner.Calls);
        string timeoutDir = NewDirectory();
        runner.GenerateException = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(timeoutDir, runner, _keygen));
        Assert.Equal(3, runner.Calls);
        Assert.True(File.Exists(Private(timeoutDir)));
        runner.GenerateException = null;
        string locked = NewDirectory();
        using (new FileStream(Path.Combine(locked, ".identity.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => OfflineUpdateSshIdentityStore.EnsureAsync(locked, runner, _keygen));
        Assert.Equal(3, runner.Calls);
    }

    [Fact]
    public async Task Reuse_RepairsPlatformPermissions()
    {
        string dir = NewDirectory();
        File.WriteAllBytes(Private(dir), Placeholder);
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileInfo(Private(dir)).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(Private(dir)).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(Private(dir), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        await OfflineUpdateSshIdentityStore.EnsureAsync(dir, new FakeRunner(), _keygen);
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileInfo(Private(dir)).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            Assert.Equal(1, acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Count);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Private(dir)));
    }

    [Fact]
    public async Task NativeOpenSshRoundTrip()
    {
        string exe = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe") : "/usr/bin/ssh-keygen";
        Assert.True(File.Exists(exe), $"Required ssh-keygen missing: {exe}");
        string dir = NewDirectory();
        var runner = new ProcessRunner();
        OfflineUpdateSshIdentity first = await OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, exe);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(first.IdentityFile));
        OfflineUpdateSshIdentity second = await OfflineUpdateSshIdentityStore.EnsureAsync(dir, runner, exe);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(first.IdentityFile)));
        Assert.Equal(first, second);
        Assert.StartsWith("ssh-ed25519 ", second.PublicKey.PublicKey, StringComparison.Ordinal);
        ProcessResult fingerprint = await runner.RunAsync(new ProcessCommand(exe, new[] { "-lf", first.IdentityFile + ".pub", "-E", "sha256" }, TimeSpan.FromSeconds(20)), CancellationToken.None);
        Assert.Equal(0, fingerprint.ExitCode);
        string actual = fingerprint.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Equal(second.PublicKey.Fingerprint, actual);
    }

    private sealed class FakeRunner : ICommandRunner
    {
        public int Calls { get; private set; }
        public string? Comment { get; set; } = "generated comment";
        public string? Output { get; set; }
        public Action? AfterGenerate { get; set; }
        public Exception? GenerateException { get; set; }
        public List<ProcessCommand> Commands { get; } = new();
        public Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            Commands.Add(command);
            if (command.Arguments[0] == "-y")
            {
                Assert.Equal(new[] { "-y", "-P", "", "-f", command.Arguments[4] }, command.Arguments);
                return Task.FromResult(new ProcessResult(0, Output ?? Canonical + " " + Comment + "\r\n", ""));
            }
            string path = command.Arguments[^1];
            Assert.Equal(new[] { "-q", "-t", "ed25519", "-N", "", "-C", "armada-offline-installer", "-f", path }, command.Arguments);
            File.WriteAllBytes(path, Placeholder);
            File.WriteAllText(path + ".pub", Canonical + " armada-offline-installer\r\n");
            AfterGenerate?.Invoke();
            if (GenerateException is not null) throw GenerateException;
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}
