using System.Text;
using System.Text.Json;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateRpcClientTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task All_request_shapes_round_trip_with_monotonic_ids()
    {
        using var io = new IoStream();
        for (var id = 1; id <= 7; id++)
            ReplyOk(io, id);
        ReplyOk(io, 8, "{\"data\":\"AQI=\"}");
        await using var client = new OfflineUpdateRpcClient(io, io);

        var document = new byte[] { 0, 1, 2, 255 };
        var signature = new byte[] { 9, 8, 7 };
        var chunk = new byte[] { 3, 4, 5 };
        await client.GetInfoAsync();
        await client.BeginAsync(document, signature);
        await client.GetStatusAsync(Session);
        await client.UploadAsync(Session, 42, chunk);
        await client.SealAsync(Session);
        await client.PrepareAsync(Session);
        await client.ExecuteAsync(Session, Encoding.UTF8.GetBytes("{\"accepted\":true,\"note\":\"✓\"}"));
        Assert.Equal(new byte[] { 1, 2 }, await client.ExportAsync(Session, "logs/result.txt", 7, 2));

        var methods = new[] { "info", "begin", "status", "upload", "seal", "prepare", "execute", "export" };
        Assert.Equal(8, io.Frames.Count);
        for (var i = 0; i < methods.Length; i++)
        {
            var frame = Frame(io, i);
            Assert.Equal(1, frame.GetProperty("protocolVersion").GetInt32());
            Assert.Equal(i + 1, frame.GetProperty("id").GetInt32());
            Assert.Equal(methods[i], frame.GetProperty("method").GetString());
        }

        Assert.Equal(0, Count(Frame(io, 0).GetProperty("params")));
        var begin = Frame(io, 1).GetProperty("params");
        Assert.Equal(Convert.ToBase64String(document), begin.GetProperty("document").GetString());
        Assert.Equal(Convert.ToBase64String(signature), begin.GetProperty("signature").GetString());
        Assert.Equal(Session, Frame(io, 2).GetProperty("params").GetProperty("sessionId").GetString());
        var upload = Frame(io, 3).GetProperty("params");
        Assert.Equal(42, upload.GetProperty("offset").GetInt64());
        Assert.Equal(Convert.ToBase64String(chunk), upload.GetProperty("data").GetString());
        Assert.Equal(Session, Frame(io, 4).GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(Session, Frame(io, 5).GetProperty("params").GetProperty("sessionId").GetString());
        var acknowledgement = Frame(io, 6).GetProperty("params").GetProperty("acknowledgement");
        Assert.Equal(JsonValueKind.Object, acknowledgement.ValueKind);
        Assert.True(acknowledgement.GetProperty("accepted").GetBoolean());
        Assert.Equal("✓", acknowledgement.GetProperty("note").GetString());
        var export = Frame(io, 7).GetProperty("params");
        Assert.Equal("logs/result.txt", export.GetProperty("path").GetString());
        Assert.Equal(7, export.GetProperty("offset").GetInt64());
        Assert.Equal(2, export.GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task Preflight_uses_begin_field_names_without_a_session_id()
    {
        using var io = new IoStream();
        ReplyOk(io, 1, "{\"state\":\"preflight_passed\",\"model\":\"TB323FU\"}");
        await using var client = new OfflineUpdateRpcClient(io, io);

        var document = new byte[] { 1, 2, 3 };
        var signature = new byte[] { 4, 5 };
        var result = await client.PreflightAsync(document, signature);

        Assert.Equal("preflight_passed", result.GetProperty("state").GetString());
        var frame = Frame(io, 0);
        Assert.Equal("preflight", frame.GetProperty("method").GetString());
        var parameters = frame.GetProperty("params");
        Assert.Equal(Convert.ToBase64String(document), parameters.GetProperty("document").GetString());
        Assert.Equal(Convert.ToBase64String(signature), parameters.GetProperty("signature").GetString());
        Assert.False(parameters.TryGetProperty("sessionId", out _));
    }

    [Fact]
    public async Task Preflight_with_a_session_id_adds_only_that_field()
    {
        using var io = new IoStream();
        ReplyOk(io, 1, "{\"phase\":\"preflight_passed\",\"sessionId\":\"" + Session + "\"}");
        await using var client = new OfflineUpdateRpcClient(io, io);

        var document = new byte[] { 1, 2, 3 };
        var signature = new byte[] { 4, 5 };
        var result = await client.PreflightAsync(document, signature, Session);

        Assert.Equal("preflight_passed", result.GetProperty("phase").GetString());
        var parameters = Frame(io, 0).GetProperty("params");
        Assert.Equal(3, Count(parameters));
        Assert.Equal(Convert.ToBase64String(document), parameters.GetProperty("document").GetString());
        Assert.Equal(Convert.ToBase64String(signature), parameters.GetProperty("signature").GetString());
        Assert.Equal(Session, parameters.GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData("BAD")]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Preflight_rejects_an_invalid_session_id_without_writing(string sessionId)
    {
        using var io = new IoStream();
        await using var client = new OfflineUpdateRpcClient(io, io);

        Assert.ThrowsAny<ArgumentException>(() =>
        {
            _ = client.PreflightAsync(new byte[] { 1 }, new byte[] { 2 }, sessionId);
        });
        Assert.Empty(io.Frames);
    }

    [Fact]
    public async Task Carryover_and_fragmented_utf8_are_preserved()
    {
        using var io = new IoStream();
        var fragments = Ok(1, "{\"value\":\"A😀é\"}")
            .Select(value => new[] { value }).ToList();
        fragments[^1] = fragments[^1].Concat(Ok(2, "{\"next\":true}")).ToArray();
        io.Reply(fragments.ToArray());
        await using var client = new OfflineUpdateRpcClient(io, io);

        var first = await client.GetInfoAsync();
        var second = await client.GetStatusAsync(Session);

        Assert.Equal("A😀é", first.GetProperty("value").GetString());
        Assert.True(second.GetProperty("next").GetBoolean());
    }

    [Fact]
    public async Task Remote_error_is_typed_and_does_not_poison()
    {
        using var io = new IoStream();
        io.Reply(Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"id\":1,\"ok\":false,\"error\":{\"code\":\"SESSION_BUSY\",\"message\":\"busy\"}}\n"));
        ReplyOk(io, 2, "{\"ready\":true}");
        await using var client = new OfflineUpdateRpcClient(io, io);

        var error = await Assert.ThrowsAsync<OfflineUpdateRemoteException>(() => client.GetInfoAsync());
        Assert.Equal("SESSION_BUSY", error.Code);
        Assert.Equal("busy", error.Message);
        Assert.True((await client.GetInfoAsync()).GetProperty("ready").GetBoolean());
    }

    [Theory]
    [InlineData("{\"protocolVersion\":1,\"id\":2,\"ok\":true,\"result\":{}}\n")]
    [InlineData("{\"protocolVersion\":2,\"id\":1,\"ok\":true,\"result\":{}}\n")]
    [InlineData("{\"protocolVersion\":1,\"id\":1,\"id\":1,\"ok\":true,\"result\":{}}\n")]
    public async Task Malformed_response_poisons_without_a_later_write(string reply)
    {
        using var io = new IoStream();
        io.Reply(Encoding.UTF8.GetBytes(reply));
        await using var client = new OfflineUpdateRpcClient(io, io);

        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        Assert.Single(io.Frames);
    }

    [Fact]
    public async Task Invalid_utf8_poisons_without_a_later_write()
    {
        using var io = new IoStream();
        var reply = Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"id\":1,\"ok\":true,\"result\":{\"text\":\"")
            .Concat(new byte[] { 0xff })
            .Concat(Encoding.UTF8.GetBytes("\"}}\n")).ToArray();
        io.Reply(reply);
        await using var client = new OfflineUpdateRpcClient(io, io);

        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        Assert.Single(io.Frames);
    }

    [Fact]
    public async Task Unterminated_eof_poisons_without_a_later_write()
    {
        using var io = new IoStream();
        io.Reply(Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"id\":1"));
        io.End();
        await using var client = new OfflineUpdateRpcClient(io, io);

        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        Assert.Single(io.Frames);
    }

    [Fact]
    public async Task Oversized_frame_poisons_without_a_later_write()
    {
        using var io = new IoStream();
        var oversized = new byte[4 * 1024 * 1024];
        Array.Fill(oversized, (byte)' ');
        io.Reply(oversized);
        await using var client = new OfflineUpdateRpcClient(io, io);

        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        Assert.Single(io.Frames);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AQID")]
    public async Task Invalid_export_payload_poisons_before_another_request(string data)
    {
        using var io = new IoStream();
        await using var client = new OfflineUpdateRpcClient(io, io);

        var export = client.ExportAsync(Session, "file", 0, 2);
        await io.Written.Task;
        var queued = client.GetInfoAsync();

        io.Reply(Ok(1, JsonSerializer.Serialize(new { data })));

        await Assert.ThrowsAnyAsync<IOException>(() => export);
        await Assert.ThrowsAnyAsync<IOException>(() => queued);
        Assert.Single(io.Frames);
    }

    [Fact]
    public async Task Queued_cancellation_does_not_poison_active_request()
    {
        using var io = new IoStream();
        await using var client = new OfflineUpdateRpcClient(io, io);
        var active = client.GetInfoAsync();
        await io.Written.Task;
        using var cancelled = new CancellationTokenSource();
        var queued = client.GetStatusAsync(Session, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(client.IsPoisoned);

        ReplyOk(io, 1);
        await active;
        Assert.False(client.IsPoisoned);
        ReplyOk(io, 2);
        await client.PrepareAsync(Session);

        Assert.Equal(2, io.Frames.Count);
        Assert.Equal(2, Frame(io, 1).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Timeout_poisons_and_prevents_a_later_write()
    {
        using var io = new IoStream();
        await using var client = new OfflineUpdateRpcClient(io, io, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetInfoAsync());
        Assert.True(client.IsPoisoned);
        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        Assert.Single(io.Frames);
    }

    [Fact]
    public async Task Timeout_is_bounded_when_the_stream_ignores_the_token()
    {
        using var io = new IoStream(ignoreReadCancellation: true);
        await using var client = new OfflineUpdateRpcClient(io, io, TimeSpan.FromMilliseconds(50));

        var request = client.GetInfoAsync();
        var winner = await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(request, winner);

        await Assert.ThrowsAsync<TimeoutException>(() => request);
        Assert.True(client.IsPoisoned);
        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        Assert.Single(io.Frames);
    }

    [Fact]
    public async Task Inflight_cancellation_poisons_and_prevents_a_later_write()
    {
        using var io = new IoStream();
        await using var client = new OfflineUpdateRpcClient(io, io);
        using var cancelled = new CancellationTokenSource();
        var pending = client.GetInfoAsync(cancelled.Token);
        await io.Written.Task;
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(client.IsPoisoned);
        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        Assert.Single(io.Frames);
    }

    [Fact]
    public async Task Cancellation_is_bounded_when_the_stream_ignores_the_token_and_late_data_is_isolated()
    {
        using var io = new IoStream(ignoreReadCancellation: true);
        await using var client = new OfflineUpdateRpcClient(io, io);
        using var cancelled = new CancellationTokenSource();
        var pending = client.GetInfoAsync(cancelled.Token);
        await io.Written.Task;

        cancelled.Cancel();

        var winner = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(pending, winner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(client.IsPoisoned);
        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());

        // Let the abandoned physical read complete after the request already
        // failed. It must not be picked up by a later call or escape as an
        // unobserved fault.
        io.Reply(Ok(1));
        await io.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(client.IsPoisoned);
        await Assert.ThrowsAnyAsync<IOException>(() => client.GetInfoAsync());
        Assert.Single(io.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_is_bounded_when_a_read_ignores_the_token(bool leaveOpen)
    {
        using var io = new IoStream(ignoreReadCancellation: true);
        var client = new OfflineUpdateRpcClient(io, io, leaveOpen: leaveOpen);
        var pending = client.GetInfoAsync();
        await io.Written.Task;

        var dispose = client.DisposeAsync().AsTask();
        var winner = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(dispose, winner);
        await dispose;

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => pending);
        Assert.True(client.IsPoisoned);
        Assert.Equal(leaveOpen ? 0 : 1, io.DisposeCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Dispose_unblocks_pending_read_and_honors_ownership(bool sameStream, bool leaveOpen)
    {
        using var output = new IoStream();
        using var input = sameStream ? output : new IoStream();
        var client = new OfflineUpdateRpcClient(output, input, leaveOpen: leaveOpen);
        var pending = client.GetInfoAsync();
        await output.Written.Task;

        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => pending);
        await client.DisposeAsync();

        var expected = leaveOpen ? 0 : 1;
        Assert.Equal(expected, output.DisposeCount);
        Assert.Equal(expected, input.DisposeCount);
    }

    [Fact]
    public async Task Precancelled_token_and_invalid_inputs_send_nothing()
    {
        using var io = new IoStream();
        await using var client = new OfflineUpdateRpcClient(io, io);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetInfoAsync(cancelled.Token));
        Assert.False(client.IsPoisoned);

        Assert.ThrowsAny<ArgumentException>(() => { _ = client.BeginAsync(null!, new byte[] { 1 }); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = client.BeginAsync(Array.Empty<byte>(), new byte[] { 1 }); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = client.GetStatusAsync("BAD"); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = client.UploadAsync(Session, -1, new byte[] { 1 }); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = client.ExecuteAsync(Session, Encoding.UTF8.GetBytes("[]")); });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.ExportAsync(Session, "", 0, 1));
        Assert.Empty(io.Frames);
    }

    [Fact]
    public async Task Byte_inputs_are_snapshotted_while_waiting_for_gate()
    {
        using var io = new IoStream();
        await using var client = new OfflineUpdateRpcClient(io, io);
        var active = client.GetInfoAsync();
        await io.Written.Task;
        var document = new byte[] { 1, 2, 3 };
        var signature = new byte[] { 4, 5 };
        var chunk = new byte[] { 6, 7 };
        var begin = client.BeginAsync(document, signature);
        var upload = client.UploadAsync(Session, 0, chunk);
        Array.Fill(document, (byte)99);
        Array.Fill(signature, (byte)99);
        Array.Fill(chunk, (byte)99);

        ReplyOk(io, 1);
        ReplyOk(io, 2);
        ReplyOk(io, 3);
        await active;
        await begin;
        await upload;

        var beginParams = Frame(io, 1).GetProperty("params");
        Assert.Equal("AQID", beginParams.GetProperty("document").GetString());
        Assert.Equal("BAU=", beginParams.GetProperty("signature").GetString());
        Assert.Equal("Bgc=", Frame(io, 2).GetProperty("params").GetProperty("data").GetString());
    }

    [Fact]
    public void Constructor_rejects_timeout_outside_cts_bounds()
    {
        using var io = new IoStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OfflineUpdateRpcClient(io, io, TimeSpan.FromMilliseconds(uint.MaxValue)));
    }

    [Fact]
    public async Task Restart_to_verify_sends_only_the_session_id()
    {
        var transaction = "11111111-2222-4333-8444-555555555555";
        using var io = new IoStream();
        ReplyOk(io, 1,
            "{\"schemaVersion\":1,\"sessionId\":\"" + Session + "\"," +
            "\"state\":\"awaiting_reboot\",\"transactionId\":\"" + transaction + "\"," +
            "\"deployment\":\"armada/" + new string('a', 64) + ".1\"," +
            "\"restart\":{\"schemaVersion\":1,\"state\":\"dispatched\"," +
            "\"transactionId\":\"" + transaction + "\"}}");
        await using var client = new OfflineUpdateRpcClient(io, io);

        var result = await client.RestartToVerifyAsync(Session);

        Assert.Single(io.Frames);
        var frame = Frame(io, 0);
        Assert.Equal(1, frame.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(1, frame.GetProperty("id").GetInt32());
        Assert.Equal("restart_to_verify", frame.GetProperty("method").GetString());
        var parameters = frame.GetProperty("params");
        Assert.Equal(1, Count(parameters));
        Assert.Equal(Session, parameters.GetProperty("sessionId").GetString());
        Assert.Equal("dispatched", result.GetProperty("restart").GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("BAD")]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Restart_to_verify_rejects_invalid_session_without_writing(string sessionId)
    {
        using var io = new IoStream();
        await using var client = new OfflineUpdateRpcClient(io, io);

        Assert.ThrowsAny<ArgumentException>(() => { _ = client.RestartToVerifyAsync(sessionId); });
        Assert.ThrowsAny<ArgumentNullException>(() => { _ = client.RestartToVerifyAsync(null!); });
        Assert.Empty(io.Frames);
    }

    private static int Count(JsonElement value) => value.EnumerateObject().Count();

    private static byte[] Ok(int id, string result = "{}") =>
        Encoding.UTF8.GetBytes($"{{\"protocolVersion\":1,\"id\":{id},\"ok\":true,\"result\":{result}}}\n");

    private static void ReplyOk(IoStream io, int id, string result = "{}") => io.Reply(Ok(id, result));

    private static JsonElement Frame(IoStream io, int index)
    {
        using var document = JsonDocument.Parse(io.Frames[index]);
        return document.RootElement.Clone();
    }

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class IoStream : Stream
    {
        private readonly object _sync = new();
        private readonly MemoryStream _written = new();
        private readonly Queue<byte[]> _chunks = new();
        private readonly bool _ignoreReadCancellation;
        private TaskCompletionSource<bool> _changed = Signal();
        private byte[]? _current;
        private int _currentOffset;
        private bool _eof;
        private bool _disposed;

        public IoStream(bool ignoreReadCancellation = false)
        {
            _ignoreReadCancellation = ignoreReadCancellation;
        }

        public TaskCompletionSource<bool> Written { get; } = Signal();

        /// <summary>Completes once a read has actually returned bytes.</summary>
        public TaskCompletionSource<bool> ReadCompleted { get; } = Signal();

        public int DisposeCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public IReadOnlyList<byte[]> Frames
        {
            get
            {
                lock (_sync)
                {
                    var data = _written.ToArray();
                    var frames = new List<byte[]>(data.Length / 32 + 1);
                    var start = 0;
                    for (var i = 0; i < data.Length; i++)
                    {
                        if (data[i] != (byte)'\n')
                            continue;
                        if (i > start)
                            frames.Add(data[start..i]);
                        start = i + 1;
                    }
                    return frames;
                }
            }
        }

        public void Reply(params byte[][] chunks)
        {
            lock (_sync)
            {
                foreach (var chunk in chunks)
                    _chunks.Enqueue(chunk);
            }
            Pulse();
        }

        public void End()
        {
            lock (_sync)
                _eof = true;
            Pulse();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_disposed)
                        return 0;
                    if (_current is null && _chunks.Count != 0)
                    {
                        _current = _chunks.Dequeue();
                        _currentOffset = 0;
                    }
                    if (_current is not null)
                    {
                        var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
                        _current.AsMemory(_currentOffset, count).CopyTo(buffer);
                        _currentOffset += count;
                        if (_currentOffset == _current.Length)
                            _current = null;
                        ReadCompleted.TrySetResult(true);
                        return count;
                    }
                    if (_eof)
                        return 0;
                    wait = _changed.Task;
                }
                await (_ignoreReadCancellation ? wait : wait.WaitAsync(cancellationToken)).ConfigureAwait(false);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _written.Write(buffer.Span);
            }
            Written.TrySetResult(true);
            await Task.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    DisposeCount++;
                }
            }
            Pulse();
            base.Dispose(disposing);
        }

        private void Pulse()
        {
            TaskCompletionSource<bool> previous;
            lock (_sync)
            {
                previous = _changed;
                _changed = Signal();
            }
            previous.TrySetResult(true);
        }
    }
}
