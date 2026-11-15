// Narrow opt-in gating for tests whose fixture inputs are external: this
// repository neither provides them nor generates them during public test-input
// preparation.  That is a statement about provenance, not about availability:
// official stock firmware is publicly obtainable, it is simply not checked in
// here and not produced by the public preparation step.
//
// A test decorated with one of these attributes is skipped only while EVERY
// variable of its fixture family is unset or blank.  As soon as the caller
// provides any member of the family, the test runs and the existing fixture
// validators decide the outcome: an incomplete, malformed or mismatched input
// still fails, and is never converted into a skip.
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace FreedomInstaller.Core.Tests;

/// <summary>
/// Environment variable names that form the external fixture families.  They
/// are constants so the attributes below can name them directly.
/// </summary>
internal static class RequiredFixtureVariables
{
    internal const string G5PublicAblPath = "G5_PUBLIC_ABL_PATH";

    internal const string G5LoaderFixtureRoot = "G5_LOADER_FIXTURE_ROOT";

    internal const string EdlClientLoaderLogPath = "EDL_CLIENT_LOADER_LOG_PATH";

    internal const string EdlClientMiscLogPath = "EDL_CLIENT_MISC_LOG_PATH";

    internal const string MaintenanceTestAssetRoot = "MAINTENANCE_TEST_ASSET_ROOT";

    internal const string TfNativePackagePath = "TF_NATIVE_PACKAGE_PATH";

    internal const string InternalInstallMaintenanceBoot = "INTERNAL_INSTALL_MAINTENANCE_BOOT";

    internal const string InternalInstallSupportArchive = "INTERNAL_INSTALL_SUPPORT_ARCHIVE";

    internal const string InternalInstallToolbox = "INTERNAL_INSTALL_TOOLBOX";
}

/// <summary>A <see cref="FactAttribute"/> that skips only when its whole fixture family is absent.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RequiredFixturesFactAttribute : FactAttribute
{
    public RequiredFixturesFactAttribute(params string[] variables)
    {
        Skip = RequiredFixtureGate.AbsentMessage(variables);
    }
}

/// <summary>A <see cref="TheoryAttribute"/> with the same absent-family rule.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RequiredFixturesTheoryAttribute : TheoryAttribute
{
    public RequiredFixturesTheoryAttribute(params string[] variables)
    {
        Skip = RequiredFixtureGate.AbsentMessage(variables);
    }
}

internal static class RequiredFixtureGate
{
    /// <summary>
    /// Returns the skip reason when every named variable is unset or blank, and
    /// null when at least one member of the family was provided.  A provided but
    /// unusable value therefore reaches the test and fails its own validator.
    /// </summary>
    internal static string? AbsentMessage(IReadOnlyList<string> variables)
    {
        if (variables is null || variables.Count == 0)
        {
            throw new ArgumentException(
                "a required-fixture attribute needs at least one environment variable",
                nameof(variables));
        }

        if (variables.Any(IsProvided))
        {
            return null;
        }

        return "SKIPPED (not a pass): external fixture input is absent; none of "
            + string.Join(", ", variables)
            + " is set. Providing any member of this family runs the test and "
            + "fails on incomplete or malformed input.";
    }

    private static bool IsProvided(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
}
