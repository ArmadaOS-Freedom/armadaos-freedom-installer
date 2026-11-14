using System.Text;
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
    private const string OtherUuid = "123e4567-e89b-12d3-a456-426614174001";
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

    private static OfflineUpdateSession Session(
        string sid = SidA, string model = Model, string uuid = Uuid, string sha = Sha) =>
        new(sid, model, uuid, sha);

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
    public void UndecodableJsonString_IsReportedAsDamaged_AndKeepsTheValidNeighbor()
    {
        var intact = Session();
        string intactPath = PackageImporter.SaveOfflineUpdateSession(_root, intact);
        string damagedPath = PackageImporter.SaveOfflineUpdateSession(_root, Session(SidB));

        // A JSON string carrying a lone surrogate parses as JSON but cannot always be decoded by
        // GetString(). That failure must become a per-record InvalidDataException instead of
        // escaping as InvalidOperationException and aborting the whole scan.
        File.WriteAllBytes(damagedPath, Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"sessionId\":\"" + SidB + "\"," +
            "\"deviceModel\":\"\\ud800\"," +
            "\"recoveryPartUuid\":\"" + Uuid + "\"," +
            "\"packageDescriptorSha256\":\"" + Sha + "\"}"));
        byte[] damagedBytes = File.ReadAllBytes(damagedPath);

        var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, Sha);

        Assert.Equal(new[] { intact }, scan.Sessions);
        var damaged = Assert.Single(scan.Damaged);
        Assert.Equal(Path.GetFileName(damagedPath), damaged.RecordName);
        Assert.Equal(damagedBytes, File.ReadAllBytes(damagedPath));
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
    public void RefreshSelection_KeepsTheExactRecordThatWasSelectedAmongSeveral()
    {
        var selected = Session();
        var refreshedCopy = new OfflineUpdateSession(SidA, Model, Uuid, Sha);

        var result = OfflineUpdateSessionCatalog.SelectAfterRefresh(
            selected, new[] { Session(SidB), refreshedCopy });

        // The refreshed instance is installed, and a re-read record that
        // matches every field is still the operator's selection.
        Assert.Same(refreshedCopy, result);
    }

    [Fact]
    public void RefreshSelection_LeavesUnselectedWhenTheSelectedRecordVanished_EvenWithOneReplacement()
    {
        // A sole different record is never promoted to the previous selection,
        // and losing the record entirely also leaves nothing selected.
        Assert.Null(OfflineUpdateSessionCatalog.SelectAfterRefresh(
            Session(), new[] { Session(SidB) }));
        Assert.Null(OfflineUpdateSessionCatalog.SelectAfterRefresh(
            Session(), Array.Empty<OfflineUpdateSession>()));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("uuid")]
    [InlineData("sha")]
    public void RefreshSelection_LeavesUnselectedWhenAnyBindingFieldChanged(string field)
    {
        var previous = Session();
        var changed = field switch
        {
            "model" => Session(model: OtherModel),
            "uuid" => Session(uuid: OtherUuid),
            _ => Session(sha: OtherSha),
        };

        // Every binding field is part of the record identity: a changed value
        // is a different session, not a reason to retarget the selection.
        Assert.Null(OfflineUpdateSessionCatalog.SelectAfterRefresh(previous, new[] { changed }));
        Assert.Null(OfflineUpdateSessionCatalog.SelectAfterRefresh(
            previous, new[] { changed, Session(SidB) }));
    }

    [Fact]
    public void RefreshSelection_WithoutAPreviousSelectionKeepsTheOrdinaryRule()
    {
        var only = Session();

        Assert.Same(only, OfflineUpdateSessionCatalog.SelectAfterRefresh(null, new[] { only }));
        Assert.Null(OfflineUpdateSessionCatalog.SelectAfterRefresh(
            null, new[] { only, Session(SidB) }));
        Assert.Null(OfflineUpdateSessionCatalog.SelectAfterRefresh(
            null, Array.Empty<OfflineUpdateSession>()));
    }

    [Fact]
    public void ReplacementSession_IsListedBesideTheFailedRecord_WhichStaysByteIdentical()
    {
        // A failed preparation the operator explicitly replaces with a new
        // session: the new record is listed next to the old one, and neither the
        // failed record nor its saved receipt is rewritten or removed.
        var failed = Session();
        string failedPath = PackageImporter.SaveOfflineUpdateSession(_root, failed);
        string receiptDir = Path.Combine(_root, SidA);
        Directory.CreateDirectory(receiptDir);
        string receiptPath = Path.Combine(receiptDir, "last-receipt.json");
        const string FailedReceipt =
            "{\"sessionId\":\"" + SidA + "\",\"state\":\"failed\",\"jobOperation\":\"prepare\"," +
            "\"result\":{\"state\":\"failed\",\"jobOperation\":\"prepare\"}}";
        File.WriteAllText(receiptPath, FailedReceipt);
        byte[] failedBytes = File.ReadAllBytes(failedPath);

        var replacement = Session(SidB);
        string replacementPath = PackageImporter.SaveOfflineUpdateSession(_root, replacement);

        var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, Sha);

        Assert.Equal(new[] { failed, replacement }, scan.Sessions);
        Assert.Empty(scan.Damaged);
        Assert.Null(scan.UnavailableReason);
        Assert.True(File.Exists(replacementPath));
        Assert.Equal(failedBytes, File.ReadAllBytes(failedPath));
        Assert.Equal(FailedReceipt, File.ReadAllText(receiptPath));
        Assert.Equal(failed, PackageImporter.LoadOfflineUpdateSession(_root, SidA));
    }

    [Fact]
    public void MissingRecordDirectory_IsAGenuinelyEmptyStore_NotAnUnavailableOne()
    {
        var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, Sha);

        Assert.Empty(scan.Sessions);
        Assert.Empty(scan.Damaged);
        // The records root exists and the store directory was simply never
        // created: this is the first upgrade of the tablet, not a broken store.
        Assert.Null(scan.UnavailableReason);
        Assert.False(Directory.Exists(Store()));
    }

    [Fact]
    public void EmptyRecordDirectory_IsAGenuinelyEmptyStore_NotAnUnavailableOne()
    {
        Directory.CreateDirectory(Store());

        var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, Sha);

        Assert.Empty(scan.Sessions);
        Assert.Empty(scan.Damaged);
        Assert.Null(scan.UnavailableReason);
    }

    [Fact]
    public void StorePathThatIsNotADirectory_IsReportedUnavailable_NotAsAnEmptyDevice()
    {
        // Directory.Exists answers false for this store exactly as it does for
        // a missing one, which is the masking this scan must not repeat: a
        // non-directory store is a broken location that may hide records, not a
        // tablet without sessions.
        File.WriteAllText(Store(), "not a directory");

        var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, Sha);

        Assert.Empty(scan.Sessions);
        Assert.Empty(scan.Damaged);
        Assert.False(string.IsNullOrWhiteSpace(scan.UnavailableReason));
        // Reporting it never rewrites the location.
        Assert.Equal("not a directory", File.ReadAllText(Store()));
    }

    [Fact]
    public void UnreadableRecordDirectory_IsReportedUnavailable_WhenTheFixtureCanDenyEnumeration()
    {
        // A POSIX deny fixture is the only portable way to provoke the
        // access-denied path; the test is injected only where it can actually
        // deny (never as root) and the non-directory case above covers the rest.
        if (OperatingSystem.IsWindows())
            return;

        string store = Store();
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, SidA + ".json"), DamagedJson);
        try
        {
            File.SetUnixFileMode(store, UnixFileMode.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        try
        {
            bool denied;
            try
            {
                _ = Directory.GetFiles(store, "*.json", SearchOption.TopDirectoryOnly);
                denied = false;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // The scan classifies either failure as an unreadable store;
                // this probe only asks whether the fixture really denies.
                denied = true;
            }

            if (!denied)
                return; // running as root: the store cannot deny its own owner

            var scan = OfflineUpdateSessionCatalog.Load(_root, Model, Uuid, Sha);

            Assert.Empty(scan.Sessions);
            Assert.Empty(scan.Damaged);
            Assert.False(string.IsNullOrWhiteSpace(scan.UnavailableReason));
        }
        finally
        {
            File.SetUnixFileMode(store,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
