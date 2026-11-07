using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core.Gbl;

namespace FreedomInstaller.Core;

// A repair-only proof, created from authenticated captures of the same physical device.
// It never substitutes historical Android payloads for the currently installed Android.
internal sealed class BootMaintenanceAblRecovery : IDisposable
{
    private readonly InternalBackupSet _current;
    private readonly InternalBackupSet? _original;
    private readonly Dictionary<string, string> _rootKeys;
    private readonly List<VerifiedEdlFile> _claims;
    private bool _disposed;

    private BootMaintenanceAblRecovery(InternalBackupSet current, InternalBackupSet? original,
        Dictionary<string, string> rootKeys, Dictionary<string, VerifiedEdlFile> replacements,
        List<VerifiedEdlFile> claims)
    {
        _current = current;
        _original = original;
        _rootKeys = rootKeys;
        Replacements = new ReadOnlyDictionary<string, VerifiedEdlFile>(replacements);
        _claims = claims;
    }

    internal IReadOnlyDictionary<string, VerifiedEdlFile> Replacements { get; }

    internal static async Task<BootMaintenanceAblRecovery?> CreateAsync(
        EdlClient client, InternalBackupSet current, BootloaderState lockState,
        string? originalDirectory, CancellationToken ct)
    {
        if (current.Model != "TB322FC" || lockState != BootloaderState.Locked) return null;
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slot in new[] { "a", "b" })
        {
            VerifiedEdlFile root = Claim(current, "vbmeta_" + slot);
            if (await EdlRawBootBuilder.CurrentBootDetectEmptyAsync(root, ct).ConfigureAwait(false)) continue;
            var chain = await EdlRawBootBuilder.AuthenticateOriginalBootChainAsync(root, ct).ConfigureAwait(false);
            roots.Add(slot, chain.PublicKeySha256);
        }
        if (roots.Count == 0 || roots.Values.All(IsAosp)) return null;

        // Preserve the established GBL route when the updated loader provides it.
        foreach (string slot in roots.Where(p => !IsAosp(p.Value)).Select(p => p.Key))
        {
            byte[] abl = await ReadAsync(Claim(current, "abl_" + slot), ct).ConfigureAwait(false);
            if (InternalSlotLoaderStructure.TryDecodeAblForGbl(abl, ct, out byte[] pe)
                && EfispLoadDetector.Detect(pe).Status == EfispLoad.Yes) return null;
        }

