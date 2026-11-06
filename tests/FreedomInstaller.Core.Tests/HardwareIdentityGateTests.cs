using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class HardwareIdentityGateTests : IClassFixture<EdlClientToolchainFixture>
{
    private readonly EdlClientToolchainFixture _fixture;
    public HardwareIdentityGateTests(EdlClientToolchainFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongHardwareStopsBeforeLoaderOrPartitionRead(bool raw)
    {
        var runner = new E003FakeCommandRunner { IdentificationHardwareId = "002B40E100400000" };
        var discovery = Discovery();
        var client = new EdlClient(_fixture.Toolchain, Binding(), runner, discovery);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe(client, raw));
        var command = Assert.Single(runner.Commands);
        Assert.Equal("identify", command.Arguments.Last());
        Assert.DoesNotContain("--loader", command.Arguments);
        Assert.False(client.HasInitialSaharaProof);
        Assert.Null(client.HardwareIdentity);
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    [InlineData("TB710FU")]
    public async Task HardwareResultCarriesCanonicalProfileAndPhysicalSerial(string model)
    {
        var profile = EdlDeviceProfiles.FindByModel(model)!;
        var runner = new E003FakeCommandRunner { IdentificationHardwareId = profile.FullHardwareId! };
        var hardware = await GlobalEdlDetection.DetectIdentityAsync(
            _fixture.Toolchain.ClientPath, runner, Discovery(), E003EdlTestData.BusSerialHex);
        Assert.Same(profile, hardware.Profile);
        Assert.Equal(E003EdlTestData.BusSerialHex, hardware.BusSerialHex);
        Assert.Equal(E003EdlTestData.DeviceInstanceId, hardware.DeviceInstanceId);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task FreshAndroidEntryIdentifiesBeforeLoading()
    {
        var runner = new E003FakeCommandRunner();
        runner.Enqueue(E003EdlTestData.InitialProbe());
        var client = new EdlClient(_fixture.Toolchain, Binding(), runner, Discovery());
        await client.ProbeAsync(0, true);
        Assert.Equal("identify", runner.Commands[0].Arguments.Last());
        Assert.Equal("printgpt", runner.Commands[1].Arguments[0]);
        Assert.True(client.HasInitialSaharaProof);
        Assert.Equal(_fixture.Toolchain.Model, client.HardwareIdentity!.Profile.Model);
    }

    [Fact]
    public async Task OfflineDetectionIsReusedWithoutSecondIdentify()
    {
        var runner = new E003FakeCommandRunner();
        var discovery = Discovery();
        var hardware = await GlobalEdlDetection.DetectIdentityAsync(
            _fixture.Toolchain.ClientPath, runner, discovery);
        runner.Enqueue(E003EdlTestData.InitialProbe());
        var client = new EdlClient(_fixture.Toolchain,
            GlobalEdlBinding.Attach(hardware.Profile, discovery), runner, discovery, hardware);
        await client.ProbeAsync(0, true);
        Assert.Single(runner.Commands.Where(c => c.Arguments.Contains("identify")));
        Assert.Same(hardware, client.HardwareIdentity);
    }

    [Fact]
    public async Task EndpointChangeAfterDetectionStopsBeforeLoader()
    {
        var runner = new E003FakeCommandRunner();
        var discovery = Discovery();
        var hardware = await GlobalEdlDetection.DetectIdentityAsync(
            _fixture.Toolchain.ClientPath, runner, discovery);
        discovery.Endpoints = new[] { E003EdlTestData.Endpoint() with { DeviceInstanceId = E003EdlTestData.OtherInstanceId } };
        var client = new EdlClient(_fixture.Toolchain, Binding(), runner, discovery, hardware);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ProbeAsync(0, true));
        Assert.Single(runner.Commands);
        Assert.False(client.HasInitialSaharaProof);
    }

    private Task<EdlProbeEvidence> Probe(EdlClient client, bool raw) => raw
        ? client.ProbeRawGeometryAsync(0, true,
            Path.Combine(_fixture.NewCaseDirectory("hardware"), "probe.bin"))
        : client.ProbeAsync(0, true);

    private static E003ScriptedEndpointDiscovery Discovery() => new()
    {
        Endpoints = new[] { E003EdlTestData.Endpoint() },
    };

    private EdlEntryResult Binding() => new(new AndroidMaintenanceIdentity(
        new DeviceIdentity("SYNTHETIC", _fixture.Toolchain.Model, BootloaderState.Unknown)
        {
            Firmware = new FirmwareSnapshot("ported-ROM", null, null, null, BootloaderState.Unknown),
        }, E003EdlTestData.SocSerialDecimal), E003EdlTestData.Endpoint(), E003EdlTestData.BusSerialHex);
}
