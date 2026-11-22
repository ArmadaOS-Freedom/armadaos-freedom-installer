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
    private readonly IReadOnlyList<string> _ablGuardRoles;
    private readonly IReadOnlyDictionary<string, EdlCapturedRegion> _directReplacementTargets;
    private readonly bool _ownsReferencedAblEvidence;
    private readonly List<VerifiedEdlFile> _claims;
    private bool _disposed;

    private BootMaintenanceAblRecovery(InternalBackupSet? current, InternalBackupSet? original, string model,
        Dictionary<string, string> rootKeys, IReadOnlyDictionary<string, string> vbmetaSha256,
        Dictionary<string, VerifiedEdlFile> replacements, List<VerifiedEdlFile> claims,
        IReadOnlyList<string>? ablGuardRoles = null,
        IReadOnlyDictionary<string, EdlCapturedRegion>? directReplacementTargets = null,
        bool ownsReferencedAblEvidence = false)
    {
        _current = current;
        _original = original;
        _model = model;
        _rootKeys = rootKeys;
        _vbmetaSha256 = vbmetaSha256;
        _ablGuardRoles = Array.AsReadOnly((ablGuardRoles ?? Array.Empty<string>()).ToArray());
        _directReplacementTargets = new ReadOnlyDictionary<string, EdlCapturedRegion>(
            directReplacementTargets is null
                ? new Dictionary<string, EdlCapturedRegion>(StringComparer.Ordinal)
                : new Dictionary<string, EdlCapturedRegion>(directReplacementTargets, StringComparer.Ordinal));
        _ownsReferencedAblEvidence = ownsReferencedAblEvidence;
        Replacements = new ReadOnlyDictionary<string, VerifiedEdlFile>(replacements);
        _claims = claims;
    }

    internal IReadOnlyDictionary<string, VerifiedEdlFile> Replacements { get; }

    /// <summary>
    /// The live loader roles that authorize a direct proof. This includes the selected source ABLs and, for
    /// slots receiving the 188 loader, the XBL configuration that defines the replacement window. A direct
    /// proof reads them again in every original-device capture, so a live loader that changed after the backup
    /// cannot receive AOSP metadata. Always empty for a repair proof, which keeps its own guarded roles.
    /// </summary>
    internal IReadOnlyList<string> AblGuardRoles => _ablGuardRoles;

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
            if (IsGblCapableAbl(abl, ct)) return null;
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
    /// First-install proof for a locked TB322FC. Every selected source slot is classified from the effective
    /// (primary) AVB key of its current ABL. A slot whose primary key is AOSP keeps that live ABL; a slot
    /// whose primary key is not AOSP is replaced with the pinned AOSP-capable 188 ABL. A secondary AOSP key
    /// blob is not evidence: the live TB322FC OEM ABL carries one and still rejects the AOSP-signed chain.
    /// The provider is invoked only when at least one selected slot needs that replacement, so an all-AOSP
    /// source never stages the asset. Otherwise this returns null and the existing GBL / fail-closed gates decide.
    /// </summary>
    internal static async Task<BootMaintenanceAblRecovery?> CreateDirectAsync(
        InternalBackupSet current,
        BootloaderState lockState,
        IReadOnlyDictionary<string, string> sourceSlots,
        Func<CancellationToken, Task<VerifiedEdlFile?>>? tb322fcAbl188Provider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(sourceSlots);
        if (current.Model != "TB322FC" || lockState != BootloaderState.Locked) return null;

        // Only the distinct source slots this install actually reads are authenticated. A target mapped to the
        // current slot by the strict inactive-slot proof never touches the other physical slot's vbmeta, so a
        // stale, non-zero or unparseable slot that is not a source can neither block nor be authorized here.
        var selected = new SortedSet<string>(sourceSlots.Values, StringComparer.Ordinal);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var vbmetaSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slot in selected)
        {
            VerifiedEdlFile root = Claim(current, "vbmeta_" + slot);
            if (await EdlRawBootBuilder.CurrentBootDetectEmptyAsync(root, ct).ConfigureAwait(false)) continue;
            var chain = await EdlRawBootBuilder.AuthenticateOriginalBootChainAsync(root, ct).ConfigureAwait(false);
            roots.Add(slot, chain.PublicKeySha256);
            vbmetaSha256.Add(slot, root.Sha256);
        }
        if (roots.Count == 0) return null;

        bool allRootsAosp = roots.Values.All(IsAosp);

        // The replacement decision is per selected slot and comes from the live ABL, not from a firmware
        // version. The primary key is the one the loader actually uses; merely referencing a secondary AOSP
        // blob does not mean the locked bootloader accepts AOSP-signed vbmeta.
        var primaryKeySha256 = new Dictionary<string, string>(StringComparer.Ordinal);
        var aospPrimarySlots = new HashSet<string>(StringComparer.Ordinal);
        var claims = new List<VerifiedEdlFile>();
        foreach (string slot in selected)
        {
            ct.ThrowIfCancellationRequested();

            // A selected source without its own non-empty authenticated root is not evidence; the existing
            // GBL / fail-closed paths decide instead.
            if (!roots.ContainsKey(slot)) return null;

            VerifiedEdlFile ablClaim;
            bool acceptsAosp;
            try
            {
                ablClaim = Claim(current, "abl_" + slot);
                byte[] abl = await ReadAsync(ablClaim, ct).ConfigureAwait(false);
                var primary = PrimaryAblKey(abl, ct);
                acceptsAosp = IsAosp(primary.KeySha256);

                // A GBL-capable loader is owned by the existing GBL route, even when its primary key
                // is not AOSP. Replacing it with 188 would move that device onto an
                // ABL-writing route for no reason.
                if (!acceptsAosp && IsGblCapableAbl(abl, ct)) return null;

                primaryKeySha256[slot] = primary.KeySha256;
            }
            catch (InvalidDataException)
            {
                // A missing, unreadable or unsupported ABL is not AOSP evidence: the existing gates decide.
                return null;
            }

            claims.Add(ablClaim);
            claims.Add(Claim(current, "vbmeta_" + slot));
            if (acceptsAosp) aospPrimarySlots.Add(slot);
        }

        // Nothing to replace: an all-AOSP source map is owned by the existing eligibility path, while a
        // mixed-root source still uses the established AOSP-primary proof.
        if (aospPrimarySlots.Count == selected.Count)
        {
            if (allRootsAosp) return null;
            return CreateDirect("TB322FC", lockState, sourceSlots, roots, vbmetaSha256, primaryKeySha256, claims);
        }

        // Builds without the optional 188 asset keep the upstream fail-closed behavior.
        if (tb322fcAbl188Provider is null) return null;

        VerifiedEdlFile? tb322fcAbl188 = await tb322fcAbl188Provider(ct).ConfigureAwait(false);
        if (tb322fcAbl188 is null)
        {
            throw new InvalidOperationException(
                "tb322fc-abl188-missing: a selected TB322FC ABL does not use the AOSP signing key as its "
                + "effective key "
                + "and this build does not embed the AOSP-capable 188 ABL.");
        }

        Tb322fcAbl188Assets.RequirePinnedCandidate(tb322fcAbl188);
        byte[] candidate = await ReadAsync(tb322fcAbl188, ct).ConfigureAwait(false);
        foreach (string slot in selected)
        {
            if (aospPrimarySlots.Contains(slot)) continue;

            ct.ThrowIfCancellationRequested();
            VerifiedEdlFile xbl = Claim(current, "xbl_config_" + slot);
            byte[] xblBytes = await ReadAsync(xbl, ct).ConfigureAwait(false);
            if (!InternalSlotLoaderStructure.TryRecognize(candidate, xblBytes, ct, out _))
            {
                throw new InvalidDataException(
                    "tb322fc-abl188-window: the pinned 188 ABL does not match the current "
                    + "xbl_config_" + slot + " loader window.");
            }

            claims.Add(xbl);
        }

        return CreateDirectWithAbl188(
            current.Model,
            lockState,
            sourceSlots,
            roots,
            vbmetaSha256,
            current.Files,
            tb322fcAbl188,
            aospPrimarySlots,
            claims);
    }

    /// <summary>
    /// The pure trust decision behind <see cref="CreateDirectAsync"/>: every selected source slot's current
    /// ABL must use the AOSP signing key as its effective (primary) key, including a mixed slot whose original
    /// root already is the AOSP key. The facts are already authenticated; this method never reads, writes or
    /// replaces anything.
    /// </summary>
    internal static BootMaintenanceAblRecovery? CreateDirect(
        string model,
        BootloaderState lockState,
        IReadOnlyDictionary<string, string> sourceSlots,
        IReadOnlyDictionary<string, string> originalRootKeys,
        IReadOnlyDictionary<string, string> currentVbmetaSha256,
        IReadOnlyDictionary<string, string> primaryAblKeySha256,
        List<VerifiedEdlFile>? claims = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSlots);
        ArgumentNullException.ThrowIfNull(originalRootKeys);
        ArgumentNullException.ThrowIfNull(currentVbmetaSha256);
        ArgumentNullException.ThrowIfNull(primaryAblKeySha256);

        if (model != "TB322FC" || lockState != BootloaderState.Locked) return null;

        var selected = new SortedSet<string>(sourceSlots.Values, StringComparer.Ordinal);
        if (selected.Count == 0) return null;

        // Retain authenticated facts only for the sources this install reads. An unused physical slot may be
        // stale, non-zero or invalid: it must neither block the proof nor ever be authorized by it.
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var vbmetaSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slot in selected)
        {
            if (!originalRootKeys.TryGetValue(slot, out string? root) || root is null) return null;
            if (!currentVbmetaSha256.TryGetValue(slot, out string? sha) || sha is null) return null;
            roots.Add(slot, root);
            vbmetaSha256.Add(slot, sha);
        }

        if (roots.Values.All(IsAosp)) return null;

        foreach (string slot in selected)
        {
            // Evidence is required for every selected source, never only for an OEM-root one.
            if (!primaryAblKeySha256.TryGetValue(slot, out string? key)
                || key is null
                || !IsAosp(key)) return null;
        }

        return new BootMaintenanceAblRecovery(
            null,
            null,
            model,
            roots,
            vbmetaSha256,
            new Dictionary<string, VerifiedEdlFile>(StringComparer.Ordinal),
            claims ?? new List<VerifiedEdlFile>(),
            selected.Select(slot => "abl_" + slot).ToArray(),
            ownsReferencedAblEvidence: true);
    }

    /// <summary>
    /// The test-build trust decision: a selected source slot whose effective (primary) key is not the AOSP
    /// signing key is replaced with the reviewed 188 ABL. Slots that already use that key keep their live
    /// ABL. The candidate must cover the full captured abl_{slot} partition before the
    /// replacement is authorized. This method performs no I/O and no write.
    /// </summary>
    internal static BootMaintenanceAblRecovery? CreateDirectWithAbl188(
        string model,
        BootloaderState lockState,
        IReadOnlyDictionary<string, string> sourceSlots,
        IReadOnlyDictionary<string, string> originalRootKeys,
        IReadOnlyDictionary<string, string> currentVbmetaSha256,
        IReadOnlyDictionary<string, EdlCapturedRegion> currentFiles,
        VerifiedEdlFile? candidate,
        IReadOnlySet<string> aospPrimarySlots,
        List<VerifiedEdlFile>? claims = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSlots);
        ArgumentNullException.ThrowIfNull(originalRootKeys);
        ArgumentNullException.ThrowIfNull(currentVbmetaSha256);
        ArgumentNullException.ThrowIfNull(currentFiles);
        ArgumentNullException.ThrowIfNull(aospPrimarySlots);

        if (model != "TB322FC" || lockState != BootloaderState.Locked) return null;

        var selected = new SortedSet<string>(sourceSlots.Values, StringComparer.Ordinal);
        if (selected.Count == 0) return null;
        if (aospPrimarySlots.Any(slot => !selected.Contains(slot)))
        {
            throw new InvalidDataException(
                "tb322fc-abl188-slots: AOSP-primary evidence was supplied for a slot that is not a selected source.");
        }

        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var vbmetaSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string slot in selected)
        {
            if (!originalRootKeys.TryGetValue(slot, out string? root) || root is null) return null;
            if (!currentVbmetaSha256.TryGetValue(slot, out string? sha) || sha is null) return null;
            roots.Add(slot, root);
            vbmetaSha256.Add(slot, sha);
        }

        string[] replacementSlots = selected
            .Where(slot => !aospPrimarySlots.Contains(slot))
            .ToArray();
        if (replacementSlots.Length == 0 && roots.Values.All(IsAosp)) return null;
        if (replacementSlots.Length > 0 && candidate is null)
        {
            throw new InvalidOperationException(
                "tb322fc-abl188-missing: a selected TB322FC ABL does not use the AOSP signing key as its "
                + "effective key "
                + "and this build does not embed the AOSP-capable 188 ABL.");
        }

        var replacements = new Dictionary<string, VerifiedEdlFile>(StringComparer.Ordinal);
        var replacementTargets = new Dictionary<string, EdlCapturedRegion>(StringComparer.Ordinal);
        foreach (string slot in replacementSlots)
        {
            VerifiedEdlFile replacement = candidate
                ?? throw new InvalidOperationException("tb322fc-abl188-missing: replacement candidate is null.");

            string role = "abl_" + slot;
            if (!currentFiles.TryGetValue(role, out EdlCapturedRegion? target) || target is null)
            {
                throw new InvalidDataException(
                    "tb322fc-abl188-target: the current backup carries no '" + role + "' target.");
            }

            if (target.Length != replacement.Length)
            {
                throw new InvalidDataException(
                    "tb322fc-abl188-size: the pinned ABL is " + replacement.Length + " bytes, but the "
                    + "current '" + role + "' partition is " + target.Length + " bytes.");
            }

            replacements.Add(role, replacement);
            replacementTargets.Add(role, target);
        }

        // The live ABL of every selected source is guarded. The XBL configuration is also guarded for
        // every slot that receives 188, because that is the exact loader window used to authorize it.
        string[] guardRoles = selected
            .Select(slot => "abl_" + slot)
            .Concat(replacementSlots.Select(slot => "xbl_config_" + slot))
            .ToArray();

        return new BootMaintenanceAblRecovery(
            null,
            null,
            model,
            roots,
            vbmetaSha256,
            replacements,
            claims ?? new List<VerifiedEdlFile>(),
            guardRoles,
            replacementTargets,
            ownsReferencedAblEvidence: true);
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

        if (_directReplacementTargets.Count != Replacements.Count)
            throw new InvalidDataException(
                "direct-abl-binding: every ABL replacement must carry its own captured target.");

        foreach (KeyValuePair<string, EdlCapturedRegion> pair in _directReplacementTargets)
        {
            if (!Replacements.ContainsKey(pair.Key)
                || !backup.Files.TryGetValue(pair.Key, out EdlCapturedRegion? current)
                || !Same(current.Role, pair.Value.Role)
                || current.Lun != pair.Value.Lun
                || current.StartSector != pair.Value.StartSector
                || current.SectorCount != pair.Value.SectorCount
                || current.SectorSize != pair.Value.SectorSize
                || !Same(current.PartUuid ?? string.Empty, pair.Value.PartUuid ?? string.Empty)
                || current.Length != pair.Value.Length
                || !Same(current.Sha256, pair.Value.Sha256))
            {
                throw new InvalidDataException(
                    "direct-abl-binding: the ABL replacement target does not belong to this backup.");
            }
        }
    }

    internal bool AuthorizesRoot(string slot, EdlRawResignedAvbNode node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _rootKeys.TryGetValue(slot, out string? key)
            && Same(key, node.OriginalPublicKeySha256)
            && _vbmetaSha256.TryGetValue(slot, out string? original)
            && Same(node.Original.Sha256, original)
            && IsAosp(node.PublicKeySha256)
            && (IsAosp(key) || _ownsReferencedAblEvidence || Replacements.ContainsKey("abl_" + slot));
    }

    internal bool MatchesReplacement(string role, int lun, long startSector, long sectorCount,
        long length, string currentSha256, string candidateSha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Replacements.TryGetValue(role, out var candidate)) return false;
        if (!_directReplacementTargets.TryGetValue(role, out EdlCapturedRegion? target))
        {
            if (_current is null || !_current.Files.TryGetValue(role, out target)) return false;
        }

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
        if (!Same(Sha256Text.Hex(bytes).ToUpperInvariant(), claim.Sha256))
            throw new InvalidDataException("repair-abl-source-changed");
        return bytes;
    }

    private static bool IsAosp(string hash) => Same(hash, EdlRawBootBuilder.SigningPublicKeySha256);

    /// <summary>
    /// The ABL's effective AVB key: the referenced key with the strongest executable-code evidence.
    /// </summary>
    internal static EdlRawBootBuilder.AblAvbKeyFacts PrimaryAblKey(byte[] abl, CancellationToken ct) =>
        EdlRawBootBuilder.InspectAblAvbKey(abl, ct);

    /// <summary>
    /// Whether the live ABL actually uses the AOSP signing key as its effective key. A secondary AOSP
    /// key blob is deliberately not enough: the reviewed TB322FC OEM ABL references one and still
    /// rejects AOSP-signed vbmeta under a locked bootloader.
    /// </summary>
    internal static bool AblAcceptsAosp(byte[] abl, CancellationToken ct) =>
        IsAosp(PrimaryAblKey(abl, ct).KeySha256);

    internal static bool IsGblCapableAbl(byte[] abl, CancellationToken ct) =>
        InternalSlotLoaderStructure.TryDecodeAblForGbl(abl, ct, out byte[] pe)
        && EfispLoadDetector.Detect(pe).Status == EfispLoad.Yes;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _original?.Dispose();
    }
}
