using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;
using AvbFixture = FreedomInstaller.Core.Tests.EdlRawAvbChainTests.AvbFixture;

namespace FreedomInstaller.Core.Tests;

public sealed partial class SystemUpdateTrustTests : IClassFixture<SystemUpdateTrustTests.SharedFixture>
{
    private const string PublicTestKeyResourceName = "FreedomInstaller.AvbTestKey.pem";
    private const long EfispPartitionBytes = 3L * 1024 * 1024;

    private readonly SharedFixture _fx;

    public SystemUpdateTrustTests(SharedFixture fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task Aosp_Unlocked_Succeeds_WithDistinctPreservedSlotBases_AndUnchangedInputs()
    {
        var snapshot = CaptureSnapshot(_fx.AospA, _fx.AospB, efisp: null);

        SystemUpdateTrust trust = await EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
            "TB323FU",
            BootloaderState.Unlocked,
            _fx.AospA,
            _fx.AospB,
            efisp: null,
            CancellationToken.None);

        Assert.Equal("TB323FU", trust.Model);
        Assert.Equal(SystemUpdateEnvelopeMode.AospResign, trust.Mode);
        Assert.Equal(BootloaderState.Unlocked, trust.LockState);
        Assert.Null(trust.GblRegion);
        Assert.Null(trust.EfispSha256);

        Assert.Equal("a", trust.A.Slot);
        Assert.Equal("b", trust.B.Slot);
        Assert.True(trust.A.Material.IsKshim);
        Assert.True(trust.B.Material.IsKshim);
        Assert.True(trust.A.Material.LoaderBudget.KnownLoaderMatch);
        Assert.True(trust.B.Material.LoaderBudget.KnownLoaderMatch);
        Assert.True(trust.A.MaxRawBytes > 0);
        Assert.True(trust.B.MaxRawBytes > 0);
        Assert.Equal(trust.A.Material.LoaderBudget.MaxRawBytes, trust.A.MaxRawBytes);
        Assert.Equal(trust.B.Material.LoaderBudget.MaxRawBytes, trust.B.MaxRawBytes);

        Assert.True(trust.A.PayloadDigestMatchesSignedMetadata);
        Assert.True(trust.B.PayloadDigestMatchesSignedMetadata);
        Assert.Null(trust.A.AblRootPublicKeySha256);
        Assert.Null(trust.B.AblRootPublicKeySha256);

        Assert.Equal(_fx.AospPublicKeySha256, trust.A.RootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_fx.AospPublicKeySha256, trust.B.RootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_fx.AospPublicKeySha256, trust.A.BootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_fx.AospPublicKeySha256, trust.B.BootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(trust.A.BootPublicKeySha256, trust.B.BootPublicKeySha256, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(_fx.Built["a"].SlotMaterial.BaseImageSha256, trust.A.Material.BaseImageSha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_fx.Built["b"].SlotMaterial.BaseImageSha256, trust.B.Material.BaseImageSha256, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(trust.A.Material.BaseImageSha256, trust.B.Material.BaseImageSha256);

        AssertSnapshotUnchanged(snapshot);
    }

    [Theory]
    [InlineData(BootloaderState.Locked)]
    [InlineData(BootloaderState.Unknown)]
    [InlineData(BootloaderState.Unlocked)]
    public async Task Gbl_AllLockStates_Succeed_WithPrcEfisp_RootAndChildKeys_AndUnchangedInputs(BootloaderState lockState)
    {
        var snapshot = CaptureSnapshot(_fx.GblA, _fx.GblB, _fx.PrcEfisp);

        SystemUpdateTrust trust = await EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
            "TB323FU",
            lockState,
            _fx.GblA,
            _fx.GblB,
            _fx.PrcEfisp,
            CancellationToken.None);

        Assert.Equal("TB323FU", trust.Model);
        Assert.Equal(SystemUpdateEnvelopeMode.GblPreserveMetadata, trust.Mode);
        Assert.Equal(lockState, trust.LockState);
        Assert.Equal("PRC", trust.GblRegion);
        Assert.Equal(_fx.PrcEfisp.Sha256, trust.EfispSha256, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("a", trust.A.Slot);
        Assert.Equal("b", trust.B.Slot);
        Assert.True(trust.A.Material.IsKshim);
        Assert.True(trust.B.Material.IsKshim);
        Assert.True(trust.A.MaxRawBytes > 0);
        Assert.True(trust.B.MaxRawBytes > 0);
        Assert.True(trust.A.MaxRawBytes <= trust.A.Material.LoaderBudget.MaxRawBytes);
        Assert.True(trust.B.MaxRawBytes <= trust.B.Material.LoaderBudget.MaxRawBytes);

        Assert.False(trust.A.PayloadDigestMatchesSignedMetadata);
        Assert.False(trust.B.PayloadDigestMatchesSignedMetadata);
        Assert.Null(trust.A.AblRootPublicKeySha256);
        Assert.Null(trust.B.AblRootPublicKeySha256);

        Assert.Equal(_fx.Source.ParentPublicKeySha256, trust.A.RootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_fx.Source.ParentPublicKeySha256, trust.B.RootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_fx.Source.BootChildPublicKeySha256, trust.A.BootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_fx.Source.BootChildPublicKeySha256, trust.B.BootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(trust.A.RootPublicKeySha256, trust.A.BootPublicKeySha256);

        Assert.Equal(_fx.Built["a"].SlotMaterial.BaseImageSha256, trust.A.Material.BaseImageSha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_fx.Built["b"].SlotMaterial.BaseImageSha256, trust.B.Material.BaseImageSha256, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(trust.A.Material.BaseImageSha256, trust.B.Material.BaseImageSha256);

        AssertSnapshotUnchanged(snapshot);
    }

    [Theory]
    [InlineData(BootloaderState.Locked)]
    [InlineData(BootloaderState.Unknown)]
    public async Task Aosp_LockedOrUnknown_RejectsOemAblKeyMismatch(BootloaderState lockState)
    {
        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                lockState,
                _fx.AospA,
                _fx.AospB,
                efisp: null,
                CancellationToken.None));

        Assert.Contains("ABL AVB key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MixedModes_AndEfispViolations_AreRefused()
    {
        InvalidDataException mixedEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA,
                _fx.GblB,
                _fx.PrcEfisp,
                CancellationToken.None));
        Assert.Contains("mixed trust modes", mixedEx.Message, StringComparison.OrdinalIgnoreCase);

        InvalidDataException missingEfispEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Locked,
                _fx.GblA,
                _fx.GblB,
                efisp: null,
                CancellationToken.None));
        Assert.Contains("efisp", missingEfispEx.Message, StringComparison.OrdinalIgnoreCase);

        InvalidDataException rowOnPrcEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Locked,
                _fx.GblA,
                _fx.GblB,
                _fx.RowEfisp,
                CancellationToken.None));
        Assert.Contains("prefix", rowOnPrcEx.Message, StringComparison.OrdinalIgnoreCase);

