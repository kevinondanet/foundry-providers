using Azure;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Tests;

public class RetryTests
{
    [Fact]
    public void should_retry_classification()
    {
        var api = Fixtures.Api();

        var rateLimited = api.ShouldRetry(Fixtures.Http(429, "slow down", new Dictionary<string, string> { ["Retry-After"] = "7" }));
        Assert.Equal(RetryDecision.RateLimit(7.0), rateLimited);

        Assert.Equal(RetryDecision.Transient(), api.ShouldRetry(Fixtures.Http(503, "unavailable")));
        Assert.Equal(RetryDecision.Transient(2.5), api.ShouldRetry(Fixtures.Http(408, "timeout", new Dictionary<string, string> { ["retry-after"] = "2.5" })));
        Assert.Equal(RetryDecision.No(), api.ShouldRetry(Fixtures.Http(400, "bad")));
        Assert.Equal(RetryDecision.No(), api.ShouldRetry(Fixtures.Http(401, "unauthorized")));
        Assert.Equal(RetryDecision.No(), api.ShouldRetry(new RequestFailedException("connection refused")));
        Assert.Equal(RetryDecision.Transient(), api.ShouldRetry(new ServiceResponseException("read timeout")));
        Assert.Equal(RetryDecision.No(), api.ShouldRetry(new ArgumentException("x")));

        Assert.True(api.IsAuthFailure(Fixtures.Http(401, "unauthorized")));
        Assert.False(api.IsAuthFailure(Fixtures.Http(403, "forbidden")));
        Assert.False(api.IsAuthFailure(new ArgumentException("x")));
    }

    [Fact]
    public void as_azure_error_normalises_sdk_transport_failures()
    {
        var http = Fixtures.Http(503, "x");
        Assert.Same(http, AzureAIModelApi.AsAzureError(http));
        var connection = new RequestFailedException("connection refused");
        Assert.Same(connection, AzureAIModelApi.AsAzureError(connection));
        var response = new ServiceResponseException("read timeout");
        Assert.Same(response, AzureAIModelApi.AsAzureError(response));

        var io = new IOException("socket reset");
        var mapped = Assert.IsType<ServiceResponseException>(AzureAIModelApi.AsAzureError(io));
        Assert.Same(io, mapped.InnerException);
        Assert.Equal("socket reset", mapped.Message);

        var timeout = new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout of 0:01:40.");
        Assert.IsType<ServiceResponseException>(AzureAIModelApi.AsAzureError(timeout, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(AzureAIModelApi.AsAzureError(new OperationCanceledException(cts.Token), cts.Token));

        var exhausted = new AggregateException("Retry failed after 3 tries.", [connection, io, timeout]);
        Assert.IsType<ServiceResponseException>(AzureAIModelApi.AsAzureError(exhausted));
        Assert.Same(connection, AzureAIModelApi.AsAzureError(new AggregateException([io, connection])));
        Assert.Null(AzureAIModelApi.AsAzureError(new AggregateException([new ArgumentException("x")])));
        Assert.Null(AzureAIModelApi.AsAzureError(new AggregateException()));

        Assert.Null(AzureAIModelApi.AsAzureError(new ArgumentException("x")));
        Assert.Null(AzureAIModelApi.AsAzureError(new InvalidOperationException("Streaming response ended without delivering any chunks.")));
        Assert.Null(AzureAIModelApi.AsAzureError(new System.Text.Json.JsonException("bad chunk")));
    }

    [Fact]
    public void retry_decision_factories()
    {
        Assert.False(RetryDecision.No().Retry);
        Assert.Equal(new RetryDecision(true, RetryKind.Transient, null), RetryDecision.Transient());
        Assert.Equal(new RetryDecision(true, RetryKind.RateLimit, 2.5), RetryDecision.RateLimit(2.5));
        Assert.Equal("retry (rate_limit, retry_after=2.5)", RetryDecision.RateLimit(2.5).ToString());
        Assert.Equal("no retry", RetryDecision.No().ToString());
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    public void is_retryable_http_status(int status, bool expected) => Assert.Equal(expected, HttpRetryUtil.IsRetryableHttpStatus(status));

    [Fact]
    public void parse_retry_after_formats()
    {
        static double? Parse(params (string, string)[] headers) =>
            HttpRetryUtil.ParseRetryAfter(headers.Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)));

        Assert.Equal(7.0, Parse(("Retry-After", "7")));
        Assert.Equal(90.0, Parse(("retry-after", "1m30s")));
        Assert.Equal(0.5, Parse(("Retry-After", "500ms")));
        Assert.Equal(5.0, Parse(("x-ratelimit-reset-requests", "2s"), ("x-ratelimit-reset-tokens", "5s")));
        Assert.Equal(5.0, Parse(("Retry-After", "garbage"), ("x-ratelimit-reset-tokens", "5s")));
        Assert.Null(Parse(("Retry-After", "inf")));
        Assert.Null(Parse(("Retry-After", "-1")));
        Assert.Null(Parse(("Retry-After", "")));
        Assert.Null(Parse());

        HttpRetryUtil.UtcNow = () => new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        try
        {
            Assert.Equal(60.0, Parse(("Retry-After", "Wed, 02 Sep 2026 12:01:00 GMT")));
            Assert.Equal(120.0, Parse(("Retry-After", "2026-09-02T12:02:00Z")));
            Assert.Null(Parse(("Retry-After", "Wed, 02 Sep 2026 11:00:00 GMT")));
        }
        finally
        {
            HttpRetryUtil.UtcNow = () => DateTimeOffset.UtcNow;
        }
    }

    [Fact]
    public void status_code_of_reads_request_failed_exceptions()
    {
        Assert.Equal(503, HttpRetryUtil.StatusCodeOf(Fixtures.Http(503, "x")));
        Assert.Null(HttpRetryUtil.StatusCodeOf(new RequestFailedException("transport")));
        Assert.Null(HttpRetryUtil.StatusCodeOf(new ArgumentException("x")));
    }
}
