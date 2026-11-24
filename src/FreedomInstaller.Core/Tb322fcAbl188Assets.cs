using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

/// <summary>
/// The reviewed TB322FC 188 ABL, used as an AOSP-capable loader rather than a firmware-version-specific
/// image. This optional test-build asset is pinned by both length and SHA-256 and is staged into the
/// preparation's evidence directory only when a selected live ABL does not use the AOSP signing key as
/// its effective (primary) key. There is no path, stream or file-name input and no download or generation
/// at run time. The pinned facts live in <see cref="PinnedAospAbl.Tb322fc"/>.
/// </summary>
internal static class Tb322fcAbl188Assets
{
    internal const int ImageLength = 1_048_576;

    internal const string ImageSha256 =
        "6C2DD7CA087C4481B9B3970E8251C4E09846ED85FE1A279EB8A2F7E637B3FF15";

    // Provenance of the pinned bytes only. Selection never compares this value with the device firmware.
    internal const string SourceFirmware = "TB322FC ZUXOS 1.1.11.188";

    internal static bool IsBundled => PinnedAospAbl.Tb322fc.IsBundled;

    internal static void RequirePinnedCandidate(VerifiedEdlFile candidate) =>
        PinnedAospAbl.Tb322fc.RequirePinnedCandidate(candidate);

    internal static Task<VerifiedEdlFile> StageAsync(
        string existingEvidenceDirectory,
        CancellationToken cancellationToken) =>
        PinnedAospAbl.Tb322fc.StageAsync(existingEvidenceDirectory, cancellationToken);
}
