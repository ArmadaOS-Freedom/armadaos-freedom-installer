using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateSshHostTrustStoreTests : IDisposable
{
    private readonly string _root;

    public OfflineUpdateSshHostTrustStoreTests()
    {
        var rootEnv = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(rootEnv))
            throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be explicitly set.");

        _root = Path.Combine(rootEnv, "HostTrust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static OfflineUpdateSshKey CreateKey(byte seed)
    {
        var wire = new byte[51];
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0, 4), 11);
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(wire.AsSpan(4, 11));
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(15, 4), 32);
        for (var i = 19; i < wire.Length; i++)
            wire[i] = seed;

        var publicKey = "ssh-ed25519 " + Convert.ToBase64String(wire);
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(wire)).TrimEnd('=');
        return new OfflineUpdateSshKey(publicKey, fingerprint);
    }

    [Fact]
    public void Evaluate_Unknown_WritesNothing()
    {
        var store = new OfflineUpdateSshHostTrustStore(_root);
        var key = CreateKey(1);

        var state = store.Evaluate("172.16.42.1", 22, key);

        Assert.Equal(OfflineUpdateSshHostTrustStatus.Unknown, state.Status);
        Assert.Null(state.StoredFingerprint);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void Store_PersistsOnlyThePublicKeyAndThenEvaluatesTrusted()
    {
        var store = new OfflineUpdateSshHostTrustStore(_root);
        var key = CreateKey(2);

        store.Store("172.16.42.1", 22, key);

        var state = store.Evaluate("172.16.42.1", 22, key);
        Assert.Equal(OfflineUpdateSshHostTrustStatus.Trusted, state.Status);
        Assert.Equal(key.Fingerprint, state.StoredFingerprint);

        var path = Assert.Single(Directory.GetFiles(_root));
        var content = File.ReadAllText(path);
        Assert.Contains(key.PublicKey, content, StringComparison.Ordinal);
        Assert.Contains(key.Fingerprint, content, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ChangedKey_ReportsChangeAndDoesNotOverwrite()
    {
        var store = new OfflineUpdateSshHostTrustStore(_root);
        var trusted = CreateKey(3);
        var changed = CreateKey(4);
        store.Store("172.16.42.1", 22, trusted);
        var path = Assert.Single(Directory.GetFiles(_root));
        var before = File.ReadAllText(path);

        var state = store.Evaluate("172.16.42.1", 22, changed);

        Assert.Equal(OfflineUpdateSshHostTrustStatus.Changed, state.Status);
        Assert.Equal(trusted.Fingerprint, state.StoredFingerprint);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Store_AfterExplicitApproval_ReplacesChangedKey()
    {
        var store = new OfflineUpdateSshHostTrustStore(_root);
        var trusted = CreateKey(5);
        var replacement = CreateKey(6);
        store.Store("172.16.42.1", 22, trusted);

        store.Store("172.16.42.1", 22, replacement);

        var state = store.Evaluate("172.16.42.1", 22, replacement);
        Assert.Equal(OfflineUpdateSshHostTrustStatus.Trusted, state.Status);
        Assert.Equal(replacement.Fingerprint, state.StoredFingerprint);
    }

    [Fact]
    public void Evaluate_TamperedRecord_FailsClosed()
    {
        var store = new OfflineUpdateSshHostTrustStore(_root);
        var key = CreateKey(7);
        store.Store("172.16.42.1", 22, key);

        var path = Assert.Single(Directory.GetFiles(_root));
        // Change the JSON property through the model. A raw string replace can
        // silently do nothing because Utf8JsonWriter escapes characters such
        // as '/', which are common in SHA256 base64 fingerprints.
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(key.Fingerprint, record["fingerprint"]!.GetValue<string>());
        record["fingerprint"] = CreateKey(8).Fingerprint;
        File.WriteAllText(path, record.ToJsonString());

        Assert.Throws<IOException>(() => store.Evaluate("172.16.42.1", 22, key));
    }

    [Fact]
    public void Store_RejectsMismatchedFingerprint()
    {
        var store = new OfflineUpdateSshHostTrustStore(_root);
        var key = CreateKey(9);
        var wrong = new OfflineUpdateSshKey(key.PublicKey, CreateKey(10).Fingerprint);

        Assert.Throws<ArgumentException>(() => store.Store("172.16.42.1", 22, wrong));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void RecordForDifferentHostOrPortIsNotUsed()
    {
        var store = new OfflineUpdateSshHostTrustStore(_root);
        var key = CreateKey(11);
        store.Store("172.16.42.1", 22, key);

        var otherPort = store.Evaluate("172.16.42.1", 2222, key);
        Assert.Equal(OfflineUpdateSshHostTrustStatus.Unknown, otherPort.Status);

        var otherHost = store.Evaluate("172.16.42.2", 22, key);
        Assert.Equal(OfflineUpdateSshHostTrustStatus.Unknown, otherHost.Status);

        Assert.Single(Directory.GetFiles(_root).Where(p => p.EndsWith(".json", StringComparison.Ordinal)));
    }
}
