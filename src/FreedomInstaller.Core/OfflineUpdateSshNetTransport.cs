using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FreedomInstaller.Core;

internal sealed class OfflineUpdateSshNetTransportFactory : IOfflineUpdateSshTransportFactory
{
    public IOfflineUpdateSshTransport Create(OfflineUpdateSshPasswordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new OfflineUpdateSshNetTransport(options);
    }
}

/// <summary>
/// SSH.NET implementation of the managed transport. The peer host key is
/// captured in a credential-free discovery handshake, the approval decision is
/// taken after that handshake has been closed, and the single password attempt
/// then has to present exactly the approved key. Only password authentication is
/// configured, and negotiation is restricted to the single ed25519 algorithm
/// that the enrollment/trust contract accepts.
/// </summary>
internal sealed class OfflineUpdateSshNetTransport : IOfflineUpdateSshTransport
{
    private const string HostKeyAlgorithm = "ssh-ed25519";
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandDrainTimeout = TimeSpan.FromSeconds(5);

    // The discovery handshake never waits for a person, so one network budget is
    // enough for it even when the caller allows minutes for the whole connect.
    private static readonly TimeSpan MaxDiscoveryTimeout = TimeSpan.FromSeconds(30);

    private readonly OfflineUpdateSshPasswordOptions _options;
    private SshClient? _client;
    private int _disposed;

    internal OfflineUpdateSshNetTransport(OfflineUpdateSshPasswordOptions options)
    {
        _options = options;
    }

    public void Connect(TimeSpan timeout, Func<OfflineUpdateSshHostKey, bool> approveHostKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(approveHostKey);
        token.ThrowIfCancellationRequested();

        // No credential exists yet, so the discovery handshake only has to be
        // bounded by the network timeout; it is disposed before any prompt.
        var captured = DiscoverHostKey(BoundDiscoveryTimeout(timeout), token);
        token.ThrowIfCancellationRequested();

        // The human decision runs with no SSH handshake open. SSH.NET's own
        // ConnectionInfo.Timeout therefore cannot expire while a host-key
        // dialog waits for the user.
        var gate = new OfflineUpdateSshHostKeyGate(captured);
        gate.Approve(approveHostKey, token);

        token.ThrowIfCancellationRequested();

        // One password attempt, only against the key that was just approved.
        AuthenticateWithPassword(timeout, gate, token);
    }

