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

            return RestartState(receipt) is null or "failed";
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
        /// The exact read-only preflight success contract. Only a v1 response
        /// that explicitly reports the passed phase with no boot write started
        /// unlocks Prepare. A legacy or malformed response, a device-side
        /// <c>operation_failed</c>, or an older updater that cannot preflight
        /// is never treated as a silent pass.
        /// </summary>
        internal static bool PreflightPassed(JsonElement? preflight)
        {
            if (!preflight.HasValue || preflight.Value.ValueKind != JsonValueKind.Object)
                return false;

            JsonElement value = preflight.Value;
            return GetInt32(value, "schemaVersion") == 1 &&
                   string.Equals(GetString(value, "phase"), "preflight_passed", StringComparison.Ordinal) &&
                   string.Equals(GetString(value, "bootWriteState"), "not_started", StringComparison.Ordinal);
        }

        /// <summary>
        /// The deployment reported by a successful preflight. The legacy
        /// <c>deployment</c> field never existed and must not be read.
        /// </summary>
        internal static string? PreflightDeployment(JsonElement? preflight) =>
            preflight.HasValue ? GetString(preflight.Value, "currentDeployment") : null;

        internal static string? PreflightPhase(JsonElement? preflight) =>
            preflight.HasValue ? GetString(preflight.Value, "phase") : null;

        private static int? GetInt32(JsonElement info, string name) =>
            info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number)
                ? number
                : null;

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

        internal static bool CanExecute(JsonElement? receipt, bool hasSavedRecovery, bool preflightPassed) =>
            preflightPassed && hasSavedRecovery && State(receipt) == "prepared" && !HasBlockingFlags(receipt);

        /// <summary>
        /// The tablet already finished preparing this session, so the only
        /// remaining local step is exporting the recovery bundle. Both the
        /// final <c>prepared</c> state and a receipt still reporting the
        /// export phase qualify; nothing here is allowed to prepare again.
        /// </summary>
        internal static bool NeedsRecoveryExport(JsonElement? receipt) =>
            !HasBlockingFlags(receipt) && State(receipt) is ("prepared" or "awaiting_recovery_export");

        /// <summary>
        /// The Plan action may export recovery only while no verified local
        /// bundle is held. Once a bundle exists, executing is what consumes
        /// its acknowledgement; a second export would replace nothing.
        /// </summary>
        internal static bool CanExportRecovery(JsonElement? receipt, bool hasSavedRecovery) =>
            !hasSavedRecovery && NeedsRecoveryExport(receipt);

        /// <summary>
        /// The recovery metadata a prepared receipt carries. It follows the
        /// Core precedence exactly: the <c>result</c> block first, then
        /// <c>transaction</c>; a present block must describe an
        /// <c>awaiting_recovery_export</c> transaction, and every present block
        /// must agree. A partial, foreign or contradictory receipt yields no
        /// context, so callers never build a recovery bundle from weak
        /// evidence.
        /// </summary>
        internal static bool TryGetRecoveryMetadata(JsonElement? receipt,
            out string? transactionId, out string? planSha256, out string? recoveryManifestSha256)
        {
            transactionId = null;
            planSha256 = null;
            recoveryManifestSha256 = null;
            if (!receipt.HasValue || receipt.Value.ValueKind != JsonValueKind.Object)
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
                if (string.IsNullOrWhiteSpace(tx) || string.IsNullOrWhiteSpace(plan) || string.IsNullOrWhiteSpace(manifest))
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

        private static bool GetBool(JsonElement receipt, string property) =>
            receipt.TryGetProperty(property, out JsonElement value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) &&
            value.GetBoolean();
    }
}
