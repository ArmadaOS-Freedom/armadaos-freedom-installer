using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Fixtures mirror the actual <c>armada-offline-bootstrap</c> contract:
/// schemaVersion 1, nested connection/upgrade/system/dependencies/extensions/
/// updater/service/trust objects, and an install receipt that echoes the
/// installed bundle identity.
/// </summary>
public sealed class OfflineUpdateBootstrapCoordinatorTests
{
    private const string TemporaryDirectory = "/tmp/armada-offline-bootstrap.abcdef1234";
    private const string EntryPath = "/usr/libexec/armada/armada-offline-update";
    private const string ExtensionName = "zzzzzz-armada-offline-updater";
    private const string BundleId = "armada-offline-updater";
    private const string BundleVersion = "0.8.0";

    private static readonly string[] IdentityFields =
    {
        "bundle", "extension", "entry", "sha256", "version",
    };

    private static readonly string[] DependencyNames =
    {
        "python3", "systemdSysext", "openssl", "bootc", "ostree", "rpmOstree", "fdtget", "fdtput", "dtc",
    };

    private static readonly byte[] Bundle = Encoding.UTF8.GetBytes("test-updater-bundle-bytes");

    private static string BundleSha256() => Convert.ToHexStringLower(SHA256.HashData(Bundle));

