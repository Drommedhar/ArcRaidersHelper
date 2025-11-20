using OverlayApp.Infrastructure;
using OverlayApp.ViewModels;
using System.Windows;

namespace OverlayApp;

public partial class SettingsWindow : Window
{
    private bool _suppressAutoCaptureWarning = true;

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
        _suppressAutoCaptureWarning = false;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.ApplyChanges(out var updated))
        {
            MessageBox.Show(this, LocalizationService.Instance["Settings_InvalidHotkeys"], LocalizationService.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void OnAutoCaptureChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoCaptureWarning)
        {
            return;
        }

        MessageBox.Show(this,
            LocalizationService.Instance["Settings_AutoCaptureWarning"],
            LocalizationService.Instance["Settings_AutoCaptureWarningTitle"],
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
