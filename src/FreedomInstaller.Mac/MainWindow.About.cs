using Avalonia.Interactivity;

namespace FreedomInstaller.Mac;

/// <summary>
/// The About page, mirroring the WPF window's MainWindow.Support.cs: the same links, the same QQ group and the
/// shared strings and images. maintenance/tests/test_mac_about_parity.py fails when the two pages drift apart.
/// </summary>
public partial class MainWindow
{
    private const string AboutQqGroupNumber = "487945399";

    private Task<bool> OpenLinkAsync(string url) => Launcher.LaunchUriAsync(new Uri(url));

    private async void OnOpenAboutRepository(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync("https://github.com/mydanyi/armadaos-freedom");

    private async void OnOpenLichWangGithub(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync("https://github.com/lich-wang");

    private async void OnOpenLichWangBilibili(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync("https://space.bilibili.com/170842457");

    private async void OnOpenH0CheungGithub(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync("https://github.com/h0cheung");

    private async void OnOpenH0CheungBilibili(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync("https://space.bilibili.com/33282562");

    private async void OnOpenSunflowerGithub(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync("https://github.com/sunflower2333");

    private async void OnOpenBilibiliThanks(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync("https://space.bilibili.com/509773383");

    private async void OnCopyQqGroup(object? sender, RoutedEventArgs e)
    {
        var copied = false;
        if (Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(AboutQqGroupNumber);
                copied = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                copied = false;
            }
        }

        SetText(AboutCopyStatus, copied ? "Dynamic.AboutCopySucceeded" : "Dynamic.AboutCopyFailed");
        AboutCopyStatus.IsVisible = true;
    }
}
