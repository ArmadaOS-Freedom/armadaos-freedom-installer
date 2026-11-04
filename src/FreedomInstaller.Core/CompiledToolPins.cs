namespace FreedomInstaller.Core;

// Build-time frozen pins for bundled native tools. Overridable via BundledToolPinsSource
// (MSBuild property) so freshly compiled tools can pass the existing runtime guards.
internal static class CompiledToolPins
{
    internal const long ClientBytes = 12015022L;
    internal const string ClientSha256 =
        "655530e926b57d70f1c690869c495253a6eaae28746f370d5b9dbf921d4a4b3b";

    internal const long PackerBytes = 277460L;
    internal const string PackerSha256 =
        "a2f03fc6b0e5d1b8defb60dd2a880bd5933c5133613778b17690172570ac20bb";

    internal const long UnpackerBytes = 262703L;
    internal const string UnpackerSha256 =
        "24d4788646282d23665b61b28460808480420a0a72e45afca6ab1a167b4b656f";
}
