using System.Text;
using System.Text.Json.Nodes;
using FreedomInstaller.App;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// A 0.8 runtime that does not advertise <c>preflight-session-v1</c> is refused
/// by the pairing gate instead of being asked to preflight. The password-route
/// bootstrap install is what unlocks the capability, and the next connection
/// then proves the *existing* session with a read-only, bound preflight instead
/// of starting a second prepare/execute.
/// </summary>
/// <remarks>
/// The coordinator, the UI capability gate, the transfer preflight and the RPC
/// client are the real product types. The SSH transport is doubled: the loopback
/// device server answers the updater RPC, and a small managed-session adapter
/// stands in for the authenticated shell the bootstrap runs on. This is
/// therefore a bootstrap/transport integration test, not real SSH against a
/// real tablet.
/// </remarks>
public sealed partial class OfflineUpdatePackageTests
{
    private static readonly string[] BootstrapDependencies =
    {
        "python3", "systemdSysext", "openssl", "bootc", "ostree", "rpmOstree", "fdtget", "fdtput", "dtc",
    };

    [Fact]
    public async Task BootstrapInstallUnlocksSessionPreflight_ForTheSessionThatAlreadyExists()
    {
        var c = new Case(Path.Combine(root, "bootstrap-session"));
        var work = Directory.CreateDirectory(Path.Combine(root, "bootstrap-session-work")).FullName;
        using var imported = await Import(c.ArchivePath, Model, "0.8.0", work, c.Verify);
        var payload = c.Entries["images/linux-root.img.gz"];
        var session = new OfflineUpdateSession(
            SessionIdConst, Model, RecoveryUuidConst, Hash(c.DescriptorBytes));

        // The tablet is already prepared for this exact package and session; only
        // the updater runtime reachable by the key route is too old to name it.
        // Its reported version stays 0.8.0 across the bootstrap: the helper
        // replaces the code, not the version string it advertises.
        var server = new FakeDeviceServer(Model, c.DescriptorBytes, payload, [1], [2])
        {
            State = "prepared",
            UpdaterVersion = "0.8.0",
        };
        // The prepared session already stores the whole payload; the bound
        // preflight must credit it instead of asking for another upload.
        server.SeedResumeOffset(payload.Length);

        // 1. Key route: the updater reports the expected 0.8.0 but not the
        //    capability, so the pairing gate refuses this connection and never
        //    asks for a preflight.
        await using (var keyClient = server.CreateClient())
        {
            var info = await keyClient.GetInfoAsync();
            Assert.Equal("0.8.0", info.GetProperty("updaterVersion").GetString());
            Assert.True(OfflineUpdateUiState.IsUpdaterVersionAtLeast(info, "0.8.0"));
            Assert.False(OfflineUpdateUiState.SupportsPreflightSession(info));

            Assert.Equal(["info"], server.Methods);
            Assert.Equal(0, server.PreflightCalls);
            Assert.Null(server.PreflightSessionId);
            Assert.Equal("prepared", server.State);
            Assert.Equal(0, server.UploadCalls);
            Assert.Equal(0, server.PrepareCalls);
            Assert.Equal(0, server.ExecuteCalls);
        }

        // 2. Password route: the bootstrap install runs over the authenticated
        //    session and is the only step that changes the device's capability.
        var bundle = Encoding.UTF8.GetBytes("armada-offline-updater-bundle");
        var bootstrap = new BootstrapManagedSession(server, Model);
        var coordinator = new OfflineUpdateBootstrapCoordinator(
            () => new MemoryStream(Encoding.UTF8.GetBytes("#!/usr/bin/env python3\n")),
            () => new MemoryStream(bundle),
            Hash(bundle),
            expectedBundleVersion: "0.8.0");

        var bootstrapResult = await coordinator.EnsureAsync(bootstrap);

        Assert.True(bootstrapResult.Deployed);
        Assert.Equal("0.8.0", bootstrapResult.Probe.Updater.Version);
        Assert.True(bootstrapResult.Probe.Updater.Capable);
        Assert.Equal(1, bootstrap.InstallCount);
        Assert.True(bootstrap.InstallUsedSudo);

        // The receipt the coordinator accepted echoed the SHA-256 of the bytes
        // that were actually uploaded, not a value written into the fixture.
        Assert.Equal(Hash(bundle), bootstrap.UploadedBundleSha256);
        Assert.Equal(Hash(bundle), bootstrap.InstallShaArgument);
        Assert.True(server.SessionPreflightCapability);

        // 3. Reconnect: the same device now advertises the capability, so the
        //    bound preflight can prove the session that already exists.
        server.Methods.Clear();
        await using (var passwordClient = server.CreateClient())
        {
            var upgradedInfo = await passwordClient.GetInfoAsync();
            // The bootstrap changed the code, not the version it reports, so
            // the capability alone is what unlocks the bound preflight.
            Assert.Equal("0.8.0", upgradedInfo.GetProperty("updaterVersion").GetString());
            Assert.True(OfflineUpdateUiState.SupportsPreflightSession(upgradedInfo));

            var transfer = new OfflineUpdateTransfer(passwordClient, TimeSpan.FromMilliseconds(1));
            var preflight = await transfer.PreflightAsync(imported, session);

            Assert.Equal(1, server.PreflightCalls);
            Assert.Equal(SessionIdConst, server.PreflightSessionId);
            Assert.Equal(SessionIdConst, preflight.GetProperty("sessionId").GetString());
            Assert.Equal("preflight_passed", preflight.GetProperty("phase").GetString());
            Assert.Equal("not_started", preflight.GetProperty("bootWriteState").GetString());
            Assert.True(OfflineUpdateUiState.PreflightPassed(preflight));

            // A pass for the stored session is read-only evidence: the payload is
            // not uploaded again, the session is not prepared again, and nothing
            // is executed or restarted.
            Assert.Equal(["info", "preflight"], server.Methods);
            Assert.Equal("prepared", server.State);
            Assert.Equal(0, server.UploadCalls);
            Assert.Equal(0, server.PrepareCalls);
            Assert.Equal(0, server.ExecuteCalls);
            Assert.Equal(1, bootstrap.InstallCount);
        }

        // Control: a failed install leaves the capability off, so the other
        // device is refused in the same way and is never asked to preflight.
        var failing = new FakeDeviceServer(Model, c.DescriptorBytes, payload, [3], [4])
        {
            State = "prepared",
            UpdaterVersion = "0.8.0",
        };
        var failingBootstrap = new BootstrapManagedSession(failing, Model) { FailInstall = true };
        var failure = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(
            () => coordinator.EnsureAsync(failingBootstrap));

        Assert.Equal(OfflineUpdateBootstrapFailure.DeployFailed, failure.Reason);
        Assert.False(failing.SessionPreflightCapability);
        Assert.Equal(1, failingBootstrap.InstallCount);

        await using var failingClient = failing.CreateClient();
        var failingInfo = await failingClient.GetInfoAsync();
        Assert.Equal("0.8.0", failingInfo.GetProperty("updaterVersion").GetString());
        Assert.False(OfflineUpdateUiState.SupportsPreflightSession(failingInfo));
        Assert.Equal(["info"], failing.Methods);
        Assert.Equal(0, failing.PreflightCalls);
        Assert.Null(failing.PreflightSessionId);
        Assert.Equal("prepared", failing.State);
        Assert.Equal(0, failing.UploadCalls);
        Assert.Equal(0, failing.PrepareCalls);
        Assert.Equal(0, failing.ExecuteCalls);
    }

