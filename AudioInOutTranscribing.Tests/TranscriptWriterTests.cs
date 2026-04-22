using System.Text.Json;
using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Core;
using AudioInOutTranscribing.App.Transcription;

namespace AudioInOutTranscribing.Tests;

public sealed class TranscriptWriterTests
{
    [Fact]
    public async Task AppendChunkOutcome_WritesTranscriptAndIndexInOrder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        var sessionRoot = Path.Combine(tempRoot, "session-101010");
        Directory.CreateDirectory(Path.Combine(sessionRoot, "mic"));

        try
        {
            var writer = new TranscriptWriter(sessionRoot);
            var start = DateTimeOffset.UtcNow;
            var end = start.AddSeconds(30);

            var chunk1 = new ChunkJob("session-101010", AudioSourceKind.Mic, 1, start, end, Path.Combine(sessionRoot, "mic", "0001.wav"), 0);
            var chunk2 = new ChunkJob("session-101010", AudioSourceKind.Mic, 2, end, end.AddSeconds(30), Path.Combine(sessionRoot, "mic", "0002.wav"), 0);

            await writer.AppendChunkOutcomeAsync(
                chunk1,
                new TranscriptionResult(1, AudioSourceKind.Mic, "hello world", 30000, "req-1", TranscriptionStatus.Success, null, Array.Empty<SpeakerSegment>()),
                attempts: 1,
                CancellationToken.None);

            await writer.AppendChunkOutcomeAsync(
                chunk2,
                new TranscriptionResult(2, AudioSourceKind.Mic, string.Empty, 30000, "req-2", TranscriptionStatus.PermanentFailure, "bad request", Array.Empty<SpeakerSegment>()),
                attempts: 3,
                CancellationToken.None);

            var transcriptPath = Path.Combine(sessionRoot, "mic", "transcript.txt");
            var indexPath = Path.Combine(sessionRoot, "mic", "index.jsonl");
            var transcript = await File.ReadAllTextAsync(transcriptPath);
            var indexLines = await File.ReadAllLinesAsync(indexPath);

            Assert.Contains("hello world", transcript, StringComparison.Ordinal);
            Assert.Equal(2, indexLines.Length);

            using var firstDoc = JsonDocument.Parse(indexLines[0]);
            using var secondDoc = JsonDocument.Parse(indexLines[1]);

            Assert.Equal(1, firstDoc.RootElement.GetProperty("chunkIndex").GetInt32());
            Assert.Equal("Success", firstDoc.RootElement.GetProperty("status").GetString());

            Assert.Equal(2, secondDoc.RootElement.GetProperty("chunkIndex").GetInt32());
            Assert.Equal("PermanentFailure", secondDoc.RootElement.GetProperty("status").GetString());
            Assert.Equal(3, secondDoc.RootElement.GetProperty("attempts").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WriteSessionSummary_CreatesSessionJson()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var writer = new TranscriptWriter(tempRoot);
            var summary = new RecordingSessionSummary
            {
                SessionId = "session-1",
                SessionRootPath = tempRoot,
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                EndedUtc = DateTimeOffset.UtcNow,
                Mic = new SourceSessionSummary(AudioSourceKind.Mic) { ProcessedChunks = 4, SucceededChunks = 3, FailedChunks = 1 },
                Speaker = new SourceSessionSummary(AudioSourceKind.Speaker) { ProcessedChunks = 5, SucceededChunks = 5 }
            };

            await writer.WriteSessionSummaryAsync(summary, CancellationToken.None);

            var sessionPath = Path.Combine(tempRoot, "session.json");
            Assert.True(File.Exists(sessionPath));

            var json = await File.ReadAllTextAsync(sessionPath);
            Assert.Contains("session-1", json, StringComparison.Ordinal);
            Assert.Contains("processedChunks", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AppendChunkOutcome_WritesSpeakerLabelsWhenAvailable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        var sessionRoot = Path.Combine(tempRoot, "session-121212");
        Directory.CreateDirectory(Path.Combine(sessionRoot, "speaker"));

        try
        {
            var writer = new TranscriptWriter(sessionRoot);
            var start = DateTimeOffset.UtcNow;
            var end = start.AddSeconds(30);
            var chunk = new ChunkJob("session-121212", AudioSourceKind.Speaker, 1, start, end, Path.Combine(sessionRoot, "speaker", "0001.wav"), 0);
            var segments = new[]
            {
                new SpeakerSegment(0.0, 2.0, "speaker_1", "speaker_1", "hello"),
                new SpeakerSegment(2.0, 4.0, "speaker_2", "speaker_2", "world")
            };
            var result = new TranscriptionResult(
                chunk.ChunkIndex,
                chunk.Source,
                "hello world",
                30000,
                "req-99",
                TranscriptionStatus.Success,
                null,
                segments);

            await writer.AppendChunkOutcomeAsync(chunk, result, attempts: 1, CancellationToken.None);

            var transcriptPath = Path.Combine(sessionRoot, "speaker", "transcript.txt");
            var transcript = await File.ReadAllTextAsync(transcriptPath);

            Assert.Contains("[speaker_1] hello", transcript, StringComparison.Ordinal);
            Assert.Contains("[speaker_2] world", transcript, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeSessionOutputs_WritesMergedTranscriptWithDistinctSpeakerIdsAcrossSources()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        var sessionRoot = Path.Combine(tempRoot, "session-131313");
        Directory.CreateDirectory(Path.Combine(sessionRoot, "mic"));
        Directory.CreateDirectory(Path.Combine(sessionRoot, "speaker"));

        try
        {
            var writer = new TranscriptWriter(sessionRoot, writeMergedTranscript: true);
            var start = new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

            var micChunk = new ChunkJob(
                "session-131313",
                AudioSourceKind.Mic,
                1,
                start,
                start.AddSeconds(30),
                Path.Combine(sessionRoot, "mic", "0001.wav"),
                0);

            var speakerChunk = new ChunkJob(
                "session-131313",
                AudioSourceKind.Speaker,
                1,
                start.AddSeconds(31),
                start.AddSeconds(61),
                Path.Combine(sessionRoot, "speaker", "0001.wav"),
                0);

            await writer.AppendChunkOutcomeAsync(
                micChunk,
                new TranscriptionResult(
                    micChunk.ChunkIndex,
                    micChunk.Source,
                    "hello world",
                    30000,
                    "req-mic",
                    TranscriptionStatus.Success,
                    null,
                    new[]
                    {
                        new SpeakerSegment(0.0, 2.0, "speaker_1", "speaker_1", "hello"),
                        new SpeakerSegment(2.0, 4.0, "speaker_2", "speaker_2", "world")
                    }),
                attempts: 1,
                CancellationToken.None);

            await writer.AppendChunkOutcomeAsync(
                speakerChunk,
                new TranscriptionResult(
                    speakerChunk.ChunkIndex,
                    speakerChunk.Source,
                    "response",
                    30000,
                    "req-speaker",
                    TranscriptionStatus.Success,
                    null,
                    new[]
                    {
                        new SpeakerSegment(0.0, 2.0, "speaker_1", "speaker_1", "response")
                    }),
                attempts: 1,
                CancellationToken.None);

            await writer.FinalizeSessionOutputsAsync(CancellationToken.None);

            var mergedTranscriptPath = Path.Combine(sessionRoot, "transcript.txt");
            var mergedTranscript = await File.ReadAllTextAsync(mergedTranscriptPath);

            Assert.Contains("source=mic", mergedTranscript, StringComparison.Ordinal);
            Assert.Contains("source=speaker", mergedTranscript, StringComparison.Ordinal);
            Assert.Contains("[speaker_1] hello", mergedTranscript, StringComparison.Ordinal);
            Assert.Contains("[speaker_2] world", mergedTranscript, StringComparison.Ordinal);
            Assert.Contains("[speaker_3] response", mergedTranscript, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
