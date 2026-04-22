namespace AudioInOutTranscribing.App.Core;

public static class AppPaths
{
    public const string AppName = "AudioInOutTranscribing";

    public static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string SettingsFilePath => Path.Combine(AppDataRoot, "settings.json");

    public static string LogsDirectory => Path.Combine(AppDataRoot, "logs");

    public static string TempDirectory => Path.Combine(AppDataRoot, "temp");

    public static string DefaultTranscriptRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Transcripts");
}
