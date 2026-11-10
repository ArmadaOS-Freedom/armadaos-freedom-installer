using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

public enum SystemUpdateEnvelopeMode
{
    AospResign,
    GblPreserveMetadata
}

public sealed record SystemUpdateBootEnvelope(
    VerifiedEdlFile Candidate,
    SystemUpdateEnvelopeMode Mode,
    InternalSlotMaterial SourceMaterial,
    long RawBudgetBytes,
    string RootPublicKeySha256,
    string BootPublicKeySha256,
    string MetadataSha256,
    bool PayloadDigestMatchesSignedMetadata);

public static partial class EdlRawBootBuilder
{
    /// <summary>
    /// Prepares and verifies a file-only boot envelope from measured same-slot material.
    /// This is not write authorization or evidence of hardware acceptance. Before any
    /// device write, the caller must separately prove the physical slot, all ancillary
    /// trust, the GBL loader and efisp, the common window, OTA and model constraints,
    /// and the actual new payload recipe. No OEM private signature or complete-chain
    /// claim is created here.
    /// </summary>
    public static async Task<SystemUpdateBootEnvelope> PrepareSystemUpdateEnvelopeAsync(
        string slot,
        VerifiedEdlFile boot,
        VerifiedEdlFile vendorBoot,
        VerifiedEdlFile initBoot,
        VerifiedEdlFile abl,
        VerifiedEdlFile xblConfig,
        VerifiedEdlFile vbmeta,
        VerifiedEdlFile packedRaw,
        long maxRawBytes,
        string outputPath,
        SystemUpdateEnvelopeMode mode,
        CancellationToken ct = default)
    {
        if (boot is null) throw new ArgumentNullException(nameof(boot));
        if (vendorBoot is null) throw new ArgumentNullException(nameof(vendorBoot));
        if (initBoot is null) throw new ArgumentNullException(nameof(initBoot));
        if (abl is null) throw new ArgumentNullException(nameof(abl));
        if (xblConfig is null) throw new ArgumentNullException(nameof(xblConfig));
        if (vbmeta is null) throw new ArgumentNullException(nameof(vbmeta));
        if (packedRaw is null) throw new ArgumentNullException(nameof(packedRaw));
        if (!string.Equals(slot, "a", StringComparison.Ordinal) &&
            !string.Equals(slot, "b", StringComparison.Ordinal))
            throw new ArgumentException("Slot must be exactly 'a' or 'b'.", nameof(slot));
        if (mode != SystemUpdateEnvelopeMode.AospResign &&
            mode != SystemUpdateEnvelopeMode.GblPreserveMetadata)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (maxRawBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRawBytes));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An output path is required.", nameof(outputPath));
        if (outputPath.IndexOf('\0') >= 0)
            throw new ArgumentException("The output path contains a NUL character.", nameof(outputPath));
        if (!Path.IsPathFullyQualified(outputPath))
            throw new ArgumentException("The output path must be absolute.", nameof(outputPath));

