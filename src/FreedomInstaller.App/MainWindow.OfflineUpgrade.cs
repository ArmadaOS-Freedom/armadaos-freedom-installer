using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FreedomInstaller.Core;

namespace FreedomInstaller.App
{
    public partial class MainWindow
    {
        private bool _showingOfflineUpgrade;
        private bool _offlineUpgradeInitialized;
        private bool _offlineUpgradeActive;
        private bool _offlineUpgradeCloseApproved;
        private bool _offlineUpgradeCloseInProgress;
        private bool _suppressOfflineSessionSelectionChanged;

        // Targets whose one automatic factory-credential attempt was actually
        // rejected: a Wi-Fi host/port, or a USB device instance on its
        // endpoint. Only a real authentication rejection is recorded, so a
        // cancelled host-key prompt or a network failure can retry.
        private readonly HashSet<string> _offlineDefaultCredentialRejected =
            new(StringComparer.OrdinalIgnoreCase);
        private IOfflineUpdateConnection? _offlineSsh;
        private OfflineUpdateTransfer? _offlineTransfer;
        private ImportedUpdatePackage? _offlinePackage;
        private string? _offlineDescriptorSha256;
        private string? _offlineConnectedModel;
        private string? _offlineActualSlot;
        private string? _offlineRecoveryPartUuid;
        private IReadOnlyList<OfflineUpdateSession> _offlineSessions = Array.Empty<OfflineUpdateSession>();
        // Records that exist for the connected device but cannot be read. They
        // are reported and skipped; they never remove a valid session or an
        // imported package, and they are never rewritten.
        private IReadOnlyList<OfflineDamagedSessionRecord> _offlineDamagedSessionRecords =
            Array.Empty<OfflineDamagedSessionRecord>();
        private OfflineUpdateSession? _offlineSelectedSession;
        private SavedOfflineUpdateRecovery? _offlineSavedRecovery;
        private JsonElement? _offlineLastReceipt;
        private bool _offlineReceiptLive;
        private string? _offlineLastLoggedPhase;
        private string? _offlineLastCompletionState;
        // The read-only preflight is bound to this exact package and this exact
        // tablet connection. It is recorded only after the device returns the
        // exact success schema and is cleared by every package/device change.
        private bool _offlinePreflightPassed;
        // The mutating upgrade contract recorded from the authenticated info:
        // both physical A/B candidates boot the same Linux deployment and the
        // helper never controls Android's active/selected/successful slot.
        private bool _offlineBootPolicySupported;
        private string? _offlinePreflightError;
        private long _offlineOperationGeneration;
        // Set when the panel itself moved the user to the custom account, so the
        // password field receives focus once the busy state releases it.
        private bool _offlineFocusPasswordOnIdle;
        private readonly List<UiMessage> _offlineLogEntries = new();

        private void InitializeOfflineUpgradeView()
        {
            if (OfflineUpgradePanel == null || _offlineUpgradeInitialized)
                return;

            _offlineUpgradeInitialized = true;
            OfflineUpgradePanel.SessionCombo.DisplayMemberPath = nameof(OfflineUpdateSession.SessionId);
            OfflineUpgradePanel.BackButton.Click += OnOfflineUpgradeBackAsync;
            OfflineUpgradePanel.TransportCombo.SelectionChanged += OnOfflineConnectionSelectionChanged;
            OfflineUpgradePanel.CredentialCombo.SelectionChanged += OnOfflineConnectionSelectionChanged;
            OfflineUpgradePanel.BrowseKeyButton.Click += OnBrowseOfflineKey;
            OfflineUpgradePanel.BrowseHostsButton.Click += OnBrowseOfflineKnownHosts;
            OfflineUpgradePanel.ConnectButton.Click += OnConnectOfflineTabletAsync;
            OfflineUpgradePanel.CopyPairingCommandButton.Click += OnCopyOfflinePairingCommand;
            OfflineUpgradePanel.ConfirmPairingButton.Click += OnConfirmOfflinePairingAsync;
            OfflineUpgradePanel.PairingReceiptBox.TextChanged += OnOfflinePairingReceiptChanged;
            OfflineUpgradePanel.DisconnectButton.Click += OnDisconnectOfflineSshAsync;
            OfflineUpgradePanel.BrowsePackageButton.Click += OnBrowseOfflinePackage;
            OfflineUpgradePanel.ValidatePackageButton.Click += OnValidateOfflinePackageAsync;
            OfflineUpgradePanel.SessionCombo.SelectionChanged += OnOfflineSessionSelectionChanged;
            OfflineUpgradePanel.RefreshButton.Click += OnRefreshOfflineStatusAsync;
            OfflineUpgradePanel.PrepareButton.Click += OnPrepareOfflineUpgradeAsync;
            OfflineUpgradePanel.ConfirmCheck.Checked += OnOfflineConfirmChanged;
            OfflineUpgradePanel.ConfirmCheck.Unchecked += OnOfflineConfirmChanged;
            OfflineUpgradePanel.ExecuteButton.Click += OnExecuteOfflineUpgradeAsync;
            OfflineUpgradePanel.StopWaitingButton.Click += OnStopWaitingOfflineUpgrade;
            OfflineUpgradePanel.RestartToVerifyButton.Click += OnRestartToVerifyOfflineUpgradeAsync;
            RefreshOfflineUpgradeActions();
        }

        private void OnShowOfflineUpgrade(object? sender, RoutedEventArgs e)
        {
            if (!_ready || !EnvironmentIsReady || _busy || !_showingHome || HasOfflineUpgradeProtectedState())
                return;

            _showingHome = false;
            _showingOfflineUpgrade = true;
            ApplyFunctionView();
            RefreshActions();
        }

        private bool HasOfflineUpgradeProtectedState() =>
            _showingEnvironment || _showingSupport || _showingGlobalRecovery ||
            HasGlobalRecoveryProtectedState || HasPendingTfTabletRecovery || HasProtectedUninstallState ||
            ProtectedBootMaintenanceState || ProtectedModernTfInstallState || HasInternalInstallLiveEdlState ||
            (HasInternalInstallExecutionStarted && !IsCompletedInternalInstallExecution);

