using NAudio.Wave;

namespace AudioInOutTranscribing.App.Audio;

public static class WaveFileChunkWriter
{
    public static void WriteChunk(string wavPath, WaveFormat waveFormat, byte[] data, int bytesRecorded)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(wavPath) ?? ".");
        using var writer = new WaveFileWriter(wavPath, waveFormat);
        writer.Write(data, 0, bytesRecorded);
    }
}
