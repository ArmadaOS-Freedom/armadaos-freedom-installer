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
    public async Task Dispose_HealthyRpc_DisconnectsInOrderAndNeverReleasesTheSocket()
    {
        // No exchange is in flight and none was interrupted, so teardown is an
        // ordinary cancel-and-disconnect: the session socket stays untouched and
        // only the channel and the transport are released, exactly once. The
        // abort handle is still captured before cleanup starts, because only a
        // cleanup that stalls needs it.
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

        Assert.Equal(1, transport.SocketAbortCaptureCount);
        Assert.Equal(0, transport.SocketAbortCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.True(channel.Disposed);
        Assert.Contains("channel-cancel", events);
    }

    [Fact]
    public async Task Dispose_IdleSessionWhoseChannelCancelIsBlocked_IsReleasedByTheWatchdog()
    {
        // Nothing has failed here: the idle RPC session is healthy and no
        // exchange is in flight. SSH.NET's 15 second keepalive send still holds
        // the session write lock, so the updater's synchronous channel cancel
        // blocks inside the graceful cleanup. Only the watchdog may end that:
        // DisposeAsync must return a task instead of blocking on the caller's
        // thread, the captured socket must be the one released, and the
        // cleanup must still release the transport exactly once.
        var hostKey = HostKey(51);
        var events = new List<string>();
        var cancelStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new FakeChannel(
            ReadyOutput(),
            gaiteOutputOnPassword: false,
            exited: false,
            events: events,
            cancelGate: cancelGate.Task,
            cancelStarted: cancelStarted);
        var transport = new FakeTransport(hostKey)
        {
            ChannelFactory = _ => channel,
            SocketAbortFactory = () => new FakeSocketAbort(() =>
            {
                events.Add("socket-release");
                cancelGate.TrySetResult(true);
            }),
        };

        var connection = await OfflineUpdateSshManagedConnection.ConnectCoreAsync(
            Options(hostKey),
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            Factory(transport),
            TimeSpan.FromSeconds(2));
        Assert.False(connection.Rpc.IsPoisoned);

        var dispose = connection.DisposeAsync().AsTask();

        // The cancel is already blocked inside the cleanup and cannot return
        // until the socket is released, so DisposeAsync had to hand that work
        // off the caller's thread and no release has happened yet.
        await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, transport.SocketAbortCount);

        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, transport.SocketAbortCaptureCount);
        Assert.Equal(1, transport.SocketAbortCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.True(channel.Disposed);
        Assert.Contains("socket-release", events);
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

        AssertSocketReleasedBeforeChannelCancel(events);
    }

    [Fact]
    public async Task Dispose_WhileAnRpcSendIsStillBlocked_ReleasesTheSocketBeforeCancellingTheChannel()
    {
        // Nothing has failed here: the exchange is healthy and merely waits in
        // a send the peer stopped reading. The decision therefore has to come
        // from the exchange that still holds the gate, not from a poison flag,
        // and the socket has to be released before the channel cancel queues
        // behind the same writer.
        var hostKey = HostKey(47);
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

        input.Arm();
        var query = connection.Rpc.PrepareAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await input.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(connection.Rpc.IsPoisoned);

        var dispose = connection.DisposeAsync().AsTask();
        var winner = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(dispose, winner);
        await dispose;

        // The abandoned exchange ends with teardown. Which of its guard
        // clauses reports that is a race, so only the failure is asserted.
        await Assert.ThrowsAnyAsync<Exception>(() => query);

        Assert.Equal(1, transport.SocketAbortCount);
        Assert.Equal(1, transport.DisposeCount);
        Assert.True(channel.Disposed);
        AssertSocketReleasedBeforeChannelCancel(events);
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
        AssertSocketReleasedBeforeChannelCancel(events);
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
        AssertSocketReleasedBeforeChannelCancel(events);
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
        AssertSocketReleasedBeforeChannelCancel(events);
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
        AssertSocketReleasedBeforeChannelCancel(events);
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
        AssertSocketReleasedBeforeChannelCancel(events);
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
}
