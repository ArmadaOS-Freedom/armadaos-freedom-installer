using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.App;

/// <summary>
/// 安装日志的纯逻辑部分：失败诊断的格式化，以及工作会话目录里的追加落盘。
/// 每一份不可变的增量批次都交给一个串行的后台队列，界面线程不做整文件写入；
/// 文件只追加，从不截断已经写入的整场会话内容，因此关闭窗口只需等队列排空。
/// 只依赖 BCL，测试程序集可以直接链接本文件做回归，无需启动 WPF。
/// </summary>
internal static class InstallerLog
{
    /// <summary>工作会话目录里的日志文件名；关闭窗口与追加日志写的是同一个文件。</summary>
    internal const string FileName = "installer.log";

    private static readonly object Gate = new();

    private static readonly Queue<PersistEntry> Pending = new();

    /// <summary>已入队但尚未写完的条数；归零时唤醒 <see cref="Flush"/>。</summary>
    private static int _outstanding;

    /// <summary>非空表示后台写线程还在运行；空闲时归空，下一次入队再启动，绝不常驻阻塞线程。</summary>
    private static Task? _worker;

    /// <summary>
    /// 日志里的失败诊断直接使用 <see cref="Exception.ToString"/>：真实异常类型、完整消息、
    /// 内部原因与调用堆栈都会保留。界面随后可以把它换成简短的本地化提示，但诊断内容不变。
    /// </summary>
    internal static string DescribeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.ToString();
    }

    /// <summary>
    /// 把一份增量批次排进串行后台队列。调用方在界面线程上只传新产生的日志行，这里再复制一份，
    /// 因此队列里的内容不会再被后续追加或语言切换改写。落盘失败只影响日志文件本身。
    /// </summary>
    internal static void EnqueuePersist(string directoryPath, string[] lines)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        ArgumentNullException.ThrowIfNull(lines);

        var batch = new PersistEntry(directoryPath, (string[])lines.Clone());

        lock (Gate)
        {
            Pending.Enqueue(batch);
            _outstanding++;
            if (_worker is null || _worker.IsCompleted)
            {
                _worker = Task.Run(Drain);
            }

            Monitor.PulseAll(Gate);
        }
    }

    /// <summary>
    /// 等待队列里的全部写入完成。只在关闭窗口时调用，因此界面最多等待已经排队的那几行。
    /// 没有排队项时立即返回。
    /// </summary>
    internal static void Flush()
    {
        lock (Gate)
        {
            while (_outstanding > 0)
            {
                Monitor.Wait(Gate);
            }
        }
    }

    /// <summary>
    /// 把一份增量批次同步追加到工作会话目录里的日志文件末尾，已有内容原样保留；
    /// 文件不存在时就创建它。落盘失败只影响日志文件本身，不打断安装流程，
    /// 也不改写调用方已经展示的内容。
    /// </summary>
    internal static void TryPersist(string directoryPath, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        ArgumentNullException.ThrowIfNull(lines);
        try
        {
            File.AppendAllLines(Path.Combine(directoryPath, FileName), lines);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 唯一的写线程：只在队列非空时存在，逐份按 FIFO 顺序追加，绝不并发写同一个文件。
    /// 任何写入失败都不允许卡住队列或冒泡到调用方。
    /// </summary>
    private static void Drain()
    {
        while (true)
        {
            PersistEntry entry;
            lock (Gate)
            {
                if (Pending.Count == 0)
                {
                    _worker = null;
                    Monitor.PulseAll(Gate);
                    return;
                }

                entry = Pending.Dequeue();
            }

            try
            {
                TryPersist(entry.DirectoryPath, entry.Lines);
            }
            catch (Exception)
            {
                // 日志落盘永远不是致命路径：失败只丢掉这一份批次，队列继续前进。
            }
            finally
            {
                lock (Gate)
                {
                    _outstanding--;
                    if (_outstanding == 0)
                    {
                        Monitor.PulseAll(Gate);
                    }
                }
            }
        }
    }

    private sealed class PersistEntry
    {
        internal PersistEntry(string directoryPath, string[] lines)
        {
            DirectoryPath = directoryPath;
            Lines = lines;
        }

        internal string DirectoryPath { get; }

        internal string[] Lines { get; }
    }
}
