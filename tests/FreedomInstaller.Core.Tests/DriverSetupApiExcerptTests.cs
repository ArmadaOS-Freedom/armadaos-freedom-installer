using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class DriverSetupApiExcerptTests : IDisposable
{
    private readonly string _root;

    public DriverSetupApiExcerptTests()
    {
        string? live = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT");
        string baseDir = string.IsNullOrWhiteSpace(live)
            ? Path.Combine(Path.GetTempPath(), "freedominstaller-tests")
            : live!;
        _root = Path.Combine(baseDir, "drvsetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string GoogleLogPath  = @"C:\Users\fixture\cache\google\android_winusb.inf";
    private const string OplusLogPath   = @"C:\Users\fixture\cache\oplus\android_winusb.inf";

    private static readonly DateTimeOffset Started  = new(2026, 11, 21, 20, 14, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Finished = new(2026, 11, 21, 20, 14, 30, TimeSpan.Zero);

    private static readonly string GoogleSection =
        ">>>  [Driver Install (DrvSetupInstallDriver) - " + GoogleLogPath + "]\n" +
        ">>>  Section start 2026/11/21 20:14:02.499\n" +
        "     dvs:           Outcome        - Already Imported\n" +
        "     dvs:           Driver INF     - android_winusb.inf (oem56.inf)\n" +
        "!    sig:                     Error 0x800b0109: secret\n" +
        "!    dvs: Unable to find any matching devices.\n" +
        "<<<  Section end 2026/11/21 20:14:02.523\n" +
        "<<<  [Exit status: SUCCESS]\n";

    private static readonly string OplusSection =
        ">>>  [Driver Install (DrvSetupInstallDriver) - " + OplusLogPath + "]\n" +
        ">>>  Section start 2026/11/21 20:14:04.100\n" +
        "     dvs:           Outcome        - Already Imported\n" +
        "     dvs:           Driver INF     - android_winusb.inf (oem57.inf)\n" +
        "<<<  Section end 2026/11/21 20:14:04.120\n" +
        "<<<  [Exit status: SUCCESS]\n";

    private static readonly string FailureSection =
        ">>>  [Driver Install (DrvSetupInstallDriver) - " + GoogleLogPath + "]\n" +
        ">>>  Section start 2026/11/21 20:14:02.499\n" +
        "!    dvs:                     Error 0xE0000242: driver store corrupt\n" +
        "<<<  Section end 2026/11/21 20:14:02.523\n" +
        "<<<  [Exit status: FAILURE(0xE0000242)]\n";

    private static readonly string IncompleteSection =
        ">>>  [Driver Install (DrvSetupInstallDriver) - " + GoogleLogPath + "]\n" +
        ">>>  Section start 2026/11/21 20:14:02.499\n" +
        "     dvs:           Outcome        - Already Imported\n" +
        "     dvs:           Driver INF     - android_winusb.inf (oem56.inf)\n" +
        "<<<  Section end 2026/11/21 20:14:02.523\n";

    private static string? Extract(string text, string infPath, DateTimeOffset started, DateTimeOffset finished)
        => DriverSetupApiExcerpt.Extract(new StringReader(text), infPath, started, finished);

    [Fact]
    public void Google_entry_within_window_yields_numeric_code_oem_and_result()
    {
        string? excerpt = Extract(GoogleSection, GoogleLogPath, Started, Finished);
        Assert.NotNull(excerpt);

        Assert.Contains("0x800B0109", excerpt);
        Assert.Contains("oem56", excerpt);
        Assert.Contains("SUCCESS", excerpt);
        Assert.DoesNotContain("fixture", excerpt);
        Assert.DoesNotContain("Users", excerpt);
        Assert.DoesNotContain("secret", excerpt);
    }

    [Fact]
    public void Oplus_same_basename_is_distinguished_by_infPath_and_log_path()
    {
        string combined = GoogleSection + OplusSection;

        string? google = Extract(combined, GoogleLogPath, Started, Finished);
        Assert.NotNull(google);
        Assert.Contains("oem56", google);
        Assert.DoesNotContain("oem57", google);

        string? oplus = Extract(combined, OplusLogPath, Started, Finished);
        Assert.NotNull(oplus);
        Assert.Contains("oem57", oplus);
        Assert.DoesNotContain("oem56", oplus);
    }

    [Fact]
    public void Entry_outside_time_window_is_skipped()
    {
        DateTimeOffset lateStart = new(2026, 11, 21, 21, 0, 0, TimeSpan.Zero);
        DateTimeOffset lateEnd   = new(2026, 11, 21, 21, 5, 0, TimeSpan.Zero);

        string? excerpt = Extract(GoogleSection, GoogleLogPath, lateStart, lateEnd);
        Assert.Null(excerpt);
    }

    [Fact]
    public void New_most_recent_entry_is_selected_when_multiple_in_window()
    {
        string older =
            ">>>  [Driver Install (DrvSetupInstallDriver) - " + GoogleLogPath + "]\n" +
            ">>>  Section start 2026/11/21 20:10:00.000\n" +
            "     dvs:           Driver INF     - android_winusb.inf (oem55.inf)\n" +
            "<<<  Section end 2026/11/21 20:10:00.010\n" +
            "<<<  [Exit status: SUCCESS]\n";

        DateTimeOffset wideStart  = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
        DateTimeOffset wideFinish = new(2026, 11, 21, 20, 30, 0, TimeSpan.Zero);

        string? excerpt = Extract(older + GoogleSection, GoogleLogPath, wideStart, wideFinish);
        Assert.NotNull(excerpt);
        Assert.Contains("oem56", excerpt);
        Assert.DoesNotContain("oem55", excerpt);
    }

    [Fact]
    public void Failure_excerpt_without_outcome_or_oem_is_preserved()
    {
        string? excerpt = Extract(FailureSection, GoogleLogPath, Started, Finished);
        Assert.NotNull(excerpt);

        Assert.Contains("FAILURE(0xE0000242)", excerpt);
        Assert.Contains("0xE0000242", excerpt);
        Assert.DoesNotContain("Driver INF", excerpt);
        Assert.DoesNotContain("Outcome", excerpt);
        Assert.DoesNotContain("Users", excerpt);
    }

    [Fact]
    public void Incomplete_section_without_exit_status_is_rejected()
    {
        string? excerpt = Extract(IncompleteSection, GoogleLogPath, Started, Finished);
        Assert.Null(excerpt);
    }

    [Fact]
    public void Missing_inf_path_returns_null()
    {
        string? excerpt = Extract(GoogleSection, string.Empty, Started, Finished);
        Assert.Null(excerpt);
    }

    [Fact]
    public void Wrong_full_inf_path_returns_null()
    {
        string? excerpt = Extract(GoogleSection, @"C:\Windows\INF\oem99.inf", Started, Finished);
        Assert.Null(excerpt);
    }

    [Fact]
    public void Basename_only_inf_path_returns_null()
    {
        string? excerpt = Extract(GoogleSection, "android_winusb.inf", Started, Finished);
        Assert.Null(excerpt);
    }

    [Fact]
    public async Task Export_carries_excerpt_via_host_environment_and_zip_environment_json()
    {
        string? excerpt = Extract(GoogleSection, GoogleLogPath, Started, Finished);
        Assert.False(string.IsNullOrWhiteSpace(excerpt));

        string secretSerial = "AB12CD&0&3";
        string secretUser   = "fixture";
        string envelope = "DRIVER-SETUP-API-EXCERPT\n" + excerpt +
            "user=" + secretUser + " serial=" + secretSerial + "\n";

        string dest = Path.Combine(_root, "bundle.zip");
        var hostEnv = new Dictionary<string, string?>
        {
            ["OS"] = "Windows 11",
            ["driver.install.attempt.0.setupApiExcerpt"] = envelope,
        };

        var req = new DiagnosticExportRequest(
            "driver setup excerpt test",
            "driver:setup\n",
            new[] { secretSerial, secretUser },
            Array.Empty<DiagnosticBootFile>(),
            false,
            false)
        {
            HostEnvironment = hostEnv,
        };

        await DiagnosticBundle.ExportAsync(dest, req);

        using var fs = File.OpenRead(dest);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        ZipArchiveEntry? entry = zip.GetEntry("environment.json");
        Assert.NotNull(entry);
        using var stream = entry!.Open();
        using var doc = JsonDocument.Parse(stream);

        JsonElement root = doc.RootElement;
        Assert.Equal("Windows 11", root.GetProperty("OS").GetString());
        string carried = root.GetProperty("driver.install.attempt.0.setupApiExcerpt").GetString()!;
        Assert.Contains("DRIVER-SETUP-API-EXCERPT", carried);
        Assert.Contains("0x800B0109", carried);
        Assert.Contains("oem56", carried);
        Assert.Contains("SUCCESS", carried);
        Assert.DoesNotContain(secretUser, carried);
        Assert.DoesNotContain(secretSerial, carried);
        Assert.DoesNotContain("Users", carried);
    }

}
