using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using AvbFixture = FreedomInstaller.Core.Tests.EdlRawAvbChainTests.AvbFixture;

namespace FreedomInstaller.Core.Tests;

public sealed partial class SystemUpdateBootToolTests : IClassFixture<EdlRawGblEnvelopeTests.SharedFixture>
{
    private const string EmbeddedTestKeyResource = "FreedomInstaller.AvbTestKey.pem";
    private const int AvbFooterSize = 64;
    private const int BootMagicSize = 8;
    private const int BootPageSize = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly EdlRawGblEnvelopeTests.SharedFixture _fx;

    public SystemUpdateBootToolTests(EdlRawGblEnvelopeTests.SharedFixture fx) => _fx = fx;

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task InspectSlot_ValidKshimSlot_WritesBaseAndReport(string slot)
    {
        using var scope = NewScope();
        EdlRawBuiltBoot built = BuiltFor(slot);
        string requestPath = Path.Combine(scope.Root, "inspect.json");
        string outputDir = Path.Combine(scope.Root, "out");
        WriteJson(requestPath, InspectRequest(slot, Claim(built.CandidateBoot.Path), slot));

        CliResult run = await RunToolAsync("inspect-slot", requestPath, outputDir);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.StdErr);

        string resultPath = Path.Combine(outputDir, "result.json");
        string basePath = Path.Combine(outputDir, "base.bin");
        Assert.True(File.Exists(resultPath));
        Assert.True(File.Exists(basePath));

        string fileJson = (await File.ReadAllTextAsync(resultPath)).TrimEnd('\r', '\n');
        Assert.Equal(fileJson, run.StdOut.TrimEnd('\r', '\n'));
        Assert.DoesNotContain('\n', run.StdOut.TrimEnd('\r', '\n'));

        byte[] baseBytes = await File.ReadAllBytesAsync(basePath);
        string baseSha = Convert.ToHexStringLower(SHA256.HashData(baseBytes));
        Assert.Equal(built.SlotMaterial.BaseImageLength, baseBytes.LongLength);
        Assert.Equal(built.SlotMaterial.BaseImageSha256, baseSha);

        using var doc = JsonDocument.Parse(fileJson);
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("inspect-slot", root.GetProperty("operation").GetString());
        Assert.Equal("slot_inspected", root.GetProperty("phase").GetString());
        Assert.Equal(slot, root.GetProperty("slot").GetString());
        Assert.False(root.TryGetProperty("signature", out _));
        Assert.False(root.TryGetProperty("signaturePerformed", out _));

        JsonElement baseImage = root.GetProperty("baseImage");
        Assert.Equal(basePath, baseImage.GetProperty("path").GetString());
        Assert.Equal(baseBytes.LongLength, baseImage.GetProperty("length").GetInt64());
        Assert.Equal(baseSha, baseImage.GetProperty("sha256").GetString());

        JsonElement material = root.GetProperty("material");
        Assert.Equal(slot, material.GetProperty("slot").GetString());
        Assert.True(material.GetProperty("isKshim").GetBoolean());
        Assert.Equal(built.SlotMaterial.BaseImageLength, material.GetProperty("baseImageLength").GetInt64());
        Assert.Equal(built.SlotMaterial.BaseImageSha256, material.GetProperty("baseImageSha256").GetString());
        Assert.False(material.GetProperty("signatureVerificationPerformed").GetBoolean());

        if (OperatingSystem.IsLinux())
        {
            UnixFileMode mode = File.GetUnixFileMode(outputDir);
            UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(expected, mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
        }
    }

    [Fact]
    public async Task RepackEnvelope_AospResign_PreparesAuthenticatedCandidate()
    {
        using var scope = NewScope();
        EdlRawBuiltBoot built = _fx.BudgetBuilt;
        using RSA rsa = LoadEmbeddedTestRsa();
        using var testAvb = new AvbFixture(rsa);

        string vbmetaPath = Path.Combine(scope.Root, "vbmeta_b.img");
        await File.WriteAllBytesAsync(
            vbmetaPath,
            testAvb.BuildRootPartition(
                testAvb.ChainFor("boot", 3, 0),
                testAvb.ChainFor("recovery", 1, 0),
                testAvb.ChainFor("vbmeta_system", 2, 0)));

        string rawPath = Path.Combine(scope.Root, "packed_b.raw");
        VerifiedEdlFile changedRaw = CreateChangedRawCopy(built.PackedRaw, rawPath);
        string requestPath = Path.Combine(scope.Root, "repack-aosp.json");
        string outputDir = Path.Combine(scope.Root, "out");
        WriteJson(
            requestPath,
            RepackRequest(
                "b",
                Claim(built.CandidateBoot.Path),
                "b",
                Claim(vbmetaPath),
                Claim(changedRaw),
                "aosp-resign",
                built.SlotMaterial.LoaderBudget.MaxRawBytes));

        CliResult run = await RunToolAsync("repack-envelope", requestPath, outputDir);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.StdErr);

