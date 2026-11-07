using System.Reflection;
using System.Text.Json;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class EdlRawBootDiagnosticsTests
{
    [Fact]
    public async Task NativeFailureRetainsReceiptAfterOwnedChildAndSessionCleanup()
    {
        string root = Path.Combine(TestArtifactRoot.Resolve(), "native-failure-" + Guid.NewGuid().ToString("N"));
        var session = new InstallerWorkSession(root);
        string preparation = Path.Combine(session.DirectoryPath, "prepare");
        string child = Path.Combine(preparation, "edl-boot-a-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(child, "repack");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "build.cfg"), "[Pack]\nShim=runtime.bin\n");
        File.WriteAllBytes(Path.Combine(nested, "runtime.bin"), [1, 2, 3]);
        try
        {
            IOException error = await Assert.ThrowsAsync<IOException>(() => InvokeNative(
                new FailedRunner(), nested));
            Assert.Contains("synthetic refusal", error.Message);
            Assert.DoesNotContain("\u001b", error.Message, StringComparison.Ordinal);
            string receipt = Assert.Single(Directory.GetFiles(preparation, "native-failure-*.json"));
            using (JsonDocument record = JsonDocument.Parse(File.ReadAllText(receipt)))
            {
                Assert.Equal(9, record.RootElement.GetProperty("exitCode").GetInt32());
                Assert.Equal(nested, record.RootElement.GetProperty("workingDirectory").GetString());
                Assert.Equal("synthetic stdout", record.RootElement.GetProperty("stdout").GetString());
                Assert.Equal("synthetic refusal", record.RootElement.GetProperty("stderr").GetString());
                Assert.Contains("[Pack]", record.RootElement.GetProperty("configs").GetProperty("build.cfg").GetString());
                Assert.Contains(record.RootElement.GetProperty("files").EnumerateArray(),
                    f => f.GetProperty("name").GetString() == "runtime.bin" && f.GetProperty("bytes").GetInt64() == 3);
            }
            Directory.Delete(child, recursive: true);
            session.DiscardPreparation(preparation, false);
            session.Complete(false);
            Assert.Single(Directory.GetFiles(session.RecordsDirectory, "native-failure-*.json", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(session.RecordsDirectory, "*.bin", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExistingOwnedFileIsNotOverwrittenAndErrorNamesThePath()
    {
        string root = Path.Combine(TestArtifactRoot.Resolve(), "native-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "dtb-normal.dtb");
        File.WriteAllText(path, "keep original");
        try
        {
            MethodInfo method = typeof(EdlRawBootBuilder).GetMethod("CreateNew", BindingFlags.NonPublic | BindingFlags.Static)!;
            var wrapped = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [path, "package dtb-normal"]));
            IOException error = Assert.IsType<IOException>(wrapped.InnerException);
            Assert.Contains(path, error.Message);
            Assert.Contains("already exists", error.Message);
            Assert.IsType<IOException>(error.InnerException);
            Assert.Equal("keep original", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WindowsNativeCommandsPackAndRoundTripBeyondTheProcessCwdLimit()
    {
        if (!OperatingSystem.IsWindows()) return; // Executed separately on Windows with built EXEs.
        string packer = Environment.GetEnvironmentVariable("EDL_RAW_PACKER_PATH")
            ?? throw new InvalidOperationException("EDL_RAW_PACKER_PATH is required on Windows.");
        string unpacker = Environment.GetEnvironmentVariable("EDL_RAW_UNPACKER_PATH")
            ?? throw new InvalidOperationException("EDL_RAW_UNPACKER_PATH is required on Windows.");
        string root = Path.Combine(TestArtifactRoot.Resolve(), "native-roundtrip-" + Guid.NewGuid().ToString("N"));
        string child = Path.Combine(root, "edl-boot-a-" + Guid.NewGuid().ToString("N"));
        while (child.Length < 350) child = Path.Combine(child, new string('x', 50));
        Directory.CreateDirectory(child);
        try
        {
            byte[] image = new byte[4096];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(), 0x14000010);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 0xD503201F);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(16), 4096);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(56), 0x644D5241);
            File.WriteAllBytes(Path.Combine(child, "BaseImage"), image);
            File.WriteAllBytes(Path.Combine(child, "runtime.bin"), new byte[4 * 1024 * 1024]);
            File.WriteAllText(Path.Combine(child, "build.cfg"),
                "[Pack]\nShim=runtime.bin\nOutput=PackedKernel\nDefault=Image-00\nTimeout=0\n"
                + "[Image-00]\nName=Base\nPath=BaseImage\nType=Linux\nBaseImage=true\n[Manifest]\nType=Manifest\n");
            var runner = new ProcessRunner();
            await InvokeNative(runner, child, packer, ["build.cfg"]);
            await InvokeNative(runner, child, unpacker, ["PackedKernel", "unpacked"]);
            await InvokeNative(runner, Path.Combine(child, "unpacked"), packer, ["config.cfg"]);
            Assert.Equal(File.ReadAllBytes(Path.Combine(child, "PackedKernel")),
                File.ReadAllBytes(Path.Combine(child, "unpacked", "RepackedKernel")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task InvokeNative(ICommandRunner runner, string workingDirectory,
        string executable = "synthetic-packer", IReadOnlyList<string>? arguments = null) =>
        (Task)typeof(EdlRawBootBuilder).GetMethod("RunNativeAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [runner, executable, arguments ?? new[] { "build.cfg" }, workingDirectory, "packer", CancellationToken.None])!;

    private sealed class FailedRunner : ICommandRunner
    {
        public Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(9, "synthetic stdout", "\u001b[31msynthetic refusal\u001b[0m"));
    }
}