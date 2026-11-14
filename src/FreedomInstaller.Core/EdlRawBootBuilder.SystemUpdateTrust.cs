using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core.Gbl;

namespace FreedomInstaller.Core;

public sealed record SystemUpdateSlotFiles(
    VerifiedEdlFile Boot,
    VerifiedEdlFile VendorBoot,
    VerifiedEdlFile InitBoot,
    VerifiedEdlFile Abl,
    VerifiedEdlFile XblConfig,
    VerifiedEdlFile Vbmeta,
    VerifiedEdlFile Recovery,
    VerifiedEdlFile VbmetaSystem);

public sealed record SystemUpdateSlotTrust(
    string Slot,
    InternalSlotMaterial Material,
    long MaxRawBytes,
    string RootPublicKeySha256,
    string BootPublicKeySha256,
    string MetadataSha256,
    bool PayloadDigestMatchesSignedMetadata,
    string? AblRootPublicKeySha256);

public sealed record SystemUpdateTrust(
    string Model,
    SystemUpdateEnvelopeMode Mode,
    BootloaderState LockState,
    SystemUpdateSlotTrust A,
    SystemUpdateSlotTrust B,
    string? GblRegion,
    string? EfispSha256);

public static partial class EdlRawBootBuilder
{
    private const long SystemUpdateMaxAblXblBytes = 16L * 1024 * 1024;
    private const long SystemUpdateMaxVendorOrEfispBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Authenticates supplied file claims and boot-chain metadata only, including the boot payload digest for the AOSP route. The caller must bind claims to the current physical device, slots, lock state, and OTA+EFI geometry; this does not validate actual hardware acceptance or unchanged recovery, super, or rootfs payload contents.
    /// </summary>
    public static async Task<SystemUpdateTrust> InspectSystemUpdateTrustAsync(
        string model,
        BootloaderState lockState,
        SystemUpdateSlotFiles a,
        SystemUpdateSlotFiles b,
        VerifiedEdlFile? efisp,
        CancellationToken ct = default)
    {
        if (!string.Equals(model, "TB322FC", StringComparison.Ordinal) &&
            !string.Equals(model, "TB323FU", StringComparison.Ordinal) &&
            !string.Equals(model, "TB710FU", StringComparison.Ordinal))
        {
            throw new ArgumentException("Model must be exactly 'TB322FC', 'TB323FU', or 'TB710FU'.", nameof(model));
        }

        if (!Enum.IsDefined(typeof(BootloaderState), lockState))
            throw new ArgumentOutOfRangeException(nameof(lockState));

        ValidateSlotFiles(a, nameof(a));
        ValidateSlotFiles(b, nameof(b));
        ct.ThrowIfCancellationRequested();

        var leases = new List<HeldLease>(17);
        try
        {
            SlotInspectionContext ctxA = await InspectSingleSlotAsync("a", a, leases, ct).ConfigureAwait(false);
            SlotInspectionContext ctxB = await InspectSingleSlotAsync("b", b, leases, ct).ConfigureAwait(false);

            HeldLease? efispLease = null;
            if (efisp is not null)
            {
                var openedEfisp = OpenClaimLease("system update efisp", efisp, ct);
                leases.Add(openedEfisp);
                efispLease = openedEfisp;
            }

            if (ctxA.Mode != ctxB.Mode)
                throw new InvalidDataException("Slot A and Slot B have mixed trust modes; repair installation is required.");

            SystemUpdateEnvelopeMode mode = ctxA.Mode;
            string? ablKeyA = null;
            string? ablKeyB = null;
            string? gblRegionText = null;
            string? efispSha256 = null;
            long boundA;
            long boundB;

            if (mode == SystemUpdateEnvelopeMode.AospResign)
            {
                if (!ctxA.Leaf.DigestMatches || !ctxB.Leaf.DigestMatches)
                    throw new InvalidDataException("The AOSP route requires both slots to have valid boot leaf payload digests.");

                if (lockState != BootloaderState.Unlocked)
                {
                    byte[] ablBytesA = CurrentBootReadBounded(ctxA.AblLease.Stream, SystemUpdateMaxAblXblBytes, "slot a abl", ct);
                    byte[] ablBytesB = CurrentBootReadBounded(ctxB.AblLease.Stream, SystemUpdateMaxAblXblBytes, "slot b abl", ct);
                    AblAvbKeyFacts factsA = InspectAblAvbKey(ablBytesA, ct);
                    AblAvbKeyFacts factsB = InspectAblAvbKey(ablBytesB, ct);
                    if (!HashEquals(factsA.KeySha256, ctxA.RootKeySha256) ||
                        !HashEquals(factsB.KeySha256, ctxB.RootKeySha256))
                    {
                        throw new InvalidDataException("Locked or unknown bootloader state requires each slot ABL AVB key to match its authenticated root key.");
                    }

                    ablKeyA = factsA.KeySha256;
                    ablKeyB = factsB.KeySha256;
                }

                boundA = ctxA.Material.LoaderBudget.MaxRawBytes;
                boundB = ctxB.Material.LoaderBudget.MaxRawBytes;
                if (boundA <= 0 || boundB <= 0)
                    throw new InvalidDataException("Measured AOSP loader budget must be positive.");
            }
            else
            {
                if (efisp is null || efispLease is null)
                    throw new InvalidDataException("The GBL route requires a verified efisp file.");

                byte[] ablBytesA = CurrentBootReadBounded(ctxA.AblLease.Stream, SystemUpdateMaxAblXblBytes, "slot a abl", ct);
                byte[] ablBytesB = CurrentBootReadBounded(ctxB.AblLease.Stream, SystemUpdateMaxAblXblBytes, "slot b abl", ct);
                byte[] xblBytesA = CurrentBootReadBounded(ctxA.XblLease.Stream, SystemUpdateMaxAblXblBytes, "slot a xbl_config", ct);
                byte[] xblBytesB = CurrentBootReadBounded(ctxB.XblLease.Stream, SystemUpdateMaxAblXblBytes, "slot b xbl_config", ct);

                if (!InternalSlotLoaderStructure.TryDecodeAblForGbl(ablBytesA, ct, out byte[] peA) ||
                    !InternalSlotLoaderStructure.TryDecodeAblForGbl(ablBytesB, ct, out byte[] peB))
                {
                    throw new InvalidDataException("Both slots must have decodable GBL ABL images.");
                }

                if (EfispLoadDetector.Detect(peA).Status != EfispLoad.Yes ||
                    EfispLoadDetector.Detect(peB).Status != EfispLoad.Yes)
                {
                    throw new InvalidDataException("Both slots must positively detect GBL efisp loading in ABL.");
                }

                if (!InternalSlotLoaderStructure.TryRecognize(ablBytesA, xblBytesA, ct, out _) ||
                    !InternalSlotLoaderStructure.TryRecognize(ablBytesA, xblBytesB, ct, out _) ||
                    !InternalSlotLoaderStructure.TryRecognize(ablBytesB, xblBytesA, ct, out _) ||
                    !InternalSlotLoaderStructure.TryRecognize(ablBytesB, xblBytesB, ct, out _))
                {
                    throw new InvalidDataException("Every cross-slot pair of ABL and XBL_CONFIG must be a recognized loader structure.");
                }

                byte[] vendorBytesA = CurrentBootReadBounded(ctxA.VendorLease.Stream, SystemUpdateMaxVendorOrEfispBytes, "slot a vendor_boot", ct);
                byte[] vendorBytesB = CurrentBootReadBounded(ctxB.VendorLease.Stream, SystemUpdateMaxVendorOrEfispBytes, "slot b vendor_boot", ct);
                GblRuntimeRegion? regionA = InternalInstallPreparationContext.ReadGblRuntimeRegion(vendorBytesA, ct);
                GblRuntimeRegion? regionB = InternalInstallPreparationContext.ReadGblRuntimeRegion(vendorBytesB, ct);
                if (regionA is null || regionB is null || regionA.Value != regionB.Value)
                    throw new InvalidDataException("Both slots must declare the same exact GBL product_region (PRC or ROW).");

                GblRuntimeRegion region = regionA.Value;
                gblRegionText = region == GblRuntimeRegion.Prc ? "PRC" : "ROW";
                byte[] pinnedAsset = await GblRuntimeAssets.ReadVerifiedBytesAsync(region, ct).ConfigureAwait(false);
                efispSha256 = VerifyEfispStream(efispLease.Stream, efisp.Length, pinnedAsset, ct);

                boundA = ComputeGblTargetBound(ctxA.Material, ctxA.Material, ctxB.Material);
                boundB = ComputeGblTargetBound(ctxB.Material, ctxA.Material, ctxB.Material);
            }

            foreach (HeldLease held in leases)
            {
                VerifyClaim(held.Stream, held.Claim, "system update trust final", ct);
            }

            var trustA = new SystemUpdateSlotTrust(
                "a",
                ctxA.Material,
                boundA,
                ctxA.RootKeySha256,
                ctxA.Leaf.KeySha256,
                ctxA.Leaf.BlobSha256,
                ctxA.Leaf.DigestMatches,
                ablKeyA);

            var trustB = new SystemUpdateSlotTrust(
                "b",
                ctxB.Material,
                boundB,
                ctxB.RootKeySha256,
                ctxB.Leaf.KeySha256,
                ctxB.Leaf.BlobSha256,
                ctxB.Leaf.DigestMatches,
                ablKeyB);

            return new SystemUpdateTrust(
                model,
                mode,
                lockState,
                trustA,
                trustB,
                gblRegionText,
                efispSha256);
        }
        finally
        {
            foreach (HeldLease held in leases)
                CloseQuietly(held.Stream);
        }
    }

