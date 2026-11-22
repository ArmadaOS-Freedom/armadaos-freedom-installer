using System.Reflection;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class WindowsAndroidDriverTests
{
    private const string D00d = @"USB\VID_18D1&PID_D00D";
    private const string Ee0 = @"USB\VID_18D1&PID_4EE0";
    private const string AdbCompatible = @"USB\Class_FF&SubClass_42&Prot_01";
    private static readonly string[] Adb = {
        @"USB\VID_18D1&PID_4E11", @"USB\VID_18D1&PID_D001", @"USB\VID_18D1&PID_4EE7",
        @"USB\VID_18D1&PID_4EE2&MI_01", @"USB\VID_18D1&PID_4EE4&MI_02", @"USB\VID_18D1&PID_4EE6&MI_01"
    };

    [Theory]
    [InlineData(@"USB\VID_18D1&PID_D00D", AndroidUsbMode.Fastboot, D00d)]
    [InlineData(@"usb\vid_18d1&pid_d00d&rev_0100", AndroidUsbMode.Fastboot, D00d)]
    [InlineData(@"USB\VID_18D1&PID_4EE0", AndroidUsbMode.Fastboot, Ee0)]
    [InlineData(@"USB\VID_18D1&PID_4EE2&REV_0515&MI_01", AndroidUsbMode.Adb, @"USB\VID_18D1&PID_4EE2&MI_01")]
    [InlineData(@"USB\VID_18D1&PID_4EE2&MI_01", AndroidUsbMode.Fastboot, null)]
    [InlineData(@"USB\VID_18D1&PID_D00D", AndroidUsbMode.Adb, null)]
    [InlineData(@"USB\VID_18D1&PID_4EE2", AndroidUsbMode.Adb, null)]
    [InlineData(@"USB\VID_18D1&PID_D00D0", AndroidUsbMode.Fastboot, null)]
    [InlineData(@"USB\VID_18D1&PID_D00D\serial", AndroidUsbMode.Fastboot, null)]
    [InlineData(@"PCI\VID_18D1&PID_D00D", AndroidUsbMode.Fastboot, null)]
    [InlineData(@"USB\VID_18D1&PID_D00D&MI_00", AndroidUsbMode.Fastboot, null)]
    [InlineData(@"USB\VID_18D1&PID_D00D&VID_18D1", AndroidUsbMode.Fastboot, null)]
    public void HardwareIdsAreExactAndModeSpecific(string id, AndroidUsbMode mode, string? expected) =>
        Assert.Equal(expected, Method("CanonicalAndroidId").Invoke(null, new object?[] { id, mode }));

    [Fact]
    public void NonWindowsInspectionDoesNotInvokeNativeApis()
    {
        Assert.False(OperatingSystem.IsWindows());
        Assert.Equal(WindowsDriverCheckState.Unsupported, new WindowsAndroidDriver().Inspect(AndroidUsbMode.Fastboot).State);
    }

    [Fact]
    public void NoPackagesIsMissing() => Assert.Equal(WindowsDriverCheckState.Missing, Decide([]).State);

    [Fact]
    public void GoogleOnlyDoesNotPrepareD00d() =>
        Assert.Equal(WindowsDriverCheckState.Missing, Decide([Package("oem1.inf", [Ee0])]).State);

    [Fact]
    public void TwoVerifiedPackagesCanSupplyFullCoverage() =>
        Assert.Equal(WindowsDriverCheckState.Prepared,
            Decide([Package("oem1.inf", [Ee0]), Package("oem2.inf", [D00d])]).State);

    [Fact]
    public void AdbRequiresItsOwnInterfaceSet()
    {
        Assert.False(Decide([Package("oem1.inf", [D00d, Ee0])], mode: AndroidUsbMode.Adb).IsReady);
        Assert.Equal(WindowsDriverCheckState.Prepared,
            Decide([Package("oem98.inf", Adb[..4], signed: true)], mode: AndroidUsbMode.Adb).State);
    }

    [Fact]
    public void AdbEndpointRequiresBoundPackage()
    {
        var adb = Package("oem98.inf", Adb[..4], signed: true);
        var endpoint = Create("DriverEndpointEvidence", Adb[0] + @"\PRIVATE-SERIAL", "oem98.inf", "WinUSB", 8u, 0u, true, null);
        Assert.Equal(WindowsDriverCheckState.Connected, Decide([adb], [endpoint], mode: AndroidUsbMode.Adb).State);
    }

    [Fact]
    public void AdbUntrustedBindingIsProblem()
    {
        var adb = Package("oem98.inf", Adb[..4], signed: true);
        var google = Package("oem56.inf", Adb, signed: false);
        var endpoint = Create("DriverEndpointEvidence", Adb[4] + @"\PRIVATE-SERIAL", "oem56.inf", "WinUSB", 8u, 0u, true, null);
        Assert.Equal(WindowsDriverCheckState.Problem, Decide([adb, google], [endpoint], mode: AndroidUsbMode.Adb).State);
    }

    [Fact]
    public void RejectedGooglePackageRemainsProblem()
    {
        Assert.Equal(WindowsDriverCheckState.Problem,
            Decide([Package("oem56.inf", Adb, signed: false)], mode: AndroidUsbMode.Adb).State);
    }

    [Fact]
    public void MissingAdbEndpointsReportRejectedGooglePackage()
    {
        var adb = Package("oem98.inf", Adb[..4], signed: true);
        var google = Package("oem56.inf", Adb, signed: false);
        google.GetType().GetProperty("SignatureErrorCode")!.SetValue(google, unchecked((int)0xE0000242));
        var result = Decide([adb, google], mode: AndroidUsbMode.Adb);
        Assert.Equal(WindowsDriverCheckState.Prepared, result.State);
        Assert.Equal(Adb[4..], result.MissingHardwareIds.ToArray());
        var rejection = Assert.Single(result.RejectedPackages);
        Assert.Equal("oem56.inf", rejection.PublishedInf);
        Assert.Equal<int?>(unchecked((int)0xE0000242), rejection.SignatureErrorCode);
    }

    [Fact]
    public void UnsignedPackageDoesNotPrepareDriver() =>
        Assert.Equal(WindowsDriverCheckState.Problem, Decide([Package("oem1.inf", [D00d, Ee0], false)]).State);

    [Fact]
    public void HealthyEndpointRequiresItsOwnBoundPackageCoverage()
    {
        var packages = new[] { Package("oem1.inf", [Ee0]), Package("oem2.inf", [D00d]) };
        Assert.Equal(WindowsDriverCheckState.Problem, Decide(packages, [Endpoint(inf: "oem1.inf")]).State);
        Assert.Equal(WindowsDriverCheckState.Connected, Decide(packages, [Endpoint(inf: "oem2.inf")]).State);
    }

    [Theory]
    [InlineData("qcusbser", 8u, 0u, true, WindowsDriverCheckState.Problem)]
    [InlineData("WinUSB", 0u, 28u, true, WindowsDriverCheckState.Problem)]
    [InlineData("WinUSB", 1032u, 10u, true, WindowsDriverCheckState.Problem)]
    [InlineData("WinUSB", 8u, 0u, false, WindowsDriverCheckState.Unknown)]
    [InlineData("WinUSB", 8u, 0u, true, WindowsDriverCheckState.Connected)]
    public void BindingAndPnpStateControlReadiness(string service, uint status, uint problem, bool query, WindowsDriverCheckState expected)
    {
        var result = Decide([Package("oem1.inf", [D00d, Ee0])], [Endpoint("oem1.inf", service, status, problem, query)]);
        Assert.Equal(expected, result.State);
        Assert.DoesNotContain("PRIVATE-SERIAL", result.Detail);
    }

    [Fact]
    public void QueryFailuresNeverBecomeMissingOrReady()
    {
        Assert.Equal(WindowsDriverCheckState.Unknown, Decide([], packageFailure: "denied").State);
        Assert.Equal(WindowsDriverCheckState.Unknown, Decide([Package("oem1.inf", [Ee0, D00d])], endpointFailure: "denied").State);
    }

    [Fact]
    public void EndpointQueryFailureStillCarriesPackageEvidence()
    {
        var adb = Package("oem98.inf", Adb[..4], signed: true);
        var google = Package("oem56.inf", Adb, signed: false);
        google.GetType().GetProperty("SignatureErrorCode")!.SetValue(google, unchecked((int)0xE0000242));
        var result = Decide([adb, google], endpointFailure: "fixture endpoint query unavailable", mode: AndroidUsbMode.Adb);
        Assert.Equal(WindowsDriverCheckState.Unknown, result.State);
        Assert.Equal(Adb[4..], result.MissingHardwareIds.ToArray());
        var rejection = Assert.Single(result.RejectedPackages);
        Assert.Equal("oem56.inf", rejection.PublishedInf);
        Assert.Equal<int?>(unchecked((int)0xE0000242), rejection.SignatureErrorCode);
    }

    [Theory]
    [InlineData(true, "SERIAL-01")]
    [InlineData(false, null)]
    public void ResolveAdbUsbSerialUsesParentCompositeSerialOnlyWhenUnique(bool uniqueId, string? expected) =>
        Assert.Equal(expected, Method("ResolveAdbUsbSerial").Invoke(null,
            new object?[] { @"USB\VID_17EF&PID_7FE2&MI_01\6&ABC&0&0001", @"USB\VID_17EF&PID_7FE2\SERIAL-01", uniqueId }));

    [Theory]
    [InlineData("SERIAL_01", "SERIAL_01")]
    [InlineData("SERIAL.01", "SERIAL.01")]
    public void ResolveAdbUsbSerialAcceptsCompositeSerialWithUnderscoreAndDot(string parentSerial, string expected) =>
        Assert.Equal(expected, Method("ResolveAdbUsbSerial").Invoke(null,
            new object?[] { @"USB\VID_17EF&PID_7FE2&MI_01\6&ABC&0&0001", @"USB\VID_17EF&PID_7FE2\" + parentSerial, true }));

    [Fact]
    public void ResolveAdbUsbSerialRejectsNonUsbParents()
    {
        Assert.Null(Method("ResolveAdbUsbSerial").Invoke(null, new object?[] { @"USB\VID_17EF&PID_7FE2&MI_01\6&ABC&0&0001", @"USB\VID_17EF&PID_7FE3\SERIAL-01", true }));
        Assert.Null(Method("ResolveAdbUsbSerial").Invoke(null, new object?[] { @"USB\VID_17EF&PID_7FE2&MI_01\6&ABC&0&0001", @"USB\VID_17EF&PID_7FE2&MI_00\SERIAL-01", true }));
    }

    [Fact]
    public void ResolveAdbUsbSerialRejectsGeneratedParentSuffixAndAcceptsOwnSerial()
    {
        Assert.Null(Method("ResolveAdbUsbSerial").Invoke(null, new object?[] { @"USB\VID_17EF&PID_7FE2&MI_01\6&ABC&0&0001", @"USB\VID_17EF&PID_7FE2\6&ABC&0&0001", true }));
        Assert.Equal("SERIAL-01", Method("ResolveAdbUsbSerial").Invoke(null, new object?[] { @"USB\VID_17EF&PID_7FE2\SERIAL-01", null, true }));
    }

    private static WindowsDriverCheckResult Decide(object[] packages, object[]? endpoints = null,
        string? packageFailure = null, string? endpointFailure = null, AndroidUsbMode mode = AndroidUsbMode.Fastboot)
    {
        var snapshot = Create("DriverEvidenceSnapshot", true, null, packageFailure, endpointFailure,
            Typed("DriverPackageEvidence", packages), Typed("DriverEndpointEvidence", endpoints ?? []));
        return (WindowsDriverCheckResult)Method("DecideAndroid").Invoke(null, [snapshot, mode])!;
    }
    [Fact]
    public void SelectedAdbMissingWhenNoMatchingEndpointPresent()
    {
        var adb = Package("oem98.inf", Adb, signed: true);
        var snapshot = CreateSnapshot([adb], [Endpoint("oem98.inf")]);
        Assert.Equal(WindowsDriverCheckState.Missing, SelectedAdb(snapshot, "SERIAL-01").State);
    }

    [Fact]
    public void SelectedAdbConnectsExactOemSignedBinding()
    {
        var adb = Package("oem98.inf", [AdbCompatible], signed: true);
        var google = Package("oem56.inf", Adb, signed: false);
        var good = SelectedEndpoint("oem98.inf", "SERIAL-01");
        var bad = SelectedEndpoint("oem56.inf", "SERIAL-02");
        Assert.Equal(WindowsDriverCheckState.Connected, SelectedAdb(CreateSnapshot([adb, google], [good, bad]), "SERIAL-01").State);
        Assert.Equal(WindowsDriverCheckState.Problem, SelectedAdb(CreateSnapshot([adb, google], [good, bad]), "SERIAL-02").State);
    }

    [Fact]
    public void SelectedAdbDuplicateSerialIsProblem()
    {
        var adb = Package("oem98.inf", [AdbCompatible], signed: true);
        var a = SelectedEndpoint("oem98.inf", "SERIAL-01");
        var b = SelectedEndpoint("oem98.inf", "SERIAL-01");
        Assert.Equal(WindowsDriverCheckState.Problem, SelectedAdb(CreateSnapshot([adb], [a, b]), "SERIAL-01").State);
    }

    [Fact]
    public void SelectedAdbAcceptsArbitraryVidPidWithInboxWinUsbPackage()
    {
        const string arbitrary = @"USB\VID_1234&PID_ABCD&MI_02";
        var ep = Create("DriverEndpointEvidence", arbitrary + @"\SERIAL-01", "winusb.inf", "WinUSB", 8u, 0u, true, null);
        ep.GetType().GetProperty("UsbSerial")!.SetValue(ep, "SERIAL-01");
        ep.GetType().GetProperty("AdbCompatible")!.SetValue(ep, true);
        ep.GetType().GetProperty("InboxAdbPackage")!.SetValue(ep, Package("winusb.inf", [AdbCompatible], signed: true));
        Assert.Equal(WindowsDriverCheckState.Connected, SelectedAdb(CreateSnapshot([], [ep]), "SERIAL-01").State);
        ep.GetType().GetProperty("InboxAdbPackage")!.SetValue(ep, null);
        Assert.Equal(WindowsDriverCheckState.Problem, SelectedAdb(CreateSnapshot([], [ep]), "SERIAL-01").State);
        ep.GetType().GetProperty("InboxAdbPackage")!.SetValue(ep, Package("winusb.inf", [AdbCompatible], signed: false));
        Assert.Equal(WindowsDriverCheckState.Problem, SelectedAdb(CreateSnapshot([], [ep]), "SERIAL-01").State);
    }

    [Fact]
    public void SelectedAdbAcceptsArbitraryVidPidWithSignedOemGenericCompatibleId()
    {
        const string arbitrary = @"USB\VID_1234&PID_ABCD&MI_02";
        var adb = Package("oem42.inf", [AdbCompatible], signed: true);
        var endpoint = SelectedEndpoint("oem42.inf", "SERIAL-01", arbitrary);
        Assert.Equal(WindowsDriverCheckState.Connected, SelectedAdb(CreateSnapshot([adb], [endpoint]), "SERIAL-01").State);
    }

    [Fact]
    public void SelectedAdbRejectsOemPackageWithoutGenericCompatibleId()
    {
        const string arbitrary = @"USB\VID_1234&PID_ABCD&MI_02";
        var adb = Package("oem42.inf", [arbitrary], signed: true);
        var endpoint = SelectedEndpoint("oem42.inf", "SERIAL-01", arbitrary);
        Assert.Equal(WindowsDriverCheckState.Problem, SelectedAdb(CreateSnapshot([adb], [endpoint]), "SERIAL-01").State);
    }

    [Fact]
    public void SelectedAdbMatchingSerialWithoutAdbCompatibleIdIsMissing()
    {
        const string arbitrary = @"USB\VID_1234&PID_ABCD&MI_00";
        var endpoint = Create("DriverEndpointEvidence", arbitrary + @"\SERIAL-01", "oem42.inf", "WinUSB", 8u, 0u, true, null);
        endpoint.GetType().GetProperty("UsbSerial")!.SetValue(endpoint, "SERIAL-01");
        Assert.Equal(WindowsDriverCheckState.Missing, SelectedAdb(CreateSnapshot([], [endpoint]), "SERIAL-01").State);
    }

    [Fact]
    public void SelectedAdbUnknownDriverIsProblem()
    {
        var endpoint = SelectedEndpoint("unknown.inf", "SERIAL-01", @"USB\VID_1234&PID_ABCD&MI_02");
        Assert.Equal(WindowsDriverCheckState.Problem, SelectedAdb(CreateSnapshot([], [endpoint]), "SERIAL-01").State);
    }

    [Fact]
    public async Task DiagnosticBundle_SelectedAdbVerifierDetail_RedactsSyntheticIdentityButKeepsCauseAndCode()
    {
        var ep = Create("DriverEndpointEvidence", @"USB\VID_17EF&PID_7FE2&MI_01\PRIVATE_USB.77", "winusb.inf", "WinUSB", 8u, 0u, true,
            "SetupVerifyInfFileW could not verify 0x00000005 for " + @"USB\VID_17EF&PID_7FE2&MI_01\PRIVATE_USB.77 using " + @"C:\Users\private-fixture\driver.inf");
        ep.GetType().GetProperty("UsbSerial")!.SetValue(ep, "PRIVATE_USB.77");
        ep.GetType().GetProperty("AdbCompatible")!.SetValue(ep, true);
        var result = SelectedAdb(CreateSnapshot([], [ep]), "PRIVATE_USB.77");
        Assert.Equal(WindowsDriverCheckState.Unknown, result.State);

        var dest = Directory.CreateTempSubdirectory();
        try
        {
            var request = new DiagnosticExportRequest("summary", "log", [], [], false, false)
            {
                HostEnvironment = new Dictionary<string, string?> { ["driver.adb.deviceCheck.detail"] = result.Detail }
            };
            await DiagnosticBundle.ExportAsync(Path.Combine(dest.FullName, "bundle.zip"), request);
            using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(dest.FullName, "bundle.zip"));
            using var stream = zip.Entries.Single(e => e.FullName == "environment.json").Open();
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            var text = doc.RootElement.GetProperty("driver.adb.deviceCheck.detail").GetString();
            Assert.Contains("SetupVerifyInfFileW could not verify", text);
            Assert.Contains("0x00000005", text);
            Assert.DoesNotContain("private-fixture", text);
            Assert.DoesNotContain("PRIVATE_USB.77", text);
        }
        finally { dest.Delete(true); }
    }

    private static WindowsDriverCheckResult SelectedAdb(object snapshot, string serial) =>
        (WindowsDriverCheckResult)Method("DecideSelectedAdb").Invoke(null, [snapshot, serial])!;
    private static object CreateSnapshot(object[] packages, object[] endpoints) =>
        Create("DriverEvidenceSnapshot", true, null, null, null,
            Typed("DriverPackageEvidence", packages), Typed("DriverEndpointEvidence", endpoints));
    private static object SelectedEndpoint(string inf, string serial, string? hardwareId = null)
    {
        var obj = Create("DriverEndpointEvidence", (hardwareId ?? Adb[0]) + @"\" + serial, inf, "WinUSB", 8u, 0u, true, null);
        obj.GetType().GetProperty("UsbSerial")!.SetValue(obj, serial);
        obj.GetType().GetProperty("AdbCompatible")!.SetValue(obj, true);
        return obj;
    }
    private static object Package(string inf, string[] ids, bool signed = true)
    {
        var obj = Create("DriverPackageEvidence", inf, @"C:\Windows\DriverStore\android_winusb.inf",
            "11.0", "signed publisher", "android.cat", signed, null);
        obj.GetType().GetProperty("MatchingHardwareIds")!.SetValue(obj, ids);
        return obj;
    }
    private static object Endpoint(string inf, string service = "WinUSB", uint status = 8,
        uint problem = 0, bool query = true) =>
        Create("DriverEndpointEvidence", D00d + @"\PRIVATE-SERIAL", inf, service, status, problem, query, null);

    private static Type Type(string name) => typeof(WindowsAndroidDriver).Assembly.GetType("FreedomInstaller.Core." + name, true)!;
    private static object Create(string name, params object?[] args) => Activator.CreateInstance(Type(name), args)!;
    private static Array Typed(string name, object[] items)
    {
        var array = Array.CreateInstance(Type(name), items.Length);
        for (var i = 0; i < items.Length; i++) array.SetValue(items[i], i);
        return array;
    }
    private static MethodInfo Method(string name) =>
        typeof(WindowsQualcommDriver).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
}
