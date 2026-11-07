using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreedomInstaller.Core;

public static partial class EdlRawBootBuilder
{
    private static string CleanNativeDiagnostic(string text, int limit)
    {
        string clean = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        return clean.Length > limit ? clean[..limit] : clean;
    }

    private static ProcessCommand CreateNativeCommand(
        string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return new ProcessCommand(executable, arguments, NativeCommandTimeout) { WorkingDirectory = workingDirectory };

        // CreateProcess still limits its cwd. Both tools accept absolute paths, so
        // keep the native process at the volume/share root and leave all files in place.
        string fullDirectory = Path.GetFullPath(workingDirectory);
        string[] absoluteArguments = arguments.Select(path => Path.GetFullPath(path, fullDirectory)).ToArray();
        return new ProcessCommand(Path.GetFullPath(executable), absoluteArguments, NativeCommandTimeout)
        {
            WorkingDirectory = Path.GetPathRoot(fullDirectory),
        };
    }

    private static string? TrySaveNativeFailure(
        ProcessCommand command, string workingDirectory, string subject,
        ProcessResult? result, Exception? exception)
    {
        try
        {
            DirectoryInfo? owner = new(workingDirectory);
            while (owner is not null && !owner.Name.StartsWith("edl-boot-", StringComparison.Ordinal))
                owner = owner.Parent;
            if (owner?.Parent is null) return null;
            if ((owner.Attributes & FileAttributes.ReparsePoint) != 0) return null;

            var files = new List<object>();
            var configs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (FileInfo file in new DirectoryInfo(workingDirectory).EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                files.Add(new { name = file.Name, bytes = file.Length, pathCharacters = file.FullName.Length });
                if (file.Extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) && file.Length <= 65536)
                    configs.Add(file.Name, File.ReadAllText(file.FullName));
            }
            string path = Path.Combine(owner.Parent.FullName, "native-failure-" + owner.Name + "-" + Guid.NewGuid().ToString("N") + ".json");
            var receipt = new
            {
                schemaVersion = 1,
                subject,
                executable = command.Executable,
                arguments = command.Arguments,
                workingDirectory,
                workingDirectoryCharacters = workingDirectory.Length,
                processWorkingDirectory = command.WorkingDirectory,
                exitCode = result?.ExitCode,
                stdout = CleanNativeDiagnostic(result?.StandardOutput ?? "", 65536),
                stderr = CleanNativeDiagnostic(result?.StandardError ?? "", 65536),
                exception = exception?.ToString(),
                files,
                configs,
            };
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(output, receipt, new JsonSerializerOptions { WriteIndented = true });
            output.Flush(true);
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Reporting failure must not replace the original native tool error.
            return null;
        }
    }
}