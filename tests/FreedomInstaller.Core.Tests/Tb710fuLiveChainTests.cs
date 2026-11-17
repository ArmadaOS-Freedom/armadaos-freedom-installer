using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Real-device check of the TB710FU AOSP-preserving boot-only route, against partitions read from a TB710FU.
/// Skipped unless TB710FU_LIVE_PARTITIONS names a directory with vbmeta_a.img, vbmeta_b.img and boot_a.img (a
/// Freedom-installed kShim boot). The repacked candidate is left under TEST_ARTIFACT_ROOT so it can also be checked
/// with AOSP avbtool against the real vbmeta chain.
/// </summary>
public sealed class Tb710fuLiveChainTests
{
    private static string? LiveDirectory()
    {
        string? directory = Environment.GetEnvironmentVariable("TB710FU_LIVE_PARTITIONS");
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    private static VerifiedEdlFile Claim(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new VerifiedEdlFile(path, stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    [Theory]
    [InlineData("vbmeta_a.img")]
    [InlineData("vbmeta_b.img")]
    public async Task The_real_root_vbmeta_and_its_boot_chain_are_the_aosp_key(string name)
    {
        string? directory = LiveDirectory();
        if (directory is null)
            return;

        EdlRawOriginalBootChainFacts facts = await EdlRawBootBuilder.AuthenticatePreservedBootChainAsync(
            Claim(Path.Combine(directory, name)));

        // The same comparison the AOSP-preserving route makes before it plans any boot-only write.
        Assert.Equal(EdlRawBootBuilder.SigningPublicKeySha256, facts.PublicKeySha256, ignoreCase: true);
        Assert.Equal(EdlRawBootBuilder.SigningPublicKeySha256, facts.BootChainPublicKeySha256, ignoreCase: true);
    }

    [Fact]
    public async Task The_real_kshim_boot_repacks_into_a_same_size_candidate()
    {
        string? directory = LiveDirectory();
        string? artifacts = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT");
        if (directory is null || string.IsNullOrWhiteSpace(artifacts))
            return;

        string work = Path.Combine(artifacts, "tb710fu-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        string bootPath = Path.Combine(directory, "boot_a.img");
        string rawPath = Path.Combine(work, "kshim-raw.bin");
        ExtractKernel(bootPath, rawPath);

        string candidatePath = Path.Combine(work, "boot.img");
        VerifiedEdlFile boot = Claim(bootPath);
        VerifiedEdlFile raw = Claim(rawPath);
        // The real bound is the loader budget of the source material; this check is about the signature, so the raw
        // image's own length is the bound.
        EdlRawBootEnvelope envelope = await EdlRawBootBuilder.RepackKshimHybridEnvelopeAsync(
            boot, raw, raw.Length, candidatePath);

        Assert.Equal(boot.Length, envelope.CandidateBoot.Length);
        Assert.Equal(2, envelope.AlgorithmType);
        File.WriteAllText(Path.Combine(work, "candidate-path.txt"), candidatePath);
    }

    /// <summary>The gzip-compressed kernel of a boot v4 image, decompressed: the packed kShim raw image.</summary>
    private static void ExtractKernel(string bootPath, string rawPath)
    {
        byte[] header = new byte[4096];
        using (FileStream boot = File.OpenRead(bootPath))
        {
            boot.ReadExactly(header);
            Assert.Equal("ANDROID!", System.Text.Encoding.ASCII.GetString(header, 0, 8));
            Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
            int kernelSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
            byte[] kernel = new byte[kernelSize];
            boot.ReadExactly(kernel);
            using var gzip = new GZipStream(new MemoryStream(kernel), CompressionMode.Decompress);
            using FileStream raw = File.Create(rawPath);
            gzip.CopyTo(raw);
        }
    }
}
