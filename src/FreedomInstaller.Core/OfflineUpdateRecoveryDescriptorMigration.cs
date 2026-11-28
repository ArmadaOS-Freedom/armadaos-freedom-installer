using System.Text.Json;

namespace FreedomInstaller.Core;

public sealed record OfflineRecoveryDescriptorMigrationResult(string State, int DescriptorVersion);

/// <summary>
/// Rewrites a legacy v1/v2 ARMADA_BACKUP settings descriptor to the v3 layout
/// on a device that is already running ArmadaOS.
///
/// The on-device updater refuses a legacy descriptor when it opens the trial
/// journal, which makes the 1124 preflight stop before it can report anything.
/// The installer can repair the descriptor before that updater starts: the
/// bootstrap prepare hook already runs as root over the same authenticated
/// session, and the migration is a single 4096-byte write that is verified,
/// flushed and read back. A v3 descriptor is a no-op, so every connection may
/// run it.
///
/// This is deliberately independent of the updater bundle: it must work on a
/// tablet whose installed runtime still predates the migration.
/// </summary>
internal static class OfflineUpdateRecoveryDescriptorMigration
{
    internal const string ScriptName = "armada-offline-recovery-migration.py";
    internal const string ResourceName = "FreedomInstaller.OfflineRecoveryDescriptor.Script.py";

    private const int SupportedSchemaVersion = 1;
    private const int MaxFailureDetailChars = 512;
    private static readonly TimeSpan KeyRouteTimeout = TimeSpan.FromMinutes(2);

    private static readonly string[] SupportedStates = { "migrated", "already-v3" };
    private static readonly string[] SupportedModels = { "TB322FC", "TB323FU", "TB710FU" };

    private static byte[]? _script;

    internal static bool IsSupportedModel(string? model) =>
        model is not null && Array.IndexOf(SupportedModels, model) >= 0;

    /// <summary>
    /// The public-key route has no bootstrap probe. Its <c>info</c> document
    /// already advertises the same mutating-upgrade gate the rest of the
    /// installer uses: an exact <c>bootPolicy</c>, plus a boot path this model
    /// may update through. A device that fails either check must not have its
    /// descriptor rewritten by a read-only connection.
    /// </summary>
    internal static bool IsMigrationTarget(JsonElement info, string? model)
    {
        if (!IsSupportedModel(model) || !OfflineUpdateBootPolicy.IsNoSlotControl(info))
            return false;

        // The transfer already rejects a model/boot-path/slot shape it cannot
        // update. Reuse that rule instead of duplicating it: migration must
        // never write a descriptor for a report the upgrade itself refuses.
        return OfflineUpdateTransfer.AcceptsBootReport(info, model!);
    }

    private static byte[] ScriptBytes =>
        _script ??= ReadScript();

    /// <summary>
    /// Runs the migration over the public-key transport, which has no managed
    /// session. It is invoked after the updater reported its model and before
    /// the caller can preflight anything, so the descriptor is repaired even
    /// when the first pairing (and therefore the bootstrap) happened long ago.
    /// </summary>
    internal static async Task<OfflineRecoveryDescriptorMigrationResult> EnsureOverKeyRouteAsync(
        OfflineUpdateSshOptions options,
        string model,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsSupportedModel(model))
            throw new ArgumentException("The model has no recovery settings layout.", nameof(model));

        OfflineUpdateSshCommandResult result;
        try
        {
            result = await OfflineUpdateSshConnection
                .RunRemoteCommandAsync(
                    options,
                    BuildRemoteCommand(model, string.Equals(options.UserName, "root", StringComparison.Ordinal)),
                    ScriptBytes,
                    KeyRouteTimeout,
                    token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DescriptorMigrationFailed,
                "The recovery settings migration did not finish in time.",
                ex);
        }

