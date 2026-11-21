using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// AVB03: the test-build compatibility decision that replaces an OEM-root TB322FC ABL with the
/// pinned 188 loader before an AOSP-resigned root can be written. The decision and its guards are
/// pure; no device is read or written here.
/// </summary>
public sealed class Tb322fcAbl188CompatibilityTests
{
    private const string SupportedModel = "TB322FC";

    private static readonly Type RecoveryType = typeof(EdlRawBootBuilder).Assembly
        .GetType("FreedomInstaller.Core.BootMaintenanceAblRecovery", throwOnError: true)!;

    [Fact]
    public async Task LockedOemRootsPlanThePinnedLoaderForBothSelectedSlots()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);
        var targets = Targets(candidate.Path, 4096);

        object? proof = CreateDirectWithAbl188(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            targets,
            candidate.File);

        Assert.NotNull(proof);
        Assert.Equal(new[] { "abl_a", "abl_b" }, ReplacementRoles(proof!));
        Assert.Equal(
            new[] { "abl_a", "abl_b", "xbl_config_a", "xbl_config_b" },
            GuardRoles(proof!));
        Assert.True(MatchesReplacement(proof!, "abl_a", targets["abl_a"], candidate.File));
        Assert.True(MatchesReplacement(proof!, "abl_b", targets["abl_b"], candidate.File));

        object nodeA = await ResignOemRootAsync(fixture.Sources["vbmeta_a"]);
        object nodeB = await ResignOemRootAsync(fixture.Sources["vbmeta_b"]);
        Assert.True(AuthorizesRoot(proof!, "a", nodeA));
        Assert.True(AuthorizesRoot(proof!, "b", nodeB));
    }

    [Fact]
    public async Task MixedRootsReplaceOnlyTheOemSlot()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Aosp, DirectAvbSourceKind.Oem);
        using var candidate = Candidate(8192);
        var targets = Targets(candidate.Path, 8192);

        object? proof = CreateDirectWithAbl188(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            targets,
            candidate.File,
            new HashSet<string>(StringComparer.Ordinal) { "a" });

        Assert.NotNull(proof);
        Assert.Equal(new[] { "abl_b" }, ReplacementRoles(proof!));
        Assert.Equal(
            new[] { "abl_a", "abl_b", "xbl_config_b" },
            GuardRoles(proof!));

        object nodeA = await ResignOemRootAsync(fixture.Sources["vbmeta_a"]);
        object nodeB = await ResignOemRootAsync(fixture.Sources["vbmeta_b"]);
        Assert.True(AuthorizesRoot(proof!, "a", nodeA));
        Assert.True(AuthorizesRoot(proof!, "b", nodeB));
    }

    [Fact]
    public async Task MixedCurrentAblEvidenceReplacesOnlyTheSlotWithoutAosp()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);
        var targets = Targets(candidate.Path, 4096);

        object? proof = CreateDirectWithAbl188(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            targets,
            candidate.File,
            new HashSet<string>(StringComparer.Ordinal) { "a" });

        Assert.NotNull(proof);
        Assert.Equal(new[] { "abl_b" }, ReplacementRoles(proof!));
        Assert.Equal(
            new[] { "abl_a", "abl_b", "xbl_config_b" },
            GuardRoles(proof!));

        object nodeA = await ResignOemRootAsync(fixture.Sources["vbmeta_a"]);
        object nodeB = await ResignOemRootAsync(fixture.Sources["vbmeta_b"]);
        Assert.True(AuthorizesRoot(proof!, "a", nodeA));
        Assert.True(AuthorizesRoot(proof!, "b", nodeB));
    }

    [Fact]
    public void CurrentAblAlreadyReferencesAospKeepsTheSlotUnchanged()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);
        var targets = Targets(candidate.Path, 4096);

        object? proof = CreateDirectWithAbl188(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            targets,
            candidate.File,
            new HashSet<string>(StringComparer.Ordinal) { "a", "b" });

        Assert.NotNull(proof);
        Assert.Empty(ReplacementRoles(proof!));
        Assert.Equal(new[] { "abl_a", "abl_b" }, GuardRoles(proof!));
    }

    [Fact]
    public void UnusedMappedSlotGetsNoReplacement()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);
        var targets = Targets(candidate.Path, 4096);
        var sourceSlots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "a",
            ["b"] = "a",
        };

        object? proof = CreateDirectWithAbl188(
            BootloaderState.Locked,
            sourceSlots,
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            targets,
            candidate.File);

        Assert.NotNull(proof);
        Assert.Equal(new[] { "abl_a" }, ReplacementRoles(proof!));
        Assert.False(MatchesReplacement(proof!, "abl_b", targets["abl_b"], candidate.File));
    }

    [Fact]
    public void AllAospRootsNeedNoReplacement()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Aosp);
        using var candidate = Candidate(4096);

        object? proof = CreateDirectWithAbl188(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            Targets(candidate.Path, 4096),
            candidate.File,
            new HashSet<string>(StringComparer.Ordinal) { "a", "b" });

        Assert.Null(proof);
    }

    [Fact]
    public void AllAospRootsStillReplaceAnAblWithoutAospEvidence()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Aosp);
        using var candidate = Candidate(4096);
        var targets = Targets(candidate.Path, 4096);

        object? proof = CreateDirectWithAbl188(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            targets,
            candidate.File,
            new HashSet<string>(StringComparer.Ordinal) { "a" });

        Assert.NotNull(proof);
        Assert.Equal(new[] { "abl_b" }, ReplacementRoles(proof!));
        Assert.Equal(
            new[] { "abl_a", "abl_b", "xbl_config_b" },
            GuardRoles(proof!));
    }

    [Theory]
    [InlineData("TB323FU", "Locked")]
    [InlineData("TB322FC", "Unlocked")]
    [InlineData("TB322FC", "Unknown")]
    public void NonApplicableModelOrLockStateReturnsNull(string model, string stateName)
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);

        object? proof = CreateDirectWithAbl188(
            Enum.Parse<BootloaderState>(stateName),
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            Targets(candidate.Path, 4096),
            candidate.File,
            model: model);

        Assert.Null(proof);
    }

    [Fact]
    public void CandidateMustCoverTheWholeCapturedAblPartition()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);
        var targets = Targets(candidate.Path, 8192);

        var error = Assert.Throws<InvalidDataException>(() => CreateDirectWithAbl188(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            targets,
            candidate.File));

        Assert.StartsWith("tb322fc-abl188-size:", error.Message);
    }

    [Fact]
    public async Task Bundled188Asset_WhenPresent_IsPinnedAndStages()
    {
        if (!Tb322fcAbl188Assets.IsBundled)
        {
            return;
        }

        string directory = NewCaseDirectory();
        VerifiedEdlFile staged = await Tb322fcAbl188Assets.StageAsync(directory, CancellationToken.None);

        Assert.Equal(Tb322fcAbl188Assets.ImageLength, staged.Length);
        Assert.Equal(Tb322fcAbl188Assets.ImageSha256, staged.Sha256);
        byte[] bytes = await File.ReadAllBytesAsync(staged.Path);
        Assert.Equal(Tb322fcAbl188Assets.ImageLength, bytes.Length);
        Assert.Equal(
            Tb322fcAbl188Assets.ImageSha256,
            Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.True(InternalSlotLoaderStructure.TryDecodeAblForGbl(bytes, default, out _));
    }

    [Fact]
    public async Task Pinned188AblIsNotDeferredAsGblCapable()
    {
        if (!Tb322fcAbl188Assets.IsBundled)
        {
            return;
        }

        string directory = NewCaseDirectory();
        VerifiedEdlFile staged = await Tb322fcAbl188Assets.StageAsync(directory, CancellationToken.None);
        byte[] bytes = await File.ReadAllBytesAsync(staged.Path);

        Assert.False(BootMaintenanceAblRecovery.IsGblCapableAbl(bytes, CancellationToken.None));
    }

    [Fact]
    public void Reviewed188LoaderPair_PassesTheProductionWindowGate()
    {
        string? imagesDirectory = Environment.GetEnvironmentVariable("TB322FC_188_IMAGES_DIR");
        if (string.IsNullOrWhiteSpace(imagesDirectory))
        {
            return;
        }

        string root = Path.GetFullPath(imagesDirectory);
        string ablPath = FindReviewImage(root, "abl.img");
        byte[] abl = File.ReadAllBytes(ablPath);
        Assert.Equal(
            Tb322fcAbl188Assets.ImageSha256,
            Convert.ToHexString(SHA256.HashData(abl)));
        Assert.Equal(Tb322fcAbl188Assets.ImageLength, abl.Length);

        foreach (string xblName in new[] { "xbl_config.img", "xbl_config_b.img" })
        {
            byte[] xbl = File.ReadAllBytes(FindReviewImage(root, xblName));
            Assert.True(
                InternalSlotLoaderStructure.TryRecognize(abl, xbl, default, out string evidence),
                "The pinned 188 ABL must match the " + xblName + " loader window.");
            Assert.False(string.IsNullOrWhiteSpace(evidence));
        }
    }

    [Fact]
    public void Pinned188Abl_MatchesTheCapturedCurrentDeviceXbl_WhenProvided()
    {
        string? currentImagesDirectory = Environment.GetEnvironmentVariable("TB322FC_CURRENT_XBL_DIR");
        if (string.IsNullOrWhiteSpace(currentImagesDirectory))
        {
            return;
        }

        string? reviewImagesDirectory = Environment.GetEnvironmentVariable("TB322FC_188_IMAGES_DIR");
        Assert.False(string.IsNullOrWhiteSpace(reviewImagesDirectory));

        byte[] abl = File.ReadAllBytes(FindReviewImage(
            Path.GetFullPath(reviewImagesDirectory!),
            "abl.img"));
        Assert.Equal(
            Tb322fcAbl188Assets.ImageSha256,
            Convert.ToHexString(SHA256.HashData(abl)));

        foreach (string xblName in new[] { "xbl_config_a.img", "xbl_config_b.img" })
        {
            byte[] xbl = File.ReadAllBytes(FindReviewImage(
                Path.GetFullPath(currentImagesDirectory),
                xblName));
            Assert.True(
                InternalSlotLoaderStructure.TryRecognize(abl, xbl, default, out string evidence),
                "The pinned 188 ABL must match the current " + xblName + " loader window.");
            Assert.False(string.IsNullOrWhiteSpace(evidence));
        }
    }

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

    private static Dictionary<string, EdlCapturedRegion> Targets(string candidatePath, long length)
    {
        byte[] candidate = File.ReadAllBytes(candidatePath);
        var result = new Dictionary<string, EdlCapturedRegion>(StringComparer.Ordinal);
        foreach (string slot in new[] { "a", "b" })
        {
            string current = Convert.ToHexString(SHA256.HashData(
                Enumerable.Repeat((byte)(slot == "a" ? 0x31 : 0x71), checked((int)length)).ToArray()));
            result["abl_" + slot] = new EdlCapturedRegion(
                "abl_" + slot,
                4,
                slot == "a" ? 96518 : 305024,
                length / 4096,
                4096,
                Guid.NewGuid().ToString("D"),
                candidatePath,
                length,
                current);
        }

        return result;
    }

    private static CandidateFile Candidate(int length)
    {
        string directory = NewCaseDirectory();
        string path = Path.Combine(directory, "tb322fc-abl-188.bin");
        byte[] bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index * 31 + 7);
        }

        File.WriteAllBytes(path, bytes);
        return new CandidateFile(path, new VerifiedEdlFile(
            path, length, Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private static object? CreateDirectWithAbl188(
        BootloaderState lockState,
        Dictionary<string, string> sourceSlots,
        Dictionary<string, string> originalRootKeys,
        Dictionary<string, string> currentVbmetaSha256,
        Dictionary<string, EdlCapturedRegion> currentFiles,
        VerifiedEdlFile candidate,
        IReadOnlySet<string>? aospReferencedSlots = null,
        string model = SupportedModel)
    {
        MethodInfo method = RecoveryType
            .GetMethod("CreateDirectWithAbl188", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("CreateDirectWithAbl188 was not found.");

        try
        {
            return method.Invoke(null, new object?[]
            {
                model,
                lockState,
                sourceSlots,
                originalRootKeys,
                currentVbmetaSha256,
                currentFiles,
                candidate,
                aospReferencedSlots ?? new HashSet<string>(StringComparer.Ordinal),
                new List<VerifiedEdlFile>(),
            });
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static string[] ReplacementRoles(object proof) =>
        ((IEnumerable<KeyValuePair<string, VerifiedEdlFile>>)RecoveryType
            .GetProperty("Replacements", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(proof)!)
        .Select(pair => pair.Key)
        .OrderBy(role => role, StringComparer.Ordinal)
        .ToArray();

    private static string[] GuardRoles(object proof) =>
        ((IEnumerable<string>)RecoveryType
            .GetProperty("AblGuardRoles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(proof)!)
        .OrderBy(role => role, StringComparer.Ordinal)
        .ToArray();

    private static bool MatchesReplacement(
        object proof,
        string role,
        EdlCapturedRegion target,
        VerifiedEdlFile candidate) =>
        (bool)RecoveryType.GetMethod("MatchesReplacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(proof, new object[]
            {
                role,
                target.Lun,
                target.StartSector,
                target.SectorCount,
                target.Length,
                target.Sha256,
                candidate.Sha256,
            })!;

    private static bool AuthorizesRoot(object proof, string slot, object node) =>
        (bool)RecoveryType.GetMethod("AuthorizesRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(proof, new[] { slot, node })!;

    private static async Task<object> ResignOemRootAsync(VerifiedEdlFile root)
    {
        MethodInfo method = typeof(EdlRawBootBuilder)
            .GetMethod("ResignAvbNodeAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ResignAvbNodeAsync was not found.");
        Type kindType = typeof(EdlRawBootBuilder).Assembly
            .GetType("FreedomInstaller.Core.EdlRawAvbNodeKind", throwOnError: true)!;
        object kind = Enum.Parse(kindType, "Vbmeta");

        string candidatePath = Path.Combine(NewCaseDirectory(), "avb03-vbmeta.bin");
        var task = (Task)method.Invoke(null, new object[] { root, kind, candidatePath, CancellationToken.None })!;
        await task.ConfigureAwait(false);

        return task.GetType().GetProperty("Result")!.GetValue(task)
            ?? throw new InvalidOperationException("ResignAvbNodeAsync produced no node.");
    }

    private static string NewCaseDirectory()
    {
        string root = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
            ?? throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be set for tests.");
        string directory = Path.Combine(root, "avb03-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FindReviewImage(string root, string fileName)
    {
        string[] matches = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                "Expected exactly one '" + fileName + "' below '" + root + "', found " + matches.Length + ".");
    }

    private sealed class CandidateFile : IDisposable
    {
        internal CandidateFile(string path, VerifiedEdlFile file)
        {
            Path = path;
            File = file;
        }

        internal string Path { get; }

        internal VerifiedEdlFile File { get; }

        public void Dispose()
        {
            try
            {
                System.IO.File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}
