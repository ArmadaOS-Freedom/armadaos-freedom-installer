using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvbFixture = FreedomInstaller.Core.Tests.EdlRawAvbChainTests.AvbFixture;

namespace FreedomInstaller.Core.Tests;

public sealed class SystemUpdateBootEnvelopeTests : IClassFixture<EdlRawGblEnvelopeTests.SharedFixture>
{
    private const string PublicTestKeyResourceName = "FreedomInstaller.AvbTestKey.pem";
    private const int BootHeaderPageSize = 4096;
    private const int AvbFooterSize = 64;

    private readonly EdlRawGblEnvelopeTests.SharedFixture _fx;

    public SystemUpdateBootEnvelopeTests(EdlRawGblEnvelopeTests.SharedFixture fx)
    {
        _fx = fx ?? throw new ArgumentNullException(nameof(fx));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task PrepareSystemUpdateEnvelopeAsync_AospResign_SucceedsWithChangedRawAndVerifiedMetadata(string slot)
    {
        using var scope = CreateCaseScope();
        EdlRawBuiltBoot built = GetBuiltForSlot(slot);
        using var aospKey = LoadPublicTestRsa();
        using var aospAvb = new AvbFixture(aospKey);
        string expectedAospKeySha = ComputeSha256Hex(aospAvb.KeyBlob);

        SlotInputs inputs = CreateSlotInputs(slot);
        VerifiedEdlFile vbmeta = WriteVbmetaRoot(scope.DirectoryPath, "vbmeta-aosp.img", aospAvb, aospAvb);
        VerifiedEdlFile changedRaw = CreateChangedRawCopy(built.PackedRaw, Path.Combine(scope.DirectoryPath, $"changed-raw-{slot}.bin"));
        string outputPath = Path.Combine(scope.DirectoryPath, $"candidate-aosp-{slot}.img");

        var snapshot = SnapshotFiles(built.CandidateBoot, inputs.VendorBoot, inputs.InitBoot, inputs.Abl, inputs.XblConfig, vbmeta, changedRaw);

        SystemUpdateBootEnvelope envelope = await EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
            slot,
            built.CandidateBoot,
            inputs.VendorBoot,
            inputs.InitBoot,
            inputs.Abl,
            inputs.XblConfig,
            vbmeta,
            changedRaw,
            built.SlotMaterial.LoaderBudget.MaxRawBytes,
            outputPath,
            SystemUpdateEnvelopeMode.AospResign);

        Assert.Equal(SystemUpdateEnvelopeMode.AospResign, envelope.Mode);
        Assert.True(envelope.PayloadDigestMatchesSignedMetadata);
        Assert.Equal(built.SlotMaterial.LoaderBudget.MaxRawBytes, envelope.RawBudgetBytes);
        Assert.Equal(built.CandidateBoot.Length, envelope.Candidate.Length);
        Assert.Equal(new FileInfo(outputPath).Length, envelope.Candidate.Length);
        Assert.Equal(ComputeFileSha256Hex(outputPath), envelope.Candidate.Sha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expectedAospKeySha, envelope.RootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expectedAospKeySha, envelope.BootPublicKeySha256, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(built.SlotMaterial.BaseImageLength, envelope.SourceMaterial.BaseImageLength);
        Assert.Equal(built.SlotMaterial.BaseImageSize, envelope.SourceMaterial.BaseImageSize);
        Assert.Equal(built.SlotMaterial.BaseTextOffset, envelope.SourceMaterial.BaseTextOffset);
        Assert.Equal(built.SlotMaterial.BaseImageSha256, envelope.SourceMaterial.BaseImageSha256, StringComparer.OrdinalIgnoreCase);

        byte[] decompressedKernel = DecompressCandidateKernel(outputPath);
        Assert.Equal(changedRaw.Length, decompressedKernel.LongLength);
        Assert.Equal(changedRaw.Sha256, ComputeSha256Hex(decompressedKernel), StringComparer.OrdinalIgnoreCase);

        AvbFooterInfo footer = ReadAvbFooter(outputPath);
        byte[] signedBlob = ReadSlice(outputPath, footer.VbmetaOffset, footer.VbmetaSize);
        AvbFixture.AssertAuthenticatedSignedBlob(signedBlob);
        Assert.Equal(ComputeSha256Hex(signedBlob), envelope.MetadataSha256, StringComparer.OrdinalIgnoreCase);

        AssertSnapshotUnchanged(snapshot);
    }

    [Fact]
    public async Task PrepareSystemUpdateEnvelopeAsync_GblPreserveMetadata_PreservesExactSignedBlobWithChangedRaw()
    {
        using var scope = CreateCaseScope();
        EdlRawBuiltBoot built = _fx.NormalBuilt;
        SlotInputs inputs = CreateSlotInputs("a");

        string gblSourcePath = Path.Combine(scope.DirectoryPath, "source-gbl-a.img");
        using IDisposable preservedSource = await CreateGblSourceAsync(built, _fx.NormalOriginal, gblSourcePath);
        VerifiedEdlFile gblSourceBoot = ClaimFile(gblSourcePath);

        VerifiedEdlFile vbmeta = WriteVbmetaRoot(scope.DirectoryPath, "vbmeta-oem.img", _fx.DefaultOem, _fx.DefaultOem);
        VerifiedEdlFile changedRaw = CreateChangedRawCopy(built.PackedRaw, Path.Combine(scope.DirectoryPath, "changed-raw-gbl.bin"));
        string outputPath = Path.Combine(scope.DirectoryPath, "candidate-gbl-a.img");

        AvbFooterInfo stockFooter = ReadAvbFooter(_fx.NormalOriginal.Path);
        byte[] stockSignedBlob = ReadSlice(_fx.NormalOriginal.Path, stockFooter.VbmetaOffset, stockFooter.VbmetaSize);
        AvbFooterInfo sourceFooter = ReadAvbFooter(gblSourceBoot.Path);
        byte[] sourceSignedBlob = ReadSlice(gblSourceBoot.Path, sourceFooter.VbmetaOffset, sourceFooter.VbmetaSize);
        Assert.Equal(stockSignedBlob, sourceSignedBlob);

        var snapshot = SnapshotFiles(gblSourceBoot, inputs.VendorBoot, inputs.InitBoot, inputs.Abl, inputs.XblConfig, vbmeta, changedRaw);

        SystemUpdateBootEnvelope envelope = await EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
            "a",
            gblSourceBoot,
            inputs.VendorBoot,
            inputs.InitBoot,
            inputs.Abl,
            inputs.XblConfig,
            vbmeta,
            changedRaw,
            built.SlotMaterial.LoaderBudget.MaxRawBytes,
            outputPath,
            SystemUpdateEnvelopeMode.GblPreserveMetadata);

        Assert.Equal(SystemUpdateEnvelopeMode.GblPreserveMetadata, envelope.Mode);
        Assert.False(envelope.PayloadDigestMatchesSignedMetadata);
        Assert.Equal(gblSourceBoot.Length, envelope.Candidate.Length);
        Assert.Equal(_fx.DefaultOemKeySha, envelope.RootPublicKeySha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_fx.DefaultOemKeySha, envelope.BootPublicKeySha256, StringComparer.OrdinalIgnoreCase);

        AvbFooterInfo candidateFooter = ReadAvbFooter(outputPath);
        byte[] candidateSignedBlob = ReadSlice(outputPath, candidateFooter.VbmetaOffset, candidateFooter.VbmetaSize);
        Assert.Equal(stockSignedBlob, candidateSignedBlob);
        AvbFixture.AssertAuthenticatedSignedBlob(candidateSignedBlob);
        Assert.Equal(ComputeSha256Hex(candidateSignedBlob), envelope.MetadataSha256, StringComparer.OrdinalIgnoreCase);

        byte[] decompressedKernel = DecompressCandidateKernel(outputPath);
        Assert.Equal(changedRaw.Length, decompressedKernel.LongLength);
        Assert.Equal(changedRaw.Sha256, ComputeSha256Hex(decompressedKernel), StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(gblSourceBoot.Sha256, envelope.Candidate.Sha256, StringComparer.OrdinalIgnoreCase);

        AssertSnapshotUnchanged(snapshot);
    }

    [Fact]
    public async Task PrepareSystemUpdateEnvelopeAsync_WrongChildChainOrMismatchedMode_RefusesAndLeavesNoCandidate()
    {
        using var scope = CreateCaseScope();
        EdlRawBuiltBoot built = _fx.NormalBuilt;
        using var aospKey = LoadPublicTestRsa();
        using var aospAvb = new AvbFixture(aospKey);
        SlotInputs inputs = CreateSlotInputs("a");
        VerifiedEdlFile changedRaw = CreateChangedRawCopy(built.PackedRaw, Path.Combine(scope.DirectoryPath, "changed-raw-chain.bin"));

        VerifiedEdlFile wrongChildVbmeta = WriteVbmetaRoot(scope.DirectoryPath, "vbmeta-wrong-child.img", aospAvb, _fx.OtherOem);
        string wrongChildOutput = Path.Combine(scope.DirectoryPath, "candidate-wrong-child.img");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
                "a",
                built.CandidateBoot,
                inputs.VendorBoot,
                inputs.InitBoot,
                inputs.Abl,
                inputs.XblConfig,
                wrongChildVbmeta,
                changedRaw,
                built.SlotMaterial.LoaderBudget.MaxRawBytes,
                wrongChildOutput,
                SystemUpdateEnvelopeMode.AospResign));
        Assert.False(File.Exists(wrongChildOutput));