        private void RefreshOfflineUpgradeActions()
        {
            if (OfflineUpgradePanel == null)
                return;

            bool connected = _offlineSsh != null && !string.IsNullOrEmpty(_offlineConnectedModel);
            bool imported = _offlinePackage != null && !string.IsNullOrEmpty(_offlineDescriptorSha256);
            // Closing or an older helper still gets read-only info/status; only
            // the mutating prepare/execute/restart actions need the policy.
            bool bootPolicySupported = connected && _offlineBootPolicySupported;
            // 关闭等待期间同样按忙碌处理：按钮全部禁用，直到清理完成。
            bool busy = _busy || _offlineUpgradeCloseInProgress;
            bool preparedReady = bootPolicySupported &&
                OfflineUpdateUiState.CanExecute(_offlineLastReceipt, _offlineSavedRecovery != null, _offlinePreflightPassed);

            OfflineUpgradePanel.BackButton.IsEnabled = !busy;
            var transport = SelectedOfflineTransport;
            var credential = SelectedOfflineCredential;
            var fields = OfflineUpdateConnectionUiState.Fields(transport, credential);
            bool fieldsEnabled = !busy && !connected;
            OfflineUpgradePanel.TransportCombo.IsEnabled = fieldsEnabled;
            OfflineUpgradePanel.CredentialCombo.IsEnabled = fieldsEnabled;
            OfflineUpgradePanel.UsbAddressHint.Visibility = fields.ShowWifiAddress ? Visibility.Collapsed : Visibility.Visible;
            OfflineUpgradePanel.WifiAddressPanel.Visibility = fields.ShowWifiAddress ? Visibility.Visible : Visibility.Collapsed;
            OfflineUpgradePanel.HostBox.IsEnabled = fieldsEnabled && fields.ShowWifiAddress;
            OfflineUpgradePanel.PortBox.IsEnabled = fieldsEnabled && fields.ShowWifiAddress;
            OfflineUpgradePanel.DefaultAccountText.Visibility = fields.ShowDefaultAccount ? Visibility.Visible : Visibility.Collapsed;
            OfflineUpgradePanel.ManualUserNamePanel.Visibility = fields.ShowManualUserName ? Visibility.Visible : Visibility.Collapsed;
            OfflineUpgradePanel.UserBox.IsEnabled = fieldsEnabled && fields.ShowManualUserName;
            OfflineUpgradePanel.CustomPasswordPanel.Visibility = fields.ShowCustomPassword ? Visibility.Visible : Visibility.Collapsed;
            OfflineUpgradePanel.PasswordBox.IsEnabled = fieldsEnabled && fields.ShowCustomPassword;
            OfflineUpgradePanel.KeyFieldsPanel.Visibility = fields.ShowKeyFields ? Visibility.Visible : Visibility.Collapsed;
            OfflineUpgradePanel.UsbKeyEnrollmentHint.Visibility = fields.ShowKeyFields && transport == OfflineUpdateTransport.Usb
                ? Visibility.Visible : Visibility.Collapsed;
            OfflineUpgradePanel.WifiKeyEnrollmentHint.Visibility = fields.ShowKeyFields && transport == OfflineUpdateTransport.Wifi
                ? Visibility.Visible : Visibility.Collapsed;
            OfflineUpgradePanel.KeyPathBox.IsEnabled = fieldsEnabled && fields.ShowKeyFields;
            OfflineUpgradePanel.KnownHostsBox.IsEnabled = fieldsEnabled && fields.ShowKeyFields;
            OfflineUpgradePanel.BrowseKeyButton.IsEnabled = fieldsEnabled && fields.ShowKeyFields;
            OfflineUpgradePanel.BrowseHostsButton.IsEnabled = fieldsEnabled && fields.ShowKeyFields;
            OfflineUpgradePanel.ConnectButton.IsEnabled = fieldsEnabled;
            bool pairingPending = _offlinePairingCandidate != null && _offlinePairingIdentity != null;
            OfflineUpgradePanel.CopyPairingCommandButton.IsEnabled = !busy && !connected && pairingPending;
            OfflineUpgradePanel.ConfirmPairingButton.IsEnabled = !busy && !connected && pairingPending && !string.IsNullOrWhiteSpace(OfflineUpgradePanel.PairingReceiptBox.Text);
            OfflineUpgradePanel.PairingReceiptBox.IsReadOnly = busy || connected || !pairingPending;
            OfflineUpgradePanel.DisconnectButton.IsEnabled = !busy && connected;

            OfflineUpgradePanel.BrowsePackageButton.IsEnabled = !busy && connected;
            OfflineUpgradePanel.ValidatePackageButton.IsEnabled = !busy && connected && !string.IsNullOrWhiteSpace(OfflineUpgradePanel.PackagePathBox.Text);
            // Saved sessions come from the device-bound records, so they can be
            // listed and queried without importing the package. Preparing,
            // executing and saving recovery still require a validated package;
            // preparing may start with no session because it creates the first
            // one, while executing also needs the selected session.
            OfflineUpgradePanel.SessionCombo.IsEnabled = !busy && connected && _offlineSessions.Count > 0;

            bool canPrepare = !busy && bootPolicySupported &&
                OfflineUpdateUiState.CanPrepareSession(connected, imported, _offlinePreflightPassed,
                _offlineLastReceipt, _offlineSessions.Count > 0, _offlineSelectedSession != null);
            // A session the tablet already prepared reaches the recovery
            // export through the same Plan action. Export is read-only, so it
            // is enabled with the same live evidence a prepare needs (package,
            // selection, preflight) and only while no verified local bundle is
            // held; the boot policy is not part of this gate.
            bool canExportRecovery = !busy && OfflineUpdateUiState.CanExportRecoverySession(
                connected, imported, _offlinePreflightPassed, _offlineSelectedSession != null,
                _offlineLastReceipt, _offlineSavedRecovery != null);
            OfflineUpgradePanel.PrepareButton.IsEnabled = canPrepare || canExportRecovery;
            OfflineUpgradePanel.RefreshButton.IsEnabled = !busy &&
                OfflineUpdateUiState.CanRefreshStatus(connected, _offlineSelectedSession != null);

            OfflineUpgradePanel.ConfirmCheck.IsEnabled = !busy && OfflineUpdateUiState.CanMutateSession(connected, imported, _offlineSelectedSession != null) && preparedReady;
            OfflineUpgradePanel.ExecuteButton.IsEnabled = !busy && OfflineUpdateUiState.CanMutateSession(connected, imported, _offlineSelectedSession != null) && preparedReady && OfflineUpgradePanel.ConfirmCheck.IsChecked == true;
            OfflineUpgradePanel.StopWaitingButton.IsEnabled = busy && _offlineUpgradeActive;
            // Only a receipt read live from this tablet in this connection may
            // offer the restart action; a locally saved receipt is informative.
            OfflineUpgradePanel.RestartToVerifyButton.IsEnabled = !busy && connected && bootPolicySupported &&
                _offlineReceiptLive && _offlineSelectedSession != null &&
                OfflineUpdateUiState.CanRestartToVerify(_offlineLastReceipt);
        }

        private void RerenderOfflineUpgradeLog()
        {
            if (OfflineUpgradePanel == null)
                return;

            var lines = new List<string>(_offlineLogEntries.Count);
            foreach (var msg in _offlineLogEntries)
                lines.Add(_uiText.Format(msg));
            OfflineUpgradePanel.LogText.Text = string.Join(Environment.NewLine, lines);
            OfflineUpgradePanel.LogText.ScrollToEnd();
        }

        private void AppendOfflineLog(string key, params object?[] args)
        {
            if (_offlineLogEntries.Count >= 200)
                _offlineLogEntries.RemoveAt(0);
            _offlineLogEntries.Add(M(key, args));
            RerenderOfflineUpgradeLog();
        }

        private string GetPersistentRecordsRoot()
        {
            if (!_workDirectoryReady || _workDirectory == null || string.IsNullOrWhiteSpace(_workDirectory.RootPath))
                throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorWorkDirNotReady"));
            return OfflineUpdateRecordsDirectory.Resolve(BundledTools.GetCacheRoot(), _workDirectory.RootPath);
        }

        private async Task RunOfflineUpgradeAsync(Func<long, CancellationToken, Task> action)
        {
            if (_busy || _offlineUpgradeCloseInProgress || _closed)
                return;

            long gen = Interlocked.Increment(ref _offlineOperationGeneration);
            _offlineUpgradeActive = true;
            _offlineLastLoggedPhase = null;
            OfflineUpgradePanel.TransferProgress.Value = 0;
            SetOfflineIndeterminateProgress(true);

            await RunBusyAsync(async ct =>
            {
                try
                {
                    await action(gen, ct);
                }
                catch (OperationCanceledException)
                {
                    ClearOfflineActionEligibility();
                    _offlineLastReceipt = null;
                    _offlineReceiptLive = false;
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateStoppedWaiting");
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogStoppedWaiting");

                    // A cancel that interrupted an already sent request leaves
                    // this request/response stream in an unknown state; the
                    // client reports that as poisoned. Only the transport is
                    // released then: the imported package, the durable session
                    // records and the reconnect details stay so the operator can
                    // reconnect and continue. A cancel during a polling delay
                    // wrote nothing and keeps the connection.
                    if (_offlineSsh != null && _offlineSsh.Rpc.IsPoisoned)
                        await ReleasePoisonedOfflineConnectionAsync();
                }
                catch (OfflineUpdateTransferException tex)
                {
                    ClearOfflineActionEligibility();
                    if (tex.Receipt.HasValue)
                    {
                        _offlineLastReceipt = tex.Receipt.Value.Clone();
                        await TryPersistReceiptAsync(_offlineSelectedSession, _offlineLastReceipt.Value);
                        UpdateDetailsWithReceipt(_offlineLastReceipt.Value, tex.Message);
                    }
                    else
                    {
                        // 无回执的传输失败不能沿用上一次的旧回执，否则旧状态会继续驱动按钮。
                        _offlineLastReceipt = null;
                        _offlineReceiptLive = false;
                        SetText(OfflineUpgradePanel.DetailsText, "Dynamic.OfflineUpdate.Value", tex.Message);
                    }
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateFailed", tex.Message);
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogError", tex.Message);
                }
                catch (Exception ex)
                {
                    ClearOfflineActionEligibility();
                    _offlineLastReceipt = null;
                    _offlineReceiptLive = false;
                    SetText(OfflineUpgradePanel.DetailsText, "Dynamic.OfflineUpdate.Value", ex.Message);
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateFailed", ex.Message);
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogError", ex.Message);
                }
                finally
                {
                    Interlocked.Increment(ref _offlineOperationGeneration);
                    _offlineUpgradeActive = false;
                    SetOfflineIndeterminateProgress(false);
                    RefreshActions();
                }
            });
        }

        private void SetOfflineIndeterminateProgress(bool active)
        {
            if (OfflineUpgradePanel == null)
                return;
            OfflineUpgradePanel.TransferProgress.IsIndeterminate = active;
        }

        private IProgress<OfflineUpdateTransferProgress> CreateOfflineProgress(long generation)
        {
            return new Progress<OfflineUpdateTransferProgress>(p =>
            {
                if (Interlocked.Read(ref _offlineOperationGeneration) != generation || !_offlineUpgradeActive || _closed)
                    return;

                if (!string.IsNullOrWhiteSpace(p.Phase) && !string.Equals(_offlineLastLoggedPhase, p.Phase, StringComparison.Ordinal))
                {
                    _offlineLastLoggedPhase = p.Phase;
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogPhase", LocalizePhase(p.Phase));
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StatePhase", LocalizePhase(p.Phase));
                }

                if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0 && p.BytesCompleted.HasValue)
                {
                    OfflineUpgradePanel.TransferProgress.IsIndeterminate = false;
                    double pct = Math.Clamp((double)p.BytesCompleted.Value * 100.0 / p.TotalBytes.Value, 0.0, 100.0);
                    OfflineUpgradePanel.TransferProgress.Value = pct;
                    SetText(OfflineUpgradePanel.ProgressText, "Dynamic.OfflineUpdate.Value", $"{pct:F0}% ({p.BytesCompleted.Value / 1024.0:F1} KiB / {p.TotalBytes.Value / 1024.0:F1} KiB)");
                }
                else
                {
                    OfflineUpgradePanel.TransferProgress.IsIndeterminate = true;
                    SetText(OfflineUpgradePanel.ProgressText, LocalizePhase(p.Phase));
                }
            });
        }

