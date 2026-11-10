using System;
using System.Reflection;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// The fingerprint pin describes one immutable historical manifest, so the legacy origin recorded in
/// that manifest is pinned with it.
/// </summary>
public sealed class InternalInstallLegacyPackageBindingTests
{
    private const string ContextTypeName = "FreedomInstaller.Core.InternalInstallPreparationContext";
    private const string FingerprintPin = "0424D61B0B545CAC4B76F089BF294CE5BF0B01911CB655A8D64754ABE22188B7";
    private const string OriginPin = "ostree-image-signed:docker://ghcr.io/mydanyi/armadaos-freedom:testing";

    private static readonly Type ContextType =
        typeof(PackageReader).Assembly.GetType(ContextTypeName, throwOnError: true)!;

    [Fact]
    public void PinnedOriginMatchesTheManifestBehindTheFingerprintPin()
    {
        Assert.Equal(FingerprintPin, ReadPin("PackageFingerprintPin"));
        Assert.Equal(OriginPin, ReadPin("SeedOriginPin"));
    }

    private static string ReadPin(string name) => (string)RequirePin(name).GetRawConstantValue()!;

    private static FieldInfo RequirePin(string name) =>
        ContextType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The frozen pin '" + name + "' is missing.");
}
