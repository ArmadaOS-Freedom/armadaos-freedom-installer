using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed partial class SystemUpdateTrustTests
{
    private const string PythonBootTrustRunnerScript = @"
import copy
import json
import os
import shutil
import stat
import sys
from pathlib import Path

source_root, request_path, output_dir, boot_tool_path, scenario = sys.argv[1:6]
sys.path.insert(0, source_root + '/system_files/usr/lib/armada')

import updates.boot_trust as bt

with open(request_path, 'r', encoding='utf-8') as f:
    req = json.load(f)

model = req['model']
lock_state = req['lockState']
expected_extras = ('recovery_a', 'recovery_b', 'vbmeta_system_a', 'vbmeta_system_b')
if model == 'TB323FU':
    expected_extras += ('efisp',)

physical_template = {
    'schemaVersion': 1,
    'model': model,
    'actualSlot': 'a',
    'inactiveSlot': 'b',
    'selectedSlot': 'a',
}

inspect_calls = 0

def fake_inspect_physical_slots():
    global inspect_calls
    inspect_calls += 1
    snap = copy.deepcopy(physical_template)
    if scenario == 'physical-changed' and inspect_calls >= 2:
        snap['selectedSlot'] = 'b'
    return snap

lock_calls = 0

def fake_read_boot_lock():
    global lock_calls
    lock_calls += 1
    if scenario == 'lock-changed' and lock_calls >= 2:
        return 'locked'
    return lock_state

capture_calls = []
captured_boot_a_path = None

def copy_claim(claim_obj, dest_path):
    src_path = Path(claim_obj['path'])
    shutil.copyfile(src_path, dest_path)
    os.chmod(dest_path, 0o600)
    return {
        'path': str(dest_path),
        'sha256': claim_obj['sha256'],
        'bytes': int(claim_obj['length']),
    }

def fake_capture_boot_sources(expected, out_sources, extra_labels=()):
    global captured_boot_a_path
    capture_calls.append(tuple(extra_labels))
    if tuple(extra_labels) != expected_extras:
        raise AssertionError(f'unexpected extra_labels: {extra_labels!r} != {expected_extras!r}')
    out_path = Path(out_sources)
    out_path.mkdir(mode=0o700)

    slots = {'a': {}, 'b': {}}
    for slot in ('a', 'b'):
        slot_req = req[slot]
        for key in ('boot', 'vendorBoot', 'initBoot', 'abl', 'xblConfig', 'vbmeta'):
            dest = out_path / f'{key}_{slot}.img'
            slots[slot][key] = copy_claim(slot_req[key], dest)
            if slot == 'a' and key == 'boot':
                captured_boot_a_path = dest

    extra_map = {
        'recovery_a': req['a']['recovery'],
        'recovery_b': req['b']['recovery'],
        'vbmeta_system_a': req['a']['vbmetaSystem'],
        'vbmeta_system_b': req['b']['vbmetaSystem'],
    }
    if 'efisp' in extra_labels:
        extra_map['efisp'] = req['efisp']

    extra_files = {}
    for label in extra_labels:
        dest = out_path / f'{label}.img'
        extra_files[label] = copy_claim(extra_map[label], dest)

    return {
        'schemaVersion': 1,
        'phase': 'boot_sources_captured',
        'physical': copy.deepcopy(expected),
        'slots': slots,
        'extraFiles': extra_files,
        'partitions': {},
    }

bt.inspect_physical_slots = fake_inspect_physical_slots
bt.read_boot_lock = fake_read_boot_lock
bt.capture_boot_sources = fake_capture_boot_sources

real_run_stage = bt._run_stage
stage_calls = 0

def wrapped_run_stage(*args, **kwargs):
    global stage_calls
    stage_calls += 1
    res = real_run_stage(*args, **kwargs)
    if scenario == 'source-changed' and stage_calls == 1:
        data = bytearray(captured_boot_a_path.read_bytes())
        data[0] ^= 0x01
        captured_boot_a_path.write_bytes(data)
    return res

bt._run_stage = wrapped_run_stage

if scenario == 'sync-failed':
    def failing_fsync_directory(path):
        raise OSError('fixture directory sync failure')
    bt._fsync_directory = failing_fsync_directory

report = bt.capture_boot_trust(output_dir=output_dir, boot_tool_path=boot_tool_path)

if scenario != 'success':
    raise AssertionError(f'expected scenario {scenario} to fail, but capture_boot_trust succeeded')

if len(capture_calls) != 1:
    raise AssertionError(f'expected capture_boot_sources called once, got {len(capture_calls)}')
if stage_calls != 1:
    raise AssertionError(f'expected _run_stage called once, got {stage_calls}')

out_dir_path = Path(output_dir)
report_path = out_dir_path / 'boot-trust.json'
if not report_path.is_file():
    raise AssertionError('boot-trust.json was not written')

with open(report_path, 'r', encoding='utf-8') as f:
    disk_report = json.load(f)

if disk_report != report:
    raise AssertionError('returned report does not match boot-trust.json on disk')

for check_path in (out_dir_path, out_dir_path / 'tmp', out_dir_path / 'sources'):
    mode = stat.S_IMODE(check_path.stat().st_mode)
    if mode != 0o700:
        raise AssertionError(f'expected directory mode 0700 for {check_path}, got {oct(mode)}')

for slot in ('a', 'b'):
    st = report['trust'][slot]
    if len(st['rootPublicKeySha256']) != 64 or len(st['bootPublicKeySha256']) != 64:
        raise AssertionError(f'invalid key digests for slot {slot}')
    if st['maxRawBytes'] <= 0 or st['maxRawBytes'] > st['material']['loaderBudget']['maxRawBytes']:
        raise AssertionError(f'invalid loader budget for slot {slot}')
    if st['ablRootPublicKeySha256'] is not None:
        raise AssertionError(f'unexpected non-null ablRootPublicKeySha256 for slot {slot}')

print(json.dumps({'ok': True, 'mode': report['mode'], 'model': report['physical']['model'], 'lockState': report['lockState']}))
";

    [Theory]
    [InlineData(false, "TB322FC", "unlocked")]
    [InlineData(false, "TB323FU", "unlocked")]
    [InlineData(true, "TB323FU", "locked")]
    [InlineData(true, "TB323FU", "unknown")]
    public async Task PythonBootTrust_Success_AuthenticatesWithRealBootToolAndWritesReport(
        bool gbl,
        string model,
        string lockState)
    {
        var snapshot = CaptureSnapshot(
            gbl ? _fx.GblA : _fx.AospA,
            gbl ? _fx.GblB : _fx.AospB,
            _fx.PrcEfisp);

        string requestPath = _fx.NewCaseFilePath($"py-trust-req-{model}-{lockState}-{gbl}.json");
        string outDir = _fx.NewCaseFilePath($"py-trust-out-{model}-{lockState}-{gbl}");
        WritePythonRequestFile(requestPath, gbl, model, lockState);

        var (exitCode, stdout, stderr) = await RunPythonBootTrustAsync(
            requestPath,
            outDir,
            "success");

        Assert.True(exitCode == 0, $"Expected exit code 0, got {exitCode}. Stderr:\n{stderr}\nStdout:\n{stdout}");
        Assert.Equal(string.Empty, stderr);

        string reportPath = Path.Combine(outDir, "boot-trust.json");
        Assert.True(File.Exists(reportPath), "Expected boot-trust.json to exist.");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        JsonElement root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("boot_trust_checked", root.GetProperty("phase").GetString());
        Assert.Equal(lockState, root.GetProperty("lockState").GetString());
        Assert.Equal(gbl ? "gbl-preserve-metadata" : "aosp-resign", root.GetProperty("mode").GetString());
        Assert.Equal(model, root.GetProperty("physical").GetProperty("model").GetString());

        JsonElement trust = root.GetProperty("trust");
        Assert.Equal(model, trust.GetProperty("model").GetString());
        Assert.Equal(gbl ? "GblPreserveMetadata" : "AospResign", trust.GetProperty("mode").GetString());

        JsonElement extraFiles = root.GetProperty("sources").GetProperty("extraFiles");
        Assert.True(extraFiles.TryGetProperty("recovery_a", out _));
        Assert.True(extraFiles.TryGetProperty("recovery_b", out _));
        Assert.True(extraFiles.TryGetProperty("vbmeta_system_a", out _));
        Assert.True(extraFiles.TryGetProperty("vbmeta_system_b", out _));
        Assert.Equal(model == "TB323FU", extraFiles.TryGetProperty("efisp", out _));

        if (gbl)
        {
            Assert.Equal("PRC", trust.GetProperty("gblRegion").GetString());
            Assert.Equal(_fx.PrcEfisp.Sha256, trust.GetProperty("efispSha256").GetString(), StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, trust.GetProperty("gblRegion").ValueKind);
            Assert.Equal(JsonValueKind.Null, trust.GetProperty("efispSha256").ValueKind);
        }

        AssertSnapshotUnchanged(snapshot);
    }

    [Theory]
    [InlineData("aosp-unknown", "unknown", "ABL AVB key", false)]
    [InlineData("source-changed", "unlocked", "sources.slots.a.boot", true)]
    [InlineData("physical-changed", "unlocked", "physical boot slots changed during trust inspection", true)]
    [InlineData("lock-changed", "unlocked", "boot-lock state changed during trust inspection", true)]
    [InlineData("sync-failed", "unlocked", "fixture directory sync failure", true)]
    public async Task PythonBootTrust_Failure_RejectsInvalidStateAndLeavesNoFinalReport(
        string scenario,
        string lockState,
        string expectedErrorSubstring,
        bool expectCliStageResult)
    {
        var snapshot = CaptureSnapshot(_fx.AospA, _fx.AospB, _fx.PrcEfisp);

        string requestPath = _fx.NewCaseFilePath($"py-trust-fail-req-{scenario}.json");
        string outDir = _fx.NewCaseFilePath($"py-trust-fail-out-{scenario}");
        WritePythonRequestFile(requestPath, gbl: false, model: "TB323FU", lockState: lockState);

        var (exitCode, stdout, stderr) = await RunPythonBootTrustAsync(
            requestPath,
            outDir,
            scenario);

        Assert.True(exitCode != 0, $"Expected non-zero exit code for scenario '{scenario}'. Stdout:\n{stdout}\nStderr:\n{stderr}");
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("BootTrustError", stderr);
        Assert.Contains(expectedErrorSubstring, stderr);

        string reportPath = Path.Combine(outDir, "boot-trust.json");
        Assert.False(File.Exists(reportPath), "boot-trust.json must not exist when capture_boot_trust fails.");

        string cliStageResultPath = Path.Combine(outDir, "inspect-trust", "result.json");
        Assert.Equal(expectCliStageResult, File.Exists(cliStageResultPath));

        AssertSnapshotUnchanged(snapshot);
    }

    private void WritePythonRequestFile(string requestPath, bool gbl, string model, string lockState)
    {
        JsonObject obj = CreateRequestObject(gbl);
        obj["model"] = model;
        obj["lockState"] = lockState;
        obj["efisp"] = JsonSerializer.SerializeToNode(_fx.PrcEfisp, CliRequestJsonOptions);
        File.WriteAllText(requestPath, obj.ToJsonString(CliRequestJsonOptions));
    }

    private static string RequireSystemSourceRoot()
    {
        string? sourceRoot = Environment.GetEnvironmentVariable("ARMADA_SYSTEM_SOURCE_PATH");
        Assert.False(
            string.IsNullOrWhiteSpace(sourceRoot),
            "Required environment variable 'ARMADA_SYSTEM_SOURCE_PATH' is not set.");
        Assert.True(
            Path.IsPathFullyQualified(sourceRoot),
            $"ARMADA_SYSTEM_SOURCE_PATH must be an absolute path: '{sourceRoot}'.");
        Assert.True(
            Directory.Exists(sourceRoot),
            $"ARMADA_SYSTEM_SOURCE_PATH directory does not exist: '{sourceRoot}'.");
        string bootTrustPy = Path.Combine(sourceRoot, "system_files", "usr", "lib", "armada", "updates", "boot_trust.py");
        Assert.True(
            File.Exists(bootTrustPy),
            $"Expected updates/boot_trust.py at '{bootTrustPy}'.");
        return sourceRoot;
    }

    private static string RequireBootToolApphost()
    {
        string dll = RequireBootToolDll();
        string? dir = Path.GetDirectoryName(dll);
        Assert.False(string.IsNullOrWhiteSpace(dir), $"Could not determine directory for BOOT_TOOL_DLL '{dll}'.");
        string apphost = Path.Combine(dir, "armada-boot-tool");
        Assert.True(File.Exists(apphost), $"Expected armada-boot-tool apphost at '{apphost}'.");
        return apphost;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPythonBootTrustAsync(
        string requestPath,
        string outDir,
        string scenario)
    {
        string sourceRoot = RequireSystemSourceRoot();
        string apphost = RequireBootToolApphost();

        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(PythonBootTrustRunnerScript);
        psi.ArgumentList.Add(sourceRoot);
        psi.ArgumentList.Add(requestPath);
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add(apphost);
        psi.ArgumentList.Add(scenario);

        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

        using var process = new Process { StartInfo = psi };
        Assert.True(process.Start(), "Failed to start python3 boot_trust runner process.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
            Assert.Fail("Python boot_trust subprocess timed out after 120 seconds.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }
}