        private UiMessage LocalizePhase(string? phase) => phase switch
        {
            "uploading" => M("Dynamic.OfflineUpdate.Phase.Uploading"),
            "uploaded" => M("Dynamic.OfflineUpdate.Phase.Uploaded"),
            "prepare_queued" => M("Dynamic.OfflineUpdate.Phase.PrepareQueued"),
            "preparing" => M("Dynamic.OfflineUpdate.Phase.Preparing"),
            "awaiting_recovery_export" => M("Dynamic.OfflineUpdate.Phase.AwaitingRecoveryExport"),
            "saving_recovery_metadata" => M("Dynamic.OfflineUpdate.Phase.SavingRecoveryMetadata"),
            "saving_recovery" => M("Dynamic.OfflineUpdate.Phase.SavingRecovery"),
            "prepared" => M("Dynamic.OfflineUpdate.Phase.Prepared"),
            "up_to_date" => M("Dynamic.OfflineUpdate.Phase.UpToDate"),
            "execute_queued" => M("Dynamic.OfflineUpdate.Phase.ExecuteQueued"),
            "executing" => M("Dynamic.OfflineUpdate.Phase.Executing"),
            "awaiting_reboot" => M("Dynamic.OfflineUpdate.Phase.AwaitingReboot"),
            "failed" => M("Dynamic.OfflineUpdate.Phase.Failed"),
            _ => string.IsNullOrWhiteSpace(phase) ? M("Dynamic.OfflineUpdate.Phase.Working") : M("Dynamic.OfflineUpdate.Value", phase!)
        };

        private void OnBrowseOfflineKey(object? sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { CheckFileExists = true };
            if (dlg.ShowDialog(this) == true)
                OfflineUpgradePanel.KeyPathBox.Text = dlg.FileName;
        }

        private void OnBrowseOfflineKnownHosts(object? sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { CheckFileExists = true };
            if (dlg.ShowDialog(this) == true)
                OfflineUpgradePanel.KnownHostsBox.Text = dlg.FileName;
        }

        private async void OnBrowseOfflinePackage(object? sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            var dlg = new OpenFileDialog
            {
                CheckFileExists = true,
                Filter = "Update Packages (*.zip;*.001)|*.zip;*.001|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true && !string.Equals(OfflineUpgradePanel.PackagePathBox.Text, dlg.FileName, StringComparison.Ordinal))
            {
                await RunOfflineUpgradeAsync((_, _) =>
                {
                    DisposeImportedPackageAndInvalidate();
                    OfflineUpgradePanel.PackagePathBox.Text = dlg.FileName;
                    RefreshActions();
                    return Task.CompletedTask;
                });
            }
        }

        private OfflineUpdateTransport SelectedOfflineTransport =>
            OfflineUpdateConnectionUiState.TransportFromIndex(
                OfflineUpgradePanel?.TransportCombo.SelectedIndex ?? OfflineUpdateConnectionUiState.UsbTransportIndex);

        private OfflineUpdateCredentialMode SelectedOfflineCredential =>
            OfflineUpdateConnectionUiState.CredentialFromIndex(
                OfflineUpgradePanel?.CredentialCombo.SelectedIndex ?? OfflineUpdateConnectionUiState.DefaultCredentialIndex);

        private void OnOfflineConnectionSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (OfflineUpgradePanel == null)
                return;

            // A pending key-pairing prompt belongs to one connection attempt, and
            // a secret typed for another selection is cleared instead of lingering
            // in a hidden field. The Wi-Fi address stays exactly as typed.
            ClearOfflinePairing();
            OfflineUpgradePanel.PasswordBox.Clear();
            RefreshOfflineUpgradeActions();
        }

        private string GetOfflineHostTrustDirectory() =>
            Path.Combine(BundledTools.GetCacheRoot(), "connections", "offline-upgrade", "trusted-hosts");

        private async Task<OfflineUpdateUsbEndpoint> FindOfflineUsbEndpointAsync(CancellationToken ct)
        {
            try
            {
                return await OfflineUpdateUsbDiscovery.FindSingleAsync(_runner, token: ct);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorUsbConnection", ex.Message));
            }
        }

        /// <summary>
        /// One connect action. The explicitly selected transport decides between
        /// USB discovery and the typed Wi-Fi address, then the explicitly
        /// selected credential source decides between the factory account, a
        /// custom account and an SSH key.
        /// </summary>
        private async void OnConnectOfflineTabletAsync(object? sender, RoutedEventArgs e)
        {
            if (_busy || _offlineSsh != null)
                return;

            var transport = SelectedOfflineTransport;
            var credential = SelectedOfflineCredential;
            await RunOfflineUpgradeAsync(async (_, ct) =>
            {
                string typedUser = OfflineUpgradePanel.UserBox.Text.Trim();
                string typedPassword = OfflineUpgradePanel.PasswordBox.Password;
                string keyPath = OfflineUpgradePanel.KeyPathBox.Text.Trim();
                string hostsPath = OfflineUpgradePanel.KnownHostsBox.Text.Trim();

                if (OfflineUpdateConnectionUiState.IsWifiAddressMissing(transport, OfflineUpgradePanel.HostBox.Text))
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorWifiAddressRequired"));

                int port = OfflineUpdateConnectionUiState.UsbSshPort;
                string host = string.Empty;
                // Only a validated USB discovery result sets this; the Wi-Fi
                // route keeps its typed host/port identity.
                string? usbDeviceInstanceId = null;
                if (OfflineUpdateConnectionUiState.UsesWifiAddress(transport))
                {
                    if (!OfflineUpdateConnectionUiState.TryParsePort(OfflineUpgradePanel.PortBox.Text, out port))
                        throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorWifiPortInvalid"));
                    host = OfflineUpgradePanel.HostBox.Text.Trim();
                }

                if (credential == OfflineUpdateCredentialMode.CustomAccount &&
                    OfflineUpdateConnectionUiState.IsCustomCredentialIncomplete(typedUser, typedPassword))
                {
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorCustomCredentialsRequired"));
                }

                if (credential == OfflineUpdateCredentialMode.PublicKey &&
                    OfflineUpdateConnectionUiState.IsExplicitKeyIncomplete(transport, credential, typedUser, keyPath, hostsPath))
                {
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorKeyCredentialsRequired"));
                }

                if (credential == OfflineUpdateCredentialMode.PublicKey)
                {
                    if (OfflineUpdateConnectionUiState.IsUsbKeyEnrollment(transport, credential, keyPath, hostsPath))
                    {
                        await BeginOfflineUsbKeyEnrollmentAsync(ct);
                        return;
                    }

                    string? keyBindAddress = null;
                    if (OfflineUpdateConnectionUiState.UsesUsbDiscovery(transport))
                    {
                        var endpoint = await FindOfflineUsbEndpointAsync(ct);
                        host = endpoint.Host;
                        keyBindAddress = endpoint.LocalAddress;
                    }

                    var keyOptions = new OfflineUpdateSshOptions(host, port, typedUser, keyPath, hostsPath, BindAddress: keyBindAddress);
                    ClearOfflinePairing();
                    await ConnectOfflineOptionsAsync(keyOptions, ct);
                    return;
                }

                var defaultCredential = OfflineUpdateSshDefaultCredentials.Instance;
                if (!OfflineUpdateConnectionUiState.TryResolvePasswordCredential(
                        credential, typedUser, typedPassword,
                        defaultCredential.UserName, defaultCredential.Password,
                        out string userName, out string password))
                {
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorCustomCredentialsRequired"));
                }

                string? bindAddress = null;
                if (OfflineUpdateConnectionUiState.UsesUsbDiscovery(transport))
                {
                    var endpoint = await FindOfflineUsbEndpointAsync(ct);
                    host = endpoint.Host;
                    bindAddress = endpoint.LocalAddress;
                    usbDeviceInstanceId = endpoint.DeviceInstanceId;
                }

                string rejectionKey = OfflineUpdateSshAuthPolicy.DefaultCredentialRejectionKey(host, port, usbDeviceInstanceId);
                bool defaultAccount = credential == OfflineUpdateCredentialMode.DefaultAccount;
                bool usingDefault = defaultAccount &&
                    OfflineUpdateSshAuthPolicy.ShouldAttemptDefaultCredential(
                        OfflineUpdateSshAuthMode.Password,
                        credentialsSuppliedByUser: false,
                        defaultAlreadyAttempted: _offlineDefaultCredentialRejected.Contains(rejectionKey));

                // The automatic factory attempt is limited to once per target;
                // afterwards the same selection asks for the password instead.
                if (defaultAccount && !usingDefault)
                {
                    ShowOfflinePasswordEntry("Dynamic.OfflineUpdate.DefaultCredentialRejected");
                    return;
                }

                var options = new OfflineUpdateSshPasswordOptions(
                    host,
                    port,
                    userName,
                    password,
                    GetOfflineHostTrustDirectory(),
                    bindAddress)
                {
                    ApproveHostKey = ApproveOfflineSshHostKeyAsync,
                };

                // Only a user-entered secret is cleared; the public factory
                // credential is product configuration, not a stored user secret.
                if (!defaultAccount)
                    OfflineUpgradePanel.PasswordBox.Clear();
                ClearOfflinePairing();
                try
                {
                    await ConnectOfflinePasswordAsync(options, ct);
                }
                catch (InvalidOperationException ex) when (usingDefault && IsOfflinePasswordRejection(ex))
                {
                    // Only a real authentication rejection consumes the single
                    // automatic attempt for this exact target.
                    _offlineDefaultCredentialRejected.Add(rejectionKey);
                    ShowOfflinePasswordEntry("Dynamic.OfflineUpdate.DefaultCredentialRejected");
                }
            });

            FocusOfflinePasswordIfPending();
        }

