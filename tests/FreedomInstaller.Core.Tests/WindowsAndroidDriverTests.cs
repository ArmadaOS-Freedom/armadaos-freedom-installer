using System.Reflection;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class WindowsAndroidDriverTests
{
    private const string D00d = @"USB\VID_18D1&PID_D00D";
    private const string Ee0 = @"USB\VID_18D1&PID_4EE0";
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
            Decide([Package("oem1.inf", Adb)], mode: AndroidUsbMode.Adb).State);
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

    private static WindowsDriverCheckResult Decide(object[] packages, object[]? endpoints = null,
        string? packageFailure = null, string? endpointFailure = null, AndroidUsbMode mode = AndroidUsbMode.Fastboot)
    {
        var snapshot = Create("DriverEvidenceSnapshot", true, null, packageFailure, endpointFailure,
            Typed("DriverPackageEvidence", packages), Typed("DriverEndpointEvidence", endpoints ?? []));
        return (WindowsDriverCheckResult)Method("DecideAndroid").Invoke(null, [snapshot, mode])!;
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
