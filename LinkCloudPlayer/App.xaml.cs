using System.IO;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace LinkCloudPlayer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinkCloudPlayer",
        "crash.log"
    );

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

        // Managed exceptions that would otherwise close the app silently.
        // A NATIVE crash (e.g. inside libvlc's video renderer) can't be
        // caught here at all - that's what the libvlc log file is for.
        DispatcherUnhandledException += (_, args) =>
        {
            LogToFile("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(args.Exception.ToString(), "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogToFile("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogToFile("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        Core.Initialize(); // loads libvlc from the VideoLAN.LibVLC.Windows package; must run once before any LibVLC instance
    }

    public static void LogToFile(string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {source}: {ex}\n\n");
        }
        catch { /* logging must never itself crash the app */ }
    }
}
