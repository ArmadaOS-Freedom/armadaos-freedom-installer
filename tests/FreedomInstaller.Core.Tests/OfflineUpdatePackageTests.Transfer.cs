using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed partial class OfflineUpdatePackageTests
{
    private const string SessionIdConst = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TransactionIdConst = "11111111-1111-4111-8111-111111111111";
    private const string RecoveryUuidConst = "22222222-2222-4222-8222-222222222222";

    [Theory]
    [InlineData("TB322FC", true)]
    [InlineData("TB323FU", false)]
    public async Task Transfer_EndToEnd_MultiChunkResumeSaveAndExecute_Succeeds(string model, bool largeAndResume)
    {
        var rawRoot = largeAndResume ? RandomNumberGenerator.GetBytes(1_400_000) : "small-root-payload"u8.ToArray();
        var c = new Case(Path.Combine(root, "e2e-" + model), deviceModel: model, rootPayload: rawRoot);
        var work = Directory.CreateDirectory(Path.Combine(root, "e2e-work-" + model)).FullName;
        var recWork = Directory.CreateDirectory(Path.Combine(root, "e2e-rec-" + model)).FullName;

        using var imported = await Import(c.ArchivePath, model, "0.8.0", work, c.Verify);
        var signedGz = c.Entries["images/linux-root.img.gz"];
        var bootA = largeAndResume ? RandomNumberGenerator.GetBytes(1_100_000) : "boot-slot-a"u8.ToArray();
        var bootB = "boot-slot-b-image"u8.ToArray();

        var server = new FakeDeviceServer(model, c.DescriptorBytes, signedGz, bootA, bootB);
        if (largeAndResume)
        {
            server.SeedResumeOffset(131_072);
        }

        OfflineUpdateSession? persisted = null;
        var callbackBeforeUpload = false;
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));

        var session = await transfer.CreateSessionAsync(imported, (s, _) =>
        {
            persisted = s;
            callbackBeforeUpload = server.UploadCalls == 0 && server.PrepareCalls == 0 && server.ExecuteCalls == 0;
            return Task.CompletedTask;
        });

        Assert.True(callbackBeforeUpload);
        Assert.Equal(session, persisted);
        Assert.Equal(SessionIdConst, session.SessionId);
        Assert.Equal(model, session.DeviceModel);
        Assert.Equal(RecoveryUuidConst, session.RecoveryPartUuid);
        Assert.Equal(Hash(c.DescriptorBytes), session.PackageDescriptorSha256);
        Assert.Equal(c.DescriptorBytes, server.ReceivedDescriptor);
        Assert.Equal(c.SignatureBytes, server.ReceivedSignature);
        Assert.StartsWith(" \r\n", Encoding.UTF8.GetString(server.ReceivedDescriptor), StringComparison.Ordinal);
        Assert.EndsWith("\n\t ", Encoding.UTF8.GetString(server.ReceivedDescriptor), StringComparison.Ordinal);

        var prepPhases = new List<string>();
        var prepared = await transfer.PrepareAsync(imported, session,
            new SyncProgress<OfflineUpdateTransferProgress>(p => prepPhases.Add(p.Phase)));

        Assert.Equal("prepared", prepared.GetProperty("state").GetString());
        Assert.Equal(1, server.PrepareCalls);
        Assert.Equal(0, server.ExecuteCalls);
        Assert.Equal(signedGz, server.UploadedPayload);
        if (largeAndResume)
        {
            Assert.Equal(131_072L, server.UploadOffsets[0]);
            Assert.True(server.UploadCalls >= 2);
        }
        Assert.Contains("preparing", prepPhases);
        Assert.Contains("prepared", prepPhases);

        var saved = await transfer.SaveRecoveryAsync(imported, session, recWork);
        Assert.Equal(0, server.ExecuteCalls);
        Assert.All(server.ExportLengths, len => Assert.InRange(len, 1, 1024 * 1024));
        var diskBootA = File.ReadAllBytes(Path.Combine(saved.DirectoryPath, "trust", "slots", "a", "boot.img"));
        var diskBootB = File.ReadAllBytes(Path.Combine(saved.DirectoryPath, "trust", "slots", "b", "boot.img"));
        Assert.Equal(bootA, diskBootA);
        Assert.Equal(bootB, diskBootB);
        Assert.Equal(Hash(bootA), Hash(diskBootA));
        Assert.Equal(server.ExpectedAckBytes, saved.Acknowledgement);
        Assert.Equal(server.ExpectedAckBytes, File.ReadAllBytes(Path.Combine(saved.DirectoryPath, "recovery-ack.json")));

        var finalReceipt = await transfer.ExecuteAsync(imported, session, saved);
        Assert.Equal("awaiting_reboot", finalReceipt.GetProperty("state").GetString());
        Assert.False(finalReceipt.TryGetProperty("started", out _));
        Assert.Equal(1, server.PrepareCalls);
        Assert.Equal(1, server.ExecuteCalls);
        Assert.Equal(server.ExpectedAckBytes, server.ReceivedAck);
        Assert.DoesNotContain(server.Methods, m => m.Contains("reboot", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("partition", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Transfer_PersistenceCallbackFails_AbortsBeforeUploadPrepareOrExecute()
    {
        var c = new Case(Path.Combine(root, "persist-fail"));
        var work = Directory.CreateDirectory(Path.Combine(root, "persist-fail-work")).FullName;
        using var imported = await Import(c.ArchivePath, Model, "0.8.0", work, c.Verify);
        var server = new FakeDeviceServer(Model, c.DescriptorBytes, c.Entries["images/linux-root.img.gz"], [1, 2], [3, 4]);
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.CreateSessionAsync(imported,
            (_, _) => throw new InvalidOperationException("disk full")));

        Assert.Equal(["info", "begin"], server.Methods);
        Assert.Equal(0, server.UploadCalls);
        Assert.Equal(0, server.PrepareCalls);
        Assert.Equal(0, server.ExecuteCalls);
    }

    [Fact]
    public async Task Transfer_ValidationRejections_NeverInvokeMutatingRpcCalls()
    {
        var c = new Case(Path.Combine(root, "val"));
        var work = Directory.CreateDirectory(Path.Combine(root, "val-work")).FullName;
        using var imported = await Import(c.ArchivePath, Model, "0.8.0", work, c.Verify);

        var stage = Directory.GetParent(imported.PackageDirectory)!.FullName;
        Assert.Equal("payload", Path.GetFileName(imported.PackageDirectory));
        Assert.Equal(stage, Path.GetDirectoryName(imported.DescriptorPath));
        Assert.Equal(stage, Path.GetDirectoryName(imported.SignaturePath));

        var server = new FakeDeviceServer(Model, c.DescriptorBytes, c.Entries["images/linux-root.img.gz"], [1], [2]);
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));
        var session = await transfer.CreateSessionAsync(imported, (_, _) => Task.CompletedTask);

        server.Methods.Clear();
        var wrongModelSession = session with { DeviceModel = "TB322FC" };
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.PrepareAsync(imported, wrongModelSession));
        Assert.Empty(server.Methods);

        server.RecoveryPartUuid = "33333333-3333-4333-8333-333333333333";
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.PrepareAsync(imported, session));
        Assert.Equal(["info"], server.Methods);
        server.RecoveryPartUuid = RecoveryUuidConst;
        server.Methods.Clear();

        var origDesc = File.ReadAllBytes(imported.DescriptorPath);
        try
        {
            File.WriteAllBytes(imported.DescriptorPath, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(origDesc) + " "));
            await Assert.ThrowsAsync<InvalidDataException>(() => transfer.PrepareAsync(imported, session));
            await Assert.ThrowsAsync<InvalidDataException>(() => transfer.SaveRecoveryAsync(imported, session, work));
            Assert.Empty(server.Methods);
        }
        finally
        {
            File.WriteAllBytes(imported.DescriptorPath, origDesc);
        }

        Assert.Equal(0, server.UploadCalls);
        Assert.Equal(0, server.PrepareCalls);
        Assert.Equal(0, server.ExecuteCalls);
    }

    [Fact]
    public async Task Transfer_FaultStatesAndResumedPolling_PreserveReceiptAndNeverResubmit()
    {
        var c = new Case(Path.Combine(root, "faults"));
        var work = Directory.CreateDirectory(Path.Combine(root, "faults-work")).FullName;
        var recWork = Directory.CreateDirectory(Path.Combine(root, "faults-rec")).FullName;
        using var imported = await Import(c.ArchivePath, Model, "0.8.0", work, c.Verify);
        var server = new FakeDeviceServer(Model, c.DescriptorBytes, c.Entries["images/linux-root.img.gz"], [10, 20], [30, 40]);
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));
        var session = await transfer.CreateSessionAsync(imported, (_, _) => Task.CompletedTask);

        server.JobInterrupted = true;
        var ex1 = await Assert.ThrowsAsync<OfflineUpdateTransferException>(() => transfer.PrepareAsync(imported, session));
        Assert.True(ex1.Receipt?.GetProperty("jobInterrupted").GetBoolean());
        server.JobInterrupted = false;

        server.AttentionRequired = true;
        var ex2 = await Assert.ThrowsAsync<OfflineUpdateTransferException>(() => transfer.PrepareAsync(imported, session));
        Assert.True(ex2.Receipt?.GetProperty("attentionRequired").GetBoolean());
        server.AttentionRequired = false;
        Assert.Equal(0, server.PrepareCalls);

        server.StateQueue.Enqueue("prepare_queued");
        server.StateQueue.Enqueue("preparing");
        server.StateQueue.Enqueue("prepared");
        _ = await transfer.PrepareAsync(imported, session);
        Assert.Equal(1, server.PrepareCalls);

        server.State = "preparing";
        server.StateQueue.Enqueue("prepared");
        _ = await transfer.PrepareAsync(imported, session);
        Assert.Equal(1, server.PrepareCalls);

        server.CorruptPlanPackageHash = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.SaveRecoveryAsync(imported, session, recWork));
        server.CorruptPlanPackageHash = false;

        var saved = await transfer.SaveRecoveryAsync(imported, session, recWork);

        server.ExecuteTerminalFailure = true;
        var exExec = await Assert.ThrowsAsync<OfflineUpdateTransferException>(() =>
            transfer.ExecuteAsync(imported, session, saved));
        Assert.Equal("failed", exExec.Receipt?.GetProperty("state").GetString());
        Assert.Equal(1, server.ExecuteCalls);

        server.ExecuteTerminalFailure = false;
        server.State = "execute_queued";
        server.StateQueue.Enqueue("executing");
        server.StateQueue.Enqueue("awaiting_reboot");
        var done = await transfer.ExecuteAsync(imported, session, saved);
        Assert.Equal("awaiting_reboot", done.GetProperty("state").GetString());
        Assert.Equal(1, server.ExecuteCalls);
    }

    [Fact]
    public async Task Transfer_TamperedSavedRecoveryFiles_PreventExecuteAndKeepNeighborsIntact()
    {
        var c = new Case(Path.Combine(root, "tamper"));
        var work = Directory.CreateDirectory(Path.Combine(root, "tamper-work")).FullName;
        var recWork = Directory.CreateDirectory(Path.Combine(root, "tamper-rec")).FullName;
        using var imported = await Import(c.ArchivePath, Model, "0.8.0", work, c.Verify);
        var bootA = "boot-a-content-12345"u8.ToArray();
        var bootB = "boot-b-content-67890"u8.ToArray();
        var server = new FakeDeviceServer(Model, c.DescriptorBytes, c.Entries["images/linux-root.img.gz"], bootA, bootB);
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));
        var session = await transfer.CreateSessionAsync(imported, (_, _) => Task.CompletedTask);
        _ = await transfer.PrepareAsync(imported, session);
        var saved = await transfer.SaveRecoveryAsync(imported, session, recWork);

        var pathA = Path.Combine(saved.DirectoryPath, "trust", "slots", "a", "boot.img");
        var pathB = Path.Combine(saved.DirectoryPath, "trust", "slots", "b", "boot.img");

        var mutated = bootA.ToArray();
        mutated[0] ^= 0xFF;
        File.WriteAllBytes(pathA, mutated);
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.ExecuteAsync(imported, session, saved));
        Assert.Equal(0, server.ExecuteCalls);
        Assert.Equal(bootB, File.ReadAllBytes(pathB));

        File.WriteAllBytes(pathA, bootA[..^2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.ExecuteAsync(imported, session, saved));
        Assert.Equal(0, server.ExecuteCalls);
        Assert.Equal(bootB, File.ReadAllBytes(pathB));

        File.Delete(pathA);
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.ExecuteAsync(imported, session, saved));
        Assert.Equal(0, server.ExecuteCalls);
        Assert.Equal(bootB, File.ReadAllBytes(pathB));

        Directory.CreateDirectory(Path.GetDirectoryName(pathA)!);
        File.WriteAllBytes(pathA, bootA);
        var done = await transfer.ExecuteAsync(imported, session, saved);
        Assert.Equal("awaiting_reboot", done.GetProperty("state").GetString());
        Assert.Equal(1, server.ExecuteCalls);
    }

    [Fact]
    public async Task Transfer_CancellationDuringPoll_ReconnectResumesPollOnly_AndUpToDateSkipsExportExecute()
    {
        var c = new Case(Path.Combine(root, "cancel"));
        var work = Directory.CreateDirectory(Path.Combine(root, "cancel-work")).FullName;
        var recWork = Directory.CreateDirectory(Path.Combine(root, "cancel-rec")).FullName;
        using var imported = await Import(c.ArchivePath, Model, "0.8.0", work, c.Verify);
        var server = new FakeDeviceServer(Model, c.DescriptorBytes, c.Entries["images/linux-root.img.gz"], [1], [2]);
        await using var client1 = server.CreateClient();
        var transfer1 = new OfflineUpdateTransfer(client1, TimeSpan.FromMilliseconds(1));
        var session = await transfer1.CreateSessionAsync(imported, (_, _) => Task.CompletedTask);

        server.StateQueue.Enqueue("preparing");
        server.StateQueue.Enqueue("preparing");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<OfflineUpdateTransferProgress>(p =>
        {
            if (p.Phase == "preparing" && server.PrepareCalls > 0) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transfer1.PrepareAsync(imported, session, progress, cts.Token));
        Assert.Equal(1, server.PrepareCalls);
        Assert.Equal("preparing", server.State);

        server.StateQueue.Clear();
        server.StateQueue.Enqueue("up_to_date");
        await using var client2 = server.CreateClient();
        var transfer2 = new OfflineUpdateTransfer(client2, TimeSpan.FromMilliseconds(1));
        var result = await transfer2.PrepareAsync(imported, session);
        Assert.Equal("up_to_date", result.GetProperty("state").GetString());
        Assert.Equal(1, server.PrepareCalls);

        await Assert.ThrowsAsync<OfflineUpdateTransferException>(() =>
            transfer2.SaveRecoveryAsync(imported, session, recWork));
        Assert.Empty(server.ExportLengths);
        Assert.Equal(0, server.ExecuteCalls);
    }

    [Fact]
    public async Task SessionStatus_ReadsSavedSessionReadOnlyWithoutPackage()
    {
        var c = new Case(Path.Combine(root, "session-status"));
        var gz = c.Entries["images/linux-root.img.gz"];
        var server = new FakeDeviceServer("TB322FC", c.DescriptorBytes, gz,
            "boot-slot-a"u8.ToArray(), "boot-slot-b"u8.ToArray())
        {
            State = "awaiting_reboot"
        };
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));

        // Rebuilt from the saved record alone: no imported package exists, so
        // the payload identity cannot be compared with a descriptor.
        var session = new OfflineUpdateSession(SessionIdConst, "TB322FC", RecoveryUuidConst,
            Hash(c.DescriptorBytes));

        var receipt = await transfer.QuerySessionStatusAsync(session);

        Assert.Equal("awaiting_reboot", receipt.GetProperty("state").GetString());
        Assert.Equal(SessionIdConst, receipt.GetProperty("sessionId").GetString());
        Assert.Equal(new[] { "info", "status" }, server.Methods);
        Assert.Equal(0, server.UploadCalls);
        Assert.Equal(0, server.PrepareCalls);
        Assert.Equal(0, server.ExecuteCalls);
        Assert.DoesNotContain("begin", server.Methods);
        Assert.DoesNotContain("seal", server.Methods);
        Assert.DoesNotContain("export", server.Methods);
    }

    [Fact]
    public async Task SessionStatus_RejectsAnotherDeviceAndUnacknowledgedAttentionFlag()
    {
        var c = new Case(Path.Combine(root, "session-status-reject"));
        var gz = c.Entries["images/linux-root.img.gz"];
        var server = new FakeDeviceServer("TB322FC", c.DescriptorBytes, gz,
            "boot-slot-a"u8.ToArray(), "boot-slot-b"u8.ToArray())
        {
            State = "awaiting_reboot"
        };
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));
        var session = new OfflineUpdateSession(SessionIdConst, "TB322FC", RecoveryUuidConst,
            Hash(c.DescriptorBytes));

        // The saved session belongs to another recovery partition.
        var otherDevice = session with { RecoveryPartUuid = Guid.NewGuid().ToString("D") };
        await Assert.ThrowsAsync<InvalidDataException>(
            () => transfer.QuerySessionStatusAsync(otherDevice));

        server.AttentionRequired = true;
        var failure = await Assert.ThrowsAsync<OfflineUpdateTransferException>(
            () => transfer.QuerySessionStatusAsync(session));
        Assert.Equal("awaiting_reboot", failure.Receipt!.Value.GetProperty("state").GetString());
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class FakeDeviceServer
    {
        private readonly string _model;
        private readonly byte[] _expectedPayload;
        private readonly string _expectedPayloadSha;
        private readonly byte[] _descriptorBytes;
        private readonly Dictionary<string, byte[]> _exports;
        private readonly MemoryStream _uploadBuffer = new();

        public FakeDeviceServer(string model, byte[] descriptorBytes, byte[] expectedPayload, byte[] bootA, byte[] bootB)
        {
            _model = model;
            _descriptorBytes = descriptorBytes;
            _expectedPayload = expectedPayload;
            _expectedPayloadSha = Hash(expectedPayload);

            var files = new JsonArray
            {
                new JsonObject { ["path"] = "trust/slots/a/boot.img", ["bytes"] = bootA.LongLength, ["sha256"] = Hash(bootA) },
                new JsonObject { ["path"] = "trust/slots/b/boot.img", ["bytes"] = bootB.LongLength, ["sha256"] = Hash(bootB) }
            };
            var manifestBytes = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["schemaVersion"] = 1,
                ["operation"] = "offline-upgrade",
                ["transactionId"] = TransactionIdConst,
                ["model"] = model,
                ["recoveryPartUuid"] = RecoveryUuidConst,
                ["files"] = files
            }.ToJsonString());
            ManifestSha = Hash(manifestBytes);
            PlanBytes = BuildPlanBytes(Hash(descriptorBytes));
            PlanSha = Hash(PlanBytes);

            _exports = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["plan.json"] = PlanBytes,
                ["recovery-manifest.json"] = manifestBytes,
                ["trust/slots/a/boot.img"] = bootA,
                ["trust/slots/b/boot.img"] = bootB
            };

            ExpectedAckBytes = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["schemaVersion"] = 1,
                ["operation"] = "offline-upgrade",
                ["transactionId"] = TransactionIdConst,
                ["planSha256"] = PlanSha,
                ["recoveryManifestSha256"] = ManifestSha,
                ["files"] = new JsonArray
                {
                    new JsonObject { ["path"] = "trust/slots/a/boot.img", ["bytes"] = bootA.LongLength, ["sha256"] = Hash(bootA) },
                    new JsonObject { ["path"] = "trust/slots/b/boot.img", ["bytes"] = bootB.LongLength, ["sha256"] = Hash(bootB) }
                }
            }.ToJsonString());
        }

        public string RecoveryPartUuid { get; set; } = RecoveryUuidConst;
        public string State { get; set; } = "uploading";
        public Queue<string> StateQueue { get; } = new();
        public bool JobInterrupted { get; set; }
        public bool AttentionRequired { get; set; }
        public bool CorruptPlanPackageHash { get; set; }
        public bool ExecuteTerminalFailure { get; set; }
        public List<string> Methods { get; } = [];
        public List<long> UploadOffsets { get; } = [];
        public List<int> ExportLengths { get; } = [];
        public int UploadCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public int ExecuteCalls { get; private set; }
        public byte[] ReceivedDescriptor { get; private set; } = [];
        public byte[] ReceivedSignature { get; private set; } = [];
        public byte[] ReceivedAck { get; private set; } = [];
        public byte[] UploadedPayload => _uploadBuffer.ToArray();
        public byte[] PlanBytes { get; }
        public string PlanSha { get; }
        public string ManifestSha { get; }
        public byte[] ExpectedAckBytes { get; }

        public void SeedResumeOffset(int count)
        {
            _uploadBuffer.SetLength(0);
            _uploadBuffer.Write(_expectedPayload, 0, count);
        }

        public OfflineUpdateRpcClient CreateClient()
        {
            var stream = new RpcLoopbackStream(this);
            return new OfflineUpdateRpcClient(stream, stream);
        }

        private byte[] BuildPlanBytes(string pkgSha) => Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["operation"] = "offline-upgrade",
            ["policy"] = "offline-dual",
            ["transactionId"] = TransactionIdConst,
            ["model"] = _model,
            ["packageDescriptorSha256"] = pkgSha,
            ["recoveryManifestSha256"] = ManifestSha
        }.ToJsonString());

        public byte[] HandleFrame(byte[] frame)
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetInt32();
            var method = root.GetProperty("method").GetString()!;
            var @params = root.GetProperty("params");
            Methods.Add(method);

            JsonNode result = method switch
            {
                "info" => BuildInfo(),
                "begin" => HandleBegin(@params),
                "status" => HandleStatus(@params),
                "upload" => HandleUpload(@params),
                "seal" => HandleSeal(@params),
                "prepare" => HandlePrepare(@params),
                "export" => HandleExport(@params),
                "execute" => HandleExecute(@params),
                _ => throw new InvalidOperationException("Unknown RPC method: " + method)
            };

            var response = new JsonObject
            {
                ["protocolVersion"] = 1,
                ["id"] = id,
                ["ok"] = true,
                ["result"] = result
            };
            return Encoding.UTF8.GetBytes(response.ToJsonString());
        }

        private JsonObject BuildInfo() => new()
        {
            ["schemaVersion"] = 1,
            ["updaterVersion"] = "0.8.1",
            ["model"] = _model,
            ["actualSlot"] = "a",
            ["inactiveSlot"] = "b",
            ["deployment"] = new string('a', 64) + ".0",
            ["recoveryPartUuid"] = RecoveryPartUuid,
            ["channel"] = "testing"
        };

        private JsonObject HandleBegin(JsonElement p)
        {
            ReceivedDescriptor = Convert.FromBase64String(p.GetProperty("document").GetString()!);
            ReceivedSignature = Convert.FromBase64String(p.GetProperty("signature").GetString()!);
            State = "uploading";
            return BuildReceipt();
        }

        private JsonObject HandleStatus(JsonElement p)
        {
            Assert.Equal(SessionIdConst, p.GetProperty("sessionId").GetString());
            if (StateQueue.Count > 0 && State is "prepare_queued" or "preparing" or "execute_queued" or "executing")
                State = StateQueue.Dequeue();
            return BuildReceipt();
        }

        private JsonObject HandleUpload(JsonElement p)
        {
            Assert.Equal(SessionIdConst, p.GetProperty("sessionId").GetString());
            var offset = p.GetProperty("offset").GetInt64();
            var data = Convert.FromBase64String(p.GetProperty("data").GetString()!);
            Assert.Equal(_uploadBuffer.Length, offset);
            UploadOffsets.Add(offset);
            UploadCalls++;
            _uploadBuffer.Position = offset;
            _uploadBuffer.Write(data);
            return new JsonObject { ["receivedBytes"] = _uploadBuffer.Length };
        }

        private JsonObject HandleSeal(JsonElement p)
        {
            Assert.Equal(SessionIdConst, p.GetProperty("sessionId").GetString());
            Assert.Equal(_expectedPayload.Length, _uploadBuffer.Length);
            Assert.Equal(_expectedPayloadSha, Hash(_uploadBuffer.ToArray()));
            State = "uploaded";
            return BuildReceipt();
        }

        private JsonObject HandlePrepare(JsonElement p)
        {
            Assert.Equal(SessionIdConst, p.GetProperty("sessionId").GetString());
            PrepareCalls++;
            State = StateQueue.Count > 0 ? StateQueue.Dequeue() : "prepared";
            return BuildReceipt();
        }

        private JsonObject HandleExport(JsonElement p)
        {
            Assert.Equal(SessionIdConst, p.GetProperty("sessionId").GetString());
            var path = p.GetProperty("path").GetString()!;
            var offset = (int)p.GetProperty("offset").GetInt64();
            var length = p.GetProperty("length").GetInt32();
            ExportLengths.Add(length);

            var source = path == "plan.json" && CorruptPlanPackageHash
                ? BuildPlanBytes(new string('f', 64))
                : _exports[path];
            var slice = offset >= source.Length
                ? []
                : source.AsSpan(offset, Math.Min(length, source.Length - offset)).ToArray();
            return new JsonObject
            {
                ["data"] = Convert.ToBase64String(slice)
            };
        }

        private JsonObject HandleExecute(JsonElement p)
        {
            Assert.Equal(SessionIdConst, p.GetProperty("sessionId").GetString());
            ReceivedAck = Encoding.UTF8.GetBytes(p.GetProperty("acknowledgement").GetRawText());
            using var ackDoc = JsonDocument.Parse(ReceivedAck);
            var root = ackDoc.RootElement;
            Assert.Equal(TransactionIdConst, root.GetProperty("transactionId").GetString());
            Assert.Equal(PlanSha, root.GetProperty("planSha256").GetString());
            Assert.Equal(ManifestSha, root.GetProperty("recoveryManifestSha256").GetString());
            Assert.Equal(ExpectedAckBytes, ReceivedAck);
            ExecuteCalls++;
            State = ExecuteTerminalFailure ? "failed" : (StateQueue.Count > 0 ? StateQueue.Dequeue() : "awaiting_reboot");
            return BuildReceipt();
        }

        private JsonObject BuildReceipt()
        {
            var curPlanBytes = CorruptPlanPackageHash ? BuildPlanBytes(new string('f', 64)) : PlanBytes;
            var curPlanSha = Hash(curPlanBytes);
            var obj = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["sessionId"] = SessionIdConst,
                ["state"] = State,
                ["model"] = _model,
                ["channel"] = "testing",
                ["receivedBytes"] = _uploadBuffer.Length,
                ["physical"] = new JsonObject
                {
                    ["model"] = _model,
                    ["actualSlot"] = "a",
                    ["inactiveSlot"] = "b",
                    ["deployment"] = new string('a', 64) + ".0",
                    ["recoveryPartUuid"] = RecoveryPartUuid
                },
                ["payload"] = new JsonObject
                {
                    ["path"] = "images/linux-root.img.gz",
                    ["bytes"] = _expectedPayload.LongLength,
                    ["sha256"] = _expectedPayloadSha
                }
            };
            if (JobInterrupted) obj["jobInterrupted"] = true;
            if (AttentionRequired) obj["attentionRequired"] = true;
            if (State == "prepared")
            {
                var meta = new JsonObject
                {
                    ["state"] = "awaiting_recovery_export",
                    ["transactionId"] = TransactionIdConst,
                    ["planSha256"] = curPlanSha,
                    ["recoveryManifestSha256"] = ManifestSha
                };
                obj["result"] = meta.DeepClone();
                obj["transaction"] = meta;
            }
            else if (State == "failed")
            {
                obj["result"] = new JsonObject { ["state"] = "recovery_required" };
            }
            return obj;
        }
    }

    private sealed class RpcLoopbackStream(FakeDeviceServer server) : Stream
    {
        private readonly MemoryStream _writeAccumulator = new();
        private readonly Queue<byte> _readQueue = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = 0;
            while (read < count && _readQueue.Count > 0)
                buffer[offset + read++] = _readQueue.Dequeue();
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var span = buffer.Span;
            var read = 0;
            while (read < span.Length && _readQueue.Count > 0)
                span[read++] = _readQueue.Dequeue();
            return ValueTask.FromResult(read);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _writeAccumulator.Write(buffer, offset, count);
            ProcessFrames();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writeAccumulator.Write(buffer.Span);
            ProcessFrames();
            return ValueTask.CompletedTask;
        }

        private void ProcessFrames()
        {
            var data = _writeAccumulator.ToArray();
            var pos = 0;
            while (pos < data.Length)
            {
                var newline = Array.IndexOf(data, (byte)'\n', pos);
                if (newline < 0) break;
                var payload = data.AsSpan(pos, newline - pos).ToArray();
                pos = newline + 1;

                var respPayload = server.HandleFrame(payload);
                foreach (var b in respPayload) _readQueue.Enqueue(b);
                _readQueue.Enqueue((byte)'\n');
            }
            _writeAccumulator.SetLength(0);
            if (pos < data.Length)
                _writeAccumulator.Write(data, pos, data.Length - pos);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _writeAccumulator.Dispose();
            base.Dispose(disposing);
        }
    }
}
