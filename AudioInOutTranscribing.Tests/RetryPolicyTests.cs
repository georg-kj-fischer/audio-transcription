using System.Net;
using AudioInOutTranscribing.App.Transcription;

namespace AudioInOutTranscribing.Tests;

public sealed class RetryPolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData((HttpStatusCode)429, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void IsRetryableStatusCode_UsesExpectedClassification(HttpStatusCode code, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.IsRetryableStatusCode(code));
    }

    [Fact]
    public void GetDelayForAttempt_UsesExponentialBackoffWithCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.GetDelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(4), RetryPolicy.GetDelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(8), RetryPolicy.GetDelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(16), RetryPolicy.GetDelayForAttempt(4));
        Assert.Equal(TimeSpan.FromSeconds(32), RetryPolicy.GetDelayForAttempt(5));
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.GetDelayForAttempt(6));
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.GetDelayForAttempt(10));
    }
}
