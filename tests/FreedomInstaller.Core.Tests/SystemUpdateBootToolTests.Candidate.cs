using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvbFixture = FreedomInstaller.Core.Tests.EdlRawAvbChainTests.AvbFixture;

namespace FreedomInstaller.Core.Tests;

public sealed partial class SystemUpdateBootToolTests
{
    private const string CandidateRunnerScript = """
        import json
        import sys

        sys.path.insert(0, sys.argv[1] + "/system_files/usr/lib/armada")
        with open(sys.argv[2], "r", encoding="utf-8") as handle:
            kwargs = json.load(handle)
        from updates.boot_candidate import prepare_boot_candidate
        print(json.dumps(prepare_boot_candidate(**kwargs)))
        """;

    private static string RequireSystemSourceRoot()
    {
        string? root = Environment.GetEnvironmentVariable("ARMADA_SYSTEM_SOURCE_PATH");
        Assert.False(string.IsNullOrWhiteSpace(root),
            "ARMADA_SYSTEM_SOURCE_PATH must be set to the mounted system source tree");
        string script = Path.Combine(root!, "system_files", "usr", "lib", "armada", "updates", "boot_candidate.py");
        Assert.True(File.Exists(script), $"missing boot_candidate.py at {script}");
        return root!;
    }

    private static string RequireBootToolAppHost()
    {
        string appHost = Path.Combine(Path.GetDirectoryName(LocateBootToolDll())!, "armada-boot-tool");
        Assert.True(File.Exists(appHost), $"missing apphost at {appHost}");
        return appHost;
    }

    private static async Task<CliResult> RunPythonCandidateAsync(string scriptPath, string sourceRoot, string requestPath)
    {
        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(sourceRoot);
        psi.ArgumentList.Add(requestPath);
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        using var process = new Process { StartInfo = psi };
        Assert.True(process.Start());

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
            Assert.Fail("boot_candidate python process timed out after 120 seconds.");
        }

        return new CliResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private sealed record CandidateSetup(
        string RequestPath,
        string OutputDir,
        string TargetDir,
        string BootSourcePath,
        long BootSourceLength,
        string BootSourceSha256,
        IReadOnlyDictionary<string, string> SourceHashes,
        IDisposable? PreserveLease,
        Dictionary<string, object?> Request);

    private static Dictionary<string, object?> CandidateClaim(string path)
    {
        Dictionary<string, object?> claim = Claim(path);
        return new Dictionary<string, object?>
        {
            ["path"] = claim["path"],
            ["bytes"] = claim["length"],
            ["sha256"] = claim["sha256"]
        };
    }

