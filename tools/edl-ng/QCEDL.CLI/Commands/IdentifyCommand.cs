using System.CommandLine;
using QCEDL.CLI.Core;
using QCEDL.CLI.Helpers;

namespace QCEDL.CLI.Commands;

internal sealed class IdentifyCommand
{
    public static Command Create(GlobalOptionsBinder globalOptionsBinder)
    {
        var command = new Command(
            "identify",
            "Reads the Sahara serial number and HWID from an EDL device without uploading a loader or touching storage.");
        command.SetHandler(ExecuteAsync, globalOptionsBinder);
        return command;
    }

    private static async Task<int> ExecuteAsync(GlobalOptionsBinder globalOptions)
    {
        Logging.Log("Executing 'identify' command...", LogLevel.Trace);

        return await CommandExecutor.RunAsync("identify", async () =>
        {
            using var manager = new EdlManager(globalOptions);
            var identity = await manager.IdentifySaharaAsync();

            Console.WriteLine(
                $"ARMADA_EDL_IDENTITY_V1 serial={identity.SerialNumberHex} hwid={identity.HwidHex}");
            return 0;
        });
    }
}
