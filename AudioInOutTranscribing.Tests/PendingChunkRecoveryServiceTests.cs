using System.Text.Json;
using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Transcription;

namespace AudioInOutTranscribing.Tests;

public sealed class PendingChunkRecoveryServiceTests
{
    [Fact]
    public async Task RecoverAsync_RetriesPendingChunk_AndWritesSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        var sessionRoot = Path.Combine(root, "2026-04-21", "session-101010");
        var micFolder = Path.Combine(sessionRoot, "mic");
        Directory.CreateDirectory(micFolder);

        var wavPath = Path.Combine(micFolder, "0001.wav");
        await File.WriteAllBytesAsync(wavPath, new byte[] { 0, 1, 2, 3 });

        var indexPath = Path.Combine(micFolder, "index.jsonl");
        var indexLines = new[]
        {
            "{\"chunkIndex\":1,\"source\":\"mic\",\"startUtc\":\"2026-04-21T10:10:10.0000000+00:00\",\"endUtc\":\"2026-04-21T10:10:40.0000000+00:00\",\"wavPath\":\"" +
            EscapePath(wavPath) +
            "\",\"attempts\":1,\"status\":\"PermanentFailure\",\"providerRequestId\":null,\"error\":\"boom\",\"text\":\"\"}"
        };
        await File.WriteAllLinesAsync(indexPath, indexLines);

        try
        {
            var fakeClient = new FakeTranscriptionClient(TranscriptionStatus.Success);
            var service = new PendingChunkRecoveryService(maxRetryAttempts: 5);

            var summary = await service.RecoverAsync(root, saveRawAudio: false, fakeClient, CancellationToken.None);

            Assert.Equal(1, summary.PendingChunksFound);
            Assert.Equal(1, summary.RetriedChunks);
            Assert.Equal(1, summary.SucceededChunks);
            Assert.Equal(0, summary.FailedChunks);
            Assert.Equal(0, summary.SkippedDueToMaxRetries);
            Assert.Equal(1, fakeClient.CallCount);
            Assert.False(File.Exists(wavPath));

            var written = await File.ReadAllLinesAsync(indexPath);
            Assert.Equal(2, written.Length);

            using var lastDoc = JsonDocument.Parse(written[^1]);
            Assert.Equal("Success", lastDoc.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, lastDoc.RootElement.GetProperty("attempts").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecoverAsync_SkipsPendingChunk_WhenMaxAttemptsReached()
    {
        var root = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        var sessionRoot = Path.Combine(root, "2026-04-21", "session-111111");
        var speakerFolder = Path.Combine(sessionRoot, "speaker");
        Directory.CreateDirectory(speakerFolder);

        var wavPath = Path.Combine(speakerFolder, "0001.wav");
        await File.WriteAllBytesAsync(wavPath, new byte[] { 0, 1, 2, 3 });

        var indexPath = Path.Combine(speakerFolder, "index.jsonl");
        var indexLines = new[]
        {
            "{\"chunkIndex\":1,\"source\":\"speaker\",\"startUtc\":\"2026-04-21T11:11:11.0000000+00:00\",\"endUtc\":\"2026-04-21T11:11:41.0000000+00:00\",\"wavPath\":\"" +
            EscapePath(wavPath) +
            "\",\"attempts\":5,\"status\":\"PermanentFailure\",\"providerRequestId\":null,\"error\":\"still failing\",\"text\":\"\"}"
        };
        await File.WriteAllLinesAsync(indexPath, indexLines);

        try
        {
            var fakeClient = new FakeTranscriptionClient(TranscriptionStatus.Success);
            var service = new PendingChunkRecoveryService(maxRetryAttempts: 5);

            var summary = await service.RecoverAsync(root, saveRawAudio: true, fakeClient, CancellationToken.None);

            Assert.Equal(1, summary.PendingChunksFound);
            Assert.Equal(0, summary.RetriedChunks);
            Assert.Equal(0, summary.SucceededChunks);
            Assert.Equal(0, summary.FailedChunks);
            Assert.Equal(1, summary.SkippedDueToMaxRetries);
            Assert.Equal(0, fakeClient.CallCount);
            Assert.True(File.Exists(wavPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string EscapePath(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private sealed class FakeTranscriptionClient : ITranscriptionClient
    {
        private readonly Queue<TranscriptionStatus> _statuses;

        public FakeTranscriptionClient(params TranscriptionStatus[] statuses)
        {
            _statuses = new Queue<TranscriptionStatus>(statuses);
        }

        public int CallCount { get; private set; }

        public Task<TranscriptionResult> TranscribeAsync(ChunkJob job, CancellationToken cancellationToken)
        {
            CallCount++;
            var status = _statuses.Count > 0 ? _statuses.Dequeue() : TranscriptionStatus.PermanentFailure;
            var text = status == TranscriptionStatus.Success ? "Recovered transcript" : string.Empty;
            var error = status == TranscriptionStatus.Success ? null : "retry failed";

            return Task.FromResult(new TranscriptionResult(
                ChunkIndex: job.ChunkIndex,
                Source: job.Source,
                Text: text,
                DurationMs: 1000,
                ProviderRequestId: "fake-request",
                Status: status,
                Error: error,
                SpeakerSegments: Array.Empty<SpeakerSegment>()));
        }
    }
}
