using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// The macOS toolchain's Python runtime expansion (python-build-standalone install_only_stripped layout),
/// exercised with synthetic tar.gz archives.
/// </summary>
public sealed class MacPythonRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mac-python-" + Guid.NewGuid().ToString("N"));

    public MacPythonRuntimeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Item(string Name, TarEntryType Type, byte[]? Data = null, string? Link = null, bool Executable = false);

    private static readonly UnixFileMode FileMode644 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly UnixFileMode FileMode755 =
        FileMode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private static List<Item> Complete() => new()
    {
        new("python/", TarEntryType.Directory),
        new("python/bin/", TarEntryType.Directory),
        new("python/bin/python3.14", TarEntryType.RegularFile, new byte[] { 1, 2, 3 }, Executable: true),
        new("python/bin/python3", TarEntryType.SymbolicLink, Link: "python3.14"),
        new("python/lib/", TarEntryType.Directory),
        new("python/lib/python3.14/", TarEntryType.Directory),
        new("python/lib/python3.14/os.py", TarEntryType.RegularFile, "# os\n"u8.ToArray()),
    };

    private string Archive(IEnumerable<Item> items)
    {
        string path = Path.Combine(_root, "runtime-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var item in items)
            {
                var entry = new PaxTarEntry(item.Type, item.Name);
                entry.Mode = item.Type == TarEntryType.Directory || item.Executable ? FileMode755 : FileMode644;
                if (item.Link != null)
                    entry.LinkName = item.Link;
                if (item.Data != null)
                    entry.DataStream = new MemoryStream(item.Data);
                writer.WriteEntry(entry);
            }
        }

        return path;
    }

    private Task<string> Expand(IEnumerable<Item> items, out string runtime)
    {
        runtime = Path.Combine(_root, "runtime-" + Guid.NewGuid().ToString("N"));
        return EdlToolchain.ExpandMacPythonRuntimeAsync(Archive(items), runtime, CancellationToken.None);
    }

    [Fact]
    public async Task Expands_files_and_directories_and_returns_the_versioned_interpreter()
    {
        string interpreter = await Expand(Complete(), out var runtime);

        Assert.Equal(Path.Combine(runtime, "python", "bin", "python3.14"), interpreter);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(interpreter));
        Assert.True(File.Exists(Path.Combine(runtime, "python", "lib", "python3.14", "os.py")));
        // The convenience symlink is accepted but never materialized.
        Assert.False(File.Exists(Path.Combine(runtime, "python", "bin", "python3")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(interpreter).HasFlag(UnixFileMode.UserExecute));
            var stdlibMode = File.GetUnixFileMode(Path.Combine(runtime, "python", "lib", "python3.14", "os.py"));
            Assert.False(stdlibMode.HasFlag(UnixFileMode.UserExecute));
            Assert.False(stdlibMode.HasFlag(UnixFileMode.GroupRead));
        }
    }

    [Fact]
    public void Bridge_options_isolate_the_interpreter_but_keep_the_script_directory() =>
        Assert.Equal(new[] { "-E", "-s" }, EdlToolchain.MacPythonOptions);

    [Theory]
    [InlineData("../escape.py")]
    [InlineData("python/../../escape.py")]
    [InlineData("/etc/escape.py")]
    [InlineData("python\\evil.py")]
    [InlineData("python/./x.py")]
    public async Task Refuses_unsafe_paths(string name)
    {
        var items = Complete();
        items.Add(new(name, TarEntryType.RegularFile, new byte[] { 0 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Expand(items, out _));
    }

    [Theory]
    [InlineData("../lib/libpython3.14.dylib")]
    [InlineData("/usr/bin/python3")]
    [InlineData("..")]
    public async Task Refuses_symlinks_that_leave_their_directory(string target)
    {
        var items = Complete();
        items.Add(new("python/bin/python", TarEntryType.SymbolicLink, Link: target));
        await Assert.ThrowsAsync<InvalidDataException>(() => Expand(items, out _));
    }

    [Theory]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.CharacterDevice)]
    [InlineData(TarEntryType.Fifo)]
    public async Task Refuses_other_entry_types(TarEntryType type)
    {
        var items = Complete();
        items.Add(new("python/bin/odd", type, Link: type == TarEntryType.HardLink ? "python3.14" : null));
        await Assert.ThrowsAsync<InvalidDataException>(() => Expand(items, out _));
    }

    [Fact]
    public async Task Refuses_duplicates_and_case_collisions()
    {
        var items = Complete();
        items.Add(new("python/lib/python3.14/OS.py", TarEntryType.RegularFile, new byte[] { 0 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Expand(items, out _));
    }

    [Theory]
    [InlineData("python/bin/python3.14")]
    [InlineData("python/lib/python3.14/os.py")]
    public async Task Refuses_an_incomplete_runtime(string missing)
    {
        var items = Complete();
        items.RemoveAll(i => i.Name == missing);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Expand(items, out _));
        Assert.Contains("incomplete", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_a_non_executable_interpreter()
    {
        if (OperatingSystem.IsWindows())
            return;
        var items = Complete();
        items.RemoveAll(i => i.Name == "python/bin/python3.14");
        items.Add(new("python/bin/python3.14", TarEntryType.RegularFile, new byte[] { 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Expand(items, out _));
    }

    [Fact]
    public async Task Refuses_a_missing_archive()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => EdlToolchain.ExpandMacPythonRuntimeAsync(
            Path.Combine(_root, "absent.tar.gz"), Path.Combine(_root, "runtime"), CancellationToken.None));
    }
}
