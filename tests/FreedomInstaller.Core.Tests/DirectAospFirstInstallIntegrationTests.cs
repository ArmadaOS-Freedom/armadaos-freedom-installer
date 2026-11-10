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
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            FixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(
            File.Exists(fixturePath),
            "the synthetic ABL container is missing; generate it with "
            + "`python validation/generate_direct_aosp_abl.py --key <avb-test-key.pem>`: " + fixturePath);

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

        AssertAospSignedMetadataWithoutGblOrAblRoles(prepared);
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
