// tests/FreedomInstaller.Core.Tests/UpdatePackageInteropTests.cs
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public class UpdatePackageInteropTests
{
    private static string FixtureRoot => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "update-package-v1");

    private static readonly string[] Models =
        ["TB322FC", "TB323FU", "TB710FU"];

    private static UpdatePackageDescriptor ParseDescriptor(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var method = typeof(UpdatePackageDescriptor).GetMethod(
            "ParseVerifiedRoot",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ParseVerifiedRoot not found");
        try
        {
            return (UpdatePackageDescriptor)method.Invoke(null, [doc.RootElement])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void VerifyMedia(string mediaPath, UpdatePackageDescriptor descriptor)
    {
        var method = typeof(PackageImporter).GetMethod(
            "VerifyUpdateMediaFile",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VerifyUpdateMediaFile not found");
        try
        {
            method.Invoke(null, [mediaPath, descriptor, CancellationToken.None]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void AssertCommonContract(UpdatePackageDescriptor descriptor)
    {
        var root = descriptor.Root;
        Assert.Equal(
            Models,
            root.GetProperty("supportedModels").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            ["install"],
            root.GetProperty("operations").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Empty(root.GetProperty("recoveryManifests").EnumerateObject());

        var roles = root.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("role").GetString()!)
            .ToArray();
        Assert.Equal(1, roles.Count(r => r == "linux-root"));
        Assert.Equal(1, roles.Count(r => r == "linux-boot"));

        foreach (var model in Models)
        {
            descriptor.RequireOperation(model, "install", "0.8.0");
            Assert.Throws<InvalidDataException>(
                () => descriptor.RequireOperation(model, "offline-upgrade", "0.8.0"));
        }
    }

    [Fact]
    public void SingleArchiveDescriptorMeetsInstallerContract()
    {
        var root = FixtureRoot;
        var descriptor = ParseDescriptor(Path.Combine(
            root, "update-bundle.zip.update.json"));
        AssertCommonContract(descriptor);
        VerifyMedia(Path.Combine(root, "update-bundle.zip"), descriptor);
    }

    [Fact]
    public void SplitArchiveDescriptorMeetsInstallerContract()
    {
        var root = Path.Combine(FixtureRoot, "split");
        var descriptor = ParseDescriptor(Path.Combine(
            root, "update-bundle.zip.update.json"));
        AssertCommonContract(descriptor);
        VerifyMedia(Path.Combine(root, "update-bundle.zip.002"), descriptor);
    }

    [Fact]
    public void CorruptedSplitPartFailsWholeMediaVerification()
    {
        var owned = Path.Combine(TestArtifactRoot.Resolve(), Guid.NewGuid().ToString("N"));
        try
        {
            CopyTree(new DirectoryInfo(FixtureRoot), new DirectoryInfo(owned));
            var part = Path.Combine(owned, "split", "update-bundle.zip.002");
            var bytes = File.ReadAllBytes(part);
            bytes[bytes.Length / 2] ^= 0xFF; // same length, single flipped byte
            File.WriteAllBytes(part, bytes);

            var descriptor = ParseDescriptor(Path.Combine(
                owned, "split", "update-bundle.zip.update.json"));
            Assert.Throws<InvalidDataException>(
                () => VerifyMedia(part, descriptor));
        }
        finally
        {
            // Clean up only our owned copy; checked-in fixtures stay untouched.
            if (Directory.Exists(owned))
                Directory.Delete(owned, recursive: true);
        }
    }

    private static void CopyTree(DirectoryInfo source, DirectoryInfo target)
    {
        target.Create();
        foreach (var file in source.GetFiles())
            file.CopyTo(Path.Combine(target.FullName, file.Name));
        foreach (var dir in source.GetDirectories())
            CopyTree(dir, new DirectoryInfo(Path.Combine(target.FullName, dir.Name)));
    }
}
