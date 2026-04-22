using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AudioInOutTranscribing.App.Transcription;

public sealed class MistralModelCatalogClient
{
    private static readonly Uri ModelsEndpoint = new("https://api.mistral.ai/v1/models");
    private static readonly Uri TranscriptionsEndpoint = new("https://api.mistral.ai/v1/audio/transcriptions");
    private static readonly string[] KnownDiarizationCapabilityNames =
    {
        "audio_transcription_diarization",
        "speaker_diarization",
        "audio_diarization",
        "diarization"
    };

    private readonly HttpClient _httpClient;

    public MistralModelCatalogClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<MistralModelCatalogResult> GetModelsAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return MistralModelCatalogResult.Failure("Missing Mistral API key.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = $"HTTP {(int)response.StatusCode} {response.StatusCode}: {TrimForDisplay(body)}";
                return MistralModelCatalogResult.Failure(message);
            }

            var models = ParseModels(body);
            if (models.Count == 0)
            {
                return MistralModelCatalogResult.Failure("No audio-transcription-capable models were returned by Mistral for this API key.");
            }

            return MistralModelCatalogResult.Success(models);
        }
        catch (Exception ex)
        {
            return MistralModelCatalogResult.Failure(ex.Message);
        }
    }

    public Task<MistralModelCatalogResult> GetModelIdsAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        return GetModelsAsync(apiKey, cancellationToken);
    }

    public async Task<MistralDiarizationProbeResult> ProbeDiarizationSupportAsync(
        string apiKey,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return MistralDiarizationProbeResult.Unverified("Missing Mistral API key.");
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return MistralDiarizationProbeResult.Unverified("Missing model id.");
        }

        try
        {
            using var form = new MultipartFormDataContent();
            using var wavContent = new ByteArrayContent(CreateSilentProbeWavBytes());
            wavContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(wavContent, "file", "diarization-probe.wav");
            form.Add(new StringContent(modelId), "model");
            form.Add(new StringContent("true"), "diarize");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");

            using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = form;

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if ((int)response.StatusCode is >= 200 and < 300)
            {
                return MistralDiarizationProbeResult.Verified(DiarizationSupport.Supported);
            }

            if (IsUnsupportedDiarizationResponse(response.StatusCode, body))
            {
                return MistralDiarizationProbeResult.Verified(DiarizationSupport.NotSupported);
            }

            var error = $"HTTP {(int)response.StatusCode} {response.StatusCode}: {TrimForDisplay(body)}";
            return MistralDiarizationProbeResult.Unverified(error);
        }
        catch (Exception ex)
        {
            return MistralDiarizationProbeResult.Unverified(ex.Message);
        }
    }

    public static IReadOnlyList<string> ParseModelIds(string json)
    {
        return ParseModels(json)
            .Select(model => model.Id)
            .ToList();
    }

    public static IReadOnlyList<MistralModelInfo> ParseModels(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<MistralModelInfo>();
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MistralModelInfo>();
        }

        var modelsById = new Dictionary<string, MistralModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idProperty) ||
                idProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!IsAudioTranscriptionCapable(item))
            {
                continue;
            }

            var id = idProperty.GetString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                var normalizedId = id.Trim();
                var support = ResolveDiarizationSupport(item);
                if (!modelsById.TryGetValue(normalizedId, out var existing))
                {
                    modelsById[normalizedId] = new MistralModelInfo(normalizedId, support);
                }
                else
                {
                    // For duplicate aliases in API results, keep the strongest signal:
                    // Supported > Unknown > NotSupported.
                    if (Score(support) > Score(existing.DiarizationSupport))
                    {
                        modelsById[normalizedId] = existing with { DiarizationSupport = support };
                    }
                }
            }
        }

        return modelsById.Values
            .OrderByDescending(model => Score(model.DiarizationSupport))
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsAudioTranscriptionCapable(JsonElement modelElement)
    {
        if (!modelElement.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // For this app we only want models supported by /v1/audio/transcriptions.
        if (TryGetCapability(capabilities, "audio_transcription", out var batchTranscription))
        {
            return batchTranscription;
        }

        // Conservative fallback for potential schema differences.
        if (TryGetCapability(capabilities, "transcription", out var genericTranscription))
        {
            return genericTranscription;
        }

        return false;
    }

    private static bool TryGetCapability(JsonElement capabilities, string capabilityName, out bool value)
    {
        value = false;
        if (capabilities.TryGetProperty(capabilityName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return false;
    }

    private static string TrimForDisplay(string input)
    {
        const int maxLength = 400;
        return input.Length <= maxLength ? input : input[..maxLength];
    }

    private static DiarizationSupport ResolveDiarizationSupport(JsonElement modelElement)
    {
        if (!modelElement.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object)
        {
            return DiarizationSupport.Unknown;
        }

        foreach (var capabilityName in KnownDiarizationCapabilityNames)
        {
            if (TryGetCapability(capabilities, capabilityName, out var supported))
            {
                return supported ? DiarizationSupport.Supported : DiarizationSupport.NotSupported;
            }
        }

        var sawDiarizationFlag = false;
        var anyTrue = false;
        foreach (var property in capabilities.EnumerateObject())
        {
            if (!property.Name.Contains("diar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                continue;
            }

            sawDiarizationFlag = true;
            anyTrue |= property.Value.GetBoolean();
        }

        if (sawDiarizationFlag)
        {
            return anyTrue ? DiarizationSupport.Supported : DiarizationSupport.NotSupported;
        }

        return DiarizationSupport.Unknown;
    }

    private static int Score(DiarizationSupport support)
    {
        return support switch
        {
            DiarizationSupport.Supported => 2,
            DiarizationSupport.Unknown => 1,
            _ => 0
        };
    }

    private static bool IsUnsupportedDiarizationResponse(HttpStatusCode statusCode, string responseBody)
    {
        if (statusCode != HttpStatusCode.BadRequest || string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        if (responseBody.Contains("Diarization is not enabled for this model", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("\"code\":\"3051\"", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("\"code\":3051", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            return ContainsDiarizationDisabledSignal(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsDiarizationDisabledSignal(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String &&
            (message.GetString() ?? string.Empty).Contains("Diarization is not enabled for this model", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (element.TryGetProperty("code", out var code))
        {
            if (code.ValueKind == JsonValueKind.String &&
                string.Equals(code.GetString(), "3051", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var codeNumber) &&
                codeNumber == 3051)
            {
                return true;
            }
        }

        if (element.TryGetProperty("error", out var nestedError))
        {
            return ContainsDiarizationDisabledSignal(nestedError);
        }

        return false;
    }

    private static byte[] CreateSilentProbeWavBytes()
    {
        const int sampleRate = 16000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int seconds = 1;
        const short blockAlign = (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        var dataSize = sampleRate * blockAlign * seconds;
        var buffer = new byte[44 + dataSize];

        WriteAscii(buffer, 0, "RIFF");
        WriteInt32(buffer, 4, 36 + dataSize);
        WriteAscii(buffer, 8, "WAVE");
        WriteAscii(buffer, 12, "fmt ");
        WriteInt32(buffer, 16, 16);
        WriteInt16(buffer, 20, 1);
        WriteInt16(buffer, 22, channels);
        WriteInt32(buffer, 24, sampleRate);
        WriteInt32(buffer, 28, byteRate);
        WriteInt16(buffer, 32, blockAlign);
        WriteInt16(buffer, 34, bitsPerSample);
        WriteAscii(buffer, 36, "data");
        WriteInt32(buffer, 40, dataSize);
        return buffer;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            buffer[offset + i] = (byte)value[i];
        }
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}

public enum DiarizationSupport
{
    NotSupported = 0,
    Unknown = 1,
    Supported = 2
}

public sealed record MistralModelInfo(string Id, DiarizationSupport DiarizationSupport);

public sealed record MistralModelCatalogResult(bool IsSuccess, IReadOnlyList<MistralModelInfo> Models, string? Error)
{
    public IReadOnlyList<string> ModelIds => Models.Select(model => model.Id).ToList();

    public static MistralModelCatalogResult Success(IReadOnlyList<MistralModelInfo> models)
        => new(true, models, null);

    public static MistralModelCatalogResult Failure(string error)
        => new(false, Array.Empty<MistralModelInfo>(), error);
}

public sealed record MistralDiarizationProbeResult(bool IsVerified, DiarizationSupport Support, string? Error)
{
    public static MistralDiarizationProbeResult Verified(DiarizationSupport support)
        => new(true, support, null);

    public static MistralDiarizationProbeResult Unverified(string error)
        => new(false, DiarizationSupport.Unknown, error);
}
