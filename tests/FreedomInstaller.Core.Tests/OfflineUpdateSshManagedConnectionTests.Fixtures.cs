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
