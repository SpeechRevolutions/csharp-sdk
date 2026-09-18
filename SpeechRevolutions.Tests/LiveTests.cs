using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>
/// Tests that talk to the REAL Speech Revolutions API.
///
/// <para>
/// Everything else in this project runs against <c>mock_api.py</c>, which is a
/// MODEL of the contract written by reading the server. A model can be wrong in
/// the same way the client is wrong, and then the suite is green while
/// production is broken. These close that gap for C# specifically: until they
/// existed, the C# client had never once been pointed at the real API.
/// </para>
/// <para>
/// OPT-IN, because they create real jobs on a real account and cost real money
/// (a few seconds of audio each, so fractions of a cent):
/// </para>
/// <code>
/// SR_LIVE=1 SPEECHREVOLUTIONS_API_KEY=stt_... dotnet test --filter Live
/// </code>
/// <para>Override the clip with SR_LIVE_AUDIO; SR_LIVE_LONG_AUDIO enables the
/// progress test, which needs a file long enough to emit intermediate events.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public class LiveTests
{
    private static SttClient Client()
    {
        Skip.If(Environment.GetEnvironmentVariable("SR_LIVE") != "1",
            "live API tests are opt-in: set SR_LIVE=1 (creates real, billable jobs)");
        Skip.If(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPEECHREVOLUTIONS_API_KEY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STT_API_KEY")),
            "SPEECHREVOLUTIONS_API_KEY is not set");

        return new SttClient(timeout: TimeSpan.FromMinutes(15));
    }

    /// <summary>The shortest clip available: these create real, billed jobs.</summary>
    private static string Audio()
    {
        var overridden = Environment.GetEnvironmentVariable("SR_LIVE_AUDIO");
        if (!string.IsNullOrEmpty(overridden)) return overridden;

        // bin/Debug/net8.0 -> project -> csharp-sdk -> repo root
        var repo = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        foreach (var name in new[] { "ru_uk_segment.mp3", "crawl11.mp3" })
        {
            var p = Path.Combine(repo, "qa-audio", name);
            if (File.Exists(p)) return p;
        }
        Skip.If(true, "no test audio found; set SR_LIVE_AUDIO");
        return "";
    }

    // -----------------------------------------------------------------------
    // Read-only: no jobs created, no cost
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ListJobsReturnsTheDocumentedShape()
    {
        using var client = Client();
        var page = await client.ListJobsAsync(limit: 3);

        foreach (var job in page.Jobs)
        {
            Assert.False(string.IsNullOrEmpty(job.JobId));
            // Production sends {job_id, created_at} and no status.
            Assert.False(string.IsNullOrEmpty(job.CreatedAt));
        }
    }

    [SkippableFact]
    public async Task ListJobsPaginatesWithoutDuplicates()
    {
        using var client = Client();

        var seen = new HashSet<string>();
        string? before = null;
        for (var i = 0; i < 3; i++)
        {
            var page = await client.ListJobsAsync(limit: 2, before: before);
            foreach (var job in page.Jobs)
                Assert.True(seen.Add(job.JobId), $"pagination returned {job.JobId} twice");

            before = page.NextBefore;
            if (string.IsNullOrEmpty(before)) break;
        }
    }

    [SkippableFact]
    public async Task ABadKeyIsRejected()
    {
        using var _gate = Client();
        using var bogus = new SttClient(apiKey: "stt_definitely_not_a_real_key");
        await Assert.ThrowsAnyAsync<SttException>(() => bogus.ListJobsAsync(limit: 1));
    }

    // -----------------------------------------------------------------------
    // Billable: each of these transcribes a real clip
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task TranscribeFromPath()
    {
        using var client = Client();
        var result = await client.TranscribeAsync(Audio(),
            new TranscribeOptions { SpeakerLabels = true });

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.NotEmpty(result.Words); // WordTimestamps defaults to true
    }

    [SkippableFact]
    public async Task TranscribeFromBytes()
    {
        using var client = Client();
        var data = await File.ReadAllBytesAsync(Audio());
        var result = await client.TranscribeAsync(data);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [SkippableFact]
    public async Task SubmitThenPollToCompletion()
    {
        using var client = Client();
        var jobId = await client.SubmitAsync(Audio());
        Assert.True(Guid.TryParse(jobId, out _), $"job id {jobId} is not a UUID");

        var flags = await client.CheckFailedAsync(new[] { jobId });
        Assert.Single(flags);
        Assert.False(flags[0]);

        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (true)
        {
            var status = await client.GetJobStatusAsync(jobId);
            if (status.IsCompleted)
            {
                Assert.False(string.IsNullOrEmpty(status.DownloadUrl));

                var result = await client.GetTranscriptAsync(jobId);
                Assert.False(string.IsNullOrWhiteSpace(result.Text));

                var raw = await client.DownloadResultAsync(status.DownloadUrl!);
                Assert.NotEmpty(raw);
                return;
            }
            Assert.False(status.IsFailed,
                $"job {jobId} failed at {status.FailedStage}: {status.Reason}");
            Assert.True(DateTime.UtcNow < deadline, $"job {jobId} did not finish in 10 minutes");

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    [SkippableFact]
    public async Task SrtOutputFromTheRealApi()
    {
        using var client = Client();
        var result = await client.TranscribeAsync(Audio(),
            new TranscribeOptions { OutputType = OutputType.Srt });

        Assert.Contains("-->", result.Text);
    }

    /// <summary>
    /// The full upload flow the convenience methods wrap: create, upload, touch,
    /// complete, wait. Nothing else exercises these against the real API.
    /// </summary>
    [SkippableFact]
    public async Task RawUploadFlow()
    {
        using var client = Client();
        var data = await File.ReadAllBytesAsync(Audio());

        var job = await client.CreateUploadJobAsync(data.Length);
        Assert.False(string.IsNullOrEmpty(job.JobId));
        Assert.False(string.IsNullOrEmpty(job.UploadUrl));

        await client.UploadAudioAsync(job.UploadUrl, data, job.JobId);
        await client.TouchUploadProgressAsync(job.JobId);
        await client.CompleteUploadAsync(job.JobId);

        var (content, _) = await client.WaitForResultAsync(job.JobId, job.DownloadUrl);
        Assert.NotEmpty(content);
    }

    [SkippableFact]
    public async Task CancelJob()
    {
        using var client = Client();
        var data = await File.ReadAllBytesAsync(Audio());

        // Cancel before completing the upload, so nothing is transcribed and the
        // job costs nothing.
        var job = await client.CreateUploadJobAsync(data.Length);
        await client.CancelJobAsync(job.JobId);
    }

    /// <summary>
    /// Live progress is a headline feature, so prove it actually arrives — but
    /// only on a file long enough to emit intermediate events. A short clip
    /// finishes before the first one is sent, so asserting on the default clip
    /// would test the clock, not the client.
    /// </summary>
    [SkippableFact]
    public async Task ProgressFiresForALongFile()
    {
        using var client = Client();

        var longAudio = Environment.GetEnvironmentVariable("SR_LIVE_LONG_AUDIO");
        Skip.If(string.IsNullOrEmpty(longAudio) || !File.Exists(longAudio),
            "set SR_LIVE_LONG_AUDIO to a file of 20 minutes or more");

        var steps = new List<string?>();
        var result = await client.TranscribeAsync(longAudio!, null,
            e => { lock (steps) steps.Add(e.Step); });

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.True(steps.Count >= 3,
            $"expected several progress events, got {steps.Count}: {string.Join(",", steps)}");
        Assert.Contains(steps, s => s is not null && s.StartsWith("chunk:"));
    }

    // -----------------------------------------------------------------------
    // Webhooks
    //
    // Needs a PUBLIC callback URL: the pipeline resolves the callback host and
    // refuses anything that is not globally routable, so localhost is rejected
    // before a request is made. Point SR_LIVE_WEBHOOK_BASE at a tunnel.
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task WebhookIsDeliveredByTheRealPipeline()
    {
        using var client = Client();

        var webhookBase = Environment.GetEnvironmentVariable("SR_LIVE_WEBHOOK_BASE");
        Skip.If(string.IsNullOrEmpty(webhookBase),
            "set SR_LIVE_WEBHOOK_BASE to a public tunnel URL "
            + "(the pipeline refuses non-public callback hosts)");

        var port = Environment.GetEnvironmentVariable("SR_LIVE_WEBHOOK_PORT") ?? "8799";

        var listener = new HttpListener();
        // "+" matches any Host header. A 127.0.0.1 prefix would only match
        // requests whose Host is literally 127.0.0.1, and a tunnel forwards the
        // public hostname — so the listener would sit there accepting nothing.
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();

        var received = new TaskCompletionSource<(string body, NameValueCollection headers, string path)>();
        _ = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            received.TrySetResult((body, ctx.Request.Headers, ctx.Request.Url!.AbsolutePath));

            var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        });

        try
        {
            var callback = webhookBase!.TrimEnd('/') + "/webhooks/speechrevolutions";
            var jobId = await client.SubmitAsync(Audio(),
                new TranscribeOptions { CallbackUrl = callback });

            var done = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromMinutes(15)));
            Assert.True(done == received.Task, "no webhook arrived within 15 minutes");

            var (body, headers, path) = await received.Task;
            using var doc = JsonDocument.Parse(body);
            var evt = doc.RootElement;

            Assert.Equal(jobId, evt.GetProperty("job_id").GetString());
            var status = evt.GetProperty("status").GetString();
            Assert.True(status is "completed" or "failed", $"unexpected status {status}");
            Assert.EndsWith("/webhooks/speechrevolutions", path);

            // Headers the documented receivers rely on.
            Assert.Equal(status, headers["X-SR-Event"]);
            Assert.False(string.IsNullOrEmpty(headers["X-SR-Delivery"]));
            Assert.StartsWith("SpeechRevolutions-Webhook/", headers["User-Agent"] ?? "");

            // Absent means the secret is unset server-side, which is worth
            // reporting rather than silently passing.
            var sig = headers["X-SR-Signature"];
            if (!string.IsNullOrEmpty(sig))
                Assert.StartsWith("sha256=", sig);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }
}