    private static void ValidateSlotFiles(SystemUpdateSlotFiles slotFiles, string paramName)
    {
        if (slotFiles is null) throw new ArgumentNullException(paramName);
        if (slotFiles.Boot is null) throw new ArgumentNullException(paramName, "Boot claim is required.");
        if (slotFiles.VendorBoot is null) throw new ArgumentNullException(paramName, "VendorBoot claim is required.");
        if (slotFiles.InitBoot is null) throw new ArgumentNullException(paramName, "InitBoot claim is required.");
        if (slotFiles.Abl is null) throw new ArgumentNullException(paramName, "Abl claim is required.");
        if (slotFiles.XblConfig is null) throw new ArgumentNullException(paramName, "XblConfig claim is required.");
        if (slotFiles.Vbmeta is null) throw new ArgumentNullException(paramName, "Vbmeta claim is required.");
        if (slotFiles.Recovery is null) throw new ArgumentNullException(paramName, "Recovery claim is required.");
        if (slotFiles.VbmetaSystem is null) throw new ArgumentNullException(paramName, "VbmetaSystem claim is required.");
    }

    private static async Task<SlotInspectionContext> InspectSingleSlotAsync(
        string slot,
        SystemUpdateSlotFiles files,
        List<HeldLease> leases,
        CancellationToken ct)
    {
        var bootLease = OpenClaimLease($"slot {slot} boot", files.Boot, ct);
        leases.Add(bootLease);
        var vendorLease = OpenClaimLease($"slot {slot} vendor_boot", files.VendorBoot, ct);
        leases.Add(vendorLease);
        var initLease = OpenClaimLease($"slot {slot} init_boot", files.InitBoot, ct);
        leases.Add(initLease);
        var ablLease = OpenClaimLease($"slot {slot} abl", files.Abl, ct);
        leases.Add(ablLease);
        var xblLease = OpenClaimLease($"slot {slot} xbl_config", files.XblConfig, ct);
        leases.Add(xblLease);
        var vbmetaLease = OpenClaimLease($"slot {slot} vbmeta", files.Vbmeta, ct);
        leases.Add(vbmetaLease);
        var recoveryLease = OpenClaimLease($"slot {slot} recovery", files.Recovery, ct);
        leases.Add(recoveryLease);
        var vbmetaSystemLease = OpenClaimLease($"slot {slot} vbmeta_system", files.VbmetaSystem, ct);
        leases.Add(vbmetaSystemLease);

        InternalSlotMaterial material = await InternalSlotMaterialReader.AnalyzeAsync(
            slot, files.Boot, files.VendorBoot, files.InitBoot, files.Abl, files.XblConfig, ct).ConfigureAwait(false);

        if (!material.IsKshim)
            throw new InvalidDataException($"Slot '{slot}' is not the required K shim layout.");
        if (material.PageSize != 4096)
            throw new InvalidDataException($"Slot '{slot}' boot page size must be 4096.");
        if (!material.LoaderBudget.KnownLoaderMatch || material.LoaderBudget.MaxRawBytes <= 0)
            throw new InvalidDataException($"Slot '{slot}' does not have a recognized positive loader budget.");

        vbmetaLease.Stream.Position = 0;
        AvbNodeMetadata rootMeta = AvbNodeReadMetadata(vbmetaLease.Stream, EdlRawAvbNodeKind.Vbmeta, files.Vbmeta.Length, ct);
        recoveryLease.Stream.Position = 0;
        AvbNodeMetadata recoveryMeta = AvbNodeReadMetadata(recoveryLease.Stream, EdlRawAvbNodeKind.Recovery, files.Recovery.Length, ct);
        vbmetaSystemLease.Stream.Position = 0;
        AvbNodeMetadata vbmetaSystemMeta = AvbNodeReadMetadata(vbmetaSystemLease.Stream, EdlRawAvbNodeKind.VbmetaSystem, files.VbmetaSystem.Length, ct);
        CurrentBootLeafFacts bootLeaf = CurrentBootReadLeafFacts(bootLease.Stream, files.Boot.Length, ct);

        string? expectedBootKey = null;
        string? expectedRecoveryKey = null;
        string? expectedVbmetaSystemKey = null;

        foreach (AvbNodeChainKey chain in rootMeta.ChainKeys)
        {
            byte[] keyBlob = AvbNodeSlice(rootMeta.Blob, chain.KeyOffset, chain.KeyLength);
            string keySha256 = AvbNodeSha256Hex(keyBlob);
            if (string.Equals(chain.Name, "boot", StringComparison.Ordinal))
                expectedBootKey = keySha256;
            else if (string.Equals(chain.Name, "recovery", StringComparison.Ordinal))
                expectedRecoveryKey = keySha256;
            else if (string.Equals(chain.Name, "vbmeta_system", StringComparison.Ordinal))
                expectedVbmetaSystemKey = keySha256;
            else
                throw new InvalidDataException($"Unexpected chain partition '{chain.Name}' in slot '{slot}' root vbmeta.");
        }

        if (expectedBootKey is null || expectedRecoveryKey is null || expectedVbmetaSystemKey is null)
            throw new InvalidDataException($"Slot '{slot}' root vbmeta is missing required chain descriptors.");

        if (!HashEquals(expectedBootKey, bootLeaf.KeySha256))
            throw new InvalidDataException($"Slot '{slot}' boot leaf key does not match its root vbmeta chain descriptor.");
        if (!HashEquals(expectedRecoveryKey, recoveryMeta.SourcePublicKeySha256))
            throw new InvalidDataException($"Slot '{slot}' recovery leaf key does not match its root vbmeta chain descriptor.");
        if (!HashEquals(expectedVbmetaSystemKey, vbmetaSystemMeta.SourcePublicKeySha256))
            throw new InvalidDataException($"Slot '{slot}' vbmeta_system key does not match its root vbmeta chain descriptor.");

        SystemUpdateEnvelopeMode mode =
            HashEquals(rootMeta.SourcePublicKeySha256, SigningPublicKeySha256) &&
            HashEquals(bootLeaf.KeySha256, SigningPublicKeySha256)
                ? SystemUpdateEnvelopeMode.AospResign
                : SystemUpdateEnvelopeMode.GblPreserveMetadata;

        return new SlotInspectionContext(
            material,
            rootMeta.SourcePublicKeySha256,
            bootLeaf,
            mode,
            vendorLease,
            ablLease,
            xblLease);
    }

