using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateSshEnrollmentTests : IDisposable
{
    private const string Id1 = "11111111-1111-1111-1111-111111111111";
    private const string Id2 = "22222222-2222-2222-2222-2222222222aa";

    private readonly string _testDir;
    private readonly string _powerShell;
    private readonly string _keyScan;

    public OfflineUpdateSshEnrollmentTests()
    {
        var artifactRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
            ?? throw new InvalidOperationException("TEST_ARTIFACT_ROOT required"));
        _testDir = Path.Combine(artifactRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        _powerShell = Path.Combine(_testDir, "fake-powershell.exe");
        File.WriteAllText(_powerShell, "placeholder; never executed by tests");

        _keyScan = Path.Combine(_testDir, "fake-ssh-keyscan.exe");
        File.WriteAllText(_keyScan, "placeholder; never executed by tests");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    // ---- synthetic key helpers (stdlib only, no generation) ----

    private static byte[] MakeWire(byte fill = 0xAA)
    {
        var wire = new byte[51];
        wire[0] = 0; wire[1] = 0; wire[2] = 0; wire[3] = 11;
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(wire, 4);
        wire[15] = 0; wire[16] = 0; wire[17] = 0; wire[18] = 32;
        for (var i = 19; i < wire.Length; i++)
            wire[i] = (byte)(fill + i);
        return wire;
    }

    private static string CanonicalKey(byte fill = 0xAA) =>
        "ssh-ed25519 " + Convert.ToBase64String(MakeWire(fill));

    private static string IndependentFingerprint(byte fill = 0xAA) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(MakeWire(fill))).TrimEnd('=');

    private static string ScanLine(string host = "172.16.42.1", string type = "ssh-ed25519", string? key = null) =>
        $"{host} {type} {key ?? Convert.ToBase64String(MakeWire())}";

    private static string Envelope(params object?[] rows) =>
        JsonSerializer.Serialize(new { schemaVersion = 1, adapters = rows });

    private static object Row(string id, string pnp, string address = "172.16.42.2", string mask = "255.255.255.0") =>
        new { id, pnpDeviceId = pnp, address, mask };

    private static string OneEndpoint() =>
        Envelope(Row(Id1, @"USB\VID_1D6B&PID_0104\instance"));

    // ---- ParsePublicKey ----

    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    public void ParsePublicKey_AcceptsSpaceOrTabSeparatorAndMatchesIndependentFingerprint(char sep)
    {
        var key = OfflineUpdateSshEnrollment.ParsePublicKey(CanonicalKey().Replace(' ', sep));
        Assert.Equal(CanonicalKey(), key.PublicKey);
        Assert.StartsWith("SHA256:", key.Fingerprint, StringComparison.Ordinal);
        Assert.Equal(IndependentFingerprint(), key.Fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExtra field")] // 3 fields
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAC3NzaC1lZDI1NTE5")]
    [InlineData("ssh-ed25519 AAAAC3NzaélZDI1NTE5")] // non-ASCII
    [InlineData("ssh-ed25519\nAAAAC3NzaC1lZDI1NTE5AAAAI...")]
    [InlineData("ssh-ed25519\0AAAAC3NzaC1lZDI1NTE5")]
    [InlineData("ssh-ed25519 not!base64")]
    [InlineData("ssh-ed25519 ")]
    [InlineData(" ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFoo")] // leading empty field
    public void ParsePublicKey_RejectsMalformedInput(string value)
    {
        Assert.Throws<InvalidDataException>(() => OfflineUpdateSshEnrollment.ParsePublicKey(value));
    }

    [Fact]
    public void ParsePublicKey_RejectsOversizedAndMalformedWire()
    {
        Assert.Throws<InvalidDataException>(() =>
            OfflineUpdateSshEnrollment.ParsePublicKey("ssh-ed25519 " + new string('A', 5000)));

        // Wrong wire length (50 bytes).
        var shortWire = new byte[50];
        Assert.Throws<InvalidDataException>(() =>
            OfflineUpdateSshEnrollment.ParsePublicKey("ssh-ed25519 " + Convert.ToBase64String(shortWire)));

        // Declared key type on the wire doesn't match.
        var wrongType = MakeWire();
        Encoding.ASCII.GetBytes("ssh-rsa++++").CopyTo(wrongType, 4);
        Assert.Throws<InvalidDataException>(() =>
            OfflineUpdateSshEnrollment.ParsePublicKey("ssh-ed25519 " + Convert.ToBase64String(wrongType)));

        // Non-canonical base64 (embedded whitespace round-trips differently).
        var canonical = Convert.ToBase64String(MakeWire());
        var padded = canonical + " ";
        Assert.Throws<InvalidDataException>(() =>
            OfflineUpdateSshEnrollment.ParsePublicKey("ssh-ed25519 " + padded));

        // Wire with wrong type-length prefix (length != 11).
        var badTypeLen = MakeWire();
        badTypeLen[3] = 12;
        Assert.Throws<InvalidDataException>(() =>
            OfflineUpdateSshEnrollment.ParsePublicKey("ssh-ed25519 " + Convert.ToBase64String(badTypeLen)));

        // Wire with wrong key-length prefix (tail length != 32).
        var badKeyLen = MakeWire();
        badKeyLen[18] = 31;
        Assert.Throws<InvalidDataException>(() =>
            OfflineUpdateSshEnrollment.ParsePublicKey("ssh-ed25519 " + Convert.ToBase64String(badKeyLen)));
    }

    [Fact]
    public void ParsePublicKey_NullThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => OfflineUpdateSshEnrollment.ParsePublicKey(null!));
    }

    // ---- BuildAuthorizationCommand ----

    [Fact]
    public void BuildAuthorizationCommand_IsExactForSyntheticKey()
    {
        Assert.Equal(
            "/usr/bin/python3 -I /usr/libexec/armada/armada-authorize-installer '" + CanonicalKey() + "'",
            OfflineUpdateSshEnrollment.BuildAuthorizationCommand(CanonicalKey()));
    }

    [Theory]
    [InlineData("ssh-ed25519,root@host AAAAC3NzaC1lZDI1NTE5")] // key options field
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5' && rm -rf /")] // quote injection
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5\nroot@host x y")] // line injection
    [InlineData("")]
    public void BuildAuthorizationCommand_RejectsAnythingNotCanonicalKey(string input)
    {
        Assert.Throws<InvalidDataException>(() => OfflineUpdateSshEnrollment.BuildAuthorizationCommand(input));
    }

    // ---- ParseHostKeyScanOutput ----

    [Fact]
    public void ParseHostKeyScanOutput_AcceptsDuplicatesCommentsAndCrlf()
    {
        var output =
            "# 172.16.42.1 SSH-2.0-OpenSSH_9.6\r\n" +
            "\r\n" +
            ScanLine() + "\r\n" +
            ScanLine("[172.16.42.1]:22") + "\n" +
            "   \n";
        var key = OfflineUpdateSshEnrollment.ParseHostKeyScanOutput(output);
        Assert.Equal(CanonicalKey(), key.PublicKey);
        Assert.Equal(IndependentFingerprint(), key.Fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# only comments\n\n")]
    [InlineData("192.0.2.1 ssh-ed25519 AAAA")] // other host (reserved test-net)
    [InlineData("[172.16.42.1]:2222 ssh-ed25519 AAAA")] // other port
    [InlineData("172.16.42.1 ssh-rsa AAAA")] // no RSA fallback
    [InlineData("172.16.42.1 ssh-ed25519 AAAA trailing")] // trailing garbage
    [InlineData("@cert-authority 172.16.42.1 ssh-ed25519 AAAA")] // options field
    [InlineData("172.16.42.1 ssh-ed25519 not!base64")]
    public void ParseHostKeyScanOutput_RejectsBadOrConflictingOutput(string output)
    {
        Assert.Throws<InvalidDataException>(() => OfflineUpdateSshEnrollment.ParseHostKeyScanOutput(output));
    }

    [Fact]
    public void ParseHostKeyScanOutput_RejectsDistinctValidHostKeys()
    {
        var output = ScanLine(key: Convert.ToBase64String(MakeWire(0xAA))) + "\n" +
                     ScanLine(key: Convert.ToBase64String(MakeWire(0xBB))) + "\n";
        Assert.Throws<InvalidDataException>(() => OfflineUpdateSshEnrollment.ParseHostKeyScanOutput(output));
    }

    [Fact]
    public void ParseHostKeyScanOutput_RejectsOversizedOutput()
    {
        Assert.Throws<InvalidDataException>(() =>
            OfflineUpdateSshEnrollment.ParseHostKeyScanOutput(new string('#', 16 * 1024 + 1)));
    }

    // ---- DiscoverUsbHostAsync ----

    [Fact]
    public async Task DiscoverUsbHostAsync_RunsTwoOrderedFixedCommandsAndReturnsCandidate()
    {
        var runner = new FakeRunner(
            new ProcessResult(0, OneEndpoint(), ""),
            new ProcessResult(0, ScanLine() + "\n", ""));

        var candidate = await OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(runner, _powerShell, _keyScan);

        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal(_powerShell, runner.Invocations[0].Executable);
        Assert.Equal(_keyScan, runner.Invocations[1].Executable);
        Assert.Equal("172.16.42.1", runner.Invocations[1].Arguments[^1]);
        Assert.Equal(
            new[] { "-4", "-T", "5", "-t", "ed25519", "-p", "22", "172.16.42.1" },
            runner.Invocations[1].Arguments);
        Assert.Equal(TimeSpan.FromSeconds(20), runner.Invocations[1].Timeout);
        Assert.DoesNotContain(runner.Invocations[1].Arguments, a => a == "-b");

        Assert.Equal(Id1, candidate.Endpoint.AdapterId);
        Assert.Equal("172.16.42.2", candidate.Endpoint.LocalAddress); // preserved, not keyscan host
        Assert.Equal("172.16.42.1", candidate.Endpoint.Host);
        Assert.Equal(CanonicalKey(), candidate.HostKey.PublicKey);
        Assert.Equal(IndependentFingerprint(), candidate.HostKey.Fingerprint);
    }

    [Fact]
    public async Task DiscoverUsbHostAsync_PrecancelsBeforeAnyCommand()
    {
        var runner = new FakeRunner(
            new ProcessResult(0, OneEndpoint(), ""),
            new ProcessResult(0, ScanLine(), ""));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(runner, _powerShell, _keyScan, cts.Token));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task DiscoverUsbHostAsync_CancellationAfterCimThrowsWithoutKeyscanAndNoRetry()
    {
        using var cts = new CancellationTokenSource();
        var runner = new FakeRunner(
            new ProcessResult(0, OneEndpoint(), ""),
            new OperationCanceledException(cts.Token));

        // Runner cancels the token during or after the first command
        runner.OnAfterFirstRun = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(runner, _powerShell, _keyScan, cts.Token));

        Assert.Single(runner.Invocations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task DiscoverUsbHostAsync_ZeroOrMultipleCIMPreventsKeyscan(int endpointCount)
    {
        var rows = endpointCount switch
        {
            0 => Array.Empty<object>(),
            _ => new object[]
            {
                Row(Id1, @"USB\VID_1D6B&PID_0104\a"),
                Row(Id2, @"USB\VID_1D6B&PID_0100\b", "172.16.42.3"),
            },
        };
        var runner = new FakeRunner(
            new ProcessResult(0, Envelope(rows), ""),
            new ProcessResult(0, ScanLine(), ""));

        await Assert.ThrowsAsync<IOException>(() =>
            OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(runner, _powerShell, _keyScan));
        Assert.Single(runner.Invocations); // keyscan never queued
    }

    [Fact]
    public async Task DiscoverUsbHostAsync_KeyscanFailureBoundedStderrNoRetry()
    {
        var runner = new FakeRunner(
            new ProcessResult(0, OneEndpoint(), ""),
            new ProcessResult(255, "", new string('x', 3000) + "TAIL"));

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(runner, _powerShell, _keyScan));

        Assert.Contains("exit code 255", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 2300);
        Assert.EndsWith("TAIL", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, runner.Invocations.Count); // no retry
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverUsbHostAsync_KeyscanTimeoutOrCancellationPropagatesWithoutRetry(bool useCancellation)
    {
        Exception secondCall = useCancellation
            ? new OperationCanceledException()
            : new TimeoutException();
        var runner = new FakeRunner(
            new ProcessResult(0, OneEndpoint(), ""),
            secondCall);

        if (useCancellation)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(runner, _powerShell, _keyScan));
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(runner, _powerShell, _keyScan));
        }

        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task DiscoverUsbHostAsync_KeyscanFailureSanitizesControlChars()
    {
        var runner = new FakeRunner(
            new ProcessResult(0, OneEndpoint(), ""),
            new ProcessResult(255, "", "error\u0000with\u001bcontrol\u0007chars\tand\nlines"));

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(runner, _powerShell, _keyScan));

        Assert.DoesNotContain("\u0000", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\t", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverUsbHostAsync_NoKeysInScanOutputRefuses()
    {
        var runner = new FakeRunner(
            new ProcessResult(0, OneEndpoint(), ""),
            new ProcessResult(0, "# nothing\r\n", ""));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(runner, _powerShell, _keyScan));
        Assert.Equal(2, runner.Invocations.Count); // single attempt, no retry
    }

    private sealed class FakeRunner(params object[] queue) : ICommandRunner
    {
        private readonly Queue<object> _queue = new(queue);
        public List<ProcessCommand> Invocations { get; } = new();
        public Action? OnAfterFirstRun { get; set; }

        public Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken token = default)
        {
            Invocations.Add(command);
            token.ThrowIfCancellationRequested();
            if (_queue.Count == 0)
                throw new InvalidOperationException("Unexpected extra command invocation.");
            var next = _queue.Dequeue();
            if (Invocations.Count == 1)
                OnAfterFirstRun?.Invoke();
            return next switch
            {
                ProcessResult result => Task.FromResult(result),
                Exception error => Task.FromException<ProcessResult>(error),
                _ => throw new InvalidOperationException("Bad queue entry."),
            };
        }
    }
}
