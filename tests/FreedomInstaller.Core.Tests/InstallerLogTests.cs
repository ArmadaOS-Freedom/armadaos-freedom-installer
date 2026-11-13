using System;
using System.IO;
using System.Linq;
using FreedomInstaller.App;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// LOG01 / REVIEW01：「内部安装」准备失败的真实诊断必须进入日志；追加日志只把不可变快照排队，
/// 由单个后台写线程按 FIFO 串行落盘，关闭窗口先等队列清空再写最终快照。
/// 这里链接 App 侧的纯逻辑源文件来回归，不需要启动 WPF 窗口。
/// </summary>
public sealed class InstallerLogTests
{
    /// <summary>诊断行必须原样采用 <see cref="Exception.ToString"/>：真实类型全名与完整消息都要保留。</summary>
    [Theory]
    [InlineData("requires-compatible-bootloader: the original vbmeta_a root or its boot chain is unknown.")]
    [InlineData("requires-complete-backup-with-efisp: boot recovery material is incomplete.")]
    [InlineData("gbl-runtime-region-unknown: the two slots disagree.")]
    [InlineData("preparation failed before any write")]
    public void DescribeFailureUsesExceptionToString(string message)
    {
        var exception = new InvalidOperationException(message);

        string line = InstallerLog.DescribeFailure(exception);

        // 原样使用异常自己的文本：类型、完整消息、内部原因与堆栈都保留。
        Assert.Equal(exception.ToString(), line);
        Assert.Contains(typeof(InvalidOperationException).FullName!, line);
        Assert.Contains(message, line);
    }

    /// <summary>非拒绝类失败（例如写盘 IO 错误）同样保留真实异常类型，不被换成笼统文案。</summary>
    [Fact]
    public void DescribeFailureKeepsRealTypeForNonRefusalFailures()
    {
        var exception = new IOException("disk full while writing prepared artifacts");

        string line = InstallerLog.DescribeFailure(exception);

        Assert.Equal(exception.ToString(), line);
        Assert.StartsWith(typeof(IOException).FullName!, line);
    }

    /// <summary>包装异常的原始内部原因（类型与消息）也必须出现在诊断行里。</summary>
    [Fact]
    public void DescribeFailureKeepsWrappedInnerCause()
    {
        var exception = new InvalidOperationException(
            "preparation failed", new IOException("the captured device disappeared"));

        string line = InstallerLog.DescribeFailure(exception);

        Assert.Equal(exception.ToString(), line);
        Assert.Contains(typeof(IOException).FullName!, line);
        Assert.Contains("the captured device disappeared", line);
    }

    /// <summary>入队后 <see cref="InstallerLog.Flush"/> 返回即可读到内容，不必等窗口关闭。</summary>
    [Fact]
    public void FlushWithoutClosingPersistsQueuedSnapshot()
    {
        string directory = NewDirectory();
        string[] lines = { "10:00:00  Dynamic.InternalInstall.Prepared.StatusReady" };

        InstallerLog.EnqueuePersist(directory, lines);
        InstallerLog.Flush();

        // 不需要关闭窗口：队列排空后内容已经可读。
        Assert.Equal(lines, File.ReadAllLines(Path.Combine(directory, "installer.log")));
    }

    /// <summary>三份累积快照按 FIFO 串行写入：最终文件是最后入队的那份，不被更早的写入覆盖。</summary>
    [Fact]
    public void QueuedSnapshotsKeepFifoOrder()
    {
        string directory = NewDirectory();
        string path = Path.Combine(directory, "installer.log");
        string[] first = { "10:00:00  first" };
        string[] second = first.Append("10:00:01  second").ToArray();
        string[] third = second.Append("10:00:02  third").ToArray();

        InstallerLog.EnqueuePersist(directory, first);
        InstallerLog.EnqueuePersist(directory, second);
        InstallerLog.EnqueuePersist(directory, third);
        InstallerLog.Flush();

        // 最后写入的必须是最后入队的那份快照，不能被更早的写入覆盖。
        Assert.Equal(third, File.ReadAllLines(path));
    }

