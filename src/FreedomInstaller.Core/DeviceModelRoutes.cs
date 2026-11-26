using System;

namespace FreedomInstaller.Core;

/// <summary>
/// The one fail-closed write gate for a model that is registered for identification, its 9008 tool profile
/// and read-only validation, but whose uninstall, global restore and TF card write routes are not open yet.
/// <para>
/// Today that is exactly <c>TB710FU</c> (Lenovo Xiaoxin Pad Pro GT, SM8650): its bootloader stays locked. Its
/// install route (<see cref="IsInstallRoutePending"/>) is open on the AOSP-preserving boot-only chain. Its boot
/// maintenance route has its own gate (<see cref="IsMaintenanceRoutePending"/>), implemented but held closed until
/// it is accepted on a device; uninstall, global restore and the TF card install stay behind
/// <see cref="IsWriteRoutePending"/>. The gate never throws for TB322FC or TB323FU, so their behaviour is unchanged.
/// </para>
/// <para>
/// Two layers use it. The App entry points check <see cref="IsWriteRoutePending"/> on the identified model and
/// stop before entering 9008. The Core write back ends (install, install backup, boot maintenance, uninstall,
/// global restore) call <see cref="RequireWriteRoute"/> before their first device write as a backstop; some of
/// them only receive a client that is already in 9008, so that layer does not by itself keep the tablet out of
/// 9008. The historical-identity read refuses a TB710FU backup the same way.
/// </para>
/// </summary>
public static class DeviceModelRoutes
{
    /// <summary>The canonical model whose write routes are still pending.</summary>
    public const string Tb710fuModel = "TB710FU";

    /// <summary>Stable code carried at the start of the refusal message.</summary>
    public const string RoutePendingCode = "MODEL_ROUTE_PENDING";

    /// <summary>
    /// True when <paramref name="model"/> names a model whose write routes are not open. The comparison
    /// ignores case only, so every casing of TB710FU is held back; nothing is trimmed or prefix-matched.
    /// </summary>
    public static bool IsWriteRoutePending(string? model) =>
        string.Equals(model, Tb710fuModel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Throws before any write work when <paramref name="model"/> names a model whose write routes are not
    /// open. Never throws for any other value; unknown models are left to the existing exact model checks.
    /// </summary>
    /// <exception cref="InvalidOperationException">The model's write route is pending.</exception>
    public static void RequireWriteRoute(string? model)
    {
        if (IsWriteRoutePending(model))
        {
            throw new InvalidOperationException(
                RoutePendingCode
                + ": TB710FU 的这项写入路线（引导维护、卸载、全局恢复或 TF 卡安装）尚未开放，本次不会写入设备。");
        }
    }

    /// <summary>
    /// True when the internal install route (device check, install backup, direct install) of
    /// <paramref name="model"/> is not open. The TB710FU install route is open: on the no-unlock 495 chain it
    /// writes only boot and the new Linux partitions and keeps the device's AVB metadata (see
    /// <c>DirectTrustMode.AospPreserved</c>); on Lenovo's stock 495 chain it re-signs the AVB metadata and
    /// replaces the ABL with the pinned AOSP-capable 279 loader (see <see cref="PinnedAospAbl.Tb710fu"/>).
    /// Uninstall, global restore and the TF card install stay behind <see cref="IsWriteRoutePending"/>.
    /// </summary>
    public static bool IsInstallRoutePending(string? model) => false;

    /// <summary>
    /// True when the boot maintenance route (pre-update preparation, post-update repair and their historical
    /// identity read) of <paramref name="model"/> is not open. The TB710FU route is implemented on the
    /// AOSP-preserving boot-only chain: the project candidates are rebuilt per slot from the selected package's
    /// launcher, only boot_a/boot_b are re-signed and written, and the original root vbmeta, vbmeta_system and
    /// recovery stay untouched (<see cref="BootMaintenanceBootTrust.AospPreserved"/>); it never uses GBL. It stays
    /// held back until it is accepted on a device, so opening it is a change of this one predicate.
    /// </summary>
    public static bool IsMaintenanceRoutePending(string? model) =>
        string.Equals(model, Tb710fuModel, StringComparison.OrdinalIgnoreCase);

    /// <summary>Maintenance-route counterpart of <see cref="RequireWriteRoute"/>.</summary>
    public static void RequireMaintenanceRoute(string? model)
    {
        if (IsMaintenanceRoutePending(model))
        {
            RequireWriteRoute(model);
        }
    }

    /// <summary>Install-route counterpart of <see cref="RequireWriteRoute"/>.</summary>
    public static void RequireInstallRoute(string? model)
    {
        if (IsInstallRoutePending(model))
        {
            RequireWriteRoute(model);
        }
    }

    /// <summary>True when <paramref name="exception"/> is the refusal thrown by <see cref="RequireWriteRoute"/>.</summary>
    public static bool IsRouteRefusal(Exception? exception) =>
        exception is InvalidOperationException
        && exception.Message.StartsWith(RoutePendingCode + ":", StringComparison.Ordinal);
}
