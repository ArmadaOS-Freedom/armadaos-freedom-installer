using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FreedomInstaller.Core;

namespace FreedomInstaller.App
{
    public partial class MainWindow
    {
        private OfflineUpdateSshIdentity? _offlinePairingIdentity;
        private OfflineUpdateSshHostCandidate? _offlinePairingCandidate;

        private void ClearOfflinePairing()
        {
            _offlinePairingIdentity = null;
            _offlinePairingCandidate = null;
            if (OfflineUpgradePanel == null)
                return;
            OfflineUpgradePanel.PairingPanel.Visibility = Visibility.Collapsed;
            OfflineUpgradePanel.PairingCommandBox.Clear();
            OfflineUpgradePanel.PairingReceiptBox.Clear();
            SetText(OfflineUpgradePanel.PairingHostText, "Dynamic.OfflineUpdate.Value", "");
        }

        private void OnOfflinePairingReceiptChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            RefreshOfflineUpgradeActions();

        private async void OnConnectOfflineUsbAsync(object sender, RoutedEventArgs e)
        {
            if (_busy || _offlineSsh != null)
                return;
            await RunOfflineUpgradeAsync(async (_, ct) =>
            {
                ClearOfflinePairing();
                var candidate = await OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(_runner, token: ct);
                var identity = await OfflineUpdateSshIdentityStore.EnsureAsync(
                    Path.Combine(BundledTools.GetCacheRoot(), "connections", "offline-upgrade"), _runner, token: ct);
                var options = OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate, identity);
                if (options == null)
                {
                    _offlinePairingCandidate = candidate;
                    _offlinePairingIdentity = identity;
                    OfflineUpgradePanel.PairingPanel.Visibility = Visibility.Visible;
                    OfflineUpgradePanel.PairingCommandBox.Text =
                        OfflineUpdateSshEnrollment.BuildAuthorizationCommand(identity.PublicKey.PublicKey);
                    SetText(OfflineUpgradePanel.PairingHostText, "Dynamic.OfflineUpdate.PairingFingerprints",
                        candidate.HostKey.Fingerprint, identity.PublicKey.Fingerprint);
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.PairingNeeded");
                    AppendOfflineLog("Dynamic.OfflineUpdate.PairingNeeded");
                    RefreshOfflineUpgradeActions();
                    return;
                }
                await ConnectOfflineOptionsAsync(options, ct);
            });
        }

        private async void OnConfirmOfflinePairingAsync(object sender, RoutedEventArgs e)
        {
            if (_busy || _offlineSsh != null || _offlinePairingCandidate == null || _offlinePairingIdentity == null)
                return;
            string receipt = OfflineUpgradePanel.PairingReceiptBox.Text;
            if (string.IsNullOrWhiteSpace(receipt))
                return;
            var capturedCandidate = _offlinePairingCandidate;
            var capturedIdentity = _offlinePairingIdentity;
            await RunOfflineUpgradeAsync(async (_, ct) =>
            {
                var candidate = await OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(_runner, token: ct);
                var identity = await OfflineUpdateSshIdentityStore.EnsureAsync(
                    Path.Combine(BundledTools.GetCacheRoot(), "connections", "offline-upgrade"), _runner, token: ct);
                if (candidate.HostKey.PublicKey != capturedCandidate.HostKey.PublicKey ||
                    identity.PublicKey.PublicKey != capturedIdentity.PublicKey.PublicKey)
                {
                    ClearOfflinePairing();
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.PairingChanged"));
                }
                ct.ThrowIfCancellationRequested();
                var options = OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, receipt);
                await ConnectOfflineOptionsAsync(options, ct);
            });
        }

        private void OnCopyOfflinePairingCommand(object sender, RoutedEventArgs e)
        {
            if (_offlinePairingCandidate == null)
                return;
            try
            {
                Clipboard.SetText(OfflineUpgradePanel.PairingCommandBox.Text);
                AppendOfflineLog("Dynamic.OfflineUpdate.PairingCopied");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, T("Dynamic.OfflineUpdate.PairingCopyFailed", ex.Message),
                    T("Static.OfflineUpdate.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ConnectOfflineOptionsAsync(OfflineUpdateSshOptions opts, CancellationToken ct)
        {
            AppendOfflineLog("Dynamic.OfflineUpdate.LogConnecting", opts.Host, opts.Port);
            var conn = await OfflineUpdateSshConnection.ConnectAsync(opts, null, ct);
            try
            {
                string model = GetJsonString(conn.DeviceInfo, "model") ?? string.Empty;
                if (!string.Equals(model, "TB322FC", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(model, "TB323FU", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorUnsupportedModel", LocalizeModelName(model)));
                }
                _offlineSsh = conn;
                _offlineTransfer = new OfflineUpdateTransfer(conn.Rpc);
                _offlineConnectedModel = model.ToUpperInvariant();
                _offlineActualSlot = GetJsonString(conn.DeviceInfo, "actualSlot") ?? "?";
                _offlineRecoveryPartUuid = GetJsonString(conn.DeviceInfo, "recoveryPartUuid") ?? string.Empty;
                ClearOfflinePairing();
                SetText(OfflineUpgradePanel.DeviceText, "Dynamic.OfflineUpdate.DeviceConnected", LocalizeModelName(_offlineConnectedModel), _offlineActualSlot);
                SetText(OfflineUpgradePanel.DetailsText, "Dynamic.OfflineUpdate.Value", FormatJson(conn.DeviceInfo));
                SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateConnected");
                AppendOfflineLog("Dynamic.OfflineUpdate.LogConnected", LocalizeModelName(_offlineConnectedModel), _offlineActualSlot);
                // Sessions saved by an earlier run are bound to this device and
                // can be queried read-only without importing the package again.
                LoadSavedOfflineSessionsForDevice();
            }
            catch
            {
                if (_offlineSsh == null)
                    await conn.DisposeAsync();
                throw;
            }
        }
    }
}
