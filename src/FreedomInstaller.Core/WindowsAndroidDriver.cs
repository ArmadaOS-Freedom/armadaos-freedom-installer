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

    public WindowsDriverCheckResult InspectSelectedAdb(string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        return WindowsQualcommDriver.InspectAndroid(AndroidUsbMode.Adb, serial);
    }
}

// Shares signature verification and native SetupAPI bindings with the 9008 probe.
public sealed partial class WindowsQualcommDriver
{
    internal const string LenovoAdbHardwareId = @"USB\VID_17EF&PID_7FE2&MI_01";
    internal const string InboxWinUsbInf = "winusb.inf";
    internal const string AdbCompatibleUsbId = @"USB\Class_FF&SubClass_42&Prot_01";

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

    private static string? AndroidEndpointHardwareId(string? prefix, AndroidUsbMode mode, bool selected)
    {
        var canonical = CanonicalAndroidId(prefix, mode);
        if (canonical is not null) return canonical;
        return selected && mode == AndroidUsbMode.Adb
            && string.Equals(prefix, LenovoAdbHardwareId, StringComparison.OrdinalIgnoreCase)
            ? LenovoAdbHardwareId
            : null;
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

    internal static WindowsDriverCheckResult InspectAndroid(AndroidUsbMode mode, string? selectedAdbSerial = null)
    {
        _ = RequiredAndroidIds(mode);
        var unsupported = DescribeUnsupportedPlatform(
            OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture, Environment.OSVersion.Version);
        if (unsupported is not null) return UnsupportedResult(unsupported.Replace("Qualcomm 9008", "Android USB"));
        if (!OperatingSystem.IsWindows()) return UnsupportedResult("Windows is required.");
        try
        {
            var snapshot = CollectAndroidEvidence(mode, selectedAdbSerial);
            return selectedAdbSerial is null
                ? DecideAndroid(snapshot, mode)
                : DecideSelectedAdb(snapshot, selectedAdbSerial);
        }
        catch (Exception ex) when (IsOsQueryFailure(ex)) { return UnknownResult(ex.Message); }
    }

    internal static WindowsDriverCheckResult DecideSelectedAdb(DriverEvidenceSnapshot evidence, string serial)
    {
        var matches = evidence.Endpoints
            .Where(e => e.UsbSerial is not null && string.Equals(e.UsbSerial, serial, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        var decision = DecideAndroid(
            evidence with { Endpoints = matches.Count == 1 ? matches : Array.Empty<DriverEndpointEvidence>() },
            AndroidUsbMode.Adb);
        if (evidence.EndpointQueryFailure is not null
            || decision.State == WindowsDriverCheckState.Unsupported
            || matches.Count == 1)
            return decision;
        return decision with {
            State = matches.Count == 0 ? WindowsDriverCheckState.Missing : WindowsDriverCheckState.Problem,
            DriverVersion = null,
            PublishedInf = null,
            Detail = Bound(matches.Count == 0
                ? "No present ADB USB interface matches the selected device."
                : "More than one present ADB USB interface matches the selected device.") };
    }

    internal static WindowsDriverCheckResult DecideAndroid(DriverEvidenceSnapshot evidence, AndroidUsbMode mode)
    {
        if (!evidence.WindowsSupported) return UnsupportedResult(evidence.UnsupportedReason ?? "Unsupported platform.");
        var verified = evidence.Packages.Where(IsVerifiedPackage).ToList();
        var missing = RequiredAndroidIds(mode).Where(id =>
            !verified.Any(p => p.MatchingHardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase))).ToArray();
        var rejected = DescribeRejectedPackages(evidence).ToList();
        var canUsePartialAdbCoverage = mode == AndroidUsbMode.Adb
            && missing.Length < AdbIds.Length
            && evidence.PackageQueryFailure is null;

        WindowsDriverCheckResult WithEvidence(WindowsDriverCheckResult result) => result with {
            MissingHardwareIds = missing,
            RejectedPackages = rejected };

        WindowsDriverCheckResult Problem(string detail) => WithEvidence(new(WindowsDriverCheckState.Problem, null, null, Bound(detail)));
        if (evidence.EndpointQueryFailure is not null) return WithEvidence(UnknownResult(evidence.EndpointQueryFailure));
        foreach (var endpoint in evidence.Endpoints)
        {
            var prefix = HardwarePrefix(endpoint.DeviceInstanceId);
            if (!endpoint.StatusQuerySucceeded)
                return WithEvidence(UnknownResult($"Cannot read device state for {prefix}."));
            if (endpoint.Problem != 0 || (endpoint.Status & DnHasProblem) != 0 || (endpoint.Status & DnStarted) == 0)
                return Problem($"{prefix}: Windows device problem {endpoint.Problem}, status 0x{endpoint.Status:X8}.");
            if (endpoint.FailureDetail is not null)
                return WithEvidence(UnknownResult($"Cannot read driver binding for {prefix}."));
            if (!string.Equals(endpoint.Service, WinUsbService, StringComparison.OrdinalIgnoreCase))
                return Problem($"{prefix}: expected WinUSB; bound service is '{endpoint.Service ?? "<none>"}'.");
            bool selectedAdb = mode == AndroidUsbMode.Adb && evidence.Endpoints.Count == 1 && endpoint.UsbSerial is not null;
            var id = AndroidEndpointHardwareId(prefix, mode, selectedAdb);
            if (selectedAdb
                && endpoint.BoundInfName is not null
                && string.Equals(endpoint.BoundInfName, InboxWinUsbInf, StringComparison.OrdinalIgnoreCase))
            {
                var inbox = endpoint.InboxAdbPackage;
                if (id is null || inbox is null || !inbox.SignatureAccepted
                    || !string.Equals(inbox.InfName, InboxWinUsbInf, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(inbox.StoreInfPath)
                    || string.IsNullOrWhiteSpace(inbox.DigitalSigner)
                    || string.IsNullOrWhiteSpace(inbox.CatalogFile))
                {
                    if (inbox is not null)
                        rejected.Add(new WindowsRejectedDriverPackage(inbox.InfName, inbox.DriverVersion,
                            inbox.DigitalSigner, Path.GetFileName(inbox.CatalogFile),
                            inbox.SignatureErrorCode, inbox.SignatureDetail));
                    return Problem($"{prefix}: inbox {InboxWinUsbInf} does not satisfy the selected ADB binding requirements.");
                }
                return WithEvidence(new WindowsDriverCheckResult(WindowsDriverCheckState.Connected, inbox.DriverVersion, inbox.InfName,
                    $"{mode}: selected ADB interface bound to signed inbox {InboxWinUsbInf}; {prefix}."));
            }
            var package = FindPackage(verified, endpoint.BoundInfName);
            if (package is null)
            {
                if (evidence.PackageQueryFailure is not null) return WithEvidence(UnknownResult(evidence.PackageQueryFailure));
                return Problem($"{prefix}: bound INF '{endpoint.BoundInfName}' has no verified Android driver signature.");
            }
            if (id is null || !package.MatchingHardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                return Problem($"{prefix}: bound INF '{package.InfName}' does not cover this hardware ID.");
            if (selectedAdb)
                return WithEvidence(new WindowsDriverCheckResult(WindowsDriverCheckState.Connected, package.DriverVersion, package.InfName,
                    $"{mode}: selected ADB interface bound to signed OEM {package.InfName}; {prefix}."));
        }
        if (missing.Length != 0 && !canUsePartialAdbCoverage)
        {
            if (evidence.PackageQueryFailure is not null) return WithEvidence(UnknownResult(evidence.PackageQueryFailure));
            if (evidence.Packages.Any(p => !IsVerifiedPackage(p)))
                return Problem("An Android driver package failed signature verification.");
            return WithEvidence(new(WindowsDriverCheckState.Missing, null, null,
                Bound($"{mode} driver coverage is missing: {string.Join(", ", missing)}.")));
        }
        DriverPackageEvidence? selected;
        string detail;
        if (evidence.Endpoints.Count == 0)
        {
            selected = verified.FirstOrDefault(p => RequiredAndroidIds(mode).Any(id =>
                p.MatchingHardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase))) ?? verified[0];
        }
        else selected = FindPackage(verified, evidence.Endpoints[0].BoundInfName)!;
        if (canUsePartialAdbCoverage && missing.Length != 0)
        {
            var covered = AdbIds.Length - missing.Length;
            detail = $"{mode}: partial verified coverage {covered}/{AdbIds.Length}; remaining IDs: {string.Join(", ", missing)}; {evidence.Endpoints.Count} present interface(s).";
        }
        else detail = $"{mode}: signed driver packages cover the supported USB IDs; {evidence.Endpoints.Count} present interface(s).";
        return WithEvidence(new(evidence.Endpoints.Count == 0 ? WindowsDriverCheckState.Prepared : WindowsDriverCheckState.Connected,
            selected.DriverVersion, selected.InfName, detail));
    }

    [SupportedOSPlatform("windows")]
    private static DriverEvidenceSnapshot CollectAndroidEvidence(AndroidUsbMode mode, string? selectedAdbSerial = null)
    {
        var selected = selectedAdbSerial is not null;
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
                    var id = AndroidEndpointHardwareId(raw, mode, selected);
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
        try { CollectAndroidBindings(mode, endpoints, selectedAdbSerial); }
        catch (Exception ex) when (IsOsQueryFailure(ex)) { endpointError = Bound(ex.Message); }
        return new(true, null, packageError, endpointError, packages, endpoints);
    }

    [SupportedOSPlatform("windows")]
    private static void CollectAndroidBindings(AndroidUsbMode mode, List<DriverEndpointEvidence> results, string? selectedAdbSerial)
    {
        var selected = selectedAdbSerial is not null;
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
                if (AndroidEndpointHardwareId(HardwarePrefix(instanceId), mode, selected) is null) continue;
                if (!selected)
                {
                    results.Add(InspectEndpointBinding(set, instanceId, ref data));
                    continue;
                }
                var usbSerial = ReadAdbUsbSerial(data.DevInst, instanceId);
                if (!string.Equals(usbSerial, selectedAdbSerial, StringComparison.OrdinalIgnoreCase)) continue;
                var binding = InspectEndpointBinding(set, instanceId, ref data) with { UsbSerial = usbSerial };
                results.Add(binding);
                if (binding.BoundInfName is not null
                    && string.Equals(binding.BoundInfName, InboxWinUsbInf, StringComparison.OrdinalIgnoreCase)
                    && HasAdbCompatibleId(set, ref data))
                {
                    try
                    {
                        var inboxPackage = InspectPublishedInf(InboxWinUsbInf, candidate =>
                            string.Equals(candidate, AdbCompatibleUsbId, StringComparison.OrdinalIgnoreCase));
                        if (inboxPackage is not null) binding = binding with { InboxAdbPackage = inboxPackage };
                    }
                    catch (Exception ex) when (IsOsQueryFailure(ex)) { binding = binding with { FailureDetail = Bound("Windows could not verify the bound inbox ADB driver: " + ex.Message) }; }
                    results[^1] = binding;
                }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
