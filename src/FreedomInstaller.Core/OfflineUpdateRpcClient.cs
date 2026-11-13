using System.Buffers;
using System.Text;
using System.Text.Json;

namespace FreedomInstaller.Core;

public sealed class OfflineUpdateRemoteException : IOException
{
    public OfflineUpdateRemoteException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public sealed class OfflineUpdateRpcClient : IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private const int MaxDocumentBytes = 2 * 1024 * 1024;
    private const int MaxSignatureBytes = 16 * 1024;
    private const int MaxChunkBytes = 1024 * 1024;
    private const int MaxAcknowledgementBytes = 2 * 1024 * 1024;
    private const int MaxErrorMessageLength = 500;
    private const int ReadBufferSize = 64 * 1024;
    private const string PoisonedMessage =
        "The offline update RPC connection is no longer usable after an interrupted or invalid exchange; reconnect and query the existing session before retrying.";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Stream _requestOutput;
    private readonly Stream _responseInput;
    private readonly TimeSpan _timeout;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private byte[] _readBuffer = Array.Empty<byte>();
    private int _readOffset;
    private int _readCount;
    private int _nextRequestId;
    private bool _poisoned;
    private int _disposed;

    public OfflineUpdateRpcClient(
        Stream requestOutput,
        Stream responseInput,
        TimeSpan? timeout = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(requestOutput);
        ArgumentNullException.ThrowIfNull(responseInput);
        if (!requestOutput.CanWrite)
            throw new ArgumentException("Request stream must be writable.", nameof(requestOutput));
        if (!responseInput.CanRead)
            throw new ArgumentException("Response stream must be readable.", nameof(responseInput));

        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero ||
            effectiveTimeout == Timeout.InfiniteTimeSpan ||
            effectiveTimeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be a positive finite duration supported by CancellationTokenSource.");
        }

        _requestOutput = requestOutput;
        _responseInput = responseInput;
        _timeout = effectiveTimeout;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// True once the exchange can no longer be used and the connection must be
    /// replaced. It stays false for a call that was cancelled before anything
    /// was written, so a caller may safely retry that request.
    /// </summary>
    public bool IsPoisoned => Volatile.Read(ref _poisoned);

    public Task<JsonElement> GetInfoAsync(CancellationToken cancellationToken = default) =>
        SendAsync("info", new Dictionary<string, object?>(0), cancellationToken);

    public Task<JsonElement> BeginAsync(byte[] document, byte[] signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signature);
        if (document.Length is <= 0 or > MaxDocumentBytes)
            throw new ArgumentOutOfRangeException(nameof(document), $"Document must be between 1 and {MaxDocumentBytes} bytes.");
        if (signature.Length is <= 0 or > MaxSignatureBytes)
            throw new ArgumentOutOfRangeException(nameof(signature), $"Signature must be between 1 and {MaxSignatureBytes} bytes.");

