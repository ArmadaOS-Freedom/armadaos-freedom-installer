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
        Assert.False(OfflineUpdateUiState.CanExecute(receipt, hasSavedRecovery: false, preflightPassed: true));
        Assert.True(OfflineUpdateUiState.CanExecute(receipt, hasSavedRecovery: true, preflightPassed: true));
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
        Assert.False(OfflineUpdateUiState.CanExecute(Receipt(state), hasSavedRecovery: true, preflightPassed: true));
    }

    [Fact]
    public void CanExecute_TopPreparedWithResultAwaitingRecoveryExport_OnlyTopStateCounts()
    {
        // Only the top-level state drives the gate; a nested result must never enable execution.
        var nested = Parse("{\"state\":\"prepared\",\"result\":{\"state\":\"awaiting_recovery_export\"}}");
        Assert.True(OfflineUpdateUiState.CanExecute(nested, hasSavedRecovery: true, preflightPassed: true));
        Assert.False(OfflineUpdateUiState.CanExecute(nested, hasSavedRecovery: false, preflightPassed: true));

        var nestedOnly = Parse("{\"result\":{\"state\":\"prepared\"}}");
        Assert.False(OfflineUpdateUiState.CanExecute(nestedOnly, hasSavedRecovery: true, preflightPassed: true));
    }

    [Theory]
    [InlineData("{\"state\":\"prepared\",\"jobInterrupted\":true}")]
    [InlineData("{\"state\":\"prepared\",\"attentionRequired\":true}")]
    public void CanExecute_BlockingFlags_DeniedEvenForPrepared(string json)
    {
        Assert.False(OfflineUpdateUiState.CanExecute(Parse(json), hasSavedRecovery: true, preflightPassed: true));
    }

    [Fact]
    public void CanExecute_NullOrMalformedReceipt_Denied()
    {
        Assert.False(OfflineUpdateUiState.CanExecute(null, hasSavedRecovery: true, preflightPassed: true));
        Assert.False(OfflineUpdateUiState.CanExecute(Parse("{}"), hasSavedRecovery: true, preflightPassed: true));
        Assert.False(OfflineUpdateUiState.CanExecute(Parse("{\"state\":5}"), hasSavedRecovery: true, preflightPassed: true));
    }

    [Fact]
    public void TopExecutingWithNestedPrepared_And_TopPreparedWithNestedAwaitingRecoveryExport_GatesCorrectly()
    {
        var executingWithNestedPrepared = Parse("{\"state\":\"executing\",\"result\":{\"state\":\"prepared\"}}");
        Assert.Equal("executing", OfflineUpdateUiState.State(executingWithNestedPrepared));
        Assert.False(OfflineUpdateUiState.CanPrepare(executingWithNestedPrepared, hasMatchingSessions: true, hasSelectedSession: true));
        Assert.False(OfflineUpdateUiState.CanExecute(executingWithNestedPrepared, hasSavedRecovery: true, preflightPassed: true));

        var preparedWithNestedExport = Parse("{\"state\":\"prepared\",\"result\":{\"state\":\"awaiting_recovery_export\"}}");
        Assert.True(OfflineUpdateUiState.CanPrepare(preparedWithNestedExport, hasMatchingSessions: true, hasSelectedSession: true));
    }

    [Fact]
    public void MatchingAndSelected_NoReceiptOrMalformedOrUnknownState_DeniesGates()
    {
        Assert.False(OfflineUpdateUiState.CanPrepare(null, hasMatchingSessions: true, hasSelectedSession: true));
        Assert.False(OfflineUpdateUiState.CanExecute(null, hasSavedRecovery: true, preflightPassed: true));

        var malformedNonString = Parse("{\"state\":[\"prepared\"]}");
        Assert.False(OfflineUpdateUiState.CanPrepare(malformedNonString, hasMatchingSessions: true, hasSelectedSession: true));
        Assert.False(OfflineUpdateUiState.CanExecute(malformedNonString, hasSavedRecovery: true, preflightPassed: true));

        var unknownState = Parse("{\"state\":\"unknown_custom_state\"}");
        Assert.False(OfflineUpdateUiState.CanPrepare(unknownState, hasMatchingSessions: true, hasSelectedSession: true));
        Assert.False(OfflineUpdateUiState.CanExecute(unknownState, hasSavedRecovery: true, preflightPassed: true));
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
    public void CanPrepareSession_FirstUpgrade_NeedsConnectionPackageAndPreflight()
    {
        // A tablet that has never been upgraded has no saved session and no
        // receipt yet; preparing is what creates the first session. Requiring a
        // selection here would disable Prepare forever on the first upgrade.
        // The read-only preflight pass is still required.
        Assert.True(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: true, receipt: null,
            hasMatchingSessions: false, hasSelectedSession: false));

        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: false, imported: true, preflightPassed: true, receipt: null,
            hasMatchingSessions: false, hasSelectedSession: false));
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: false, preflightPassed: true, receipt: null,
            hasMatchingSessions: false, hasSelectedSession: false));
    }

    [Fact]
    public void CanPrepareSession_ExistingSessionsWithoutSelection_AreAmbiguousAndBlocked()
    {
        // More than one saved session and no choice made: preparing could
        // target the wrong transaction, so the button stays disabled until the
        // user selects one.
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: true, receipt: Parse("{\"state\":\"uploaded\"}"),
            hasMatchingSessions: true, hasSelectedSession: false));

        Assert.True(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: true, receipt: Parse("{\"state\":\"uploaded\"}"),
            hasMatchingSessions: true, hasSelectedSession: true));
        Assert.True(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: true, receipt: Parse("{\"state\":\"prepare_queued\"}"),
            hasMatchingSessions: true, hasSelectedSession: true));
    }

    [Fact]
    public void CanPrepareSession_CompletedOrMalformedReceipt_IsBlocked()
    {
        // A finished upgrade is not preparable again.
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: true, receipt: Parse("{\"state\":\"awaiting_reboot\"}"),
            hasMatchingSessions: false, hasSelectedSession: false));
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: true, receipt: Parse("{\"state\":\"failed\"}"),
            hasMatchingSessions: false, hasSelectedSession: false));

        // A receipt nobody can interpret must be refreshed, not treated as
        // "no session yet".
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: true, receipt: Parse("{}"),
            hasMatchingSessions: false, hasSelectedSession: false));
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: true, receipt: Parse("[]"),
            hasMatchingSessions: false, hasSelectedSession: false));
    }

    [Theory]
    [InlineData("{\"bootContext\":\"legacy-controller\",\"actualSlot\":null,\"inactiveSlot\":null,\"selectedSlot\":\"b\"}", true)]
    [InlineData("{\"bootContext\":\"legacy-controller\",\"actualSlot\":\"a\",\"inactiveSlot\":\"b\",\"selectedSlot\":\"b\"}", false)]
    [InlineData("{\"actualSlot\":\"a\",\"inactiveSlot\":\"b\"}", false)]
    [InlineData("{\"bootContext\":\"legacy-controller\",\"actualSlot\":null,\"inactiveSlot\":null}", false)]
    public void LegacyController_RequiresTheExactNullSlotShape(string json, bool expected)
    {
        Assert.Equal(expected, OfflineUpdateUiState.IsLegacyController(Parse(json)!.Value));
    }

    [Theory]
    [InlineData("0.8.0", true)]
    [InlineData("0.8.1", true)]
    [InlineData("0.7.9", false)]
    [InlineData("not-a-version", false)]
    public void UpdaterVersion_ComparisonIsHonest(string version, bool expected)
    {
        var info = Parse("{\"updaterVersion\":\"" + version + "\"}")!.Value;
        Assert.Equal(expected, OfflineUpdateUiState.IsUpdaterVersionAtLeast(info, "0.8.0"));
    }

    // ---------- Runtime capability gate ----------

    [Fact]
    public void PreflightSessionCapability_AdvertisedByTheUpdater_IsAccepted()
    {
        var info = Parse("{\"updaterVersion\":\"0.8.0\",\"capabilities\":[\"preflight-session-v1\"]}")!.Value;
        Assert.True(OfflineUpdateUiState.SupportsPreflightSession(info));

        // Unknown future capabilities are ignored rather than rejected.
        var withExtras = Parse("{\"capabilities\":[\"something-new\",\"preflight-session-v1\"]}")!.Value;
        Assert.True(OfflineUpdateUiState.SupportsPreflightSession(withExtras));
    }

    [Fact]
    public void SameVersionWithoutTheCapability_IsRejectedEvenThoughTheVersionPasses()
    {
        // A 0.8 updater can lack the optional preflight session parameter, so
        // the version string alone must never be accepted as proof.
        var info = Parse("{\"updaterVersion\":\"0.8.0\"}")!.Value;
        Assert.True(OfflineUpdateUiState.IsUpdaterVersionAtLeast(info, "0.8.0"));
        Assert.False(OfflineUpdateUiState.SupportsPreflightSession(info));

        var differentCapability =
            Parse("{\"updaterVersion\":\"0.8.0\",\"capabilities\":[\"preflight-session-v2\"]}")!.Value;
        Assert.False(OfflineUpdateUiState.SupportsPreflightSession(differentCapability));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"capabilities\":[]}")]
    [InlineData("{\"capabilities\":null}")]
    [InlineData("{\"capabilities\":\"preflight-session-v1\"}")]
    [InlineData("{\"capabilities\":{\"preflight-session-v1\":true}}")]
    [InlineData("{\"capabilities\":[1]}")]
    [InlineData("{\"capabilities\":[null]}")]
    [InlineData("{\"capabilities\":[\"preflight-session-v1\",5]}")]
    [InlineData("{\"capabilities\":[\"preflight-session-v1 \"]}")]
    [InlineData("{\"capabilities\":[\"PREFLIGHT-SESSION-V1\"]}")]
    [InlineData("[]")]
    [InlineData("\"preflight-session-v1\"")]
    public void MissingOrMalformedCapabilityAdvertisement_IsRejected(string json)
    {
        Assert.False(OfflineUpdateUiState.SupportsPreflightSession(Parse(json)!.Value));
    }

    // ---------- Mutating boot policy gate ----------

    [Theory]
    [InlineData("{\"bootPolicy\":\"dual-boot-no-slot-control\"}", true)]
    [InlineData("{\"bootPolicy\":\"dual-boot-slot-control\"}", false)]
    [InlineData("{\"bootPolicy\":\"Dual-Boot-No-Slot-Control\"}", false)]
    [InlineData("{\"bootPolicy\":\"dual-boot-no-slot-control \"}", false)]
    [InlineData("{\"bootPolicy\":\"\"}", false)]
    [InlineData("{\"bootPolicy\":5}", false)]
    [InlineData("{\"bootPolicy\":null}", false)]
    [InlineData("{\"bootPolicy\":[\"dual-boot-no-slot-control\"]}", false)]
    [InlineData("{\"capabilities\":[\"preflight-session-v1\"]}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    public void BootPolicy_RequiresTheExactNoSlotControlString(string json, bool expected)
    {
        Assert.Equal(expected, OfflineUpdateUiState.SupportsNoSlotControlBootPolicy(Parse(json)!.Value));
    }

    // ---------- Read-only preflight gate ----------

    private const string ExactPreflight =
        "{\"schemaVersion\":1," +
        "\"phase\":\"preflight_passed\"," +
        "\"bootWriteState\":\"not_started\"," +
        "\"model\":\"TB323FU\"," +
        "\"bootContext\":\"uefi\"," +
        "\"actualSlot\":\"a\"," +
        "\"selectedSlot\":\"b\"," +
        "\"currentDeployment\":\"deployment-2026.11\"," +
        "\"recoveryPartUuid\":\"11111111-2222-3333-4444-555555555555\"," +
        "\"recoveryDescriptorVersion\":2," +
        "\"recoveryMigrationRequired\":false," +
        "\"channel\":\"stable\"}";

    [Fact]
    public void PreflightPassed_ExactSuccessSchema_IsARecordedPass()
    {
        var preflight = Parse(ExactPreflight);

        Assert.True(OfflineUpdateUiState.PreflightPassed(preflight));
        Assert.Equal("deployment-2026.11", OfflineUpdateUiState.PreflightDeployment(preflight));
        Assert.Equal("preflight_passed", OfflineUpdateUiState.PreflightPhase(preflight));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("\"preflight_passed\"")]
    [InlineData("{}")]
    // A legacy success shape must never count as a pass.
    [InlineData("{\"deployment\":\"A\",\"state\":\"preflight_passed\"}")]
    [InlineData("{\"deployment\":\"A\",\"status\":\"preflight_passed\"}")]
    // Schema version is part of the contract.
    [InlineData("{\"schemaVersion\":2,\"phase\":\"preflight_passed\",\"bootWriteState\":\"not_started\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"phase\":\"preflight_passed\",\"bootWriteState\":\"not_started\"}")]
    // Both the phase and the no-write proof are required.
    [InlineData("{\"schemaVersion\":1,\"phase\":\"preflight_failed\",\"bootWriteState\":\"not_started\"}")]
    [InlineData("{\"schemaVersion\":1,\"phase\":\"preflight_passed\"}")]
    [InlineData("{\"schemaVersion\":1,\"phase\":\"preflight_passed\",\"bootWriteState\":\"started\"}")]
    public void PreflightPassed_MalformedOrForeignShapes_AreNotAPass(string? json)
    {
        Assert.False(OfflineUpdateUiState.PreflightPassed(Parse(json)));
    }

    [Fact]
    public void PreflightFields_UseCurrentDeploymentAndPhaseOnly()
    {
        var legacy = Parse("{\"deployment\":\"A\",\"state\":\"preflight_passed\",\"status\":\"preflight_passed\"}");
        Assert.Null(OfflineUpdateUiState.PreflightDeployment(legacy));
        Assert.Null(OfflineUpdateUiState.PreflightPhase(legacy));

        var wrongTypes = Parse("{\"currentDeployment\":5,\"phase\":true}");
        Assert.Null(OfflineUpdateUiState.PreflightDeployment(wrongTypes));
        Assert.Null(OfflineUpdateUiState.PreflightPhase(wrongTypes));
    }

    [Fact]
    public void CanPrepareSession_FailedOrResetPreflight_BlocksPrepare()
    {
        // A connection and a validated package are not enough: the preflight
        // pass is bound to this package/device and may have been reset.
        Assert.False(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: false, receipt: null,
            hasMatchingSessions: false, hasSelectedSession: false));

        Assert.True(OfflineUpdateUiState.CanPrepareSession(
            connected: true, imported: true, preflightPassed: true, receipt: null,
            hasMatchingSessions: false, hasSelectedSession: false));
    }

    [Fact]
    public void CanExecute_WithoutPassedPreflight_BlocksExecution()
    {
        var prepared = Receipt("prepared");

        Assert.False(OfflineUpdateUiState.CanExecute(
            prepared, hasSavedRecovery: true, preflightPassed: false));
        Assert.True(OfflineUpdateUiState.CanExecute(
            prepared, hasSavedRecovery: true, preflightPassed: true));
    }

    // ---------- Recovery export routing (the Plan action) ----------

    [Theory]
    [InlineData("prepared")]
    [InlineData("awaiting_recovery_export")]
    public void CanExportRecovery_AlreadyPreparedSessionNeedsOnlyTheLocalBundle(string state)
    {
        // The tablet finished preparing, so the Plan action must reach the
        // recovery export without another PrepareAsync.
        Assert.True(OfflineUpdateUiState.NeedsRecoveryExport(Receipt(state)));
        Assert.True(OfflineUpdateUiState.CanExportRecovery(Receipt(state), hasSavedRecovery: false));

        // A verified acknowledgement is already held: exporting again is not
        // the remaining step, executing is.
        Assert.False(OfflineUpdateUiState.CanExportRecovery(Receipt(state), hasSavedRecovery: true));
    }

    [Theory]
    [InlineData("uploading")]
    [InlineData("uploaded")]
    [InlineData("prepare_queued")]
    [InlineData("preparing")]
    [InlineData("execute_queued")]
    [InlineData("executing")]
    [InlineData("awaiting_reboot")]
    [InlineData("up_to_date")]
    [InlineData("failed")]
    [InlineData("unknown_custom_state")]
    public void CanExportRecovery_NeverForOtherStates(string state)
    {
        Assert.False(OfflineUpdateUiState.NeedsRecoveryExport(Receipt(state)));
        Assert.False(OfflineUpdateUiState.CanExportRecovery(Receipt(state), hasSavedRecovery: false));
    }

    [Fact]
    public void CanExportRecovery_BlockedOrMissingReceipt_IsDenied()
    {
        Assert.False(OfflineUpdateUiState.CanExportRecovery(null, hasSavedRecovery: false));
        Assert.False(OfflineUpdateUiState.CanExportRecovery(Parse("{}"), hasSavedRecovery: false));
        Assert.False(OfflineUpdateUiState.CanExportRecovery(Parse("{\"state\":5}"), hasSavedRecovery: false));
        Assert.False(OfflineUpdateUiState.NeedsRecoveryExport(Receipt("prepared", jobInterrupted: true)));
        Assert.False(OfflineUpdateUiState.CanExportRecovery(
            Receipt("prepared", attentionRequired: true), hasSavedRecovery: false));
    }

    [Fact]
    public void CanExportRecovery_TopPreparedWithNestedExport_StillExports()
    {
        // The export phase lives in the nested result while the top-level
        // state is already prepared; both must route to the export.
        var receipt = Parse("{\"state\":\"prepared\",\"result\":{\"state\":\"awaiting_recovery_export\"}}");
        Assert.True(OfflineUpdateUiState.CanExportRecovery(receipt, hasSavedRecovery: false));
        Assert.False(OfflineUpdateUiState.CanExportRecovery(receipt, hasSavedRecovery: true));
    }

    // ---------- Read-only export gate (the button) ----------

    [Theory]
    [InlineData("{\"state\":\"prepared\"}")]
    [InlineData("{\"state\":\"awaiting_recovery_export\"}")]
    [InlineData("{\"state\":\"prepared\",\"result\":{\"state\":\"awaiting_recovery_export\"}}")]
    public void CanExportRecoverySession_DoesNotNeedTheWriteBootPolicy(string json)
    {
        // Recovery export is read-only: a helper that never declared
        // dual-boot-no-slot-control may still export the bundle its prepared
        // transaction already holds. The gate therefore has no boot-policy
        // input at all, while a real prepare/execute still needs it.
        var info = Parse("{\"capabilities\":[\"preflight-session-v1\"],\"bootPolicy\":\"dual-boot-slot-control\"}")!.Value;
        Assert.False(OfflineUpdateUiState.SupportsNoSlotControlBootPolicy(info));

        Assert.True(OfflineUpdateUiState.CanExportRecoverySession(
            connected: true, imported: true, preflightPassed: true, hasSelectedSession: true,
            receipt: Parse(json), hasSavedRecovery: false));
    }

    [Theory]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, true, true, true)]
    public void CanExportRecoverySession_RequiresEveryLiveGateAndNoHeldBundle(
        bool connected, bool imported, bool preflightPassed, bool hasSelectedSession, bool hasSavedRecovery)
    {
        Assert.False(OfflineUpdateUiState.CanExportRecoverySession(
            connected, imported, preflightPassed, hasSelectedSession,
            receipt: Receipt("prepared"), hasSavedRecovery: hasSavedRecovery));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("uploading")]
    [InlineData("preparing")]
    [InlineData("execute_queued")]
    [InlineData("executing")]
    [InlineData("awaiting_reboot")]
    [InlineData("failed")]
    [InlineData("unknown_custom_state")]
    public void CanExportRecoverySession_NeverOpensTheMutatingStates(string? state)
    {
        Assert.False(OfflineUpdateUiState.CanExportRecoverySession(
            connected: true, imported: true, preflightPassed: true, hasSelectedSession: true,
            receipt: state is null ? null : Receipt(state), hasSavedRecovery: false));
    }

    // ---------- Read-only preflight refresh ----------

    [Theory]
    [InlineData("uploading", true)]
    [InlineData("uploaded", true)]
    [InlineData("prepare_queued", true)]
    [InlineData("preparing", true)]
    [InlineData("prepared", true)]
    // The export phase may be the top-level state, exactly as it appears
    // nested in a prepared receipt's result.
    [InlineData("awaiting_recovery_export", true)]
    [InlineData("up_to_date", true)]
    [InlineData("execute_queued", false)]
    [InlineData("executing", false)]
    [InlineData("awaiting_reboot", false)]
    [InlineData("failed", false)]
    [InlineData("unknown_custom_state", false)]
    [InlineData(null, false)]
    public void ShouldRefreshPreflight_CoversTheExportPhaseInBothForms(string? state, bool expected)
    {
        Assert.Equal(expected, OfflineUpdateUiState.ShouldRefreshPreflight(state));
    }

    // ---------- Recovery metadata precedence (must match Core) ----------

    private const string TxId = "123e4567-e89b-12d3-a456-426614174000";
    private static readonly string PlanSha = new('a', 64);
    private static readonly string ManifestSha = new('b', 64);
    private static readonly string OtherManifestSha = new('c', 64);

    private static string ExportBlock(string tx = TxId, string? plan = null, string? manifest = null) =>
        "{\"state\":\"awaiting_recovery_export\"," +
        $"\"transactionId\":\"{tx}\"," +
        $"\"planSha256\":\"{plan ?? PlanSha}\"," +
        $"\"recoveryManifestSha256\":\"{manifest ?? ManifestSha}\"}}";

    [Fact]
    public void RecoveryMetadata_ResultAndTransactionAreBothRequiredAndMustAgree()
    {
        var both = Parse(
            $"{{\"state\":\"prepared\",\"result\":{ExportBlock()},\"transaction\":{ExportBlock()}}}");
        Assert.True(OfflineUpdateUiState.TryGetRecoveryMetadata(both,
            out string? tx, out string? plan, out string? manifest));
        Assert.Equal(TxId, tx);
        Assert.Equal(PlanSha, plan);
        Assert.Equal(ManifestSha, manifest);

        // Same precedence as the Core validator: two blocks that disagree are
        // contradictory evidence, never "last one wins".
        var conflict = Parse(
            $"{{\"state\":\"prepared\",\"result\":{ExportBlock()}," +
            $"\"transaction\":{ExportBlock(manifest: OtherManifestSha)}}}");
        Assert.False(OfflineUpdateUiState.TryGetRecoveryMetadata(conflict, out _, out _, out _));
    }

    [Fact]
    public void RecoveryMetadata_TransactionOnlyIsAccepted_TopLevelFieldsAreNot()
    {
        var transactionOnly = Parse($"{{\"state\":\"prepared\",\"transaction\":{ExportBlock()}}}");
        Assert.True(OfflineUpdateUiState.TryGetRecoveryMetadata(transactionOnly, out _, out _, out _));

        // The Core never reads these fields from the receipt root, so a
        // receipt carrying them there must not name local recovery bytes.
        var topLevelOnly = Parse(
            $"{{\"state\":\"prepared\",\"transactionId\":\"{TxId}\"," +
            $"\"planSha256\":\"{PlanSha}\",\"recoveryManifestSha256\":\"{ManifestSha}\"}}");
        Assert.False(OfflineUpdateUiState.TryGetRecoveryMetadata(topLevelOnly, out _, out _, out _));
    }

    [Theory]
    // A present block that is not an export transaction is not this metadata.
    [InlineData("{\"state\":\"prepared\",\"result\":{\"state\":\"prepared\",\"transactionId\":\"123e4567-e89b-12d3-a456-426614174000\",\"planSha256\":\"PLAN\",\"recoveryManifestSha256\":\"MANIFEST\"}}")]
    [InlineData("{\"state\":\"prepared\",\"result\":\"awaiting_recovery_export\"}")]
    // A partial triple is refused rather than completed from somewhere else.
    [InlineData("{\"state\":\"prepared\",\"result\":{\"state\":\"awaiting_recovery_export\",\"transactionId\":\"123e4567-e89b-12d3-a456-426614174000\"}}")]
    [InlineData("{\"state\":\"prepared\",\"result\":{\"state\":\"awaiting_recovery_export\",\"transactionId\":\"123e4567-e89b-12d3-a456-426614174000\",\"planSha256\":\"PLAN\"}}")]
    [InlineData("{}")]
    public void RecoveryMetadata_ForeignOrPartialReceiptIsRefused(string json)
    {
        var receipt = Parse(json.Replace("PLAN", PlanSha).Replace("MANIFEST", ManifestSha));
        Assert.False(OfflineUpdateUiState.TryGetRecoveryMetadata(receipt, out _, out _, out _));
    }
}
