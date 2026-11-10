using System;
using System.Collections.Generic;
using System.IO;

namespace FreedomInstaller.App;

/// <summary>
/// 安装日志的纯逻辑部分：失败诊断的格式化，以及工作会话目录里的即时落盘。
/// 只依赖 BCL，测试程序集可以直接链接本文件做回归，无需启动 WPF。
/// </summary>
internal static class InstallerLog
{
    /// <summary>工作会话目录里的日志文件名；关闭窗口与追加日志写的是同一个文件。</summary>
    internal const string FileName = "installer.log";

    /// <summary>
    /// 日志里的失败诊断：真实异常类型 + 完整消息。界面可以随后把它换成简短的本地化提示，
    /// 但真正的诊断必须先以这里的内容进入日志。
    /// </summary>
    internal static string DescribeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return (exception.GetType().FullName ?? exception.GetType().Name) + ": " + exception.Message;
    }

    /// <summary>
    /// 把当前日志行写进工作会话目录。落盘失败只影响日志文件本身，
    /// 不打断安装流程，也不改写调用方已经展示的内容。
    /// </summary>
    internal static void TryPersist(string directoryPath, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        ArgumentNullException.ThrowIfNull(lines);
        try
        {
            File.WriteAllLines(Path.Combine(directoryPath, FileName), lines);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
