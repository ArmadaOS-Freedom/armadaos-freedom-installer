using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FreedomInstaller.Core.Gbl;

namespace FreedomInstaller.Core;

/// <summary>
/// The reviewed TB323FU 088 ABL, used as an efisp/GBL-capable loader rather than a firmware-version-specific
/// image. Pinned by length and SHA-256 and staged into the preparation's evidence directory only when a
/// selected live TB323FU ABL does not load efisp. No path, stream or file-name input and no download or
/// generation at run time.
/// </summary>
internal static class Tb323fuAbl088Assets
{
    internal const int ImageLength = 1_048_576;

    internal const string ImageSha256 =
        "B1504A04685D7FD46935E670734B7AAEB8AE6BE57BE5512AE69A185219FFB596";

    // Provenance of the pinned bytes only. Selection never compares this value with the device firmware.
    internal const string SourceFirmware = "TB323FU ZUXOS 2.0.12.088";

    private const string ResourceName = "FreedomInstaller.Tb323fu.Abl088.img";
    private const string StagedFileName = "tb323fu-abl-088.img";

    internal static bool IsBundled
    {
        get
        {
            Assembly assembly = typeof(Tb323fuAbl088Assets).Assembly;
            return assembly.GetManifestResourceInfo(ResourceName) is not null;
        }
    }

    internal static void RequirePinnedCandidate(VerifiedEdlFile candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Length != ImageLength
            || !string.Equals(candidate.Sha256, ImageSha256, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(candidate.Path))
        {
            throw new InvalidOperationException(
                "tb323fu-abl088-invalid: the GBL-capable ABL is not the pinned "
                + ImageLength + "-byte image.");
        }
    }

    internal static byte[] ReadVerifiedBytes(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Assembly assembly = typeof(Tb323fuAbl088Assets).Assembly;
        using Stream resource = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "tb323fu-abl088-missing: this build does not embed the pinned TB323FU 088 ABL. "
                + "Pass BundledTb323fuAbl088; nothing is downloaded at run time.");

        if (resource.Length != ImageLength)
        {
            throw new InvalidOperationException(
                "tb323fu-abl088-invalid: the embedded ABL is " + resource.Length
                + " bytes; exactly " + ImageLength + " bytes are required.");
        }

        byte[] bytes = new byte[ImageLength];
        int read = 0;
        while (read < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunk = resource.Read(bytes, read, bytes.Length - read);
            if (chunk <= 0)
            {
                throw new InvalidOperationException(
                    "tb323fu-abl088-invalid: the embedded ABL ended after " + read
                    + " bytes; exactly " + ImageLength + " bytes are required.");
            }

            read += chunk;
        }

        string sha256 = Sha256Text.Hex(bytes).ToUpperInvariant();
        if (!string.Equals(sha256, ImageSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "tb323fu-abl088-invalid: the embedded ABL SHA-256 is " + sha256
                + "; the pinned value is " + ImageSha256 + ".");
        }

        if (!InternalSlotLoaderStructure.TryDecodeAblForGbl(bytes, cancellationToken, out byte[] pe)
            || EfispLoadDetector.Detect(pe).Status != EfispLoad.Yes)
        {
            throw new InvalidOperationException(
                "tb323fu-abl088-invalid: the pinned image is not an efisp-loading TB323FU ABL container.");
        }

        return bytes;
    }

    internal static async Task<VerifiedEdlFile> StageAsync(
        string existingEvidenceDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(existingEvidenceDirectory))
        {
            throw new ArgumentException(
                "An existing evidence directory is required.", nameof(existingEvidenceDirectory));
        }

        string directory = Path.GetFullPath(existingEvidenceDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                "The evidence directory does not exist: '" + directory + "'.");
        }

        byte[] bytes = ReadVerifiedBytes(cancellationToken);

        string path = Path.Combine(directory, StagedFileName);
        bool created = false;
        try
        {
            using (FileStream output = new(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (created)
            {
                try
                {
                    File.Delete(path);
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

        using (FileStream readback = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
        {
            if (readback.Length != ImageLength)
            {
                throw new InvalidDataException("tb323fu-abl088-staging-failed: the staged ABL changed length.");
            }

            string readbackSha256 = Convert.ToHexString(SHA256.HashData(readback));
            if (!string.Equals(readbackSha256, ImageSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("tb323fu-abl088-staging-failed: the staged ABL changed after writing.");
            }
        }

        return new VerifiedEdlFile(path, ImageLength, ImageSha256);
    }
}
