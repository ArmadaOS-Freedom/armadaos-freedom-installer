using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class UpdatePackageMediaTests : IDisposable
{
    private const string Model = "TB323FU";
    private static readonly MethodInfo VerifyMethod = typeof(PackageImporter).GetMethod(
        "VerifyUpdateMediaFile", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ParseMethod = typeof(UpdatePackageDescriptor).GetMethod(
        "ParseVerifiedRoot", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "FreedomInstaller-media-tests-" + Guid.NewGuid().ToString("N"));

    public UpdatePackageMediaTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void ValidStoredZipAndNumericSplitVolumesPass()
    {
        var single = CreateCase("single");
        Verify(single.Path, single);

        var split = CreateCase("split", splitCount: 3);
        Verify(Path.Combine(split.Directory, split.BaseName + ".002"), split);

        var oneVolume = CreateCase("one-volume", splitCount: 1);
        Verify(oneVolume.Path, oneVolume);
    }

    [Theory]
    [InlineData("middle")]
    [InlineData("tail")]
    [InlineData("extra")]
    public void SplitVolumeSetMustMatchDescriptor(string mode)
    {
        var c = CreateCase(mode, splitCount: 3);
        if (mode == "middle") File.Delete(Part(c, 2));
        if (mode == "tail") File.Delete(Part(c, 3));
        if (mode == "extra") File.WriteAllBytes(Part(c, 4), [0]);
        AssertMediaInvalid(() => Verify(Part(c, 1), c));
    }

    [Fact]
    public void WrongWidthAndSameSizeCorruptionAreRejected()
    {
        var wrongWidth = CreateCase("wrong-width", splitCount: 3);
        var renamed = Path.Combine(wrongWidth.Directory, wrongWidth.BaseName + ".00001");
        File.Move(Part(wrongWidth, 1), renamed);
        AssertMediaInvalid(() => Verify(renamed, wrongWidth));

        var corrupt = CreateCase("corrupt", splitCount: 3);
        var bytes = File.ReadAllBytes(Part(corrupt, 2));
        bytes[0] ^= 0x80;
        File.WriteAllBytes(Part(corrupt, 2), bytes);
        AssertMediaInvalid(() => Verify(corrupt.Path, corrupt));
    }

    [Fact]
    public void SignedPartAndWholeArchiveDigestMismatchAreRejected()
    {
        var part = CreateCase("part-digest", splitCount: 3);
        part.Descriptor["parts"]![1]!["sha256"] = new string('0', 64);
        AssertMediaInvalid(() => Verify(part.Path, part));

        var whole = CreateCase("whole-digest");
        whole.Descriptor["archive"]!["sha256"] = new string('0', 64);
        AssertMediaInvalid(() => Verify(whole.Path, whole));
    }

    [Fact]
    public void ExtraAndMissingZipEntriesAreRejected()
    {
        var standard = StandardEntries();
        var extraEntries = standard.Concat([new EntrySpec(
            "extra.bin", [1, 2, 3], null, null, "extra", null)]).ToArray();
        var extra = CreateCase("extra-entry", archiveEntries: extraEntries);
        AssertMediaInvalid(() => Verify(extra.Path, extra));

        var missing = CreateCase(
            "missing-entry",
            archiveEntries: standard.Where(e => e.Path != "images/linux-boot.img").ToArray());
        AssertMediaInvalid(() => Verify(missing.Path, missing));
    }

    [Fact]
    public void RawAndExpandedDigestMismatchAndExpandedSizeOverrunAreRejected()
    {
        var raw = CreateCase("raw-hash");
        FileNode(raw, "images/linux-root.img.gz")["sha256"] = new string('0', 64);
        AssertMediaInvalid(() => Verify(raw.Path, raw));

        var expanded = CreateCase("expanded-hash");
        FileNode(expanded, "images/linux-root.img.gz")["expandedSha256"] = new string('0', 64);
        AssertMediaInvalid(() => Verify(expanded.Path, expanded));

        var overrun = CreateCase("expanded-bound");
        var node = FileNode(overrun, "images/linux-root.img.gz");
        var expected = node["expandedBytes"]!.GetValue<long>();
        node["expandedBytes"] = expected - 1;
        AssertMediaInvalid(() => Verify(overrun.Path, overrun));
    }

    [Fact]
    public void StrictCompressionValidationIsEnabledAtStartup()
    {
        Assert.True(AppContext.TryGetSwitch(
            "System.IO.Compression.UseStrictValidation", out var enabled) && enabled);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void TruncatedGzipIsRejected(int bytes)
    {
        var entries = StandardEntries();
        var rootEntry = entries.Single(e => e.Path == "images/linux-root.img.gz");
        var truncated = rootEntry.Data[..^bytes];
        entries[Array.IndexOf(entries, rootEntry)] = rootEntry with { Data = truncated };
        var c = CreateCase($"truncated-gzip-{bytes}", archiveEntries: entries, descriptorEntries: entries);
        AssertMediaInvalid(() => Verify(c.Path, c));
    }

    [Theory]
    [InlineData("crc", 8)]
    [InlineData("isize", 4)]
    public void CorruptGzipTrailerIsRejected(string kind, int offset)
    {
        var entries = StandardEntries();
        var rootEntry = entries.Single(e => e.Path == "images/linux-root.img.gz");
        var corrupt = rootEntry.Data.ToArray();
        corrupt[^offset] ^= 0x01;
        entries[Array.IndexOf(entries, rootEntry)] = rootEntry with { Data = corrupt };
        var c = CreateCase($"corrupt-gzip-{kind}", archiveEntries: entries, descriptorEntries: entries);
        AssertMediaInvalid(() => Verify(c.Path, c));
    }

    [Fact]
    public void ConcatenatedGzipMembersAreFullyExpandedAndVerified()
    {
        var entries = StandardEntries();
        var rootEntry = entries.Single(e => e.Path == "images/linux-root.img.gz");
        var secondExpanded = "second-member"u8.ToArray();
        var concatenated = rootEntry.Data.Concat(Gzip(secondExpanded)).ToArray();
        var expanded = rootEntry.Expanded!.Concat(secondExpanded).ToArray();
        entries[Array.IndexOf(entries, rootEntry)] = rootEntry with
        {
            Data = concatenated,
            Expanded = expanded
        };
        var c = CreateCase("concatenated-gzip", archiveEntries: entries, descriptorEntries: entries);
        Verify(c.Path, c);
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("fifo")]
    [InlineData("directory")]
    public void NonRegularZipEntriesAreRejected(string kind)
    {
        var entries = StandardEntries();
        if (kind == "directory")
        {
            entries = entries.Concat([new EntrySpec(
                "directory/", [], null, null, "directory", null)]).ToArray();
        }
        else
        {
            uint mode = kind == "symlink" ? 0xA1EDu : 0x11EDu;
            entries[0] = entries[0] with
            {
                Configure = e => e.ExternalAttributes = unchecked((int)(mode << 16))
            };
        }
        var c = CreateCase(kind, archiveEntries: entries);
        AssertMediaInvalid(() => Verify(c.Path, c));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeflateAndEncryptedZipEntriesAreRejected(bool encrypted)
    {
        var c = CreateCase(encrypted ? "encrypted" : "deflate", patch: bytes =>
        {
            PatchFirstEntry(bytes, 0x04034b50, encrypted ? 6 : 8, encrypted ? (short)1 : (short)8);
            PatchFirstEntry(bytes, 0x02014b50, encrypted ? 8 : 10, encrypted ? (short)1 : (short)8);
            return bytes;
        });
        AssertMediaInvalid(() => Verify(c.Path, c));
    }

    [Fact]
    public void CancellationIsNotWrapped()
    {
        var c = CreateCase("cancelled");
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Verify(c.Path, c, source.Token));
    }

    [Fact]
    public async Task InspectRequiresBothSidecarsAndTrustedSignatureWithoutCreatingFiles()
    {
        await AssertInspectDenied("missing", null, null);
        await AssertInspectDenied("descriptor-only", "{}", null);
        await AssertInspectDenied("signature-only", null, "AA==");
        await AssertInspectDenied("untrusted", "{}", Convert.ToBase64String(new byte[64]));
    }

    private async Task AssertInspectDenied(string name, string? descriptor, string? signature)
    {
        var c = CreateCase(name);
        var basePath = Path.Combine(c.Directory, c.BaseName + ".update.json");
        if (descriptor is not null) await File.WriteAllTextAsync(basePath, descriptor);
        if (signature is not null) await File.WriteAllTextAsync(basePath + ".sig", signature);
        var before = Directory.EnumerateFiles(c.Directory).Order().ToArray();
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => PackageImporter.InspectUpdatePackageAsync(
            c.Path, Model, "install", "1.0.0"));
        Assert.Equal(before, Directory.EnumerateFiles(c.Directory).Order().ToArray());
    }

    private static void Verify(string path, Case c, CancellationToken token = default)
    {
        using var document = JsonDocument.Parse(c.Descriptor.ToJsonString());
        try
        {
            var descriptor = ParseMethod.Invoke(null, [document.RootElement])!;
            VerifyMethod.Invoke(null, [path, descriptor, token]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void AssertMediaInvalid(Action action) =>
        Assert.IsType<InvalidDataException>(Record.Exception(action));

    private Case CreateCase(
        string name,
        int splitCount = 0,
        EntrySpec[]? archiveEntries = null,
        EntrySpec[]? descriptorEntries = null,
        Func<byte[], byte[]>? patch = null)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var baseName = "update.zip";
        var archivePath = Path.Combine(directory, baseName);
        archiveEntries ??= StandardEntries();
        descriptorEntries ??= StandardEntries();
        using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var item in archiveEntries)
            {
                var entry = zip.CreateEntry(item.Path, CompressionLevel.NoCompression);
                item.Configure?.Invoke(entry);
                using var output = entry.Open();
                output.Write(item.Data);
            }
        }
        var archiveBytes = File.ReadAllBytes(archivePath);
        if (patch is not null) File.WriteAllBytes(archivePath, archiveBytes = patch(archiveBytes));
        var parts = new JsonArray();
        string selectedPath;
        if (splitCount > 0)
        {
            var chunk = (archiveBytes.Length + splitCount - 1) / splitCount;
            for (var i = 0; i < splitCount; i++)
            {
                var data = archiveBytes.Skip(i * chunk).Take(chunk).ToArray();
                var partName = $"{baseName}.{i + 1:000}";
                File.WriteAllBytes(Path.Combine(directory, partName), data);
                parts.Add(PartNode(partName, data));
            }
            File.Delete(archivePath);
            selectedPath = Path.Combine(directory, baseName + (splitCount == 1 ? ".001" : ".002"));
        }
        else selectedPath = archivePath;
        var files = new JsonArray();
        foreach (var item in descriptorEntries)
        {
            var expanded = item.Expanded ?? item.Data;
            files.Add(new JsonObject
            {
                ["path"] = item.Path, ["bytes"] = item.Data.LongLength,
                ["sha256"] = Hash(item.Data), ["expandedBytes"] = expanded.LongLength,
                ["expandedSha256"] = Hash(expanded),
                ["compression"] = item.Expanded is null ? "none" : "gzip", ["role"] = item.Role
            });
        }
        var descriptor = new JsonObject
        {
            ["schemaVersion"] = 1, ["kind"] = "freedom-universal-package",
            ["product"] = "armadaos-freedom", ["architecture"] = "aarch64",
            ["releaseId"] = "release-1", ["version"] = "1.0.0",
            ["sourceRevision"] = new string('1', 40),
            ["ociDigest"] = "sha256:" + new string('2', 64), ["ostreeCommit"] = new string('3', 64),
            ["minimumInstallerVersion"] = "0.8.0", ["minimumUpdaterVersion"] = "0.8.0",
            ["supportedModels"] = new JsonArray(Model), ["operations"] = new JsonArray("install"),
            ["archive"] = new JsonObject
            {
                ["fileName"] = baseName, ["bytes"] = archiveBytes.LongLength, ["sha256"] = Hash(archiveBytes)
            },
            ["parts"] = parts, ["files"] = files,
            ["modelManifests"] = new JsonObject { [Model] = $"models/{Model}/package.json" },
            ["recoveryManifests"] = new JsonObject(), ["deckySnapshot"] = null
        };
        return new Case(directory, baseName, selectedPath, descriptor);
    }

    private static EntrySpec[] StandardEntries()
    {
        var expanded = "tiny-linux-root"u8.ToArray();
        return
        [
            new("bundle.json", "{}"u8.ToArray(), null, null, "manifest", null),
            new($"models/{Model}/package.json", "{}"u8.ToArray(), null, null, "manifest", null),
            new("images/linux-root.img.gz", Gzip(expanded), expanded, null, "linux-root", null),
            new("images/linux-boot.img", "tiny-linux-boot"u8.ToArray(), null, null, "linux-boot", null)
        ];
    }

    private static JsonObject PartNode(string name, byte[] data) =>
        new() { ["fileName"] = name, ["bytes"] = data.LongLength, ["sha256"] = Hash(data) };

    private static JsonObject FileNode(Case c, string path) => c.Descriptor["files"]!
        .AsArray().Select(n => n!.AsObject()).Single(n => n["path"]!.GetValue<string>() == path);

    private static string Part(Case c, int number) =>
        Path.Combine(c.Directory, $"{c.BaseName}.{number:000}");

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true)) gzip.Write(data);
        return output.ToArray();
    }

    private static void PatchFirstEntry(byte[] bytes, int signature, int offset, short value)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if (BitConverter.ToInt32(bytes, i) != signature) continue;
            BitConverter.TryWriteBytes(bytes.AsSpan(i + offset), value);
            return;
        }
        throw new InvalidDataException("ZIP header not found.");
    }

    private sealed record Case(string Directory, string BaseName, string Path, JsonObject Descriptor);
    private sealed record EntrySpec(
        string Path, byte[] Data, byte[]? Expanded, byte[]? Unused, string Role,
        Action<ZipArchiveEntry>? Configure);
}
