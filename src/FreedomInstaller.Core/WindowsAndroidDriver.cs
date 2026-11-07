using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace FreedomInstaller.Core;

public enum AndroidUsbMode { Adb, Fastboot }

public sealed class WindowsAndroidDriver
{
    public WindowsDriverCheckResult Inspect(AndroidUsbMode mode) =>
        WindowsQualcommDriver.InspectAndroid(mode);
}

// Shares signature verification and native SetupAPI bindings with the 9008 probe.
public sealed partial class WindowsQualcommDriver
{
    private static readonly string[] FastbootIds = {
        @"USB\VID_18D1&PID_D00D", @"USB\VID_18D1&PID_4EE0"
    };
    private static readonly string[] AdbIds = {
        @"USB\VID_18D1&PID_4E11", @"USB\VID_18D1&PID_D001", @"USB\VID_18D1&PID_4EE7",
        @"USB\VID_18D1&PID_4EE2&MI_01", @"USB\VID_18D1&PID_4EE4&MI_02", @"USB\VID_18D1&PID_4EE6&MI_01"
    };
    private static readonly Regex AndroidUsbId = new(
        @"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&REV_([0-9A-F]{4}))?(?:&MI_([0-9A-F]{2}))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string? CanonicalAndroidId(string? id, AndroidUsbMode mode)
    {
        if (id is null) return null;
        var match = AndroidUsbId.Match(id.Trim());
        if (!match.Success) return null;
        var canonical = $@"USB\VID_{match.Groups[1].Value.ToUpperInvariant()}&PID_{match.Groups[2].Value.ToUpperInvariant()}";
        if (match.Groups[4].Success) canonical += "&MI_" + match.Groups[4].Value.ToUpperInvariant();
        return RequiredAndroidIds(mode).Contains(canonical, StringComparer.OrdinalIgnoreCase) ? canonical : null;
    }

    private static IReadOnlyList<string> RequiredAndroidIds(AndroidUsbMode mode) => mode switch {
        AndroidUsbMode.Adb => AdbIds,
        AndroidUsbMode.Fastboot => FastbootIds,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static string HardwarePrefix(string instanceId)
    {
        int index = instanceId.LastIndexOf('\\');
        return index > 3 ? instanceId[..index] : instanceId;
    }

    internal static WindowsDriverCheckResult InspectAndroid(AndroidUsbMode mode)
    {
        _ = RequiredAndroidIds(mode);
        var unsupported = DescribeUnsupportedPlatform(
            OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture, Environment.OSVersion.Version);
        if (unsupported is not null) return UnsupportedResult(unsupported.Replace("Qualcomm 9008", "Android USB"));
        if (!OperatingSystem.IsWindows()) return UnsupportedResult("Windows is required.");
        try { return DecideAndroid(CollectAndroidEvidence(mode), mode); }
        catch (Exception ex) when (IsOsQueryFailure(ex)) { return UnknownResult(ex.Message); }
    }

    internal static WindowsDriverCheckResult DecideAndroid(DriverEvidenceSnapshot evidence, AndroidUsbMode mode)
    {
        if (!evidence.WindowsSupported) return UnsupportedResult(evidence.UnsupportedReason ?? "Unsupported platform.");
        if (evidence.EndpointQueryFailure is not null) return UnknownResult(evidence.EndpointQueryFailure);
        var verified = evidence.Packages.Where(IsVerifiedPackage).ToList();
        var missing = RequiredAndroidIds(mode).Where(id =>
            !verified.Any(p => p.MatchingHardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase))).ToArray();

        WindowsDriverCheckResult Problem(string detail) => new(WindowsDriverCheckState.Problem, null, null, Bound(detail));
        foreach (var endpoint in evidence.Endpoints)
        {
            var prefix = HardwarePrefix(endpoint.DeviceInstanceId);
            if (!endpoint.StatusQuerySucceeded)
                return UnknownResult($"Cannot read device state for {prefix}.");
            if (endpoint.Problem != 0 || (endpoint.Status & DnHasProblem) != 0 || (endpoint.Status & DnStarted) == 0)
                return Problem($"{prefix}: Windows device problem {endpoint.Problem}, status 0x{endpoint.Status:X8}.");
            if (endpoint.FailureDetail is not null)
                return UnknownResult($"Cannot read driver binding for {prefix}.");
            if (!string.Equals(endpoint.Service, WinUsbService, StringComparison.OrdinalIgnoreCase))
                return Problem($"{prefix}: expected WinUSB; bound service is '{endpoint.Service ?? "<none>"}'.");
            var package = FindPackage(verified, endpoint.BoundInfName);
            if (package is null)
            {
                if (evidence.PackageQueryFailure is not null) return UnknownResult(evidence.PackageQueryFailure);
                return Problem($"{prefix}: bound INF '{endpoint.BoundInfName}' has no verified Android driver signature.");
            }
            var id = CanonicalAndroidId(prefix, mode);
            if (id is null || !package.MatchingHardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                return Problem($"{prefix}: bound INF '{package.InfName}' does not cover this hardware ID.");
        }
        if (missing.Length != 0)
        {
            if (evidence.PackageQueryFailure is not null) return UnknownResult(evidence.PackageQueryFailure);
            if (evidence.Packages.Any(p => !IsVerifiedPackage(p)))
                return Problem("An Android driver package failed signature verification.");
            return new(WindowsDriverCheckState.Missing, null, null,
                Bound($"{mode} driver coverage is missing: {string.Join(", ", missing)}."));
        }
        var selected = evidence.Endpoints.Count == 0 ? verified[0] : FindPackage(verified, evidence.Endpoints[0].BoundInfName)!;
        return new(evidence.Endpoints.Count == 0 ? WindowsDriverCheckState.Prepared : WindowsDriverCheckState.Connected,
            selected.DriverVersion, selected.InfName,
            $"{mode}: signed driver packages cover the supported USB IDs; {evidence.Endpoints.Count} present interface(s).");
    }

    [SupportedOSPlatform("windows")]
    private static DriverEvidenceSnapshot CollectAndroidEvidence(AndroidUsbMode mode)
    {
        var packages = new List<DriverPackageEvidence>();
        string? packageError = null;
        foreach (var inf in ListPublishedOemInfs())
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var package = InspectPublishedInf(inf, raw =>
                {
                    // A revision-specific INF line cannot prove support for all device revisions.
                    if (raw?.Contains("&REV_", StringComparison.OrdinalIgnoreCase) == true) return false;
                    var id = CanonicalAndroidId(raw, mode);
                    if (id is null) return false;
                    ids.Add(id);
                    return true;
                });
                if (package is not null) packages.Add(package with { MatchingHardwareIds = ids.ToArray() });
            }
            catch (Exception ex) when (IsOsQueryFailure(ex)) { packageError ??= Bound($"INF '{inf}': {ex.Message}"); }
        }
        var endpoints = new List<DriverEndpointEvidence>();
        string? endpointError = null;
        try { CollectAndroidBindings(mode, endpoints); }
        catch (Exception ex) when (IsOsQueryFailure(ex)) { endpointError = Bound(ex.Message); }
        return new(true, null, packageError, endpointError, packages, endpoints);
    }

    [SupportedOSPlatform("windows")]
    private static void CollectAndroidBindings(AndroidUsbMode mode, List<DriverEndpointEvidence> results)
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, UsbEnumerator, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == InvalidHandleValue || set == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
                    throw new Win32Exception(error);
                }
                var instanceId = ReadDeviceInstanceId(set, ref data);
                if (CanonicalAndroidId(HardwarePrefix(instanceId), mode) is not null)
                    results.Add(InspectEndpointBinding(set, instanceId, ref data));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