        /// <summary>
        /// The rejection path clears and shows the custom password field while
        /// the window is still busy, so focus is applied once the fields are
        /// enabled again.
        /// </summary>
        private void FocusOfflinePasswordIfPending()
        {
            if (!_offlineFocusPasswordOnIdle)
                return;
            _offlineFocusPasswordOnIdle = false;
            if (_busy || _offlineSsh != null || OfflineUpgradePanel == null)
                return;
            OfflineUpgradePanel.PasswordBox.Focus();
        }

        private async void OnDisconnectOfflineSshAsync(object? sender, RoutedEventArgs e)
        {
            await RunOfflineUpgradeAsync(async (_, _) =>
            {
                await DisconnectAndCleanupStageAsync();
                SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateDisconnected");
                AppendOfflineLog("Dynamic.OfflineUpdate.LogDisconnected");
            });
        }

        private async void OnOfflineUpgradeBackAsync(object? sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            if (_offlineSsh != null || _offlinePackage != null)
            {
                await RunOfflineUpgradeAsync(async (_, _) =>
                {
                    await DisconnectAndCleanupStageAsync();
                });
            }

            if (!_busy && _offlineSsh == null && _offlinePackage == null)
            {
                ClearOfflinePairing();
                _showingOfflineUpgrade = false;
                ShowFunctionHome();
            }
        }

        private async Task DisconnectAndCleanupStageAsync()
        {
            ClearOfflinePairing();
            ClearOfflinePasswordEntry();
            var ssh = _offlineSsh;
            try
            {
                DisposeImportedPackageAndInvalidate(disconnecting: true);
            }
            finally
            {
                _offlineSsh = null;
                _offlineTransfer = null;
                _offlineConnectedModel = null;
                _offlineActualSlot = null;
                _offlineRecoveryPartUuid = null;
                _offlineBootPolicySupported = false;
                ClearOfflinePreflightState();
                SetText(OfflineUpgradePanel.DeviceText, "Static.OfflineUpdate.DevicePending");
                SetText(OfflineUpgradePanel.DetailsText, "Static.OfflineUpdate.DetailsPending");
                if (ssh != null)
                    await ssh.DisposeAsync();
            }
        }

        private void ClearOfflinePasswordEntry()
        {
            // The rejected-target set is intentionally kept: the automatic
            // attempt is limited per target, not reset by a disconnect.
            if (OfflineUpgradePanel != null)
                OfflineUpgradePanel.PasswordBox.Clear();
        }

        private void DisposeImportedPackageAndInvalidate(bool disconnecting = false)
        {
            ClearOfflinePreflightState();
            ClearOfflineSelectionState();
            _suppressOfflineSessionSelectionChanged = true;
            OfflineUpgradePanel.SessionCombo.ItemsSource = null;
            _suppressOfflineSessionSelectionChanged = false;
            _offlineSessions = Array.Empty<OfflineUpdateSession>();
            _offlineDescriptorSha256 = null;
            var pkg = _offlinePackage;
            _offlinePackage = null;
            pkg?.Dispose();
            SetText(OfflineUpgradePanel.PackageText, "Static.OfflineUpdate.PackagePending");
            if (!disconnecting && _offlineSsh != null)
                LoadSavedOfflineSessionsForDevice();
        }

        private void ClearOfflineSelectionState()
        {
            _offlineSelectedSession = null;
            _offlineLastReceipt = null;
            _offlineReceiptLive = false;
            _offlineLastCompletionState = null;
            ClearOfflineActionEligibility();
            SetText(OfflineUpgradePanel.RecoveryPathText, "Static.OfflineUpdate.RecoveryPending");
        }

        private void ClearOfflineActionEligibility()
        {
            _offlineSavedRecovery = null;
            if (OfflineUpgradePanel != null)
                OfflineUpgradePanel.ConfirmCheck.IsChecked = false;
        }

        /// <summary>
        /// The read-only preflight only covers the package and device it was
        /// run against. Any package change, disconnect or invalidation drops
        /// the pass so Prepare cannot run on stale evidence.
        /// </summary>
        private void ClearOfflinePreflightState()
        {
            _offlinePreflightPassed = false;
            _offlinePreflightError = null;
        }

        private void MarkOfflinePreflightFailed(string reason)
        {
            _offlinePreflightPassed = false;
            _offlinePreflightError = reason;
            SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StatePreflightFailed", reason);
            AppendOfflineLog("Dynamic.OfflineUpdate.LogPreflightFailed", reason);
        }

        private async void OnValidateOfflinePackageAsync(object? sender, RoutedEventArgs e)
        {
            await RunOfflineUpgradeAsync(async (_, ct) =>
            {
                if (_offlineSsh == null || string.IsNullOrEmpty(_offlineConnectedModel) || string.IsNullOrEmpty(_offlineRecoveryPartUuid))
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorNotConnected"));

                ClearOfflinePreflightState();
                string path = OfflineUpgradePanel.PackagePathBox.Text.Trim();
                if (string.IsNullOrEmpty(path))
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorNoPackagePath"));

                DisposeImportedPackageAndInvalidate();
                string installerVer = GetThreePartInstallerVersion();
                var imported = await PackageImporter.ImportOfflineUpdateAsync(path, _offlineConnectedModel, installerVer, TemporaryWorkRoot, ct);
                try
                {
                    byte[] descBytes = await File.ReadAllBytesAsync(imported.DescriptorPath, ct);
                    string descSha = Convert.ToHexString(SHA256.HashData(descBytes)).ToLowerInvariant();

                    _offlinePackage = imported;
                    _offlineDescriptorSha256 = descSha;

                    SetText(OfflineUpgradePanel.PackageText, "Dynamic.OfflineUpdate.PackageValidated", imported.Descriptor.Version, imported.Descriptor.ReleaseId);
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogPackageValidated", imported.Descriptor.Version, imported.Descriptor.ReleaseId);

                    // Sessions are matched one record at a time. An unrelated
                    // damaged record is reported and skipped, and the validated
                    // package stays imported so it can still be inspected and
                    // used with the intact sessions.
                    ApplyFilteredOfflineSessions(LoadSavedOfflineSessions());
                    ReportDamagedOfflineSessionRecords();

                    // Read-only preflight. It never creates a session, prepares
                    // a candidate or writes a slot. Only the exact success
                    // schema unlocks Prepare; every failure keeps the package
                    // imported for inspection but records the actionable
                    // reason and blocks any new prepare/execute.
                    var deviceInfo = _offlineSsh!.DeviceInfo;
                    if (!OfflineUpdateUiState.IsUpdaterVersionAtLeast(deviceInfo, "0.8.0"))
                    {
                        MarkOfflinePreflightFailed(T("Dynamic.OfflineUpdate.ErrorPreflightUnsupportedUpdater",
                            GetJsonString(deviceInfo, "updaterVersion") ?? "?"));
                        return;
                    }

                    try
                    {
                        // A matching session already on the tablet is named so
                        // the device can credit the payload it already stores
                        // instead of demanding the full compressed size again.
                        var preflight = await _offlineTransfer!.PreflightAsync(imported, _offlineSelectedSession, ct);
                        if (!OfflineUpdateUiState.PreflightPassed(preflight))
                        {
                            MarkOfflinePreflightFailed(T("Dynamic.OfflineUpdate.ErrorPreflightInvalidResponse",
                                OfflineUpdateUiState.PreflightPhase(preflight) ?? "?"));
                            return;
                        }

                        _offlinePreflightPassed = true;
                        _offlinePreflightError = null;
                        AppendOfflineLog("Dynamic.OfflineUpdate.LogPreflightPassed",
                            OfflineUpdateUiState.PreflightDeployment(preflight) ?? "?",
                            OfflineUpdateUiState.PreflightPhase(preflight) ?? "?");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or ObjectDisposedException or OfflineUpdateTransferException)
                    {
                        // The device-side code (for example operation_failed)
                        // names the rejected check; keep it next to its cause.
                        string detail = ex is OfflineUpdateRemoteException remote
                            ? $"{remote.Code}: {remote.Message}"
                            : ex.Message;
                        MarkOfflinePreflightFailed(T("Dynamic.OfflineUpdate.ErrorPreflightFailed", detail));
                    }
                }
                catch
                {
                    if (_offlinePackage == null)
                        imported.Dispose();
                    throw;
                }
            });
        }

