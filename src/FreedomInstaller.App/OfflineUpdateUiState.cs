using System.Text.Json;
using FreedomInstaller.Core;

namespace FreedomInstaller.App
{
    internal static class OfflineUpdateUiState
    {
        internal static string? State(JsonElement? receipt)
        {
            if (!receipt.HasValue || receipt.Value.ValueKind != JsonValueKind.Object ||
                !receipt.Value.TryGetProperty("state", out JsonElement state) ||
                state.ValueKind != JsonValueKind.String)
                return null;

            string? value = state.GetString();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        internal static bool HasBlockingFlags(JsonElement? receipt)
        {
            if (!receipt.HasValue || receipt.Value.ValueKind != JsonValueKind.Object)
                return false;

            return GetBool(receipt.Value, "jobInterrupted") ||
                   GetBool(receipt.Value, "attentionRequired") ||
                   State(receipt) == "failed";
        }

        internal static JsonElement? Completion(JsonElement? receipt)
        {
            if (!receipt.HasValue || receipt.Value.ValueKind != JsonValueKind.Object ||
                !receipt.Value.TryGetProperty("completion", out JsonElement completion) ||
                completion.ValueKind != JsonValueKind.Object)
                return null;

            return completion;
        }

        internal static string? CompletionState(JsonElement? receipt)
        {
            JsonElement? completion = Completion(receipt);
            if (!completion.HasValue || !completion.Value.TryGetProperty("state", out JsonElement state) ||
                state.ValueKind != JsonValueKind.String)
                return null;

            string? value = state.GetString();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        internal static string? RestartState(JsonElement? receipt)
        {
            JsonElement? completion = Completion(receipt);
            if (!completion.HasValue ||
                !completion.Value.TryGetProperty("restartState", out JsonElement restart) ||
                restart.ValueKind != JsonValueKind.String)
                return null;

            string? value = restart.GetString();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// The restart action is offered only when the tablet itself bound this
        /// exact executed transaction to a prepared trial that has not booted
        /// into the new deployment yet. A request that was stored as
        /// dispatching, dispatched or uncertain may already have reached the
        /// firmware and is never re-issued; only an explicitly rejected request
        /// (failed) may be retried by the user.
        /// </summary>
        internal static bool CanRestartToVerify(JsonElement? receipt)
        {
            if (HasBlockingFlags(receipt) || State(receipt) != "awaiting_reboot")
                return false;

            JsonElement? completion = Completion(receipt);
            if (!completion.HasValue || CompletionState(receipt) != "awaiting_reboot")
                return false;
            if (!GetBool(completion.Value, "restartEligible"))
                return false;

            return IsRestartRetryable(completion.Value);
        }

        /// <summary>
        /// Only an absent or explicitly null restart record proves that the
        /// tablet never recorded a dispatch, and only the exact
        /// <c>failed</c> string proves the request was rejected. Any other
        /// raw value — another state, an empty string or a malformed type —
        /// may already have reached the firmware and must never be retried.
        /// </summary>
        private static bool IsRestartRetryable(JsonElement completion)
        {
            if (!completion.TryGetProperty("restartState", out JsonElement restart))
                return true;
            if (restart.ValueKind == JsonValueKind.Null)
                return true;

            return restart.ValueKind == JsonValueKind.String &&
                   string.Equals(restart.GetString(), "failed", StringComparison.Ordinal);
        }

        internal static bool IsCompletionConfirmed(JsonElement? receipt) =>
            CompletionState(receipt) == "confirmed";

        /// <summary>
        /// A 0.7 controller reports only a selected slot; it does not prove the
        /// running slot. The UI must describe the BOTH-slot upgrade instead of
        /// presenting selectedSlot as the actual slot.
        /// </summary>
        internal static bool IsLegacyController(JsonElement info) =>
            info.ValueKind == JsonValueKind.Object &&
            string.Equals(GetString(info, "bootContext"), "legacy-controller", StringComparison.Ordinal) &&
            GetString(info, "actualSlot") is null &&
            GetString(info, "inactiveSlot") is null &&
            GetString(info, "selectedSlot") is "a" or "b";

        internal static bool IsUpdaterVersionAtLeast(JsonElement info, string minimum)
        {
            var value = GetString(info, "updaterVersion");
            return Version.TryParse(value, out var parsed) &&
                   Version.TryParse(minimum, out var required) &&
                   parsed >= required;
        }

        /// <summary>
        /// The runtime capability that proves the connected offline updater
        /// accepts the optional <c>sessionId</c> on <c>preflight</c>. It is
        /// reported by the device service itself; a version string is not
        /// evidence, because a build can share a version and still lack the
        /// parameter.
        /// </summary>
        internal const string PreflightSessionCapability = "preflight-session-v1";

        /// <summary>
        /// True only when the device info advertises
        /// <see cref="PreflightSessionCapability"/> in a well-formed string
        /// array. A missing, non-array or mixed-type advertisement is treated
        /// as unsupported: the caller must never assume the capability.
        /// </summary>
        internal static bool SupportsPreflightSession(JsonElement info)
        {
            if (info.ValueKind != JsonValueKind.Object ||
                !info.TryGetProperty("capabilities", out JsonElement capabilities) ||
                capabilities.ValueKind != JsonValueKind.Array)
                return false;

            bool advertised = false;
            foreach (JsonElement entry in capabilities.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    return false;
                if (string.Equals(entry.GetString(), PreflightSessionCapability, StringComparison.Ordinal))
                    advertised = true;
            }

            return advertised;
        }

        /// <summary>
        /// The mutating upgrade contract: the device must advertise the exact
        /// <see cref="OfflineUpdateBootPolicy.NoSlotControl"/> string. It means
        /// both physical A/B candidates boot the same Linux deployment and the
        /// helper never writes slot metadata, selects a slot or marks one
        /// successful — Android alone owns the active/selected/successful slot.
        /// Read-only info, preflight and status do not require it, so an older
        /// helper stays inspectable while Prepare and Execute stay disabled.
        /// </summary>
        internal static bool SupportsNoSlotControlBootPolicy(JsonElement info) =>
            OfflineUpdateBootPolicy.IsNoSlotControl(info);

        /// <summary>
        /// The exact read-only preflight success contract, read through the
        /// same <see cref="OfflineUpdatePreflight"/> type the opt-in probe
        /// uses. Only a v1 response that explicitly reports the passed phase
        /// with no boot write started unlocks Prepare. A legacy or malformed
        /// response, a device-side <c>operation_failed</c>, or an older updater
        /// that cannot preflight is never treated as a silent pass.
        /// </summary>
        internal static bool PreflightPassed(JsonElement? preflight) =>
            OfflineUpdatePreflight.Passed(preflight);

        /// <summary>
        /// The deployment reported by a successful preflight. The legacy
        /// <c>deployment</c> field never existed and must not be read.
        /// </summary>
        internal static string? PreflightDeployment(JsonElement? preflight) =>
            OfflineUpdatePreflight.Deployment(preflight);

        internal static string? PreflightPhase(JsonElement? preflight) =>
            OfflineUpdatePreflight.Phase(preflight);

        private static string? GetString(JsonElement info, string name) =>
            info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>
        /// A session saved by an earlier run is bound to the device, so its
        /// status can be read with only a connection and a selected session.
        /// No package is needed for this read-only path.
        /// </summary>
        internal static bool CanRefreshStatus(bool connected, bool hasSelectedSession) =>
            connected && hasSelectedSession;

        /// <summary>
        /// Validation imports the package first and runs the read-only
        /// preflight second, so a failed preflight leaves the validated
        /// package imported while no session exists. Retrying that read-only
        /// step needs exactly the connection and the imported package: it
        /// creates no session and uploads, imports or prepares nothing.
        /// A device that already has saved sessions still requires a
        /// selection, which only <see cref="CanRefreshStatus"/> can satisfy.
        /// </summary>
        internal static bool CanRetryPreflightWithoutSession(
            bool connected, bool imported, bool hasSavedSessions) =>
            connected && imported && !hasSavedSessions;

        /// <summary>
        /// Preparing, saving recovery and executing still require a validated
        /// package in addition to the connection and the selected session.
        /// </summary>
        internal static bool CanMutateSession(bool connected, bool imported, bool hasSelectedSession) =>
            connected && imported && hasSelectedSession;

        /// <summary>
        /// The complete prepare gate. A connection, a validated package and a
        /// passed read-only preflight are required, but a session is not: the
        /// first upgrade of a tablet has no session yet and preparing is what
        /// creates it. Whether an existing selection is needed is decided by
        /// <see cref="CanPrepare"/> from the receipt and the saved-session
        /// count.
        /// The caller must also withhold this gate while the records location
        /// cannot be read: <paramref name="hasMatchingSessions"/> is false in
        /// that case too, and an unreadable store is not an empty device.
        /// </summary>
        internal static bool CanPrepareSession(bool connected, bool imported, bool preflightPassed,
            JsonElement? receipt, bool hasMatchingSessions, bool hasSelectedSession) =>
            connected && imported && preflightPassed &&
            CanPrepare(receipt, hasMatchingSessions, hasSelectedSession);

        internal static bool CanPrepare(JsonElement? receipt, bool hasMatchingSessions, bool hasSelectedSession)
        {
            if (HasBlockingFlags(receipt))
                return false;

            if (!hasMatchingSessions)
                // 只有从未收到过回执（初始状态）才允许直接建新会话；任意非空（含畸形）
                // 回执都不能当作「无会话可准备」，必须先刷新确认设备侧状态。
                return !receipt.HasValue;

            if (!hasSelectedSession)
                return false;

            return State(receipt) is "uploading" or "uploaded" or "prepare_queued" or "preparing" or "prepared";
        }

        /// <summary>
        /// The job operation the tablet recorded on a receipt. The device keeps
        /// it on the job result, so a terminal failure still names the
        /// operation that failed instead of only "failed".
        /// </summary>
        internal static string? JobOperation(JsonElement? receipt)
        {
            if (!receipt.HasValue || receipt.Value.ValueKind != JsonValueKind.Object)
                return null;

            string? value = GetString(receipt.Value, "jobOperation");
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>The session a receipt names, when it names one.</summary>
        internal static string? SessionId(JsonElement? receipt)
        {
            if (!receipt.HasValue || receipt.Value.ValueKind != JsonValueKind.Object)
                return null;

            string? value = GetString(receipt.Value, "sessionId");
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// True only for the one terminal receipt an explicit replacement
        /// session may follow: a failure the tablet recorded for the
        /// preparation job itself. The operation is named once, at the top
        /// level; the retained nested job result only repeats the failed state
        /// (the canonical producer stores <c>jobOperation</c> on the job and
        /// writes the worker result with just state and error). A nested
        /// result that does name an operation must still say prepare.
        /// Preparation verifies the package, checks free space and generates a
        /// fresh session id without writing a boot slot, so a failed
        /// preparation proves nothing was written — but only while every one of
        /// those facts is stated. A missing, malformed, interrupted or
        /// recovery/execute result is ambiguous: it never authorizes a new
        /// begin, a retry of the failed record, an execute or a restart.
        /// </summary>
        internal static bool IsVerifiedFailedPrepare(JsonElement? receipt)
        {
            if (!receipt.HasValue || receipt.Value.ValueKind != JsonValueKind.Object)
                return false;
            // This gate authorizes starting over, so a flag that is present in
            // any form other than an explicit false refutes it: a malformed
            // value is not evidence that the job finished cleanly.
            if (IsFlagRaised(receipt.Value, "jobInterrupted") ||
                IsFlagRaised(receipt.Value, "attentionRequired"))
                return false;
            if (State(receipt) != "failed" || !IsPrepareOperation(receipt.Value))
                return false;

            if (!receipt.Value.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Object)
                return false;
            if (IsFlagRaised(result, "jobInterrupted") || IsFlagRaised(result, "attentionRequired"))
                return false;
            if (!string.Equals(GetString(result, "state"), "failed", StringComparison.Ordinal))
                return false;

            // The canonical failed result carries no operation marker; only one
            // that is present must agree. Any other value names a different
            // operation and refutes the preparation claim.
            return !result.TryGetProperty("jobOperation", out _) || IsPrepareOperation(result);
        }

        private static bool IsPrepareOperation(JsonElement block) =>
            string.Equals(GetString(block, "jobOperation"), "prepare", StringComparison.Ordinal);

        private static bool IsFlagRaised(JsonElement block, string property) =>
            block.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.False;

        /// <summary>
        /// The selected record, the connected tablet, the imported package and
        /// the receipt must still name the same session before a replacement
        /// begin is offered or performed. A receipt read for another record,
        /// device or package never authorizes a new session, and the failed
        /// record itself is never retried or rewritten.
        /// </summary>
        internal static bool IsBoundForReplacement(JsonElement? receipt, OfflineUpdateSession? session,
            string? deviceModel, string? recoveryPartUuid, string? packageDescriptorSha256)
        {
            if (session == null || !IsVerifiedFailedPrepare(receipt))
                return false;
            if (!string.Equals(session.DeviceModel, deviceModel, StringComparison.Ordinal) ||
                !string.Equals(session.RecoveryPartUuid, recoveryPartUuid, StringComparison.Ordinal))
                return false;
            if (!string.IsNullOrEmpty(packageDescriptorSha256) &&
                !string.Equals(session.PackageDescriptorSha256, packageDescriptorSha256, StringComparison.Ordinal))
                return false;

            return string.Equals(SessionId(receipt), session.SessionId, StringComparison.Ordinal);
        }

        /// <summary>
        /// The complete replacement-begin gate. It needs the same live
        /// evidence as a first prepare — connection, validated package, passed
        /// read-only preflight — plus a readable records location, a receipt
        /// read live from this connection, and the fully verified failed
        /// preparation of the selected record. The caller still runs a fresh
        /// preflight and lets the tablet's begin verify the package, the free
        /// space and the new id before anything is prepared; the failed record
        /// stays on disk throughout.
        /// </summary>
        internal static bool CanStartReplacementSession(bool connected, bool imported, bool preflightPassed,
            bool catalogAvailable, bool receiptLive, bool hasSelectedSession, bool selectionBound) =>
            connected && imported && preflightPassed && catalogAvailable && receiptLive &&
            hasSelectedSession && selectionBound;

        internal static bool CanExecute(JsonElement? receipt, bool hasSavedRecovery, bool preflightPassed) =>
            preflightPassed && hasSavedRecovery && State(receipt) == "prepared" && !HasBlockingFlags(receipt);

        /// <summary>
        /// The tablet already finished preparing this session, so the only
        /// remaining local step is exporting the recovery bundle. Only the
        /// canonical top-level <c>prepared</c> state qualifies: the device maps
        /// its nested <c>awaiting_recovery_export</c> worker result into that
        /// state, so a receipt that reports the export phase at the top level
        /// is not a session receipt and is refused.
        /// </summary>
        internal static bool NeedsRecoveryExport(JsonElement? receipt) =>
            !HasBlockingFlags(receipt) && State(receipt) == "prepared";

        /// <summary>
        /// The receipt gate for offering recovery export: the tablet prepared
        /// this session, no verified local bundle is held, and the receipt
        /// carries the metadata Core
        /// <see cref="OfflineUpdateTransfer.SaveRecoveryAsync"/> will read. A
        /// prepared receipt whose export metadata is missing, partial,
        /// contradictory or unbound is refused here instead of failing later
        /// in Core.
        /// </summary>
        internal static bool CanExportRecovery(JsonElement? receipt, bool hasSavedRecovery) =>
            !hasSavedRecovery && NeedsRecoveryExport(receipt) &&
            TryGetRecoveryMetadata(receipt, out _, out _, out _);

        /// <summary>
        /// The complete export gate behind the Plan button. Recovery export is
        /// read-only, so the write-side boot policy is deliberately absent: a
        /// helper that never declared <see cref="OfflineUpdateBootPolicy.NoSlotControl"/>
        /// still exports the bundle its prepared transaction already holds.
        /// The connection, the imported package, the read-only preflight pass
        /// and the selected session stay required, and a verified bundle
        /// already on disk is never replaced.
        /// </summary>
        internal static bool CanExportRecoverySession(bool connected, bool imported, bool preflightPassed,
            bool hasSelectedSession, JsonElement? receipt, bool hasSavedRecovery) =>
            connected && imported && preflightPassed && hasSelectedSession &&
            CanExportRecovery(receipt, hasSavedRecovery);

        /// <summary>
        /// The receipt states whose read-only preflight is worth refreshing.
        /// The export phase is not a top-level state: the device maps the
        /// nested <c>awaiting_recovery_export</c> worker result into a
        /// <c>prepared</c> receipt, so <c>prepared</c> alone keeps the pass for
        /// a session that still only needs the export.
        /// </summary>
        internal static bool ShouldRefreshPreflight(string? state) =>
            state is "uploading" or "uploaded" or "prepare_queued" or "preparing" or
                "prepared" or "up_to_date";

        /// <summary>
        /// The recovery metadata a prepared receipt carries. It follows the
        /// canonical wire and the Core precedence exactly: the receipt itself
        /// must be <c>prepared</c> and the IDs/hash live only inside the
        /// nested <c>result</c> and <c>transaction</c> blocks; a present block
        /// must describe an <c>awaiting_recovery_export</c> transaction, and
        /// every present block must agree. The triple must also be canonical,
        /// as <see cref="OfflineUpdateTransfer.SaveRecoveryAsync"/> requires,
        /// and root-level export fields are unbound and never read. A partial,
        /// foreign, contradictory or naked receipt yields no context, so
        /// callers never build a recovery bundle from weak evidence.
        /// </summary>
        internal static bool TryGetRecoveryMetadata(JsonElement? receipt,
            out string? transactionId, out string? planSha256, out string? recoveryManifestSha256)
        {
            transactionId = null;
            planSha256 = null;
            recoveryManifestSha256 = null;
            if (!receipt.HasValue || receipt.Value.ValueKind != JsonValueKind.Object ||
                State(receipt) != "prepared")
                return false;

            bool found = false;
            foreach (string name in new[] { "result", "transaction" })
            {
                if (!receipt.Value.TryGetProperty(name, out JsonElement block))
                    continue;
                if (block.ValueKind != JsonValueKind.Object ||
                    !string.Equals(GetString(block, "state"), "awaiting_recovery_export", StringComparison.Ordinal))
                    return false;

                string? tx = GetString(block, "transactionId");
                string? plan = GetString(block, "planSha256");
                string? manifest = GetString(block, "recoveryManifestSha256");
                if (!IsCanonicalTransactionId(tx) || !IsSha256(plan) || !IsSha256(manifest))
                    return false;

                if (found &&
                    (!string.Equals(tx, transactionId, StringComparison.Ordinal) ||
                     !string.Equals(plan, planSha256, StringComparison.Ordinal) ||
                     !string.Equals(manifest, recoveryManifestSha256, StringComparison.Ordinal)))
                    return false;

                transactionId = tx;
                planSha256 = plan;
                recoveryManifestSha256 = manifest;
                found = true;
            }

            return found;
        }

        // The Core receipt validators accept only a non-zero lowercase canonical
        // GUID and 64 lowercase hex characters; the UI gate must not admit
        // metadata that SaveRecoveryAsync would then reject.
        private static bool IsCanonicalTransactionId(string? value) =>
            value is not null &&
            Guid.TryParseExact(value, "D", out Guid parsed) &&
            parsed != Guid.Empty &&
            string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

        private static bool IsSha256(string? value) =>
            value is { Length: 64 } &&
            value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static bool GetBool(JsonElement receipt, string property) =>
            receipt.TryGetProperty(property, out JsonElement value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) &&
            value.GetBoolean();
    }
}