    /// <summary>
    /// The authenticated shell the bootstrap runs on, backed by the same device
    /// fixture the RPC connections use. Only a successful install receipt turns
    /// on the capability, mirroring a helper that deploys the runtime itself.
    /// </summary>
    private sealed class BootstrapManagedSession(FakeDeviceServer server, string model) : IOfflineUpdateManagedSession
    {
        internal const string RemoteDirectory = "/tmp/armada-offline-bootstrap.abcdef1234";

        internal bool FailInstall { get; init; }

        internal int InstallCount { get; private set; }

        internal string? UploadedBundleSha256 { get; private set; }

        internal string? InstallShaArgument { get; private set; }

        internal bool InstallUsedSudo { get; private set; }

        public Task<OfflineUpdateSshCommandResult> ExecuteAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            bool useSudo,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (executablePath == "/bin/sh" &&
                arguments.Count == 2 &&
                arguments[0] == "-c" &&
                arguments[1].StartsWith("mktemp -d ", StringComparison.Ordinal))
            {
                return Task.FromResult(new OfflineUpdateSshCommandResult(
                    0, RemoteDirectory + "\n", string.Empty));
            }

            if (executablePath == "/bin/sh" &&
                arguments.Count == 2 &&
                arguments[0] == "-c" &&
                arguments[1].StartsWith("rm -rf -- ", StringComparison.Ordinal))
            {
                return Task.FromResult(new OfflineUpdateSshCommandResult(0, string.Empty, string.Empty));
            }

