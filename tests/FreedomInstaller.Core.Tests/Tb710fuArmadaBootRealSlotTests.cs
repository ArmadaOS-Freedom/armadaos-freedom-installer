// Fork only: the ArmadaOS-Freedom/armadaos-freedom#174 offline acceptance of the TB710FU ARMADA_BOOT boot repack.
// Builds both slot candidates from partitions read off a real tablet with an armada-boot-v1 TB710FU package
// directory (package.json + runtime + DTBs, unsigned: this exercises the boot builder, not the importer). Skipped
// unless TB710FU_REAL_SLOT_DIR (boot/vendor_boot/init_boot/abl/xbl_config/vbmeta _a/_b as .bin or .img),
// TB710FU_ARMADA_PACKAGE_DIR and the native EDL_RAW_PACKER_PATH / EDL_RAW_UNPACKER_PATH are set; those inputs are
// device data and are never committed. Candidates are left under TEST_ARTIFACT_ROOT for avbtool.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;
using Xunit.Abstractions;

namespace FreedomInstaller.Core.Tests;

public sealed class Tb710fuArmadaBootRealSlotTests
{
    private readonly ITestOutputHelper _output;

    public Tb710fuArmadaBootRealSlotTests(ITestOutputHelper output) => _output = output;

    private static VerifiedEdlFile Claim(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new VerifiedEdlFile(path, stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private static string Slot(string directory, string role, string slot)
    {
        foreach (string extension in new[] { ".bin", ".img" })
        {
            string path = Path.Combine(directory, role + "_" + slot + extension);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("missing real partition " + role + "_" + slot, directory);
    }

    [Fact]
    public async Task Both_slots_repack_from_real_partitions_with_the_armada_boot_recipe()
    {
        string? slots = Environment.GetEnvironmentVariable("TB710FU_REAL_SLOT_DIR");
        string? package = Environment.GetEnvironmentVariable("TB710FU_ARMADA_PACKAGE_DIR");
        string? packer = Environment.GetEnvironmentVariable("EDL_RAW_PACKER_PATH");
        string? unpacker = Environment.GetEnvironmentVariable("EDL_RAW_UNPACKER_PATH");
        if (string.IsNullOrWhiteSpace(slots) || string.IsNullOrWhiteSpace(package)
            || string.IsNullOrWhiteSpace(packer) || string.IsNullOrWhiteSpace(unpacker))
        {
            return;
        }

        string work = Path.Combine(
            Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
            "tb710fu-armada-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        EdlRawPackageContract contract = PackageReader.ReadEdlRawContract(package, "TB710FU");
        Assert.True(contract.BootRecipe.UsesArmadaBoot);
        Assert.False(LinuxRuntimeContract.RequiresMirrorMarker(contract.DeviceModel));

        using EdlRawBootTools tools = await EdlRawBootTools.LoadAsync(Claim(packer), Claim(unpacker));
        var runner = new ProcessRunner();

        foreach (string slot in new[] { "b", "a" })
        {
            VerifiedEdlFile sourceBoot = Claim(Slot(slots, "boot", slot));
            using EdlRawBuiltBoot built = await EdlRawBootBuilder.BuildAsync(
                new EdlRawBootBuildRequest(
                    slot,
                    sourceBoot,
                    Claim(Slot(slots, "vendor_boot", slot)),
                    Claim(Slot(slots, "init_boot", slot)),
                    Claim(Slot(slots, "abl", slot)),
                    Claim(Slot(slots, "xbl_config", slot)),
                    contract,
                    tools,
                    work),
                runner);

            _output.WriteLine(
                $"slot {slot}: source {sourceBoot.Sha256}, candidate {built.CandidateBoot.Length} B sha "
                + $"{built.CandidateBoot.Sha256}, packed raw {built.PackedRaw.Length} B, budget "
                + $"{built.SlotMaterial.LoaderBudget.Profile} {built.SlotMaterial.LoaderBudget.MaxRawBytes}, "
                + $"payload {built.Envelope.OriginalImageSize} B, rollback {built.Envelope.RollbackIndex}");

            // Same partition size, within the slot's loader budget, and AOSP-signed (SHA256_RSA4096).
            Assert.Equal(sourceBoot.Length, built.CandidateBoot.Length);
            Assert.True(built.PackedRaw.Length <= built.SlotMaterial.LoaderBudget.MaxRawBytes);
            Assert.Equal(2, built.Envelope.AlgorithmType);
            File.Copy(built.CandidateBoot.Path, Path.Combine(work, "candidate_boot_" + slot + ".img"));
            File.Copy(built.PackedRaw.Path, Path.Combine(work, "packed_raw_" + slot + ".bin"));
        }

        File.WriteAllText(Path.Combine(work, "README.txt"), "candidates for avbtool verify_image\n");
        _output.WriteLine("candidates in " + work);
    }
}
