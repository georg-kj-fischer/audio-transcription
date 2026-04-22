namespace AudioInOutTranscribing.App.Audio;

public sealed class AudioChunkReadyEventArgs : EventArgs
{
    public AudioChunkReadyEventArgs(
        AudioSourceKind source,
        int chunkIndex,
        string wavPath,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int payloadBytes)
    {
        Source = source;
        ChunkIndex = chunkIndex;
        WavPath = wavPath;
        StartUtc = startUtc;
        EndUtc = endUtc;
        PayloadBytes = payloadBytes;
    }

    public AudioSourceKind Source { get; }

    public int ChunkIndex { get; }

    public string WavPath { get; }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }

    public int PayloadBytes { get; }
}