    private static string VerifyEfispStream(FileStream stream, long expectedLength, byte[] pinnedPrefix, CancellationToken ct)
    {
        if (expectedLength < pinnedPrefix.Length || expectedLength > SystemUpdateMaxVendorOrEfispBytes || stream.Length != expectedLength)
            throw new InvalidDataException("The efisp partition length is outside the valid GBL asset bounds.");

        stream.Position = 0;
        byte[] prefix = new byte[pinnedPrefix.Length];
        ReadExactly(stream, prefix, 0, prefix.Length);
        ct.ThrowIfCancellationRequested();

        if (! prefix.AsSpan().SequenceEqual(pinnedPrefix))
            throw new InvalidDataException("The efisp prefix does not match the compiled GBL runtime asset for the slot region.");

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha256.AppendData(prefix);

        byte[] buffer = new byte[65536];
        long remaining = expectedLength - pinnedPrefix.Length;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of stream while reading efisp zero tail.");

            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != 0)
                    throw new InvalidDataException("The efisp tail contains non-zero bytes after the pinned GBL runtime asset.");
            }

            sha256.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(sha256.GetHashAndReset());
    }

    private static long ComputeGblTargetBound(
        InternalSlotMaterial target,
        InternalSlotMaterial slotA,
        InternalSlotMaterial slotB)
    {
        long boundFromA = ComputeSingleCrossBound(target, slotA);
        long boundFromB = ComputeSingleCrossBound(target, slotB);
        long min = Math.Min(boundFromA, boundFromB);
        if (min <= 0)
            throw new InvalidDataException("Computed cross-slot GBL loader raw budget is not positive.");
        return min;
    }

    private static long ComputeSingleCrossBound(InternalSlotMaterial target, InternalSlotMaterial m)
    {
        checked
        {
            long targetAligned = Align4096(target.TotalRamdiskSize);
            long mAligned = Align4096(m.TotalRamdiskSize);
            long delta = Math.Max(0L, targetAligned - mAligned);
            long adjusted = m.LoaderBudget.MaxRawBytes - delta;
            return Math.Min(target.LoaderBudget.MaxRawBytes, adjusted);
        }
    }

    private static long Align4096(long value)
    {
        if (value < 0)
            throw new InvalidDataException("Ramdisk size cannot be negative.");
        checked
        {
            return (value + 4095L) & ~4095L;
        }
    }

    private readonly record struct SlotInspectionContext(
        InternalSlotMaterial Material,
        string RootKeySha256,
        CurrentBootLeafFacts Leaf,
        SystemUpdateEnvelopeMode Mode,
        HeldLease VendorLease,
        HeldLease AblLease,
        HeldLease XblLease);
}
