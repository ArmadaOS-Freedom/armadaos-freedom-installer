using System.Security.Cryptography;
using System.Text.Json;

namespace FreedomInstaller.Core;

public static partial class PackageImporter
{
    public static SavedOfflineUpdateRecovery LoadOfflineUpdateRecovery(
        string directory, OfflineUpdateRecoveryContext expected, string packageDescriptorSha256,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidDataException("恢复备份目录必须为完整路径。");
        token.ThrowIfCancellationRequested();
        var full = CanonicalRecoveryDirectory(directory);
        var planPath = Path.GetFullPath(Path.Combine(full, RecoveryPlanFileName));
        var manifestPath = Path.GetFullPath(Path.Combine(full, RecoveryManifestFileName));
        var ackPath = Path.GetFullPath(Path.Combine(full, RecoveryAckFileName));
        var ack = ReadOfflineControlFile(ackPath, full, token);
        var saved = new SavedOfflineUpdateRecovery(DirectoryPath: full, ManifestPath: manifestPath, PlanPath: planPath, Acknowledgement: ack);
        _ = ValidateSavedOfflineUpdateRecovery(saved, expected, packageDescriptorSha256, token);
        return saved;
    }

    internal static byte[] ValidateSavedOfflineUpdateRecovery(
        SavedOfflineUpdateRecovery saved,
        OfflineUpdateRecoveryContext expected,
        string packageDescriptorSha256,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(saved.Acknowledgement);
        token.ThrowIfCancellationRequested();
        var directory = CanonicalRecoveryDirectory(saved.DirectoryPath);
        EnsureDirectorySubtreeNotReparse(directory);
        var tx = ValidateTransactionId(expected.TransactionId);
        var model = ValidateDeviceModel(expected.DeviceModel);
        var planSha = ValidateLowerHexSha256(expected.PlanSha256, "PlanSha256");
        var manifestSha = ValidateLowerHexSha256(expected.RecoveryManifestSha256, "RecoveryManifestSha256");
        var packageSha = ValidateLowerHexSha256(packageDescriptorSha256, "PackageDescriptorSha256");

        var planPath = Path.GetFullPath(Path.Combine(directory, RecoveryPlanFileName));
        var manifestPath = Path.GetFullPath(Path.Combine(directory, RecoveryManifestFileName));
        var ackPath = Path.GetFullPath(Path.Combine(directory, RecoveryAckFileName));
        if (!IsBeneath(planPath, directory) || !IsBeneath(manifestPath, directory) || !IsBeneath(ackPath, directory) ||
            !string.Equals(Path.GetFullPath(saved.PlanPath), planPath, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(saved.ManifestPath), manifestPath, StringComparison.Ordinal))
            throw new InvalidDataException("保存的恢复元数据路径与规范路径不一致。");

        var plan = ReadOfflineControlFile(planPath, directory, token);
        var manifest = ReadOfflineControlFile(manifestPath, directory, token);
        VerifyMetadataBlob(plan, planSha, RecoveryPlanFileName);
        VerifyMetadataBlob(manifest, manifestSha, RecoveryManifestFileName);
        ValidateRecoveryPlanJson(plan, tx, model, manifestSha);
        using (var doc = ParseStrictJsonObject(plan, RecoveryPlanFileName))
            RequireExactStringProperty(doc.RootElement, "packageDescriptorSha256", packageSha, RecoveryPlanFileName);
        var (files, _) = ParseAndValidateRecoveryManifestJson(manifest, tx, model);
        var buffer = new byte[RecoveryCopyBufferBytes];
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var path = ResolveSafePayloadTargetPath(directory, file.Path);
            VerifyStoredFileOnDisk(path, directory, file.Bytes, file.Sha256, buffer, token);
        }
        VerifyStoredFileOnDisk(planPath, directory, plan.Length, planSha, buffer, token);
        VerifyStoredFileOnDisk(manifestPath, directory, manifest.Length, manifestSha, buffer, token);
        var diskAck = ReadOfflineControlFile(ackPath, directory, token);
        var rebuilt = BuildRecoveryAcknowledgementBytes(tx, planSha, manifestSha, files);
        if (!CryptographicOperations.FixedTimeEquals(diskAck, rebuilt) ||
            !CryptographicOperations.FixedTimeEquals(diskAck, saved.Acknowledgement))
            throw new InvalidDataException("恢复确认文件与已验证的恢复内容不一致。");
        return diskAck;
    }

    /// <summary>
    /// Resolves a bundle directory the same way the exporter resolves its work root: a linked
    /// ancestor is followed to its target, while the bundle directory itself must be an existing
    /// ordinary directory. Nothing is created here, so loading can never turn a missing root into a
    /// new one.
    /// </summary>
    private static string CanonicalRecoveryDirectory(string directory)
    {
        try
        {
            return PrepareOfflineWorkRoot(directory);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new InvalidDataException("恢复备份目录不存在或为 reparse point。", ex);
        }
    }

    private static byte[] ReadOfflineControlFile(string path, string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(path);
        if (!IsBeneath(full, root)) throw new InvalidDataException("恢复控制文件路径越界。");
        EnsureSafeDirectoryTree(Path.GetDirectoryName(full) ?? root, root);
        var info = new FileInfo(full);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
            info.Length <= 0 || info.Length > MaxRecoveryMetadataBytes)
            throw new InvalidDataException($"恢复控制文件状态或长度非法：{full}。");
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
            RecoveryCopyBufferBytes, FileOptions.SequentialScan);
        var bytes = new byte[info.Length];
        stream.ReadExactly(bytes);
        token.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1) throw new InvalidDataException("恢复控制文件读取期间长度发生变化。");
        return bytes;
    }
}
