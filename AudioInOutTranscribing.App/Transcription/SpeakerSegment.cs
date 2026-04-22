namespace AudioInOutTranscribing.App.Transcription;

public sealed record SpeakerSegment(
    double StartSeconds,
    double EndSeconds,
    string SpeakerLabel,
    string RawSpeakerLabel,
    string Text);
