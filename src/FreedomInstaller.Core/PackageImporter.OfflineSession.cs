using System.Text;
using System.Text.Json;

namespace FreedomInstaller.Core;

public static partial class PackageImporter
{
    private const string OfflineSessionSubdirectoryName = "offline-update-sessions";
    private const int OfflineSessionSchemaVersion = 1;
    private const int MaxOfflineSessionFileBytes = 64 * 1024;

    public static string SaveOfflineUpdateSession(
        string recordsRoot,
        OfflineUpdateSession session,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var canonicalRoot = ValidateSessionRecordsRoot(recordsRoot);
        var normalized = ValidateSessionRecord(session);
        var sessionsDir = EnsureSessionSubdirectory(canonicalRoot, createIfMissing: true)!
            ?? throw new InvalidDataException("无法准备离线会话记录目录。");
        var targetPath = GetSessionRecordFilePath(sessionsDir, normalized.SessionId);

        if (Path.Exists(targetPath))
        {
            var existing = LoadOfflineUpdateSession(canonicalRoot, normalized.SessionId, token);
            if (existing == normalized)
            {
                return targetPath;
            }

            throw new InvalidDataException($"离线升级会话记录已存在且内容冲突：{normalized.SessionId}。");
        }

        var payload = SerializeSessionRecord(normalized);
        if (payload.Length == 0 || payload.Length > MaxOfflineSessionFileBytes)
        {
            throw new InvalidDataException("离线升级会话记录序列化长度越界。");
        }

        var tempPath = Path.Combine(sessionsDir, $"{normalized.SessionId}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteBoundedStageFile(tempPath, payload, token);
            token.ThrowIfCancellationRequested();
            try
            {
                File.Move(tempPath, targetPath, overwrite: false);
            }
            catch (IOException) when (Path.Exists(targetPath))
            {
                var concurrent = LoadOfflineUpdateSession(canonicalRoot, normalized.SessionId, token);
                if (concurrent == normalized)
                {
                    return targetPath;
                }

                throw new InvalidDataException($"并发写入的离线升级会话记录内容冲突：{normalized.SessionId}。");
            }

            var verified = LoadOfflineUpdateSession(canonicalRoot, normalized.SessionId, token);
            if (verified != normalized)
            {
                throw new InvalidDataException("离线升级会话记录落盘校验不一致。");
            }

            return targetPath;
        }
        finally
        {
            if (Path.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public static OfflineUpdateSession LoadOfflineUpdateSession(
        string recordsRoot,
        string sessionId,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var canonicalRoot = ValidateSessionRecordsRoot(recordsRoot);
        var canonicalSessionId = ValidateSessionId(sessionId);
        var sessionsDir = EnsureSessionSubdirectory(canonicalRoot, createIfMissing: false)
            ?? throw new InvalidDataException($"离线升级会话目录不存在：{canonicalSessionId}。");
        var targetPath = GetSessionRecordFilePath(sessionsDir, canonicalSessionId);

        var info = new FileInfo(targetPath);
        if (!info.Exists ||
            info.LinkTarget is not null ||
            (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
            info.Length <= 0 ||
            info.Length > MaxOfflineSessionFileBytes)
        {
            throw new InvalidDataException($"离线升级会话记录文件状态或大小非法：{targetPath}。");
        }

        var bytes = ReadOfflineControlFile(targetPath, canonicalRoot, token);
        if (bytes.Length <= 0 || bytes.Length > MaxOfflineSessionFileBytes)
        {
            throw new InvalidDataException($"离线升级会话记录文件字节数越界：{targetPath}。");
        }

        var loaded = DeserializeSessionRecord(bytes);
        if (!string.Equals(loaded.SessionId, canonicalSessionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("离线升级会话记录 ID 与文件名不一致。");
        }

        return loaded;
    }

    public static IReadOnlyList<OfflineUpdateSession> FindOfflineUpdateSessions(
        string recordsRoot,
        string deviceModel,
        string recoveryPartUuid,
        string packageDescriptorSha256,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var canonicalRoot = ValidateSessionRecordsRoot(recordsRoot);
        var expectedModel = ValidateDeviceModel(deviceModel);
        var expectedUuid = ValidateTransactionId(recoveryPartUuid);
        var expectedSha = ValidateLowerHexSha256(packageDescriptorSha256, "离线包描述 SHA256");

        var sessionsDir = EnsureSessionSubdirectory(canonicalRoot, createIfMissing: false);
        if (sessionsDir is null)
        {
            return Array.Empty<OfflineUpdateSession>();
        }

        var files = Directory.GetFiles(sessionsDir, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        var matches = new List<OfflineUpdateSession>();

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (!fileName.EndsWith(".json", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"离线升级会话记录文件扩展名非法：{fileName}。");
            }

            var fileSessionId = ValidateSessionId(fileName[..^5]);
            var record = LoadOfflineUpdateSession(canonicalRoot, fileSessionId, token);
            if (string.Equals(record.DeviceModel, expectedModel, StringComparison.Ordinal) &&
                string.Equals(record.RecoveryPartUuid, expectedUuid, StringComparison.Ordinal) &&
                string.Equals(record.PackageDescriptorSha256, expectedSha, StringComparison.Ordinal))
            {
                matches.Add(record);
            }
        }

        return matches;
    }

    private static string ValidateSessionRecordsRoot(string recordsRoot)
    {
        if (string.IsNullOrWhiteSpace(recordsRoot) || !Path.IsPathFullyQualified(recordsRoot))
        {
            throw new InvalidDataException("离线升级会话记录根目录必须为非空绝对路径。");
        }

        return PrepareOfflineWorkRoot(recordsRoot);
    }

    private static string? EnsureSessionSubdirectory(string canonicalRoot, bool createIfMissing)
    {
        var sessionsDir = Path.Combine(canonicalRoot, OfflineSessionSubdirectoryName);
        EnsureNoReparseAncestors(sessionsDir);
        if (!Path.Exists(sessionsDir))
        {
            if (!createIfMissing)
            {
                return null;
            }

            Directory.CreateDirectory(sessionsDir);
        }

        EnsureNoReparseAncestors(sessionsDir);
        EnsureSafeDirectoryTree(sessionsDir, canonicalRoot);
        return sessionsDir;
    }

    private static string GetSessionRecordFilePath(string sessionsDir, string sessionId) =>
        Path.Combine(sessionsDir, $"{sessionId}.json");

    private static OfflineUpdateSession ValidateSessionRecord(OfflineUpdateSession session)
    {
        if (session is null)
        {
            throw new InvalidDataException("离线升级会话记录不能为空。");
        }

        var sid = ValidateSessionId(session.SessionId);
        var model = ValidateDeviceModel(session.DeviceModel);
        if (!string.Equals(session.DeviceModel, model, StringComparison.Ordinal))
        {
            throw new InvalidDataException("离线升级会话设备型号包含非规范空白字符。");
        }

        var uuid = ValidateTransactionId(session.RecoveryPartUuid);
        var sha = ValidateLowerHexSha256(session.PackageDescriptorSha256, "离线包描述 SHA256");
        return new OfflineUpdateSession(sid, model, uuid, sha);
    }

    private static string ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length != 32)
        {
            throw new InvalidDataException("离线升级会话 ID 必须为 32 位小写十六进制字符串。");
        }

        foreach (var ch in sessionId)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                throw new InvalidDataException("离线升级会话 ID 包含非法字符。");
            }
        }

        return sessionId;
    }

    private static byte[] SerializeSessionRecord(OfflineUpdateSession session) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = OfflineSessionSchemaVersion,
            sessionId = session.SessionId,
            deviceModel = session.DeviceModel,
            recoveryPartUuid = session.RecoveryPartUuid,
            packageDescriptorSha256 = session.PackageDescriptorSha256
        });

    private static OfflineUpdateSession DeserializeSessionRecord(byte[] utf8Json)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("离线升级会话记录根节点必须为 JSON 对象。");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int? schemaVersion = null;
            string? sessionId = null, deviceModel = null, recoveryPartUuid = null, packageSha = null;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!seen.Add(prop.Name))
                {
                    throw new InvalidDataException($"离线升级会话记录包含重复字段：{prop.Name}。");
                }

                switch (prop.Name)
                {
                    case "schemaVersion" when prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var ver):
                        schemaVersion = ver;
                        break;
                    case "sessionId" when prop.Value.ValueKind == JsonValueKind.String:
                        sessionId = prop.Value.GetString();
                        break;
                    case "deviceModel" when prop.Value.ValueKind == JsonValueKind.String:
                        deviceModel = prop.Value.GetString();
                        break;
                    case "recoveryPartUuid" when prop.Value.ValueKind == JsonValueKind.String:
                        recoveryPartUuid = prop.Value.GetString();
                        break;
                    case "packageDescriptorSha256" when prop.Value.ValueKind == JsonValueKind.String:
                        packageSha = prop.Value.GetString();
                        break;
                    default:
                        throw new InvalidDataException($"离线升级会话记录包含未知或类型非法字段：{prop.Name}。");
                }
            }

            if (seen.Count != 5 || schemaVersion != OfflineSessionSchemaVersion)
            {
                throw new InvalidDataException("离线升级会话记录结构版本或字段数量非法。");
            }

            return ValidateSessionRecord(new OfflineUpdateSession(
                sessionId ?? string.Empty,
                deviceModel ?? string.Empty,
                recoveryPartUuid ?? string.Empty,
                packageSha ?? string.Empty));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("离线升级会话记录 JSON 格式损坏。", ex);
        }
    }
}
