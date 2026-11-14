using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
    private readonly Func<IPAddress, IPAddress?> _resolveSelectedSource;
    private readonly OfflineUpdateSshClientLease _clientLease = new();

    internal OfflineUpdateSshNetTransport(OfflineUpdateSshPasswordOptions options)
        : this(options, OfflineUpdateSshUsbRoute.ResolveSelectedSource)
    {
    }

    internal OfflineUpdateSshNetTransport(
        OfflineUpdateSshPasswordOptions options,
        Func<IPAddress, IPAddress?> resolveSelectedSource)
    {
        _options = options;
        _resolveSelectedSource = resolveSelectedSource ??
            throw new ArgumentNullException(nameof(resolveSelectedSource));
    }

    public void Connect(TimeSpan timeout, Func<OfflineUpdateSshHostKey, bool> approveHostKey, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(approveHostKey);
        token.ThrowIfCancellationRequested();
        if (_clientLease.IsDisposed)
            throw new ObjectDisposedException(nameof(OfflineUpdateSshNetTransport));

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
        var client = _clientLease.Current as SshClient
            ?? throw new ObjectDisposedException(nameof(OfflineUpdateSshNetTransport));
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
        var client = _clientLease.Current as SshClient
            ?? throw new ObjectDisposedException(nameof(OfflineUpdateSshNetTransport));
        var sshCommand = client.CreateCommand(command);

        // SSH.NET's command timeout stays at its infinite default on purpose.
        // This channel carries the long-lived updater RPC and the preparation
        // commands, whose real work can outlast any fixed ceiling -- a session
        // that is still exchanging frames must not be killed at a wall-clock
        // mark. Liveness is bounded per request instead: the caller's own
        // timeout and cancellation token cover each exchange, and disposing the
        // channel cancels the command.
        //
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

    /// <summary>
    /// Resolves the socket of the client that is actually authenticated and
    /// in use. The pinned SSH.NET keeps it behind internal members, so the
    /// adapter below either finds the exact socket or reports that it cannot;
    /// there is no fallback to a transfer that no watchdog can break.
    /// </summary>
    public IOfflineUpdateSshUploadGuard? TryCreateUploadGuard(TimeSpan sendTimeout) =>
        _clientLease.Current is SshClient client
            ? OfflineUpdateSshNetUploadGuard.TryCreate(client, sendTimeout)
            : null;

    /// <summary>
    /// Same exact-socket resolution as the upload guard, without applying a
    /// send timeout: teardown only needs to break a writer that a cancelled
    /// exchange may still hold, and the surviving RPC traffic must keep
    /// SSH.NET's own socket budget.
    /// </summary>
    public IOfflineUpdateSshSocketAbort? TryCreateSocketAbort() =>
        _clientLease.Current is SshClient client
            ? OfflineUpdateSshNetSocketAbort.TryCreate(client)
            : null;

    public void Dispose()
    {
        // Atomically claim the installed client and mark the lease disposed.
        // A client that loses this race is released by its own late-acquisition
        // path before it is ever connected.
        if (_clientLease.Release() is SshClient client)
            ReleaseClient(client);
    }

    /// <summary>
    /// Re-validates the USB source route immediately before a managed
    /// handshake. The route can change between the key preflight and the
    /// password handshake, so each handshake gets its own decision. Wi-Fi
    /// (no bind address) performs no lookup.
    /// </summary>
    internal void VerifySelectedUsbSource() =>
        OfflineUpdateSshUsbRoute.VerifySelectedSource(_options.Host, _options.BindAddress, _resolveSelectedSource);

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

        // Key preflight: the OS-selected source must still be the USB bind
        // address before this handshake sends anything.
        VerifySelectedUsbSource();

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
        token.ThrowIfCancellationRequested();

        // Password handshake: re-check the USB route against the destination
        // actually used by this connection attempt, not the earlier preflight.
        VerifySelectedUsbSource();

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

        // Atomic ownership handoff: either the lease installs this client for
        // later commands, or disposal already won and this late client is
        // released before it ever connects. No lock is held across Connect.
        if (!_clientLease.TryAcquire(client))
        {
            ReleaseClient(client);
            throw new ObjectDisposedException(nameof(OfflineUpdateSshNetTransport));
        }

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
            // A command whose execution already completed needs no remote
            // cancel, and asking for one would take the session write lock for
            // a signal nobody can act on. That matters after an upload guard
            // shut the socket down: cleanup must not queue a send behind the
            // lock that the aborted transfer may still have been holding.
            if (!_completion.IsCompleted)
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
/// Version-pinned adapter for the one SSH.NET detail this project needs and the
/// library does not expose: the socket of an authenticated session. The pinned
/// package keeps <c>BaseClient.Session</c> internal and the socket in a private
/// <c>Session._socket</c> field. Resolution is all-or-nothing; a missing or
/// renamed member yields null, and the upload then fails closed instead of
/// falling back to a transfer no watchdog can break. This is deliberately
/// limited to the exact pinned version and is verified by a metadata test.
/// </summary>
internal static class OfflineUpdateSshNetSessionSocket
{
    private const string SessionMemberName = "Session";
    private const string SocketFieldName = "_socket";
    private const BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly PropertyInfo? SessionProperty =
        typeof(BaseClient).GetProperty(SessionMemberName, InstanceMembers);

    /// <summary>
    /// True when the pinned SSH.NET metadata exposes both members the upload
    /// guard depends on. A package bump that changes either shape has to fail
    /// this contract instead of silently degrading uploads.
    /// </summary>
    internal static bool HasPinnedMembers =>
        SessionProperty is not null &&
        (FindSocketField(SessionProperty.PropertyType) is not null ||
         FindSocketField(typeof(BaseClient).Assembly.GetType("Renci.SshNet.Session")) is not null);

    internal static Socket? TryGetSocket(BaseClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (SessionProperty is null)
            return null;

        object? session;
        try
        {
            session = SessionProperty.GetValue(client);
        }
        catch
        {
            return null;
        }

        if (session is null)
            return null;

        // The property type and the live instance are usually the same type;
        // walking the instance keeps a subclass or an interface-typed property
        // from hiding the private field.
        var field = FindSocketField(session.GetType()) ?? FindSocketField(SessionProperty.PropertyType);
        if (field is null)
            return null;

        try
        {
            return field.GetValue(session) as Socket;
        }
        catch
        {
            return null;
        }
    }

    private static FieldInfo? FindSocketField(Type? type)
    {
        while (type is not null)
        {
            var field = type.GetField(SocketFieldName, InstanceMembers);
            if (field is not null)
                return field;

            type = type.BaseType;
        }

        return null;
    }
}

/// <summary>
/// Upload-only abort handle over the exact socket of an authenticated SSH
/// session. Shutting that socket down is the only way to break a send SSH.NET
/// performs synchronously while holding its session write lock:
/// <c>SshCommand.Cancel</c> and <c>Disconnect</c> travel through that same lock
/// and are never used here. Creation applies a finite send timeout, and
/// disposal restores the previous value when the transfer ended without an
/// abort.
/// </summary>
internal sealed class OfflineUpdateSshNetUploadGuard : IOfflineUpdateSshUploadGuard
{
    private readonly Socket _socket;
    private readonly int _previousSendTimeout;
    private int _aborted;
    private int _disposed;

    private OfflineUpdateSshNetUploadGuard(Socket socket, int previousSendTimeout)
    {
        _socket = socket;
        _previousSendTimeout = previousSendTimeout;
    }

    internal static OfflineUpdateSshNetUploadGuard? TryCreate(SshClient client, TimeSpan sendTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        var socket = OfflineUpdateSshNetSessionSocket.TryGetSocket(client);
        return socket is null ? null : TryCreate(socket, sendTimeout);
    }

    internal static OfflineUpdateSshNetUploadGuard? TryCreate(Socket socket, TimeSpan sendTimeout)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (sendTimeout <= TimeSpan.Zero || sendTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sendTimeout),
                "The upload send timeout must be a positive duration representable in milliseconds.");
        }

        var milliseconds = (int)Math.Ceiling(sendTimeout.TotalMilliseconds);
        try
        {
            // A socket that was already closed -- a connection torn down while
            // the guard was being created -- has nothing left to bound.
            if (socket.SafeHandle.IsClosed || socket.SafeHandle.IsInvalid)
                return null;

            // Socket.SendTimeout applies the option to the established socket,
            // so a synchronous send observes it without a second native call.
            var previous = socket.SendTimeout;
            socket.SendTimeout = milliseconds;
            return new OfflineUpdateSshNetUploadGuard(socket, previous);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or
                                   NotSupportedException or ArgumentOutOfRangeException)
        {
            // A socket that no longer accepts options cannot bound the upload;
            // the caller has to fail closed rather than start the transfer.
            return null;
        }
    }

    public bool IsAborted => Volatile.Read(ref _aborted) != 0;

    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return;

        OfflineUpdateSshNetSocketAbort.ShutdownSocket(_socket);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || IsAborted)
            return;

        // The upload finished normally, so the session keeps living: hand the
        // socket back with the timeout SSH.NET normally uses.
        try
        {
            _socket.SendTimeout = _previousSendTimeout;
        }
        catch
        {
        }
    }
}

