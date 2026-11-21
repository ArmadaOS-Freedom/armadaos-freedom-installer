using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

public partial class MainWindow
{
    // Latest real-run results for the ADB device-check probe and the last driver install. Written only by the
    // actual execution entry points; read here for diagnostics export. Never populated by any probe in this file.
    private DriverInstallResult? _diagnosticLastDriverInstall;
    private WindowsDriverCheckResult? _diagnosticLastConnectedAdb;

    /// <summary>
    /// Snapshot of the driver recheck that immediately followed an explicit repair (install) run. Captured only
    /// when the repair produced a fresh environment check; later manual rechecks do not overwrite it.
    /// </summary>
    private sealed record DriverRepairProbe(
        DateTime? StartedUtc,
        DateTime? FinishedUtc,
        WindowsDriverCheckResult? Qualcomm,
        WindowsDriverCheckResult? Adb,
        WindowsDriverCheckResult? Fastboot);

    private DriverRepairProbe? _diagnosticLastDriverRepairProbe;

    /// <summary>
    /// Stable, flat, already-sanitized-or-safe host/readiness snapshot for diagnostics. Values are strings or
    /// null; missing/unknown is expressed as null, never as a fabricated value. No probe runs here: every
    /// entry reflects state already collected by the environment check and work-directory check.
    /// </summary>
    private IReadOnlyDictionary<string, string?> BuildDiagnosticHostEnvironment()
    {
        static string? Bool(bool? value) => value is bool b ? (b ? "true" : "false") : null;

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Host OS / runtime.
        map["host.os.version"] = Environment.OSVersion.Version.ToString();
        map["host.os.description"] = RuntimeInformation.OSDescription;
        map["host.os.architecture"] = RuntimeInformation.OSArchitecture.ToString();
        map["host.process.architecture"] = RuntimeInformation.ProcessArchitecture.ToString();
        map["host.process.is64bit"] = Bool(Environment.Is64BitProcess);
        map["host.framework.description"] = RuntimeInformation.FrameworkDescription;

        var appAssembly = typeof(MainWindow).Assembly;
        map["app.version"] = appAssembly.GetName().Version?.ToString();
        map["app.buildCommit"] = InstallerDisplayVersion.TryGetCommitForAssembly(appAssembly);
        map["app.informationalVersion"] = appAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Environment-check lifecycle: real start/finish timestamps captured in CheckEnvironmentCoreAsync /
        // MarkEnvironmentCheckFailed, plus the latest scan state.
        map["environment.startUtc"] = _environmentCheckStartUtc?.ToString("O", CultureInfo.InvariantCulture);
        map["environment.finishUtc"] = _environmentCheckFinishUtc?.ToString("O", CultureInfo.InvariantCulture);
        map["environment.scanState"] = _environmentScanState;

        // Readiness outcomes, independent of the scanState (finished merely means the check ran to end).
        map["environment.hostReady"] = Bool(EnvironmentIsReady);
        map["environment.platformReady"] = Bool(EnvironmentPlatformReady);
        map["environment.restorePlatformReady"] = Bool(EnvironmentRestorePlatformReady);
        map["environment.systemSupported"] = Bool(_environmentSystemSupported);
        map["environment.toolsReady"] = Bool(_environmentToolsReady);
        map["environment.transportPrepared"] = Bool(_transport is not null);
        map["environment.bootVerified"] = Bool(_environmentBootVerified);
        map["environment.pendingReboot"] = Bool(_environmentPendingReboot);
        map["environment.stateUnknown"] = Bool(_environmentStateUnknown);

        // Driver details, one helper used for all three probes. The exporter sanitizes every string value.
        void AddDriver(string prefix, WindowsDriverCheckResult? result)
        {
            map[prefix + ".state"] = result?.State.ToString();
            map[prefix + ".version"] = result?.DriverVersion;
            map[prefix + ".publishedInf"] = result?.PublishedInf;
            map[prefix + ".detail"] = result?.Detail;
            map[prefix + ".ready"] = Bool(result?.IsReady);

            if (result?.MissingHardwareIds is { Count: > 0 } missing)
            {
                for (var i = 0; i < missing.Count; i++)
                {
                    map[prefix + ".missingHardwareId." + i.ToString(CultureInfo.InvariantCulture)] = missing[i];
                }
            }

            if (result?.RejectedPackages is { Count: > 0 } rejected)
            {
                var count = Math.Min(rejected.Count, 12);
                for (var i = 0; i < count; i++)
                {
                    var entry = rejected[i];
                    var p = prefix + ".rejectedPackage." + i.ToString(CultureInfo.InvariantCulture) + ".";
                    map[p + "publishedInf"] = entry.PublishedInf;
                    map[p + "driverVersion"] = entry.DriverVersion;
                    map[p + "signer"] = entry.Signer;
                    map[p + "catalog"] = entry.Catalog;
                    map[p + "signatureErrorCode"] = entry.SignatureErrorCode is int code
                        ? "0x" + code.ToString("X8", CultureInfo.InvariantCulture)
                        : null;
                    map[p + "signatureDetail"] = entry.SignatureDetail;
                }
            }
        }
        AddDriver("driver.qualcomm", _environmentDriverResult);
        AddDriver("driver.adb", _environmentAdbResult);
        AddDriver("driver.fastboot", _environmentFastbootResult);
        AddDriver("driver.adb.deviceCheck", _diagnosticLastConnectedAdb);

        // Driver recheck that immediately followed the last explicit repair. Independent of later manual
        // rechecks: these entries only change when a repair actually produces a fresh environment check.
        if (_diagnosticLastDriverRepairProbe is { } repair)
        {
            map["driver.install.recheck.startUtc"] =
                repair.StartedUtc?.ToString("O", CultureInfo.InvariantCulture);
            map["driver.install.recheck.finishUtc"] =
                repair.FinishedUtc?.ToString("O", CultureInfo.InvariantCulture);
            AddDriver("driver.install.recheck.qualcomm", repair.Qualcomm);
            AddDriver("driver.install.recheck.adb", repair.Adb);
            AddDriver("driver.install.recheck.fastboot", repair.Fastboot);
        }

        // Last real driver-install run, read from _diagnosticLastDriverInstall (written by the run entry point).
        if (_diagnosticLastDriverInstall is { } install)
        {
            map["driver.install.outcome"] = install.Outcome.ToString();
            map["driver.install.exitCode"] = install.ExitCode?.ToString(CultureInfo.InvariantCulture);
            map["driver.install.error"] = install.Error;

            if (install.Attempts is { Count: > 0 } attempts)
            {
                for (var i = 0; i < attempts.Count; i++)
                {
                    var attempt = attempts[i];
                    var p = "driver.install.attempt." + i.ToString(CultureInfo.InvariantCulture) + ".";
                    map[p + "package"] = attempt.Package;
                    map[p + "started"] = attempt.Started.ToString("O", CultureInfo.InvariantCulture);
                    map[p + "finished"] = attempt.Finished.ToString("O", CultureInfo.InvariantCulture);
                    map[p + "outcome"] = attempt.Outcome.ToString();
                    map[p + "exitCode"] = attempt.ExitCode?.ToString(CultureInfo.InvariantCulture);
                    map[p + "error"] = attempt.Error;
                    // SetupApiExcerpt is already sanitized by the parser; explicit null is preserved.
                    map[p + "setupApiExcerpt"] = attempt.SetupApiExcerpt;
                }
            }
        }

        // Work directory: reflects the last completed check result, not a fresh probe.
        // availableFreeBytes is annotated as the last successful check; it is not re-measured here.
        map["work.checked"] = Bool(_environmentWorkCheckCompleted);
        map["work.ready"] = Bool(_workDirectoryReady);
        map["work.availableFreeBytes"] = _workDirectory?.AvailableFreeBytes
            .ToString(CultureInfo.InvariantCulture);
        map["work.availableFreeBytes.source"] = "last-successful-check";
        map["work.error"] = _diagnosticWorkDirectoryError;
        map["work.defaultSelected"] = Bool(_manualWorkRoot is null);

        // Session binding.
        map["session.bound"] = Bool(_workSession is not null);

        return map;
    }
}
