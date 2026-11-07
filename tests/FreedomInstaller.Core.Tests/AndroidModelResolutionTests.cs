using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Port-ROM model identity specifications. A port ROM may report a supported Lenovo model only in
/// bootimage / attestation hints while its surface props claim another vendor or generation. The
/// resolver must prefer those hints, fail closed on contradictions, and never treat a platform name or
/// SoC token as a device identity. These tests drive the real DeviceTransport.ReadAdbIdentityAsync
/// through a fake command runner; they must fail on the pre-fix code and pass after it.
/// </summary>
public sealed class AndroidModelResolutionTests
{
    private const string AdbPath = "/tools/adb";

    private sealed class ScriptedRunner : ICommandRunner
    {
        private readonly Func<ProcessCommand, string> _responder;
        public List<ProcessCommand> Commands { get; } = new();

        public ScriptedRunner(Func<ProcessCommand, string> responder) => _responder = responder;

        public Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(new ProcessResult(0, _responder(command), string.Empty));
        }
    }

    private static ScriptedRunner RunnerReturning(string stdout) => new(_ => stdout);

    private static DeviceTransport TransportWith(string stdout) =>
        new(AdbPath, "/tools/fastboot", RunnerReturning(stdout));

    private static string Prop(string key, string value) => $"[{key}]: [{value}]\n";

    // ------------------------------------------------------------------ single hint wins

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    [InlineData("TB710FU")]
    public async Task BootimageHintOverridesXiaomiSurface_AllSupportedModels(string model)
    {
        var dump =
            Prop("ro.product.brand", "Xiaomi") +
            Prop("ro.product.model", "25091RP04C") +
            Prop("ro.product.vendor.model", "25091RP04C") +
            Prop("ro.product.bootimage.model", model) +
            Prop("ro.build.version.incremental", "V1.0.0");

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0001");

        Assert.Equal(model, identity.Model);
        Assert.Equal(BootloaderState.Unknown, identity.BootloaderState);
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    [InlineData("TB710FU")]
    public async Task AttestationHintOnly_IdentifiesAllSupportedModels(string model)
    {
        var dump =
            Prop("ro.product.model", "25091RP04C") +
            Prop("ro.product.model_for_attestation", model);

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0002");

        Assert.Equal(model, identity.Model);
    }

    [Fact]
    public async Task DeviceForAttestationHintOnly_IdentifiesModel()
    {
        var dump =
            Prop("ro.product.model", "25091RP04C") +
            Prop("ro.product.device_for_attestation", "TB710FU");

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0003");

        Assert.Equal("TB710FU", identity.Model);
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    [InlineData("TB710FU")]
    public async Task LenovoBootimageFingerprint_HintsModel(string model)
    {
        var fingerprint = $"Lenovo/{model.ToLowerInvariant()}_prc_wifi/{model.ToLowerInvariant()}:14/UP1A/eng:user/release-keys";
        var dump =
            Prop("ro.product.brand", "Xiaomi") +
            Prop("ro.product.model", "25091RP04C") +
            Prop("ro.bootimage.build.fingerprint", fingerprint);

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0004");

        Assert.Equal(model, identity.Model);
    }

    [Fact]
    public async Task NonLenovoFingerprint_IsNotAHint_SurfaceStillWins()
    {
        var dump =
            Prop("ro.product.brand", "Xiaomi") +
            Prop("ro.product.model", "TB322FC") +
            Prop("ro.product.vendor.model", "TB322FC") +
            Prop("ro.bootimage.build.fingerprint", "Xiaomi/piano/piano:14/UP1A/eng:user/release-keys");

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0005");

        Assert.Equal("TB322FC", identity.Model);
    }

    [Theory]
    [InlineData("TB322FC", "elden")]
    [InlineData("TB323FU", "baldur")]
    [InlineData("TB710FU", "topaz")]
    public async Task LenovoBoardCodenamesAgreeWithBootimageModel(string model, string board)
    {
        var dump = Prop("ro.product.model", "25091RP04C")
            + Prop("ro.product.bootimage.model", model)
            + Prop("ro.product.device_for_attestation", board)
            + Prop("ro.bootimage.build.fingerprint", $"Lenovo/{board}_prc_wifi/{board}:16/build/id:user/release-keys");
        Assert.Equal(model, (await TransportWith(dump).ReadAdbIdentityAsync("SYNTH-BOARD")).Model);
    }

    [Fact]
    public async Task ConflictingBoardCodenameIsRejected()
    {
        var dump = Prop("ro.product.bootimage.model", "TB322FC")
            + Prop("ro.bootimage.build.fingerprint", "Lenovo/baldur_prc_wifi/baldur:16/build/id:user/release-keys");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH-CONFLICT"));
    }

    [Fact]
    public async Task StockSurface_StillIdentifies()
    {
        var dump =
            Prop("ro.product.model", "TB323FU") +
            Prop("ro.product.vendor.model", "TB323FU");

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0006");

        Assert.Equal("TB323FU", identity.Model);
    }

    [Fact]
    public async Task SurfaceAndBootimageHint_Agree_Identifies()
    {
        var dump =
            Prop("ro.product.model", "TB322FC") +
            Prop("ro.product.vendor.model", "TB322FC") +
            Prop("ro.product.bootimage.model", "TB322FC");

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0007");

        Assert.Equal("TB322FC", identity.Model);
    }

    // ---------------------------------------------------------------- preferred wins over surface

    [Fact]
    public async Task SurfaceTb323fuWithBootimageTb322fc_PreferredWins()
    {
        // ROM surface claims a different supported generation; the bootimage hint must decide the candidate.
        // Later hardware proof (Sahara HWID / serial) will confirm or reject; the resolver only nominates.
        var dump =
            Prop("ro.product.model", "TB323FU") +
            Prop("ro.product.vendor.model", "TB323FU") +
            Prop("ro.product.bootimage.model", "TB322FC");

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0008");

        Assert.Equal("TB322FC", identity.Model);
        Assert.Equal(BootloaderState.Unknown, identity.BootloaderState);
    }

    // ---------------------------------------------------------------- fail closed

    [Fact]
    public async Task ConflictingPreferredHints_ThrowWithoutFallingThroughToSurface()
    {
        var dump =
            Prop("ro.product.model", "TB322FC") +
            Prop("ro.product.bootimage.model", "TB322FC") +
            Prop("ro.product.model_for_attestation", "TB323FU");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH0009"));

        Assert.Contains("MODEL_EVIDENCE_CONFLICT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictingPreferredHints_DoNotFallBackToMatchingSurface()
    {
        // Even if the surface agrees with one hint, two different preferred canonical models are fatal.
        var dump =
            Prop("ro.product.model", "TB322FC") +
            Prop("ro.product.vendor.model", "TB322FC") +
            Prop("ro.product.bootimage.model", "TB322FC") +
            Prop("ro.product.device_for_attestation", "TB710FU");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH0010"));
    }

    [Fact]
    public async Task UnsupportedBoardHint_DoesNotFallThroughToSpoofedSurface()
    {
        var dump =
            Prop("ro.product.model", "TB322FC") +
            Prop("ro.product.vendor.model", "TB322FC") +
            Prop("ro.product.bootimage.model", "TB999ZZ");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH0011"));

        Assert.Contains("MODEL_NOT_IDENTIFIED", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LenovoFingerprintUnsupportedDevice_DoesNotFallThrough()
    {
        var dump =
            Prop("ro.product.model", "TB322FC") +
            Prop("ro.bootimage.build.fingerprint", "Lenovo/unknown/unknown:14/UP1A/eng:user/release-keys");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH0012"));

        Assert.Contains("MODEL_NOT_IDENTIFIED", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicatePropertyConflictingValues_Throw()
    {
        var dump =
            Prop("ro.product.model", "TB322FC") +
            Prop("ro.product.bootimage.model", "TB322FC") +
            Prop("ro.product.bootimage.model", "TB323FU");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH0013"));

        Assert.Contains("MODEL_EVIDENCE_CONFLICT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateSameModel_IsNotAConflict()
    {
        var dump =
            Prop("ro.product.bootimage.model", "TB710FU") +
            Prop("ro.product.bootimage.model", "Lenovo TB710FU_PRC");

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0014");

        Assert.Equal("TB710FU", identity.Model);
    }

    [Fact]
    public async Task SameSocOnly_IsNotEnough()
    {
        var dump =
            Prop("ro.board.platform", "sun") +
            Prop("ro.soc.model", "SM8750") +
            Prop("ro.product.model", "25091RP04C");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH0015"));
    }

    [Fact]
    public async Task NoHints_NoSupportedSurface_Throws()
    {
        var dump = Prop("ro.product.model", "25091RP04C");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH0016"));
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    [InlineData("TB710FU")]
    public async Task MixedOnePlusSurfaceUnknownBootimageWithBothAttestationKeys_IdentifiesModel(string model)
    {
        var dump =
            Prop("ro.product.brand", "OnePlus") +
            Prop("ro.product.model", "OPD2413") +
            Prop("ro.product.vendor.model", "OPD2413") +
            Prop("ro.product.name", "OPD2413") +
            Prop("ro.product.bootimage.model", "OPD2413") +
            Prop("ro.product.model_for_attestation", model) +
            Prop("ro.product.device_for_attestation", model);

        var transport = TransportWith(dump);
        var identity = await transport.ReadAdbIdentityAsync("SYNTH-MIXED-01");

        Assert.Equal(model, identity.Model);
        Assert.StartsWith(model, AndroidModelResolver.Describe(dump), StringComparison.Ordinal);
        Assert.Contains("OPD2413", AndroidModelResolver.Describe(dump), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    [InlineData("TB710FU")]
    public async Task SupportedBootimageWithUnknownAttestation_SucceedsAsCandidate(string model)
    {
        var dump =
            Prop("ro.product.model", "OPD2413") +
            Prop("ro.product.bootimage.model", model) +
            Prop("ro.product.model_for_attestation", "OPD2413");

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH-MIXED-02");

        Assert.Equal(model, identity.Model);
    }

    [Fact]
    public async Task UnknownPreferredClaimsWithSpoofedSurface_RejectedAsNotIdentified()
    {
        var dump =
            Prop("ro.product.model", "TB322FC") +
            Prop("ro.product.vendor.model", "TB322FC") +
            Prop("ro.product.bootimage.model", "OPD2413") +
            Prop("ro.product.model_for_attestation", "OP615EL1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH-MIXED-03"));

        Assert.Contains("MODEL_NOT_IDENTIFIED", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MODEL_EVIDENCE_CONFLICT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownBootimageWithContradictoryAttestationValues_ThrowsConflict()
    {
        var dump =
            Prop("ro.product.bootimage.model", "OPD2413") +
            Prop("ro.product.model_for_attestation", "TB322FC") +
            Prop("ro.product.device_for_attestation", "TB323FU");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH-MIXED-04"));

        Assert.Contains("MODEL_EVIDENCE_CONFLICT", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedSurfaceAndBootimageOnly_RejectedAsNotIdentified(bool withBootimage)
    {
        // Donor OnePlus model is not an alias: even as the only surface/bootimage claim it never
        // nominates a supported canonical model.
        var dump =
            Prop("ro.product.brand", "OnePlus") +
            Prop("ro.product.model", "OPD2413") +
            Prop("ro.product.vendor.model", "OPD2413") +
            Prop("ro.product.name", "OPD2413");
        if (withBootimage)
            dump += Prop("ro.product.bootimage.model", "OPD2413");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransportWith(dump).ReadAdbIdentityAsync("SYNTH-OPD-ONLY"));

        Assert.StartsWith(AndroidModelResolver.NotIdentifiedCode, AndroidModelResolver.Describe(dump), StringComparison.Ordinal);
        Assert.Contains(AndroidModelResolver.NotIdentifiedCode, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticReportsActualUnknownModelWithoutDumpingOtherProperties()
    {
        var dump = Prop("ro.product.model", "25091RP04C") + Prop("ro.serialno", "DO-NOT-LOG");
        var diagnostic = AndroidModelResolver.Describe(dump);
        Assert.Contains("25091RP04C", diagnostic);
        Assert.DoesNotContain("DO-NOT-LOG", diagnostic);
    }

    // ---------------------------------------------------------------- invariants preserved

    [Fact]
    public async Task SerialIsScopedAndGetpropOnce()
    {
        var runner = RunnerReturning(Prop("ro.product.bootimage.model", "TB322FC"));
        var transport = new DeviceTransport(AdbPath, "/tools/fastboot", runner);

        await transport.ReadAdbIdentityAsync("SYNTH-SCOPED-01");

        var command = Assert.Single(runner.Commands);
        Assert.Equal(AdbPath, command.Executable);
        Assert.Equal(new[] { "-s", "SYNTH-SCOPED-01", "shell", "getprop" }, command.Arguments);
        Assert.True(command.Timeout > TimeSpan.Zero);
    }

    [Fact]
    public async Task ResolverFailure_KeepsBootloaderUnknown()
    {
        var dump =
            Prop("ro.product.model", "TB323FU") +
            Prop("ro.product.bootimage.model", "TB323FU");

        var identity = await TransportWith(dump).ReadAdbIdentityAsync("SYNTH0017");

        Assert.Equal(BootloaderState.Unknown, identity.BootloaderState);
    }
}
