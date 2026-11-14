using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class UpdateSignatureVerifierTests
{
    private static readonly MethodInfo InternalVerify = typeof(UpdateSignatureVerifier).GetMethod(
        "VerifyAndParseWithKey",
        BindingFlags.Static | BindingFlags.NonPublic,
        null,
        [typeof(byte[]), typeof(byte[]), typeof(string)],
        null)!;

    private static JsonDocument InvokeWithKey(byte[]? doc, byte[]? sig, string? pem)
    {
        try
        {
            return (JsonDocument)InternalVerify.Invoke(null, [doc, sig, pem])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static byte[] SignDerBase64(ECDsa key, byte[] data) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(
            key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));

    [Fact]
    public void ValidSignedChineseAndHugeInt_SurroundingWhitespace_ClonesInput()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        byte[] doc = Encoding.UTF8.GetBytes("""{"名称":"自由安装器","big":9999999999999999999999999999999999999999}""");
        string b64 = Convert.ToBase64String(
            key.SignData(doc, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        byte[] sig = Encoding.ASCII.GetBytes($" \t\r\n{b64}\n\r\t ");

        using JsonDocument parsed = InvokeWithKey(doc, sig, pem);
        Array.Fill(doc, (byte)'x');
        Assert.Equal("自由安装器", parsed.RootElement.GetProperty("名称").GetString());
        Assert.Equal("9999999999999999999999999999999999999999", parsed.RootElement.GetProperty("big").GetRawText());
    }

    [Fact]
    public void Tamper_WrongKey_ChangedSig_P1363_And_BadPem_Rejected()
    {
        using ECDsa key1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa key2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem1 = key1.ExportSubjectPublicKeyInfoPem();
        byte[] doc = JsonSerializer.SerializeToUtf8Bytes(new { v = 1 });
        byte[] sig = SignDerBase64(key1, doc);

        byte[] wsTampered = [.. doc, (byte)' '];
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(wsTampered, sig, pem1));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, sig, key2.ExportSubjectPublicKeyInfoPem()));

        byte[] raw = key1.SignData(doc, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        raw[^1] ^= 0x01;
        byte[] badSig = Encoding.ASCII.GetBytes(Convert.ToBase64String(raw));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, badSig, pem1));

        byte[] p1363 = Encoding.ASCII.GetBytes(Convert.ToBase64String(
            key1.SignData(doc, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, p1363, pem1));

        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, sig, "not-a-valid-pem"));
    }

    [Fact]
    public void Null_SizeBounds_And_SignatureDecoding_Rejected()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        byte[] doc = JsonSerializer.SerializeToUtf8Bytes(new { ok = true });
        byte[] sig = SignDerBase64(key, doc);

        Assert.Throws<ArgumentNullException>(() => UpdateSignatureVerifier.VerifyAndParse(null!, sig));
        Assert.Throws<ArgumentNullException>(() => UpdateSignatureVerifier.VerifyAndParse(doc, null!));
        Assert.Throws<ArgumentNullException>(() => InvokeWithKey(null, sig, pem));
        Assert.Throws<ArgumentNullException>(() => InvokeWithKey(doc, null, pem));
        Assert.Throws<ArgumentNullException>(() => InvokeWithKey(doc, sig, null));

        Assert.Throws<InvalidDataException>(() => InvokeWithKey([], sig, pem));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(new byte[2 * 1024 * 1024 + 1], sig, pem));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, [], pem));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, " \t\r\n"u8.ToArray(), pem));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, new byte[4097], pem));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, "abc$"u8.ToArray(), pem));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, "AAAA AAAA"u8.ToArray(), pem));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, Encoding.UTF8.GetBytes("中"), pem));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, "abc="u8.ToArray(), pem));
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, Encoding.ASCII.GetBytes(Convert.ToBase64String([1, 2, 3])), pem));
    }

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":1,"\u0061":2}""")]
    [InlineData("""{"nested":{"x":1,"x":2}}""")]
    [InlineData("""{"arr":[{"k":1,"\u006b":2}]}""")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{"a":1/*c*/}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""123""")]
    [InlineData("""{"n":NaN}""")]
    [InlineData("""{"n":Infinity}""")]
    [InlineData("""{"n":1e999}""")]
    public void SignedInvalidJsonStrings_Rejected(string json)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        byte[] doc = Encoding.UTF8.GetBytes(json);
        byte[] sig = SignDerBase64(key, doc);
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(doc, sig, pem));
    }

    [Fact]
    public void SignedBomAndInvalidUtf8_Rejected()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportSubjectPublicKeyInfoPem();
        byte[] bomDoc = [0xef, 0xbb, 0xbf, .. "{}"u8];
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(bomDoc, SignDerBase64(key, bomDoc), pem));

        byte[] badUtf8 = [0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d];
        Assert.Throws<InvalidDataException>(() => InvokeWithKey(badUtf8, SignDerBase64(key, badUtf8), pem));
    }

    [Fact]
    public void PublicApi_EmbeddedKey_And_SurfaceGuard()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] doc = JsonSerializer.SerializeToUtf8Bytes(new { test = 1 });
        byte[] sig = SignDerBase64(key, doc);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => UpdateSignatureVerifier.VerifyAndParse(doc, sig));
        Assert.Equal("Signature verification failed.", ex.Message);

        using Stream? s = typeof(UpdateSignatureVerifier).Assembly.GetManifestResourceStream("FreedomInstaller.UpdateTrust.PublicKey.pem");
        Assert.NotNull(s);
        using var r = new StreamReader(s!, Encoding.UTF8);
        using ECDsa embedded = ECDsa.Create();
        embedded.ImportFromPem(r.ReadToEnd());
        Assert.Equal(256, embedded.KeySize);

        Assert.DoesNotContain(
            typeof(UpdateSignatureVerifier).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance),
            m => m.GetParameters().Any(p => p.ParameterType == typeof(string)));
    }

    [Fact]
    public void CosignSyntheticVector_Verifies()
    {
        byte[] doc = Convert.FromBase64String("eyJjb21wb25lbnQiOiJmcmVlZG9tIiwidmVyc2lvbiI6IjAuOC4wIn0=");
        byte[] sig = Convert.FromBase64String("TUVZQ0lRRGM0RkU5a1VPVit1Y3hLS29tRjZvMnFWMlV1aitqbjM1dHJacDBubnFpVmdJaEFPa2NqZGczeTE5N21ScVhsdFU5dVVzbGpJektXUG5WYUZIWWVaTS9McW1o");
        string pem = Encoding.UTF8.GetString(Convert.FromBase64String("LS0tLS1CRUdJTiBQVUJMSUMgS0VZLS0tLS0KTUZrd0V3WUhLb1pJemowQ0FRWUlLb1pJemowREFRY0RRZ0FFUnQwSDJScXMwZkx6NWRaakJiQm5Dd3dINnJiRApPdGhtKzE2U0xtNUQ1TkRkeHhuMVVBVy9KUVI2bUZZWFVmNXNqT1IxNmlWcmRRZ2hpV2liUExSVXVBPT0KLS0tLS1FTkQgUFVCTElDIEtFWS0tLS0tCg=="));

        using JsonDocument parsed = InvokeWithKey(doc, sig, pem);
        Assert.Equal("freedom", parsed.RootElement.GetProperty("component").GetString());
        Assert.Equal("0.8.0", parsed.RootElement.GetProperty("version").GetString());
    }
}
