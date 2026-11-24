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
/// The TB710FU counterpart of the TB322FC 188 decision: Lenovo's stock ZUXOS 1.5.04.495 signs every AVB node
/// with its own key and its ABL verifies with that key, so an AOSP-resigned install replaces the ABL with the
/// pinned AOSP-capable CN 279 loader. The SM8650 loader has no XBL replacement window, so no XBL configuration
/// is guarded. Pure decisions and the checked-in loader only; no device is read or written.
/// </summary>
public sealed class Tb710fuAospAblCompatibilityTests
{
    private const string Model = "TB710FU";

    private const string LenovoKeySha256 =
        "7B34B55104C8F0F48AE3763B1B20990AA9F8F5F0DFFB838D7E31EF506C819281";

    private const string AospKeySha256 =
        "7728E30F50BFA5CEA165F473175A08803F6A8346642B5AA10913E9D9E6DEFEF6";

    private static readonly Type RecoveryType = typeof(EdlRawBootBuilder).Assembly
        .GetType("FreedomInstaller.Core.BootMaintenanceAblRecovery", throwOnError: true)!;

    [Fact]
    public async Task LockedOemRootsReplaceBothSlotsWithoutGuardingXbl()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);
        var targets = Targets(candidate.Path, 4096);

        object? proof = CreateDirectWithPinnedAbl(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            targets,
            candidate.File);

        Assert.NotNull(proof);
        Assert.Equal(new[] { "abl_a", "abl_b" }, ReplacementRoles(proof!));
        Assert.Equal(new[] { "abl_a", "abl_b" }, GuardRoles(proof!));
        Assert.True(MatchesReplacement(proof!, "abl_a", targets["abl_a"], candidate.File));
        Assert.True(MatchesReplacement(proof!, "abl_b", targets["abl_b"], candidate.File));

        Assert.True(AuthorizesRoot(proof!, "a", await ResignOemRootAsync(fixture.Sources["vbmeta_a"])));
        Assert.True(AuthorizesRoot(proof!, "b", await ResignOemRootAsync(fixture.Sources["vbmeta_b"])));
    }

    [Fact]
    public void AnAblAlreadyUsingAospKeepsItsSlot()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);

        object? proof = CreateDirectWithPinnedAbl(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            Targets(candidate.Path, 4096),
            candidate.File,
            new HashSet<string>(StringComparer.Ordinal) { "a" });

        Assert.NotNull(proof);
        Assert.Equal(new[] { "abl_b" }, ReplacementRoles(proof!));
        Assert.Equal(new[] { "abl_a", "abl_b" }, GuardRoles(proof!));
    }

    [Fact]
    public void FactoryEmptyTargetSourcedFromTheCurrentSlotGetsNoReplacement()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);
        var targets = Targets(candidate.Path, 4096);

        object? proof = CreateDirectWithPinnedAbl(
            BootloaderState.Locked,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "a", ["b"] = "a" },
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            targets,
            candidate.File);

        Assert.NotNull(proof);
        Assert.Equal(new[] { "abl_a" }, ReplacementRoles(proof!));
        Assert.False(MatchesReplacement(proof!, "abl_b", targets["abl_b"], candidate.File));
    }

    [Fact]
    public void CandidateMustCoverTheWholeCapturedAblPartition()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);

        var error = Assert.Throws<InvalidDataException>(() => CreateDirectWithPinnedAbl(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            Targets(candidate.Path, 8192),
            candidate.File));

        Assert.StartsWith("tb710fu-abl279-size:", error.Message);
    }

    [Fact]
    public void MissingCandidateNamesTheTb710fuLoader()
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);

        var error = Assert.Throws<InvalidOperationException>(() => CreateDirectWithPinnedAbl(
            BootloaderState.Locked,
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            Targets(candidate.Path, 4096),
            null));

        Assert.StartsWith("tb710fu-abl279-missing:", error.Message);
        Assert.Contains("TB710FU", error.Message);
    }

    [Theory]
    [InlineData("Unlocked")]
    [InlineData("Unknown")]
    public void NonLockedStateReturnsNull(string stateName)
    {
        using var fixture = new DirectAvbSourceTestFixture(DirectAvbSourceKind.Oem);
        using var candidate = Candidate(4096);

        Assert.Null(CreateDirectWithPinnedAbl(
            Enum.Parse<BootloaderState>(stateName),
            SelfSourcedBothSlots(),
            OriginalRootKeys(fixture),
            CurrentVbmeta(fixture),
            Targets(candidate.Path, 4096),
            candidate.File));
    }

    [Fact]
    public void OnlyTheTwoReviewedModelsHaveAPinnedLoader()
    {
        Assert.Same(PinnedAospAbl.Tb322fc, PinnedAospAbl.ForModel("TB322FC"));
        Assert.Same(PinnedAospAbl.Tb710fu, PinnedAospAbl.ForModel("TB710FU"));
        Assert.Null(PinnedAospAbl.ForModel("TB323FU"));
        Assert.Null(PinnedAospAbl.ForModel("tb710fu"));
        Assert.Null(PinnedAospAbl.ForModel(null));

        Assert.True(PinnedAospAbl.Tb322fc.RequiresXblWindow);
        Assert.False(PinnedAospAbl.Tb710fu.RequiresXblWindow);
        Assert.Equal("tb710fu-279", PinnedAospAbl.Tb710fu.Kind);
        Assert.Equal(Tb322fcAbl188Assets.ImageSha256, PinnedAospAbl.Tb322fc.ImageSha256);
        Assert.Equal(Tb322fcAbl188Assets.ImageLength, PinnedAospAbl.Tb322fc.ImageLength);
    }

    [Fact]
    public void CheckedInLoaderIsThePinnedAospCapableImage()
    {
        byte[] abl = File.ReadAllBytes(ResolveCheckedInAbl());

        Assert.Equal(PinnedAospAbl.Tb710fu.ImageLength, abl.Length);
        Assert.Equal(PinnedAospAbl.Tb710fu.ImageSha256, Convert.ToHexString(SHA256.HashData(abl)));
        Assert.True(BootMaintenanceAblRecovery.AblAcceptsAosp(abl, CancellationToken.None));
        Assert.Equal(
            AospKeySha256,
            BootMaintenanceAblRecovery.PrimaryAblKey(abl, CancellationToken.None).KeySha256);
        Assert.False(BootMaintenanceAblRecovery.IsGblCapableAbl(abl, CancellationToken.None));
    }

    [Fact]
    public async Task BundledLoader_WhenPresent_IsPinnedAndStages()
    {
        if (!PinnedAospAbl.Tb710fu.IsBundled)
        {
            return;
        }

        VerifiedEdlFile staged = await PinnedAospAbl.Tb710fu.StageAsync(NewCaseDirectory(), CancellationToken.None);

        Assert.Equal(PinnedAospAbl.Tb710fu.ImageLength, staged.Length);
        Assert.Equal(PinnedAospAbl.Tb710fu.ImageSha256, staged.Sha256);
        Assert.Equal(
            PinnedAospAbl.Tb710fu.ImageSha256,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(staged.Path))));
    }

    /// <summary>
    /// Opt-in: Lenovo's stock ZUXOS 1.5.04.495 abl.elf (TB710FU_STOCK_495_ABL) references the AOSP key only as a
    /// secondary blob and uses Lenovo's key, so it must not be treated as accepting the AOSP-signed chain.
    /// </summary>
    [Fact]
    public void Stock495LoaderWithSecondaryAospReferenceIsNotAccepted()
    {
        string? path = Environment.GetEnvironmentVariable("TB710FU_STOCK_495_ABL");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        byte[] abl = File.ReadAllBytes(Path.GetFullPath(path));
        Assert.Equal(
            "8787D92EB449C1F619807EE73F54EEE120607ED539575B8A26A8378EE282A93E",
            Convert.ToHexString(SHA256.HashData(abl)));
        Assert.False(BootMaintenanceAblRecovery.AblAcceptsAosp(abl, CancellationToken.None));
        Assert.Equal(
            LenovoKeySha256,
            BootMaintenanceAblRecovery.PrimaryAblKey(abl, CancellationToken.None).KeySha256);
        Assert.Contains(
            AospKeySha256,
            EdlRawBootBuilder.InspectAblAvbKeys(abl, CancellationToken.None).Select(key => key.KeySha256));
    }

    /// <summary>
    /// The route choice of an AOSP-preserving model: an AOSP root (the no-unlock 495 chain) keeps the boot-only
    /// route, an OEM root (Lenovo's stock 495) leaves it, and only the selected source slots count.
    /// </summary>
    [Theory]
    [InlineData("Aosp", "Aosp", "b", false)]
    [InlineData("Oem", "Oem", "b", true)]
    [InlineData("Aosp", "Oem", "b", true)]
    [InlineData("Aosp", "Oem", "a", false)]
    [InlineData("Oem", "Aosp", "a", true)]
    public async Task OnlyAnOemSourceRootLeavesTheBootOnlyRoute(
        string slotA,
        string slotB,
        string sourceOfB,
        bool expectedOem)
    {
        using var fixture = new DirectAvbSourceTestFixture(
            Enum.Parse<DirectAvbSourceKind>(slotA), Enum.Parse<DirectAvbSourceKind>(slotB));

        Type contextType = typeof(EdlRawBootBuilder).Assembly
            .GetType("FreedomInstaller.Core.InternalInstallPreparationContext", throwOnError: true)!;
        Type factsType = contextType.GetNestedType("BackupFacts", BindingFlags.NonPublic)!;
        object facts = Activator.CreateInstance(factsType, nonPublic: true)!;
        var files = new Dictionary<string, EdlCapturedRegion>(StringComparer.Ordinal);
        foreach (string role in new[] { "vbmeta_a", "vbmeta_b" })
        {
            VerifiedEdlFile source = fixture.Sources[role];
            files[role] = new EdlCapturedRegion(
                role, 4, 0, source.Length / 4096, 4096, null, source.Path, source.Length, source.Sha256);
        }

        factsType.GetField("Files", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(facts, files);

        MethodInfo method = contextType.GetMethod(
            "AnyDirectSourceRootIsOemAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var sourceSlots = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "a", ["b"] = sourceOfB };
        var task = (Task<bool>)method.Invoke(null, new object[] { sourceSlots, facts, CancellationToken.None })!;

        Assert.Equal(expectedOem, await task);
    }

    private static string ResolveCheckedInAbl()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? current = new(start); current is not null; current = current.Parent)
            {
                string candidate = Path.Combine(
                    current.FullName, "vendor", "qualcomm", "tb710fu", "abl_aosp_capable.img");
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException(
            "The checked-in TB710FU AOSP-capable ABL is missing.",
            "vendor/qualcomm/tb710fu/abl_aosp_capable.img");
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
        var result = new Dictionary<string, EdlCapturedRegion>(StringComparer.Ordinal);
        foreach (string slot in new[] { "a", "b" })
        {
            string current = Convert.ToHexString(SHA256.HashData(
                Enumerable.Repeat((byte)(slot == "a" ? 0x31 : 0x71), checked((int)length)).ToArray()));
            result["abl_" + slot] = new EdlCapturedRegion(
                "abl_" + slot,
                4,
                slot == "a" ? 95238 : 437686,
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
        string path = Path.Combine(NewCaseDirectory(), "tb710fu-abl-279.bin");
        byte[] bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index * 29 + 3);
        }

        File.WriteAllBytes(path, bytes);
        return new CandidateFile(path, new VerifiedEdlFile(
            path, length, Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private static object? CreateDirectWithPinnedAbl(
        BootloaderState lockState,
        Dictionary<string, string> sourceSlots,
        Dictionary<string, string> originalRootKeys,
        Dictionary<string, string> currentVbmetaSha256,
        Dictionary<string, EdlCapturedRegion> currentFiles,
        VerifiedEdlFile? candidate,
        IReadOnlySet<string>? aospPrimarySlots = null)
    {
        MethodInfo method = RecoveryType
            .GetMethod("CreateDirectWithAbl188", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("CreateDirectWithAbl188 was not found.");

        try
        {
            return method.Invoke(null, new object?[]
            {
                Model,
                lockState,
                sourceSlots,
                originalRootKeys,
                currentVbmetaSha256,
                currentFiles,
                candidate,
                aospPrimarySlots ?? new HashSet<string>(StringComparer.Ordinal),
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

        string candidatePath = Path.Combine(NewCaseDirectory(), "tb710fu-vbmeta.bin");
        var task = (Task)method.Invoke(
            null, new object[] { root, Enum.Parse(kindType, "Vbmeta"), candidatePath, CancellationToken.None })!;
        await task.ConfigureAwait(false);

        return task.GetType().GetProperty("Result")!.GetValue(task)
            ?? throw new InvalidOperationException("ResignAvbNodeAsync produced no node.");
    }

    private static string NewCaseDirectory()
    {
        string root = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
            ?? throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be set for tests.");
        string directory = Path.Combine(root, "tb710fu-abl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
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