    private static JsonArray Strings(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static string Probe(
        bool connectionReady = true,
        string[]? connectionReasons = null,
        bool upgradeEligible = true,
        string[]? upgradeReasons = null,
        string model = "TB323FU",
        string architecture = "aarch64",
        string[]? compatible = null,
        IReadOnlyDictionary<string, bool>? dependencies = null,
        bool extensionTargetInstalled = false,
        bool entryPresent = false,
        string? version = null,
        bool capable = false,
        string[]? updaterMissing = null,
        bool servicePresent = false,
        int schemaVersion = 1,
        bool ok = true,
        string? error = null)
    {
        var dependencyObject = new JsonObject();
        foreach (var name in DependencyNames)
        {
            var present = dependencies is null || !dependencies.TryGetValue(name, out var configured) || configured;
            dependencyObject[name] = new JsonObject { ["present"] = present };
        }

        return new JsonObject
        {
            ["schemaVersion"] = schemaVersion,
            ["ok"] = ok,
            ["command"] = "probe",
            ["connection"] = new JsonObject
            {
                ["ready"] = connectionReady,
                ["reasons"] = Strings(connectionReasons ?? Array.Empty<string>()),
            },
            ["upgrade"] = new JsonObject
            {
                ["eligible"] = upgradeEligible,
                ["reasons"] = Strings(upgradeReasons ?? Array.Empty<string>()),
                ["tokensEvaluated"] = false,
            },
            ["system"] = new JsonObject
            {
                ["model"] = model,
                ["architecture"] = architecture,
                ["compatible"] = Strings(compatible ?? new[] { "lenovo,baldur", "qcom,kaanapali" }),
                ["osId"] = "armada",
                ["osVersionId"] = "44",
            },
            ["dependencies"] = dependencyObject,
            ["extensions"] = new JsonObject
            {
                ["target"] = ExtensionName,
                ["targetInstalled"] = extensionTargetInstalled,
                ["others"] = new JsonArray(),
            },
            ["updater"] = new JsonObject
            {
                ["entryPresent"] = entryPresent,
                ["version"] = version,
                ["source"] = entryPresent ? "native" : null,
                ["capable"] = capable,
                ["missing"] = Strings(
                    updaterMissing ??
                    (entryPresent ? Array.Empty<string>() : new[] { "usr/libexec/armada/armada-offline-update" })),
            },
            ["service"] = new JsonObject { ["present"] = servicePresent },
            ["trust"] = new JsonObject { ["present"] = false, ["conflict"] = false, ["source"] = null },
            ["error"] = error,
        }.ToJsonString();
    }

    private static string FullyCapableProbe(string version = BundleVersion, bool servicePresent = false) =>
        Probe(entryPresent: true, version: version, capable: true, servicePresent: servicePresent);

    private static string InstallReceipt(
        string state = "installed",
        string? sha256 = null,
        string? version = BundleVersion,
        string? bundle = BundleId,
        string? extension = ExtensionName,
        string? entry = EntryPath,
        bool? sysextRefreshed = null,
        bool? verified = null,
        int schemaVersion = 1,
        bool ok = true)
    {
        var reused = string.Equals(state, "reused", StringComparison.Ordinal);
        var receipt = new JsonObject
        {
            ["schemaVersion"] = schemaVersion,
            ["ok"] = ok,
            ["command"] = "install",
            ["state"] = state,
            ["bundle"] = bundle,
            ["version"] = version,
            ["extension"] = extension,
            ["path"] = null,
            ["replaced"] = false,
            ["previousVersion"] = reused ? version : null,
            ["files"] = 261,
            ["sha256"] = sha256 ?? BundleSha256(),
            // The helper only refreshes the system extension when it actually
            // replaces the runtime, so a reused receipt reports false.
            ["sysextRefreshed"] = sysextRefreshed ?? !reused,
            ["entry"] = entry,
        };

        // Only the reuse path proves the existing files by hash; an installed
        // receipt carries no verified field at all.
        if (verified is not null)
            receipt["verified"] = verified.Value;
        else if (reused)
            receipt["verified"] = true;

        return receipt.ToJsonString();
    }

    private static OfflineUpdateBootstrapCoordinator Coordinator(
        string? expectedSha = null,
        byte[]? bundle = null,
        List<string>? phases = null)
    {
        var bytes = bundle ?? Bundle;
        var progress = phases is null
            ? null
            : new ImmediateProgress<OfflineUpdateBootstrapProgress>(p => phases.Add(p.Phase));
        return new OfflineUpdateBootstrapCoordinator(
            () => new MemoryStream(Encoding.UTF8.GetBytes("#!/usr/bin/env python3\n")),
            () => new MemoryStream(bytes),
            expectedSha ?? Convert.ToHexStringLower(SHA256.HashData(bytes)),
            progress);
    }

    [Fact]
    public async Task Ensure_DeploysMissingUpdaterAndVerifiesWithPinnedHash()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(),
                FullyCapableProbe(),
            }),
        };

        var result = await Coordinator().EnsureAsync(session);

        Assert.True(result.Deployed);
        Assert.True(result.Probe.Updater.EntryPresent);
        Assert.True(result.Probe.Updater.Capable);
        Assert.Equal(TemporaryDirectory, session.CreatedDirectory);
        Assert.Contains(session.Uploads, u => u.Path == TemporaryDirectory + "/armada-offline-bootstrap");
        Assert.Contains(session.Uploads, u => u.Path == TemporaryDirectory + "/armada-offline-updater.tar.gz");
        Assert.Contains(session.Uploads, u => u.UseSudo == false);

        var install = Assert.Single(session.Commands, c => c.Arguments.Contains("install"));
        Assert.True(install.UseSudo);
        Assert.Equal("/usr/bin/python3", install.Executable);
        Assert.Contains("--sha256", install.Arguments);
        Assert.Equal(BundleSha256(), install.Arguments[install.Arguments.Count - 1]);

        var cleanup = Assert.Single(CleanupCommands(session));
        Assert.Contains(TemporaryDirectory, cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_ProbeRunsWithTheSameElevationAsInstall()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { FullyCapableProbe(version: "0.9.0") }),
        };

        await Coordinator().EnsureAsync(session);

        var probes = session.Commands.Where(c => c.Arguments.Contains("probe")).ToList();
        Assert.NotEmpty(probes);
        Assert.All(probes, c => Assert.True(c.UseSudo));
    }

    [Fact]
    public async Task Ensure_RootProbeDoesNotUseSudo()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { FullyCapableProbe(version: "0.9.0") }),
        };
        var coordinator = new OfflineUpdateBootstrapCoordinator(
            () => new MemoryStream(Encoding.UTF8.GetBytes("#!/usr/bin/env python3\n")),
            () => new MemoryStream(Bundle),
            BundleSha256(),
            useSudoForInstall: false);

        await coordinator.EnsureAsync(session);

        var probe = Assert.Single(session.Commands, c => c.Arguments.Contains("probe"));
        Assert.False(probe.UseSudo);
    }

    [Fact]
    public async Task Ensure_ReusesNewerNativeRuntimeWithoutDowngrade()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { FullyCapableProbe(version: "0.9.0") }),
        };

        var result = await Coordinator().EnsureAsync(session);

        Assert.False(result.Deployed);
        Assert.Single(session.ProbesExecuted());
        Assert.Single(session.Uploads);
        Assert.DoesNotContain(session.Commands, c => c.Arguments.Contains("install"));
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_NewerIncapableRuntimeFailsWithoutDowngrade()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(entryPresent: true, version: "0.9.0", capable: false),
            }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.UpdaterIncompatible, error.Reason);
        Assert.Contains("0.9.0", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Commands, c => c.Arguments.Contains("install"));
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_SameVersionCapableRuntimeUsesHashVerifiedInstall()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                FullyCapableProbe(),
                FullyCapableProbe(),
            }),
        };

        var result = await Coordinator().EnsureAsync(session);

        Assert.True(result.Deployed);
        Assert.Single(session.Commands, c => c.Arguments.Contains("install"));
    }

    [Fact]
    public async Task Ensure_SameVersionIncompleteRuntimeIsReplaced()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(entryPresent: true, version: BundleVersion, capable: false),
                FullyCapableProbe(),
            }),
        };

        var result = await Coordinator().EnsureAsync(session);

        Assert.True(result.Deployed);
        Assert.Single(session.Commands, c => c.Arguments.Contains("install"));
    }

    [Fact]
    public async Task Ensure_AcceptsReusedInstallReceiptAfterHealthyReconnect()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(),
                FullyCapableProbe(),
            }),
            InstallResult = new OfflineUpdateSshCommandResult(0, InstallReceipt(state: "reused"), string.Empty),
        };

        var result = await Coordinator().EnsureAsync(session);

        Assert.True(result.Deployed);
        Assert.True(result.Probe.Updater.EntryPresent);
        Assert.True(result.Probe.Updater.Capable);
        Assert.Single(session.Commands, c => c.Arguments.Contains("install"));
        // A reuse without sysext refresh is healthy, but the mandatory
        // post-install probe still has to run and pass.
        Assert.Equal(2, session.ProbesExecuted().Count);
        Assert.Single(CleanupCommands(session));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("string")]
    [InlineData("missing")]
    public async Task Ensure_ReusedInstallReceiptWithoutVerificationIsRejected(string verified)
    {
        var receipt = JsonNode.Parse(InstallReceipt(state: "reused"))!.AsObject();
        switch (verified)
        {
            case "false":
                receipt["verified"] = false;
                break;
            case "string":
                receipt["verified"] = "true";
                break;
            default:
                receipt.Remove("verified");
                break;
        }

        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe() }),
            InstallResult = new OfflineUpdateSshCommandResult(0, receipt.ToJsonString(), string.Empty),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.DeployFailed, error.Reason);
        Assert.Contains("verified", error.Message, StringComparison.Ordinal);
        Assert.Single(CleanupCommands(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Ensure_InstalledInstallReceiptWithoutSysextRefreshIsRejected(bool? sysextRefreshed)
    {
        var receipt = JsonNode.Parse(InstallReceipt())!.AsObject();
        if (sysextRefreshed is { } value)
            receipt["sysextRefreshed"] = value;
        else
            receipt.Remove("sysextRefreshed");

        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe() }),
            InstallResult = new OfflineUpdateSshCommandResult(0, receipt.ToJsonString(), string.Empty),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.DeployFailed, error.Reason);
        if (sysextRefreshed is null)
        {
            // A removed field fails the required-bool read, which names the
            // property in its own casing before the not-refreshed check runs.
            Assert.Contains(
                "missing a valid 'sysextRefreshed' value",
                error.Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(
                "system extension was not refreshed",
                error.Message,
                StringComparison.Ordinal);
        }
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_MissingUpdaterIsBootstrapEligibleAndServicePresenceIsIgnored()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                // The installed update service exists, but the offline runtime
                // does not. That is not an error and must still deploy.
                Probe(servicePresent: true),
                FullyCapableProbe(servicePresent: true),
            }),
        };

        var result = await Coordinator().EnsureAsync(session);

        Assert.True(result.Deployed);
        Assert.Single(session.Commands, c => c.Arguments.Contains("install"));
    }

    [Fact]
    public async Task Ensure_ProbeFailureIsPreciseAndStillCleansUp()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(ok: false, error: "missing python module"),
            }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.ProbeFailed, error.Reason);
        Assert.Contains("missing python module", error.Message, StringComparison.Ordinal);
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_FabricatedFlatProbeShapeIsRejected()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                new JsonObject
                {
                    ["ok"] = true,
                    ["model"] = "TB323FU",
                    ["architecture"] = "aarch64",
                    ["readyForBootstrap"] = true,
                    ["updater"] = new JsonObject { ["present"] = false },
                }.ToJsonString(),
            }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.ProbeFailed, error.Reason);
        Assert.False(session.Commands.Any(c => c.Arguments.Contains("install")));
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_UnsupportedSchemaVersionIsRejected()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe(schemaVersion: 2) }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.ProbeFailed, error.Reason);
        Assert.Contains("schema version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_ConnectionReasonsAreReported()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(connectionReady: false, connectionReasons: new[] { "not-root", "extensions-dir-unusable" }),
            }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.ProbeFailed, error.Reason);
        Assert.Contains("not-root", error.Message, StringComparison.Ordinal);
        Assert.Contains("extensions-dir-unusable", error.Message, StringComparison.Ordinal);
        Assert.False(session.Commands.Any(c => c.Arguments.Contains("install")));
    }

    [Fact]
    public async Task Ensure_UpgradeReasonsAreReported()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(upgradeEligible: false, upgradeReasons: new[] { "insufficient-free-space" }),
            }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.ProbeFailed, error.Reason);
        Assert.Contains("insufficient-free-space", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Commands, c => c.Arguments.Contains("install"));
    }

    [Fact]
    public async Task Ensure_MissingDependencyIsNamed()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(dependencies: new Dictionary<string, bool> { ["python3"] = false }),
            }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.ProbeFailed, error.Reason);
        Assert.Contains("missing device dependencies", error.Message, StringComparison.Ordinal);
        Assert.Contains("python3", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Commands, c => c.Arguments.Contains("install"));
    }

    [Fact]
    public async Task Ensure_NonArm64ProbeIsRejected()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe(architecture: "x86_64") }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.ProbeFailed, error.Reason);
        Assert.Contains("ARM64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_MissingCompatibleBoardListIsRejected()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe(compatible: Array.Empty<string>()) }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.ProbeFailed, error.Reason);
        Assert.Contains("compatible board", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_UnexpectedExtensionTargetIsRejected()
    {
        var document = JsonNode.Parse(Probe())!.AsObject();
        document["extensions"]!.AsObject()["target"] = "other-updater";

        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { document.ToJsonString() }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.ProbeFailed, error.Reason);
        Assert.Contains("other-updater", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_BundleHashMismatchFailsBeforeInstall()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe() }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator(expectedSha: new string('0', 64)).EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.BundleHashMismatch, error.Reason);
        Assert.DoesNotContain(session.Commands, c => c.Arguments.Contains("install"));
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_InstallFailureReportsDeviceMessageAndCleansUp()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe() }),
            InstallResult = new OfflineUpdateSshCommandResult(2, string.Empty, "install: permission denied"),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.DeployFailed, error.Reason);
        Assert.Contains("permission denied", error.Message, StringComparison.Ordinal);
        var install = Assert.Single(session.Commands, c => c.Arguments.Contains("install"));
        Assert.True(install.UseSudo);
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_EmptyInstallOutputIsNotSuccess()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe() }),
            InstallResult = new OfflineUpdateSshCommandResult(0, "  \n", string.Empty),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.DeployFailed, error.Reason);
        Assert.Contains("no JSON receipt", error.Message, StringComparison.Ordinal);
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_InstallReceiptWithWrongHashIsRejected()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe() }),
            InstallResult = new OfflineUpdateSshCommandResult(
                0,
                InstallReceipt(sha256: new string('a', 64)),
                string.Empty),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.DeployFailed, error.Reason);
        Assert.Contains("sha256", error.Message, StringComparison.Ordinal);
        Assert.Single(CleanupCommands(session));
    }

    /// <summary>
    /// Every identity field the helper echoes on both the installed and the
    /// reused path, once per way of leaving it unproven.
    /// </summary>
    public static IEnumerable<object[]> UnprovenReceiptIdentities()
    {
        foreach (var state in new[] { "installed", "reused" })
        {
            foreach (var field in IdentityFields)
            {
                foreach (var kind in new[] { "missing", "null", "nonstring" })
                    yield return new object[] { state, field, kind };
            }
        }
    }

    [Theory]
    [MemberData(nameof(UnprovenReceiptIdentities))]
    public async Task Ensure_InstallReceiptWithUnprovenIdentityFieldIsRejected(
        string state,
        string field,
        string kind)
    {
        var receipt = JsonNode.Parse(InstallReceipt(state: state))!.AsObject();
        string text;
        switch (kind)
        {
            case "null":
                // JsonObject's indexer drops a property assigned null, so the
                // explicit JSON null is written through a sentinel instead.
                const string Sentinel = "__identity_sentinel__";
                receipt[field] = Sentinel;
                text = receipt.ToJsonString().Replace(
                    "\"" + Sentinel + "\"",
                    "null",
                    StringComparison.Ordinal);
                break;
            case "nonstring":
                receipt[field] = 7;
                text = receipt.ToJsonString();
                break;
            default:
                receipt.Remove(field);
                text = receipt.ToJsonString();
                break;
        }

        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe() }),
            InstallResult = new OfflineUpdateSshCommandResult(0, text, string.Empty),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.DeployFailed, error.Reason);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
        Assert.Single(session.Commands, c => c.Arguments.Contains("install"));
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_InstallReceiptWithFailedStateIsRejected()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe() }),
            InstallResult = new OfflineUpdateSshCommandResult(0, InstallReceipt(state: "failed"), string.Empty),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.DeployFailed, error.Reason);
        Assert.Contains("unexpected state", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_InstallReceiptThatFailedOkIsRejected()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[] { Probe() }),
            InstallResult = new OfflineUpdateSshCommandResult(
                0,
                new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["ok"] = false,
                    ["command"] = "install",
                    ["error"] = "bundle digest mismatch",
                }.ToJsonString(),
                string.Empty),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.DeployFailed, error.Reason);
        Assert.Contains("bundle digest mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_VerifyFailureAfterInstallFailsClosed()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(),
                Probe(entryPresent: false),
            }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.VerifyFailed, error.Reason);
        Assert.Contains("entry is still missing", error.Message, StringComparison.Ordinal);
        Assert.Single(CleanupCommands(session));
    }

    [Fact]
    public async Task Ensure_VerifyRequiresCapability()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(),
                Probe(entryPresent: true, version: BundleVersion, capable: false),
            }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.VerifyFailed, error.Reason);
        Assert.Contains("capability", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_VerifyChecksInstalledFilesReportedByProbe()
    {
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(),
                Probe(
                    entryPresent: true,
                    version: BundleVersion,
                    capable: true,
                    updaterMissing: new[] { "usr/libexec/armada/armada-offline-update" }),
            }),
        };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.VerifyFailed, error.Reason);
        Assert.Contains("missing updater files", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/tmp/armada-offline-bootstrap.abcdef123")]
    [InlineData("/tmp/armada-offline-bootstrap.abcdef12345")]
    [InlineData("/tmp/armada-offline-bootstrap.abcdef1234/")]
    [InlineData("/tmp/armada-offline-bootstrap.abcdef1234/extra")]
    [InlineData("/tmp/armada-offline-bootstrap./abcdef1234")]
    [InlineData("/tmp/armada-offline-bootstrap..abcdef123")]
    [InlineData("/tmp/armada-offline-bootstrap.abcdef-234")]
    [InlineData("/tmp/armada-offline-bootstrap.abcdef_234")]
    [InlineData("/tmp/armada-offline-bootstrap.abcdef 234")]
    [InlineData("/tmp/armada-offline-bootstrap.abcdef1234\nrm -rf /tmp/x")]
    [InlineData("/tmp/evil.abcdef1234")]
    [InlineData("relative/armada-offline-bootstrap.abcdef1234")]
    [InlineData("")]
    public async Task Ensure_RejectsEveryPathThatIsNotTheExactMktempShape(string directory)
    {
        var session = new FakeSession { DirectoryOutput = directory + "\n" };

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            Coordinator().EnsureAsync(session));

        Assert.Equal(OfflineUpdateBootstrapFailure.RemoteDirectory, error.Reason);
        Assert.Empty(session.Uploads);
        Assert.Empty(CleanupCommands(session));
        Assert.DoesNotContain(session.Commands, c => c.Arguments.Contains("install"));
    }

    [Theory]
    [InlineData("/tmp/armada-offline-bootstrap.abcdef1234")]
    [InlineData("/tmp/armada-offline-bootstrap.ABCDEF1234")]
    [InlineData("/tmp/armada-offline-bootstrap.0123456789")]
    public void TemporaryDirectoryPredicate_AcceptsExactMktempShapes(string directory) =>
        Assert.True(OfflineUpdateBootstrapCoordinator.IsValidTemporaryDirectory(directory));

    [Fact]
    public async Task Ensure_ProgressReportsProbeDeployVerifyReady()
    {
        var phases = new List<string>();
        var session = new FakeSession
        {
            ProbeResponses = new Queue<string>(new[]
            {
                Probe(),
                FullyCapableProbe(),
            }),
        };

        await Coordinator(phases: phases).EnsureAsync(session);

        Assert.Contains(OfflineUpdateBootstrapCoordinator.ProbingPhase, phases);
        Assert.Contains(OfflineUpdateBootstrapCoordinator.DeployingPhase, phases);
        Assert.Contains(OfflineUpdateBootstrapCoordinator.VerifyingPhase, phases);
        Assert.Contains(OfflineUpdateBootstrapCoordinator.ReadyPhase, phases);
    }

    private sealed class FakeSession : IOfflineUpdateManagedSession
    {
        internal Queue<string> ProbeResponses { get; init; } = new();

        internal OfflineUpdateSshCommandResult InstallResult { get; init; } =
            new(0, InstallReceipt(), string.Empty);

        internal string DirectoryOutput { get; init; } = TemporaryDirectory + "\n";

        internal string? CreatedDirectory { get; private set; }

        internal List<(string Path, long Bytes, bool UseSudo)> Uploads { get; } = new();

        internal List<(string Executable, IReadOnlyList<string> Arguments, bool UseSudo)> Commands { get; } = new();

        internal List<string> ProbesExecuted() =>
            Commands.Where(c => c.Arguments.Contains("probe")).Select(c => c.Executable).ToList();

        public Task<OfflineUpdateSshCommandResult> ExecuteAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            bool useSudo,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add((executablePath, arguments, useSudo));

            if (executablePath == "/bin/sh" &&
                arguments.Count == 2 &&
                arguments[0] == "-c" &&
                arguments[1].StartsWith("mktemp -d ", StringComparison.Ordinal))
            {
                CreatedDirectory = DirectoryOutput.Trim();
                return Task.FromResult(new OfflineUpdateSshCommandResult(0, DirectoryOutput, string.Empty));
            }

            if (executablePath == "/bin/sh" &&
                arguments.Count == 2 &&
                arguments[0] == "-c" &&
                arguments[1].StartsWith("rm -rf -- ", StringComparison.Ordinal))
            {
                return Task.FromResult(new OfflineUpdateSshCommandResult(0, string.Empty, string.Empty));
            }

            if (executablePath == "/usr/bin/python3" && arguments.Contains("probe"))
            {
                if (ProbeResponses.Count == 0)
                    return Task.FromResult(new OfflineUpdateSshCommandResult(1, string.Empty, "no probe response"));
                return Task.FromResult(new OfflineUpdateSshCommandResult(0, ProbeResponses.Dequeue(), string.Empty));
            }

            if (executablePath == "/usr/bin/python3" && arguments.Contains("install"))
                return Task.FromResult(InstallResult);

            return Task.FromResult(new OfflineUpdateSshCommandResult(0, string.Empty, string.Empty));
        }

        public async Task UploadFileAsync(
            string remotePath,
            Stream content,
            bool useSudo,
            CancellationToken token = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, token);
            Uploads.Add((remotePath, buffer.Length, useSudo));
        }
    }

    private static List<string> CleanupCommands(FakeSession session) =>
        session.Commands
            .Where(c => c.Executable == "/bin/sh" &&
                        c.Arguments.Count == 2 &&
                        c.Arguments[0] == "-c" &&
                        c.Arguments[1].StartsWith("rm -rf -- ", StringComparison.Ordinal))
            .Select(c => c.Arguments[1])
            .ToList();

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        internal ImmediateProgress(Action<T> report) => _report = report;

        public void Report(T value) => _report(value);
    }
}
