using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed partial class OfflineUpdatePackageTests
{
    [Theory]
    [InlineData("missing-signature")]
    [InlineData("corrupt-signature")]
    [InlineData("wrong-length")]
    [InlineData("extra")]
    [InlineData("case-extra")]
    [InlineData("traversal")]
    [InlineData("duplicate-case")]
    [InlineData("ambiguous")]
    [InlineData("foreign-volume")]
    [InlineData("foreign-dir-volume")]
    [InlineData("foreign-dir-volume-mixed-case")]
    public async Task CiWrapperRejectsIntegrityMutationsWithoutStaging(string mode)
    {
        var label = "ciwrap-" + mode;
        var c = new Case(Path.Combine(root, label), splitCount: 3);
        var source = WriteCiWrapper(c, label, mutate: entries =>
        {
            switch (mode)
            {
                case "missing-signature":
                    entries.Remove("update.zip.update.json.sig");
                    break;
                case "corrupt-signature":
                    entries["update.zip.update.json.sig"] = Encoding.ASCII.GetBytes("invalidbytes");
                    break;
                case "wrong-length":
                    entries["update.zip.002"] = entries["update.zip.002"].AsSpan(0, entries["update.zip.002"].Length - 1).ToArray();
                    break;
                case "extra":
                    entries["update.zip.999"] = new byte[] { 0 };
                    break;
                case "case-extra":
                    entries["UPDATE.ZIP.999"] = new byte[] { 0 };
                    break;
                case "traversal":
                    entries["../escape"] = new byte[] { 0 };
                    break;
                case "duplicate-case":
                    entries["UPDATE.ZIP.001"] = entries["update.zip.001"];
                    break;
                case "ambiguous":
                    entries["other/update.zip.update.json"] = c.DescriptorBytes;
                    break;
                case "foreign-volume":
                    entries["other-package.zip.001"] = new byte[] { 0 };
                    break;
                case "foreign-dir-volume":
                    entries["other/foreign-package.zip.001"] = new byte[] { 0 };
                    break;
                case "foreign-dir-volume-mixed-case":
                    entries["other/ForEIgn-Package.ZIP.001"] = new byte[] { 0 };
                    break;
            }
        });
        var work = Directory.CreateDirectory(Path.Combine(root, label + "-work")).FullName;
        File.WriteAllText(Path.Combine(work, "sentinel"), "keep");
        var outerBytes = File.ReadAllBytes(source);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Import(source, Model, "1.0.0", work, c.Verify));

        Assert.Equal(outerBytes, File.ReadAllBytes(source));
        var remaining = Directory.EnumerateFileSystemEntries(work).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "sentinel" }, remaining);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(work, "sentinel")));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("version")]
    [InlineData("operation")]
    public async Task CiWrapperRejectsPolicyMismatchWithoutStaging(string kind)
    {
        var label = "ciwrap-policy-" + kind;
        var operation = kind == "operation" ? "install" : "offline-upgrade";
        var c = new Case(Path.Combine(root, label), splitCount: 3, operation: operation);
        var source = WriteCiWrapper(c, label);
        var work = Directory.CreateDirectory(Path.Combine(root, label + "-work")).FullName;
        var model = kind == "model" ? "TB322FC" : Model;
        var version = kind == "version" ? "0.7.9" : "1.0.0";

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Import(source, model, version, work, c.Verify));

        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public async Task CiWrapperCancellationDuringVerifyLeavesWorkEmpty()
    {
        var label = "ciwrap-cancel";
        var c = new Case(Path.Combine(root, label), splitCount: 3);
        var source = WriteCiWrapper(c, label);
        var work = Directory.CreateDirectory(Path.Combine(root, label + "-work")).FullName;
        var outerBytes = File.ReadAllBytes(source);
        using var cts = new CancellationTokenSource();
        Func<byte[], byte[], UpdatePackageDescriptor> verify = (doc, sig) =>
        {
            cts.Cancel();
            return c.Verify(doc, sig);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Import(source, Model, "1.0.0", work, verify, cts.Token));

        Assert.Equal(outerBytes, File.ReadAllBytes(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public void CiWrapperPrepareInstallationSourceVerifiesAndCleans()
    {
        var label = "ciwrap-prepare";
        var c = new Case(Path.Combine(root, label), splitCount: 3, operation: "install");
        var source = WriteCiWrapper(c, label);
        var work = Directory.CreateDirectory(Path.Combine(root, label + "-work")).FullName;

        using var result = PackageImporter.PrepareCiArtifactSource(
            source, Model, "install", "1.0.0", work, c.Verify, CancellationToken.None);

        Assert.NotNull(result);
        PackageImporter.VerifyUpdateMediaFile(result!.MediaPath, c.Verify(c.DescriptorBytes, c.SignatureBytes));
        var stages = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).ToArray();
        Assert.Equal(5, stages.Length);
        result.Dispose();
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }
}
