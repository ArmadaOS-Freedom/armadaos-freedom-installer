using System;
using System.IO;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateSshPasswordOptionsTests
{
    private static string TrustDirectory()
    {
        var root = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be set.");
        var directory = Path.Combine(root, "PasswordOptions_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void ToString_RedactsPassword()
    {
        var options = new OfflineUpdateSshPasswordOptions(
            "device.example", 22, "armada", "s3cret-value", TrustDirectory());

        var text = options.ToString();

        Assert.Contains("<redacted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-value", text, StringComparison.Ordinal);
        Assert.Contains("armada", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearPassword_DropsStoredSecret()
    {
        var options = new OfflineUpdateSshPasswordOptions(
            "device.example", 22, "armada", "s3cret-value", TrustDirectory());

        Assert.True(options.HasPassword);
        options.ClearPassword();

        Assert.False(options.HasPassword);
        Assert.Throws<InvalidOperationException>(() => options.Password);
        Assert.DoesNotContain("s3cret-value", options.ToString(), StringComparison.Ordinal);
        Assert.Contains("<cleared>", options.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("line\rbreak")]
    [InlineData("line\nbreak")]
    [InlineData("nul\0byte")]
    public void Constructor_RejectsUnusablePasswords(string password)
    {
        Assert.Throws<ArgumentException>(() =>
            new OfflineUpdateSshPasswordOptions("device.example", 22, "armada", password, TrustDirectory()));
    }

    [Fact]
    public void Constructor_RejectsInvalidEndpointValues()
    {
        var trust = TrustDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OfflineUpdateSshPasswordOptions("device.example", 0, "armada", "secret", trust));
        Assert.Throws<ArgumentException>(() =>
            new OfflineUpdateSshPasswordOptions("device.example", 22, "root\n", "secret", trust));
        Assert.Throws<ArgumentException>(() =>
            new OfflineUpdateSshPasswordOptions("device.example", 22, "armada", "secret", "relative-dir"));
        Assert.Throws<ArgumentException>(() =>
            new OfflineUpdateSshPasswordOptions("device.example", 22, "armada", "secret", trust, "192.168.1.2"));
        Assert.Throws<ArgumentException>(() =>
            new OfflineUpdateSshPasswordOptions("device.example", 22, "armada", "secret", trust, "172.16.42.200 "));
    }

    [Fact]
    public void AuthPolicy_DefaultIsPasswordAndSwitchesFieldVisibility()
    {
        Assert.Equal(OfflineUpdateSshAuthMode.Password, OfflineUpdateSshAuthPolicy.FromSelectionIndex(0));
        Assert.Equal(OfflineUpdateSshAuthMode.PublicKey, OfflineUpdateSshAuthPolicy.FromSelectionIndex(1));
        Assert.Equal(OfflineUpdateSshAuthMode.Password, OfflineUpdateSshAuthPolicy.FromSelectionIndex(-1));

        var password = OfflineUpdateSshAuthPolicy.Fields(OfflineUpdateSshAuthMode.Password);
        Assert.True(password.ShowPassword);
        Assert.False(password.ShowKeyPath);
        Assert.False(password.ShowKnownHosts);

        var key = OfflineUpdateSshAuthPolicy.Fields(OfflineUpdateSshAuthMode.PublicKey);
        Assert.False(key.ShowPassword);
        Assert.True(key.ShowKeyPath);
        Assert.True(key.ShowKnownHosts);
    }

    [Fact]
    public void DefaultCredentialPolicy_TriesOnlyOnceAndNeverForManualOrKey()
    {
        Assert.True(OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
            OfflineUpdateSshAuthMode.Password, credentialsSuppliedByUser: false, defaultAlreadyAttempted: false));

        Assert.False(OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
            OfflineUpdateSshAuthMode.Password, credentialsSuppliedByUser: true, defaultAlreadyAttempted: false));

        Assert.False(OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
            OfflineUpdateSshAuthMode.Password, credentialsSuppliedByUser: false, defaultAlreadyAttempted: true));

        Assert.False(OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
            OfflineUpdateSshAuthMode.PublicKey, credentialsSuppliedByUser: false, defaultAlreadyAttempted: false));
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("admin", "")]
    [InlineData("admin", "secret")]
    public void ManualCredentialInput_IsDetectedFromEitherField(string userName, string password) =>
        Assert.True(OfflineUpdateSshAuthPolicy.HasManualCredentialInput(userName, password));

    [Fact]
    public void ManualCredentialInput_TreatsUserNameWithoutPasswordAsManual()
    {
        // The factory account must never replace a typed name just because the
        // password field is blank; the UI asks for the missing password.
        Assert.True(OfflineUpdateSshAuthPolicy.HasManualCredentialInput("admin", string.Empty));
        Assert.False(OfflineUpdateSshAuthPolicy.HasManualCredentialInput(null, string.Empty));
        Assert.False(OfflineUpdateSshAuthPolicy.HasManualCredentialInput("   ", string.Empty));
    }

    [Fact]
    public void EndpointKey_IsStableAndDistinguishesUsbFromWifiTargets()
    {
        Assert.Equal("192.168.42.129:22", OfflineUpdateSshAuthPolicy.EndpointKey(" 192.168.42.129 ", 22));
        Assert.Equal(
            OfflineUpdateSshAuthPolicy.EndpointKey("device.example", 22),
            OfflineUpdateSshAuthPolicy.EndpointKey("DEVICE.Example", 22));
        Assert.NotEqual(
            OfflineUpdateSshAuthPolicy.EndpointKey("192.168.42.129", 22),
            OfflineUpdateSshAuthPolicy.EndpointKey("192.168.1.50", 22));
        Assert.NotEqual(
            OfflineUpdateSshAuthPolicy.EndpointKey("192.168.1.50", 22),
            OfflineUpdateSshAuthPolicy.EndpointKey("192.168.1.50", 2222));
    }

    [Fact]
    public void DefaultCredential_RejectedOnOneEndpointStillAllowsTheOther()
    {
        var rejected = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OfflineUpdateSshAuthPolicy.EndpointKey("192.168.42.129", 22),
        };

        Assert.False(OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
            OfflineUpdateSshAuthMode.Password,
            credentialsSuppliedByUser: false,
            defaultAlreadyAttempted: rejected.Contains(OfflineUpdateSshAuthPolicy.EndpointKey("192.168.42.129", 22))));

        Assert.True(OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
            OfflineUpdateSshAuthMode.Password,
            credentialsSuppliedByUser: false,
            defaultAlreadyAttempted: rejected.Contains(OfflineUpdateSshAuthPolicy.EndpointKey("192.168.1.50", 22))));
    }

    [Fact]
    public void DefaultCredentialRejectionKey_KeepsWifiOnHostAndPort()
    {
        // Wi-Fi keeps exactly the previous identity: no device instance is
        // involved, and the port still distinguishes targets on one host.
        Assert.Equal(
            OfflineUpdateSshAuthPolicy.EndpointKey("192.168.42.129", 22),
            OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey("192.168.42.129", 22));
        Assert.Equal(
            OfflineUpdateSshAuthPolicy.EndpointKey("device.example", 22),
            OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey("device.example", 22, "   "));
        Assert.NotEqual(
            OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey("192.168.1.50", 22),
            OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey("192.168.1.50", 2222));
    }

    [Fact]
    public void DefaultCredentialRejectionKey_SeparatesUsbInstancesSharingOneEndpoint()
    {
        const string firstUsb = @"USB\VID_1D6B&PID_0104&MI_00\6&1A2B3C4D&0&0000";
        const string secondUsb = @"USB\VID_1D6B&PID_0104&MI_00\6&1A2B3C4D&0&0001";
        string usbHost = OfflineUpdateUsbDiscovery.DeviceHost;
        const int usbPort = 22;

        string firstKey = OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey(usbHost, usbPort, firstUsb);
        string secondKey = OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey(usbHost, usbPort, secondUsb);

        // The same device on the same endpoint reuses its key, so its one
        // automatic attempt stays consumed after a rejection.
        Assert.Equal(
            firstKey,
            OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey(usbHost, usbPort, firstUsb));
        Assert.Equal(
            firstKey,
            OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey(usbHost, usbPort, firstUsb.ToLowerInvariant()));

        // A different USB instance on the very same USB endpoint keeps its own
        // attempt instead of inheriting the rejection.
        Assert.NotEqual(firstKey, secondKey);

        // The USB key never collides with the plain host/port key, so a Wi-Fi
        // rejection cannot consume the USB attempt either.
        Assert.NotEqual(OfflineUpdateSshAuthPolicy.EndpointKey(usbHost, usbPort), firstKey);

        var rejected = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { firstKey };
        Assert.False(OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
            OfflineUpdateSshAuthMode.Password, credentialsSuppliedByUser: false,
            defaultAlreadyAttempted: rejected.Contains(firstKey)));
        Assert.True(OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
            OfflineUpdateSshAuthMode.Password, credentialsSuppliedByUser: false,
            defaultAlreadyAttempted: rejected.Contains(secondKey)));
    }

    [Fact]
    public void DefaultCredential_IsPublicConfigurationButToStringIsRedacted()
    {
        var credential = OfflineUpdateSshDefaultCredentials.Instance;
        Assert.Equal("armada", credential.UserName);
        Assert.Equal("armada", credential.Password);
        Assert.DoesNotContain("Password = armada", credential.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", credential.ToString(), StringComparison.Ordinal);
    }
}
