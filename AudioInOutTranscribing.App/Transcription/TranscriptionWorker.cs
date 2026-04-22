using System.Threading.Channels;
using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Core;
using Serilog;

namespace AudioInOutTranscribing.App.Transcription;

public sealed class TranscriptionWorker
{
    private readonly AudioSourceKind _source;
    private readonly ChannelReader<ChunkJob> _reader;
    private readonly ITranscriptionClient _transcriptionClient;
    private readonly TranscriptWriter _transcriptWriter;
    private readonly bool _saveRawAudio;
    private readonly Dictionary<string, string> _speakerLabelMap = new(StringComparer.OrdinalIgnoreCase);
    private int _nextSpeakerNumber = 1;

    public TranscriptionWorker(
        AudioSourceKind source,
        ChannelReader<ChunkJob> reader,
        ITranscriptionClient transcriptionClient,
        TranscriptWriter transcriptWriter,
        bool saveRawAudio)
    {
        _source = source;
        _reader = reader;
        _transcriptionClient = transcriptionClient;
        _transcriptWriter = transcriptWriter;
        _saveRawAudio = saveRawAudio;
        Summary = new SourceSessionSummary(source);
    }

    public SourceSessionSummary Summary { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var job in _reader.ReadAllAsync(cancellationToken))
        {
            await ProcessChunkAsync(job, cancellationToken);
        }
    }

    private async Task ProcessChunkAsync(ChunkJob baseJob, CancellationToken cancellationToken)
    {
        var attempts = 0;

        while (true)
        {
            attempts++;
            var job = baseJob with { RetryCount = attempts - 1 };
            var result = await _transcriptionClient.TranscribeAsync(job, cancellationToken);
            result = StabilizeSpeakerLabels(result);

            if (result.Status == TranscriptionStatus.Success)
            {
                Summary.ProcessedChunks++;
                Summary.SucceededChunks++;
                Summary.RetriedChunks += Math.Max(attempts - 1, 0);
                await _transcriptWriter.AppendChunkOutcomeAsync(job, result, attempts, cancellationToken);
                CleanupRawAudio(job.WavPath);
                return;
            }

            if (result.Status == TranscriptionStatus.PermanentFailure)
            {
                Summary.ProcessedChunks++;
                Summary.FailedChunks++;
                Summary.RetriedChunks += Math.Max(attempts - 1, 0);
                await _transcriptWriter.AppendChunkOutcomeAsync(job, result, attempts, cancellationToken);
                return;
            }

            var delay = RetryPolicy.GetDelayForAttempt(attempts);
            Log.Warning(
                "Transient transcription failure. source={Source} chunk={ChunkIndex} attempt={Attempt} delay={DelaySeconds}s error={Error}",
                _source,
                job.ChunkIndex,
                attempts,
                (int)delay.TotalSeconds,
                result.Error);

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Summary.UnresolvedChunks++;
                var cancelled = new TranscriptionResult(
                    job.ChunkIndex,
                    _source,
                    string.Empty,
                    0,
                    null,
                    TranscriptionStatus.Cancelled,
                    "Cancelled while retrying.",
                    Array.Empty<SpeakerSegment>());
                await _transcriptWriter.AppendChunkOutcomeAsync(job, cancelled, attempts, CancellationToken.None);
                return;
            }
        }
    }

    private TranscriptionResult StabilizeSpeakerLabels(TranscriptionResult result)
    {
        if (result.SpeakerSegments.Count == 0)
        {
            return result;
        }

        var rewritten = new List<SpeakerSegment>(result.SpeakerSegments.Count);
        foreach (var segment in result.SpeakerSegments)
        {
            var raw = string.IsNullOrWhiteSpace(segment.RawSpeakerLabel) ? "unknown" : segment.RawSpeakerLabel;
            var stable = ResolveStableSpeakerLabel(raw);
            rewritten.Add(segment with { SpeakerLabel = stable, RawSpeakerLabel = raw });
        }

        return result with { SpeakerSegments = rewritten };
    }

    private string ResolveStableSpeakerLabel(string rawSpeakerLabel)
    {
        if (string.IsNullOrWhiteSpace(rawSpeakerLabel) || string.Equals(rawSpeakerLabel, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "unknown";
        }

        if (_speakerLabelMap.TryGetValue(rawSpeakerLabel, out var existing))
        {
            return existing;
        }

        var stable = $"speaker_{_nextSpeakerNumber}";
        _nextSpeakerNumber++;
        _speakerLabelMap[rawSpeakerLabel] = stable;
        return stable;
    }

    private void CleanupRawAudio(string wavPath)
    {
        if (_saveRawAudio)
        {
            return;
        }

        try
        {
            if (File.Exists(wavPath))
            {
                File.Delete(wavPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete temporary audio chunk {Path}.", wavPath);
        }
    }
}
