using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// AVB01: the first-install AOSP re-sign proof for a locked TB322FC whose current ABL references both the
/// OEM and the AOSP AVB key. Every input here is a synthetic fixture that already exists in the repository;
/// nothing reads, restores, substitutes or writes an ABL, and no original/historical backup is involved.
/// </summary>
public sealed class DirectAospAblTrustTests
{
    private const string SupportedModel = "TB322FC";

    private static readonly Type RecoveryType = typeof(EdlRawBootBuilder).Assembly
        .GetType("FreedomInstaller.Core.BootMaintenanceAblRecovery", throwOnError: true)!;

    private static readonly Type NodeType = typeof(EdlRawBootBuilder).Assembly
        .GetType("FreedomInstaller.Core.EdlRawResignedAvbNode", throwOnError: true)!;

    /// <summary>
    /// An OEM vbmeta root on a locked device is accepted for an AOSP re-sign when that slot's current ABL
    /// really references the AOSP key beside the OEM key. Zero replacements exist and no original backup is
    /// needed, on slot A and on slot B alike.
    /// </summary>
    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task LockedFirstInstallAuthorizesAospResignForEveryOemRootSlot(string slot)
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);

        object? proof = CreateDirectProof(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            DualKeyEvidenceForBothSlots());

        Assert.NotNull(proof);

        // Zero replacements: the proof carries no ABL role and can never authorize a replacement write.
        Assert.Equal(0, ReplacementCount(proof!));
        Assert.False(MatchesReplacement(proof!));

        object node = await ResignOemRootAsync(fixture.Sources["vbmeta_" + slot]);
        Assert.True(AuthorizesRoot(proof!, slot, node));
    }

    /// <summary>
    /// The same OEM vbmeta root without a referenced AOSP key in that slot's ABL: the proof is null, and the
    /// existing locked gate still refuses the AOSP re-sign with its compatibility code.
    /// </summary>
    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task MissingAospEvidenceReturnsNullAndCannotAuthorize(string slot)
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);

        Dictionary<string, string[]> evidence = DualKeyEvidenceForBothSlots();
        evidence[slot] = KeyHashes(AblAvbTrustTests.DualKeyImage(aospReferenced: false));

        object? proof = CreateDirectProof(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            evidence);

        Assert.Null(proof);

        object node = await ResignOemRootAsync(fixture.Sources["vbmeta_" + slot]);
        var error = Assert.Throws<InvalidOperationException>(
            () => RequireDirectAvbRootTrust(slot, BootloaderState.Locked, node, proof: null));
        Assert.StartsWith("requires-compatible-bootloader:", error.Message);
    }

    /// <summary>All original roots already use the AOSP key: the existing path owns them and no proof is made.</summary>
    [Fact]
    public void AllAospOriginalRootsReturnNull()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Aosp);

        object? proof = CreateDirectProof(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            DualKeyEvidenceForBothSlots());

        Assert.Null(proof);
    }

    /// <summary>The proof only exists for a locked TB322FC; every other model or lock fact keeps its path.</summary>
    [Theory]
    [InlineData("TB323FU", "Locked")]
    [InlineData("TB322FC", "Unlocked")]
    [InlineData("TB322FC", "Unknown")]
    public void NonApplicableModelOrLockStateReturnsNull(string model, string stateName)
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);

        object? proof = CreateDirectProof(
            Enum.Parse<BootloaderState>(stateName),
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            DualKeyEvidenceForBothSlots(),
            model);

        Assert.Null(proof);
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, string> SelfSourcedBothSlots() =>
        new(StringComparer.Ordinal) { ["a"] = "a", ["b"] = "b" };

    private static Dictionary<string, string> OriginalRootKeys(DirectAvbSourceTestFixture fixture) =>
        new(StringComparer.Ordinal)
        {
            ["a"] = fixture.OriginalRootKeySha256BySlot["A"],
            ["b"] = fixture.OriginalRootKeySha256BySlot["B"],
        };

    private static Dictionary<string, string> CurrentVbmeta(DirectAvbSourceTestFixture fixture) =>
        new(StringComparer.Ordinal)
        {
            ["a"] = fixture.Sources["vbmeta_a"].Sha256,
            ["b"] = fixture.Sources["vbmeta_b"].Sha256,
        };

    private static Dictionary<string, string[]> DualKeyEvidenceForBothSlots() =>
        new(StringComparer.Ordinal)
        {
            ["a"] = KeyHashes(AblAvbTrustTests.DualKeyImage()),
            ["b"] = KeyHashes(AblAvbTrustTests.DualKeyImage()),
        };

    /// <summary>Every referenced key the production all-keys view reports for the synthetic dual-key ABL.</summary>
    private static string[] KeyHashes(byte[] pe) => AblAvbTrustTests.InspectAllKeys(pe)
        .Select(facts => (string)facts.GetType().GetProperty("KeySha256")!.GetValue(facts)!)
        .ToArray();

    private static object? CreateDirectProof(
        BootloaderState lockState,
        Dictionary<string, string> sourceSlots,
        Dictionary<string, string> originalRootKeys,
        Dictionary<string, string> currentVbmetaSha256,
        Dictionary<string, string[]> referencedAblKeySha256,
        string model = SupportedModel)
    {
        MethodInfo method = RecoveryType
            .GetMethod("CreateDirect", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BootMaintenanceAblRecovery.CreateDirect was not found.");

        try
        {
            return method.Invoke(null, new object?[]
            {
                model,
                lockState,
                sourceSlots,
                originalRootKeys,
                currentVbmetaSha256,
                referencedAblKeySha256,
                new List<VerifiedEdlFile>(),
            });
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static async Task<object> ResignOemRootAsync(VerifiedEdlFile root)
    {
        MethodInfo method = typeof(EdlRawBootBuilder)
            .GetMethod("ResignAvbNodeAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EdlRawBootBuilder.ResignAvbNodeAsync was not found.");
        Type kindType = typeof(EdlRawBootBuilder).Assembly
            .GetType("FreedomInstaller.Core.EdlRawAvbNodeKind", throwOnError: true)!;
        object kind = Enum.Parse(kindType, "Vbmeta");

        string candidatePath = Path.Combine(NewCaseDirectory(), "avb01-vbmeta.bin");
        var task = (Task)method.Invoke(null, new object[] { root, kind, candidatePath, CancellationToken.None })!;
        await task.ConfigureAwait(false);

        object node = task.GetType().GetProperty("Result")!.GetValue(task)
            ?? throw new InvalidOperationException("ResignAvbNodeAsync produced no node.");
        Assert.Equal(NodeType, node.GetType());
        return node;
    }

    private static bool AuthorizesRoot(object proof, string slot, object node) =>
        (bool)RecoveryType.GetMethod("AuthorizesRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(proof, new[] { slot, node })!;

    private static int ReplacementCount(object proof) =>
        ((IEnumerable)RecoveryType.GetProperty("Replacements", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(proof)!).Cast<object>().Count();

    private static bool MatchesReplacement(object proof) =>
        (bool)RecoveryType.GetMethod("MatchesReplacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(proof, new object[]
            {
                "abl_a", 0, 0L, 0L, 0L, new string('0', 64), new string('0', 64),
            })!;

    private static void RequireDirectAvbRootTrust(string slot, BootloaderState state, object node, object? proof)
    {
        Type context = typeof(EdlRawBootBuilder).Assembly
            .GetType("FreedomInstaller.Core.InternalInstallPreparationContext", throwOnError: true)!;
        MethodInfo method = context
            .GetMethod("RequireDirectAvbRootTrust", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RequireDirectAvbRootTrust was not found.");

        try
        {
            method.Invoke(null, new object?[] { slot, state, node, proof });
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static string NewCaseDirectory()
    {
        string root = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
            ?? throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be set for tests.");
        string directory = Path.Combine(root, "avb01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