    private static Dictionary<string, object?> CopyAndClaim(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: false);
        return CandidateClaim(destinationPath);
    }

    private Dictionary<string, object?> SlotFileMap(TestScope scope, string slot)
    {
        string dir = SlotDir(slot);
        return new Dictionary<string, object?>
        {
            ["vendorBoot"] = CopyAndClaim(Path.Combine(dir, $"vendor-{slot}.img"), Path.Combine(scope.Root, "slot", $"vendor-{slot}.img")),
            ["initBoot"] = CopyAndClaim(Path.Combine(dir, $"init-{slot}.img"), Path.Combine(scope.Root, "slot", $"init-{slot}.img")),
            ["abl"] = CopyAndClaim(Path.Combine(dir, $"abl-{slot}.bin"), Path.Combine(scope.Root, "slot", $"abl-{slot}.bin")),
            ["xblConfig"] = CopyAndClaim(Path.Combine(dir, $"xbl-{slot}.bin"), Path.Combine(scope.Root, "slot", $"xbl-{slot}.bin"))
        };
    }

    private static byte[] BuildCandidateVbmeta(AvbFixture fixture)
    {
        return fixture.BuildRootPartition(
            fixture.ChainFor("boot", 3, 0),
            fixture.ChainFor("recovery", 1, 0),
            fixture.ChainFor("vbmeta_system", 2, 0));
    }

    private async Task<CandidateSetup> SetupCandidateAsync(TestScope scope, string slot, string mode)
    {
        string bootTool = RequireBootToolAppHost();

        string targetDir = Path.Combine(scope.Root, "target");
        Directory.CreateDirectory(targetDir);
        string outputDir = Path.Combine(scope.Root, "out");

        bool gbl = mode == "gbl-preserve-metadata";
        var built = BuiltFor(slot);
        var original = slot == "a" ? _fx.NormalOriginal : _fx.BudgetOriginal;
        var originalBoot = new VerifiedEdlFile(
            Path.GetFullPath(original.Path), original.Length, original.Sha256);

        string bootPath = Path.Combine(scope.Root, "slot", $"boot-{slot}.img");
        IDisposable? lease = null;
        if (gbl)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bootPath)!);
            lease = await InvokePreserveOriginalSignedBootMetadataAsync(built, originalBoot, bootPath)
                .ConfigureAwait(false);
        }
        else
        {
            _ = CopyAndClaim(built.CandidateBoot.Path, bootPath);
        }

        try
        {
            var slotFiles = SlotFileMap(scope, slot);
            Dictionary<string, object?> bootClaim = CandidateClaim(bootPath);
            slotFiles["boot"] = bootClaim;

            string vbmetaPath = Path.Combine(scope.Root, "slot", $"vbmeta-{slot}.img");
            byte[] vbmetaBytes;
            if (gbl)
            {
                vbmetaBytes = BuildCandidateVbmeta(_fx.DefaultOem);
            }
            else
            {
                using RSA rsa = LoadEmbeddedTestRsa();
                using var fixture = new AvbFixture(rsa);
                vbmetaBytes = BuildCandidateVbmeta(fixture);
            }
            File.WriteAllBytes(vbmetaPath, vbmetaBytes);
            slotFiles["vbmeta"] = CandidateClaim(vbmetaPath);

            var artifacts = new Dictionary<string, object?>();
            foreach ((string outputKey, string sourceKey, string fileName) in new[]
                     {
                         ("kernel", "kernel", "kernel"),
                         ("initramfs", "initramfs", "initramfs.cpio"),
                         ("runtime", "runtime", "runtime.bin"),
                         ("dtbNormal", "dtb-normal", "dtb-normal.dtb"),
                         ("dtbDiagnostic", "dtb-diagnostic", "dtb-diagnostic.dtb")
                     })
            {
                var artifact = _fx.Package.Contract.Artifacts[sourceKey];
                artifacts[outputKey] = CopyAndClaim(artifact.FullPath, Path.Combine(scope.Root, "artifacts", fileName));
            }

            var materials = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["phase"] = "materials_prepared",
                ["model"] = "TB322FC",
                ["deployment"] = "armada/" + new string('a', 64) + ".1",
                ["kernelRelease"] = "fixture",
                ["rootUuid"] = "11111111-2222-3333-4444-555555555555",
                ["bootUuid"] = "66666666-7777-8888-9999-aaaaaaaaaaaa",
                ["target"] = Path.GetFullPath(targetDir),
                ["normalBootArgs"] = _fx.Package.Contract.BootRecipe.NormalBootArgs,
                ["diagnosticBootArgs"] = _fx.Package.Contract.BootRecipe.DiagnosticBootArgs,
                ["artifacts"] = artifacts
            };

            var request = new Dictionary<string, object?>
            {
                ["slot"] = slot,
                ["slot_files"] = slotFiles,
                ["materials"] = materials,
                ["mode"] = mode,
                ["max_raw_bytes"] = built.SlotMaterial.LoaderBudget.MaxRawBytes,
                ["output_dir"] = Path.GetFullPath(outputDir),
                ["boot_tool_path"] = bootTool
            };

            string requestPath = Path.Combine(scope.Root, "candidate-request.json");
            WriteJson(requestPath, request);

            var sourceHashes = new Dictionary<string, string>();
            foreach (string path in slotFiles.Values
                         .Concat(artifacts.Values)
                         .OfType<Dictionary<string, object?>>()
                         .Select(c => (string)c["path"]!))
            {
                sourceHashes[path] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            }

            return new CandidateSetup(
                requestPath,
                outputDir,
                targetDir,
                bootPath,
                new FileInfo(bootPath).Length,
                (string)bootClaim["sha256"]!,
                sourceHashes,
                lease,
                request);
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    [Theory]
    [InlineData("a", "aosp-resign")]
    [InlineData("b", "aosp-resign")]
    [InlineData("a", "gbl-preserve-metadata")]
    public async Task PrepareBootCandidateThroughPythonOrchestratorProducesVerifiedCandidate(string slot, string mode)
    {
        string sourceRoot = RequireSystemSourceRoot();
        using var scope = NewScope();
        CandidateSetup candidate = await SetupCandidateAsync(scope, slot, mode);
        using var lease = candidate.PreserveLease;

        string runnerPath = Path.Combine(scope.Root, "runner.py");
        File.WriteAllText(runnerPath, CandidateRunnerScript);

        CliResult result = await RunPythonCandidateAsync(runnerPath, sourceRoot, candidate.RequestPath);
        Assert.True(result.ExitCode == 0, $"candidate exit {result.ExitCode}: {result.StdErr}");
        Assert.Equal(string.Empty, result.StdErr);

        using JsonDocument stdoutDoc = JsonDocument.Parse(result.StdOut);
        string candidateJsonPath = Path.Combine(candidate.OutputDir, "candidate.json");
        Assert.True(File.Exists(candidateJsonPath));
        using JsonDocument fileDoc = JsonDocument.Parse(File.ReadAllText(candidateJsonPath));
        Assert.True(JsonElement.DeepEquals(stdoutDoc.RootElement, fileDoc.RootElement));

        JsonElement report = fileDoc.RootElement;
        Assert.Equal(1, report.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("candidate_prepared", report.GetProperty("phase").GetString());
        Assert.Equal(slot, report.GetProperty("slot").GetString());
        Assert.Equal("TB322FC", report.GetProperty("model").GetString());
        Assert.Equal((string)((Dictionary<string, object?>)candidate.Request["materials"]!)["deployment"],
            report.GetProperty("deployment").GetString());
        Assert.Equal(mode, report.GetProperty("mode").GetString());
        Assert.Equal((long)candidate.Request["max_raw_bytes"]!, report.GetProperty("maxRawBytes").GetInt64());
        Assert.Equal(candidate.BootSourceSha256, report.GetProperty("sourceBootSha256").GetString(), ignoreCase: true);

        JsonElement candidateClaim = report.GetProperty("candidate");
        Assert.Equal(candidate.BootSourceLength, candidateClaim.GetProperty("bytes").GetInt64());

        string candidatePath = candidateClaim.GetProperty("path").GetString()!;
        Assert.True(File.Exists(candidatePath));
        byte[] candidateBytes = File.ReadAllBytes(candidatePath);
        Assert.Equal(candidate.BootSourceLength, candidateBytes.LongLength);

        string actualCandidateSha = await HashFileAsync(candidatePath).ConfigureAwait(false);
        Assert.Equal(actualCandidateSha, candidateClaim.GetProperty("sha256").GetString(), ignoreCase: true);


        string packedPath = Path.Combine(candidate.OutputDir, "build-raw", "PackedKernel");
        Assert.True(File.Exists(packedPath));
        Assert.Equal(await HashFileAsync(packedPath).ConfigureAwait(false),
            ExtractGzipBootKernelSha256(candidateBytes), ignoreCase: true);

        AvbFixture.AssertAuthenticatedSignedBlob(ExtractSignedAvbBlob(candidateBytes));

        bool digestMatches = report.GetProperty("payloadDigestMatchesSignedMetadata").GetBoolean();
        if (mode == "gbl-preserve-metadata")
        {
            Assert.False(digestMatches);
            var original = slot == "a" ? _fx.NormalOriginal : _fx.BudgetOriginal;
            Assert.Equal(
                ExtractSignedAvbBlob(File.ReadAllBytes(original.Path)),
                ExtractSignedAvbBlob(candidateBytes));
        }
        else
        {
            Assert.True(digestMatches);
        }

        foreach ((string operation, string phase) in new[]
                 {
                     ("inspect-slot", "slot_inspected"),
                     ("build-raw", "raw_prepared"),
                     ("repack-envelope", "envelope_prepared")
                 })
        {
            string stepResult = Path.Combine(candidate.OutputDir, operation, "result.json");
            Assert.True(File.Exists(stepResult), $"missing {stepResult}");
            using JsonDocument step = JsonDocument.Parse(File.ReadAllText(stepResult));
            JsonElement root = step.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(operation, root.GetProperty("operation").GetString());
            Assert.Equal(phase, root.GetProperty("phase").GetString());
            Assert.Equal(slot, root.GetProperty("slot").GetString());
        }

        foreach ((string path, string expectedSha) in candidate.SourceHashes)
        {
            Assert.Equal(expectedSha,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
    }

    [Theory]
    [InlineData("bad-source-hash")]
    [InlineData("excessive-budget")]
    [InlineData("wrong-model")]
    public async Task PrepareBootCandidateRejectsInvalidInputs(string failure)
    {
        string sourceRoot = RequireSystemSourceRoot();
        using var scope = NewScope();
        CandidateSetup setup = await SetupCandidateAsync(scope, "a", "aosp-resign");
        using var lease = setup.PreserveLease;

        string runnerPath = Path.Combine(scope.Root, "runner.py");
        File.WriteAllText(runnerPath, CandidateRunnerScript);

        if (failure == "excessive-budget")
        {
            long excessive = checked(BuiltFor("a").SlotMaterial.LoaderBudget.MaxRawBytes + 4096);
            Assert.True(excessive < 256L * 1024 * 1024);
            setup.Request["max_raw_bytes"] = excessive;
            WriteJson(setup.RequestPath, setup.Request);
        }
        else if (failure == "bad-source-hash")
        {
            var slotFiles = (Dictionary<string, object?>)setup.Request["slot_files"]!;
            var boot = (Dictionary<string, object?>)slotFiles["boot"]!;
            boot["sha256"] = new string('0', 64);
            WriteJson(setup.RequestPath, setup.Request);
        }
        else
        {
            var materials = (Dictionary<string, object?>)setup.Request["materials"]!;
            materials["model"] = "TB323FU";
            WriteJson(setup.RequestPath, setup.Request);
        }

        CliResult result = await RunPythonCandidateAsync(runnerPath, sourceRoot, setup.RequestPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StdOut);

        switch (failure)
        {
            case "bad-source-hash":
                Assert.Contains("content mismatch", result.StdErr);
                Assert.False(Directory.Exists(setup.OutputDir));
                break;
            case "excessive-budget":
                Assert.Contains("exceeds loader budget", result.StdErr);
                Assert.True(File.Exists(Path.Combine(setup.OutputDir, "inspect-slot", "result.json")));
                Assert.False(Directory.Exists(Path.Combine(setup.OutputDir, "build-raw")));
                break;
            case "wrong-model":
                Assert.True(result.StdErr.Contains("compatible", StringComparison.Ordinal), result.StdErr);
                Assert.True(File.Exists(Path.Combine(setup.OutputDir, "inspect-slot", "result.json")));
                Assert.False(Directory.Exists(Path.Combine(setup.OutputDir, "repack-envelope")));
                break;
        }

        Assert.False(File.Exists(Path.Combine(setup.OutputDir, "candidate.json")));

        foreach ((string path, string expectedSha) in setup.SourceHashes)
        {
            Assert.Equal(expectedSha,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
    }
}
