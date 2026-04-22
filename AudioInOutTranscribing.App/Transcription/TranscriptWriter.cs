using System.Text.Json;
using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Core;

namespace AudioInOutTranscribing.App.Transcription;

public sealed class TranscriptWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _sessionRoot;
    private readonly SemaphoreSlim _micLock = new(1, 1);
    private readonly SemaphoreSlim _speakerLock = new(1, 1);

    public TranscriptWriter(string sessionRoot)
    {
        _sessionRoot = sessionRoot;
    }

    public async Task AppendChunkOutcomeAsync(
        ChunkJob job,
        TranscriptionResult result,
        int attempts,
        CancellationToken cancellationToken)
    {
        var gate = job.Source == AudioSourceKind.Mic ? _micLock : _speakerLock;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var sourceDirectory = Path.Combine(_sessionRoot, job.Source == AudioSourceKind.Mic ? "mic" : "speaker");
            Directory.CreateDirectory(sourceDirectory);

            var transcriptPath = Path.Combine(sourceDirectory, "transcript.txt");
            var indexPath = Path.Combine(sourceDirectory, "index.jsonl");

            if (result.Status == TranscriptionStatus.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
                if (result.SpeakerSegments.Count > 0)
                {
                    foreach (var segment in result.SpeakerSegments)
                    {
                        var transcriptLine =
                            $"[{job.StartUtc:O}] chunk={job.ChunkIndex:D4} attempts={attempts} " +
                            $"[{segment.SpeakerLabel}] {segment.Text}{Environment.NewLine}";
                        await File.AppendAllTextAsync(transcriptPath, transcriptLine, cancellationToken);
                    }
                }
                else
                {
                    var transcriptLine =
                        $"[{job.StartUtc:O}] chunk={job.ChunkIndex:D4} attempts={attempts} {result.Text}{Environment.NewLine}";
                    await File.AppendAllTextAsync(transcriptPath, transcriptLine, cancellationToken);
                }
            }

            var indexRecord = new TranscriptIndexRecord(
                job.ChunkIndex,
                job.Source.ToString().ToLowerInvariant(),
                job.StartUtc,
                job.EndUtc,
                job.WavPath,
                attempts,
                result.Status.ToString(),
                result.ProviderRequestId,
                result.Error,
                result.Text,
                result.SpeakerSegments);

            var jsonLine = JsonSerializer.Serialize(indexRecord, JsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(indexPath, jsonLine, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteSessionSummaryAsync(RecordingSessionSummary summary, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_sessionRoot, "session.json");
        var json = JsonSerializer.Serialize(summary, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private sealed record TranscriptIndexRecord(
        int ChunkIndex,
        string Source,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string WavPath,
        int Attempts,
        string Status,
        string? ProviderRequestId,
        string? Error,
        string Text,
        IReadOnlyList<SpeakerSegment> SpeakerSegments);
}
