using System.Reflection;

namespace FreedomInstaller.Core;

/// <summary>
/// Pure, offline version helpers for the installer front ends. Numeric comparison still uses the
/// AssemblyVersion/FileVersion exactly as before; this type only derives the human display string
/// and the full commit SHA from an assembly's AssemblyInformationalVersion attribute.
/// Core itself remains SDK opt-out for IncludeSourceRevisionInInformationalVersion; this helper
/// merely reads whatever value is present on the given assembly.
/// </summary>
public static class InstallerDisplayVersion
{
    /// <summary>
    /// Title / About text: X.Y.Z, or X.Y.Z+abcdef0 when the informational version carries a
    /// validated source commit suffix (built-in .NET SDK SourceRevisionId).
    /// </summary>
    public static string ForAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var numeric = assembly.GetName().Version?.ToString(3) ?? string.Empty;
        var commit = TryGetCommitForAssembly(assembly);
        return commit is null ? numeric : numeric + "+" + commit[..Math.Min(7, commit.Length)];
    }

    /// <summary>
    /// Full validated source commit SHA (40 or 64 ASCII hex chars) from the informational version,
    /// or null. Never returns the short display hash.
    /// </summary>
    public static string? TryGetCommitForAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
        {
            return null;
        }

        int metaIndex = info.IndexOf('+');
        if (metaIndex < 0 || metaIndex == info.Length - 1)
        {
            return null;
        }

        string meta = info[(metaIndex + 1)..];
        int dotIndex = meta.LastIndexOf('.');
        string finalSegment = dotIndex < 0 ? meta : meta[(dotIndex + 1)..];

        return IsFullCommitSha(finalSegment) ? finalSegment.ToLowerInvariant() : null;
    }

    private static bool IsFullCommitSha(string value)
    {
        if (value.Length != 40 && value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
