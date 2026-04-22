namespace AudioInOutTranscribing.App.Audio;

public sealed class AudioDataEventArgs : EventArgs
{
    public AudioDataEventArgs(byte[] buffer, int bytesRecorded)
    {
        Buffer = buffer;
        BytesRecorded = bytesRecorded;
    }

    public byte[] Buffer { get; }

    public int BytesRecorded { get; }
}
