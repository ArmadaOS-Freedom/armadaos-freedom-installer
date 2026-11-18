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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CiWrapperRootBundleIsRejectedWithoutStaging(bool moveToDirectories)
    {
        var label = "ciwrap-root-bundle";
        var c = new Case(Path.Combine(root, label), splitCount: 3, operation: "install");
        var source = WriteCiWrapper(c, label, "", entries =>
        {
            if (moveToDirectories)
            {
                var moved = entries.ToArray();
                entries.Clear();
                foreach (var pair in moved)
                {
                    if (pair.Key == "bundle.json")
                    {
                        continue;
                    }

                    var target = pair.Key is "update.zip.update.json" or "update.zip.update.json.sig"
                        ? "ci/meta/" + pair.Key
                        : "ci/media/" + pair.Key;
                    entries[target] = pair.Value;
                }
            }

            entries["bundle.json"] = Encoding.UTF8.GetBytes("{}");
        });
        var work = Directory.CreateDirectory(Path.Combine(root, label + "-work")).FullName;
        File.WriteAllText(Path.Combine(work, "sentinel"), "keep");

        Assert.ThrowsAny<InvalidDataException>(() => PackageImporter.PrepareCiArtifactSource(
            source, Model, "install", "1.0.0", work, c.Verify, CancellationToken.None));

        var remaining = Directory.EnumerateFileSystemEntries(work).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "sentinel" }, remaining);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(work, "sentinel")));
    }

    [Fact]
    public void CiWrapperDisposeRetriesAfterRejectedStagePath()
    {
        var label = "ciwrap-dispose-retry";
        var c = new Case(Path.Combine(root, label), splitCount: 3, operation: "install");
        var source = WriteCiWrapper(c, label);
        var work = Directory.CreateDirectory(Path.Combine(root, label + "-work")).FullName;
        var result = PackageImporter.PrepareCiArtifactSource(
            source, Model, "install", "1.0.0", work, c.Verify, CancellationToken.None)!;
        var stageRoot = Path.GetDirectoryName(result.MediaPath)!;
        var spare = stageRoot + "-spare";
        Directory.Move(stageRoot, spare);
        File.WriteAllText(stageRoot, "blocker");
        try
        {
            Assert.ThrowsAny<Exception>(() => result.Dispose());
        }
        finally
        {
            File.Delete(stageRoot);
            Directory.Move(spare, stageRoot);
        }

        result.Dispose();
        Assert.False(Directory.Exists(stageRoot));
    }

    [Fact]
    public async Task CiWrapperCancellationSurvivesFailedPhysicalCleanup()
    {
        var label = "ciwrap-cancel-cleanup";
        var c = new Case(Path.Combine(root, label), splitCount: 3, operation: "offline-upgrade");
        var source = WriteCiWrapper(c, label);
        var work = Directory.CreateDirectory(Path.Combine(root, label + "-work")).FullName;
        using var cts = new CancellationTokenSource();
        var invocations = 0;
        string? stageRoot = null;
        string? spare = null;
        string? blocker = null;
        Func<byte[], byte[], UpdatePackageDescriptor> verify = (doc, sig) =>
        {
            invocations++;
            if (invocations == 2)
            {
                var stages = Directory.EnumerateDirectories(work, "offline-update-*").ToArray();
                Assert.Single(stages);
                stageRoot = stages[0];
                spare = stageRoot + "-spare";
                blocker = stageRoot;
                Directory.Move(stageRoot, spare);
                File.WriteAllText(stageRoot, "blocker");
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            return c.Verify(doc, sig);
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Import(source, Model, "1.0.0", work, verify, cts.Token));
        }
        finally
        {
            if (blocker is not null && File.Exists(blocker))
            {
                File.Delete(blocker);
            }
            if (spare is not null && stageRoot is not null && Directory.Exists(spare) && !Directory.Exists(stageRoot))
            {
                Directory.Move(spare, stageRoot);
            }
        }
    }
}
