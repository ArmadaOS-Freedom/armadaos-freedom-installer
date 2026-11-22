using System;
using System.Globalization;
using System.Text;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

/// <summary>
/// Workflow-context enrichment for the diagnostic summary only.
/// <para>
/// Everything here reads in-memory snapshot properties that other workflow files already hold.
/// No probe, no directory enumeration, no log copy, no evidence-directory access and no raw
/// serial / PnP path is emitted. Labels are stable so support can grep them. This file never
/// changes UI feedback, workflow gates or authorization. Values are appended as-is; the Core
/// bundle sanitizer redacts the complete summary afterwards, including multi-line details.
/// </para>
/// </summary>
public partial class MainWindow
{
    private void AppendWorkflowDiagnosticSummary(StringBuilder sb)
    {
        sb.AppendLine("SnapshotTimestampUtc: " + DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

        AppendFirstInstallDiagnosticSummary(sb);
        AppendPackageDiagnosticSummary(sb);
        AppendInternalInstallBackendDiagnosticSummary(sb);

        if (_showingOfflineUpgrade) AppendOfflineUpgradeDiagnosticSummary(sb);
    }

    /// <summary>
    /// Step 1 check / package / EDL / execute facts available in memory. Serial, device instance
    /// id and bus-reported strings are deliberately not serialized.
    /// </summary>
    private void AppendFirstInstallDiagnosticSummary(StringBuilder sb)
    {
        var check = _internalInstallDeviceCheck;
        if (check?.Succeeded != true
            && _diagnosticLastSuccessfulCheck is { } last
            && !string.IsNullOrWhiteSpace(_diagnosticLastDeviceSerial)
            && !string.IsNullOrWhiteSpace(last.Result.Android?.Identity.Serial)
            && string.Equals(
                last.Result.Android.Identity.Serial,
                _diagnosticLastDeviceSerial,
                StringComparison.Ordinal))
        {
            // 当前没有可确认结果：单独标注历史成功快照，时间戳与本轮快照严格区分。
            sb.AppendLine("HistoricalCheckSucceeded: true");
            sb.AppendLine(
                "HistoricalCheckObservedAtUtc: "
                + last.ObservedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(last.Result.FastbootProduct))
                sb.AppendLine("HistoricalCheckFastbootProduct: " + last.Result.FastbootProduct);
            if (!string.IsNullOrWhiteSpace(last.Result.CurrentSlot))
                sb.AppendLine("HistoricalCheckCurrentSlot: " + last.Result.CurrentSlot);
            sb.AppendLine("HistoricalCheckFastbootLockState: " + last.Result.FastbootLockState);

            var historicalFirmware = last.Result.Android?.Identity.Firmware;
            if (historicalFirmware is not null)
            {
                AppendFirmwareDiagnosticSummary(sb, "Historical", historicalFirmware);
                sb.AppendLine("HistoricalAndroidReportedBootloaderState: " + historicalFirmware.AndroidReportedBootloaderState);
            }
        }

        if (check is not null)
        {
            if (!string.IsNullOrWhiteSpace(check.FastbootProduct)) sb.AppendLine("DeviceCheckFastbootProduct: " + check.FastbootProduct);
            sb.AppendLine("DeviceCheckCancelled: " + (check.Cancelled ? "true" : "false"));
            sb.AppendLine("DeviceCheckSucceeded: " + (check.Succeeded ? "true" : "false"));
            if (!string.IsNullOrWhiteSpace(check.Error)) sb.AppendLine("DeviceCheckError: " + check.Error);

            var firmware = check.Android?.Identity.Firmware;
            if (firmware is not null)
            {
                AppendFirmwareDiagnosticSummary(sb, string.Empty, firmware);
                sb.AppendLine("AndroidReportedBootloaderState: " + firmware.AndroidReportedBootloaderState);
            }
        }

        if (_internalInstallPower is { } power)
        {
            if (power.Percentage is { } pct) sb.AppendLine("AndroidBatteryPercent: " + pct.ToString(CultureInfo.InvariantCulture));
            if (power.ExternalPower is { } ext) sb.AppendLine("AndroidExternalPower: " + (ext ? "true" : "false"));
            sb.AppendLine("AndroidPowerCapturedAtUtc: " + power.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }

        sb.AppendLine("EdlEntryAttempted: " + (_internalInstallEdlEntryAttempted ? "true" : "false"));
        if (_internalInstallEdlEntry is not null)
        {
            sb.AppendLine("EdlEntryConfirmed: true");
            var portName = _internalInstallEdlEntry.Endpoint?.PortName;
            if (!string.IsNullOrWhiteSpace(portName)) sb.AppendLine("EdlComPort: " + portName);
            sb.AppendLine("EdlOfflineOnly: " + (_internalInstallEdlEntry.OfflineIdentity is not null ? "true" : "false"));
        }
        else
        {
            sb.AppendLine("EdlEntryConfirmed: false");
        }

        sb.AppendLine("ExitResetAttempted: " + (_internalInstallExitResetAttempted ? "true" : "false"));
        sb.AppendLine("ExitConfirmed: " + (_internalInstallExitConfirmed ? "true" : "false"));
        sb.AppendLine("ExitActive: " + (_internalInstallExitActive ? "true" : "false"));
    }

    /// <summary>Firmware 字段的稳定标签输出；<paramref name="prefix"/> 为空或 "Historical"。</summary>
    private static void AppendFirmwareDiagnosticSummary(StringBuilder sb, string prefix, FirmwareSnapshot firmware)
    {
        if (!string.IsNullOrWhiteSpace(firmware.DisplayId)) sb.AppendLine(prefix + "AndroidDisplayId: " + firmware.DisplayId);
        if (!string.IsNullOrWhiteSpace(firmware.AndroidRelease)) sb.AppendLine(prefix + "AndroidRelease: " + firmware.AndroidRelease);
        if (!string.IsNullOrWhiteSpace(firmware.SecurityPatch)) sb.AppendLine(prefix + "AndroidSecurityPatch: " + firmware.SecurityPatch);
        if (!string.IsNullOrWhiteSpace(firmware.BuildFingerprint)) sb.AppendLine(prefix + "AndroidBuildFingerprint: " + firmware.BuildFingerprint);
    }

    /// <summary>Validated package facts already exposed by the importer (no whole-object dump).</summary>
    private void AppendPackageDiagnosticSummary(StringBuilder sb)
    {
        var validation = _internalInstallPackage?.Validation;
        if (validation is null) return;

        var model = _internalInstallPackage?.DeviceModel;
        if (!string.IsNullOrWhiteSpace(model)) sb.AppendLine("PackageDeviceModel: " + model);
        sb.AppendLine("PackageValid: " + (validation.IsValid ? "true" : "false"));
        sb.AppendLine("PackageTestOnly: " + (validation.TestOnly ? "true" : "false"));
        if (validation.DeploymentKind is { } kind) sb.AppendLine("PackageDeploymentKind: " + kind);
    }

    /// <summary>
    /// Internal install backend snapshot as last read into memory. Prefers the attempted backend
    /// so a stale reference cannot outlive the session that produced it.
    /// </summary>
    private void AppendInternalInstallBackendDiagnosticSummary(StringBuilder sb)
    {
        var backend = _internalInstallAttemptedBackend ?? _internalInstallBackend;
        if (backend is null) return;

        var snapshot = backend.Snapshot;
        sb.AppendLine("InstallBackendStage: " + snapshot.Stage);
        sb.AppendLine("InstallBackendWriteState: " + snapshot.WriteState);
        sb.AppendLine("InstallBackendOutcome: " + (snapshot.Outcome?.ToString() ?? "none"));
        sb.AppendLine("InstallBackendRequiredUserAction: " + snapshot.RequiredUserAction);
        sb.AppendLine("InstallBackendAndroidDataMayBeErased: " + (snapshot.AndroidDataMayBeErased ? "true" : "false"));

        var last = backend.LastResult;
        if (last is not null)
        {
            if (last.LastVerifiedStage is { } verified) sb.AppendLine("InstallLastVerifiedStage: " + verified);
            if (last.VerifiedSlots is { Count: > 0 } slots)
                sb.AppendLine("InstallLastVerifiedSlots: " + string.Join(",", slots));
            if (!string.IsNullOrWhiteSpace(last.ErrorCode)) sb.AppendLine("InstallLastErrorCode: " + last.ErrorCode);
            if (!string.IsNullOrWhiteSpace(last.Diagnostic)) sb.AppendLine("InstallLastDiagnostic: " + last.Diagnostic);
        }
    }

    /// <summary>
    /// Offline upgrade facts, only while the offline panel is showing. The receipt itself, credentials,
    /// endpoints and UUIDs are never serialized; only the descriptor digest and the final live flag.
    /// </summary>
    private void AppendOfflineUpgradeDiagnosticSummary(StringBuilder sb)
    {
        if (!string.IsNullOrWhiteSpace(_offlineConnectedModel)) sb.AppendLine("OfflineUpgradeModel: " + _offlineConnectedModel);
        if (!string.IsNullOrWhiteSpace(_offlineActualSlot)) sb.AppendLine("OfflineUpgradeObservedSlot: " + _offlineActualSlot);
        if (!string.IsNullOrWhiteSpace(_offlineDescriptorSha256)) sb.AppendLine("OfflineUpgradeDescriptorSha256: " + _offlineDescriptorSha256);
        sb.AppendLine("OfflineUpgradePreflightPassed: " + (_offlinePreflightPassed ? "true" : "false"));
        sb.AppendLine("OfflineUpgradeBootPolicySupported: " + (_offlineBootPolicySupported ? "true" : "false"));
        if (_offlinePreflightError != null) sb.AppendLine("OfflineUpgradePreflightError: " + _uiText.Format(_offlinePreflightError));
        if (!string.IsNullOrWhiteSpace(_offlineLastLoggedPhase)) sb.AppendLine("OfflineUpgradeLastLoggedPhase: " + _offlineLastLoggedPhase);
        if (!string.IsNullOrWhiteSpace(_offlineLastCompletionState)) sb.AppendLine("OfflineUpgradeLastCompletionState: " + _offlineLastCompletionState);
        sb.AppendLine("OfflineUpgradeReceiptLive: " + (_offlineReceiptLive ? "true" : "false"));
    }
}
