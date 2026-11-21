using System.IO.Compression;
using System.Text.Json;

namespace FreedomInstaller.Core;

/// <summary>
/// A private, verified copy of the inner signed split archive contained in a CI download
/// wrapper ZIP. Owns the private stage directory below the caller's work root and removes only
/// that directory when disposed. Source files are never modified.
/// </summary>
internal sealed class PreparedCiArtifactSource : IDisposable
{
    private readonly string _workRoot;
    private readonly string _stageRoot;
    private readonly object _disposeLock = new();
    private bool _disposed;

    internal PreparedCiArtifactSource(string workRoot, string stageRoot, string mediaPath)
    {
        _workRoot = workRoot;
        _stageRoot = stageRoot;
        MediaPath = mediaPath;
    }

    /// <summary>Absolute path to the first staged signed volume; the existing resolver accepts it.</summary>
    public string MediaPath { get; }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
            {
                return;
            }

            PackageImporter.CleanupCiArtifactStage(_workRoot, _stageRoot);
            _disposed = true;
        }
    }
}

public static partial class PackageImporter
{
    private const string CiUpdateJsonSuffix = ".zip.update.json";

    /// <summary>
    /// If <paramref name="fullSource"/> is a CI download wrapper ZIP (an outer archive whose only
    /// signed descriptor may be at the archive root or in a nested wrapper folder and whose
    /// payload is a split inner ZIP plus its <c>.update.json</c>/<c>.sig</c>), verify the
    /// descriptor and declared split parts and stage them privately below
    /// <paramref name="workRoot"/>. Only the descriptor signature is verified here; volume
    /// hashes are verified by the unchanged caller. The signer is determined by the supplied
    /// <paramref name="verify"/> delegate. Returns null for every other shape (direct archives,
    /// split archives, RAR sets, native non-ZIP containers, non-wrapper bundles) so existing
    /// import paths are preserved.
    /// </summary>
    internal static PreparedCiArtifactSource? PrepareCiArtifactSource(
        string fullSource,
        string expectedModel,
        string operation,
        string installerVersion,
        string workRoot,
        Func<byte[], byte[], UpdatePackageDescriptor> verify,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(verify);
        token.ThrowIfCancellationRequested();

        var source = ResolveArchiveSource(fullSource);
        if (source.IsNativeRar || source.Parts.Count != 1)
        {
            return null;
        }

        using var stream = source.OpenStream();
        if (!HasZipHeader(stream))
        {
            return null;
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var items = new List<ArchiveItem>();
        var indexReasons = new List<string>();
        if (!TryIndexArchive(zip, items, indexReasons))
        {
            throw new InvalidDataException("CI 下载包条目校验失败：" + string.Join(" ", indexReasons));
        }

        foreach (var item in items)
        {
            if (!item.IsDirectory && string.Equals(item.Name, BundleFileName, StringComparison.OrdinalIgnoreCase))
            {
                if (HasCiWrapperLayout(items))
                {
                    throw new InvalidDataException(
                        "CI 下载包根目录同时包含 bundle.json 和拆分分卷，拒绝按直接包处理。");
                }
                return null;
            }
        }

        if (items.Count == 0)
        {
            return null;
        }

        var descriptorCandidates = new List<ArchiveItem>();
        foreach (var item in items)
        {
            if (!item.IsDirectory
                && item.Name.EndsWith(CiUpdateJsonSuffix, StringComparison.OrdinalIgnoreCase))
            {
                descriptorCandidates.Add(item);
            }
        }

        if (descriptorCandidates.Count == 0)
        {
            // Not a wrapper; let the existing missing-bundle rejection run.
            return null;
        }

        if (descriptorCandidates.Count != 1)
        {
            throw new InvalidDataException(
                $"CI 下载包包含 {descriptorCandidates.Count} 个 .zip.update.json，拒绝猜测。");
        }

        var descriptorItem = descriptorCandidates[0];
        var descriptorName = descriptorItem.Name;
        var normalizedPrefix = GetNormalizedPrefix(descriptorName);
        var expectedSignatureName = descriptorName + ".sig";

        ArchiveItem? signatureItem = null;
        foreach (var item in items)
        {
            if (!item.IsDirectory && string.Equals(item.Name, expectedSignatureName, StringComparison.Ordinal))
            {
                signatureItem = item;
                break;
            }
        }

        if (signatureItem is null)
        {
            throw new InvalidDataException($"CI 下载包缺少签名文件：{expectedSignatureName}。");
        }

        var descriptorBytes = ReadLimited(descriptorItem.Entry, MaxDescriptorBytes, token);
        var signatureBytes = ReadLimited(signatureItem.Entry, MaxSignatureBytes, token);

        var descriptor = verify(descriptorBytes, signatureBytes)
            ?? throw new InvalidDataException("CI 下载包描述文件校验返回空结果。");

        descriptor.RequireOperation(expectedModel, operation, installerVersion);

        // Bind the signed descriptor name to the signed archive.fileName.
        var declaredArchiveName = descriptor.Root
            .GetProperty("archive")
            .GetProperty("fileName")
            .GetString();
        if (string.IsNullOrEmpty(declaredArchiveName))
        {
            throw new InvalidDataException("CI 下载包签名 archive.fileName 为空。");
        }
        var archiveFileName = declaredArchiveName;
        var signedUpdateName = archiveFileName + ".update.json";
        var signedUpdateSigName = signedUpdateName + ".sig";
        var expectedDescriptorName = normalizedPrefix + signedUpdateName;
        if (!string.Equals(descriptorName, expectedDescriptorName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CI 下载包描述文件名 {descriptorName} 与签名 archive.fileName {declaredArchiveName} 不一致。");
        }
        if (!string.Equals(expectedSignatureName, normalizedPrefix + signedUpdateSigName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CI 下载包签名文件 {expectedSignatureName} 与签名 archive.fileName {declaredArchiveName} 不一致。");
        }

        // Build the exact declared part list from the signed descriptor. Only parts[] participates
        // in selection; parts.json is never read to decide what to stage.
        var declaredParts = new List<(string Name, long Bytes)>(descriptor.Root.GetProperty("parts").GetArrayLength());
        foreach (var part in descriptor.Root.GetProperty("parts").EnumerateArray())
        {
            var partName = part.GetProperty("fileName").GetString()
                ?? throw new InvalidDataException("签名 parts[].fileName 为空。");
            var partBytes = part.GetProperty("bytes").GetInt64();
            declaredParts.Add((partName, partBytes));
        }

        if (declaredParts.Count == 0)
        {
            // A CI wrapper must carry a split archive; a single-file archive is the direct form.
            return null;
        }

        var watchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in declaredParts)
        {
            if (!part.Name.StartsWith(archiveFileName + ".", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"签名 parts[].fileName 与 archive.fileName 不匹配：{part.Name}。");
            }
            watchedNames.Add(normalizedPrefix + part.Name);
        }

        // Pre-scan every non-directory entry before staging. Any final path component matching
        // ".zip." (case-insensitive) followed by a nonempty ASCII-digit suffix must be declared,
        // regardless of base name or directory; otherwise it is an undeclared volume. Never trust
        // parts.json entries for selection. Validation above already guaranteed normalized names,
        // uniqueness, and no traversal, duplicates or symlinks.
        foreach (var item in items)
        {
            if (item.IsDirectory || watchedNames.Contains(item.Name))
            {
                continue;
            }

            var finalComponent = item.Name[(item.Name.LastIndexOf('/') + 1)..];
            var dotZipIndex = finalComponent.LastIndexOf(".zip.", StringComparison.OrdinalIgnoreCase);
            if (dotZipIndex < 0)
            {
                continue;
            }

            var suffix = finalComponent[(dotZipIndex + ".zip.".Length)..];
            if (suffix.Length > 0 && suffix.All(char.IsAsciiDigit))
            {
                throw new InvalidDataException(
                    $"CI 下载包包含签名未声明的额外分卷：{item.Name}。");
            }
        }

        var stagedPlan = new List<(ArchiveItem Item, long Expected)>(declaredParts.Count);
        for (int index = 0; index < declaredParts.Count; index++)
        {
            var declared = declaredParts[index];
            var expectedName = normalizedPrefix + declared.Name;
            var matched = items.FirstOrDefault(
                item => !item.IsDirectory && string.Equals(item.Name, expectedName, StringComparison.Ordinal));
            if (matched is null)
            {
                throw new InvalidDataException($"CI 下载包缺少签名声明的分卷：{declared.Name}。");
            }
            if (matched.Entry.Length != declared.Bytes)
            {
                throw new InvalidDataException(
                    $"CI 下载包分卷长度不匹配：{declared.Name} 实际 {matched.Entry.Length}，签名 {declared.Bytes}。");
            }
            stagedPlan.Add((matched, declared.Bytes));
        }

        // Stage the signed descriptor, its signature, and each declared volume into a private
        // directory below the caller's work root. The bytes are copied with a bounded,
        // cancellable loop that fails on truncation.
        EnsureRegularSourceFiles(source);
        var canonicalWorkRoot = PrepareOfflineWorkRoot(workRoot);
        var stageRoot = CreateOfflineStageDirectory(canonicalWorkRoot);
        try
        {
            token.ThrowIfCancellationRequested();

            var stagedDescriptorPath = Path.Combine(stageRoot, signedUpdateName);
            var stagedSignaturePath = Path.Combine(stageRoot, signedUpdateSigName);
            if (!IsBeneath(stagedDescriptorPath, stageRoot) ||
                !IsBeneath(stagedSignaturePath, stageRoot))
            {
                throw new InvalidDataException("CI 下载包私密暂存路径越界。");
            }

            WriteBoundedStageFile(stagedDescriptorPath, descriptorBytes, token);
            WriteBoundedStageFile(stagedSignaturePath, signatureBytes, token);

            string? firstMediaPath = null;
            foreach (var (item, expected) in stagedPlan)
            {
                token.ThrowIfCancellationRequested();
                var relativeName = item.Name[normalizedPrefix.Length..];
                var targetPath = Path.Combine(stageRoot, relativeName);
                if (!IsBeneath(targetPath, stageRoot))
                {
                    throw new InvalidDataException($"CI 下载包分卷暂存路径越界：{relativeName}。");
                }

                using var input = item.Entry.Open();
                using var output = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    MediaHashBufferBytes,
                    FileOptions.SequentialScan);
                CopyBoundedCiPart(input, output, expected, token, relativeName);

                if (firstMediaPath is null)
                {
                    firstMediaPath = targetPath;
                }
            }

            if (firstMediaPath is null)
            {
                throw new InvalidDataException("CI 下载包未产出任何暂存分卷。");
            }

            return new PreparedCiArtifactSource(canonicalWorkRoot, stageRoot, firstMediaPath);
        }
        catch (Exception ex)
        {
            try
            {
                CleanupCiArtifactStage(canonicalWorkRoot, stageRoot);
            }
            catch (Exception cleanupEx)
            {
                ex.Data["CiArtifactStagingCleanupFailure"] = cleanupEx;
            }
            throw;
        }
    }

    internal static TResult WithPreparedCiArtifact<TResult>(
        PreparedCiArtifactSource? ciArtifact,
        Func<TResult> import)
    {
        ArgumentNullException.ThrowIfNull(import);

        TResult result;
        try
        {
            result = import();
        }
        catch (Exception primary)
        {
            try
            {
                ciArtifact?.Dispose();
            }
            catch (Exception cleanupEx)
            {
                primary.Data["CiArtifactStagingCleanupFailure"] = cleanupEx;
            }
            throw;
        }

        ciArtifact?.Dispose();
        return result;
    }

    private static bool HasCiWrapperLayout(List<ArchiveItem> items)
    {
        if (items is null)
        {
            return false;
        }

        var descriptorLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                continue;
            }

            var name = item.Name;
            if (name.EndsWith(CiUpdateJsonSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var leaf = name[(name.LastIndexOf('/') + 1)..];
                descriptorLeaves.Add(leaf[..^CiUpdateJsonSuffix.Length]);
            }
        }

        if (descriptorLeaves.Count == 0)
        {
            return false;
        }

        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                continue;
            }

