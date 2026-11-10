using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreedomInstaller.Core;

// File-only raw payload preparation; does not sign, access devices, authorize physical activation,
// or enable TB710FU physical A/B. Generic core remains available to Windows callers separately.
internal static partial class Program
{
    private const long MaxRawBytesLimit = 268435456L;
    private const string InstalledPackerPath = "/usr/libexec/armada/kshim-tools/MultiBootKernelPatcher";
    private const string InstalledUnpackerPath = "/usr/libexec/armada/kshim-tools/UnpackTool";
    private const UnixFileMode ExecutableMask =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private static async Task<int> RunRawAsync(JsonElement root, string outputArgument, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("build-raw requires Linux installed kshim tools");

        RequireFields(root, [
            "schemaVersion",
            "slot",
            "model",
            "normalBootArgs",
            "diagnosticBootArgs",
            "maxRawBytes",
            "files"
        ]);

        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("schemaVersion must be numeric 1");

        var slot = ReadNonBlankString(root, "slot");
        if (slot is not ("a" or "b"))
            throw new InvalidDataException("slot must be 'a' or 'b'");

        var model = ReadNonBlankString(root, "model");
        if (model is not ("TB322FC" or "TB323FU" or "TB710FU"))
            throw new InvalidDataException("model must be 'TB322FC', 'TB323FU', or 'TB710FU'");

        var normalBootArgs = ReadNonBlankString(root, "normalBootArgs");
        var diagnosticBootArgs = ReadNonBlankString(root, "diagnosticBootArgs");

        var maxElement = root.GetProperty("maxRawBytes");
        if (maxElement.ValueKind != JsonValueKind.Number ||
            !maxElement.TryGetInt64(out var maxRawBytes) ||
            maxRawBytes <= 0 ||
            maxRawBytes > MaxRawBytesLimit)
            throw new InvalidDataException("maxRawBytes must be a positive int64 <= 268435456");

        var files = root.GetProperty("files");
        RequireFields(files, ["baseImage", "kernel", "initramfs", "runtime", "dtbNormal", "dtbDiagnostic"]);
        var baseImage = Claim(files, "baseImage");
        var kernel = Claim(files, "kernel");
        var initramfs = Claim(files, "initramfs");
        var runtime = Claim(files, "runtime");
        var dtbNormal = Claim(files, "dtbNormal");
        var dtbDiagnostic = Claim(files, "dtbDiagnostic");

        var output = ValidateOutput(outputArgument);
        var packer = await ClaimInstalledToolAsync(InstalledPackerPath, ct);
        var unpacker = await ClaimInstalledToolAsync(InstalledUnpackerPath, ct);

        var rawRequest = new SystemUpdateRawRequest(
            model,
            baseImage,
            kernel,
            initramfs,
            runtime,
            dtbNormal,
            dtbDiagnostic,
            normalBootArgs,
            diagnosticBootArgs,
            maxRawBytes,
            output,
            packer,
            unpacker);

        var raw = await EdlRawBootBuilder.BuildSystemUpdateRawAsync(rawRequest, new ProcessRunner(), ct);

        var report = new
        {
            schemaVersion = 1,
            operation = "build-raw",
            phase = "raw_prepared",
            slot,
            raw
        };

        var json = JsonSerializer.Serialize(report, ReportJson);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        var resultPath = Path.Combine(output, "result.json");
        var resultCreated = false;
        var resultWritten = false;
        try
        {
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

    private static string ReadNonBlankString(JsonElement root, string name)
    {
        var element = root.GetProperty(name);
        var value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            throw new InvalidDataException($"{name} must be a non-blank string without NUL");
        return value;
    }

    // Tool provenance comes strictly from fixed trusted Linux installation paths, never request/env/output.
    private static async Task<VerifiedEdlFile> ClaimInstalledToolAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists ||
            info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.LinkTarget is not null)
            throw new InvalidDataException($"installed tool must be a regular non-symlink file: {path}");

        for (DirectoryInfo? current = info.Directory; current is not null; current = current.Parent)
        {
            if (!current.Exists ||
                current.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                current.LinkTarget is not null)
                throw new InvalidDataException($"installed tool ancestry must not contain symlinks: {path}");
        }

        if ((File.GetUnixFileMode(path) & ExecutableMask) == 0)
            throw new InvalidDataException($"installed tool is not executable: {path}");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            sha256.AppendData(buffer, 0, read);
            total += read;
            if (total > MaxRawBytesLimit)
                throw new InvalidDataException($"installed tool exceeds size limit: {path}");
        }
        if (total <= 0)
            throw new InvalidDataException($"installed tool must be non-empty: {path}");

        var hash = Convert.ToHexStringLower(sha256.GetHashAndReset());
        return new VerifiedEdlFile(path, total, hash);
    }
}
