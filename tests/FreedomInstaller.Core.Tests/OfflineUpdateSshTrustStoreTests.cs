using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateSshTrustStoreTests : IDisposable
{
    private readonly string _testRoot;

    public OfflineUpdateSshTrustStoreTests()
    {
        var rootEnv = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(rootEnv))
        {
            throw new InvalidOperationException("TEST_ARTIFACT_ROOT must be explicitly set.");
        }

        _testRoot = Path.Combine(rootEnv, "TrustStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static string AuthJson(
        OfflineUpdateSshHostCandidate candidate,
        OfflineUpdateSshIdentity identity,
        string userName = "alice",
        int schema = 1,
        string? hostFingerprint = null,
        string? installerFingerprint = null)
    {
        return $"{{\"schema\":{schema},\"userName\":\"{userName}\",\"hostKeyFingerprint\":\"{hostFingerprint ?? candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{installerFingerprint ?? identity.PublicKey.Fingerprint}\"}}";
    }

    private string TargetPath(OfflineUpdateSshHostCandidate candidate, string extension)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate.HostKey.PublicKey));
        return Path.Combine(_testRoot, "usb-" + Convert.ToHexString(hash).ToLowerInvariant() + extension);
    }

    private static (string PublicKey, string Fingerprint) CreateWireKey(byte seedByte)
    {
        var wire = new byte[51];
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0, 4), 11);
        Encoding.ASCII.GetBytes("ssh-ed25519").CopyTo(wire.AsSpan(4, 11));
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(15, 4), 32);
        for (int i = 19; i < 51; i++)
        {
            wire[i] = seedByte;
        }

        var b64 = Convert.ToBase64String(wire);
        var pkString = $"ssh-ed25519 {b64}";
        var hash = SHA256.HashData(wire);
        var fpString = "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
        return (pkString, fpString);
    }

    private (OfflineUpdateSshIdentity Identity, OfflineUpdateSshHostCandidate HostCandidate) CreateFixtures(byte hostSeed, byte clientSeed, string? localIp = "172.16.42.2")
    {
        var identityFile = Path.Combine(_testRoot, "id_ed25519");
        File.WriteAllText(identityFile, "---SYNTHETIC PRIVATE PLACEHOLDER---");

        var (clientPk, clientFp) = CreateWireKey(clientSeed);
        var identity = new OfflineUpdateSshIdentity(identityFile, new OfflineUpdateSshKey(clientPk, clientFp));

        var (hostPk, hostFp) = CreateWireKey(hostSeed);
        var endpoint = new OfflineUpdateUsbEndpoint("usb0", "dev1", "172.16.42.1", localIp ?? "172.16.42.2");
        var candidate = new OfflineUpdateSshHostCandidate(endpoint, new OfflineUpdateSshKey(hostPk, hostFp));

        return (identity, candidate);
    }

    [Fact]
    public void TryLoadUsbHost_WhenUnknown_ReturnsNullAndWritesNoFiles()
    {
        var (identity, candidate) = CreateFixtures(1, 2);

        var loaded = OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate, identity);

        Assert.Null(loaded);
        var remainingFiles = Directory.GetFiles(_testRoot);
        Assert.Single(remainingFiles);
        Assert.Equal(Path.GetFullPath(identity.IdentityFile), Path.GetFullPath(remainingFiles[0]));
    }

    [Fact]
    public void TrustUsbHost_ValidPayload_SavesAndReloadsExactOptions()
    {
        var (identity, candidate) = CreateFixtures(10, 20);
        var authJson = $"{{\"schema\":1,\"userName\":\"freedom\",\"hostKeyFingerprint\":\"{candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\"}}";

        var options = OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, authJson);

        Assert.NotNull(options);
        Assert.Equal("172.16.42.1", options.Host);
        Assert.Equal(22, options.Port);
        Assert.Equal("freedom", options.UserName);
        Assert.Equal(identity.IdentityFile, options.IdentityFile);
        Assert.Equal("172.16.42.2", options.BindAddress);
        Assert.True(File.Exists(options.KnownHostsFile));

        var knownHostsText = File.ReadAllText(options.KnownHostsFile);
        Assert.Equal($"172.16.42.1 {candidate.HostKey.PublicKey}\n", knownHostsText);

        var reloaded = OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate, identity);
        Assert.NotNull(reloaded);
        Assert.Equal(options.Host, reloaded.Host);
        Assert.Equal(options.UserName, reloaded.UserName);
        Assert.Equal(options.KnownHostsFile, reloaded.KnownHostsFile);
    }

    [Fact]
    public void TryLoadUsbHost_SameIpDifferentHost_ReturnsNullAndIndependentProfiles()
    {
        var (identity, candidate1) = CreateFixtures(31, 32);
        var authJson1 = $"{{\"schema\":1,\"userName\":\"user1\",\"hostKeyFingerprint\":\"{candidate1.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\"}}";
        OfflineUpdateSshTrustStore.TrustUsbHost(candidate1, identity, authJson1);

        var (hostPk2, hostFp2) = CreateWireKey(33);
        var candidate2 = new OfflineUpdateSshHostCandidate(candidate1.Endpoint, new OfflineUpdateSshKey(hostPk2, hostFp2));

        var loaded2 = OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate2, identity);
        Assert.Null(loaded2);

        var loaded1 = OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate1, identity);
        Assert.NotNull(loaded1);
        Assert.Equal("user1", loaded1.UserName);
    }

    [Fact]
    public void TryLoadUsbHost_ChangedClientFingerprint_ReturnsNullWithoutWriting()
    {
        var (identity1, candidate) = CreateFixtures(41, 42);
        var authJson = $"{{\"schema\":1,\"userName\":\"user1\",\"hostKeyFingerprint\":\"{candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity1.PublicKey.Fingerprint}\"}}";
        OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity1, authJson);

        var (newClientPk, newClientFp) = CreateWireKey(99);
        var identity2 = new OfflineUpdateSshIdentity(identity1.IdentityFile, new OfflineUpdateSshKey(newClientPk, newClientFp));

        var loaded = OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate, identity2);
        Assert.Null(loaded);
    }

    [Theory]
    [InlineData("root", "valid user name rejection")]
    [InlineData("-invalid-", "invalid user regex")]
    [InlineData("Freedom", "capital letters not permitted")]
    public void TrustUsbHost_InvalidUser_ThrowsIOExceptionAndCreatesNoProfile(string invalidUser, string _)
    {
        var (identity, candidate) = CreateFixtures(51, 52);
        var authJson = $"{{\"schema\":1,\"userName\":\"{invalidUser}\",\"hostKeyFingerprint\":\"{candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\"}}";

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, authJson));
        var jsonFiles = Directory.GetFiles(_testRoot, "*.json");
        Assert.Empty(jsonFiles);
    }

    [Fact]
    public void TrustUsbHost_InvalidSchemaOrDuplicateOrExtraJson_ThrowsIOException()
    {
        var (identity, candidate) = CreateFixtures(61, 62);

        var duplicateJson = $"{{\"schema\":1,\"userName\":\"alice\",\"userName\":\"bob\",\"hostKeyFingerprint\":\"{candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\"}}";
        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, duplicateJson));

        var extraJson = $"{{\"schema\":1,\"userName\":\"alice\",\"hostKeyFingerprint\":\"{candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\",\"extra\":true}}";
        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, extraJson));

        var wrongFpJson = $"{{\"schema\":1,\"userName\":\"alice\",\"hostKeyFingerprint\":\"SHA256:WRONG\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\"}}";
        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, wrongFpJson));

        var trailingJson = $"{{\"schema\":1,\"userName\":\"alice\",\"hostKeyFingerprint\":\"{candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\"}}{{}}";
        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, trailingJson));
    }

    [Fact]
    public void TrustUsbHost_InvalidSchemaAndWrongInstallerFingerprint_ThrowIOException()
    {
        var (identity, candidate) = CreateFixtures(63, 64);

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(
            candidate, identity, AuthJson(candidate, identity, schema: 2)));
        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(
            candidate, identity, AuthJson(candidate, identity, installerFingerprint: "SHA256:WRONG")));
        Assert.Empty(Directory.GetFiles(_testRoot, "*.json"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schema\":1,")]
    [InlineData("{\"schema\":1}")]
    public void TrustUsbHost_MalformedTruncatedOrTrailingJson_ThrowsIOException(string json)
    {
        var (identity, candidate) = CreateFixtures(65, 66);

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, json));
        Assert.Empty(Directory.GetFiles(_testRoot, "*.json"));
    }

    [Fact]
    public void TrustUsbHost_EscapedNewlineUserName_RejectsWithoutWrites()
    {
        var (identity, candidate) = CreateFixtures(67, 68);

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(
            candidate, identity, AuthJson(candidate, identity, userName: @"alice\n")));
        Assert.Single(Directory.GetFiles(_testRoot));
    }

    [Fact]
    public void TrustUsbHost_NonCanonicalLocalIpv4_ThrowsIOException()
    {
        var (identity, candidate) = CreateFixtures(69, 70, "172.16.42.002");

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(
            candidate, identity, AuthJson(candidate, identity)));
    }

    [Fact]
    public void TrustUsbHost_RelativeIdentityPath_ThrowsIOException()
    {
        var (identity, candidate) = CreateFixtures(73, 74);
        var relativeIdentity = new OfflineUpdateSshIdentity(Path.GetFileName(identity.IdentityFile), identity.PublicKey);

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(
            candidate, relativeIdentity, AuthJson(candidate, identity)));
    }

    [Fact]
    public void TrustUsbHost_SecondValidHost_CreatesIndependentProfileAndPreservesFirst()
    {
        var (identity, candidate1) = CreateFixtures(75, 76);
        OfflineUpdateSshTrustStore.TrustUsbHost(candidate1, identity, AuthJson(candidate1, identity, "user1"));
        var firstProfilePath = TargetPath(candidate1, ".json");
        var firstKnownHostsPath = TargetPath(candidate1, ".known_hosts");
        var firstProfile = File.ReadAllBytes(firstProfilePath);
        var firstKnownHosts = File.ReadAllBytes(firstKnownHostsPath);

        var (hostPk2, hostFp2) = CreateWireKey(77);
        var candidate2 = new OfflineUpdateSshHostCandidate(candidate1.Endpoint, new OfflineUpdateSshKey(hostPk2, hostFp2));
        OfflineUpdateSshTrustStore.TrustUsbHost(candidate2, identity, AuthJson(candidate2, identity, "user2"));

        Assert.Equal("user1", OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate1, identity)!.UserName);
        Assert.Equal("user2", OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate2, identity)!.UserName);
        Assert.Equal(firstProfile, File.ReadAllBytes(firstProfilePath));
        Assert.Equal(firstKnownHosts, File.ReadAllBytes(firstKnownHostsPath));
    }

    [Fact]
    public void TryLoadUsbHost_TamperedProfile_ThrowsIOException()
    {
        var (identity, candidate) = CreateFixtures(71, 72);
        var authJson = $"{{\"schema\":1,\"userName\":\"alice\",\"hostKeyFingerprint\":\"{candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\"}}";
        OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, authJson);

        var profileFile = Directory.GetFiles(_testRoot, "*.json").Single();
        File.WriteAllText(profileFile, "{\"tampered\":true}");

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate, identity));
    }

    [Fact]
    public void TryLoadUsbHost_ExistingKnownHostsRebuiltFromProfile()
    {
        var (identity, candidate) = CreateFixtures(81, 82);
        var authJson = $"{{\"schema\":1,\"userName\":\"alice\",\"hostKeyFingerprint\":\"{candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\"}}";
        var options = OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, authJson);

        File.WriteAllText(options.KnownHostsFile, "corrupted-known-hosts-line\n");

        var reloaded = OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate, identity);
        Assert.NotNull(reloaded);
        var content = File.ReadAllText(reloaded.KnownHostsFile);
        Assert.Equal($"172.16.42.1 {candidate.HostKey.PublicKey}\n", content);
    }

    [LinuxFact]
    public void TrustUsbHost_DanglingProfileSymlink_RefusedWithoutTargetCreation()
    {
        var (identity, candidate) = CreateFixtures(93, 94);
        var profilePath = TargetPath(candidate, ".json");
        var missingTarget = Path.Combine(_testRoot, "missing-profile-target");
        File.CreateSymbolicLink(profilePath, missingTarget);

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(
            candidate, identity, AuthJson(candidate, identity)));
        Assert.False(File.Exists(missingTarget));
        Assert.NotNull(new FileInfo(profilePath).LinkTarget);
    }

    [LinuxFact]
    public void TrustUsbHost_DanglingKnownHostsSymlink_RefusedBeforeAnyWrites()
    {
        var (identity, candidate) = CreateFixtures(95, 96);
        var knownHostsPath = TargetPath(candidate, ".known_hosts");
        var missingTarget = Path.Combine(_testRoot, "missing-known-hosts-target");
        File.CreateSymbolicLink(knownHostsPath, missingTarget);

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(
            candidate, identity, AuthJson(candidate, identity)));
        Assert.False(File.Exists(missingTarget));
        Assert.False(File.Exists(TargetPath(candidate, ".json")));
        Assert.NotNull(new FileInfo(knownHostsPath).LinkTarget);
    }

    [LinuxFact]
    public void TryLoadUsbHost_DirectoryProfileTarget_RefusedWithoutWrites()
    {
        var (identity, candidate) = CreateFixtures(97, 98);
        var profilePath = TargetPath(candidate, ".json");
        Directory.CreateDirectory(profilePath);

        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TryLoadUsbHost(candidate, identity));
        Assert.True(Directory.Exists(profilePath));
        Assert.False(File.Exists(TargetPath(candidate, ".known_hosts")));
    }

    [LinuxFact]
    public void TrustUsbHost_TargetSymlink_RefusesWithTargetUnchanged()
    {
        var (identity, candidate) = CreateFixtures(91, 92);
        var fakeTarget = Path.Combine(_testRoot, "symlink-dest.txt");
        File.WriteAllText(fakeTarget, "original-content");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate.HostKey.PublicKey));
        var baseName = "usb-" + Convert.ToHexString(hash).ToLowerInvariant();
        var symlinkPath = Path.Combine(_testRoot, baseName + ".json");

        File.CreateSymbolicLink(symlinkPath, fakeTarget);

        var authJson = $"{{\"schema\":1,\"userName\":\"alice\",\"hostKeyFingerprint\":\"{candidate.HostKey.Fingerprint}\",\"installerKeyFingerprint\":\"{identity.PublicKey.Fingerprint}\"}}";
        Assert.Throws<IOException>(() => OfflineUpdateSshTrustStore.TrustUsbHost(candidate, identity, authJson));

        Assert.Equal("original-content", File.ReadAllText(fakeTarget));
    }
}
