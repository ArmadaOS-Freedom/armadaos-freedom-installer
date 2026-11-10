using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Regression tests for the signed-sidecar branch reachable through the public
/// <see cref="PackageImporter.ImportAsync"/> path. Sidecars use synthetic base64 filler, so no
/// test claims an actual signed import success.
/// </summary>
public sealed class UpdatePackageImportTests
{
    private const string ModelA = "TB322FC";
    private const string ModelB = "TB323FU";

    private static string DescriptorJson(string model, string directory) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            deviceModel = model,
            version = PackageFixture.PackageVersion,
            packages = new[] { new { deviceModel = model, directory } },
        });

    private static string WriteBundle(ImportFixture fixture)
    {
        var bundle = new BundleBuilder(fixture.StagingRoot);
        bundle.AddPackage(ModelA);
        bundle.AddPackage(ModelB);
        return bundle.Write(
            fixture.BundlePath(),
            BundleJson((ModelA, "models/" + ModelA), (ModelB, "models/" + ModelB)));
    }

    private static string BundleJson(params (string Model, string Directory)[] packages)
    {
        var bundle = new
        {
            schemaVersion = 1,
            bundleId = "armadaos-freedom",
            version = PackageFixture.PackageVersion,
            packages = packages.Select(p => new { deviceModel = p.Model, directory = p.Directory }).ToArray(),
        };
        return JsonSerializer.Serialize(bundle);
    }

    private static void WriteSidecars(string zipPath, bool descriptor, string? signature)
    {
        if (descriptor)
        {
            File.WriteAllText(zipPath + ".update.json", DescriptorJson(ModelA, "models/" + ModelA));
        }

        if (signature is not null)
        {
            File.WriteAllText(zipPath + ".update.json.sig", signature);
        }
    }

    private static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static void AssertUnchanged(Dictionary<string, byte[]> before, string root)
    {
        var after = Snapshot(root);
        Assert.Equal(before.Keys, after.Keys);
        foreach (var pair in before)
        {
            Assert.Equal(pair.Value, after[pair.Key]);
        }
    }

    private static void AssertNoWorkOutput(ImportFixture fixture)
    {
        Assert.Equal(
            new[] { "neighbor", "sentinel" },
            Directory.EnumerateDirectories(fixture.WorkRoot).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(ModelA)]
    [InlineData(ModelB)]
    public async Task Import_LegacyZipWithoutSidecarIsValidAndDescriptorIsNull(string model)
    {
        using var fixture = new ImportFixture();
        var zip = WriteBundle(fixture);

        using var imported = await PackageImporter.ImportAsync(
            zip, model, InstallationTarget.Internal, fixture.WorkRoot);

        Assert.True(imported.Validation.IsValid);
        Assert.Null(imported.UpdateDescriptor);
    }

    [Theory]
    [InlineData(ModelA)]
    [InlineData(ModelB)]
    public async Task Import_LegacyDirectoryIsNeverOwnedOrDeleted(string model)
    {
        using var fixture = new ImportFixture();
        var source = fixture.WriteLegacyPackage(model);

        using (var imported = await PackageImporter.ImportAsync(
            source, model, InstallationTarget.Internal, fixture.WorkRoot))
        {
            Assert.True(imported.Validation.IsValid);
            Assert.Null(imported.UpdateDescriptor);
            Assert.Equal(source, imported.PackageDirectory);
        }

        Assert.True(Directory.Exists(source));
        Assert.True(File.Exists(PackageFixture.MetadataPath(source)));
    }

    [Theory]
    [InlineData("descriptor-only", true, null)]
    [InlineData("signature-only", false, "TllBUnRydXN0ZWQ")]
    [InlineData("both-untrusted", true, "TllBUnRydXN0ZWQ=")]
    public async Task Import_UntrustedSidecarCombinationsAreRejectedWithoutTouchingWorkRoot(
        string scenario, bool descriptor, string? signature)
    {
        using var fixture = new ImportFixture();
        var zip = WriteBundle(fixture);
        WriteSidecars(zip, descriptor, signature);
        var before = Snapshot(fixture.WorkRoot);

        using var imported = await PackageImporter.ImportAsync(
            zip, ModelA, InstallationTarget.Internal, fixture.WorkRoot);

        Assert.False(imported.Validation.IsValid);
        Assert.NotEmpty(imported.Validation.Reasons);
        Assert.Null(imported.UpdateDescriptor);
        AssertUnchanged(before, fixture.WorkRoot);
        AssertNoWorkOutput(fixture);
    }

    [Fact]
    public async Task Import_SplitVolumeWithDescriptorButMissingSignatureIsRejectedBeforeExtraction()
    {
        using var fixture = new ImportFixture();
        var zip = WriteBundle(fixture);
        var bytes = File.ReadAllBytes(zip);
        var third = (bytes.Length + 2) / 3;

        for (var volume = 1; volume <= 3; volume++)
        {
            var part = bytes.Skip((volume - 1) * third).Take(third).ToArray();
            File.WriteAllBytes(zip + ".00" + volume, part);
        }

        File.Delete(zip);
        File.WriteAllText(
            Path.Combine(fixture.SourceRoot, "package.zip.update.json"),
            DescriptorJson(ModelA, "models/" + ModelA));
        var before = Snapshot(fixture.WorkRoot);

        using var imported = await PackageImporter.ImportAsync(
            Path.Combine(fixture.SourceRoot, "package.zip.002"),
            ModelA,
            InstallationTarget.Internal,
            fixture.WorkRoot);

        Assert.False(imported.Validation.IsValid);
        Assert.NotEmpty(imported.Validation.Reasons);
        Assert.Null(imported.UpdateDescriptor);
        AssertUnchanged(before, fixture.WorkRoot);
        AssertNoWorkOutput(fixture);
    }

    [Fact]
    public async Task Import_SplitVolumesWithoutSidecarsRemainValidLegacy()
    {
        using var fixture = new ImportFixture();
        var zip = WriteBundle(fixture);
        var bytes = File.ReadAllBytes(zip);
        var half = bytes.Length / 2;

        File.WriteAllBytes(zip + ".001", bytes.Take(half).ToArray());
        File.WriteAllBytes(zip + ".002", bytes.Skip(half).ToArray());
        File.Delete(zip);

        using var imported = await PackageImporter.ImportAsync(
            zip + ".002", ModelA, InstallationTarget.Internal, fixture.WorkRoot);

        Assert.True(imported.Validation.IsValid);
        Assert.Null(imported.UpdateDescriptor);
    }

    [Fact]
    public async Task Inspect_RejectsSidecarlessSourceAndCreatesNoFiles()
    {
        using var fixture = new ImportFixture();
        var zip = WriteBundle(fixture);
        var before = Snapshot(fixture.WorkRoot);
        var sourceBefore = Snapshot(fixture.SourceRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackageImporter.InspectUpdatePackageAsync(zip, ModelA, "install", "0.8.0"));

        AssertUnchanged(before, fixture.WorkRoot);
        AssertNoWorkOutput(fixture);
        AssertUnchanged(sourceBefore, fixture.SourceRoot);
    }

    [Fact]
    public void Core_AssemblyVersionIs08x()
    {
        var version = typeof(PackageImporter).Assembly.GetName().Version;
        Assert.NotNull(version);
        Assert.Equal("0.8.0", version!.ToString(3));
    }
}
