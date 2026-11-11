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
    /// SSH.NET's accumulated Result/Error strings are never touched, so a
    /// chatty command cannot grow memory without limit.
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
        var asyncResult = sshCommand.BeginExecute();
        using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stdoutTask = PumpAsync(sshCommand.OutputStream, stdout, ioCts.Token);
        var stderrTask = PumpAsync(sshCommand.ExtendedOutputStream, stderr, ioCts.Token);
        var endTask = Task.Run(() => sshCommand.EndExecute(asyncResult), CancellationToken.None);
        var completed = false;

        try
        {
            try
            {
                await endTask.WaitAsync(timeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Cancel(sshCommand);
                await WaitQuietlyAsync(endTask).ConfigureAwait(false);
                throw;
            }
            catch (SshOperationTimeoutException ex)
            {
                Cancel(sshCommand);
                await WaitQuietlyAsync(endTask).ConfigureAwait(false);
                throw new TimeoutException("The SSH command did not finish in time.", ex);
            }
            catch (OperationCanceledException)
            {
                Cancel(sshCommand);
                await WaitQuietlyAsync(endTask).ConfigureAwait(false);
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
                await WaitQuietlyAsync(endTask).ConfigureAwait(false);
                await DrainQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            }

            if (endTask.IsCompleted && stdoutTask.IsCompleted && stderrTask.IsCompleted)
            {
                try
                {
                    sshCommand.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    public IOfflineUpdateSshChannel Start(string command)
    {
        var client = _client ?? throw new ObjectDisposedException(nameof(OfflineUpdateSshNetTransport));
        var sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = TimeSpan.FromHours(6);

        // SSH.NET only exposes the command's streams while it is executing:
        // CreateInputStream() rejects a command whose execution has not begun,
        // and the output pipes are attached to the opened channel. Execution
        // therefore has to start before the streams are taken.
        IAsyncResult asyncResult;
        try
        {
            asyncResult = sshCommand.BeginExecute();
        }
        catch
        {
            try
            {
                sshCommand.Dispose();
            }
            catch
            {
            }

            throw;
        }

        try
        {
            var output = sshCommand.OutputStream;
            var error = sshCommand.ExtendedOutputStream;
            var input = sshCommand.CreateInputStream();
            return new SshNetChannel(sshCommand, input, output, error, asyncResult);
        }
        catch
        {
            AbandonCommand(sshCommand, asyncResult);
            throw;
        }
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

    private static async Task PumpAsync(Stream stream, OfflineUpdateBoundedText target, CancellationToken token)
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
    /// bounded time for EndExecute so the command is not disposed mid-use, and
    /// then release it. The caller rethrows the original failure.
    /// </summary>
    private static void AbandonCommand(SshCommand command, IAsyncResult asyncResult)
    {
        Cancel(command);

        var endTask = Task.Run(() => command.EndExecute(asyncResult), CancellationToken.None);
        try
        {
            endTask.Wait(CommandDrainTimeout);
        }
        catch
        {
        }

        if (!endTask.IsCompleted)
            return;

        try
        {
            command.Dispose();
        }
        catch
        {
        }
    }

    private sealed class SshNetChannel : IOfflineUpdateSshChannel
    {
        private readonly SshCommand _command;
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly Stream _error;
        private readonly Task _endTask;

        internal SshNetChannel(SshCommand command, Stream input, Stream output, Stream error, IAsyncResult asyncResult)
        {
            _command = command;
            _input = input;
            _output = output;
            _error = error;
            _endTask = Task.Run(
                () =>
                {
                    try
                    {
                        _command.EndExecute(asyncResult);
                    }
                    catch
                    {
                    }
                },
                CancellationToken.None);
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
            if (!_endTask.Wait(timeout))
            {
                Cancel();
                try
                {
                    _endTask.Wait(CommandDrainTimeout);
                }
                catch
                {
                }
            }

            return _endTask.IsCompleted && HasExited;
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
                _endTask.Wait(CommandDrainTimeout);
            }
            catch
            {
            }

            // Never dispose the command while EndExecute is still using it.
            if (!_endTask.IsCompleted)
                return;

            try
            {
                _command.Dispose();
            }
            catch
            {
            }
        }
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