        InternalBackupSet? original = null;
        var replacements = new Dictionary<string, VerifiedEdlFile>(StringComparer.Ordinal);
        var claims = new List<VerifiedEdlFile>();
        try
        {
            foreach (string slot in roots.Keys)
            {
                VerifiedEdlFile currentAbl = Claim(current, "abl_" + slot);
                byte[] abl = await ReadAsync(currentAbl, ct).ConfigureAwait(false);
                var key = EdlRawBootBuilder.InspectAblAvbKey(abl, ct);
                claims.Add(currentAbl);
                claims.Add(Claim(current, "vbmeta_" + slot));
                if (IsAosp(key.KeySha256)) continue;
                if (!Same(key.KeySha256, roots[slot]))
                    throw new InvalidDataException("repair-abl-key-mismatch: current ABL and original vbmeta disagree, slot " + slot);
                if (string.IsNullOrWhiteSpace(originalDirectory))
                    throw new InvalidOperationException("repair-abl-original-required: this device's original board backup is required.");
                original ??= await InternalInstallBackup.OpenBootMaintenanceBootloaderRecoveryAsync(
                    client, current, originalDirectory, ct).ConfigureAwait(false);
                VerifiedEdlFile oldAbl = Claim(original, "abl_" + slot);
                VerifiedEdlFile oldRoot = Claim(original, "vbmeta_" + slot);
                byte[] candidate = await ReadAsync(oldAbl, ct).ConfigureAwait(false);
                var oldKey = EdlRawBootBuilder.InspectAblAvbKey(candidate, ct);
                var oldChain = await EdlRawBootBuilder.AuthenticateOriginalBootChainAsync(oldRoot, ct).ConfigureAwait(false);
                if (!IsAosp(oldKey.KeySha256) || !IsAosp(oldChain.PublicKeySha256))
                    throw new InvalidDataException("repair-abl-original-key: original ABL and vbmeta must both use the AOSP key.");
                VerifiedEdlFile xbl = Claim(current, "xbl_config_" + slot);
                byte[] currentXbl = await ReadAsync(xbl, ct).ConfigureAwait(false);
                if (!InternalSlotLoaderStructure.TryRecognize(abl, currentXbl, ct, out _)
                    || !InternalSlotLoaderStructure.TryRecognize(candidate, currentXbl, ct, out _))
                    throw new InvalidDataException("repair-abl-window: current and original loaders must match the current XBL window.");
                replacements.Add("abl_" + slot, oldAbl);
                claims.Add(oldAbl);
                claims.Add(oldRoot);
                claims.Add(xbl);
            }
            return new BootMaintenanceAblRecovery(current, original, roots, replacements, claims);
        }
        catch
        {
            original?.Dispose();
            throw;
        }
    }

    internal void RequireBinding(InternalBackupSet backup, BootloaderState state,
        IReadOnlyDictionary<string, string> sourceSlots)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(backup, _current) || state != BootloaderState.Locked || backup.Model != "TB322FC"
            || sourceSlots.Values.Distinct(StringComparer.Ordinal).Any(slot => !_rootKeys.ContainsKey(slot))
            || Replacements.Keys.Any(role => !sourceSlots.Values.Contains(role.Substring(4), StringComparer.Ordinal)))
            throw new InvalidDataException("repair-abl-binding: recovery proof does not belong to this source map.");
    }

    internal bool AuthorizesRoot(string slot, EdlRawResignedAvbNode node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _rootKeys.TryGetValue(slot, out string? key)
            && Same(key, node.OriginalPublicKeySha256)
            && Same(node.Original.Sha256, Claim(_current, "vbmeta_" + slot).Sha256)
            && IsAosp(node.PublicKeySha256);
    }

    internal bool MatchesReplacement(string role, int lun, long startSector, long sectorCount,
        long length, string currentSha256, string candidateSha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Replacements.TryGetValue(role, out var candidate)) return false;
        var target = _current.Files[role];
        return target.Lun == lun && target.StartSector == startSector && target.SectorCount == sectorCount
            && target.Length == length && candidate.Length == length
            && Same(target.Sha256, currentSha256) && Same(candidate.Sha256, candidateSha256);
    }

    internal async Task RevalidateAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (VerifiedEdlFile claim in _claims)
            await ReadAsync(claim, ct).ConfigureAwait(false);
    }

    private static VerifiedEdlFile Claim(InternalBackupSet set, string role)
    {
        if (!set.Files.TryGetValue(role, out var region))
            throw new InvalidDataException("repair-abl-missing-role: " + role);
        return new VerifiedEdlFile(region.FilePath, region.Length, region.Sha256);
    }

    private static async Task<byte[]> ReadAsync(VerifiedEdlFile claim, CancellationToken ct)
    {
        if (claim.Length <= 0 || claim.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("repair-abl-invalid-length: " + claim.Path);
        using var stream = new FileStream(claim.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != claim.Length) throw new InvalidDataException("repair-abl-source-changed");
        var bytes = new byte[checked((int)claim.Length)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (!Same(Convert.ToHexString(SHA256.HashData(bytes)), claim.Sha256))
            throw new InvalidDataException("repair-abl-source-changed");
        return bytes;
    }

    private static bool IsAosp(string hash) => Same(hash, EdlRawBootBuilder.SigningPublicKeySha256);
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _original?.Dispose();
    }
}
