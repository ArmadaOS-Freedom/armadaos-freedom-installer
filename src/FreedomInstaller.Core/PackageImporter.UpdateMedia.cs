using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
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
        CancellationToken token)
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

        EnsureRegularSourceFiles(source);

        var root = descriptor.Root;
        var archiveNode = root.GetProperty("archive");
        var expectedArchiveName = archiveNode.GetProperty("fileName").GetString()
            ?? throw new InvalidDataException("升级包文件名缺失。");
        var expectedArchiveBytes = archiveNode.GetProperty("bytes").GetInt64();
        var expectedArchiveSha256 = archiveNode.GetProperty("sha256").GetString()
            ?? throw new InvalidDataException("升级包哈希缺失。");

        var actualBaseName = GetArchiveBaseFileName(source.Parts[0]);
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
                token);

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
        CancellationToken token)
    {
        var isSplitSource = IsSplitVolumeFileName(Path.GetFileName(source.Parts[0]));
        stream.Position = 0;

        if (stream.Length != expectedArchiveBytes)
        {
            throw new InvalidDataException(
                $"升级包总大小不匹配：实际为 {stream.Length} 字节，期望 {expectedArchiveBytes} 字节。");
        }

        var buffer = new byte[MediaHashBufferBytes];

        if (!isSplitSource)
        {
            if (source.Parts.Count != 1)
            {
                throw new InvalidDataException("单文件升级包包含多个输入源。");
            }

            using var singlePartStream = OpenPart(source.Parts[0]);
            if (singlePartStream.Length != expectedArchiveBytes)
            {
                throw new InvalidDataException("升级包文件大小与受保护流不一致。");
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
        if (source.Parts.Count != declaredCount)
        {
            throw new InvalidDataException(
                $"分卷数量不匹配：实际为 {source.Parts.Count}，描述文件要求 {declaredCount}。");
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

            var actualPartPath = source.Parts[index];
            var actualPartName = Path.GetFileName(actualPartPath);
            if (!string.Equals(actualPartName, expectedPartName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"第 {index + 1} 卷文件名不匹配：实际为 {actualPartName}，期望 {expectedPartName}。");
            }

            EnsureRegularFile(actualPartPath);
            using (var partFile = OpenPart(actualPartPath))
            {
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

    private static byte[] ReadBoundedRegularFile(string path, int maxBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureRegularFile(path);

        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);

        if (fs.Length <= 0 || fs.Length > maxBytes)
        {
            throw new InvalidDataException($"文件大小超出允许范围：{path}。");
        }

        var limit = maxBytes + 1;
        using var ms = new MemoryStream((int)fs.Length);
        var buffer = new byte[Math.Min(16384, maxBytes + 1)];
        var total = 0;

        while (total < limit)
        {
            token.ThrowIfCancellationRequested();
            var read = fs.Read(buffer, 0, Math.Min(buffer.Length, limit - total));
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

    private readonly record struct DescriptorFileEntry(
        long Bytes,
        string Sha256,
        long ExpandedBytes,
        string ExpandedSha256,
        string Compression);
}
