using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.App;
using FreedomInstaller.Core;
using Xunit;

namespace FreedomInstaller.Core.Tests
{
    public sealed partial class InternalInstallExistingBackupTests
    {
        [Theory]
        [InlineData("foreign-cache", 1, false)]
        [InlineData("missing-cache", 1, false)]
        [InlineData("valid-cache", 0, false)]
        [InlineData("explicit-pc", 0, false)]
        [InlineData("wrong-explicit-pc", 0, true)]
        public async Task GetBoardFirstRecoverySourceAsync_uses_board_when_discovered_cache_is_unusable(string scenario, long expectedReads, bool expectFailure)
        {
            const string Name = nameof(GetBoardFirstRecoverySourceAsync_uses_board_when_discovered_cache_is_unusable);
            string sourceDirectory = _fixture.NewCaseDirectory(Name + "-sources-" + scenario);
            IReadOnlyDictionary<string, VerifiedEdlFile> sources = CreateDirectSources(sourceDirectory);
            string backing = _fixture.NewCaseDirectory(Name + "-backing-" + scenario);
            IB001SyntheticDevice device = IB001SyntheticDevice.CreateDirect(backing, sources);

            string backupParent = _fixture.NewCaseDirectory(Name + "-backup-" + scenario);
            var runner = new IB001FakeEdlRunner(device);
            EdlClient createClient = NewClient(runner, NewBinding());

            using InternalBackupSet original = await InternalInstallBackup.CreateAsync(createClient, backupParent);
            string originalDirectory = original.DirectoryPath;
            string originalManifest = original.ManifestSha256;
            Assert.Equal(51, original.Files.Count);

            string artifactsParent = _fixture.NewCaseDirectory(Name + "-artifacts-" + scenario);
            var factory = original.Tables[0];
            var alignment = (1L << 20) / factory.SectorSize;
            var retainedEnd = (factory.LastUsableSector + 1) / alignment * alignment;
            var retainedStart = retainedEnd - BoardArchiveBytes / factory.SectorSize;

            using InternalArtifactSet artifacts = await InternalInstallArtifacts.PrepareAsync(
                original,
                48,
                new PackageStorage(1L << 30, 8L << 30, 4L << 30, 1L << 30),
                artifactsParent);
            Assert.Equal(BoardArchiveBytes, artifacts.BoardSummary.Length);

            var backupUuid = artifacts.BackupPartition.PartUuid;
            var specs = factory.Partitions.Select(p => new GptPartitionSpec(p.Number, p.Name, p.TypeGuid,
                p.PartUuid, p.StartSector, p.Name == "userdata" ? retainedStart - p.StartSector : p.SectorCount,
                p.Attributes)).Append(new GptPartitionSpec(factory.Partitions.Max(p => p.Number) + 1,
                    "ARMADA_BACKUP", "0FC63DAF-8483-4772-8E79-3D69D8477DE4", backupUuid,
                    retainedStart, BoardArchiveBytes / factory.SectorSize, 1UL << 60)).ToArray();
            var retainedImage = new GptImage(factory.SectorSize, factory.DiskBytes, factory.DiskGuid,
                factory.FirstUsableSector, factory.LastUsableSector, specs);
            var captureBytes = 64 * factory.SectorSize;
            var primary = new byte[captureBytes];
            var secondary = new byte[captureBytes];
            var head = retainedImage.BuildPrimaryFragment();
            var tail = retainedImage.BuildBackupFragment();
            Array.Copy(head, 0, primary, 0, head.Length);
            Array.Copy(tail, 0, secondary, secondary.Length - tail.Length, tail.Length);
            var primaryPath = Path.Combine(artifactsParent, "retained-primary.bin");
            var secondaryPath = Path.Combine(artifactsParent, "retained-secondary.bin");
            File.WriteAllBytes(primaryPath, primary);
            File.WriteAllBytes(secondaryPath, secondary);
            device.AddWriteOverlay(0, 0, 64, primaryPath);
            device.AddWriteOverlay(0, device.TotalSectors(0) - 64, 64, secondaryPath);
            device.AddWriteOverlay(0, retainedStart, BoardArchiveBytes / factory.SectorSize, artifacts.BoardSummary.FilePath);

            using InternalBackupSet current = await InternalInstallBackup.CreateAsync(createClient, backupParent);
            Assert.NotEqual(originalManifest, current.ManifestSha256);

            string foreignCache;
            {
                IB001SyntheticDevice foreignDevice = IB001SyntheticDevice.Create(archiveLayout: true);
                string foreignParent = _fixture.NewCaseDirectory(Name + "-foreign-parent-" + scenario);
                var foreignRunner = new IB001FakeEdlRunner(foreignDevice);
                EdlClient foreignClient = NewClient(foreignRunner, NewBinding(serial: "OTHER_DEVICE"));
                using InternalBackupSet foreign = await InternalInstallBackup.CreateAsync(foreignClient, foreignParent);
                Assert.NotEqual(string.Empty, foreign.ManifestSha256);
                foreignCache = foreign.DirectoryPath;
                Assert.Equal(0, CountWrites(foreignRunner));
            }

            string missingCache = Path.Combine(_fixture.NewCaseDirectory(Name + "-missing-parent-" + scenario), "child");

            string cachedDirectory = scenario switch
            {
                "foreign-cache" => foreignCache,
                "missing-cache" => missingCache,
                "valid-cache" => originalDirectory,
                "explicit-pc" => foreignCache,
                "wrong-explicit-pc" => originalDirectory,
                _ => throw new InvalidOperationException("unknown scenario " + scenario),
            };
            string? explicitPcDirectory = scenario switch
            {
                "explicit-pc" => originalDirectory,
                "wrong-explicit-pc" => foreignCache,
                _ => null,
            };

            long readsBefore = CountBoardReads();
            string tempRoot = _fixture.NewCaseDirectory(Name + "-temp-" + scenario);
            var shell = new MainWindow(createClient, cachedDirectory, explicitPcDirectory, tempRoot)
            {
                IsModernUserdataUninstallMode = true,
            };
            try
            {
                if (!expectFailure)
                {
                    InternalBackupSet first = await shell.ResolveAsync(current, backupParent);
                    Assert.Equal(0, shell.PickerCallCount);
                    Assert.Equal(expectedReads, CountBoardReads() - readsBefore);
                    bool isBoard = scenario is "foreign-cache" or "missing-cache";
                    if (isBoard)
                    {
                        Assert.NotEqual(originalDirectory, first.DirectoryPath, StringComparer.OrdinalIgnoreCase);
                        Assert.Equal(originalManifest, first.ManifestSha256);
                    }
                    else
                    {
                        Assert.Equal(originalDirectory, first.DirectoryPath, ignoreCase: true);
                        Assert.Equal(originalManifest, first.ManifestSha256);
                    }

                    InternalBackupSet second = await shell.ResolveAsync(current, backupParent);
                    Assert.Equal(0, shell.PickerCallCount);
                    Assert.Equal(expectedReads, CountBoardReads() - readsBefore);
                    Assert.Same(first, second);
                    Assert.Equal(first.DirectoryPath, second.DirectoryPath, ignoreCase: true);
                    Assert.Equal(first.ManifestSha256, second.ManifestSha256);
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => shell.ResolveAsync(current, backupParent));
                    Assert.Equal(0, shell.PickerCallCount);
                    Assert.Equal(expectedReads, CountBoardReads() - readsBefore);
                }
            }
            finally
            {
                shell.DisposeRecoveryLease();
            }