            if (executablePath == "/usr/bin/python3" && arguments.Contains("probe"))
                return Task.FromResult(new OfflineUpdateSshCommandResult(0, BootstrapProbe(model), string.Empty));

            if (executablePath == "/usr/bin/python3" && arguments.Contains("install"))
            {
                InstallCount++;
                InstallShaArgument = arguments[arguments.Count - 1];
                InstallUsedSudo = useSudo;

                // The helper hashes the bundle it received; a receipt digest that
                // was not derived from this upload cannot pass here.
                Assert.Equal(UploadedBundleSha256, InstallShaArgument);

                if (FailInstall)
                {
                    return Task.FromResult(new OfflineUpdateSshCommandResult(
                        2, string.Empty, "install: the system extension could not be refreshed"));
                }

                server.SessionPreflightCapability = true;
                return Task.FromResult(new OfflineUpdateSshCommandResult(
                    0, BootstrapInstallReceipt(InstallShaArgument!), string.Empty));
            }

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
            Assert.StartsWith(RemoteDirectory + "/", remotePath, StringComparison.Ordinal);
            if (remotePath.EndsWith("/armada-offline-updater.tar.gz", StringComparison.Ordinal))
                UploadedBundleSha256 = Hash(buffer.ToArray());
        }
    }

    /// <summary>Probe document of the documented <c>armada-offline-bootstrap</c> contract.</summary>
    private static string BootstrapProbe(string model)
    {
        var dependencies = new JsonObject();
        foreach (var name in BootstrapDependencies)
            dependencies[name] = new JsonObject { ["present"] = true };

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["ok"] = true,
            ["command"] = "probe",
            ["connection"] = new JsonObject
            {
                ["ready"] = true,
                ["reasons"] = new JsonArray(),
            },
            ["upgrade"] = new JsonObject
            {
                ["eligible"] = true,
                ["reasons"] = new JsonArray(),
                ["tokensEvaluated"] = false,
            },
            ["system"] = new JsonObject
            {
                ["model"] = model,
                ["architecture"] = "aarch64",
                ["compatible"] = new JsonArray("lenovo,baldur", "qcom,kaanapali"),
                ["osId"] = "armada",
                ["osVersionId"] = "44",
            },
            ["dependencies"] = dependencies,
            ["extensions"] = new JsonObject
            {
                ["target"] = "zzzzzz-armada-offline-updater",
                ["targetInstalled"] = false,
                ["others"] = new JsonArray(),
            },
            ["updater"] = new JsonObject
            {
                ["entryPresent"] = true,
                ["version"] = "0.8.0",
                ["source"] = "native",
                ["capable"] = true,
                ["missing"] = new JsonArray(),
            },
            ["service"] = new JsonObject { ["present"] = false },
            ["trust"] = new JsonObject { ["present"] = false, ["conflict"] = false },
        }.ToJsonString();
    }

    /// <summary>Install receipt of the documented contract, echoing the uploaded bundle identity.</summary>
    private static string BootstrapInstallReceipt(string bundleSha256) => new JsonObject
    {
        ["schemaVersion"] = 1,
        ["ok"] = true,
        ["command"] = "install",
        ["state"] = "installed",
        ["bundle"] = "armada-offline-updater",
        ["version"] = "0.8.0",
        ["extension"] = "zzzzzz-armada-offline-updater",
        ["files"] = 261,
        ["sha256"] = bundleSha256,
        ["sysextRefreshed"] = true,
        ["entry"] = "/usr/libexec/armada/armada-offline-update",
    }.ToJsonString();
}