        string candidatePath = Path.Combine(outputDir, "candidate.img");
        string resultPath = Path.Combine(outputDir, "result.json");
        Assert.True(File.Exists(candidatePath));
        Assert.True(File.Exists(resultPath));

        string fileJson = (await File.ReadAllTextAsync(resultPath)).TrimEnd('\r', '\n');
        Assert.Equal(fileJson, run.StdOut.TrimEnd('\r', '\n'));
        Assert.DoesNotContain('\n', run.StdOut.TrimEnd('\r', '\n'));

        byte[] candidateBytes = await File.ReadAllBytesAsync(candidatePath);
        string candidateSha = Convert.ToHexStringLower(SHA256.HashData(candidateBytes));
        Assert.Equal(built.CandidateBoot.Length, candidateBytes.LongLength);
        Assert.Equal(changedRaw.Sha256, ExtractGzipBootKernelSha256(candidateBytes));
        AvbFixture.AssertAuthenticatedSignedBlob(ExtractSignedAvbBlob(candidateBytes));

        using var doc = JsonDocument.Parse(fileJson);
        JsonElement root = doc.RootElement;
        Assert.Equal("envelope_prepared", root.GetProperty("phase").GetString());
        JsonElement env = root.GetProperty("envelope");
        Assert.Equal("AospResign", env.GetProperty("mode").GetString());
        Assert.True(env.GetProperty("payloadDigestMatchesSignedMetadata").GetBoolean());
        JsonElement candidate = env.GetProperty("candidate");
        Assert.Equal(candidatePath, candidate.GetProperty("path").GetString());
        Assert.Equal(candidateBytes.LongLength, candidate.GetProperty("length").GetInt64());
        Assert.Equal(candidateSha, candidate.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task RepackEnvelope_GblPreserveMetadata_PreservesAuthenticatedOemMetadata()
    {
        using var scope = NewScope();
        EdlRawBuiltBoot built = _fx.NormalBuilt;
        string sourceBootPath = Path.Combine(scope.Root, "source_boot_a.img");
        using IDisposable preservedSource = await InvokePreserveOriginalSignedBootMetadataAsync(
            built,
            _fx.NormalOriginal,
            sourceBootPath);

        string vbmetaPath = Path.Combine(scope.Root, "vbmeta_a.img");
        await File.WriteAllBytesAsync(
            vbmetaPath,
            _fx.DefaultOem.BuildRootPartition(
                _fx.DefaultOem.ChainFor("boot", 3, 0),
                _fx.DefaultOem.ChainFor("recovery", 1, 0),
                _fx.DefaultOem.ChainFor("vbmeta_system", 2, 0)));

        string rawPath = Path.Combine(scope.Root, "packed_a.raw");
        VerifiedEdlFile changedRaw = CreateChangedRawCopy(built.PackedRaw, rawPath);
        string requestPath = Path.Combine(scope.Root, "repack-gbl.json");
        string outputDir = Path.Combine(scope.Root, "out");
        WriteJson(
            requestPath,
            RepackRequest(
                "a",
                Claim(sourceBootPath),
                "a",
                Claim(vbmetaPath),
                Claim(changedRaw),
                "gbl-preserve-metadata",
                built.SlotMaterial.LoaderBudget.MaxRawBytes));

        CliResult run = await RunToolAsync("repack-envelope", requestPath, outputDir);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.StdErr);

        string candidatePath = Path.Combine(outputDir, "candidate.img");
        byte[] sourceBytes = await File.ReadAllBytesAsync(sourceBootPath);
        byte[] candidateBytes = await File.ReadAllBytesAsync(candidatePath);
        byte[] sourceSignedBlob = ExtractSignedAvbBlob(sourceBytes);
        byte[] candidateSignedBlob = ExtractSignedAvbBlob(candidateBytes);

        Assert.Equal(sourceSignedBlob, candidateSignedBlob);
        AvbFixture.AssertAuthenticatedSignedBlob(candidateSignedBlob);
        Assert.Equal(changedRaw.Sha256, ExtractGzipBootKernelSha256(candidateBytes));

        string resultPath = Path.Combine(outputDir, "result.json");
        Assert.True(File.Exists(resultPath));
        string fileJson = (await File.ReadAllTextAsync(resultPath)).TrimEnd('\r', '\n');
        Assert.Equal(fileJson, run.StdOut.TrimEnd('\r', '\n'));
        Assert.DoesNotContain('\n', run.StdOut.TrimEnd('\r', '\n'));

        using var doc = JsonDocument.Parse(fileJson);
        JsonElement env = doc.RootElement.GetProperty("envelope");
        Assert.Equal("GblPreserveMetadata", env.GetProperty("mode").GetString());
        Assert.False(env.GetProperty("payloadDigestMatchesSignedMetadata").GetBoolean());
    }

