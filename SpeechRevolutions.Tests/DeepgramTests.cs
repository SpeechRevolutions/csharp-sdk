using System.Text;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>ToDeepgram numbers speakers from 0, as Deepgram does: our SPEAKER_1 is its speaker 0.</summary>
public class DeepgramTests
{
    [Fact]
    public void SpeakersAreZeroBasedIntegersOnWordsAndUtterances()
    {
        const string raw = """
        {"words":[
          {"word":"Hi.","start":0.0,"end":0.5,"speaker":"SPEAKER_1"},
          {"word":"Hello","start":0.6,"end":1.0,"speaker":"SPEAKER_2"}]}
        """;
        var t = TranscriptResult.FromContent("j", Encoding.UTF8.GetBytes(raw), "json", "");
        var results = (Dictionary<string, object?>)t.ToDeepgram()["results"]!;

        var channels = (Dictionary<string, object?>[])results["channels"]!;
        var alt = ((Dictionary<string, object?>[])channels[0]["alternatives"]!)[0];
        var words = (List<Dictionary<string, object?>>)alt["words"]!;
        Assert.Equal(new object?[] { 0, 1 }, words.Select(w => w["speaker"]).ToArray());
        Assert.Equal("hi", words[0]["word"]);
        Assert.Equal("Hi.", words[0]["punctuated_word"]);

        var utts = (List<Dictionary<string, object?>>)results["utterances"]!;
        Assert.Equal(new object?[] { 0, 1 }, utts.Select(u => u["speaker"]).ToArray());
        Assert.Equal("Hello", utts[1]["transcript"]);
        Assert.Equal(0.6, utts[1]["start"]);
    }

    [Fact]
    public void AnUnrecognisedSpeakerLabelPassesThrough()
    {
        const string raw = """{"words":[{"word":"Hi","start":0.0,"end":0.5,"speaker":"A"}]}""";
        var t = TranscriptResult.FromContent("j", Encoding.UTF8.GetBytes(raw), "json", "");
        var results = (Dictionary<string, object?>)t.ToDeepgram()["results"]!;
        var utts = (List<Dictionary<string, object?>>)results["utterances"]!;
        Assert.Equal("A", utts[0]["speaker"]);
    }
}
