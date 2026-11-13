using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace FreedomInstaller.Core;

public static partial class PackageImporter
{
    private const int MaxDescriptorBytes = 2 * 1024 * 1024;
    private const int MaxSignatureBytes = 4096;
    private const int MediaHashBufferBytes = 1024 * 1024;
    private const long MaxEntryBytes = 1L << 40;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int WindowsInvalidEntryAttributes =
        (int)(FileAttributes.Directory | FileAttributes.ReparsePoint);

    private const int NativeNoEntry = 2;

    // Linux open(2) flags per UAPI: the generic x86/x64 headers put O_NOFOLLOW at 1<<17, the
    // arm/arm64 headers at 1<<15 (where x86 has O_LARGEFILE); O_NONBLOCK and O_CLOEXEC agree.
    private const int LinuxNonBlock = 0x800;
    private const int LinuxNoFollowGeneric = 0x20000;
    private const int LinuxNoFollowArm = 0x8000;
    private const int LinuxCloseOnExec = 0x80000;

    private const int MacOpenFlags =
        0x4 /* O_NONBLOCK */ | 0x100 /* O_NOFOLLOW */ | 0x1000000 /* O_CLOEXEC */;
    private const int FreeBsdOpenFlags =
        0x4 /* O_NONBLOCK */ | 0x100 /* O_NOFOLLOW */ | 0x100000 /* O_CLOEXEC */;

    public static Task<UpdatePackageDescriptor> InspectUpdatePackageAsync(
        string sourcePath,
        string expectedModel,
        string operation,
        string installerVersion,
        CancellationToken token = default) =>
        Task.Run(
            () =>
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    throw new InvalidDataException("未指定升级包路径。");
                }

                var fullSource = Path.GetFullPath(sourcePath);
                var source = ResolveArchiveSource(fullSource);
                if (source.IsNativeRar)
                {
                    throw new InvalidDataException("签名升级包不支持 RAR 分卷格式。");
                }

                EnsureRegularSourceFiles(source);
                using var stream = source.OpenStream();
                var descriptor = ReadUpdateDescriptor(
                    source,
                    expectedModel,
                    operation,
                    installerVersion,
                    required: true,
                    token)!;

