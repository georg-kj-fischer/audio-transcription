namespace AudioInOutTranscribing.App.Transcription;

public interface ITranscriptionClient
{
    Task<TranscriptionResult> TranscribeAsync(ChunkJob job, CancellationToken cancellationToken);
}
