using System.Text;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class OfflineUpdateSessionStoreTests : IDisposable
{
    private const string StoreName = "offline-update-sessions";
    private const string SidA = "0123456789abcdef0123456789abcdef";
    private const string SidB = "fedcba9876543210fedcba9876543210";
    private const string SidC = "11111111111111111111111111111111";
    private const string Uuid = "123e4567-e89b-12d3-a456-426614174000";
    private const string OtherUuid = "123e4567-e89b-12d3-a456-426614174001";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _artifactRoot = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
        ?? throw new InvalidOperationException("TEST_ARTIFACT_ROOT is required.");
    private readonly string _root;
    private readonly List<(string Path, bool IsDirectory)> _links = [];

    public OfflineUpdateSessionStoreTests()
    {
        Assert.True(Path.IsPathFullyQualified(_artifactRoot));
        Assert.True(Directory.Exists(_artifactRoot));
        _root = Path.Combine(Path.GetFullPath(_artifactRoot),
            "OfflineUpdateSessionStoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        for (int i = _links.Count - 1; i >= 0; i--)
        {
            var link = _links[i];
            if (link.IsDirectory && Directory.Exists(link.Path)) Directory.Delete(link.Path, false);
            else if (!link.IsDirectory && File.Exists(link.Path)) File.Delete(link.Path);
        }
        string full = Path.GetFullPath(_root);
        string parent = Path.GetFullPath(_artifactRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.True(full.StartsWith(parent, StringComparison.Ordinal));
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    private static OfflineUpdateSession Session(string sid = SidA, string model = "TB322FC",
        string uuid = Uuid, string sha = Sha) => new(sid, model, uuid, sha);

    private string Store(string? root = null) => Path.Combine(root ?? _root, StoreName);

    [Fact]
    public void RoundTrip_IdempotentBytes_AndFindFiltersInOrdinalOrder()
    {
        var sidD = new string('d', 32);
        var a = Session();
        var b = Session(SidB);
        var c = Session(SidC, sha: OtherSha);
        var d = Session(sidD, "TB323FU");

        var pathB = PackageImporter.SaveOfflineUpdateSession(_root, b);
        var pathA = PackageImporter.SaveOfflineUpdateSession(_root, a);
        PackageImporter.SaveOfflineUpdateSession(_root, c);
        PackageImporter.SaveOfflineUpdateSession(_root, d);

        Assert.Equal(Path.Combine(Store(), SidA + ".json"), pathA);
        Assert.Equal(Path.Combine(Store(), SidB + ".json"), pathB);

        var firstBytesA = File.ReadAllBytes(pathA);
        var pathA2 = PackageImporter.SaveOfflineUpdateSession(_root, a);
        Assert.Equal(pathA, pathA2);
        Assert.Equal(firstBytesA, File.ReadAllBytes(pathA2));

        Assert.Equal(a, PackageImporter.LoadOfflineUpdateSession(_root, SidA));
        Assert.Equal(b, PackageImporter.LoadOfflineUpdateSession(_root, SidB));
        Assert.Equal(d, PackageImporter.LoadOfflineUpdateSession(_root, sidD));

        Assert.Equal(new[] { a, b }, PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", Uuid, Sha));
        Assert.Equal(new[] { d }, PackageImporter.FindOfflineUpdateSessions(_root, "TB323FU", Uuid, Sha));
        Assert.Empty(PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", OtherUuid, Sha));
        Assert.Equal(new[] { c }, PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", Uuid, OtherSha));
    }

    [Fact]
    public async Task ConcurrentIdenticalSaves_Converge_AndConflictsPreserveOriginal()
    {
        var original = Session();
        string[] paths = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => PackageImporter.SaveOfflineUpdateSession(_root, original))));
        Assert.Single(paths.Distinct(StringComparer.Ordinal));
        Assert.Single(Directory.GetFiles(Store(), "*.json"));
        Assert.Empty(Directory.GetFiles(Store(), "*.tmp"));
        byte[] before = File.ReadAllBytes(paths[0]);
        Assert.Throws<InvalidDataException>(() =>
            PackageImporter.SaveOfflineUpdateSession(_root, Session(sha: OtherSha)));
        Assert.Throws<InvalidDataException>(() =>
            PackageImporter.SaveOfflineUpdateSession(_root, Session(uuid: OtherUuid)));
        Assert.Equal(before, File.ReadAllBytes(paths[0]));
        Assert.Equal(original, PackageImporter.LoadOfflineUpdateSession(_root, SidA));
    }

    [Fact]
    public void PreCancellation_AndInvalidRoots_DoNotCreateStore()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PackageImporter.SaveOfflineUpdateSession(_root, Session(), cts.Token));
        Assert.Throws<OperationCanceledException>(() =>
            PackageImporter.LoadOfflineUpdateSession(_root, SidA, cts.Token));
        Assert.Throws<OperationCanceledException>(() =>
            PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", Uuid, Sha, cts.Token));
        string missing = Path.Combine(_root, "missing");
        string rootFile = Path.Combine(_root, "root-file");
        File.WriteAllText(rootFile, "x");
        foreach (string root in new[] { missing, "relative-records-root", rootFile })
        {
            Assert.Throws<InvalidDataException>(() => PackageImporter.SaveOfflineUpdateSession(root, Session()));
            Assert.Throws<InvalidDataException>(() => PackageImporter.LoadOfflineUpdateSession(root, SidA));
            Assert.Throws<InvalidDataException>(() =>
                PackageImporter.FindOfflineUpdateSessions(root, "TB322FC", Uuid, Sha));
        }
        Assert.False(Directory.Exists(missing));
        Assert.False(Directory.Exists(Store()));
        Assert.Equal("x", File.ReadAllText(rootFile));
    }

    [Fact]
    public void InvalidRecordInputs_AreRejectedBeforeStoreCreation()
    {
        var invalid = new[]
        {
            Session("../0123456789abcdef0123456789abcd"), Session(SidA.ToUpperInvariant()),
            Session(model: "TB999XX"), Session(uuid: "not-a-guid"),
            Session(uuid: Guid.Empty.ToString("D")), Session(sha: Sha.ToUpperInvariant()),
            Session(sha: "xyz")
        };
        foreach (var record in invalid)
            Assert.Throws<InvalidDataException>(() => PackageImporter.SaveOfflineUpdateSession(_root, record));
        Assert.Throws<InvalidDataException>(() => PackageImporter.LoadOfflineUpdateSession(_root, "../escape"));
        Assert.Throws<InvalidDataException>(() => PackageImporter.LoadOfflineUpdateSession(_root, SidA.ToUpperInvariant()));
        Assert.Throws<InvalidDataException>(() => PackageImporter.FindOfflineUpdateSessions(_root, "BAD", Uuid, Sha));
        Assert.Throws<InvalidDataException>(() => PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", "bad", Sha));
        Assert.Throws<InvalidDataException>(() => PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", Uuid, "bad"));
        Assert.False(Directory.Exists(Store()));
    }

    [Fact]
    public void MalformedPersistedVariants_ThrowFromLoadAndFind_WithoutRewrite()
    {
        var record = Session();
        string path = PackageImporter.SaveOfflineUpdateSession(_root, record);
        byte[] valid = File.ReadAllBytes(path);
        string json = Encoding.UTF8.GetString(valid);
        byte[] oversize = new byte[64 * 1024 + 1];
        Array.Fill(oversize, (byte)' ');
        valid.CopyTo(oversize, 0);
        var variants = new[]
        {
            Encoding.UTF8.GetBytes(json.Replace($"\"sessionId\":\"{SidA}\"",
                $"\"sessionId\":\"{SidA}\",\"sessionId\":\"{SidA}\"", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(json[..^1] + ",\"unknown\":1}"),
            Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(json.Replace($"\"sessionId\":\"{SidA}\"", "\"sessionId\":1", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(json.Replace(SidA, SidB, StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(json.Replace(",\"deviceModel\":\"TB322FC\"", "", StringComparison.Ordinal)),
            oversize,
            Encoding.UTF8.GetBytes(json[..^1])
        };
        foreach (byte[] malformed in variants)
        {
            File.WriteAllBytes(path, malformed);
            Assert.Throws<InvalidDataException>(() => PackageImporter.LoadOfflineUpdateSession(_root, SidA));
            Assert.Throws<InvalidDataException>(() =>
                PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", Uuid, Sha));
            Assert.Equal(malformed, File.ReadAllBytes(path));
            File.WriteAllBytes(path, valid);
            Assert.Equal(record, PackageImporter.LoadOfflineUpdateSession(_root, SidA));
        }
    }

    [LinuxFact]
    public void LinuxDirectoryAndRecordSymlinks_AreRejected_AndNeighborsRemainIntact()
    {
        string directoryRoot = Path.Combine(_root, "directory-case");
        string directoryTarget = Path.Combine(_root, "directory-target");
        Directory.CreateDirectory(directoryRoot);
        Directory.CreateDirectory(directoryTarget);
        string directoryLink = Store(directoryRoot);
        Directory.CreateSymbolicLink(directoryLink, directoryTarget);
        _links.Add((directoryLink, true));
        Assert.Throws<InvalidDataException>(() => PackageImporter.SaveOfflineUpdateSession(directoryRoot, Session()));
        Assert.Throws<InvalidDataException>(() => PackageImporter.LoadOfflineUpdateSession(directoryRoot, SidA));
        Assert.Throws<InvalidDataException>(() =>
            PackageImporter.FindOfflineUpdateSessions(directoryRoot, "TB322FC", Uuid, Sha));
        var record = Session();
        string path = PackageImporter.SaveOfflineUpdateSession(_root, record);
        string target = Path.Combine(_root, "record-target.json");
        byte[] original = File.ReadAllBytes(path);
        File.Move(path, target);
        File.CreateSymbolicLink(path, target);
        _links.Add((path, false));
        var neighbor = Session(SidB, "TB323FU");
        PackageImporter.SaveOfflineUpdateSession(_root, neighbor);
        string unrelated = Path.Combine(Store(), "neighbor.txt");
        File.WriteAllText(unrelated, "keep");
        Assert.Throws<InvalidDataException>(() => PackageImporter.LoadOfflineUpdateSession(_root, SidA));
        Assert.Throws<InvalidDataException>(() => PackageImporter.SaveOfflineUpdateSession(_root, record));
        Assert.Throws<InvalidDataException>(() =>
            PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", Uuid, Sha));
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(neighbor, PackageImporter.LoadOfflineUpdateSession(_root, SidB));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Fact]
    public void WorkSessionCompleteFalse_RemovesWorkButRetainsRecordAndRecovery()
    {
        var work = new InstallerWorkSession(Path.Combine(_root, "work"));
        Directory.CreateDirectory(work.RecordsDirectory);
        var record = Session();
        string path = PackageImporter.SaveOfflineUpdateSession(work.RecordsDirectory, record);
        byte[] recovery = [1, 2, 3, 4, 5];
        string recoveryPath = Path.Combine(work.RecordsDirectory, "recovery.bin");
        File.WriteAllBytes(recoveryPath, recovery);
        File.WriteAllText(Path.Combine(work.DirectoryPath, "temporary.bin"), "temporary");
        work.Complete(false);
        Assert.False(Directory.Exists(work.DirectoryPath));
        Assert.Equal(record, PackageImporter.LoadOfflineUpdateSession(work.RecordsDirectory, SidA));
        Assert.Equal(recovery, File.ReadAllBytes(recoveryPath));
        Assert.Equal(new[] { record }, PackageImporter.FindOfflineUpdateSessions(
            work.RecordsDirectory, "TB322FC", Uuid, Sha));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void MissingFindIsReadOnly_UnrelatedFilesSurvive_AndCorruptJsonIsNotEmpty()
    {
        Assert.Empty(PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", Uuid, Sha));
        Assert.False(Directory.Exists(Store()));
        var record = Session();
        PackageImporter.SaveOfflineUpdateSession(_root, record);
        string temp = Path.Combine(Store(), "unrelated.tmp");
        string neighbor = Path.Combine(_root, "neighbor.bin");
        File.WriteAllText(temp, "temp");
        File.WriteAllText(neighbor, "neighbor");
        string corrupt = PackageImporter.SaveOfflineUpdateSession(_root, Session(SidB));
        File.WriteAllText(corrupt, "{truncated");
        Assert.Throws<InvalidDataException>(() =>
            PackageImporter.FindOfflineUpdateSessions(_root, "TB322FC", Uuid, Sha));
        Assert.Equal("temp", File.ReadAllText(temp));
        Assert.Equal("neighbor", File.ReadAllText(neighbor));
        Assert.Equal(record, PackageImporter.LoadOfflineUpdateSession(_root, SidA));
        Assert.Equal("{truncated", File.ReadAllText(corrupt));
    }
}
