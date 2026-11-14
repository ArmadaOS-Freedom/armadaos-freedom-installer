using System.Text;

namespace FreedomInstaller.Core;

/// <summary>
/// Bounded authenticated command and upload operations over an already
/// authenticated SSH connection. Arguments are validated and shell-quoted. The
/// password is never part of a command; sudo uses an explicit stderr prompt
/// handshake, and uploads are staged unprivileged for the device bootstrap to
/// verify and install.
/// </summary>
internal sealed class OfflineUpdateSshManagedSession : IOfflineUpdateManagedSession
{
    private const int MaxArguments = 64;
    private const int MaxArgumentLength = 4096;
    private const int MaxExecutableLength = 1024;
    private const int MaxRemotePathLength = 1024;
    private const int MaxCommandOutputBytes = 64 * 1024;
    private const long MaxUploadBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(30);

    // SSH.NET performs stdin writes with a synchronous socket send under the
    // session write lock. A finite send timeout bounds one such call even
    // before the absolute upload watchdog closes the socket; the pinned
    // transport nevertheless restores the previous value when the upload ends
    // normally, so the long-lived RPC traffic keeps SSH.NET's default.
    private static readonly TimeSpan UploadSendTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] PrivilegedRoots =
    {
        "/usr", "/etc", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32",
        "/boot", "/var", "/run", "/root", "/sys", "/proc", "/dev",
    };

    private readonly IOfflineUpdateSshTransport _transport;
    private readonly TimeSpan _uploadTimeout;
    private string? _sudoPassword;

    internal OfflineUpdateSshManagedSession(
        IOfflineUpdateSshTransport transport,
        string? sudoPassword = null,
        TimeSpan? uploadTimeout = null)
    {
        _transport = transport;
        _sudoPassword = sudoPassword;
        _uploadTimeout = uploadTimeout ?? UploadTimeout;
        if (_uploadTimeout <= TimeSpan.Zero ||
            _uploadTimeout == Timeout.InfiniteTimeSpan ||
            _uploadTimeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(uploadTimeout),
                "The upload timeout must be a positive finite duration supported by CancellationTokenSource.");
        }
    }

    /// <summary>Drops the elevation secret after the preparation callback has finished.</summary>
    internal void ClearSecret() => _sudoPassword = null;

    public async Task<OfflineUpdateSshCommandResult> ExecuteAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        bool useSudo,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var commandLine = BuildCommandLine(executablePath, arguments);
        try
        {
            if (useSudo)
            {
                return await OfflineUpdateSshSudo
                    .RunAsync(_transport, commandLine, _sudoPassword, CommandTimeout, MaxCommandOutputBytes, token)
                    .ConfigureAwait(false);
            }

            var result = await _transport
                .RunAsync(commandLine, CommandTimeout, MaxCommandOutputBytes, token)
                .ConfigureAwait(false);

            return new OfflineUpdateSshCommandResult(
                result.ExitStatus,
                result.StandardOutput,
                result.StandardError);
        }
        catch (TimeoutException ex)
        {
            throw new OfflineUpdateSshManagedException(
                OfflineUpdateSshManagedFailure.Timeout,
                "The authenticated command did not finish in time.",
                ex);
        }
    }

    /// <summary>
    /// Uploads to a caller-created, user-owned staging path. The original
    /// version redirected through an unprivileged shell even with
    /// <c>useSudo</c>, which never produced a root-owned file and could leak a
    /// password into the redirected input. Privileged installation is now the
    /// device bootstrap's job after it verifies the uploaded hash.
    /// </summary>
    public async Task UploadFileAsync(
        string remotePath,
        Stream content,
        bool useSudo,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
            throw new ArgumentException("Upload content must be readable.", nameof(content));

        var target = ValidateRemotePath(remotePath);
        if (useSudo)
        {
            throw new OfflineUpdateSshManagedException(
                OfflineUpdateSshManagedFailure.SudoUnavailable,
                "Privileged uploads are not supported. Stage the file in a user-owned temporary directory; the device bootstrap verifies its hash and installs it with root privileges.");
        }

        if (IsPrivilegedUploadPath(target))
        {
            throw new ArgumentException(
                "RemotePath is under a privileged system location. Stage uploads in a user-owned temporary directory and let the device bootstrap install them.",
                nameof(remotePath));
        }

        // The remote command truncates its target as soon as it starts, and the
        // staging uploads are nonempty bootstrap inputs. Read one bounded chunk
        // (honoring cancellation) before starting the remote command so an empty
        // or already-exhausted stream, seekable or not, can never truncate the
        // staging path or send `cat`.
        var firstChunk = new byte[64 * 1024];
        var firstRead = await content.ReadAsync(firstChunk.AsMemory(), token).ConfigureAwait(false);
        if (firstRead <= 0)
        {
            throw new OfflineUpdateSshManagedException(
                OfflineUpdateSshManagedFailure.Bootstrap,
                "The upload content is empty; staging uploads must contain data.");
        }

        // The read above cannot be trusted to honor the token: a stream may
        // ignore cancellation and hand back a chunk while cancellation is
        // already requested. The remote command truncates its target on start,
        // so re-check here, before the only mutating step.
        token.ThrowIfCancellationRequested();

        // `cat > path` truncates as soon as the remote command starts, and the
        // bytes travel through SSH.NET's synchronous socket send under the
        // session write lock. Neither the caller token nor a channel cancel can
        // break a send whose peer stopped reading, so capture the exact
        // authenticated socket first. When it cannot be captured the upload
        // must fail closed: an unbreakable transfer must not start at all.
        var guard = _transport.TryCreateUploadGuard(UploadSendTimeout);
        if (guard is null)
        {
            throw new OfflineUpdateSshManagedException(
                OfflineUpdateSshManagedFailure.Bootstrap,
                "The file upload was not started because the authenticated SSH socket could not be captured for a bounded abort. Reconnect and retry.");
        }

        using (guard)
        using (var deadline = new CancellationTokenSource(_uploadTimeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
        {
            // One absolute budget covers the copy, the stdin EOF and the remote
            // completion. Both the caller token and that deadline break the
            // transfer by shutting the captured socket down directly, never by
            // asking SSH.NET to cancel a command through the lock that is
            // already stuck.
            //
            // 0 = still running, 1 = remote completion, 2 = abort. Completion
            // and abort claim the same slot with a compare-and-swap, so exactly
            // one of them wins: either the socket is shut down, or a completed
            // upload leaves the healthy session alone.
            var finished = 0;
            void AbortUnlessFinished()
            {
                if (Interlocked.CompareExchange(ref finished, 2, 0) == 0)
                    guard.Abort();
            }

            using var callerRegistration = token.Register(AbortUnlessFinished);
            using var deadlineRegistration = deadline.Token.Register(AbortUnlessFinished);

            // A cancellation may have landed between the re-check above and the
            // registrations; the socket is already aborted and no remote
            // command exists yet.
            if (token.IsCancellationRequested || deadline.IsCancellationRequested)
                ThrowUploadAbort(token, deadline.Token, inner: null);

            var command = "cat > '" + target + "'";
            IOfflineUpdateSshChannel channel;
            try
            {
                channel = _transport.Start(command);
            }
            catch (Exception ex)
            {
                ThrowUploadAbort(token, deadline.Token, ex);
                throw;
            }

            var stdout = new OfflineUpdateBoundedText(4096);
            var stderr = new OfflineUpdateBoundedText(4096);
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var stdoutTask = DrainAsync(channel.StandardOutput, stdout, ioCts.Token);
            var stderrTask = DrainAsync(channel.StandardError, stderr, ioCts.Token);
            var exitTask = Task.Run(() => channel.WaitForExit(_uploadTimeout), CancellationToken.None);

            try
            {
                try
                {
                    await CopyBoundedAsync(content, channel.StandardInput, firstChunk, firstRead, MaxUploadBytes, linked.Token).ConfigureAwait(false);
                    await channel.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        channel.StandardInput.Dispose();
                    }
                    catch
                    {
                    }

                    var exited = await exitTask.WaitAsync(linked.Token).ConfigureAwait(false);
                    if (!exited)
                    {
                        ThrowUploadAbort(token, deadline.Token, inner: null);
                        throw new OfflineUpdateSshManagedException(
                            OfflineUpdateSshManagedFailure.Timeout,
                            "The file upload did not finish in time.");
                    }

                    await DrainTaskAsync(stdoutTask).ConfigureAwait(false);
                    await DrainTaskAsync(stderrTask).ConfigureAwait(false);

                    // The remote process exited and both pipes are drained.
                    // Claiming completion is the other half of the abort race:
                    // if the caller token or the deadline already owns the
                    // abort, the socket is gone and the transfer must report
                    // that cause even though the remote returned success.
                    if (Interlocked.CompareExchange(ref finished, 1, 0) != 0)
                    {
                        ThrowUploadAbort(token, deadline.Token, inner: null);
                        throw new OfflineUpdateSshManagedException(
                            OfflineUpdateSshManagedFailure.Timeout,
                            "The file upload did not finish in time.");
                    }

                    if (channel.ExitStatus != 0)
                    {
                        var tail = stderr.Snapshot();
                        throw new OfflineUpdateSshManagedException(
                            OfflineUpdateSshManagedFailure.UpdaterUnavailable,
                            "The file upload failed with exit code " +
                            channel.ExitStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            "." + (tail.Length == 0 ? string.Empty : " " + tail));
                    }
                }
                catch (Exception ex) when (ex is not OfflineUpdateSshManagedException &&
                                           (token.IsCancellationRequested || deadline.IsCancellationRequested))
                {
                    // The socket shutdown surfaces as whatever the blocked I/O
                    // throws; report the cause the caller actually requested.
                    ThrowUploadAbort(token, deadline.Token, ex);
                    throw;
                }
            }
            finally
            {
                // Anything short of a claimed remote completion is an
                // incomplete transfer: a local read or size failure, a native
                // send timeout, a cancellation or the deadline. Break the
                // captured socket here, before cleanup, while the abort can
                // still win the race; a local failure keeps its original
                // exception. A claimed completion is left untouched, so a
                // normal or nonzero remote exit never disconnects the session.
                //
                // SSH.NET is never asked to cancel from upload cleanup:
                // channel.Cancel would take the session write lock that the
                // incomplete transfer may still hold. A command that is still
                // running is torn down by the socket abort above.
                AbortUnlessFinished();
                await ioCts.CancelAsync().ConfigureAwait(false);

                await DrainTaskAsync(exitTask).ConfigureAwait(false);
                await DrainTaskAsync(stdoutTask).ConfigureAwait(false);
                await DrainTaskAsync(stderrTask).ConfigureAwait(false);
                if (exitTask.IsCompleted && stdoutTask.IsCompleted && stderrTask.IsCompleted)
                {
                    try
                    {
                        channel.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reports the abort that actually ended an upload: the caller's
    /// cancellation first, then the absolute upload deadline. Cancellation and
    /// timeout stay distinguishable even though both close the same socket, and
    /// an unrelated failure that happened to overlap a cancellation is never
    /// relabelled. Returns only when neither was requested.
    /// </summary>
    private static void ThrowUploadAbort(
        CancellationToken callerToken,
        CancellationToken deadlineToken,
        Exception? inner)
    {
        if (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The file upload was cancelled.",
                inner,
                callerToken);
        }

        if (deadlineToken.IsCancellationRequested)
        {
            throw new OfflineUpdateSshManagedException(
                OfflineUpdateSshManagedFailure.Timeout,
                "The file upload did not finish in time.",
                inner);
        }
    }

    internal static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var executable = ValidateExecutable(executablePath);
        if (arguments.Count > MaxArguments)
            throw new ArgumentOutOfRangeException(nameof(arguments), $"At most {MaxArguments} arguments are allowed.");

        var builder = new StringBuilder(executable);
        foreach (var argument in arguments)
        {
            if (argument is null)
                throw new ArgumentException("Arguments must not contain null entries.", nameof(arguments));
            if (argument.Length > MaxArgumentLength)
                throw new ArgumentOutOfRangeException(nameof(arguments), $"An argument exceeds the {MaxArgumentLength}-character limit.");
            if (argument.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("Arguments must not contain newline or null characters.", nameof(arguments));

            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string ValidateExecutable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Length > MaxExecutableLength ||
            !executablePath.StartsWith('/') ||
            executablePath.Contains("..", StringComparison.Ordinal) ||
            executablePath.Any(c => !IsSafePathChar(c)))
        {
            throw new ArgumentException(
                "ExecutablePath must be an absolute path made of letters, digits, and /._+- characters.",
                nameof(executablePath));
        }

        return executablePath;
    }

    private static string ValidateRemotePath(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        if (remotePath.Length > MaxRemotePathLength ||
            !remotePath.StartsWith('/') ||
            remotePath.EndsWith('/') ||
            remotePath.Contains("//", StringComparison.Ordinal) ||
            remotePath[1..].Split('/').Any(segment => segment is "." or ".." || segment.Length == 0) ||
            remotePath.Any(c => !IsSafePathChar(c)))
        {
            throw new ArgumentException(
                "RemotePath must be an absolute file path made of letters, digits, and /._+- characters.",
                nameof(remotePath));
        }

        return remotePath;
    }

    private static bool IsPrivilegedUploadPath(string path)
    {
        foreach (var root in PrivilegedRoots)
        {
            if (string.Equals(path, root, StringComparison.Ordinal) ||
                path.StartsWith(root + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafePathChar(char value) =>
        value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '/' or '.' or '_' or '-' or '+';

    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "''";

        var needsQuoting = false;
        foreach (var c in value)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or
                '_' or '-' or '.' or '/' or ':' or '=' or '+' or '@' or '%' or ',')
            {
                continue;
            }

            needsQuoting = true;
            break;
        }

        return needsQuoting ? "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'" : value;
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        byte[] firstChunk,
        int firstChunkLength,
        long limit,
        CancellationToken token)
    {
        await destination.WriteAsync(firstChunk.AsMemory(0, firstChunkLength), token).ConfigureAwait(false);

        // The first chunk is written, so its buffer can carry the rest of the
        // copy instead of allocating a second 64 KiB block.
        var buffer = firstChunk.AsMemory();
        long total = firstChunkLength;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
                return;

            total += read;
            if (total > limit)
                throw new OfflineUpdateSshManagedException(
                    OfflineUpdateSshManagedFailure.UpdaterUnavailable,
                    $"Upload exceeds the {limit} byte limit.");

            await destination.WriteAsync(buffer[..read], token).ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(Stream stream, OfflineUpdateBoundedText target, CancellationToken token)
    {
        var buffer = new byte[4096];

        // One decoder for the whole stream, flushed at end of stream: a read can
        // stop inside a multi-byte character, and a per-chunk decode would emit
        // replacement characters instead of the bytes the remote command wrote.
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

    private static async Task DrainTaskAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
