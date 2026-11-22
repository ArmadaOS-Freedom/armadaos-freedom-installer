using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Offline acceptance checks for the live ABL. The reviewed TB322FC OEM loader references the AOSP key
/// as a secondary blob but uses the OEM key as its effective key, so it must not be treated as accepting
/// AOSP-signed vbmeta. The reviewed 188 loader uses AOSP as its effective key. Real device images are
/// opt-in through environment variables; no device is read or written.
/// </summary>
public sealed class AblAospAcceptanceTests
{
    private const string OemKeySha256 =
        "7B34B55104C8F0F48AE3763B1B20990AA9F8F5F0DFFB838D7E31EF506C819281";

    private const string AospKeySha256 =
        "7728E30F50BFA5CEA165F473175A08803F6A8346642B5AA10913E9D9E6DEFEF6";

    private const string Reviewed188AblSha256 =
        "6C2DD7CA087C4481B9B3970E8251C4E09846ED85FE1A279EB8A2F7E637B3FF15";

    [Fact]
    public void SyntheticAospLoaderIsAccepted()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "direct-aosp-abl",
            "abl-aosp-synthetic.bin");
        byte[] abl = File.ReadAllBytes(path);

        Assert.True(BootMaintenanceAblRecovery.AblAcceptsAosp(abl, CancellationToken.None));
        Assert.Equal(
            AospKeySha256,
            BootMaintenanceAblRecovery.PrimaryAblKey(abl, CancellationToken.None).KeySha256);
    }

    [Theory]
    [InlineData("abl_a.bin", "E7062378A1FE977DF95341E5D3023DD510CFFB283B11902A7CDBE74BA3E43885")]
    [InlineData("abl_b.bin", "890FBB34A96ADA40FD02539DACAA63EA9E2BFEBA922CF49968F480582541B172")]
    public void LocalOemLoaderWithSecondaryAospReferenceIsNotAccepted(string fileName, string expectedSha256)
    {
        string? directory = Environment.GetEnvironmentVariable("TB322FC_LOCAL_ABL_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        byte[] abl = File.ReadAllBytes(Path.Combine(Path.GetFullPath(directory), fileName));
        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(abl)));

        Assert.False(BootMaintenanceAblRecovery.AblAcceptsAosp(abl, CancellationToken.None));
        Assert.Equal(
            OemKeySha256,
            BootMaintenanceAblRecovery.PrimaryAblKey(abl, CancellationToken.None).KeySha256);

        // The secondary AOSP blob is still reported by the all-keys evidence view; it is deliberately
        // not used for the replacement decision.
        Assert.Contains(
            AospKeySha256,
            EdlRawBootBuilder.InspectAblAvbKeys(abl, CancellationToken.None)
                .Select(key => key.KeySha256));
    }

    [Fact]
    public void Reviewed188LoaderIsAccepted()
    {
        string? directory = Environment.GetEnvironmentVariable("TB322FC_188_IMAGES_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        string[] matches = Directory.GetFiles(
            Path.GetFullPath(directory),
            "abl.img",
            SearchOption.AllDirectories);
        string path = Assert.Single(matches);
        byte[] abl = File.ReadAllBytes(path);

        Assert.Equal(Reviewed188AblSha256, Convert.ToHexString(SHA256.HashData(abl)));
        Assert.True(BootMaintenanceAblRecovery.AblAcceptsAosp(abl, CancellationToken.None));
        Assert.Equal(
            new[] { AospKeySha256 },
            EdlRawBootBuilder.InspectAblAvbKeys(abl, CancellationToken.None)
                .Select(key => key.KeySha256)
                .ToArray());
    }
}
