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

    [Fact]
    public void OfflineUpgradeView_BindsVisibleLabelsToTheConnectionControls()
    {
        string xaml = ReadEmbedded("FreedomInstaller.Tests.OfflineUpgradeView.xaml");

        AssertLabeledBy(xaml, "TransportLabel", "TransportCombo");
        AssertLabeledBy(xaml, "CredentialLabel", "CredentialCombo");
        AssertLabeledBy(xaml, "WifiAddressLabel", "HostBox");
        AssertLabeledBy(xaml, "PortLabel", "PortBox");
        AssertLabeledBy(xaml, "UserLabel", "UserBox");
        AssertLabeledBy(xaml, "PasswordLabel", "PasswordBox");
        AssertLabeledBy(xaml, "KeyPathLabel", "KeyPathBox");
        AssertLabeledBy(xaml, "KnownHostsLabel", "KnownHostsBox");
    }

    [Fact]
    public void Connect_KeysTheRejectedDefaultAttemptByTheValidatedUsbInstance()
    {
        string codeBehind = ReadEmbedded("FreedomInstaller.Tests.MainWindow.OfflineUpgrade.cs");

        // The discovery result, not the shared USB host, identifies the device.
        Assert.Contains("usbDeviceInstanceId = endpoint.DeviceInstanceId;", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey(host, port, usbDeviceInstanceId)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OfflineUpdateSshAuthPolicy.EndpointKey(host, port)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void BothConnectRoutes_GoThroughTheOnePreflightSessionFinishHook()
    {
        string code = ReadEmbedded("FreedomInstaller.Tests.MainWindow.OfflinePairing.cs");

        int keyRoute = code.IndexOf("private async Task ConnectOfflineOptionsAsync", StringComparison.Ordinal);
        int passwordRoute = code.IndexOf("private async Task ConnectOfflinePasswordAsync", StringComparison.Ordinal);
        int hook = code.IndexOf("private async Task FinishOfflineConnectionAsync", StringComparison.Ordinal);
        Assert.True(keyRoute >= 0 && passwordRoute > keyRoute && hook > passwordRoute);

        // Both routes end at the same hook instead of each deciding for itself.
        Assert.Contains("await FinishOfflineConnectionAsync(conn, ct);",
            code[keyRoute..passwordRoute], StringComparison.Ordinal);
        Assert.Contains("await FinishOfflineConnectionAsync(conn, ct);",
            code[passwordRoute..hook], StringComparison.Ordinal);
        Assert.DoesNotContain("SupportsPreflightSession", code[..hook], StringComparison.Ordinal);

        // The hook refuses an incapable helper before adopting it, so the
        // transport is disposed exactly once on that path.
        string body = code[hook..];
        int capability = body.IndexOf("SupportsPreflightSession(conn.DeviceInfo)", StringComparison.Ordinal);
        int dispose = body.IndexOf("await conn.DisposeAsync();", StringComparison.Ordinal);
        int adopt = body.IndexOf("_offlineSsh = conn;", StringComparison.Ordinal);
        Assert.True(capability >= 0 && dispose > capability && adopt > dispose);
        Assert.Equal(1, CountOccurrences(body, "OfflineUpdateUiState.SupportsPreflightSession(conn.DeviceInfo)"));

        // The boot policy remains a separate gate: such a device still
        // connects with the supported read-only status.
        Assert.Contains("StateBootPolicyReadOnly", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryExport_IsReadOnlyAndOnlyRealPrepareRequiresTheWriteBootPolicy()
    {
        string code = ReadEmbedded("FreedomInstaller.Tests.MainWindow.OfflineUpgrade.cs");

        // The export button uses the gate that has no boot-policy input.
        Assert.Contains("OfflineUpdateUiState.CanExportRecoverySession(", code, StringComparison.Ordinal);
        // The Plan handler refuses an unsupported helper only for the real
        // prepare, never for the export-only branch.
        Assert.Contains("if (!exportRecoveryOnly && !_offlineBootPolicySupported)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("bool canExportRecovery = !busy && bootPolicySupported", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementSession_IsASeparateConfirmedActionGatedOnAReadableStore()
    {
        string xaml = ReadEmbedded("FreedomInstaller.Tests.OfflineUpgradeView.xaml");
        string buttonLine = Assert.Single(xaml.Split('\n')
            .Where(line => line.Contains("x:Name=\"StartReplacementSessionButton\"", StringComparison.Ordinal)));
        // Its own label, disabled and hidden: it is never the Prepare action and
        // it never appears without the exact failed-preparation receipt.
        Assert.Contains("Content=\"{DynamicResource Static.OfflineUpdate.StartReplacementSession}\"",
            buttonLine, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", buttonLine, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"False\"", buttonLine, StringComparison.Ordinal);

        string code = ReadEmbedded("FreedomInstaller.Tests.MainWindow.OfflineUpgrade.cs");

        // The action runs only from the explicit click and is wired once.
        Assert.Equal(1, CountOccurrences(code, "StartReplacementSessionButton.Click +="));
        Assert.Contains("StartReplacementSessionButton.Click += OnStartReplacementOfflineSessionAsync;",
            code, StringComparison.Ordinal);

        // Both new-session begins refuse the mutation itself while the saved
        // records cannot be read, and the control gates carry the same reason.
        Assert.Equal(2, CountOccurrences(code, "EnsureOfflineCatalogAvailable();"));
        Assert.Contains("bool catalogAvailable = _offlineSavedSessionsUnavailableReason == null;",
            code, StringComparison.Ordinal);
        Assert.Contains("bool canPrepare = !busy && bootPolicySupported && catalogAvailable &&",
            code, StringComparison.Ordinal);
        Assert.Contains("catalogAvailable, _offlineReceiptLive, _offlineSelectedSession != null, failedPrepareSelected",
            code, StringComparison.Ordinal);
    }

    private static void AssertLabeledBy(string xaml, string labelName, string controlName)
    {
        string line = Assert.Single(xaml.Split('\n')
            .Where(candidate => candidate.Contains($"x:Name=\"{controlName}\"", StringComparison.Ordinal)));
        Assert.Contains(
            $"AutomationProperties.LabeledBy=\"{{Binding ElementName={labelName}}}\"",
            line,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, $"x:Name=\"{labelName}\""));
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