    /// <summary>
    /// Runs a one-shot command while draining stdout/stderr into bounded tails.
    /// SSH.NET creates both pipes with the command and keeps those instances
    /// for its first execution, so they are captured before ExecuteAsync
    /// starts: a command that finishes immediately has already written its
    /// raw bytes into them, and the pumps stay the only readers at every
    /// speed. Completion is taken from the non-draining ExecuteAsync instead
    /// of EndExecute: EndExecute hands back the command's accumulated Result,
    /// which makes it compete with the independent stream readers and can take
    /// the output away from them.
    /// </summary>
    public async Task<OfflineUpdateSshProbeResult> RunAsync(
        string command,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var client = _client ?? throw new ObjectDisposedException(nameof(OfflineUpdateSshNetTransport));
        var sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = timeout;

        var stdout = new OfflineUpdateBoundedText(maxOutputBytes);
        var stderr = new OfflineUpdateBoundedText(maxOutputBytes);

        Stream output;
        Stream error;
        try
        {
            // Captured before the command runs on purpose: these are the pipe
            // instances the execution writes into and SSH.NET keeps them for
            // the command's whole life. Bytes an instant command produced are
            // therefore still buffered here when the pumps read them.
            output = sshCommand.OutputStream;
            error = sshCommand.ExtendedOutputStream;
        }
        catch
        {
            DisposeQuietly(sshCommand);
            throw;
        }

        using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        // ExecuteAsync starts the command and completes when it exits; only its
        // completion is consumed, so the pumps below remain the readers of a
        // normally streaming command. The command is started exactly once and
        // never retried.
        Task execution;
        try
        {
            execution = sshCommand.ExecuteAsync(CancellationToken.None);
        }
        catch
        {
            DisposeQuietly(sshCommand);
            throw;
        }

        // Streaming and fast completion take the same path: the pumps read the
        // raw bytes from the pipes captured above and copy them into the
        // bounded tails. Nothing is rebuilt from a decoded Result or Error.
        var stdoutTask = PumpAsync(output, stdout, ioCts.Token);
        var stderrTask = PumpAsync(error, stderr, ioCts.Token);
        var completed = false;

        try
        {
            try
            {
                await execution.WaitAsync(timeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Cancel(sshCommand);
                await WaitQuietlyAsync(execution).ConfigureAwait(false);
                throw;
            }
            catch (SshOperationTimeoutException ex)
            {
                Cancel(sshCommand);
                await WaitQuietlyAsync(execution).ConfigureAwait(false);
                throw new TimeoutException("The SSH command did not finish in time.", ex);
            }
            catch (OperationCanceledException)
            {
                Cancel(sshCommand);
                await WaitQuietlyAsync(execution).ConfigureAwait(false);
                throw;
            }

            await DrainQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            completed = true;

            return new OfflineUpdateSshProbeResult(
                TryExitStatus(sshCommand),
                stdout.Snapshot(),
                stderr.Snapshot());
        }
        finally
        {
            await ioCts.CancelAsync().ConfigureAwait(false);
            if (!completed)
            {
                Cancel(sshCommand);
                await WaitQuietlyAsync(execution).ConfigureAwait(false);
                await DrainQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            }

            if (!execution.IsCompleted)
                _ = ObserveAsync(execution);

            if (execution.IsCompleted && stdoutTask.IsCompleted && stderrTask.IsCompleted)
                DisposeQuietly(sshCommand);
        }
    }

    public IOfflineUpdateSshChannel Start(string command)
    {
        var client = _client ?? throw new ObjectDisposedException(nameof(OfflineUpdateSshNetTransport));
        var sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = TimeSpan.FromHours(6);

        // Both pipes exist from construction and are the instances the single
        // execution writes into, so they are captured before it starts. A short
        // command (passwordless sudo is the common case) has then already
        // delivered its raw bytes into these pipes, and the caller reads them
        // without any decoded-Result reconstruction.
        Stream output;
        Stream error;
        try
        {
            output = sshCommand.OutputStream;
            error = sshCommand.ExtendedOutputStream;
        }
        catch
        {
            DisposeQuietly(sshCommand);
            throw;
        }

        // ExecuteAsync is used for the same reason as in RunAsync: EndExecute
        // would return (and drain) the accumulated Result, taking output away
        // from the channel.
        Task execution;
        try
        {
            execution = sshCommand.ExecuteAsync(CancellationToken.None);
        }
        catch
        {
            DisposeQuietly(sshCommand);
            throw;
        }

        // A command that already failed must report that failure; its pipes are
        // not a substitute for the connection error.
        if (execution.IsFaulted)
        {
            try
            {
                execution.GetAwaiter().GetResult();
            }
            finally
            {
                DisposeQuietly(sshCommand);
            }

            throw new OfflineUpdateSshTransportException(
                "The SSH command failed before its channel became available.");
        }

        Stream input;
        try
        {
            input = sshCommand.CreateInputStream();
        }
        catch (Exception ex) when (IsCompletedStdinLifecycleFailure(ex, execution, () => sshCommand.ExitStatus))
        {
            // SSH.NET only exposes stdin while the command is still running. A
            // command that finished normally before stdin was acquired can no
            // longer accept input, but its output is already buffered in the
            // captured pipes: hand out the channel with closed stdin instead
            // of failing or restarting a command that ran to completion.
            input = ClosedStandardInput.Instance;
        }
        catch
        {
            // Active or disconnected: the failure is real and must be seen, not
            // replaced by an empty but "successful" channel.
            AbandonCommand(sshCommand, execution);
            throw;
        }

        return new SshNetChannel(sshCommand, input, output, error, execution);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var client = _client;
        _client = null;
        if (client is null)
            return;

        ReleaseClient(client);
    }

    private static TimeSpan BoundDiscoveryTimeout(TimeSpan timeout) =>
        timeout < MaxDiscoveryTimeout ? timeout : MaxDiscoveryTimeout;

    /// <summary>
    /// Reads the peer host key without offering any credential. SSH.NET always
    /// performs key exchange before authentication, so a session that is
    /// deliberately unauthenticated still yields the key, and any rejection
    /// after key exchange is irrelevant here.
    /// </summary>
    private OfflineUpdateSshHostKey DiscoverHostKey(TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var connectionInfo = new ConnectionInfo(
            _options.Host,
            _options.Port,
            _options.UserName,
            new NoneAuthenticationMethod(_options.UserName))
        {
            Timeout = timeout,
            RetryAttempts = 1,
        };

        RestrictHostKeyAlgorithms(connectionInfo);

        OfflineUpdateSshHostKey? captured = null;
        var rejectedKeyType = false;
        var client = new SshClient(connectionInfo);

        client.HostKeyReceived += (_, args) =>
        {
            // Only the first presentation is the key this attempt is about;
            // nothing here may ask the user anything.
            if (captured is not null)
                return;

            var keyType = args.HostKeyName ?? string.Empty;
            if (!string.Equals(keyType, HostKeyAlgorithm, StringComparison.Ordinal))
            {
                args.CanTrust = false;
                rejectedKeyType = true;
                return;
            }

            captured = new OfflineUpdateSshHostKey(keyType, (byte[])args.HostKey.Clone());
            args.CanTrust = true;
        };

        try
        {
            client.Connect();
        }
        catch (Exception ex) when (captured is null)
        {
            throw new OfflineUpdateSshTransportException(
                rejectedKeyType
                    ? "The tablet presented a host-key type other than ssh-ed25519, which the trust contract does not accept."
                    : $"The SSH connection to {_options.Host}:{_options.Port} failed before the tablet presented its host key: {ex.Message}",
                ex);
        }
        catch (Exception)
        {
            // The key was already read; an unauthenticated session is expected
            // to be refused after key exchange.
        }
        finally
        {
            ReleaseClient(client);
        }

        return captured ?? throw new OfflineUpdateSshTransportException(
            "The tablet completed key exchange without presenting a host key.");
    }

    private void AuthenticateWithPassword(TimeSpan timeout, OfflineUpdateSshHostKeyGate gate, CancellationToken token)
    {
        var connectionInfo = new ConnectionInfo(
            _options.Host,
            _options.Port,
            _options.UserName,
            new PasswordAuthenticationMethod(_options.UserName, _options.Password))
        {
            Timeout = timeout,
            // A rejected password is reported once; never retry credentials.
            RetryAttempts = 1,
        };

        RestrictHostKeyAlgorithms(connectionInfo);

        var keyChanged = false;
        var client = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        };

        client.HostKeyReceived += (_, args) =>
        {
            // Runs for the authenticated handshake and for every later rekey.
            // Only the captured, approved key is trusted, and the approval
            // callback is never invoked from here, so a rekey cannot prompt.
            var accepted = gate.Accepts(args.HostKeyName, args.HostKey);
            args.CanTrust = accepted;
            if (!accepted)
                keyChanged = true;
        };

        _client = client;

        try
        {
            client.Connect();
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (keyChanged)
        {
            throw new OfflineUpdateSshHostKeyRejectedException(
                "The tablet presented a different host key between approval and authentication, so no password was sent.",
                ex);
        }
        catch (SshAuthenticationException ex)
        {
            throw new OfflineUpdateSshAuthenticationException(
                "The tablet rejected the SSH password authentication.",
                ex);
        }
        catch (Exception ex)
        {
            throw new OfflineUpdateSshTransportException(
                $"The SSH connection to {_options.Host}:{_options.Port} failed: {ex.Message}",
                ex);
        }
    }

    private static void ReleaseClient(SshClient client)
    {
        var cleanup = Task.Run(
            () =>
            {
                try
                {
                    client.Disconnect();
                }
                catch
                {
                }

                try
                {
                    client.Dispose();
                }
                catch
                {
                }
            },
            CancellationToken.None);

        try
        {
            cleanup.Wait(DisconnectTimeout);
        }
        catch
        {
        }
    }

    private static void RestrictHostKeyAlgorithms(ConnectionInfo connectionInfo)
    {
        var algorithms = connectionInfo.HostKeyAlgorithms;
        foreach (var name in algorithms.Keys.ToArray())
        {
            if (!string.Equals(name, HostKeyAlgorithm, StringComparison.Ordinal))
                algorithms.Remove(name);
        }

        if (!algorithms.ContainsKey(HostKeyAlgorithm))
        {
            throw new OfflineUpdateSshTransportException(
                "The SSH client does not offer the ssh-ed25519 host-key algorithm required by the tablet trust contract.");
        }
    }

    private static int TryExitStatus(SshCommand command)
    {
        try
        {
            return command.ExitStatus ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// True only for an execution that finished successfully and reported an
    /// exit status. A running, disconnected or cancelled command is never
    /// treated as completed: its failure has to reach the caller instead of
    /// being replaced by a channel built from buffered output. The status
    /// accessor is guarded because SSH.NET throws from it once the command is
    /// gone.
    /// </summary>
    internal static bool HasCompletedWithStatus(Task execution, Func<int?> exitStatus)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(exitStatus);

        if (!execution.IsCompletedSuccessfully)
            return false;

        try
        {
            return exitStatus() is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True only for SSH.NET's "no running command" standard-input failure on a
    /// command that has already finished normally and reported an exit status.
    /// A disconnect, a cancellation, a missing status or any other error is
    /// never suppressed: those failures have to reach the caller.
    /// </summary>
    internal static bool IsCompletedStdinLifecycleFailure(
        Exception failure,
        Task execution,
        Func<int?> exitStatus)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // ObjectDisposedException also derives from InvalidOperationException,
        // but a disposed command is a real defect rather than a lifecycle race.
        return failure is InvalidOperationException and not ObjectDisposedException &&
               HasCompletedWithStatus(execution, exitStatus);
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Observes a command completion task without letting a late disconnect
    /// become an unobserved task exception. The failure still shows up through
    /// the channel's exit status, exactly as it did when EndExecute was owned
    /// by the channel.
    /// </summary>
    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Copies the bytes SSH.NET's output pipes hold into the caller's bounded
    /// tail. The same reader serves a command that is still streaming and one
    /// that finished before the pumps started, because the pipes were captured
    /// with the command: already buffered bytes are read like any other data.
    /// A pipe read can stop in the middle of a multi-byte character at any
    /// speed, so the decoder carries that partial sequence into the next read.
    /// </summary>
    internal static async Task PumpAsync(Stream stream, OfflineUpdateBoundedText target, CancellationToken token)
    {
        var bytes = new byte[4096];

        // The decoder may carry up to three bytes of an unfinished sequence
        // into the next read, so a full 4096-byte read plus that state can
        // decode into more UTF-16 code units than the read has bytes: a
        // completed four-byte character is a surrogate pair, and the carried
        // prefix can also surface as a replacement character. Sizing the
        // destination from the encoding's own worst-case promise keeps a
        // single read from overrunning it and losing the whole output.
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                {
                    var tail = decoder.GetChars(bytes, 0, 0, chars, 0, flush: true);
                    if (tail > 0)
                        target.Append(new string(chars, 0, tail));
                    return;
                }

                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
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

    private static async Task DrainQuietlyAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(CommandDrainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        catch
        {
        }
    }

    private static async Task WaitQuietlyAsync(Task task)
    {
        try
        {
            await task.WaitAsync(CommandDrainTimeout).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void Cancel(SshCommand command)
    {
        try
        {
            command.CancelAsync();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Best-effort teardown for a command that started executing but whose
    /// channel could not be handed to the caller: cancel it remotely, wait a
    /// bounded time for ExecuteAsync to finish so the command is not disposed
    /// mid-use, and then release it. The caller rethrows the original failure
    /// and the command is never started again.
    /// </summary>
    private static void AbandonCommand(SshCommand command, Task execution)
    {
        Cancel(command);

        try
        {
            execution.Wait(CommandDrainTimeout);
        }
        catch
        {
        }

        if (!execution.IsCompleted)
        {
            // Keep observing a command that outlived the bounded wait so a
            // later disconnect cannot surface as an unobserved failure.
            _ = ObserveAsync(execution);
            return;
        }

        DisposeQuietly(command);
    }

    private sealed class SshNetChannel : IOfflineUpdateSshChannel
    {
        private readonly SshCommand _command;
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly Stream _error;
        private readonly Task _completion;

        internal SshNetChannel(SshCommand command, Stream input, Stream output, Stream error, Task execution)
        {
            _command = command;
            _input = input;
            _output = output;
            _error = error;
            _completion = ObserveAsync(execution);
        }

        public Stream StandardInput => _input;

        public Stream StandardOutput => _output;

        public Stream StandardError => _error;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _command.ExitStatus.HasValue;
                }
                catch
                {
                    return true;
                }
            }
        }

        public int ExitStatus
        {
            get
            {
                try
                {
                    return _command.ExitStatus ?? -1;
                }
                catch
                {
                    return -1;
                }
            }
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            if (!_completion.Wait(timeout))
            {
                Cancel();
                try
                {
                    _completion.Wait(CommandDrainTimeout);
                }
                catch
                {
                }
            }

            return _completion.IsCompleted && HasExited;
        }

        public void Cancel()
        {
            try
            {
                _command.CancelAsync();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Cancel();
            try
            {
                _completion.Wait(CommandDrainTimeout);
            }
            catch
            {
            }

            // Never dispose the command while ExecuteAsync is still using it.
            if (!_completion.IsCompleted)
                return;

            DisposeQuietly(_command);
        }
    }

    /// <summary>
    /// Standard input of a command that already exited. Reads report end of
    /// stream, flushing is a no-op, and a write fails with IOException so a
    /// caller that would have sent a sudo password or an upload observes the
    /// closed channel instead of hanging or crashing the process.
    /// </summary>
    internal sealed class ClosedStandardInput : Stream
    {
        internal static readonly ClosedStandardInput Instance = new();

        private const string ClosedMessage =
            "The remote command has already completed; its standard input is closed.";

        private ClosedStandardInput()
        {
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException(ClosedMessage);

        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException(ClosedMessage);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException(ClosedMessage));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException(ClosedMessage));
    }
}

/// <summary>
/// The host-key decision of one managed connection attempt. The peer key is
/// captured during the credential-free discovery handshake, the approval
/// callback runs after that handshake has been closed, and every later
/// presentation of a key -- the authenticated handshake and any rekey -- is
/// compared to the captured key without asking again.
/// </summary>
internal sealed class OfflineUpdateSshHostKeyGate
{
    private readonly OfflineUpdateSshHostKey _captured;
    private int _approved;

    internal OfflineUpdateSshHostKeyGate(OfflineUpdateSshHostKey captured)
    {
        _captured = captured;
    }

    internal OfflineUpdateSshHostKey Captured => _captured;

    internal bool IsApproved => Volatile.Read(ref _approved) != 0;

    /// <summary>
    /// True only for the exact key presented during discovery, and only after
    /// that key was approved. A rekey is answered from this same decision, so
    /// it can never raise a second prompt.
    /// </summary>
    internal bool Accepts(string? keyType, ReadOnlySpan<byte> keyBlob) =>
        IsApproved &&
        string.Equals(keyType, _captured.KeyType, StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(keyBlob, _captured.KeyBlob);

    /// <summary>
    /// Runs the approval callback with no SSH handshake in flight, so a UI
    /// prompt may take as long as the caller allows. Cancellation is checked
    /// before and after the callback; nothing is trusted unless the callback
    /// returned true and the attempt was not cancelled.
    /// </summary>
    internal void Approve(Func<OfflineUpdateSshHostKey, bool> approveHostKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(approveHostKey);
        token.ThrowIfCancellationRequested();

        bool approved;
        try
        {
            approved = approveHostKey(_captured);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OfflineUpdateSshHostKeyRejectedException(
                "The tablet host key could not be approved, so no password was sent.",
                ex);
        }

        token.ThrowIfCancellationRequested();

        if (!approved)
        {
            throw new OfflineUpdateSshHostKeyRejectedException(
                "The tablet host key was not approved, so no password was sent.");
        }

        Volatile.Write(ref _approved, 1);
    }
}
