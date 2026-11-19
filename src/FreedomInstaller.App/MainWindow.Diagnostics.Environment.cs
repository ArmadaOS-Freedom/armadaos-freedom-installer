using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

public partial class MainWindow
{
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

        // Driver details, one helper used for all three probes.
        void AddDriver(string prefix, WindowsDriverCheckResult? result)
        {
            map[prefix + ".state"] = result?.State.ToString();
            map[prefix + ".version"] = result?.DriverVersion;
            map[prefix + ".publishedInf"] = result?.PublishedInf;
            map[prefix + ".detail"] = result?.Detail;
            map[prefix + ".ready"] = Bool(result?.IsReady);
        }
        AddDriver("driver.qualcomm", _environmentDriverResult);
        AddDriver("driver.adb", _environmentAdbResult);
        AddDriver("driver.fastboot", _environmentFastbootResult);

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
