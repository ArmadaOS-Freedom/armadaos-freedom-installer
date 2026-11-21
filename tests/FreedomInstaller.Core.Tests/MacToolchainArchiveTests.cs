using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Loads a real macOS EDL toolchain archive (built with package_edl.py --platform macos-arm64), including its
/// Python runtime. Skipped unless
/// EDL_TOOLCHAIN_MACOS_TEST_BUNDLE_PATH names one; the client is only started on a macOS host.
/// </summary>
public sealed class MacToolchainArchiveTests
{
    [Fact]
    public async Task Real_macos_archive_stages_an_executable_client_and_python()
    {
        string? bundle = Environment.GetEnvironmentVariable("EDL_TOOLCHAIN_MACOS_TEST_BUNDLE_PATH");
        string? artifacts = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(bundle) || string.IsNullOrWhiteSpace(artifacts))
        {
            return;
        }

        string workRoot = Path.Combine(artifacts, "mac-toolchain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);

        await using FileStream archive = File.OpenRead(bundle);
        using EdlToolchain tools = await EdlToolchain.LoadAsync(archive, workRoot);

        Assert.EndsWith(Path.Combine("tools", "edl-ng"), tools.ClientPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("tools", "libusb-1.0.dylib"), tools.UsbLibraryPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("runtime", "python", "bin", "python3.14"), tools.PythonPath, StringComparison.Ordinal);
        Assert.Equal(new[] { "-E", "-s" }, tools.PythonOptions);
        Assert.True(File.Exists(tools.FirehosePath));

        if (OperatingSystem.IsMacOS())
        {
            Assert.True(File.GetUnixFileMode(tools.ClientPath).HasFlag(UnixFileMode.UserExecute));
            using Process process = Process.Start(new ProcessStartInfo(tools.ClientPath, "--help")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("edl-ng", output, StringComparison.Ordinal);

            // The bridges import their sibling modules from the script directory; the runtime must find them.
            var python = new ProcessStartInfo(tools.PythonPath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (string option in tools.PythonOptions)
                python.ArgumentList.Add(option);
            python.ArgumentList.Add("-B");
            python.ArgumentList.Add("-c");
            python.ArgumentList.Add(
                "import sys; sys.path.insert(0, sys.argv[1]); import portable_gpt, avb_stock; print(sys.version_info[:2])");
            python.ArgumentList.Add(Path.GetDirectoryName(tools.GptCliPath)!);
            using Process runtime = Process.Start(python)!;
            string version = await runtime.StandardOutput.ReadToEndAsync();
            await runtime.WaitForExitAsync();
            Assert.Equal(0, runtime.ExitCode);
            Assert.Equal("(3, 14)", version.Trim());
        }
    }
}
