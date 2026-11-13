using System.Text;

namespace FreedomInstaller.Core;

/// <summary>
/// Bounded text accumulator. Diagnostics keep only the tail so a noisy remote
/// command cannot grow memory without limit.
/// </summary>
internal sealed class OfflineUpdateBoundedText
{
    private readonly int _capacity;
    private readonly StringBuilder _text;
    private readonly object _gate = new();

    internal OfflineUpdateBoundedText(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _text = new StringBuilder(Math.Min(capacity, 4096));
    }

    internal void Append(string value)
    {
        if (value.Length == 0)
            return;

        lock (_gate)
        {
            _text.Append(value);
            var excess = _text.Length - _capacity;
            if (excess > 0)
                _text.Remove(0, excess);
        }
    }

    internal string Snapshot()
    {
        lock (_gate)
        {
            return _text.ToString();
        }
    }
}

/// <summary>
/// Watches the stderr byte stream of a sudo command for the unique prompt
/// marker. The first occurrence means sudo is waiting for a password; a later
/// occurrence after the password has been written means the password was
/// rejected. Raw bytes are inspected so a multi-byte UTF-8 sequence cannot hide
/// the ASCII marker across read boundaries.
/// </summary>
internal sealed class OfflineUpdateSudoPromptTracker
{
    private readonly byte[] _marker;
    private readonly TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _repeated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private byte[] _overlap = Array.Empty<byte>();
    private bool _passwordSent;

    internal OfflineUpdateSudoPromptTracker(string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        _marker = Encoding.ASCII.GetBytes(marker);
    }

    internal Task FirstPrompt => _first.Task;

    internal Task RepeatedPrompt => _repeated.Task;

    internal bool PasswordSent
    {
        get
        {
            lock (_gate)
            {
                return _passwordSent;
            }
        }
    }

    /// <summary>Marks the password as written before the write is attempted, so it is never sent twice.</summary>
    internal void MarkPasswordSent()
    {
        lock (_gate)
        {
            _passwordSent = true;
        }
    }

    internal void Feed(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        byte[] combined;
        lock (_gate)
        {
            combined = new byte[_overlap.Length + data.Length];
            _overlap.CopyTo(combined, 0);
            data.CopyTo(combined.AsSpan(_overlap.Length));
            _overlap = Array.Empty<byte>();
        }

        var index = 0;
        while (index <= combined.Length - _marker.Length)
        {
            var found = combined.AsSpan(index).IndexOf(_marker);
            if (found < 0)
                break;

            index += found;
            OnPrompt();
            index += _marker.Length;
        }

        var keep = Math.Min(_marker.Length - 1, combined.Length);
        lock (_gate)
        {
            _overlap = combined[^keep..];
        }
    }

    private void OnPrompt()
    {
        lock (_gate)
        {
            if (_passwordSent)
            {
                _repeated.TrySetResult(true);
                return;
            }
        }

        // A second prompt before the password was sent means sudo is asking
        // again for a reason other than a rejected password. Treat it as a
        // repeat as well; the caller never writes a secret without a single
        // matching prompt.
        if (!_first.TrySetResult(true))
            _repeated.TrySetResult(true);
    }
}

/// <summary>
/// One sudo channel plus a continuously drained, bounded stderr tail. The
/// prompt tracker observes the same bytes that are retained for diagnostics,
/// so a client never has to choose between prompt detection and draining.
/// </summary>
internal sealed class OfflineUpdateSshElevatedProcess : IAsyncDisposable
{
    private readonly IOfflineUpdateSshChannel _channel;
    private readonly OfflineUpdateBoundedText _stderr;
    private readonly OfflineUpdateSudoPromptTracker _prompts;
    private readonly CancellationTokenSource _stderrCts = new();
    private readonly Task _stderrTask;
    private int _disposed;
    private int _writeAbandoned;

