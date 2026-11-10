using System.IO.Compression;
using System.Security.Cryptography;

namespace FreedomInstaller.Core;

public sealed class ImportedUpdatePackage : IDisposable
{
    private readonly string _workRoot;
    private readonly string _stageRoot;
    private bool _disposed;

    internal ImportedUpdatePackage(
        string sourcePath,
        string workRoot,
        string stageRoot,
        string packageDirectory,
        string deviceModel,
        UpdatePackageDescriptor descriptor,
        string descriptorPath,
        string signaturePath)
    {
        SourcePath = sourcePath;
        _workRoot = workRoot;
        _stageRoot = stageRoot;
        PackageDirectory = packageDirectory;
        DeviceModel = deviceModel;
        Descriptor = descriptor;
        DescriptorPath = descriptorPath;
        SignaturePath = signaturePath;
    }

    public string SourcePath { get; }

    public string PackageDirectory { get; }

    public string DeviceModel { get; }

    public UpdatePackageDescriptor Descriptor { get; }

    public string DescriptorPath { get; }

    public string SignaturePath { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PackageImporter.CleanupOfflineUpdateStage(_workRoot, _stageRoot);
    }
}

public static partial class PackageImporter
{
    private const string OfflineUpgradeOperation = "offline-upgrade";
    private const string StagedDescriptorFileName = "package.update.json";
    private const string StagedSignatureFileName = "package.update.json.sig";
    private const string StagedPayloadDirectoryName = "payload";

    public static Task<ImportedUpdatePackage> ImportOfflineUpdateAsync(
        string sourcePath,
        string expectedModel,
        string installerVersion,
        string workRoot,
        CancellationToken token = default) =>
        ImportOfflineUpdateAsync(
            sourcePath,
            expectedModel,
            installerVersion,
            workRoot,
            UpdatePackageDescriptor.VerifyAndParse,
            token);

    internal static Task<ImportedUpdatePackage> ImportOfflineUpdateAsync(
        string sourcePath,
        string expectedModel,
        string installerVersion,
        string workRoot,
        Func<byte[], byte[], UpdatePackageDescriptor> verify,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(verify);

        return Task.Run(
            () => ImportOfflineUpdateCore(
                sourcePath,
                expectedModel,
                installerVersion,
                workRoot,
                verify,
                token),
            token);
    }

    private static ImportedUpdatePackage ImportOfflineUpdateCore(
        string sourcePath,
        string expectedModel,
        string installerVersion,
        string workRoot,
        Func<byte[], byte[], UpdatePackageDescriptor> verify,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new InvalidDataException("未指定离线升级包路径。");
        }

        if (string.IsNullOrWhiteSpace(expectedModel))
        {
            throw new InvalidDataException("未指定目标设备型号。");
        }

        if (string.IsNullOrWhiteSpace(installerVersion))
        {
            throw new InvalidDataException("未指定安装器版本。");
        }

        if (string.IsNullOrWhiteSpace(workRoot))
        {
            throw new InvalidDataException("未指定离线升级工作目录。");
        }

        var normalizedModel = expectedModel.Trim();
        var fullSource = Path.GetFullPath(sourcePath);
        if (Directory.Exists(fullSource))
        {
            throw new InvalidDataException("离线升级包不支持目录导入。");
        }

        var canonicalWorkRoot = PrepareOfflineWorkRoot(workRoot);
        var source = ResolveArchiveSource(fullSource);
        if (source.IsNativeRar)
        {
            throw new InvalidDataException("签名升级包不支持 RAR 分卷格式。");
        }

        EnsureRegularSourceFiles(source);
        var (descriptor, descriptorBytes, signatureBytes) = ReadOfflineUpdateDescriptor(
            source,
            normalizedModel,
            installerVersion,
            verify,
            token);

        using var stream = source.OpenStream();
        VerifyUpdateMedia(source, stream, descriptor, token);

