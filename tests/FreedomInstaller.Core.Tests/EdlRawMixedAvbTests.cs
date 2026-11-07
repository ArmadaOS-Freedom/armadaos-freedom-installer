using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class EdlRawMixedAvbTests : IDisposable
{
    private const string Pin = "7728E30F50BFA5CEA165F473175A08803F6A8346642B5AA10913E9D9E6DEFEF6";
    private static readonly Lazy<RSA> Small = new(() => RSA.Create(2048));
    private static readonly Lazy<RSA> Medium = new(() => RSA.Create(4096));
    private static readonly Lazy<RSA> Large = new(() => RSA.Create(8192));
    private readonly string directory = Path.Combine(TestArtifactRoot.Resolve(), "avb-mixed-" + Guid.NewGuid().ToString("N"));
    public EdlRawMixedAvbTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static RSA Key(int bits) => (bits == 2048 ? Small : bits == 4096 ? Medium : Large).Value;
    internal static (byte[] Partition, byte[] Chain) SmallSystemLeaf() =>
        (Partition(Blob(Key(2048), false, Opaque()), false), Chain("vbmeta_system", Key(2048), 2));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
    private static int U64(byte[] b, int o) => checked((int)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o, 8)));
    private static void W32(byte[] b, int o, uint n) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o, 4), n);
    private static void W64(byte[] b, int o, ulong n) => BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(o, 8), n);
    private static int Round(int n, int alignment) => (n + alignment - 1) / alignment * alignment;
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
    private static byte[] Fixed(BigInteger n, int size)
    {
        byte[] value = n.ToByteArray(true, true), result = new byte[size];
        value.CopyTo(result, size - value.Length);
        return result;
    }
    private static byte[] PublicKey(RSA rsa)
    {
        var modulus = rsa.ExportParameters(false).Modulus!;
        int bits = modulus.Length * 8;
        uint n0 = U32(modulus, modulus.Length - 4), inverse = 1;
        for (int i = 0; i < 5; i++) inverse = unchecked(inverse * (2u - n0 * inverse));
        var key = new byte[8 + 2 * modulus.Length];
        W32(key, 0, (uint)bits);
        W32(key, 4, unchecked(0u - inverse));
        modulus.CopyTo(key, 8);
        Fixed(BigInteger.ModPow(2, 2 * bits, new BigInteger(modulus, true, true)), modulus.Length)
            .CopyTo(key, 8 + modulus.Length);
        return key;
    }
    private static byte[] Chain(string name, RSA rsa, uint location)
    {
        byte[] text = Encoding.ASCII.GetBytes(name), key = PublicKey(rsa);
        byte[] d = new byte[Round(92 + text.Length + key.Length, 8)];
        W64(d, 0, 4); W64(d, 8, (ulong)d.Length - 16);
        W32(d, 16, location); W32(d, 20, (uint)text.Length); W32(d, 24, (uint)key.Length);
        text.CopyTo(d, 92); key.CopyTo(d, 92 + text.Length);
        return d;
    }
    private static byte[] Opaque()
    {
        byte[] b = new byte[32];
        W64(b, 0, 77); W64(b, 8, 16);
        for (int i = 16; i < b.Length; i++) b[i] = (byte)(i + 1);
        return b;
    }

    private static byte[] PayloadHash(string name)
    {
        byte[] payload = new byte[32768];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        byte[] text = Encoding.ASCII.GetBytes(name), salt = { 1, 3, 5, 7 };
        byte[] digest = SHA256.HashData(salt.Concat(payload).ToArray());
        byte[] d = new byte[Round(132 + text.Length + salt.Length + digest.Length, 8)];
        W64(d, 0, 2); W64(d, 8, (ulong)d.Length - 16); W64(d, 16, (ulong)payload.Length);
        Encoding.ASCII.GetBytes("sha256").CopyTo(d, 24);
        W32(d, 56, (uint)text.Length); W32(d, 60, (uint)salt.Length); W32(d, 64, (uint)digest.Length);
        text.CopyTo(d, 132); salt.CopyTo(d, 132 + text.Length);
        digest.CopyTo(d, 132 + text.Length + salt.Length);
        return d;
    }

    private async Task VerifyChain(byte[] root, byte[] boot, byte[] recovery, byte[] system)
    {
        var parameters = new List<object>();
        foreach (var (name, bytes) in new[] { ("root", root), ("boot", boot), ("recovery", recovery), ("system", system) })
        {
            string path = Path.Combine(directory, "verify-" + name);
            File.WriteAllBytes(path, bytes);
            parameters.Add(new VerifiedEdlFile(path, bytes.Length, Hash(bytes)));
        }
        parameters.Add(CancellationToken.None);
        var method = typeof(EdlRawBootBuilder).GetMethod("VerifyAvbBootChainAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(null, parameters.ToArray())!;
    }

    [Theory]
    [InlineData(2048, 4096, 8192)]
    [InlineData(4096, 2048, 2048)]
    public async Task CompleteMixedChainAuthenticatesBeforeAndAfterResigning(int bootBits, int recoveryBits, int systemBits)
    {
        byte[] boot = Partition(Blob(Key(bootBits), false, PayloadHash("boot")), true);
        byte[] recovery = Partition(Blob(Key(recoveryBits), false, PayloadHash("recovery")), true);
        byte[] system = Partition(Blob(Key(systemBits), true, Opaque()), false);
        byte[] root = Partition(Blob(Key(4096), false, Chain("boot", Key(bootBits), 1),
            Chain("recovery", Key(recoveryBits), 2), Chain("vbmeta_system", Key(systemBits), 3)), false);
        await VerifyChain(root, boot, recovery, system);
        byte[] signedBoot = await Resign(boot, "Recovery", "boot");
        byte[] signedRecovery = await Resign(recovery, "Recovery", "recovery");
        byte[] signedSystem = await Resign(system, "VbmetaSystem", "system");
        byte[] signedRoot = await Resign(root, "Vbmeta", "root");
        await VerifyChain(signedRoot, signedBoot, signedRecovery, signedSystem);
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifyChain(root, signedBoot, recovery, system));
        signedRecovery[123] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifyChain(signedRoot, signedBoot, signedRecovery, signedSystem));
    }
    private static byte[] Blob(RSA key, bool sha512, params byte[][] descriptors)
    {
        int bytes = key.KeySize / 8, hashBytes = sha512 ? 64 : 32;
        int auth = Round(hashBytes + bytes, 64);
        byte[] publicKey = PublicKey(key), desc = descriptors.SelectMany(x => x).ToArray();
        int aux = Round(desc.Length + publicKey.Length, 64);
        byte[] blob = new byte[256 + auth + aux];
        Encoding.ASCII.GetBytes("AVB0").CopyTo(blob, 0);
        W32(blob, 4, 1); W32(blob, 8, 2);
        W64(blob, 12, (ulong)auth); W64(blob, 20, (ulong)aux);
        W32(blob, 28, (uint)((bytes == 256 ? 1 : bytes == 512 ? 2 : 3) + (sha512 ? 3 : 0)));
        W64(blob, 40, (ulong)hashBytes);
        W64(blob, 48, (ulong)hashBytes); W64(blob, 56, (ulong)bytes);
        W64(blob, 64, (ulong)desc.Length); W64(blob, 72, (ulong)publicKey.Length);
        W64(blob, 104, (ulong)desc.Length); W64(blob, 112, 12345);
        W32(blob, 124, 7);
        desc.CopyTo(blob, 256 + auth); publicKey.CopyTo(blob, 256 + auth + desc.Length);
        var hashName = sha512 ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256;
        using var hash = IncrementalHash.CreateHash(hashName);
        hash.AppendData(blob, 0, 256); hash.AppendData(blob, 256 + auth, aux);
        byte[] digest = hash.GetHashAndReset();
        digest.CopyTo(blob, 256);
        lock (key) key.SignHash(digest, hashName, RSASignaturePadding.Pkcs1).CopyTo(blob, 256 + hashBytes);
        return blob;
    }
    private static byte[] Partition(byte[] blob, bool footer, int length = 65536)
    {
        int offset = footer ? 32768 : 0;
        var p = new byte[length];
        if (footer) for (int i = 0; i < offset; i++) p[i] = (byte)(i % 251);
        blob.CopyTo(p, offset);
        if (footer)
        {
            int f = length - 64;
            Encoding.ASCII.GetBytes("AVBf").CopyTo(p, f);
            W32(p, f + 4, 1); W64(p, f + 12, (ulong)offset);
            W64(p, f + 20, (ulong)offset); W64(p, f + 28, (ulong)blob.Length);
        }
        return p;
    }
    private async Task<byte[]> Resign(byte[] original, string kind, string name)
    {
        string source = Path.Combine(directory, name + ".source"), output = Path.Combine(directory, name + ".out");
        File.WriteAllBytes(source, original);
        var claim = new VerifiedEdlFile(source, original.Length, Hash(original));
        Type builder = typeof(EdlRawBootBuilder);
        var method = builder.GetMethod("ResignAvbNodeAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Type enumType = builder.Assembly.GetType("FreedomInstaller.Core.EdlRawAvbNodeKind")!;
        try
        {
            var task = (Task)method.Invoke(null, new object[] { claim, Enum.Parse(enumType, kind), output, CancellationToken.None })!;
            await task;
            Assert.Equal(original, File.ReadAllBytes(source));
            byte[] candidate = File.ReadAllBytes(output);
            Assert.Equal(original.Length, candidate.Length);
            return candidate;
        }
        catch
        {
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.False(File.Exists(output));
            throw;
        }
    }
    private static (byte[] Key, List<byte[]> Descriptors, int Size) Verify(byte[] partition, bool footer)
    {
        int offset = footer ? U64(partition, partition.Length - 44) : 0;
        byte[] b = partition.AsSpan(offset).ToArray();
        int auth = U64(b, 12), aux = U64(b, 20), start = 256 + auth;
        Assert.Equal(2u, U32(b, 28));
        Assert.Equal(12345, U64(b, 112)); Assert.Equal(7u, U32(b, 124));
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(b, 0, 256); digest.AppendData(b, start, aux);
        var hash = digest.GetHashAndReset();
        Assert.Equal(hash, b.AsSpan(256 + U64(b, 32), U64(b, 40)).ToArray());
        byte[] key = b.AsSpan(start + U64(b, 64), U64(b, 72)).ToArray();
        Assert.Equal(Pin, Hash(key)); Assert.Equal(4096u, U32(key, 0));
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = key.AsSpan(8, 512).ToArray(), Exponent = new byte[] { 1, 0, 1 } });
        Assert.True(rsa.VerifyHash(hash, b.AsSpan(256 + U64(b, 48), U64(b, 56)).ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var ds = new List<byte[]>();
        int position = start + U64(b, 96), end = position + U64(b, 104);
        while (position < end) { int size = 16 + U64(b, position + 8); ds.Add(b.AsSpan(position, size).ToArray()); position += size; }
        Assert.Equal(end, position);
        if (footer) Assert.Equal(start + aux, U64(partition, partition.Length - 36));
        return (key, ds, start + aux);
    }
    [Theory]
    [InlineData(2048, false, false)]
    [InlineData(2048, false, true)]
    [InlineData(8192, false, true)]
    [InlineData(2048, true, false)]
    [InlineData(4096, true, true)]
    [InlineData(8192, true, false)]
    public async Task LeafAlgorithmConversionPreservesPayloadAndUpdatesFooter(int bits, bool sha512, bool footer)
    {
        byte[] original = Partition(Blob(Key(bits), sha512, Opaque()), footer);
        byte[] candidate = await Resign(original, footer ? "Recovery" : "VbmetaSystem", "leaf");
        var parsed = Verify(candidate, footer);
        Assert.Equal(Opaque(), Assert.Single(parsed.Descriptors));
        if (footer) Assert.Equal(original.AsSpan(0, 32768).ToArray(), candidate.AsSpan(0, 32768).ToArray());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothSlotsWithDifferentSystemKeysProduceClosedChains(bool reverse)
    {
        foreach (var slot in new[] { "a", "b" })
        {
            int systemBits = (slot == "a") ^ reverse ? 4096 : 2048;
            var root = Partition(Blob(Key(4096), false, Opaque(),
                Chain("boot", Key(4096), 3), Chain("recovery", Key(4096), 1),
                Chain("vbmeta_system", Key(systemBits), 2)), false);
            var recovery = await Resign(Partition(Blob(Key(4096), false, Opaque()), true), "Recovery", slot + "-recovery");
            var system = await Resign(Partition(Blob(Key(systemBits), false, Opaque()), false), "VbmetaSystem", slot + "-system");
            var output = Verify(await Resign(root, "Vbmeta", slot + "-root"), false);
            var childKeys = new Dictionary<string, byte[]> { ["boot"] = output.Key,
                ["recovery"] = Verify(recovery, true).Key, ["vbmeta_system"] = Verify(system, false).Key };
            Assert.Equal(Opaque(), output.Descriptors[0]);
            foreach (var d in output.Descriptors.Skip(1))
            {
                int nameLength = (int)U32(d, 20), keyLength = (int)U32(d, 24);
                string name = Encoding.ASCII.GetString(d, 92, nameLength);
                Assert.Equal(childKeys[name], d.AsSpan(92 + nameLength, keyLength).ToArray());
                Assert.Equal(name == "boot" ? 3u : name == "recovery" ? 1u : 2u, U32(d, 16));
            }
        }
    }
    [Fact]
    public async Task ValidSourceWithNoExpansionSpaceIsRejected()
    {
        var blob = Blob(Key(2048), false, Opaque());
        var source = Partition(blob, true, 32768 + blob.Length + 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => Resign(source, "Recovery", "no-room"));
    }
    [Fact]
    public async Task ExpansionNeverOverwritesNonzeroPartitionData()
    {
        var blob = Blob(Key(2048), false, Opaque());
        var source = Partition(blob, false);
        source[blob.Length] = 0x5a;
        await Assert.ThrowsAsync<InvalidDataException>(() => Resign(source, "VbmetaSystem", "occupied"));
    }
    [Fact]
    public async Task InvalidSignatureIsRejected()
    {
        var source = Partition(Blob(Key(4096), false, Opaque()), false);
        source[256 + U64(source, 48)] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => Resign(source, "VbmetaSystem", "signature"));
    }
    [Fact]
    public async Task SignedMalformedChainLengthIsRejected()
    {
        var chain = Chain("vbmeta_system", Key(2048), 2);
        W32(chain, 24, 1032);
        var source = Partition(Blob(Key(4096), false, Chain("boot", Key(4096), 3),
            Chain("recovery", Key(4096), 1), chain), false);
        await Assert.ThrowsAsync<InvalidDataException>(() => Resign(source, "Vbmeta", "malformed"));
    }
}
