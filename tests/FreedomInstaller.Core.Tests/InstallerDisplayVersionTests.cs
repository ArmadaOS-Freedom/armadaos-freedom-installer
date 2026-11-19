using System.Reflection;
using System.Reflection.Emit;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class InstallerDisplayVersionTests
{
    private const string Sha40 = "aaa956ad7edd5a87e8001d1e6e963acc82b9f978";
    private const string Sha64 =
        "aaa956ad7edd5a87e8001d1e6e963acc82b9f978aaa956ad7edd5a87e8001d1e";

    private static AssemblyBuilder NewAssembly(string name, Version version, string? informationalVersion)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(name) { Version = version },
            AssemblyBuilderAccess.Run);

        if (informationalVersion is not null)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!,
                new object[] { informationalVersion }));
        }

        return builder;
    }

    [Fact]
    public void ForAssembly_WithActualSha40_ReturnsNumericPlusFirstSeven()
    {
        var asm = NewAssembly("Sha40Display", new Version(0, 9, 11, 0), "0.9.11+" + Sha40);
        Assert.Equal("0.9.11+aaa956a", InstallerDisplayVersion.ForAssembly(asm));
    }

    [Fact]
    public void TryGetCommitForAssembly_WithActualSha40_ReturnsFullLowercaseSha()
    {
        var asm = NewAssembly("Sha40Full", new Version(0, 9, 11, 0), "0.9.11+" + Sha40.ToUpperInvariant());
        Assert.Equal(Sha40, InstallerDisplayVersion.TryGetCommitForAssembly(asm));
    }

    [Fact]
    public void TryGetCommitForAssembly_WithActualSha64_ReturnsFullLowercaseSha()
    {
        var asm = NewAssembly("Sha64Full", new Version(0, 9, 11, 0), "0.9.11+" + Sha64.ToUpperInvariant());
        Assert.Equal(Sha64, InstallerDisplayVersion.TryGetCommitForAssembly(asm));
    }

    [Fact]
    public void ForAssembly_WithoutInformationalVersion_ReturnsNumericOnly()
    {
        var asm = NewAssembly("NoInfo", new Version(0, 9, 11, 0), null);
        Assert.Equal("0.9.11", InstallerDisplayVersion.ForAssembly(asm));
        Assert.Null(InstallerDisplayVersion.TryGetCommitForAssembly(asm));
    }

    [Fact]
    public void TryGetCommitForAssembly_WithShortHash_ReturnsNull()
    {
        var asm = NewAssembly("ShortHash", new Version(0, 9, 11, 0), "0.9.11+aaa956a");
        Assert.Null(InstallerDisplayVersion.TryGetCommitForAssembly(asm));
    }

    [Fact]
    public void TryGetCommitForAssembly_WithPrereleaseBeforeValidSha_ReturnsFullSha()
    {
        var asm = NewAssembly("PrereleaseSha", new Version(0, 9, 11, 0), "0.9.11-rc.1+" + Sha40);
        Assert.Equal(Sha40, InstallerDisplayVersion.TryGetCommitForAssembly(asm));
    }

    [Fact]
    public void TryGetCommitForAssembly_WithExistingMetadataBeforeValidSha_ReturnsFullSha()
    {
        var asm = NewAssembly("ExistingMeta", new Version(0, 9, 11, 0), "0.9.11+build.1." + Sha40);
        Assert.Equal(Sha40, InstallerDisplayVersion.TryGetCommitForAssembly(asm));
    }

    [Fact]
    public void TryGetCommitForAssembly_WithPriorShaButInvalidFinalSegment_ReturnsNull()
    {
        var asm = NewAssembly("PriorShaInvalidFinal", new Version(0, 9, 11, 0), "0.9.11+" + Sha40 + ".not-hex");
        Assert.Null(InstallerDisplayVersion.TryGetCommitForAssembly(asm));
    }
}
