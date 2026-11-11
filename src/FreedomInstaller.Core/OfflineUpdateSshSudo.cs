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
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                    return;

                tracker.Feed(buffer.AsSpan(0, read));
                target.Append(Encoding.UTF8.GetString(buffer, 0, read));
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
                    throw new TimeoutException("The sudo command did not finish in time.");

                var waitSet = new List<Task>(3) { exitTask, Task.Delay(remaining, token) };
                waitSet.Add(promptPending ? elevated.FirstPrompt : elevated.RepeatedPrompt);

                var completed = await Task.WhenAny(waitSet).ConfigureAwait(false);
                if (completed == elevated.RepeatedPrompt)
                    throw SudoRejected(elevated.ErrorTail);
                if (completed == elevated.FirstPrompt)
                {
                    promptPending = false;
                    await WritePasswordOnceAsync(channel, elevated, password, token).ConfigureAwait(false);
                    continue;
                }
                if (completed == exitTask)
                    break;

                token.ThrowIfCancellationRequested();
                throw new TimeoutException("The sudo command did not finish in time.");
            }

            var exited = await exitTask.ConfigureAwait(false);
            if (!exited)
                throw new TimeoutException("The sudo command did not finish in time.");

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
                    throw new TimeoutException("The tablet did not start the offline updater in time.");

                var waitSet = new List<Task>(3) { lineTask, Task.Delay(remaining, token) };
                waitSet.Add(promptPending ? elevated.FirstPrompt : elevated.RepeatedPrompt);

                var completed = await Task.WhenAny(waitSet).ConfigureAwait(false);
                if (completed == elevated.RepeatedPrompt)
                    throw SudoRejected(elevated.ErrorTail);
                if (completed == elevated.FirstPrompt)
                {
                    promptPending = false;
                    await WritePasswordOnceAsync(channel, elevated, password, token).ConfigureAwait(false);
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

                    var detail = string.Join(
                        " ",
                        new[] { line, elevated.ErrorTail }.Where(value => !string.IsNullOrWhiteSpace(value)));
                    throw new OfflineUpdateSshManagedException(
                        OfflineUpdateSshManagedFailure.UpdaterUnavailable,
                        BuildStartupFailureMessage(
                            ClassifyStartupFailure(detail, password is not null),
                            detail));
                }
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("The tablet did not start the offline updater in time.");
            }
        }
        finally
        {
            await lineCts.CancelAsync().ConfigureAwait(false);
            if (!handedOff)
            {
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

    internal static async Task WritePasswordOnceAsync(
        IOfflineUpdateSshChannel channel,
        OfflineUpdateSshElevatedProcess elevated,
        string? password,
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

        // Mark first: a cancelled or failed write must never be retried and send
        // the secret a second time.
        elevated.MarkPasswordSent();
        var bytes = new byte[Encoding.UTF8.GetByteCount(password) + 1];
        try
        {
            var written = Encoding.UTF8.GetBytes(password, bytes);
            bytes[written] = (byte)'\n';
            await channel.StandardInput.WriteAsync(bytes, token).ConfigureAwait(false);
            await channel.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(bytes);
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
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                    return;
                target.Append(Encoding.UTF8.GetString(buffer, 0, read));
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
