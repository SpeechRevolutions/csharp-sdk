using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>
/// The published surface, end to end against a real server.
///
/// <para>
/// The retry tests use a stub <c>HttpMessageHandler</c>; these drive the API
/// mock from the python-sdk checkout — the same reference implementation the
/// Python suites, the Node suite and the cookbook tests use, so none of them
/// can drift from each other or from the contract.
/// </para>
/// <para>
/// Shapes here match production, verified live on 2026-09-18: job ids are
/// UUIDs, a job summary is {job_id, created_at} with no status, the list cursor
/// is a created_at timestamp, and a completion webhook carries a presigned
/// download_url alongside duration_seconds and rtf.
/// </para>
/// <para>Skipped when the mock is not found. Override with SR_MOCK_API.</para>
/// </summary>
public class FunctionalTests
{
    private static string MockPath()
    {
        var env = Environment.GetEnvironmentVariable("SR_MOCK_API");
        if (!string.IsNullOrEmpty(env)) return env;
        var dir = AppContext.BaseDirectory;
        // bin/Debug/net8.0 -> project -> repo -> side-by-side python-sdk
        return Path.GetFullPath(Path.Combine(
            dir, "..", "..", "..", "..", "..", "python-sdk", "tests", "mock_api.py"));
    }

    private static string PythonBin() =>
        Environment.GetEnvironmentVariable("SR_PYTHON") ?? "python3";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Boots mock_api.py and waits for it to answer.</summary>
    private sealed class Mock : IDisposable
    {
        public string Base { get; }
        private readonly Process _proc;

