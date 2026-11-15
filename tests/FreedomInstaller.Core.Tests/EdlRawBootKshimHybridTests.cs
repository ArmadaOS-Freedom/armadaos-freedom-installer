// AVB-32G-01 regression: a boot slot already wrapped by the project carries the
// kshim composite in the footer while its authenticated vbmeta still signs the
// previous leaf. Every fixture here is the genuine native oracle build plus the
// genuine OEM signature; the hybrid source is produced by the existing
// preservation helper, never hand-assembled. Oracles are independent BCL reads:
// footer fields, RSA authentication of the emitted blob, salted digests over the
// physical candidate, gzip round-trip and whole-file hashes. No device, signing
// key or hardware claim is made.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;
using AvbFixture = FreedomInstaller.Core.Tests.EdlRawAvbChainTests.AvbFixture;

namespace FreedomInstaller.Core.Tests;

public sealed class EdlRawBootKshimHybridTests
    : IClassFixture<EdlRawGblEnvelopeTests.SharedFixture>, IDisposable
{
    private const long RawBudgetBytes = 64L * 1024L * 1024L;

    private const int FooterSizeBytes = 64;

    private const int VbmetaHeaderSizeBytes = 256;

    private const int MaxMetadataArrayBytes = 1024 * 1024;

    private readonly EdlRawGblEnvelopeTests.SharedFixture _fixture;

    private readonly List<IDisposable> _stagedSources = new();

    public EdlRawBootKshimHybridTests(EdlRawGblEnvelopeTests.SharedFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        foreach (IDisposable staged in _stagedSources)
        {
            staged.Dispose();
        }

        _stagedSources.Clear();
    }

    // ------------------------------------------------------------ 1. the real build path

    [Fact]
    public async Task FullBuild_ProjectWrappedKshimHybrid_TwoOwnerChain_PreservesOemLeafOverRebuiltCandidate()
    {
        EdlRawGblEnvelopeTests.SharedFixture fx = _fixture;
        string caseDir = NewCaseDirectory(fx);
        string hybridPath = Path.Combine(caseDir, "wrapped-kshim-source.img");
        await StageWrappedKshimSourceAsync(fx, hybridPath);

        // Reproduced failure precondition: the footer is the new composite payload while the
        // authenticated descriptor still signs the previous leaf size.
        (long hybridImageSize, long hybridBlobOffset, long hybridBlobSize) = ReadFooter(hybridPath);
        byte[] sourceBlob = ReadRange(hybridPath, hybridBlobOffset, hybridBlobSize);
        AvbFixture.AssertAuthenticatedSignedBlob(sourceBlob);
        (long signedOldSize, byte[] sourceSalt, byte[] sourceDigest) = ParseBootHashDescriptor(sourceBlob);
        Assert.NotEqual(hybridImageSize, signedOldSize);
        Assert.Equal(ReadFooter(fx.NormalOriginal.Path).ImageSize, signedOldSize);

        byte[] hybridHeader = ReadRange(hybridPath, 0, 4096);
        Assert.Equal("ANDROID!", Encoding.ASCII.GetString(hybridHeader, 0, 8));
        Assert.Equal(4u, ReadLeU32(hybridHeader, 40));
        Assert.Equal(1584u, ReadLeU32(hybridHeader, 20));
        Assert.Equal(0u, ReadLeU32(hybridHeader, 12));
        Assert.Equal(0u, ReadLeU32(hybridHeader, 1580));
        Assert.Equal((long)ReadLeU32(hybridHeader, 8), fx.NormalBuilt.Envelope.CompressedKernelBytes);

        string hybridSha = Sha256OfFile(hybridPath);
        string originalSha = Sha256OfFile(fx.NormalOriginal.Path);
        string sourceCandidateSha = Sha256OfFile(fx.NormalBuilt.CandidateBoot.Path);

        var request = new EdlRawBootBuildRequest(
            "a",
            Claim(hybridPath),
            fx.SlotA.VendorBoot,
            fx.SlotA.InitBoot,
            fx.SlotA.Abl,
            fx.SlotA.XblConfig,
            fx.Package.Contract,
            fx.Tools,
            fx.Workspace.Root);

        using EdlRawBuiltBoot built = await EdlRawBootBuilder.BuildAsync(request, fx.Runner);

        // The source carries the kshim composite: that recovered fact is the only gate for the
        // authenticated source entry the build used.
        Assert.True(built.SlotMaterial.IsKshim);
        Assert.Equal(fx.NormalBuilt.BaseImage.Length, built.BaseImage.Length);
        Assert.Equal(fx.NormalBuilt.BaseImage.Sha256, built.BaseImage.Sha256);
        Assert.Equal(
            File.ReadAllBytes(fx.NormalBuilt.BaseImage.Path),
            File.ReadAllBytes(built.BaseImage.Path));

        // The output is still the ordinary envelope record over the ordinary candidate partition.
        long partitionLength = new FileInfo(hybridPath).Length;
        Assert.Equal(partitionLength, new FileInfo(built.CandidateBoot.Path).Length);
        Assert.Equal(partitionLength, built.CandidateBoot.Length);
        Assert.Equal(Sha256OfFile(built.CandidateBoot.Path), built.CandidateBoot.Sha256);

        (long candidateImageSize, long candidateBlobOffset, long candidateBlobSize) =
            ReadFooter(built.CandidateBoot.Path);
        Assert.Equal(built.Envelope.OriginalImageSize, candidateImageSize);
        Assert.Equal(built.Envelope.VbmetaOffset, candidateBlobOffset);
        Assert.Equal(built.Envelope.VbmetaBytes, candidateBlobSize);

        // The emitted blob is a fresh AOSP candidate: authenticated bytes, a new descriptor size,
        // and the exact salted digest over the physical payload.
        byte[] candidateBlob = ReadRange(built.CandidateBoot.Path, candidateBlobOffset, candidateBlobSize);
        AvbFixture.AssertAuthenticatedSignedBlob(candidateBlob);
        (long candidateSignedSize, byte[] candidateSalt, byte[] candidateDigest) =
            ParseBootHashDescriptor(candidateBlob);
        Assert.Equal(candidateImageSize, candidateSignedSize);
        Assert.NotEqual(signedOldSize, candidateSignedSize);
        Assert.True(candidateSalt.AsSpan().SequenceEqual(sourceSalt));
        Assert.Equal(ToHex(candidateDigest), built.Envelope.DigestHex);
        Assert.NotEqual(ToHex(sourceDigest), built.Envelope.DigestHex);
        Assert.Equal(SaltedSha256Hex(candidateSalt, built.CandidateBoot.Path, candidateSignedSize), built.Envelope.DigestHex);
        Assert.False(candidateBlob.AsSpan().SequenceEqual(sourceBlob));

        // The authenticated candidate key is not the OEM source principal.
        Assert.NotEqual(VbmetaPublicKeyShaHex(sourceBlob), VbmetaPublicKeyShaHex(candidateBlob));

        // Existing envelope facts stay truthful.
        Assert.Equal(ToHex(SHA256.HashData(sourceBlob)), built.Envelope.SourceEmbeddedVbmetaSha256);
        Assert.Equal("sha256", built.Envelope.HashAlgorithm);
        Assert.Equal(ToHex(sourceSalt), built.Envelope.SaltHex);
        Assert.Equal(ReadBeU64(sourceBlob, 112), built.Envelope.RollbackIndex);
        Assert.Equal(ReadBeU32(sourceBlob, 124), built.Envelope.RollbackIndexLocation);
        Assert.False(built.Envelope.OemSigned);
        Assert.True(built.Envelope.SignatureVerificationPerformed);

        // ------------------------------------------------ second owner: real preservation chain
        // The actual GBL helper now runs over the real built candidate. Its footer carries the NEW
        // payload boundary while the hybrid source's authenticated OEM descriptor still signs the
        // stock size — the exact shape that failed in the second consumer on the device.
        string builtCandidateSha = Sha256OfFile(built.CandidateBoot.Path);
        string effectivePath = Path.Combine(caseDir, "effective.img");

        object preserved = (await InvokeAwaitAsync(
            PreserveMethod(),
            built,
            Claim(hybridPath),
            effectivePath,
            CancellationToken.None))!;
        var preservedLease = Assert.IsAssignableFrom<IDisposable>(preserved);
        _stagedSources.Add(preservedLease);

        Assert.Equal(partitionLength, new FileInfo(effectivePath).Length);

        // The new footer keeps the built candidate's payload boundary and relocates the original
        // blob size; the stock payload size is never restored.
        (long effectiveImageSize, long effectiveBlobOffset, long effectiveBlobSize) =
            ReadFooter(effectivePath);
        Assert.Equal(built.Envelope.OriginalImageSize, effectiveImageSize);
        Assert.Equal(built.Envelope.VbmetaOffset, effectiveBlobOffset);
        Assert.Equal(sourceBlob.Length, effectiveBlobSize);
        Assert.NotEqual(signedOldSize, effectiveImageSize);

        // The effective OEM vbmeta is the genuine original, byte for byte and RSA-authentic.
        byte[] effectiveBlob = ReadRange(effectivePath, effectiveBlobOffset, effectiveBlobSize);
        Assert.True(effectiveBlob.AsSpan().SequenceEqual(sourceBlob));
        Assert.Equal(ToHex(SHA256.HashData(sourceBlob)), ToHex(SHA256.HashData(effectiveBlob)));
        AvbFixture.AssertAuthenticatedSignedBlob(effectiveBlob);

        // Leaf key and rollback fields stay the original signed ones, not the AOSP candidate's.
        Assert.Equal(VbmetaPublicKeyShaHex(sourceBlob), VbmetaPublicKeyShaHex(effectiveBlob));
        Assert.NotEqual(VbmetaPublicKeyShaHex(candidateBlob), VbmetaPublicKeyShaHex(effectiveBlob));
        Assert.Equal(ReadBeU64(sourceBlob, 112), ReadBeU64(effectiveBlob, 112));
        Assert.Equal(ReadBeU32(sourceBlob, 124), ReadBeU32(effectiveBlob, 124));

        // The stored OEM digest is still the original signed value over the stock body; it is never
        // reinterpreted as authenticating the new payload.
        (long effectiveSignedSize, byte[] effectiveSalt, byte[] effectiveDigest) =
            ParseBootHashDescriptor(effectiveBlob);
        Assert.Equal(signedOldSize, effectiveSignedSize);
        Assert.True(effectiveSalt.AsSpan().SequenceEqual(sourceSalt));
        Assert.True(effectiveDigest.AsSpan().SequenceEqual(sourceDigest));
        Assert.Equal(ToHex(sourceDigest), SaltedSha256Hex(sourceSalt, fx.NormalOriginal.Path, signedOldSize));
        Assert.NotEqual(ToHex(effectiveDigest), SaltedSha256Hex(sourceSalt, effectivePath, effectiveImageSize));

        // The physical payload prefix is the built candidate's own bytes, copied unchanged.
        Assert.Equal(
            Sha256OfPrefix(built.CandidateBoot.Path, effectiveImageSize),
            Sha256OfPrefix(effectivePath, effectiveImageSize));

        // Dispose the owned lease while its borrowed source is still alive.
        preservedLease.Dispose();

        // The preserved composite kernel of the input source is untouched byte for byte.
        long kernelBytes = ReadLeU32(hybridHeader, 8);
        Assert.Equal(
            Sha256OfPrefix(fx.NormalBuilt.CandidateBoot.Path, 4096 + kernelBytes),
            Sha256OfPrefix(hybridPath, 4096 + kernelBytes));

        // Every input stays exactly as claimed.
        Assert.Equal(hybridSha, Sha256OfFile(hybridPath));
        Assert.Equal(originalSha, Sha256OfFile(fx.NormalOriginal.Path));
        Assert.Equal(sourceCandidateSha, Sha256OfFile(fx.NormalBuilt.CandidateBoot.Path));
        Assert.Equal(builtCandidateSha, Sha256OfFile(built.CandidateBoot.Path));
    }

    // ------------------------------------------------------------ 2. direct repacker entry

    [Fact]
    public async Task HybridEntry_RebuildsCandidateWithFreshSignedDescriptorAndPhysicallyVerifiableDigest()
    {
        EdlRawGblEnvelopeTests.SharedFixture fx = _fixture;
        string caseDir = NewCaseDirectory(fx);
        string hybridPath = Path.Combine(caseDir, "wrapped-kshim-source.img");
        await StageWrappedKshimSourceAsync(fx, hybridPath);

        (long hybridImageSize, long hybridBlobOffset, long hybridBlobSize) = ReadFooter(hybridPath);
        byte[] sourceBlob = ReadRange(hybridPath, hybridBlobOffset, hybridBlobSize);
        AvbFixture.AssertAuthenticatedSignedBlob(sourceBlob);
        (long signedOldSize, byte[] sourceSalt, byte[] sourceDigest) =
            ParseBootHashDescriptor(sourceBlob);
        Assert.NotEqual(hybridImageSize, signedOldSize);

        string rawPath = fx.NormalBuilt.PackedRaw.Path;
        string candidatePath = Path.Combine(caseDir, "candidate.img");
        string hybridSha = Sha256OfFile(hybridPath);
        string rawSha = Sha256OfFile(rawPath);
        string[] entriesBefore = EntriesOf(caseDir);

        object result = (await InvokeAwaitAsync(
            HybridRepackMethod(),
            Claim(hybridPath),
            Claim(rawPath),
            RawBudgetBytes,
            candidatePath,
            CancellationToken.None))!;
        EdlRawBootEnvelope envelope = Assert.IsType<EdlRawBootEnvelope>(result);

        long expectedPayloadBytes = 4096L + RoundUp(GzipLength(File.ReadAllBytes(rawPath)), 4096);
        Assert.Equal(expectedPayloadBytes, envelope.OriginalImageSize);
        Assert.Equal(envelope.OriginalImageSize, envelope.VbmetaOffset);

        (long imageSize, long blobOffset, long blobSize) = ReadFooter(candidatePath);
        Assert.Equal(envelope.OriginalImageSize, imageSize);
        Assert.Equal(envelope.VbmetaOffset, blobOffset);
        Assert.Equal(envelope.VbmetaBytes, blobSize);

        byte[] blob = ReadRange(candidatePath, blobOffset, blobSize);
        AvbFixture.AssertAuthenticatedSignedBlob(blob);
        (long candidateSignedSize, byte[] candidateSalt, byte[] candidateDigest) =
            ParseBootHashDescriptor(blob);
        Assert.Equal(imageSize, candidateSignedSize);
        Assert.NotEqual(signedOldSize, candidateSignedSize);
        Assert.True(candidateSalt.AsSpan().SequenceEqual(sourceSalt));
        Assert.Equal(ToHex(candidateDigest), envelope.DigestHex);
        Assert.NotEqual(ToHex(sourceDigest), envelope.DigestHex);
        Assert.Equal(SaltedSha256Hex(candidateSalt, candidatePath, candidateSignedSize), envelope.DigestHex);
        Assert.Equal(ToHex(SHA256.HashData(sourceBlob)), envelope.SourceEmbeddedVbmetaSha256);
        Assert.False(blob.AsSpan().SequenceEqual(sourceBlob));

        // The candidate kernel decompresses to exactly the packed raw that was handed in.
        Assert.Equal(rawSha, DecompressedKernelSha256(candidatePath));
        Assert.Equal(ReadBeU64(sourceBlob, 112), envelope.RollbackIndex);
        Assert.Equal(ReadBeU32(sourceBlob, 124), envelope.RollbackIndexLocation);

        Assert.Equal(Path.GetFullPath(candidatePath), envelope.CandidateBoot.Path);
        Assert.Equal(new FileInfo(candidatePath).Length, envelope.CandidateBoot.Length);
        Assert.Equal(Sha256OfFile(candidatePath), envelope.CandidateBoot.Sha256);

        Assert.Equal(hybridSha, Sha256OfFile(hybridPath));
        Assert.Equal(rawSha, Sha256OfFile(rawPath));
        Assert.Equal(
            entriesBefore.Append(Path.GetFileName(candidatePath)!).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            EntriesOf(caseDir));
    }

    // ------------------------------------------------------------ 3. corrupt authenticated blob

    [Fact]
    public async Task HybridEntry_CorruptAuthenticatedBlob_IsRejectedWithoutOutput()
    {
        EdlRawGblEnvelopeTests.SharedFixture fx = _fixture;
        string caseDir = NewCaseDirectory(fx);
        string hybridPath = Path.Combine(caseDir, "wrapped-kshim-source.img");
        await StageWrappedKshimSourceAsync(fx, hybridPath);

        string tamperedPath = Path.Combine(caseDir, "tampered-source.img");
        CopyFile(hybridPath, tamperedPath);
        var tamperedFooter = ReadFooter(tamperedPath);
        long blobOffset = tamperedFooter.VbmetaOffset;
        FlipByte(tamperedPath, blobOffset + VbmetaHeaderSizeBytes + 64);

        // The whole-file claim is refreshed, so only the authenticated leaf reader can refuse it.
        VerifiedEdlFile freshClaim = Claim(tamperedPath);
        Assert.False(IsAuthenticBlob(tamperedPath, blobOffset, tamperedFooter.VbmetaSize));

        string rawPath = fx.NormalBuilt.PackedRaw.Path;
        string candidatePath = Path.Combine(caseDir, "candidate.img");
        string tamperedSha = Sha256OfFile(tamperedPath);
        string[] entriesBefore = EntriesOf(caseDir);

        await Assert.ThrowsAsync<InvalidDataException>(() => InvokeAwaitAsync(
            HybridRepackMethod(),
            freshClaim,
            Claim(rawPath),
            RawBudgetBytes,
            candidatePath,
            CancellationToken.None));

        Assert.False(File.Exists(candidatePath));
        Assert.Equal(entriesBefore, EntriesOf(caseDir));
        Assert.Equal(tamperedSha, Sha256OfFile(tamperedPath));
    }

    // ------------------------------------------------------------ 4. strict path unchanged

    [Fact]
    public async Task StrictEntry_WrappedHybridDescriptorMismatch_IsStillRejectedWithoutOutput()
    {
        EdlRawGblEnvelopeTests.SharedFixture fx = _fixture;
        string caseDir = NewCaseDirectory(fx);
        string hybridPath = Path.Combine(caseDir, "wrapped-kshim-source.img");
        await StageWrappedKshimSourceAsync(fx, hybridPath);

        (long hybridImageSize, long blobOffset, long blobSize) = ReadFooter(hybridPath);
        (long signedSize, _, _) = ParseBootHashDescriptor(ReadRange(hybridPath, blobOffset, blobSize));
        Assert.NotEqual(hybridImageSize, signedSize);

        string candidatePath = Path.Combine(caseDir, "candidate.img");
        string[] entriesBefore = EntriesOf(caseDir);
        string hybridSha = Sha256OfFile(hybridPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => InvokeAwaitAsync(
            StrictRepackMethod(),
            Claim(hybridPath),
            Claim(fx.NormalBuilt.PackedRaw.Path),
            RawBudgetBytes,
            candidatePath,
            CancellationToken.None));

        Assert.False(File.Exists(candidatePath));
        Assert.Equal(entriesBefore, EntriesOf(caseDir));
        Assert.Equal(hybridSha, Sha256OfFile(hybridPath));
    }

    // ------------------------------------------------------------ reflection boundary

    private static MethodInfo HybridRepackMethod() => RequireMethod(
        typeof(EdlRawBootBuilder),
        "RepackKshimHybridEnvelopeAsync",
        typeof(VerifiedEdlFile),
        typeof(VerifiedEdlFile),
        typeof(long),
        typeof(string),
        typeof(CancellationToken));

    private static MethodInfo StrictRepackMethod() => RequireMethod(
        typeof(EdlRawBootBuilder),
        "RepackEnvelopeAsync",
        typeof(VerifiedEdlFile),
        typeof(VerifiedEdlFile),
        typeof(long),
        typeof(string),
        typeof(CancellationToken));

    private static MethodInfo PreserveMethod() => RequireMethod(
        typeof(EdlRawBootBuilder),
        "PreserveOriginalSignedBootMetadataAsync",
        typeof(EdlRawBuiltBoot),
        typeof(VerifiedEdlFile),
        typeof(string),
        typeof(CancellationToken));

    private static MethodInfo RequireMethod(Type type, string name, params Type[] parameters)
    {
        MethodInfo? method = type.GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: parameters,
            modifiers: null);
        Assert.NotNull(method);
        return method!;
    }

    private static async Task<object?> InvokeAwaitAsync(MethodInfo method, params object?[] arguments)
    {
        object? invocation;
        try
        {
            invocation = method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        Task task = Assert.IsAssignableFrom<Task>(invocation);
        await task.ConfigureAwait(false);
        PropertyInfo? result = task.GetType().GetProperty("Result");
        return result?.GetValue(task);
    }

    // ------------------------------------------------------------ genuine hybrid source

    // The existing preservation helper owns the whole hybrid shape: new composite payload in
    // the footer, the genuine OEM-signed blob byte-identically in place of the AOSP blob, and
    // the signed descriptor still describing the previous leaf.
    private async Task StageWrappedKshimSourceAsync(
        EdlRawGblEnvelopeTests.SharedFixture fixture,
        string hybridPath)
    {
        object preserved = (await InvokeAwaitAsync(
            PreserveMethod(),
            fixture.NormalBuilt,
            fixture.NormalOriginal,
            hybridPath,
            CancellationToken.None))!;
        _stagedSources.Add(Assert.IsAssignableFrom<IDisposable>(preserved));
        Assert.True(File.Exists(hybridPath));
    }

    // ------------------------------------------------------------ independent oracles

    private static string NewCaseDirectory(EdlRawGblEnvelopeTests.SharedFixture fixture)
    {
        string directory = Path.Combine(
            fixture.Workspace.Root, "kshim-hybrid-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static (long ImageSize, long VbmetaOffset, long VbmetaSize) ReadFooter(string path)
    {
        using FileStream stream = OpenRead(path);
        var footer = new byte[FooterSizeBytes];
        stream.Position = stream.Length - FooterSizeBytes;
        ReadAll(stream, footer);
        Assert.Equal("AVBf", Encoding.ASCII.GetString(footer, 0, 4));
        Assert.Equal(1u, ReadBeU32(footer, 4));
        return (
            checked((long)ReadBeU64(footer, 12)),
            checked((long)ReadBeU64(footer, 20)),
            checked((long)ReadBeU64(footer, 28)));
    }

    private static (long ImageSize, byte[] Salt, byte[] Digest) ParseBootHashDescriptor(byte[] blob)
    {
        Assert.True(blob.Length >= VbmetaHeaderSizeBytes);
        long authSize = (long)ReadBeU64(blob, 12);
        long auxStart = VbmetaHeaderSizeBytes + authSize;
        long descriptorsOffset = (long)ReadBeU64(blob, 96);
        long descriptorsEnd = descriptorsOffset + (long)ReadBeU64(blob, 104);
        Assert.True(descriptorsOffset >= 0 && auxStart + descriptorsEnd <= blob.Length);

        long position = descriptorsOffset;
        while (position < descriptorsEnd)
        {
            Assert.True(descriptorsEnd - position >= 16, "truncated descriptor header");
            int at = checked((int)(auxStart + position));
            ulong tag = ReadBeU64(blob, at);
            long length = 16L + (long)ReadBeU64(blob, at + 8);
            Assert.True(length >= 16 && length % 8 == 0 && position + length <= descriptorsEnd);
            if (tag == 2)
            {
                Assert.True(length >= 136, "hash descriptor too small");
                long imageSize = (long)ReadBeU64(blob, at + 16);
                uint saltLength = ReadBeU32(blob, at + 60);
                uint digestLength = ReadBeU32(blob, at + 64);
                Assert.True(132 + 4 + (long)saltLength + digestLength <= length);
                var salt = new byte[saltLength];
                Buffer.BlockCopy(blob, at + 136, salt, 0, salt.Length);
                var digest = new byte[digestLength];
                Buffer.BlockCopy(blob, checked(at + 136 + (int)saltLength), digest, 0, digest.Length);
                Assert.Equal("boot", Encoding.ASCII.GetString(blob, at + 132, 4));
                return (imageSize, salt, digest);
            }

            position += length;
        }

        throw new InvalidOperationException("no AVB hash descriptor found");
    }

    private static string VbmetaPublicKeyShaHex(byte[] blob)
    {
        long authSize = (long)ReadBeU64(blob, 12);
        long keyOffset = (long)ReadBeU64(blob, 64);
        long keySize = (long)ReadBeU64(blob, 72);
        Assert.True(keySize > 0 && keySize <= MaxMetadataArrayBytes);
        int start = checked((int)(VbmetaHeaderSizeBytes + authSize + keyOffset));
        Assert.True(start + keySize <= blob.Length);
        var key = new byte[keySize];
        Buffer.BlockCopy(blob, start, key, 0, key.Length);
        return Convert.ToHexString(SHA256.HashData(key));
    }

    private static bool IsAuthenticBlob(string path, long offset, long size)
    {
        try
        {
            AvbFixture.AssertAuthenticatedSignedBlob(ReadRange(path, offset, size));
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or Xunit.Sdk.XunitException)
        {
            return false;
        }
    }

    private static string DecompressedKernelSha256(string path)
    {
        using FileStream stream = OpenRead(path);
        var head = new byte[64];
        ReadAll(stream, head);
        Assert.Equal("ANDROID!", Encoding.ASCII.GetString(head, 0, 8));
        long compressedLength = ReadLeU32(head, 8);
        Assert.InRange(compressedLength, 1, 32L * 1024L * 1024L);

        var compressed = new byte[compressedLength];
        stream.Position = 4096;
        ReadAll(stream, compressed);
        using var source = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return ToHex(hash.GetHashAndReset());
    }

    private static int GzipLength(byte[] data)
    {
        using var output = new MemoryStream();
        using (var compressor = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(data, 0, data.Length);
        }

        return checked((int)output.Length);
    }

    private static string SaltedSha256Hex(byte[] salt, string path, long count)
    {
        Assert.True(count >= 0);
        using FileStream stream = OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(salt);
        var buffer = new byte[81920];
        long remaining = count;
        while (remaining > 0)
        {
            int chunk = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            Assert.True(chunk > 0, "truncated salted digest range");
            hash.AppendData(buffer, 0, chunk);
            remaining -= chunk;
        }

        return ToHex(hash.GetHashAndReset());
    }

    private static string Sha256OfPrefix(string path, long count)
    {
        Assert.True(count >= 0);
        using FileStream stream = OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long remaining = count;
        while (remaining > 0)
        {
            int chunk = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            Assert.True(chunk > 0, "truncated payload prefix");
            hash.AppendData(buffer, 0, chunk);
            remaining -= chunk;
        }

        return ToHex(hash.GetHashAndReset());
    }

    private static string Sha256OfFile(string path)
    {
        using FileStream stream = OpenRead(path);
        return ToHex(SHA256.HashData(stream));
    }

    private static VerifiedEdlFile Claim(string path) =>
        new(Path.GetFullPath(path), new FileInfo(path).Length, Sha256OfFile(path));

    private static byte[] ReadRange(string path, long offset, long count)
    {
        Assert.True(count >= 0 && count <= MaxMetadataArrayBytes, "metadata reads are bounded to 1 MiB");
        var data = new byte[count];
        using FileStream stream = OpenRead(path);
        stream.Position = offset;
        ReadAll(stream, data);
        return data;
    }

    private static void ReadAll(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = stream.Read(buffer, read, buffer.Length - read);
            Assert.True(chunk > 0, "unexpected end of file while reading bytes");
            read += chunk;
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);

    private static void CopyFile(string source, string destination)
    {
        using FileStream input = OpenRead(source);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
        input.CopyTo(output, 81920);
        output.Flush(true);
    }

    private static void FlipByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(offset >= 0 && offset < stream.Length, "tamper offset must stay inside the file");
        stream.Position = offset;
        int value = stream.ReadByte();
        Assert.True(value >= 0);
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0x5A));
        stream.Flush(true);
    }

    private static string[] EntriesOf(string directory) => Directory.Exists(directory)
        ? Directory.GetFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!
        : Array.Empty<string>();

    private static int RoundUp(int value, int alignment)
    {
        int remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static uint ReadLeU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static uint ReadBeU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static ulong ReadBeU64(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
}
