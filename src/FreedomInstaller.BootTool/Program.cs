using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreedomInstaller.Core;

internal static partial class Program
{
    private const int MaxRequestBytes = 65536;
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var resultCreated = false;
        var resultWritten = false;
        var resultPath = "";
        try
        {
            if (args.Length != 3 || args[0] is not ("inspect-slot" or "repack-envelope" or "build-raw" or "inspect-trust"))
                throw new UsageException("usage: armada-boot-tool <inspect-slot|repack-envelope|build-raw|inspect-trust> <request.json> <output-directory>");

            var operation = args[0];
            using var request = await ReadRequestAsync(args[1], cancellation.Token);
            var root = request.RootElement;
            if (operation == "build-raw")
                return await RunRawAsync(root, args[2], cancellation.Token);
            if (operation == "inspect-trust")
                return await RunTrustAsync(root, args[2], cancellation.Token);
            var inspect = operation == "inspect-slot";
            RequireFields(root, inspect
                ? ["schemaVersion", "slot", "files"]
                : ["schemaVersion", "slot", "files", "mode", "maxRawBytes"]);
            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                !root.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1)
                throw new InvalidDataException("schemaVersion must be numeric 1");
            var slotElement = root.GetProperty("slot");
            var slot = slotElement.ValueKind == JsonValueKind.String ? slotElement.GetString() : null;
            if (slot is not ("a" or "b")) throw new InvalidDataException("slot must be 'a' or 'b'");

            var files = root.GetProperty("files");
            RequireFields(files, inspect
                ? ["boot", "vendorBoot", "initBoot", "abl", "xblConfig"]
                : ["boot", "vendorBoot", "initBoot", "abl", "xblConfig", "vbmeta", "packedRaw"]);
            var boot = Claim(files, "boot");
            var vendorBoot = Claim(files, "vendorBoot");
            var initBoot = Claim(files, "initBoot");
            var abl = Claim(files, "abl");
            var xblConfig = Claim(files, "xblConfig");
            VerifiedEdlFile? vbmeta = null, packedRaw = null;
            var maxRawBytes = 0L;
            var mode = SystemUpdateEnvelopeMode.AospResign;
            if (!inspect)
            {
                vbmeta = Claim(files, "vbmeta");
                packedRaw = Claim(files, "packedRaw");
                var max = root.GetProperty("maxRawBytes");
                if (max.ValueKind != JsonValueKind.Number || !max.TryGetInt64(out maxRawBytes) || maxRawBytes <= 0)
                    throw new InvalidDataException("maxRawBytes must be a positive int64");
                var modeElement = root.GetProperty("mode");
                if (modeElement.ValueKind != JsonValueKind.String) throw new InvalidDataException("mode must be a string");
                mode = modeElement.GetString() switch
                {
                    "aosp-resign" => SystemUpdateEnvelopeMode.AospResign,
                    "gbl-preserve-metadata" => SystemUpdateEnvelopeMode.GblPreserveMetadata,
                    _ => throw new InvalidDataException("invalid mode")
                };
            }

