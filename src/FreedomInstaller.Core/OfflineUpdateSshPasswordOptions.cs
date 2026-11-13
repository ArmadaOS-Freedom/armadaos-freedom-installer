using System.Globalization;

namespace FreedomInstaller.Core;

/// <summary>Authentication mode offered by the offline-upgrade connection panel.</summary>
public enum OfflineUpdateSshAuthMode
{
    Password = 0,
    PublicKey = 1,
}

/// <summary>
/// Which input fields belong to an authentication mode. The application maps
/// these flags to WPF visibility; keeping the decision here makes the wiring
/// testable without a window.
/// </summary>
public readonly record struct OfflineUpdateSshAuthFields(
    bool ShowPassword,
    bool ShowKeyPath,
    bool ShowKnownHosts);

public static class OfflineUpdateSshAuthPolicy
{
    public const int PasswordSelectionIndex = 0;
    public const int PublicKeySelectionIndex = 1;

    public static OfflineUpdateSshAuthMode FromSelectionIndex(int index) =>
        index == PublicKeySelectionIndex ? OfflineUpdateSshAuthMode.PublicKey : OfflineUpdateSshAuthMode.Password;

    /// <summary>
    /// Password is the default for manual Wi-Fi and USB connections; the key
    /// flow keeps its own key path and known_hosts inputs.
    /// </summary>
    public static OfflineUpdateSshAuthFields Fields(OfflineUpdateSshAuthMode mode) => mode switch
    {
        OfflineUpdateSshAuthMode.PublicKey => new(ShowPassword: false, ShowKeyPath: true, ShowKnownHosts: true),
        _ => new(ShowPassword: true, ShowKeyPath: false, ShowKnownHosts: false),
    };

    /// <summary>
    /// The public factory credential is attempted at most once per endpoint,
    /// only for an automatic password connection whose credentials were not
    /// entered by the user and only if that attempt has not already been
    /// rejected for the endpoint.
    /// </summary>
    public static bool ShouldAttemptDefaultCredential(
        OfflineUpdateSshAuthMode mode,
        bool credentialsSuppliedByUser,
        bool defaultAlreadyAttempted) =>
        mode == OfflineUpdateSshAuthMode.Password && !credentialsSuppliedByUser && !defaultAlreadyAttempted;

    /// <summary>
    /// A non-empty username is manual input too. The factory account must
    /// never replace a name the user typed just because the password field was
    /// left blank; that case asks for the password instead.
    /// </summary>
    public static bool HasManualCredentialInput(string? userName, string? password) =>
        !string.IsNullOrEmpty(password) || !string.IsNullOrWhiteSpace(userName);