    [Theory]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-claim")]
    [InlineData("unknown-field")]
    [InlineData("numeric-slot")]
    [InlineData("numeric-path")]
    [InlineData("numeric-mode")]
    [InlineData("bad-hash")]
    [InlineData("length-bool")]
    [InlineData("schema-bool")]
    [InlineData("invalid-utf8")]
    [InlineData("oversize")]
    public async Task InvalidRequest_RejectedBeforeOutputCreation(string kind)
    {
        using var scope = NewScope();
        string requestPath = Path.Combine(scope.Root, $"invalid-{kind}.json");
        string outputDir = Path.Combine(scope.Root, $"out-{kind}");
        WriteInvalidRequest(requestPath, kind);

        CliResult run = await RunToolAsync(
            kind == "numeric-mode" ? "repack-envelope" : "inspect-slot",
            requestPath,
            outputDir);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(string.Empty, run.StdOut);
        AssertErrorJson(run.StdErr);
        Assert.False(Directory.Exists(outputDir));
    }

    [Fact]
    public async Task InspectSlot_CorruptClaimOrStockBoot_FailsCleanlyWithoutResultOrBase()
    {
        using var scope = NewScope();
        EdlRawBuiltBoot built = _fx.NormalBuilt;

        string corruptBootPath = Path.Combine(scope.Root, "corrupt_boot_a.img");
        byte[] bootBytes = await File.ReadAllBytesAsync(built.CandidateBoot.Path);
        bootBytes[BootPageSize + 17] ^= 0x5A;
        await File.WriteAllBytesAsync(corruptBootPath, bootBytes);
        byte[] snapshotBefore = (byte[])bootBytes.Clone();

        string badClaimRequest = Path.Combine(scope.Root, "bad-claim.json");
        string badClaimOut = Path.Combine(scope.Root, "bad-claim-out");
        WriteJson(
            badClaimRequest,
            InspectRequest(
                "a",
                new Dictionary<string, object?>
                {
                    ["path"] = corruptBootPath,
                    ["length"] = bootBytes.LongLength,
                    ["sha256"] = built.CandidateBoot.Sha256
                },
                "a"));

        CliResult badClaimRun = await RunToolAsync("inspect-slot", badClaimRequest, badClaimOut);
        Assert.Equal(1, badClaimRun.ExitCode);
        Assert.Equal(string.Empty, badClaimRun.StdOut);
        AssertErrorJson(badClaimRun.StdErr);
        Assert.False(File.Exists(Path.Combine(badClaimOut, "result.json")));
        Assert.False(File.Exists(Path.Combine(badClaimOut, "base.bin")));
        Assert.Equal(snapshotBefore, await File.ReadAllBytesAsync(corruptBootPath));

        string stockRequest = Path.Combine(scope.Root, "stock.json");
        string stockOut = Path.Combine(scope.Root, "stock-out");
        WriteJson(stockRequest, InspectRequest("a", Claim(_fx.NormalOriginal), "a"));

        CliResult stockRun = await RunToolAsync("inspect-slot", stockRequest, stockOut);
        Assert.Equal(1, stockRun.ExitCode);
        Assert.Equal(string.Empty, stockRun.StdOut);
        AssertErrorJson(stockRun.StdErr);
        Assert.False(File.Exists(Path.Combine(stockOut, "result.json")));
        Assert.False(File.Exists(Path.Combine(stockOut, "base.bin")));
    }

