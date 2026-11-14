using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreedomInstaller.Core;

public static class OfflineUpdateSshTrustStore
{
    private const string ExpectedHostIp = "172.16.42.1";
    private const int MaxJsonUtf8Bytes = 4096;
    private static readonly Regex ValidUserRegex = new(@"\A[a-z_][a-z0-9_.-]{0,63}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static OfflineUpdateSshOptions TrustUsbHost(OfflineUpdateSshHostCandidate candidate, OfflineUpdateSshIdentity identity, string authorizationJson)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationJson);

        ValidateEndpoint(candidate.Endpoint);
        ValidateIdentity(identity);

        var (hostPublicKey, canonicalHostFp) = ValidatePublicKeyRecord(candidate.HostKey);
        var (installerPublicKey, canonicalInstallerFp) = ValidatePublicKeyRecord(identity.PublicKey);

        AuthPayload auth;
        try
        {
            auth = ParseAuthorizationPayload(authorizationJson);
        }
        catch (JsonException ex)
        {
            throw new IOException("Malformed authorization JSON.", ex);
        }

        if (auth.HostKeyFingerprint != canonicalHostFp || auth.InstallerKeyFingerprint != canonicalInstallerFp)
        {
            throw new IOException("Authorization fingerprints do not match provided candidate or installer keys.");
        }

        var trustDir = Path.GetDirectoryName(Path.GetFullPath(identity.IdentityFile))
            ?? throw new IOException("Cannot determine trust directory from identity file.");

        var baseName = ComputeHostBaseName(hostPublicKey);
        var profilePath = Path.Combine(trustDir, baseName + ".json");
        var knownHostsPath = Path.Combine(trustDir, baseName + ".known_hosts");

        EnsureTargetNotLinkOrDirectory(profilePath);
        EnsureTargetNotLinkOrDirectory(knownHostsPath);

        var profileJsonBytes = SerializeProfile(new StoredProfile(
            1,
            auth.UserName,
            hostPublicKey,
            canonicalInstallerFp
        ));

        var knownHostsContent = Encoding.UTF8.GetBytes($"{ExpectedHostIp} {hostPublicKey}\n");

        WriteAtomic(profilePath, profileJsonBytes);
        WriteAtomic(knownHostsPath, knownHostsContent);

        return new OfflineUpdateSshOptions(
            Host: ExpectedHostIp,
            Port: 22,
            UserName: auth.UserName,
            IdentityFile: identity.IdentityFile,
            KnownHostsFile: knownHostsPath,
            SshExecutable: null,
            BindAddress: candidate.Endpoint.LocalAddress
        );
    }

    public static OfflineUpdateSshOptions? TryLoadUsbHost(OfflineUpdateSshHostCandidate candidate, OfflineUpdateSshIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(identity);

        ValidateEndpoint(candidate.Endpoint);
        ValidateIdentity(identity);

        var (hostPublicKey, canonicalHostFp) = ValidatePublicKeyRecord(candidate.HostKey);
        var (_, canonicalInstallerFp) = ValidatePublicKeyRecord(identity.PublicKey);

        var trustDir = Path.GetDirectoryName(Path.GetFullPath(identity.IdentityFile))
            ?? throw new IOException("Cannot determine trust directory from identity file.");

        var baseName = ComputeHostBaseName(hostPublicKey);
        var profilePath = Path.Combine(trustDir, baseName + ".json");
        var knownHostsPath = Path.Combine(trustDir, baseName + ".known_hosts");

        EnsureTargetNotLinkOrDirectory(profilePath);
        if (!File.Exists(profilePath))
        {
            return null;
        }

        StoredProfile profile;
        try
        {
            profile = ReadAndValidateProfile(profilePath);
        }
        catch (JsonException ex)
        {
            throw new IOException("Malformed stored profile JSON.", ex);
        }

        var (storedHostPk, storedHostFp) = ValidatePublicKeyRecord(new OfflineUpdateSshKey(profile.HostPublicKey, canonicalHostFp));
        if (storedHostPk != hostPublicKey || storedHostFp != canonicalHostFp)
        {
            throw new IOException("Stored profile host public key does not match candidate host key.");
        }

        if (profile.InstallerKeyFingerprint != canonicalInstallerFp)
        {
            return null;
        }

        EnsureTargetNotLinkOrDirectory(knownHostsPath);
        var knownHostsContent = Encoding.UTF8.GetBytes($"{ExpectedHostIp} {hostPublicKey}\n");
        WriteAtomic(knownHostsPath, knownHostsContent);

        return new OfflineUpdateSshOptions(
            Host: ExpectedHostIp,
            Port: 22,
            UserName: profile.UserName,
            IdentityFile: identity.IdentityFile,
            KnownHostsFile: knownHostsPath,
            SshExecutable: null,
            BindAddress: candidate.Endpoint.LocalAddress
        );
    }

