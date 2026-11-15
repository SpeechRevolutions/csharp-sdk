using System.Text.Json.Nodes;
using SpeechRevolutions;
using Xunit;

public class StreamingTests
{
    [Fact]
    public void EventMapping()
    {
        var p = StreamEvent.From(JsonNode.Parse("""{"type":"transcription.partial","index":2,"text":"hel","stable":1,"audio_s":3.5}""")!.AsObject());
        Assert.Equal(("partial", 2, "hel", 1, 3.5), (p.Type, p.Index!.Value, p.Text, p.Stable!.Value, p.Start!.Value));
        Assert.Equal("revision", StreamEvent.From(JsonNode.Parse("""{"type":"final.revised","text":"x"}""")!.AsObject()).Type);
        Assert.Equal("v2 style", StreamEvent.From(JsonNode.Parse("""{"type":"error","code":"c","message":"v2 style"}""")!.AsObject()).Text);
    }

    [Fact]
    public void SessionUpdate()
    {
        var u = new StreamOptions { Language = "auto", Vocabulary = new[] { "Acme" }, EndUserId = "u1", TurnThreshold = 0.6 }.ToSessionUpdate();
        Assert.Equal("""{"type":"session.update","vocabulary":["Acme"],"end_user_id":"u1","turn_detection":{"threshold":0.6}}""", u.ToJsonString());
    }
}
