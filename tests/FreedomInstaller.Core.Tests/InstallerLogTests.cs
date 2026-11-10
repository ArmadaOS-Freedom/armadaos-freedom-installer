using System;
using System.IO;
using System.Linq;
using FreedomInstaller.App;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// LOG01：「内部安装」准备失败的真实诊断必须进入日志，且工作会话存在时追加一行就落盘。
/// 这里链接 App 侧的纯逻辑源文件来回归，不需要启动 WPF 窗口。
/// </summary>
public sealed class InstallerLogTests
{
    [Theory]
    [InlineData("requires-compatible-bootloader: the original vbmeta_a root or its boot chain is unknown.")]
    [InlineData("requires-complete-backup-with-efisp: boot recovery material is incomplete.")]
    [InlineData("gbl-runtime-region-unknown: the two slots disagree.")]
    [InlineData("preparation failed before any write")]
    public void DescribeFailureKeepsRealTypeAndFullMessage(string message)
    {
        var exception = new InvalidOperationException(message);

        string line = InstallerLog.DescribeFailure(exception);

        // 类型用完整名称，消息一字不改：界面随后换成简短提示也不影响这里的内容。
        Assert.Equal(typeof(InvalidOperationException).FullName + ": " + message, line);
        Assert.Contains(nameof(InvalidOperationException), line);
        Assert.Contains(message, line);
    }

    [Fact]
    public void DescribeFailureKeepsRealTypeForNonRefusalFailures()
    {
        var exception = new IOException("disk full while writing prepared artifacts");

        string line = InstallerLog.DescribeFailure(exception);

        Assert.StartsWith(typeof(IOException).FullName!, line);
        Assert.EndsWith(": disk full while writing prepared artifacts", line);
    }

    [Fact]
    public void AppendPersistsToWorkSessionWithoutClosingTheWindow()
    {
        string directory = NewDirectory();
        string[] first = { "10:00:00  Dynamic.InternalInstall.Prepared.StatusReady" };

        InstallerLog.TryPersist(directory, first);

        // 第一次追加之后就能读到文件，不需要任何关闭窗口的动作。
        string path = Path.Combine(directory, "installer.log");
        Assert.Equal(first, File.ReadAllLines(path));

        string[] second = first.Append(
            "10:00:01  System.InvalidOperationException: requires-compatible-bootloader: no proof").ToArray();
        InstallerLog.TryPersist(directory, second);

        Assert.Equal(second, File.ReadAllLines(path));
    }

    [Fact]
    public void PersistenceFailureStaysInsideTheLogFile()
    {
        string missing = Path.Combine(NewDirectory(), "not-created");

        // 目录不存在只影响日志文件，异常不冒泡打断安装流程。
        InstallerLog.TryPersist(missing, new[] { "line" });

        Assert.False(File.Exists(Path.Combine(missing, "installer.log")));
    }

    private static string NewDirectory()
    {
        string root = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
            ?? throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be set for tests.");
        string directory = Path.Combine(root, "log01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