        /// <summary>Install the matched sessions, dropping the older package's receipt, live flag, completion and recovery.</summary>
        private void ApplyFilteredOfflineSessions(IReadOnlyList<OfflineUpdateSession> sessions)
        {
            ClearOfflineSelectionState();
            _offlineSessions = sessions;

            _suppressOfflineSessionSelectionChanged = true;
            try
            {
                OfflineUpgradePanel.SessionCombo.ItemsSource = _offlineSessions;
                if (_offlineSessions.Count == 1)
                {
                    OfflineUpgradePanel.SessionCombo.SelectedItem = _offlineSessions[0];
                    _offlineSelectedSession = _offlineSessions[0];
                    _offlineLastReceipt = TryLoadPersistedOfflineReceipt(_offlineSelectedSession);
                    if (_offlineLastReceipt.HasValue)
                    {
                        UpdateDetailsWithReceipt(_offlineLastReceipt.Value, null);
                        ShowPersistedOfflineResult(_offlineLastReceipt.Value);
                    }
                    else
                    {
                        SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateSingleSessionMatched", _offlineSelectedSession.SessionId);
                    }
                }
                else if (_offlineSessions.Count > 1)
                {
                    OfflineUpgradePanel.SessionCombo.SelectedIndex = -1;
                    _offlineSelectedSession = null;
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateMultipleSessionsFound", _offlineSessions.Count);
                }
                else
                {
                    OfflineUpgradePanel.SessionCombo.SelectedIndex = -1;
                    _offlineSelectedSession = null;
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateReadyToPrepareNew");
                }
            }
            finally
            {
                _suppressOfflineSessionSelectionChanged = false;
            }
        }

