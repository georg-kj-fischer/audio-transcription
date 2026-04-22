using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AudioInOutTranscribing.App.Audio;
using AudioInOutTranscribing.App.Transcription;

namespace AudioInOutTranscribing.Tests;

public sealed class MistralTranscriptionClientTests
{
    [Fact]
    public async Task TranscribeAsync_RetriesWithoutDiarization_WhenModelRejectsDiarization()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var wavPath = CreateTempWavFile(tempRoot);
            var handler = new SequencedHandler(
                CreateJsonResponse(HttpStatusCode.BadRequest, """{"message":"Diarization is not enabled for this model","code":"3051"}"""),
                CreateJsonResponse(HttpStatusCode.OK, """{"text":"hello from fallback"}"""));
            using var httpClient = new HttpClient(handler);
            var client = new MistralTranscriptionClient(httpClient, "test-key", "voxtral-mini-latest");

            var now = DateTimeOffset.UtcNow;
            var job = new ChunkJob("session-1", AudioSourceKind.Mic, 1, now, now.AddSeconds(30), wavPath, 0);

            var result = await client.TranscribeAsync(job, CancellationToken.None);

            Assert.Equal(2, handler.RequestBodies.Count);
            Assert.Equal(TranscriptionStatus.Success, result.Status);
            Assert.Equal("hello from fallback", result.Text);
            Assert.Contains("diarize", handler.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("diarize", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("timestamp_granularities", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TranscribeAsync_DisablesDiarizationForLaterRequests_AfterUnsupportedModelResponse()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var firstWavPath = CreateTempWavFile(tempRoot);
            var secondWavPath = CreateTempWavFile(tempRoot);

            var handler = new SequencedHandler(
                CreateJsonResponse(HttpStatusCode.BadRequest, """{"message":"Diarization is not enabled for this model","code":"3051"}"""),
                CreateJsonResponse(HttpStatusCode.OK, """{"text":"chunk 1"}"""),
                CreateJsonResponse(HttpStatusCode.OK, """{"text":"chunk 2"}"""));
            using var httpClient = new HttpClient(handler);
            var client = new MistralTranscriptionClient(httpClient, "test-key", "voxtral-mini-latest");

            var now = DateTimeOffset.UtcNow;
            var chunk1 = new ChunkJob("session-2", AudioSourceKind.Mic, 1, now, now.AddSeconds(30), firstWavPath, 0);
            var chunk2 = new ChunkJob("session-2", AudioSourceKind.Mic, 2, now.AddSeconds(30), now.AddSeconds(60), secondWavPath, 0);

            var result1 = await client.TranscribeAsync(chunk1, CancellationToken.None);
            var result2 = await client.TranscribeAsync(chunk2, CancellationToken.None);

            Assert.Equal(TranscriptionStatus.Success, result1.Status);
            Assert.Equal(TranscriptionStatus.Success, result2.Status);
            Assert.Equal(3, handler.RequestBodies.Count);
            Assert.Contains("diarize", handler.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("diarize", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("diarize", handler.RequestBodies[2], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "audio-transcriber-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateTempWavFile(string root)
    {
        var path = Path.Combine(root, $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3 });
        return path;
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

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequencedHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is null)
            {
                RequestBodies.Add(string.Empty);
            }
            else
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No mocked response is available.");
            }

            return _responses.Dequeue();
        }
    }
}
