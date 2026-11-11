using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FreedomInstaller.App;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateConnectionUiStateTests
{
    [Theory]
    [InlineData(OfflineUpdateConnectionUiState.UsbTransportIndex, OfflineUpdateConnectionUiState.DefaultCredentialIndex, false, true, false, false, false)]
    [InlineData(OfflineUpdateConnectionUiState.UsbTransportIndex, OfflineUpdateConnectionUiState.CustomCredentialIndex, false, false, true, true, false)]
    [InlineData(OfflineUpdateConnectionUiState.UsbTransportIndex, OfflineUpdateConnectionUiState.PublicKeyCredentialIndex, false, false, true, false, true)]
    [InlineData(OfflineUpdateConnectionUiState.WifiTransportIndex, OfflineUpdateConnectionUiState.DefaultCredentialIndex, true, true, false, false, false)]
    [InlineData(OfflineUpdateConnectionUiState.WifiTransportIndex, OfflineUpdateConnectionUiState.CustomCredentialIndex, true, false, true, true, false)]
    [InlineData(OfflineUpdateConnectionUiState.WifiTransportIndex, OfflineUpdateConnectionUiState.PublicKeyCredentialIndex, true, false, true, false, true)]
    public void Fields_CoverEveryTransportAndCredentialCombination(
        int transportIndex,
        int credentialIndex,
        bool showWifiAddress,
        bool showDefaultAccount,
        bool showManualUserName,
        bool showCustomPassword,
        bool showKeyFields)
    {
        var fields = OfflineUpdateConnectionUiState.Fields(
            OfflineUpdateConnectionUiState.TransportFromIndex(transportIndex),
            OfflineUpdateConnectionUiState.CredentialFromIndex(credentialIndex));

        Assert.Equal(showWifiAddress, fields.ShowWifiAddress);
        Assert.Equal(showDefaultAccount, fields.ShowDefaultAccount);
        Assert.Equal(showManualUserName, fields.ShowManualUserName);
        Assert.Equal(showCustomPassword, fields.ShowCustomPassword);
        Assert.Equal(showKeyFields, fields.ShowKeyFields);
    }

    [Fact]
    public void Selection_DefaultsToUsbWithTheFactoryAccount()
    {
        Assert.Equal(OfflineUpdateTransport.Usb, OfflineUpdateConnectionUiState.TransportFromIndex(0));
        Assert.Equal(OfflineUpdateTransport.Wifi, OfflineUpdateConnectionUiState.TransportFromIndex(1));
        Assert.Equal(OfflineUpdateTransport.Usb, OfflineUpdateConnectionUiState.TransportFromIndex(-1));

        Assert.Equal(OfflineUpdateCredentialMode.DefaultAccount,
            OfflineUpdateConnectionUiState.CredentialFromIndex(OfflineUpdateConnectionUiState.DefaultCredentialIndex));
        Assert.Equal(OfflineUpdateCredentialMode.CustomAccount,
            OfflineUpdateConnectionUiState.CredentialFromIndex(OfflineUpdateConnectionUiState.CustomCredentialIndex));
        Assert.Equal(OfflineUpdateCredentialMode.PublicKey,
            OfflineUpdateConnectionUiState.CredentialFromIndex(OfflineUpdateConnectionUiState.PublicKeyCredentialIndex));
    }

    [Fact]
    public void DefaultRejected_SelectsCustomAndNeverReusesTheFactoryPassword()
    {
        var custom = OfflineUpdateConnectionUiState.Fields(
            OfflineUpdateTransport.Usb,
            OfflineUpdateConnectionUiState.CredentialFromIndex(OfflineUpdateConnectionUiState.CustomCredentialIndex));

        // The rejected attempt moves the visible selection to custom, which is
        // the only password selection that asks for a typed password.
        Assert.True(custom.ShowCustomPassword);
        Assert.False(custom.ShowDefaultAccount);
        Assert.False(OfflineUpdateConnectionUiState.TryResolvePasswordCredential(
            OfflineUpdateCredentialMode.CustomAccount, "armada", string.Empty, "armada", "armada", out _, out _));
    }

    [Fact]
    public void Route_IsTheExplicitSelectionAndNeverInferredFromTheAddress()
    {
        Assert.True(OfflineUpdateConnectionUiState.UsesUsbDiscovery(OfflineUpdateTransport.Usb));
        Assert.False(OfflineUpdateConnectionUiState.UsesWifiAddress(OfflineUpdateTransport.Usb));

        Assert.True(OfflineUpdateConnectionUiState.UsesWifiAddress(OfflineUpdateTransport.Wifi));
        Assert.False(OfflineUpdateConnectionUiState.UsesUsbDiscovery(OfflineUpdateTransport.Wifi));

        // A leftover Wi-Fi address never turns the USB route into a Wi-Fi one,
        // and a blank address never turns the Wi-Fi route into USB.
        Assert.False(OfflineUpdateConnectionUiState.IsWifiAddressMissing(OfflineUpdateTransport.Usb, string.Empty));
        Assert.True(OfflineUpdateConnectionUiState.IsWifiAddressMissing(OfflineUpdateTransport.Wifi, string.Empty));
        Assert.True(OfflineUpdateConnectionUiState.IsWifiAddressMissing(OfflineUpdateTransport.Wifi, "   "));
        Assert.False(OfflineUpdateConnectionUiState.IsWifiAddressMissing(OfflineUpdateTransport.Wifi, "192.168.42.129"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("abc", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("65536", false)]
    [InlineData("22", true)]
    [InlineData("2222", true)]
    [InlineData(" 2222 ", true)]
    public void WifiPort_RequiresAValueInsideTheTcpRange(string? text, bool expected) =>
        Assert.Equal(expected, OfflineUpdateConnectionUiState.TryParsePort(text, out _));

    [Fact]
    public void CustomCredentials_RequireBothFields()
    {
        Assert.True(OfflineUpdateConnectionUiState.IsCustomCredentialIncomplete(string.Empty, "secret"));
        Assert.True(OfflineUpdateConnectionUiState.IsCustomCredentialIncomplete("admin", string.Empty));
        Assert.True(OfflineUpdateConnectionUiState.IsCustomCredentialIncomplete("   ", "secret"));
        Assert.False(OfflineUpdateConnectionUiState.IsCustomCredentialIncomplete("admin", "secret"));

        // The custom selection never inherits the factory pair.
        Assert.False(OfflineUpdateConnectionUiState.TryResolvePasswordCredential(
            OfflineUpdateCredentialMode.CustomAccount, "admin", string.Empty, "armada", "armada", out _, out _));
    }

    [Fact]
    public void DefaultCredential_IgnoresStaleValuesLeftInHiddenCustomFields()
    {
        Assert.True(OfflineUpdateConnectionUiState.TryResolvePasswordCredential(
            OfflineUpdateCredentialMode.DefaultAccount,
            "stale-user",
            "stale-secret",
            "armada",
            "armada",
            out string userName,
            out string password));

        Assert.Equal("armada", userName);
        Assert.Equal("armada", password);

        Assert.True(OfflineUpdateConnectionUiState.TryResolvePasswordCredential(
            OfflineUpdateCredentialMode.CustomAccount,
            "admin",
            "typed-secret",
            "armada",
            "armada",
            out string customUserName,
            out string customPassword));
        Assert.Equal("admin", customUserName);
        Assert.Equal("typed-secret", customPassword);

        Assert.False(OfflineUpdateConnectionUiState.TryResolvePasswordCredential(
            OfflineUpdateCredentialMode.PublicKey, "admin", "typed-secret", "armada", "armada", out _, out _));
    }

    [Fact]
    public void UsbKey_UsesTheEnrollmentFlowOnlyWhenBothKeyFilesAreEmpty()
    {
        Assert.True(OfflineUpdateConnectionUiState.IsUsbKeyEnrollment(
            OfflineUpdateTransport.Usb, OfflineUpdateCredentialMode.PublicKey, string.Empty, string.Empty));

        // A partial entry is an explicit key connection that must error, not
        // silently enroll a new key.
        Assert.False(OfflineUpdateConnectionUiState.IsUsbKeyEnrollment(
            OfflineUpdateTransport.Usb, OfflineUpdateCredentialMode.PublicKey, "/home/user/id_ed25519", string.Empty));
        Assert.False(OfflineUpdateConnectionUiState.IsUsbKeyEnrollment(
            OfflineUpdateTransport.Usb, OfflineUpdateCredentialMode.PublicKey, string.Empty, "/home/user/known_hosts"));
        Assert.False(OfflineUpdateConnectionUiState.IsUsbKeyEnrollment(
            OfflineUpdateTransport.Usb, OfflineUpdateCredentialMode.PublicKey, "/home/user/id_ed25519", "/home/user/known_hosts"));

        // Wi-Fi has no enrollment flow at all.
        Assert.False(OfflineUpdateConnectionUiState.IsUsbKeyEnrollment(
            OfflineUpdateTransport.Wifi, OfflineUpdateCredentialMode.PublicKey, string.Empty, string.Empty));
    }

    [Fact]
    public void ExplicitKey_RequiresTheUserNameKeyAndKnownHostsTogether()
    {
        Assert.True(OfflineUpdateConnectionUiState.IsExplicitKeyIncomplete(
            OfflineUpdateTransport.Usb, OfflineUpdateCredentialMode.PublicKey, "armada", "/k", string.Empty));
        Assert.True(OfflineUpdateConnectionUiState.IsExplicitKeyIncomplete(
            OfflineUpdateTransport.Usb, OfflineUpdateCredentialMode.PublicKey, string.Empty, "/k", "/h"));
        Assert.True(OfflineUpdateConnectionUiState.IsExplicitKeyIncomplete(
            OfflineUpdateTransport.Wifi, OfflineUpdateCredentialMode.PublicKey, string.Empty, "/k", "/h"));
        Assert.True(OfflineUpdateConnectionUiState.IsExplicitKeyIncomplete(
            OfflineUpdateTransport.Wifi, OfflineUpdateCredentialMode.PublicKey, "armada", string.Empty, "/h"));

        Assert.False(OfflineUpdateConnectionUiState.IsExplicitKeyIncomplete(
            OfflineUpdateTransport.Usb, OfflineUpdateCredentialMode.PublicKey, "armada", "/k", "/h"));
        Assert.False(OfflineUpdateConnectionUiState.IsExplicitKeyIncomplete(
            OfflineUpdateTransport.Wifi, OfflineUpdateCredentialMode.PublicKey, "armada", "/k", "/h"));
        Assert.False(OfflineUpdateConnectionUiState.IsExplicitKeyIncomplete(
            OfflineUpdateTransport.Usb, OfflineUpdateCredentialMode.PublicKey, string.Empty, string.Empty, string.Empty));
    }

    [Fact]
    public void OfflineUpgradeView_HasOnePrimaryConnectActionAndKeepsDisconnect()
    {
        string xaml = ReadEmbedded("FreedomInstaller.Tests.OfflineUpgradeView.xaml");

        string connectLine = Assert.Single(xaml.Split('\n')
            .Where(line => line.Contains("x:Name=\"ConnectButton\"", StringComparison.Ordinal)));
        Assert.Contains("Style=\"{DynamicResource PrimaryButton}\"", connectLine, StringComparison.Ordinal);
        Assert.Contains("Content=\"{DynamicResource Static.OfflineUpdate.ConnectTablet}\"", connectLine, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "Static.OfflineUpdate.ConnectTablet"));
        Assert.DoesNotContain("UsbConnectButton", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "x:Name=\"DisconnectButton\""));

        string codeBehind = ReadEmbedded("FreedomInstaller.Tests.MainWindow.OfflineUpgrade.cs");
        Assert.Equal(1, CountOccurrences(codeBehind, "ConnectButton.Click +="));
        Assert.Contains("ConnectButton.Click += OnConnectOfflineTabletAsync;", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("UsbConnectButton", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("OnConnectOfflineSshAsync", codeBehind, StringComparison.Ordinal);
    }

    private static string ReadEmbedded(string name)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