        return Parse(result);
    }

    internal static string BuildRemoteCommand(string model, bool root)
    {
        if (!IsSupportedModel(model))
            throw new ArgumentException("The model has no recovery settings layout.", nameof(model));

        var sudo = root ? string.Empty : "/usr/bin/sudo -n -- ";
        // The remote login shell is not guaranteed to be POSIX, so the whole
        // pipeline runs under an explicit /bin/sh. No single quotes occur in
        // the command, which keeps the outer quoting exact.
        return "/bin/sh -c '" +
               "d=$(mktemp -d /tmp/armada-offline-migration.XXXXXXXXXX) || exit 90; " +
               "cat > \"$d/migrate.py\" || { rm -rf -- \"$d\"; exit 91; }; " +
               sudo + "/usr/bin/python3 -I \"$d/migrate.py\" migrate --model " + model + "; " +
               "rc=$?; rm -rf -- \"$d\"; exit $rc'";
    }

    internal static async Task<OfflineRecoveryDescriptorMigrationResult> EnsureAsync(
        IOfflineUpdateManagedSession session,
        string directory,
        string model,
        bool useSudo,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var scriptPath = directory + "/" + ScriptName;
        await using (var script = OpenScript())
        {
            await session.UploadFileAsync(scriptPath, script, useSudo: false, token).ConfigureAwait(false);
        }

        OfflineUpdateSshCommandResult result;
        try
        {
            result = await session
                .ExecuteAsync(
                    "/usr/bin/python3",
                    new[] { "-I", scriptPath, "migrate", "--model", model },
                    useSudo,
                    token)
                .ConfigureAwait(false);
        }
        catch (OfflineUpdateSshManagedException ex) when (ex.Reason == OfflineUpdateSshManagedFailure.SudoUnavailable)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DescriptorMigrationFailed,
                "The recovery settings migration could not run through sudo: " + ex.Message,
                ex);
        }

        return Parse(result);
    }

    private static OfflineRecoveryDescriptorMigrationResult Parse(OfflineUpdateSshCommandResult result)
    {
        var document = TryParseDocument(result.StandardOutput);
        if (document is null)
        {
            throw new OfflineUpdateBootstrapException(
                OfflineUpdateBootstrapFailure.DescriptorMigrationFailed,
                "The recovery settings migration did not return a parseable result" +
                Describe(result));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.DescriptorMigrationFailed,
                    "The recovery settings migration did not return a JSON object" + Describe(result));
            }

            if (!root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != SupportedSchemaVersion)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.DescriptorMigrationFailed,
                    "The recovery settings migration returned an unsupported result schema" + Describe(result));
            }

            if (!root.TryGetProperty("command", out var command) ||
                command.ValueKind != JsonValueKind.String ||
                !string.Equals(command.GetString(), "migrate", StringComparison.Ordinal))
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.DescriptorMigrationFailed,
                    "The recovery settings migration identified an unexpected command" + Describe(result));
            }

            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            if (!ok || !result.Succeeded)
            {
                var code = root.TryGetProperty("error", out var errorElement) &&
                           errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;
                var message = root.TryGetProperty("message", out var messageElement) &&
                              messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : null;
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.DescriptorMigrationFailed,
                    "The recovery settings descriptor was not migrated" +
                    (string.IsNullOrEmpty(code) ? string.Empty : " (" + code + ")") +
                    (string.IsNullOrEmpty(message) ? string.Empty : ": " + Clip(message!)) +
                    Describe(result));
            }

            var state = root.TryGetProperty("state", out var stateElement) &&
                        stateElement.ValueKind == JsonValueKind.String
                ? stateElement.GetString()
                : null;
            if (state is null || Array.IndexOf(SupportedStates, state) < 0)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.DescriptorMigrationFailed,
                    "The recovery settings migration reported an unknown state '" + (state ?? "<none>") + "'.");
            }

            var descriptorVersion = root.TryGetProperty("descriptorVersion", out var versionElement) &&
                                    versionElement.ValueKind == JsonValueKind.Number &&
                                    versionElement.TryGetInt32(out var version)
                ? version
                : 3;
            if (descriptorVersion != 3)
            {
                throw new OfflineUpdateBootstrapException(
                    OfflineUpdateBootstrapFailure.DescriptorMigrationFailed,
                    "The recovery settings migration left descriptor version " + descriptorVersion + ".");
            }

            return new OfflineRecoveryDescriptorMigrationResult(state, descriptorVersion);
        }
    }

    private static JsonDocument? TryParseDocument(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
                continue;
            try
            {
                return JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // Keep looking: a wrapper may have printed a non-JSON line.
            }
        }

        return null;
    }

    private static string Describe(OfflineUpdateSshCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return " (exit " + result.ExitStatus.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" +
               (string.IsNullOrWhiteSpace(detail) ? "." : ": " + Clip(detail));
    }

    private static string Clip(string text) =>
        text.Length > MaxFailureDetailChars ? text[^MaxFailureDetailChars..] : text;

    private static Stream OpenScript() =>
        typeof(OfflineUpdateRecoveryDescriptorMigration).Assembly.GetManifestResourceStream(ResourceName) ??
        throw new InvalidOperationException(
            "The embedded recovery settings migration resource '" + ResourceName + "' is unavailable.");

    private static byte[] ReadScript()
    {
        using var stream = OpenScript();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
