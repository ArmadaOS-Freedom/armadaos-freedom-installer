using System.CommandLine;
using System.Diagnostics;
using QCEDL.CLI.Core;
using QCEDL.CLI.Helpers;

namespace QCEDL.CLI.Commands;

internal sealed class EraseSectorCommand
{
    private static readonly Argument<ulong> StartSectorArgument = new("start_sector", "The starting sector LBA to erase from.");
    private static readonly Argument<ulong> SectorsArgument = new("sectors", "The number of sectors to erase.");

    private static readonly Option<uint> LunOption = new(
        aliases: ["--lun", "-u"],
        description: "Specify the LUN number to erase from.",
        getDefaultValue: () => 0);

    private static readonly Option<string?> ExpectedBusSerialOption = new(
        aliases: ["--expected-bus-serial"],
        description: "Expected 8-hex-digit bus serial to validate against the selected EDL device (Windows Qualcomm USB Firehose only).");

    private static readonly Option<uint?> ExpectedSectorSizeOption = new(
        aliases: ["--expected-sector-size"],
        description: "Expected sector size to verify against the actual storage geometry (requires --expected-bus-serial; 512 or 4096).");

    public static Command Create(GlobalOptionsBinder globalOptionsBinder)
    {
        var command = new Command("erase-sector", "Erases a specified number of sectors from a given LUN and start LBA.")
        {
            StartSectorArgument,
            SectorsArgument,
            LunOption,
            ExpectedBusSerialOption,
            ExpectedSectorSizeOption
        };

        command.SetHandler(ExecuteAsync,
            globalOptionsBinder,
            StartSectorArgument,
            SectorsArgument,
            LunOption,
            ExpectedBusSerialOption,
            ExpectedSectorSizeOption);

        return command;
    }

    private static async Task<int> ExecuteAsync(
        GlobalOptionsBinder globalOptions,
        ulong startSectorUlong,
        ulong sectorsToEraseUlong,
        uint lun,
        string? expectedBusSerial,
        uint? expectedSectorSize)
    {
        Logging.Log($"Executing 'erase-sector' command: LUN {lun}, Start LBA {startSectorUlong}, Sectors {sectorsToEraseUlong}...", LogLevel.Trace);

        if (sectorsToEraseUlong == 0)
        {
            Logging.Log("Error: Number of sectors to erase must be greater than 0.", LogLevel.Error);
            return 1;
        }

        if (expectedBusSerial != null)
        {
            if (expectedBusSerial.Length != 8 || !expectedBusSerial.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            {
                Logging.Log("Error: --expected-bus-serial must be exactly 8 hexadecimal digits.", LogLevel.Error);
                return 1;
            }
        }

        if (expectedSectorSize.HasValue != (expectedBusSerial != null))
        {
            Logging.Log("Error: --expected-sector-size and --expected-bus-serial must be provided together.", LogLevel.Error);
            return 1;
        }

        if (expectedSectorSize is not null and not (512 or 4096))
        {
            Logging.Log("Error: --expected-sector-size must be 512 or 4096.", LogLevel.Error);
            return 1;
        }

        return await CommandExecutor.RunAsync("erase-sector", async () =>
        {
            using var manager = expectedBusSerial == null
                ? new EdlManager(globalOptions)
                : new EdlManager(globalOptions, expectedBusSerial);
            if (expectedBusSerial is not null && (!OperatingSystem.IsWindows() || manager.IsDirectMode))
            {
                throw new InvalidOperationException("--expected-bus-serial is not supported on this platform or in direct mode.");
            }

            var effectiveLun = manager.IsDirectMode ? 0u : lun;
            var geometry = await manager.GetStorageGeometryAsync(effectiveLun);
            var targetDescription = manager.GetTargetDescription(effectiveLun);

            if (expectedSectorSize is not null
                && (!geometry.SectorSizeVerified || geometry.SectorSize != expectedSectorSize.Value))
            {
                throw new InvalidOperationException(
                    $"--expected-sector-size {expectedSectorSize.Value} was requested, but the actual sector size for {targetDescription} could not be verified or does not match (reported: {geometry.SectorSize}{(geometry.SectorSizeVerified ? "" : ", unverified")}).");
            }

            Logging.Log($"Using sector size: {geometry.SectorSize} bytes for {targetDescription}.", LogLevel.Debug);

            Logging.Log($"Attempting to erase {sectorsToEraseUlong} sectors starting at LBA {startSectorUlong} on {targetDescription}...");
            var eraseStopwatch = Stopwatch.StartNew();

            await manager.EraseSectorsAsync(effectiveLun, startSectorUlong, sectorsToEraseUlong);

            eraseStopwatch.Stop();
            Logging.Log($"Successfully erased {sectorsToEraseUlong} sectors in {eraseStopwatch.Elapsed.TotalSeconds:F2}s.");

            return 0;
        });
    }
}
