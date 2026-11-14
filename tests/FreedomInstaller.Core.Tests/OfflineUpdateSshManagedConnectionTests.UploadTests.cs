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
}
