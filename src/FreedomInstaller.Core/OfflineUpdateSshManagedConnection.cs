using System.Text.Json;

namespace FreedomInstaller.Core;

public readonly record struct OfflineUpdateSshHostKey(string KeyType, byte[] KeyBlob);

public sealed record OfflineUpdateSshCommandResult(int ExitStatus, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitStatus == 0;
}

/// <summary>
/// Authenticated operations available to the bootstrap component. Command
/// arguments are validated and quoted. Sudo uses an explicit stderr prompt
/// handshake: the password is written at most once and only after the unique
/// prompt marker is observed.
/// </summary>
public interface IOfflineUpdateManagedSession
{
    Task<OfflineUpdateSshCommandResult> ExecuteAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        bool useSudo,
        CancellationToken token = default);

    Task UploadFileAsync(
        string remotePath,
        Stream content,
        bool useSudo,
        CancellationToken token = default);
}

internal readonly record struct OfflineUpdateSshProbeResult(int ExitStatus, string StandardOutput, string StandardError);

internal interface IOfflineUpdateSshChannel : IDisposable
{
    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    /// <summary>Streaming stderr; callers must drain it through a bounded pump.</summary>
    Stream StandardError { get; }

    bool HasExited { get; }

    int ExitStatus { get; }

    bool WaitForExit(TimeSpan timeout);

    void Cancel();
}

/// <summary>
/// Direct abort handle for the exact socket that carries the authenticated
/// session. It closes that socket without SSH.NET's write path and without its
/// session lock, so a send the peer stopped reading can still be broken.
/// </summary>
internal interface IOfflineUpdateSshSocketAbort
{
    /// <summary>True once the captured socket has been shut down.</summary>
    bool IsAborted { get; }

    /// <summary>
    /// Shuts the captured socket down and closes it. Idempotent and safe to
    /// call from any thread, including a cancellation callback that runs while
    /// a synchronous send holds the session write lock.
    /// </summary>
    void Abort();
}

/// <summary>
/// Upload-only abort handle. It additionally owns the upload's finite send
/// timeout, which teardown must never apply: the long-lived RPC session has to
/// keep SSH.NET's own socket budget.
/// </summary>
internal interface IOfflineUpdateSshUploadGuard : IOfflineUpdateSshSocketAbort, IDisposable
{
}

internal interface IOfflineUpdateSshTransport : IDisposable
{
    /// <summary>
    /// Blocking connect. It is always invoked on a thread-pool thread so that a
    /// host-key approval callback may marshal to the UI thread.
    /// </summary>
    void Connect(TimeSpan timeout, Func<OfflineUpdateSshHostKey, bool> approveHostKey, CancellationToken token);

    Task<OfflineUpdateSshProbeResult> RunAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken token);

    IOfflineUpdateSshChannel Start(string command);

    /// <summary>
    /// Captures an upload abort handle for the exact authenticated socket and
    /// applies a finite send timeout for the upload's duration. Returns null
    /// when that socket cannot be resolved; an upload must then fail closed
    /// instead of starting a transfer it could not abort.
    /// </summary>
    IOfflineUpdateSshUploadGuard? TryCreateUploadGuard(TimeSpan sendTimeout);

    /// <summary>
    /// Captures a bare abort handle for the exact authenticated socket, without
    /// touching its send timeout. Teardown uses it to release a writer that a
    /// cancelled exchange may still hold; the upload guard's transient send
    /// budget must never be carried onto the long-lived RPC session. Returns
    /// null when the socket cannot be resolved.
    /// </summary>
    IOfflineUpdateSshSocketAbort? TryCreateSocketAbort();
}

internal interface IOfflineUpdateSshTransportFactory
{
    IOfflineUpdateSshTransport Create(OfflineUpdateSshPasswordOptions options);
}

/// <summary>
/// Managed SSH password transport with strict host-key approval before any
/// credential is sent. It also starts the fixed offline updater over the same
/// authenticated session, optionally through non-interactive sudo.
/// </summary>
public sealed class OfflineUpdateSshManagedConnection : IOfflineUpdateConnection
{
    internal const string ReadyMarker = "ARMADA-OFFLINE-UPDATER-READY-V1";
    private const string UpdaterCommand = "/usr/bin/python3 -I /usr/libexec/armada/armada-offline-update";
    private const int MaxDiagnosticsLength = 8192;
    private const int MaxFailureDetailLength = 512;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(2);

    private readonly IOfflineUpdateSshTransport _transport;
    private readonly OfflineUpdateSshStartedUpdater _updater;
    private readonly OfflineUpdateRpcClient _rpc;
    private readonly OfflineUpdateSshManagedSession _session;
    private readonly string _diagnostics;
    private int _disposed;

