using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>1.1.0 add-on options: what is sent, and what comes back.</summary>
public class AddonTests
{
    private const string OkBody =
        """{"job_id":"j1","upload_url":"u","download_url":"d","content_type":"audio/mpeg","expires_in":900}""";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkBody, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<JsonElement> CreateBody(TranscribeOptions options)
    {
        var handler = new CapturingHandler();
        var client = new SpeechRevolutionsClient(apiKey: "k", baseUrl: "http://localhost:1",
            httpClient: new HttpClient(handler), maxRetries: 0, retryBackoff: TimeSpan.FromMilliseconds(1));
        await client.CreateUploadJobAsync(1024, options);
        return JsonDocument.Parse(handler.Bodies.Single()).RootElement.Clone();
    }

    private static JsonElement Wire(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task AddOnOptionsAreSentOnlyWhenSet()
    {
        var b = await CreateBody(new TranscribeOptions());
        foreach (var k in new[] { "auto_chapters", "known_speakers", "redact_pii", "redact_pii_policies", "redact_pii_sub", "entity_detection" })
            Assert.False(b.TryGetProperty(k, out _), k);
        Assert.False(b.GetProperty("speaker_labels").GetBoolean()); // explicit, off by default

        var p = await CreateBody(new TranscribeOptions
        {
            AutoChapters = true, KnownSpeakers = new[] { "Agent", "Customer" }, RedactPii = true,
            RedactPiiPolicies = new[] { "person_name", "phone_number" }, RedactPiiSub = "hash",
            EntityDetection = true, SpeakerLabels = true,
        });
        Assert.True(p.GetProperty("auto_chapters").GetBoolean());
        Assert.Equal(new[] { "Agent", "Customer" }, p.GetProperty("known_speakers").EnumerateArray().Select(x => x.GetString()));
        Assert.True(p.GetProperty("redact_pii").GetBoolean());
        Assert.Equal(new[] { "person_name", "phone_number" }, p.GetProperty("redact_pii_policies").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("hash", p.GetProperty("redact_pii_sub").GetString());
        Assert.True(p.GetProperty("entity_detection").GetBoolean());
        Assert.True(p.GetProperty("speaker_labels").GetBoolean());

        // Policies and substitution need RedactPii.
        var noPii = await CreateBody(new TranscribeOptions { RedactPiiPolicies = new[] { "date" }, RedactPiiSub = "hash" });
        Assert.False(noPii.TryGetProperty("redact_pii_policies", out _));
        Assert.False(noPii.TryGetProperty("redact_pii_sub", out _));

        Assert.Contains("person_name", PiiPolicies.All);
        Assert.Equal(12, PiiPolicies.All.Count);
    }

    [Fact]
    public void TranscriptParsesChaptersEntitiesNamesAndPiiStatus()
    {
        var raw = """
            {"words": [{"word": "Hi.", "start": 0.0, "end": 0.4, "speaker": "SPEAKER_1"}],
             "chapters": [{"start": 0.0, "end": 0.4, "headline": "Greeting", "gist": "Hello."}],
             "entities": [{"entity_type": "person_name", "text": "[PERSON_NAME]", "start": 0.0, "end": 0.4}],
             "speaker_names": {"SPEAKER_1": "Agent"}, "pii_status": "patterns_only"}
            """;
        var t = TranscriptResult.FromContent("j", Encoding.UTF8.GetBytes(raw), "json", "d");
        Assert.Equal("Greeting", t.Chapters[0].Headline);
        Assert.Equal("Hello.", t.Chapters[0].Gist);
        Assert.Equal(0.4, t.Chapters[0].End);
        Assert.Equal("person_name", t.Entities[0].EntityType);
        Assert.Equal("[PERSON_NAME]", t.Entities[0].Text);
        Assert.Equal("Agent", t.SpeakerNames["SPEAKER_1"]);
        Assert.Single(t.SpeakerNames);
        Assert.Equal("patterns_only", t.PiiStatus);
        var d = t.ToDict();
        Assert.Equal("Hello.", ((IReadOnlyList<Chapter>)d["chapters"]!)[0].Gist);
        Assert.Equal("Agent", ((IReadOnlyDictionary<string, string>)d["speaker_names"]!)["SPEAKER_1"]);
        Assert.Equal("patterns_only", d["pii_status"]);

        var plain = TranscriptResult.FromContent("j", Encoding.UTF8.GetBytes("""{"words": []}"""), "json", "d");
        Assert.Empty(plain.Chapters);
        Assert.Empty(plain.Entities);
        Assert.Empty(plain.SpeakerNames);
        Assert.Null(plain.PiiStatus);
        Assert.False(plain.ToDict().ContainsKey("chapters"));
    }

    [Fact]
    public void StreamingOptionsReachSessionUpdate()
    {
        var m = Wire(new StreamOptions
        {
            Language = "en", RedactPii = true, RedactPiiSub = "hash", IncludePartials = true,
            Voiceprint = "vp1.x", SpeakerPolicy = "strict",
        }.SessionUpdate());
        Assert.True(m.GetProperty("redact_pii").GetBoolean());
        Assert.Equal("hash", m.GetProperty("redact_pii_sub").GetString());
        Assert.True(m.GetProperty("include_partials").GetBoolean());
        Assert.Equal("vp1.x", m.GetProperty("voiceprint").GetString());
        Assert.Equal("strict", m.GetProperty("speaker_policy").GetString());
        Assert.False(m.TryGetProperty("capture_voiceprint", out _));

        var plain = Wire(new StreamOptions().SessionUpdate());
        foreach (var k in new[] { "redact_pii", "redact_pii_policies", "redact_pii_sub", "include_partials", "voiceprint", "capture_voiceprint", "speaker_policy" })
            Assert.False(plain.TryGetProperty(k, out _), k);

        // Policies and substitution need RedactPii; IncludePartials = false is still sent.
        var noPii = Wire(new StreamOptions { RedactPiiPolicies = new[] { "date" }, RedactPiiSub = "hash", IncludePartials = false, CaptureVoiceprint = true }.SessionUpdate());
        Assert.False(noPii.TryGetProperty("redact_pii_policies", out _));
        Assert.False(noPii.TryGetProperty("redact_pii_sub", out _));
        Assert.False(noPii.GetProperty("include_partials").GetBoolean());
        Assert.True(noPii.GetProperty("capture_voiceprint").GetBoolean());

        var withPolicies = Wire(new StreamOptions { RedactPii = true, RedactPiiPolicies = new[] { "person_name" } }.SessionUpdate());
        Assert.Equal("person_name", withPolicies.GetProperty("redact_pii_policies")[0].GetString());
    }

    [Fact]
    public void StreamEventsCarryActiveAndPiiStatus()
    {
        var focus = StreamEvent.Parse("""{"type": "voice_focus", "active": true}""")!;
        Assert.Equal("voice_focus", focus.Type);
        Assert.True(focus.Active);
        var final = StreamEvent.Parse("""{"type": "final", "index": 0, "text": "[PERSON_NAME]", "pii_status": "patterns_only"}""")!;
        Assert.Equal("patterns_only", final.PiiStatus);
        Assert.Null(final.Active);
    }
}
