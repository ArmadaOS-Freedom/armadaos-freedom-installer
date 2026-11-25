using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using FreedomInstaller.Core.Gbl;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class Tb323fuGblAblReplacementTests
    : IClassFixture<Gen5EdlToolchainFixture>,
      IClassFixture<EdlRawSharedInput>
{
    private const string ConfirmationFileName = "prepared-confirmation.json";
    private const int BcbUnitSize = 2048;

    private static readonly string[] MetadataRoles =
    {
        "recovery_a", "vbmeta_system_a", "vbmeta_a",
        "recovery_b", "vbmeta_system_b", "vbmeta_b",
    };

    private static readonly MethodInfo ControlBlockMethod = typeof(BootSlotControlTests).GetMethod(
            "ControlBlock", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BootSlotControlTests.ControlBlock was not found.");

    private static readonly MethodInfo BuildMiscMethod = typeof(BootSlotControlTests).GetMethod(
            "BuildMisc", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BootSlotControlTests.BuildMisc was not found.");

    private readonly Gen5EdlToolchainFixture _g5;
    private readonly EdlRawSharedInput _raw;

    public Tb323fuGblAblReplacementTests(Gen5EdlToolchainFixture g5, EdlRawSharedInput raw)
    {
        _g5 = g5;
        _raw = raw;
    }

    [Fact]
    public async Task Bundled088Asset_WhenPresent_IsPinnedDecodesEfispYesAndMatchesGen5XblWindow()
    {
        if (!Tb323fuAbl088Assets.IsBundled)
        {
            return;
        }

        string directory = _g5.NewCaseDirectory(nameof(Bundled088Asset_WhenPresent_IsPinnedDecodesEfispYesAndMatchesGen5XblWindow));
        VerifiedEdlFile staged = await Tb323fuAbl088Assets.StageAsync(directory, CancellationToken.None);

        Tb323fuAbl088Assets.RequirePinnedCandidate(staged);
        Assert.Equal(Tb323fuAbl088Assets.ImageLength, staged.Length);
        Assert.Equal(Tb323fuAbl088Assets.ImageSha256, staged.Sha256);

        byte[] bytes = await File.ReadAllBytesAsync(staged.Path);
        Assert.Equal(Tb323fuAbl088Assets.ImageLength, bytes.Length);
        Assert.Equal(
            Tb323fuAbl088Assets.ImageSha256,
            Convert.ToHexString(SHA256.HashData(bytes)));

        Assert.True(InternalSlotLoaderStructure.TryDecodeAblForGbl(bytes, CancellationToken.None, out byte[] pe));
        Assert.Equal(EfispLoad.Yes, EfispLoadDetector.Detect(pe).Status);

        byte[] xbl = BuildSyntheticGen5XblConfig();
        Assert.True(
            InternalSlotLoaderStructure.TryRecognize(bytes, xbl, CancellationToken.None, out string evidence),
            "Pinned TB323FU 088 ABL must match the 256 MiB Gen5 memory@C7800000 XBL window.");
        Assert.False(string.IsNullOrWhiteSpace(evidence));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task FirstInstallBackup_ActiveIncompatible1MiBAbl_SucceedsWhenBundled088Present(string activeSlot)
    {
        if (!Tb323fuAbl088Assets.IsBundled)
        {
            return;
        }

        string caseDir = _g5.NewCaseDirectory(nameof(FirstInstallBackup_ActiveIncompatible1MiBAbl_SucceedsWhenBundled088Present) + "-" + activeSlot);
        VerifiedEdlFile incompatibleAbl = WriteDeterministicFile(
            caseDir,
            "abl-incompatible.img",
            Tb323fuAbl088Assets.ImageLength,
            0x5A);
        VerifiedEdlFile xbl = WriteVerifiedFile(caseDir, "xbl.elf", BuildSyntheticGen5XblConfig());

        using var source = new GblDirectSourceFixture(
            region: "PRC",
            mismatchParentBootChild: false,
            androidTextOffset: null,
            customAbl: incompatibleAbl,
            customXbl: xbl);

        string deviceDir = Path.Combine(caseDir, "device");
        Directory.CreateDirectory(deviceDir);
        IB001SyntheticDevice device = IB001SyntheticDevice.CreateDirect(
            deviceDir,
            source.Sources,
            source.AvbSources,
            source.EfispSource,
            gen5: true);

        IB001FakeEdlRunner runner = new(device, hardwareId: _g5.Toolchain.HardwareId);
        FirmwareSnapshot firmware = new(
            "TB323FU_CN_OPEN_USER_Q00020.0_A17_ZUXOS_2.5.10.055_ST_261102",
            "Lenovo/TB323FU/TB323FU:17/test:user/release-keys",
            "17",
            "2026-11-01",
            BootloaderState.Locked);
        AndroidMaintenanceIdentity android = new(
            new DeviceIdentity("SN0123456789", "TB323FU", BootloaderState.Locked)
            {
                Firmware = firmware,
            },
            E003EdlTestData.SocSerialDecimal);
        EdlEntryResult binding = new(android, E003EdlTestData.Endpoint(), E003EdlTestData.BusSerialHex);
        EdlClient client = new(
            _g5.Toolchain,
            binding,
            runner,
            new E003ScriptedEndpointDiscovery { Endpoints = new[] { E003EdlTestData.Endpoint() } });
        InternalDeviceCheckResult check = new(
            android: android,
            fastbootSerial: "SN0123456789",
            fastbootProduct: "baldur",
            fastbootLockState: BootloaderState.Locked,
            otaState: default,
            androidReturn: default,
            cancelled: false,
            failedStage: null,
            error: null,
            currentSlot: activeSlot);

        string backupParent = Path.Combine(caseDir, "backup");
        Directory.CreateDirectory(backupParent);
        List<DiagnosticBootFile> capturedAbls = new();

        using InternalBackupSet backup = await InternalInstallBackup.CreateForFirstInstallAsync(
            client,
            backupParent,
            check,
            progress: null,
            CancellationToken.None,
            capturedAbls.Add);

        Assert.Equal(48, backup.Files.Count);
        Assert.Equal(2, capturedAbls.Count);
        Assert.Equal(0, runner.WriteOrResetCount);
    }

    [Theory]
    [InlineData(false, true, "b", new[] { "b" })]
    [InlineData(true, false, "a", new[] { "a" })]
    [InlineData(true, true, "b", new[] { "b", "a" })]
    [InlineData(false, false, "a", new string[0])]
    public async Task LockedTb323fu_ReplacesExactIncompatibleAblSlotsAndExecutesInFrozenOrder(
        bool slotAIncompatible,
        bool slotBIncompatible,
        string currentSlot,
        string[] expectedPreparedReplacementSlots)
    {
        if (!Tb323fuAbl088Assets.IsBundled)
        {
            return;
        }

        string caseDir = _g5.NewCaseDirectory(
            $"gbl-abl088-{slotAIncompatible}-{slotBIncompatible}-{currentSlot}");
        VerifiedEdlFile compatibleAbl = await Tb323fuAbl088Assets.StageAsync(caseDir, CancellationToken.None);
        VerifiedEdlFile incompatibleAblA = WriteDeterministicFile(
            caseDir,
            "abl-a-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x41);
        VerifiedEdlFile incompatibleAblB = WriteDeterministicFile(
            caseDir,
            "abl-b-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x72);
        VerifiedEdlFile xbl = WriteVerifiedFile(caseDir, "xbl-gen5.elf", BuildSyntheticGen5XblConfig());

        using var source = new GblDirectSourceFixture(
            region: "PRC",
            mismatchParentBootChild: false,
            androidTextOffset: null,
            customAbl: compatibleAbl,
            customXbl: xbl);

        var overrides = new Dictionary<string, VerifiedEdlFile>(StringComparer.Ordinal)
        {
            ["abl_a"] = slotAIncompatible ? incompatibleAblA : compatibleAbl,
            ["abl_b"] = slotBIncompatible ? incompatibleAblB : compatibleAbl,
        };

        using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
            _g5.Toolchain,
            _raw,
            currentSlot: currentSlot,
            avbSourceKind: DirectAvbSourceKind.Oem,
            observedFastbootLockState: BootloaderState.Locked,
            gblSources: source,
            sourceOverrides: overrides);

        InternalInstallPrepared prepared = session.Backend.Prepared;
        Assert.Equal(InternalInstallBootTrustMode.GblPreserveOem, prepared.BootTrustMode);
        Assert.Empty(prepared.Abl188ReplacementSlots);
        Assert.Equal(expectedPreparedReplacementSlots, prepared.GblAblReplacementSlots);

        using JsonDocument confirmation = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(prepared.EvidenceDirectory, ConfirmationFileName)));
        JsonElement gbl = confirmation.RootElement.GetProperty("direct").GetProperty("gbl");
        if (expectedPreparedReplacementSlots.Length == 0)
        {
            Assert.False(gbl.TryGetProperty("ablReplacement", out _));
        }
        else
        {
            JsonElement replacement = gbl.GetProperty("ablReplacement");
            Assert.Equal("tb323fu-088", replacement.GetProperty("kind").GetString());
            Assert.Equal(Tb323fuAbl088Assets.SourceFirmware, replacement.GetProperty("sourceFirmware").GetString());
            Assert.Equal(Tb323fuAbl088Assets.ImageLength, replacement.GetProperty("imageLength").GetInt64());
            Assert.Equal(Tb323fuAbl088Assets.ImageSha256, replacement.GetProperty("imageSha256").GetString());

            JsonElement entries = replacement.GetProperty("replacements");
            Assert.Equal(expectedPreparedReplacementSlots.Length, entries.GetArrayLength());
            string[] recordedSlots = entries.EnumerateArray()
                .Select(entry => entry.GetProperty("slot").GetString()!)
                .ToArray();
            Assert.Equal(expectedPreparedReplacementSlots.OrderBy(slot => slot, StringComparer.Ordinal), recordedSlots);
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                string slot = entry.GetProperty("slot").GetString()!;
                string role = entry.GetProperty("role").GetString()!;
                Assert.Equal("abl_" + slot, role);
                Assert.Equal(Tb323fuAbl088Assets.ImageLength, entry.GetProperty("length").GetInt64());
                Assert.Equal(Tb323fuAbl088Assets.ImageSha256, entry.GetProperty("candidateSha256").GetString());
                Assert.Equal(
                    session.Backup.Files[role].Sha256,
                    entry.GetProperty("currentSha256").GetString(),
                    ignoreCase: true);
            }
        }

        var stageEvents = new List<(InternalInstallStage Stage, string? Slot, InternalInstallProgressKind Kind)>();
        var progress = new SynchronousProgress<InternalInstallProgress>(
            item => stageEvents.Add((item.Stage, item.Slot, item.Kind)));

        InternalInstallResult result = await session.Backend.ExecuteAsync(
            prepared.ConfirmationId,
            userdataEraseConfirmed: true,
            progress: progress);

        Assert.Equal(InternalInstallOutcome.InstallationCompleted, result.Outcome);
        Assert.Equal(InternalInstallWriteState.AllVerified, result.WriteState);
        Assert.Equal(new[] { "b", "a" }, result.VerifiedSlots);
        Assert.Equal(1, session.EdlRunner.ResetCount);

        var writes = session.EdlRunner.Writes;
        string[] expectedWriteSlotOrder = new[] { "b", "a" }
            .Where(slot => expectedPreparedReplacementSlots.Contains(slot, StringComparer.Ordinal))
            .ToArray();

        Assert.Equal(11 + expectedWriteSlotOrder.Length, writes.Count);
        AssertFullWrite(writes[7], session.Backup.Files["efisp"]);

        for (int index = 0; index < expectedWriteSlotOrder.Length; index++)
        {
            string slot = expectedWriteSlotOrder[index];
            EdlCapturedRegion targetAbl = session.Backup.Files["abl_" + slot];
            IB001Write ablWrite = writes[8 + index];
            AssertFullWrite(ablWrite, targetAbl);
            Assert.Equal(Tb323fuAbl088Assets.ImageSha256, ablWrite.Sha256, ignoreCase: true);
        }

        int bootBIndex = 8 + expectedWriteSlotOrder.Length;
        AssertFullWrite(writes[bootBIndex], session.Backup.Files["boot_b"]);
        AssertFullWrite(writes[bootBIndex + 1], session.Backup.Files["boot_a"]);

        if (!slotAIncompatible)
        {
            Assert.DoesNotContain(writes, write => OverlapsRegion(write, session.Backup.Files["abl_a"]));
        }

        if (!slotBIncompatible)
        {
            Assert.DoesNotContain(writes, write => OverlapsRegion(write, session.Backup.Files["abl_b"]));
        }

        AssertBcbWrite(writes[10 + expectedWriteSlotOrder.Length], session.Backup.Files["misc"]);

        foreach (string metadataRole in MetadataRoles)
        {
            Assert.DoesNotContain(writes, write => OverlapsRegion(write, session.Backup.Files[metadataRole]));
        }

        string[] verifiedGblAblSlots = stageEvents
            .Where(item => item.Stage == InternalInstallStage.WritingGblAbl && item.Kind == InternalInstallProgressKind.Verified)
            .Select(item => item.Slot!)
            .ToArray();
        Assert.Equal(expectedWriteSlotOrder, verifiedGblAblSlots);
    }

    [Fact]
    public async Task LockedTb323fu_EmptyInactiveSlotB_ReplacesActiveAblAndCopiesItToB()
    {
        if (!Tb323fuAbl088Assets.IsBundled)
        {
            return;
        }

        string caseDir = _g5.NewCaseDirectory(nameof(LockedTb323fu_EmptyInactiveSlotB_ReplacesActiveAblAndCopiesItToB));
        VerifiedEdlFile incompatibleAblA = WriteDeterministicFile(
            caseDir,
            "abl-a-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x33);
        VerifiedEdlFile xbl = WriteVerifiedFile(caseDir, "xbl-gen5.elf", BuildSyntheticGen5XblConfig());

        using var source = new GblDirectSourceFixture(
            region: "PRC",
            mismatchParentBootChild: false,
            androidTextOffset: null,
            customAbl: incompatibleAblA,
            customXbl: xbl);

        using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
            _g5.Toolchain,
            _raw,
            currentSlot: "a",
            zeroSlot: "b",
            avbSourceKind: DirectAvbSourceKind.Oem,
            observedFastbootLockState: BootloaderState.Locked,
            gblSources: source);

        InternalInstallPrepared prepared = session.Backend.Prepared;
        Assert.Equal(InternalInstallBootTrustMode.GblPreserveOem, prepared.BootTrustMode);
        Assert.Equal(new[] { "a" }, prepared.GblAblReplacementSlots);
        Assert.Equal(new[] { "b" }, prepared.GblAblCopySlots);
        Assert.True(prepared.AvbSlots["b"].NonbootableSystem);
        Assert.Equal(Tb323fuAbl088Assets.ImageLength, session.Backup.Files["abl_b"].Length);

        InternalInstallResult result = await session.Backend.ExecuteAsync(
            prepared.ConfirmationId,
            userdataEraseConfirmed: true);

        Assert.Equal(InternalInstallOutcome.InstallationCompleted, result.Outcome);
        Assert.Equal(InternalInstallWriteState.AllVerified, result.WriteState);
        var writes = session.EdlRunner.Writes;
        Assert.Equal(13, writes.Count);
        AssertFullWrite(writes[7], session.Backup.Files["efisp"]);
        AssertFullWrite(writes[8], session.Backup.Files["abl_a"]);
        Assert.Equal(Tb323fuAbl088Assets.ImageSha256, writes[8].Sha256, ignoreCase: true);
        AssertFullWrite(writes[9], session.Backup.Files["abl_b"]);
        Assert.Equal(writes[8].Sha256, writes[9].Sha256, ignoreCase: true);
        AssertFullWrite(writes[10], session.Backup.Files["boot_b"]);
        AssertFullWrite(writes[11], session.Backup.Files["boot_a"]);
        AssertBcbWrite(writes[12], session.Backup.Files["misc"]);
        foreach (string role in MetadataRoles)
        {
            Assert.DoesNotContain(writes, write => OverlapsRegion(write, session.Backup.Files[role]));
        }
    }

    [Fact]
    public async Task LockedTb323fu_BootOnlyInactiveSlotB_DowngradesSlotAFirstThenPopulatesSlotBBoot()
    {
        if (!Tb323fuAbl088Assets.IsBundled)
        {
            return;
        }

        string caseDir = _g5.NewCaseDirectory(nameof(LockedTb323fu_BootOnlyInactiveSlotB_DowngradesSlotAFirstThenPopulatesSlotBBoot));
        VerifiedEdlFile incompatibleAblA = WriteDeterministicFile(
            caseDir,
            "abl-a-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x37);
        VerifiedEdlFile xbl = WriteVerifiedFile(caseDir, "xbl-gen5.elf", BuildSyntheticGen5XblConfig());

        using var source = new GblDirectSourceFixture(
            region: "PRC",
            mismatchParentBootChild: false,
            androidTextOffset: null,
            customAbl: incompatibleAblA,
            customXbl: xbl);

        using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
            _g5.Toolchain,
            _raw,
            currentSlot: "a",
            zeroSlot: "b",
            bootOnlyInactiveB: true,
            avbSourceKind: DirectAvbSourceKind.Oem,
            observedFastbootLockState: BootloaderState.Locked,
            gblSources: source);

        InternalInstallPrepared prepared = session.Backend.Prepared;
        Assert.Equal(InternalInstallBootTrustMode.GblPreserveOem, prepared.BootTrustMode);
        Assert.Equal(new[] { "a" }, prepared.GblAblReplacementSlots);
        Assert.Equal(new[] { "b" }, prepared.GblAblCopySlots);
        Assert.Equal("a", prepared.AvbSlots["b"].SourceSlot);
        Assert.True(prepared.AvbSlots["b"].NonbootableSystem);
        Assert.Equal("a", prepared.SlotMaterials["b"].Slot);
        Assert.Equal(Tb323fuAbl088Assets.ImageLength, session.Backup.Files["abl_b"].Length);
        Assert.Equal(
            Tb323fuAbl088Assets.ImageSha256,
            prepared.SlotMaterials["a"].SourceSha256["abl"],
            ignoreCase: true);
        Assert.Equal(
            Tb323fuAbl088Assets.ImageSha256,
            prepared.SlotMaterials["b"].SourceSha256["abl"],
            ignoreCase: true);
        Assert.NotEqual(
            prepared.CurrentBootSha256["a"],
            prepared.CurrentBootSha256["b"],
            StringComparer.OrdinalIgnoreCase);
        Assert.Empty(session.EdlRunner.Writes);

        InternalInstallResult result = await session.Backend.ExecuteAsync(
            prepared.ConfirmationId,
            userdataEraseConfirmed: true);

        Assert.Equal(InternalInstallOutcome.InstallationCompleted, result.Outcome);
        Assert.Equal(InternalInstallWriteState.AllVerified, result.WriteState);
        Assert.Equal(new[] { "b", "a" }, result.VerifiedSlots);
        Assert.Equal(1, session.EdlRunner.ResetCount);

        var writes = session.EdlRunner.Writes;
        Assert.Equal(13, writes.Count);
        AssertFullWrite(writes[7], session.Backup.Files["efisp"]);
        AssertFullWrite(writes[8], session.Backup.Files["abl_a"]);
        Assert.Equal(Tb323fuAbl088Assets.ImageSha256, writes[8].Sha256, ignoreCase: true);
        AssertFullWrite(writes[9], session.Backup.Files["abl_b"]);
        Assert.Equal(writes[8].Sha256, writes[9].Sha256, ignoreCase: true);
        AssertFullWrite(writes[10], session.Backup.Files["boot_b"]);
        AssertFullWrite(writes[11], session.Backup.Files["boot_a"]);
        AssertBcbWrite(writes[12], session.Backup.Files["misc"]);
        Assert.NotEqual(session.Backup.Files["boot_b"].Sha256, writes[10].Sha256, StringComparer.OrdinalIgnoreCase);

        foreach (string role in new[] { "vendor_boot_b", "init_boot_b", "xbl_config_b", "vbmeta_b", "vbmeta_system_b", "recovery_b" })
        {
            Assert.DoesNotContain(writes, write => OverlapsRegion(write, session.Backup.Files[role]));
        }
    }

    [Fact]
    public async Task UnlockedTb323fu_NeverReplacesGblAblEvenWhenLiveAblIsIncompatible()
    {
        string caseDir = _g5.NewCaseDirectory(nameof(UnlockedTb323fu_NeverReplacesGblAblEvenWhenLiveAblIsIncompatible));
        VerifiedEdlFile incompatibleAbl = WriteDeterministicFile(
            caseDir,
            "abl-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x4C);
        VerifiedEdlFile xbl = WriteVerifiedFile(caseDir, "xbl-gen5.elf", BuildSyntheticGen5XblConfig());

        using var source = new GblDirectSourceFixture(
            region: "PRC",
            mismatchParentBootChild: false,
            androidTextOffset: null,
            customAbl: incompatibleAbl,
            customXbl: xbl);

        using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
            _g5.Toolchain,
            _raw,
            currentSlot: "a",
            avbSourceKind: DirectAvbSourceKind.Oem,
            observedFastbootLockState: BootloaderState.Unlocked,
            gblSources: source);

        InternalInstallPrepared prepared = session.Backend.Prepared;
        Assert.Equal(InternalInstallBootTrustMode.AospResigned, prepared.BootTrustMode);
        Assert.Empty(prepared.GblAblReplacementSlots);
        Assert.Empty(prepared.Abl188ReplacementSlots);
    }

    [Fact]
    public async Task LockedTb323fu_ChangedLiveReplacedAblAfterPrepare_FailsBeforeAnyWrite()
    {
        if (!Tb323fuAbl088Assets.IsBundled)
        {
            return;
        }

        string caseDir = _g5.NewCaseDirectory(nameof(LockedTb323fu_ChangedLiveReplacedAblAfterPrepare_FailsBeforeAnyWrite));
        VerifiedEdlFile incompatibleAblA = WriteDeterministicFile(
            caseDir,
            "abl-a-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x21);
        VerifiedEdlFile incompatibleAblB = WriteDeterministicFile(
            caseDir,
            "abl-b-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x63);
        VerifiedEdlFile xbl = WriteVerifiedFile(caseDir, "xbl-gen5.elf", BuildSyntheticGen5XblConfig());

        using var source = new GblDirectSourceFixture(
            region: "PRC",
            mismatchParentBootChild: false,
            androidTextOffset: null,
            customAbl: incompatibleAblA,
            customXbl: xbl);

        var overrides = new Dictionary<string, VerifiedEdlFile>(StringComparer.Ordinal)
        {
            ["abl_a"] = incompatibleAblA,
            ["abl_b"] = incompatibleAblB,
        };

        using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
            _g5.Toolchain,
            _raw,
            currentSlot: "b",
            avbSourceKind: DirectAvbSourceKind.Oem,
            observedFastbootLockState: BootloaderState.Locked,
            gblSources: source,
            sourceOverrides: overrides);

        session.Device.MutateDirectSourceByte("abl_a", 0);

        InternalInstallResult result = await session.Backend.ExecuteAsync(
            session.Backend.Prepared.ConfirmationId,
            userdataEraseConfirmed: true);

        Assert.Equal(InternalInstallOutcome.Failed, result.Outcome);
        Assert.Equal(InternalInstallWriteState.NoneIssued, result.WriteState);
        Assert.Empty(session.EdlRunner.Writes);
        Assert.Equal(0, session.EdlRunner.ResetCount);
    }

    [Fact]
    public async Task LockedTb323fu_ChangedUnreplacedSourceXblConfigAfterPrepare_FailsBeforeAnyWrite()
    {
        if (!Tb323fuAbl088Assets.IsBundled)
        {
            return;
        }

        string caseDir = _g5.NewCaseDirectory(
            nameof(LockedTb323fu_ChangedUnreplacedSourceXblConfigAfterPrepare_FailsBeforeAnyWrite));
        VerifiedEdlFile compatibleAblB = await Tb323fuAbl088Assets.StageAsync(caseDir, CancellationToken.None);
        VerifiedEdlFile incompatibleAblA = WriteDeterministicFile(
            caseDir,
            "abl-a-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x29);
        VerifiedEdlFile xblA = WriteVerifiedFile(caseDir, "xbl-a-gen5.elf", BuildSyntheticGen5XblConfig());
        VerifiedEdlFile xblB = WriteVerifiedFile(caseDir, "xbl-b-gen5.elf", BuildSyntheticGen5XblConfig());

        using var source = new GblDirectSourceFixture(
            region: "PRC",
            mismatchParentBootChild: false,
            androidTextOffset: null,
            customAbl: compatibleAblB,
            customXbl: xblA);

        var overrides = new Dictionary<string, VerifiedEdlFile>(StringComparer.Ordinal)
        {
            ["abl_a"] = incompatibleAblA,
            ["abl_b"] = compatibleAblB,
            ["xbl_config_b"] = xblB,
        };

        using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
            _g5.Toolchain,
            _raw,
            currentSlot: "a",
            avbSourceKind: DirectAvbSourceKind.Oem,
            observedFastbootLockState: BootloaderState.Locked,
            gblSources: source,
            sourceOverrides: overrides);

        Assert.Equal(new[] { "a" }, session.Backend.Prepared.GblAblReplacementSlots);
        session.Device.MutateDirectSourceByte("xbl_config_b", 0);

        InternalInstallResult result = await session.Backend.ExecuteAsync(
            session.Backend.Prepared.ConfirmationId,
            userdataEraseConfirmed: true);

        Assert.Equal(InternalInstallOutcome.Failed, result.Outcome);
        Assert.Equal(InternalInstallWriteState.NoneIssued, result.WriteState);
        Assert.Empty(session.EdlRunner.Writes);
        Assert.Equal(0, session.EdlRunner.ResetCount);
    }

    [Fact]
    public async Task LockedTb323fu_ReinstallChecksInactiveBootBudgetAndRejectsIncompleteArmadaBootImage()
    {
        if (!Tb323fuAbl088Assets.IsBundled)
        {
            return;
        }

        string caseDir = _g5.NewCaseDirectory(
            nameof(LockedTb323fu_ReinstallChecksInactiveBootBudgetAndRejectsIncompleteArmadaBootImage));
        VerifiedEdlFile compatibleAbl = await Tb323fuAbl088Assets.StageAsync(caseDir, CancellationToken.None);
        VerifiedEdlFile incompatibleAbl = WriteDeterministicFile(
            caseDir, "abl-a-055.img", Tb323fuAbl088Assets.ImageLength, 0x51);
        VerifiedEdlFile xbl = WriteVerifiedFile(caseDir, "xbl-gen5.elf", BuildSyntheticGen5XblConfig());

        using var source = new GblDirectSourceFixture(
            region: "PRC",
            mismatchParentBootChild: false,
            androidTextOffset: null,
            customAbl: incompatibleAbl,
            customXbl: xbl);

        InvalidOperationException rejectedPackage = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
                _g5.Toolchain,
                _raw,
                currentSlot: "a",
                zeroSlot: "b",
                bootOnlyInactiveB: true,
                avbSourceKind: DirectAvbSourceKind.Oem,
                observedFastbootLockState: BootloaderState.Locked,
                gblSources: source,
                reinstallAndroidGiB: 16,
                usesArmadaBoot: true,
                configureReinstallOverlayAsync: VerifyPreviousBootAsync);
        });

        // The ordinary raw-package fixture has only an ext4 superblock. Advertising ARMADA_BOOT
        // must reach the full filesystem verifier and fail before selecting the reinstall boot plan.
        Assert.StartsWith("maintenance-installed-linux-mismatch:", rejectedPackage.Message, StringComparison.Ordinal);
        Assert.Contains("extents feature", rejectedPackage.Message, StringComparison.Ordinal);

        async Task VerifyPreviousBootAsync(
            IB001SyntheticDevice device,
            EdlRawBootTools tools,
            ICommandRunner runner,
            InternalBackupSet factoryBackup,
            EdlRawPackageContract _,
            string workRoot)
        {
            string evidence = Path.Combine(workRoot, "reinstall-project-b");
            Directory.CreateDirectory(evidence);

            VerifiedEdlFile Claim(string role)
            {
                EdlCapturedRegion file = factoryBackup.Files[role];
                return new VerifiedEdlFile(file.FilePath, file.Length, file.Sha256);
            }

            using var priorPackage = new EdlRawBootBuilderTests.SyntheticPackage(deviceModel: "TB323FU");
            byte[] kernel = (byte[])priorPackage.Kernel.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(kernel.AsSpan(16, 8), 130UL * 1024 * 1024);
            priorPackage.ReplaceKernel(kernel);

            VerifiedEdlFile bootA = Claim("boot_a");
            VerifiedEdlFile vendorA = Claim("vendor_boot_a");
            VerifiedEdlFile initA = Claim("init_boot_a");
            VerifiedEdlFile xblA = Claim("xbl_config_a");
            VerifiedEdlFile vendorB = Claim("vendor_boot_b");
            VerifiedEdlFile initB = Claim("init_boot_b");
            VerifiedEdlFile ablB = Claim("abl_b");
            VerifiedEdlFile xblB = Claim("xbl_config_b");
            VerifiedEdlFile rootA = Claim("vbmeta_a");

            using EdlRawInactiveSlotBackup donor = await EdlRawInactiveSlotBackup.VerifyBootOnlyInactiveBAsync(
                factoryBackup.ProfileId, Claim("boot_b"), vendorB, initB, ablB, xblB, CancellationToken.None);
            using EdlRawBuiltBoot prior = await EdlRawBootBuilder.BuildAsync(
                new EdlRawBootBuildRequest(
                    "b", bootA, vendorA, initA, compatibleAbl, xblA, priorPackage.Contract, tools, evidence, donor),
                runner,
                CancellationToken.None);

            Assert.True(prior.PackedRaw.Length > 128L * 1024 * 1024);

            // Exercise the read-only proof against the complete test-signed boot candidate. It must
            // reject the fallback budget and authenticate and decode the same bytes with the measured budget.
            Task<CurrentBootInitializedInactiveProof> VerifyWithAblAsync(VerifiedEdlFile abl) =>
                EdlRawBootBuilder.VerifyInitializedInactiveSlotAsync(
                    factoryBackup.ProfileId, "a", "b", bootA, vendorA, initA, abl, xblA,
                    prior.CandidateBoot, vendorB, initB, ablB, xblB, rootA, tools, runner, evidence);

            InvalidDataException tooLarge = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                using CurrentBootInitializedInactiveProof proof = await VerifyWithAblAsync(Claim("abl_a"));
            });
            Assert.Contains("exceeds the bounded decode limit", tooLarge.Message, StringComparison.Ordinal);

            using CurrentBootInitializedInactiveProof proven = await VerifyWithAblAsync(compatibleAbl);
            Assert.Equal("a", proven.SourceSlot);
            Assert.Equal("b", proven.TargetSlot);
            Assert.Equal(compatibleAbl.Sha256, proven.SourceMaterial.SourceSha256["abl"], ignoreCase: true);
            Assert.Equal(prior.PackedRaw.Length, proven.ProjectRaw.Length);
            Assert.Equal(prior.PackedRaw.Sha256, proven.ProjectRaw.Sha256, ignoreCase: true);

            EdlCapturedRegion bootB = factoryBackup.Files["boot_b"];
            string overlay = Path.Combine(evidence, "overlay-boot-b.img");
            File.Copy(prior.CandidateBoot.Path, overlay);
            device.AddWriteOverlay(bootB.Lun, bootB.StartSector, bootB.SectorCount, overlay);
        }
    }

    [Fact]
    public async Task LockedTb323fu_AblReadbackFailureAfterWrite_StopsBeforeMiscAndKeepsOriginalBackups()
    {
        if (!Tb323fuAbl088Assets.IsBundled)
        {
            return;
        }

        string caseDir = _g5.NewCaseDirectory(
            nameof(LockedTb323fu_AblReadbackFailureAfterWrite_StopsBeforeMiscAndKeepsOriginalBackups));
        VerifiedEdlFile incompatibleAblA = WriteDeterministicFile(
            caseDir,
            "abl-a-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x21);
        VerifiedEdlFile incompatibleAblB = WriteDeterministicFile(
            caseDir,
            "abl-b-055.img",
            Tb323fuAbl088Assets.ImageLength,
            0x63);
        VerifiedEdlFile xbl = WriteVerifiedFile(caseDir, "xbl-gen5.elf", BuildSyntheticGen5XblConfig());

        using var source = new GblDirectSourceFixture(
            region: "PRC",
            mismatchParentBootChild: false,
            androidTextOffset: null,
            customAbl: incompatibleAblA,
            customXbl: xbl);

        var overrides = new Dictionary<string, VerifiedEdlFile>(StringComparer.Ordinal)
        {
            ["abl_a"] = incompatibleAblA,
            ["abl_b"] = incompatibleAblB,
        };

        using DirectInstallReadyTestSession session = await DirectInstallReadyTestSession.CreateAsync(
            _g5.Toolchain,
            _raw,
            currentSlot: "b",
            avbSourceKind: DirectAvbSourceKind.Oem,
            observedFastbootLockState: BootloaderState.Locked,
            gblSources: source,
            sourceOverrides: overrides,
            configureInitialDevice: static (device, workRoot) =>
            {
                (int miscLun, long miscStart, long miscCount, _) = device.Expectation("misc");
                string miscPath = WriteMiscOverlay(Path.Combine(workRoot, "misc-init"), "b", miscCount);
                device.AddWriteOverlay(miscLun, miscStart, miscCount, miscPath);
            });

        InternalInstallPrepared prepared = session.Backend.Prepared;
        Assert.Equal(InternalInstallBootTrustMode.GblPreserveOem, prepared.BootTrustMode);
        Assert.Equal(new[] { "b", "a" }, prepared.GblAblReplacementSlots);

        EdlCapturedRegion ablA = session.Backup.Files["abl_a"];
        EdlCapturedRegion ablB = session.Backup.Files["abl_b"];
        EdlCapturedRegion misc = session.Backup.Files["misc"];
        session.EdlRunner.InjectReadbackFailureOnce(ablA.Lun, ablA.StartSector, ablA.SectorCount);

        var verifiedAblSlots = new List<string>();
        var progress = new SynchronousProgress<InternalInstallProgress>(item =>
        {
            if (item.Stage == InternalInstallStage.WritingGblAbl
                && item.Kind == InternalInstallProgressKind.Verified)
            {
                verifiedAblSlots.Add(item.Slot!);
            }
        });

        InternalInstallResult result = await session.Backend.ExecuteAsync(
            prepared.ConfirmationId,
            userdataEraseConfirmed: true,
            progress: progress);

        Assert.Equal(InternalInstallOutcome.UnknownWriteOutcome, result.Outcome);
        Assert.Equal(InternalInstallWriteState.Unknown, result.WriteState);
        Assert.Equal(new[] { "b" }, verifiedAblSlots);
        var writes = session.EdlRunner.Writes;
        AssertFullWrite(writes[^1], ablA);
        Assert.DoesNotContain(writes, write => OverlapsRegion(write, session.Backup.Files["boot_b"]));
        Assert.DoesNotContain(writes, write => OverlapsRegion(write, session.Backup.Files["boot_a"]));
        Assert.DoesNotContain(writes, write => OverlapsRegion(write, misc));
        Assert.Equal(0, session.EdlRunner.ResetCount);

        foreach (EdlCapturedRegion backup in new[] { ablA, ablB })
        {
            Assert.True(File.Exists(backup.FilePath));
            using var stream = File.OpenRead(backup.FilePath);
            Assert.Equal(backup.Sha256, Convert.ToHexString(SHA256.HashData(stream)), ignoreCase: true);
        }

        // Partially written / replaced live ABLs must remain recoverable through RestoreStock using the
        // original factory backup directory captured before execution.
        string damagedAblAPath = Path.Combine(caseDir, "damaged-live-abl_a.bin");
        byte[] damagedAblA = await File.ReadAllBytesAsync(ablA.FilePath);
        damagedAblA[0] ^= 0xFF;
        damagedAblA[4096] ^= 0x55;
        await File.WriteAllBytesAsync(damagedAblAPath, damagedAblA);
        session.Device.AddWriteOverlay(ablA.Lun, ablA.StartSector, ablA.SectorCount, damagedAblAPath);

        var recoveryRunner = new IB001FakeEdlRunner(
            session.Device,
            enableWrites: true,
            hardwareId: _g5.Toolchain.HardwareId);
        var recoveryClient = new EdlClient(
            _g5.Toolchain,
            session.Backup.Binding,
            recoveryRunner,
            new E003ScriptedEndpointDiscovery { Endpoints = new[] { E003EdlTestData.Endpoint() } });

        string currentParent = Path.Combine(caseDir, "restore-current");
        string restoreEvidenceParent = Path.Combine(caseDir, "restore-evidence");
        Directory.CreateDirectory(currentParent);
        Directory.CreateDirectory(restoreEvidenceParent);

        using InternalBackupSet currentDamaged = await InternalInstallBackup.CreateAsync(recoveryClient, currentParent);
        BootMaintenanceHistoricalIdentity history = await BootMaintenanceBackend.ReadHistoricalIdentityAsync(
            session.Backup.DirectoryPath,
            CancellationToken.None);

        using BootMaintenanceBackend restoreBackend = await BootMaintenanceBackend.CreatePreparedFromEdlAsync(
            MaintenanceMode.RestoreStock,
            history,
            recoveryClient,
            currentDamaged,
            new BootMaintenanceSources(OriginalBackupDirectory: session.Backup.DirectoryPath),
            recoveryRunner,
            restoreEvidenceParent,
            progress: null,
            CancellationToken.None);

        Assert.Contains(
            restoreBackend.Prepared.Regions,
            region => region.Role == "abl_a"
                && region.WillWrite
                && string.Equals(region.CandidateSha256, ablA.Sha256, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            restoreBackend.Prepared.Regions,
            region => region.Role == "abl_b"
                && region.WillWrite
                && string.Equals(region.CandidateSha256, ablB.Sha256, StringComparison.OrdinalIgnoreCase));

        BootMaintenanceResult restoreResult = await restoreBackend.ExecuteAsync(restoreBackend.Prepared.ConfirmationId);
        Assert.Equal(BootMaintenanceOutcome.Completed, restoreResult.Outcome);
        Assert.Equal(InternalInstallWriteState.AllVerified, restoreResult.WriteState);
        Assert.Contains("abl_a", restoreResult.VerifiedRegions);
        Assert.Contains("abl_b", restoreResult.VerifiedRegions);
    }

    private static VerifiedEdlFile WriteDeterministicFile(string directory, string name, int length, byte seed)
    {
        byte[] bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((seed + index * 17) % 251);
            if (bytes[index] == 0)
            {
                bytes[index] = 1;
            }
        }

        return WriteVerifiedFile(directory, name, bytes);
    }

    private static VerifiedEdlFile WriteVerifiedFile(string directory, string name, byte[] bytes)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return new VerifiedEdlFile(path, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string WriteMiscOverlay(string directory, string currentSlot, long miscSectorCount)
    {
        Directory.CreateDirectory(directory);
        byte[] control = (byte[])ControlBlockMethod.Invoke(
            null, new object?[] { "_" + currentSlot, (byte)0, null, null })!;
        byte[] controlPage = (byte[])BuildMiscMethod.Invoke(null, new object?[] { control })!;
        var overlay = new byte[miscSectorCount * IB001SyntheticDevice.SectorSize];
        Buffer.BlockCopy(controlPage, 0, overlay, 0, controlPage.Length);

        overlay[32768] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(overlay.AsSpan(32768 + 1), 0x56740AB0);
        overlay[32768 + 5] = 0;
        overlay[32768 + 6] = currentSlot == "b" ? (byte)1 : (byte)0;

        string path = Path.Combine(directory, "misc-overlay.img");
        File.WriteAllBytes(path, overlay);
        return path;
    }

    /// <summary>
    /// Minimal ELF64 LE container carrying one PT_LOAD FDT segment with
    /// <c>/soc/memorymap/memory@C7800000</c> (<c>mem-label = "Kernel\0"</c>,
    /// <c>device_type = "memory\0"</c>, <c>reg = &lt;0xC7800000 0x10000000&gt;</c>).
    /// </summary>
    private static byte[] BuildSyntheticGen5XblConfig()
    {
        byte[] fdt = BuildGen5KernelMemoryFdt();
        const int elfHeaderSize = 64;
        const int programHeaderSize = 56;
        const int payloadOffset = elfHeaderSize + programHeaderSize;

        byte[] elf = new byte[payloadOffset + fdt.Length];
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2; // ELFCLASS64
        elf[5] = 1; // ELFDATA2LSB
        elf[6] = 1; // EV_CURRENT
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16, 2), 2); // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18, 2), 0xB7); // EM_AARCH64
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(20, 4), 1); // EV_CURRENT
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(32, 8), elfHeaderSize); // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(52, 2), elfHeaderSize); // e_ehsize
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(54, 2), programHeaderSize); // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(56, 2), 1); // e_phnum

        Span<byte> phdr = elf.AsSpan(elfHeaderSize, programHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(phdr.Slice(0, 4), 1); // PT_LOAD
        BinaryPrimitives.WriteUInt64LittleEndian(phdr.Slice(8, 8), (ulong)payloadOffset); // p_offset
        BinaryPrimitives.WriteUInt64LittleEndian(phdr.Slice(32, 8), (ulong)fdt.Length); // p_filesz
        BinaryPrimitives.WriteUInt64LittleEndian(phdr.Slice(40, 8), (ulong)fdt.Length); // p_memsz

        Buffer.BlockCopy(fdt, 0, elf, payloadOffset, fdt.Length);
        return elf;
    }

    private static byte[] BuildGen5KernelMemoryFdt()
    {
        using var strings = new MemoryStream();
        int memLabelNameOff = AddString(strings, "mem-label");
        int deviceTypeNameOff = AddString(strings, "device_type");
        int regNameOff = AddString(strings, "reg");
        byte[] stringsBytes = strings.ToArray();

        using var dtStruct = new MemoryStream();
        WriteBeginNode(dtStruct, string.Empty);
        WriteBeginNode(dtStruct, "soc");
        WriteBeginNode(dtStruct, "memorymap");
        WriteBeginNode(dtStruct, "memory@C7800000");
        WriteProperty(dtStruct, memLabelNameOff, Encoding.ASCII.GetBytes("Kernel\0"));
        WriteProperty(dtStruct, deviceTypeNameOff, Encoding.ASCII.GetBytes("memory\0"));
        byte[] regValue = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(regValue.AsSpan(0, 8), 0xC7800000UL);
        BinaryPrimitives.WriteUInt64BigEndian(regValue.AsSpan(8, 8), 0x10000000UL);
        WriteProperty(dtStruct, regNameOff, regValue);
        WriteBeU32(dtStruct, 2); // end memory@C7800000
        WriteBeU32(dtStruct, 2); // end memorymap
        WriteBeU32(dtStruct, 2); // end soc
        WriteBeU32(dtStruct, 2); // end root
        WriteBeU32(dtStruct, 9); // FDT_END
        byte[] structBytes = dtStruct.ToArray();

        const int headerSize = 40;
        const int reserveOffset = headerSize;
        const int reserveSize = 16;
        int structOffset = reserveOffset + reserveSize;
        int stringsOffset = structOffset + structBytes.Length;
        int totalSize = stringsOffset + stringsBytes.Length;

        byte[] fdt = new byte[totalSize];
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(0, 4), 0xD00DFEEDu);
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(4, 4), (uint)totalSize);
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(8, 4), (uint)structOffset);
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(12, 4), (uint)stringsOffset);
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(16, 4), reserveOffset);
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(20, 4), 17u);
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(24, 4), 16u);
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(28, 4), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(32, 4), (uint)stringsBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(fdt.AsSpan(36, 4), (uint)structBytes.Length);

        Buffer.BlockCopy(structBytes, 0, fdt, structOffset, structBytes.Length);
        Buffer.BlockCopy(stringsBytes, 0, fdt, stringsOffset, stringsBytes.Length);
        return fdt;
    }

    private static int AddString(MemoryStream stream, string value)
    {
        int offset = checked((int)stream.Position);
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
        return offset;
    }

    private static void WriteBeginNode(MemoryStream stream, string name)
    {
        WriteBeU32(stream, 1u);
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
        Pad4(stream);
    }

    private static void WriteProperty(MemoryStream stream, int nameOffset, byte[] value)
    {
        WriteBeU32(stream, 3u);
        WriteBeU32(stream, (uint)value.Length);
        WriteBeU32(stream, (uint)nameOffset);
        stream.Write(value, 0, value.Length);
        Pad4(stream);
    }

    private static void WriteBeU32(MemoryStream stream, uint value)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, value);
        stream.Write(word);
    }

    private static void Pad4(MemoryStream stream)
    {
        while ((stream.Position & 3) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void AssertFullWrite(IB001Write write, EdlCapturedRegion region)
    {
        Assert.Equal(Convert.ToInt64(region.Lun), Convert.ToInt64(write.Lun));
        Assert.Equal(Convert.ToInt64(region.StartSector), Convert.ToInt64(write.StartSector));
        Assert.Equal(Convert.ToInt64(region.SectorCount), Convert.ToInt64(write.SectorCount));
        Assert.True(File.Exists(write.FilePath));
        Assert.Equal(region.Length, new FileInfo(write.FilePath).Length);
        using var stream = File.OpenRead(write.FilePath);
        Assert.Equal(write.Sha256, Convert.ToHexString(SHA256.HashData(stream)), ignoreCase: true);
    }

    private static void AssertBcbWrite(IB001Write write, EdlCapturedRegion misc)
    {
        long sectorSize = Convert.ToInt64(misc.SectorSize);
        long expectedUnits = (BcbUnitSize + sectorSize - 1) / sectorSize;
        Assert.Equal(Convert.ToInt64(misc.Lun), Convert.ToInt64(write.Lun));
        Assert.Equal(Convert.ToInt64(misc.StartSector), Convert.ToInt64(write.StartSector));
        Assert.Equal(expectedUnits, Convert.ToInt64(write.SectorCount));
        Assert.Equal(expectedUnits * sectorSize, new FileInfo(write.FilePath).Length);
    }

    private static bool OverlapsRegion(IB001Write write, EdlCapturedRegion region) =>
        write.Lun == region.Lun
        && write.StartSector < region.StartSector + region.SectorCount
        && region.StartSector < write.StartSector + write.SectorCount;

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}