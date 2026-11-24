using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FreedomInstaller.Core;

/// <summary>
/// One reviewed, OEM-signed ABL whose effective AVB key is the AOSP signing key, used to replace a locked live
/// ABL that does not accept the AOSP-resigned chain. Each instance belongs to exactly one model and is an
/// optional build input pinned by both length and SHA-256: it is embedded byte-for-byte under a fixed logical
/// name and staged into the preparation's evidence directory only when a selected live ABL needs it. There is
/// no path, stream or file-name input and no download or generation at run time.
/// </summary>
internal sealed class PinnedAospAbl
{
    /// <summary>
    /// TB322FC: the 188 loader. It is also checked against the slot's XBL loader window before use.
    /// </summary>
    internal static PinnedAospAbl Tb322fc { get; } = new(
        model: "TB322FC",
        kind: "tb322fc-188",
        codePrefix: "tb322fc-abl188",
        label: "188",
        sourceFirmware: "TB322FC ZUXOS 1.1.11.188",
        imageLength: 1_048_576,
        imageSha256: "6C2DD7CA087C4481B9B3970E8251C4E09846ED85FE1A279EB8A2F7E637B3FF15",
        resourceName: "FreedomInstaller.Tb322fc.Abl188.img",
        stagedFileName: "tb322fc-abl-188.img",
        buildProperty: "BundledTb322fcAbl188",
        requiresXblWindow: true,
        isSupportedContainer: static (bytes, token) => InternalSlotLoaderStructure.TryDecodeAblForGbl(bytes, token, out _));

    /// <summary>
    /// TB710FU: the CN ZUI 17.0.04.279 loader as one whole 1 MiB abl partition (the same bytes the ZUXOS
    /// 1.5.04.495 no-unlock supplement writes). Lenovo's stock ZUXOS 1.5.04.495 ABL carries an AOSP key blob
    /// only as a secondary reference and verifies with Lenovo's own key, exactly like the TB322FC OEM loader.
    /// The SM8650 loader is not a GBL ARM64 container and has no XBL replacement window, so the evidence is the
    /// pinned bytes and their effective AVB key.
    /// </summary>
    internal static PinnedAospAbl Tb710fu { get; } = new(
        model: "TB710FU",
        kind: "tb710fu-279",
        codePrefix: "tb710fu-abl279",
        label: "279",
        sourceFirmware: "TB710FU ZUI 17.0.04.279 (ZUXOS 1.5.04.495 no-unlock supplement)",
        imageLength: 1_048_576,
        imageSha256: "6CFA16E65C2DADD0FC557DD356E6C383C693B4C3B7F0A651D92BB9F6E38B74A8",
        resourceName: "FreedomInstaller.Tb710fu.AospAbl.img",
        stagedFileName: "tb710fu-abl-279.img",
        buildProperty: "BundledTb710fuAospAbl",
        requiresXblWindow: false,
        isSupportedContainer: static (bytes, token) =>
            bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F'
            && BootMaintenanceAblRecovery.AblAcceptsAosp(bytes, token));

    private readonly Func<byte[], CancellationToken, bool> _isSupportedContainer;

    private PinnedAospAbl(
        string model,
        string kind,
        string codePrefix,
        string label,
        string sourceFirmware,
        int imageLength,
        string imageSha256,
        string resourceName,
        string stagedFileName,
        string buildProperty,
        bool requiresXblWindow,
        Func<byte[], CancellationToken, bool> isSupportedContainer)
    {
        Model = model;
        Kind = kind;
        CodePrefix = codePrefix;
        Label = label;
        SourceFirmware = sourceFirmware;
        ImageLength = imageLength;
        ImageSha256 = imageSha256;
        ResourceName = resourceName;
        StagedFileName = stagedFileName;
        BuildProperty = buildProperty;
        RequiresXblWindow = requiresXblWindow;
        _isSupportedContainer = isSupportedContainer;
    }

    /// <summary>The exact model this loader belongs to.</summary>
    internal string Model { get; }

    /// <summary>The confirmation record's compatibility kind.</summary>
    internal string Kind { get; }

    /// <summary>The prefix of every refusal code about this loader.</summary>
    internal string CodePrefix { get; }

    /// <summary>The short firmware label used in messages ("188", "279").</summary>
    internal string Label { get; }

    /// <summary>Provenance of the pinned bytes only. Selection never compares this with the device firmware.</summary>
    internal string SourceFirmware { get; }

    internal int ImageLength { get; }

    internal string ImageSha256 { get; }

    internal string ResourceName { get; }

    internal string StagedFileName { get; }

    /// <summary>The MSBuild property that embeds this loader into a build.</summary>
    internal string BuildProperty { get; }

    /// <summary>
    /// Whether the loader must also match the slot's current XBL loader window before it may replace the live
    /// ABL; the XBL configuration is then guarded as well.
    /// </summary>
    internal bool RequiresXblWindow { get; }

    /// <summary>The pinned loader of <paramref name="model"/>, or null for a model without one.</summary>
    internal static PinnedAospAbl? ForModel(string? model) => model switch
    {
        "TB322FC" => Tb322fc,
        "TB710FU" => Tb710fu,
        _ => null,
    };

    internal bool IsBundled =>
        typeof(PinnedAospAbl).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    internal string MissingMessage =>
        CodePrefix + "-missing: a selected " + Model + " ABL does not use the AOSP signing key as its effective key "
        + "and this build does not embed the AOSP-capable " + Label + " ABL.";

    internal void RequirePinnedCandidate(VerifiedEdlFile candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Length != ImageLength
            || !string.Equals(candidate.Sha256, ImageSha256, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(candidate.Path))
        {
            throw new InvalidOperationException(
                CodePrefix + "-invalid: the AOSP-capable ABL is not the pinned "
                + ImageLength + "-byte image.");
        }
    }

    internal async Task<VerifiedEdlFile> StageAsync(
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

        Assembly assembly = typeof(PinnedAospAbl).Assembly;
        using Stream resource = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                CodePrefix + "-missing: this build does not embed the pinned " + Model + " " + Label + " ABL. "
                + "A test build must pass " + BuildProperty + "; nothing is downloaded at run time.");

        if (resource.Length != ImageLength)
        {
            throw new InvalidOperationException(
                CodePrefix + "-invalid: the embedded ABL is " + resource.Length
                + " bytes; exactly " + ImageLength + " bytes are required.");
        }

        byte[] bytes = new byte[ImageLength];
        await resource.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

        string sha256 = Sha256Text.Hex(bytes).ToUpperInvariant();
        if (!string.Equals(sha256, ImageSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                CodePrefix + "-invalid: the embedded ABL SHA-256 is " + sha256
                + "; the pinned value is " + ImageSha256 + ".");
        }

        if (!_isSupportedContainer(bytes, cancellationToken))
        {
            throw new InvalidOperationException(
                CodePrefix + "-invalid: the pinned image is not a supported " + Model + " ABL container.");
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
                throw new InvalidDataException(CodePrefix + "-staging-failed: the staged ABL changed length.");
            }

            string readbackSha256 = Convert.ToHexString(SHA256.HashData(readback));
            if (!string.Equals(readbackSha256, ImageSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(CodePrefix + "-staging-failed: the staged ABL changed after writing.");
            }
        }

        return new VerifiedEdlFile(path, ImageLength, ImageSha256);
    }
}
