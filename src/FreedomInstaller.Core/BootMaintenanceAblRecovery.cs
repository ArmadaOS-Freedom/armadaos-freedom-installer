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

// A trusted proof created from authenticated captures of the same physical device.
// It never substitutes historical Android payloads for the currently installed Android.
internal sealed class BootMaintenanceAblRecovery : IDisposable
{
    private readonly InternalBackupSet? _current;
    private readonly InternalBackupSet? _original;
    private readonly string _model;
    private readonly Dictionary<string, string> _rootKeys;
    private readonly IReadOnlyDictionary<string, string> _vbmetaSha256;
    private readonly List<VerifiedEdlFile> _claims;
    private bool _disposed;

    private BootMaintenanceAblRecovery(InternalBackupSet? current, InternalBackupSet? original, string model,
        Dictionary<string, string> rootKeys, IReadOnlyDictionary<string, string> vbmetaSha256,
        Dictionary<string, VerifiedEdlFile> replacements, List<VerifiedEdlFile> claims)
    {
        _current = current;
        _original = original;
        _model = model;
        _rootKeys = rootKeys;
        _vbmetaSha256 = vbmetaSha256;
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
        var vbmetaSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slot in new[] { "a", "b" })
        {
            VerifiedEdlFile root = Claim(current, "vbmeta_" + slot);
            if (await EdlRawBootBuilder.CurrentBootDetectEmptyAsync(root, ct).ConfigureAwait(false)) continue;
            var chain = await EdlRawBootBuilder.AuthenticateOriginalBootChainAsync(root, ct).ConfigureAwait(false);
            roots.Add(slot, chain.PublicKeySha256);
            vbmetaSha256.Add(slot, root.Sha256);
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
            return new BootMaintenanceAblRecovery(current, original, current.Model, roots, vbmetaSha256,
                replacements, claims);
        }
        catch
        {
            original?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// First-install proof for a locked TB322FC whose current ABL already carries the AOSP key beside the
    /// OEM key. It reads only the current complete backup: no original or historical directory is opened,
    /// no replacement is created and no ABL is ever substituted, restored or written. Every source slot
    /// that still has an OEM original root must actually reference the AOSP key in that same slot's
    /// current ABL; otherwise this returns null and the existing GBL / fail-closed gates decide.
    /// </summary>
    internal static async Task<BootMaintenanceAblRecovery?> CreateDirectAsync(
        InternalBackupSet current,
        BootloaderState lockState,
        IReadOnlyDictionary<string, string> sourceSlots,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(sourceSlots);
        if (current.Model != "TB322FC" || lockState != BootloaderState.Locked) return null;

        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var vbmetaSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slot in new[] { "a", "b" })
        {
            VerifiedEdlFile root = Claim(current, "vbmeta_" + slot);
            if (await EdlRawBootBuilder.CurrentBootDetectEmptyAsync(root, ct).ConfigureAwait(false)) continue;
            var chain = await EdlRawBootBuilder.AuthenticateOriginalBootChainAsync(root, ct).ConfigureAwait(false);
            roots.Add(slot, chain.PublicKeySha256);
            vbmetaSha256.Add(slot, root.Sha256);
        }
        if (roots.Count == 0) return null;

        var referenced = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var claims = new List<VerifiedEdlFile>();
        foreach (string slot in sourceSlots.Values.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!roots.TryGetValue(slot, out string? root) || root is null || IsAosp(root)) continue;

            VerifiedEdlFile ablClaim;
            IReadOnlyList<EdlRawBootBuilder.AblAvbKeyFacts> keys;
            try
            {
                ablClaim = Claim(current, "abl_" + slot);
                byte[] abl = await ReadAsync(ablClaim, ct).ConfigureAwait(false);
                keys = EdlRawBootBuilder.InspectAblAvbKeys(abl, ct);
            }
            catch (InvalidDataException)
            {
                // A missing, unreadable or unsupported ABL is not AOSP evidence: the existing gates decide.
                return null;
            }

            claims.Add(ablClaim);
            claims.Add(Claim(current, "vbmeta_" + slot));
            referenced[slot] = keys.Select(key => key.KeySha256).ToArray();
        }

