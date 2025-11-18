using OverlayApp.Infrastructure;
using OverlayApp.ViewModels;
using System.Windows;

namespace OverlayApp;

public partial class SettingsWindow : Window
{
    public SettingsWindow(UserSettings draft)
    {
        InitializeComponent();
        ViewModel = new SettingsViewModel(draft);
        DataContext = ViewModel;
    }

    public SettingsViewModel ViewModel { get; }

    public UserSettings? ResultSettings { get; private set; }

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
}
