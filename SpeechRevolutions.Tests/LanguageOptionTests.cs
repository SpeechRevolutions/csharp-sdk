using System.Net;
using System.Text.Json;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>
/// <see cref="TranscribeOptions.Language"/> on the wire: sent as <c>language</c>
/// on every job-creating request when set, and absent when null so an older
/// server sees exactly the request it always has.
/// </summary>
public class LanguageOptionTests
{
    private const string OkBody =
        """{"job_id":"j1","upload_url":"u","download_url":"d","content_type":"audio/mpeg","expires_in":900}""";

    /// <summary>Records each request's path and JSON body.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;
        public List<(string Path, string Body)> Requests { get; } = new();

        public CapturingHandler(Func<string, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((path, body));
            return _respond(path);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (SpeechRevolutionsClient, CapturingHandler) Client(Func<string, HttpResponseMessage> respond)
    {
        var handler = new CapturingHandler(respond);
        var client = new SpeechRevolutionsClient(
            apiKey: "k",
            baseUrl: "http://localhost:1",
            httpClient: new HttpClient(handler),
            maxRetries: 0,
            retryBackoff: TimeSpan.FromMilliseconds(1));
        return (client, handler);
    }

    private static JsonElement BodyOf(CapturingHandler handler, string path)
    {
        var (_, body) = handler.Requests.First(r => r.Path == path);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task SingleShotCreateSendsLanguageWhenSet()
    {
        var (client, handler) = Client(_ => Json(HttpStatusCode.OK, OkBody));
        await client.CreateUploadJobAsync(1024, new TranscribeOptions { Language = "ru" });

        var body = BodyOf(handler, "/api/v1/upload");
        Assert.Equal("ru", body.GetProperty("language").GetString());
    }

    [Fact]
    public async Task SingleShotCreateOmitsLanguageWhenNull()
    {
        var (client, handler) = Client(_ => Json(HttpStatusCode.OK, OkBody));
        await client.CreateUploadJobAsync(1024, new TranscribeOptions());

        var body = BodyOf(handler, "/api/v1/upload");
        Assert.False(body.TryGetProperty("language", out _));
    }

    [Theory]
    [InlineData("en")]
    [InlineData(null)]
    public async Task MultipartCreateAndFallbackCarryLanguage(string? language)
    {
        // Multipart create 404s (disabled server), so the client falls back to
        // the single-shot create; that one is refused to end the flow. Both
        // create bodies must agree on `language`.
        var (client, handler) = Client(path => path switch
        {
            "/api/v1/upload/multipart/create" => Json(HttpStatusCode.NotFound, """{"detail":"Not Found"}"""),
            _ => Json(HttpStatusCode.BadRequest, """{"detail":"stop"}"""),
        });

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.SubmitAsync(new byte[] { 1, 2, 3 }, new TranscribeOptions { Language = language }));

        foreach (var path in new[] { "/api/v1/upload/multipart/create", "/api/v1/upload" })
        {
            var body = BodyOf(handler, path);
            if (language is null)
                Assert.False(body.TryGetProperty("language", out _), path);
            else
                Assert.Equal(language, body.GetProperty("language").GetString());
        }
    }
}