        string gblSourcePath = Path.Combine(scope.DirectoryPath, "source-gbl-for-aosp-refusal.img");
        using IDisposable preservedSource = await CreateGblSourceAsync(built, _fx.NormalOriginal, gblSourcePath);
        VerifiedEdlFile gblSourceBoot = ClaimFile(gblSourcePath);
        VerifiedEdlFile oemVbmeta = WriteVbmetaRoot(scope.DirectoryPath, "vbmeta-oem-valid.img", _fx.DefaultOem, _fx.DefaultOem);
        string fallbackRefusalOutput = Path.Combine(scope.DirectoryPath, "candidate-no-fallback.img");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
                "a",
                gblSourceBoot,
                inputs.VendorBoot,
                inputs.InitBoot,
                inputs.Abl,
                inputs.XblConfig,
                oemVbmeta,
                changedRaw,
                built.SlotMaterial.LoaderBudget.MaxRawBytes,
                fallbackRefusalOutput,
                SystemUpdateEnvelopeMode.AospResign));
        Assert.False(File.Exists(fallbackRefusalOutput));
    }

    [Fact]
    public async Task PrepareSystemUpdateEnvelopeAsync_ModifiedAospSignedPaddingWithRefreshedClaim_RejectsSignedDigest()
    {
        using var scope = CreateCaseScope();
        EdlRawBuiltBoot built = _fx.NormalBuilt;
        using var aospKey = LoadPublicTestRsa();
        using var aospAvb = new AvbFixture(aospKey);
        SlotInputs inputs = CreateSlotInputs("a");
        VerifiedEdlFile vbmeta = WriteVbmetaRoot(scope.DirectoryPath, "vbmeta-aosp-tamper.img", aospAvb, aospAvb);
        VerifiedEdlFile changedRaw = CreateChangedRawCopy(built.PackedRaw, Path.Combine(scope.DirectoryPath, "changed-raw-tamper.bin"));

        string tamperedBootPath = Path.Combine(scope.DirectoryPath, "tampered-aosp-boot.img");
        File.Copy(built.CandidateBoot.Path, tamperedBootPath, overwrite: false);

        uint kernelSize = ReadBootHeaderKernelSize(tamperedBootPath);
        AvbFooterInfo footer = ReadAvbFooter(tamperedBootPath);
        long safePaddingOffset = BootHeaderPageSize + (long)kernelSize + 1L;
        Assert.True(safePaddingOffset < (long)footer.OriginalImageSize, "Safe padding offset must lie inside the AVB-hashed image payload.");

        using (var fs = new FileStream(tamperedBootPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Position = safePaddingOffset;
            int current = fs.ReadByte();
            Assert.InRange(current, 0, 255);
            fs.Position = safePaddingOffset;
            fs.WriteByte((byte)(current ^ 0xFF));
        }

        VerifiedEdlFile refreshedTamperedBoot = ClaimFile(tamperedBootPath);
        string outputPath = Path.Combine(scope.DirectoryPath, "candidate-tampered-padding.img");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
                "a",
                refreshedTamperedBoot,
                inputs.VendorBoot,
                inputs.InitBoot,
                inputs.Abl,
                inputs.XblConfig,
                vbmeta,
                changedRaw,
                built.SlotMaterial.LoaderBudget.MaxRawBytes,
                outputPath,
                SystemUpdateEnvelopeMode.AospResign));
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task PrepareSystemUpdateEnvelopeAsync_CrossSlotRawWithDifferentBase_RejectsAfterRepackAndDeletesCandidate()
    {
        using var scope = CreateCaseScope();
        EdlRawBuiltBoot slotABuilt = _fx.NormalBuilt;
        EdlRawBuiltBoot slotBBuilt = _fx.BudgetBuilt;
        Assert.NotEqual(slotABuilt.SlotMaterial.BaseImageSha256, slotBBuilt.SlotMaterial.BaseImageSha256, StringComparer.OrdinalIgnoreCase);
        Assert.True(slotBBuilt.PackedRaw.Length <= slotABuilt.SlotMaterial.LoaderBudget.MaxRawBytes);

        using var aospKey = LoadPublicTestRsa();
        using var aospAvb = new AvbFixture(aospKey);
        SlotInputs inputsA = CreateSlotInputs("a");
        VerifiedEdlFile vbmeta = WriteVbmetaRoot(scope.DirectoryPath, "vbmeta-aosp-cross.img", aospAvb, aospAvb);
        string outputPath = Path.Combine(scope.DirectoryPath, "candidate-cross-slot-base.img");

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
                "a",
                slotABuilt.CandidateBoot,
                inputsA.VendorBoot,
                inputsA.InitBoot,
                inputsA.Abl,
                inputsA.XblConfig,
                vbmeta,
                slotBBuilt.PackedRaw,
                slotABuilt.SlotMaterial.LoaderBudget.MaxRawBytes,
                outputPath,
                SystemUpdateEnvelopeMode.AospResign));

        Assert.Contains("base kernel identity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task PrepareSystemUpdateEnvelopeAsync_ExcessRawBudgetOrBadSourceClaim_RejectsAndLeavesNoCandidate()
    {
        using var scope = CreateCaseScope();
        EdlRawBuiltBoot built = _fx.NormalBuilt;
        using var aospKey = LoadPublicTestRsa();
        using var aospAvb = new AvbFixture(aospKey);
        SlotInputs inputs = CreateSlotInputs("a");
        VerifiedEdlFile vbmeta = WriteVbmetaRoot(scope.DirectoryPath, "vbmeta-aosp-bounds.img", aospAvb, aospAvb);
        VerifiedEdlFile changedRaw = CreateChangedRawCopy(built.PackedRaw, Path.Combine(scope.DirectoryPath, "changed-raw-bounds.bin"));

        string overBudgetOutput = Path.Combine(scope.DirectoryPath, "candidate-over-budget.img");
        long overBudget = checked(built.SlotMaterial.LoaderBudget.MaxRawBytes + 1L);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
                "a",
                built.CandidateBoot,
                inputs.VendorBoot,
                inputs.InitBoot,
                inputs.Abl,
                inputs.XblConfig,
                vbmeta,
                changedRaw,
                overBudget,
                overBudgetOutput,
                SystemUpdateEnvelopeMode.AospResign));
        Assert.False(File.Exists(overBudgetOutput));

        string badClaimSha = FlipFirstHexNibble(inputs.VendorBoot.Sha256);
        var badVendorClaim = new VerifiedEdlFile(inputs.VendorBoot.Path, inputs.VendorBoot.Length, badClaimSha);
        string badClaimOutput = Path.Combine(scope.DirectoryPath, "candidate-bad-claim.img");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
                "a",
                built.CandidateBoot,
                badVendorClaim,
                inputs.InitBoot,
                inputs.Abl,
                inputs.XblConfig,
                vbmeta,
                changedRaw,
                built.SlotMaterial.LoaderBudget.MaxRawBytes,
                badClaimOutput,
                SystemUpdateEnvelopeMode.AospResign));
        Assert.False(File.Exists(badClaimOutput));
    }

    [Fact]
    public async Task PrepareSystemUpdateEnvelopeAsync_ExistingOutputSentinelOrPrecancelled_PreservesExactSnapshots()
    {
        using var scope = CreateCaseScope();
        EdlRawBuiltBoot built = _fx.NormalBuilt;
        using var aospKey = LoadPublicTestRsa();
        using var aospAvb = new AvbFixture(aospKey);
        SlotInputs inputs = CreateSlotInputs("a");
        VerifiedEdlFile vbmeta = WriteVbmetaRoot(scope.DirectoryPath, "vbmeta-aosp-guards.img", aospAvb, aospAvb);
        VerifiedEdlFile changedRaw = CreateChangedRawCopy(built.PackedRaw, Path.Combine(scope.DirectoryPath, "changed-raw-guards.bin"));

        string existingOutput = Path.Combine(scope.DirectoryPath, "existing-sentinel.img");
        byte[] sentinelBytes = Encoding.UTF8.GetBytes("DO-NOT-OVERWRITE-SENTINEL");
        File.WriteAllBytes(existingOutput, sentinelBytes);
        VerifiedEdlFile sentinelClaim = ClaimFile(existingOutput);

        var snapshotWithSentinel = SnapshotFiles(
            built.CandidateBoot,
            inputs.VendorBoot,
            inputs.InitBoot,
            inputs.Abl,
            inputs.XblConfig,
            vbmeta,
            changedRaw,
            sentinelClaim);

        await Assert.ThrowsAsync<IOException>(() =>
            EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
                "a",
                built.CandidateBoot,
                inputs.VendorBoot,
                inputs.InitBoot,
                inputs.Abl,
                inputs.XblConfig,
                vbmeta,
                changedRaw,
                built.SlotMaterial.LoaderBudget.MaxRawBytes,
                existingOutput,
                SystemUpdateEnvelopeMode.AospResign));

        Assert.Equal(sentinelBytes, File.ReadAllBytes(existingOutput));
        AssertSnapshotUnchanged(snapshotWithSentinel);

        string cancelledOutput = Path.Combine(scope.DirectoryPath, "cancelled-candidate.img");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
                "a",
                built.CandidateBoot,
                inputs.VendorBoot,
                inputs.InitBoot,
                inputs.Abl,
                inputs.XblConfig,
                vbmeta,
                changedRaw,
                built.SlotMaterial.LoaderBudget.MaxRawBytes,
                cancelledOutput,
                SystemUpdateEnvelopeMode.AospResign,
                cts.Token));

        Assert.False(File.Exists(cancelledOutput));
        AssertSnapshotUnchanged(snapshotWithSentinel);
    }

    private EdlRawBuiltBoot GetBuiltForSlot(string slot) =>
        slot switch
        {
            "a" => _fx.NormalBuilt,
            "b" => _fx.BudgetBuilt,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };

    private SlotInputs CreateSlotInputs(string slot)
    {
        string slotDir = Path.Combine(_fx.Workspace.Root, $"slot-{slot}");
        return new SlotInputs(
            ClaimFile(Path.Combine(slotDir, $"vendor-{slot}.img")),
            ClaimFile(Path.Combine(slotDir, $"init-{slot}.img")),
            ClaimFile(Path.Combine(slotDir, $"abl-{slot}.bin")),
            ClaimFile(Path.Combine(slotDir, $"xbl-{slot}.bin")));
    }

    private CaseScope CreateCaseScope()
    {
        string dir = Path.Combine(_fx.Workspace.Root, "sys-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new CaseScope(dir);
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

    private static VerifiedEdlFile WriteVbmetaRoot(string directory, string fileName, AvbFixture rootAuthority, AvbFixture bootAuthority)
    {
        byte[] image = rootAuthority.BuildRootPartition(
            bootAuthority.ChainFor("boot", 3, 0),
            rootAuthority.ChainFor("recovery", 1, 0),
            rootAuthority.ChainFor("vbmeta_system", 2, 0));
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, image);
        return ClaimFile(path);
    }

    private static async Task<IDisposable> CreateGblSourceAsync(EdlRawBuiltBoot built, VerifiedEdlFile originalBoot, string candidatePath)
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

    private static VerifiedEdlFile CreateChangedRawCopy(VerifiedEdlFile sourceRaw, string destinationPath)
    {
        File.Copy(sourceRaw.Path, destinationPath, overwrite: false);

        using (var fs = new FileStream(destinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Span<byte> header = stackalloc byte[64];
            ReadExactly(fs, header);
            ulong manifestOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(32, 8));
            long targetOffset = checked((long)manifestOffset + 12L);
            Assert.InRange(targetOffset, 0L, fs.Length - 4L);

            fs.Position = targetOffset;
            Span<byte> word = stackalloc byte[4];
            ReadExactly(fs, word);
            uint originalDefault = BinaryPrimitives.ReadUInt32LittleEndian(word);
            Assert.NotEqual(0u, originalDefault);

            BinaryPrimitives.WriteUInt32LittleEndian(word, 0u);
            fs.Position = targetOffset;
            fs.Write(word);
        }

        VerifiedEdlFile mutated = ClaimFile(destinationPath);
        Assert.Equal(sourceRaw.Length, mutated.Length);
        Assert.NotEqual(sourceRaw.Sha256, mutated.Sha256, StringComparer.OrdinalIgnoreCase);
        return mutated;
    }

    private static uint ReadBootHeaderKernelSize(string bootPath)
    {
        using var fs = new FileStream(bootPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[16];
        ReadExactly(fs, header);
        Assert.Equal("ANDROID!", Encoding.ASCII.GetString(header.Slice(0, 8)));
        uint kernelSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
        Assert.True(kernelSize > 0u);
        return kernelSize;
    }

    private static byte[] DecompressCandidateKernel(string bootPath)
    {
        uint kernelSize = ReadBootHeaderKernelSize(bootPath);
        byte[] compressed = ReadSlice(bootPath, BootHeaderPageSize, kernelSize);
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static AvbFooterInfo ReadAvbFooter(string bootPath)
    {
        using var fs = new FileStream(bootPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(fs.Length >= AvbFooterSize);
        fs.Position = fs.Length - AvbFooterSize;
        Span<byte> footer = stackalloc byte[AvbFooterSize];
        ReadExactly(fs, footer);
        Assert.Equal("AVBf", Encoding.ASCII.GetString(footer.Slice(0, 4)));

        ulong originalImageSize = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(12, 8));
        ulong vbmetaOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(20, 8));
        ulong vbmetaSize = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(28, 8));
        Assert.True(vbmetaSize > 0UL);
        Assert.True(checked((long)(vbmetaOffset + vbmetaSize)) <= fs.Length - AvbFooterSize);

        return new AvbFooterInfo(originalImageSize, vbmetaOffset, vbmetaSize);
    }

    private static byte[] ReadSlice(string path, ulong offset, ulong size)
    {
        int checkedSize = checked((int)size);
        byte[] buffer = new byte[checkedSize];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = checked((long)offset);
        ReadExactly(fs, buffer);
        return buffer;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer.Slice(total));
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of stream while reading test fixture artifact.");
            total += read;
        }
    }

    private static VerifiedEdlFile ClaimFile(string path)
    {
        var info = new FileInfo(path);
        Assert.True(info.Exists, $"Expected file to exist: {path}");
        return new VerifiedEdlFile(info.FullName, info.Length, ComputeFileSha256Hex(info.FullName));
    }

    private static string ComputeFileSha256Hex(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FlipFirstHexNibble(string sha256)
    {
        Assert.False(string.IsNullOrEmpty(sha256));
        char replacement = char.ToLowerInvariant(sha256[0]) == 'a' ? 'b' : 'a';
        return replacement + sha256.Substring(1);
    }

    private static IReadOnlyList<VerifiedEdlFile> SnapshotFiles(params VerifiedEdlFile[] files)
    {
        var list = new List<VerifiedEdlFile>(files.Length);
        foreach (VerifiedEdlFile file in files)
        {
            VerifiedEdlFile actual = ClaimFile(file.Path);
            Assert.Equal(file.Length, actual.Length);
            Assert.Equal(file.Sha256, actual.Sha256, StringComparer.OrdinalIgnoreCase);
            list.Add(actual);
        }
        return list;
    }

    private static void AssertSnapshotUnchanged(IReadOnlyList<VerifiedEdlFile> snapshot)
    {
        foreach (VerifiedEdlFile expected in snapshot)
        {
            VerifiedEdlFile current = ClaimFile(expected.Path);
            Assert.Equal(expected.Length, current.Length);
            Assert.Equal(expected.Sha256, current.Sha256, StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed record SlotInputs(
        VerifiedEdlFile VendorBoot,
        VerifiedEdlFile InitBoot,
        VerifiedEdlFile Abl,
        VerifiedEdlFile XblConfig);

    private readonly record struct AvbFooterInfo(
        ulong OriginalImageSize,
        ulong VbmetaOffset,
        ulong VbmetaSize);

    private sealed class CaseScope : IDisposable
    {
        public string DirectoryPath { get; }

        public CaseScope(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                    Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
