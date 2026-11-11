using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using FreedomInstaller.Core;

namespace FreedomInstaller.App
{
    /// <summary>
    /// Reads the build-supplied bootstrap script and updater bundle from the
    /// application assembly. A normal source build may omit them; a single-file
    /// publish is rejected by the project target if either input is missing.
    /// The bundle hash is computed from the embedded bytes, never from the
    /// remote device.
    /// </summary>
    internal static class OfflineBootstrapAssets
    {
        internal const string ScriptResourceName = "FreedomInstaller.OfflineBootstrap.Script.py";
        internal const string BundleResourceName = "FreedomInstaller.OfflineBootstrap.Updater.tar.gz";

        private static string? _bundleSha256;

        internal static OfflineUpdateBootstrapCoordinator? TryCreate(
            IProgress<OfflineUpdateBootstrapProgress>? progress,
            bool useSudoForInstall)
        {
            var assembly = typeof(OfflineBootstrapAssets).Assembly;
            if (assembly.GetManifestResourceInfo(ScriptResourceName) is null ||
                assembly.GetManifestResourceInfo(BundleResourceName) is null)
            {
                return null;
            }

            var expected = _bundleSha256 ??= ComputeBundleSha256(assembly);
            return new OfflineUpdateBootstrapCoordinator(
                () => Open(assembly, ScriptResourceName),
                () => Open(assembly, BundleResourceName),
                expected,
                progress,
                useSudoForInstall);
        }

        private static Stream Open(Assembly assembly, string name) =>
            assembly.GetManifestResourceStream(name) ??
            throw new InvalidOperationException("The embedded offline bootstrap resource '" + name + "' is unavailable.");

        private static string ComputeBundleSha256(Assembly assembly)
        {
            using var stream = Open(assembly, BundleResourceName);
            using var hash = SHA256.Create();
            return Convert.ToHexStringLower(hash.ComputeHash(stream));
        }
    }
}
