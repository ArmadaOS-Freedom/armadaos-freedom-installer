using System.Globalization;

namespace FreedomInstaller.App
{
    /// <summary>Connection route chosen explicitly in the offline-upgrade panel.</summary>
    internal enum OfflineUpdateTransport
    {
        Usb = 0,
        Wifi = 1,
    }

    /// <summary>Credential source chosen explicitly in the offline-upgrade panel.</summary>
    internal enum OfflineUpdateCredentialMode
    {
        DefaultAccount = 0,
        CustomAccount = 1,
        PublicKey = 2,
    }

    /// <summary>
    /// Which input controls belong to a transport/auth combination. The window
    /// maps these flags to WPF visibility; keeping the decision here makes the
    /// wiring testable without constructing a window.
    /// </summary>
    internal readonly record struct OfflineUpdateConnectionFields(
        bool ShowWifiAddress,
        bool ShowDefaultAccount,
        bool ShowManualUserName,
        bool ShowCustomPassword,
        bool ShowKeyFields);

    /// <summary>
    /// Pure policy for the connection panel. The route and the credential
    /// source are always the two explicit selections: nothing is inferred from
    /// a blank address or from leftover values in hidden fields.
    /// </summary>
    internal static class OfflineUpdateConnectionUiState
    {
        internal const int UsbTransportIndex = 0;
        internal const int WifiTransportIndex = 1;
        internal const int DefaultCredentialIndex = 0;
        internal const int CustomCredentialIndex = 1;
        internal const int PublicKeyCredentialIndex = 2;

        /// <summary>The USB port is fixed by the device gadget; only Wi-Fi has an editable port.</summary>
        internal const int UsbSshPort = 22;

        internal static OfflineUpdateTransport TransportFromIndex(int index) =>
            index == WifiTransportIndex ? OfflineUpdateTransport.Wifi : OfflineUpdateTransport.Usb;

        internal static OfflineUpdateCredentialMode CredentialFromIndex(int index) => index switch
        {
            CustomCredentialIndex => OfflineUpdateCredentialMode.CustomAccount,
            PublicKeyCredentialIndex => OfflineUpdateCredentialMode.PublicKey,
            _ => OfflineUpdateCredentialMode.DefaultAccount,
        };

        internal static OfflineUpdateConnectionFields Fields(
            OfflineUpdateTransport transport,
            OfflineUpdateCredentialMode credential) => new(
                ShowWifiAddress: transport == OfflineUpdateTransport.Wifi,
                ShowDefaultAccount: credential == OfflineUpdateCredentialMode.DefaultAccount,
                ShowManualUserName: credential != OfflineUpdateCredentialMode.DefaultAccount,
                ShowCustomPassword: credential == OfflineUpdateCredentialMode.CustomAccount,
                ShowKeyFields: credential == OfflineUpdateCredentialMode.PublicKey);

        /// <summary>The Wi-Fi route needs its own address; a blank one never falls back to USB.</summary>
        internal static bool UsesWifiAddress(OfflineUpdateTransport transport) =>
            transport == OfflineUpdateTransport.Wifi;

        /// <summary>The USB route always discovers the exact endpoint and binds its local interface.</summary>
        internal static bool UsesUsbDiscovery(OfflineUpdateTransport transport) =>
            transport == OfflineUpdateTransport.Usb;

        internal static bool IsWifiAddressMissing(OfflineUpdateTransport transport, string? address) =>
            UsesWifiAddress(transport) && string.IsNullOrWhiteSpace(address);

        internal static bool TryParsePort(string? text, out int port) =>
            int.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
            port is >= 1 and <= 65535;

        internal static bool IsCustomCredentialIncomplete(string? userName, string? password) =>
            string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password);

        /// <summary>
        /// Leaving both key files empty on USB is the existing one-time key
        /// enrollment flow. Supplying either one makes it an explicit existing
        /// key connection, so a partial entry is never treated as enrollment.
        /// </summary>
        internal static bool IsUsbKeyEnrollment(
            OfflineUpdateTransport transport,
            OfflineUpdateCredentialMode credential,
            string? keyPath,
            string? knownHosts) =>
            transport == OfflineUpdateTransport.Usb &&
            credential == OfflineUpdateCredentialMode.PublicKey &&
            string.IsNullOrWhiteSpace(keyPath) &&
            string.IsNullOrWhiteSpace(knownHosts);

        internal static bool IsExplicitKeyIncomplete(
            OfflineUpdateTransport transport,
            OfflineUpdateCredentialMode credential,
            string? userName,
            string? keyPath,
            string? knownHosts)
        {
            if (credential != OfflineUpdateCredentialMode.PublicKey ||
                IsUsbKeyEnrollment(transport, credential, keyPath, knownHosts))
                return false;

            return string.IsNullOrWhiteSpace(userName) ||
                   string.IsNullOrWhiteSpace(keyPath) ||
                   string.IsNullOrWhiteSpace(knownHosts);
        }

        /// <summary>
        /// Resolves the password-mode credential from the explicit selection.
        /// The factory pair is used only when the default account is selected;
        /// values left in the hidden custom fields are ignored in that case.
        /// </summary>
        internal static bool TryResolvePasswordCredential(
            OfflineUpdateCredentialMode credential,
            string? enteredUserName,
            string? enteredPassword,
            string defaultUserName,
            string defaultPassword,
            out string userName,
            out string password)
        {
            userName = string.Empty;
            password = string.Empty;

            switch (credential)
            {
                case OfflineUpdateCredentialMode.DefaultAccount:
                    userName = defaultUserName;
                    password = defaultPassword;
                    return true;

                case OfflineUpdateCredentialMode.CustomAccount:
                    if (IsCustomCredentialIncomplete(enteredUserName, enteredPassword))
                        return false;
                    userName = enteredUserName!.Trim();
                    password = enteredPassword!;
                    return true;

                default:
                    return false;
            }
        }
    }
}