/// <summary>
/// Bare abort handle over the exact socket of an authenticated session, used by
/// teardown. It performs the same shutdown as the upload guard -- the only way
/// to break a send SSH.NET executes synchronously under its session write lock
/// -- but it never changes the socket's send timeout, so a transient upload
/// budget can never be left behind on a long-lived RPC session.
/// </summary>
internal sealed class OfflineUpdateSshNetSocketAbort : IOfflineUpdateSshSocketAbort
{
    private readonly Socket _socket;
    private int _aborted;

    private OfflineUpdateSshNetSocketAbort(Socket socket) => _socket = socket;

    internal static OfflineUpdateSshNetSocketAbort? TryCreate(SshClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var socket = OfflineUpdateSshNetSessionSocket.TryGetSocket(client);
        return socket is null ? null : TryCreate(socket);
    }

    internal static OfflineUpdateSshNetSocketAbort? TryCreate(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        try
        {
            // A socket that is already closed has nothing left to release.
            return socket.SafeHandle.IsClosed || socket.SafeHandle.IsInvalid
                ? null
                : new OfflineUpdateSshNetSocketAbort(socket);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    public bool IsAborted => Volatile.Read(ref _aborted) != 0;

    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return;

        ShutdownSocket(_socket);
    }

    /// <summary>
    /// Shutdown first: closing alone does not reliably wake a send that is
    /// already blocked inside the OS. Both calls are best effort because a
    /// concurrent teardown may have closed the socket.
    /// </summary>
    internal static void ShutdownSocket(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
        }

        try
        {
            socket.Close();
        }
        catch
        {
        }
    }
}

/// <summary>
/// Atomic single-owner handoff between a newly constructed SSH client and
/// disposal. Exactly one party wins: either the client is installed for later
/// commands, or the lease is already disposed and the late client is reported
/// as unowned so its caller can release it before connecting. The exchange is
/// a lock-free compare-and-swap, so no lock is ever held across blocking SSH
/// I/O.
/// </summary>
internal sealed class OfflineUpdateSshClientLease
{
    private static readonly object Disposed = new();
    private object? _state;

    internal bool IsDisposed => ReferenceEquals(Volatile.Read(ref _state), Disposed);

    internal object? Current =>
        Volatile.Read(ref _state) is { } state && !ReferenceEquals(state, Disposed) ? state : null;

    /// <summary>Installs the client only while the lease is still empty.</summary>
    internal bool TryAcquire(object client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return !IsDisposed && Interlocked.CompareExchange(ref _state, client, null) is null;
    }

    /// <summary>
    /// Atomically marks the lease disposed and returns the installed client, if
    /// any. The result is returned at most once.
    /// </summary>
    internal object? Release() =>
        Interlocked.Exchange(ref _state, Disposed) is { } state && !ReferenceEquals(state, Disposed)
            ? state
            : null;
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