        string badPrefixPath = _fx.NewCaseFilePath("efisp-bad-prefix.img");
        File.Copy(_fx.PrcEfisp.Path, badPrefixPath, overwrite: true);
        FlipByteAt(badPrefixPath, 16);
        VerifiedEdlFile badPrefixEfisp = ClaimFile(badPrefixPath);

        InvalidDataException prefixEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Locked,
                _fx.GblA,
                _fx.GblB,
                badPrefixEfisp,
                CancellationToken.None));
        Assert.Contains("prefix", prefixEx.Message, StringComparison.OrdinalIgnoreCase);

        string badTailPath = _fx.NewCaseFilePath("efisp-bad-tail.img");
        File.Copy(_fx.PrcEfisp.Path, badTailPath, overwrite: true);
        FlipByteAt(badTailPath, EfispPartitionBytes - 1);
        VerifiedEdlFile badTailEfisp = ClaimFile(badTailPath);

        InvalidDataException tailEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Locked,
                _fx.GblA,
                _fx.GblB,
                badTailEfisp,
                CancellationToken.None));
        Assert.Contains("tail", tailEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AvbChainViolations_ChildMismatch_CorruptedRootSignature_AndWrongBootChain_AreRefused()
    {
        using var otherKey = RSA.Create(4096);
        using var otherAvb = new AvbFixture(otherKey);

        VerifiedEdlFile otherRecovery = ClaimFile(otherAvb.RecoveryPath);
        InvalidDataException recEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA with { Recovery = otherRecovery },
                _fx.AospB,
                efisp: null,
                CancellationToken.None));
        Assert.Contains("recovery", recEx.Message, StringComparison.OrdinalIgnoreCase);

        VerifiedEdlFile otherVbmetaSystem = ClaimFile(otherAvb.VbmetaSystemPath);
        InvalidDataException sysEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA with { VbmetaSystem = otherVbmetaSystem },
                _fx.AospB,
                efisp: null,
                CancellationToken.None));
        Assert.Contains("vbmeta_system", sysEx.Message, StringComparison.OrdinalIgnoreCase);

        string corruptedVbmetaPath = _fx.NewCaseFilePath("vbmeta-corrupt-sig.img");
        File.Copy(_fx.AospA.Vbmeta.Path, corruptedVbmetaPath, overwrite: true);
        byte[] vbmetaHeader = ReadRange(corruptedVbmetaPath, 0, 64);
        ulong authenticationSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaHeader.AsSpan(12, 8));
        ulong signatureOffset = BinaryPrimitives.ReadUInt64BigEndian(vbmetaHeader.AsSpan(48, 8));
        ulong signatureSize = BinaryPrimitives.ReadUInt64BigEndian(vbmetaHeader.AsSpan(56, 8));
        Assert.True(signatureSize > 0);
        Assert.True(signatureOffset <= authenticationSize && signatureSize <= authenticationSize - signatureOffset);
        FlipByteAt(corruptedVbmetaPath, checked(256L + (long)signatureOffset));
        VerifiedEdlFile corruptedVbmeta = ClaimFile(corruptedVbmetaPath);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA with { Vbmeta = corruptedVbmeta },
                _fx.AospB,
                efisp: null,
                CancellationToken.None));

        byte[] wrongBootChainRoot = _fx.AospAvb.BuildRootPartition(
            otherAvb.ChainFor("boot", 3, 4),
            _fx.AospAvb.ChainFor("recovery", 1, 8),
            _fx.AospAvb.ChainFor("vbmeta_system", 2, 16));
        string wrongBootRootPath = _fx.NewCaseFilePath("vbmeta-wrong-boot-chain.img");
        File.WriteAllBytes(wrongBootRootPath, wrongBootChainRoot);
        VerifiedEdlFile wrongBootRoot = ClaimFile(wrongBootRootPath);

        InvalidDataException bootChainEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA with { Vbmeta = wrongBootRoot },
                _fx.AospB,
                efisp: null,
                CancellationToken.None));
        Assert.Contains("boot leaf key", bootChainEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnrecognizedXblBudget_AndNonKshimStockBoot_AreRefused()
    {
        string badXblPath = _fx.NewCaseFilePath("xbl-unrecognized.elf");
        File.WriteAllBytes(badXblPath, new byte[4096]);
        VerifiedEdlFile badXbl = ClaimFile(badXblPath);

        InvalidDataException xblEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA with { XblConfig = badXbl },
                _fx.AospB,
                efisp: null,
                CancellationToken.None));
        Assert.Contains("loader budget", xblEx.Message, StringComparison.OrdinalIgnoreCase);

        InvalidDataException stockBootEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Locked,
                _fx.GblA with { Boot = _fx.Source.Sources["boot_a"] },
                _fx.GblB,
                _fx.PrcEfisp,
                CancellationToken.None));
        Assert.Contains("K shim", stockBootEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Aosp_BootPayloadPaddingCorruption_AndBadClaimSha_AreRefused()
    {
        string mutatedBootPath = _fx.NewCaseFilePath("boot-a-padding-mutated.img");
        File.Copy(_fx.AospA.Boot.Path, mutatedBootPath, overwrite: true);

        byte[] header = ReadRange(mutatedBootPath, 0, 16);
        Assert.Equal("ANDROID!", Encoding.ASCII.GetString(header, 0, 8));
        uint kernelSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));

        byte[] footer = ReadRange(mutatedBootPath, _fx.AospA.Boot.Length - 64, 64);
        Assert.Equal("AVBf", Encoding.ASCII.GetString(footer, 0, 4));
        ulong originalImageSize = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(12, 8));

        long mutateOffset = checked((long)kernelSize + 4096L + 1L);
        Assert.True(mutateOffset < (long)originalImageSize);
        FlipByteAt(mutatedBootPath, mutateOffset);

        VerifiedEdlFile mutatedBootClaim = ClaimFile(mutatedBootPath);

        InvalidDataException digestEx = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA with { Boot = mutatedBootClaim },
                _fx.AospB,
                efisp: null,
                CancellationToken.None));
        Assert.Contains("payload digest", digestEx.Message, StringComparison.OrdinalIgnoreCase);

        string badSha = _fx.AospA.Vbmeta.Sha256[0] == '0'
            ? "1" + _fx.AospA.Vbmeta.Sha256.Substring(1)
            : "0" + _fx.AospA.Vbmeta.Sha256.Substring(1);
        var badClaim = new VerifiedEdlFile(_fx.AospA.Vbmeta.Path, _fx.AospA.Vbmeta.Length, badSha);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA with { Vbmeta = badClaim },
                _fx.AospB,
                efisp: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task GuardClauses_Model_Enum_NullClaims_AndPreCanceledToken_ThrowExpectedExceptions()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "INVALID_MODEL",
                BootloaderState.Unlocked,
                _fx.AospA,
                _fx.AospB,
                efisp: null,
                CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                (BootloaderState)999,
                _fx.AospA,
                _fx.AospB,
                efisp: null,
                CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                null!,
                _fx.AospB,
                efisp: null,
                CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA with { Boot = null! },
                _fx.AospB,
                efisp: null,
                CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EdlRawBootBuilder.InspectSystemUpdateTrustAsync(
                "TB323FU",
                BootloaderState.Unlocked,
                _fx.AospA,
                _fx.AospB,
                efisp: null,
                cts.Token));
    }

    private static IReadOnlyList<(string Path, long Length, string Sha256)> CaptureSnapshot(
        SystemUpdateSlotFiles a,
        SystemUpdateSlotFiles b,
        VerifiedEdlFile? efisp)
    {
        var list = new List<(string Path, long Length, string Sha256)>(17);
        AddSlot(list, a);
        AddSlot(list, b);
        if (efisp is not null)
        {
            list.Add((efisp.Path, new FileInfo(efisp.Path).Length, Sha256File(efisp.Path)));
        }
        return list;

        static void AddSlot(List<(string Path, long Length, string Sha256)> target, SystemUpdateSlotFiles s)
        {
            foreach (VerifiedEdlFile f in new[]
                     {
                         s.Boot, s.VendorBoot, s.InitBoot, s.Abl,
                         s.XblConfig, s.Vbmeta, s.Recovery, s.VbmetaSystem
                     })
            {
                target.Add((f.Path, new FileInfo(f.Path).Length, Sha256File(f.Path)));
            }
        }
    }

    private static void AssertSnapshotUnchanged(IReadOnlyList<(string Path, long Length, string Sha256)> snapshot)
    {
        foreach ((string path, long length, string sha) in snapshot)
        {
            var info = new FileInfo(path);
            Assert.True(info.Exists);
            Assert.Equal(length, info.Length);
            Assert.Equal(sha, Sha256File(path));
        }
    }

    internal static VerifiedEdlFile ClaimFile(string path)
    {
        string full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        Assert.True(info.Exists, "Expected file to exist: " + full);
        return new VerifiedEdlFile(full, info.Length, Sha256File(full));
    }

    internal static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static byte[] ReadRange(string path, long offset, int count)
    {
        byte[] buffer = new byte[count];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = offset;
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0)
                throw new EndOfStreamException();
            read += n;
        }
        return buffer;
    }

    internal static void FlipByteAt(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int b = stream.ReadByte();
        Assert.InRange(b, 0, 255);
        stream.Position = offset;
        stream.WriteByte((byte)(b ^ 0xFF));
    }

    public sealed class SharedFixture : IAsyncLifetime, IDisposable
    {
        private readonly List<IDisposable> _gblLeases = new();
        private RSA? _aospRsa;
        private int _disposed;

        internal EdlRawBootBuilderTests.Workspace Workspace { get; private set; } = null!;
        internal GblDirectSourceFixture Source { get; private set; } = null!;
        internal EdlRawBootTools Tools { get; private set; } = null!;
        internal EdlRawBootBuilderTests.SyntheticPackage Package { get; private set; } = null!;
        internal Dictionary<string, EdlRawBuiltBoot> Built { get; } = new(StringComparer.Ordinal);
        internal AvbFixture AospAvb { get; private set; } = null!;
        internal string AospPublicKeySha256 { get; private set; } = string.Empty;

        internal SystemUpdateSlotFiles AospA { get; private set; } = null!;
        internal SystemUpdateSlotFiles AospB { get; private set; } = null!;
        internal SystemUpdateSlotFiles GblA { get; private set; } = null!;
        internal SystemUpdateSlotFiles GblB { get; private set; } = null!;
        internal VerifiedEdlFile PrcEfisp { get; private set; } = null!;
        internal VerifiedEdlFile RowEfisp { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            try
            {
                Workspace = new EdlRawBootBuilderTests.Workspace();
                Source = new GblDirectSourceFixture("PRC");
                Tools = await EdlRawBootBuilderTests.LoadToolsAsync().ConfigureAwait(false);
                Package = new EdlRawBootBuilderTests.SyntheticPackage(deviceModel: "TB323FU");

                string packer = RequireVariable("EDL_RAW_LINUX_PACKER_PATH");
                string unpacker = RequireVariable("EDL_RAW_LINUX_UNPACKER_PATH");
                var runner = new EdlRawBootBuilderTests.LinuxOracleRunner(Tools, packer, unpacker);

                foreach (string slot in new[] { "a", "b" })
                {
                    string buildDir = Path.Combine(Workspace.Root, "build-" + slot);
                    Directory.CreateDirectory(buildDir);
                    var req = new EdlRawBootBuildRequest(
                        slot,
                        Source.Sources["boot_" + slot],
                        Source.Sources["vendor_boot_" + slot],
                        Source.Sources["init_boot_" + slot],
                        Source.Sources["abl_" + slot],
                        Source.Sources["xbl_config_" + slot],
                        Package.Contract,
                        Tools,
                        buildDir);
                    EdlRawBuiltBoot built = await EdlRawBootBuilder.BuildAsync(req, runner).ConfigureAwait(false);
                    Built[slot] = built;
                }

                _aospRsa = LoadPublicTestRsa();
                AospAvb = new AvbFixture(_aospRsa);
                AospPublicKeySha256 = Convert.ToHexString(SHA256.HashData(AospAvb.KeyBlob));

                VerifiedEdlFile aospVbmeta = ClaimFile(AospAvb.VbmetaPath);
                VerifiedEdlFile aospRecovery = ClaimFile(AospAvb.RecoveryPath);
                VerifiedEdlFile aospVbmetaSystem = ClaimFile(AospAvb.VbmetaSystemPath);

                AospA = new SystemUpdateSlotFiles(
                    Built["a"].CandidateBoot,
                    Source.Sources["vendor_boot_a"],
                    Source.Sources["init_boot_a"],
                    Source.Sources["abl_a"],
                    Source.Sources["xbl_config_a"],
                    aospVbmeta,
                    aospRecovery,
                    aospVbmetaSystem);

                AospB = new SystemUpdateSlotFiles(
                    Built["b"].CandidateBoot,
                    Source.Sources["vendor_boot_b"],
                    Source.Sources["init_boot_b"],
                    Source.Sources["abl_b"],
                    Source.Sources["xbl_config_b"],
                    aospVbmeta,
                    aospRecovery,
                    aospVbmetaSystem);

                string gblBootPathA = Path.Combine(Workspace.Root, "gbl-boot-a.img");
                _gblLeases.Add(await CreateGblSourceAsync(Built["a"], Source.Sources["boot_a"], gblBootPathA).ConfigureAwait(false));
                VerifiedEdlFile gblBootA = ClaimFile(gblBootPathA);

                string gblBootPathB = Path.Combine(Workspace.Root, "gbl-boot-b.img");
                _gblLeases.Add(await CreateGblSourceAsync(Built["b"], Source.Sources["boot_b"], gblBootPathB).ConfigureAwait(false));
                VerifiedEdlFile gblBootB = ClaimFile(gblBootPathB);

                GblA = new SystemUpdateSlotFiles(
                    gblBootA,
                    Source.Sources["vendor_boot_a"],
                    Source.Sources["init_boot_a"],
                    Source.Sources["abl_a"],
                    Source.Sources["xbl_config_a"],
                    Source.AvbSources["vbmeta_a"],
                    Source.AvbSources["recovery_a"],
                    Source.AvbSources["vbmeta_system_a"]);

                GblB = new SystemUpdateSlotFiles(
                    gblBootB,
                    Source.Sources["vendor_boot_b"],
                    Source.Sources["init_boot_b"],
                    Source.Sources["abl_b"],
                    Source.Sources["xbl_config_b"],
                    Source.AvbSources["vbmeta_b"],
                    Source.AvbSources["recovery_b"],
                    Source.AvbSources["vbmeta_system_b"]);

                byte[] prcBytes = await ReadGblAssetBytesAsync("Prc").ConfigureAwait(false);
                byte[] rowBytes = await ReadGblAssetBytesAsync("Row").ConfigureAwait(false);
                PrcEfisp = WriteEfispPartition(Path.Combine(Workspace.Root, "efisp-prc.img"), prcBytes);
                RowEfisp = WriteEfispPartition(Path.Combine(Workspace.Root, "efisp-row.img"), rowBytes);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Task DisposeAsync()
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            foreach (IDisposable lease in _gblLeases)
            {
                try { lease.Dispose(); } catch { }
            }
            _gblLeases.Clear();

            foreach (EdlRawBuiltBoot built in Built.Values)
            {
                try { built.Dispose(); } catch { }
            }
            Built.Clear();

            try { AospAvb?.Dispose(); } catch { }
            try { _aospRsa?.Dispose(); } catch { }
            try { Package?.Dispose(); } catch { }
            try { Tools?.Dispose(); } catch { }
            try { Source?.Dispose(); } catch { }
            try { Workspace?.Dispose(); } catch { }
        }

        internal string NewCaseFilePath(string name)
        {
            string dir = Path.Combine(Workspace.Root, "case-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        private static string RequireVariable(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            Assert.False(string.IsNullOrWhiteSpace(value), $"Required environment variable '{name}' is not set.");
            return value!;
        }

        private static RSA LoadPublicTestRsa()
        {
            Assembly coreAssembly = typeof(EdlRawBootBuilder).Assembly;
            using Stream? stream = coreAssembly.GetManifestResourceStream(PublicTestKeyResourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!, Encoding.UTF8);
            string pem = reader.ReadToEnd();
            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(pem);
                return rsa;
            }
            catch
            {
                rsa.Dispose();
                throw;
            }
        }

        private static async Task<IDisposable> CreateGblSourceAsync(
            EdlRawBuiltBoot built,
            VerifiedEdlFile originalBoot,
            string candidatePath)
        {
            MethodInfo? method = typeof(EdlRawBootBuilder).GetMethod(
                "PreserveOriginalSignedBootMetadataAsync",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);

            object? taskObj = method!.Invoke(null, new object[] { built, originalBoot, candidatePath, CancellationToken.None });
            Assert.NotNull(taskObj);
            var task = Assert.IsAssignableFrom<Task>(taskObj);
            await task.ConfigureAwait(false);

            PropertyInfo? resultProp = taskObj!.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(resultProp);
            object? result = resultProp!.GetValue(taskObj);
            Assert.NotNull(result);
            Assert.True(File.Exists(candidatePath));

            return Assert.IsAssignableFrom<IDisposable>(result);
        }

        private static async Task<byte[]> ReadGblAssetBytesAsync(string regionName)
        {
            Assembly coreAssembly = typeof(EdlRawBootBuilder).Assembly;
            Type? assetsType = coreAssembly.GetType("FreedomInstaller.Core.Gbl.GblRuntimeAssets", throwOnError: false);
            Type? regionType = coreAssembly.GetType("FreedomInstaller.Core.Gbl.GblRuntimeRegion", throwOnError: false);
            Assert.NotNull(assetsType);
            Assert.NotNull(regionType);

            MethodInfo? readMethod = assetsType!.GetMethod(
                "ReadVerifiedBytesAsync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(readMethod);

            ParameterInfo[] parameters = readMethod!.GetParameters();
            Assert.Equal(2, parameters.Length);
            object regionValue = Enum.Parse(parameters[0].ParameterType, regionName, ignoreCase: true);

            object? taskObj = readMethod.Invoke(null, new object[] { regionValue, CancellationToken.None });
            Assert.NotNull(taskObj);
            var task = Assert.IsAssignableFrom<Task<byte[]>>(taskObj);
            return await task.ConfigureAwait(false);
        }

        private static VerifiedEdlFile WriteEfispPartition(string path, byte[] prefix)
        {
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(prefix, 0, prefix.Length);
                fs.SetLength(EfispPartitionBytes);
            }
            return ClaimFile(path);
        }
    }
}