            var name = item.Name;
            var leaf = name[(name.LastIndexOf('/') + 1)..];
            var dotZipIndex = leaf.LastIndexOf(".zip.", StringComparison.OrdinalIgnoreCase);
            if (dotZipIndex < 0)
            {
                continue;
            }

            var suffix = leaf[(dotZipIndex + ".zip.".Length)..];
            if (suffix.Length == 0 || !suffix.All(char.IsAsciiDigit))
            {
                continue;
            }

            var volumeLeaf = leaf[..dotZipIndex];
            if (descriptorLeaves.Contains(volumeLeaf))
            {
                return true;
            }
        }

        return false;
    }

    internal static void CleanupCiArtifactStage(string workRoot, string stageRoot)
    {
        try
        {
            CleanupOfflineUpdateStage(workRoot, stageRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Thread.Sleep(50);
            CleanupOfflineUpdateStage(workRoot, stageRoot);
        }
    }

    private static string GetNormalizedPrefix(string entryName)
    {
        var lastSlash = entryName.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : entryName[..(lastSlash + 1)];
    }

    private static void CopyBoundedCiPart(Stream input, Stream output, long expectedBytes, CancellationToken token, string partName)
    {
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        while (total < expectedBytes)
        {
            token.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(buffer.Length, expectedBytes - total);
            var read = input.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                break;
            }
            output.Write(buffer, 0, read);
            total += read;
        }

        if (total != expectedBytes)
        {
            throw new InvalidDataException(
                $"CI 下载包分卷 {partName} 实际读取 {total} 字节，签名声明 {expectedBytes} 字节。");
        }

        // Refuse overrun: try to read one more byte; a well-formed entry must not return one.
        if (input.Read(buffer, 0, 1) != 0)
        {
            throw new InvalidDataException($"CI 下载包分卷 {partName} 超过签名声明的 {expectedBytes} 字节。");
        }
    }
}
