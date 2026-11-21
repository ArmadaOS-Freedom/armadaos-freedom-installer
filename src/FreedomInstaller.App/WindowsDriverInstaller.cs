using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

internal enum DriverInstallOutcome
{
    Completed,
    Cancelled,
    RebootRequired,
    Failed,
}

internal sealed record DriverInstallAttempt(
    string Package,
    DateTimeOffset Started,
    DateTimeOffset Finished,
    DriverInstallOutcome Outcome,
    int? ExitCode,
    string? Error,
    string? SetupApiExcerpt);

internal record DriverInstallResult(DriverInstallOutcome Outcome, int? ExitCode, string? Error)
{
    internal bool EarlierPackageCompleted { get; init; }

    internal IReadOnlyList<DriverInstallAttempt> Attempts { get; init; } = Array.Empty<DriverInstallAttempt>();
}

internal readonly record struct DriverPackageSelection(bool InstallQualcomm, bool InstallAndroid);

internal static class WindowsDriverInstaller
{
    private const int ErrorCancelled = 1223;
    private const int RebootRequiredExitCode = 3010;

    /// <summary>
    /// 只做纯选择：任一组确实缺失 / 有问题时只处理指示的类别；
    /// 两组都就绪时允许用户主动「修复」两个随附驱动组。
    /// </summary>
    internal static DriverPackageSelection SelectPackages(bool qualcommNeedsInstall, bool androidNeedsInstall)
    {
        if (qualcommNeedsInstall || androidNeedsInstall)
            return new DriverPackageSelection(qualcommNeedsInstall, androidNeedsInstall);
        return new DriverPackageSelection(true, true);
    }

    internal static async Task<DriverInstallResult> InstallAsync(QualcommDriverFiles files)
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.OSArchitecture != Architecture.X64
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return Failure("Driver installation requires a native Windows x64 process.");
        }

        try
        {
            BundledQualcommDriver.ValidateFiles(files);
        }
        catch (Exception error)
        {
            return Failure($"Bundled driver package validation failed: {Describe(error)}");
        }

        return await InstallInfWithAttemptAsync(files.InfPath, "Qualcomm").ConfigureAwait(false);
    }

    internal static async Task<DriverInstallResult> InstallAndroidAsync(AndroidDriverFiles files)
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.OSArchitecture != Architecture.X64
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return Failure("Driver installation requires a native Windows x64 process.");
        try { BundledAndroidDriver.ValidateFiles(files); }
        catch (Exception error) { return Failure($"Bundled Android driver validation failed: {Describe(error)}"); }
        return await InstallAndroidSequenceAsync(
            files,
            path => InstallInfWithAttemptAsync(path, "Google"),
            path => InstallInfWithAttemptAsync(path, "OPlus"))
            .ConfigureAwait(false);
    }

    internal static async Task<DriverInstallResult> InstallAndroidSequenceAsync(
        AndroidDriverFiles files, Func<string, Task<DriverInstallResult>> install)
    {
        return await InstallAndroidSequenceAsync(files, install, install).ConfigureAwait(false);
    }

    private static async Task<DriverInstallResult> InstallAndroidSequenceAsync(
        AndroidDriverFiles files,
        Func<string, Task<DriverInstallResult>> installGoogle,
        Func<string, Task<DriverInstallResult>> installOplus)
    {
        var google = await installGoogle(files.GoogleInfPath).ConfigureAwait(false);
        if (google.Outcome != DriverInstallOutcome.Completed) return google;
        var oplus = await installOplus(files.OplusInfPath).ConfigureAwait(false);
        // 只有 Google 明确 Completed 后才走到这里：把「前一个包已完成」标记带到第二步结果上，
        // 供调用方在后续步骤被取消时决定是否需要重新自检。
        return oplus with
        {
            EarlierPackageCompleted = true,
            Attempts = CombineAttempts(google.Attempts, oplus.Attempts),
        };
    }

    private static IReadOnlyList<DriverInstallAttempt> CombineAttempts(
        IReadOnlyList<DriverInstallAttempt> first,
        IReadOnlyList<DriverInstallAttempt> second)
    {
        if (first.Count == 0) return second;
        if (second.Count == 0) return first;
        var combined = new DriverInstallAttempt[first.Count + second.Count];
        for (int i = 0; i < first.Count; i++) combined[i] = first[i];
        for (int i = 0; i < second.Count; i++) combined[first.Count + i] = second[i];
        return combined;
    }

    private static async Task<DriverInstallResult> InstallInfWithAttemptAsync(string infPath, string package)
    {
        var started = DateTimeOffset.Now;
        var result = await InstallInfAsync(infPath).ConfigureAwait(false);
        var finished = DateTimeOffset.Now;

        string? excerpt = null;
        try
        {
            excerpt = SanitizeExcerpt(DriverSetupApiExcerpt.ReadForInstall(infPath, started, finished));
        }
        catch (Exception)
        {
            // Best-effort context capture: diagnostic lookups must never change the install result.
            excerpt = null;
        }

        var attempt = new DriverInstallAttempt(
            package,
            started,
            finished,
            result.Outcome,
            result.ExitCode,
            result.Error,
            excerpt);

        return result with { Attempts = new[] { attempt } };
    }

    private static string? SanitizeExcerpt(string? excerpt)
    {
        if (string.IsNullOrWhiteSpace(excerpt)) return null;
        const int MaxLength = 4096;
        return excerpt.Length <= MaxLength ? excerpt : excerpt.Substring(0, MaxLength);
    }

    private static async Task<DriverInstallResult> InstallInfAsync(string infPath)
    {
        // SpecialFolder.System maps to the real System32 only because the guard above
        // already rejected emulated x86 / ARM64 processes.
        string pnputilPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "pnputil.exe");

        if (string.IsNullOrWhiteSpace(pnputilPath)
            || !Path.IsPathFullyQualified(pnputilPath)
            || !File.Exists(pnputilPath))
        {
            return Failure("System pnputil.exe could not be located.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pnputilPath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("/add-driver");
        startInfo.ArgumentList.Add(infPath);
        startInfo.ArgumentList.Add("/install");

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return Failure("pnputil.exe could not be started.");
            }

            await process.WaitForExitAsync().ConfigureAwait(false);

            int exitCode = process.ExitCode;
            return InterpretExitCode(exitCode);
        }
        catch (Exception error)
        {
            return InterpretStartFailure(error);
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static DriverInstallResult InterpretExitCode(int exitCode)
    {
        switch (exitCode)
        {
            case 0:
                return new DriverInstallResult(DriverInstallOutcome.Completed, 0, null);
            case RebootRequiredExitCode:
                return new DriverInstallResult(DriverInstallOutcome.RebootRequired, RebootRequiredExitCode, null);
            default:
                return new DriverInstallResult(
                    DriverInstallOutcome.Failed,
                    exitCode,
                    $"pnputil.exe exited with code {exitCode}.");
        }
    }

    internal static DriverInstallResult InterpretStartFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error is Win32Exception win32 && win32.NativeErrorCode == ErrorCancelled)
        {
            return new DriverInstallResult(DriverInstallOutcome.Cancelled, null, null);
        }

        return new DriverInstallResult(DriverInstallOutcome.Failed, null, Describe(error));
    }

    private static DriverInstallResult Failure(string error)
    {
        return new DriverInstallResult(DriverInstallOutcome.Failed, null, error);
    }

    private static string Describe(Exception error)
    {
        return string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
    }
}