    internal static OfflineUpdateSshOptions TrustRecoveryUsbHost(
        OfflineUpdateUsbEndpoint endpoint,
        OfflineUpdateSshIdentity identity,
        OfflineUpdateSshKey expectedHostKey,
        string sessionId)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(expectedHostKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (sessionId.Length != 32 || !sessionId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new IOException("Recovery session id must be 32 lowercase hex characters.");
        }

        ValidateEndpoint(endpoint);
        ValidateIdentity(identity);
        ValidatePublicKeyRecord(identity.PublicKey);
        var (hostPublicKey, _) = ValidatePublicKeyRecord(expectedHostKey);

        var trustDir = Path.GetDirectoryName(Path.GetFullPath(identity.IdentityFile))
            ?? throw new IOException("Cannot determine trust directory from identity file.");

        var knownHostsPath = Path.Combine(
            trustDir,
            "recovery-" + sessionId + "-" + ComputeHostBaseName(hostPublicKey) + ".known_hosts");

        EnsureTargetNotLinkOrDirectory(knownHostsPath);

        var knownHostsContent = Encoding.UTF8.GetBytes($"{ExpectedHostIp} {hostPublicKey}\n");
        WriteAtomic(knownHostsPath, knownHostsContent);

        // Temporary trusted-local-recovery known_hosts only; no stored profile or authorization
        // JSON is written, and root is intentionally permitted only on this recovery path.
        return new OfflineUpdateSshOptions(
            Host: ExpectedHostIp,
            Port: 22,
            UserName: "root",
            IdentityFile: identity.IdentityFile,
            KnownHostsFile: knownHostsPath,
            SshExecutable: null,
            BindAddress: endpoint.LocalAddress
        );
    }

    private static (string CanonicalPublicKey, string CanonicalFingerprint) ValidatePublicKeyRecord(OfflineUpdateSshKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(key.PublicKey) || string.IsNullOrWhiteSpace(key.Fingerprint))
        {
            throw new IOException("Public key and fingerprint must not be empty.");
        }

        var parsed = OfflineUpdateSshEnrollment.ParsePublicKey(key.PublicKey);
        if (parsed.Fingerprint != key.Fingerprint)
        {
            throw new IOException("Supplied key fingerprint does not match computed public key fingerprint.");
        }