                VerifyUpdateMedia(source, stream, descriptor, token);
                return descriptor;
            },
            token);

    internal static void VerifyUpdateMediaFile(
        string sourcePath,
        UpdatePackageDescriptor descriptor,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        token.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new InvalidDataException("未指定升级包路径。");
        }

        var source = ResolveArchiveSource(Path.GetFullPath(sourcePath));
        if (source.IsNativeRar)
        {
            throw new InvalidDataException("签名升级包不支持 RAR 分卷格式。");
        }

        EnsureRegularSourceFiles(source);
        using var stream = source.OpenStream();
        VerifyUpdateMedia(source, stream, descriptor, token);
    }

    private static UpdatePackageDescriptor? ReadUpdateDescriptor(
        ArchiveSource source,
        string expectedModel,
        string operation,
        string installerVersion,
        bool required,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (source.Parts.Count == 0)
        {
            throw new InvalidDataException("升级包源文件列表无效。");
        }

        var firstPart = Path.GetFullPath(source.Parts[0]);
        var directory = Path.GetDirectoryName(firstPart);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidDataException($"无法解析升级包目录：{firstPart}。");
        }

        var archiveBaseName = GetArchiveBaseFileName(firstPart);
        var descriptorPath = Path.Combine(directory, archiveBaseName + ".update.json");
        var signaturePath = Path.Combine(directory, archiveBaseName + ".update.json.sig");

        var descriptorExists = Path.Exists(descriptorPath);
        var signatureExists = Path.Exists(signaturePath);
        if (!descriptorExists && !signatureExists)
        {
            if (!required)
            {
                return null;
            }

            throw new InvalidDataException($"缺少升级包描述文件或签名：{descriptorPath}。");
        }

        if (!descriptorExists || !signatureExists)
        {
            throw new InvalidDataException($"升级包描述文件与签名必须同时存在：{descriptorPath}。");
        }

        if (source.IsNativeRar)
        {
            throw new InvalidDataException("升级包源文件列表无效。");
        }

        EnsureRegularSourceFiles(source);

        var descriptorBytes = ReadBoundedRegularFile(descriptorPath, MaxDescriptorBytes, token);
        var signatureBytes = ReadBoundedRegularFile(signaturePath, MaxSignatureBytes, token);

        var descriptor = UpdatePackageDescriptor.VerifyAndParse(descriptorBytes, signatureBytes);
        var declaredArchiveName = descriptor.Root
            .GetProperty("archive")
            .GetProperty("fileName")
            .GetString();

        if (!string.Equals(archiveBaseName, declaredArchiveName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"升级包文件名不匹配：实际为 {archiveBaseName}，描述文件要求 {declaredArchiveName}。");
        }

        descriptor.RequireOperation(expectedModel, operation, installerVersion);
        return descriptor;
    }

    private static void VerifyUpdateMedia(
        ArchiveSource source,
        Stream stream,
        UpdatePackageDescriptor descriptor,
        CancellationToken token,
        VerifiedArchiveSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(descriptor);
        token.ThrowIfCancellationRequested();

        if (source.IsNativeRar || source.Parts.Count == 0)
        {
            throw new InvalidDataException("升级包分卷源不受支持。");
        }

        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new InvalidDataException("升级包数据流不支持随机读取与校验。");
        }

        if (snapshot is null)
        {
            // The parts are read directly here, so links and reparse points are rejected right before
            // the read. A snapshot was captured from handles that were already checked.
            EnsureRegularSourceFiles(source);
        }

        var root = descriptor.Root;
        var archiveNode = root.GetProperty("archive");
        var expectedArchiveName = archiveNode.GetProperty("fileName").GetString()
            ?? throw new InvalidDataException("升级包文件名缺失。");
        var expectedArchiveBytes = archiveNode.GetProperty("bytes").GetInt64();
        var expectedArchiveSha256 = archiveNode.GetProperty("sha256").GetString()
            ?? throw new InvalidDataException("升级包哈希缺失。");

        var actualBaseName = snapshot?.ArchiveBaseName ?? GetArchiveBaseFileName(source.Parts[0]);
        if (!string.Equals(actualBaseName, expectedArchiveName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"升级包名称不匹配：实际为 {actualBaseName}，期望 {expectedArchiveName}。");
        }

        try
        {
            VerifyStreamAndParts(
                source,
                stream,
                root,
                expectedArchiveBytes,
                expectedArchiveSha256,
                token,
                snapshot);

            var expectedEntries = ReadExpectedEntries(root);
            VerifyZipMetadataWithSharpCompress(stream, expectedEntries, token);
            VerifyZipContentsWithBcl(stream, expectedEntries, token);
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }
        }
    }

    private static void VerifyStreamAndParts(
        ArchiveSource source,
        Stream stream,
        JsonElement root,
        long expectedArchiveBytes,
        string expectedArchiveSha256,
        CancellationToken token,
        VerifiedArchiveSnapshot? snapshot = null)
    {
        var parts = snapshot?.Parts;
        var isSplitSource = snapshot is not null
            ? snapshot.IsSplitSource
            : IsSplitVolumeFileName(Path.GetFileName(source.Parts[0]));

        stream.Position = 0;

        if (stream.Length != expectedArchiveBytes)
        {
            throw new InvalidDataException(
                $"升级包总大小不匹配：实际为 {stream.Length} 字节，期望 {expectedArchiveBytes} 字节。");
        }

        var buffer = new byte[MediaHashBufferBytes];

        if (!isSplitSource)
        {
            var actualCount = parts?.Count ?? source.Parts.Count;
            if (actualCount != 1)
            {
                throw new InvalidDataException("单文件升级包包含多个输入源。");
            }

            if (parts is not null)
            {
                // The snapshot recorded the length of the part it copied, so the original file is not
                // reopened to repeat the size check.
                if (parts[0].Bytes != expectedArchiveBytes)
                {
                    throw new InvalidDataException("升级包文件大小与受保护流不一致。");
                }
            }
            else
            {
                using var singlePartStream = OpenPart(source.Parts[0]);
                if (singlePartStream.Length != expectedArchiveBytes)
                {
                    throw new InvalidDataException("升级包文件大小与受保护流不一致。");
                }
            }

            var archiveHashLimit = expectedArchiveBytes == long.MaxValue
                ? long.MaxValue
                : expectedArchiveBytes + 1;
            var (digest, totalBytes) = HashStreamBounded(stream, archiveHashLimit, buffer, token);
            if (totalBytes != expectedArchiveBytes ||
                !HashesEqual(digest, expectedArchiveSha256))
            {
                throw new InvalidDataException("升级包整体 SHA-256 校验失败。");
            }

            return;
        }

        if (!root.TryGetProperty("parts", out var partsElement) ||
            partsElement.ValueKind != JsonValueKind.Array ||
            partsElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException("分卷升级包缺少已签名的分卷列表。");
        }

        var declaredCount = partsElement.GetArrayLength();
        var availableParts = parts?.Count ?? source.Parts.Count;
        if (availableParts != declaredCount)
        {
            throw new InvalidDataException(
                $"分卷数量不匹配：实际为 {availableParts}，描述文件要求 {declaredCount}。");
        }

        using var wholeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalRead = 0;
        var index = 0;

        foreach (var partNode in partsElement.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();

            var expectedPartName = partNode.GetProperty("fileName").GetString()
                ?? throw new InvalidDataException("分卷文件名缺失。");
            var expectedPartBytes = partNode.GetProperty("bytes").GetInt64();
            var expectedPartSha256 = partNode.GetProperty("sha256").GetString()
                ?? throw new InvalidDataException("分卷哈希缺失。");

            // With a snapshot the original names and byte counts captured while copying stand in for
            // the originals, so nothing is reopened and the digest below is taken from the snapshot.
            var actualPartName = parts is not null
                ? parts[index].Name
                : Path.GetFileName(source.Parts[index]);
            if (!string.Equals(actualPartName, expectedPartName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"第 {index + 1} 卷文件名不匹配：实际为 {actualPartName}，期望 {expectedPartName}。");
            }

            if (parts is not null)
            {
                if (parts[index].Bytes != expectedPartBytes)
                {
                    throw new InvalidDataException(
                        $"分卷 {actualPartName} 大小不匹配：实际为 {parts[index].Bytes}，期望 {expectedPartBytes}。");
                }
            }
            else
            {
                var actualPartPath = source.Parts[index];
                EnsureRegularFile(actualPartPath);
                using var partFile = OpenPart(actualPartPath);
                if (partFile.Length != expectedPartBytes)
                {
                    throw new InvalidDataException(
                        $"分卷 {actualPartName} 大小不匹配：实际为 {partFile.Length}，期望 {expectedPartBytes}。");
                }
            }

            using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = expectedPartBytes;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, toRead);
                if (read <= 0)
                {
                    throw new InvalidDataException($"分卷 {actualPartName} 在读取结束前提前截断。");
                }

                partHash.AppendData(buffer, 0, read);
                wholeHash.AppendData(buffer, 0, read);
                remaining -= read;
                totalRead += read;
            }

            var partDigest = Convert.ToHexStringLower(partHash.GetHashAndReset());
            if (!HashesEqual(partDigest, expectedPartSha256))
            {
                throw new InvalidDataException($"分卷 {actualPartName} 的 SHA-256 校验失败。");
            }

            index++;
        }

        if (stream.Read(buffer, 0, 1) != 0)
        {
            throw new InvalidDataException("分卷流包含超出签名长度的尾部数据。");
        }

        var wholeDigest = Convert.ToHexStringLower(wholeHash.GetHashAndReset());
        if (totalRead != expectedArchiveBytes || !HashesEqual(wholeDigest, expectedArchiveSha256))
        {
            throw new InvalidDataException("分卷合并后的整体 SHA-256 或字节数校验失败。");
        }
    }

    /// <summary>
    /// Reads the archive byte count the signature declares. It is the exact size of the private
    /// snapshot, so a part set that grew past it is abandoned before a single byte is copied.
    /// </summary>
    private static long ReadExpectedArchiveBytes(UpdatePackageDescriptor descriptor)
    {
        if (!descriptor.Root.TryGetProperty("archive", out var archive)
            || !archive.TryGetProperty("bytes", out var bytesElement)
            || !bytesElement.TryGetInt64(out var bytes)
            || bytes <= 0)
        {
            throw new InvalidDataException("升级包描述文件的归档大小无效。");
        }

        return bytes;
    }

    /// <summary>
    /// Copies the complete logical archive of <paramref name="partPaths"/> once into a private,
    /// seekable snapshot below <paramref name="root"/>. Volumes are opened in order, the length is taken
    /// from the opened handle and exactly that many bytes are copied, so the copy can neither grow past
    /// the signed size nor follow a rename that happens afterwards.
    /// </summary>
    internal static VerifiedArchiveSnapshot CaptureArchiveSnapshot(
        IReadOnlyList<string> partPaths,
        long expectedBytes,
        string root,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(partPaths);
        ArgumentNullException.ThrowIfNull(root);

        if (partPaths.Count == 0)
        {
            throw new InvalidDataException("升级包源文件列表无效。");
        }

        using var logical = partPaths.Count == 1
            ? OpenPart(partPaths[0])
            : (Stream)new ConcatenatedArchiveStream(partPaths);

        IReadOnlyList<long> lengths = logical is ConcatenatedArchiveStream concatenated
            ? concatenated.PartLengths
            : new[] { logical.Length };

        var names = new string[partPaths.Count];
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = Path.GetFileName(partPaths[index]);
        }

        return VerifiedArchiveSnapshot.Copy(logical, names, lengths, expectedBytes, root, token);
    }

    /// <summary>
    /// Verifies the signed media of a captured snapshot exactly as a signed import does, without
    /// reopening the original parts. Used by the focused media tests to exercise the snapshot path on
    /// every platform.
    /// </summary>
    internal static void VerifyCapturedSnapshot(
        VerifiedArchiveSnapshot snapshot,
        IReadOnlyList<string> originalPartPaths,
        UpdatePackageDescriptor descriptor,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(originalPartPaths);
        ArgumentNullException.ThrowIfNull(descriptor);

        var source = new ArchiveSource(originalPartPaths, nativeRar: false);
        VerifyUpdateMedia(source, snapshot.Stream, descriptor, token, snapshot);
    }

    private static void VerifyZipMetadataWithSharpCompress(
        Stream stream,
        IReadOnlyDictionary<string, DescriptorFileEntry> expectedEntries,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        stream.Position = 0;

        try
        {
            using var archive = ArchiveFactory.OpenArchive(
                stream,
                new ReaderOptions { LeaveStreamOpen = true });

            if (archive.Type != ArchiveType.Zip)
            {
                throw new InvalidDataException("升级包容器必须是 ZIP 归档。");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();

                if (entry.IsDirectory)
                {
                    throw new InvalidDataException($"升级包内不允许存在目录项：{entry.Key}。");
                }

                if (entry.IsEncrypted)
                {
                    throw new InvalidDataException($"升级包内不允许存在加密项：{entry.Key}。");
                }

                if (entry.CompressionType != CompressionType.None)
                {
                    throw new InvalidDataException($"升级包条目必须使用 ZIP Store（无压缩）：{entry.Key}。");
                }

                var key = entry.Key ?? throw new InvalidDataException("升级包包含空名称条目。");
                if (!seen.Add(key))
                {
                    throw new InvalidDataException($"升级包包含重复条目：{key}。");
                }

                if (!expectedEntries.TryGetValue(key, out var expected))
                {
                    throw new InvalidDataException($"升级包包含未签名的额外条目：{key}。");
                }

                if (entry.Size != expected.Bytes || entry.CompressedSize != expected.Bytes)
                {
                    throw new InvalidDataException($"升级包条目大小与描述文件不一致：{key}。");
                }
            }

            if (seen.Count != expectedEntries.Count)
            {
                throw new InvalidDataException("升级包缺少描述文件中声明的条目。");
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            throw new InvalidDataException($"无法解析 ZIP 升级包结构：{ex.Message}", ex);
        }
    }

    private static void VerifyZipContentsWithBcl(
        Stream stream,
        IReadOnlyDictionary<string, DescriptorFileEntry> expectedEntries,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        stream.Position = 0;

        var buffer = new byte[MediaHashBufferBytes];

        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();

                var name = entry.FullName;
                if (string.IsNullOrEmpty(name) || name.EndsWith('/') || name.EndsWith('\\'))
                {
                    throw new InvalidDataException($"升级包包含目录或非法条目：{name}。");
                }

                if (!seen.Add(name))
                {
                    throw new InvalidDataException($"升级包包含重复的 ZIP 条目：{name}。");
                }

                if (!expectedEntries.TryGetValue(name, out var expected))
                {
                    throw new InvalidDataException($"升级包包含未声明的 ZIP 条目：{name}。");
                }

                if (entry.IsEncrypted)
                {
                    throw new InvalidDataException($"升级包条目已加密：{name}。");
                }

                var externalAttributes = entry.ExternalAttributes;
                if ((externalAttributes & WindowsInvalidEntryAttributes) != 0)
                {
                    throw new InvalidDataException($"升级包条目带有目录或重解析点属性：{name}。");
                }

                var unixFileType = (externalAttributes >> 16) & UnixFileTypeMask;
                if (unixFileType != 0 && unixFileType != UnixRegularFile)
                {
                    throw new InvalidDataException($"升级包条目不是普通文件：{name}。");
                }

                if (expected.Bytes < 0 || expected.Bytes > MaxEntryBytes ||
                    entry.Length != expected.Bytes || entry.CompressedLength != expected.Bytes)
                {
                    throw new InvalidDataException($"升级包条目长度不匹配：{name}。");
                }

                using (var rawStream = entry.Open())
                {
                    var (rawDigest, rawBytes) = HashStreamBounded(
                        rawStream,
                        expected.Bytes + 1,
                        buffer,
                        token);

                    if (rawBytes != expected.Bytes || !HashesEqual(rawDigest, expected.Sha256))
                    {
                        throw new InvalidDataException($"升级包条目内容哈希不匹配：{name}。");
                    }
                }

                if (string.Equals(expected.Compression, "gzip", StringComparison.Ordinal))
                {
                    if (expected.ExpandedBytes < 0 || expected.ExpandedBytes > MaxEntryBytes)
                    {
                        throw new InvalidDataException($"升级包条目解压大小无效：{name}。");
                    }

                    using var rawStream = entry.Open();
                    using var gzipStream = new GZipStream(rawStream, CompressionMode.Decompress, leaveOpen: false);
                    var (expandedDigest, expandedBytes) = HashStreamBounded(
                        gzipStream,
                        expected.ExpandedBytes + 1,
                        buffer,
                        token);

                    if (expandedBytes != expected.ExpandedBytes ||
                        !HashesEqual(expandedDigest, expected.ExpandedSha256))
                    {
                        throw new InvalidDataException($"升级包条目 GZip 展开哈希或大小不匹配：{name}。");
                    }
                }
            }

            if (seen.Count != expectedEntries.Count)
            {
                throw new InvalidDataException("升级包 ZIP 条目数量与签名清单不一致。");
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            throw new InvalidDataException($"升级包内容校验失败：{ex.Message}", ex);
        }
    }

    private static Dictionary<string, DescriptorFileEntry> ReadExpectedEntries(JsonElement root)
    {
        if (!root.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array ||
            filesElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException("升级包描述文件缺少文件清单。");
        }

        var map = new Dictionary<string, DescriptorFileEntry>(StringComparer.Ordinal);
        foreach (var item in filesElement.EnumerateArray())
        {
            var path = item.GetProperty("path").GetString()
                ?? throw new InvalidDataException("升级包文件清单存在空路径。");
            var bytes = item.GetProperty("bytes").GetInt64();
            var sha256 = item.GetProperty("sha256").GetString()
                ?? throw new InvalidDataException($"条目 {path} 缺少 SHA-256。");
            var expandedBytes = item.GetProperty("expandedBytes").GetInt64();
            var expandedSha256 = item.GetProperty("expandedSha256").GetString()
                ?? throw new InvalidDataException($"条目 {path} 缺少展开 SHA-256。");
            var compression = item.GetProperty("compression").GetString()
                ?? throw new InvalidDataException($"条目 {path} 缺少压缩类型。");

            if (!map.TryAdd(
                    path,
                    new DescriptorFileEntry(bytes, sha256, expandedBytes, expandedSha256, compression)))
            {
                throw new InvalidDataException($"升级包文件清单存在重复路径：{path}。");
            }
        }

        return map;
    }

    private static (string Digest, long TotalBytes) HashStreamBounded(
        Stream stream,
        long limit,
        byte[] buffer,
        CancellationToken token)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;

        while (total < limit)
        {
            token.ThrowIfCancellationRequested();
            var maxRead = (int)Math.Min(buffer.Length, limit - total);
            var read = stream.Read(buffer, 0, maxRead);
            if (read <= 0)
            {
                break;
            }

            sha256.AppendData(buffer, 0, read);
            total += read;
        }

        return (Convert.ToHexStringLower(sha256.GetHashAndReset()), total);
    }

    /// <summary>
    /// Reads one signed sidecar (the descriptor or its signature) with the descriptor that was actually
    /// checked: on Unix the path is opened with no-follow and nonblocking and the regular-file decision
    /// comes from <c>fstat</c> on that same descriptor, so the file that is read cannot be a link, a FIFO
    /// or a device and cannot be swapped between the check and the read. Windows keeps the existing
    /// share-mode open and attribute gate.
    /// </summary>
    internal static byte[] ReadBoundedRegularFile(string path, int maxBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
        {
            // FileInfo.Exists is false for a directory, so the sidecar path is checked for one first to
            // keep the refusal a data error instead of a misleading "file not found".
            if (Directory.Exists(path))
            {
                throw new InvalidDataException($"拒绝读取符号链接、重解析点或目录：{path}。");
            }

            EnsureRegularFile(path);

            using var windowsStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);

            return ReadBoundedStream(windowsStream, path, maxBytes, token);
        }

        return ReadBoundedRegularFileUnix(path, maxBytes, token);
    }

    private static byte[] ReadBoundedRegularFileUnix(string path, int maxBytes, CancellationToken token)
    {
        var handle = OpenUnixVerifiedRegularFile(path, out var status);
        FileStream stream;
        try
        {
            // The size comes from the same descriptor as the type check, so an oversized sidecar is
            // refused before a single byte is read or a managed stream is created.
            if (status.Size <= 0 || status.Size > maxBytes)
            {
                throw new InvalidDataException($"文件大小超出允许范围：{path}。");
            }

            stream = new FileStream(handle, FileAccess.Read, 4096);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        // The FileStream owns the descriptor from here on and closes it exactly once.
        using (stream)
        {
            return ReadBoundedStream(stream, path, maxBytes, token);
        }
    }

    /// <summary>
    /// Opens one archive part (the selected archive or one volume of a split set) as a streaming,
    /// seekable read-only file whose type was established from the descriptor that is read. Unlike a
    /// signed sidecar there is no byte cap: a part is expected to be large, so the file is never
    /// buffered and only its handles are held for the pass that reads it. On Unix the path is opened
    /// with the same no-follow and nonblocking flags as <see cref="ReadBoundedRegularFile"/> and the
    /// regular-file decision comes from <c>fstat</c> on that descriptor, so a FIFO cannot park the
    /// open and a link, device or directory is refused instead of read. Windows keeps the existing
    /// share-mode open after its attribute gate. The returned stream owns the only handle.
    /// </summary>
    private static FileStream OpenVerifiedRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // FileInfo.Exists is false for a directory, so the path is checked for one first to keep
            // the refusal a data error instead of a misleading "file not found".
            if (Directory.Exists(path))
            {
                throw new InvalidDataException($"拒绝读取符号链接、重解析点或目录：{path}。");
            }

            EnsureRegularFile(path);

            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.SequentialScan);
        }

        var handle = OpenUnixVerifiedRegularFile(path, out _);
        try
        {
            return new FileStream(handle, FileAccess.Read, CopyBufferSize);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The single open-and-verify primitive behind both the signed sidecars and the archive parts:
    /// <c>open(2)</c> with the per-OS O_NOFOLLOW, O_NONBLOCK and O_CLOEXEC flags, then <c>fstat(2)</c>
    /// on that same descriptor. The caller owns the returned handle and must dispose it; every failure
    /// path closes it here, and a platform without verified flags fails closed rather than opening the
    /// file under weaker semantics.
    /// </summary>
    private static SafeFileHandle OpenUnixVerifiedRegularFile(string path, out FileStatus status)
    {
        if (!TryGetUnixOpenFlags(out var openFlags))
        {
            // Without the per-OS flags the descriptor could be opened under weaker semantics, so the
            // platform fails closed instead of reading a file whose type was not established.
            throw new InvalidDataException($"无法在当前平台安全校验文件：{path}。");
        }

        var descriptor = NativeOpen(path, openFlags);
        if (descriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == NativeNoEntry)
            {
                throw new FileNotFoundException($"找不到文件：{path}。", path);
            }

            throw new InvalidDataException($"无法安全打开文件：{path}（errno {errno}）。");
        }

        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            if (!TryReadNativeFileStatus(handle, out status))
            {
                throw new InvalidDataException($"无法读取文件状态：{path}。");
            }

            if (!IsRegularFileMode(status.Mode))
            {
                throw new InvalidDataException($"拒绝读取符号链接、重解析点或目录：{path}。");
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return handle;
    }

    /// <summary>
    /// True when POSIX mode bits describe a regular file. Exposed so the focused media tests can cover
    /// FIFO, socket, device, directory and link modes without creating such files.
    /// </summary>
    internal static bool IsRegularFileMode(int mode) =>
        (mode & UnixFileTypeMask) == UnixRegularFile;

    /// <summary>
    /// O_NONBLOCK (no parking on a writerless FIFO), O_NOFOLLOW (the kernel rejects a final link) and
    /// O_CLOEXEC; O_RDONLY is zero on every Unix.
    /// </summary>
    private static bool TryGetUnixOpenFlags(out int flags)
    {
        if (OperatingSystem.IsLinux())
        {
            return TryGetLinuxOpenFlags(RuntimeInformation.ProcessArchitecture, out flags);
        }

        if (OperatingSystem.IsMacOS())
        {
            flags = MacOpenFlags;
            return true;
        }

        if (OperatingSystem.IsFreeBSD())
        {
            flags = FreeBsdOpenFlags;
            return true;
        }

        flags = 0;
        return false;
    }

    /// <summary>
    /// Linux open(2) flags for one process architecture. Only the ABIs checked against the kernel UAPI
    /// headers are accepted; any other architecture fails closed instead of opening with a wrong bit.
    /// </summary>
    internal static bool TryGetLinuxOpenFlags(Architecture architecture, out int flags)
    {
        int? noFollow = architecture switch
        {
            Architecture.X86 or Architecture.X64 => LinuxNoFollowGeneric,
            Architecture.Arm or Architecture.Arm64 => LinuxNoFollowArm,
            _ => null
        };

        flags = noFollow is { } value ? LinuxNonBlock | value | LinuxCloseOnExec : 0;
        return noFollow is not null;
    }

    private static bool TryReadNativeFileStatus(SafeFileHandle handle, out FileStatus status)
    {
        try
        {
            return NativeFStat(handle.DangerousGetHandle(), out status) == 0;
        }
        catch (DllNotFoundException)
        {
            status = default;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            status = default;
            return false;
        }
    }

    private static byte[] ReadBoundedStream(
        FileStream stream,
        string path,
        int maxBytes,
        CancellationToken token)
    {
        // The length is read once and that snapshot is used for both the range
        // check and the initial capacity, so the buffer can never be sized from
        // a length that was not validated.
        var length = stream.Length;
        if (length <= 0 || length > maxBytes)
        {
            throw new InvalidDataException($"文件大小超出允许范围：{path}。");
        }

        var limit = maxBytes + 1;
        using var ms = new MemoryStream((int)length);
        var buffer = new byte[Math.Min(16384, maxBytes + 1)];
        var total = 0;

        while (total < limit)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, limit - total));
            if (read <= 0)
            {
                break;
            }

            ms.Write(buffer, 0, read);
            total += read;
        }

        if (total <= 0 || total > maxBytes)
        {
            throw new InvalidDataException($"文件读取长度异常：{path}。");
        }

        return ms.ToArray();
    }

    private static void EnsureRegularSourceFiles(ArchiveSource source)
    {
        foreach (var part in source.Parts)
        {
            EnsureRegularFile(part);
        }
    }

    private static void EnsureRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"找不到文件：{path}。", path);
        }

        if (info.LinkTarget is not null ||
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException($"拒绝读取符号链接、重解析点或目录：{path}。");
        }
    }

    private static string GetArchiveBaseFileName(string firstPartPath)
    {
        var fileName = Path.GetFileName(firstPartPath);
        return IsSplitVolumeFileName(fileName, out var baseName) ? baseName : fileName;
    }

    private static bool IsSplitVolumeFileName(string fileName) =>
        IsSplitVolumeFileName(fileName, out _);

    private static bool IsSplitVolumeFileName(string fileName, out string baseName)
    {
        var lastDot = fileName.LastIndexOf('.');
        if (lastDot > 0)
        {
            var digits = fileName[(lastDot + 1)..];
            if (digits.Length is >= MinVolumeDigits and <= MaxVolumeDigits && IsAsciiDigits(digits))
            {
                baseName = fileName[..lastDot];
                return true;
            }
        }

        baseName = fileName;
        return false;
    }

    private static bool HashesEqual(string actualHex, string expectedHex) =>
        string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One private, bounded copy of the complete logical archive of a signed import. The file is
    /// created exclusively below the import work root, carries user-only permissions from the moment
    /// it exists on Unix and is deleted when the snapshot is disposed, so verification and extraction
    /// read exactly the bytes that were checked and never the caller's files.
    /// </summary>
    internal sealed class VerifiedArchiveSnapshot : IDisposable
    {
        /// <summary>Largest signed archive size accepted for a private snapshot.</summary>
        internal const long MaxBytes = 1L << 40;

        private const string FileNamePrefix = ".update-snapshot-";

        /// <summary>
        /// Mode passed as <see cref="FileStreamOptions.UnixCreateMode"/> so the kernel applies it while
        /// the file is being created instead of after it already exists.
        /// </summary>
        private const UnixFileMode PrivateUnixMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite;

        private readonly FileStream _file;

        private int _disposed;

        private VerifiedArchiveSnapshot(
            FileStream file,
            IReadOnlyList<SnapshotPart> parts,
            bool isSplitSource,
            string archiveBaseName,
            UnixFileMode unixPermissions)
        {
            _file = file;
            Parts = parts;
            IsSplitSource = isSplitSource;
            ArchiveBaseName = archiveBaseName;
            UnixPermissions = unixPermissions;
        }

        /// <summary>Seekable view of the snapshot, positioned at the start of the archive.</summary>
        internal Stream Stream => _file;

        /// <summary>Original volume names and byte counts in archive order, recorded while copying.</summary>
        internal IReadOnlyList<SnapshotPart> Parts { get; }

        /// <summary>True when the copied source was a split volume group.</summary>
        internal bool IsSplitSource { get; }

        /// <summary>Archive base name, derived from the first original part name.</summary>
        internal string ArchiveBaseName { get; }

        /// <summary>
        /// Permission bits the kernel applied when the snapshot was created, read back from the open
        /// handle, or zero on Windows.
        /// </summary>
        internal UnixFileMode UnixPermissions { get; }

        /// <summary>Byte length of the snapshot, always the signed archive size.</summary>
        internal long Length => _file.Length;

        /// <summary>
        /// Copies <paramref name="partLengths"/> bytes per logical part in order into a new private file.
        /// The signed size is checked before the file is created, the copy stops exactly at each part
        /// length and the file is removed again if anything fails, including cancellation.
        /// </summary>
        internal static VerifiedArchiveSnapshot Copy(
            Stream logical,
            IReadOnlyList<string> partNames,
            IReadOnlyList<long> partLengths,
            long expectedBytes,
            string root,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(logical);
            ArgumentNullException.ThrowIfNull(partNames);
            ArgumentNullException.ThrowIfNull(partLengths);
            ArgumentNullException.ThrowIfNull(root);

            token.ThrowIfCancellationRequested();

            if (partNames.Count == 0 || partNames.Count != partLengths.Count)
            {
                throw new InvalidDataException("分卷元数据不一致，无法建立私有快照。");
            }

            if (expectedBytes <= 0 || expectedBytes > MaxBytes)
            {
                throw new InvalidDataException(
                    $"升级包声明的归档大小 {expectedBytes} 字节超出允许范围。");
            }

            var declaredTotal = 0L;
            foreach (var partBytes in partLengths)
            {
                if (partBytes < 0 || partBytes > expectedBytes - declaredTotal)
                {
                    throw new InvalidDataException(
                        $"升级包实际大小超出签名声明的 {expectedBytes} 字节，拒绝使用。");
                }

                declaredTotal += partBytes;
            }

            if (declaredTotal != expectedBytes)
            {
                throw new InvalidDataException(
                    $"升级包总大小不匹配：实际为 {declaredTotal} 字节，期望 {expectedBytes} 字节。");
            }

            var path = Path.Combine(root, FileNamePrefix + Path.GetRandomFileName());
            var permissions = default(UnixFileMode);
            FileStream? file = null;

            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = CopyBufferSize,
                Options = FileOptions.DeleteOnClose,
            };

            if (!OperatingSystem.IsWindows())
            {
                // The mode is applied by the create call itself, so the file never exists under the
                // process umask with group or other bits that a later SetUnixFileMode would have to undo.
                options.UnixCreateMode = PrivateUnixMode;
            }

            try
            {
                file = new FileStream(path, options);

                if (!OperatingSystem.IsWindows())
                {
                    // Read back what the kernel actually applied; setting it again is not needed.
                    permissions = File.GetUnixFileMode(file.SafeFileHandle);
                }

                var recorded = new SnapshotPart[partNames.Count];
                var buffer = new byte[CopyBufferSize];

                for (var index = 0; index < recorded.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    CopyExactly(logical, file, partLengths[index], buffer, partNames[index], token);
                    recorded[index] = new SnapshotPart(partNames[index], partLengths[index]);
                }

                if (file.Length != expectedBytes)
                {
                    throw new InvalidDataException("私有快照大小与签名声明不一致，拒绝使用。");
                }

                file.Flush();
                file.Position = 0;

                return new VerifiedArchiveSnapshot(
                    file,
                    recorded,
                    IsSplitVolumeFileName(partNames[0]),
                    GetArchiveBaseFileName(partNames[0]),
                    permissions);
            }
            catch
            {
                file?.Dispose();
                TryDeleteSnapshot(path);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _file.Dispose();
            }
        }

        private static void CopyExactly(
            Stream input,
            Stream output,
            long bytes,
            byte[] buffer,
            string partName,
            CancellationToken token)
        {
            var remaining = bytes;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();

                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    throw new InvalidDataException($"升级包 {partName} 在复制完成前被截断，拒绝使用。");
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        /// <summary>Best-effort removal after a failed copy; the DeleteOnClose flag is the backstop.</summary>
        private static void TryDeleteSnapshot(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>One original volume: the name it was selected under and the bytes it supplied.</summary>
        internal readonly record struct SnapshotPart(string Name, long Bytes);
    }

    private readonly record struct DescriptorFileEntry(
        long Bytes,
        string Sha256,
        long ExpandedBytes,
        string ExpandedSha256,
        string Compression);

    /// <summary>
    /// stat result exactly as the runtime's own System.Native shim lays it out for this process
    /// (pal_io.h FileStatus). The order and widths are the native ABI and must not be rearranged.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatus
    {
        public int Flags;
        public int Mode;
        public uint Uid;
        public uint Gid;
        public long Size;
        public long ATime;
        public long ATimeNsec;
        public long MTime;
        public long MTimeNsec;
        public long CTime;
        public long CTimeNsec;
        public long BirthTime;
        public long BirthTimeNsec;
        public long Dev;
        public long RDev;
        public long Ino;
        public uint UserFlags;
    }

    /// <summary>
    /// fstat(2) through the System.Native library the shared framework already loads, so the call uses
    /// this process's ABI and the regular-file decision needs no additional package or helper.
    /// </summary>
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat")]
    private static extern int NativeFStat(nint descriptor, out FileStatus status);

    /// <summary>open(2) from the C library, with the per-OS flag set chosen above.</summary>
    [DllImport("libc", EntryPoint = "open", SetLastError = true, ExactSpelling = true)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
