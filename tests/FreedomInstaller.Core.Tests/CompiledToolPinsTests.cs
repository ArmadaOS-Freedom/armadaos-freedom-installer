using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Hashes the real caller-supplied EDL tool files and compares them against the JSON metadata and the
/// internal CompiledToolPins constants, and reflects the Core assembly's CompiledToolPins to confirm
/// the Core build and this test assembly used the same selected pin source.
/// </summary>
public sealed class CompiledToolPinsTests
{
    private const string PinsJsonVariable = "BUILD_TOOL_PINS_JSON";
    private const string ClientPathVariable = "EDL_CLIENT_TEST_BINARY";
    private const string PackerPathVariable = "EDL_RAW_PACKER_PATH";
    private const string UnpackerPathVariable = "EDL_RAW_UNPACKER_PATH";

    [CompiledToolPinsFact]
    public async Task CompiledPins_MatchRealToolFilesAndBuildMetadata()
    {
        var pinsJsonPath = Environment.GetEnvironmentVariable(PinsJsonVariable);

        var clientPath = RequireToolPath(ClientPathVariable);
        var packerPath = RequireToolPath(PackerPathVariable);
        var unpackerPath = RequireToolPath(UnpackerPathVariable);

        Assert.True(File.Exists(pinsJsonPath), PinsJsonVariable + " does not exist: " + pinsJsonPath);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(pinsJsonPath!)) as JsonObject
            ?? throw new InvalidOperationException("BUILD_TOOL_PINS_JSON must be a JSON object.");

        Assert.Equal(1, (int)(root["schemaVersion"]?.GetValue<int>()
            ?? throw new InvalidOperationException("BUILD_TOOL_PINS_JSON.schemaVersion is required.")));

        var tools = root["tools"] as JsonObject
            ?? throw new InvalidOperationException("BUILD_TOOL_PINS_JSON.tools is required.");

        var clientPin = ReadPin(tools, "client");
        var packerPin = ReadPin(tools, "packer");
        var unpackerPin = ReadPin(tools, "unpacker");

        var clientActual = await HashFileAsync(clientPath);
        var packerActual = await HashFileAsync(packerPath);
        var unpackerActual = await HashFileAsync(unpackerPath);

        // JSON pins must match the actual bytes on disk.
        Assert.Equal(clientPin.Bytes, clientActual.Bytes);
        Assert.Equal(clientPin.Sha256, clientActual.Sha256, StringComparer.Ordinal);
        Assert.Equal(packerPin.Bytes, packerActual.Bytes);
        Assert.Equal(packerPin.Sha256, packerActual.Sha256, StringComparer.Ordinal);
        Assert.Equal(unpackerPin.Bytes, unpackerActual.Bytes);
        Assert.Equal(unpackerPin.Sha256, unpackerActual.Sha256, StringComparer.Ordinal);

        // This assembly's linked copy must equal the JSON metadata.
        var testPins = ReadLinkedCompiledToolPins();
        Assert.Equal(clientPin.Bytes, testPins.ClientBytes);
        Assert.Equal(clientPin.Sha256, testPins.ClientSha256, StringComparer.Ordinal);
        Assert.Equal(packerPin.Bytes, testPins.PackerBytes);
        Assert.Equal(packerPin.Sha256, testPins.PackerSha256, StringComparer.Ordinal);
        Assert.Equal(unpackerPin.Bytes, testPins.UnpackerBytes);
        Assert.Equal(unpackerPin.Sha256, testPins.UnpackerSha256, StringComparer.Ordinal);

        // The Core assembly's own CompiledToolPins must match this test-linked copy. A mismatch means
        // Core was built with the fallback while the tests were built with the generated source.
        var corePins = ReadCoreCompiledToolPins();
        Assert.Equal(testPins.ClientBytes, corePins.ClientBytes);
        Assert.Equal(testPins.ClientSha256, corePins.ClientSha256, StringComparer.Ordinal);
        Assert.Equal(testPins.PackerBytes, corePins.PackerBytes);
        Assert.Equal(testPins.PackerSha256, corePins.PackerSha256, StringComparer.Ordinal);
        Assert.Equal(testPins.UnpackerBytes, corePins.UnpackerBytes);
        Assert.Equal(testPins.UnpackerSha256, corePins.UnpackerSha256, StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static (long Bytes, string Sha256) ReadPin(JsonObject tools, string name)
    {
        var node = tools[name] as JsonObject
            ?? throw new InvalidOperationException("BUILD_TOOL_PINS_JSON.tools." + name + " is required.");

        var bytes = node["bytes"]?.GetValue<long>()
            ?? throw new InvalidOperationException("BUILD_TOOL_PINS_JSON.tools." + name + ".bytes is required.");
        var sha = (string?)node["sha256"]
            ?? throw new InvalidOperationException("BUILD_TOOL_PINS_JSON.tools." + name + ".sha256 is required.");

        Assert.Equal(64, sha.Length);
        foreach (var c in sha)
        {
            Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                "BUILD_TOOL_PINS_JSON.tools." + name + ".sha256 must be lowercase hex.");
        }

        return (bytes, sha);
    }

    private static string RequireToolPath(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
        {
            throw new InvalidOperationException(
                "BUILD_TOOL_PINS_JSON is set, so " + name + " must name an existing real tool file.");
        }

        return value;
    }

    private static async Task<(long Bytes, string Sha256)> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            total += read;
        }

        return (total, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
    }

    private static PinsShape ReadLinkedCompiledToolPins()
    {
        // The test project links the same CompiledToolPins.cs into this assembly; the type is internal here.
        var type = typeof(CompiledToolPinsTests).Assembly.GetType("FreedomInstaller.Core.CompiledToolPins")
            ?? throw new InvalidOperationException(
                "The test-linked FreedomInstaller.Core.CompiledToolPins type is missing; the tests csproj must link the selected CompiledToolPins.cs.");
        return ReadShape(type);
    }

    private static PinsShape ReadCoreCompiledToolPins()
    {
        var coreAssembly = typeof(EdlToolchain).Assembly;
        var type = coreAssembly.GetType("FreedomInstaller.Core.CompiledToolPins", throwOnError: false)
            ?? throw new InvalidOperationException(
                "The Core assembly has no internal FreedomInstaller.Core.CompiledToolPins type.");
        return ReadShape(type);
    }

    private static PinsShape ReadShape(Type type)
    {
        long bytes(string name) => (long)(type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue()
            ?? throw new InvalidOperationException(type.FullName + "." + name + " is missing."));
        string sha(string name) => (string)(type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue()
            ?? throw new InvalidOperationException(type.FullName + "." + name + " is missing."));

        return new PinsShape(
            bytes("ClientBytes"),
            sha("ClientSha256"),
            bytes("PackerBytes"),
            sha("PackerSha256"),
            bytes("UnpackerBytes"),
            sha("UnpackerSha256"));
    }

    private sealed class CompiledToolPinsFactAttribute : FactAttribute
    {
        public CompiledToolPinsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinsJsonVariable)))
            {
                Skip = PinsJsonVariable + " is not set.";
            }
        }
    }

    private readonly record struct PinsShape(
        long ClientBytes,
        string ClientSha256,
        long PackerBytes,
        string PackerSha256,
        long UnpackerBytes,
        string UnpackerSha256);
}
