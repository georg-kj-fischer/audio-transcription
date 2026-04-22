namespace AudioInOutTranscribing.App.Core;

public sealed class AppSettings
{
    public string? InputDeviceId { get; set; }

    public string? InputDeviceName { get; set; }

    public string? OutputDeviceId { get; set; }

    public string? OutputDeviceName { get; set; }

    public string OutputFolder { get; set; } = AppPaths.DefaultTranscriptRoot;

    public bool AutoStartOnLaunch { get; set; }

    public bool SaveRawAudio { get; set; }

    public int ChunkSeconds { get; set; } = 30;

    public string TranscriptFormat { get; set; } = "txt+jsonl";

    public string ApiProvider { get; set; } = "mistral";

    public string Model { get; set; } = "voxtral-mini-latest";

    public string MistralApiKey { get; set; } = string.Empty;

    public static AppSettings CreateDefault() => new();
}
