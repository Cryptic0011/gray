using Gmux.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gmux.App.Controls;

public sealed partial class UpdateBanner : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(UpdateBannerViewModel),
            typeof(UpdateBanner),
            new PropertyMetadata(null, OnViewModelChanged));

    public UpdateBannerViewModel? ViewModel
    {
        get => (UpdateBannerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public UpdateBanner()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var banner = (UpdateBanner)d;
        if (e.OldValue is UpdateBannerViewModel old)
            old.PropertyChanged -= banner.OnViewModelPropertyChanged;
        if (e.NewValue is UpdateBannerViewModel newVm)
        {
            newVm.PropertyChanged += banner.OnViewModelPropertyChanged;
            banner.RefreshFromViewModel();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateBannerViewModel.State))
            RefreshFromViewModel();
    }

    private void RefreshFromViewModel()
    {
        if (ViewModel is null) return;
        switch (ViewModel.State)
        {
            case UpdateBannerState.Hidden:
                Bar.IsOpen = false;
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                break;
            case UpdateBannerState.Available:
                Bar.IsOpen = true;
                Bar.Severity = InfoBarSeverity.Informational;
                PrimaryButton.Content = "Install";
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                break;
            case UpdateBannerState.Downloading:
                Bar.IsOpen = true;
                Bar.Severity = InfoBarSeverity.Informational;
                PrimaryButton.Content = "Cancel";
                DownloadProgressBar.Visibility = Visibility.Visible;
                break;
            case UpdateBannerState.ReadyToInstall:
                Bar.IsOpen = true;
                Bar.Severity = InfoBarSeverity.Informational;
                PrimaryButton.Content = "…";
                PrimaryButton.IsEnabled = false;
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                break;
            case UpdateBannerState.Error:
                Bar.IsOpen = true;
                Bar.Severity = InfoBarSeverity.Error;
                PrimaryButton.Content = ViewModel.CanViewLog ? "View log" : "Retry";
                PrimaryButton.IsEnabled = true;
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        switch (ViewModel.State)
        {
            case UpdateBannerState.Available:
                await ViewModel.InstallCommand.ExecuteAsync(null);
                break;
            case UpdateBannerState.Downloading:
                ViewModel.CancelDownloadCommand.Execute(null);
                break;
            case UpdateBannerState.Error:
                if (ViewModel.CanViewLog)
                    ViewModel.ViewLogCommand.Execute(null);
                else
                    await ViewModel.RetryCommand.ExecuteAsync(null);
                break;
        }
    }

    private void OnCloseClicked(InfoBar sender, object args)
    {
        ViewModel?.LaterCommand.Execute(null);
    }
}
