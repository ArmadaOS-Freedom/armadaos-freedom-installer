using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class BoardRecoveryAdbProbeTests
{
    private const string Sentinel = "__ARMADA_BACKUP_LIST_DONE__";
    private const string Serial = "SERIAL-123";
    private const string AdbPath = "/opt/adb";

    private sealed class RecordingRunner : ICommandRunner
    {
        private readonly Func<ProcessCommand, CancellationToken, Task<ProcessResult>> _handler;

        public RecordingRunner(Func<ProcessCommand, CancellationToken, Task<ProcessResult>> handler)
            => _handler = handler;

        public ProcessCommand? Seen { get; private set; }
        public CancellationToken SeenToken { get; private set; }
        public int Calls { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
        {
            Calls++;
            Seen = command;
            SeenToken = cancellationToken;
            return _handler(command, cancellationToken);
        }
    }

    private static Task<ProcessResult> Stdout(string stdout, int exitCode = 0)
        => Task.FromResult(new ProcessResult(exitCode, stdout, string.Empty));

    [Fact]
    public async Task Present_Lf_ReturnsTrue()
    {
        var runner = new RecordingRunner((_, _) => Stdout($"misc\nARMADA_BACKUP\nuserdata\n{Sentinel}\n"));

        var present = await BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner);

        Assert.True(present);
        Assert.Equal(1, runner.Calls);
        Assert.NotNull(runner.Seen);
        Assert.Equal(AdbPath, runner.Seen!.Executable);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.Seen.Timeout);
        Assert.Equal(new[] { "-s", Serial, "shell" }, runner.Seen.Arguments.Take(3));
    }

    [Fact]
    public async Task Present_CrLf_ReturnsTrue()
    {
        var runner = new RecordingRunner((_, _) => Stdout($"misc\r\nARMADA_BACKUP\r\n{Sentinel}\r\n"));

        Assert.True(await BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner));
    }

    [Fact]
    public async Task Absent_ReturnsFalse()
    {
        var runner = new RecordingRunner((_, _) => Stdout($"misc\nuserdata\n{Sentinel}\n"));

        Assert.False(await BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner));
    }

    [Fact]
    public async Task SimilarLabel_DoesNotMatch_ReturnsFalse()
    {
        var runner = new RecordingRunner((_, _) => Stdout($"ARMADA_BACKUP_OLD\nARMADA_BACKUPX\n{Sentinel}\n"));

        Assert.False(await BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner));
    }

    [Fact]
    public async Task NonZeroExit_EvenWithSuccessLookingStdout_Throws()
    {
        var runner = new RecordingRunner((_, _) =>
            Task.FromResult(new ProcessResult(1, $"ARMADA_BACKUP\n{Sentinel}\n", "ls: /dev/block/by-name: No such file or directory")));

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner));
        Assert.Contains("exit code 1", ex.Message);
    }

    [Fact]
    public async Task MissingSentinel_Throws()
    {
        var runner = new RecordingRunner((_, _) => Stdout("misc\nARMADA_BACKUP\n"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner));
    }

    [Fact]
    public async Task EmptyStdout_Throws()
    {
        var runner = new RecordingRunner((_, _) => Stdout(string.Empty));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner));
    }

    [Fact]
    public async Task SentinalInsideOutputButNotFinal_Throws()
    {
        var runner = new RecordingRunner((_, _) => Stdout($"misc\n{Sentinel}\nARMADA_BACKUP\n"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner));
    }

    [Fact]
    public async Task Timeout_Propagates()
    {
        var runner = new RecordingRunner((_, _) =>
            Task.FromException<ProcessResult>(new TimeoutException("adb timed out")));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner));
        Assert.Equal("adb timed out", ex.Message);
    }

    [Fact]
    public async Task IOException_Propagates()
    {
        var runner = new RecordingRunner((_, _) =>
            Task.FromException<ProcessResult>(new IOException("adb not found")));

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner));
        Assert.Equal("adb not found", ex.Message);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var runner = new RecordingRunner((_, ct) =>
            Task.FromException<ProcessResult>(new OperationCanceledException(ct)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner, cts.Token));
        Assert.Equal(cts.Token, runner.SeenToken);
    }

    [Fact]
    public async Task Command_SelectsSerialAndBoundedTimeout()
    {
        var runner = new RecordingRunner((_, _) => Stdout($"misc\n{Sentinel}\n"));

        await BoardRecoveryBackup.DetectAdbAsync(AdbPath, Serial, runner);

        var seen = runner.Seen!;
        Assert.Equal(AdbPath, seen.Executable);
        Assert.Equal("-s", seen.Arguments[0]);
        Assert.Equal(Serial, seen.Arguments[1]);
        Assert.Equal("shell", seen.Arguments[2]);
        Assert.Contains("ls -1 /dev/block/by-name", seen.Arguments[3]);
        Assert.Contains("&& echo " + Sentinel, seen.Arguments[3]);
        Assert.Equal(TimeSpan.FromSeconds(10), seen.Timeout);
        Assert.Equal(4, seen.Arguments.Count);
    }
}
