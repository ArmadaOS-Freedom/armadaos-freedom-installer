using System;
using System.IO;
using System.Reflection;
using System.Text;
using Xunit;

namespace FreedomInstaller.Core.Tests;

public sealed class SourceWiringTests
{
    private static string ReadEmbedded(string logicalName)
    {
        Assembly asm = typeof(SourceWiringTests).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(logicalName);
        Assert.True(stream is not null, $"Missing embedded resource {logicalName}");
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string SliceMethod(string source, string signature, string nextSignature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method signature: {signature}");
        int end = source.IndexOf(nextSignature, start + signature.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing next method signature: {nextSignature}");
        return source.Substring(start, end - start);
    }

    [Fact]
    public void RefreshActions_DoesNotGateSupportButtonsOnBusy()
    {
        string source = ReadEmbedded("FreedomInstaller.Tests.MainWindow.xaml.cs");

        Assert.DoesNotContain("SupportProjectButton.IsEnabled = !_busy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportBackButton.IsEnabled = !_busy", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportHandlers_UseOnlySupportVisibilityGuardsAndApplyFunctionView()
    {
        string support = ReadEmbedded("FreedomInstaller.Tests.MainWindow.Support.cs");

        string show = SliceMethod(support, "private void OnShowSupport(", "private void OnCloseSupport(");
        Assert.Contains("_closed || _showingSupport", show, StringComparison.Ordinal);
        Assert.DoesNotContain("_busy", show, StringComparison.Ordinal);
        Assert.Contains("_showingSupport = true;", show, StringComparison.Ordinal);
        Assert.Contains("ApplyFunctionView();", show, StringComparison.Ordinal);

        string close = SliceMethod(support, "private void OnCloseSupport(", "private void OnOpenAboutRepository(");
        Assert.DoesNotContain("_busy", close, StringComparison.Ordinal);
        Assert.Contains("_showingSupport = false;", close, StringComparison.Ordinal);
        Assert.Contains("ApplyFunctionView();", close, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_PrefersSupportPresentationWithoutClearingFlagsAndRestoresSupport()
    {
        string views = ReadEmbedded("FreedomInstaller.Tests.MainWindow.Functions.cs");

        Assert.Contains(
            "DiagnosticExportPage.Visibility = (_showingDiagnostics && !_showingSupport) ? Visibility.Visible : Visibility.Collapsed;",
            views, StringComparison.Ordinal);

        string diagnostics = ReadEmbedded("FreedomInstaller.Tests.MainWindow.Diagnostics.cs");

        string export = SliceMethod(diagnostics, "private void OnExportDiagnostics(", "private void ShowDiagnosticPage(");
        Assert.Contains("_returnToSupportAfterDiagnostics = _showingSupport;", export, StringComparison.Ordinal);
        Assert.Contains("_showingSupport = false;", export, StringComparison.Ordinal);
        Assert.Contains("ShowDiagnosticPage();", export, StringComparison.Ordinal);

        int captureIdx = export.IndexOf("_returnToSupportAfterDiagnostics = _showingSupport;", StringComparison.Ordinal);
        int clearIdx = captureIdx >= 0 ? export.IndexOf("_showingSupport = false;", captureIdx + "_returnToSupportAfterDiagnostics = _showingSupport;".Length, StringComparison.Ordinal) : -1;
        int showIdx = export.IndexOf("ShowDiagnosticPage();", StringComparison.Ordinal);
        Assert.True(captureIdx >= 0 && clearIdx > captureIdx && showIdx > clearIdx,
            "Expected capture, then clear, then ShowDiagnosticPage();");
        Assert.DoesNotContain("_showingDiagnostics = true;", export, StringComparison.Ordinal);

        string closeDiag = SliceMethod(diagnostics, "private void OnCloseDiagnosticPage(", "private void OnChooseDiagnosticDestination(");
        Assert.Contains("if (_diagnosticExporting) return;", closeDiag, StringComparison.Ordinal);
        Assert.Contains("_showingDiagnostics = false;", closeDiag, StringComparison.Ordinal);
        Assert.Contains("_showingSupport = _returnToSupportAfterDiagnostics;", closeDiag, StringComparison.Ordinal);
    }
}
