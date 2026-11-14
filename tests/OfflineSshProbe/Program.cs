using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FreedomInstaller.Core;

namespace OfflineSshProbe;

/// <summary>
/// Opt-in verification harness. It uses the exact production managed
/// connection and bootstrap coordinator. The bootstrap/probe step can install
/// or replace the updater components on the tablet using the authorized sudo
/// account, so this harness is not read-only. The info and preflight steps are
/// read-only: preflight validates the signed document and writes neither an OS
/// upgrade nor a partition. The harness has no calls to prepare, execute or
/// restart an upgrade. A requested preflight is reported with the same
/// canonical pass contract as the installer UI, and a failed check makes the
/// run fail instead of reporting ok.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Options? options;
        try
        {
            options = ParseArguments(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("invalid arguments: " + ex.Message);
            Usage();
            return 2;
        }

        if (options is null)
        {
            Usage();
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var (result, preflightPassed) = await RunAsync(options, cts.Token);
            WriteOutput(options.OutputPath, result);
            Console.WriteLine(preflightPassed
                ? "offline SSH probe completed"
                : "offline SSH probe completed with a failed preflight");
            return preflightPassed ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            WriteFailure(options.OutputPath, "cancelled");
            Console.Error.WriteLine("cancelled");
            return 2;
        }
        catch (Exception ex)
        {
            WriteFailure(options.OutputPath, ex.Message);
            Console.Error.WriteLine("offline SSH probe failed: " + ex.Message);
            return 1;
        }
    }

    private static async Task<(JsonObject Output, bool PreflightPassed)> RunAsync(
        Options options, CancellationToken token)
    {
        var bundleSha256 = ComputeSha256(options.BundlePath);
        var scriptPath = Path.GetFullPath(options.ScriptPath);
        var bundlePath = Path.GetFullPath(options.BundlePath);
        var bootstrap = new OfflineUpdateBootstrapCoordinator(
            () => File.OpenRead(scriptPath),
            () => File.OpenRead(bundlePath),
            bundleSha256,
            BundleManifestVersion.Read(bundlePath),
            new HarnessProgress(),
            useSudoForInstall: !string.Equals(options.User, "root", StringComparison.Ordinal));

        var password = options.Password;
        OfflineUpdateBootstrapResult? bootstrapResult = null;
        var managed = new OfflineUpdateSshPasswordOptions(
            options.Host,
            options.Port,
            options.User,
            password,
            options.TrustDirectory,
            options.BindAddress)
        {
            ExpectedHostFingerprint = options.ExpectedFingerprint,
            ApproveHostKey = (_, _) => Task.FromResult(false),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PrepareSessionAsync = async (session, ct) =>
            {
                bootstrapResult = await bootstrap.EnsureAsync(session, ct);
            },
        };

        IOfflineUpdateConnection connection;
        try
        {
            connection = await OfflineUpdateSshManagedConnection.ConnectAsync(
                managed, requestTimeout: TimeSpan.FromMinutes(2), token: token);
        }
        finally
        {
            managed.ClearPassword();
        }

        await using (connection)
        {
            var output = new JsonObject
            {
                ["ok"] = true,
                ["route"] = options.BindAddress is null ? "wifi" : "usb",
                ["host"] = options.Host,
                ["port"] = options.Port,
                ["info"] = JsonNode.Parse(connection.DeviceInfo.GetRawText()),
            };

            if (bootstrapResult is { } result)
            {
                // Evidence from the same production coordinator the application
                // uses. The bootstrap may have installed or replaced the updater
                // components with sudo; the harness still never prepares,
                // executes or restarts an OS upgrade.
                var missingDependencies = new JsonArray();
                foreach (var value in result.Probe.MissingDependencies)
                    missingDependencies.Add(value);
                var missingUpdaterFiles = new JsonArray();
                foreach (var value in result.Probe.Updater.Missing)
                    missingUpdaterFiles.Add(value);

                output["bootstrap"] = new JsonObject
                {
                    ["deployed"] = result.Deployed,
                    ["connectionReady"] = result.Probe.Connection.Ready,
                    ["upgradeEligible"] = result.Probe.Upgrade.Eligible,
                    ["updaterEntryPresent"] = result.Probe.Updater.EntryPresent,
                    ["updaterVersion"] = result.Probe.Updater.Version,
                    ["updaterCapable"] = result.Probe.Updater.Capable,
                    ["missingDependencies"] = missingDependencies,
                    ["missingUpdaterFiles"] = missingUpdaterFiles,
                };
            }

            if (options.DescriptorPath is not null && options.SignaturePath is not null)
            {
                var document = await File.ReadAllBytesAsync(options.DescriptorPath, token);
                var signature = await File.ReadAllBytesAsync(options.SignaturePath, token);
                var preflight = await connection.Rpc.PreflightAsync(document, signature, token);
                // A device-side rejection still returns a JSON object, so the
                // probe must apply the same canonical pass contract as the UI
                // instead of reporting a successful run for a failed check.
                bool passed = OfflineUpdatePreflight.Passed(preflight);
                output["ok"] = passed;
                output["preflight"] = new JsonObject
                {
                    ["ok"] = passed,
                    ["requested"] = true,
                    ["phase"] = OfflineUpdatePreflight.Phase(preflight),
                    ["bootWriteState"] = OfflineUpdatePreflight.BootWriteState(preflight),
                    ["result"] = JsonNode.Parse(preflight.GetRawText()),
                };
                return (output, passed);
            }
            else
            {
                // Not requested is not a failure: the run itself still succeeded,
                // so the top-level ok stays true and only the preflight block is
                // explicitly marked as not requested.
                output["preflight"] = new JsonObject
                {
                    ["ok"] = false,
                    ["requested"] = false,
                };
            }

            return (output, true);
        }
    }

    private static void WriteOutput(string path, JsonObject value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(
            path,
            value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteFailure(string path, string message)
    {
        try
        {
            WriteOutput(path, new JsonObject
            {
                ["ok"] = false,
                ["error"] = message,
            });
        }
        catch
        {
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToHexStringLower(hash.ComputeHash(stream));
    }

    private static Options? ParseArguments(string[] args)
    {
        string? host = null;
        string? output = null;
        string? trust = null;
        string? script = null;
        string? bundle = null;
        string? fingerprint = null;
        string? bind = null;
        string? user = null;
        string? descriptor = null;
        string? signature = null;
        var port = 22;
        var passwordStdin = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host" when i + 1 < args.Length:
                    host = args[++i];
                    break;
                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out port) || port is < 1 or > 65535)
                        return null;
                    break;
                case "--bind" when i + 1 < args.Length:
                    bind = args[++i];
                    break;
                case "--fingerprint" when i + 1 < args.Length:
                    fingerprint = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--trust" when i + 1 < args.Length:
                    trust = args[++i];
                    break;
                case "--script" when i + 1 < args.Length:
                    script = args[++i];
                    break;
                case "--bundle" when i + 1 < args.Length:
                    bundle = args[++i];
                    break;
                case "--user" when i + 1 < args.Length:
                    user = args[++i];
                    break;
                case "--descriptor" when i + 1 < args.Length:
                    descriptor = args[++i];
                    break;
                case "--signature" when i + 1 < args.Length:
                    signature = args[++i];
                    break;
                case "--password-stdin":
                    passwordStdin = true;
                    break;
                default:
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(output) ||
            string.IsNullOrWhiteSpace(trust) ||
            string.IsNullOrWhiteSpace(script) ||
            string.IsNullOrWhiteSpace(bundle) ||
            string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        if (!File.Exists(script) || !File.Exists(bundle))
            return null;

        if ((descriptor is null) != (signature is null))
            return null;

        if (descriptor is not null && (!File.Exists(descriptor) || !File.Exists(signature)))
            return null;

        var factory = OfflineUpdateSshDefaultCredentials.Instance;
        var effectiveUser = user ?? factory.UserName;
        string password;
        if (passwordStdin)
        {
            var line = Console.In.ReadLine();
            if (string.IsNullOrEmpty(line))
                return null;
            password = line;
        }
        else
        {
            if (!string.Equals(effectiveUser, factory.UserName, StringComparison.Ordinal))
                return null;
            password = factory.Password;
        }

        Directory.CreateDirectory(trust);
        return new Options(
            host,
            port,
            effectiveUser,
            password,
            bind,
            fingerprint,
            Path.GetFullPath(output),
            Path.GetFullPath(trust),
            Path.GetFullPath(script),
            Path.GetFullPath(bundle),
            descriptor is null ? null : Path.GetFullPath(descriptor),
            signature is null ? null : Path.GetFullPath(signature!));
    }

    private static void Usage()
    {
        Console.Error.WriteLine(
            "OfflineSshProbe --host HOST [--port 22] [--bind USB_LOCAL_IP] " +
            "--fingerprint SHA256:... --trust DIR --script FILE --bundle FILE --output FILE " +
            "[--user NAME --password-stdin] [--descriptor FILE --signature FILE]");
        Console.Error.WriteLine(
            "This harness runs service bootstrap/probe (which may install or replace the updater components " +
            "using the authorized sudo account), info and read-only preflight. Preflight writes neither an OS " +
            "upgrade nor a partition, and the harness never prepares, executes or restarts an upgrade.");
    }

    private sealed class HarnessProgress : IProgress<OfflineUpdateBootstrapProgress>
    {
        public void Report(OfflineUpdateBootstrapProgress value) =>
            Console.Error.WriteLine("bootstrap: " + value.Phase + (value.Detail is null ? string.Empty : " (" + value.Detail + ")"));
    }

    private sealed record Options(
        string Host,
        int Port,
        string User,
        string Password,
        string? BindAddress,
        string ExpectedFingerprint,
        string OutputPath,
        string TrustDirectory,
        string ScriptPath,
        string BundlePath,
        string? DescriptorPath,
        string? SignaturePath)
    {
        public override string ToString() =>
            "OfflineSshProbe Options { Host = " + Host + ", Port = " + Port +
            ", User = " + User + ", Password = <redacted> }";
    }
}
