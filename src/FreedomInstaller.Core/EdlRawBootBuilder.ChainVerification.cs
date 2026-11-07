using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

public static partial class EdlRawBootBuilder
{
    internal static Task VerifyAvbBootChainAsync(VerifiedEdlFile root, VerifiedEdlFile boot,
        VerifiedEdlFile recovery, VerifiedEdlFile system, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            using var rootStream = OpenStockOriginalLease(root.Path);
            using var bootStream = OpenStockOriginalLease(boot.Path);
            using var recoveryStream = OpenStockOriginalLease(recovery.Path);
            using var systemStream = OpenStockOriginalLease(system.Path);
            VerifyLease(rootStream, root, "chain root", ct);
            VerifyLease(bootStream, boot, "chain boot", ct);
            VerifyLease(recoveryStream, recovery, "chain recovery", ct);
            VerifyLease(systemStream, system, "chain vbmeta_system", ct);
            var rootMeta = AvbNodeReadMetadata(rootStream, EdlRawAvbNodeKind.Vbmeta, rootStream.Length, ct);
            var bootMeta = AvbNodeReadFooterLeafMetadata(bootStream, bootStream.Length, ct);
            var recoveryMeta = AvbNodeReadFooterLeafMetadata(recoveryStream, recoveryStream.Length, ct);
            var systemMeta = AvbNodeReadMetadata(systemStream, EdlRawAvbNodeKind.VbmetaSystem, systemStream.Length, ct);
            RequirePinnedChildKey(rootMeta, bootMeta, "boot", "boot");
            RequirePinnedChildKey(rootMeta, recoveryMeta, "recovery", "recovery");
            RequirePinnedChildKey(rootMeta, systemMeta, "vbmeta_system", "vbmeta_system");
            ProveSourceRecoveryPayload(recoveryStream, recoveryMeta, ct);
            byte[] footer = new byte[AvbFooterBytes];
            ReadExactly(bootStream, footer, bootStream.Length - AvbFooterBytes, footer.Length);
            long imageSize = checked((long)ReadBeU64(footer, 12));
            var parsed = ParseVbmeta(bootStream, bootMeta.VbmetaOffset, bootMeta.VbmetaSize, imageSize, ct);
            string digest = ComputeSaltedDigest(bootStream, parsed.Hash.Salt, imageSize,
                HashNameFor(parsed.Hash.Algorithm), ct);
            if (!string.Equals(digest, ToHex(parsed.Hash.Digest), StringComparison.Ordinal))
                throw new InvalidDataException("Boot payload does not match its authenticated AVB descriptor.");
            ct.ThrowIfCancellationRequested();
        }, ct);
    }
}
