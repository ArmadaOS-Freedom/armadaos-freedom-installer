using System.Windows;
using FreedomInstaller.Core;

namespace FreedomInstaller.App;

public partial class MainWindow
{
    private const string AboutQqGroupNumber = "487945399";
    private const string QqGroupInviteUrl = "https://qm.qq.com/q/hKBry3C7Cg";

    private bool _showingSupport;

    private void OnShowSupport(object sender, RoutedEventArgs e)
    {
        if (_busy || _showingDiagnostics)
        {
            return;
        }

        _showingSupport = true;

        AboutVersionText.Text = InstallerDisplayVersion.ForAssembly(typeof(MainWindow).Assembly);
        AboutJoinQqButton.ToolTip = QqGroupInviteUrl;

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

    private void OnOpenCodyGithub(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.AboutCodyName", "https://github.com/corebear6010");

    private void OnOpenCodyBilibili(object sender, RoutedEventArgs e)
        => OpenExternalLink(BilibiliLabelKey, "https://space.bilibili.com/68995");

    private void OnOpenLichWangBilibili(object sender, RoutedEventArgs e)
        => OpenExternalLink(BilibiliLabelKey, "https://space.bilibili.com/170842457");

    private void OnOpenSunflowerGithub(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.Link.AboutSunflowerName", "https://github.com/sunflower2333");

    private void OnOpenBilibiliThanks(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.Link.AboutBilibiliThanks", "https://space.bilibili.com/509773383");

    private void OnOpenQqGroupInvite(object sender, RoutedEventArgs e)
        => OpenExternalLink("Dynamic.AboutJoinQq", QqGroupInviteUrl);
}
