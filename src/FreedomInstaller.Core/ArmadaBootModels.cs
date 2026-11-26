using System;
using System.Collections.Generic;

namespace FreedomInstaller.Core;

/// <summary>
/// The models with the ARMADA_BOOT disk boot (<c>bootRecipe.linuxBootSource = "armada-boot-v1"</c>): the launcher
/// loads the signed Linux kernel, initramfs and board DTB from ARMADA_BOOT, and updates switch a signed boot
/// generation there instead of rewriting Android Boot. The list and the board ids follow Freedom's capability
/// table (system_files/usr/lib/armada/updates/models.py: <c>disk_boot</c>, <c>DISK_BOOT_BOARDS</c>) and the
/// kShimC board table (<c>/armada/boot/&lt;model&gt;/manifest.bin</c>).
/// <para>
/// TB710FU only has this path: it never takes the raw mirrored Boot update (no
/// <see cref="LinuxRuntimeContract.RequiresMirrorMarker"/>), so its offline upgrade is accepted only from a
/// device that reports <c>bootSource=armada_boot</c>.
/// </para>
/// </summary>
public static class ArmadaBootModels
{
    private static readonly Dictionary<string, uint> BoardIds = new(StringComparer.Ordinal)
    {
        ["TB322FC"] = 1,
        ["TB323FU"] = 2,
        ["TB710FU"] = 3,
    };

    private static readonly HashSet<string> ArmadaBootOnly = new(StringComparer.Ordinal)
    {
        "TB710FU",
    };

    /// <summary>True when the exact canonical <paramref name="model"/> has the ARMADA_BOOT disk boot.</summary>
    public static bool HasDiskBoot(string? model) => model is not null && BoardIds.ContainsKey(model);

    /// <summary>
    /// True when the exact canonical <paramref name="model"/> updates only through ARMADA_BOOT and has no raw
    /// Android Boot update path.
    /// </summary>
    public static bool IsArmadaBootOnly(string? model) => model is not null && ArmadaBootOnly.Contains(model);

    /// <summary>The signed manifest board id of a disk-boot model.</summary>
    public static uint BoardId(string model) =>
        model is not null && BoardIds.TryGetValue(model, out uint board)
            ? board
            : throw new InvalidDataException("unsupported Armada board '" + model + "'");

    /// <summary>The ARMADA_BOOT directory holding a disk-boot model's manifest, signature and board DTB.</summary>
    public static string BootRoot(string model) => "/armada/boot/" + ModelOrThrow(model);

    /// <summary>
    /// True when <paramref name="model"/> may take an offline upgrade from a device that reports
    /// <paramref name="armadaBootSource"/>: an ARMADA_BOOT-only model needs the disk boot source, every other
    /// offline-upgrade model accepts either path.
    /// </summary>
    public static bool OfflineUpgradePathAllowed(string? model, bool armadaBootSource) =>
        armadaBootSource ? HasDiskBoot(model) : !IsArmadaBootOnly(model);

    private static string ModelOrThrow(string model)
    {
        _ = BoardId(model);
        return model;
    }
}
