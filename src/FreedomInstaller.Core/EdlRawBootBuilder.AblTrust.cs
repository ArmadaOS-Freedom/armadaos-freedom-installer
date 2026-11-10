using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using FreedomInstaller.Core.Gbl;

namespace FreedomInstaller.Core;

public static partial class EdlRawBootBuilder
{
    internal readonly record struct AblAvbKeyFacts(string KeySha256, string PeSha256, int XrefCount, int RawOffset);

    internal static AblAvbKeyFacts InspectAblAvbKey(byte[] abl, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(abl);
        token.ThrowIfCancellationRequested();
        if (abl.Length == 0 || abl.Length > 16 * 1024 * 1024
            || !InternalSlotLoaderStructure.TryDecodeAblForGbl(abl, token, out byte[] pe))
            throw new InvalidDataException("ABL is not a supported ELF/FV/LZMA container.");
        return InspectAblPeAvbKey(pe, token);
    }

    internal static AblAvbKeyFacts InspectAblPeAvbKey(byte[] pe, CancellationToken token)
    {
        var keys = ReadAblAvbKeyCandidates(pe, token);
        var ranked = keys.OrderByDescending(pair => pair.Value.XrefCount).ToArray();
        if (ranked.Length == 0 || ranked[0].Value.XrefCount == 0
            || (ranked.Length > 1 && ranked[0].Value.XrefCount == ranked[1].Value.XrefCount))
            throw new InvalidDataException("ABL AVB key is missing, unreferenced or ambiguous.");
        var best = ranked[0];
        return new AblAvbKeyFacts(best.Key, Convert.ToHexString(SHA256.HashData(pe)),
            best.Value.XrefCount, best.Value.RawOffset);
    }

    /// <summary>
    /// Read-only view of every structurally valid AVB key the ABL actually references from executable
    /// code, deduplicated by SHA-256 and keeping the strongest cross-reference evidence per key. The
    /// single-key <see cref="InspectAblAvbKey"/> keeps its own ranking; a key that is referenced but
    /// does not win that ranking is still valid evidence here.
    /// </summary>
    internal static IReadOnlyList<AblAvbKeyFacts> InspectAblAvbKeys(byte[] abl, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(abl);
        token.ThrowIfCancellationRequested();
        if (abl.Length == 0 || abl.Length > 16 * 1024 * 1024
            || !InternalSlotLoaderStructure.TryDecodeAblForGbl(abl, token, out byte[] pe))
            throw new InvalidDataException("ABL is not a supported ELF/FV/LZMA container.");
        return InspectAblPeAvbKeys(pe, token);
    }

    /// <summary>
    /// Every structurally valid, code-referenced AVB key of an already decoded ABL PE image. Unreferenced
    /// keys, keys referenced only from data and keys that fail public-key validation are never evidence.
    /// </summary>
    internal static IReadOnlyList<AblAvbKeyFacts> InspectAblPeAvbKeys(byte[] pe, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(pe);
        token.ThrowIfCancellationRequested();

        var keys = ReadAblAvbKeyCandidates(pe, token);
        string peSha256 = Convert.ToHexString(SHA256.HashData(pe));

        return keys
            .Where(pair => pair.Value.XrefCount > 0)
            .OrderByDescending(pair => pair.Value.XrefCount)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new AblAvbKeyFacts(
                pair.Key, peSha256, pair.Value.XrefCount, pair.Value.RawOffset))
            .ToArray();
    }

    /// <summary>
    /// Structurally valid AVB key blobs of one PE image, deduplicated by SHA-256 and keeping the candidate
    /// with the most executable-code references. This is the shared scan behind both views.
    /// </summary>
    private static Dictionary<string, Gbl.EfispLoadDetector.AvbKeyCandidate> ReadAblAvbKeyCandidates(
        byte[] pe,
        CancellationToken token)
    {
        var keys = new Dictionary<string, Gbl.EfispLoadDetector.AvbKeyCandidate>(StringComparer.Ordinal);
        foreach (var candidate in EfispLoadDetector.ReadAvbKeyCandidates(pe, token))
        {
            token.ThrowIfCancellationRequested();
            try { AvbNodeValidatePublicKey(candidate.Blob); }
            catch (InvalidDataException) { continue; }
            string hash = Convert.ToHexString(SHA256.HashData(candidate.Blob));
            if (!keys.TryGetValue(hash, out var prior) || candidate.XrefCount > prior.XrefCount)
                keys[hash] = candidate;
        }
        return keys;
    }
}