        return (parsed.PublicKey, parsed.Fingerprint);
    }

    private static void ValidateEndpoint(OfflineUpdateUsbEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Host != ExpectedHostIp)
        {
            throw new IOException($"Candidate host must be exactly {ExpectedHostIp}.");
        }

        if (!IPAddress.TryParse(endpoint.LocalAddress, out var localIp) || localIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new IOException("Candidate local address must be a valid IPv4 address.");
        }

        if (endpoint.LocalAddress != localIp.ToString())
        {
            throw new IOException("Candidate local address must use canonical IPv4 notation.");
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!localIp.TryWriteBytes(bytes, out _))
        {
            throw new IOException("Invalid local address bytes.");
        }

        if (bytes[0] != 172 || bytes[1] != 16 || bytes[2] != 42 || bytes[3] < 2 || bytes[3] > 254)
        {
            throw new IOException("Candidate local address must be within 172.16.42.2..172.16.42.254 range.");
        }
    }

    private static void ValidateIdentity(OfflineUpdateSshIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.IdentityFile))
        {
            throw new IOException("Identity file path is required.");
        }

        if (!Path.IsPathFullyQualified(identity.IdentityFile)
            || identity.IdentityFile.IndexOfAny(new[] { '\r', '\n', '\0', '"', '%' }) >= 0
            || identity.IdentityFile.Contains("${", StringComparison.Ordinal))
        {
            throw new IOException("Identity file path must be fully qualified and contain no expansion or control characters.");
        }

        var fullPath = Path.GetFullPath(identity.IdentityFile);
        if (!File.Exists(fullPath))
        {
            throw new IOException("Identity file does not exist.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.LinkTarget != null || fileInfo.Attributes.HasFlag(FileAttributes.Directory) || fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Identity file must be a regular file, not a symlink or directory.");
        }

        var dir = Path.GetDirectoryName(fullPath) ?? throw new IOException("Invalid identity directory.");
        var dirInfo = new DirectoryInfo(dir);
        if (!dirInfo.Exists || dirInfo.LinkTarget != null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Identity directory must exist and cannot be a symlink.");
        }
    }

    private static void ValidateUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName) || !ValidUserRegex.IsMatch(userName) || userName == "root")
        {
            throw new IOException("Invalid user name or root user is rejected.");
        }
    }

    private static string ComputeHostBaseName(string canonicalHostPublicKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalHostPublicKey));
        return "usb-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void EnsureTargetNotLinkOrDirectory(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget != null)
        {
            throw new IOException($"Target path '{path}' is a directory, link, or reparse point.");
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"Target path '{path}' is a directory, link, or reparse point.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void WriteAtomic(string destinationPath, byte[] data)
    {
        EnsureTargetNotLinkOrDirectory(destinationPath);
        var dir = Path.GetDirectoryName(destinationPath) ?? throw new IOException("Invalid destination directory.");
        var tempFile = Path.Combine(dir, ".tmp-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var fs = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempFile, destinationPath, overwrite: true);
        }
        catch (Exception original)
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(original, cleanup);
            }

            throw;
        }
    }

    private sealed record AuthPayload(int Schema, string UserName, string HostKeyFingerprint, string InstallerKeyFingerprint);
    private sealed record StoredProfile(int Schema, string UserName, string HostPublicKey, string InstallerKeyFingerprint);

    private static AuthPayload ParseAuthorizationPayload(string jsonString)
    {
        var utf8 = Encoding.UTF8.GetBytes(jsonString);
        if (utf8.Length > MaxJsonUtf8Bytes)
        {
            throw new IOException("Authorization JSON exceeds 4096 bytes.");
        }

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new IOException("Authorization JSON must be a single JSON object.");
        }

        int? schema = null;
        string? userName = null;
        string? hostFp = null;
        string? instFp = null;
        int fieldCount = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new IOException("Invalid JSON structure in authorization payload.");
            }

            var propName = reader.GetString();
            fieldCount++;
            if (!reader.Read())
            {
                throw new IOException("Truncated JSON payload.");
            }

            switch (propName)
            {
                case "schema":
                    if (schema.HasValue || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var sVal))
                        throw new IOException("Invalid or duplicate schema field.");
                    schema = sVal;
                    break;
                case "userName":
                    if (userName != null || reader.TokenType != JsonTokenType.String)
                        throw new IOException("Invalid or duplicate userName field.");
                    userName = reader.GetString();
                    break;
                case "hostKeyFingerprint":
                    if (hostFp != null || reader.TokenType != JsonTokenType.String)
                        throw new IOException("Invalid or duplicate hostKeyFingerprint field.");
                    hostFp = reader.GetString();
                    break;
                case "installerKeyFingerprint":
                    if (instFp != null || reader.TokenType != JsonTokenType.String)
                        throw new IOException("Invalid or duplicate installerKeyFingerprint field.");
                    instFp = reader.GetString();
                    break;
                default:
                    throw new IOException($"Unexpected field in authorization payload: '{propName}'.");
            }
        }

        if (reader.Read())
        {
            throw new IOException("Trailing content after authorization JSON object.");
        }

        if (fieldCount != 4 || schema != 1 || string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(hostFp) || string.IsNullOrWhiteSpace(instFp))
        {
            throw new IOException("Authorization JSON does not meet schema requirements.");
        }

        ValidateUserName(userName);
        return new AuthPayload(schema.Value, userName, hostFp, instFp);
    }

    private static byte[] SerializeProfile(StoredProfile profile)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", profile.Schema);
            writer.WriteString("userName", profile.UserName);
            writer.WriteString("hostPublicKey", profile.HostPublicKey);
            writer.WriteString("installerKeyFingerprint", profile.InstallerKeyFingerprint);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static StoredProfile ReadAndValidateProfile(string profilePath)
    {
        if (new FileInfo(profilePath).Length > MaxJsonUtf8Bytes)
        {
            throw new IOException("Profile file exceeds 4096 bytes.");
        }

        var bytes = File.ReadAllBytes(profilePath);
        if (bytes.Length > MaxJsonUtf8Bytes)
        {
            throw new IOException("Profile file exceeds 4096 bytes.");
        }

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new IOException("Stored profile must be a JSON object.");
        }

        int? schema = null;
        string? userName = null;
        string? hostPk = null;
        string? instFp = null;
        int count = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new IOException("Invalid profile JSON format.");
            }

            var prop = reader.GetString();
            count++;
            if (!reader.Read())
            {
                throw new IOException("Truncated profile JSON.");
            }

            switch (prop)
            {
                case "schema":
                    if (schema.HasValue || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var sVal))
                        throw new IOException("Invalid or duplicate schema in profile.");
                    schema = sVal;
                    break;
                case "userName":
                    if (userName != null || reader.TokenType != JsonTokenType.String)
                        throw new IOException("Invalid or duplicate userName in profile.");
                    userName = reader.GetString();
                    break;
                case "hostPublicKey":
                    if (hostPk != null || reader.TokenType != JsonTokenType.String)
                        throw new IOException("Invalid or duplicate hostPublicKey in profile.");
                    hostPk = reader.GetString();
                    break;
                case "installerKeyFingerprint":
                    if (instFp != null || reader.TokenType != JsonTokenType.String)
                        throw new IOException("Invalid or duplicate installerKeyFingerprint in profile.");
                    instFp = reader.GetString();
                    break;
                default:
                    throw new IOException($"Unexpected field in profile: '{prop}'.");
            }
        }

        if (reader.Read())
        {
            throw new IOException("Trailing data in profile JSON.");
        }

        if (count != 4 || schema != 1 || string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(hostPk) || string.IsNullOrWhiteSpace(instFp))
        {
            throw new IOException("Profile content does not match expected schema.");
        }

        ValidateUserName(userName);
        return new StoredProfile(schema.Value, userName, hostPk, instFp);
    }
}
