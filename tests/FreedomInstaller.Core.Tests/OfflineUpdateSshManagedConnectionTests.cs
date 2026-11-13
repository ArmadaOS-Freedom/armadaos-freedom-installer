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

    /// <summary>
    /// A sudo channel that prompts immediately and takes its standard input
    /// from the supplied stream, so a fixture can stall exactly one password
    /// send without touching a real transport.
    /// </summary>
    private static FakeChannel PromptedSudoChannel(
        string command,
        Stream standardInput,
        List<string>? events = null,
        Task? outputGate = null) =>
        new(
            string.Empty,
            gaiteOutputOnPassword: false,
            exited: false,
            errorSegments: new[]
            {
                new ScriptedStream.Segment(Encoding.UTF8.GetBytes(PromptMarker(command)), Task.CompletedTask),
            },
            outputGate: outputGate,
            standardInput: standardInput,
            events: events);

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
        // The fake stderr pump signals its first read, so the cancel below is
        // issued exactly when the connection is waiting for a prompt instead of
        // after an arbitrary delay.
        var promptWaitStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = command =>
            {
                var marker = PromptMarker(command);
                return new FakeChannel(
                    ReadyOutput(),
                    gaiteOutputOnPassword: true,
                    errorSegments: new[]
                    {
                        // Held open by a gate that never completes: the prompt is
                        // never delivered and the handshake stays waiting.
                        new ScriptedStream.Segment(Encoding.UTF8.GetBytes(marker), Never),
                    },
                    exited: false,
                    errorReadStarted: promptWaitStarted);
            },
        };
        using var cts = new CancellationTokenSource();

        var connect = OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            Options(hostKey), TimeSpan.FromSeconds(5), cts.Token, Factory(transport));

        await promptWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
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
    public async Task Connect_SudoRefusalOnStderrAfterStdoutEof_KeepsDeviceDiagnostic()
    {
        // A command can close stdout before its refusal bytes leave stderr. The
        // failure detail must wait for the bounded stderr pump instead of
        // reporting an empty device message; the fake releases stderr only once
        // the reader has observed stdout EOF, so the ordering is deterministic.
        var hostKey = HostKey(27);
        var stdoutEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel(
            string.Empty,
            gaiteOutputOnPassword: false,
            exited: true,
            exitStatus: 1,
            errorSegments: new[]
            {
                new ScriptedStream.Segment(
                    Encoding.UTF8.GetBytes("sudo: a password is required\n"),
                    Task.CompletedTask),
            },
            stdoutEnded: stdoutEnded);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };

        var error = await ConnectFailsAsync(Options(hostKey), transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.SudoUnavailable, error.Reason);
        Assert.Contains("sudo: a password is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_SudoRefusalSplitAcrossStderrChunks_IsDecodedAsOneStream()
    {
        // A diagnostic character can straddle a pipe read. The stderr pump keeps
        // one decoder for the whole stream, so the two bytes of 'é' must not
        // become replacement characters.
        var hostKey = HostKey(28);
        var refusal = Encoding.UTF8.GetBytes("sudo: a password is required (café \u2192 denied)\n");
        var split = Array.IndexOf(refusal, (byte)0xC3) + 1;
        Assert.InRange(split, 1, refusal.Length - 1);

        var stdoutEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel(
            string.Empty,
            gaiteOutputOnPassword: false,
            exited: true,
            exitStatus: 1,
            errorSegments: new[]
            {
                new ScriptedStream.Segment(refusal[..split], Task.CompletedTask),
                new ScriptedStream.Segment(refusal[split..], Task.CompletedTask),
            },
            stdoutEnded: stdoutEnded);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };

        var error = await ConnectFailsAsync(Options(hostKey), transport);

        Assert.Equal(OfflineUpdateSshManagedFailure.SudoUnavailable, error.Reason);
        Assert.Contains("café \u2192 denied", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SudoRun_PreCancelled_NeverStartsTheChannel()
    {
        var transport = new FakeTransport(HostKey(29));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineUpdateSshSudo.RunAsync(
                transport, "/bin/true", "topsecret", TimeSpan.FromSeconds(5), 4096, cts.Token));

        Assert.Equal(0, transport.StartCount);
    }

    [Fact]
    public async Task StartUpdater_PreCancelled_NeverStartsTheChannel()
    {
        var transport = new FakeTransport(HostKey(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineUpdateSshSudo.StartUpdaterAsync(
                transport,
                "/bin/sh -c 'true'",
                useSudo: true,
                OfflineUpdateSshManagedConnection.ReadyMarker,
                "topsecret",
                TimeSpan.FromSeconds(5),
                4096,
                cts.Token));

        Assert.Equal(0, transport.StartCount);
    }

    [Fact]
    public async Task SudoRun_MultiByteOutputSplitAcrossReads_IsDecodedAsOneStream()
    {
        // The stdout pump shares the same one-decoder rule as the stderr pump.
        var output = Encoding.UTF8.GetBytes("installé \u2192 done\n");
        var split = Array.IndexOf(output, (byte)0xC3) + 1;
        Assert.InRange(split, 1, output.Length - 1);

        var transport = new FakeTransport(HostKey(31))
        {
            ChannelFactory = _ => new FakeChannel(
                string.Empty,
                gaiteOutputOnPassword: false,
                exited: true,
                outputSegments: new[]
                {
                    new ScriptedStream.Segment(output[..split], Task.CompletedTask),
                    new ScriptedStream.Segment(output[split..], Task.CompletedTask),
                }),
        };

        var result = await OfflineUpdateSshSudo.RunAsync(
            transport, "/bin/true", "topsecret", TimeSpan.FromSeconds(5), 4096, CancellationToken.None);

        Assert.Equal("installé \u2192 done\n", result.StandardOutput);
    }

    [Fact]
    public async Task SudoRun_TruncatedFinalCharacter_IsReplacedAtEndOfStream()
    {
        // The decoder is flushed at end of stream, so a truncated final sequence
        // is reported instead of silently dropped.
        var payload = Encoding.ASCII.GetBytes("done").Concat(new byte[] { 0xC3 }).ToArray();
        var transport = new FakeTransport(HostKey(32))
        {
            ChannelFactory = _ => new FakeChannel(
                string.Empty,
                gaiteOutputOnPassword: false,
                exited: true,
                outputSegments: new[]
                {
                    new ScriptedStream.Segment(payload, Task.CompletedTask),
                }),
        };

        var result = await OfflineUpdateSshSudo.RunAsync(
            transport, "/bin/true", "topsecret", TimeSpan.FromSeconds(5), 4096, CancellationToken.None);

        Assert.Equal("done\uFFFD", result.StandardOutput);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_EmptyContent_RejectsWithoutStartingAChannel(bool nonSeekable)
    {
        // `cat > path` truncates the staging target the moment it starts, so an
        // exhausted or empty stream must be refused before the remote command.
        var transport = new FakeTransport(HostKey(33));
        var session = new OfflineUpdateSshManagedSession(transport);
        Stream content = nonSeekable
            ? new ScriptedStream(Array.Empty<ScriptedStream.Segment>())
            : new MemoryStream(Array.Empty<byte>());

        var error = await Assert.ThrowsAsync<OfflineUpdateSshManagedException>(() =>
            session.UploadFileAsync("/tmp/armada-stage/file.bin", content, useSudo: false));

        Assert.Equal(OfflineUpdateSshManagedFailure.Bootstrap, error.Reason);
        Assert.Equal(0, transport.StartCount);
    }

    [Fact]
    public async Task Upload_CancelledDuringFirstRead_SendsNoCommand()
    {
        var transport = new FakeTransport(HostKey(34));
        var session = new OfflineUpdateSshManagedSession(transport);
        using var cancelled = new CancellationTokenSource();

        var upload = session.UploadFileAsync(
            "/tmp/armada-stage/file.bin", new BlockingReadStream(), useSudo: false, cancelled.Token);
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        Assert.Equal(0, transport.StartCount);
    }

    [Fact]
    public async Task Upload_CancelledTokenWithDataReturningStream_SendsNoCommand()
    {
        // The stream ignores the token and yields its bytes even though
        // cancellation is already requested. The read itself performs no
        // remote work, but `cat > path` truncates on start, so the upload must
        // re-check the token after the read instead of trusting the stream.
        var transport = new FakeTransport(HostKey(36));
        var session = new OfflineUpdateSshManagedSession(transport);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var content = new CancelledButDataStream(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.UploadFileAsync(
                "/tmp/armada-stage/file.bin",
                content,
                useSudo: false,
                cancelled.Token));

        // The read really happened while the token was already cancelled and
        // really handed back the payload, so the abort can only come from the
        // post-read guard, and that guard ran before any channel existed.
        Assert.True(content.TokenWasCancelledOnRead);
        Assert.Equal(3, content.BytesReturned);
        Assert.Equal(1, content.ReadCount);
        Assert.Equal(0, transport.StartCount);
        Assert.Null(transport.LastChannel);
        Assert.Empty(transport.StartedCommand);
    }

    [Fact]
    public async Task Upload_MultiChunkContent_WritesEveryOriginalByte()
    {
        FakeUploadGuard? guard = null;
        var transport = new FakeTransport(HostKey(35))
        {
            ChannelFactory = _ => new FakeChannel(
                string.Empty,
                gaiteOutputOnPassword: false,
                exited: false,
                exitOnInputClosed: true),
            UploadGuardFactory = () => guard = new FakeUploadGuard(),
        };
        var session = new OfflineUpdateSshManagedSession(transport);
        var payload = Payload(96 * 1024, 37);
        var firstSplit = 1024;
        var secondSplit = firstSplit + 70_000;
        var content = new ScriptedStream(new[]
        {
            new ScriptedStream.Segment(payload[..firstSplit], Task.CompletedTask),
            new ScriptedStream.Segment(payload[firstSplit..secondSplit], Task.CompletedTask),
            new ScriptedStream.Segment(payload[secondSplit..], Task.CompletedTask),
        });

        await session.UploadFileAsync("/tmp/armada-stage/file.bin", content, useSudo: false);

        Assert.Equal("cat > '/tmp/armada-stage/file.bin'", transport.StartedCommand);
        Assert.Equal(payload, transport.LastChannel!.InputBytes);

        // A successful upload keeps the session: the guard is released without
        // ever closing the socket, and it received a finite send timeout it
        // restores on disposal.
        Assert.NotNull(guard);
        Assert.Equal(0, guard!.AbortCount);
        Assert.True(guard.Disposed);
        Assert.Equal(1, transport.UploadGuardCount);
        Assert.True(transport.LastUploadSendTimeout > TimeSpan.Zero);
        Assert.True(transport.LastUploadSendTimeout < TimeSpan.FromMinutes(5));
        Assert.True(transport.LastChannel!.Disposed);

        // A completed upload keeps the session reusable: no socket release, and
        // the batch path never applies a transient budget to it.
        Assert.Equal(0, transport.SocketAbortCount);
    }

    [Fact]
    public async Task Upload_WithoutAnAbortableSocket_FailsClosedBeforeTheRemoteCommand()
    {
        // `cat > path` truncates on start, so an upload that could not be
        // aborted must never reach the remote command.
        var transport = new FakeTransport(HostKey(38)) { UploadGuardFactory = () => null };
        var session = new OfflineUpdateSshManagedSession(transport);
        var content = new MemoryStream(new byte[] { 1, 2, 3 });

        var error = await Assert.ThrowsAsync<OfflineUpdateSshManagedException>(() =>
            session.UploadFileAsync("/tmp/armada-stage/file.bin", content, useSudo: false));

        Assert.Equal(OfflineUpdateSshManagedFailure.Bootstrap, error.Reason);
        Assert.Contains("socket", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, transport.UploadGuardCount);
        Assert.Equal(0, transport.StartCount);
        Assert.Null(transport.LastChannel);

        // The source belongs to the caller and is never disposed here.
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task Upload_ExceedingTheAbsoluteDeadline_AbortsTheSocketAndReportsTimeout()
    {
        // The fake write stands in for SSH.NET's synchronous send: it ignores
        // the token and only returns when the guard closes the socket. Without
        // the abort the upload would never finish.
        var input = new BlockingWriteStream();
        var channel = new FakeChannel(
            string.Empty,
            gaiteOutputOnPassword: false,
            exited: false,
            standardInput: input);
        FakeUploadGuard? guard = null;
        var transport = new FakeTransport(HostKey(39))
        {
            ChannelFactory = _ => channel,
            UploadGuardFactory = () => guard = new FakeUploadGuard(() =>
            {
                input.Release();
                channel.Cancel();
            }),
        };
        var session = new OfflineUpdateSshManagedSession(
            transport,
            uploadTimeout: TimeSpan.FromSeconds(1));
        var content = new ScriptedStream(new[]
        {
            new ScriptedStream.Segment(new byte[] { 1, 2, 3 }, Task.CompletedTask),
        });

        var upload = session.UploadFileAsync("/tmp/armada-stage/file.bin", content, useSudo: false);
        await input.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<OfflineUpdateSshManagedException>(() => upload);

        Assert.Equal(OfflineUpdateSshManagedFailure.Timeout, error.Reason);
        Assert.Contains("did not finish", error.Message, StringComparison.Ordinal);
        Assert.NotNull(guard);
        Assert.Equal(1, guard!.AbortCount);
        Assert.True(guard.Disposed);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task Upload_CancelledWhileThePeerStopsReading_AbortsTheSocketAndReportsCancellation()
    {
        var input = new BlockingWriteStream();
        var channel = new FakeChannel(
            string.Empty,
            gaiteOutputOnPassword: false,
            exited: false,
            standardInput: input);
        FakeUploadGuard? guard = null;
        var transport = new FakeTransport(HostKey(40))
        {
            ChannelFactory = _ => channel,
            UploadGuardFactory = () => guard = new FakeUploadGuard(() =>
            {
                input.Release();
                channel.Cancel();
            }),
        };
        var session = new OfflineUpdateSshManagedSession(transport);
        var content = new ScriptedStream(new[]
        {
            new ScriptedStream.Segment(new byte[] { 1, 2, 3 }, Task.CompletedTask),
        });
        using var cancelled = new CancellationTokenSource();

        var upload = session.UploadFileAsync(
            "/tmp/armada-stage/file.bin", content, useSudo: false, cancelled.Token);
        await input.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);

        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.NotNull(guard);
        Assert.Equal(1, guard!.AbortCount);
        Assert.True(guard.Disposed);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task Upload_DeadlineDuringRemoteCompletion_AbortsTheSocketAndReportsTimeout()
    {
        // The copy and its EOF succeed, but the remote command never exits.
        // The same absolute budget has to cover completion, not only the copy.
        var input = new RecordingInput();
        var channel = new FakeChannel(
            string.Empty,
            gaiteOutputOnPassword: false,
            exited: false,
            standardInput: input,
            waitForExitIgnoresTimeout: true);
        FakeUploadGuard? guard = null;
        var transport = new FakeTransport(HostKey(41))
        {
            ChannelFactory = _ => channel,
            UploadGuardFactory = () => guard = new FakeUploadGuard(() => channel.Cancel()),
        };
        var session = new OfflineUpdateSshManagedSession(
            transport,
            uploadTimeout: TimeSpan.FromSeconds(1));
        var content = new ScriptedStream(new[]
        {
            new ScriptedStream.Segment(new byte[] { 1, 2, 3 }, Task.CompletedTask),
        });

        var error = await Assert.ThrowsAsync<OfflineUpdateSshManagedException>(() =>
            session.UploadFileAsync("/tmp/armada-stage/file.bin", content, useSudo: false));

        Assert.Equal(OfflineUpdateSshManagedFailure.Timeout, error.Reason);
        Assert.NotNull(guard);
        Assert.Equal(1, guard!.AbortCount);
        Assert.True(guard.Disposed);
        Assert.Equal(new byte[] { 1, 2, 3 }, input.ToArray());
    }

    [Fact]
    public async Task Upload_LocalReadFailureAfterStart_AbortsTheSocketAndKeepsTheOriginalFailure()
    {
        // The copy fails locally after `cat` already started, so the remote
        // transfer is incomplete. The captured socket has to be shut down
        // before cleanup, and the local failure is what the caller sees.
        var channel = new FakeChannel(
            string.Empty,
            gaiteOutputOnPassword: false,
            exited: false);
        FakeUploadGuard? guard = null;
        var transport = new FakeTransport(HostKey(42))
        {
            ChannelFactory = _ => channel,
            UploadGuardFactory = () => guard = new FakeUploadGuard(() => channel.Cancel()),
        };
        var session = new OfflineUpdateSshManagedSession(transport);
        var content = new ScriptedStream(new[]
        {
            new ScriptedStream.Segment(new byte[] { 1, 2, 3 }, Task.CompletedTask),
            new ScriptedStream.Segment(
                Array.Empty<byte>(),
                Task.FromException(new IOException("The local upload payload could not be read."))),
        });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            session.UploadFileAsync("/tmp/armada-stage/file.bin", content, useSudo: false));

        Assert.Equal("The local upload payload could not be read.", error.Message);
        Assert.Equal(1, transport.StartCount);
        Assert.NotNull(guard);
        Assert.Equal(1, guard!.AbortCount);
        Assert.True(guard.Disposed);
        Assert.True(channel.Disposed);
    }

    [Fact]
    public async Task Dispose_HealthyRpc_DisconnectsInOrderAndNeverReleasesTheSocket()
    {
        // No exchange is in flight and none was interrupted, so teardown is an
        // ordinary cancel-and-disconnect: the session socket stays untouched and
        // only the channel and the transport are released, exactly once.
        var hostKey = HostKey(43);
        var events = new List<string>();
        var channel = new FakeChannel(
            ReadyOutput(), gaiteOutputOnPassword: false, exited: false, events: events);
        var transport = new FakeTransport(hostKey) { ChannelFactory = _ => channel };

        var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            Options(hostKey), TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport));
        Assert.False(connection.Rpc.IsPoisoned);

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        Assert.Equal(0, transport.SocketAbortCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.True(channel.Disposed);
        Assert.Contains("channel-cancel", events);
    }

    [Fact]
    public async Task Dispose_AfterAnInterruptedRpcSend_ReleasesTheSocketBeforeCancellingTheChannel()
    {
        // The fake stdin stands in for SSH.NET's synchronous send under the
        // session write lock: it ignores the token, so cancelling the request
        // leaves the physical write running and poisons the RPC. Teardown has
        // to shut that socket down before it cancels the sudo channel or
        // disconnects, or cleanup would queue behind the blocked writer.
        // The stall is armed only after connect and its own info exchange
        // completed, so this proves the interrupted RPC send rather than a
        // bootstrap that never got its info frame written.
        var hostKey = HostKey(44);
        var events = new List<string>();
        var input = new BlockingWriteStream(armed: false);
        var channel = new FakeChannel(
            ReadyOutput(),
            gaiteOutputOnPassword: false,
            exited: false,
            standardInput: input,
            events: events);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = _ => channel,
            SocketAbortFactory = () => new FakeSocketAbort(() =>
            {
                events.Add("socket-release");
                input.Release();
            }),
        };

        var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            Options(hostKey), TimeSpan.FromSeconds(5), CancellationToken.None, Factory(transport));
        Assert.False(connection.Rpc.IsPoisoned);
        Assert.Equal(0, transport.SocketAbortCount);

        // From here every write stalls, exactly like a peer that stopped
        // reading the session's stdin.
        input.Arm();

        using var cancelled = new CancellationTokenSource();
        var query = connection.Rpc.GetInfoAsync(cancelled.Token);
        await input.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.True(connection.Rpc.IsPoisoned);

        await connection.DisposeAsync();

        Assert.Equal(1, transport.SocketAbortCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.True(channel.Disposed);

        var released = events.IndexOf("socket-release");
        Assert.True(released >= 0, "The teardown never released the socket: " + string.Join(",", events));
        Assert.True(
            released < events.IndexOf("channel-cancel"),
            "The socket must be released before the channel is cancelled: " + string.Join(",", events));
    }

    [Fact]
    public async Task Connect_InfoQueryCancelledWhileItsSendIsBlocked_ReleasesTheSocketBeforeTeardown()
    {
        // Ownership never reaches a connection object here, so the connect
        // cleanup has to apply the same abort-before-cancel rule on its own.
        var hostKey = HostKey(45);
        var events = new List<string>();
        var input = new BlockingWriteStream();
        var channel = new FakeChannel(
            ReadyOutput(),
            gaiteOutputOnPassword: false,
            exited: false,
            standardInput: input,
            events: events);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = _ => channel,
            SocketAbortFactory = () => new FakeSocketAbort(() =>
            {
                events.Add("socket-release");
                input.Release();
            }),
        };
        using var cancelled = new CancellationTokenSource();

        var connect = OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            Options(hostKey), TimeSpan.FromSeconds(5), cancelled.Token, Factory(transport));

        await input.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);

        Assert.Equal(1, transport.SocketAbortCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.True(channel.Disposed);
        Assert.True(
            events.IndexOf("socket-release") < events.IndexOf("channel-cancel"),
            "The socket must be released before the channel is cancelled: " + string.Join(",", events));
    }

    [Fact]
    public async Task StartUpdater_CancelledWhileThePasswordSendIsBlocked_ReleasesTheSocketBeforeCleanup()
    {
        // The startup handshake abandoned its password write, so it owns no
        // updater yet and nobody else can release the writer it left behind.
        var hostKey = HostKey(46);
        var events = new List<string>();
        var input = new BlockingWriteStream();
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = command =>
            {
                var marker = PromptMarker(command);
                return new FakeChannel(
                    string.Empty,
                    gaiteOutputOnPassword: false,
                    exited: false,
                    errorSegments: new[]
                    {
                        new ScriptedStream.Segment(Encoding.UTF8.GetBytes(marker), Task.CompletedTask),
                    },
                    outputGate: Never,
                    standardInput: input,
                    events: events);
            },
            SocketAbortFactory = () => new FakeSocketAbort(() =>
            {
                events.Add("socket-release");
                input.Release();
            }),
        };
        using var cancelled = new CancellationTokenSource();

        var startup = OfflineUpdateSshSudo.StartUpdaterAsync(
            transport,
            "/bin/sh -c 'true'",
            useSudo: true,
            OfflineUpdateSshManagedConnection.ReadyMarker,
            "topsecret",
            TimeSpan.FromSeconds(5),
            4096,
            cancelled.Token);

        await input.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);

        Assert.Equal(1, transport.SocketAbortCount);
        Assert.True(
            events.IndexOf("socket-release") < events.IndexOf("channel-cancel"),
            "The socket must be released before the channel is cancelled: " + string.Join(",", events));
    }

    [Fact]
    public async Task SudoRun_PasswordWriteStallsPastTheCommandDeadline_ReportsTimeoutAndReleasesTheSocket()
    {
        // The fake stdin stands in for SSH.NET's password send under the
        // session write lock: it ignores the caller's token and only returns
        // once the exact socket is released. Nothing cancels this run, so the
        // command's own remaining budget is the only bound left.
        var hostKey = HostKey(48);
        var events = new List<string>();
        var input = new BlockingWriteStream();
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = command => PromptedSudoChannel(command, input, events),
            SocketAbortFactory = () => new FakeSocketAbort(() =>
            {
                events.Add("socket-release");
                input.Release();
            }),
        };

        var run = OfflineUpdateSshSudo.RunAsync(
            transport, "/bin/true", "topsecret", TimeSpan.FromSeconds(2), 4096, CancellationToken.None);

        await input.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));

        // The wait is strict but bounded: losing it means the deadline was
        // never applied, and the test fails instead of hanging the run.
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => run);

        Assert.Equal("The sudo command did not finish in time.", error.Message);
        Assert.DoesNotContain("topsecret", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.SocketAbortCount);
        Assert.True(
            events.IndexOf("socket-release") < events.IndexOf("channel-cancel"),
            "The socket must be released before the channel is cancelled: " + string.Join(",", events));
    }

    [Fact]
    public async Task SudoRun_PasswordWriteCancelledByCaller_ReportsCancellationNotTheDeadline()
    {
        // The same stalled send must still surface the caller's cancellation,
        // not the command's deadline: the budget here is far longer than the
        // test, so only the token can end the handshake.
        var hostKey = HostKey(49);
        var events = new List<string>();
        var input = new BlockingWriteStream();
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = command => PromptedSudoChannel(command, input, events),
            SocketAbortFactory = () => new FakeSocketAbort(() =>
            {
                events.Add("socket-release");
                input.Release();
            }),
        };
        using var cancelled = new CancellationTokenSource();

        var run = OfflineUpdateSshSudo.RunAsync(
            transport, "/bin/true", "topsecret", TimeSpan.FromSeconds(30), 4096, cancelled.Token);

        await input.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, transport.SocketAbortCount);
        Assert.True(
            events.IndexOf("socket-release") < events.IndexOf("channel-cancel"),
            "The socket must be released before the channel is cancelled: " + string.Join(",", events));
    }

    [Fact]
    public async Task StartUpdater_PasswordFlushStallsPastTheStartupDeadline_ReportsTimeoutAndReleasesTheSocket()
    {
        // The write reaches the peer but the flush never returns on its own.
        // With no caller cancellation the startup budget must still end the
        // handshake, and the socket is released before the channel is
        // cancelled so the stalled flush does not hold cleanup.
        var hostKey = HostKey(50);
        var events = new List<string>();
        var input = new BlockingFlushStream();
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = command => PromptedSudoChannel(command, input, events, outputGate: Never),
            SocketAbortFactory = () => new FakeSocketAbort(() =>
            {
                events.Add("socket-release");
                input.Release();
            }),
        };

        var startup = OfflineUpdateSshSudo.StartUpdaterAsync(
            transport,
            "/bin/sh -c 'true'",
            useSudo: true,
            OfflineUpdateSshManagedConnection.ReadyMarker,
            "topsecret",
            TimeSpan.FromSeconds(2),
            4096,
            CancellationToken.None);

        await input.FlushStarted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(startup, await Task.WhenAny(startup, Task.Delay(TimeSpan.FromSeconds(10))));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => startup);

        Assert.Equal("The tablet did not start the offline updater in time.", error.Message);
        Assert.DoesNotContain("topsecret", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.SocketAbortCount);
        Assert.True(
            events.IndexOf("socket-release") < events.IndexOf("channel-cancel"),
            "The socket must be released before the channel is cancelled: " + string.Join(",", events));
    }

    [Fact]
    public async Task SudoRun_CompletedProbe_KeepsTheTransportAndNeverReleasesTheSocket()
    {
        // A probe that completed, even with its own exit status, must leave the
        // reusable session alone: only the disposable command is released.
        var transport = new FakeTransport(HostKey(47))
        {
            ChannelFactory = _ => new FakeChannel("installed", gaiteOutputOnPassword: false, exited: true),
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
        Assert.Equal(0, transport.SocketAbortCount);
        Assert.Equal(0, transport.DisposeCount);
        Assert.True(transport.LastChannel!.Disposed);
    }

    private static byte[] Payload(int length, byte seed)
    {
        var data = new byte[length];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)((seed * 131 + index * 17 + 1) & 0xff);
        }

        return data;
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

        /// <summary>
        /// Returns null to stand in for a transport that cannot resolve the
        /// authenticated socket; the default hands out a controllable guard.
        /// </summary>
        internal Func<IOfflineUpdateSshUploadGuard?> UploadGuardFactory { get; init; } =
            () => new FakeUploadGuard();

        internal int UploadGuardCount { get; private set; }

        internal TimeSpan? LastUploadSendTimeout { get; private set; }

        /// <summary>
        /// Teardown handle for the same socket. The default records the call
        /// and does nothing, so an untouched session stays healthy.
        /// </summary>
        internal Func<IOfflineUpdateSshSocketAbort?> SocketAbortFactory { get; init; } =
            () => new FakeSocketAbort();

        internal int SocketAbortCount { get; private set; }

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

        public IOfflineUpdateSshUploadGuard? TryCreateUploadGuard(TimeSpan sendTimeout)
        {
            UploadGuardCount++;
            LastUploadSendTimeout = sendTimeout;
            return UploadGuardFactory();
        }

        public IOfflineUpdateSshSocketAbort? TryCreateSocketAbort()
        {
            SocketAbortCount++;
            return SocketAbortFactory();
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    /// <summary>
    /// Controllable upload guard. The optional abort action stands in for the
    /// real socket shutdown breaking a blocked send.
    /// </summary>
    private sealed class FakeUploadGuard : IOfflineUpdateSshUploadGuard
    {
        private readonly Action? _onAbort;
        private int _abortCount;
        private int _disposed;

        internal FakeUploadGuard(Action? onAbort = null) => _onAbort = onAbort;

        internal int AbortCount => Volatile.Read(ref _abortCount);

        internal bool Disposed => Volatile.Read(ref _disposed) != 0;

        public bool IsAborted => AbortCount > 0;

        public void Abort()
        {
            if (Interlocked.Increment(ref _abortCount) == 1)
                _onAbort?.Invoke();
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    /// <summary>
    /// Bare teardown handle. It has no send timeout to restore, and its optional
    /// action stands in for the real socket shutdown breaking a blocked send.
    /// </summary>
    private sealed class FakeSocketAbort : IOfflineUpdateSshSocketAbort
    {
        private readonly Action? _onAbort;
        private int _abortCount;

        internal FakeSocketAbort(Action? onAbort = null) => _onAbort = onAbort;

        internal int AbortCount => Volatile.Read(ref _abortCount);

        public bool IsAborted => AbortCount > 0;

        public void Abort()
        {
            if (Interlocked.Increment(ref _abortCount) == 1)
                _onAbort?.Invoke();
        }
    }

    private sealed class FakeChannel : IOfflineUpdateSshChannel
    {
        private readonly RecordingInput _input;
        private readonly Stream _standardInput;
        private readonly ScriptedStream _output;
        private readonly ScriptedStream _error;
        private readonly TaskCompletionSource<bool> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _exitStatus;
        private readonly string _errorTail;
        private readonly bool _waitForExitIgnoresTimeout;
        private readonly List<string>? _events;

        internal FakeChannel(
            string output,
            bool gaiteOutputOnPassword,
            bool exited,
            int exitStatus = 0,
            string errorTail = "",
            IReadOnlyList<ScriptedStream.Segment>? errorSegments = null,
            bool exitOnFirstLine = false,
            Task? outputGate = null,
            bool inputClosed = false,
            TaskCompletionSource<bool>? errorReadStarted = null,
            TaskCompletionSource<bool>? stdoutEnded = null,
            IReadOnlyList<ScriptedStream.Segment>? outputSegments = null,
            bool exitOnInputClosed = false,
            Stream? standardInput = null,
            bool waitForExitIgnoresTimeout = false,
            List<string>? events = null)
        {
            _exitStatus = exitStatus;
            _errorTail = errorTail;
            _events = events;
            _input = new RecordingInput(inputClosed);
            _standardInput = standardInput ?? _input;
            _waitForExitIgnoresTimeout = waitForExitIgnoresTimeout;
            var effectiveGate = outputGate ??
                (gaiteOutputOnPassword ? _input.FirstLineWritten : Task.CompletedTask);
            _output = new ScriptedStream(
                outputSegments ?? new[]
                {
                    new ScriptedStream.Segment(Encoding.UTF8.GetBytes(output), effectiveGate),
                },
                endOfStream: stdoutEnded);

            var segments = errorSegments ?? Array.Empty<ScriptedStream.Segment>();
            if (stdoutEnded is not null)
            {
                // Deliver every stderr segment only after stdout reached end of
                // stream: the ordering a real command produces when it closes
                // the output pipe before its last diagnostic bytes are copied.
                segments = segments
                    .Select(segment => new ScriptedStream.Segment(
                        segment.Data,
                        segment.Gate is null ? stdoutEnded.Task : Task.WhenAll(segment.Gate, stdoutEnded.Task)))
                    .ToArray();
            }

            _error = new ScriptedStream(segments, _input.FirstLineWritten, errorReadStarted);
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

            if (exitOnInputClosed)
            {
                // A real `cat` exits when its standard input reaches EOF.
                _ = Task.Run(async () =>
                {
                    await _input.InputClosed;
                    _exit.TrySetResult(true);
                });
            }
        }

        public Stream StandardInput => _standardInput;

        public Stream StandardOutput => _output;

        public Stream StandardError => _error;

        public bool HasExited => _exit.Task.IsCompleted && _exit.Task.Result;

        public int ExitStatus => _exitStatus;

        internal bool Disposed { get; private set; }

        internal byte[] InputBytes => _input.ToArray();

        internal string InputText => Encoding.UTF8.GetString(InputBytes);

        public bool WaitForExit(TimeSpan timeout) =>
            _exit.Task.Wait(_waitForExitIgnoresTimeout ? Timeout.InfiniteTimeSpan : timeout) &&
            _exit.Task.Result;

        public void Cancel()
        {
            _events?.Add("channel-cancel");
            _exit.TrySetResult(false);
        }

        public void Dispose()
        {
            Cancel();
            _events?.Add("channel-dispose");
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
        private readonly TaskCompletionSource<bool> _inputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal RecordingInput(bool closed = false) => _closed = closed;

        internal Task FirstLineWritten => _firstLine.Task;

        internal Task InputClosed => _inputClosed.Task;

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

        protected override void Dispose(bool disposing)
        {
            _inputClosed.TrySetResult(true);
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
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
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A standard input whose write never returns on its own once armed,
    /// standing in for SSH.NET's synchronous socket send when the peer stops
    /// reading. The cancellation token is deliberately ignored: only the
    /// upload guard can end the write, exactly like a real socket send under
    /// the write lock. A stream that starts unarmed is an ordinary sink, so a
    /// fixture can finish the connect handshake and arm the stall only for the
    /// exchange it means to interrupt.
    /// </summary>
    private sealed class BlockingWriteStream : Stream
    {
        private readonly TaskCompletionSource<bool> _writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        internal BlockingWriteStream(bool armed = true) => _armed = armed ? 1 : 0;

        internal Task WriteStarted => _writeStarted.Task;

        /// <summary>Engages the stall; every write before this one completed.</summary>
        internal void Arm() => Volatile.Write(ref _armed, 1);

        internal void Release() => _released.TrySetResult(true);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 0)
                return;

            _writeStarted.TrySetResult(true);
            await _released.Task.ConfigureAwait(false);
            throw new IOException("The transport connection has been closed.");
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// A standard input whose write completes but whose flush never returns on
    /// its own, standing in for SSH.NET flushing the password while it holds
    /// the session write lock. The token is deliberately ignored: only the
    /// socket release ends the flush.
    /// </summary>
    private sealed class BlockingFlushStream : Stream
    {
        private readonly TaskCompletionSource<bool> _flushStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task FlushStarted => _flushStarted.Task;

        internal void Release() => _released.TrySetResult(true);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            _flushStarted.TrySetResult(true);
            return _released.Task;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// A stream that ignores cancellation and hands back its bytes anyway, so
    /// a test can prove the upload re-checks the token after the read instead
    /// of relying on the stream to honour it.
    /// </summary>
    private sealed class CancelledButDataStream : Stream
    {
        private readonly byte[] _data;
        private int _offset;

        internal CancelledButDataStream(byte[] data) => _data = data;

        internal int ReadCount { get; private set; }

        internal int BytesReturned { get; private set; }

        internal bool TokenWasCancelledOnRead { get; private set; }

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
            ReadCount++;
            TokenWasCancelledOnRead |= cancellationToken.IsCancellationRequested;
            await Task.Yield();
            if (buffer.Length == 0 || _offset >= _data.Length)
                return 0;

            var count = Math.Min(buffer.Length, _data.Length - _offset);
            _data.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            BytesReturned += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ScriptedStream : Stream
    {
        internal readonly record struct Segment(byte[] Data, Task? Gate);

        private readonly Queue<Segment> _segments;
        private readonly Task _defaultGate;
        private readonly TaskCompletionSource<bool>? _readStarted;
        private readonly TaskCompletionSource<bool>? _endOfStream;
        private MemoryStream? _current;

        internal ScriptedStream(
            IEnumerable<Segment> segments,
            Task? defaultGate = null,
            TaskCompletionSource<bool>? readStarted = null,
            TaskCompletionSource<bool>? endOfStream = null)
        {
            _segments = new Queue<Segment>(segments);
            _defaultGate = defaultGate ?? Task.CompletedTask;
            _readStarted = readStarted;
            _endOfStream = endOfStream;
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
            _readStarted?.TrySetResult(true);
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
                {
                    _endOfStream?.TrySetResult(true);
                    return 0;
                }

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
