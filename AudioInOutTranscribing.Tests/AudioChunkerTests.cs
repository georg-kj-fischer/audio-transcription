using AudioInOutTranscribing.App.Audio;
using NAudio.Wave;

namespace AudioInOutTranscribing.Tests;

public sealed class AudioChunkerTests
{
    [Fact]
    public void Chunker_SplitsExactAndPartialChunks()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var events = new List<AudioChunkReadyEventArgs>();
            var format = new WaveFormat(1000, 16, 1); // 2000 bytes/sec
            var chunker = new AudioChunker(AudioSourceKind.Mic, format, TimeSpan.FromSeconds(1), tempRoot);
            chunker.ChunkReady += (_, e) => events.Add(e);

            var input = new byte[4500];
            chunker.AddSamples(input, input.Length);
            chunker.Flush();

            Assert.Equal(3, events.Count);
            Assert.Equal(2000, events[0].PayloadBytes);
            Assert.Equal(2000, events[1].PayloadBytes);
            Assert.Equal(500, events[2].PayloadBytes);

            foreach (var evt in events)
            {
                Assert.True(File.Exists(evt.WavPath), $"Expected chunk file to exist: {evt.WavPath}");
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
