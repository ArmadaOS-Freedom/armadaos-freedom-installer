using System.IO.Compression;
using System.Security.Cryptography;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed partial class OfflineUpdatePackageTests
{
    private string WriteCiWrapper(Case c, string name, string prefix = "", Action<Dictionary<string, byte[]>>? mutate = null)
    {
        var entries = c.SourceFiles.ToDictionary(
            p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.Ordinal);
        entries["README"] = "ci wrapper"u8.ToArray();
        entries["update.zip.parts.json"] = "{}"u8.ToArray();
        mutate?.Invoke(entries);
        var path = Path.Combine(root, name + ".zip");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (var pair in entries)
            {
                var entry = zip.CreateEntry(prefix + pair.Key, CompressionLevel.NoCompression);
                using var output = entry.Open();
                output.Write(pair.Value);
            }
        return path;
    }

    [Theory]
    [InlineData("")]
    [InlineData("20261117.1f28617/")]
    public async Task CiWrapperImportsExactBytesAndCleansIntermediary(string prefix)
    {
        var c = new Case(Path.Combine(root, "ci-import-" + prefix.Replace('/', '_')), splitCount: 3);
        var outer = WriteCiWrapper(c, "ci-outer-" + prefix.Replace('/', '_'), prefix);
        var outerSnapshot = File.ReadAllBytes(outer);
        var work = Directory.CreateDirectory(Path.Combine(root, "ci-work-" + prefix.Replace('/', '_'))).FullName;
        var sentinel = Path.Combine(work, "sentinel");
        File.WriteAllText(sentinel, "keep");
        var sourceSnapshot = c.SourceFiles.ToDictionary(
            p => p, p => (Bytes: File.ReadAllBytes(p), Hash: SHA256.HashData(File.ReadAllBytes(p))));

        var imported = await Import(outer, Model, "1.0.0", work, c.Verify);

        Assert.Equal(Path.GetFullPath(outer), imported.SourcePath);
        Assert.Equal(c.DescriptorBytes, File.ReadAllBytes(imported.DescriptorPath));
        Assert.Equal(c.SignatureBytes, File.ReadAllBytes(imported.SignaturePath));
        var staged = Directory.EnumerateFiles(imported.PackageDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(imported.PackageDirectory, p).Replace('\\', '/'),
                File.ReadAllBytes);
        Assert.Equal(c.Entries.Keys.Order(), staged.Keys.Order());
        foreach (var pair in c.Entries) Assert.Equal(pair.Value, staged[pair.Key]);
        Assert.Empty(Directory.EnumerateFiles(work, "*.001", SearchOption.AllDirectories));

        var stage = Directory.GetParent(imported.PackageDirectory)!.FullName;
        imported.Dispose();
        Assert.False(Directory.Exists(stage));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Equal(outerSnapshot, File.ReadAllBytes(outer));
        Assert.Equal(new[] { sentinel }, Directory.EnumerateFileSystemEntries(work));
        foreach (var file in c.SourceFiles)
        {
            Assert.Equal(sourceSnapshot[file].Bytes, File.ReadAllBytes(file));
            Assert.Equal(sourceSnapshot[file].Hash, SHA256.HashData(File.ReadAllBytes(file)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CiWrapperCorruptedSecondPartIsRejectedWithoutStaging(bool corrupt)
    {
        var c = new Case(Path.Combine(root, "ci-bad-" + corrupt), splitCount: 3);
        var originalPart = File.ReadAllBytes(c.Part(2));
        var outer = corrupt
            ? WriteCiWrapper(c, "ci-bad-outer-" + corrupt, "", entries =>
            {
                var key = Path.GetFileName(c.Part(2));
                var bytes = entries[key];
                bytes[0] ^= 1;
            })
            : WriteCiWrapper(c, "ci-bad-outer-" + corrupt, "", entries =>
                entries.Remove(Path.GetFileName(c.Part(2))));
        var outerSnapshot = File.ReadAllBytes(outer);
        var work = Directory.CreateDirectory(Path.Combine(root, "ci-bad-work-" + corrupt)).FullName;

        await Assert.ThrowsAsync<InvalidDataException>(() => Import(outer, Model, "1.0.0", work, c.Verify));

        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
        Assert.Equal(outerSnapshot, File.ReadAllBytes(outer));
        Assert.Equal(originalPart, File.ReadAllBytes(c.Part(2)));
    }

    [Fact]
    public async Task CiWrapperSyntheticSignatureIsRefusedByPublicImport()
    {
        var c = new Case(Path.Combine(root, "ci-public"), operation: "install", splitCount: 3);
        var outer = WriteCiWrapper(c, "ci-public-outer");
        var work = Directory.CreateDirectory(Path.Combine(root, "ci-public-work")).FullName;

        using var imported = await PackageImporter.ImportAsync(
            outer, Model, InstallationTarget.Internal, work);
        Assert.False(imported.Validation.IsValid);

        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }
}
