using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MBDInspector;

public partial class App : Application
{
    private const string AppTitle = "MBD Inspector";

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
    }

    private void App_Startup(object sender, StartupEventArgs e)
    {
        RuntimeLog.Info($"Startup args: {string.Join(" ", e.Args)}");
        try
        {
            RuntimeLog.Info("Creating main window.");
            var window = new MainWindow();
            window.Show();
            RuntimeLog.Info("Main window shown.");

            if (e.Args.Length > 0)
            {
                string path = Path.GetFullPath(e.Args[0]);
                RuntimeLog.Info($"Opening startup file: {path}");
                window.OpenFile(path);
            }
        }
        catch (Exception ex)
        {
            HandleFatalException("Startup failed.", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleFatalException("An unhandled UI exception occurred.", e.Exception);
        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        RuntimeLog.Error("AppDomain unhandled exception.", e.ExceptionObject as Exception);
    }

    private void HandleFatalException(string message, Exception exception)
    {
        RuntimeLog.Error(message, exception);
        MessageBox.Show(
            $"{message}{Environment.NewLine}{Environment.NewLine}See log:{Environment.NewLine}{RuntimeLog.PathOnDisk}",
            AppTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(-1);
    }
}
