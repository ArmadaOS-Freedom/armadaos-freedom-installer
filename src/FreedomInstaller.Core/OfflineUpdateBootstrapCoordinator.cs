using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace FreedomInstaller.Core;

public enum OfflineUpdateBootstrapFailure
{
    ProbeFailed = 0,
    DeployFailed = 1,
    VerifyFailed = 2,
    BundleHashMismatch = 3,
    RemoteDirectory = 4,
    SudoUnavailable = 5,

    /// <summary>
    /// The tablet runs a newer updater that is incapable or incomplete.
    /// Deploying the bundled runtime would silently downgrade it, and reusing
    /// it would silently accept a broken runtime, so the coordinator fails
    /// instead.
    /// </summary>
    UpdaterIncompatible = 6,
}

public sealed class OfflineUpdateBootstrapException : IOException
{
    public OfflineUpdateBootstrapException(
        OfflineUpdateBootstrapFailure reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    public OfflineUpdateBootstrapFailure Reason { get; }
}

public sealed record OfflineUpdateBootstrapProgress(string Phase, string? Detail);

/// <summary>Connection half of the helper probe; <c>ready</c> is root-dependent.</summary>
public sealed record OfflineUpdateBootstrapConnectionState(bool Ready, IReadOnlyList<string> Reasons);

/// <summary>Upgrade half of the helper probe; independent of updater presence.</summary>
public sealed record OfflineUpdateBootstrapUpgradeState(bool Eligible, IReadOnlyList<string> Reasons);

/// <summary>System half of the helper probe.</summary>
public sealed record OfflineUpdateBootstrapSystemState(
    string Model,
    string Architecture,
    IReadOnlyList<string> Compatible,
    string? OsId,
    string? OsVersionId);

/// <summary>
/// Installed updater state. A missing entry is expected on a factory tablet and
/// only means that the bootstrap has to deploy the bundled runtime.
/// </summary>
public sealed record OfflineUpdateBootstrapUpdaterState(
    bool EntryPresent,
    string? Version,
    string? Source,
    bool Capable,
    IReadOnlyList<string> Missing);

public sealed record OfflineUpdateBootstrapProbeResult(
    OfflineUpdateBootstrapConnectionState Connection,
    OfflineUpdateBootstrapUpgradeState Upgrade,
    OfflineUpdateBootstrapSystemState System,
    IReadOnlyList<string> MissingDependencies,
    bool ExtensionTargetInstalled,
    OfflineUpdateBootstrapUpdaterState Updater,
    string? Detail)
{
    /// <summary>
    /// The device can host the bootstrap install. The updater itself may be
    /// absent or incomplete; that is what deployment is for. Upgrade
    /// eligibility is deliberately not part of this: an already installed,
    /// capable and complete updater is still usable for a read-only
    /// connection on a device that must not be upgraded. Eligibility gates
    /// deployment only.
    /// </summary>
    public bool Ready =>
        Connection.Ready &&
        MissingDependencies.Count == 0;
}

public sealed record OfflineUpdateBootstrapResult(bool Deployed, OfflineUpdateBootstrapProbeResult Probe);

/// <summary>
/// Small coordinator for the system-owned <c>armada-offline-bootstrap</c>
/// contract. It stages a script and (only when deployment is needed) the
/// updater bundle in a task-created, user-owned /tmp directory, probes the
/// device, and lets the bootstrap install the root-owned sysext after checking
/// a locally computed bundle hash. It never prepares, executes or restarts an
/// upgrade and never removes an already installed service extension. An
/// already installed, capable and complete updater that is at least the
/// bundled version is used as-is on a device that is not upgrade-eligible, so
/// read-only connections keep working; deployment itself is refused there.
/// </summary>
public sealed class OfflineUpdateBootstrapCoordinator
{
    public const string ProbingPhase = "bootstrap_probe";
    public const string DeployingPhase = "bootstrap_deploy";
    public const string VerifyingPhase = "bootstrap_verify";
    public const string ReadyPhase = "bootstrap_ready";

    private const string ScriptName = "armada-offline-bootstrap";
    private const string BundleName = "armada-offline-updater.tar.gz";
    private const string BundleId = "armada-offline-updater";
    private const string ExtensionName = "zzzzzz-armada-offline-updater";
    private const string UpdaterEntryPath = "/usr/libexec/armada/armada-offline-update";
    private const int SupportedSchemaVersion = 1;

