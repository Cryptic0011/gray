using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace Gmux.App.Controls;

public sealed partial class BrowserPane : UserControl
{
    public BrowserPane()
    {
        InitializeComponent();
        Loaded += BrowserPane_Loaded;
    }

    private async void BrowserPane_Loaded(object sender, RoutedEventArgs e)
    {
        await WebView.EnsureCoreWebView2Async();
        WebView.CoreWebView2.NavigationCompleted += (s, args) =>
        {
            AddressBar.Text = WebView.Source?.ToString() ?? string.Empty;
        };
    }

    public void Navigate(string url)
    {
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;

        WebView.Source = new Uri(url);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CanGoBack) WebView.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebView.CanGoForward) WebView.GoForward();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        WebView.Reload();
    }

    private void AddressBar_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            Navigate(AddressBar.Text);
            e.Handled = true;
        }
    }
}