    private OfflineUpdateSshManagedConnection(
        IOfflineUpdateSshTransport transport,
        OfflineUpdateSshStartedUpdater updater,
        OfflineUpdateRpcClient rpc,
        OfflineUpdateSshManagedSession session,
        string diagnostics,
        JsonElement deviceInfo)
    {
        _transport = transport;
        _updater = updater;
        _rpc = rpc;
        _session = session;
        _diagnostics = diagnostics;
        DeviceInfo = deviceInfo;
    }

    public OfflineUpdateRpcClient Rpc => _rpc;

    public JsonElement DeviceInfo { get; }

    public string Diagnostics => _diagnostics;

    /// <summary>Authenticated session offered to an optional preparation callback.</summary>
    public IOfflineUpdateManagedSession Session => _session;

    public static Task<OfflineUpdateSshManagedConnection> ConnectAsync(
        OfflineUpdateSshPasswordOptions options,
        TimeSpan? requestTimeout = null,
        CancellationToken token = default) =>
        ConnectCoreAsync(options, requestTimeout, token, new OfflineUpdateSshNetTransportFactory());

    internal static async Task<OfflineUpdateSshManagedConnection> ConnectCoreAsync(
        OfflineUpdateSshPasswordOptions options,
        TimeSpan? requestTimeout,
        CancellationToken token,
        IOfflineUpdateSshTransportFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        token.ThrowIfCancellationRequested();

        var timeout = ValidateTimeout(requestTimeout ?? DefaultRequestTimeout, nameof(requestTimeout));
        var connectTimeout = options.ConnectTimeout;
        if (connectTimeout <= TimeSpan.Zero || connectTimeout == Timeout.InfiniteTimeSpan ||
            connectTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ConnectTimeout must be a positive duration of at most five minutes.");
        }

        // Reject a USB bind address that is not proven local to the device subnet
        // before anything is sent to the tablet.
        OfflineUpdateSshUsbRoute.Verify(options.Host, options.BindAddress);

        var trust = new OfflineUpdateSshHostTrustStore(options.TrustDirectory);
        IOfflineUpdateSshTransport? transport = null;
        OfflineUpdateSshStartedUpdater? updater = null;
        OfflineUpdateRpcClient? rpc = null;
        var handedOff = false;
        var socketReleased = false;

        // Both failure handlers of one attempt may observe the same broken
        // exchange; the socket is only ever released once.
        void ReleaseSocket()
        {
            if (socketReleased || transport is null)
                return;

            socketReleased = true;
            AbortTransportSocket(transport);
        }

        try
        {
            var activeTransport = factory.Create(options);
            transport = activeTransport;

            string? approvedFingerprint = null;

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var connectToken = connectCts.Token;

                // The approval callback runs under the same token as the
                // transport, so a decision that arrives after this attempt was
                // cancelled is rejected instead of being written to the trust
                // store of an abandoned connection.
                bool Approve(OfflineUpdateSshHostKey key)
                {
                    var fingerprint = VerifyHostKey(options, trust, key, connectToken);
                    if (fingerprint is null)
                        return false;

                    approvedFingerprint = fingerprint;
                    return true;
                }

                var connectTask = Task.Run(
                    () => activeTransport.Connect(connectTimeout, Approve, connectToken),
                    CancellationToken.None);

                // SSH.NET enforces connectTimeout on its own network work. This
                // outer bound additionally allows time for the human fingerprint
                // decision, which happens inside key exchange.
                var overallBound = connectTimeout + TimeSpan.FromMinutes(5);

                try
                {
                    await connectTask.WaitAsync(overallBound, token).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    connectCts.Cancel();
                    throw new OfflineUpdateSshManagedException(
                        OfflineUpdateSshManagedFailure.Timeout,
                        $"The SSH connection to {options.Host}:{options.Port} did not complete within {connectTimeout.TotalSeconds:F0} seconds plus the host-key confirmation allowance.",
                        ex);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    connectCts.Cancel();
                    throw;
                }
                catch (Exception ex) when (ex is not OfflineUpdateSshManagedException)
                {
                    throw MapConnectFailure(ex);
                }
            }

            var root = string.Equals(options.UserName, "root", StringComparison.Ordinal);
            if (!root && !options.HasPassword)
            {
                throw new OfflineUpdateSshManagedException(
                    OfflineUpdateSshManagedFailure.Authentication,
                    "The SSH password is no longer available for this connection attempt. Enter it again and retry.");
            }

            var sudoPassword = root ? null : options.Password;
            var session = new OfflineUpdateSshManagedSession(activeTransport, sudoPassword);

            try
            {
                if (options.PrepareSessionAsync is { } prepare)
                {
                    try
                    {
                        await prepare(session, token).ConfigureAwait(false);
                    }
                    catch (OfflineUpdateBootstrapException ex)
                    {
                        throw new OfflineUpdateSshManagedException(
                            ex.Reason == OfflineUpdateBootstrapFailure.SudoUnavailable
                                ? OfflineUpdateSshManagedFailure.SudoUnavailable
                                : OfflineUpdateSshManagedFailure.Bootstrap,
                            ex.Message,
                            ex);
                    }
                    finally
                    {
                        // The preparation callback is the only place that may
                        // need the elevation secret. Drop it before the updater
                        // RPC starts.
                        session.ClearSecret();
                    }
                }

                // No preparation callback also means no later consumer of the
                // elevation secret.
                session.ClearSecret();

                updater = await OfflineUpdateSshSudo
                    .StartUpdaterAsync(
                        activeTransport,
                        BuildUpdaterCommand(),
                        useSudo: !root,
                        ReadyMarker,
                        sudoPassword,
                        connectTimeout,
                        4096,
                        token)
                    .ConfigureAwait(false);
                sudoPassword = null;

                var createdRpc = new OfflineUpdateRpcClient(
                    updater.Channel.StandardInput,
                    updater.Channel.StandardOutput,
                    timeout,
                    leaveOpen: true);
                rpc = createdRpc;
                JsonElement info;
                try
                {
                    info = await createdRpc.GetInfoAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
                {
                    // The info query may have been interrupted while its
                    // synchronous stdin send was still running. Releasing the
                    // socket here keeps the updater teardown from queueing
                    // behind the session writer lock.
                    if (createdRpc.RequiresSocketRelease)
                        ReleaseSocket();

                    await updater.DisposeAsync().ConfigureAwait(false);
                    throw new OfflineUpdateSshManagedException(
                        OfflineUpdateSshManagedFailure.UpdaterUnavailable,
                        BuildInfoQueryFailureMessage(ex),
                        ex);
                }

                var diagnostics = BuildDiagnostics(options, root, approvedFingerprint);
                var connection = new OfflineUpdateSshManagedConnection(
                    activeTransport,
                    updater,
                    createdRpc,
                    session,
                    diagnostics,
                    info.Clone());

                handedOff = true;
                return connection;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OfflineUpdateSshManagedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new OfflineUpdateSshManagedException(
                    OfflineUpdateSshManagedFailure.Unreachable,
                    "The authenticated SSH session failed: " + ex.Message,
                    ex);
            }
        }
        finally
        {
            if (!handedOff)
            {
                // Ownership never reached the connection object, so this
                // teardown has to apply the same rule it does: an info query
                // that was interrupted, or that is still in flight, may have a
                // synchronous stdin send under SSH.NET's writer lock, and both
                // the updater's Cancel and the transport's Disconnect queue
                // behind it.
                if (rpc is not null && rpc.RequiresSocketRelease)
                    ReleaseSocket();

                if (rpc is not null)
                {
                    try
                    {
                        await rpc.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                if (updater is not null)
                {
                    try
                    {
                        await updater.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                try
                {
                    transport?.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // An interrupted RPC exchange can leave its synchronous stdin send
        // running under SSH.NET's session writer lock. RPC stream disposal, the
        // sudo channel's Cancel and the transport's Disconnect all queue behind
        // that lock, so the exact authenticated socket is released first. This
        // is the pinned-socket shutdown the upload guard uses, without its send
        // timeout: the long-lived RPC session must not inherit that transient
        // budget. A healthy teardown takes no handle and disconnects in order.
        if (_rpc.RequiresSocketRelease)
            AbortTransportSocket(_transport);

        try
        {
            await _rpc.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await _updater.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            _transport.Dispose();
        }
        catch
        {
        }
    }

    internal static string BuildUpdaterCommand() =>
        string.Concat(
            "/bin/sh -c 'printf \"", ReadyMarker, "\\n\"; exec ", UpdaterCommand, "'");

    /// <summary>
    /// Releases the exact authenticated socket so a pending synchronous send
    /// cannot keep SSH.NET's writer lock occupied during teardown. Best effort:
    /// a socket that cannot be resolved is left to the bounded waits of the
    /// individual cleanup steps.
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

    private static string? VerifyHostKey(
        OfflineUpdateSshPasswordOptions options,
        OfflineUpdateSshHostTrustStore trust,
        OfflineUpdateSshHostKey key,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        OfflineUpdateSshKey parsed;
        try
        {
            parsed = OfflineUpdateSshEnrollment.ParsePublicKey(
                string.Concat(key.KeyType, " ", Convert.ToBase64String(key.KeyBlob)));
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
        {
            // Only the same ed25519 key shape accepted by the enrollment flow is
            // trusted; anything else is rejected before a password is offered.
            return null;
        }

        if (options.ExpectedHostFingerprint is { Length: > 0 } expected)
        {
            // An expected fingerprint is an explicit pin supplied by the
            // caller (for example the verification harness). It must match
            // exactly; a mismatch rejects the key before authentication.
            if (!string.Equals(expected.Trim(), parsed.Fingerprint, StringComparison.Ordinal))
                return null;

            // A pin that only matched after the attempt was cancelled must not
            // leave a trust record behind.
            token.ThrowIfCancellationRequested();
            trust.Store(options.Host, options.Port, parsed);
            return parsed.Fingerprint;
        }

        var state = trust.Evaluate(options.Host, options.Port, parsed);
        if (state.Status == OfflineUpdateSshHostTrustStatus.Trusted)
            return parsed.Fingerprint;

        var approval = options.ApproveHostKey;
        if (approval is null)
            return null;

        var prompt = new OfflineUpdateSshHostKeyPrompt(
            options.Host,
            options.Port,
            "ssh-ed25519",
            parsed.Fingerprint,
            state.Status,
            state.StoredFingerprint);

        bool approved;
        try
        {
            approved = approval(prompt, token).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (!approved)
            return null;

        // The confirmation may have arrived after cancellation; re-check before
        // persisting so a late Yes never trusts the key of an abandoned attempt.
        token.ThrowIfCancellationRequested();
        trust.Store(options.Host, options.Port, parsed);
        return parsed.Fingerprint;
    }

    /// <summary>
    /// The bootstrap already printed its ready marker, so the updater process
    /// did start. Report the bounded original cause instead of claiming the
    /// components are missing, and keep the remote RPC rejection distinct from
    /// a timeout or a closed connection. Credential values are never included
    /// here; only the exception text and the device-provided code/message are.
    /// </summary>
    private static string BuildInfoQueryFailureMessage(Exception error)
    {
        return error switch
        {
            OfflineUpdateRemoteException remote => string.Concat(
                "The tablet's offline updater rejected the info query (", BoundDetail(remote.Code), "): ",
                BoundDetail(remote.Message)),
            TimeoutException => "The tablet did not answer the offline-updater info query before it timed out: " +
                BoundDetail(error.Message),
            EndOfStreamException => "The offline-updater connection closed before the tablet answered the info query: " +
                BoundDetail(error.Message),
            _ => "The offline-updater info query failed: " + BoundDetail(error.Message),
        };
    }

    private static string BoundDetail(string detail)
    {
        if (detail.Length == 0)
            return "<no detail>";

        return detail.Length <= MaxFailureDetailLength
            ? detail
            : detail[..MaxFailureDetailLength] + "...";
    }

    private static string BuildDiagnostics(
        OfflineUpdateSshPasswordOptions options,
        bool root,
        string? hostKeyFingerprint)
    {
        var mode = root
            ? "direct"
            : "sudo with stderr prompt handshake (password is sent only if sudo asks)";

        var text = string.Concat(
            "Managed SSH password authentication succeeded for user '", options.UserName,
            "' on ", options.Host, ":", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ". Host key: ", hostKeyFingerprint ?? "<unknown>",
            ". Updater start mode: ", mode, ".");

        return text.Length <= MaxDiagnosticsLength ? text : text[..MaxDiagnosticsLength];
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string paramName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan ||
            timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(paramName, "Request timeout must be a positive finite duration supported by CancellationTokenSource.");
        }

        return timeout;
    }

    private static OfflineUpdateSshManagedException MapConnectFailure(Exception ex) => ex switch
    {
        OfflineUpdateSshManagedException managed => managed,
        OfflineUpdateSshHostKeyRejectedException hostKey => new OfflineUpdateSshManagedException(
            OfflineUpdateSshManagedFailure.HostKeyRejected,
            "The tablet host key was not approved, so no password was sent. Verify the fingerprint and try again.",
            hostKey),
        OfflineUpdateSshAuthenticationException auth => new OfflineUpdateSshManagedException(
            OfflineUpdateSshManagedFailure.Authentication,
            "The tablet rejected the SSH password for this user.",
            auth),
        OfflineUpdateSshTransportException transport => new OfflineUpdateSshManagedException(
            OfflineUpdateSshManagedFailure.Unreachable,
            transport.Message,
            transport),
        _ => new OfflineUpdateSshManagedException(
            OfflineUpdateSshManagedFailure.Unreachable,
            "Could not connect to the tablet over SSH: " + ex.Message,
            ex),
    };
}
