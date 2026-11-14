using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core
{
    /// <summary>
    /// Raw (pre-envelope) system-update payload construction for TB322FC / TB323FU / TB710FU.
    /// A trusted caller supplies verified target deployment files, a same-slot base image and a
    /// measured byte budget, plus installed trusted native tool executables (tool path claims alone
    /// are not code provenance; the caller attests to installation provenance). This stage builds
    /// and round-trip verifies the raw payload ONLY: it does not sign, does not write to any device,
    /// and does not authorize activation. PrepareSystemUpdateEnvelopeAsync plus physical trust
    /// checks are a separate, later stage. All three model payloads can be built; building a TB710FU
    /// payload does NOT enable TB710FU physical A/B.
    /// </summary>
    public sealed record SystemUpdateRawRequest(
        string Model,
        VerifiedEdlFile BaseImage,
        VerifiedEdlFile Kernel,
        VerifiedEdlFile Initramfs,
        VerifiedEdlFile Runtime,
        VerifiedEdlFile DtbNormal,
        VerifiedEdlFile DtbDiagnostic,
        string NormalBootArgs,
        string DiagnosticBootArgs,
        long MaxRawBytes,
        string OutputDirectory,
        VerifiedEdlFile Packer,
        VerifiedEdlFile Unpacker);

    public sealed record SystemUpdateRawBoot(
        string Model,
        VerifiedEdlFile PackedRaw,
        VerifiedEdlFile LinuxPadded,
        EdlRawDtbEvidence NormalDtb,
        EdlRawDtbEvidence DiagnosticDtb,
        long RawBudgetBytes);

    public static partial class EdlRawBootBuilder
    {
        private const long MaxRawPayloadBytes = 268435456L;

        public static async Task<SystemUpdateRawBoot> BuildSystemUpdateRawAsync(
            SystemUpdateRawRequest request,
            ICommandRunner runner,
            CancellationToken ct = default)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (runner is null) throw new ArgumentNullException(nameof(runner));

            if (request.Model != "TB322FC" && request.Model != "TB323FU" && request.Model != "TB710FU")
                throw new ArgumentException($"Unsupported model '{request.Model}'.", nameof(request));

            if (request.MaxRawBytes <= 0 || request.MaxRawBytes > MaxRawPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(request), "MaxRawBytes must be positive and <= 268435456.");

            ValidateClaimShape(request.BaseImage, "BaseImage", request.MaxRawBytes);
            ValidateClaimShape(request.Kernel, "Kernel", request.MaxRawBytes);
            ValidateClaimShape(request.Initramfs, "Initramfs", request.MaxRawBytes);
            ValidateClaimShape(request.Runtime, "Runtime", RuntimePayloadBytes);
            if (request.Runtime.Length != RuntimePayloadBytes)
                throw new ArgumentException($"Runtime length must be exactly {RuntimePayloadBytes} bytes.", nameof(request));
            ValidateClaimShape(request.DtbNormal, "DtbNormal", DtbPayloadLimitBytes);
            ValidateClaimShape(request.DtbDiagnostic, "DtbDiagnostic", DtbPayloadLimitBytes);
            ValidateClaimShape(request.Packer, "Packer", MaxRawPayloadBytes);
            ValidateClaimShape(request.Unpacker, "Unpacker", MaxRawPayloadBytes);

            if (string.IsNullOrWhiteSpace(request.Packer.Path) || string.IsNullOrWhiteSpace(request.Unpacker.Path) ||
                request.Packer.Path.IndexOf('\0') >= 0 || request.Unpacker.Path.IndexOf('\0') >= 0 ||
                !Path.IsPathFullyQualified(request.Packer.Path) || !Path.IsPathFullyQualified(request.Unpacker.Path))
                throw new ArgumentException("Tool paths must be fully qualified without NUL characters.", nameof(request));

            string outputDir = ValidateRawOutputDirectory(request.OutputDirectory);

            if (IsSubpathOrEqual(outputDir, Path.GetFullPath(request.Packer.Path)) ||
                IsSubpathOrEqual(outputDir, Path.GetFullPath(request.Unpacker.Path)))
                throw new ArgumentException("Tool executables must not reside inside the output subtree.", nameof(request));

            var leases = new List<HeldLease>(8);
            HeldLease Hold(string role, VerifiedEdlFile claim)
            {
                var lease = OpenClaimLease(role, claim, ct);
                leases.Add(lease);
                return lease;
            }

            void StageCopy(HeldLease lease, string path)
            {
                var (bytes, sha) = CopyHeldToNewFile(lease.Stream, path, lease.Role, ct);
                if (bytes != lease.Claim.Length || !HashEquals(sha, lease.Claim.Sha256))
                    throw new InvalidOperationException($"Staged {lease.Role} does not match claim.");
            }

            try
            {
                // Hold read leases on all eight claims; regular-file checks happen in OpenClaimLease.
                var baseLease = Hold("BaseImage", request.BaseImage);
                var kernelLease = Hold("Kernel", request.Kernel);
                var initramfsLease = Hold("Initramfs", request.Initramfs);
                var runtimeLease = Hold("Runtime", request.Runtime);
                // Only the new-system mirrored policy models require the marker;
                // refuse an older runtime before any native packing or output.
                LinuxRuntimeContract.RequireMirrorMarker(
                    runtimeLease.Stream, request.Model, "Runtime", ct);
                var dtbNormalLease = Hold("DtbNormal", request.DtbNormal);
                var dtbDiagnosticLease = Hold("DtbDiagnostic", request.DtbDiagnostic);
                var packerLease = Hold("Packer", request.Packer);
                var unpackerLease = Hold("Unpacker", request.Unpacker);

                // Headers and DTB evidence are validated before any subprocess is executed.
                var baseHeader = ReadHeader(baseLease);
                var kernelHeader = ReadHeader(kernelLease);
                RequireArm64Header(baseHeader, "BaseImage");
                RequireArm64Header(kernelHeader, "Kernel");

                ulong kernelImageSize = ReadLeU64(kernelHeader, Arm64ImageSizeField);
                if (kernelImageSize > (ulong)request.MaxRawBytes)
                    throw new InvalidOperationException("Kernel image_size exceeds raw budget.");
                ulong kernelTextOffset = ReadLeU64(kernelHeader, Arm64TextOffsetField);
                ulong baseTextOffset = ReadLeU64(baseHeader, Arm64TextOffsetField);
                if ((kernelTextOffset & 0x1fffffUL) != (baseTextOffset & 0x1fffffUL))
                    throw new InvalidOperationException("Kernel text_offset low 21 bits do not match base image.");
                long linuxTarget = (long)kernelImageSize;
                if (linuxTarget < kernelLease.Claim.Length) linuxTarget = kernelLease.Claim.Length;
                if (linuxTarget > request.MaxRawBytes)
                    throw new InvalidOperationException("Padded Linux target exceeds raw budget.");

                ct.ThrowIfCancellationRequested();
                if (OperatingSystem.IsLinux())
                    Directory.CreateDirectory(outputDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else
                    Directory.CreateDirectory(outputDir);

                string basePath = Path.Combine(outputDir, BaseImageFileName);
                string runtimePath = Path.Combine(outputDir, RuntimeFileName);
                string initramfsPath = Path.Combine(outputDir, InitramfsFileName);

                StageCopy(baseLease, basePath);
                StageCopy(runtimeLease, runtimePath);
                StageCopy(initramfsLease, initramfsPath);

                string linuxPath = Path.Combine(outputDir, LinuxPaddedFileName);
                string linuxSha;
                using (var stream = CreateNew(linuxPath, "LinuxPadded"))
                {
                    kernelLease.Stream.Position = 0;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    CopyTo(kernelLease.Stream, stream, hash, ct);
                    WriteZeros(stream, linuxTarget - kernelLease.Claim.Length, hash, ct);
                    stream.Flush(true);
                    linuxSha = ToHex(hash.GetHashAndReset());
                }

                if (new FileInfo(linuxPath).Length != linuxTarget)
                    throw new InvalidOperationException("Padded Linux length mismatch.");
                byte[] paddedHeader = new byte[Arm64HeaderProbeBytes];
                using (var check = OpenRead(linuxPath, "LinuxPadded"))
                {
                    ReadExactly(check, paddedHeader, 0, paddedHeader.Length);
                    for (int i = 0; i < paddedHeader.Length; i++)
                        if (paddedHeader[i] != kernelHeader[i])
                            throw new InvalidOperationException("Padded Linux header mutated.");
                }

                dtbNormalLease.Stream.Position = 0;
                var normalDtb = StageDtb(dtbNormalLease, request.NormalBootArgs, EdlRawDtbKind.Normal, request.Model,
                    Path.Combine(outputDir, NormalDtbFileName), ct);
                dtbDiagnosticLease.Stream.Position = 0;
                var diagnosticDtb = StageDtb(dtbDiagnosticLease, request.DiagnosticBootArgs, EdlRawDtbKind.Diagnostic, request.Model,
                    Path.Combine(outputDir, DiagnosticDtbFileName), ct);

                WriteNewFile(Path.Combine(outputDir, BuildConfigFileName),
                    ConfigUtf8.GetBytes(BuildConfigText(request.Model)), "BuildConfig", ct);

                await RunNativeAsync(runner, packerLease.Claim.Path,
                    new[] { BuildConfigFileName }, outputDir, "system update pack", ct);

                PackedRawFacts facts;
                using (var raw = OpenRead(Path.Combine(outputDir, PackedRawFileName), "PackedRaw"))
                    facts = InspectPackedRaw(raw, baseHeader, request.MaxRawBytes, ct);

                string unpackedDir = Path.Combine(outputDir, UnpackedDirectoryName);
                await RunNativeAsync(runner, unpackerLease.Claim.Path,
                    new[] { PackedRawFileName, UnpackedDirectoryName }, outputDir, "system update unpack", ct);

                VerifyUnpackedOutput(unpackedDir, basePath, linuxPath, runtimePath,
                    Path.Combine(outputDir, NormalDtbFileName), Path.Combine(outputDir, DiagnosticDtbFileName),
                    initramfsPath, request.Model, ct);

                await RunNativeAsync(runner, packerLease.Claim.Path,
                    new[] { UnpackedConfigFileName }, unpackedDir, "system update repack", ct);
                RequireIdenticalFile(Path.Combine(outputDir, PackedRawFileName),
                    Path.Combine(unpackedDir, RepackedRawFileName), "RepackedRaw", ct);

                // Detect mutation of any retained input during native tool execution.
                VerifyClaim(baseLease.Stream, baseLease.Claim, "BaseImage", ct);
                VerifyClaim(kernelLease.Stream, kernelLease.Claim, "Kernel", ct);
                VerifyClaim(initramfsLease.Stream, initramfsLease.Claim, "Initramfs", ct);
                VerifyClaim(runtimeLease.Stream, runtimeLease.Claim, "Runtime", ct);
                VerifyClaim(dtbNormalLease.Stream, dtbNormalLease.Claim, "DtbNormal", ct);
                VerifyClaim(dtbDiagnosticLease.Stream, dtbDiagnosticLease.Claim, "DtbDiagnostic", ct);
                VerifyClaim(packerLease.Stream, packerLease.Claim, "Packer", ct);
                VerifyClaim(unpackerLease.Stream, unpackerLease.Claim, "Unpacker", ct);

                if (!HashEquals(HashFileSha256(basePath, ct), baseLease.Claim.Sha256))
                    throw new InvalidOperationException("Staged base image mutated.");
                if (!HashEquals(HashFileSha256(linuxPath, ct), linuxSha))
                    throw new InvalidOperationException("Staged padded Linux mutated.");
                if (!HashEquals(HashFileSha256(runtimePath, ct), runtimeLease.Claim.Sha256))
                    throw new InvalidOperationException("Staged runtime mutated.");
                if (!HashEquals(HashFileSha256(Path.Combine(outputDir, NormalDtbFileName), ct), normalDtb.Sha256))
                    throw new InvalidOperationException("Staged normal DTB mutated.");
                if (!HashEquals(HashFileSha256(Path.Combine(outputDir, DiagnosticDtbFileName), ct), diagnosticDtb.Sha256))
                    throw new InvalidOperationException("Staged diagnostic DTB mutated.");
                if (!HashEquals(HashFileSha256(initramfsPath, ct), initramfsLease.Claim.Sha256))
                    throw new InvalidOperationException("Staged initramfs mutated.");

                string packedPath = Path.Combine(outputDir, PackedRawFileName);
                var packedVerified = new VerifiedEdlFile(packedPath, facts.Bytes, facts.Sha256);
                using (var packedCheck = OpenRead(packedPath, "PackedRaw"))
                {
                    if (packedCheck.Length != facts.Bytes)
                        throw new InvalidOperationException("Packed raw length mutated after round trip.");
                    VerifyClaim(packedCheck, packedVerified, "PackedRaw", ct);
                }

                return new SystemUpdateRawBoot(
                    request.Model,
                    packedVerified,
                    new VerifiedEdlFile(linuxPath, linuxTarget, linuxSha),
                    normalDtb,
                    diagnosticDtb,
                    request.MaxRawBytes);
            }
            finally
            {
                foreach (var lease in leases)
                    CloseQuietly(lease.Stream);
            }
        }

        private static byte[] ReadHeader(HeldLease lease)
        {
            var header = new byte[Arm64HeaderProbeBytes];
            lease.Stream.Position = 0;
            ReadExactly(lease.Stream, header, 0, header.Length);
            lease.Stream.Position = 0;
            return header;
        }

        private static void ValidateClaimShape(VerifiedEdlFile claim, string role, long limit)
        {
            if (claim is null) throw new ArgumentException($"{role} claim is required.");
            if (claim.Length <= 0 || claim.Length > limit)
                throw new ArgumentException($"{role} length {claim.Length} out of range (1..{limit}).");
        }

        private static string ValidateRawOutputDirectory(string requested)
        {
            if (string.IsNullOrWhiteSpace(requested) || requested.IndexOf('\0') >= 0)
                throw new ArgumentException("OutputDirectory required without NUL characters.");
            if (!Path.IsPathFullyQualified(requested))
                throw new ArgumentException("OutputDirectory must be fully qualified.");
            string full = Path.GetFullPath(requested);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(full, requested, cmp))
                throw new ArgumentException("OutputDirectory must be canonical without trailing separators or relative segments.");
            if (File.Exists(full) || Directory.Exists(full) ||
                new FileInfo(full).LinkTarget is not null || new DirectoryInfo(full).LinkTarget is not null)
                throw new InvalidOperationException("Output directory must not exist; no overwrite.");
            string? parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("Output parent directory must exist.");
            var parentInfo = new DirectoryInfo(parent);
            if (!parentInfo.Attributes.HasFlag(FileAttributes.Directory) ||
                (parentInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                parentInfo.LinkTarget is not null)
                throw new InvalidOperationException("Output parent must be a regular directory without reparse points.");
            for (var di = parentInfo; di is not null; di = di.Parent)
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget is not null)
                    throw new InvalidOperationException("Reparse point in output ancestors.");
            return full;
        }

        private static bool IsSubpathOrEqual(string root, string candidate)
        {
            string r = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string c = candidate.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return c.StartsWith(r, cmp);
        }
    }
}
