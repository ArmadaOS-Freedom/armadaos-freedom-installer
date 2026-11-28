using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateRecoveryDescriptorMigrationTests
{
    private const string Directory = "/tmp/armada-offline-bootstrap.abcdef1234";

    private static string Success(string state = "migrated", int version = 3) =>
        "{\"schemaVersion\":1,\"ok\":true,\"command\":\"migrate\",\"state\":\"" + state +
        "\",\"descriptorVersion\":" + version + "}";

    [Fact]
    public async Task Ensure_UploadsTheStandaloneScriptAndRunsItAsRoot()
    {
        var session = new FakeSession(Success());

        var result = await OfflineUpdateRecoveryDescriptorMigration.EnsureAsync(
            session, Directory, "TB323FU", useSudo: true, CancellationToken.None);

        Assert.Equal("migrated", result.State);
        Assert.Equal(3, result.DescriptorVersion);

        var upload = Assert.Single(session.Uploads);
        Assert.Equal(Directory + "/armada-offline-recovery-migration.py", upload.Path);
        Assert.True(upload.Bytes > 0);
        Assert.False(upload.UseSudo);

        var command = Assert.Single(session.Commands);
        Assert.Equal("/usr/bin/python3", command.Executable);
        Assert.Equal(
            new[] { "-I", Directory + "/armada-offline-recovery-migration.py", "migrate", "--model", "TB323FU" },
            command.Arguments);
        Assert.True(command.UseSudo);
    }

    [Fact]
    public async Task Ensure_AcceptsAnAlreadyV3Descriptor()
    {
        var session = new FakeSession(Success("already-v3"));

        var result = await OfflineUpdateRecoveryDescriptorMigration.EnsureAsync(
            session, Directory, "TB322FC", useSudo: false, CancellationToken.None);

        Assert.Equal("already-v3", result.State);
        Assert.False(Assert.Single(session.Commands).UseSudo);
    }

    [Fact]
    public async Task Ensure_ReportsTheDeviceErrorCodeWhenTheMigrationRefuses()
    {
        var session = new FakeSession(
            "{\"schemaVersion\":1,\"ok\":false,\"command\":\"migrate\"," +
            "\"error\":\"settings-not-verified\",\"message\":\"the launcher has not verified usable internal settings\"}",
            exitStatus: 1);

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            OfflineUpdateRecoveryDescriptorMigration.EnsureAsync(
                session, Directory, "TB323FU", useSudo: true, CancellationToken.None));

        Assert.Equal(OfflineUpdateBootstrapFailure.DescriptorMigrationFailed, error.Reason);
        Assert.Contains("settings-not-verified", error.Message);
        Assert.Contains("has not verified", error.Message);
    }

    [Fact]
    public async Task Ensure_RejectsAnUnknownState()
    {
        var session = new FakeSession(
            "{\"schemaVersion\":1,\"ok\":true,\"command\":\"migrate\",\"state\":\"partial\",\"descriptorVersion\":3}");

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            OfflineUpdateRecoveryDescriptorMigration.EnsureAsync(
                session, Directory, "TB323FU", useSudo: true, CancellationToken.None));

        Assert.Equal(OfflineUpdateBootstrapFailure.DescriptorMigrationFailed, error.Reason);
        Assert.Contains("partial", error.Message);
    }

    [Fact]
    public async Task Ensure_RejectsANonV3Result()
    {
        var session = new FakeSession(Success("migrated", version: 2));

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            OfflineUpdateRecoveryDescriptorMigration.EnsureAsync(
                session, Directory, "TB323FU", useSudo: true, CancellationToken.None));

        Assert.Equal(OfflineUpdateBootstrapFailure.DescriptorMigrationFailed, error.Reason);
        Assert.Contains("version 2", error.Message);
    }

    [Fact]
    public async Task Ensure_RejectsUnparseableOutputAndKeepsTheFailureDetail()
    {
        var session = new FakeSession("Traceback (most recent call last): boom", exitStatus: 2,
            standardError: "python exploded");

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            OfflineUpdateRecoveryDescriptorMigration.EnsureAsync(
                session, Directory, "TB323FU", useSudo: true, CancellationToken.None));

        Assert.Equal(OfflineUpdateBootstrapFailure.DescriptorMigrationFailed, error.Reason);
        Assert.Contains("exit 2", error.Message);
        Assert.Contains("python exploded", error.Message);
    }

    [Fact]
    public async Task Ensure_RejectsAnUnsupportedCommandEcho()
    {
        var session = new FakeSession(
            "{\"schemaVersion\":1,\"ok\":true,\"command\":\"probe\",\"state\":\"migrated\",\"descriptorVersion\":3}");

        var error = await Assert.ThrowsAsync<OfflineUpdateBootstrapException>(() =>
            OfflineUpdateRecoveryDescriptorMigration.EnsureAsync(
                session, Directory, "TB323FU", useSudo: true, CancellationToken.None));

        Assert.Equal(OfflineUpdateBootstrapFailure.DescriptorMigrationFailed, error.Reason);
        Assert.Contains("unexpected command", error.Message);
    }

    [Theory]
    [InlineData("TB322FC", true)]
    [InlineData("TB323FU", true)]
    [InlineData("TB710FU", true)]
    [InlineData("TB999XX", false)]
    [InlineData("tb323fu", false)]
    [InlineData(null, false)]
    public void IsSupportedModel_AcceptsOnlyTheShippedLayouts(string? model, bool expected)
    {
        Assert.Equal(expected, OfflineUpdateRecoveryDescriptorMigration.IsSupportedModel(model));
    }

    [Theory]
    [InlineData(
        "{\"model\":\"TB323FU\",\"bootPolicy\":\"dual-boot-no-slot-control\",\"actualSlot\":\"a\",\"inactiveSlot\":\"b\"}",
        "TB323FU", true)]
    [InlineData(
        "{\"model\":\"TB322FC\",\"bootPolicy\":\"dual-boot-no-slot-control\",\"bootSource\":\"armada_boot\",\"actualSlot\":null,\"inactiveSlot\":null}",
        "TB322FC", true)]
    [InlineData(
        "{\"model\":\"TB710FU\",\"bootPolicy\":\"dual-boot-no-slot-control\",\"bootSource\":\"armada_boot\",\"actualSlot\":null,\"inactiveSlot\":null}",
        "TB710FU", true)]
    [InlineData(
        "{\"model\":\"TB322FC\",\"bootPolicy\":\"dual-boot-no-slot-control\",\"bootContext\":\"legacy-controller\",\"actualSlot\":null,\"inactiveSlot\":null,\"selectedSlot\":\"a\"}",
        "TB322FC", true)]
    [InlineData(
        "{\"model\":\"TB323FU\",\"bootPolicy\":\"dual-boot-no-slot-control\",\"bootSource\":\"armada_boot\",\"actualSlot\":\"a\",\"inactiveSlot\":\"b\"}",
        "TB323FU", false)]
    [InlineData(
        "{\"model\":\"TB323FU\",\"bootPolicy\":\"legacy-single-slot\",\"actualSlot\":\"a\",\"inactiveSlot\":\"b\"}",
        "TB323FU", false)]
    [InlineData(
        "{\"model\":\"TB323FU\",\"actualSlot\":\"a\",\"inactiveSlot\":\"b\"}",
        "TB323FU", false)]
    [InlineData(
        "{\"model\":\"TB710FU\",\"bootPolicy\":\"dual-boot-no-slot-control\"}",
        "TB710FU", false)]
    [InlineData(
        "{\"model\":\"TB323FU\",\"bootPolicy\":\"dual-boot-no-slot-control\",\"actualSlot\":\"a\",\"inactiveSlot\":\"a\"}",
        "TB323FU", false)]
    [InlineData(
        "{\"model\":\"TB999XX\",\"bootPolicy\":\"dual-boot-no-slot-control\",\"actualSlot\":\"a\",\"inactiveSlot\":\"b\"}",
        "TB999XX", false)]
    public void IsMigrationTarget_RequiresTheMutatingUpgradeContract(
        string infoJson, string model, bool expected)
    {
        using var info = JsonDocument.Parse(infoJson);
        Assert.Equal(expected, OfflineUpdateRecoveryDescriptorMigration.IsMigrationTarget(info.RootElement, model));
    }

    [Fact]
    public void BuildRemoteCommand_RunsThePipedScriptAndCleansUp()
    {
        var command = OfflineUpdateRecoveryDescriptorMigration.BuildRemoteCommand("TB323FU", root: true);

        Assert.Contains("/usr/bin/python3 -I \"$d/migrate.py\" migrate --model TB323FU", command);
        Assert.Contains("cat > \"$d/migrate.py\"", command);
        Assert.Contains("rm -rf -- \"$d\"", command);
        Assert.DoesNotContain("sudo", command);
        Assert.StartsWith("/bin/sh -c '", command);
        Assert.EndsWith("exit $rc'", command);
    }

    [Fact]
    public void BuildRemoteCommand_UsesNonInteractiveSudoForNonRoot()
    {
        var command = OfflineUpdateRecoveryDescriptorMigration.BuildRemoteCommand("TB322FC", root: false);

        Assert.Contains("/usr/bin/sudo -n -- /usr/bin/python3", command);
    }

    [Fact]
    public void BuildRemoteCommand_RejectsAModelWithNoRecoveryLayout()
    {
        Assert.Throws<ArgumentException>(() =>
            OfflineUpdateRecoveryDescriptorMigration.BuildRemoteCommand("TB999XX", root: true));
    }

    private sealed class FakeSession : IOfflineUpdateManagedSession
    {
        private readonly string _output;
        private readonly int _exitStatus;
        private readonly string _standardError;

        internal FakeSession(string output, int exitStatus = 0, string standardError = "")
        {
            _output = output;
            _exitStatus = exitStatus;
            _standardError = standardError;
        }

        internal List<(string Path, long Bytes, bool UseSudo)> Uploads { get; } = new();

        internal List<(string Executable, IReadOnlyList<string> Arguments, bool UseSudo)> Commands { get; } = new();

        public Task<OfflineUpdateSshCommandResult> ExecuteAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            bool useSudo,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Commands.Add((executablePath, arguments, useSudo));
            return Task.FromResult(new OfflineUpdateSshCommandResult(_exitStatus, _output, _standardError));
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
}
