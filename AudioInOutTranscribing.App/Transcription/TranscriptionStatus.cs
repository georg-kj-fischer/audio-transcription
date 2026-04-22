namespace AudioInOutTranscribing.App.Transcription;

public enum TranscriptionStatus
{
    Success = 0,
    TransientFailure = 1,
    PermanentFailure = 2,
    Cancelled = 3
}
