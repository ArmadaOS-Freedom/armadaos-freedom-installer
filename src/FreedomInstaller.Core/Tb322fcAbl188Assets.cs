using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

/// <summary>
/// The reviewed TB322FC 188 ABL, used as an AOSP-capable loader rather than a firmware-version-specific
/// image. This optional test-build asset is pinned by both length and SHA-256 and is staged into the
/// preparation's evidence directory only when a selected live ABL does not use the AOSP signing key as
/// its effective (primary) key. There is no path, stream or file-name input and no download or generation
/// at run time.
/// </summary>
internal static class Tb322fcAbl188Assets
{
    internal const int ImageLength = 1_048_576;

    internal const string ImageSha256 =
        "6C2DD7CA087C4481B9B3970E8251C4E09846ED85FE1A279EB8A2F7E637B3FF15";

    // Provenance of the pinned bytes only. Selection never compares this value with the device firmware.
    internal const string SourceFirmware = "TB322FC ZUXOS 1.1.11.188";

    private const string ResourceName = "FreedomInstaller.Tb322fc.Abl188.img";
    private const string StagedFileName = "tb322fc-abl-188.img";

    internal static bool IsBundled
    {
        get
        {
            Assembly assembly = typeof(Tb322fcAbl188Assets).Assembly;
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
                "tb322fc-abl188-invalid: the AOSP-capable ABL is not the pinned "
                + ImageLength + "-byte image.");
        }
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

        cancellationToken.ThrowIfCancellationRequested();

        Assembly assembly = typeof(Tb322fcAbl188Assets).Assembly;
        using Stream resource = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "tb322fc-abl188-missing: this build does not embed the pinned TB322FC 188 ABL. "
                + "A test build must pass BundledTb322fcAbl188; nothing is downloaded at run time.");

        if (resource.Length != ImageLength)
        {
            throw new InvalidOperationException(
                "tb322fc-abl188-invalid: the embedded ABL is " + resource.Length
                + " bytes; exactly " + ImageLength + " bytes are required.");
        }

        byte[] bytes = new byte[ImageLength];
        await resource.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

        string sha256 = Sha256Text.Hex(bytes).ToUpperInvariant();
        if (!string.Equals(sha256, ImageSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "tb322fc-abl188-invalid: the embedded ABL SHA-256 is " + sha256
                + "; the pinned value is " + ImageSha256 + ".");
        }

        if (!InternalSlotLoaderStructure.TryDecodeAblForGbl(bytes, cancellationToken, out _))
        {
            throw new InvalidOperationException(
                "tb322fc-abl188-invalid: the pinned image is not a supported TB322FC ABL container.");
        }

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
                throw new InvalidDataException("tb322fc-abl188-staging-failed: the staged ABL changed length.");
            }

            string readbackSha256 = Convert.ToHexString(SHA256.HashData(readback));
            if (!string.Equals(readbackSha256, ImageSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("tb322fc-abl188-staging-failed: the staged ABL changed after writing.");
            }
        }

        return new VerifiedEdlFile(path, ImageLength, ImageSha256);
    }
}