    /// <summary>单份快照里的每条记录都按原顺序落盘，不丢行也不改序。</summary>
    [Fact]
    public void QueuedSnapshotPreservesEveryEntryInOrder()
    {
        string directory = NewDirectory();
        string[] lines = Enumerable.Range(0, 200)
            .Select(index => "10:00:00  entry " + index.ToString("000"))
            .ToArray();

        InstallerLog.EnqueuePersist(directory, lines);
        InstallerLog.Flush();

        Assert.Equal(lines, File.ReadAllLines(Path.Combine(directory, "installer.log")));
    }

    /// <summary>入队时复制数组：调用方之后改写原数组不影响已排队的日志行。</summary>
    [Fact]
    public void QueuedSnapshotIsImmutableAfterEnqueue()
    {
        string directory = NewDirectory();
        string[] lines = { "10:00:00  original" };

        InstallerLog.EnqueuePersist(directory, lines);
        lines[0] = "10:00:00  mutated on the caller thread";
        InstallerLog.Flush();

        // 入队时复制快照：调用方之后再改数组不影响已经排队的日志行。
        Assert.Equal(new[] { "10:00:00  original" }, File.ReadAllLines(Path.Combine(directory, "installer.log")));
    }

    /// <summary>同步落盘直接写会话目录里的同一个日志文件；第二次写入把文件覆盖成最新快照。</summary>
    [Fact]
    public void AppendPersistsToWorkSessionWithoutClosingTheWindow()
    {
        string directory = NewDirectory();
        string[] first = { "10:00:00  Dynamic.InternalInstall.Prepared.StatusReady" };

        InstallerLog.TryPersist(directory, first);

        // 关闭窗口的最终快照仍写同一个文件。
        string path = Path.Combine(directory, "installer.log");
        Assert.Equal(first, File.ReadAllLines(path));

        string[] second = first.Append(
            "10:00:01  System.InvalidOperationException: requires-compatible-bootloader: no proof").ToArray();
        InstallerLog.TryPersist(directory, second);

        Assert.Equal(second, File.ReadAllLines(path));
    }

    /// <summary>目标目录不存在时失败只影响日志文件：不抛异常，也不留下半份文件。</summary>
    [Fact]
    public void PersistenceFailureStaysInsideTheLogFile()
    {
        string missing = Path.Combine(NewDirectory(), "not-created");

        // 目录不存在只影响日志文件，异常不冒泡打断安装流程。
        InstallerLog.TryPersist(missing, new[] { "line" });

        Assert.False(File.Exists(Path.Combine(missing, "installer.log")));
    }

    /// <summary>队列里的写入失败不能卡住队列：这份快照丢掉，后续入队仍能正常落盘。</summary>
    [Fact]
    public void QueuedPersistenceFailureStaysNonFatal()
    {
        string missing = Path.Combine(NewDirectory(), "not-created");

        InstallerLog.EnqueuePersist(missing, new[] { "line" });
        InstallerLog.Flush();

        // 队列本身必须继续前进：失败只丢掉这份快照。
        Assert.False(File.Exists(Path.Combine(missing, "installer.log")));

        // 后续入队仍然正常落盘。
        string directory = NewDirectory();
        InstallerLog.EnqueuePersist(directory, new[] { "after failure" });
        InstallerLog.Flush();
        Assert.Equal(new[] { "after failure" }, File.ReadAllLines(Path.Combine(directory, "installer.log")));
    }

    /// <summary>在 TEST_ARTIFACT_ROOT 下建唯一子目录；变量未设置时直接失败，避免测试写到别处。</summary>
    private static string NewDirectory()
    {
        string root = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
            ?? throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be set for tests.");
        string directory = Path.Combine(root, "log01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