    internal OfflineUpdateSshElevatedProcess(
        IOfflineUpdateSshChannel channel,
        string promptMarker,
        int maxDiagnosticsBytes)
    {
        _channel = channel;
        _stderr = new OfflineUpdateBoundedText(maxDiagnosticsBytes);
        _prompts = new OfflineUpdateSudoPromptTracker(promptMarker);
        _stderrTask = Task.Run(() => PumpStderrAsync(_channel.StandardError, _stderr, _prompts, _stderrCts.Token));
    }

    internal IOfflineUpdateSshChannel Channel => _channel;

    internal string ErrorTail => _stderr.Snapshot();

    internal Task FirstPrompt => _prompts.FirstPrompt;

    internal Task RepeatedPrompt => _prompts.RepeatedPrompt;

    internal bool PasswordSent => _prompts.PasswordSent;

    /// <summary>
    /// True once a password write or its flush was given up on while the
    /// physical send was still running. SSH.NET performs that send under the
    /// session write lock, so cleanup must release the socket instead of
    /// queueing a cancel behind it.
    /// </summary>
    internal bool WriteAbandoned => Volatile.Read(ref _writeAbandoned) != 0;

    internal void MarkWriteAbandoned() => Volatile.Write(ref _writeAbandoned, 1);

    internal Task StderrDrained => _stderrTask;

    internal void MarkPasswordSent() => _prompts.MarkPasswordSent();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stderrCts.CancelAsync().ConfigureAwait(false);
        try
        {
            _channel.Cancel();
        }
        catch
        {
        }

        try
        {
            await _stderrTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        if (_stderrTask.IsCompleted)
        {
            try
            {
                _channel.Dispose();
            }
            catch
            {
            }
        }

        _stderrCts.Dispose();
    }

