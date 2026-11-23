using System;
using System.Reflection;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

/// <summary>
/// Matches the current app assembly's private direct Android-erase opt-in against the connected device.
/// <para>
/// The opt-in is the paired AssemblyMetadata pair <c>DirectAndroidEraseModel</c> and
/// <c>DirectAndroidEraseBuildFingerprint</c> embedded only when the internal MSBuild property pair is supplied.
/// Both attributes must be present and non-blank, the model must match exactly with ordinal comparison and the
/// complete build fingerprint must match exactly with ordinal comparison. There is no wildcard, no environmental
/// override and no default-on behavior: an absent or blank value rejects the match. This type only reads metadata
/// of its own assembly; it touches no device and performs no I/O.
/// </para>
/// </summary>
public static class AndroidDataErasePreview
{
    private const string ModelAttributeName = "DirectAndroidEraseModel";
    private const string BuildFingerprintAttributeName = "DirectAndroidEraseBuildFingerprint";

    /// <summary>
    /// Returns true only when the app assembly carries both paired opt-in attributes and the supplied model and
    /// complete build fingerprint match them exactly. A null or blank model or firmware fingerprint rejects.
    /// </summary>
    /// <param name="model">The connected device model, exactly as read from Android.</param>
    /// <param name="firmware">The connected device firmware snapshot, or null when absent.</param>
    public static bool Matches(string? model, FirmwareSnapshot? firmware)
    {
        if (string.IsNullOrWhiteSpace(model) || firmware is null)
        {
            return false;
        }

        string? buildFingerprint = firmware.BuildFingerprint;
        if (string.IsNullOrWhiteSpace(buildFingerprint))
        {
            return false;
        }

        string? allowedModel = ReadOwnAssemblyMetadata(ModelAttributeName);
        string? allowedFingerprint = ReadOwnAssemblyMetadata(BuildFingerprintAttributeName);

        if (string.IsNullOrWhiteSpace(allowedModel) || string.IsNullOrWhiteSpace(allowedFingerprint))
        {
            return false;
        }

        return string.Equals(model, allowedModel, StringComparison.Ordinal)
            && string.Equals(buildFingerprint, allowedFingerprint, StringComparison.Ordinal);
    }

    private static string? ReadOwnAssemblyMetadata(string name)
    {
        Assembly assembly = typeof(AndroidDataErasePreview).Assembly;

        foreach (AssemblyMetadataAttribute attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, name, StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }
}