        public Mock(params string[] extra)
        {
            var path = MockPath();
            Skip.IfNot(File.Exists(path), $"API mock not found at {path}");

            var port = FreePort();
            var args = new List<string>
            {
                path, "--port", port.ToString(), "--api-key", "test-key",
                "--progress-steps", "2",
            };
            args.AddRange(extra);

            var psi = new ProcessStartInfo(PythonBin())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            _proc = Process.Start(psi)!;

            Base = $"http://127.0.0.1:{port}";
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            probe.DefaultRequestHeaders.Add("X-API-Key", "test-key");
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    probe.GetAsync($"{Base}/api/v1/jobs").GetAwaiter().GetResult();
                    return;
                }
                catch { Thread.Sleep(150); }
            }
            Dispose();
            throw new Xunit.Sdk.XunitException("mock never came up");
        }

        public void Dispose()
        {
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
            _proc.Dispose();
        }
    }

    private static SttClient Client(string baseUrl) =>
        new(apiKey: "test-key", baseUrl: baseUrl,
            retryBackoff: TimeSpan.FromMilliseconds(1));

    private static byte[] Audio() => Encoding.UTF8.GetBytes(new string('x', 4096));

    // -----------------------------------------------------------------------
    // Base URL resolution
    // -----------------------------------------------------------------------

    [Fact]
    public void BaseUrlComesFromTheEnvironmentWhenNotGiven()
    {
        Environment.SetEnvironmentVariable("SPEECHREVOLUTIONS_BASE_URL", "https://staging.example/");
        try
        {
            Assert.Equal("https://staging.example", new SttClient("k").BaseUrl);
            // An explicit value still wins.
            Assert.Equal("https://explicit.example",
                new SttClient("k", baseUrl: "https://explicit.example").BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPEECHREVOLUTIONS_BASE_URL", null);
        }
    }

    [Fact]
    public void BaseUrlDefaultsToProduction()
    {
        Environment.SetEnvironmentVariable("SPEECHREVOLUTIONS_BASE_URL", null);
        Environment.SetEnvironmentVariable("STT_BASE_URL", null);
        Assert.Equal("https://api.speechrevolutions.com", new SttClient("k").BaseUrl);
    }

    // -----------------------------------------------------------------------
    // Uploads and transcripts
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task TranscribeBytesReturnsAParsedTranscript()
    {
        using var mock = new Mock();
        using var c = Client(mock.Base);
        var r = await c.TranscribeAsync(Audio());

        Assert.Equal("Good morning everyone. Thanks for joining.", r.Text);
        Assert.Equal(6, r.Words.Count);
        Assert.Equal(2, r.Utterances.Count);
        Assert.Equal("A", r.Utterances[0].Speaker);
        Assert.Equal("B", r.Utterances[1].Speaker);
    }

    [SkippableFact]
    public async Task TranscribeUrlIsFetchedServerSide()
    {
        using var mock = new Mock();
        using var c = Client(mock.Base);
        var r = await c.TranscribeAsync("https://example.com/a.mp3");
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
    }

    [SkippableFact]
    public async Task SrtOutput()
    {
        using var mock = new Mock();
        using var c = Client(mock.Base);
        var r = await c.TranscribeAsync(Audio(),
            new TranscribeOptions { OutputType = OutputType.Srt });
        Assert.Contains("-->", r.Text);
    }

    // -----------------------------------------------------------------------
    // Jobs API
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task SubmitReturnsAUuid()
    {
        using var mock = new Mock();
        using var c = Client(mock.Base);
        var id = await c.SubmitAsync("https://example.com/a.mp3");
        Assert.True(Guid.TryParse(id, out _), $"job id {id} is not a UUID");
    }

    [SkippableFact]
    public async Task GetJobStatusAndTranscript()
    {
        using var mock = new Mock();
        using var c = Client(mock.Base);
        var id = await c.SubmitAsync("https://example.com/a.mp3");

        var status = await c.GetJobStatusAsync(id);
        Assert.Equal("completed", status.Status);
        Assert.False(string.IsNullOrEmpty(status.DownloadUrl));

        var t = await c.GetTranscriptAsync(id);
        Assert.False(string.IsNullOrWhiteSpace(t.Text));
    }

    [SkippableFact]
    public async Task ListJobsPaginatesWithoutDuplicates()
    {
        using var mock = new Mock();
        using var c = Client(mock.Base);
        for (var i = 0; i < 5; i++)
            await c.SubmitAsync($"https://example.com/{i}.mp3");

        var seen = new HashSet<string>();
        string? before = null;
        for (var i = 0; i < 10; i++)
        {
            var page = await c.ListJobsAsync(limit: 2, before: before);
            foreach (var j in page.Jobs)
            {
                Assert.True(seen.Add(j.JobId), $"pagination returned {j.JobId} twice");
                // Production JobSummary is {job_id, created_at}; no status.
                Assert.False(string.IsNullOrEmpty(j.CreatedAt));
            }
            before = page.NextBefore;
            if (string.IsNullOrEmpty(before)) break;
        }
        Assert.Equal(5, seen.Count);
    }

    [SkippableFact]
    public async Task CheckFailedAndCancel()
    {
        using var mock = new Mock();
        using var c = Client(mock.Base);
        var id = await c.SubmitAsync("https://example.com/a.mp3");

        var flags = await c.CheckFailedAsync(new[] { id });
        Assert.Single(flags);
        Assert.False(flags[0]);

        var job = await c.CreateUploadJobAsync(1024);
        await c.CancelJobAsync(job.JobId);
    }

    [SkippableFact]
    public async Task BadApiKeyIsRejected()
    {
        using var mock = new Mock();
        using var c = new SttClient(apiKey: "wrong-key", baseUrl: mock.Base,
            retryBackoff: TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<AuthenticationException>(() => c.ListJobsAsync(limit: 1));
    }

    // -----------------------------------------------------------------------
    // Failures and fallbacks
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task AFailedJobSurfacesItsStep()
    {
        using var mock = new Mock("--fail-at", "gpu_timestamps");
        using var c = Client(mock.Base);
        var ex = await Assert.ThrowsAnyAsync<SttException>(
            () => c.TranscribeAsync(Audio()));
        Assert.Contains("gpu_timestamps", ex.ToString());
    }

    [SkippableFact]
    public async Task ARefusedStreamFallsBackToPollingQuickly()
    {
        // A proxy that will not pass text/event-stream answers 503 forever. The
        // client must still deliver, and must not burn the whole ladder first.
        using var mock = new Mock("--stream-status", "503");
        using var c = Client(mock.Base);

        var sw = Stopwatch.StartNew();
        var r = await c.TranscribeAsync(Audio());
        sw.Stop();

        Assert.False(string.IsNullOrWhiteSpace(r.Text));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(12),
            $"refused stream took {sw.Elapsed.TotalSeconds:F1}s; the full ladder would be 30s");
    }

    // -----------------------------------------------------------------------
    // Webhooks
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task WebhookIsDelivered()
    {
        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var received = new TaskCompletionSource<(string body, System.Collections.Specialized.NameValueCollection headers)>();
        _ = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            received.TrySetResult((body, ctx.Request.Headers));
            var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        });

        try
        {
            using var mock = new Mock("--webhook-secret", "whsec_csharp_functional");
            using var c = Client(mock.Base);
            var id = await c.SubmitAsync("https://example.com/a.mp3",
                new TranscribeOptions { CallbackUrl = $"http://127.0.0.1:{port}/webhooks" });

            var done = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(done == received.Task, "no webhook arrived");

            var (body, headers) = await received.Task;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Assert.Equal(id, root.GetProperty("job_id").GetString());
            Assert.Equal("completed", root.GetProperty("status").GetString());
            // Verified live: the completion carries a presigned download_url, so
            // a receiver needs no second call.
            Assert.StartsWith("http", root.GetProperty("download_url").GetString());

            Assert.Equal("completed", headers["X-SR-Event"]);
            Assert.False(string.IsNullOrEmpty(headers["X-SR-Delivery"]));
            Assert.StartsWith("sha256=", headers["X-SR-Signature"]);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }
}
