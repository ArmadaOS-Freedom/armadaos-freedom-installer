using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateSshManagedConnectionTests : IDisposable
{
    private const string SudoPromptPrefix = "ARMADA-SUDO-PROMPT-";

    private readonly string _root;
    private readonly string _trustDirectory;

    public OfflineUpdateSshManagedConnectionTests()
    {
        var rootEnv = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(rootEnv))
            throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be explicitly set.");

        _root = Path.Combine(rootEnv, "ManagedSsh_" + Guid.NewGuid().ToString("N"));
        _trustDirectory = Path.Combine(_root, "trust");
        Directory.CreateDirectory(_trustDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static OfflineUpdateSshHostKey HostKey(byte seed)
    {
        var wire = new byte[51];
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0, 4), 11);
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(wire.AsSpan(4, 11));
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(15, 4), 32);
        for (var i = 19; i < wire.Length; i++)
            wire[i] = seed;

        return new OfflineUpdateSshHostKey("ssh-ed25519", wire);
    }

    private static OfflineUpdateSshKey PublicKeyOf(OfflineUpdateSshHostKey key)
    {
        var text = "ssh-ed25519 " + Convert.ToBase64String(key.KeyBlob);
        return OfflineUpdateSshEnrollment.ParsePublicKey(text);
    }

    private static string InfoFrame(int id, string model = "TB323FU") =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            id,
            ok = true,
            result = new
            {
                schemaVersion = 1,
                updaterVersion = "0.8.0",
                model,
                actualSlot = "a",
                inactiveSlot = "b",
                deployment = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.0",
                recoveryPartUuid = "01234567-89ab-cdef-0123-456789abcdef",
            },
        });

    private static string ReadyOutput(int requestId = 1) =>
        OfflineUpdateSshManagedConnection.ReadyMarker + "\n" + InfoFrame(requestId) + "\n";

    private static string InfoErrorOutput(string code, string message, int requestId = 1) =>
        OfflineUpdateSshManagedConnection.ReadyMarker + "\n" + JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            id = requestId,
            ok = false,
            error = new { code, message },
        }) + "\n";

    private OfflineUpdateSshPasswordOptions Options(
        OfflineUpdateSshHostKey hostKey,
        string user = "armada",
        Func<OfflineUpdateSshHostKeyPrompt, CancellationToken, Task<bool>>? approve = null) =>
        new("device.example", 2222, user, "topsecret", _trustDirectory)
        {
            ApproveHostKey = approve ?? ((_, _) => Task.FromResult(true)),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

    private static FakeFactory Factory(FakeTransport transport) => new(transport);

    private static async Task<OfflineUpdateSshManagedException> ConnectFailsAsync(
        OfflineUpdateSshPasswordOptions options,
        FakeTransport transport)
    {
        return await Assert.ThrowsAsync<OfflineUpdateSshManagedException>(() =>
            OfflineUpdateSshManagedConnection.ConnectCoreAsync(
                options, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport)));
    }

    private static string PromptMarker(string command)
    {
        var index = command.IndexOf(SudoPromptPrefix, StringComparison.Ordinal);
        Assert.True(index >= 0, "The started command did not contain a sudo prompt marker: " + command);
        var end = index;
        while (end < command.Length && (char.IsAsciiLetterOrDigit(command[end]) || command[end] == '-'))
            end++;
        return command[index..end];
    }

    private static FakeChannel PasswordSudoChannel(
        string command,
        bool secondPrompt = false,
        string? readyOutput = null,
        int exitStatus = 0,
        string errorTail = "",
        bool commandMode = false,
        bool blockOutputForever = false)
    {
        var marker = PromptMarker(command);
        var prompt = Encoding.UTF8.GetBytes(marker);
        var segments = new List<ScriptedStream.Segment> { new(prompt, Task.CompletedTask) };
        if (secondPrompt)
            segments.Add(new ScriptedStream.Segment(prompt, null));

        return new FakeChannel(
            readyOutput ?? ReadyOutput(),
            gaiteOutputOnPassword: true,
            errorSegments: segments,
            exited: false,
            exitStatus: exitStatus,
            errorTail: errorTail,
            exitOnFirstLine: commandMode,
            outputGate: blockOutputForever ? Never : null);
    }

    private static readonly Task Never = new TaskCompletionSource<bool>().Task;

    [Fact]
    public async Task Connect_RootDirect_NeverUsesSudoOrPassword()
    {
        var hostKey = HostKey(1);
        var channel = new FakeChannel(ReadyOutput(), gaiteOutputOnPassword: false, exited: true);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };
        var options = Options(hostKey, user: "root");

        await using var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            options, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport));

        Assert.Equal("TB323FU", connection.DeviceInfo.GetProperty("model").GetString());
        Assert.Equal(new[] { "connect", "authenticated", "start" }, transport.Events);
        Assert.DoesNotContain("sudo", transport.StartedCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", channel.InputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_PasswordlessSudo_NeverSendsPasswordAndNeverProbes()
    {
        var hostKey = HostKey(2);
        var channel = new FakeChannel(ReadyOutput(), gaiteOutputOnPassword: false, exited: true);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };
        var options = Options(hostKey);

        await using var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            options, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport));

        Assert.Equal(new[] { "connect", "authenticated", "start" }, transport.Events);
        Assert.DoesNotContain("sudo -n", transport.StartedCommand, StringComparison.Ordinal);
        Assert.StartsWith("sudo -S -p " + SudoPromptPrefix, transport.StartedCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("topsecret", channel.InputText, StringComparison.Ordinal);
        Assert.StartsWith("{", channel.InputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_ProtectedHelperWithNopasswdProbeStyle_StillPromptsAndSendsPasswordOnce()
    {
        // There is no probe anymore; the helper itself is protected even though
        // a separate `sudo -n true` policy might have succeeded. The handshake
        // must discover the prompt from the real command.
        var hostKey = HostKey(3);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = command => PasswordSudoChannel(command),
        };
        var options = Options(hostKey);

        await using var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            options, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport));

        var lines = transport.LastChannel!.InputText.Split('\n');
        Assert.Equal("topsecret", lines[0]);
        Assert.StartsWith("{", lines[1], StringComparison.Ordinal);
        Assert.Single(lines.Where(line => line == "topsecret"));
    }

    [Fact]
    public async Task Connect_CachedSudo_NoPromptNoPassword()
    {
        var hostKey = HostKey(4);
        var channel = new FakeChannel(ReadyOutput(), gaiteOutputOnPassword: false, exited: true);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };
        var options = Options(hostKey);

        await using var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            options, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport));

        Assert.DoesNotContain("topsecret", channel.InputText, StringComparison.Ordinal);
        Assert.StartsWith("{", channel.InputText, StringComparison.Ordinal);
        Assert.StartsWith("sudo -S -p " + SudoPromptPrefix, transport.StartedCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SudoWithoutPrompt_OnAnAlreadyCompletedChannel_ReturnsOutputWithoutWriting()
    {
        // The concrete transport turns a command that finished before its
        // stdin could be acquired into a channel with the output retained at
        // completion and a write-refusing stdin. A mocked channel cannot
        // reproduce SSH.NET's own pipe lifetime, so this covers the caller-side
        // contract: passwordless sudo completes and the secret is never sent.
        var hostKey = HostKey(22);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = _ => new FakeChannel(
                "installed",
                gaiteOutputOnPassword: false,
                exited: true,
                inputClosed: true),
        };

        var result = await OfflineUpdateSshSudo.RunAsync(
            transport,
            "/bin/true",
            password: "topsecret",
            TimeSpan.FromSeconds(5),
            4096,
            CancellationToken.None);

        Assert.Equal(0, result.ExitStatus);
        Assert.Equal("installed", result.StandardOutput);
        Assert.Empty(transport.LastChannel!.InputBytes);
        Assert.False(transport.LastChannel!.StandardInput.CanWrite);
    }

    [Fact]
    public async Task SudoPromptWithoutCredential_RejectsWithoutWriting()
    {
        var hostKey = HostKey(5);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = command =>
            {
                var marker = PromptMarker(command);
                return new FakeChannel(
                    string.Empty,
                    gaiteOutputOnPassword: true,
                    errorSegments: new[] { new ScriptedStream.Segment(Encoding.UTF8.GetBytes(marker), Task.CompletedTask) },
                    exited: false);
            },
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateSshManagedException>(() =>
            OfflineUpdateSshSudo.RunAsync(
                transport, "/bin/true", password: null, TimeSpan.FromSeconds(5), 4096, CancellationToken.None));

        Assert.Equal(OfflineUpdateSshManagedFailure.SudoUnavailable, error.Reason);
        Assert.Empty(transport.LastChannel!.InputBytes);
    }

    [Fact]
    public async Task Connect_CancelledWhileWaitingForSudoPrompt_WritesNothing()
    {
        var hostKey = HostKey(6);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = _ => new FakeChannel(
                ReadyOutput(),
                gaiteOutputOnPassword: true,
                errorSegments: Array.Empty<ScriptedStream.Segment>(),
                exited: false),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineUpdateSshManagedConnection.ConnectCoreAsync(
                Options(hostKey), TimeSpan.FromSeconds(5), cts.Token, Factory(transport)));

        Assert.Empty(transport.LastChannel!.InputBytes);
    }

    [Fact]
    public async Task Connect_WrongSudoPassword_SecondPromptFailsWithOnePasswordWrite()
    {
        var hostKey = HostKey(7);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = command => PasswordSudoChannel(
                command, secondPrompt: true, blockOutputForever: true),
        };

        var error = await ConnectFailsAsync(Options(hostKey), transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.SudoUnavailable, error.Reason);
        var input = transport.LastChannel!.InputText;
        Assert.Single(input.Split('\n').Where(line => line == "topsecret"));
        Assert.DoesNotContain("{", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_UnknownHostKeyWithoutApproval_RejectsBeforeAuthenticationCompletes()
    {
        var hostKey = HostKey(8);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => new FakeChannel(ReadyOutput(), false, exited: false) };
        var options = Options(hostKey, approve: null);
        options.ApproveHostKey = null;

        var error = await ConnectFailsAsync(options, transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.HostKeyRejected, error.Reason);
        Assert.Equal(1, transport.ConnectCount);
        Assert.Equal(0, transport.StartCount);
        Assert.Empty(Directory.GetFiles(_trustDirectory));
    }

    [Fact]
    public async Task Connect_ExpectedFingerprintMismatch_RejectsBeforeAuthentication()
    {
        var actual = HostKey(9);
        var expectedOther = PublicKeyOf(HostKey(10)).Fingerprint;
        var transport = new FakeTransport(actual);
        var options = Options(actual, approve: null);
        options.ApproveHostKey = null;
        options.ExpectedHostFingerprint = expectedOther;

        var error = await ConnectFailsAsync(options, transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.HostKeyRejected, error.Reason);
        Assert.Equal(0, transport.StartCount);
        Assert.Empty(Directory.GetFiles(_trustDirectory));
    }

    [Fact]
    public async Task Connect_ExpectedFingerprintMatch_TrustsWithoutPrompt()
    {
        var key = HostKey(11);
        var transport = new FakeTransport(key) { ChannelFactory = _ => new FakeChannel(ReadyOutput(), false, exited: false) };
        var options = Options(key, approve: null);
        options.ApproveHostKey = null;
        options.ExpectedHostFingerprint = PublicKeyOf(key).Fingerprint;

        await using var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            options, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport));

        Assert.Equal(
            OfflineUpdateSshHostTrustStatus.Trusted,
            new OfflineUpdateSshHostTrustStore(_trustDirectory)
                .Evaluate("device.example", 2222, PublicKeyOf(key)).Status);
    }

    [Fact]
    public async Task Connect_ChangedHostKey_RequiresExplicitApprovalAgain()
    {
        var store = new OfflineUpdateSshHostTrustStore(_trustDirectory);
        var trusted = HostKey(12);
        store.Store("device.example", 2222, PublicKeyOf(trusted));

        var changed = HostKey(13);
        var rejectingTransport = new FakeTransport(changed) { ChannelFactory = _ => new FakeChannel(ReadyOutput(), false, exited: false) };
        var rejectOptions = Options(changed, approve: (_, _) => Task.FromResult(false));

        var error = await ConnectFailsAsync(rejectOptions, rejectingTransport);

        Assert.Equal(OfflineUpdateSshManagedFailure.HostKeyRejected, error.Reason);
        Assert.Equal(
            OfflineUpdateSshHostTrustStatus.Changed,
            store.Evaluate("device.example", 2222, PublicKeyOf(changed)).Status);

        var acceptingTransport = new FakeTransport(changed) { ChannelFactory = _ => new FakeChannel(ReadyOutput(), false, exited: false) };
        var acceptOptions = Options(changed, approve: (_, _) => Task.FromResult(true));
        await using var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            acceptOptions, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(acceptingTransport));

        Assert.Equal(
            OfflineUpdateSshHostTrustStatus.Trusted,
            store.Evaluate("device.example", 2222, PublicKeyOf(changed)).Status);
    }

    [Fact]
    public async Task Connect_LateHostKeyApprovalAfterCancellation_SavesNoTrustRecordAndStartsNoUpdater()
    {
        var hostKey = HostKey(24);
        var approvalStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateApproval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = _ => new FakeChannel(ReadyOutput(), false, exited: false),
        };
        var options = Options(hostKey, approve: async (_, _) =>
        {
            approvalStarted.TrySetResult(true);
            return await lateApproval.Task.ConfigureAwait(false);
        });
        using var cancelled = new CancellationTokenSource();

        var connect = OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            options, TimeSpan.FromSeconds(5), cancelled.Token, Factory(transport));

        await approvalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);

        // The user presses Yes only after the attempt has been cancelled.
        lateApproval.TrySetResult(true);
        await transport.ConnectFinished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, transport.StartCount);
        Assert.Empty(Directory.GetFiles(_trustDirectory));
    }

    [Fact]
    public async Task Connect_ExpectedFingerprintCancelledBeforeTrust_SavesNoTrustRecordAndStartsNoUpdater()
    {
        // A pinned key needs no prompt, so the only cancellation window is
        // between the key comparison and the trust write.
        var key = HostKey(25);
        using var cancelled = new CancellationTokenSource();
        var transport = new FakeTransport(key)
        {
            BeforeHostKeyApproval = cancelled.Cancel,
            ChannelFactory = _ => new FakeChannel(ReadyOutput(), false, exited: false),
        };
        var options = Options(key, approve: null);
        options.ApproveHostKey = null;
        options.ExpectedHostFingerprint = PublicKeyOf(key).Fingerprint;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineUpdateSshManagedConnection.ConnectCoreAsync(
                options, TimeSpan.FromSeconds(5), cancelled.Token, Factory(transport)));

        Assert.Equal(0, transport.StartCount);
        Assert.Empty(Directory.GetFiles(_trustDirectory));
    }

    [Fact]
    public async Task Connect_AuthenticationFailure_IsNotRetried()
    {
        var hostKey = HostKey(14);
        var transport = new FakeTransport(hostKey)
        {
            FailAuthentication = true,
            ChannelFactory = _ => new FakeChannel(ReadyOutput(), false, exited: false),
        };

        var error = await ConnectFailsAsync(Options(hostKey), transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.Authentication, error.Reason);
        Assert.Equal(1, transport.ConnectCount);
        Assert.Equal(0, transport.StartCount);
    }

    [Fact]
    public async Task Connect_PreCancelled_NeverCreatesTransport()
    {
        var hostKey = HostKey(15);
        var transport = new FakeTransport(hostKey);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineUpdateSshManagedConnection.ConnectCoreAsync(
                Options(hostKey), TimeSpan.FromSeconds(5), cts.Token, Factory(transport)));

        Assert.Equal(0, transport.ConnectCount);
    }

    [Fact]
    public async Task Connect_UpdaterStartupFailure_ReportsMissingComponentsAndDisposes()
    {
        var hostKey = HostKey(16);
        var channel = new FakeChannel(
            string.Empty,
            gaiteOutputOnPassword: false,
            exited: true,
            exitStatus: 127,
            errorSegments: new[]
            {
                new ScriptedStream.Segment(
                    Encoding.UTF8.GetBytes("sh: updater: No such file or directory"),
                    Task.CompletedTask),
            });
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };

        var error = await ConnectFailsAsync(Options(hostKey), transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.UpdaterUnavailable, error.Reason);
        Assert.Contains("bootstrap", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, transport.DisposeCount);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task Connect_SudoStartupFailure_ReportsSudoUnavailableReasonAndMessage()
    {
        // The sudo command exits before the ready marker and reports a policy
        // refusal. Both the classified Reason and the copy built from it must
        // identify sudo, never missing updater components.
        var hostKey = HostKey(26);
        var channel = new FakeChannel(
            "sudo: a password is required\n",
            gaiteOutputOnPassword: false,
            exited: true,
            exitStatus: 1);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };

        var error = await ConnectFailsAsync(Options(hostKey), transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.SudoUnavailable, error.Reason);
        Assert.Contains("sudo", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bootstrap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connect_InfoQueryRejectedByUpdater_KeepsRemoteCauseAndDoesNotBlameMissingComponents()
    {
        var hostKey = HostKey(22);
        const string cause = "bootc missing manifest: /usr/lib/armada/manifests/current.json";
        var channel = new FakeChannel(
            InfoErrorOutput("operation_failed", cause),
            gaiteOutputOnPassword: false,
            exited: true);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };

        var error = await ConnectFailsAsync(Options(hostKey), transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.UpdaterUnavailable, error.Reason);
        Assert.Contains("operation_failed", error.Message, StringComparison.Ordinal);
        Assert.Contains(cause, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("may not be installed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("topsecret", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task Connect_InfoQueryClosedAfterReady_ReportsClosedConnectionNotMissingComponents()
    {
        var hostKey = HostKey(23);
        var channel = new FakeChannel(
            OfflineUpdateSshManagedConnection.ReadyMarker + "\n",
            gaiteOutputOnPassword: false,
            exited: true);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };

        var error = await ConnectFailsAsync(Options(hostKey), transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.UpdaterUnavailable, error.Reason);
        Assert.Contains("closed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("may not be installed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task PrepareSession_RunsAfterAuthenticationAndBeforeUpdater()
    {
        var hostKey = HostKey(17);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = _ => new FakeChannel(ReadyOutput(), false, exited: false),
        };
        var options = Options(hostKey);
        options.PrepareSessionAsync = async (session, token) =>
        {
            transport.Events.Add("prepare");
            var result = await session.ExecuteAsync("/bin/true", Array.Empty<string>(), useSudo: false, token);
            Assert.True(result.Succeeded);
        };

        await using var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            options, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport));

        Assert.Equal(
            new[]
            {
                "connect",
                "authenticated",
                "prepare",
                "run:/bin/true",
                "start",
            },
            transport.Events);
        Assert.NotNull(connection.Session);
    }

    [Fact]
    public async Task PrepareSession_Sudo_UsesPromptHandshake()
    {
        var hostKey = HostKey(18);
        FakeChannel? prepareChannel = null;
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = _ => new FakeChannel(ReadyOutput(), false, exited: false),
        };
        transport.ChannelQueue.Enqueue(command =>
        {
            var channel = PasswordSudoChannel(
                command, readyOutput: string.Empty, commandMode: true);
            prepareChannel = channel;
            return channel;
        });
        transport.ChannelQueue.Enqueue(_ => new FakeChannel(ReadyOutput(), false, exited: false));
        var options = Options(hostKey);
        options.PrepareSessionAsync = (session, token) =>
            session.ExecuteAsync("/bin/true", Array.Empty<string>(), useSudo: true, token);

        await using var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            options, TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport));

        Assert.NotNull(prepareChannel);
        Assert.Single(prepareChannel!.InputText.Split('\n').Where(line => line == "topsecret"));
    }

    [Fact]
    public void BuildCommandLine_QuotesArgumentsAndRejectsUnsafeInput()
    {
        Assert.Equal(
            "/bin/sh -c 'echo hi'",
            OfflineUpdateSshManagedSession.BuildCommandLine("/bin/sh", new[] { "-c", "echo hi" }));

        var quoted = OfflineUpdateSshManagedSession.BuildCommandLine(
            "/bin/echo", new[] { "a'; rm -rf /" });
        Assert.Contains("'a'\\''; rm -rf /'", quoted, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            OfflineUpdateSshManagedSession.BuildCommandLine("relative", Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() =>
            OfflineUpdateSshManagedSession.BuildCommandLine("/bin/echo", new[] { "line\nbreak" }));
    }

    [Fact]
    public async Task Upload_RejectsUnsafeRemotePathWithoutStartingAChannel()
    {
        var hostKey = HostKey(19);
        var transport = new FakeTransport(hostKey);
        var session = new OfflineUpdateSshManagedSession(transport);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.UploadFileAsync("/etc/../shadow", new MemoryStream(new byte[] { 1 }), useSudo: false));

        Assert.Equal(0, transport.StartCount);
    }

    [Fact]
    public async Task Upload_RejectsPrivilegedPathWithoutStartingAChannel()
    {
        var hostKey = HostKey(20);
        var transport = new FakeTransport(hostKey);
        var session = new OfflineUpdateSshManagedSession(transport);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.UploadFileAsync("/usr/libexec/armada/armada-offline-update", new MemoryStream(new byte[] { 1 }), useSudo: false));

        Assert.Equal(0, transport.StartCount);
    }

    [Fact]
    public async Task Upload_WithSudo_IsRejectedWithoutStartingAChannel()
    {
        var hostKey = HostKey(21);
        var transport = new FakeTransport(hostKey);
        var session = new OfflineUpdateSshManagedSession(transport, "topsecret");

        var error = await Assert.ThrowsAsync<OfflineUpdateSshManagedException>(() =>
            session.UploadFileAsync("/tmp/armada-offline-bootstrap.x/file", new MemoryStream(new byte[] { 1 }), useSudo: true));

        Assert.Equal(OfflineUpdateSshManagedFailure.SudoUnavailable, error.Reason);
        Assert.Equal(0, transport.StartCount);
    }

    private sealed class FakeFactory : IOfflineUpdateSshTransportFactory
    {
        private readonly FakeTransport _transport;

        internal FakeFactory(FakeTransport transport) => _transport = transport;

        public IOfflineUpdateSshTransport Create(OfflineUpdateSshPasswordOptions options) => _transport;
    }

    private sealed class FakeTransport : IOfflineUpdateSshTransport
    {
        private readonly OfflineUpdateSshHostKey _hostKey;
        private readonly TaskCompletionSource<bool> _connectFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FakeTransport(OfflineUpdateSshHostKey hostKey) => _hostKey = hostKey;

        internal List<string> Events { get; } = new();

        /// <summary>Completes when Connect returned or threw.</summary>
        internal Task ConnectFinished => _connectFinished.Task;

        internal int ConnectCount { get; private set; }

        internal int StartCount { get; private set; }

        internal int DisposeCount { get; private set; }

        internal bool FailAuthentication { get; init; }

        internal Action? BeforeHostKeyApproval { get; init; }

        internal Func<string, FakeChannel> ChannelFactory { get; init; } =
            _ => new FakeChannel(ReadyOutput(), false, exited: false);

        internal Queue<Func<string, FakeChannel>> ChannelQueue { get; } = new();

        internal string StartedCommand { get; private set; } = string.Empty;

        internal FakeChannel? LastChannel { get; private set; }

        public void Connect(TimeSpan timeout, Func<OfflineUpdateSshHostKey, bool> approveHostKey, CancellationToken token)
        {
            try
            {
                ConnectCount++;
                Events.Add("connect");
                token.ThrowIfCancellationRequested();

                if (FailAuthentication)
                    throw new OfflineUpdateSshAuthenticationException("denied");

                BeforeHostKeyApproval?.Invoke();

                if (!approveHostKey(_hostKey))
                    throw new OfflineUpdateSshHostKeyRejectedException("rejected");

                Events.Add("authenticated");
            }
            finally
            {
                _connectFinished.TrySetResult(true);
            }
        }

        public Task<OfflineUpdateSshProbeResult> RunAsync(
            string command,
            TimeSpan timeout,
            int maxOutputBytes,
            CancellationToken token)
        {
            Events.Add("run:" + command);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new OfflineUpdateSshProbeResult(0, string.Empty, string.Empty));
        }

        public IOfflineUpdateSshChannel Start(string command)
        {
            StartCount++;
            StartedCommand = command;
            Events.Add("start");
            var channel = ChannelQueue.Count > 0 ? ChannelQueue.Dequeue()(command) : ChannelFactory(command);
            LastChannel = channel;
            return channel;
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class FakeChannel : IOfflineUpdateSshChannel
    {
        private readonly RecordingInput _input;
        private readonly ScriptedStream _output;
        private readonly ScriptedStream _error;
        private readonly TaskCompletionSource<bool> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _exitStatus;
        private readonly string _errorTail;

        internal FakeChannel(
            string output,
            bool gaiteOutputOnPassword,
            bool exited,
            int exitStatus = 0,
            string errorTail = "",
            IReadOnlyList<ScriptedStream.Segment>? errorSegments = null,
            bool exitOnFirstLine = false,
            Task? outputGate = null,
            bool inputClosed = false)
        {
            _exitStatus = exitStatus;
            _errorTail = errorTail;
            _input = new RecordingInput(inputClosed);
            var effectiveGate = outputGate ??
                (gaiteOutputOnPassword ? _input.FirstLineWritten : Task.CompletedTask);
            _output = new ScriptedStream(new[]
            {
                new ScriptedStream.Segment(Encoding.UTF8.GetBytes(output), effectiveGate),
            });
            _error = new ScriptedStream(
                errorSegments ?? Array.Empty<ScriptedStream.Segment>(),
                _input.FirstLineWritten);
            if (exited)
                _exit.TrySetResult(true);
            if (exitOnFirstLine)
            {
                _ = Task.Run(async () =>
                {
                    await _input.FirstLineWritten;
                    _exit.TrySetResult(true);
                });
            }
        }

        public Stream StandardInput => _input;

        public Stream StandardOutput => _output;

        public Stream StandardError => _error;

        public bool HasExited => _exit.Task.IsCompleted && _exit.Task.Result;

        public int ExitStatus => _exitStatus;

        internal bool Disposed { get; private set; }

        internal byte[] InputBytes => _input.ToArray();

        internal string InputText => Encoding.UTF8.GetString(InputBytes);

        public bool WaitForExit(TimeSpan timeout) => _exit.Task.Wait(timeout) && _exit.Task.Result;

        public void Cancel() => _exit.TrySetResult(false);

        public void Dispose()
        {
            Cancel();
            Disposed = true;
        }

        public string ErrorTail => _errorTail;
    }

    private sealed class RecordingInput : Stream
    {
        private const string ClosedMessage =
            "The remote command has already completed; its standard input is closed.";

        private readonly MemoryStream _inner = new();
        private readonly bool _closed;
        private readonly TaskCompletionSource<bool> _firstLine = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal RecordingInput(bool closed = false) => _closed = closed;

        internal Task FirstLineWritten => _firstLine.Task;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_closed;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        internal byte[] ToArray() => _inner.ToArray();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_closed)
                throw new IOException(ClosedMessage);

            await _inner.WriteAsync(buffer, cancellationToken);
            if (buffer.Span.IndexOf((byte)'\n') >= 0)
                _firstLine.TrySetResult(true);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_closed)
                throw new IOException(ClosedMessage);

            _inner.Write(buffer, offset, count);
            if (buffer.AsSpan(offset, count).IndexOf((byte)'\n') >= 0)
                _firstLine.TrySetResult(true);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ScriptedStream : Stream
    {
        internal readonly record struct Segment(byte[] Data, Task? Gate);

        private readonly Queue<Segment> _segments;
        private readonly Task _defaultGate;
        private MemoryStream? _current;

        internal ScriptedStream(IEnumerable<Segment> segments, Task? defaultGate = null)
        {
            _segments = new Queue<Segment>(segments);
            _defaultGate = defaultGate ?? Task.CompletedTask;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_current is not null)
                {
                    var read = await _current.ReadAsync(buffer, cancellationToken);
                    if (read > 0)
                        return read;
                    _current = null;
                }

                if (_segments.Count == 0)
                    return 0;

                var segment = _segments.Dequeue();
                await (segment.Gate ?? _defaultGate).WaitAsync(cancellationToken);
                _current = new MemoryStream(segment.Data);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