    // mktemp -d /tmp/armada-offline-bootstrap.XXXXXXXXXX creates exactly the
    // fixed prefix plus ten random alphanumeric characters. Anything else is a
    // different path and is rejected before it can be used in a command.
    private const string TemporaryDirectoryPrefix = "/tmp/armada-offline-bootstrap.";
    private const int TemporarySuffixLength = 10;

    private static readonly string[] RequiredDependencies =
    {
        "python3", "systemdSysext", "openssl", "bootc", "ostree", "rpmOstree", "fdtget", "fdtput", "dtc",
    };

    private const int MaxProbeJsonBytes = 64 * 1024;
    private const int MaxFailureDetailChars = 2048;

    private readonly Func<Stream> _openScript;
    private readonly Func<Stream> _openBundle;
    private readonly string _expectedBundleSha256;
    private readonly string _expectedBundleVersion;
    private readonly bool _useSudoForInstall;
    private readonly IProgress<OfflineUpdateBootstrapProgress>? _progress;

    /// <param name="expectedBundleVersion">
    /// The <c>version</c> of the embedded updater bundle manifest, supplied
    /// from validated build metadata. It is the only accepted install-receipt
    /// version and the only accepted post-install probe version, so a new
    /// bundle never has to change this class.
    /// </param>
    public OfflineUpdateBootstrapCoordinator(
        Func<Stream> openScript,
        Func<Stream> openBundle,
        string expectedBundleSha256,
        string expectedBundleVersion,
        IProgress<OfflineUpdateBootstrapProgress>? progress = null,
        bool useSudoForInstall = true)
    {
        ArgumentNullException.ThrowIfNull(openScript);
        ArgumentNullException.ThrowIfNull(openBundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBundleSha256);
        if (expectedBundleSha256.Length != 64 ||
            expectedBundleSha256.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The expected updater bundle SHA-256 must be 64 lowercase hexadecimal characters.",
                nameof(expectedBundleSha256));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBundleVersion);
        if (!TryParseVersionParts(expectedBundleVersion, out _))
        {
            throw new ArgumentException(
                "The expected updater bundle version must be a dotted decimal version from the validated bundle manifest.",
                nameof(expectedBundleVersion));
        }

        _openScript = openScript;
        _openBundle = openBundle;
        _expectedBundleSha256 = expectedBundleSha256;
        _expectedBundleVersion = expectedBundleVersion;
        _useSudoForInstall = useSudoForInstall;
        _progress = progress;
    }

    public async Task<OfflineUpdateBootstrapResult> EnsureAsync(
        IOfflineUpdateManagedSession session,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        token.ThrowIfCancellationRequested();

        string? directory = null;
        try
        {
            directory = await CreateRemoteDirectoryAsync(session, token).ConfigureAwait(false);
            var scriptPath = directory + "/" + ScriptName;
            await using (var script = Open(_openScript, "bootstrap script"))
            {
                await session.UploadFileAsync(scriptPath, script, useSudo: false, token).ConfigureAwait(false);
            }

            Report(ProbingPhase, null);
            var probe = await ProbeAsync(session, scriptPath, _useSudoForInstall, token).ConfigureAwait(false);
            if (!probe.Ready)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.ProbeFailed,
                    "The device is not ready for the offline updater bootstrap: " +
                    (probe.Detail ?? "the probe did not report a reason") + ".");
            }

            if (!NeedsDeployment(probe))
            {
                Report(ReadyPhase, probe.Updater.Version);
                return new OfflineUpdateBootstrapResult(false, probe);
            }

