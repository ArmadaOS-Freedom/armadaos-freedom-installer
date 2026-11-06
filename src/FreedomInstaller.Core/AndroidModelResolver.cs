using System;
using System.Collections.Generic;
using System.Linq;

namespace FreedomInstaller.Core;

/// <summary>Android properties nominate a model; they never prove hardware identity.</summary>
public static class AndroidModelResolver
{
    public const string ConflictCode = "MODEL_EVIDENCE_CONFLICT";
    public const string NotIdentifiedCode = "MODEL_NOT_IDENTIFIED";

    private static readonly HashSet<string> PreferredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ro.product.bootimage.model",
        "ro.product.model_for_attestation",
        "ro.product.device_for_attestation",
        "ro.bootimage.build.fingerprint",
    };

    private static readonly HashSet<string> SurfaceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ro.product.model", "ro.product.vendor.model", "ro.product.name",
    };

    public static string? Resolve(string dump)
    {
        var claims = ReadClaims(dump).Where(p => PreferredKeys.Contains(p.Key)).ToArray();
        if (claims.Length == 0)
            return DeviceProbe.IdentifyModel(dump);

        var models = claims.Select(p => CanonicalHint(p.Value)).Distinct().ToArray();
        return models.Length == 1 ? models[0] : null;
    }

    public static string Describe(string dump)
    {
        var claims = ReadClaims(dump).ToArray();
        var preferred = claims.Where(p => PreferredKeys.Contains(p.Key))
            .Select(p => CanonicalHint(p.Value) ?? p.Value.ToUpperInvariant())
            .Distinct().ToArray();
        var status = preferred.Length > 1 ? ConflictCode : Resolve(dump) ?? NotIdentifiedCode;
        return status + " keys[" + string.Join(", ", claims.Select(p => p.Key + "=" +
            new string(p.Value.Where(c => !char.IsControl(c)).Take(160).ToArray()))) + "]";
    }

    private static IEnumerable<(string Key, string Value)> ReadClaims(string dump)
    {
        foreach (var line in (dump ?? string.Empty).Split('\n'))
        {
            if (!DeviceProbe.TryReadProperty(line, out var key, out var value)
                || (!PreferredKeys.Contains(key) && !SurfaceKeys.Contains(key)))
                continue;

            if (key.Equals("ro.bootimage.build.fingerprint", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Split('/');
                if (parts.Length < 3 || !parts[0].Equals("Lenovo", StringComparison.OrdinalIgnoreCase))
                    continue;
                value = parts[2].Split(':')[0];
            }

            value = value.Trim();
            if (value.Length > 0)
                yield return (key, value);
        }
    }

    private static string? CanonicalHint(string value) => DeviceProbe.CanonicalModel(value)
        ?? (value.ToLowerInvariant() switch
        {
            "elden" or "elden_prc_wifi" => "TB322FC",
            "baldur" or "baldur_prc_wifi" => "TB323FU",
            "topaz" or "topaz_prc_wifi" => "TB710FU",
            _ => null,
        });
}
