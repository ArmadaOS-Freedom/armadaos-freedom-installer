using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed partial class OfflineUpdatePackageTests : IDisposable
{
    private const string Model = "TB323FU";
    private static readonly MethodInfo ImportMethod = typeof(PackageImporter).GetMethod(
        "ImportOfflineUpdateAsync", BindingFlags.Static | BindingFlags.NonPublic, null,
        [typeof(string), typeof(string), typeof(string), typeof(string),
         typeof(Func<byte[], byte[], UpdatePackageDescriptor>), typeof(CancellationToken)], null)!;
    private static readonly MethodInfo VerifyMethod = typeof(UpdateSignatureVerifier).GetMethod(
        "VerifyAndParseWithKey", BindingFlags.Static | BindingFlags.NonPublic, null,
        [typeof(byte[]), typeof(byte[]), typeof(string)], null)!;
    private static readonly MethodInfo ParseMethod = typeof(UpdatePackageDescriptor).GetMethod(
        "ParseVerifiedRoot", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "FreedomInstaller-offline-tests-" + Guid.NewGuid().ToString("N"));

    public OfflineUpdatePackageTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [LinuxFact]
    public async Task WorkRootBelowLinkedAncestorUsesPhysicalStageAndCleansOnlyThatStage()
    {
        var c = new Case(Path.Combine(root, "ancestor-package"));
        string physical = Directory.CreateDirectory(Path.Combine(root, "physical")).FullName;
        string work = Directory.CreateDirectory(Path.Combine(physical, "work")).FullName;
        string alias = Path.Combine(root, "alias");
        Directory.CreateSymbolicLink(alias, physical);
        File.WriteAllText(Path.Combine(work, "sentinel"), "keep");
        var imported = await Import(c.ArchivePath, Model, "1.0.0", Path.Combine(alias, "work"), c.Verify);
        Assert.StartsWith(work + Path.DirectorySeparatorChar, imported.PackageDirectory);
        imported.Dispose();
        Assert.Equal("keep", File.ReadAllText(Path.Combine(work, "sentinel")));
        Assert.Single(Directory.GetFileSystemEntries(work));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Import(c.ArchivePath, Model, "1.0.0", alias, c.Verify));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task SignedStoredZipStagesExactBytesAndDisposeOnlyOwnStage(int parts)
    {
        var c = new Case(Path.Combine(root, "valid-" + parts), parts);
        var work = Directory.CreateDirectory(Path.Combine(root, "work-" + parts)).FullName;
        var neighbor = Directory.CreateDirectory(Path.Combine(work, "neighbor")).FullName;
        var selected = parts == 0 ? c.ArchivePath : c.Part(2);
        // Snapshot exact bytes and SHA-256 of every real source file before import;
        // compare after Dispose to prove sources are untouched (not only existence).
        var sourceSnapshot = c.SourceFiles.ToDictionary(
            p => p,
            p =>
            {
                var bytes = File.ReadAllBytes(p);
                return (Bytes: bytes, Hash: SHA256.HashData(bytes));
            });
        var imported = await Import(selected, Model, "1.0.0", work, c.Verify);
        Assert.Equal(Model, imported.DeviceModel);
        Assert.Equal("release-1", imported.Descriptor.ReleaseId);
        Assert.Equal(c.DescriptorBytes, File.ReadAllBytes(imported.DescriptorPath));
        Assert.Equal(c.SignatureBytes, File.ReadAllBytes(imported.SignaturePath));
        var staged = Directory.EnumerateFiles(imported.PackageDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(imported.PackageDirectory, p).Replace('\\', '/'),
                File.ReadAllBytes);
        Assert.Equal(c.Entries.Keys.Order(), staged.Keys.Order());
        foreach (var pair in c.Entries) Assert.Equal(pair.Value, staged[pair.Key]);
        Assert.Single(c.Entries.Keys, p => p.Contains("linux-root", StringComparison.Ordinal));
        Assert.DoesNotContain(staged.Keys, p => p.EndsWith("linux-root.img", StringComparison.Ordinal));
        var stage = Directory.GetParent(imported.PackageDirectory)!.FullName;
        imported.Dispose();
        imported.Dispose();
        Assert.False(Directory.Exists(stage));
        Assert.True(Directory.Exists(work));
        Assert.True(Directory.Exists(neighbor));
        foreach (var file in c.SourceFiles)
        {
            Assert.True(File.Exists(file));
            Assert.Equal(sourceSnapshot[file].Bytes, File.ReadAllBytes(file));
            Assert.Equal(sourceSnapshot[file].Hash, SHA256.HashData(File.ReadAllBytes(file)));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(work).Where(p => p != neighbor));
    }

    [Fact]
    public async Task SignedPolicyAndSignatureFailuresAndPublicSyntheticKeyNeverStage()
    {
        await Reject("wrong-signature", wrongKey: true);
        await Reject("model", model: "TB322FC");
        await Reject("version", version: "0.7.9");
        await Reject("operation", operation: "install");
        var c = new Case(Path.Combine(root, "public"));
        var work = Directory.CreateDirectory(Path.Combine(root, "public-work")).FullName;
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => PackageImporter.ImportOfflineUpdateAsync(
            c.ArchivePath, Model, "1.0.0", work));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task SignedOfflineUpgradeWithoutRecoveryManifestsImportsAndStages(int parts)
    {
        var c = new Case(Path.Combine(root, "no-recovery-" + parts), parts,
            includeRecoveryManifests: false);
        var work = Directory.CreateDirectory(Path.Combine(root, "no-recovery-work-" + parts)).FullName;
        var selected = parts == 0 ? c.ArchivePath : c.Part(1);

        var imported = await Import(selected, Model, "1.0.0", work, c.Verify);

        Assert.Empty(imported.Descriptor.Root.GetProperty("recoveryManifests").EnumerateObject());
        Assert.DoesNotContain(c.Entries.Keys, p => p.StartsWith("recovery/", StringComparison.Ordinal));
        var staged = Directory.EnumerateFiles(imported.PackageDirectory, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(imported.PackageDirectory, p).Replace('\\', '/'));
        Assert.Equal(c.Entries.Keys.Order(), staged.Order());
        var stage = Directory.GetParent(imported.PackageDirectory)!.FullName;
        imported.Dispose();
        Assert.False(Directory.Exists(stage));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public async Task SignedDescriptorWithUnindexedRecoveryManifestIsRejectedWithoutStaging()
    {
        var c = new Case(Path.Combine(root, "unindexed-recovery"),
            includeRecoveryManifests: false,
            mutateDescriptor: json => json["recoveryManifests"] =
                new JsonObject { [Model] = "recovery/missing.json" });
        var work = Directory.CreateDirectory(Path.Combine(root, "unindexed-recovery-work")).FullName;

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Import(
            c.ArchivePath, Model, "1.0.0", work, c.Verify));

        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public async Task CorruptArchiveMissingPartAndCancellationNeverStage()
    {
        var corrupt = new Case(Path.Combine(root, "corrupt"));
        var bytes = File.ReadAllBytes(corrupt.ArchivePath);
        bytes[bytes.Length / 2] ^= 1;
        File.WriteAllBytes(corrupt.ArchivePath, bytes);
        var work = Directory.CreateDirectory(Path.Combine(root, "corrupt-work")).FullName;
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Import(
            corrupt.ArchivePath, Model, "1.0.0", work, corrupt.Verify));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
        var missing = new Case(Path.Combine(root, "missing"), 3);
        File.Delete(missing.Part(2));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Import(
            missing.Part(1), Model, "1.0.0", work, missing.Verify));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Import(
            corrupt.ArchivePath, Model, "1.0.0", work, corrupt.Verify, cancel.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public async Task WorkRootMustAlreadyExistAndCannotBeSymlink()
    {
        var c = new Case(Path.Combine(root, "roots"));
        var missing = Path.Combine(root, "missing-work");
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Import(
            c.ArchivePath, Model, "1.0.0", missing, c.Verify));
        Assert.False(Directory.Exists(missing));
        var target = Directory.CreateDirectory(Path.Combine(root, "link-target")).FullName;
        var link = Path.Combine(root, "link-work");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or IOException) { return; }
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Import(
            c.ArchivePath, Model, "1.0.0", link, c.Verify));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task CleanupRejectsNestedDirectoryLinkWithoutDeletingTarget()
    {
        var c = new Case(Path.Combine(root, "cleanup"));
        var work = Directory.CreateDirectory(Path.Combine(root, "cleanup-work")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep"), "keep");
        var imported = await Import(c.ArchivePath, Model, "1.0.0", work, c.Verify);
        try { Directory.CreateSymbolicLink(Path.Combine(imported.PackageDirectory, "escape"), outside); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or IOException) { imported.Dispose(); return; }
        Assert.Throws<InvalidDataException>(imported.Dispose);
        Assert.True(File.Exists(Path.Combine(outside, "keep")));
        Assert.True(Directory.Exists(imported.PackageDirectory));
    }

    private async Task Reject(string name, bool wrongKey = false, string model = Model,
        string version = "1.0.0", string operation = "offline-upgrade")
    {
        var c = new Case(Path.Combine(root, name), operation: operation, wrongKey: wrongKey);
        var work = Directory.CreateDirectory(Path.Combine(root, name + "-work")).FullName;
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Import(
            c.ArchivePath, model, version, work, c.Verify));
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    private static async Task<ImportedUpdatePackage> Import(string source, string model, string version,
        string work, Func<byte[], byte[], UpdatePackageDescriptor> verify, CancellationToken token = default) =>
        await (Task<ImportedUpdatePackage>)ImportMethod.Invoke(null,
            [source, model, version, work, verify, token])!;

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(bytes);
        return output.ToArray();
    }

    private sealed class Case
    {
        internal Case(string directory, int splitCount = 0, string operation = "offline-upgrade", bool wrongKey = false,
            string deviceModel = OfflineUpdatePackageTests.Model, byte[]? rootPayload = null,
            bool includeRecoveryManifests = true, Action<JsonObject>? mutateDescriptor = null)
        {
            var Model = deviceModel;
            Directory.CreateDirectory(directory);
            ArchivePath = Path.Combine(directory, "update.zip");
            Entries = new Dictionary<string, byte[]>
            {
                ["bundle.json"] = "{}"u8.ToArray(),
                [$"models/{Model}/package.json"] = "{ }"u8.ToArray(),
                ["images/linux-root.img.gz"] = Gzip(rootPayload ?? "expanded-root"u8.ToArray()),
                ["images/linux-boot.img.gz"] = Gzip("expanded-boot"u8.ToArray())
            };
            if (includeRecoveryManifests)
                Entries[$"recovery/{Model}/package.json"] = "{}"u8.ToArray();
            using (var stream = File.Create(ArchivePath))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var pair in Entries)
                {
                    var entry = zip.CreateEntry(pair.Key, CompressionLevel.NoCompression);
                    using var output = entry.Open();
                    output.Write(pair.Value);
                }
            var archive = File.ReadAllBytes(ArchivePath);
            var files = new JsonArray();
            foreach (var pair in Entries)
            {
                var expanded = pair.Key.EndsWith(".gz", StringComparison.Ordinal);
                using var input = new MemoryStream(pair.Value);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var plain = new MemoryStream();
                if (expanded) gzip.CopyTo(plain);
                var expandedBytes = expanded ? plain.ToArray() : pair.Value;
                files.Add(new JsonObject { ["path"] = pair.Key, ["bytes"] = pair.Value.LongLength,
                    ["sha256"] = Hash(pair.Value), ["expandedBytes"] = expandedBytes.LongLength,
                    ["expandedSha256"] = Hash(expandedBytes), ["compression"] = expanded ? "gzip" : "none",
                    ["role"] = pair.Key switch { "bundle.json" => "manifest",
                        "images/linux-root.img.gz" => "linux-root", "images/linux-boot.img.gz" => "linux-boot",
                        _ => "manifest" } });
            }
            var parts = new JsonArray();
            var partPaths = new List<string>();
            if (splitCount > 0)
            {
                var size = (archive.Length + splitCount - 1) / splitCount;
                for (var i = 0; i < splitCount; i++)
                {
                    var data = archive.Skip(i * size).Take(size).ToArray();
                    var path = ArchivePath + $".{i + 1:000}";
                    File.WriteAllBytes(path, data);
                    parts.Add(new JsonObject { ["fileName"] = Path.GetFileName(path), ["bytes"] = data.LongLength,
                        ["sha256"] = Hash(data) });
                    partPaths.Add(path);
                }
                File.Delete(ArchivePath);
                // Track the volumes and metadata sidecars that actually exist on disk;
                // update.zip was removed by the numeric split.
                SourceFiles = [.. partPaths,
                    ArchivePath + ".update.json", ArchivePath + ".update.json.sig"];
            }

            SourceFiles ??= [ArchivePath, ArchivePath + ".update.json", ArchivePath + ".update.json.sig"];
            var json = new JsonObject { ["schemaVersion"] = 1, ["kind"] = "freedom-universal-package",
                ["product"] = "armadaos-freedom", ["architecture"] = "aarch64", ["releaseId"] = "release-1",
                ["version"] = "1.0.0", ["sourceRevision"] = new string('1', 40),
                ["ociDigest"] = "sha256:" + new string('2', 64), ["ostreeCommit"] = new string('3', 64),
                ["minimumInstallerVersion"] = "0.8.0", ["minimumUpdaterVersion"] = "0.8.0",
                ["supportedModels"] = new JsonArray(Model), ["operations"] = new JsonArray(operation),
                ["archive"] = new JsonObject { ["fileName"] = "update.zip", ["bytes"] = archive.LongLength,
                    ["sha256"] = Hash(archive) }, ["parts"] = parts, ["files"] = files,
                ["modelManifests"] = new JsonObject { [Model] = $"models/{Model}/package.json" },
                ["recoveryManifests"] = includeRecoveryManifests && operation == "offline-upgrade"
                    ? new JsonObject { [Model] = $"recovery/{Model}/package.json" } : new JsonObject(),
                ["deckySnapshot"] = null };
            mutateDescriptor?.Invoke(json);
            DescriptorBytes = Encoding.UTF8.GetBytes(" \r\n" + json.ToJsonString() + "\n\t ");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            // Export the PUBLIC PEM while the key is alive; the Verify delegate must
            // not capture or retain the ECDsa instance (disposed at constructor exit)
            // and must never hold private key material.
            var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
            using var signer = wrongKey ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : key;
            SignatureBytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(signer.SignData(
                DescriptorBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
            File.WriteAllBytes(ArchivePath + ".update.json", DescriptorBytes);
            File.WriteAllBytes(ArchivePath + ".update.json.sig", SignatureBytes);
            Verify = (doc, sig) =>
            {
                try
                {
                    using var verified = (JsonDocument)VerifyMethod.Invoke(null,
                        [doc, sig, publicKeyPem])!;
                    return (UpdatePackageDescriptor)ParseMethod.Invoke(null, [verified.RootElement])!;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            };
        }
        internal string ArchivePath { get; }
        internal byte[] DescriptorBytes { get; }
        internal byte[] SignatureBytes { get; }
        internal Dictionary<string, byte[]> Entries { get; }
        internal string[] SourceFiles { get; }
        internal Func<byte[], byte[], UpdatePackageDescriptor> Verify { get; }
        internal string Part(int number) => ArchivePath + $".{number:000}";
    }
}