        private void OnOfflineSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_suppressOfflineSessionSelectionChanged)
                return;

            // Preflight evidence belongs to the session it was run for; another
            // selection always has to earn its own read-only pass.
            ClearOfflinePreflightState();
            ClearOfflineSelectionState();
            _offlineSelectedSession = OfflineUpgradePanel.SessionCombo.SelectedItem as OfflineUpdateSession;
            if (_offlineSelectedSession != null)
            {
                // After an app restart the last persisted result is shown from
                // local records without contacting the tablet or re-uploading.
                _offlineLastReceipt = TryLoadPersistedOfflineReceipt(_offlineSelectedSession);
                if (_offlineLastReceipt.HasValue)
                {
                    UpdateDetailsWithReceipt(_offlineLastReceipt.Value, null);
                    ShowPersistedOfflineResult(_offlineLastReceipt.Value);
                }
                else
                {
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateSessionSelected", _offlineSelectedSession.SessionId);
                }
            }
            RefreshActions();
        }

        private void OnOfflineConfirmChanged(object? sender, RoutedEventArgs e)
        {
            if (!_busy)
                RefreshOfflineUpgradeActions();
        }

        private async void OnPrepareOfflineUpgradeAsync(object? sender, RoutedEventArgs e)
        {
            await RunOfflineUpgradeAsync(async (gen, ct) =>
            {
                EnsureReadyForSessionWork(requireSelectedSession: _offlineSessions.Count > 0);
                // A session the tablet already prepared reaches the export step
                // instead of failing the prepare gate; it is never prepared a
                // second time. Export is read-only, so it is the only branch
                // that does not need the write-side boot policy.
                bool exportRecoveryOnly =
                    OfflineUpdateUiState.CanExportRecovery(_offlineLastReceipt, _offlineSavedRecovery != null);
                if (exportRecoveryOnly && _offlineSelectedSession == null)
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorSessionNotSelected"));
                if (!exportRecoveryOnly && !_offlineBootPolicySupported)
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorBootPolicyRequired"));
                if (!_offlinePreflightPassed)
                    throw new InvalidOperationException(string.IsNullOrEmpty(_offlinePreflightError)
                        ? T("Dynamic.OfflineUpdate.ErrorPreflightRequired")
                        : _offlinePreflightError!);
                if (!exportRecoveryOnly &&
                    !OfflineUpdateUiState.CanPrepare(_offlineLastReceipt, _offlineSessions.Count > 0, _offlineSelectedSession != null))
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorStateNotActionable", _uiText.Format(LocalizePhase(GetReceiptState(_offlineLastReceipt)))));
                ClearOfflineActionEligibility();
                string recordsRoot = GetPersistentRecordsRoot();
                var session = _offlineSelectedSession;

                if (session == null)
                {
                    session = await _offlineTransfer!.CreateSessionAsync(_offlinePackage!, (s, token) =>
                    {
                        PackageImporter.SaveOfflineUpdateSession(recordsRoot, s, token);
                        return Task.CompletedTask;
                    }, ct);

                    _offlineSessions = new[] { session };
                    _suppressOfflineSessionSelectionChanged = true;
                    OfflineUpgradePanel.SessionCombo.ItemsSource = _offlineSessions;
                    OfflineUpgradePanel.SessionCombo.SelectedItem = session;
                    _suppressOfflineSessionSelectionChanged = false;
                    _offlineSelectedSession = session;
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogSessionCreated", session.SessionId);
                }

                var progress = CreateOfflineProgress(gen);
                string sessionRoot = Path.Combine(recordsRoot, session.SessionId);
                Directory.CreateDirectory(sessionRoot);

                if (exportRecoveryOnly)
                {
                    // The tablet already prepared this session. Preparing again
                    // would repeat device-side validation for no gain, so the
                    // Plan action resumes at the only remaining local step: the
                    // verified recovery bundle whose acknowledgement execution
                    // later hands back to the tablet.
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogRecoveryExportResumed", session.SessionId);
                    JsonElement preparedReceipt = _offlineLastReceipt ?? throw new InvalidOperationException(
                        T("Dynamic.OfflineUpdate.ErrorStateNotActionable",
                            _uiText.Format(LocalizePhase(GetReceiptState(_offlineLastReceipt)))));
                    await EnsureOfflineRecoveryAsync(session, preparedReceipt, sessionRoot, gen, ct);
                }
                else
                {
                    var receipt = await _offlineTransfer!.PrepareAsync(_offlinePackage!, session, progress, ct);
                    _offlineReceiptLive = true;
                    _offlineLastReceipt = receipt.Clone();
                    await TryPersistReceiptAsync(session, _offlineLastReceipt.Value);
                    UpdateDetailsWithReceipt(_offlineLastReceipt.Value, null);

                    string state = GetReceiptState(_offlineLastReceipt) ?? string.Empty;
                    if (state == "up_to_date")
                    {
                        SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateUpToDate");
                        AppendOfflineLog("Dynamic.OfflineUpdate.LogUpToDate");
                        return;
                    }

                    EnsureNoBlockingReceiptFlags(_offlineLastReceipt.Value);
                    await EnsureOfflineRecoveryAsync(session, _offlineLastReceipt.Value, sessionRoot, gen, ct);
                }

                Interlocked.Increment(ref _offlineOperationGeneration);
                var status = await _offlineTransfer!.QueryStatusAsync(_offlinePackage!, session, ct);
                _offlineReceiptLive = true;
                _offlineLastReceipt = status.Clone();
                await TryPersistReceiptAsync(session, _offlineLastReceipt.Value);
                UpdateDetailsWithReceipt(_offlineLastReceipt.Value, null);
                await ApplyQueriedReceiptState(_offlineLastReceipt.Value, sessionRoot, ct, allowLoadRecovery: true);
            });
        }

        /// <summary>
        /// Produce the verified local recovery bundle for a session the tablet
        /// already prepared, and hold the exact bytes execution returns as its
        /// acknowledgement. It exports only: no prepare, no execute, no slot
        /// write. An existing bundle is reused only when it validates against
        /// this transaction and the imported package; an unusable leftover is
        /// reported and left where it is while a fresh bundle is written beside
        /// it.
        /// </summary>
        private async Task<SavedOfflineUpdateRecovery> EnsureOfflineRecoveryAsync(
            OfflineUpdateSession session, JsonElement receipt, string sessionRoot,
            long generation, CancellationToken ct)
        {
            if (_offlineSsh == null || _offlineTransfer == null || string.IsNullOrEmpty(_offlineConnectedModel))
                throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorNotConnected"));
            if (_offlinePackage == null || string.IsNullOrEmpty(_offlineDescriptorSha256))
                throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorPackageNotValidated"));

            OfflineUpdateRecoveryContext? context = TryExtractRecoveryContext(receipt, _offlineConnectedModel!);
            SavedOfflineUpdateRecovery? existing = null;
            if (context != null)
            {
                var scan = await Task.Run(
                    () => OfflineUpdateRecoveryCatalog.Scan(sessionRoot, context, _offlineDescriptorSha256!, ct), ct);
                ReportSkippedOfflineRecoveryBundles(scan.Skipped);
                existing = scan.Recovery;
            }

            if (existing == null)
            {
                existing = await _offlineTransfer.SaveRecoveryAsync(
                    _offlinePackage!, session, sessionRoot, CreateOfflineProgress(generation), ct);
                AppendOfflineLog("Dynamic.OfflineUpdate.LogRecoverySaved", existing.DirectoryPath);
            }
            else
            {
                AppendOfflineLog("Dynamic.OfflineUpdate.LogRecoveryReused", existing.DirectoryPath);
            }

            // Execution may only start with the validated acknowledgement that
            // belongs to this transaction; a bundle without it is not evidence.
            if (existing.Acknowledgement is not { Length: > 0 })
                throw new InvalidDataException(T("Dynamic.OfflineUpdate.ErrorRecoveryMissing"));

            _offlineSavedRecovery = existing;
            SetText(OfflineUpgradePanel.RecoveryPathText, "Dynamic.OfflineUpdate.Value", existing.DirectoryPath);
            return existing;
        }

        private async void OnRefreshOfflineStatusAsync(object? sender, RoutedEventArgs e)
        {
            // A refresh re-establishes readiness from scratch: any earlier pass
            // is dropped before the asynchronous work starts, so a stale result
            // can never unlock prepare/execute in the meantime.
            if (_offlinePackage != null && !string.IsNullOrEmpty(_offlineDescriptorSha256))
                ClearOfflinePreflightState();

            await RunOfflineUpgradeAsync(async (gen, ct) =>
            {
                if (_offlineSsh == null || _offlineTransfer == null || string.IsNullOrEmpty(_offlineConnectedModel))
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorNotConnected"));
                if (_offlineSelectedSession == null)
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorSessionNotSelected"));

                ClearOfflineActionEligibility();
                var session = _offlineSelectedSession!;
                bool hasPackage = _offlinePackage != null && !string.IsNullOrEmpty(_offlineDescriptorSha256);
                string recordsRoot = GetPersistentRecordsRoot();
                string sessionRoot = Path.Combine(recordsRoot, session.SessionId);

                // Read-only. Without an imported package only the saved
                // session/device binding is verified; nothing is uploaded,
                // prepared or executed.
                var receipt = hasPackage
                    ? await _offlineTransfer!.QueryStatusAsync(_offlinePackage!, session, ct)
                    : await _offlineTransfer!.QuerySessionStatusAsync(session, ct);
                _offlineReceiptLive = true;
                if (GetReceiptState(receipt) != "execute_queued" && GetReceiptState(receipt) != "executing")
                    Interlocked.Increment(ref _offlineOperationGeneration);
                _offlineLastReceipt = receipt.Clone();
                await TryPersistReceiptAsync(session, _offlineLastReceipt.Value);
                UpdateDetailsWithReceipt(_offlineLastReceipt.Value, null);

                string? state = GetReceiptState(_offlineLastReceipt);
                if ((state == "execute_queued" || state == "executing") && hasPackage)
                {
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogObservingExecution", session.SessionId);
                    var observed = await _offlineTransfer.ObserveExecutionAsync(_offlinePackage!, session, CreateOfflineProgress(gen), ct);
                    Interlocked.Increment(ref _offlineOperationGeneration);
                    _offlineLastReceipt = observed.Clone();
                    await TryPersistReceiptAsync(session, _offlineLastReceipt.Value);
                    UpdateDetailsWithReceipt(_offlineLastReceipt.Value, null);
                }

                await ApplyQueriedReceiptState(_offlineLastReceipt.Value, sessionRoot, ct, allowLoadRecovery: hasPackage);

                // The status above stays visible even if this read-only
                // preflight fails. With the session named, a session that
                // already holds the payload is not charged for it twice.
                // Executing/terminal receipts keep their own status message.
                string? refreshed = GetReceiptState(_offlineLastReceipt);
                bool preflightable = OfflineUpdateUiState.ShouldRefreshPreflight(refreshed);
                if (hasPackage && preflightable)
                    await RefreshOfflinePreflightAsync(ct);
            });
        }

        /// <summary>
        /// Read-only preflight for the imported package and the selected
        /// session. It records the pass or the device's rejection and never
        /// throws, so the status query that preceded it stays usable.
        /// </summary>
        private async Task RefreshOfflinePreflightAsync(CancellationToken ct)
        {
            if (_offlineSsh == null || _offlineTransfer == null || _offlinePackage == null)
                return;

            var deviceInfo = _offlineSsh.DeviceInfo;
            if (!OfflineUpdateUiState.IsUpdaterVersionAtLeast(deviceInfo, "0.8.0"))
            {
                MarkOfflinePreflightFailed(T("Dynamic.OfflineUpdate.ErrorPreflightUnsupportedUpdater",
                    GetJsonString(deviceInfo, "updaterVersion") ?? "?"));
                return;
            }

            try
            {
                var preflight = await _offlineTransfer.PreflightAsync(_offlinePackage, _offlineSelectedSession, ct);
                if (!OfflineUpdateUiState.PreflightPassed(preflight))
                {
                    MarkOfflinePreflightFailed(T("Dynamic.OfflineUpdate.ErrorPreflightInvalidResponse",
                        OfflineUpdateUiState.PreflightPhase(preflight) ?? "?"));
                    return;
                }

                _offlinePreflightPassed = true;
                _offlinePreflightError = null;
                AppendOfflineLog("Dynamic.OfflineUpdate.LogPreflightPassed",
                    OfflineUpdateUiState.PreflightDeployment(preflight) ?? "?",
                    OfflineUpdateUiState.PreflightPhase(preflight) ?? "?");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or ObjectDisposedException or OfflineUpdateTransferException)
            {
                string detail = ex is OfflineUpdateRemoteException remote
                    ? $"{remote.Code}: {remote.Message}"
                    : ex.Message;
                MarkOfflinePreflightFailed(T("Dynamic.OfflineUpdate.ErrorPreflightFailed", detail));
            }
        }

        private async void OnExecuteOfflineUpgradeAsync(object? sender, RoutedEventArgs e)
        {
            if (_busy || _offlineSsh == null || _offlinePackage == null || _offlineSelectedSession == null ||
                !_offlineBootPolicySupported ||
                !OfflineUpdateUiState.CanExecute(_offlineLastReceipt, _offlineSavedRecovery != null, _offlinePreflightPassed) || OfflineUpgradePanel.ConfirmCheck.IsChecked != true)
                return;

            UiMessage modelName = LocalizeModelName(_offlineConnectedModel ?? string.Empty);
            string msg = T("Dynamic.OfflineUpdate.ConfirmExecutePrompt", modelName, _offlinePackage.Descriptor.Version, _offlinePackage.Descriptor.ReleaseId);
            var res = MessageBox.Show(this, msg, T("Static.OfflineUpdate.Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (res != MessageBoxResult.Yes)
                return;

            await RunOfflineUpgradeAsync(async (gen, ct) =>
            {
                EnsureReadyForSessionWork(requireSelectedSession: true);
                if (!_offlineBootPolicySupported)
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorBootPolicyRequired"));
                if (!_offlinePreflightPassed)
                    throw new InvalidOperationException(string.IsNullOrEmpty(_offlinePreflightError)
                        ? T("Dynamic.OfflineUpdate.ErrorPreflightRequired")
                        : _offlinePreflightError!);
                if (!OfflineUpdateUiState.CanExecute(_offlineLastReceipt, _offlineSavedRecovery != null, _offlinePreflightPassed))
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorRecoveryMissing"));
                if (_offlineSavedRecovery == null)
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorRecoveryMissing"));

                var session = _offlineSelectedSession!;
                string recordsRoot = GetPersistentRecordsRoot();
                string sessionRoot = Path.Combine(recordsRoot, session.SessionId);
                AppendOfflineLog("Dynamic.OfflineUpdate.LogExecuting", session.SessionId);

                var receipt = await _offlineTransfer!.ExecuteAsync(_offlinePackage!, session, _offlineSavedRecovery, CreateOfflineProgress(gen), ct);
                Interlocked.Increment(ref _offlineOperationGeneration);
                _offlineReceiptLive = true;
                _offlineLastReceipt = receipt.Clone();
                await TryPersistReceiptAsync(session, _offlineLastReceipt.Value);
                UpdateDetailsWithReceipt(_offlineLastReceipt.Value, null);
                await ApplyQueriedReceiptState(_offlineLastReceipt.Value, sessionRoot, ct, allowLoadRecovery: false);
            });
        }

        private void OnStopWaitingOfflineUpgrade(object? sender, RoutedEventArgs e)
        {
            if (!_busy || !_offlineUpgradeActive)
                return;
            _cts?.Cancel();
        }

        private async void OnOfflineUpgradeClosing(object? sender, CancelEventArgs e)
        {
            if (e.Cancel || _offlineUpgradeCloseApproved)
                return;

            if (_offlineUpgradeCloseInProgress)
            {
                e.Cancel = true;
                return;
            }

            if (!_offlineUpgradeActive && _offlineSsh == null && _offlinePackage == null)
                return;

            e.Cancel = true;
            _offlineUpgradeCloseInProgress = true;
            // 先在等待清理期间禁用全部按钮，再开始取消 / 断开流程。
            RefreshActions();
            try
            {
                if (_offlineUpgradeActive)
                {
                    _cts?.Cancel();
                    if (_activeWorkCompletion != null)
                        await _activeWorkCompletion.Task;
                }

                await DisconnectAndCleanupStageAsync();
            }
            catch (Exception ex)
            {
                SetText(OfflineUpgradePanel.DetailsText, "Dynamic.OfflineUpdate.Value", ex.Message);
                SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateFailed", ex.Message);
                AppendOfflineLog("Dynamic.OfflineUpdate.LogError", ex.Message);
                return;
            }
            finally
            {
                _offlineUpgradeCloseInProgress = false;
                RefreshActions();
            }

            _offlineUpgradeCloseApproved = true;
            Dispatcher.BeginInvoke(new Action(Close));
        }

        private async Task ApplyQueriedReceiptState(JsonElement receipt, string sessionRoot, CancellationToken ct, bool allowLoadRecovery)
        {
            EnsureNoBlockingReceiptFlags(receipt);
            string state = GetReceiptState(receipt) ?? "unknown";

            switch (state)
            {
                case "prepared":
                    if (allowLoadRecovery && _offlineSavedRecovery == null && Directory.Exists(sessionRoot))
                    {
                        // Only a receipt whose export metadata agrees with the
                        // Core precedence may name the local recovery bundle.
                        var ctx = TryExtractRecoveryContext(receipt, _offlineConnectedModel!);
                        if (ctx != null)
                        {
                            var scan = await Task.Run(
                                () => OfflineUpdateRecoveryCatalog.Scan(sessionRoot, ctx, _offlineDescriptorSha256!, ct), ct);
                            ReportSkippedOfflineRecoveryBundles(scan.Skipped);
                            if (scan.Recovery != null)
                            {
                                _offlineSavedRecovery = scan.Recovery;
                                SetText(OfflineUpgradePanel.RecoveryPathText, "Dynamic.OfflineUpdate.Value", scan.Recovery.DirectoryPath);
                            }
                        }
                        else
                        {
                            AppendOfflineLog("Dynamic.OfflineUpdate.LogRecoveryIgnored",
                                T("Dynamic.OfflineUpdate.ErrorInvalidRecoveryContext"));
                        }
                    }
                    if (_offlineSavedRecovery != null)
                    {
                        if (_offlineBootPolicySupported)
                        {
                            SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StatePreparedReady");
                            AppendOfflineLog("Dynamic.OfflineUpdate.LogPreparedReady");
                        }
                        else
                        {
                            // The read-only export produced the bundle, but this
                            // helper never declared the no-slot-control policy:
                            // execution stays blocked, so the panel keeps
                            // reporting the read-only connection.
                            SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateBootPolicyReadOnly");
                        }
                    }
                    else if (string.IsNullOrEmpty(_offlineDescriptorSha256))
                    {
                        // Status was read without the package: preparation is
                        // already complete on the tablet, but executing still
                        // needs the validated package and its recovery data.
                        ClearOfflineActionEligibility();
                        SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StatePreparedNeedsPackage");
                    }
                    else
                    {
                        ClearOfflineActionEligibility();
                        SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StatePreparedMissingRecovery");
                    }
                    break;

                case "awaiting_recovery_export":
                    ClearOfflineActionEligibility();
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateAwaitingRecoveryExport");
                    break;

                case "up_to_date":
                    ClearOfflineActionEligibility();
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateUpToDate");
                    break;

                case "awaiting_reboot":
                    ClearOfflineActionEligibility();
                    ShowOfflineCompletion(receipt, log: true);
                    break;

                case "uploading":
                case "uploaded":
                case "prepare_queued":
                case "preparing":
                case "execute_queued":
                case "executing":
                    ClearOfflineActionEligibility();
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StatePhase", LocalizePhase(state));
                    break;

                default:
                    ClearOfflineActionEligibility();
                    _offlineLastReceipt = null;
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorStateNotActionable", LocalizePhase(state)));
            }
        }

        /// <summary>
        /// Render the durable tablet verdict carried by the receipt's
        /// <c>completion</c> block. The block is produced only by the device and
        /// reflects live trial/boot/finalization evidence, so this method never
        /// upgrades the message to "confirmed" on its own.
        /// </summary>
        private void ShowOfflineCompletion(JsonElement receipt, bool log)
        {
            JsonElement? completionValue = OfflineUpdateUiState.Completion(receipt);
            JsonElement completion = completionValue ?? default;
            string? completionState = OfflineUpdateUiState.CompletionState(receipt);
            string? restartState = OfflineUpdateUiState.RestartState(receipt);

            string key;
            string? detail = null;
            switch (completionState)
            {
                case null:
                    key = "Dynamic.OfflineUpdate.StateAwaitingReboot";
                    break;
                case "awaiting_reboot":
                    key = restartState switch
                    {
                        "dispatched" => "Dynamic.OfflineUpdate.StateRestartDispatched",
                        "dispatching" => "Dynamic.OfflineUpdate.StateRestartPending",
                        "uncertain" => "Dynamic.OfflineUpdate.StateRestartUncertain",
                        _ => "Dynamic.OfflineUpdate.StateCompletionAwaitingReboot"
                    };
                    break;
                case "awaiting_confirmation":
                    key = "Dynamic.OfflineUpdate.StateCompletionAwaitingConfirmation";
                    break;
                case "finalizing":
                    key = "Dynamic.OfflineUpdate.StateCompletionFinalizing";
                    break;
                case "confirmed":
                    key = "Dynamic.OfflineUpdate.StateCompletionConfirmed";
                    break;
                case "rolled_back":
                    key = "Dynamic.OfflineUpdate.StateCompletionRolledBack";
                    break;
                case "failed":
                    key = "Dynamic.OfflineUpdate.StateCompletionFailed";
                    detail = GetJsonString(completion, "error") ?? GetJsonString(completion, "reason");
                    break;
                case "mismatch":
                    key = "Dynamic.OfflineUpdate.StateCompletionMismatch";
                    detail = GetJsonString(completion, "error");
                    break;
                default:
                    key = "Dynamic.OfflineUpdate.StateCompletionUnverified";
                    detail = GetJsonString(completion, "error") ?? GetJsonString(completion, "reason");
                    break;
            }

            SetText(OfflineUpgradePanel.StateText, key, detail ?? string.Empty);
            if (!log)
            {
                _offlineLastCompletionState = null;
                return;
            }

            string signature = key + "|" + (detail ?? string.Empty);
            if (!string.Equals(_offlineLastCompletionState, signature, StringComparison.Ordinal))
            {
                _offlineLastCompletionState = signature;
                AppendOfflineLog(key, detail ?? string.Empty);
            }
        }

        private void ShowPersistedOfflineResult(JsonElement receipt)
        {
            // A receipt read from disk is informational only: it never enables
            // an action that needs live device evidence.
            _offlineReceiptLive = false;
            string? state = GetReceiptState(receipt);
            if (OfflineUpdateUiState.Completion(receipt) != null || state == "awaiting_reboot")
            {
                ShowOfflineCompletion(receipt, log: false);
                AppendOfflineLog("Dynamic.OfflineUpdate.LogPersistedNeedsRefresh");
                return;
            }

            _offlineLastCompletionState = null;
            switch (state)
            {
                case "prepared":
                    ClearOfflineActionEligibility();
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StatePreparedMissingRecovery");
                    break;
                case "up_to_date":
                    ClearOfflineActionEligibility();
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateUpToDate");
                    break;
                case "failed":
                    ClearOfflineActionEligibility();
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateFailed",
                        GetJsonString(receipt, "error") ?? string.Empty);
                    break;
                default:
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StatePersistedResult",
                        LocalizePhase(state));
                    break;
            }
            AppendOfflineLog("Dynamic.OfflineUpdate.LogPersistedResult", LocalizePhase(state));
        }

        private JsonElement? TryLoadPersistedOfflineReceipt(OfflineUpdateSession session)
        {
            try
            {
                string path = Path.Combine(GetPersistentRecordsRoot(), session.SessionId, "last-receipt.json");
                var info = new FileInfo(path);
                if (!info.Exists || info.LinkTarget != null || info.Length <= 0 || info.Length > 8L * 1024 * 1024)
                    return null;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !string.Equals(GetJsonString(root, "sessionId"), session.SessionId, StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(GetJsonString(root, "state")))
                    return null;
                return root.Clone();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Rebuild the session selector from the device-bound records on
        /// connect. This runs before any package is imported, so the saved
        /// result of an earlier run stays readable after restarting the app.
        /// </summary>
        private void LoadSavedOfflineSessionsForDevice()
        {
            _offlineSessions = LoadSavedOfflineSessions();
            ClearOfflineSelectionState();
            _suppressOfflineSessionSelectionChanged = true;
            OfflineUpgradePanel.SessionCombo.ItemsSource = _offlineSessions;
            OfflineUpgradePanel.SessionCombo.SelectedIndex = _offlineSessions.Count == 1 ? 0 : -1;
            _suppressOfflineSessionSelectionChanged = false;
            if (_offlineSessions.Count == 0)
            {
                // No readable session is left, so the damaged record is the
                // only thing worth showing.
                ReportDamagedOfflineSessionRecords();
                return;
            }

            AppendOfflineLog("Dynamic.OfflineUpdate.LogSavedSessionsFound", _offlineSessions.Count);
            if (_offlineSessions.Count > 1)
            {
                SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateMultipleSessionsFound", _offlineSessions.Count);
                return;
            }

            _offlineSelectedSession = _offlineSessions[0];
            _offlineLastReceipt = TryLoadPersistedOfflineReceipt(_offlineSelectedSession);
            if (_offlineLastReceipt.HasValue)
            {
                UpdateDetailsWithReceipt(_offlineLastReceipt.Value, null);
                ShowPersistedOfflineResult(_offlineLastReceipt.Value);
            }
            else
            {
                SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateSessionSelected", _offlineSelectedSession.SessionId);
            }
        }

        /// <summary>
        /// The saved sessions of this exact device, read one record at a time.
        /// A damaged record is remembered with its name for
        /// <see cref="ReportDamagedOfflineSessionRecords"/> instead of hiding
        /// the intact sessions, and it is never rewritten.
        /// </summary>
        private IReadOnlyList<OfflineUpdateSession> LoadSavedOfflineSessions()
        {
            try
            {
                OfflineSessionRecordScan scan = OfflineUpdateSessionCatalog.Load(
                    GetPersistentRecordsRoot(), _offlineConnectedModel, _offlineRecoveryPartUuid, _offlineDescriptorSha256);
                _offlineDamagedSessionRecords = scan.Damaged;
                return scan.Sessions;
            }
            catch (Exception)
            {
                _offlineDamagedSessionRecords = Array.Empty<OfflineDamagedSessionRecord>();
                return Array.Empty<OfflineUpdateSession>();
            }
        }

        /// <summary>
        /// Name every damaged record, so a record that cannot be read is
        /// visible instead of silently missing from the session list.
        /// </summary>
        private void ReportDamagedOfflineSessionRecords()
        {
            if (OfflineUpgradePanel == null || _offlineDamagedSessionRecords.Count == 0)
                return;

            foreach (var damaged in _offlineDamagedSessionRecords)
                AppendOfflineLog("Dynamic.OfflineUpdate.LogDamagedSessionRecord", damaged.RecordName, damaged.Reason);

            var first = _offlineDamagedSessionRecords[0];
            SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateDamagedSessionRecord",
                _offlineDamagedSessionRecords.Count, first.RecordName, first.Reason);
        }

        private async void OnRestartToVerifyOfflineUpgradeAsync(object? sender, RoutedEventArgs e)
        {
            if (_busy || _offlineSsh == null || _offlineTransfer == null || _offlineSelectedSession == null ||
                !_offlineBootPolicySupported || !_offlineReceiptLive ||
                !OfflineUpdateUiState.CanRestartToVerify(_offlineLastReceipt))
                return;

            var res = MessageBox.Show(this, T("Dynamic.OfflineUpdate.ConfirmRestartToVerifyPrompt"),
                T("Static.OfflineUpdate.Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (res != MessageBoxResult.Yes)
                return;

            await RunOfflineUpgradeAsync(async (_, ct) =>
            {
                // The restart only needs the connected session: it neither
                // re-uploads nor re-validates the package.
                if (_offlineSsh == null || _offlineTransfer == null ||
                    string.IsNullOrEmpty(_offlineConnectedModel))
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorNotConnected"));
                if (_offlineSelectedSession == null)
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorSessionNotSelected"));
                if (!_offlineBootPolicySupported)
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorBootPolicyRequired"));
                if (!_offlineReceiptLive || !OfflineUpdateUiState.CanRestartToVerify(_offlineLastReceipt))
                    throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorRestartNotEligible"));

                var session = _offlineSelectedSession!;
                AppendOfflineLog("Dynamic.OfflineUpdate.LogRestartRequested", session.SessionId);

                JsonElement result;
                try
                {
                    result = await _offlineTransfer!.RestartToVerifyAsync(session, ct);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
                {
                    // The request may have reached the tablet. This is pending or
                    // unknown, never a failed upgrade, and it is never retried
                    // automatically: the next read-only refresh decides.
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateRestartUnknown");
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogRestartUnknown", ex.Message);
                    _offlineReceiptLive = false;
                    await DropOfflineConnectionKeepPackageAsync();
                    return;
                }

                string restartState = result.TryGetProperty("restart", out JsonElement restart) &&
                    restart.ValueKind == JsonValueKind.Object
                    ? GetJsonString(restart, "state") ?? string.Empty
                    : string.Empty;
                if (restartState == "dispatched")
                {
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateRestartDispatched");
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogRestartDispatched");
                }
                else if (restartState == "dispatching")
                {
                    // A previous request from another client is still inside the
                    // reboot window; it is pending, not a new request.
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateRestartPending");
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogRestartPending");
                }
                else
                {
                    SetText(OfflineUpgradePanel.StateText, "Dynamic.OfflineUpdate.StateRestartUncertain");
                    AppendOfflineLog("Dynamic.OfflineUpdate.LogRestartUnknown", string.Empty);
                }
                _offlineReceiptLive = false;
                await DropOfflineConnectionKeepPackageAsync();
            });
        }

        /// <summary>
        /// The cancel hit a request that had already been sent, so the stream
        /// can no longer be trusted. Release only the transport: the imported
        /// package, the saved sessions and the connection fields the operator
        /// needs to reconnect are kept.
        /// </summary>
        private async Task ReleasePoisonedOfflineConnectionAsync()
        {
            AppendOfflineLog("Dynamic.OfflineUpdate.LogPoisonedConnectionReleased");
            try
            {
                await DropOfflineConnectionKeepPackageAsync();
            }
            catch (Exception ex)
            {
                // Releasing a poisoned transport must not turn the user's
                // cancel into a failed operation.
                _offlineSsh = null;
                _offlineTransfer = null;
                AppendOfflineLog("Dynamic.OfflineUpdate.LogError", ex.Message);
            }
        }

        private async Task DropOfflineConnectionKeepPackageAsync()
        {
            var ssh = _offlineSsh;
            _offlineSsh = null;
            _offlineTransfer = null;
            _offlineConnectedModel = null;
            _offlineActualSlot = null;
            _offlineRecoveryPartUuid = null;
            _offlineBootPolicySupported = false;
            _offlineReceiptLive = false;
            ClearOfflinePreflightState();
            ClearOfflinePairing();
            ClearOfflinePasswordEntry();
            if (OfflineUpgradePanel != null)
            {
                SetText(OfflineUpgradePanel.DeviceText, "Static.OfflineUpdate.DevicePending");
            }
            if (ssh != null)
                await ssh.DisposeAsync();
            AppendOfflineLog("Dynamic.OfflineUpdate.LogDisconnected");
        }

        /// <summary>
        /// Report every bundle the scan refused, on the thread that owns the
        /// panel. A refused bundle stays on disk untouched: it may belong to
        /// another session or package and is never rewritten or deleted.
        /// </summary>
        private void ReportSkippedOfflineRecoveryBundles(IReadOnlyList<OfflineSkippedRecoveryBundle> skipped)
        {
            foreach (var bundle in skipped)
                AppendOfflineLog("Dynamic.OfflineUpdate.LogRecoveryIgnored", bundle.Reason);
        }

        /// <summary>
        /// The recovery context follows the Core precedence exactly (result,
        /// then transaction, both present blocks must agree). A receipt the
        /// Core would reject yields no context, so it can never name different
        /// local bytes here.
        /// </summary>
        private OfflineUpdateRecoveryContext? TryExtractRecoveryContext(JsonElement receipt, string model) =>
            OfflineUpdateUiState.TryGetRecoveryMetadata(receipt,
                out string? transactionId, out string? planSha256, out string? recoveryManifestSha256)
                ? new OfflineUpdateRecoveryContext(transactionId!, model, planSha256!, recoveryManifestSha256!)
                : null;

        private void EnsureReadyForSessionWork(bool requireSelectedSession)
        {
            if (_offlineSsh == null || _offlineTransfer == null || string.IsNullOrEmpty(_offlineConnectedModel))
                throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorNotConnected"));
            if (_offlinePackage == null || string.IsNullOrEmpty(_offlineDescriptorSha256))
                throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorPackageNotValidated"));
            if (requireSelectedSession && _offlineSelectedSession == null)
                throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorSessionNotSelected"));
        }

        private void EnsureNoBlockingReceiptFlags(JsonElement receipt)
        {
            if (GetJsonBool(receipt, "jobInterrupted") || GetJsonBool(receipt, "attentionRequired") || GetReceiptState(receipt) == "failed")
            {
                string reason = GetJsonString(receipt, "message") ?? GetJsonString(receipt, "error") ?? _uiText.Format(LocalizePhase(GetReceiptState(receipt)));
                throw new InvalidOperationException(T("Dynamic.OfflineUpdate.ErrorReceiptFlagBlocked", reason));
            }
        }

        private static string? GetReceiptState(JsonElement? receipt) =>
            OfflineUpdateUiState.State(receipt);

        private static string? GetJsonString(JsonElement el, string prop)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out JsonElement val) && val.ValueKind == JsonValueKind.String)
                return val.GetString();
            return null;
        }

        private static bool GetJsonBool(JsonElement el, string prop)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out JsonElement val) && (val.ValueKind == JsonValueKind.True || val.ValueKind == JsonValueKind.False))
                return val.GetBoolean();
            return false;
        }

        private UiMessage LocalizeModelName(string model) => model.ToUpperInvariant() switch
        {
            "TB322FC" => M("Static.ModelName.Tb322fc"),
            "TB323FU" => M("Static.ModelName.Tb323fu"),
            "TB710FU" => M("Static.ModelName.Tb710fu"),
            _ => M("Dynamic.OfflineUpdate.Value", string.IsNullOrWhiteSpace(model) ? "?" : model)
        };

        private static string GetThreePartInstallerVersion()
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null)
                return "1.0.0";
            return $"{Math.Max(0, v.Major)}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";
        }

        private static string FormatJson(JsonElement el)
        {
            return JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = true });
        }

        private void UpdateDetailsWithReceipt(JsonElement receipt, string? error)
        {
            string json = FormatJson(receipt);
            SetText(OfflineUpgradePanel.DetailsText, "Dynamic.OfflineUpdate.Value", string.IsNullOrWhiteSpace(error) ? json : $"{error}{Environment.NewLine}{Environment.NewLine}{json}");
        }

        private async Task TryPersistReceiptAsync(OfflineUpdateSession? session, JsonElement receipt)
        {
            if (session == null)
                return;
            try
            {
                string recordsRoot = GetPersistentRecordsRoot();
                string sessionDir = Path.Combine(recordsRoot, session.SessionId);
                Directory.CreateDirectory(sessionDir);
                string finalPath = Path.Combine(sessionDir, "last-receipt.json");
                string tempPath = Path.Combine(sessionDir, $"last-receipt.{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(tempPath, FormatJson(receipt));
                File.Move(tempPath, finalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppendOfflineLog("Dynamic.OfflineUpdate.LogError", ex.Message);
            }
        }
    }
}
