using System.Text;
using System.Text.Json;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed partial class OfflineUpdatePackageTests
{
    private static OfflineUpdateSession NewSession(string model, byte[] descriptor) =>
        new(SessionIdConst, model, RecoveryUuidConst, Hash(descriptor));

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    public async Task Query_WithoutPriorBegin_IsReadOnlyAndReturnsState(string model)
    {
        var c = new Case(Path.Combine(root, "q-" + model), deviceModel: model);
        var work = Directory.CreateDirectory(Path.Combine(root, "q-work-" + model)).FullName;
        using var imported = await Import(c.ArchivePath, model, "0.8.0", work, c.Verify);
        var server = new FakeDeviceServer(model, c.DescriptorBytes,
            c.Entries["images/linux-root.img.gz"], [1, 2], [3, 4]) { State = "prepared" };
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));

        var receipt = await transfer.QueryStatusAsync(imported, NewSession(model, c.DescriptorBytes));

        Assert.Equal("prepared", receipt.GetProperty("state").GetString());
        var result = receipt.GetProperty("result");
        Assert.Equal("awaiting_recovery_export", result.GetProperty("state").GetString());
        Assert.Equal(TransactionIdConst, result.GetProperty("transactionId").GetString());
        Assert.Equal(server.PlanSha, result.GetProperty("planSha256").GetString());
        Assert.Equal(server.ManifestSha, result.GetProperty("recoveryManifestSha256").GetString());
        Assert.DoesNotContain(server.Methods, m => m != "info" && m != "status");
        Assert.DoesNotContain(server.Methods, m => m.Contains("begin", StringComparison.Ordinal));
        Assert.Equal(0, server.UploadCalls);
        Assert.Equal(0, server.PrepareCalls);
        Assert.Equal(0, server.ExecuteCalls);
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    public async Task Observe_ActiveExecution_ReportsPhasesAndReconnectsReadOnly(string model)
    {
        var c = new Case(Path.Combine(root, "obs-" + model), deviceModel: model);
        var work = Directory.CreateDirectory(Path.Combine(root, "obs-work-" + model)).FullName;
        using var imported = await Import(c.ArchivePath, model, "0.8.0", work, c.Verify);
        var server = new FakeDeviceServer(model, c.DescriptorBytes,
            c.Entries["images/linux-root.img.gz"], [1], [2]) { State = "execute_queued" };
        server.StateQueue.Enqueue("executing");
        server.StateQueue.Enqueue("awaiting_reboot");
        var phases = new List<string>();
        OfflineUpdateTransfer transfer;
        var client1 = server.CreateClient();
        await using (client1)
        {
            transfer = new OfflineUpdateTransfer(client1, TimeSpan.FromMilliseconds(1));
            var receipt = await transfer.ObserveExecutionAsync(imported,
                NewSession(model, c.DescriptorBytes),
                new SyncProgress<OfflineUpdateTransferProgress>(p => phases.Add(p.Phase)));
            Assert.Equal("awaiting_reboot", receipt.GetProperty("state").GetString());
        }

        Assert.Contains("executing", phases);
        Assert.Equal("awaiting_reboot", phases[^1]);
        await using var client2 = server.CreateClient();
        var transfer2 = new OfflineUpdateTransfer(client2, TimeSpan.FromMilliseconds(1));
        var again = await transfer2.ObserveExecutionAsync(imported, NewSession(model, c.DescriptorBytes));
        Assert.Equal("awaiting_reboot", again.GetProperty("state").GetString());
        Assert.DoesNotContain(server.Methods, m => m != "info" && m != "status");
        Assert.Equal(0, server.UploadCalls);
        Assert.Equal(0, server.PrepareCalls);
        Assert.Equal(0, server.ExecuteCalls);
    }

    [Fact]
    public async Task Observe_NonExecutingStates_ThrowWithReceipt_NoMutatingRpc()
    {
        var c = new Case(Path.Combine(root, "obs-forbid"));
        var work = Directory.CreateDirectory(Path.Combine(root, "obs-forbid-work")).FullName;
        using var imported = await Import(c.ArchivePath, Model, "0.8.0", work, c.Verify);
        var server = new FakeDeviceServer(Model, c.DescriptorBytes,
            c.Entries["images/linux-root.img.gz"], [1], [2]);
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));
        var session = NewSession(Model, c.DescriptorBytes);

        server.State = "prepared";
        await Assert.ThrowsAsync<OfflineUpdateTransferException>(
            () => transfer.ObserveExecutionAsync(imported, session));

        server.State = "failed";
        var failed = await Assert.ThrowsAsync<OfflineUpdateTransferException>(
            () => transfer.ObserveExecutionAsync(imported, session));
        Assert.Equal("recovery_required",
            failed.Receipt?.GetProperty("result").GetProperty("state").GetString());

        server.State = "executing";
        server.AttentionRequired = true;
        var attention = await Assert.ThrowsAsync<OfflineUpdateTransferException>(
            () => transfer.ObserveExecutionAsync(imported, session));
        Assert.True(attention.Receipt?.GetProperty("attentionRequired").GetBoolean());
        var queryAttention = await Assert.ThrowsAsync<OfflineUpdateTransferException>(
            () => transfer.QueryStatusAsync(imported, session));
        Assert.True(queryAttention.Receipt?.GetProperty("attentionRequired").GetBoolean());
        server.AttentionRequired = false;
        server.JobInterrupted = true;
        var interrupted = await Assert.ThrowsAsync<OfflineUpdateTransferException>(
            () => transfer.ObserveExecutionAsync(imported, session));
        Assert.True(interrupted.Receipt?.GetProperty("jobInterrupted").GetBoolean());
        server.JobInterrupted = false;

        Assert.DoesNotContain(server.Methods, m => m != "info" && m != "status");
        Assert.Equal(0, server.UploadCalls);
        Assert.Equal(0, server.PrepareCalls);
        Assert.Equal(0, server.ExecuteCalls);
    }

    [Fact]
    public async Task Observe_CancellationDuringExecuting_NewClientSeesAwaitingReboot()
    {
        var c = new Case(Path.Combine(root, "obs-cancel"));
        var work = Directory.CreateDirectory(Path.Combine(root, "obs-cancel-work")).FullName;
        using var imported = await Import(c.ArchivePath, Model, "0.8.0", work, c.Verify);
        var server = new FakeDeviceServer(Model, c.DescriptorBytes,
            c.Entries["images/linux-root.img.gz"], [1], [2]) { State = "execute_queued" };
        server.StateQueue.Enqueue("executing");
        server.StateQueue.Enqueue("awaiting_reboot");
        using var cts = new CancellationTokenSource();
        await using var client1 = server.CreateClient();
        var transfer1 = new OfflineUpdateTransfer(client1, TimeSpan.FromMilliseconds(1));
        var observed = new List<string>();
        var progress = new SyncProgress<OfflineUpdateTransferProgress>(p =>
        {
            observed.Add(p.Phase);
            if (p.Phase == "executing") cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transfer1.ObserveExecutionAsync(imported, NewSession(Model, c.DescriptorBytes), progress, cts.Token));
        Assert.Contains("executing", observed);
        Assert.Equal(0, server.UploadCalls);
        Assert.Equal(0, server.PrepareCalls);
        Assert.Equal(0, server.ExecuteCalls);

        await using var client2 = server.CreateClient();
        var transfer2 = new OfflineUpdateTransfer(client2, TimeSpan.FromMilliseconds(1));
        var done = await transfer2.ObserveExecutionAsync(imported, NewSession(Model, c.DescriptorBytes));
        Assert.Equal("awaiting_reboot", done.GetProperty("state").GetString());
        Assert.DoesNotContain(server.Methods, m => m != "info" && m != "status");
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    public async Task ReadApis_RejectSessionMismatches_BeforeOrAtFirstInfo(string model)
    {
        var c = new Case(Path.Combine(root, "mism-" + model), deviceModel: model);
        var work = Directory.CreateDirectory(Path.Combine(root, "mism-work-" + model)).FullName;
        using var imported = await Import(c.ArchivePath, model, "0.8.0", work, c.Verify);
        var server = new FakeDeviceServer(model, c.DescriptorBytes,
            c.Entries["images/linux-root.img.gz"], [1], [2]);
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));

        server.Methods.Clear();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transfer.QueryStatusAsync(imported, NewSession(model, c.DescriptorBytes), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transfer.ObserveExecutionAsync(imported, NewSession(model, c.DescriptorBytes), null, cts.Token));
        Assert.Empty(server.Methods);

        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.QueryStatusAsync(imported,
            NewSession(model == "TB322FC" ? "TB323FU" : "TB322FC", c.DescriptorBytes)));
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.ObserveExecutionAsync(imported,
            NewSession(model == "TB322FC" ? "TB323FU" : "TB322FC", c.DescriptorBytes)));
        Assert.Empty(server.Methods);

        server.RecoveryPartUuid = "33333333-3333-4333-8333-333333333333";
        var session = NewSession(model, c.DescriptorBytes);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => transfer.QueryStatusAsync(imported, session));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => transfer.ObserveExecutionAsync(imported, session));
        Assert.Equal(["info", "info"], server.Methods);
        server.RecoveryPartUuid = RecoveryUuidConst;

        server.Methods.Clear();
        var wrongHash = new string('0', 64);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => transfer.QueryStatusAsync(imported, session with { PackageDescriptorSha256 = wrongHash }));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => transfer.ObserveExecutionAsync(imported, session with { PackageDescriptorSha256 = wrongHash }));
        Assert.Empty(server.Methods);
        Assert.Equal(0, server.UploadCalls);
        Assert.Equal(0, server.PrepareCalls);
        Assert.Equal(0, server.ExecuteCalls);
    }

    [Theory]
    [InlineData("TB322FC")]
    [InlineData("TB323FU")]
    public async Task LoadOfflineUpdateRecovery_ValidatesPersistedFiles(string model)
    {
        var c = new Case(Path.Combine(root, "load-" + model), deviceModel: model);
        var work = Directory.CreateDirectory(Path.Combine(root, "load-work-" + model)).FullName;
        var recWork = Directory.CreateDirectory(Path.Combine(root, "load-rec-" + model)).FullName;
        using var imported = await Import(c.ArchivePath, model, "0.8.0", work, c.Verify);
        var bootA = "load-boot-a-0123456789"u8.ToArray();
        var bootB = "load-boot-b-9876543210"u8.ToArray();
        var server = new FakeDeviceServer(model, c.DescriptorBytes,
            c.Entries["images/linux-root.img.gz"], bootA, bootB);
        await using var client = server.CreateClient();
        var transfer = new OfflineUpdateTransfer(client, TimeSpan.FromMilliseconds(1));
        var session = await transfer.CreateSessionAsync(imported, (_, _) => Task.CompletedTask);
        _ = await transfer.PrepareAsync(imported, session);
        var saved = await transfer.SaveRecoveryAsync(imported, session, recWork);

        var context = new OfflineUpdateRecoveryContext(
            TransactionIdConst, model, server.PlanSha, server.ManifestSha);
        var descSha = Hash(c.DescriptorBytes);
        server.Methods.Clear();

        var loaded = PackageImporter.LoadOfflineUpdateRecovery(saved.DirectoryPath, context, descSha);
        Assert.Equal(Path.GetFullPath(saved.DirectoryPath), loaded.DirectoryPath);
        Assert.Equal(Path.Combine(loaded.DirectoryPath, "plan.json"), loaded.PlanPath);
        Assert.Equal(Path.Combine(loaded.DirectoryPath, "recovery-manifest.json"), loaded.ManifestPath);
        Assert.Equal(server.ExpectedAckBytes, loaded.Acknowledgement);
        Assert.Equal(server.ExpectedAckBytes, File.ReadAllBytes(
            Path.Combine(loaded.DirectoryPath, "recovery-ack.json")));
        Assert.Empty(server.Methods);

        var planPath = loaded.PlanPath;
        var ackPath = Path.Combine(loaded.DirectoryPath, "recovery-ack.json");
        var pathA = Path.Combine(loaded.DirectoryPath, "trust", "slots", "a", "boot.img");
        var pathB = Path.Combine(loaded.DirectoryPath, "trust", "slots", "b", "boot.img");
        var origPlan = File.ReadAllBytes(planPath);
        var origAck = File.ReadAllBytes(ackPath);
        var origA = File.ReadAllBytes(pathA);
        try
        {
            var mutated = origA.ToArray();
            mutated[0] ^= 0xFF;
            File.WriteAllBytes(pathA, mutated);
            Assert.Throws<InvalidDataException>(
                () => PackageImporter.LoadOfflineUpdateRecovery(saved.DirectoryPath, context, descSha));
            File.WriteAllBytes(pathA, origA);

            var ackMut = origAck.ToArray();
            ackMut[2] ^= 0xFF;
            File.WriteAllBytes(ackPath, ackMut);
            Assert.Throws<InvalidDataException>(
                () => PackageImporter.LoadOfflineUpdateRecovery(saved.DirectoryPath, context, descSha));
            File.WriteAllBytes(ackPath, origAck);

            File.WriteAllBytes(planPath, Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(origPlan).Replace('1', '9')));
            Assert.Throws<InvalidDataException>(
                () => PackageImporter.LoadOfflineUpdateRecovery(saved.DirectoryPath, context, descSha));
            File.WriteAllBytes(planPath, origPlan);

            File.Delete(pathB);
            Assert.Throws<InvalidDataException>(
                () => PackageImporter.LoadOfflineUpdateRecovery(saved.DirectoryPath, context, descSha));
            File.WriteAllBytes(pathB, bootB);

            Assert.Throws<InvalidDataException>(
                () => PackageImporter.LoadOfflineUpdateRecovery(saved.DirectoryPath, context, new string('f', 64)));
        }
        finally
        {
            File.WriteAllBytes(planPath, origPlan);
            File.WriteAllBytes(ackPath, origAck);
            File.WriteAllBytes(pathA, origA);
            if (!File.Exists(pathB)) File.WriteAllBytes(pathB, bootB);
        }

        Assert.Equal(bootB, File.ReadAllBytes(pathB));
        _ = PackageImporter.LoadOfflineUpdateRecovery(saved.DirectoryPath, context, descSha);
        Assert.Empty(server.Methods);

        var missing = Path.Combine(root, "no-such-recovery");
        Assert.Throws<InvalidDataException>(
            () => PackageImporter.LoadOfflineUpdateRecovery(missing, context, descSha));
        Assert.False(Directory.Exists(missing));
        Assert.Throws<InvalidDataException>(
            () => PackageImporter.LoadOfflineUpdateRecovery("recovery-relative", context, descSha));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => PackageImporter.LoadOfflineUpdateRecovery(saved.DirectoryPath, context, descSha, cts.Token));
        Assert.True(Directory.Exists(work));
        Assert.True(File.Exists(imported.DescriptorPath));
    }
}
