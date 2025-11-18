using OverlayApp.Infrastructure;
using OverlayApp.ViewModels;
using System.Windows;

namespace OverlayApp;

public partial class SettingsWindow : Window
{
    private bool _hideAdsPromptReady;

    public SettingsWindow(UserSettings draft)
    {
        InitializeComponent();
        ViewModel = new SettingsViewModel(draft);
        DataContext = ViewModel;
    }

    public SettingsViewModel ViewModel { get; }

    public UserSettings? ResultSettings { get; private set; }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _hideAdsPromptReady = true;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.ApplyChanges(out var updated))
        {
            MessageBox.Show(this, "Please fix invalid hotkeys or URL before applying.", "ArcTracker Overlay", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultSettings = updated;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnHideAdsChecked(object sender, RoutedEventArgs e)
    {
        if (!_hideAdsPromptReady)
        {
            return;
        }

        var result = MessageBox.Show(this,
            "ArcTracker relies on advertising to continue operating. Disabling ads may hurt the site experience. Do you want to continue?",
            "ArcTracker Overlay",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            HideAdsCheckBox.IsChecked = false;
            e.Handled = true;
        }
    }
}
