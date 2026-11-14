using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using OfflineSshProbe;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// The offline SSH probe must bind the coordinator to the version declared by
/// the same bundle file it uploads, so these tests exercise the harness parser
/// against real tar.gz bytes rather than a copied string.
/// </summary>
public sealed class BundleManifestVersionTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("bundle-manifest-version-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Read_ReturnsVersionFromTheRootManifest()
    {
        var path = WriteBundle(
            ("usr/libexec/armada/armada-offline-update", TarEntryType.RegularFile, "#!/bin/sh\n"),
            ("manifest.json", TarEntryType.RegularFile, "{\"schemaVersion\":1,\"version\":\"0.8.0\"}"));

        Assert.Equal("0.8.0", BundleManifestVersion.Read(path));
    }

    [Fact]
    public void Read_RejectsABundleWithoutARootManifest()
    {
        var path = WriteBundle(
            ("usr/libexec/armada/armada-offline-update", TarEntryType.RegularFile, "#!/bin/sh\n"));

        var error = Assert.Throws<InvalidOperationException>(() => BundleManifestVersion.Read(path));

        Assert.Contains("manifest.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsANonRegularManifest()
    {
        var path = Path.Combine(_directory, "non-regular.tar.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.SymbolicLink, "manifest.json")
            {
                LinkName = "usr/libexec/armada/armada-offline-update",
            };
            writer.WriteEntry(entry);
        }

        var error = Assert.Throws<InvalidOperationException>(() => BundleManifestVersion.Read(path));

        Assert.Contains("regular file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsAnInvalidVersion()
    {
        var path = WriteBundle(
            ("manifest.json", TarEntryType.RegularFile, "{\"version\":\"0.8.0-rc1\"}"));

        var error = Assert.Throws<InvalidOperationException>(() => BundleManifestVersion.Read(path));

        Assert.Contains("version", error.Message, StringComparison.Ordinal);
    }

    private string WriteBundle(params (string Name, TarEntryType Type, string? Content)[] entries)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tar.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, type, content) in entries)
            {
                var entry = new PaxTarEntry(type, name);
                if (content is not null)
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                writer.WriteEntry(entry);
            }
        }

        return path;
    }
}