        var docCopy = document.ToArray();
        var sigCopy = signature.ToArray();
        return SendAsync("begin", new Dictionary<string, object?>(2)
        {
            ["document"] = docCopy,
            ["signature"] = sigCopy,
        }, cancellationToken);
    }

    /// <summary>
    /// Read-only package preflight. It sends the same base64 <c>document</c>
    /// and <c>signature</c> fields as <see cref="BeginAsync"/> but creates no
    /// session and never triggers prepare or a slot write.
    /// </summary>
    public Task<JsonElement> PreflightAsync(byte[] document, byte[] signature, CancellationToken cancellationToken = default) =>
        PreflightAsync(document, signature, null, cancellationToken);

    /// <summary>
    /// Read-only preflight that also names one existing protected session. The
    /// tablet then binds that session's receipt, descriptor and stored payload
    /// to this package and device, and credits only the bytes it already
    /// stores. Passing <paramref name="sessionId"/> null keeps the plain
    /// document+signature shape.
    /// </summary>
    public Task<JsonElement> PreflightAsync(byte[] document, byte[] signature, string? sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signature);
        if (document.Length is <= 0 or > MaxDocumentBytes)
            throw new ArgumentOutOfRangeException(nameof(document), $"Document must be between 1 and {MaxDocumentBytes} bytes.");
        if (signature.Length is <= 0 or > MaxSignatureBytes)
            throw new ArgumentOutOfRangeException(nameof(signature), $"Signature must be between 1 and {MaxSignatureBytes} bytes.");

        var docCopy = document.ToArray();
        var sigCopy = signature.ToArray();
        if (sessionId is null)
        {
            return SendAsync("preflight", new Dictionary<string, object?>(2)
            {
                ["document"] = docCopy,
                ["signature"] = sigCopy,
            }, cancellationToken);
        }

        ValidateSessionId(sessionId);
        return SendAsync("preflight", new Dictionary<string, object?>(3)
        {
            ["document"] = docCopy,
            ["signature"] = sigCopy,
            ["sessionId"] = sessionId,
        }, cancellationToken);
    }

    public Task<JsonElement> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        return SendAsync("status", new Dictionary<string, object?>(1) { ["sessionId"] = sessionId }, cancellationToken);
    }

    public Task<JsonElement> UploadAsync(string sessionId, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative.");
        if (data.Length is <= 0 or > MaxChunkBytes)
            throw new ArgumentOutOfRangeException(nameof(data), $"Upload chunk must be between 1 and {MaxChunkBytes} bytes.");

        var chunkCopy = data.ToArray();
        return SendAsync("upload", new Dictionary<string, object?>(3)
        {
            ["sessionId"] = sessionId,
            ["offset"] = offset,
            ["data"] = chunkCopy,
        }, cancellationToken);
    }

    public Task<JsonElement> SealAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        return SendAsync("seal", new Dictionary<string, object?>(1) { ["sessionId"] = sessionId }, cancellationToken);
    }

    public Task<JsonElement> PrepareAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        return SendAsync("prepare", new Dictionary<string, object?>(1) { ["sessionId"] = sessionId }, cancellationToken);
    }

    public Task<JsonElement> ExecuteAsync(string sessionId, byte[] acknowledgement, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (acknowledgement.Length is <= 0 or > MaxAcknowledgementBytes)
            throw new ArgumentOutOfRangeException(nameof(acknowledgement), $"Acknowledgement must be between 1 and {MaxAcknowledgementBytes} bytes.");

        var ackElement = ParseAcknowledgementObject(acknowledgement.AsSpan());
        return SendAsync("execute", new Dictionary<string, object?>(2)
        {
            ["sessionId"] = sessionId,
            ["acknowledgement"] = ackElement,
        }, cancellationToken);
    }

    public Task<JsonElement> RestartToVerifyAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        // The tablet accepts only the session id; eligibility, persistence and
        // the single reboot are decided on the device.
        return SendAsync("restart_to_verify", new Dictionary<string, object?>(1) { ["sessionId"] = sessionId }, cancellationToken);
    }

    public async Task<byte[]> ExportAsync(string sessionId, string relativePath, long offset, int length, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        if (string.IsNullOrEmpty(relativePath))
            throw new ArgumentException("Relative path must be a non-empty string.", nameof(relativePath));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative.");
        if (length is <= 0 or > MaxChunkBytes)
            throw new ArgumentOutOfRangeException(nameof(length), $"Length must be between 1 and {MaxChunkBytes} bytes.");

        return await SendCoreAsync("export", new Dictionary<string, object?>(4)
        {
            ["sessionId"] = sessionId,
            ["path"] = relativePath,
            ["offset"] = offset,
            ["length"] = length,
        }, result =>
        {
            if (result.ValueKind != JsonValueKind.Object ||
                !TryGetSingleProperty(result, "data", out var dataProp) ||
                dataProp.ValueKind != JsonValueKind.String)
            {
                throw new IOException("Export response must be a JSON object containing only a base64 'data' string. " + PoisonedMessage);
            }

            byte[] decoded;
            try
            {
                decoded = dataProp.GetBytesFromBase64();
            }
            catch (FormatException ex)
            {
                throw new IOException("Export response 'data' is not valid base64. " + PoisonedMessage, ex);
            }

            if (decoded.Length > length)
            {
                throw new IOException($"Export response returned {decoded.Length} bytes, exceeding requested length {length}. " + PoisonedMessage);
            }

            return decoded;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        // A request in flight races its physical read against the lifetime
        // token, so this gate should free immediately. The bound guards the
        // remaining case where a stream call was already blocked; teardown
        // still closes the streams so a pending read cannot hold a UI thread
        // forever waiting for this method.
        var gateTaken = await _gate.WaitAsync(DisposeDrainTimeout).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _poisoned, true);
            await CloseOwnedStreamsAsync().ConfigureAwait(false);
        }
        finally
        {
            if (gateTaken)
                _gate.Release();
        }
    }

    private Task<JsonElement> SendAsync(string method, Dictionary<string, object?> parameters, CancellationToken cancellationToken) =>
        SendCoreAsync(method, parameters, static result => result, cancellationToken);

    private async Task<T> SendCoreAsync<T>(
        string method,
        Dictionary<string, object?> parameters,
        Func<JsonElement, T> convertResult,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (IsPoisoned)
                throw new IOException(PoisonedMessage);
            if (_nextRequestId == int.MaxValue)
            {
                Volatile.Write(ref _poisoned, true);
                throw new IOException("RPC request identifier space exhausted. " + PoisonedMessage);
            }

            var requestId = ++_nextRequestId;
            var frame = SerializeRequestFrame(requestId, method, parameters);

            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token, timeoutCts.Token);
            var ioToken = linkedCts.Token;
            var sendAttempted = false;

            try
            {
                ioToken.ThrowIfCancellationRequested();
                sendAttempted = true;
                await _requestOutput.WriteAsync(frame, ioToken).ConfigureAwait(false);
                await _requestOutput.FlushAsync(ioToken).ConfigureAwait(false);
                var responseLine = await ReadResponseFrameAsync(ioToken).ConfigureAwait(false);
                return convertResult(ParseAndValidateResponse(responseLine, requestId));
            }
            catch (OfflineUpdateRemoteException)
            {
                throw;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                if (sendAttempted)
                    Volatile.Write(ref _poisoned, true);
                throw new ObjectDisposedException(nameof(OfflineUpdateRpcClient), PoisonedMessage);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (sendAttempted)
                    Volatile.Write(ref _poisoned, true);
                throw;
            }
            catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
            {
                if (sendAttempted)
                    Volatile.Write(ref _poisoned, true);
                throw new TimeoutException($"Offline update RPC '{method}' timed out after {_timeout}. " + PoisonedMessage, ex);
            }
            catch (Exception ex) when (ex is not IOException)
            {
                if (sendAttempted)
                    Volatile.Write(ref _poisoned, true);
                throw new IOException(PoisonedMessage, ex);
            }
            catch
            {
                if (sendAttempted)
                    Volatile.Write(ref _poisoned, true);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[] SerializeRequestFrame(int requestId, string method, Dictionary<string, object?> parameters)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", ProtocolVersion);
            writer.WriteNumber("id", requestId);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            JsonSerializer.Serialize(writer, parameters);
            writer.WriteEndObject();
        }

        var newlineSpan = buffer.GetSpan(1);
        newlineSpan[0] = (byte)'\n';
        buffer.Advance(1);

        if (buffer.WrittenCount > MaxFrameBytes)
            throw new ArgumentException($"Serialized RPC request exceeds maximum frame size of {MaxFrameBytes} bytes.");

        return buffer.WrittenSpan.ToArray();
    }

    private async Task<byte[]> ReadResponseFrameAsync(CancellationToken cancellationToken)
    {
        using var frameStream = new MemoryStream();
        while (true)
        {
            if (_readCount > 0)
            {
                var span = _readBuffer.AsSpan(_readOffset, _readCount);
                var newlineIndex = span.IndexOf((byte)'\n');
                if (newlineIndex >= 0)
                {
                    var bytesToCopy = newlineIndex + 1;
                    if (frameStream.Length + bytesToCopy > MaxFrameBytes)
                        throw new IOException($"RPC response frame exceeds {MaxFrameBytes} bytes. " + PoisonedMessage);

                    frameStream.Write(span[..newlineIndex]);
                    _readOffset += bytesToCopy;
                    _readCount -= bytesToCopy;
                    if (frameStream.Length == 0)
                        throw new IOException("Received empty RPC response frame. " + PoisonedMessage);

                    return frameStream.ToArray();
                }

                if (frameStream.Length + _readCount >= MaxFrameBytes)
                    throw new IOException($"RPC response frame exceeds {MaxFrameBytes} bytes. " + PoisonedMessage);

                frameStream.Write(span);
                _readOffset = 0;
                _readCount = 0;
            }

            var buffer = new byte[ReadBufferSize];
            var bytesRead = await ReadBoundedAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                throw new EndOfStreamException("Unexpected end of stream while reading RPC response frame. " + PoisonedMessage);

            _readBuffer = buffer;
            _readOffset = 0;
            _readCount = bytesRead;
        }
    }

    /// <summary>
    /// Races the physical read against cancellation instead of trusting the
    /// stream to observe the token. A cancelled pipe may keep its read pending
    /// indefinitely; the caller still gets a bounded cancellation, and the
    /// abandoned read is left to fill a private buffer that no later request
    /// can observe. A fault from that late read is observed here so it never
    /// surfaces as an unobserved task exception.
    /// </summary>
    private async ValueTask<int> ReadBoundedAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var readTask = _responseInput.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask();
        try
        {
            return await readTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // WaitAsync abandons, rather than cancels, the physical read.
            // Keep watching it so a late failure cannot become unobserved.
            _ = readTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private static JsonElement ParseAndValidateResponse(byte[] utf8Line, int expectedId)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(utf8Line);
        }
        catch (DecoderFallbackException ex)
        {
            throw new IOException("RPC response contains invalid UTF-8 bytes. " + PoisonedMessage, ex);
        }

        ValidateNoDuplicateKeysAndConstants(utf8Line);
        using var doc = JsonDocument.Parse(utf8Line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new IOException("RPC response root must be a JSON object. " + PoisonedMessage);

        if (!root.TryGetProperty("protocolVersion", out var versionEl) ||
            versionEl.ValueKind != JsonValueKind.Number ||
            !versionEl.TryGetInt32(out var version) ||
            version != ProtocolVersion)
        {
            throw new IOException("RPC response has missing or unsupported protocolVersion. " + PoisonedMessage);
        }

        if (!root.TryGetProperty("id", out var idEl) ||
            idEl.ValueKind != JsonValueKind.Number ||
            !idEl.TryGetInt32(out var responseId) ||
            responseId != expectedId)
        {
            throw new IOException($"RPC response id did not match expected id {expectedId}. " + PoisonedMessage);
        }

        if (!root.TryGetProperty("ok", out var okEl) ||
            okEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new IOException("RPC response is missing boolean 'ok' field. " + PoisonedMessage);
        }

        var propCount = CountProperties(root);
        if (propCount != 4)
            throw new IOException("RPC response object contains unexpected properties. " + PoisonedMessage);

        if (okEl.GetBoolean())
        {
            if (!root.TryGetProperty("result", out var resultEl))
                throw new IOException("Successful RPC response is missing 'result' field. " + PoisonedMessage);

            return resultEl.Clone();
        }

        if (!root.TryGetProperty("error", out var errorEl) ||
            errorEl.ValueKind != JsonValueKind.Object ||
            CountProperties(errorEl) != 2 ||
            !errorEl.TryGetProperty("code", out var codeEl) ||
            codeEl.ValueKind != JsonValueKind.String ||
            !errorEl.TryGetProperty("message", out var messageEl) ||
            messageEl.ValueKind != JsonValueKind.String)
        {
            throw new IOException("Failed RPC response has malformed 'error' object. " + PoisonedMessage);
        }

        var code = codeEl.GetString();
        var message = messageEl.GetString();
        if (string.IsNullOrWhiteSpace(code) || message is null || message.Length > MaxErrorMessageLength)
            throw new IOException("Failed RPC response has invalid error code or oversized message. " + PoisonedMessage);

        throw new OfflineUpdateRemoteException(code, message);
    }

    private static JsonElement ParseAcknowledgementObject(ReadOnlySpan<byte> utf8Bytes)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(utf8Bytes);
            ValidateNoDuplicateKeysAndConstants(utf8Bytes);
            using var doc = JsonDocument.Parse(utf8Bytes.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Acknowledgement must be a JSON object.", "acknowledgement");

            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is DecoderFallbackException or JsonException)
        {
            throw new ArgumentException("Acknowledgement must be valid UTF-8 JSON without duplicate keys.", "acknowledgement", ex);
        }
    }

    private static void ValidateNoDuplicateKeysAndConstants(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var stack = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    stack.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    stack.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    stack.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var key = reader.GetString()!;
                    if (!stack.Peek()!.Add(key))
                        throw new JsonException($"Duplicate JSON property '{key}' is not allowed.");
                    break;
            }
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (sessionId.Length != 32)
            throw new ArgumentException("Session ID must be exactly 32 lowercase hexadecimal characters.", nameof(sessionId));

        foreach (var ch in sessionId)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                throw new ArgumentException("Session ID must be exactly 32 lowercase hexadecimal characters.", nameof(sessionId));
        }
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject())
            count++;
        return count;
    }

    private static bool TryGetSingleProperty(JsonElement element, string name, out JsonElement value)
    {
        if (CountProperties(element) == 1 && element.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    private async ValueTask CloseOwnedStreamsAsync()
    {
        if (_leaveOpen)
            return;

        try
        {
            await _requestOutput.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(_responseInput, _requestOutput))
                await _responseInput.DisposeAsync().ConfigureAwait(false);
        }
    }
}
