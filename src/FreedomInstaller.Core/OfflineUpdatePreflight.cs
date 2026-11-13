using System;
using System.Text.Json;

namespace FreedomInstaller.Core;

/// <summary>
/// The one read-only preflight success contract.
/// <para>
/// A pass is a v1 response that explicitly reports the passed phase with no
/// boot write started. A legacy or malformed response, a device-side
/// <c>operation_failed</c>, or an older updater that cannot preflight is never
/// a silent pass. The installer UI and the opt-in SSH probe both read this
/// document through this type, so neither can drift into a looser check.
/// </para>
/// </summary>
public static class OfflineUpdatePreflight
{
    /// <summary>The only accepted <c>schemaVersion</c>.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The phase that proves every read-only check ran.</summary>
    public const string PassedPhase = "preflight_passed";

    /// <summary>The only <c>bootWriteState</c> that proves nothing was written.</summary>
    public const string NotStartedBootWrite = "not_started";

    /// <summary>
    /// True only when <paramref name="preflight"/> is a v1 object that reports
    /// <see cref="PassedPhase"/> and <see cref="NotStartedBootWrite"/>.
    /// </summary>
    public static bool Passed(JsonElement? preflight)
    {
        if (!preflight.HasValue || preflight.Value.ValueKind != JsonValueKind.Object)
            return false;

        JsonElement value = preflight.Value;
        return ReadInt32(value, "schemaVersion") == SchemaVersion &&
               string.Equals(ReadString(value, "phase"), PassedPhase, StringComparison.Ordinal) &&
               string.Equals(ReadString(value, "bootWriteState"), NotStartedBootWrite, StringComparison.Ordinal);
    }

    /// <summary>The reported <c>phase</c>, or null when it is absent or not a string.</summary>
    public static string? Phase(JsonElement? preflight) =>
        preflight.HasValue ? ReadString(preflight.Value, "phase") : null;

    /// <summary>
    /// The deployment reported by a preflight. The legacy <c>deployment</c>
    /// field never existed and must not be read.
    /// </summary>
    public static string? Deployment(JsonElement? preflight) =>
        preflight.HasValue ? ReadString(preflight.Value, "currentDeployment") : null;

    /// <summary>The reported <c>bootWriteState</c>, or null when it is absent or not a string.</summary>
    public static string? BootWriteState(JsonElement? preflight) =>
        preflight.HasValue ? ReadString(preflight.Value, "bootWriteState") : null;

    private static int? ReadInt32(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out int number)
            ? number
            : null;

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
