using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using AvbFixture = FreedomInstaller.Core.Tests.EdlRawAvbChainTests.AvbFixture;

namespace FreedomInstaller.Core.Tests;

public sealed partial class SystemUpdateBootToolTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task BuildRaw_RealTools_ChainsInspectBuildAndAuthenticatedRepack(string slot)
    {
        using var scope = NewScope();
        EdlRawBuiltBoot built = BuiltFor(slot);
        string bootHashBefore = await HashFileAsync(built.CandidateBoot.Path);
        string kernelHashBefore = _fx.Package.Contract.Artifacts["kernel"].Sha256;
        string inspectRequest = Path.Combine(scope.Root, "inspect.json");
        string inspectOut = Path.Combine(scope.Root, "inspect-out");
        WriteJson(inspectRequest, InspectRequest(slot, Claim(built.CandidateBoot), slot));

        CliResult inspected = await RunToolAsync("inspect-slot", inspectRequest, inspectOut);
        Assert.Equal(0, inspected.ExitCode);
        Assert.Equal(string.Empty, inspected.StdErr);
        string basePath = Path.Combine(inspectOut, "base.bin");
        Dictionary<string, object?> baseClaim = Claim(basePath);
        using (JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(inspectOut, "result.json"))))
        {
            JsonElement report = doc.RootElement;
            Assert.Equal("slot_inspected", report.GetProperty("phase").GetString());
            Assert.Equal(slot, report.GetProperty("slot").GetString());
            Assert.Equal((string)baseClaim["sha256"]!, report.GetProperty("baseImage").GetProperty("sha256").GetString(), StringComparer.OrdinalIgnoreCase);
        }
        string baseHashBefore = (string)baseClaim["sha256"]!;

        string rawRequest = Path.Combine(scope.Root, "raw.json");
        string rawOut = Path.Combine(scope.Root, "raw-out");
        WriteJson(rawRequest, RawRequest(slot, baseClaim, built.SlotMaterial.LoaderBudget.MaxRawBytes));
        CliResult rawRun = await RunToolAsync("build-raw", rawRequest, rawOut);
        Assert.Equal(0, rawRun.ExitCode);
        Assert.Equal(string.Empty, rawRun.StdErr);
        string rawResultPath = Path.Combine(rawOut, "result.json");
        string packedRawPath = Path.Combine(rawOut, "PackedKernel");
        Assert.True(File.Exists(packedRawPath));
        string rawJson = (await File.ReadAllTextAsync(rawResultPath)).Trim();
        Assert.Equal(rawJson, rawRun.StdOut.Trim());
        Assert.DoesNotContain('\n', rawRun.StdOut.Trim());
        Dictionary<string, object?> rawClaim = Claim(packedRawPath);
        using (JsonDocument doc = JsonDocument.Parse(rawJson))
        {
            JsonElement report = doc.RootElement;
            Assert.Equal("raw_prepared", report.GetProperty("phase").GetString());
            Assert.Equal(slot, report.GetProperty("slot").GetString());
            JsonElement raw = report.GetProperty("raw");
            Assert.Equal("TB322FC", raw.GetProperty("model").GetString());
            JsonElement packed = raw.GetProperty("packedRaw");
            Assert.Equal((long)rawClaim["length"]!, packed.GetProperty("length").GetInt64());
            Assert.Equal((string)rawClaim["sha256"]!, packed.GetProperty("sha256").GetString(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal("Normal", raw.GetProperty("normalDtb").GetProperty("kind").GetString());
            Assert.Equal("Diagnostic", raw.GetProperty("diagnosticDtb").GetProperty("kind").GetString());
            Assert.Equal("TB322FC", raw.GetProperty("normalDtb").GetProperty("deviceModel").GetString());
            Assert.Equal("TB322FC", raw.GetProperty("diagnosticDtb").GetProperty("deviceModel").GetString());
        }
        Assert.Equal(bootHashBefore, await HashFileAsync(built.CandidateBoot.Path));
        Assert.Equal(baseHashBefore, await HashFileAsync(basePath));
        Assert.Equal(kernelHashBefore, await HashFileAsync(_fx.Package.Contract.Artifacts["kernel"].FullPath), StringComparer.OrdinalIgnoreCase);

        using RSA rsa = LoadEmbeddedTestRsa();
        using var avb = new AvbFixture(rsa);
        string vbmetaPath = Path.Combine(scope.Root, "vbmeta.img");
        await File.WriteAllBytesAsync(vbmetaPath, avb.BuildRootPartition(
            avb.ChainFor("boot", 3, 0), avb.ChainFor("recovery", 1, 0), avb.ChainFor("vbmeta_system", 2, 0)));
        string repackRequest = Path.Combine(scope.Root, "repack.json");
        string repackOut = Path.Combine(scope.Root, "repack-out");
        WriteJson(repackRequest, RepackRequest(slot, Claim(built.CandidateBoot), slot,
            Claim(vbmetaPath), rawClaim, "aosp-resign", built.SlotMaterial.LoaderBudget.MaxRawBytes));
        CliResult repacked = await RunToolAsync("repack-envelope", repackRequest, repackOut);
        Assert.Equal(0, repacked.ExitCode);
        Assert.Equal(string.Empty, repacked.StdErr);
        string candidatePath = Path.Combine(repackOut, "candidate.img");
        byte[] candidate = await File.ReadAllBytesAsync(candidatePath);
        Assert.Equal(built.CandidateBoot.Length, candidate.LongLength);
        Assert.Equal((string)rawClaim["sha256"]!, ExtractGzipBootKernelSha256(candidate), StringComparer.OrdinalIgnoreCase);
        AvbFixture.AssertAuthenticatedSignedBlob(ExtractSignedAvbBlob(candidate));
        using JsonDocument envelope = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repackOut, "result.json")));
        Assert.Equal("envelope_prepared", envelope.RootElement.GetProperty("phase").GetString());
        Assert.Equal(candidate.LongLength, envelope.RootElement.GetProperty("envelope").GetProperty("candidate").GetProperty("length").GetInt64());
    }

    [Theory]
    [InlineData("duplicate-root")]
    [InlineData("unknown-root")]
    [InlineData("unknown-files")]
    [InlineData("numeric-model")]
    [InlineData("wrong-slot")]
    [InlineData("zero-budget")]
    [InlineData("over-budget")]
    [InlineData("nonstring-bootargs")]
    [InlineData("bad-sha")]
    [InlineData("wrong-sha")]
    public async Task BuildRaw_InvalidRequest_FailsWithoutOutput(string kind)
    {
        using var scope = NewScope();
        Dictionary<string, object?> request = RawRequest("a", Claim(_fx.NormalBuilt.BaseImage),
            _fx.NormalBuilt.SlotMaterial.LoaderBudget.MaxRawBytes);
        string json = JsonSerializer.Serialize(request, JsonOptions);
        var files = (Dictionary<string, object?>)request["files"]!;
        switch (kind)
        {
            case "duplicate-root":
                json = json.Replace("{\"schemaVersion\":1,", "{\"schemaVersion\":1,\"schemaVersion\":1,", StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(scope.Root, "request.json"), json);
                break;
            case "unknown-root":
                request["packerPath"] = "/usr/libexec/armada/kshim-tools/MultiBootKernelPatcher";
                WriteJson(Path.Combine(scope.Root, "request.json"), request);
                break;
            case "unknown-files":
                files["packer"] = Claim(_fx.NormalBuilt.PackedRaw);
                WriteJson(Path.Combine(scope.Root, "request.json"), request);
                break;
            case "numeric-model": request["model"] = 322; WriteJson(Path.Combine(scope.Root, "request.json"), request); break;
            case "wrong-slot": request["slot"] = "c"; WriteJson(Path.Combine(scope.Root, "request.json"), request); break;
            case "zero-budget": request["maxRawBytes"] = 0; WriteJson(Path.Combine(scope.Root, "request.json"), request); break;
            case "over-budget": request["maxRawBytes"] = 268435457L; WriteJson(Path.Combine(scope.Root, "request.json"), request); break;
            case "nonstring-bootargs": request["normalBootArgs"] = 1; WriteJson(Path.Combine(scope.Root, "request.json"), request); break;
            case "bad-sha":
                ((Dictionary<string, object?>)files["kernel"]!)["sha256"] = "not-a-sha";
                WriteJson(Path.Combine(scope.Root, "request.json"), request);
                break;
            case "wrong-sha":
                ((Dictionary<string, object?>)files["kernel"]!)["sha256"] = new string('0', 64);
                WriteJson(Path.Combine(scope.Root, "request.json"), request);
                break;
        }
        string output = Path.Combine(scope.Root, "out");
        CliResult run = await RunToolAsync("build-raw", Path.Combine(scope.Root, "request.json"), output);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(string.Empty, run.StdOut);
        AssertErrorJson(run.StdErr);
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(Path.Combine(output, "result.json")));
    }

    [Fact]
    public async Task BuildRaw_ModelDtbMismatch_FailsAndPreservesExistingOutput()
    {
        using var scope = NewScope();
        Dictionary<string, object?> request = RawRequest("a", Claim(_fx.NormalBuilt.BaseImage),
            _fx.NormalBuilt.SlotMaterial.LoaderBudget.MaxRawBytes);
        request["model"] = "TB323FU";
        string requestPath = Path.Combine(scope.Root, "request.json");
        WriteJson(requestPath, request);
        string missingOut = Path.Combine(scope.Root, "missing-out");
        CliResult mismatch = await RunToolAsync("build-raw", requestPath, missingOut);
        Assert.Equal(1, mismatch.ExitCode);
        Assert.Equal(string.Empty, mismatch.StdOut);
        AssertErrorJson(mismatch.StdErr);
        Assert.Contains("compatible", mismatch.StdErr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(missingOut));
        Assert.False(File.Exists(Path.Combine(missingOut, "result.json")));
        string existingOut = Path.Combine(scope.Root, "existing-out");
        Directory.CreateDirectory(existingOut);
        string sentinel = Path.Combine(existingOut, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        CliResult existing = await RunToolAsync("build-raw", requestPath, existingOut);
        Assert.Equal(1, existing.ExitCode);
        AssertErrorJson(existing.StdErr);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(existingOut, "result.json")));
    }

    private Dictionary<string, object?> RawRequest(string slot, Dictionary<string, object?> baseImage, long budget) => new()
    {
        ["schemaVersion"] = 1, ["slot"] = slot, ["model"] = "TB322FC",
        ["normalBootArgs"] = _fx.Package.Contract.BootRecipe.NormalBootArgs,
        ["diagnosticBootArgs"] = _fx.Package.Contract.BootRecipe.DiagnosticBootArgs,
        ["maxRawBytes"] = budget,
        ["files"] = new Dictionary<string, object?>
        {
            ["baseImage"] = baseImage,
            ["kernel"] = RawArtifactClaim("kernel"), ["initramfs"] = RawArtifactClaim("initramfs"),
            ["runtime"] = RawArtifactClaim("runtime"), ["dtbNormal"] = RawArtifactClaim("dtb-normal"),
            ["dtbDiagnostic"] = RawArtifactClaim("dtb-diagnostic")
        }
    };

    private Dictionary<string, object?> RawArtifactClaim(string name)
    {
        var artifact = _fx.Package.Contract.Artifacts[name];
        return new Dictionary<string, object?>
        {
            ["path"] = artifact.FullPath, ["length"] = artifact.Bytes, ["sha256"] = artifact.Sha256
        };
    }

    private static async Task<string> HashFileAsync(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
}