    private static async Task PumpStderrAsync(
        Stream stream,
        OfflineUpdateBoundedText target,
        OfflineUpdateSudoPromptTracker tracker,
        CancellationToken token)
    {
        var buffer = new byte[1024];

        // One decoder for the whole stream: a read can end in the middle of a
        // multi-byte character, and decoding each chunk on its own would turn
        // both halves into replacement characters. The destination is sized
        // from the decoder's own worst-case promise so a carried prefix plus a
        // full read cannot overrun it. The tracker still sees raw bytes, so the
        // ASCII prompt marker is matched exactly as before.
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                {
                    // Flush the decoder at end of stream: a truncated final
                    // sequence becomes a replacement character instead of
                    // disappearing from the diagnostic tail.
                    var tail = decoder.GetChars(buffer, 0, 0, chars, 0, flush: true);
                    if (tail > 0)
                        target.Append(new string(chars, 0, tail));
                    return;
                }

                tracker.Feed(buffer.AsSpan(0, read));
                var count = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                if (count > 0)
                    target.Append(new string(chars, 0, count));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>Result of a sudo command whose stderr prompt was handled in-process.</summary>
internal static class OfflineUpdateSshSudo
{
    internal const string PromptPrefix = "ARMADA-SUDO-PROMPT-";

    /// <summary>
    /// How long a startup failure waits for the stderr pump to finish before it
    /// quotes the tail. The command has already closed stdout, so its last
    /// diagnostics are typically a pipe read away; the bound keeps a stderr that
    /// never closes from delaying the failure indefinitely.
    /// </summary>
    private static readonly TimeSpan ErrorTailSettleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The one-shot command's deadline failure. The wait loop and the password
    /// write share it, so an expired budget reads the same wherever it is
    /// observed.
    /// </summary>
    private const string CommandTimeoutMessage = "The sudo command did not finish in time.";

    /// <summary>Same rule for the updater startup handshake.</summary>
    private const string UpdaterStartTimeoutMessage = "The tablet did not start the offline updater in time.";

    internal static string NewPromptMarker() => PromptPrefix + Guid.NewGuid().ToString("N");

    internal static string BuildSudoCommand(string innerCommand, string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(innerCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        // The marker is generated from ASCII letters, digits and '-', so it is
        // safe as an unquoted sudo prompt argument.
        return "sudo -S -p " + marker + " -- " + innerCommand;
    }

    internal static async Task<OfflineUpdateSshCommandResult> RunAsync(
        IOfflineUpdateSshTransport transport,
        string innerCommand,
        string? password,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken token)
    {
        var marker = NewPromptMarker();

        // Start opens the channel synchronously and accepts no token, so a
        // cancelled attempt must be rejected before any privileged command
        // exists on the device.
        token.ThrowIfCancellationRequested();
        var channel = transport.Start(BuildSudoCommand(innerCommand, marker));
        var elevated = new OfflineUpdateSshElevatedProcess(channel, marker, maxOutputBytes);
        var stdout = new OfflineUpdateBoundedText(maxOutputBytes);
        var stdoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stdoutTask = PumpStdoutAsync(channel.StandardOutput, stdout, stdoutCts.Token);
        var exitTask = Task.Run(() => channel.WaitForExit(timeout), CancellationToken.None);
        var promptPending = true;
        var deadline = DateTime.UtcNow + timeout;

        try
        {
            while (true)
            {
                if (elevated.RepeatedPrompt.IsCompleted)
                    throw SudoRejected(elevated.ErrorTail);

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException(CommandTimeoutMessage);

                var waitSet = new List<Task>(3) { exitTask, Task.Delay(remaining, token) };
                waitSet.Add(promptPending ? elevated.FirstPrompt : elevated.RepeatedPrompt);

                var completed = await Task.WhenAny(waitSet).ConfigureAwait(false);
                if (completed == elevated.RepeatedPrompt)
                    throw SudoRejected(elevated.ErrorTail);
                if (completed == elevated.FirstPrompt)
                {
                    promptPending = false;
                    await WritePasswordOnceAsync(
                        channel, elevated, password, deadline, CommandTimeoutMessage, token).ConfigureAwait(false);
                    continue;
                }
                if (completed == exitTask)
                    break;

                token.ThrowIfCancellationRequested();
                throw new TimeoutException(CommandTimeoutMessage);
            }

            var exited = await exitTask.ConfigureAwait(false);
            if (!exited)
                throw new TimeoutException(CommandTimeoutMessage);

            await DrainAsync(stdoutTask).ConfigureAwait(false);
            await DrainAsync(elevated.StderrDrained).ConfigureAwait(false);
            return new OfflineUpdateSshCommandResult(
                channel.ExitStatus,
                stdout.Snapshot(),
                elevated.ErrorTail);
        }
        finally
        {
            await stdoutCts.CancelAsync().ConfigureAwait(false);

            // A password write that outlived its bounded wait can still be
            // inside SSH.NET's synchronous send, so the exact socket is
            // released before the channel cancel that would queue behind its
            // writer lock. A handshake that never wrote keeps the session --
            // and the transport -- reusable.
            if (elevated.WriteAbandoned)
                AbortTransportSocket(transport);

            try
            {
                channel.Cancel();
            }
            catch
            {
            }

            await DrainAsync(exitTask).ConfigureAwait(false);
            await DrainAsync(stdoutTask).ConfigureAwait(false);
            stdoutCts.Dispose();
            await elevated.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the long-lived updater with the same prompt handshake. The caller
    /// receives the channel only after the stdout ready marker proves the
    /// command is running as root, so no password can ever reach RPC input.
    /// </summary>
    internal static async Task<OfflineUpdateSshStartedUpdater> StartUpdaterAsync(
        IOfflineUpdateSshTransport transport,
        string innerCommand,
        bool useSudo,
        string readyMarker,
        string? password,
        TimeSpan timeout,
        int maxDiagnosticsBytes,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readyMarker);
        var marker = NewPromptMarker();

        // Same rule as RunAsync: the channel is created synchronously and takes
        // no token, so cancellation has to be observed before it starts.
        token.ThrowIfCancellationRequested();
        var channel = transport.Start(useSudo ? BuildSudoCommand(innerCommand, marker) : innerCommand);
        var elevated = new OfflineUpdateSshElevatedProcess(channel, marker, maxDiagnosticsBytes);
        var lineCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var lineTask = ReadLineAsync(channel.StandardOutput, readyMarker.Length + 64, lineCts.Token);
        var promptPending = true;
        var handedOff = false;
        var deadline = DateTime.UtcNow + timeout;

        try
        {
            while (true)
            {
                if (elevated.RepeatedPrompt.IsCompleted)
                    throw SudoRejected(elevated.ErrorTail);

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException(UpdaterStartTimeoutMessage);

                var waitSet = new List<Task>(3) { lineTask, Task.Delay(remaining, token) };
                waitSet.Add(promptPending ? elevated.FirstPrompt : elevated.RepeatedPrompt);

                var completed = await Task.WhenAny(waitSet).ConfigureAwait(false);
                if (completed == elevated.RepeatedPrompt)
                    throw SudoRejected(elevated.ErrorTail);
                if (completed == elevated.FirstPrompt)
                {
                    promptPending = false;
                    await WritePasswordOnceAsync(
                        channel, elevated, password, deadline, UpdaterStartTimeoutMessage, token).ConfigureAwait(false);
                    continue;
                }
                if (completed == lineTask)
                {
                    string? line;
                    try
                    {
                        line = await lineTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        throw new OfflineUpdateSshManagedException(
                            OfflineUpdateSshManagedFailure.UpdaterUnavailable,
                            "The tablet updater startup output could not be read: " + ex.Message,
                            ex);
                    }

                    if (line is not null && string.Equals(line.TrimEnd('\r'), readyMarker, StringComparison.Ordinal))
                    {
                        handedOff = true;
                        await lineCts.CancelAsync().ConfigureAwait(false);
                        return new OfflineUpdateSshStartedUpdater(elevated);
                    }

                    // stdout can reach EOF before the device's last stderr
                    // bytes are pumped. Settle the bounded stderr pump first so
                    // a refusal written on stderr still reaches the failure
                    // message instead of an empty tail.
                    var errorTail = await SettleErrorTailAsync(elevated, token).ConfigureAwait(false);
                    var detail = string.Join(
                        " ",
                        new[] { line, errorTail }.Where(value => !string.IsNullOrWhiteSpace(value)));
                    // The reason and the message must come from the same
                    // classification: a sudo startup failure must not be
                    // reported as missing updater components.
                    var reason = ClassifyStartupFailure(detail, password is not null);
                    throw new OfflineUpdateSshManagedException(
                        reason,
                        BuildStartupFailureMessage(reason, detail));
                }
                token.ThrowIfCancellationRequested();
                throw new TimeoutException(UpdaterStartTimeoutMessage);
            }
        }
        finally
        {
            await lineCts.CancelAsync().ConfigureAwait(false);
            if (!handedOff)
            {
                // Same rule as RunAsync: the startup handshake may have
                // abandoned a password write whose physical send still holds
                // the session writer lock, and this attempt owns no connection
                // yet to release it later.
                if (elevated.WriteAbandoned)
                    AbortTransportSocket(transport);

                try
                {
                    channel.Cancel();
                }
                catch
                {
                }
            }

            await DrainAsync(lineTask).ConfigureAwait(false);
            lineCts.Dispose();
            if (!handedOff)
                await elevated.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the elevation secret once, within the remaining budget of the
    /// command that asked for it. The response is never retried, so a caller
    /// cancellation and an expired budget both leave the attempt finished.
    /// </summary>
    internal static async Task WritePasswordOnceAsync(
        IOfflineUpdateSshChannel channel,
        OfflineUpdateSshElevatedProcess elevated,
        string? password,
        DateTime deadline,
        string timeoutMessage,
        CancellationToken token)
    {
        if (elevated.PasswordSent)
            return;

        if (string.IsNullOrEmpty(password))
        {
            throw new OfflineUpdateSshManagedException(
                OfflineUpdateSshManagedFailure.SudoUnavailable,
                "The tablet requires a sudo password, but no password is available for this connection.");
        }

        // The prompt can arrive just before the command's own budget runs out.
        // Check it before the secret is handed to the stream: a send that
        // starts past the deadline could only be abandoned.
        if (deadline - DateTime.UtcNow <= TimeSpan.Zero)
        {
            token.ThrowIfCancellationRequested();
            throw new TimeoutException(timeoutMessage);
        }

        // Mark first: a cancelled or failed write must never be retried and send
        // the secret a second time.
        elevated.MarkPasswordSent();
        var bytes = new byte[Encoding.UTF8.GetByteCount(password) + 1];
        try
        {
            var written = Encoding.UTF8.GetBytes(password, bytes);
            bytes[written] = (byte)'\n';

            // SSH.NET writes stdin with a synchronous socket send under the
            // session write lock, and neither the write nor the flush need to
            // observe the token. Both are bounded by the command's own
            // remaining budget instead of a fresh timeout per stage, so a
            // stalled prompt response ends the handshake with the caller's
            // deadline; racing the send keeps that bound effective, and a lost
            // race is recorded so cleanup releases the exact socket instead of
            // queueing a cancel behind the lock the send may still hold.
            var write = channel.StandardInput.WriteAsync(bytes, token).AsTask();
            await AwaitPasswordSendAsync(write, elevated, deadline, timeoutMessage, token).ConfigureAwait(false);
            await AwaitPasswordSendAsync(
                channel.StandardInput.FlushAsync(token), elevated, deadline, timeoutMessage, token).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <summary>
    /// Waits for one physical stdin send inside the command's remaining
    /// budget. Caller cancellation stays an
    /// <see cref="OperationCanceledException"/>; an expired budget is the
    /// command's own <see cref="TimeoutException"/>, so the two causes never
    /// collapse into one another.
    /// </summary>
    private static async Task AwaitPasswordSendAsync(
        Task send,
        OfflineUpdateSshElevatedProcess elevated,
        DateTime deadline,
        string timeoutMessage,
        CancellationToken token)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            AbandonPasswordSend(send, elevated);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException(timeoutMessage);
        }

        try
        {
            await send.WaitAsync(remaining, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AbandonPasswordSend(send, elevated);
            throw;
        }
        catch (TimeoutException)
        {
            AbandonPasswordSend(send, elevated);

            // Same precedence as the wait loop: a caller who cancelled in the
            // same instant still gets cancellation, not a deadline failure.
            token.ThrowIfCancellationRequested();
            throw new TimeoutException(timeoutMessage);
        }
    }

    /// <summary>
    /// Records a send that outlived its bounded wait, so teardown releases the
    /// exact socket before it cancels or disposes the channel. A send that
    /// already finished needs nothing: no writer is left behind.
    /// </summary>
    private static void AbandonPasswordSend(Task send, OfflineUpdateSshElevatedProcess elevated)
    {
        if (send.IsCompleted)
            return;

        elevated.MarkWriteAbandoned();
        ObserveLateFailure(send);
    }

    /// <summary>
    /// Keeps watching an abandoned physical write so a late failure cannot
    /// surface as an unobserved task exception.
    /// </summary>
    private static void ObserveLateFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed =>
            {
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Releases the exact authenticated socket without taking SSH.NET's write
    /// path, so a send the peer stopped reading can still be broken before
    /// cleanup asks the channel anything. Best effort: an unresolvable socket
    /// leaves the bounded cleanup waits to finish on their own.
    /// </summary>
    private static void AbortTransportSocket(IOfflineUpdateSshTransport transport)
    {
        IOfflineUpdateSshSocketAbort? release;
        try
        {
            release = transport.TryCreateSocketAbort();
        }
        catch
        {
            return;
        }

        if (release is null)
            return;

        try
        {
            release.Abort();
        }
        catch
        {
        }
    }

    internal static OfflineUpdateSshManagedException SudoRejected(string errorTail) =>
        new(
            OfflineUpdateSshManagedFailure.SudoUnavailable,
            errorTail.Length == 0
                ? "The sudo prompt was repeated after the password was sent, so the password was rejected. The password was sent once and is not retried."
                : "The sudo prompt was repeated after the password was sent, so the password was rejected. The password was sent once and is not retried. Device message: " + errorTail);

    internal static OfflineUpdateSshManagedFailure ClassifyStartupFailure(string errorTail, bool passwordSupplied)
    {
        if (errorTail.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
            errorTail.Contains("no tty present", StringComparison.OrdinalIgnoreCase) ||
            errorTail.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase) ||
            errorTail.Contains("not allowed", StringComparison.OrdinalIgnoreCase) ||
            errorTail.Contains("sudo: command not found", StringComparison.OrdinalIgnoreCase) ||
            errorTail.Contains("sudo: not found", StringComparison.OrdinalIgnoreCase) ||
            errorTail.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
            (passwordSupplied && errorTail.Contains("try again", StringComparison.OrdinalIgnoreCase)))
        {
            return OfflineUpdateSshManagedFailure.SudoUnavailable;
        }

        return OfflineUpdateSshManagedFailure.UpdaterUnavailable;
    }

    internal static string BuildStartupFailureMessage(OfflineUpdateSshManagedFailure reason, string errorTail)
    {
        var prefix = reason == OfflineUpdateSshManagedFailure.SudoUnavailable
            ? "The tablet could not start the offline updater through sudo. Confirm that the account may run the fixed updater command."
            : "The tablet is reachable, but the offline update components did not start. The bootstrap deployment may not have installed them.";

        return errorTail.Length == 0 ? prefix : prefix + " Device message: " + errorTail;
    }

    private static async Task PumpStdoutAsync(Stream stream, OfflineUpdateBoundedText target, CancellationToken token)
    {
        var buffer = new byte[4096];

        // One decoder per stream with an explicit end-of-stream flush, exactly
        // as in the stderr pump: characters split across reads are carried into
        // the next read instead of being decoded per chunk.
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                {
                    var tail = decoder.GetChars(buffer, 0, 0, chars, 0, flush: true);
                    if (tail > 0)
                        target.Append(new string(chars, 0, tail));
                    return;
                }

                var count = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                if (count > 0)
                    target.Append(new string(chars, 0, count));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken token)
    {
        var buffer = new byte[1];
        var line = new List<byte>(64);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
                return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            if (buffer[0] == (byte)'\n')
                return Encoding.UTF8.GetString(line.ToArray());
            if (line.Count >= maxBytes)
                throw new IOException("The updater startup line exceeded the allowed length.");
            line.Add(buffer[0]);
        }
    }

    /// <summary>
    /// Waits a bounded time for the stderr pump to reach end of stream, then
    /// returns the diagnostic tail. Cancellation of the caller's token
    /// propagates; a pump that is merely slow is bounded by the settle timeout.
    /// </summary>
    private static async Task<string> SettleErrorTailAsync(
        OfflineUpdateSshElevatedProcess elevated,
        CancellationToken token)
    {
        try
        {
            await elevated.StderrDrained.WaitAsync(ErrorTailSettleTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        return elevated.ErrorTail;
    }

    private static async Task DrainAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        catch
        {
        }
    }
}

/// <summary>
/// The updater channel after the ready marker was observed. Disposal waits for
/// the bounded stderr pump before releasing the command, so no task is left
/// reading a disposed SSH command.
/// </summary>
internal sealed class OfflineUpdateSshStartedUpdater : IAsyncDisposable
{
    private readonly OfflineUpdateSshElevatedProcess _elevated;

    internal OfflineUpdateSshStartedUpdater(OfflineUpdateSshElevatedProcess elevated)
    {
        _elevated = elevated;
    }

    internal IOfflineUpdateSshChannel Channel => _elevated.Channel;

    internal string ErrorTail => _elevated.ErrorTail;

    public ValueTask DisposeAsync() => _elevated.DisposeAsync();
}
