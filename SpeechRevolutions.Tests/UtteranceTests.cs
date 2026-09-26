using System.Linq;
using System.Text;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>
/// Utterances are whole speaker turns, in time order, and never lose a word — even when the
/// service's diarization list is grouped by speaker, split at pauses, and misses a word.
/// </summary>
public class UtteranceTests
{
    [Fact]
    public void UtterancesAreWholeTurnsInOrderAndKeepEveryWord()
    {
        const string raw = """
        {"words":[
          {"word":"Why,","start":0.4,"end":0.7,"speaker":"SPEAKER_1"},
          {"word":"my","start":0.8,"end":0.9,"speaker":"SPEAKER_1"},
          {"word":"dear?","start":3.5,"end":3.9,"speaker":"SPEAKER_1"},
          {"word":"She","start":5.0,"end":5.2,"speaker":"SPEAKER_2"},
          {"word":"sighed.","start":5.3,"end":5.8,"speaker":"SPEAKER_2"},
          {"word":"Well.","start":6.5,"end":6.9,"speaker":"SPEAKER_1"}],
         "diarization":[
          {"start":0.4,"end":0.9,"speaker":"SPEAKER_1"},
          {"start":6.5,"end":6.9,"speaker":"SPEAKER_1"},
          {"start":5.0,"end":5.8,"speaker":"SPEAKER_2"}]}
        """;
        var t = TranscriptResult.FromContent("j", Encoding.UTF8.GetBytes(raw), "json", "");
        Assert.Equal(
            new[] { ("SPEAKER_1", "Why, my dear?"), ("SPEAKER_2", "She sighed."), ("SPEAKER_1", "Well.") },
            t.Utterances.Select(u => (u.Speaker, u.Text)).ToArray());
    }
}
