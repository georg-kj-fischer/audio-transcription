using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace AudioInOutTranscribing.App.Transcription;

public sealed class MistralTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri TranscriptionEndpoint = new("https://api.mistral.ai/v1/audio/transcriptions");
    private const string DiarizationDisabledMessage = "Diarization is not enabled for this model";

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private int _diarizationEnabled = 1;

    public MistralTranscriptionClient(HttpClient httpClient, string apiKey, string model)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _model = model;
    }

    public async Task<TranscriptionResult> TranscribeAsync(ChunkJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return new TranscriptionResult(
                job.ChunkIndex,
                job.Source,
                string.Empty,
                0,
                null,
                TranscriptionStatus.PermanentFailure,
                "Missing Mistral API key.",
                Array.Empty<SpeakerSegment>());
        }

        try
        {
            var includeDiarization = Volatile.Read(ref _diarizationEnabled) == 1;
            var response = await SendRequestAsync(job, includeDiarization, cancellationToken);

            if (includeDiarization && ShouldRetryWithoutDiarization(response.StatusCode, response.Body))
            {
                Interlocked.Exchange(ref _diarizationEnabled, 0);
                Log.Warning(
                    "Model {Model} rejected diarization for {Source} chunk {ChunkIndex}. Falling back to transcription without diarization for this session.",
                    _model,
                    job.Source,
                    job.ChunkIndex);

                response = await SendRequestAsync(job, includeDiarization: false, cancellationToken);
            }

            if (!IsSuccessStatusCode(response.StatusCode))
            {
                var status = RetryPolicy.IsRetryableStatusCode(response.StatusCode)
                    ? TranscriptionStatus.TransientFailure
                    : TranscriptionStatus.PermanentFailure;

                var error = $"HTTP {(int)response.StatusCode} {response.StatusCode}: {TrimForLog(response.Body)}";
                return new TranscriptionResult(
                    job.ChunkIndex,
                    job.Source,
                    string.Empty,
                    0,
                    response.RequestId,
                    status,
                    error,
                    Array.Empty<SpeakerSegment>());
            }

            var payload = ParsePayload(response.Body);
            var durationMs = (int)Math.Round((job.EndUtc - job.StartUtc).TotalMilliseconds);
            return new TranscriptionResult(
                job.ChunkIndex,
                job.Source,
                payload.Text,
                durationMs,
                response.RequestId,
                TranscriptionStatus.Success,
                null,
                payload.SpeakerSegments);
        }
        catch (Exception ex) when (RetryPolicy.IsRetryableException(ex))
        {
            Log.Warning(ex, "Transient transcription error for {Source} chunk {ChunkIndex}.", job.Source, job.ChunkIndex);
            return new TranscriptionResult(
                job.ChunkIndex,
                job.Source,
                string.Empty,
                0,
                null,
                TranscriptionStatus.TransientFailure,
                ex.Message,
                Array.Empty<SpeakerSegment>());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Permanent transcription error for {Source} chunk {ChunkIndex}.", job.Source, job.ChunkIndex);
            return new TranscriptionResult(
                job.ChunkIndex,
                job.Source,
                string.Empty,
                0,
                null,
                TranscriptionStatus.PermanentFailure,
                ex.Message,
                Array.Empty<SpeakerSegment>());
        }
    }

    private async Task<RawResponse> SendRequestAsync(ChunkJob job, bool includeDiarization, CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(job.WavPath);
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", Path.GetFileName(job.WavPath));
        form.Add(new StringContent(_model), "model");

        if (includeDiarization)
        {
            form.Add(new StringContent("true"), "diarize");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var requestId = TryGetRequestId(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new RawResponse(response.StatusCode, requestId, body);
    }

    private static bool ShouldRetryWithoutDiarization(HttpStatusCode statusCode, string responseBody)
    {
        if (statusCode != HttpStatusCode.BadRequest || string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        if (responseBody.Contains(DiarizationDisabledMessage, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (responseBody.Contains("\"code\":\"3051\"", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("\"code\":3051", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            return JsonSignalsUnsupportedDiarization(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSuccessStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 200 and < 300;
    }

    private static bool JsonSignalsUnsupportedDiarization(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var message = TryGetString(element, "message");
        if (!string.IsNullOrWhiteSpace(message) &&
            message.Contains(DiarizationDisabledMessage, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (element.TryGetProperty("code", out var codeProperty))
        {
            if (codeProperty.ValueKind == JsonValueKind.String &&
                string.Equals(codeProperty.GetString(), "3051", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (codeProperty.ValueKind == JsonValueKind.Number &&
                codeProperty.TryGetInt32(out var code) &&
                code == 3051)
            {
                return true;
            }
        }

        if (element.TryGetProperty("error", out var nestedError))
        {
            return JsonSignalsUnsupportedDiarization(nestedError);
        }

        return false;
    }

    private static string? TryGetRequestId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-request-id", out var requestIds))
        {
            return requestIds.FirstOrDefault();
        }

        return null;
    }

    private static TranscriptionPayload ParsePayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new TranscriptionPayload(string.Empty, Array.Empty<SpeakerSegment>());
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var text = ExtractText(root);
                var segments = ExtractSpeakerSegments(root);
                return new TranscriptionPayload(text, segments);
            }
        }
        catch (JsonException)
        {
            // Non-JSON payloads are treated as plain text.
        }

        return new TranscriptionPayload(content.Trim(), Array.Empty<SpeakerSegment>());
    }

    private static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("text", out var directText) && directText.ValueKind == JsonValueKind.String)
        {
            return directText.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.String)
        {
            return transcript.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static IReadOnlyList<SpeakerSegment> ExtractSpeakerSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SpeakerSegment>();
        }

        var result = new List<SpeakerSegment>();
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = TryGetString(segment, "text");
            var start = TryGetDouble(segment, "start");
            var end = TryGetDouble(segment, "end");
            var rawSpeaker = TryGetString(segment, "speaker_id") ??
                             TryGetString(segment, "speaker") ??
                             "unknown";

            if (string.IsNullOrWhiteSpace(text) && start == 0 && end == 0)
            {
                continue;
            }

            result.Add(new SpeakerSegment(
                StartSeconds: start,
                EndSeconds: end,
                SpeakerLabel: rawSpeaker,
                RawSpeakerLabel: rawSpeaker,
                Text: text ?? string.Empty));
        }

        return result;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static double TryGetDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return 0;
    }

    private static string TrimForLog(string value)
    {
        const int max = 300;
        return value.Length <= max ? value : value[..max];
    }

    private sealed record RawResponse(HttpStatusCode StatusCode, string? RequestId, string Body);

    private sealed record TranscriptionPayload(string Text, IReadOnlyList<SpeakerSegment> SpeakerSegments);
}
