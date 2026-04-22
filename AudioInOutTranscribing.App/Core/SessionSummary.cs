using AudioInOutTranscribing.App.Audio;

namespace AudioInOutTranscribing.App.Core;

public sealed class RecordingSessionSummary
{
    public string SessionId { get; init; } = string.Empty;

    public string SessionRootPath { get; init; } = string.Empty;

    public DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset EndedUtc { get; init; }

    public SourceSessionSummary Mic { get; init; } = new(AudioSourceKind.Mic);

    public SourceSessionSummary Speaker { get; init; } = new(AudioSourceKind.Speaker);
}

public sealed class SourceSessionSummary
{
    public SourceSessionSummary(AudioSourceKind source)
    {
        Source = source;
    }

    public AudioSourceKind Source { get; }

    public int ProcessedChunks { get; set; }

    public int SucceededChunks { get; set; }

    public int FailedChunks { get; set; }

    public int RetriedChunks { get; set; }

    public int UnresolvedChunks { get; set; }
}
