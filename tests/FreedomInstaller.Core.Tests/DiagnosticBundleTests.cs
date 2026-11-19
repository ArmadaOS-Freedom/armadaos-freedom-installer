using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class DiagnosticBundleTests : IDisposable
{
    private readonly string _root;

    public DiagnosticBundleTests()
    {
        string? live = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT");
        string baseDir = string.IsNullOrWhiteSpace(live)
            ? Path.Combine(Path.GetTempPath(), "freedominstaller-tests")
            : live!;
        _root = Path.Combine(baseDir, "diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private (string path, long len, string sha) WriteFixture(string rel, string content)
    {
        string p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(p, bytes);
        return (p, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static Dictionary<string, byte[]> ReadZip(string path)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            result[e.FullName] = ms.ToArray();
        }
        return result;
    }

    [Fact]
    public async Task First_install_early_failure_offline_export_exposes_log_evidence()
    {
        string log = "phase:early-install\nstatus:OFFLINE\nreason:no-device-touch\n";
        string dest = Path.Combine(_root, "bundle.zip");
        var req = new DiagnosticExportRequest("early failure", log, Array.Empty<string>(), Array.Empty<DiagnosticBootFile>(), false, false);
        await DiagnosticBundle.ExportAsync(dest, req);

        var zip = ReadZip(dest);
        Assert.Contains("installer.log", zip.Keys);
        Assert.Contains("summary.txt", zip.Keys);
        Assert.Contains("manifest.json", zip.Keys);
        Assert.Contains("OFFLINE", Encoding.UTF8.GetString(zip["installer.log"]));
        Assert.Contains("schema", Encoding.UTF8.GetString(zip["manifest.json"]));
    }

    [Fact]
    public async Task Host_environment_written_redacted_with_official_serial_and_usb_id()
    {
        string secret = "SECRET-LITERAL-42";
        string envValue = "value with \"quotes\" and\nnewline secret=" + secret;
        string logLine = "driver-check path=C:\\tools\\edl.exe id=USB\\VID_05C6&PID_9008\\AB12CD&0&3 serial=" + secret;
        string expectedUsb = "USB\\VID_05C6&PID_9008\\[redacted]";

        var hostEnv = new Dictionary<string, string?>
        {
            ["OS"] = "Windows 11",
            ["NOTE"] = envValue,
            ["MISSING"] = null,
            ["USB_DEVICE"] = "USB\\VID_05C6&PID_9008\\6&AB12&0&3",
        };

        string dest = Path.Combine(_root, "bundle.zip");
        var req = new DiagnosticExportRequest("env", logLine, new[] { secret }, Array.Empty<DiagnosticBootFile>(), false, false)
        {
            HostEnvironment = hostEnv,
        };
        await DiagnosticBundle.ExportAsync(dest, req);

        var zip = ReadZip(dest);
        Assert.Contains("environment.json", zip.Keys);
        using (var doc = JsonDocument.Parse(zip["environment.json"]))
        {
            JsonElement root = doc.RootElement;
            Assert.Equal("Windows 11", root.GetProperty("OS").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("MISSING").ValueKind);
            string note = root.GetProperty("NOTE").GetString()!;
            Assert.Contains("value with \"quotes\" and\nnewline", note);
            Assert.DoesNotContain(secret, note);
            Assert.Equal(expectedUsb, root.GetProperty("USB_DEVICE").GetString());
        }

        string logText = Encoding.UTF8.GetString(zip["installer.log"]);
        Assert.Contains("driver-check path=", logText);
        Assert.Contains(expectedUsb, logText);
        Assert.DoesNotContain(secret, logText);
        Assert.Equal(4, zip.Count);
    }

    [Fact]
    public async Task Stuck_9008_status_from_fixture_exported_without_device()
    {
        string summary = "device:stuck-9008\nstatus:waiting-sahara\n";
        string log = "usb:9008-detected\n";
        string dest = Path.Combine(_root, "bundle.zip");
        await DiagnosticBundle.ExportAsync(dest, new DiagnosticExportRequest(summary, log, Array.Empty<string>(), Array.Empty<DiagnosticBootFile>(), false, false));

        var zip = ReadZip(dest);
        Assert.Contains("stuck-9008", Encoding.UTF8.GetString(zip["summary.txt"]));
        Assert.Contains("9008-detected", Encoding.UTF8.GetString(zip["installer.log"]));
    }

    [Fact]
    public async Task Captured_abl_a_and_b_preserved_in_content_and_sha_no_boot()
    {
        var a = WriteFixture("cap/abl_a.bin", "ABL-A-PAYLOAD");
        var b = WriteFixture("cap/abl_b.bin", "ABL-B-PAYLOAD");
        var bootA = WriteFixture("cap/boot_a.bin", "BOOT-A-PAYLOAD");
        string dest = Path.Combine(_root, "bundle.zip");

        var files = new List<DiagnosticBootFile>
        {
            new("captured", "abl_a", a.path, a.len, a.sha),
            new("captured", "abl_b", b.path, b.len, b.sha),
            new("captured", "boot_a", bootA.path, bootA.len, bootA.sha),
        };

        // First pass: unchecked (not included) — boot_a must be excluded, ABL preserved.
        await DiagnosticBundle.ExportAsync(dest, new DiagnosticExportRequest("x", "y", Array.Empty<string>(), files, IncludeAbl: true, IncludeLargeBoot: false));

        var zip = ReadZip(dest);
        Assert.Equal("ABL-A-PAYLOAD", Encoding.UTF8.GetString(zip["boot/captured/abl_a.bin"]));
        Assert.Equal("ABL-B-PAYLOAD", Encoding.UTF8.GetString(zip["boot/captured/abl_b.bin"]));
        Assert.DoesNotContain(zip.Keys, k => k.Contains("boot_a", StringComparison.Ordinal));

        string manifest = Encoding.UTF8.GetString(zip["manifest.json"]);
        using (var doc = JsonDocument.Parse(manifest))
        {
            var copied = doc.RootElement.GetProperty("copiedFiles");
            Assert.Equal(2, copied.GetArrayLength());
            foreach (var rec in copied.EnumerateArray())
            {
                Assert.Equal("copied", rec.GetProperty("status").GetString());
                Assert.Equal(rec.GetProperty("expectedSha256").GetString(), rec.GetProperty("computedSha256").GetString());
                // Assert source path absence via structured fields, not raw substring on archivePath.
                Assert.False(rec.TryGetProperty("path", out _), "manifest must not serialize source path");
            }
        }

        // Second pass: checked (included and bytes verified) — boot_a now present with exact bytes.
        File.Delete(dest);
        await DiagnosticBundle.ExportAsync(dest, new DiagnosticExportRequest("x", "y", Array.Empty<string>(), files, IncludeAbl: true, IncludeLargeBoot: true));

        var zip2 = ReadZip(dest);
        Assert.Equal("ABL-A-PAYLOAD", Encoding.UTF8.GetString(zip2["boot/captured/abl_a.bin"]));
        Assert.Equal("ABL-B-PAYLOAD", Encoding.UTF8.GetString(zip2["boot/captured/abl_b.bin"]));
        Assert.Equal("BOOT-A-PAYLOAD", Encoding.UTF8.GetString(zip2["boot/captured/boot_a.bin"]));

        using (var doc2 = JsonDocument.Parse(Encoding.UTF8.GetString(zip2["manifest.json"])))
        {
            var copied2 = doc2.RootElement.GetProperty("copiedFiles");
            Assert.Equal(3, copied2.GetArrayLength());
            var bootRec = copied2.EnumerateArray().Single(e => e.GetProperty("role").GetString() == "boot_a");
            Assert.Equal("copied", bootRec.GetProperty("status").GetString());
            Assert.Equal(bootA.sha, bootRec.GetProperty("computedSha256").GetString());
        }
    }

    [Fact]
    public async Task Stale_file_after_unplug_still_reads_and_missing_file_explicitly_marked()
    {
        var stale = WriteFixture("cap/abl_a.bin", "STALE-BUT-LOCAL");
        string dest = Path.Combine(_root, "bundle.zip");
        string missingPath = Path.Combine(_root, "cap", "abl_b.bin");

        var files = new List<DiagnosticBootFile>
        {
            new("captured", "abl_a", stale.path, stale.len, stale.sha),
            new("captured", "abl_b", missingPath, 10, null),
        };
        await DiagnosticBundle.ExportAsync(dest, new DiagnosticExportRequest("s", "l", Array.Empty<string>(), files, true, false));

        var zip = ReadZip(dest);
        Assert.Equal("STALE-BUT-LOCAL", Encoding.UTF8.GetString(zip["boot/captured/abl_a.bin"]));
        Assert.DoesNotContain(zip.Keys, k => k.EndsWith("abl_b.bin", StringComparison.Ordinal));

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(zip["manifest.json"]));
        var byRole = doc.RootElement.GetProperty("copiedFiles").EnumerateArray()
            .ToDictionary(e => e.GetProperty("role").GetString()!, e => e.GetProperty("status").GetString());
        Assert.Equal("copied", byRole["abl_a"]);
        Assert.Equal("missing", byRole["abl_b"]);
    }

    [Fact]
    public async Task Last_checked_device_serial_exposed_explicitly_while_incidental_and_invalid_omitted()
    {
        string intended = "R58M1234ABC";
        string incidental = "ZX98INCIDENTAL77";
        string invalidMultiline = "aB12\nmalicious=true";

        string summary = "requested-device-seen\n";
        string log = "check-target\nincidental=" + incidental + "\n";
        string dest = Path.Combine(_root, "bundle.zip");

        var req = new DiagnosticExportRequest(
            summary,
            log,
            new[] { incidental },
            Array.Empty<DiagnosticBootFile>(),
            false,
            false)
        {
            LastCheckedDeviceSerial = intended,
        };
        await DiagnosticBundle.ExportAsync(dest, req);

        var zip = ReadZip(dest);
        string logText = Encoding.UTF8.GetString(zip["installer.log"]);
        string summaryText = Encoding.UTF8.GetString(zip["summary.txt"]);

        Assert.Contains("LastCheckedDeviceSerial=" + intended, logText);
        Assert.Contains("LastCheckedDeviceSerial=" + intended, summaryText);
        Assert.DoesNotContain(incidental, logText, StringComparison.Ordinal);
        Assert.DoesNotContain("malicious=true", logText, StringComparison.Ordinal);

        // 仅一次：该显式字段行在每份文本中各出现一次。
        Assert.Equal(1, CountOccurrences(logText, "LastCheckedDeviceSerial="));
        Assert.Equal(1, CountOccurrences(summaryText, "LastCheckedDeviceSerial="));

        // 非法（多行/控制字符）序列号被忽略：不写入 LastCheckedDeviceSerial 行。
        File.Delete(dest);
        var invalidReq = new DiagnosticExportRequest(
            "s\n", "l\n",
            Array.Empty<string>(),
            Array.Empty<DiagnosticBootFile>(),
            false, false)
        {
            LastCheckedDeviceSerial = invalidMultiline,
        };
        await DiagnosticBundle.ExportAsync(dest, invalidReq);
        var zip2 = ReadZip(dest);
        string logText2 = Encoding.UTF8.GetString(zip2["installer.log"]);
        Assert.DoesNotContain("LastCheckedDeviceSerial=", logText2, StringComparison.Ordinal);
        Assert.DoesNotContain("malicious=true", logText2, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Theory]
    [InlineData("log C:\\Users\\alice\\AppData\\secret.txt during boot", "alice")]
    [InlineData("path=/home/bob/data/x failed", "bob")]
    [InlineData("serial number: ABCD1234EF", "ABCD1234EF")]
    [InlineData("Authorization: Bearer eyJ.abc.def", "eyJ.abc.def")]
    [InlineData("token=topsecretvalue&x=1", "topsecretvalue")]
    [InlineData("password: hunter2", "hunter2")]
    public void Sanitize_redacts_paths_serials_and_auth(string text, string mustNotContain)
    {
        string result = DiagnosticBundle.Sanitize(text, Array.Empty<string>());
        Assert.DoesNotContain(mustNotContain, result, StringComparison.Ordinal);
        Assert.Contains("[redacted]", result);
    }

    [Fact]
    public void Sanitize_redacts_quoted_and_bare_credential_values_preserving_syntax()
    {
        string json = "{\"token\":\"long value with space\",\"ok\":true}";
        string jsonResult = DiagnosticBundle.Sanitize(json, Array.Empty<string>());
        Assert.DoesNotContain("long value with space", jsonResult, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(jsonResult))
        {
            Assert.Equal("[redacted]", doc.RootElement.GetProperty("token").GetString());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        string escapedQuote = "password=\"a\\\"b c\" tail";
        string escapedResult = DiagnosticBundle.Sanitize(escapedQuote, Array.Empty<string>());
        Assert.DoesNotContain("a\\\"b c", escapedResult, StringComparison.Ordinal);
        Assert.Contains("password=\"[redacted]\"", escapedResult);
        Assert.Contains("tail", escapedResult);

        string singleQuoted = "secret='value with spaces'; next";
        string singleResult = DiagnosticBundle.Sanitize(singleQuoted, Array.Empty<string>());
        Assert.DoesNotContain("value with spaces", singleResult, StringComparison.Ordinal);
        Assert.Contains("secret='[redacted]'", singleResult);
        Assert.Contains("next", singleResult);

        string bare = "api_key=abc123&x=1";
        string bareResult = DiagnosticBundle.Sanitize(bare, Array.Empty<string>());
        Assert.DoesNotContain("abc123", bareResult, StringComparison.Ordinal);
        Assert.Contains("api_key=[redacted]", bareResult);
        Assert.Contains("&x=1", bareResult);

        string header = "Authorization: Bearer eyJ.abc.def";
        string headerResult = DiagnosticBundle.Sanitize(header, Array.Empty<string>());
        Assert.DoesNotContain("eyJ.abc.def", headerResult, StringComparison.Ordinal);
        Assert.Contains("Authorization: [redacted]", headerResult);
    }

    [Fact]
    public void Sanitize_literal_longest_first_ignores_short_values()
    {
        string text = "user alice hit secret-token-xyz and alice again";
        string result = DiagnosticBundle.Sanitize(text, new[] { "alice", "secret-token-xyz", "a" });
        Assert.DoesNotContain("secret-token-xyz", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", result, StringComparison.Ordinal);
        Assert.Contains("[redacted]", result);
    }

    [Fact]
    public async Task Unrelated_and_invalid_roles_excluded_from_boot_and_manifest()
    {
        var abl = WriteFixture("cap/abl_a.bin", "A");
        var dtbo = WriteFixture("cap/dtbo_a.bin", "irrelevant");
        string dest = Path.Combine(_root, "bundle.zip");

        var files = new List<DiagnosticBootFile>
        {
            new("captured", "abl_a", abl.path, abl.len, abl.sha),
            new("captured", "dtbo_a", dtbo.path, dtbo.len, dtbo.sha),   // unrelated role
            new("attacker", "abl_a", abl.path, abl.len, abl.sha),       // invalid source
        };
        await DiagnosticBundle.ExportAsync(dest, new DiagnosticExportRequest("s", "l", Array.Empty<string>(), files, true, false));

        var zip = ReadZip(dest);
        Assert.Contains("boot/captured/abl_a.bin", zip.Keys);
        Assert.DoesNotContain(zip.Keys, k => k.Contains("dtbo", StringComparison.Ordinal));

        string manifest = Encoding.UTF8.GetString(zip["manifest.json"]);
        Assert.DoesNotContain("dtbo_a", manifest);
        Assert.DoesNotContain("attacker", manifest);
    }

    [Fact]
    public async Task Expected_hash_mismatch_does_not_create_entry_and_does_not_abort_bundle()
    {
        var a = WriteFixture("cap/abl_a.bin", "PAYLOAD-A");
        var b = WriteFixture("cap/abl_b.bin", "PAYLOAD-B");
        string dest = Path.Combine(_root, "bundle.zip");

        string wrongSha = new string('0', 64);
        var files = new List<DiagnosticBootFile>
        {
            new("captured", "abl_a", a.path, a.len, a.sha),
            new("captured", "abl_b", b.path, b.len, wrongSha),
        };
        await DiagnosticBundle.ExportAsync(dest, new DiagnosticExportRequest("s", "l", Array.Empty<string>(), files, true, false));

        var zip = ReadZip(dest);
        Assert.Contains("boot/captured/abl_a.bin", zip.Keys);
        Assert.DoesNotContain("boot/captured/abl_b.bin", zip.Keys);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(zip["manifest.json"]));
        var bRec = doc.RootElement.GetProperty("copiedFiles").EnumerateArray()
            .Single(e => e.GetProperty("role").GetString() == "abl_b");
        Assert.Equal("rejected", bRec.GetProperty("status").GetString());
        Assert.Equal("sha256-mismatch", bRec.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Alias_refusal_leaves_existing_destination_untouched()
    {
        string dest = Path.Combine(_root, "bundle.zip");
        File.WriteAllText(dest, "PRE-EXISTING");
        var alias = new DiagnosticBootFile("captured", "abl_a", dest, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DiagnosticBundle.ExportAsync(dest, new DiagnosticExportRequest("s", "l", Array.Empty<string>(), new[] { alias }, true, false)));

        Assert.Equal("PRE-EXISTING", File.ReadAllText(dest));
        // No temp debris left behind.
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Alias_refusal_covers_unselected_invalid_role()
    {
        string dest = Path.Combine(_root, "bundle.zip");
        // Role not selected/cap-eligible, but path still equals dest.
        var alias = new DiagnosticBootFile("captured", "dtbo_a", dest, null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DiagnosticBundle.ExportAsync(dest, new DiagnosticExportRequest("s", "l", Array.Empty<string>(), new[] { alias }, true, false)));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Malformed_expected_hash_is_rejected_distinctly()
    {
        var a = WriteFixture("cap/abl_a.bin", "X");
        string dest = Path.Combine(_root, "bundle.zip");
        var files = new List<DiagnosticBootFile> { new("captured", "abl_a", a.path, a.len, "not-a-hash") };
        await DiagnosticBundle.ExportAsync(dest, new DiagnosticExportRequest("s", "l", Array.Empty<string>(), files, true, false));

        var zip = ReadZip(dest);
        Assert.DoesNotContain("boot/captured/abl_a.bin", zip.Keys);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(zip["manifest.json"]));
        var rec = doc.RootElement.GetProperty("copiedFiles").EnumerateArray().Single();
        Assert.Equal("rejected", rec.GetProperty("status").GetString());
        Assert.Equal("malformed-expected-sha256", rec.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Interrupted_copy_does_not_publish_partial_archive_or_overwrite_destination()
    {
        var a = WriteFixture("cap/abl_a.bin", new string('x', 4096));
        string dest = Path.Combine(_root, "bundle.zip");
        File.WriteAllText(dest, "OLD");

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiagnosticBundle.ExportAsync(dest,
                new DiagnosticExportRequest("s", "l", Array.Empty<string>(), new[] { new DiagnosticBootFile("captured", "abl_a", a.path, a.len, a.sha) }, true, false),
                cts.Token));

        Assert.Equal("OLD", File.ReadAllText(dest));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Destination_directory_symlink_aliasing_source_is_refused_and_existing_data_untouched()
    {
        if (OperatingSystem.IsWindows()) return; // uses Unix symlinks

        // Real directory that holds the "source" file we must not overwrite.
        string realDir = Path.Combine(_root, "realdata");
        Directory.CreateDirectory(realDir);
        var source = WriteFixture("realdata/abl_a.bin", "SOURCE-PAYLOAD");

        // Destination is expressed through a symlinked directory that aliases realdata.
        string linkDir = Path.Combine(_root, "destlink");
        Directory.CreateSymbolicLink(linkDir, realDir);
        string destViaLink = Path.Combine(linkDir, "abl_a.bin");

        // The supplied source is an unrelated file whose canonical location collides with
        // the destination once the destination's directory symlink is resolved.
        var alias = new DiagnosticBootFile("captured", "abl_a", source.path, source.len, source.sha);
        var unrelated = WriteFixture("cap/other_abl.bin", "OTHER");
        var files = new List<DiagnosticBootFile> { alias };

        // Not included (IncludeAbl false) but must still be refused for aliasing.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DiagnosticBundle.ExportAsync(destViaLink,
                new DiagnosticExportRequest("s", "l", Array.Empty<string>(), files, IncludeAbl: false, IncludeLargeBoot: false)));

        Assert.Equal("SOURCE-PAYLOAD", File.ReadAllText(source.path));

        // Sanity: unrelated destination through the same alias dir must still succeed and
        // create a fresh archive without disturbing the source.
        string unrelatedDest = Path.Combine(linkDir, "unrelated.zip");
        await DiagnosticBundle.ExportAsync(unrelatedDest,
            new DiagnosticExportRequest("s", "l", Array.Empty<string>(), Array.Empty<DiagnosticBootFile>(), false, false));
        Assert.True(File.Exists(unrelatedDest));
        Assert.Equal("SOURCE-PAYLOAD", File.ReadAllText(source.path));
    }
}