        ct.ThrowIfCancellationRequested();
        outputPath = Path.GetFullPath(outputPath);
        NormalizeParent(Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The output path must have a parent directory.", nameof(outputPath)));
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new IOException("The candidate output already exists; its bytes will not be replaced.");

        var leases = new List<FileStream>();
        FileStream? candidateStream = null;
        bool ownCandidateCreated = false;

        try
        {
            var bootLease = OpenClaimLease("system update source boot", boot, ct);
            leases.Add(bootLease.Stream);
            var vendorLease = OpenClaimLease("system update source vendor boot", vendorBoot, ct);
            leases.Add(vendorLease.Stream);
            var initLease = OpenClaimLease("system update source init boot", initBoot, ct);
            leases.Add(initLease.Stream);
            var ablLease = OpenClaimLease("system update source abl", abl, ct);
            leases.Add(ablLease.Stream);
            var xblLease = OpenClaimLease("system update source xbl config", xblConfig, ct);
            leases.Add(xblLease.Stream);
            var vbmetaLease = OpenClaimLease("system update source vbmeta", vbmeta, ct);
            leases.Add(vbmetaLease.Stream);
            var packedLease = OpenClaimLease("system update packed raw payload", packedRaw, ct);
            leases.Add(packedLease.Stream);

            InternalSlotMaterial sourceMaterial = await InternalSlotMaterialReader.AnalyzeAsync(
                slot, boot, vendorBoot, initBoot, abl, xblConfig, ct).ConfigureAwait(false);
            if (!sourceMaterial.IsKshim)
                throw new InvalidDataException("The source slot is not the required K shim; repair installation is separate.");
            if (maxRawBytes > sourceMaterial.LoaderBudget.MaxRawBytes)
                throw new InvalidDataException("The requested raw budget exceeds the measured own-slot loader budget.");

            EdlRawOriginalBootChainFacts root =
                await AuthenticateOriginalBootChainAsync(vbmeta, ct).ConfigureAwait(false);
            CurrentBootLeafFacts sourceLeaf =
                CurrentBootReadLeafFacts(bootLease.Stream, boot.Length, ct);
            if (!HashEquals(root.BootChainPublicKeySha256, sourceLeaf.KeySha256))
                throw new InvalidDataException("The authenticated root chain does not authenticate the source boot leaf key.");

            if (mode == SystemUpdateEnvelopeMode.AospResign)
            {
                if (!HashEquals(root.PublicKeySha256, SigningPublicKeySha256) ||
                    !HashEquals(sourceLeaf.KeySha256, SigningPublicKeySha256))
                    throw new InvalidDataException("The AOSP route requires the root, child, and source leaf keys to match the pinned signing key.");
                if (!sourceLeaf.DigestMatches)
                    throw new InvalidDataException("The AOSP route requires the source boot payload digest to match its signed metadata.");
            }

            if (mode == SystemUpdateEnvelopeMode.GblPreserveMetadata &&
                HashEquals(root.PublicKeySha256, SigningPublicKeySha256) &&
                HashEquals(sourceLeaf.KeySha256, SigningPublicKeySha256))
                throw new InvalidDataException("An AOSP-signed source must use the AOSP resign route.");

            VerifiedEdlFile candidate;
            if (mode == SystemUpdateEnvelopeMode.AospResign)
            {
                EdlRawBootEnvelope repacked = await RepackEnvelopeAsync(
                    boot, packedRaw, maxRawBytes, outputPath, ct).ConfigureAwait(false);
                candidate = repacked.CandidateBoot;
            }
            else
            {
                CurrentBootGblPreserved preserved = await RepackCurrentGblEnvelopeAsync(
                    boot, packedRaw, maxRawBytes, outputPath, ct).ConfigureAwait(false);
                candidate = preserved.Candidate;
            }
            ownCandidateCreated = true;

            if (candidate.Length != boot.Length)
                throw new InvalidDataException("The candidate length does not exactly equal the source boot partition length.");

            var candidateLease = OpenClaimLease("system update candidate", candidate, ct);
            candidateStream = candidateLease.Stream;
            leases.Add(candidateStream);
            VerifyClaim(candidateStream, candidate, "system update candidate", ct);

            InternalSlotMaterial candidateMaterial = await InternalSlotMaterialReader.AnalyzeAsync(
                slot, candidate, vendorBoot, initBoot, abl, xblConfig, ct).ConfigureAwait(false);
            if (!candidateMaterial.IsKshim)
                throw new InvalidDataException("The candidate no longer has the required own-slot K shim layout.");
            if (candidateMaterial.BaseImageLength != sourceMaterial.BaseImageLength ||
                candidateMaterial.BaseImageSize != sourceMaterial.BaseImageSize ||
                candidateMaterial.BaseTextOffset != sourceMaterial.BaseTextOffset ||
                !HashEquals(candidateMaterial.BaseImageSha256, sourceMaterial.BaseImageSha256))
                throw new InvalidDataException("The candidate changed the measured same-slot base kernel identity.");

            CurrentBootLeafFacts candidateLeaf =
                CurrentBootReadLeafFacts(candidateStream, candidate.Length, ct);
            if (mode == SystemUpdateEnvelopeMode.AospResign)
            {
                if (!candidateLeaf.DigestMatches ||
                    !HashEquals(candidateLeaf.KeySha256, SigningPublicKeySha256))
                    throw new InvalidDataException("The AOSP candidate does not match the pinned leaf key and signed payload digest.");
            }
            else
            {
                if (!HashEquals(candidateLeaf.BlobSha256, sourceLeaf.BlobSha256) ||
                    !HashEquals(candidateLeaf.KeySha256, sourceLeaf.KeySha256))
                    throw new InvalidDataException("The GBL candidate did not preserve the original signed boot blob and leaf key.");
            }

            // Rehash every retained source only after all repacking and candidate reads.
            VerifyClaim(bootLease.Stream, boot, "system update source boot final", ct);
            VerifyClaim(vendorLease.Stream, vendorBoot, "system update source vendor boot final", ct);
            VerifyClaim(initLease.Stream, initBoot, "system update source init boot final", ct);
            VerifyClaim(ablLease.Stream, abl, "system update source abl final", ct);
            VerifyClaim(xblLease.Stream, xblConfig, "system update source xbl config final", ct);
            VerifyClaim(vbmetaLease.Stream, vbmeta, "system update source vbmeta final", ct);
            VerifyClaim(packedLease.Stream, packedRaw, "system update packed raw payload final", ct);
            VerifyClaim(candidateStream, candidate, "system update candidate final", ct);

            return new SystemUpdateBootEnvelope(
                candidate,
                mode,
                sourceMaterial,
                maxRawBytes,
                root.PublicKeySha256,
                candidateLeaf.KeySha256,
                candidateLeaf.BlobSha256,
                candidateLeaf.DigestMatches);
        }
        catch (Exception)
        {
            if (ownCandidateCreated)
            {
                try
                {
                    if (candidateStream is not null)
                        CloseQuietly(candidateStream);
                    if (File.Exists(outputPath))
                        File.Delete(outputPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            throw;
        }
        finally
        {
            foreach (FileStream stream in leases)
                CloseQuietly(stream);
        }
    }

    private static bool HashEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
