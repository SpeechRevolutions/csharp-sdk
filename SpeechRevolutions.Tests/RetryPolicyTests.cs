using System.Net;
using System.Net.Sockets;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>
/// Retry policy, and the duplicate-job hazard it exists to prevent.
///
/// <para>
/// The rule under test: a request that CREATES a job is retried only when the
/// request provably never reached the server. Every other request retries
/// freely.
/// </para>
/// <para>
/// A regression here is expensive and silent — the customer gets two
/// transcripts and two charges for one file — so these assert attempt COUNTS,
/// not just the final outcome.
/// </para>
/// </summary>
public class RetryPolicyTests
{
    private const string OkBody =
        """{"job_id":"j1","upload_url":"u","download_url":"d","content_type":"audio/mpeg","expires_in":900}""";

    /// <summary>Replays a scripted list of outcomes and counts the attempts.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond;
        public int Calls { get; private set; }
        public List<string> Paths { get; } = new();

        public ScriptedHandler(Func<int, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            var response = _respond(Calls);
            Calls++;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (SpeechRevolutionsClient, ScriptedHandler) Client(Func<int, HttpResponseMessage> respond)
    {
        var handler = new ScriptedHandler(respond);
        var client = new SpeechRevolutionsClient(
            apiKey: "k",
            baseUrl: "http://localhost:1",
            httpClient: new HttpClient(handler),
            maxRetries: 3,
            retryBackoff: TimeSpan.FromMilliseconds(1));
        return (client, handler);
    }

    // -----------------------------------------------------------------------
    // Create calls: must NOT retry on an ambiguous failure
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task CreateDoesNotRetryOn5xx(HttpStatusCode code)
    {
        // A 5xx means the server saw the request. It may have created the job
        // before failing, so retrying risks a duplicate.
        var (client, handler) = Client(_ => Json(code, """{"detail":"boom"}"""));
        await Assert.ThrowsAnyAsync<SttException>(
            () => client.CreateUploadJobAsync(1024));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CreateDoesNotRetryOnAmbiguousTransportFailure()
    {
        // A reset after the connection was established is ambiguous.
        var handler = new ThrowingHandler(
            new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset)));
        var client = new SpeechRevolutionsClient(
            apiKey: "k", baseUrl: "http://localhost:1",
            httpClient: new HttpClient(handler), maxRetries: 3,
            retryBackoff: TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAnyAsync<SttException>(() => client.CreateUploadJobAsync(1024));
        Assert.Equal(1, handler.Calls);
    }

    // -----------------------------------------------------------------------
    // Create calls: SHOULD retry when the request provably never landed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateRetriesOn429()
    {
        // The server refused it outright — it did no work.
        var (client, handler) = Client(n => n == 0
            ? Json(HttpStatusCode.TooManyRequests, """{"detail":"slow down"}""")
            : Json(HttpStatusCode.OK, OkBody));

        var job = await client.CreateUploadJobAsync(1024);
        Assert.Equal("j1", job.JobId);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.TryAgain)]
    public async Task CreateRetriesWhenNoConnectionWasEstablished(SocketError error)
    {
        var handler = new ThrowingHandler(
            new HttpRequestException("no connection", new SocketException((int)error)));
        var client = new SpeechRevolutionsClient(
            apiKey: "k", baseUrl: "http://localhost:1",
            httpClient: new HttpClient(handler), maxRetries: 2,
            retryBackoff: TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAnyAsync<SttException>(() => client.CreateUploadJobAsync(1024));
        Assert.Equal(3, handler.Calls); // 1 initial + 2 retries
    }

    // -----------------------------------------------------------------------
    // Non-create calls keep the permissive behaviour
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task CancelRetriesOnTransientStatuses(HttpStatusCode code)
    {
        var (client, handler) = Client(n => n == 0
            ? Json(code, "{}")
            : Json(HttpStatusCode.OK, "{}"));

        await client.CancelJobAsync("j1");
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CompleteUploadIsRetryable()
    {
        // complete acts on a job id the caller already holds — replay is harmless.
        var (client, handler) = Client(n => n == 0
            ? Json(HttpStatusCode.InternalServerError, "{}")
            : Json(HttpStatusCode.OK, "{}"));

        await client.CompleteUploadAsync("j1");
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SafePathRetriesOnAmbiguousTransportFailure()
    {
        var handler = new ThrowingHandler(
            new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset)),
            succeedAfter: 1);
        var client = new SpeechRevolutionsClient(
            apiKey: "k", baseUrl: "http://localhost:1",
            httpClient: new HttpClient(handler), maxRetries: 3,
            retryBackoff: TimeSpan.FromMilliseconds(1));

        await client.CancelJobAsync("j1");
        Assert.Equal(2, handler.Calls);
    }

    // -----------------------------------------------------------------------
    // The scenario this whole policy exists for
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LostResponseCreatesExactlyOneJob()
    {
        // The server creates the job, then the response is lost to a gateway
        // 502. The SDK must surface the error rather than silently creating a
        // second job.
        var (client, handler) = Client(_ => Json(HttpStatusCode.BadGateway, """{"detail":"bad gateway"}"""));

        var ex = await Assert.ThrowsAnyAsync<SttException>(() => client.CreateUploadJobAsync(1024));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("/api/v1/upload", handler.Paths[0]);
    }

    /// <summary>Throws on every send until <c>succeedAfter</c> calls have been made.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        private readonly int _succeedAfter;
        public int Calls { get; private set; }

        public ThrowingHandler(Exception exception, int succeedAfter = int.MaxValue)
        {
            _exception = exception;
            _succeedAfter = succeedAfter;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls > _succeedAfter)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                });
            }
            throw _exception;
        }
    }
}
