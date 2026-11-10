using System.Text.Json;
using FreedomInstaller.App;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Regression for the pure receipt-classification gate <see cref="OfflineUpdateUiState"/>
/// (real helper compiled into this assembly via csproj link; no WPF, no string asserts on UI).
/// </summary>
public class OfflineUpdateUiStateTests
{
    private static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement? Receipt(string? state, bool jobInterrupted = false, bool attentionRequired = false) =>
        state is null
            ? null
            : Parse($"{{\"state\":\"{state}\",\"jobInterrupted\":{JsonSerializer.Serialize(jobInterrupted)},\"attentionRequired\":{JsonSerializer.Serialize(attentionRequired)}}}");

    // ---------- State ----------

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"state\":5}")]
    [InlineData("{\"state\":\"\"}")]
    [InlineData("{\"result\":{\"state\":\"prepared\"}}")]
    [InlineData("[1,2,3]")]
    [InlineData("\"prepared\"")]
    public void State_NullOrMalformedReceipts_ReturnsNull(string? json)
    {
        Assert.Null(OfflineUpdateUiState.State(Parse(json)));
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("awaiting_recovery_export")]
    [InlineData("executing")]
    [InlineData("failed")]
    public void State_KnownStates_ReturnsValue(string state)
    {
        Assert.Equal(state, OfflineUpdateUiState.State(Receipt(state)));
    }

    // ---------- HasBlockingFlags ----------

    [Theory]
    [InlineData("{\"state\":\"failed\"}", true)]
    [InlineData("{\"state\":\"prepared\",\"jobInterrupted\":true}", true)]
    [InlineData("{\"state\":\"prepared\",\"attentionRequired\":true}", true)]
    [InlineData("{\"state\":\"prepared\",\"jobInterrupted\":false,\"attentionRequired\":false}", false)]
    [InlineData("{\"state\":\"prepared\",\"jobInterrupted\":\"yes\"}", false)]
    [InlineData("{}", false)]
    public void HasBlockingFlags_FlagCases(string json, bool expected)
    {
        Assert.Equal(expected, OfflineUpdateUiState.HasBlockingFlags(Parse(json)));
    }

    [Fact]
    public void HasBlockingFlags_NullReceipt_IsNotBlocking()
    {
        Assert.False(OfflineUpdateUiState.HasBlockingFlags(null));
    }

    // ---------- CanPrepare ----------

    [Fact]
    public void CanPrepare_InitialNoSessions_NullReceipt_CanPrepare()
    {
        Assert.True(OfflineUpdateUiState.CanPrepare(null, hasMatchingSessions: false, hasSelectedSession: false));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"state\":\"prepared\"}")]
    [InlineData("{\"state\":5}")]
    [InlineData("{\"result\":{\"state\":\"prepared\"}}")]
    public void CanPrepare_NoSessions_ButNonNullReceipt_RequiresRefresh(string json)
    {
        Assert.False(OfflineUpdateUiState.CanPrepare(Parse(json), hasMatchingSessions: false, hasSelectedSession: false));
    }

    [Fact]
    public void CanPrepare_MatchingSessions_Unselected_Denied()
    {
        Assert.False(OfflineUpdateUiState.CanPrepare(null, hasMatchingSessions: true, hasSelectedSession: false));
        Assert.False(OfflineUpdateUiState.CanPrepare(Receipt("prepared"), hasMatchingSessions: true, hasSelectedSession: false));
    }

    [Theory]
    [InlineData("uploading")]
    [InlineData("uploaded")]
    [InlineData("prepare_queued")]
    [InlineData("preparing")]
    [InlineData("prepared")]
    public void CanPrepare_AllKnownResumablePrepareStates_Allowed(string state)
    {
        Assert.True(OfflineUpdateUiState.CanPrepare(Receipt(state), hasMatchingSessions: true, hasSelectedSession: true));
    }

    [Theory]
    [InlineData("execute_queued")]
    [InlineData("executing")]
    [InlineData("awaiting_reboot")]
    [InlineData("awaiting_recovery_export")]
    [InlineData("up_to_date")]
    [InlineData("failed")]
    public void CanPrepare_WriteDeniedStates_Denied(string state)
    {
        Assert.False(OfflineUpdateUiState.CanPrepare(Receipt(state), hasMatchingSessions: true, hasSelectedSession: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CanPrepare_InterruptedOrAttention_DeniedEvenForPrepared(bool jobInterrupted, bool attentionRequired)
    {
        var receipt = Receipt("prepared", jobInterrupted, attentionRequired);
        Assert.False(OfflineUpdateUiState.CanPrepare(receipt, hasMatchingSessions: true, hasSelectedSession: true));
    }

    // ---------- CanExecute ----------

    [Fact]
    public void CanExecute_PreparedReceipt_TrueOnlyWithSavedRecovery()
    {
        var receipt = Receipt("prepared");
        Assert.False(OfflineUpdateUiState.CanExecute(receipt, hasSavedRecovery: false));
        Assert.True(OfflineUpdateUiState.CanExecute(receipt, hasSavedRecovery: true));
    }

    [Theory]
    [InlineData("awaiting_recovery_export")]
    [InlineData("executing")]
    [InlineData("execute_queued")]
    [InlineData("awaiting_reboot")]
    [InlineData("up_to_date")]
    [InlineData("failed")]
    [InlineData("uploading")]
    public void CanExecute_NonPreparedStates_DeniedEvenWithSavedRecovery(string state)
    {
        Assert.False(OfflineUpdateUiState.CanExecute(Receipt(state), hasSavedRecovery: true));
    }

    [Fact]
    public void CanExecute_TopPreparedWithResultAwaitingRecoveryExport_OnlyTopStateCounts()
    {
        // Only the top-level state drives the gate; a nested result must never enable execution.
        var nested = Parse("{\"state\":\"prepared\",\"result\":{\"state\":\"awaiting_recovery_export\"}}");
        Assert.True(OfflineUpdateUiState.CanExecute(nested, hasSavedRecovery: true));
        Assert.False(OfflineUpdateUiState.CanExecute(nested, hasSavedRecovery: false));

        var nestedOnly = Parse("{\"result\":{\"state\":\"prepared\"}}");
        Assert.False(OfflineUpdateUiState.CanExecute(nestedOnly, hasSavedRecovery: true));
    }

    [Theory]
    [InlineData("{\"state\":\"prepared\",\"jobInterrupted\":true}")]
    [InlineData("{\"state\":\"prepared\",\"attentionRequired\":true}")]
    public void CanExecute_BlockingFlags_DeniedEvenForPrepared(string json)
    {
        Assert.False(OfflineUpdateUiState.CanExecute(Parse(json), hasSavedRecovery: true));
    }

    [Fact]
    public void CanExecute_NullOrMalformedReceipt_Denied()
    {
        Assert.False(OfflineUpdateUiState.CanExecute(null, hasSavedRecovery: true));
        Assert.False(OfflineUpdateUiState.CanExecute(Parse("{}"), hasSavedRecovery: true));
        Assert.False(OfflineUpdateUiState.CanExecute(Parse("{\"state\":5}"), hasSavedRecovery: true));
    }

    [Fact]
    public void TopExecutingWithNestedPrepared_And_TopPreparedWithNestedAwaitingRecoveryExport_GatesCorrectly()
    {
        var executingWithNestedPrepared = Parse("{\"state\":\"executing\",\"result\":{\"state\":\"prepared\"}}");
        Assert.Equal("executing", OfflineUpdateUiState.State(executingWithNestedPrepared));
        Assert.False(OfflineUpdateUiState.CanPrepare(executingWithNestedPrepared, hasMatchingSessions: true, hasSelectedSession: true));
        Assert.False(OfflineUpdateUiState.CanExecute(executingWithNestedPrepared, hasSavedRecovery: true));

        var preparedWithNestedExport = Parse("{\"state\":\"prepared\",\"result\":{\"state\":\"awaiting_recovery_export\"}}");
        Assert.True(OfflineUpdateUiState.CanPrepare(preparedWithNestedExport, hasMatchingSessions: true, hasSelectedSession: true));
    }

    [Fact]
    public void MatchingAndSelected_NoReceiptOrMalformedOrUnknownState_DeniesGates()
    {
        Assert.False(OfflineUpdateUiState.CanPrepare(null, hasMatchingSessions: true, hasSelectedSession: true));
        Assert.False(OfflineUpdateUiState.CanExecute(null, hasSavedRecovery: true));

        var malformedNonString = Parse("{\"state\":[\"prepared\"]}");
        Assert.False(OfflineUpdateUiState.CanPrepare(malformedNonString, hasMatchingSessions: true, hasSelectedSession: true));
        Assert.False(OfflineUpdateUiState.CanExecute(malformedNonString, hasSavedRecovery: true));

        var unknownState = Parse("{\"state\":\"unknown_custom_state\"}");
        Assert.False(OfflineUpdateUiState.CanPrepare(unknownState, hasMatchingSessions: true, hasSelectedSession: true));
        Assert.False(OfflineUpdateUiState.CanExecute(unknownState, hasSavedRecovery: true));
    }

    // ---------- Completion ----------

    private static JsonElement? CompletionReceipt(
        string state,
        bool restartEligible,
        string? restartState = null,
        string completionState = "awaiting_reboot",
        bool jobInterrupted = false)
    {
        string restart = restartState is null ? "null" : JsonSerializer.Serialize(restartState);
        return Parse(
            $"{{\"state\":\"{state}\",\"jobInterrupted\":{JsonSerializer.Serialize(jobInterrupted)}," +
            $"\"completion\":{{\"schemaVersion\":1,\"state\":\"{completionState}\"," +
            $"\"restartEligible\":{JsonSerializer.Serialize(restartEligible)}," +
            $"\"restartState\":{restart}}}}}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"state\":\"awaiting_reboot\"}")]
    [InlineData("{\"state\":\"awaiting_reboot\",\"completion\":null}")]
    [InlineData("{\"state\":\"awaiting_reboot\",\"completion\":[]}")]
    [InlineData("{\"state\":\"awaiting_reboot\",\"completion\":\"confirmed\"}")]
    public void Completion_NullOrMalformed_ReturnsNull(string? json)
    {
        Assert.Null(OfflineUpdateUiState.Completion(Parse(json)));
        Assert.Null(OfflineUpdateUiState.CompletionState(Parse(json)));
        Assert.Null(OfflineUpdateUiState.RestartState(Parse(json)));
        Assert.False(OfflineUpdateUiState.IsCompletionConfirmed(Parse(json)));
    }

    [Theory]
    [InlineData("unverified")]
    [InlineData("mismatch")]
    [InlineData("awaiting_reboot")]
    [InlineData("awaiting_confirmation")]
    [InlineData("finalizing")]
    [InlineData("confirmed")]
    [InlineData("rolled_back")]
    [InlineData("failed")]
    public void Completion_States_AreReadVerbatim(string state)
    {
        var receipt = CompletionReceipt("awaiting_reboot", restartEligible: false, completionState: state);
        Assert.Equal(state, OfflineUpdateUiState.CompletionState(receipt));
        Assert.Equal(state == "confirmed", OfflineUpdateUiState.IsCompletionConfirmed(receipt));
    }

    [Theory]
    [InlineData("dispatching")]
    [InlineData("dispatched")]
    [InlineData("uncertain")]
    [InlineData("failed")]
    public void Completion_RestartState_IsReadVerbatim(string state)
    {
        var receipt = CompletionReceipt("awaiting_reboot", restartEligible: true, restartState: state);
        Assert.Equal(state, OfflineUpdateUiState.RestartState(receipt));
    }

    [Fact]
    public void CanRestartToVerify_EligibleAwaitingReboot_True()
    {
        var receipt = CompletionReceipt("awaiting_reboot", restartEligible: true);
        Assert.True(OfflineUpdateUiState.CanRestartToVerify(receipt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"state\":\"prepared\"}")]
    [InlineData("{\"state\":\"executing\"}")]
    [InlineData("{\"state\":\"failed\"}")]
    public void CanRestartToVerify_NullOrNonAwaitingReceipts_False(string? json)
    {
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(Parse(json)));
    }

    [Fact]
    public void CanRestartToVerify_RequiresCompletionBlock()
    {
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            Parse("{\"state\":\"awaiting_reboot\"}")));
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            Parse("{\"state\":\"awaiting_reboot\",\"completion\":{\"state\":\"awaiting_reboot\"}}")));
    }

    [Theory]
    [InlineData("awaiting_confirmation")]
    [InlineData("finalizing")]
    [InlineData("confirmed")]
    [InlineData("rolled_back")]
    [InlineData("failed")]
    [InlineData("mismatch")]
    [InlineData("unverified")]
    public void CanRestartToVerify_NonAwaitingCompletion_False(string completionState)
    {
        var receipt = CompletionReceipt("awaiting_reboot", restartEligible: true,
            completionState: completionState);
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(receipt));
    }

    [Fact]
    public void CanRestartToVerify_IneligibleOrInterruptedOrAlreadyDispatched_False()
    {
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: false)));
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: true, jobInterrupted: true)));
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: true, restartState: "dispatching")));
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: true, restartState: "dispatched")));
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: true, restartState: "uncertain")));
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: true, restartState: "pending")));
    }

    [Fact]
    public void CanRestartToVerify_OnlyAPreviouslyRejectedRequestMayBeRetried()
    {
        // A request the system explicitly rejected can be sent again by the
        // user; a pending or unknown request must never be re-issued.
        Assert.True(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: true, restartState: "failed")));
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: true, restartState: "uncertain")));
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: true, restartState: "dispatching")));
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            CompletionReceipt("awaiting_reboot", restartEligible: true, restartState: "dispatched")));
    }

    [Fact]
    public void CanRestartToVerify_NonBooleanEligibility_False()
    {
        Assert.False(OfflineUpdateUiState.CanRestartToVerify(
            Parse("{\"state\":\"awaiting_reboot\",\"completion\":{\"state\":\"awaiting_reboot\",\"restartEligible\":\"true\"}}")));
    }

    // ---------- Session-only refresh gates ----------

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void CanRefreshStatus_NeedsOnlyTheConnectionAndASelectedSession(
        bool connected, bool hasSelectedSession, bool expected)
    {
        // A saved result can be read after an app restart without importing or
        // re-validating the package.
        Assert.Equal(expected, OfflineUpdateUiState.CanRefreshStatus(connected, hasSelectedSession));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    public void CanMutateSession_StillRequiresTheValidatedPackage(
        bool connected, bool imported, bool hasSelectedSession, bool expected)
    {
        Assert.Equal(expected,
            OfflineUpdateUiState.CanMutateSession(connected, imported, hasSelectedSession));
    }

    // ---------- Combined prepare gate (exactly what the button uses) ----------

    [Fact]
    public void CanPrepareSession_FirstUpgrade_NeedsOnlyAConnectionAndAPackage()
    {
        // A tablet that has never been upgraded has no saved session and no
        // receipt yet; preparing is what creates the first session. Requiring a
        // selection here would disable Prepare forever on the first upgrade.
        Assert.True(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, receipt: null,
            hasMatchingSessions: false, hasSelectedSession: false));

        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: false, imported: true, receipt: null,
            hasMatchingSessions: false, hasSelectedSession: false));
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: false, receipt: null,
            hasMatchingSessions: false, hasSelectedSession: false));
    }

    [Fact]
    public void CanPrepareSession_ExistingSessionsWithoutSelection_AreAmbiguousAndBlocked()
    {
        // More than one saved session and no choice made: preparing could
        // target the wrong transaction, so the button stays disabled until the
        // user selects one.
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, receipt: Parse("{\"state\":\"uploaded\"}"),
            hasMatchingSessions: true, hasSelectedSession: false));

        Assert.True(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, receipt: Parse("{\"state\":\"uploaded\"}"),
            hasMatchingSessions: true, hasSelectedSession: true));
        Assert.True(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, receipt: Parse("{\"state\":\"prepare_queued\"}"),
            hasMatchingSessions: true, hasSelectedSession: true));
    }

    [Fact]
    public void CanPrepareSession_CompletedOrMalformedReceipt_IsBlocked()
    {
        // A finished upgrade is not preparable again.
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, receipt: Parse("{\"state\":\"awaiting_reboot\"}"),
            hasMatchingSessions: false, hasSelectedSession: false));
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, receipt: Parse("{\"state\":\"failed\"}"),
            hasMatchingSessions: false, hasSelectedSession: false));

        // A receipt nobody can interpret must be refreshed, not treated as
        // "no session yet".
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, receipt: Parse("{}"),
            hasMatchingSessions: false, hasSelectedSession: false));
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, receipt: Parse("[]"),
            hasMatchingSessions: false, hasSelectedSession: false));
    }
}
