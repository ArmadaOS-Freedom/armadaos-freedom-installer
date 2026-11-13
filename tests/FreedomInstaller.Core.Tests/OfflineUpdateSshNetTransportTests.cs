#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Renci.SshNet;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// One managed connection attempt captures the peer key without a credential,
/// asks for approval only after that handshake is gone, and then trusts nothing
/// but the captured key. These tests drive that decision directly: no socket,
/// no server and no UI thread, so a slow approval can be simulated without a
/// network timeout in the way.
/// </summary>
public sealed class OfflineUpdateSshNetTransportTests
{
    private static byte[] Blob(byte seed)
    {
        var wire = new byte[51];
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0, 4), 11);
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(wire.AsSpan(4, 11));
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(15, 4), 32);
        wire.AsSpan(19).Fill(seed);
        return wire;
    }

    private static OfflineUpdateSshHostKey Key(byte seed) => new("ssh-ed25519", Blob(seed));

    [Fact]
    public void CapturedKeyIsNotTrustedBeforeTheApprovalReturns()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(1));

        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(1)));
    }

    [Fact]
    public void AKeyApprovedAfterALongPauseIsTrustedAndLaterPresentationsNeverAskAgain()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(2));
        var calls = 0;
        OfflineUpdateSshHostKey? approved = null;

        gate.Approve(
            key =>
            {
                calls++;
                approved = key;
                // Stands in for a dialog answered long after the discovery
                // handshake was closed; nothing network-facing is waiting.
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
                return true;
            },
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.NotNull(approved);
        Assert.Equal("ssh-ed25519", approved.Value.KeyType);
        Assert.Equal(gate.Captured.KeyBlob, approved.Value.KeyBlob);
        Assert.True(gate.IsApproved);

        // The authenticated handshake and every later rekey ask the same
        // question, and all of them get the recorded answer.
        Assert.True(gate.Accepts("ssh-ed25519", Blob(2)));
        Assert.True(gate.Accepts("ssh-ed25519", Blob(2)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AKeyReplacedBetweenApprovalAndAuthenticationIsRejected()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(4));
        gate.Approve(_ => true, CancellationToken.None);

        Assert.False(gate.Accepts("ssh-ed25519", Blob(5)));
        Assert.False(gate.Accepts("ssh-rsa", Blob(4)));
        Assert.False(gate.Accepts(null, Blob(4)));
        Assert.False(gate.Accepts("ssh-ed25519", new byte[50]));
        Assert.True(gate.Accepts("ssh-ed25519", Blob(4)));
    }

    [Fact]
    public void ADeniedKeyIsNotTrusted()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(3));
        var calls = 0;

        var error = Assert.Throws<OfflineUpdateSshHostKeyRejectedException>(() =>
            gate.Approve(
                _ =>
                {
                    calls++;
                    return false;
                },
                CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Null(error.InnerException);
        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(3)));
    }

    [Fact]
    public void ATrustStoreFailureRejectsTheKeyWithoutTrustingIt()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(6));
        var failure = new IOException("trust store is unavailable");

        var error = Assert.Throws<OfflineUpdateSshHostKeyRejectedException>(() =>
            gate.Approve(_ => throw failure, CancellationToken.None));

        Assert.Same(failure, error.InnerException);
        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(6)));
    }

    [Fact]
    public void ACancellationAfterTheDialogReturnsTrueStillLeavesTheKeyUntrusted()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(7));
        using var cts = new CancellationTokenSource();
        var calls = 0;

        Assert.Throws<OperationCanceledException>(() =>
            gate.Approve(
                _ =>
                {
                    calls++;
                    // The attempt was abandoned while the dialog was open, so a
                    // late "yes" must not authorize any handshake.
                    cts.Cancel();
                    return true;
                },
                cts.Token));

        Assert.Equal(1, calls);
        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(7)));
    }

    [Fact]
    public void ACancellationFromTheDialogPropagatesWithoutApproval()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(8));

        Assert.Throws<OperationCanceledException>(() =>
            gate.Approve(_ => throw new OperationCanceledException(), CancellationToken.None));

        Assert.False(gate.IsApproved);
        Assert.False(gate.Accepts("ssh-ed25519", Blob(8)));
    }

    [Fact]
    public void APreCancelledAttemptNeverAsksForApproval()
    {
        var gate = new OfflineUpdateSshHostKeyGate(Key(9));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;

        Assert.Throws<OperationCanceledException>(() =>
            gate.Approve(
                _ =>
                {
                    calls++;
                    return true;
                },
                cts.Token));

        Assert.Equal(0, calls);
        Assert.False(gate.IsApproved);
    }

    [Fact]
    public void UsbRouteChecksTheSourceTheOsSelectsForTheNumericDestination()
    {
        IPAddress? destination = null;
        var transport = new OfflineUpdateSshNetTransport(
            UsbOptions(),
            host =>
            {
                destination = host;
                return IPAddress.Parse("172.16.42.2");
            });

        transport.VerifySelectedUsbSource();

        Assert.Equal(IPAddress.Parse("172.16.42.10"), destination);
    }

    [Fact]
    public void UsbRouteRejectsADifferentSelectedSource()
    {
        var transport = new OfflineUpdateSshNetTransport(UsbOptions(), _ => IPAddress.Parse("172.16.42.3"));

        Assert.Throws<IOException>(() => transport.VerifySelectedUsbSource());
    }

    [Fact]
    public void UsbRouteRejectsAnUnresolvedSelectedSourceWithoutFallback()
    {
        var transport = new OfflineUpdateSshNetTransport(UsbOptions(), _ => null);

        Assert.Throws<IOException>(() => transport.VerifySelectedUsbSource());
    }

    [Fact]
    public void UsbRouteIsReevaluatedBeforeEveryHandshake()
    {
        // First call stands in for the host-key preflight, the second for the
        // password handshake. A route change between them must be rejected
        // instead of reusing the first decision.
        var calls = 0;
        var transport = new OfflineUpdateSshNetTransport(
            UsbOptions(),
            _ =>
            {
                calls++;
                return IPAddress.Parse(calls == 1 ? "172.16.42.2" : "192.168.1.5");
            });

        transport.VerifySelectedUsbSource();
        Assert.Throws<IOException>(() => transport.VerifySelectedUsbSource());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WifiConnectionsNeverResolveAUsbSource()
    {
        var options = new OfflineUpdateSshPasswordOptions(
            "device.example", 2222, "armada", "topsecret", Path.GetTempPath());
        var calls = 0;
        var transport = new OfflineUpdateSshNetTransport(
            options,
            _ =>
            {
                calls++;
                return null;
            });

        transport.VerifySelectedUsbSource();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void AClientOfferedAfterDisposalIsNotOwned()
    {
        var lease = new OfflineUpdateSshClientLease();
        var late = new object();

        Assert.Null(lease.Release());

        Assert.False(lease.TryAcquire(late));
        Assert.True(lease.IsDisposed);
        Assert.Null(lease.Current);
    }

    [Fact]
    public void DisposalTransfersTheOwnedClientExactlyOnce()
    {
        var lease = new OfflineUpdateSshClientLease();
        var client = new object();

        Assert.True(lease.TryAcquire(client));
        Assert.Same(client, lease.Current);
        Assert.Same(client, lease.Release());
        Assert.Null(lease.Release());
    }

    [Fact]
    public void AcquiringTwiceNeverReplacesTheInstalledClient()
    {
        var lease = new OfflineUpdateSshClientLease();
        var first = new object();
        var second = new object();

        Assert.True(lease.TryAcquire(first));
        Assert.False(lease.TryAcquire(second));
        Assert.Same(first, lease.Current);
    }

    [Fact]
    public void ThePinnedSshNetMetadataStillExposesTheSessionSocket()
    {
        // The upload abort depends on BaseClient.Session and Session._socket.
        // A package bump that renames either member must fail here instead of
        // silently losing the abort capability.
        Assert.True(OfflineUpdateSshNetSessionSocket.HasPinnedMembers);
    }

    [Fact]
    public void AClientBeforeItsHandshakeHasNoCapturableSocket()
    {
        // Nothing is authenticated yet, so there is no socket to abort and the
        // upload must fail closed rather than start unguarded.
        using var client = new SshClient(new ConnectionInfo(
            "device.example",
            2222,
            "armada",
            new PasswordAuthenticationMethod("armada", "topsecret")));

        Assert.Null(OfflineUpdateSshNetSessionSocket.TryGetSocket(client));
    }

    [Fact]
    public void TheUploadGuardBoundsTheSendTimeoutAndRestoresItOnDisposal()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.Equal(0, socket.SendTimeout);

        var guard = OfflineUpdateSshNetUploadGuard.TryCreate(socket, TimeSpan.FromSeconds(3));

        Assert.NotNull(guard);
        Assert.False(guard!.IsAborted);
        Assert.Equal(3000, socket.SendTimeout);

        guard.Dispose();

        Assert.Equal(0, socket.SendTimeout);
        Assert.False(guard.IsAborted);
    }

    [Fact]
    public void AbortingTheUploadGuardClosesTheCapturedSocketExactlyOnce()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var guard = OfflineUpdateSshNetUploadGuard.TryCreate(socket, TimeSpan.FromSeconds(3));

        Assert.NotNull(guard);
        guard!.Abort();
        guard.Abort();

        Assert.True(guard.IsAborted);

        // Shutdown/Close is what actually breaks a blocked send; the guard does
        // not touch SSH.NET's write path, so the local socket is unusable and
        // a second abort is harmless.
        Assert.Throws<ObjectDisposedException>(() => socket.Send(new byte[] { 1 }));
        guard.Dispose();
        socket.Dispose();
    }

    [Fact]
    public void AClosedSocketCannotBeGuarded()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Dispose();

        Assert.Null(OfflineUpdateSshNetUploadGuard.TryCreate(socket, TimeSpan.FromSeconds(3)));
    }

    private static OfflineUpdateSshPasswordOptions UsbOptions() =>
        new("172.16.42.10", 2222, "armada", "topsecret", Path.GetTempPath(), "172.16.42.2");

    [Fact]
    public void ARunningCommandIsNeverTreatedAsCompleted()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusReads = 0;

        Assert.False(OfflineUpdateSshNetTransport.HasCompletedWithStatus(
            pending.Task,
            () =>
            {
                statusReads++;
                return 0;
            }));

        Assert.Equal(0, statusReads);
    }

    [Fact]
    public void ACommandThatFinishedWithAStatusIsTreatedAsCompleted()
    {
        Assert.True(OfflineUpdateSshNetTransport.HasCompletedWithStatus(Task.CompletedTask, () => 0));
    }

    [Fact]
    public void ACommandWithoutAnExitStatusIsNotTreatedAsCompleted()
    {
        Assert.False(OfflineUpdateSshNetTransport.HasCompletedWithStatus(Task.CompletedTask, () => null));
    }

    [Fact]
    public void ADisconnectedCommandIsNotTreatedAsCompleted()
    {
        var execution = Task.FromException(new IOException("connection lost"));
        try
        {
            Assert.False(OfflineUpdateSshNetTransport.HasCompletedWithStatus(execution, () => 0));
        }
        finally
        {
            // Observe the fault so the test does not leave it unobserved.
            _ = execution.Exception;
        }
    }

    [Fact]
    public void ACancelledCommandIsNotTreatedAsCompleted()
    {
        var execution = Task.FromCanceled(new CancellationToken(canceled: true));
        Assert.False(OfflineUpdateSshNetTransport.HasCompletedWithStatus(execution, () => 0));
    }

    [Fact]
    public void AStatusAccessorFailureIsNotTreatedAsCompleted()
    {
        Assert.False(OfflineUpdateSshNetTransport.HasCompletedWithStatus(
            Task.CompletedTask,
            () => throw new ObjectDisposedException("command")));
    }

    [Fact]
    public void AFinishedCommandCanLoseStdinWithoutFailingTheChannel()
    {
        // Passwordless sudo routinely exits before stdin exists; that single
        // lifecycle failure is the only one a fast completion may absorb.
        Assert.True(OfflineUpdateSshNetTransport.IsCompletedStdinLifecycleFailure(
            new InvalidOperationException("The command is not running."),
            Task.CompletedTask,
            () => 0));
    }

    [Fact]
    public void ARunningCommandNeverAbsorbsAStdinFailure()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.False(OfflineUpdateSshNetTransport.IsCompletedStdinLifecycleFailure(
            new InvalidOperationException("The command is not running."),
            pending.Task,
            () => 0));
    }

    [Fact]
    public void AFinishedCommandWithoutAnExitStatusNeverAbsorbsAStdinFailure()
    {
        Assert.False(OfflineUpdateSshNetTransport.IsCompletedStdinLifecycleFailure(
            new InvalidOperationException("The command is not running."),
            Task.CompletedTask,
            () => null));
    }

    [Fact]
    public void AFaultedCommandNeverAbsorbsAStdinFailure()
    {
        var execution = Task.FromException(new IOException("connection lost"));
        try
        {
            Assert.False(OfflineUpdateSshNetTransport.IsCompletedStdinLifecycleFailure(
                new InvalidOperationException("The command is not running."),
                execution,
                () => 0));
        }
        finally
        {
            _ = execution.Exception;
        }
    }

    [Fact]
    public void ACancelledCommandNeverAbsorbsAStdinFailure()
    {
        var execution = Task.FromCanceled(new CancellationToken(canceled: true));

        Assert.False(OfflineUpdateSshNetTransport.IsCompletedStdinLifecycleFailure(
            new InvalidOperationException("The command is not running."),
            execution,
            () => 0));
    }

    [Fact]
    public void ATransportFailureWhileAcquiringStdinIsNeverAbsorbed()
    {
        Assert.False(OfflineUpdateSshNetTransport.IsCompletedStdinLifecycleFailure(
            new IOException("The SSH pipe was closed."),
            Task.CompletedTask,
            () => 0));
    }

    [Fact]
    public void ADisposedCommandIsNeverTreatedAsAFastCompletion()
    {
        // ObjectDisposedException derives from InvalidOperationException, but a
        // disposed command is not a lifecycle race and must still be reported.
        Assert.False(OfflineUpdateSshNetTransport.IsCompletedStdinLifecycleFailure(
            new ObjectDisposedException("command"),
            Task.CompletedTask,
            () => 0));
    }

    [Fact]
    public async Task OutputBufferedBeforeThePumpStartsIsCapturedInFull()
    {
        // A command that finished before the pump ran left its raw bytes in
        // the pipe; reading them must behave like reading a live stream.
        var payload = Encoding.UTF8.GetBytes("sudo: password accepted\nhost: tablet\n");
        var tail = new OfflineUpdateBoundedText(1024);

        await OfflineUpdateSshNetTransport.PumpAsync(
            new MemoryStream(payload, writable: false), tail, CancellationToken.None);

        Assert.Equal(Encoding.UTF8.GetString(payload), tail.Snapshot());
    }

    [Fact]
    public async Task OutputArrivingInSmallChunksIsCapturedInFull()
    {
        // A command that streams slowly takes the same reader and must end up
        // with exactly the bytes it produced, not a truncated tail.
        var payload = Encoding.UTF8.GetBytes("first line\nsecond line\nthird line\n");
        var tail = new OfflineUpdateBoundedText(1024);

        await OfflineUpdateSshNetTransport.PumpAsync(
            new ChunkedStream(payload, 3), tail, CancellationToken.None);

        Assert.Equal(Encoding.UTF8.GetString(payload), tail.Snapshot());
    }

    [Fact]
    public async Task MultiByteTextSplitAcrossReadsIsDecodedAsOneStream()
    {
        // A byte-at-a-time stream splits every multi-byte character; the pump
        // must carry the partial sequence forward instead of decoding each
        // read on its own.
        var payload = Encoding.UTF8.GetBytes("h\u00e9llo \u2192 tablet\n");
        var tail = new OfflineUpdateBoundedText(1024);

        await OfflineUpdateSshNetTransport.PumpAsync(
            new ChunkedStream(payload, 1), tail, CancellationToken.None);

        Assert.Equal("h\u00e9llo \u2192 tablet\n", tail.Snapshot());
    }

    [Fact]
    public async Task ACarriedOverPrefixPlusAFullReadThatDecodesToMoreCharsIsNotLost()
    {
        // The first packet ends inside a four-byte character, so the decoder
        // keeps a three-byte prefix. The next read is a full 4096 bytes that
        // completes that character (a surrogate pair) and continues with 4095
        // ASCII bytes: 4097 UTF-16 code units from one 4096-byte read. A
        // destination sized from the byte count alone would throw and drop the
        // output, so the whole stream has to arrive intact.
        var leading = new byte[] { 0xF0, 0x9F, 0x98 };
        var full = new byte[4096];
        full[0] = 0x80;
        Array.Fill(full, (byte)'A', 1, 4095);

        var expected = "\U0001F600" + new string('A', 4095);
        var tail = new OfflineUpdateBoundedText(8192);

        await OfflineUpdateSshNetTransport.PumpAsync(
            new SequenceStream(new[] { leading, full }), tail, CancellationToken.None);

        Assert.Equal(4097, expected.Length);
        Assert.Equal(expected, tail.Snapshot());
        Assert.EndsWith(new string('A', 4095), tail.Snapshot());
    }

    [Fact]
    public async Task ATruncatedFinalSequenceIsReplacedAtEndOfStream()
    {
        // The pump flushes its decoder when the stream ends, so a final
        // character cut in half is reported instead of silently dropped.
        var payload = new byte[] { (byte)'t', (byte)'a', (byte)'i', (byte)'l', 0xE2 };
        var tail = new OfflineUpdateBoundedText(1024);

        await OfflineUpdateSshNetTransport.PumpAsync(
            new ChunkedStream(payload, 4096), tail, CancellationToken.None);

        Assert.Equal("tail\uFFFD", tail.Snapshot());
    }

    [Fact]
    public async Task ClosedStandardInputEndsReadsAndRefusesWrites()
    {
        var input = OfflineUpdateSshNetTransport.ClosedStandardInput.Instance;

        Assert.False(input.CanRead);
        Assert.False(input.CanWrite);
        Assert.Equal(0, input.Read(new byte[1], 0, 1));
        Assert.Equal(0, await input.ReadAsync(new byte[1].AsMemory()));
        Assert.Throws<IOException>(() => input.Write(new byte[] { 1 }, 0, 1));
        Assert.Throws<IOException>(() => input.Write(new ReadOnlySpan<byte>(new byte[] { 1 })));
        await Assert.ThrowsAsync<IOException>(() => input.WriteAsync(new byte[] { 1 }, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(async () =>
            await input.WriteAsync(new ReadOnlyMemory<byte>(new byte[] { 1 })));

        // Flushing a closed pipe is harmless; only a write is refused.
        input.Flush();
        await input.FlushAsync();
        input.Dispose();
    }

    /// <summary>
    /// Hands out a pre-decided sequence of pieces, standing in for packets whose
    /// boundaries fall inside a multi-byte character and whose later piece is a
    /// full read buffer.
    /// </summary>
    private sealed class SequenceStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _chunkIndex;
        private int _chunkOffset;

        internal SequenceStream(byte[][] chunks)
        {
            _chunks = chunks;
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

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_chunkIndex >= _chunks.Length)
                return 0;

            var chunk = _chunks[_chunkIndex];
            var count = Math.Min(buffer.Length, chunk.Length - _chunkOffset);
            chunk.AsSpan(_chunkOffset, count).CopyTo(buffer);
            _chunkOffset += count;
            if (_chunkOffset == chunk.Length)
            {
                _chunkIndex++;
                _chunkOffset = 0;
            }

            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hands out a payload in fixed-size pieces, standing in for a command whose
    /// output arrives over time instead of in one buffered burst.
    /// </summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _payload;
        private readonly int _chunkSize;
        private int _position;

        internal ChunkedStream(byte[] payload, int chunkSize)
        {
            _payload = payload;
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _payload.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, _chunkSize), _payload.Length - _position);
            _payload.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
