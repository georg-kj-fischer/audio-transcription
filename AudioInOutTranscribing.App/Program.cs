using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Core;
using AudioInOutTranscribing.App.Infrastructure;
using Serilog;

namespace AudioInOutTranscribing.App;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\AudioInOutTranscribing.App";

    [STAThread]
    private static void Main()
    {
        Logging.Configure();
        Log.Information(
            "Application starting. pid={ProcessId} version={Version}",
            Environment.ProcessId,
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            Log.Fatal(eventArgs.ExceptionObject as Exception, "Unhandled domain exception.");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("Process exit. pid={ProcessId}", Environment.ProcessId);
        };

        Application.ThreadException += (_, eventArgs) =>
        {
            Log.Fatal(eventArgs.Exception, "Unhandled UI thread exception.");
        };

        try
        {
            using var singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isPrimaryInstance);
            if (!isPrimaryInstance)
            {
                Log.Warning("Startup rejected because another app instance is already running.");
                MessageBox.Show(
                    "Audio InOut Transcribing is already running in the system tray.",
                    "Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var settingsStore = new SettingsStore(AppPaths.SettingsFilePath);
            var settings = settingsStore.LoadAsync().GetAwaiter().GetResult();
            var deviceEnumerator = new DeviceEnumerator();

            ApplicationConfiguration.Initialize();
            using var appContext = new TrayApplicationContext(settingsStore, deviceEnumerator, settings);
            Application.Run(appContext);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to start application.");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
