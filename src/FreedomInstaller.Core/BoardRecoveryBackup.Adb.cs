using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

public static partial class BoardRecoveryBackup
{
    private const string BoardBackupListDoneSentinel = "__ARMADA_BACKUP_LIST_DONE__";
    private const string BoardBackupName = "ARMADA_BACKUP";

    /// <summary>
    /// Probes one device for the round original board backup partition via an argv adb command.
    /// <para>
    /// Returns <c>true</c> only when the device confirmed that <c>/dev/block/by-name</c> was listed successfully,
    /// contains an exact <c>ARMADA_BACKUP</c> entry, and emitted the completion sentinel as the final non-empty
    /// line. Returns <c>false</c> only for a confirmed absence: exit code 0, the completion sentinel present as
    /// the final non-empty stdout line, and no exact <c>ARMADA_BACKUP</c> entry. Any missing directory, read
    /// error, unauthorized or offline device, unknown output, missing sentinel or nonzero exit throws instead,
    /// so an error can never be mistaken for an absence. Timeouts, start failures and cancellation propagate.
    /// </para>
    /// </summary>
    public static async Task<bool> DetectAdbAsync(
        string adbPath,
        string serial,
        ICommandRunner runner,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var command = new ProcessCommand(
            adbPath,
            new[]
            {
                "-s", serial, "shell",
                $"ls -1 /dev/block/by-name && echo {BoardBackupListDoneSentinel}"
            },
            TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync(command, token);

        if (result.ExitCode != 0)
        {
            var stderr = (result.StandardError ?? string.Empty).Trim();
            throw new IOException(
                $"adb board-backup probe failed for device '{serial}' with exit code {result.ExitCode}"
                + (stderr.Length == 0 ? "." : $": {stderr}"));
        }

        var lines = (result.StandardOutput ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length != 0)
            .ToArray();

        if (lines.Length == 0 || lines[^1] != BoardBackupListDoneSentinel)
        {
            throw new InvalidDataException(
                $"adb board-backup probe for device '{serial}' did not emit the completion sentinel; output was truncated or unknown.");
        }

        var present = false;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (string.Equals(lines[i], BoardBackupName, StringComparison.Ordinal))
            {
                present = true;
                break;
            }
        }

        return present;
    }
}
