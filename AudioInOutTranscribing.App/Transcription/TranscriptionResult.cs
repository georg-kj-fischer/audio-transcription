using AudioInOutTranscribing.App.Audio;

namespace AudioInOutTranscribing.App.Transcription;

public sealed record TranscriptionResult(
    int ChunkIndex,
    AudioSourceKind Source,
    string Text,
    int DurationMs,
    string? ProviderRequestId,
    TranscriptionStatus Status,
    string? Error,
    IReadOnlyList<SpeakerSegment> SpeakerSegments);
