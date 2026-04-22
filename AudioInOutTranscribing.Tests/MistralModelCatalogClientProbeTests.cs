using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AudioInOutTranscribing.App.Transcription;

namespace AudioInOutTranscribing.Tests;

public sealed class MistralModelCatalogClientProbeTests
{
    [Fact]
    public async Task ProbeDiarizationSupportAsync_ReturnsVerifiedSupported_OnSuccess()
    {
        var handler = new SingleResponseHandler(CreateJsonResponse(HttpStatusCode.OK, """{"text":""}"""));
        using var httpClient = new HttpClient(handler);
        var client = new MistralModelCatalogClient(httpClient);

        var result = await client.ProbeDiarizationSupportAsync("test-key", "voxtral-mini-latest", CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal(DiarizationSupport.Supported, result.Support);
        Assert.Null(result.Error);
        Assert.Contains("diarize", handler.RequestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeDiarizationSupportAsync_ReturnsVerifiedNotSupported_OnKnownDiarizationError()
    {
        var handler = new SingleResponseHandler(CreateJsonResponse(
            HttpStatusCode.BadRequest,
            """{"message":"Diarization is not enabled for this model","code":"3051"}"""));
        using var httpClient = new HttpClient(handler);
        var client = new MistralModelCatalogClient(httpClient);

        var result = await client.ProbeDiarizationSupportAsync("test-key", "voxtral-mini-transcribe-2507", CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal(DiarizationSupport.NotSupported, result.Support);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ProbeDiarizationSupportAsync_ReturnsUnverified_OnUnexpectedError()
    {
        var handler = new SingleResponseHandler(CreateJsonResponse(HttpStatusCode.InternalServerError, """{"message":"oops"}"""));
        using var httpClient = new HttpClient(handler);
        var client = new MistralModelCatalogClient(httpClient);

        var result = await client.ProbeDiarizationSupportAsync("test-key", "voxtral-mini-latest", CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal(DiarizationSupport.Unknown, result.Support);
        Assert.NotNull(result.Error);
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            }
        };
    }

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public SingleResponseHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
