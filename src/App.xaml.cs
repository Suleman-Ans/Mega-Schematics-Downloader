using System.Windows;
using System.Windows.Threading;

namespace MegaSchematicsDownloader;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Mega Schematics Downloader", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
