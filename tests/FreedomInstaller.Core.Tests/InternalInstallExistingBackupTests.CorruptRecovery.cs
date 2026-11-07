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
        [InlineData("valid-factory", 1, false)]
        [InlineData("null-picker", 1, true)]
        [InlineData("foreign-device", 1, true)]
        public async Task GetBoardFirstRecoverySourceAsync_corrupt_on_device_archive_falls_back_to_picker(
            string scenario, int expectedPickerCalls, bool expectFailure)
        {
            const string Name = nameof(GetBoardFirstRecoverySourceAsync_corrupt_on_device_archive_falls_back_to_picker);
            var setup = await CreateCorruptArchiveSetupAsync(Name + "-" + scenario);
            try
            {
                string? pickerResult = scenario switch
                {
                    "valid-factory" => setup.OriginalDirectory,
                    "null-picker" => null,
                    "foreign-device" => setup.ForeignDirectory,
                    _ => throw new InvalidOperationException("unknown scenario " + scenario),
                };

                var shell = new MainWindow(setup.Client, cachedDirectory: null, selectedPcDirectory: null, setup.TempRoot)
                {
                    IsModernUserdataUninstallMode = true,
                    PickerResult = pickerResult,
                };
                try
                {
                    if (!expectFailure)
                    {
                        InternalBackupSet resolved = await shell.ResolveAsync(setup.Current, setup.DurableParent);
                        Assert.Equal(expectedPickerCalls, shell.PickerCallCount);
                        Assert.Equal(setup.OriginalManifest, resolved.ManifestSha256);
                        Assert.Equal(setup.OriginalDirectory, resolved.DirectoryPath, ignoreCase: true);
                        Assert.Contains(shell.TestLog, m => m == "Dynamic.RecoverySource.BoardInvalid");
                    }
                    else if (scenario == "null-picker")
                    {
                        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => shell.ResolveAsync(setup.Current, setup.DurableParent));
                        Assert.Equal("Dynamic.RecoverySource.NeedOriginal", ex.Message);
                        Assert.Equal(expectedPickerCalls, shell.PickerCallCount);
                        Assert.Contains(shell.TestLog, m => m == "Dynamic.RecoverySource.BoardInvalid");
                    }
                    else
                    {
                        await Assert.ThrowsAsync<InvalidDataException>(() => shell.ResolveAsync(setup.Current, setup.DurableParent));
                        Assert.Equal(expectedPickerCalls, shell.PickerCallCount);
                        Assert.Contains(shell.TestLog, m => m == "Dynamic.RecoverySource.BoardInvalid");
                    }
                }
                finally
                {
                    shell.DisposeRecoveryLease();
                }

                Assert.Equal(0, CountWrites(setup.Runner));
                Assert.Equal(1, CountCorruptArchiveReads(setup));
                Assert.Equal(0, setup.Runner.ResetCount);
            }
            finally
            {
                setup.Dispose();
            }
        }

        [Theory]
        [InlineData("no-cache")]
        [InlineData("invalid-cache")]
        public async Task CheckUninstallBoardRecoveryEdlAsync_corrupt_on_device_archive_offers_fallback(string scenario)
        {
            const string Name = nameof(CheckUninstallBoardRecoveryEdlAsync_corrupt_on_device_archive_offers_fallback);
            var setup = await CreateCorruptArchiveSetupAsync(Name + "-" + scenario);
            try
            {
                string? cachedDirectory = scenario switch
                {
                    "no-cache" => null,
                    "invalid-cache" => CreateInvalidCachedBoardDirectory(setup),
                    _ => throw new InvalidOperationException("unknown scenario " + scenario),
                };

                if (cachedDirectory != null)
                {
                    await Assert.ThrowsAsync<InvalidDataException>(
                        () => BootMaintenanceBackend.ReadHistoricalIdentityAsync(cachedDirectory));
                }

                var shell = new MainWindow(setup.Client, cachedDirectory, selectedPcDirectory: null, setup.TempRoot)
                {
                    IsModernUserdataUninstallMode = true,
                    PickerResult = null,
                };
                try
                {
                    await shell.RunCheckBoardRecoveryEdlAsync(setup.DurableParent);

                    Assert.Equal(1, shell.PickerCallCount);
                    Assert.Contains(shell.TestLog, m => m == "Dynamic.RecoverySource.BoardInvalid");
                    Assert.False(shell.InternalInstallOperationActive);
                }
                finally
                {
                    shell.DisposeRecoveryLease();
                }

                Assert.Equal(0, CountWrites(setup.Runner));
                Assert.Equal(1, CountCorruptArchiveReads(setup));
                Assert.Equal(0, setup.Runner.ResetCount);
            }
            finally
            {
                setup.Dispose();
            }
        }

        private async Task<CorruptArchiveSetup> CreateCorruptArchiveSetupAsync(string caseName)
        {
            string sourceDirectory = _fixture.NewCaseDirectory(caseName + "-sources");
            IReadOnlyDictionary<string, VerifiedEdlFile> sources = CreateDirectSources(sourceDirectory);
            string backing = _fixture.NewCaseDirectory(caseName + "-backing");
            IB001SyntheticDevice device = IB001SyntheticDevice.CreateDirect(backing, sources);

            string backupParent = _fixture.NewCaseDirectory(caseName + "-backup");
            var runner = new IB001FakeEdlRunner(device);
            var geometryRunner = new GeometryAwareCommandRunner(runner, device);
            EdlClient client = new EdlClient(_fixture.Toolchain, NewBinding(), geometryRunner, new E003ScriptedEndpointDiscovery { Endpoints = new[] { E003EdlTestData.Endpoint() } });

            using InternalBackupSet original = await InternalInstallBackup.CreateAsync(client, backupParent);
            string originalDirectory = original.DirectoryPath;
            string originalManifest = original.ManifestSha256;

            string artifactsParent = _fixture.NewCaseDirectory(caseName + "-artifacts");
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

            string corruptPath = Path.Combine(artifactsParent, "corrupt-recovery.img");
            using (var corrupt = new FileStream(corruptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                corrupt.SetLength(BoardArchiveBytes);
                using var valid = new FileStream(artifacts.BoardSummary.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var header = new byte[65536];
                valid.ReadExactly(header);
                corrupt.Position = 0;
                corrupt.Write(header);
                corrupt.Flush(true);
            }
            device.AddWriteOverlay(0, retainedStart, BoardArchiveBytes / factory.SectorSize, corruptPath);

            InternalBackupSet current = await InternalInstallBackup.CreateAsync(client, backupParent);
            try
            {
                Assert.NotEqual(originalManifest, current.ManifestSha256);

                string tempRoot = _fixture.NewCaseDirectory(caseName + "-temp");

                string foreignDirectory;
                {
                    IB001SyntheticDevice foreignDevice = IB001SyntheticDevice.Create(archiveLayout: true);
                    string foreignParent = _fixture.NewCaseDirectory(caseName + "-foreign-parent");
                    var foreignRunner = new IB001FakeEdlRunner(foreignDevice);
                    EdlClient foreignClient = NewClient(foreignRunner, NewBinding(serial: "OTHER_DEVICE"));
                    using InternalBackupSet foreign = await InternalInstallBackup.CreateAsync(foreignClient, foreignParent);
                    Assert.NotEqual(string.Empty, foreign.ManifestSha256);
                    foreignDirectory = foreign.DirectoryPath;
                    Assert.Equal(0, CountWrites(foreignRunner));
                }

                return new CorruptArchiveSetup
                {
                    Runner = runner,
                    Client = client,
                    OriginalDirectory = originalDirectory,
                    OriginalManifest = originalManifest,
                    Current = current,
                    DurableParent = backupParent,
                    TempRoot = tempRoot,
                    ForeignDirectory = foreignDirectory,
                };
            }
            catch
            {
                current.Dispose();
                throw;
            }
        }

        private static string CreateInvalidCachedBoardDirectory(CorruptArchiveSetup setup)
        {
            string directory = Path.Combine(setup.TempRoot, "invalid-cached-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "backup-manifest.json");
            File.WriteAllText(path, "{\"profile\":{\"profileId\":\"invalid-test-profile\"}}");
            File.WriteAllText(path + ".sha256", HashFile(path) + "\n");
            return directory;
        }

        private sealed class GeometryAwareCommandRunner : ICommandRunner
        {
            private readonly IB001FakeEdlRunner _inner;
            private readonly IB001SyntheticDevice _device;

            public GeometryAwareCommandRunner(IB001FakeEdlRunner inner, IB001SyntheticDevice device)
            {
                _inner = inner;
                _device = device;
            }

            public async Task<ProcessResult> RunAsync(ProcessCommand command, CancellationToken cancellationToken = default)
            {
                ProcessResult result = await _inner.RunAsync(command, cancellationToken);
                var args = command.Arguments.ToArray();
                if (args.Length > 2 && args[0] == "read-sector" && args[1] == "0" && args[2] == "1")
                {
                    int lunIndex = Array.IndexOf(args, "--lun");
                    if (lunIndex >= 0 && lunIndex + 1 < args.Length && int.TryParse(args[lunIndex + 1], out int lun))
                    {
                        var geometry = E003EdlTestData.Geometry(lun, _device.TotalSectors(lun));
                        var filtered = geometry.Split('\n').Where(line => !line.Contains("GPT Header") && !line.Contains("printgpt"));
                        result = new ProcessResult(result.ExitCode, result.StandardOutput + "\n" + string.Join("\n", filtered), result.StandardError);
                    }
                }
                return result;
            }
        }

        private static int CountCorruptArchiveReads(CorruptArchiveSetup setup)
        {
            var backupPartition = setup.Current.Tables[0].Partitions.Single(p => p.Name == "ARMADA_BACKUP");
            return setup.Runner.Reads.Count(r =>
                r.Lun == 0 &&
                r.StartSector == backupPartition.StartSector &&
                r.SectorCount == backupPartition.SectorCount);
        }

        private sealed class CorruptArchiveSetup : IDisposable
        {
            public required IB001FakeEdlRunner Runner { get; set; }
            public required EdlClient Client { get; set; }
            public required string OriginalDirectory { get; set; }
            public required string OriginalManifest { get; set; }
            public required InternalBackupSet Current { get; set; }
            public required string DurableParent { get; set; }
            public required string TempRoot { get; set; }
            public required string ForeignDirectory { get; set; }

            public void Dispose()
            {
                Current.Dispose();
            }
        }
    }
}
