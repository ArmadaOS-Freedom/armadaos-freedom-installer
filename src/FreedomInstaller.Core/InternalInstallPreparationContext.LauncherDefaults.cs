using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

internal sealed partial class InternalInstallPreparationContext
{
    private LauncherDefaultsPlan? _launcherDefaults;

    internal LauncherDefaultsPlan? LauncherDefaults => _launcherDefaults;

    private async Task PrepareLauncherDefaultsAsync(EdlClient edl, CancellationToken ct)
    {
        // A factory or resized layout destroys the Android data window, so there is no
        // launcher-defaults region to preserve and ARMADA_BACKUP may not exist yet.
        // Keep the verified 1119 behavior: only a non-erasing install may read it.
        if (AndroidDataErase)
        {
            return;
        }

        var original = _originalTables[0];
        GptPartition? backup = InternalInstallLauncherDefaults.FindInstalledBackupPartition(
            _package.EdlRawContract.BootRecipe.UsesArmadaBoot,
            original);
        if (backup is null)
        {
            return;
        }

        long sectorSize = original.SectorSize;
        long start = backup.StartSector;
        long count = backup.SectorCount;

        string headerPath = Path.Combine(_evidenceDirectory, "launcher-defaults-header-" + Guid.NewGuid().ToString("N") + ".bin");
        EdlReadEvidence header = await edl.ReadAsync(0, start, 65536 / sectorSize, headerPath, ct).ConfigureAwait(false);

        string tailPath = Path.Combine(_evidenceDirectory, "launcher-defaults-tail-" + Guid.NewGuid().ToString("N") + ".bin");
        EdlReadEvidence tail = await edl.ReadAsync(0, start + count - (65536 / sectorSize), 65536 / sectorSize, tailPath, ct).ConfigureAwait(false);

        Guid uuid = Guid.Parse(backup.PartUuid);
        _launcherDefaults = InternalInstallLauncherDefaults.PlanFromReads(
            header,
            tail,
            uuid,
            _package.EdlRawContract.DeviceModel,
            _evidenceDirectory,
            ct);

        if (_launcherDefaults is null)
        {
            return;
        }

        _claims.Add(Hold("launcher-defaults-before-header", header.FilePath, header.Length, header, ct));
        _claims.Add(Hold("launcher-defaults-before-tail", tail.FilePath, tail.Length, tail, ct));

        foreach (InternalArtifactRegion region in _launcherDefaults.Writes)
        {
            FileStream stream = OpenRead(region.FilePath);
            try
            {
                string sha256 = HashHeld(stream, ct);
                if (!SameSha(sha256, region.Sha256) || stream.Length != region.Length)
                {
                    throw new InvalidOperationException("A staged launcher-default region changed after planning.");
                }

                _claims.Add(new HeldClaim(region.Role, region.FilePath, region.Length, region.Sha256, stream));
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    internal async Task VerifyLauncherDefaultsAsync(EdlClient edl, bool after, CancellationToken ct)
    {
        if (_launcherDefaults is null)
        {
            return;
        }

        string headerPath = Path.Combine(_evidenceDirectory, "launcher-defaults-verify-header-" + Guid.NewGuid().ToString("N") + ".bin");
        EdlReadEvidence header = await edl.ReadAsync(0, _launcherDefaults.BeforeHeader.StartSector, _launcherDefaults.BeforeHeader.SectorCount, headerPath, ct).ConfigureAwait(false);

        string tailPath = Path.Combine(_evidenceDirectory, "launcher-defaults-verify-tail-" + Guid.NewGuid().ToString("N") + ".bin");
        EdlReadEvidence tail = await edl.ReadAsync(0, _launcherDefaults.BeforeTail.StartSector, _launcherDefaults.BeforeTail.SectorCount, tailPath, ct).ConfigureAwait(false);

        if (!SameSha(header.Sha256, _launcherDefaults.BeforeHeader.Sha256) || header.Length != _launcherDefaults.BeforeHeader.Length)
        {
            throw new InvalidOperationException("Launcher-defaults header region changed since the original read.");
        }

        if (!SameSha(tail.Sha256, after ? _launcherDefaults.AfterTailSha256 : _launcherDefaults.BeforeTail.Sha256) || tail.Length != _launcherDefaults.BeforeTail.Length)
        {
            throw new InvalidOperationException("Launcher-defaults tail region changed since the original read.");
        }

    }

    private void WriteLauncherDefaultsConfirmation(Utf8JsonWriter json)
    {
        if (_launcherDefaults is null)
        {
            return;
        }

        json.WriteStartObject("launcherDefaults");
        json.WriteNumber("beforeHeaderStart", _launcherDefaults.BeforeHeader.StartSector);
        json.WriteNumber("beforeHeaderCount", _launcherDefaults.BeforeHeader.SectorCount);
        json.WriteString("beforeHeaderSha256", _launcherDefaults.BeforeHeader.Sha256);
        json.WriteNumber("beforeTailStart", _launcherDefaults.BeforeTail.StartSector);
        json.WriteNumber("beforeTailCount", _launcherDefaults.BeforeTail.SectorCount);
        json.WriteString("beforeTailSha256", _launcherDefaults.BeforeTail.Sha256);
        json.WriteString("afterTailSha256", _launcherDefaults.AfterTailSha256);
        json.WriteStartArray("writes");
        foreach (InternalArtifactRegion region in _launcherDefaults.Writes)
        {
            json.WriteStartObject();
            json.WriteString("role", region.Role);
            json.WriteNumber("start", region.StartSector);
            json.WriteNumber("count", region.SectorCount);
            json.WriteString("sha256", region.Sha256);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }
}
