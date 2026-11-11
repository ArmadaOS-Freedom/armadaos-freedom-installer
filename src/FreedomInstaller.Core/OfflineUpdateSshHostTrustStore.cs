using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreedomInstaller.Core;

/// <summary>
/// Task/application-owned trust storage for the managed password transport.
/// Only the host public key and its fingerprint are written; no password,
/// private key, or known_hosts file is required for password authentication.
/// </summary>
public sealed class OfflineUpdateSshHostTrustStore
{
    private const int Schema = 1;
    private const int MaxJsonBytes = 4096;
    private const string KeyType = "ssh-ed25519";

    public OfflineUpdateSshHostTrustStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Trust directory must be fully qualified.", nameof(directory));

        DirectoryPath = Path.GetFullPath(directory);
    }

    public string DirectoryPath { get; }

    public OfflineUpdateSshHostTrustState Evaluate(string host, int port, OfflineUpdateSshKey hostKey)
    {
        var (canonicalHost, canonicalPort, parsed) = Normalize(host, port, hostKey);
        var path = GetPath(canonicalHost, canonicalPort);
        if (!File.Exists(path))
            return new OfflineUpdateSshHostTrustState(OfflineUpdateSshHostTrustStatus.Unknown, null);

        var stored = Read(path, canonicalHost, canonicalPort);
        return string.Equals(stored.PublicKey, parsed.PublicKey, StringComparison.Ordinal)
            ? new OfflineUpdateSshHostTrustState(OfflineUpdateSshHostTrustStatus.Trusted, stored.Fingerprint)
            : new OfflineUpdateSshHostTrustState(OfflineUpdateSshHostTrustStatus.Changed, stored.Fingerprint);
    }

    /// <summary>
    /// Persists the approved host key. Callers must have obtained explicit
    /// approval for an unknown or changed key first.
    /// </summary>
    public void Store(string host, int port, OfflineUpdateSshKey hostKey)
    {
        var (canonicalHost, canonicalPort, parsed) = Normalize(host, port, hostKey);
        Directory.CreateDirectory(DirectoryPath);
        EnsureRegularDirectory(DirectoryPath);

        var path = GetPath(canonicalHost, canonicalPort);
        EnsureTargetNotLinkOrDirectory(path);

        var payload = Serialize(canonicalHost, canonicalPort, parsed);
        var temp = Path.Combine(DirectoryPath, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Best-effort temp cleanup; the original error is more useful.
            }

            throw;
        }
    }

    public string GetPath(string host, int port) =>
        Path.Combine(DirectoryPath, "host-" + HashHost(host, port) + ".json");

    private static (string Host, int Port, OfflineUpdateSshKey Key) Normalize(string host, int port, OfflineUpdateSshKey hostKey)
    {
        ArgumentNullException.ThrowIfNull(hostKey);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(hostKey.PublicKey) || string.IsNullOrWhiteSpace(hostKey.Fingerprint))
            throw new ArgumentException("Host key and fingerprint must not be empty.", nameof(hostKey));

        var canonicalHost = OfflineUpdateSshConnection.ValidateHost(host);
        var parsed = OfflineUpdateSshEnrollment.ParsePublicKey(hostKey.PublicKey);
        if (!string.Equals(parsed.Fingerprint, hostKey.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Host key fingerprint does not match the supplied public key.", nameof(hostKey));

        return (canonicalHost, port, parsed);
    }

    private static string HashHost(string host, int port)
    {
        var bytes = Encoding.UTF8.GetBytes(
            host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static byte[] Serialize(string host, int port, OfflineUpdateSshKey key)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", Schema);
            writer.WriteString("host", host);
            writer.WriteNumber("port", port);
            writer.WriteString("keyType", KeyType);
            writer.WriteString("publicKey", key.PublicKey);
            writer.WriteString("fingerprint", key.Fingerprint);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static OfflineUpdateSshKey Read(string path, string expectedHost, int expectedPort)
    {
        EnsureTargetNotLinkOrDirectory(path);
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaxJsonBytes)
            throw new IOException("Stored host trust record has an invalid size.");

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxJsonBytes)
            throw new IOException("Stored host trust record exceeds the size limit.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new IOException("Stored host trust record is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || CountProperties(root) != 6)
                throw new IOException("Stored host trust record has an unexpected shape.");

            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaValue) || schemaValue != Schema)
            {
                throw new IOException("Stored host trust record uses an unsupported schema.");
            }

            if (!root.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.String ||
                !string.Equals(host.GetString(), expectedHost, StringComparison.Ordinal))
            {
                throw new IOException("Stored host trust record belongs to a different host.");
            }

            if (!root.TryGetProperty("port", out var port) || port.ValueKind != JsonValueKind.Number ||
                !port.TryGetInt32(out var portValue) || portValue != expectedPort)
            {
                throw new IOException("Stored host trust record belongs to a different port.");
            }

            if (!root.TryGetProperty("keyType", out var keyType) || keyType.ValueKind != JsonValueKind.String ||
                !string.Equals(keyType.GetString(), KeyType, StringComparison.Ordinal))
            {
                throw new IOException("Stored host trust record uses an unsupported key type.");
            }

            if (!root.TryGetProperty("publicKey", out var publicKey) || publicKey.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("fingerprint", out var fingerprint) || fingerprint.ValueKind != JsonValueKind.String)
            {
                throw new IOException("Stored host trust record is missing its public key or fingerprint.");
            }

            var parsed = OfflineUpdateSshEnrollment.ParsePublicKey(publicKey.GetString() ?? string.Empty);
            if (!string.Equals(parsed.Fingerprint, fingerprint.GetString(), StringComparison.Ordinal))
                throw new IOException("Stored host trust record fingerprint does not match its public key.");

            return parsed;
        }
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject())
            count++;
        return count;
    }

    private static void EnsureRegularDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            throw new IOException("Trust directory does not exist.");
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Trust directory must not be a link or reparse point.");
    }

    private static void EnsureTargetNotLinkOrDirectory(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            throw new IOException("Trust record path is a link or reparse point.");

        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Trust record path is a directory, link, or reparse point.");
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