        var stageRoot = CreateOfflineStageDirectory(canonicalWorkRoot);
        try
        {
            var payloadDirectory = Path.GetFullPath(Path.Combine(stageRoot, StagedPayloadDirectoryName));
            var descriptorPath = Path.GetFullPath(Path.Combine(stageRoot, StagedDescriptorFileName));
            var signaturePath = Path.GetFullPath(Path.Combine(stageRoot, StagedSignatureFileName));

            if (!IsBeneath(payloadDirectory, stageRoot) ||
                !IsBeneath(descriptorPath, stageRoot) ||
                !IsBeneath(signaturePath, stageRoot))
            {
                throw new InvalidDataException("离线升级暂存路径超出受控目录。");
            }

            Directory.CreateDirectory(payloadDirectory);
            EnsureSafeDirectoryTree(stageRoot, canonicalWorkRoot);

            WriteBoundedStageFile(descriptorPath, descriptorBytes, token);
            WriteBoundedStageFile(signaturePath, signatureBytes, token);
            StageVerifiedOfflinePayload(stream, descriptor, payloadDirectory, token);
            token.ThrowIfCancellationRequested();

            return new ImportedUpdatePackage(
                fullSource,
                canonicalWorkRoot,
                stageRoot,
                payloadDirectory,
                normalizedModel,
                descriptor,
                descriptorPath,
                signaturePath);
        }
        catch (Exception ex)
        {
            try
            {
                CleanupOfflineUpdateStage(canonicalWorkRoot, stageRoot);
            }
            catch (Exception cleanupEx)
            {
                throw new AggregateException("离线升级暂存失败，且清理暂存目录也失败。", ex, cleanupEx);
            }

            throw;
        }
    }

    private static (UpdatePackageDescriptor Descriptor, byte[] DescriptorBytes, byte[] SignatureBytes)
        ReadOfflineUpdateDescriptor(
            ArchiveSource source,
            string expectedModel,
            string installerVersion,
            Func<byte[], byte[], UpdatePackageDescriptor> verify,
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
            throw new InvalidDataException($"缺少升级包描述文件或签名：{descriptorPath}。");
        }

        if (!descriptorExists || !signatureExists)
        {
            throw new InvalidDataException($"升级包描述文件与签名必须同时存在：{descriptorPath}。");
        }

        var descriptorBytes = ReadBoundedRegularFile(descriptorPath, MaxDescriptorBytes, token);
        var signatureBytes = ReadBoundedRegularFile(signaturePath, MaxSignatureBytes, token);
        var descriptor = verify(descriptorBytes, signatureBytes)
            ?? throw new InvalidDataException("升级包描述文件校验返回空结果。");

        var declaredArchiveName = descriptor.Root
            .GetProperty("archive")
            .GetProperty("fileName")
            .GetString();

        if (!string.Equals(archiveBaseName, declaredArchiveName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"升级包文件名不匹配：实际为 {archiveBaseName}，描述文件要求 {declaredArchiveName}。");
        }

        descriptor.RequireOperation(expectedModel, OfflineUpgradeOperation, installerVersion);
        return (descriptor, descriptorBytes, signatureBytes);
    }

    private static void StageVerifiedOfflinePayload(
        Stream stream,
        UpdatePackageDescriptor descriptor,
        string payloadDirectory,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        stream.Position = 0;

        var expectedEntries = ReadExpectedEntries(descriptor.Root);
        var buffer = new byte[MediaHashBufferBytes];

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();

            var name = entry.FullName;
            if (!seen.Add(name))
            {
                throw new InvalidDataException($"升级包包含重复的 ZIP 条目：{name}。");
            }

            if (!expectedEntries.TryGetValue(name, out var expected))
            {
                throw new InvalidDataException($"升级包包含未声明的 ZIP 条目：{name}。");
            }

            if (entry.Length != expected.Bytes || entry.CompressedLength != expected.Bytes)
            {
                throw new InvalidDataException($"升级包条目长度不匹配：{name}。");
            }

            var targetPath = ResolveSafePayloadTargetPath(payloadDirectory, name);
            var parentDir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(parentDir) ||
                (!string.Equals(parentDir, payloadDirectory, StringComparison.Ordinal) &&
                 !IsBeneath(parentDir, payloadDirectory)))
            {
                throw new InvalidDataException($"升级包条目目标目录越界：{name}。");
            }

            Directory.CreateDirectory(parentDir);
            EnsureSafeDirectoryTree(parentDir, payloadDirectory);

            using var sourceStream = entry.Open();
            using var fileStream = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                MediaHashBufferBytes,
                FileOptions.SequentialScan);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            long totalWritten = 0;
            var limit = expected.Bytes == long.MaxValue ? long.MaxValue : expected.Bytes + 1;
            while (totalWritten < limit)
            {
                token.ThrowIfCancellationRequested();
                var maxRead = (int)Math.Min(buffer.Length, limit - totalWritten);
                var read = sourceStream.Read(buffer, 0, maxRead);
                if (read <= 0)
                {
                    break;
                }

                fileStream.Write(buffer, 0, read);
                sha256.AppendData(buffer, 0, read);
                totalWritten += read;
            }

            fileStream.Flush(flushToDisk: true);

            var digest = Convert.ToHexStringLower(sha256.GetHashAndReset());
            if (totalWritten != expected.Bytes || !HashesEqual(digest, expected.Sha256))
            {
                throw new InvalidDataException($"离线升级暂存文件校验失败：{name}。");
            }
        }

        if (seen.Count != expectedEntries.Count)
        {
            throw new InvalidDataException("离线升级暂存条目数量与签名清单不一致。");
        }
    }

    private static string ResolveSafePayloadTargetPath(string payloadDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Contains((char)92) ||
            relativePath.StartsWith('/') ||
            relativePath.EndsWith('/') ||
            relativePath.Contains(':'))
        {
            throw new InvalidDataException($"升级包条目路径非法：{relativePath}。");
        }

        var segments = relativePath.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"升级包条目路径段非法：{relativePath}。");
            }
        }

        var combined = Path.GetFullPath(Path.Combine(
            payloadDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsBeneath(combined, payloadDirectory))
        {
            throw new InvalidDataException($"升级包条目路径越界：{relativePath}。");
        }

        return combined;
    }

    private static void WriteBoundedStageFile(string path, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var fs = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.SequentialScan);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    private static string PrepareOfflineWorkRoot(string workRoot)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workRoot));
        if (string.IsNullOrWhiteSpace(canonical))
        {
            throw new InvalidDataException("离线升级工作目录无效。");
        }

        canonical = ResolveOfflineWorkRootAncestors(canonical);
        var info = new DirectoryInfo(canonical);
        if (!info.Exists ||
            info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            (info.Attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidDataException($"离线升级工作目录必须已存在且为普通目录：{canonical}。");
        }

        return canonical;
    }

    private static string CreateOfflineStageDirectory(string canonicalWorkRoot)
    {
        // Media verification can take a long time; recheck the existing work root
        // immediately before creating the private child directory. This only
        // revalidates the caller-provided root and never creates or recreates it.
        canonicalWorkRoot = PrepareOfflineWorkRoot(canonicalWorkRoot);
        var candidate = Path.GetFullPath(
            Path.Combine(canonicalWorkRoot, $"offline-update-{Guid.NewGuid():N}"));
        if (!IsBeneath(candidate, canonicalWorkRoot))
        {
            throw new InvalidDataException("生成的离线升级暂存目录越界。");
        }

        if (Path.Exists(candidate))
        {
            throw new IOException($"离线升级暂存目录已存在：{candidate}。");
        }

        Directory.CreateDirectory(candidate);
        EnsureSafeDirectoryTree(candidate, canonicalWorkRoot);
        return candidate;
    }

    internal static void CleanupOfflineUpdateStage(string workRoot, string stageRoot)
    {
        if (string.IsNullOrWhiteSpace(workRoot) || string.IsNullOrWhiteSpace(stageRoot))
        {
            throw new InvalidDataException("离线升级暂存清理路径无效。");
        }

        var canonicalWorkRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workRoot));
        var canonicalStageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stageRoot));

        if (!IsBeneath(canonicalStageRoot, canonicalWorkRoot))
        {
            throw new InvalidDataException($"拒绝清理工作目录之外的路径：{canonicalStageRoot}。");
        }

        var parent = Path.GetDirectoryName(canonicalStageRoot);
        if (!string.Equals(parent, canonicalWorkRoot, StringComparison.Ordinal) ||
            !Path.GetFileName(canonicalStageRoot).StartsWith("offline-update-", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"拒绝清理非离线升级暂存目录：{canonicalStageRoot}。");
        }

        EnsureSafeDirectoryTree(canonicalWorkRoot, canonicalWorkRoot);
        if (!Path.Exists(canonicalStageRoot))
        {
            return;
        }

        EnsureDirectorySubtreeNotReparse(canonicalStageRoot);
        Directory.Delete(canonicalStageRoot, recursive: true);
    }

    private static string ResolveOfflineWorkRootAncestors(string directoryPath)
    {
        string full = Path.GetFullPath(directoryPath);
        string parent = Path.GetDirectoryName(full)
            ?? throw new InvalidDataException("The offline work root cannot be a filesystem root.");
        string current = Path.GetPathRoot(parent)!;
        foreach (string segment in parent[current.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            var info = new DirectoryInfo(Path.Combine(current, segment));
            if (!info.Exists)
                throw new DirectoryNotFoundException(info.FullName);
            current = info.LinkTarget != null || (info.Attributes & FileAttributes.ReparsePoint) != 0
                ? (info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new InvalidDataException($"Cannot resolve work directory ancestor: {info.FullName}"))
                : info.FullName;
        }
        return Path.Combine(current, Path.GetFileName(full));
    }

    private static void EnsureSafeDirectoryTree(string targetDirectory, string stopRoot)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var canonicalStop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopRoot));

        while (true)
        {
            var info = new DirectoryInfo(current);
            if (!info.Exists ||
                info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"暂存目录包含重解析点或无效目录：{current}。");
            }

            if (string.Equals(current, canonicalStop, StringComparison.Ordinal))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || !IsBeneath(current, canonicalStop))
            {
                throw new InvalidDataException($"暂存目录越界：{current}。");
            }

            current = Path.TrimEndingDirectorySeparator(parent);
        }
    }

    private static void EnsureDirectorySubtreeNotReparse(string rootDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var info = new DirectoryInfo(current);
            if (!info.Exists ||
                info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (info.Attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException($"拒绝操作重解析点或非目录路径：{current}。");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var fullEntry = Path.GetFullPath(entry);
                if (!IsBeneath(fullEntry, rootDirectory))
                {
                    throw new InvalidDataException($"暂存目录内发现越界路径：{fullEntry}。");
                }

                var attributes = File.GetAttributes(fullEntry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"暂存目录内发现重解析点：{fullEntry}。");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(fullEntry);
                }
            }
        }
    }
}