        return CreateDirect("TB322FC", lockState, sourceSlots, roots, vbmetaSha256, referenced, claims);
    }

    /// <summary>
    /// The pure trust decision behind <see cref="CreateDirectAsync"/>: every required source slot with a
    /// non-empty OEM original root must reference the AOSP signing key in its own current ABL. The facts
    /// are already authenticated; this method never reads, writes or replaces anything.
    /// </summary>
    internal static BootMaintenanceAblRecovery? CreateDirect(
        string model,
        BootloaderState lockState,
        IReadOnlyDictionary<string, string> sourceSlots,
        IReadOnlyDictionary<string, string> originalRootKeys,
        IReadOnlyDictionary<string, string> currentVbmetaSha256,
        IReadOnlyDictionary<string, string[]> referencedAblKeySha256,
        List<VerifiedEdlFile>? claims = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSlots);
        ArgumentNullException.ThrowIfNull(originalRootKeys);
        ArgumentNullException.ThrowIfNull(currentVbmetaSha256);
        ArgumentNullException.ThrowIfNull(referencedAblKeySha256);

        if (model != "TB322FC" || lockState != BootloaderState.Locked) return null;
        if (originalRootKeys.Count == 0 || originalRootKeys.Values.All(IsAosp)) return null;
        if (originalRootKeys.Keys.Any(slot => !currentVbmetaSha256.ContainsKey(slot))) return null;

        foreach (string slot in sourceSlots.Values.Distinct(StringComparer.Ordinal))
        {
            // A required source without its own current root is not evidence; the existing paths decide.
            if (!originalRootKeys.TryGetValue(slot, out string? root) || root is null) return null;
            if (IsAosp(root)) continue;
            if (!referencedAblKeySha256.TryGetValue(slot, out string[]? keys)
                || keys is null
                || !keys.Any(IsAosp)) return null;
        }

        var roots = new Dictionary<string, string>(originalRootKeys, StringComparer.Ordinal);
        var vbmetaSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slot in roots.Keys)
        {
            vbmetaSha256.Add(slot, currentVbmetaSha256[slot]);
        }

        return new BootMaintenanceAblRecovery(
            null,
            null,
            model,
            roots,
            vbmetaSha256,
            new Dictionary<string, VerifiedEdlFile>(StringComparer.Ordinal),
            claims ?? new List<VerifiedEdlFile>());
    }

    internal void RequireBinding(InternalBackupSet backup, BootloaderState state,
        IReadOnlyDictionary<string, string> sourceSlots)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Replacements.Keys.Any(role => !sourceSlots.Values.Contains(role.Substring(4), StringComparer.Ordinal)))
            throw new InvalidDataException("repair-abl-binding: recovery proof does not belong to this source map.");

        if (_current is not null)
        {
            if (!ReferenceEquals(backup, _current) || state != BootloaderState.Locked || backup.Model != _model
                || sourceSlots.Values.Distinct(StringComparer.Ordinal).Any(slot => !_rootKeys.ContainsKey(slot)))
                throw new InvalidDataException("repair-abl-binding: recovery proof does not belong to this source map.");
            return;
        }

        // Direct proof: it was created from this backup's own vbmeta facts, so binding re-reads them.
        if (state != BootloaderState.Locked || backup.Model != _model
            || sourceSlots.Values.Distinct(StringComparer.Ordinal).Any(slot => !_rootKeys.ContainsKey(slot)))
            throw new InvalidDataException("direct-abl-binding: the AOSP re-sign proof does not belong to this backup.");

        foreach (string slot in sourceSlots.Values.Distinct(StringComparer.Ordinal))
        {
            if (!Same(Claim(backup, "vbmeta_" + slot).Sha256, _vbmetaSha256[slot]))
                throw new InvalidDataException("direct-abl-binding: the AOSP re-sign proof does not belong to this backup.");
        }
    }

    internal bool AuthorizesRoot(string slot, EdlRawResignedAvbNode node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _rootKeys.TryGetValue(slot, out string? key)
            && Same(key, node.OriginalPublicKeySha256)
            && _vbmetaSha256.TryGetValue(slot, out string? original)
            && Same(node.Original.Sha256, original)
            && IsAosp(node.PublicKeySha256);
    }

    internal bool MatchesReplacement(string role, int lun, long startSector, long sectorCount,
        long length, string currentSha256, string candidateSha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is null) return false;
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
