using AudioInOutTranscribing.App.Core;
using Serilog;

namespace AudioInOutTranscribing.App.Infrastructure;

public static class Logging
{
    public static void Configure()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(AppPaths.LogsDirectory, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true)
            .CreateLogger();
    }
}
