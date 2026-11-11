using System;
using System.Globalization;
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

            bool keyMode = SelectedOfflineAuthMode == OfflineUpdateSshAuthMode.PublicKey;
            await RunOfflineUpgradeAsync(async (_, ct) =>
            {
                ClearOfflinePairing();
                var candidate = await OfflineUpdateSshEnrollment.DiscoverUsbHostAsync(_runner, token: ct);

                if (keyMode)
                {
                    // Existing one-time public-key pairing stays available unchanged.
                    var identity = await OfflineUpdateSshIdentityStore.EnsureAsync(
                        Path.Combine(BundledTools.GetCacheRoot(), "connections", "offline-upgrade"), _runner, token: ct);
                    var keyOptions = OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate, identity);
                    if (keyOptions == null)
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

                    await ConnectOfflineOptionsAsync(keyOptions, ct);
                    return;
                }

                var defaultCredential = OfflineUpdateSshDefaultCredentials.Instance;
                string enteredUser = OfflineUpgradePanel.UserBox.Text.Trim();
                string enteredPassword = OfflineUpgradePanel.PasswordBox.Password;
                bool manualCredentials = OfflineUpdateSshAuthPolicy.HasManualCredentialInput(enteredUser, enteredPassword);

                if (enteredPassword.Length > 0 && string.IsNullOrEmpty(enteredUser))
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorInvalidConnectionFields"));

                string endpointKey = OfflineUpdateSshAuthPolicy.EndpointKey(candidate.Endpoint.Host, 22);

                if (manualCredentials && enteredPassword.Length == 0)
                {
                    // The user named an account. Never replace it with the
                    // factory account behind their back; ask for the password.
                    ShowOfflinePasswordEntry("Dynamic.OfflineUpdate.ErrorPasswordRequired", setDefaultUserName: false);
                    return;
                }

                bool usingDefault = !manualCredentials &&
                    OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
                        OfflineUpdateSshAuthMode.Password,
                        credentialsSuppliedByUser: false,
                        defaultAlreadyAttempted: _offlineDefaultCredentialRejected.Contains(endpointKey));

                if (!manualCredentials && !usingDefault)
                {
                    ShowOfflinePasswordEntry("Dynamic.OfflineUpdate.DefaultCredentialRejected", setDefaultUserName: true);
                    return;
                }

                var options = new OfflineUpdateSshPasswordOptions(
                    candidate.Endpoint.Host,
                    22,
                    usingDefault ? defaultCredential.UserName : enteredUser,
                    usingDefault ? defaultCredential.Password : enteredPassword,
                    GetOfflineHostTrustDirectory(),
                    candidate.Endpoint.LocalAddress)
                {
                    ApproveHostKey = ApproveOfflineSshHostKeyAsync,
                };

                // Only the user-entered secret is cleared here; the public factory
                // credential is product configuration, not a stored user secret.
                if (!usingDefault)
                    OfflineUpgradePanel.PasswordBox.Clear();

                try
                {
                    await ConnectOfflinePasswordAsync(options, ct);
                }
                catch (InvalidOperationException ex) when (usingDefault && IsOfflinePasswordRejection(ex))
                {
                    // Only a real authentication rejection consumes the single
                    // automatic attempt for this endpoint.
                    _offlineDefaultCredentialRejected.Add(endpointKey);
                    ShowOfflinePasswordEntry("Dynamic.OfflineUpdate.DefaultCredentialRejected", setDefaultUserName: true);
                }
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
            await FinishOfflineConnectionAsync(conn, ct);
        }

        private async Task ConnectOfflinePasswordAsync(OfflineUpdateSshPasswordOptions options, CancellationToken ct)
        {
            AppendOfflineLog("Dynamic.OfflineUpdate.LogConnecting", options.Host, options.Port);

            // The bootstrap assistant is build-supplied. When it is present,
            // run it after SSH trust/authentication and before the updater.info
            // query; it only probes/installs the service and never prepares or
            // executes an upgrade.
            if (OfflineBootstrapAssets.TryCreate(
                    new Progress<OfflineUpdateBootstrapProgress>(p =>
                    {
                        if (_closed || OfflineUpgradePanel == null)
                            return;
                        string key = p.Phase switch
                        {
                            OfflineUpdateBootstrapCoordinator.ProbingPhase => "Dynamic.OfflineUpdate.BootstrapProbing",
                            OfflineUpdateBootstrapCoordinator.DeployingPhase => "Dynamic.OfflineUpdate.BootstrapDeploying",
                            OfflineUpdateBootstrapCoordinator.VerifyingPhase => "Dynamic.OfflineUpdate.BootstrapVerifying",
                            _ => "Dynamic.OfflineUpdate.BootstrapReady",
                        };
                        if (p.Phase != OfflineUpdateBootstrapCoordinator.ReadyPhase)
                            SetText(OfflineUpgradePanel.StateText, key);
                        AppendOfflineLog(key);
                    }),
                    useSudoForInstall: !string.Equals(options.UserName, "root", StringComparison.Ordinal))
                is { } bootstrap)
            {
                options.PrepareSessionAsync = (session, token) => bootstrap.EnsureAsync(session, token);
            }

            IOfflineUpdateConnection conn;
            try
            {
                conn = await OfflineUpdateSshManagedConnection.ConnectAsync(options, null, ct);
            }
            catch (OfflineUpdateSshManagedException ex)
            {
                throw new InvalidOperationException(T(OfflineManagedFailureKey(ex.Reason), ex.Message), ex);
            }
            finally
            {
                // Authenticated or not, this attempt no longer needs the secret.
                options.ClearPassword();
            }

            await FinishOfflineConnectionAsync(conn, ct);
        }

        private async Task FinishOfflineConnectionAsync(IOfflineUpdateConnection conn, CancellationToken ct)
        {
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
                bool legacyController = OfflineUpdateUiState.IsLegacyController(conn.DeviceInfo);
                // selectedSlot is never presented as a proven actual slot.
                _offlineActualSlot = legacyController ? null : GetJsonString(conn.DeviceInfo, "actualSlot");
                _offlineRecoveryPartUuid = GetJsonString(conn.DeviceInfo, "recoveryPartUuid") ?? string.Empty;
                ClearOfflinePairing();
                if (legacyController)
                {
                    SetText(OfflineUpgradePanel.DeviceText, "Dynamic.OfflineUpdate.LegacyDeviceConnected", LocalizeModelName(_offlineConnectedModel));
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogLegacyConnected", LocalizeModelName(_offlineConnectedModel));
                }
                else
                {
                    string role = _offlineActualSlot ?? "?";
                    SetText(OfflineUpgradePanel.DeviceText, "Dynamic.OfflineUpdate.DeviceConnected", LocalizeModelName(_offlineConnectedModel), role);
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogConnected", LocalizeModelName(_offlineConnectedModel), role);
                }
                SetText(OfflineUpgradePanel.DetailsText, "Dynamic.OfflineUpdate.Value", FormatJson(conn.DeviceInfo));
                SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateConnected");
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

        /// <summary>
        /// Called by the managed transport during key exchange, on a background
        /// thread, before any password is sent. The answer is a plain explicit
        /// user decision; a changed key is approved only by answering again.
        /// </summary>
        private Task<bool> ApproveOfflineSshHostKeyAsync(OfflineUpdateSshHostKeyPrompt prompt, CancellationToken token)
        {
            if (_closed || token.IsCancellationRequested)
                return Task.FromResult(false);

            return Dispatcher.InvokeAsync(() =>
            {
                if (_closed || token.IsCancellationRequested)
                    return false;

                string key = prompt.Status == OfflineUpdateSshHostTrustStatus.Changed
                    ? "Dynamic.OfflineUpdate.HostKeyChangedPrompt"
                    : "Dynamic.OfflineUpdate.HostKeyNewPrompt";

                string message = T(
                    key,
                    prompt.Host,
                    prompt.Port.ToString(CultureInfo.InvariantCulture),
                    prompt.Fingerprint,
                    prompt.StoredFingerprint ?? string.Empty);

                return MessageBox.Show(
                    this,
                    message,
                    T("Static.OfflineUpdate.Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) == MessageBoxResult.Yes;
            }).Task;
        }

        /// <summary>
        /// Clears only the secret field and asks the user to enter it. The
        /// factory account name is shown only when the automatic attempt was
        /// the one that failed; a name the user typed is never overwritten.
        /// </summary>
        private void ShowOfflinePasswordEntry(string messageKey, bool setDefaultUserName)
        {
            if (OfflineUpgradePanel == null)
                return;

            OfflineUpgradePanel.PasswordBox.Clear();
            if (setDefaultUserName && string.IsNullOrEmpty(OfflineUpgradePanel.UserBox.Text))
                OfflineUpgradePanel.UserBox.Text = OfflineUpdateSshDefaultCredentials.Instance.UserName;
            OfflineUpgradePanel.PasswordBox.Focus();
            SetText(OfflineUpgradePanel.StateText, messageKey);
            AppendOfflineLog(messageKey);
            RefreshOfflineUpgradeActions();
        }

        private static bool IsOfflinePasswordRejection(InvalidOperationException exception) =>
            exception.InnerException is OfflineUpdateSshManagedException
            {
                Reason: OfflineUpdateSshManagedFailure.Authentication
            };

        private static string OfflineManagedFailureKey(OfflineUpdateSshManagedFailure reason) => reason switch
        {
            OfflineUpdateSshManagedFailure.Authentication => "Dynamic.OfflineUpdate.ErrorPasswordAuthRejected",
            OfflineUpdateSshManagedFailure.HostKeyRejected => "Dynamic.OfflineUpdate.ErrorHostKeyRejected",
            OfflineUpdateSshManagedFailure.SudoUnavailable => "Dynamic.OfflineUpdate.ErrorSudoUnavailable",
            OfflineUpdateSshManagedFailure.UpdaterUnavailable => "Dynamic.OfflineUpdate.ErrorUpdateComponentsMissing",
            OfflineUpdateSshManagedFailure.Bootstrap => "Dynamic.OfflineUpdate.ErrorBootstrapFailed",
            OfflineUpdateSshManagedFailure.Timeout => "Dynamic.OfflineUpdate.ErrorConnectTimeout",
            _ => "Dynamic.OfflineUpdate.ErrorConnectFailed",
        };
    }
}
