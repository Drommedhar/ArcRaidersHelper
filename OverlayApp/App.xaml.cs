using OverlayApp.Infrastructure;
using System;
using System.Windows;
using System.Windows.Threading;

namespace OverlayApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ILogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _logger = LoggerFactory.CreateDefaultLogger();
        
        // Setup global exception handling
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        
        _logger.Log("App", "Application started.");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Log("Crash", $"Unhandled UI exception: {e.Exception}");
        e.Handled = true; // Prevent immediate crash if possible, though state might be corrupt
        MessageBox.Show($"An unexpected error occurred: {e.Exception.Message}", "ArcRaidersHelper Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _logger?.Log("Crash", $"Fatal domain exception: {ex}");
            MessageBox.Show($"A fatal error occurred: {ex.Message}", "ArcRaidersHelper Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Log("App", "Application exiting.");
        base.OnExit(e);
    }
}

