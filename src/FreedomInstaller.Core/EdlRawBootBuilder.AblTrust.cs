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
        var ranked = keys.OrderByDescending(pair => pair.Value.XrefCount).ToArray();
        if (ranked.Length == 0 || ranked[0].Value.XrefCount == 0
            || (ranked.Length > 1 && ranked[0].Value.XrefCount == ranked[1].Value.XrefCount))
            throw new InvalidDataException("ABL AVB key is missing, unreferenced or ambiguous.");
        var best = ranked[0];
        return new AblAvbKeyFacts(best.Key, Convert.ToHexString(SHA256.HashData(pe)),
            best.Value.XrefCount, best.Value.RawOffset);
    }
}
