using System.Globalization;
using System.Text.Json;
using AudioInOutTranscribing.App.Audio;
using Serilog;

namespace AudioInOutTranscribing.App.Transcription;

public sealed class PendingChunkRecoveryService
{
    private readonly int _maxRetryAttempts;

    public PendingChunkRecoveryService(int maxRetryAttempts = 5)
    {
        _maxRetryAttempts = Math.Max(1, maxRetryAttempts);
    }

    public async Task<PendingChunkRecoverySummary> RecoverAsync(
        string outputRoot,
        bool saveRawAudio,
        bool writeMergedTranscript,
        ITranscriptionClient transcriptionClient,
        CancellationToken cancellationToken = default)
    {
        var summary = new PendingChunkRecoverySummary();
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return summary;
        }

        var sessionDirectories = Directory.EnumerateDirectories(outputRoot, "session-*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var sessionRoot in sessionDirectories)
        {
            summary.ScannedSessions++;
            var writer = new TranscriptWriter(sessionRoot, writeMergedTranscript);

            foreach (var source in new[] { AudioSourceKind.Mic, AudioSourceKind.Speaker })
            {
                var sourceFolder = Path.Combine(sessionRoot, SourceFolderName(source));
                if (!Directory.Exists(sourceFolder))
                {
                    continue;
                }

                var indexPath = Path.Combine(sourceFolder, "index.jsonl");
                var latestByWavPath = LoadLatestIndexByWavPath(indexPath);

                var wavFiles = Directory.EnumerateFiles(sourceFolder, "*.wav", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var wavPath in wavFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    summary.ScannedWavFiles++;
                    var normalizedWavPath = NormalizePath(wavPath);
                    latestByWavPath.TryGetValue(normalizedWavPath, out var lastRecord);

                    if (lastRecord is not null &&
                        string.Equals(lastRecord.Status, nameof(TranscriptionStatus.Success), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    summary.PendingChunksFound++;
                    var attemptsSoFar = Math.Max(lastRecord?.Attempts ?? 0, 0);
                    if (attemptsSoFar >= _maxRetryAttempts)
                    {
                        summary.SkippedDueToMaxRetries++;
                        continue;
                    }

                    var chunkIndex = lastRecord?.ChunkIndex ?? ParseChunkIndexFromFileName(wavPath);
                    if (chunkIndex <= 0)
                    {
                        chunkIndex = 1;
                    }

                    var (startUtc, endUtc) = ResolveChunkTimes(wavPath, lastRecord);
                    var job = new ChunkJob(
                        SessionId: Path.GetFileName(sessionRoot),
                        Source: source,
                        ChunkIndex: chunkIndex,
                        StartUtc: startUtc,
                        EndUtc: endUtc,
                        WavPath: wavPath,
                        RetryCount: attemptsSoFar);

                    summary.RetriedChunks++;
                    var outcome = await RetryChunkAsync(
                        baseJob: job,
                        attemptsSoFar: attemptsSoFar,
                        saveRawAudio: saveRawAudio,
                        transcriptionClient: transcriptionClient,
                        transcriptWriter: writer,
                        cancellationToken: cancellationToken);

                    if (outcome == ChunkRecoveryOutcome.Succeeded)
                    {
                        summary.SucceededChunks++;
                    }
                    else
                    {
                        summary.FailedChunks++;
                    }
                }
            }

            await writer.FinalizeSessionOutputsAsync(cancellationToken);
        }

        return summary;
    }

    private async Task<ChunkRecoveryOutcome> RetryChunkAsync(
        ChunkJob baseJob,
        int attemptsSoFar,
        bool saveRawAudio,
        ITranscriptionClient transcriptionClient,
        TranscriptWriter transcriptWriter,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(attemptsSoFar, 0);

        while (attempts < _maxRetryAttempts)
        {
            attempts++;
            var job = baseJob with { RetryCount = attempts - 1 };
            var result = await transcriptionClient.TranscribeAsync(job, cancellationToken);
            await transcriptWriter.AppendChunkOutcomeAsync(job, result, attempts, cancellationToken);

            if (result.Status == TranscriptionStatus.Success)
            {
                if (!saveRawAudio)
                {
                    TryDeleteFile(job.WavPath);
                }

                return ChunkRecoveryOutcome.Succeeded;
            }

            if (result.Status == TranscriptionStatus.PermanentFailure)
            {
                return ChunkRecoveryOutcome.Failed;
            }

            if (attempts < _maxRetryAttempts)
            {
                var delay = RetryPolicy.GetDelayForAttempt(attempts);
                await Task.Delay(delay, cancellationToken);
            }
        }

        return ChunkRecoveryOutcome.Failed;
    }

    private static Dictionary<string, RecoveryIndexRecord> LoadLatestIndexByWavPath(string indexPath)
    {
        var result = new Dictionary<string, RecoveryIndexRecord>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(indexPath))
        {
            return result;
        }

        foreach (var line in File.ReadLines(indexPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var wavPath = TryGetString(root, "wavPath");
                if (string.IsNullOrWhiteSpace(wavPath))
                {
                    continue;
                }

                var record = new RecoveryIndexRecord(
                    Status: TryGetString(root, "status") ?? string.Empty,
                    Attempts: TryGetInt(root, "attempts"),
                    ChunkIndex: TryGetInt(root, "chunkIndex"),
                    StartUtc: TryGetDateTimeOffset(root, "startUtc"),
                    EndUtc: TryGetDateTimeOffset(root, "endUtc"));

                result[NormalizePath(wavPath)] = record;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to parse index line while loading pending chunk recovery state.");
            }
        }

        return result;
    }

    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ResolveChunkTimes(string wavPath, RecoveryIndexRecord? record)
    {
        if (record is not null && record.StartUtc is not null && record.EndUtc is not null && record.EndUtc > record.StartUtc)
        {
            return (record.StartUtc.Value, record.EndUtc.Value);
        }

        var createdUtc = File.GetCreationTimeUtc(wavPath);
        var start = new DateTimeOffset(createdUtc, TimeSpan.Zero);
        return (start, start.AddSeconds(1));
    }

    private static string SourceFolderName(AudioSourceKind source)
    {
        return source == AudioSourceKind.Mic ? "mic" : "speaker";
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).Trim();
    }

    private static int ParseChunkIndexFromFileName(string wavPath)
    {
        var name = Path.GetFileNameWithoutExtension(wavPath);
        return int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete recovered audio chunk {Path}.", path);
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int TryGetInt(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return 0;
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

    private enum ChunkRecoveryOutcome
    {
        Succeeded = 0,
        Failed = 1
    }

    private sealed record RecoveryIndexRecord(
        string Status,
        int Attempts,
        int ChunkIndex,
        DateTimeOffset? StartUtc,
        DateTimeOffset? EndUtc);
}

public sealed class PendingChunkRecoverySummary
{
    public int ScannedSessions { get; set; }

    public int ScannedWavFiles { get; set; }

    public int PendingChunksFound { get; set; }

    public int RetriedChunks { get; set; }

    public int SucceededChunks { get; set; }

    public int FailedChunks { get; set; }

    public int SkippedDueToMaxRetries { get; set; }
}
