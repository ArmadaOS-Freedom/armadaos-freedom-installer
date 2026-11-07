using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class AblAvbTrustTests
{
    private static readonly byte[] Aosp = LoadAosp();
    private static readonly byte[] Other = LoadOther();

    private static byte[] LoadAosp()
    {
        var type = typeof(EdlRawBootBuilder);
        using var stream = type.Assembly.GetManifestResourceStream("FreedomInstaller.AvbTestKey.pem")!;
        using var key = (RSA)type.GetMethod("LoadAvbSigningKey", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { stream })!;
        return (byte[])type.GetMethod("CreateAvbPublicKey", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { key })!;
    }

    private static byte[] LoadOther()
    {
        using var key = RSA.Create(2048);
        return (byte[])typeof(EdlRawMixedAvbTests).GetMethod("PublicKey", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { key })!;
    }

    private static object Inspect(byte[] image, CancellationToken ct = default, bool container = false)
    {
        try
        {
            return typeof(EdlRawBootBuilder).GetMethod(container ? "InspectAblAvbKey" : "InspectAblPeAvbKey",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { image, ct })!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static T Value<T>(object facts, string name) => (T)facts.GetType().GetProperty(name)!.GetValue(facts)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportsReferencedKeyInDataSection(bool other)
    {
        var key = other ? Other : Aosp;
        var pe = Image();
        key.CopyTo(pe, 0x1803);
        Adr(pe, 0x240, 0x1803);
        var facts = Inspect(pe);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(key)), Value<string>(facts, "KeySha256"));
        Assert.Equal(1, Value<int>(facts, "XrefCount"));
    }

    [Fact]
    public void DuplicateLaterKeyUsesItsOwnStrongerReferences()
    {
        var pe = Image();
        Aosp.CopyTo(pe, 0x1803);
        Aosp.CopyTo(pe, 0x2003);
        Other.CopyTo(pe, 0x2803);
        Adr(pe, 0x240, 0x1803);
        Adr(pe, 0x244, 0x2003);
        Adr(pe, 0x248, 0x2003);
        Adr(pe, 0x24c, 0x2803);
        var facts = Inspect(pe);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Aosp)), Value<string>(facts, "KeySha256"));
        Assert.Equal(2, Value<int>(facts, "XrefCount"));
        Assert.Equal(0x2003, Value<int>(facts, "RawOffset"));
    }

    [Fact]
    public void DifferentKeysTiedOnReferencesAreRejected()
    {
        var pe = Image(); Aosp.CopyTo(pe, 0x1803); Other.CopyTo(pe, 0x2803);
        Adr(pe, 0x240, 0x1803); Adr(pe, 0x244, 0x2803);
        Assert.Throws<InvalidDataException>(() => Inspect(pe));
    }

    [Fact]
    public void UnreferencedAndDataOnlyReferencesAreRejected()
    {
        var pe = Image(); Aosp.CopyTo(pe, 0x1803);
        Assert.Throws<InvalidDataException>(() => Inspect(pe));
        Adr(pe, 0x3100, 0x1803);
        Assert.Throws<InvalidDataException>(() => Inspect(pe));
    }

    [Fact]
    public void BadMontgomeryRemainderIsRejected()
    {
        var pe = Image(); Aosp.CopyTo(pe, 0x1803); Adr(pe, 0x240, 0x1803);
        pe[0x1803 + 600] ^= 1;
        Assert.Throws<InvalidDataException>(() => Inspect(pe));
    }

    [Fact]
    public void CandidateCannotCrossMappedSectionEnd()
    {
        var pe = Image(); Aosp.CopyTo(pe, 0xe00); Adr(pe, 0x240, 0xe00);
        Assert.Throws<InvalidDataException>(() => Inspect(pe));
    }

    [Fact]
    public void WrongArchitectureAndOversizeAreRejected()
    {
        var pe = Image(); W16(pe, 0x84, 0x8664);
        Assert.Throws<InvalidDataException>(() => Inspect(pe));
        Assert.Throws<InvalidDataException>(() => Inspect(new byte[4 * 1024 * 1024 + 1]));
    }

    [Fact]
    public void ProductionRejectsRawPe()
        => Assert.Throws<InvalidDataException>(() => Inspect(Image(), container: true));

    [Fact]
    public void CancellationPropagates()
        => Assert.Throws<OperationCanceledException>(() => Inspect(Image(), new CancellationToken(true)));

    internal static byte[] Image()
    {
        var data = new byte[0x4000];
        data[0] = (byte)'M'; data[1] = (byte)'Z'; W32(data, 0x3c, 0x80);
        data[0x80] = (byte)'P'; data[0x81] = (byte)'E';
        W16(data, 0x84, 0xAA64); W16(data, 0x86, 2); W16(data, 0x94, 0x60); W16(data, 0x98, 0x20b);
        Section(data, 0xf8, ".text", 0x200, 0xe00, 0x60000020);
        Section(data, 0x120, ".rdata", 0x1000, 0x3000, 0x40000040);
        return data;
    }

    private static void Section(byte[] b, int p, string name, uint raw, uint size, uint flags)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(b, p);
        W32(b, p + 12, raw + 0xe00); W32(b, p + 16, size); W32(b, p + 20, raw); W32(b, p + 36, flags);
    }

    private static void Adr(byte[] b, int p, int target)
    {
        uint immediate = (uint)(target - p) & 0x1fffff;
        W32(b, p, 0x10000000 | ((immediate & 3) << 29) | ((immediate >> 2) << 5));
    }
    private static void W16(byte[] b, int p, ushort n) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), n);
    private static void W32(byte[] b, int p, uint n) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), n);
}
