using System.Text.Json;
using System.Globalization;
using System.Text;
using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Core;
using Serilog;

namespace AudioInOutTranscribing.App.Transcription;

public sealed class TranscriptWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _sessionRoot;
    private readonly bool _writeMergedTranscript;
    private readonly SemaphoreSlim _micLock = new(1, 1);
    private readonly SemaphoreSlim _speakerLock = new(1, 1);

    public TranscriptWriter(string sessionRoot, bool writeMergedTranscript = false)
    {
        _sessionRoot = sessionRoot;
        _writeMergedTranscript = writeMergedTranscript;
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

    public async Task FinalizeSessionOutputsAsync(CancellationToken cancellationToken)
    {
        if (!_writeMergedTranscript)
        {
            return;
        }

        var micIndexPath = Path.Combine(_sessionRoot, "mic", "index.jsonl");
        var speakerIndexPath = Path.Combine(_sessionRoot, "speaker", "index.jsonl");

        var records = LoadIndexRecords(micIndexPath, AudioSourceKind.Mic)
            .Concat(LoadIndexRecords(speakerIndexPath, AudioSourceKind.Speaker))
            .OrderBy(record => record.StartUtc)
            .ThenBy(record => record.Source)
            .ThenBy(record => record.ChunkIndex)
            .ThenBy(record => record.Attempts)
            .ToList();

        var transcriptBuilder = new StringBuilder();
        var mergedSpeakerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nextSpeakerNumber = 1;

        foreach (var record in records)
        {
            if (!string.Equals(record.Status, nameof(TranscriptionStatus.Success), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceLabel = record.Source.ToString().ToLowerInvariant();
            var wroteSegmentLine = false;

            foreach (var segment in record.SpeakerSegments)
            {
                if (string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                var mergedSpeaker = ResolveMergedSpeakerLabel(
                    record.Source,
                    segment,
                    mergedSpeakerMap,
                    ref nextSpeakerNumber);

                transcriptBuilder.Append('[')
                    .Append(record.StartUtc.ToString("O", CultureInfo.InvariantCulture))
                    .Append("] source=")
                    .Append(sourceLabel)
                    .Append(" chunk=")
                    .Append(record.ChunkIndex.ToString("D4", CultureInfo.InvariantCulture))
                    .Append(" attempts=")
                    .Append(record.Attempts.ToString(CultureInfo.InvariantCulture))
                    .Append(" [")
                    .Append(mergedSpeaker)
                    .Append("] ")
                    .Append(segment.Text)
                    .Append(Environment.NewLine);
                wroteSegmentLine = true;
            }

            if (wroteSegmentLine || string.IsNullOrWhiteSpace(record.Text))
            {
                continue;
            }

            transcriptBuilder.Append('[')
                .Append(record.StartUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append("] source=")
                .Append(sourceLabel)
                .Append(" chunk=")
                .Append(record.ChunkIndex.ToString("D4", CultureInfo.InvariantCulture))
                .Append(" attempts=")
                .Append(record.Attempts.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(record.Text)
                .Append(Environment.NewLine);
        }

        var mergedTranscriptPath = Path.Combine(_sessionRoot, "transcript.txt");
        await File.WriteAllTextAsync(mergedTranscriptPath, transcriptBuilder.ToString(), cancellationToken);
    }

    public async Task WriteSessionSummaryAsync(RecordingSessionSummary summary, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_sessionRoot, "session.json");
        var json = JsonSerializer.Serialize(summary, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static IEnumerable<MergedIndexRecord> LoadIndexRecords(string indexPath, AudioSourceKind source)
    {
        if (!File.Exists(indexPath))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(indexPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            MergedIndexRecord? record = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var startUtc = TryGetDateTimeOffset(root, "startUtc") ?? DateTimeOffset.MinValue;
                var chunkIndex = TryGetInt(root, "chunkIndex");
                var attempts = TryGetInt(root, "attempts");
                var status = TryGetString(root, "status") ?? string.Empty;
                var text = TryGetString(root, "text") ?? string.Empty;
                var speakerSegments = ParseSpeakerSegments(root);

                record = new MergedIndexRecord(
                    source,
                    chunkIndex,
                    startUtc,
                    attempts,
                    status,
                    text,
                    speakerSegments);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to parse transcript index line at {IndexPath}.", indexPath);
            }

            if (record is not null)
            {
                yield return record;
            }
        }
    }

    private static IReadOnlyList<SpeakerSegment> ParseSpeakerSegments(JsonElement root)
    {
        if (!root.TryGetProperty("speakerSegments", out var speakerSegmentsElement) ||
            speakerSegmentsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SpeakerSegment>();
        }

        var segments = new List<SpeakerSegment>();
        foreach (var segmentElement in speakerSegmentsElement.EnumerateArray())
        {
            var startSeconds = TryGetDouble(segmentElement, "startSeconds");
            var endSeconds = TryGetDouble(segmentElement, "endSeconds");
            var speakerLabel = TryGetString(segmentElement, "speakerLabel") ?? "unknown";
            var rawSpeakerLabel = TryGetString(segmentElement, "rawSpeakerLabel") ?? speakerLabel;
            var text = TryGetString(segmentElement, "text") ?? string.Empty;

            segments.Add(new SpeakerSegment(startSeconds, endSeconds, speakerLabel, rawSpeakerLabel, text));
        }

        return segments;
    }

    private static string ResolveMergedSpeakerLabel(
        AudioSourceKind source,
        SpeakerSegment segment,
        Dictionary<string, string> mergedSpeakerMap,
        ref int nextSpeakerNumber)
    {
        var localSpeaker = string.IsNullOrWhiteSpace(segment.SpeakerLabel)
            ? segment.RawSpeakerLabel
            : segment.SpeakerLabel;

        if (string.IsNullOrWhiteSpace(localSpeaker) ||
            string.Equals(localSpeaker, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "unknown";
        }

        var key = $"{source}:{localSpeaker.Trim()}";
        if (mergedSpeakerMap.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var mapped = $"speaker_{nextSpeakerNumber}";
        nextSpeakerNumber++;
        mergedSpeakerMap[key] = mapped;
        return mapped;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int TryGetInt(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static double TryGetDouble(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var parsed))
        {
            return parsed;
        }

        return 0.0;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = value.GetString();
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private sealed record MergedIndexRecord(
        AudioSourceKind Source,
        int ChunkIndex,
        DateTimeOffset StartUtc,
        int Attempts,
        string Status,
        string Text,
        IReadOnlyList<SpeakerSegment> SpeakerSegments);

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
