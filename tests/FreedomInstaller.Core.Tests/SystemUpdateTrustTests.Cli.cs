using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed partial class SystemUpdateTrustTests
{
    private static readonly JsonSerializerOptions CliRequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_InspectTrust_ValidFixture_WritesResultJsonAndMatchesStdout(bool gbl)
    {
        string requestPath = _fx.NewCaseFilePath("request.json");
        string outDir = _fx.NewCaseFilePath("out");

        JsonObject request = CreateRequestObject(gbl);
        await File.WriteAllTextAsync(requestPath, request.ToJsonString(CliRequestJsonOptions));

        (int exitCode, string stdout, string stderr) = await RunInspectTrustCliAsync(requestPath, outDir);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.True(Directory.Exists(outDir));

        if (OperatingSystem.IsLinux())
        {
            UnixFileMode mode = File.GetUnixFileMode(outDir);
            UnixFileMode expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(expectedMode, mode);
        }

        string[] entries = Directory.GetFileSystemEntries(outDir);
        string singleEntry = Assert.Single(entries);
        string resultPath = Path.Combine(outDir, "result.json");
        Assert.Equal(resultPath, singleEntry);

        string fileContent = await File.ReadAllTextAsync(resultPath, Encoding.UTF8);
        Assert.Equal(fileContent, stdout.Replace("\r\n", "\n", StringComparison.Ordinal));

        using JsonDocument doc = JsonDocument.Parse(fileContent);
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("inspect-trust", root.GetProperty("operation").GetString());
        Assert.Equal("trust_inspected", root.GetProperty("phase").GetString());

        JsonElement trust = root.GetProperty("trust");
        Assert.Equal("TB323FU", trust.GetProperty("model").GetString());
        Assert.Equal(gbl ? "GblPreserveMetadata" : "AospResign", trust.GetProperty("mode").GetString());

        JsonElement slotA = trust.GetProperty("a");
        JsonElement slotB = trust.GetProperty("b");
        Assert.Equal("a", slotA.GetProperty("slot").GetString());
        Assert.Equal("b", slotB.GetProperty("slot").GetString());
        Assert.True(slotA.GetProperty("maxRawBytes").GetInt64() > 0);
        Assert.True(slotB.GetProperty("maxRawBytes").GetInt64() > 0);
    }

    [Theory]
    [InlineData("numeric-lock")]
    [InlineData("unknown-lock")]
    [InlineData("extra-top")]
    [InlineData("missing-recovery")]
    [InlineData("nonnumeric-length")]
    [InlineData("unknown-model")]
    public async Task Cli_InspectTrust_InvalidRequestMutations_FailsWithoutCreatingOutput(string mutation)
    {
        string requestPath = _fx.NewCaseFilePath("request.json");
        string outDir = _fx.NewCaseFilePath("out");

        JsonObject request = CreateRequestObject(gbl: false);
        switch (mutation)
        {
            case "numeric-lock":
                request["lockState"] = 1;
                break;
            case "unknown-lock":
                request["lockState"] = "relocked";
                break;
            case "extra-top":
                request["unexpectedField"] = "extra";
                break;
            case "missing-recovery":
                Assert.True(request["a"]!.AsObject().Remove("recovery"));
                break;
            case "nonnumeric-length":
                request["a"]!["boot"]!["length"] = "4096";
                break;
            case "unknown-model":
                request["model"] = "TB999FU";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        await File.WriteAllTextAsync(requestPath, request.ToJsonString(CliRequestJsonOptions));

        (int exitCode, string stdout, string stderr) = await RunInspectTrustCliAsync(requestPath, outDir);

        AssertCliFailure(exitCode, stdout, stderr);
        Assert.False(Directory.Exists(outDir));
    }

    [Theory]
    [InlineData("duplicate-root-schemaVersion")]
    [InlineData("duplicate-claim-path")]
    public async Task Cli_InspectTrust_DuplicateJsonKeys_FailsWithoutCreatingOutput(string duplicateCase)
    {
        string requestPath = _fx.NewCaseFilePath("request.json");
        string outDir = _fx.NewCaseFilePath("out");

        string json = CreateRequestObject(gbl: false).ToJsonString(CliRequestJsonOptions);
        string mutatedJson = duplicateCase switch
        {
            "duplicate-root-schemaVersion" => InsertFirstOccurrence(
                json,
                "\"schemaVersion\":1,",
                "\"schemaVersion\":1,\"schemaVersion\":1,"),
            "duplicate-claim-path" => InsertFirstOccurrence(
                json,
                "\"boot\":{\"path\":",
                "\"boot\":{\"path\":\"dup\",\"path\":"),
            _ => throw new ArgumentOutOfRangeException(nameof(duplicateCase), duplicateCase, null)
        };

        await File.WriteAllTextAsync(requestPath, mutatedJson);

        (int exitCode, string stdout, string stderr) = await RunInspectTrustCliAsync(requestPath, outDir);

        AssertCliFailure(exitCode, stdout, stderr);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public async Task Cli_InspectTrust_PreexistingOutputDir_PreservesSentinelAndWritesNoResult()
    {
        string requestPath = _fx.NewCaseFilePath("request.json");
        string outDir = _fx.NewCaseFilePath("out");

        Directory.CreateDirectory(outDir);
        string sentinelPath = Path.Combine(outDir, "sentinel.txt");
        const string sentinelContent = "keep-existing-content";
        await File.WriteAllTextAsync(sentinelPath, sentinelContent);

        JsonObject request = CreateRequestObject(gbl: false);
        await File.WriteAllTextAsync(requestPath, request.ToJsonString(CliRequestJsonOptions));

        (int exitCode, string stdout, string stderr) = await RunInspectTrustCliAsync(requestPath, outDir);

        AssertCliFailure(exitCode, stdout, stderr);
        Assert.True(File.Exists(sentinelPath));
        Assert.Equal(sentinelContent, await File.ReadAllTextAsync(sentinelPath));
        Assert.False(File.Exists(Path.Combine(outDir, "result.json")));
        string singleEntry = Assert.Single(Directory.GetFileSystemEntries(outDir));
        Assert.Equal(sentinelPath, singleEntry);
    }

    [Fact]
    public async Task Cli_InspectTrust_BadBootSha_FailsWithoutOutputAndLeavesFixtureBootUnchanged()
    {
        string requestPath = _fx.NewCaseFilePath("request.json");
        string outDir = _fx.NewCaseFilePath("out");
        VerifiedEdlFile expectedBoot = _fx.AospA.Boot;

        JsonObject request = CreateRequestObject(gbl: false);
        request["a"]!["boot"]!["sha256"] = new string('0', 64);
        await File.WriteAllTextAsync(requestPath, request.ToJsonString(CliRequestJsonOptions));

        (int exitCode, string stdout, string stderr) = await RunInspectTrustCliAsync(requestPath, outDir);

        AssertCliFailure(exitCode, stdout, stderr);
        Assert.False(Directory.Exists(outDir));
        VerifiedEdlFile actualBoot = ClaimFile(_fx.AospA.Boot.Path);
        Assert.Equal(expectedBoot.Path, actualBoot.Path);
        Assert.Equal(expectedBoot.Length, actualBoot.Length);
        Assert.Equal(expectedBoot.Sha256, actualBoot.Sha256,
            StringComparer.OrdinalIgnoreCase);
    }

    private JsonObject CreateRequestObject(bool gbl)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(
            new
            {
                schemaVersion = 1,
                model = "TB323FU",
                lockState = gbl ? "locked" : "unlocked",
                a = gbl ? _fx.GblA : _fx.AospA,
                b = gbl ? _fx.GblB : _fx.AospB,
                efisp = gbl ? _fx.PrcEfisp : null
            },
            CliRequestJsonOptions);

        JsonObject obj = Assert.IsType<JsonObject>(node);
        Assert.Equal(JsonValueKind.Number, obj["a"]!["boot"]!["length"]!.GetValueKind());
        Assert.True(obj["a"]!["boot"]!["length"]!.GetValue<long>() > 0);
        return obj;
    }

    private static string InsertFirstOccurrence(string input, string search, string replacement)
    {
        int index = input.IndexOf(search, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected substring '{search}' was not found in serialized request.");
        return string.Concat(input.AsSpan(0, index), replacement, input.AsSpan(index + search.Length));
    }

    private static void AssertCliFailure(int exitCode, string stdout, string stderr)
    {
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);

        string trimmed = stderr.TrimEnd('\r', '\n');
        Assert.False(string.IsNullOrWhiteSpace(trimmed));
        Assert.DoesNotContain('\n', trimmed);

        using JsonDocument doc = JsonDocument.Parse(trimmed);
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("error", out JsonElement errorProp));
        Assert.Equal(JsonValueKind.String, errorProp.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(errorProp.GetString()));
    }

    private static string RequireBootToolDll()
    {
        string? dll = Environment.GetEnvironmentVariable("BOOT_TOOL_DLL");
        Assert.False(string.IsNullOrWhiteSpace(dll), "Required environment variable 'BOOT_TOOL_DLL' is not set.");
        Assert.True(Path.IsPathFullyQualified(dll), $"BOOT_TOOL_DLL must be an absolute path: '{dll}'.");
        Assert.True(File.Exists(dll), $"BOOT_TOOL_DLL does not exist: '{dll}'.");
        return dll;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunInspectTrustCliAsync(
        string requestPath,
        string outDir)
    {
        string dll = RequireBootToolDll();
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("inspect-trust");
        psi.ArgumentList.Add(requestPath);
        psi.ArgumentList.Add(outDir);

        using var process = new Process { StartInfo = psi };
        Assert.True(process.Start(), "Failed to start dotnet inspect-trust process.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
            Assert.Fail("CLI inspect-trust subprocess timed out after 90 seconds.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }
}