    [Fact]
    public async Task OutputValidation_ExistingTargetsAndSymlinks_FailWithoutTouchingTarget()
    {
        using var scope = NewScope();
        string requestPath = Path.Combine(scope.Root, "valid.json");
        WriteJson(requestPath, InspectRequest("a", Claim(_fx.NormalBuilt.CandidateBoot.Path), "a"));

        string existingDir = Path.Combine(scope.Root, "existing-dir");
        Directory.CreateDirectory(existingDir);
        string sentinelPath = Path.Combine(existingDir, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "keep");

        CliResult dirRun = await RunToolAsync("inspect-slot", requestPath, existingDir);
        Assert.Equal(1, dirRun.ExitCode);
        Assert.Equal(string.Empty, dirRun.StdOut);
        AssertErrorJson(dirRun.StdErr);
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinelPath));
        Assert.False(File.Exists(Path.Combine(existingDir, "result.json")));

        string existingFile = Path.Combine(scope.Root, "existing-file");
        await File.WriteAllTextAsync(existingFile, "file-sentinel");
        CliResult fileRun = await RunToolAsync("inspect-slot", requestPath, existingFile);
        Assert.Equal(1, fileRun.ExitCode);
        Assert.Equal("file-sentinel", await File.ReadAllTextAsync(existingFile));

        if (OperatingSystem.IsLinux())
        {
            string brokenTargetOutput = Path.Combine(scope.Root, "broken-link-out");
            File.CreateSymbolicLink(brokenTargetOutput, Path.Combine(scope.Root, "missing-target"));
            CliResult brokenRun = await RunToolAsync("inspect-slot", requestPath, brokenTargetOutput);
            Assert.Equal(1, brokenRun.ExitCode);
            Assert.NotNull(new FileInfo(brokenTargetOutput).LinkTarget);

            string realParent = Path.Combine(scope.Root, "real-parent");
            Directory.CreateDirectory(realParent);
            string realSentinel = Path.Combine(realParent, "external.txt");
            await File.WriteAllTextAsync(realSentinel, "untouched");
            string symlinkParent = Path.Combine(scope.Root, "symlink-parent");
            Directory.CreateSymbolicLink(symlinkParent, realParent);
            string throughSymlinkOut = Path.Combine(symlinkParent, "child-out");

            CliResult symlinkRun = await RunToolAsync("inspect-slot", requestPath, throughSymlinkOut);
            Assert.Equal(1, symlinkRun.ExitCode);
            Assert.Equal(string.Empty, symlinkRun.StdOut);
            AssertErrorJson(symlinkRun.StdErr);
            Assert.Equal("untouched", await File.ReadAllTextAsync(realSentinel));
            Assert.False(Directory.Exists(Path.Combine(realParent, "child-out")));
        }
    }

    [Fact]
    public async Task UsageErrors_ReturnExitCodeTwoAndJsonError()
    {
        using var scope = NewScope();
        string requestPath = Path.Combine(scope.Root, "req.json");
        string outputDir = Path.Combine(scope.Root, "out");

        CliResult badOp = await RunToolAsync("unknown-op", requestPath, outputDir);
        Assert.Equal(2, badOp.ExitCode);
        Assert.Equal(string.Empty, badOp.StdOut);
        AssertErrorJson(badOp.StdErr);
        Assert.False(Directory.Exists(outputDir));

        CliResult badCount = await RunToolAsync("inspect-slot", requestPath);
        Assert.Equal(2, badCount.ExitCode);
        Assert.Equal(string.Empty, badCount.StdOut);
        AssertErrorJson(badCount.StdErr);
    }

    private EdlRawBuiltBoot BuiltFor(string slot) => slot == "a" ? _fx.NormalBuilt : _fx.BudgetBuilt;

    private string SlotDir(string slot) => Path.Combine(_fx.Workspace.Root, $"slot-{slot}");

    private Dictionary<string, object?> InspectRequest(string slot, Dictionary<string, object?> bootClaim, string slotFilesFrom)
    {
        string dir = SlotDir(slotFilesFrom);
        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["slot"] = slot,
            ["files"] = new Dictionary<string, object?>
            {
                ["boot"] = bootClaim,
                ["vendorBoot"] = Claim(Path.Combine(dir, $"vendor-{slotFilesFrom}.img")),
                ["initBoot"] = Claim(Path.Combine(dir, $"init-{slotFilesFrom}.img")),
                ["abl"] = Claim(Path.Combine(dir, $"abl-{slotFilesFrom}.bin")),
                ["xblConfig"] = Claim(Path.Combine(dir, $"xbl-{slotFilesFrom}.bin"))
            }
        };
    }

    private Dictionary<string, object?> RepackRequest(
        string slot,
        Dictionary<string, object?> bootClaim,
        string slotFilesFrom,
        Dictionary<string, object?> vbmetaClaim,
        Dictionary<string, object?> packedRawClaim,
        object mode,
        long maxRawBytes)
    {
        string dir = SlotDir(slotFilesFrom);
        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["slot"] = slot,
            ["mode"] = mode,
            ["maxRawBytes"] = maxRawBytes,
            ["files"] = new Dictionary<string, object?>
            {
                ["boot"] = bootClaim,
                ["vendorBoot"] = Claim(Path.Combine(dir, $"vendor-{slotFilesFrom}.img")),
                ["initBoot"] = Claim(Path.Combine(dir, $"init-{slotFilesFrom}.img")),
                ["abl"] = Claim(Path.Combine(dir, $"abl-{slotFilesFrom}.bin")),
                ["xblConfig"] = Claim(Path.Combine(dir, $"xbl-{slotFilesFrom}.bin")),
                ["vbmeta"] = vbmetaClaim,
                ["packedRaw"] = packedRawClaim
            }
        };
    }

    private void WriteInvalidRequest(string path, string kind)
    {
        Dictionary<string, object?> valid = InspectRequest("a", Claim(_fx.NormalBuilt.CandidateBoot.Path), "a");
        string validJson = JsonSerializer.Serialize(valid, JsonOptions);
        switch (kind)
        {
            case "duplicate-root":
                File.WriteAllText(path, "{\"schemaVersion\":1,\"schemaVersion\":1,\"slot\":\"a\",\"files\":{}}");
                break;
            case "duplicate-claim":
                string bootPath = JsonSerializer.Serialize(_fx.NormalBuilt.CandidateBoot.Path);
                string filesJson = JsonSerializer.Serialize(valid["files"], JsonOptions);
                string dupClaim = $"{{\"path\":{bootPath},\"path\":{bootPath},\"length\":1,\"sha256\":\"{new string('a', 64)}\"}}";
                File.WriteAllText(path, $"{{\"schemaVersion\":1,\"slot\":\"a\",\"files\":{filesJson.Replace(JsonSerializer.Serialize(Claim(_fx.NormalBuilt.CandidateBoot.Path), JsonOptions), dupClaim, StringComparison.Ordinal)}}}");
                break;
            case "unknown-field":
                valid["extra"] = 1;
                WriteJson(path, valid);
                break;
            case "numeric-slot":
                valid["slot"] = 1;
                WriteJson(path, valid);
                break;
            case "numeric-path":
                ((Dictionary<string, object?>)((Dictionary<string, object?>)valid["files"]!)["boot"]!)["path"] = 123;
                WriteJson(path, valid);
                break;
            case "numeric-mode":
                Dictionary<string, object?> repack = RepackRequest(
                    "a",
                    Claim(_fx.NormalBuilt.CandidateBoot.Path),
                    "a",
                    Claim(_fx.NormalBuilt.CandidateBoot.Path),
                    Claim(_fx.NormalBuilt.PackedRaw),
                    123,
                    _fx.NormalBuilt.SlotMaterial.LoaderBudget.MaxRawBytes);
                WriteJson(path, repack);
                break;
            case "bad-hash":
                ((Dictionary<string, object?>)((Dictionary<string, object?>)valid["files"]!)["boot"]!)["sha256"] = "xyz";
                WriteJson(path, valid);
                break;
            case "length-bool":
                ((Dictionary<string, object?>)((Dictionary<string, object?>)valid["files"]!)["boot"]!)["length"] = true;
                WriteJson(path, valid);
                break;
            case "schema-bool":
                valid["schemaVersion"] = true;
                WriteJson(path, valid);
                break;
            case "invalid-utf8":
                File.WriteAllBytes(path, [0x7B, 0xFF, 0xFE, 0x7D]);
                break;
            case "oversize":
                File.WriteAllText(path, validJson[..^1] + new string(' ', 70000) + "}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static Dictionary<string, object?> Claim(VerifiedEdlFile file) => new()
    {
        ["path"] = file.Path,
        ["length"] = file.Length,
        ["sha256"] = file.Sha256
    };

    private static Dictionary<string, object?> Claim(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new Dictionary<string, object?>
        {
            ["path"] = Path.GetFullPath(path),
            ["length"] = bytes.LongLength,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));

    private static VerifiedEdlFile CreateChangedRawCopy(VerifiedEdlFile sourceRaw, string destinationPath)
    {
        byte[] raw = File.ReadAllBytes(sourceRaw.Path);
        ulong manifestOffset = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(32, 8));
        int flagOffset = checked((int)manifestOffset + 12);
        uint defaultWord = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(flagOffset, sizeof(uint)));
        Assert.NotEqual(0u, defaultWord);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(flagOffset, sizeof(uint)), 0);
        File.WriteAllBytes(destinationPath, raw);
        return new VerifiedEdlFile(
            Path.GetFullPath(destinationPath),
            raw.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(raw)));
    }

    private static async Task<IDisposable> InvokePreserveOriginalSignedBootMetadataAsync(
        EdlRawBuiltBoot built,
        VerifiedEdlFile originalBoot,
        string candidatePath)
    {
        MethodInfo? method = typeof(EdlRawBootBuilder).GetMethod(
            "PreserveOriginalSignedBootMetadataAsync",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        object? taskObj = method!.Invoke(null, new object[] { built, originalBoot, candidatePath, CancellationToken.None });
        Assert.NotNull(taskObj);
        var task = Assert.IsAssignableFrom<Task>(taskObj);
        await task.ConfigureAwait(false);

        PropertyInfo? resultProp = taskObj!.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(resultProp);
        object? result = resultProp!.GetValue(taskObj);
        Assert.NotNull(result);
        Assert.True(File.Exists(candidatePath));

        return Assert.IsAssignableFrom<IDisposable>(result);
    }

    private static byte[] ExtractSignedAvbBlob(byte[] bootPartition)
    {
        ReadOnlySpan<byte> footer = bootPartition.AsSpan(bootPartition.Length - AvbFooterSize, AvbFooterSize);
        Assert.Equal("AVBf", Encoding.ASCII.GetString(footer[..4]));
        ulong vbmetaOffset = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(20, 8));
        ulong vbmetaSize = BinaryPrimitives.ReadUInt64BigEndian(footer.Slice(28, 8));
        return bootPartition.AsSpan(checked((int)vbmetaOffset), checked((int)vbmetaSize)).ToArray();
    }

    private static string ExtractGzipBootKernelSha256(byte[] bootPartition)
    {
        Assert.Equal("ANDROID!", Encoding.ASCII.GetString(bootPartition, 0, BootMagicSize));
        uint kernelSize = BinaryPrimitives.ReadUInt32LittleEndian(bootPartition.AsSpan(8, 4));
        using var compressed = new MemoryStream(bootPartition, BootPageSize, checked((int)kernelSize), writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        gzip.CopyTo(raw);
        return Convert.ToHexStringLower(SHA256.HashData(raw.ToArray()));
    }

    private static RSA LoadEmbeddedTestRsa()
    {
        using Stream? stream = typeof(EdlRawBootBuilder).Assembly.GetManifestResourceStream(EmbeddedTestKeyResource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        RSA rsa = RSA.Create();
        rsa.ImportFromPem(reader.ReadToEnd());
        return rsa;
    }

    private static void AssertErrorJson(string stderr)
    {
        Assert.False(string.IsNullOrWhiteSpace(stderr));
        using var doc = JsonDocument.Parse(stderr.Trim());
        string? message = doc.RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    private static string LocateBootToolDll()
    {
        string? env = Environment.GetEnvironmentVariable("BOOT_TOOL_DLL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string fullEnv = Path.GetFullPath(env);
            Assert.True(File.Exists(fullEnv), $"BOOT_TOOL_DLL does not exist: {fullEnv}");
            return fullEnv;
        }

        string configuration = typeof(SystemUpdateBootToolTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } cfg
            ? cfg
            : "Debug";

        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            current = current.Parent;

        Assert.NotNull(current);
        string dllPath = Path.Combine(
            current!.FullName,
            "src",
            "FreedomInstaller.BootTool",
            "bin",
            configuration,
            "net10.0",
            "armada-boot-tool.dll");
        Assert.True(File.Exists(dllPath), $"Expected built BootTool DLL at: {dllPath}");
        return dllPath;
    }

    private static async Task<CliResult> RunToolAsync(params string[] args)
    {
        string dllPath = LocateBootToolDll();
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(dllPath);
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        Assert.True(process.Start());

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(); } catch { }
            Assert.Fail("armada-boot-tool process timed out after 90 seconds.");
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private TestScope NewScope() => new(_fx.Workspace.Root);

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);

    private sealed class TestScope : IDisposable
    {
        public string Root { get; }

        public TestScope(string workspaceRoot)
        {
            Root = Path.Combine(workspaceRoot, "boot-tool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
