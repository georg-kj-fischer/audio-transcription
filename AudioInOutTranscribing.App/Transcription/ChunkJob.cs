using AudioInOutTranscribing.App.Audio;

namespace AudioInOutTranscribing.App.Transcription;

public sealed record ChunkJob(
    string SessionId,
    AudioSourceKind Source,
    int ChunkIndex,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string WavPath,
    int RetryCount);
