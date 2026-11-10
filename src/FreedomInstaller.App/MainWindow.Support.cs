using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace FreedomInstaller.App;

public partial class MainWindow
{
    private const string AboutQqGroupNumber = "487945399";

    private bool _showingSupport;

    private void OnShowSupport(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _showingSupport = true;

        AboutVersionText.Text = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
        AboutCopyStatus.Visibility = Visibility.Collapsed;
        AboutCopyStatus.ClearValue(TextBlock.TextProperty);

        ApplyFunctionView();
    }

    private void OnCloseSupport(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _showingSupport = false;
        ApplyFunctionView();
    }

    private void OnOpenAboutRepository(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.Link.AboutRepository", "https://github.com/ArmadaOS-Freedom/armadaos-freedom");

    private void OnOpenLichWangGithub(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.Link.AboutLichWangName", "https://github.com/lich-wang");

    private void OnOpenH0CheungGithub(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.Link.AboutH0CheungName", "https://github.com/h0cheung");

    private void OnOpenH0CheungBilibili(object sender, RoutedEventArgs e)
        => OpenExternalLink(BilibiliLabelKey, "https://space.bilibili.com/33282562");

    private void OnOpenLichWangBilibili(object sender, RoutedEventArgs e)
        => OpenExternalLink(BilibiliLabelKey, "https://space.bilibili.com/170842457");

    private void OnOpenSunflowerGithub(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.Link.AboutSunflowerName", "https://github.com/sunflower2333");

    private void OnOpenBilibiliThanks(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.Link.AboutBilibiliThanks", "https://space.bilibili.com/509773383");

    private void OnCopyQqGroup(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AboutQqGroupNumber);
        }
        catch (ExternalException)
        {
            AboutCopyStatus.SetResourceReference(TextBlock.TextProperty, "Dynamic.AboutCopyFailed");
            AboutCopyStatus.Visibility = Visibility.Visible;
            return;
        }

        AboutCopyStatus.SetResourceReference(TextBlock.TextProperty, "Dynamic.AboutCopySucceeded");
        AboutCopyStatus.Visibility = Visibility.Visible;
    }
}