            // The installed updater is missing, incomplete or older, so the
            // bundled runtime has to be deployed. That writes to the device
            // and therefore requires an upgrade-eligible, known-model, safe
            // boot state. An ineligible device is still allowed to use an
            // already installed runtime read-only, which the branch above
            // handled.
            var deployBlockers = new List<string>();
            if (!probe.Upgrade.Eligible)
                deployBlockers.Add("upgrade is not eligible" + DescribeReasons(probe.Upgrade.Reasons));
            if (IsUnknownModel(probe.System.Model))
                deployBlockers.Add("the device model is unknown");
            if (deployBlockers.Count > 0)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.ProbeFailed,
                    "The bundled offline updater cannot be deployed: " +
                    string.Join("; ", deployBlockers) + ".");
            }

            Report(DeployingPhase, probe.Updater.Version);
            var bundlePath = directory + "/" + BundleName;
            string actualSha256;
            await using (var bundle = Open(_openBundle, "updater bundle"))
            await using (var hashing = new HashingReadStream(bundle))
            {
                await session.UploadFileAsync(bundlePath, hashing, useSudo: false, token).ConfigureAwait(false);
                actualSha256 = hashing.GetHashHex();
            }

            if (!string.Equals(actualSha256, _expectedBundleSha256, StringComparison.Ordinal))
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.BundleHashMismatch,
                    "The embedded updater bundle SHA-256 does not match the pinned build value; nothing was installed.");
            }

            OfflineUpdateSshCommandResult install;
            try
            {
                install = await session
                    .ExecuteAsync(
                        "/usr/bin/python3",
                        new[] { "-I", scriptPath, "install", "--bundle", bundlePath, "--sha256", actualSha256 },
                        useSudo: _useSudoForInstall,
                        token)
                    .ConfigureAwait(false);
            }
            catch (OfflineUpdateSshManagedException ex) when (ex.Reason == OfflineUpdateSshManagedFailure.SudoUnavailable)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.SudoUnavailable,
                    "The device bootstrap could not install the updater through sudo: " + ex.Message,
                    ex);
            }

            if (!install.Succeeded)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.DeployFailed,
                    "The device bootstrap install command failed with exit code " +
                    install.ExitStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "." + Describe(install));
            }

            EnsureInstallResult(install.StandardOutput);

            Report(VerifyingPhase, null);
            var verified = await ProbeAsync(session, scriptPath, _useSudoForInstall, token).ConfigureAwait(false);
            EnsureVerified(verified);

            Report(ReadyPhase, verified.Updater.Version);
            return new OfflineUpdateBootstrapResult(true, verified);
        }
        finally
        {
            if (directory is not null)
                await CleanupAsync(session, directory).ConfigureAwait(false);
        }
    }

    private static async Task<string> CreateRemoteDirectoryAsync(
        IOfflineUpdateManagedSession session,
        CancellationToken token)
    {
        var result = await session
            .ExecuteAsync(
                "/bin/sh",
                new[] { "-c", "mktemp -d /tmp/armada-offline-bootstrap.XXXXXXXXXX" },
                useSudo: false,
                token)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.RemoteDirectory,
                "The device could not create a temporary upload directory." + Describe(result));
        }

        var path = result.StandardOutput.Trim();
        if (!IsValidTemporaryDirectory(path))
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.RemoteDirectory,
                "The device returned an unexpected temporary upload path.");
        }

        return path;
    }

    /// <summary>
    /// Accepts only the exact fixed prefix followed by the ten alphanumeric
    /// characters mktemp generates. No extra separator, dot segment, whitespace
    /// or control character can survive this check.
    /// </summary>
    internal static bool IsValidTemporaryDirectory(string? path)
    {
        if (path is null || path.Length != TemporaryDirectoryPrefix.Length + TemporarySuffixLength)
            return false;
        if (!path.StartsWith(TemporaryDirectoryPrefix, StringComparison.Ordinal))
            return false;

        foreach (var c in path.AsSpan(TemporaryDirectoryPrefix.Length))
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the documented helper probe document. The probe is executed with
    /// the same elevation as install because <c>connection.ready</c> is
    /// root-dependent; the command itself is read-only.
    /// </summary>
    private static async Task<OfflineUpdateBootstrapProbeResult> ProbeAsync(
        IOfflineUpdateManagedSession session,
        string scriptPath,
        bool useSudo,
        CancellationToken token)
    {
        OfflineUpdateSshCommandResult result;
        try
        {
            result = await session
                .ExecuteAsync(
                    "/usr/bin/python3",
                    new[] { "-I", scriptPath, "probe" },
                    useSudo,
                    token)
                .ConfigureAwait(false);
        }
        catch (OfflineUpdateSshManagedException ex) when (ex.Reason == OfflineUpdateSshManagedFailure.SudoUnavailable)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.SudoUnavailable,
                "The root-capable device probe could not run through sudo: " + ex.Message,
                ex);
        }

        if (!result.Succeeded)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.ProbeFailed,
                "The device bootstrap probe failed with exit code " +
                result.ExitStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "." + Describe(result));
        }

        using var document = ParseJson(
            result.StandardOutput,
            "probe",
            OfflineUpdateBootstrapFailure.ProbeFailed,
            result);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.ProbeFailed,
                "The device bootstrap probe did not return a JSON object." + Describe(result));

        var schemaVersion = GetRequiredInt(root, "schemaVersion", "probe", OfflineUpdateBootstrapFailure.ProbeFailed);
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.ProbeFailed,
                "The device bootstrap probe uses schema version " +
                schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "; this installer supports version " +
                SupportedSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        var ok = GetRequiredBool(root, "ok", "probe", OfflineUpdateBootstrapFailure.ProbeFailed);
        var command = GetRequiredString(root, "command", "probe", OfflineUpdateBootstrapFailure.ProbeFailed);
        if (!string.Equals(command, "probe", StringComparison.Ordinal))
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.ProbeFailed,
                "The device bootstrap probe identified its command as '" + command + "'.");
        }

        if (!ok)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.ProbeFailed,
                "The device bootstrap probe reported ok=false." + DescribeFailure(root));
        }

        var connection = ParseConnection(root);
        var upgrade = ParseUpgrade(root);
        var system = ParseSystem(root);
        var dependencies = ParseMissingDependencies(root);
        var extensionTargetInstalled = ParseExtensionTargetInstalled(root);
        var updater = ParseUpdater(root);

        // service.present and trust are deliberately not used: the installed
        // update service is independent of the offline runtime, and only the
        // updater entry, its capability and its files decide readiness.

        var detail = DescribeBlockers(connection, dependencies);
        return new OfflineUpdateBootstrapProbeResult(
            connection,
            upgrade,
            system,
            dependencies,
            extensionTargetInstalled,
            updater,
            detail);
    }

    private static OfflineUpdateBootstrapConnectionState ParseConnection(JsonElement root)
    {
        var value = RequireObject(root, "connection", "probe");
        var ready = GetRequiredBool(value, "ready", "probe", OfflineUpdateBootstrapFailure.ProbeFailed);
        return new OfflineUpdateBootstrapConnectionState(
            ready,
            GetStringArray(value, "reasons"));
    }

    private static OfflineUpdateBootstrapUpgradeState ParseUpgrade(JsonElement root)
    {
        var value = RequireObject(root, "upgrade", "probe");
        var eligible = GetRequiredBool(value, "eligible", "probe", OfflineUpdateBootstrapFailure.ProbeFailed);
        return new OfflineUpdateBootstrapUpgradeState(
            eligible,
            GetStringArray(value, "reasons"));
    }

    private static OfflineUpdateBootstrapSystemState ParseSystem(JsonElement root)
    {
        var value = RequireObject(root, "system", "probe");
        var model = GetRequiredString(value, "model", "probe", OfflineUpdateBootstrapFailure.ProbeFailed);
        var architecture = GetRequiredString(value, "architecture", "probe", OfflineUpdateBootstrapFailure.ProbeFailed);
        var compatible = GetStringArray(value, "compatible");
        if (compatible.Count == 0)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.ProbeFailed,
                "The device bootstrap probe did not report a compatible board list.");
        }

        if (!string.Equals(architecture, "aarch64", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(architecture, "arm64", StringComparison.OrdinalIgnoreCase))
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.ProbeFailed,
                "The device reports architecture '" + architecture + "', but this updater bundle is ARM64-only.");
        }

        return new OfflineUpdateBootstrapSystemState(
            model,
            architecture,
            compatible,
            GetString(value, "osId"),
            GetString(value, "osVersionId"));
    }

    private static IReadOnlyList<string> ParseMissingDependencies(JsonElement root)
    {
        var value = RequireObject(root, "dependencies", "probe");
        var missing = new List<string>();
        foreach (var name in RequiredDependencies)
        {
            if (!value.TryGetProperty(name, out var entry) ||
                entry.ValueKind != JsonValueKind.Object ||
                !TryGetBool(entry, "present", out var present) ||
                !present)
            {
                missing.Add(name);
            }
        }

        return missing;
    }

    private static bool ParseExtensionTargetInstalled(JsonElement root)
    {
        if (!root.TryGetProperty("extensions", out var value) || value.ValueKind != JsonValueKind.Object)
            return false;

        var target = GetString(value, "target");
        if (target is not null && !string.Equals(target, ExtensionName, StringComparison.Ordinal))
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.ProbeFailed,
                "The device bootstrap probe reported an unexpected updater extension target: '" + target + "'.");
        }

        return TryGetBool(value, "targetInstalled", out var installed) && installed;
    }

    private static OfflineUpdateBootstrapUpdaterState ParseUpdater(JsonElement root)
    {
        var value = RequireObject(root, "updater", "probe");
        var entryPresent = GetRequiredBool(value, "entryPresent", "probe", OfflineUpdateBootstrapFailure.ProbeFailed);
        var capable = GetRequiredBool(value, "capable", "probe", OfflineUpdateBootstrapFailure.ProbeFailed);
        return new OfflineUpdateBootstrapUpdaterState(
            entryPresent,
            GetString(value, "version"),
            GetString(value, "source"),
            capable,
            GetStringArray(value, "missing"));
    }

    private static string? DescribeBlockers(
        OfflineUpdateBootstrapConnectionState connection,
        IReadOnlyList<string> missingDependencies)
    {
        var parts = new List<string>();
        if (!connection.Ready)
            parts.Add("connection is not root-capable" + DescribeReasons(connection.Reasons));
        if (missingDependencies.Count > 0)
            parts.Add("missing device dependencies: " + string.Join(", ", missingDependencies));
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string DescribeReasons(IReadOnlyList<string> reasons) =>
        reasons.Count == 0 ? string.Empty : " (" + string.Join(", ", reasons) + ")";

    private void EnsureVerified(OfflineUpdateBootstrapProbeResult probe)
    {
        var problems = new List<string>();
        if (!probe.Ready)
            problems.Add(probe.Detail ?? "the device no longer reports a ready bootstrap");
        if (!probe.Updater.EntryPresent)
            problems.Add("the updater entry is still missing");
        if (!probe.Updater.Capable)
            problems.Add("the updater does not report the required capability");
        if (probe.Updater.Missing.Count > 0)
            problems.Add("missing updater files: " + string.Join(", ", probe.Updater.Missing));
        if (!string.Equals(probe.Updater.Version, _expectedBundleVersion, StringComparison.Ordinal))
        {
            problems.Add(
                "the reported updater version '" + (probe.Updater.Version ?? "<none>") +
                "' is not the deployed bundle version " + _expectedBundleVersion);
        }

        if (problems.Count == 0)
            return;

        throw new OfflineUpdateBootstrapException(
            OfflineUpdateBootstrapFailure.VerifyFailed,
            "The device bootstrap finished, but the updater is not ready: " + string.Join("; ", problems) + ".");
    }

    private void EnsureInstallResult(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DeployFailed,
                "The device bootstrap install command returned no JSON receipt; it was not treated as success.");
        }

        using var document = ParseJson(
            stdout,
            "install",
            OfflineUpdateBootstrapFailure.DeployFailed,
            null);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DeployFailed,
                "The device bootstrap install receipt is not a JSON object.");
        }

        var schemaVersion = GetRequiredInt(root, "schemaVersion", "install", OfflineUpdateBootstrapFailure.DeployFailed);
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DeployFailed,
                "The device bootstrap install receipt uses unsupported schema version " +
                schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        var ok = GetRequiredBool(root, "ok", "install", OfflineUpdateBootstrapFailure.DeployFailed);
        var command = GetRequiredString(root, "command", "install", OfflineUpdateBootstrapFailure.DeployFailed);
        if (!string.Equals(command, "install", StringComparison.Ordinal))
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DeployFailed,
                "The device bootstrap install receipt identified its command as '" + command + "'.");
        }

        if (!ok)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DeployFailed,
                "The device bootstrap install command reported failure." + DescribeFailure(root));
        }

        var state = GetRequiredString(root, "state", "install", OfflineUpdateBootstrapFailure.DeployFailed);
        if (state is not ("installed" or "reused"))
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DeployFailed,
                "The device bootstrap install receipt reported an unexpected state: '" + state + "'.");
        }

        EnsureReceiptField(root, "bundle", BundleId);
        EnsureReceiptField(root, "extension", ExtensionName);
        EnsureReceiptField(root, "entry", UpdaterEntryPath);
        EnsureReceiptField(root, "sha256", _expectedBundleSha256);
        EnsureReceiptField(root, "version", _expectedBundleVersion);

        if (string.Equals(state, "installed", StringComparison.Ordinal))
        {
            // A fresh install must refresh the system extension, otherwise the
            // newly deployed updater is not visible to the running system.
            if (!GetRequiredBool(
                    root,
                    "sysextRefreshed",
                    "install",
                    OfflineUpdateBootstrapFailure.DeployFailed))
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.DeployFailed,
                    "The device bootstrap install receipt reports that the system extension was not refreshed.");
            }

            return;
        }

        // A reused receipt comes from a healthy reconnect: the helper found the
        // exact bundle already installed, so it did not touch the system
        // extension and reports sysextRefreshed=false. What it must prove
        // instead is that the existing files were hash-verified.
        if (!GetRequiredBool(
                root,
                "verified",
                "install",
                OfflineUpdateBootstrapFailure.DeployFailed))
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DeployFailed,
                "The device bootstrap install receipt reused the installed updater without reporting a verified bundle.");
        }
    }

    private static void EnsureReceiptField(JsonElement root, string name, string expected)
    {
        // The helper echoes the full identity on both the installed and the
        // reused path, so a missing, null or non-string field is a receipt that
        // proves nothing. Only an exact string match is accepted.
        var actual = GetRequiredString(root, name, "install", OfflineUpdateBootstrapFailure.DeployFailed);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DeployFailed,
                "The device bootstrap install receipt reported " + name + " '" + actual +
                "', but '" + expected + "' was expected.");
        }
    }

    private bool NeedsDeployment(OfflineUpdateBootstrapProbeResult probe)
    {
        var updater = probe.Updater;

        // On a device that must not be upgraded, a capable and complete
        // updater that is at least the bundled version is used read-only as it
        // is. Nothing is written, so the hash-verified reinstall that an
        // upgrade-eligible device performs for an equal version is skipped.
        if (!probe.Upgrade.Eligible && CanRunReadOnly(probe))
            return false;

        // A reported newer version is checked before the entry flag: a probe
        // that names a newer runtime but fails to see its entry must never be
        // answered by deploying the older bundle, which would silently
        // downgrade a runtime this installer cannot validate. Only a newer
        // runtime that is actually installed, capable and complete may be
        // reused as it is.
        if (IsNewer(updater.Version, _expectedBundleVersion))
        {
            if (!updater.EntryPresent)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.UpdaterIncompatible,
                    "The tablet reports a newer offline updater (" + updater.Version +
                    ") but no installed updater entry. This installer never downgrades a newer runtime; " +
                    "update the tablet runtime manually or use the matching installer.");
            }

            if (!updater.Capable)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.UpdaterIncompatible,
                    "The tablet has a newer offline updater (" + updater.Version +
                    ") that does not report the required capability. This installer never downgrades a newer runtime; " +
                    "update the tablet runtime manually or use the matching installer.");
            }

            if (updater.Missing.Count > 0)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.UpdaterIncompatible,
                    "The tablet has a newer offline updater (" + updater.Version +
                    ") that is missing required files: " + string.Join(", ", updater.Missing) +
                    ". This installer never downgrades a newer runtime; repair the tablet runtime manually or use the matching installer.");
            }

            return false;
        }

        // A missing entry is what deployment is for. An equal or older entry
        // is not proof that the runtime contains the current fixes: the
        // helper install verifies every bundled file hash and reuses only
        // exact matches, so it goes through that verification path too.
        return true;
    }

    private bool CanRunReadOnly(OfflineUpdateBootstrapProbeResult probe) =>
        probe.Updater.EntryPresent &&
        probe.Updater.Capable &&
        probe.Updater.Missing.Count == 0 &&
        IsAtLeast(probe.Updater.Version, _expectedBundleVersion);

    private static bool IsUnknownModel(string model) =>
        string.Equals(model, "unknown", StringComparison.OrdinalIgnoreCase);

    // A version that cannot be parsed proves nothing about the installed
    // runtime, so it must never satisfy a minimum-version check.
    private static bool IsAtLeast(string? version, string baseline) =>
        TryParseVersionParts(version, out var parsed) &&
        TryParseVersionParts(baseline, out var reference) &&
        CompareVersionParts(parsed, reference) >= 0;

    private static bool IsNewer(string? version, string baseline) =>
        TryParseVersionParts(version, out var parsed) &&
        TryParseVersionParts(baseline, out var reference) &&
        CompareVersionParts(parsed, reference) > 0;

    /// <summary>
    /// Dotted decimal version parts, compared numerically. Using the helper's
    /// reported string as a rounded <see cref="Version"/> would either reject
    /// valid manifests or mis-order values such as 0.8.10 against 0.8.9.
    /// </summary>
    private static bool TryParseVersionParts(string? value, out int[] parts)
    {
        parts = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var segments = value.Split('.');
        var parsed = new int[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length == 0 ||
                !int.TryParse(
                    segments[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out parsed[index]))
            {
                return false;
            }
        }

        parts = parsed;
        return true;
    }

    private static int CompareVersionParts(int[] left, int[] right)
    {
        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var l = index < left.Length ? left[index] : 0;
            var r = index < right.Length ? right[index] : 0;
            if (l != r)
                return l.CompareTo(r);
        }

        return 0;
    }

    private void Report(string phase, string? detail) =>
        _progress?.Report(new OfflineUpdateBootstrapProgress(phase, detail));

    private static Stream Open(Func<Stream> open, string name)
    {
        var stream = open();
        if (stream is null)
            throw new InvalidOperationException("The embedded " + name + " stream is unavailable.");
        return stream;
    }

    private static JsonDocument ParseJson(
        string text,
        string phase,
        OfflineUpdateBootstrapFailure failure,
        OfflineUpdateSshCommandResult? result)
    {
        if (text.Length > MaxProbeJsonBytes)
            throw new OfflineUpdateBootstrapException(
                failure,
                "The device bootstrap " + phase + " output exceeded the " + MaxProbeJsonBytes + "-byte limit.");

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new OfflineUpdateBootstrapException(
                failure,
                "The device bootstrap " + phase + " output is not valid JSON." +
                (result is null ? string.Empty : Describe(result)),
                ex);
        }
    }

    private static JsonElement RequireObject(JsonElement root, string name, string phase)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.ProbeFailed,
                "The device bootstrap " + phase + " response is missing its '" + name + "' object.");
        }

        return value;
    }

    private static int GetRequiredInt(
        JsonElement element,
        string name,
        string phase,
        OfflineUpdateBootstrapFailure failure)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
        {
            throw new OfflineUpdateBootstrapException(
                failure,
                "The device bootstrap " + phase + " response is missing a valid '" + name + "' value.");
        }

        return number;
    }

    private static bool GetRequiredBool(
        JsonElement element,
        string name,
        string phase,
        OfflineUpdateBootstrapFailure failure)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new OfflineUpdateBootstrapException(
                failure,
                "The device bootstrap " + phase + " response is missing a valid '" + name + "' value.");
        }

        return value.GetBoolean();
    }

    private static string GetRequiredString(
        JsonElement element,
        string name,
        string phase,
        OfflineUpdateBootstrapFailure failure)
    {
        var value = GetString(element, name);
        if (string.IsNullOrEmpty(value))
        {
            throw new OfflineUpdateBootstrapException(
                failure,
                "The device bootstrap " + phase + " response is missing a valid '" + name + "' value.");
        }

        return value;
    }

    private static bool TryGetBool(JsonElement element, string name, out bool value)
    {
        if (element.TryGetProperty(name, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                item.GetString() is { Length: > 0 } text)
            {
                items.Add(text);
            }
        }

        return items;
    }

    private static string DescribeFailure(JsonElement root)
    {
        var detail = GetString(root, "error") ?? GetString(root, "message") ?? GetString(root, "detail");
        if (detail is null &&
            root.TryGetProperty("upgrade", out var upgrade) &&
            upgrade.ValueKind == JsonValueKind.Object)
        {
            var reasons = GetStringArray(upgrade, "reasons");
            if (reasons.Count > 0)
                detail = string.Join(", ", reasons);
        }

        return detail is null ? string.Empty : " Device message: " + Clip(detail);
    }

    private static string Describe(OfflineUpdateSshCommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        text = text.Trim();
        if (text.Length == 0)
            return string.Empty;
        return " Device message: " + Clip(text);
    }

    private static string Clip(string text) =>
        text.Length > MaxFailureDetailChars ? text[^MaxFailureDetailChars..] : text;

    private static async Task CleanupAsync(IOfflineUpdateManagedSession session, string directory)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await session
                .ExecuteAsync(
                    "/bin/sh",
                    new[] { "-c", "rm -rf -- '" + directory + "'" },
                    useSudo: false,
                    cts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best effort only. The installed service extension is never touched.
        }
    }

    /// <summary>Read-through stream that hashes exactly the bytes uploaded.</summary>
    private sealed class HashingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private string? _hashHex;

        internal HashingReadStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
                _hash.AppendData(buffer.Span[..read]);
            return read;
        }

        internal string GetHashHex() =>
            _hashHex ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
