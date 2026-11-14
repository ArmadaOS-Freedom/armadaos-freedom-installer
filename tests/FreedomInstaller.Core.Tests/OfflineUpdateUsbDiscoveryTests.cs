using System.Text.Json;
using System.Text.RegularExpressions;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateUsbDiscoveryTests
{
    private const string Id1 = "11111111-1111-1111-1111-111111111111";
    private const string Id2 = "22222222-2222-2222-2222-2222222222aa";
    private static readonly string PowerShell = Path.Combine(
        Path.GetFullPath(
            Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT") is { Length: > 0 } root
                ? root
                : Path.Combine(Path.GetTempPath(), "freedom-installer-usb-discovery-tests")),
        "fake-powershell.exe");

    private static object Row(string id, string pnp, string address = "172.16.42.2", string mask = "255.255.255.0") =>
        new { id, pnpDeviceId = pnp, address, mask };

    private static string Envelope(params object?[] rows) =>
        JsonSerializer.Serialize(new { schemaVersion = 1, adapters = rows });

    private static string One(string id = Id1) =>
        Envelope(Row(id, @"USB\VID_1D6B&PID_0104\instance"));

    [Fact]
    public void ParseCandidates_AcceptsFixturesCanonicalizesAndDedups()
    {
        var json = Envelope(
            Row(Id2.ToUpperInvariant(), @"usb\vid_1d6b&pid_0100&rev_abCD&mi_0f\composite", "172.16.42.254"),
            Row(Id2, @"usb\vid_1d6b&pid_0100&rev_abCD&mi_0f\composite", "172.16.42.254"),
            Row(Id1, @"USB\VID_1D6B&PID_0104\standalone"),
            Row("33333333-3333-3333-3333-333333333333", @"PCI\VEN_1234&DEV_0104\x"),
            Row("44444444-4444-4444-4444-444444444444", @"USB\VID_1D6B&PID_01040\x"),
            Row("55555555-5555-5555-5555-555555555555", @"USB\VID_01D6B&PID_0104\x"),
            Row("66666666-6666-6666-6666-666666666666", @"USB\VID_1D6B&PID_0104\x", "2001:db8::2"));
        var endpoints = OfflineUpdateUsbDiscovery.ParseCandidates(json);
        Assert.Equal(2, endpoints.Count);
        Assert.Equal(Id1, endpoints[0].AdapterId);
        Assert.Equal(Id2, endpoints[1].AdapterId);
        Assert.Equal("172.16.42.2", endpoints[0].LocalAddress);
        Assert.Equal("172.16.42.254", endpoints[1].LocalAddress);
        Assert.All(endpoints, e => Assert.Equal("172.16.42.1", e.Host));
    }

    [Theory]
    [InlineData("172.16.42.0")]
    [InlineData("172.16.42.1")]
    [InlineData("172.16.42.255")]
    [InlineData("172.16.43.2")]
    [InlineData("2001:db8::2")]
    [InlineData("172.16.42")]
    [InlineData("172.16.42.02")]
    [InlineData(" 172.16.42.2")]
    [InlineData("172.16.42.2 ")]
    public void ParseCandidates_RejectsInvalidAddresses(string address)
    {
        Assert.Empty(OfflineUpdateUsbDiscovery.ParseCandidates(
            Envelope(Row(Id1, @"USB\VID_1D6B&PID_0104\x", address))));
    }

    [Fact]
    public void ParseCandidates_EnforcesMaskAndIgnoresMalformedRows()
    {
        var rows = Envelope(
            null,
            Row(Id1, @"USB\VID_1D6B&PID_0104\x", mask: "255.255.0.0"),
            Row(Guid.Empty.ToString(), @"USB\VID_1D6B&PID_0104\x"),
            Row("", @"USB\VID_1D6B&PID_0104\x"),
            Row(Id2, "USB\\VID_1D6B&PID_0104\\x\nUSB\\VID_1D6B&PID_0104\\y"));
        Assert.Empty(OfflineUpdateUsbDiscovery.ParseCandidates(rows));
    }

    [Fact]
    public void ParseCandidates_RejectsMalformedJsonAndWrongEnvelopes()
    {
        Assert.ThrowsAny<JsonException>(() => OfflineUpdateUsbDiscovery.ParseCandidates("{"));
        Assert.Throws<InvalidDataException>(() => OfflineUpdateUsbDiscovery.ParseCandidates("[]"));
        Assert.Throws<InvalidDataException>(() => OfflineUpdateUsbDiscovery.ParseCandidates("{\"schemaVersion\":2,\"adapters\":[]}"));
        Assert.Throws<InvalidDataException>(() => OfflineUpdateUsbDiscovery.ParseCandidates("{\"schemaVersion\":1,\"adapters\":{}}"));
    }

    [Fact]
    public async Task FindSingleAsync_RunsOneFixedCommandAndReturnsEndpoint()
    {
        var runner = new FakeRunner(new ProcessResult(0, One(), ""));
        var endpoint = await OfflineUpdateUsbDiscovery.FindSingleAsync(runner, PowerShell);
        Assert.Equal(Id1, endpoint.AdapterId);
        var command = Assert.Single(runner.Invocations);
        Assert.Equal(PowerShell, command.Executable);
        Assert.Equal(OfflineUpdateUsbDiscovery.BuildCommand(PowerShell).Arguments, command.Arguments);
    }

    [Fact]
    public async Task FindSingleAsync_PrecancelDoesNotInvokeRunner()
    {
        var runner = new FakeRunner(new ProcessResult(0, One(), ""));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineUpdateUsbDiscovery.FindSingleAsync(runner, PowerShell, cts.Token));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task FindSingleAsync_ZeroOrMultipleThrowsWithoutChoosingFirst()
    {
        var zero = new FakeRunner(new ProcessResult(0, Envelope(), ""));
        await Assert.ThrowsAsync<IOException>(() => OfflineUpdateUsbDiscovery.FindSingleAsync(zero, PowerShell));
        var many = new FakeRunner(new ProcessResult(0, Envelope(
            Row(Id1, @"USB\VID_1D6B&PID_0104\one"),
            Row(Id2, @"USB\VID_1D6B&PID_0100\two", "172.16.42.3")), ""));
        var ex = await Assert.ThrowsAsync<IOException>(() => OfflineUpdateUsbDiscovery.FindSingleAsync(many, PowerShell));
        Assert.Contains("Multiple (2)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSingleAsync_BoundsDiagnosticsAndPropagatesCancellationWithoutRetry()
    {
        var failing = new FakeRunner(new ProcessResult(7, "", new string('x', 3000) + "TAIL"));
        var ex = await Assert.ThrowsAsync<IOException>(() => OfflineUpdateUsbDiscovery.FindSingleAsync(failing, PowerShell));
        Assert.True(ex.Message.Length < 2200);
        Assert.EndsWith("TAIL", ex.Message, StringComparison.Ordinal);
        var cancelled = new FakeRunner(new ProcessResult(0, "", ""), new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineUpdateUsbDiscovery.FindSingleAsync(cancelled, PowerShell));
        Assert.Single(cancelled.Invocations);
    }

    [Fact]
    public void BuildCommand_IsFixedReadOnlyQuery()
    {
        var command = OfflineUpdateUsbDiscovery.BuildCommand(PowerShell);
        Assert.Equal(TimeSpan.FromSeconds(20), command.Timeout);
        Assert.Equal(4, command.Arguments.Count);
        Assert.Equal("-NoProfile", command.Arguments[0]);
        Assert.Equal("-NonInteractive", command.Arguments[1]);
        Assert.Equal("-Command", command.Arguments[2]);
        var script = command.Arguments[3];
        Assert.Contains("Get-CimInstance Win32_NetworkAdapter", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json", script, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(Set|New|Remove|Start|Stop|Restart)-", script);
    }

    private sealed class FakeRunner(ProcessResult result, Exception? error = null) : ICommandRunner
    {
        public List<ProcessCommand> Invocations { get; } = new();
        public Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken token = default)
        {
            Invocations.Add(command);
            token.ThrowIfCancellationRequested();
            return error is null ? Task.FromResult(result) : Task.FromException<ProcessResult>(error);
        }
    }
}
