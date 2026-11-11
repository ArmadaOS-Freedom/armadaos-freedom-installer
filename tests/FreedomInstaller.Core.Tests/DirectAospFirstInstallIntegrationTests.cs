using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// AVB02: the positive first-install proof end to end. A locked TB322FC with OEM original vbmeta roots and a
/// current ABL that references the AOSP key must reach the AOSP re-sign path through the real
/// <see cref="InternalInstallPreparationContext.PrepareDirectAsync"/> factory, even though that ABL carries
/// no GBL efisp chain. The ABL claim is the checked-in synthetic ELF/FV/LZMA container; no device firmware,
/// no injected proof and no bypass of the production decoder or of <c>RequireDirectAvbRootTrust</c> is used.
/// </summary>
public sealed class DirectAospFirstInstallIntegrationTests
    : IClassFixture<EdlClientToolchainFixture>, IClassFixture<EdlRawSharedInput>
{
    private const string FixtureRelativePath =
        "Fixtures/direct-aosp-abl/abl-aosp-synthetic.bin";

    private const string Model = "TB322FC";

    private readonly EdlClientToolchainFixture _toolchain;

    private readonly EdlRawSharedInput _rawShared;

    public DirectAospFirstInstallIntegrationTests(EdlClientToolchainFixture toolchain, EdlRawSharedInput rawShared)
    {
        _toolchain = toolchain;
        _rawShared = rawShared;
    }

    [Fact]
    public async Task LockedOemFirstInstallWithAospAblEvidenceSelectsAospResign()
    {
        string fixturePath = RequireAblFixture();
        Assert.Equal(Model, _toolchain.Toolchain.Model);

        using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
            _toolchain,
            _rawShared,
            avbSourceKind: DirectAvbSourceKind.Oem,
            observedFastbootLockState: BootloaderState.Locked,
            ablFixturePath: fixturePath);

        InternalInstallPrepared prepared = session.Backend.Prepared;

        // The real factory completed and chose the AOSP re-sign metadata path, not GBL and not a preserved chain.
        Assert.Equal(InternalInstallStage.Prepared, session.Backend.Snapshot.Stage);
        Assert.Equal(BootloaderState.Locked, prepared.FastbootLockState);
        Assert.Equal(InternalInstallBootTrustMode.AospResigned, prepared.BootTrustMode);
        Assert.Equal(new[] { "a", "b" }, prepared.AvbSlots.Keys.OrderBy(slot => slot, StringComparer.Ordinal));

        // Preparation writes nothing and resets nothing; the native oracle ran its usual six builds.
        Assert.Equal(0, session.EdlRunner.Writes.Count);
        Assert.Equal(0, session.EdlRunner.ResetCount);
        Assert.Equal(6, session.NativeCommandCount);

        AssertOriginalCaptureReadsBothLiveAbls(prepared);
        AssertAospSignedMetadataWithoutGblOrAblRoles(prepared);
    }

    /// <summary>
    /// REVIEW02: a live loader that changed after the backup is refused by the existing pre-write recheck,
    /// before any write is issued. Only the device's own abl_a bytes change; the local evidence stays intact.
    /// </summary>
    [Fact]
    public async Task ChangedLiveAblIsRefusedBeforeAnyWrite()
    {
        string fixturePath = RequireAblFixture();
        Assert.Equal(Model, _toolchain.Toolchain.Model);

        using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
            _toolchain,
            _rawShared,
            avbSourceKind: DirectAvbSourceKind.Oem,
            observedFastbootLockState: BootloaderState.Locked,
            ablFixturePath: fixturePath);

        InternalInstallPrepared prepared = session.Backend.Prepared;
        Assert.Equal(InternalInstallStage.Prepared, session.Backend.Snapshot.Stage);
        Assert.Equal(0, session.EdlRunner.Writes.Count);

        // One byte of the live device copy changes after preparation; the length stays identical.
        session.Device.MutateDirectSourceByte("abl_a", 0);

        InternalInstallResult result = await session.Backend.ExecuteAsync(
            prepared.ConfirmationId,
            userdataEraseConfirmed: true);

        // The existing fresh-content gate stops the run at the recheck, so nothing is written or reset.
        Assert.Equal(InternalInstallOutcome.Failed, result.Outcome);
        Assert.Equal(InternalInstallWriteState.NoneIssued, result.WriteState);
        Assert.Equal(0, session.EdlRunner.Writes.Count);
        Assert.Equal(0, session.EdlRunner.ResetCount);

        // The execute-direct read payload exists and really differs from the verified backup claim.
        string liveRead = Path.Combine(prepared.EvidenceDirectory, "execute-direct-abl_a.bin");
        Assert.True(File.Exists(liveRead), "the pre-write capture did not read the live ABL: " + liveRead);
        string liveSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(liveRead)));
        Assert.NotEqual(session.Backup.Files["abl_a"].Sha256, liveSha);
    }

    /// <summary>
    /// REVIEW02: the original capture must actually read the live ABL of every selected source slot, so a
    /// loader that changed after the backup cannot reach the write plan behind this proof.
    /// </summary>
    private static void AssertOriginalCaptureReadsBothLiveAbls(InternalInstallPrepared prepared)
    {
        foreach (string slot in new[] { "a", "b" })
        {
            string path = Path.Combine(prepared.EvidenceDirectory, "original-abl_" + slot + ".bin");
            Assert.True(File.Exists(path), "the original capture did not read the live ABL: " + path);
            Assert.True(new FileInfo(path).Length > 0, "the captured live ABL is empty: " + path);
        }
    }

    /// <summary>
    /// Reads the sealed confirmation: every metadata node under <c>direct.avbChain</c> is signed with the
    /// pinned AOSP key, none carries an ABL role, and there is no <c>direct.gbl</c> section and no staged GBL
    /// file because the direct proof skipped that selection.
    /// </summary>
    private static void AssertAospSignedMetadataWithoutGblOrAblRoles(InternalInstallPrepared prepared)
    {
        string confirmationPath = Path.Combine(prepared.EvidenceDirectory, "prepared-confirmation.json");
        Assert.True(File.Exists(confirmationPath), "the sealed confirmation is missing: " + confirmationPath);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(confirmationPath));
        JsonElement root = document.RootElement;

        // The direct section is where both the AVB chain and, on a GBL run only, the GBL facts are nested.
        JsonElement direct = root.GetProperty("direct");
        Assert.False(direct.TryGetProperty("gbl", out _), "the AOSP re-sign path must not carry a GBL plan");
        Assert.Equal("AospResigned", direct.GetProperty("bootTrustMode").GetString());

        Assert.False(File.Exists(Path.Combine(prepared.EvidenceDirectory, "gbl-efisp-candidate.img")));
        Assert.False(File.Exists(Path.Combine(prepared.EvidenceDirectory, "generic_superfastboot_prc.efi")));

        string pin = SigningPublicKeySha256().ToUpperInvariant();
        JsonElement chain = direct.GetProperty("avbChain");
        Assert.Equal(pin, chain.GetProperty("signingPublicKeySha256").GetString());

        int nodes = 0;
        foreach (JsonElement slot in chain.GetProperty("slots").EnumerateArray())
        {
            foreach (JsonElement node in slot.GetProperty("metadataNodes").EnumerateArray())
            {
                nodes++;
                string role = node.GetProperty("role").GetString() ?? string.Empty;
                Assert.False(
                    role.StartsWith("abl_", StringComparison.Ordinal),
                    "no ABL role may be prepared: " + role);
                Assert.Equal(pin, node.GetProperty("candidate").GetProperty("publicKeySha256").GetString());
            }
        }

        // Two self-sourced slots, each with recovery, vbmeta_system and vbmeta.
        Assert.Equal(6, nodes);
    }

    /// <summary>Resolves the checked-in synthetic ABL container and fails with the generation command.</summary>
    private static string RequireAblFixture()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            FixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(
            File.Exists(path),
            "the synthetic ABL container is missing; generate it with "
            + "`python validation/generate_direct_aosp_abl.py --key <avb-test-key.pem>`: " + path);
        return path;
    }

    /// <summary>Reads the internal AOSP target pin by reflection so the test never duplicates it.</summary>
    private static string SigningPublicKeySha256()
    {
        var property = typeof(EdlRawBootBuilder).GetProperty(
            "SigningPublicKeySha256",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EdlRawBootBuilder.SigningPublicKeySha256 is missing.");
        string? value = property.GetValue(null) as string;
        Assert.False(string.IsNullOrWhiteSpace(value), "the AOSP signing pin is empty.");
        return value!;
    }
}