            var output = ValidateOutput(args[2]);
            cancellation.Token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsLinux())
                Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else
                Directory.CreateDirectory(output);
            resultPath = Path.Combine(output, "result.json");
            object report;
            if (inspect)
            {
                var basePath = Path.Combine(output, "base.bin");
                var material = await InternalSlotMaterialReader.AnalyzeAndExportBaseAsync(
                    slot, boot, vendorBoot, initBoot, abl, xblConfig, basePath, cancellation.Token);
                if (!material.IsKshim)
                {
                    File.Delete(basePath);
                    throw new InvalidDataException("slot material is not kshim; stock repair is separate");
                }
                report = new
                {
                    schemaVersion = 1, operation, phase = "slot_inspected", slot,
                    baseImage = new { path = basePath, length = material.BaseImageLength, sha256 = material.BaseImageSha256 },
                    material
                };
            }
            else
            {
                var candidatePath = Path.Combine(output, "candidate.img");
                var envelope = await EdlRawBootBuilder.PrepareSystemUpdateEnvelopeAsync(
                    slot, boot, vendorBoot, initBoot, abl, xblConfig, vbmeta!, packedRaw!, maxRawBytes,
                    candidatePath, mode, cancellation.Token);
                report = new { schemaVersion = 1, operation, phase = "envelope_prepared", slot, envelope };
            }

            var json = JsonSerializer.Serialize(report, ReportJson);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            using (var stream = new FileStream(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resultCreated = true;
                await stream.WriteAsync(bytes, cancellation.Token);
                stream.Flush(true);
            }
            resultWritten = true;
            await Console.Out.WriteLineAsync(json.AsMemory(), cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            if (resultCreated && !resultWritten) TryDelete(resultPath);
            return await ErrorAsync("operation cancelled", 130);
        }
        catch (UsageException e)
        {
            return await ErrorAsync(e.Message, 2);
        }
        catch (Exception e)
        {
            if (resultCreated && !resultWritten) TryDelete(resultPath);
            return await ErrorAsync(e.Message, 1);
        }
    }

    private static async Task<JsonDocument> ReadRequestAsync(string path, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("request path must be fully qualified");
        if (path.Contains('\0')) throw new InvalidDataException("request path must not contain NUL");
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            throw new InvalidDataException("request must be a regular non-reparse file");
        if (info.Length > MaxRequestBytes) throw new InvalidDataException("request exceeds 65536 bytes");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            memory.Write(buffer, 0, read);
            if (memory.Length > MaxRequestBytes) throw new InvalidDataException("request exceeds 65536 bytes");
        }
        var text = new UTF8Encoding(false, true).GetString(memory.ToArray());
        var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("request root must be an object");
        }
        return document;
    }

    private static void RequireFields(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("expected an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name)) throw new InvalidDataException($"duplicate field: {property.Name}");
        if (seen.Count != fields.Length || fields.Any(field => !seen.Contains(field)))
            throw new InvalidDataException("missing or unknown fields");
    }

    private static VerifiedEdlFile Claim(JsonElement files, string name)
    {
        var claim = files.GetProperty(name);
        RequireFields(claim, ["path", "length", "sha256"]);
        var pathElement = claim.GetProperty("path");
        var path = pathElement.ValueKind == JsonValueKind.String ? pathElement.GetString() : null;
        if (path is null || !Path.IsPathFullyQualified(path)) throw new InvalidDataException($"{name}.path must be a fully qualified string");
        if (path.Contains('\0')) throw new InvalidDataException($"{name}.path must not contain NUL");
        var lengthElement = claim.GetProperty("length");
        if (lengthElement.ValueKind != JsonValueKind.Number || !lengthElement.TryGetInt64(out var length) || length <= 0)
            throw new InvalidDataException($"{name}.length must be a positive int64");
        var hashElement = claim.GetProperty("sha256");
        var hash = hashElement.ValueKind == JsonValueKind.String ? hashElement.GetString() : null;
        if (hash is null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException($"{name}.sha256 must be 64 hexadecimal characters");
        return new VerifiedEdlFile(path, length, hash);
    }

    private static string ValidateOutput(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("output path must be fully qualified");
        var output = Path.GetFullPath(path);
        if (output.Contains('\0')) throw new InvalidDataException("output path must not contain NUL");
        if (File.Exists(output) || Directory.Exists(output) || new FileInfo(output).LinkTarget is not null)
            throw new InvalidDataException("output directory must not exist");
        var parent = new DirectoryInfo(Path.GetDirectoryName(output) ?? throw new InvalidDataException("output parent is required"));
        if (!parent.Exists || parent.Attributes.HasFlag(FileAttributes.ReparsePoint) || parent.LinkTarget is not null)
            throw new InvalidDataException("output parent must exist and be non-reparse");
        for (DirectoryInfo? current = parent; current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes.HasFlag(FileAttributes.ReparsePoint) || current.LinkTarget is not null))
                throw new InvalidDataException("output ancestry must not contain reparse points or symlinks");
        return output;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private static async Task<int> ErrorAsync(string message, int code)
    {
        await Console.Error.WriteLineAsync(JsonSerializer.Serialize(new { error = message }));
        return code;
    }

    private sealed class UsageException(string message) : Exception(message);
}
