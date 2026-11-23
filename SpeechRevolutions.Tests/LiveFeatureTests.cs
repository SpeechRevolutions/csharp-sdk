using SpeechRevolutions;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>
/// Live tests for 1.1 against the REAL API: add-on options, DeleteJobAsync and real-time
/// streaming, including a cut connection that must resume. Opt-in (bills a few minutes of audio):
/// <code>SR_LIVE=1 SPEECHREVOLUTIONS_API_KEY=stt_... dotnet test --filter LiveFeature</code>
/// </summary>
public class LiveFeatureTests
{
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "Assets");
    private static string Call => Path.Combine(Assets, "call.mp3");     // bank call, "Northwind Bank"
    private static string NoName => Path.Combine(Assets, "noname.mp3"); // two speakers, no names said
    private static string Prof => Path.Combine(Assets, "prof.mp3");     // three strong swear words

    private static SpeechRevolutionsClient Live()
    {
        Skip.If(Environment.GetEnvironmentVariable("SR_LIVE") != "1", "live API tests are opt-in: set SR_LIVE=1");
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPEECHREVOLUTIONS_API_KEY")), "no API key");
        return new SpeechRevolutionsClient();
    }

    private static string Norm(string s) =>
        string.Join(" ", s.ToLowerInvariant().Replace(".", " ").Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [SkippableFact]
    public async Task LiveFeatureSpeakerLabelsOffByDefault()
    {
        using var c = Live();
        var plain = await c.TranscribeAsync(NoName);
        Assert.Contains("quarterly", Norm(plain.Text));
        Assert.All(plain.Words, w => Assert.Null(w.Speaker));
        var labelled = await c.TranscribeAsync(NoName, new TranscribeOptions { SpeakerLabels = true });
        Assert.Equal(2, labelled.Words.Select(w => w.Speaker).Distinct().Count());
    }

    [SkippableFact]
    public async Task LiveFeatureSummaryAndParagraphs()
    {
        using var c = Live();
        var t = await c.TranscribeAsync(NoName, new TranscribeOptions { SpeakerLabels = true, Summarize = true, Paragraphs = true });
        Assert.NotNull(t.Summary);
        Assert.True(t.Summary!.Text.Length > 40);
        Assert.NotEmpty(t.Summary.Bullets);
        Assert.True(t.Paragraphs.Count >= 2);
        Assert.All(t.Paragraphs, p => Assert.NotEmpty(p.Sentences));
    }

    [SkippableFact]
    public async Task LiveFeatureProfanityAndExtra()
    {
        using var c = Live();
        var f = await c.TranscribeAsync(Prof, new TranscribeOptions { FilterProfanity = true });
        Assert.Contains("s***", f.Text);
        Assert.DoesNotContain("shit", f.Text.ToLowerInvariant());
        var e = await c.TranscribeAsync(Prof, new TranscribeOptions { Extra = new Dictionary<string, object?> { ["filter_profanity"] = true } });
        Assert.Contains("s***", e.Text);
    }

    [SkippableFact]
    public async Task LiveFeatureCustomVocabulary()
    {
        using var c = Live();
        var t = await c.TranscribeAsync(Call, new TranscribeOptions { CustomVocabulary = new[] { "Northwind" } });
        Assert.Contains("northwind bank", Norm(t.Text));
        Assert.DoesNotContain("north northwind", Norm(t.Text));
    }

    [SkippableFact]
    public async Task LiveFeatureDeleteJob()
    {
        using var c = Live();
        var id = await c.SubmitAsync(Prof);
        for (var i = 0; i < 60 && (await c.GetJobStatusAsync(id)).Status != "completed"; i++) await Task.Delay(2000);
        try
        {
            await c.DeleteJobAsync(id);
        }
        catch (Exception e) when (e.Message.Contains("503") || e.Message.Contains("temporarily unavailable"))
        {
            Skip.If(true, "prod user_cluster lacks s3:DeleteObject (2026-11-23)");
        }
        await Assert.ThrowsAnyAsync<Exception>(() => c.GetTranscriptAsync(id));
    }

    [SkippableFact]
    public async Task LiveFeatureStreamFile()
    {
        using var c = Live();
        var seen = new HashSet<string>();
        string? detected = null;
        var r = await c.StreamFileAsync(NoName, new StreamOptions
        {
            OnEvent = e => { lock (seen) { seen.Add(e.Type); if (e.Type == "language.detected") detected ??= e.Language; } },
        });
        Assert.True(r.Complete);
        Assert.NotNull(r.JobId);
        Assert.Contains("planning meeting", Norm(r.Text));
        foreach (var t in new[] { "session.created", "transcription.partial", "final", "transcription.done" }) Assert.Contains(t, seen);
        Assert.Equal("en", detected);
    }

    [SkippableFact]
    public async Task LiveFeatureStreamTurnsAndVocabulary()
    {
        using var c = Live();
        var seen = new HashSet<string>();
        var r = await c.StreamFileAsync(Call, new StreamOptions
        {
            Language = "en", Vocabulary = new[] { "Northwind" }, OnEvent = e => { lock (seen) seen.Add(e.Type); },
        });
        Assert.Contains("northwind bank", Norm(r.Text));
        Assert.Contains("speech.started", seen);
        Assert.Contains("turn.ended", seen);
    }

    [SkippableFact]
    public async Task LiveFeatureStreamResumesAfterCut()
    {
        using var c = Live();
        var resumed = false;
        var session = await c.StreamAsync(new StreamOptions { Language = "en", OnEvent = e => { if (e.Type == "session.resumed") resumed = true; } });
        var jobId = session.JobId;
        var pcm = await Streaming.LoadPcm16Async(NoName);
        const int step = 80 * 32;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0, n = 0; i < pcm.Length; i += step, n++)
        {
            session.SendAudio(pcm.AsSpan(i, Math.Min(step, pcm.Length - i)));
            if (i <= pcm.Length / 2 && pcm.Length / 2 < i + step) session.SimulateDrop();
            var wait = TimeSpan.FromMilliseconds((n + 1) * 80 / 1.3) - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
        }
        var r = await session.FinishAsync(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);
        Assert.True(resumed, "expected a session.resumed");
        Assert.True(r.Complete);
        Assert.Equal(jobId, r.JobId);
        Assert.Contains("quarterly", Norm(r.Text));
        Assert.Contains("deck with the numbers", Norm(r.Text));
    }

    [SkippableFact]
    public async Task LiveFeatureStreamBadKey()
    {
        using var _ = Live();
        using var c = new SpeechRevolutionsClient("stt_" + new string('0', 64));
        await Assert.ThrowsAnyAsync<Exception>(() => c.StreamAsync(new StreamOptions { Language = "en" }));
    }
}
