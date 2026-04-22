using System.Net;

namespace AudioInOutTranscribing.App.Transcription;

public static class RetryPolicy
{
    public static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout ||
               (int)statusCode == 429 ||
               (int)statusCode >= 500;
    }

    public static bool IsRetryableException(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException or IOException;
    }

    public static TimeSpan GetDelayForAttempt(int attemptNumber)
    {
        if (attemptNumber <= 0)
        {
            return TimeSpan.FromSeconds(2);
        }

        if (attemptNumber <= 5)
        {
            var seconds = (int)Math.Pow(2, attemptNumber);
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(60);
    }
}
