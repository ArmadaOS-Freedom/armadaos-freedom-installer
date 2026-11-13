using FreedomInstaller.App;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Regression for the per-record session scan the offline panel uses while a
/// package is validated. One damaged unrelated record must not hide the intact
/// sessions, must not drop the validated package, and must never be rewritten.
/// </summary>
public sealed class OfflineUpdateSessionCatalogTests : IDisposable
{
    private const string StoreName = "offline-update-sessions";
    private const string Model = "TB322FC";
    private const string OtherModel = "TB323FU";
    private const string SidA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string SidB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    private const string SidC = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";
    private const string Uuid = "123e4567-e89b-12d3-a456-426614174000";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string DamagedJson = "{truncated";

    private readonly string _artifactRoot = Environment.GetEnvironmentVariable("TEST_ARTIFACT_ROOT")
        ?? throw new InvalidOperationException("TEST_ARTIFACT_ROOT is required.");
    private readonly string _root;

    public OfflineUpdateSessionCatalogTests()
    {
        Assert.True(Path.IsPathFullyQualified(_artifactRoot));
        Assert.True(Directory.Exists(_artifactRoot));
        _root = Path.Combine(Path.GetFullPath(_artifactRoot),
            "OfflineUpdateSessionCatalogTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        string parent = Path.GetFullPath(_artifactRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.True(full.StartsWith(parent, StringComparison.Ordinal));
        if (Directory.Exists(full))
        {
            Directory.Delete(full, true);
        }
    }

    private static OfflineUpdateSession Session(string sid = SidA, string model = Model, string sha = Sha) =>
        new(sid, model, Uuid, sha);

    private string Store() => Path.Combine(_root, StoreName);

    [Fact]
    public void DamagedUnrelatedRecord_IsReportedAndSkipped_IntactSessionSurvivesUntouched()
    {
        var intact = Session();
        string intactPath = PackageImporter.SaveOfflineUpdateSession(_root, intact);
        string damagedPath = PackageImporter.SaveOfflineUpdateSession(_root, Session(SidB));
        File.WriteAllText(damagedPath, DamagedJson);

        var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, Sha);

        Assert.Equal(new[] { intact }, scan.Sessions);
        var damaged = Assert.Single(scan.Damaged);
        Assert.Equal(Path.GetFileName(damagedPath), damaged.RecordName);
        Assert.False(string.IsNullOrWhiteSpace(damaged.Reason));

        // Reporting a damaged record never rewrites or removes it, and the
        // intact record stays loadable by the strict Core reader.
        Assert.Equal(DamagedJson, File.ReadAllText(damagedPath));
        Assert.Equal(intact, PackageImporter.LoadOfflineUpdateSession(_root, SidA));
        Assert.True(File.Exists(intactPath));
    }

    [Fact]
    public void NewSessionBesideADamagedRecord_LeavesTheDamagedBytesInPlace()
    {
        string damagedPath = PackageImporter.SaveOfflineUpdateSession(_root, Session(SidB));
        File.WriteAllText(damagedPath, DamagedJson);

        var fresh = Session(SidC);
        string freshPath = PackageImporter.SaveOfflineUpdateSession(_root, fresh);

        Assert.Equal(DamagedJson, File.ReadAllText(damagedPath));
        Assert.Equal(fresh, PackageImporter.LoadOfflineUpdateSession(_root, SidC));
        Assert.True(File.Exists(freshPath));
    }

    [Fact]
    public void RecordsOfAnotherDeviceOrPackage_AreNeitherMatchedNorReportedAsDamaged()
    {
        PackageImporter.SaveOfflineUpdateSession(_root, Session(SidA, OtherModel));
        PackageImporter.SaveOfflineUpdateSession(_root, Session(SidB, sha: OtherSha));

        var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, Sha);

        Assert.Empty(scan.Sessions);
        Assert.Empty(scan.Damaged);
    }

    [Fact]
    public void WithoutADescriptorFilter_AllSessionsOfTheDeviceAreListed()
    {
        var a = Session();
        var b = Session(SidB, sha: OtherSha);
        PackageImporter.SaveOfflineUpdateSession(_root, a);
        PackageImporter.SaveOfflineUpdateSession(_root, b);
        PackageImporter.SaveOfflineUpdateSession(_root, Session(SidC, OtherModel));

        var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, packageDescriptorSha256: null);

        Assert.Equal(new[] { a, b }, scan.Sessions);
        Assert.Empty(scan.Damaged);
    }

    [Fact]
    public void MissingRecordDirectory_IsReadOnlyAndReportsNothing()
    {
        var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, Sha);

        Assert.Empty(scan.Sessions);
        Assert.Empty(scan.Damaged);
        Assert.False(Directory.Exists(Store()));
    }
}