    /// <summary>
    /// Stable identity of the chosen connection target. The automatic
    /// credential attempt is tracked per target, so a rejected USB endpoint
    /// does not consume the attempt for a different Wi-Fi endpoint.
    /// </summary>
    public static string EndpointKey(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return string.Concat(host.Trim().ToLowerInvariant(), ":", port.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Key of the single automatic factory-credential attempt. Wi-Fi is
    /// identified by the typed host and port alone. Every USB gadget presents
    /// the same private host, so the validated discovery instance id is part
    /// of the key: the same device on the same endpoint stays limited, while a
    /// different USB instance starts with its own attempt.
    /// </summary>
    public static string DefaultCredentialRejectionKey(string host, int port, string? usbDeviceInstanceId = null)
    {
        string endpoint = EndpointKey(host, port);
        if (string.IsNullOrWhiteSpace(usbDeviceInstanceId))
            return endpoint;
        // Windows reports PNP ids without case significance; normalizing keeps
        // "the same device" one key regardless of how the id was reported.
        return string.Concat(usbDeviceInstanceId.Trim().ToUpperInvariant(), "|", endpoint);
    }
}

/// <summary>
/// Intentional public product configuration for the factory tablet image.
/// This is not a user secret and must never be used to replace credentials the
/// user entered.
/// </summary>
public readonly record struct OfflineUpdateSshDefaultCredential(string UserName, string Password)
{
    public override string ToString() =>
        string.Concat("OfflineUpdateSshDefaultCredential { UserName = ", UserName, ", Password = <redacted> }");
}

public static class OfflineUpdateSshDefaultCredentials
{
    public static OfflineUpdateSshDefaultCredential Instance { get; } = new("armada", "armada");
}

/// <summary>Host trust state produced by <see cref="OfflineUpdateSshHostTrustStore"/>.</summary>
public enum OfflineUpdateSshHostTrustStatus
{
    Unknown = 0,
    Trusted = 1,
    Changed = 2,
}

public sealed record OfflineUpdateSshHostTrustState(
    OfflineUpdateSshHostTrustStatus Status,
    string? StoredFingerprint);

/// <summary>
/// Fingerprint approval request shown before any password is sent. It carries
/// no secret material.
/// </summary>
public sealed record OfflineUpdateSshHostKeyPrompt(
    string Host,
    int Port,
    string KeyType,
    string Fingerprint,
    OfflineUpdateSshHostTrustStatus Status,
    string? StoredFingerprint);

/// <summary>
/// Options for the managed password transport. This is a class instead of a
/// positional record so that the generated record ToString can never print the
/// password, and so the stored secret can be dropped explicitly.
/// </summary>
public sealed class OfflineUpdateSshPasswordOptions
{
    private const int MaxPasswordLength = 512;

    private string? _password;

    public OfflineUpdateSshPasswordOptions(
        string host,
        int port,
        string userName,
        string password,
        string trustDirectory,
        string? bindAddress = null)
    {
        Host = OfflineUpdateSshConnection.ValidateHost(host);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        Port = port;
        UserName = OfflineUpdateSshConnection.ValidateUserName(userName);

        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
            throw new ArgumentException("Password must not be empty.", nameof(password));
        if (password.Length > MaxPasswordLength)
            throw new ArgumentException($"Password exceeds the {MaxPasswordLength}-character limit.", nameof(password));
        if (password.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("Password must not contain newline or null characters.", nameof(password));

        _password = password;
        TrustDirectory = ValidateTrustDirectory(trustDirectory);
        BindAddress = OfflineUpdateSshConnection.ValidateBindAddress(bindAddress);
    }

    public string Host { get; }

    public int Port { get; }

    public string UserName { get; }

    /// <summary>
    /// Directory owned by this task/application where only trusted host public
    /// keys are persisted. It never receives a password or a private key.
    /// </summary>
    public string TrustDirectory { get; }

    public string? BindAddress { get; }

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Optional fingerprint approval callback; invoked on a background thread.</summary>
    public Func<OfflineUpdateSshHostKeyPrompt, CancellationToken, Task<bool>>? ApproveHostKey { get; set; }

    /// <summary>
    /// Optional exact host-key pin in the canonical <c>SHA256:</c> form. When
    /// set, the key must match before any credential is sent and no approval
    /// prompt is shown. A mismatch rejects the connection.
    /// </summary>
    public string? ExpectedHostFingerprint { get; set; }

    /// <summary>
    /// Optional hook invoked after authentication and before the fixed updater
    /// command starts. It receives the authenticated managed session so a
    /// future bootstrap component can prepare the device. There is no
    /// dependency on any service being installed.
    /// </summary>
    public Func<IOfflineUpdateManagedSession, CancellationToken, Task>? PrepareSessionAsync { get; set; }

    public bool HasPassword => _password is not null;

    public string Password => _password ??
        throw new InvalidOperationException("The password has already been cleared for this connection attempt.");

    /// <summary>Drops the stored reference to the secret; the object stays reusable for diagnostics.</summary>
    public void ClearPassword() => _password = null;

    public override string ToString() =>
        string.Concat(
            "OfflineUpdateSshPasswordOptions { Host = ", Host,
            ", Port = ", Port.ToString(CultureInfo.InvariantCulture),
            ", UserName = ", UserName,
            ", BindAddress = ", BindAddress ?? "<none>",
            ", Password = ", HasPassword ? "<redacted>" : "<cleared>",
            " }");

    private static string ValidateTrustDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("TrustDirectory must not contain newline or null characters.", nameof(path));
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("TrustDirectory must be a fully qualified path.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("TrustDirectory has no filesystem root.", nameof(path));
        if (string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("TrustDirectory must not be a filesystem root.", nameof(path));
        }

        return fullPath;
    }
}

/// <summary>Classifies a failed managed password connection for localized UI copy.</summary>
public enum OfflineUpdateSshManagedFailure
{
    Authentication = 0,
    HostKeyRejected = 1,
    Unreachable = 2,
    SudoUnavailable = 3,
    UpdaterUnavailable = 4,
    Timeout = 5,
    Bootstrap = 6,
}

public sealed class OfflineUpdateSshManagedException : IOException
{
    public OfflineUpdateSshManagedException(OfflineUpdateSshManagedFailure reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    public OfflineUpdateSshManagedFailure Reason { get; }
}

/// <summary>Thrown by a transport when the host key was not approved during key exchange.</summary>
public sealed class OfflineUpdateSshHostKeyRejectedException : IOException
{
    public OfflineUpdateSshHostKeyRejectedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown by a transport when the server rejected the password authentication.</summary>
public sealed class OfflineUpdateSshAuthenticationException : IOException
{
    public OfflineUpdateSshAuthenticationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown by a transport when the SSH connection itself could not be established.</summary>
public sealed class OfflineUpdateSshTransportException : IOException
{
    public OfflineUpdateSshTransportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