            Assert.Equal(0, CountWrites(runner));


            int CountBoardReads() => runner.Reads.Count(r =>
                r.Lun == 0 && r.StartSector == retainedStart &&
                r.SectorCount == BoardArchiveBytes / factory.SectorSize);
        }

        private static int CountWrites(IB001FakeEdlRunner runner) => runner.Writes.Count;
    }
}

namespace FreedomInstaller.App
{
    public partial class MainWindow
    {
        private readonly List<string> _testLog = new();

        private string? _internalInstallBackupParent;
        private bool _internalInstallOperationActive;
        private EdlClient? _internalInstallEdlClient;
        private bool _internalInstallEdlEntryAttempted;
        private EdlToolchain? _internalInstallEdlTools;
        private EdlEntryResult? _internalInstallEdlEntry;
        private string? _bootMaintenanceOriginalDirectory;
        private BootMaintenanceHistoricalIdentity? _bootMaintenanceHistoricalIdentity;
        private DeviceTransport? _transport;
        private ICommandRunner? _runner;

        public bool IsModernUserdataUninstallMode { get; set; }

        public bool IsRepairInstallationMode { get; set; }

        public int PickerCallCount { get; private set; }

        public string? PickerResult { get; set; }

        public IReadOnlyList<string> TestLog => _testLog;

        public bool InternalInstallOperationActive => _internalInstallOperationActive;

        public Task RunCheckBoardRecoveryEdlAsync(string parent)
        {
            _internalInstallBackupParent = parent;
            return CheckUninstallBoardRecoveryEdlAsync();
        }

        public MainWindow(EdlClient client, string? cachedDirectory, string? selectedPcDirectory, string tempRoot)
        {
            _internalInstallEdlClient = client;
            _boardRecoveryDiscoveredDirectory = cachedDirectory;
            _bootMaintenanceOriginalDirectory = selectedPcDirectory;
            TemporaryWorkRoot = tempRoot;
        }

        internal string TemporaryWorkRoot { get; }

        internal Task<InternalBackupSet> ResolveAsync(InternalBackupSet current, string durableParent) =>
            GetBoardFirstRecoverySourceAsync(current, durableParent, CancellationToken.None);

        public void DisposeRecoveryLease()
        {
            _internalInstallRecoverySource?.Dispose();
            _internalInstallRecoverySource = null;
            _internalInstallEdlClient = null;
        }

        private static string T(string key, params object[] args) => key;

        private static string M(string key, params object[] args) => key;

        private void AppendLocalizedLog(string message) => _testLog.Add(message);

        private void SetInternalInstallCheckStatus(string message) => _testLog.Add(message);

        private void RefreshInternalInstallActions() { }

        private void RefreshBootMaintenanceSharedActions() { }

        private string? PickExistingDirectory(string? initial, string key)
        {
            PickerCallCount++;
            return PickerResult;
        }

        private void ApplyInternalInstallBackupParent(string parent) => _internalInstallBackupParent = parent;

        private Task RunBusyAsync(Func<CancellationToken, Task> work) =>
            work(CancellationToken.None);

        private Task<EdlToolchain> LoadGlobalToolsAsync(EdlDeviceProfile profile, CancellationToken token) =>
            throw new NotSupportedException("LoadGlobalToolsAsync is not available in the headless recovery-selection test shell.");
    }
}
