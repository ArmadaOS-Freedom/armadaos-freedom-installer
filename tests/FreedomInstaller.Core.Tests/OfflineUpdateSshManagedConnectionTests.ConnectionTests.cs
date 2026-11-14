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

public sealed partial class OfflineUpdateSshManagedConnectionTests
{
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
}
