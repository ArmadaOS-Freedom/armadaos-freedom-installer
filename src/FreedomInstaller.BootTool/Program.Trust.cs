using System.Text;
using System.Text.Json;
using FreedomInstaller.Core;

internal static partial class Program
{
    private static async Task<int> RunTrustAsync(JsonElement root, string outputArgument, CancellationToken ct)
    {
        RequireFields(root, ["schemaVersion", "model", "lockState", "a", "b", "efisp"]);
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("schemaVersion must be numeric 1");

        var modelElement = root.GetProperty("model");
        if (modelElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("model must be a string");
        var model = modelElement.GetString()!;

        var lockElement = root.GetProperty("lockState");
        if (lockElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("lockState must be a string");
        var lockState = lockElement.GetString() switch
        {
            "unknown" => BootloaderState.Unknown,
            "locked" => BootloaderState.Locked,
            "unlocked" => BootloaderState.Unlocked,
            _ => throw new InvalidDataException("lockState must be 'unknown', 'locked', or 'unlocked'")
        };

        var a = TrustSlot(root, "a");
        var b = TrustSlot(root, "b");
        var efisp = root.GetProperty("efisp").ValueKind == JsonValueKind.Null
            ? null
            : Claim(root, "efisp");

        var output = ValidateOutput(outputArgument);
        var trust = await EdlRawBootBuilder.InspectSystemUpdateTrustAsync(model, lockState, a, b, efisp, ct);
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(output);

        var resultPath = Path.Combine(output, "result.json");
        var resultCreated = false;
        var resultWritten = false;
        try
        {
            var report = new
            {
                schemaVersion = 1,
                operation = "inspect-trust",
                phase = "trust_inspected",
                trust
            };
            var json = JsonSerializer.Serialize(report, ReportJson);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            using (var stream = new FileStream(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resultCreated = true;
                await stream.WriteAsync(bytes, ct);
                stream.Flush(true);
            }
            resultWritten = true;
            await Console.Out.WriteLineAsync(json.AsMemory(), ct);
            return 0;
        }
        catch
        {
            if (resultCreated && !resultWritten) TryDelete(resultPath);
            throw;
        }
    }

    private static SystemUpdateSlotFiles TrustSlot(JsonElement root, string name)
    {
        var slot = root.GetProperty(name);
        RequireFields(slot, ["boot", "vendorBoot", "initBoot", "abl", "xblConfig", "vbmeta", "recovery", "vbmetaSystem"]);
        return new SystemUpdateSlotFiles(
            Claim(slot, "boot"),
            Claim(slot, "vendorBoot"),
            Claim(slot, "initBoot"),
            Claim(slot, "abl"),
            Claim(slot, "xblConfig"),
            Claim(slot, "vbmeta"),
            Claim(slot, "recovery"),
            Claim(slot, "vbmetaSystem"));
    }
}
