using System.Text.Json;

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
        /// The complete prepare gate. A connection and a validated package are
        /// required, but a session is not: the first upgrade of a tablet has no
        /// session yet and preparing is what creates it. Whether an existing
        /// selection is needed is decided by <see cref="CanPrepare"/> from the
        /// receipt and the saved-session count.
        /// </summary>
        internal static bool CanPrepareSession(bool connected, bool imported, JsonElement? receipt,
            bool hasMatchingSessions, bool hasSelectedSession) =>
            connected && imported &&
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

        internal static bool CanExecute(JsonElement? receipt, bool hasSavedRecovery) =>
            hasSavedRecovery && State(receipt) == "prepared" && !HasBlockingFlags(receipt);

        private static bool GetBool(JsonElement receipt, string property) =>
            receipt.TryGetProperty(property, out JsonElement value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) &&
            value.GetBoolean();
    }
}
