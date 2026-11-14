using System;
using System.Text.Json;

namespace FreedomInstaller.Core;

/// <summary>
/// The one boot policy under which a mutating offline upgrade may run.
/// <para>
/// Both physical A/B boot candidates launch the same Linux deployment. The
/// installer and the on-device updater build, upload and write those two
/// candidates only: they never write slot metadata, never select a slot and
/// never mark a slot successful. Android alone owns the active, selected and
/// successful slot.
/// </para>
/// <para>
/// The device advertises this exact string as <c>bootPolicy</c> in its
/// <c>info</c> document. A helper that omits it, reports another value or
/// reports a non-string value may still control Android's slots, so every
/// mutating step is refused before any write request is sent. Read-only
/// information, preflight, status and recovery export do not depend on it.
/// </para>
/// </summary>
public static class OfflineUpdateBootPolicy
{
    /// <summary>The exact <c>bootPolicy</c> string required for a mutating offline upgrade.</summary>
    public const string NoSlotControl = "dual-boot-no-slot-control";

    /// <summary>
    /// True only when <paramref name="info"/> is an object that advertises the
    /// exact <see cref="NoSlotControl"/> string. Any other value kind, an
    /// absent property or a different string is unsupported.
    /// </summary>
    public static bool IsNoSlotControl(JsonElement info) =>
        info.ValueKind == JsonValueKind.Object &&
        info.TryGetProperty("bootPolicy", out JsonElement policy) &&
        policy.ValueKind == JsonValueKind.String &&
        string.Equals(policy.GetString(), NoSlotControl, StringComparison.Ordinal);
}
