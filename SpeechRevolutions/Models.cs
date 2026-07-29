using System.Text.Json.Serialization;

namespace SpeechRevolutions;

public enum OutputType
{
    [JsonPropertyName("txt")] Txt,
    [JsonPropertyName("json")] Json,
    [JsonPropertyName("srt")] Srt,
    [JsonPropertyName("vtt")] Vtt,
    [JsonPropertyName("docx")] Docx,
    [JsonPropertyName("pdf")] Pdf,
}

public static class OutputTypeExtensions
{
    public static string ToApiValue(this OutputType type) => type switch
    {
        OutputType.Txt => "txt",
        OutputType.Json => "json",
        OutputType.Srt => "srt",
        OutputType.Vtt => "vtt",
        OutputType.Docx => "docx",
        OutputType.Pdf => "pdf",
        _ => "json",
    };
}

public enum ProcessingTier
{
    Standard,
    Economy,
}

public static class ProcessingTierExtensions
{
    public static string ToApiValue(this ProcessingTier tier) => tier switch
    {
        ProcessingTier.Economy => "economy",
        _ => "standard",
    };
}

public sealed class TranscribeOptions
{
    public OutputType OutputType { get; set; } = OutputType.Json;
    public bool WordTimestamps { get; set; } = true;
    public bool SpeakerLabels { get; set; } = true;
    /// <summary>Alias for SpeakerLabels (ElevenLabs / Deepgram).</summary>
    public bool? Diarize { get; set; }
    public bool Nltk { get; set; } = true;
    public ProcessingTier Tier { get; set; } = ProcessingTier.Standard;
    public IReadOnlyList<string>? CustomVocabulary { get; set; }

    /// <summary>
    /// Optional http(s) webhook POSTed a signed completion/failure notification
    /// (<c>X-SR-Signature: sha256=...</c>).
    /// </summary>
    public string? CallbackUrl { get; set; }

    /// <summary>
    /// Render live progress bars (an <c>Uploading</c> byte bar, then a
    /// <c>Transcribing</c> bar) to <see cref="Console.Error"/>. Off by default.
    /// </summary>
    public bool Progress { get; set; }

    /// <summary>
    /// Callback invoked with a <see cref="ProgressEvent"/> for byte-level upload
    /// progress (<c>Step == "upload"</c>). Composes with <see cref="Progress"/>.
    /// </summary>
    public Action<ProgressEvent>? OnUploadProgress { get; set; }

    public bool EffectiveSpeakerLabels => Diarize ?? SpeakerLabels;
}

public sealed class JobStatus
{
    public required string JobId { get; init; }
    public required string Status { get; init; } // "processing" | "completed" | "failed"
    public string? DownloadUrl { get; init; }
    public string? FailedStage { get; init; }
    public string? Reason { get; init; }

    public bool IsCompleted => Status == "completed";
    public bool IsFailed => Status == "failed";
}

public sealed class JobSummary
{
    public required string JobId { get; init; }
    public required string CreatedAt { get; init; }
}

public sealed class JobList
{
    public required IReadOnlyList<JobSummary> Jobs { get; init; }
    public string? NextBefore { get; init; }
}

public sealed class UploadJob
{
    public required string JobId { get; init; }
    public required string UploadUrl { get; init; }
    public required string DownloadUrl { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public int ExpiresIn { get; init; }
}

public sealed class ProgressEvent
{
    public int? Completed { get; init; }
    public int? Total { get; init; }
    public string? Step { get; init; }
    public double? ElapsedSeconds { get; init; }

    /// <summary>
    /// Completion as a 0–100 value (clamped), or <c>null</c> if it can't be
    /// computed yet (unknown or zero total).
    /// </summary>
    public double? Percent
    {
        get
        {
            if (Completed is null || Total is null || Total.Value == 0)
                return null;
            var pct = (double)Completed.Value / Total.Value * 100.0;
            return Math.Clamp(pct, 0.0, 100.0);
        }
    }
}

public sealed class Word
{
    public required string Text { get; init; }
    public string WordText => Text;
    public double? Start { get; init; }
    public double? End { get; init; }
    public string? Speaker { get; init; }
    public string? Language { get; init; }
}

/// <summary>A contiguous time range spoken in a single detected language.</summary>
public sealed class LanguageSegment
{
    public double Start { get; init; }
    public double End { get; init; }
    public required string Language { get; init; }
}

public sealed class Utterance
{
    public required string Text { get; init; }
    public string Transcript => Text;
    public string? Speaker { get; init; }
    public double? Start { get; init; }
    public double? End { get; init; }
    public IReadOnlyList<Word> Words { get; init; } = Array.Empty<Word>();
}

public sealed class TranscriptResult
{
    public required string JobId { get; init; }
    public required byte[] Content { get; init; }
    public required string DownloadUrl { get; init; }
    public required string OutputType { get; init; }
    public IReadOnlyList<Word> Words { get; init; } = Array.Empty<Word>();
    public IReadOnlyList<Utterance> Utterances { get; init; } = Array.Empty<Utterance>();
    public IReadOnlyList<LanguageSegment> Languages { get; init; } = Array.Empty<LanguageSegment>();
    public string? RawJson { get; init; }

    private string? _text;

    /// <summary>Full transcript text (AssemblyAI / ElevenLabs-style).</summary>
    public string Text
    {
        get
        {
            if (_text != null) return _text;
            if (Utterances.Count > 0)
                return string.Join(" ", Utterances.Select(u => u.Text).Where(t => !string.IsNullOrEmpty(t)));
            if (Words.Count > 0)
                return JoinWords(Words);
            try { return System.Text.Encoding.UTF8.GetString(Content); }
            catch { return ""; }
        }
        init => _text = value;
    }

    /// <summary>Deepgram-compatible alias.</summary>
    public string Transcript => Text;

    public async Task<string> SaveAsync(string path, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        var outPath = name.Contains('.') ? path : $"{path}.{OutputType}";
        await File.WriteAllBytesAsync(outPath, Content, ct);
        return outPath;
    }

    /// <summary>Normalized dictionary of the result (AssemblyAI-inspired).</summary>
    public Dictionary<string, object?> ToDict()
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = JobId,
            ["status"] = "completed",
            ["text"] = Text,
            ["words"] = Words,
            ["utterances"] = Utterances,
            ["output_type"] = OutputType,
        };
        if (Languages.Count > 0) d["languages"] = Languages;
        return d;
    }

    /// <summary>The result reshaped as a Deepgram pre-recorded response.</summary>
    public Dictionary<string, object?> ToDeepgram()
    {
        var dgWords = Words.Select(w =>
        {
            var item = new Dictionary<string, object?>
            {
                ["word"] = w.Text.ToLowerInvariant().TrimEnd('.', ',', '!', '?', ';', ':'),
                ["punctuated_word"] = w.Text,
            };
            if (w.Start is not null) item["start"] = w.Start;
            if (w.End is not null) item["end"] = w.End;
            if (w.Speaker is not null) item["speaker"] = w.Speaker;
            return item;
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?> { ["request_id"] = JobId, ["channels"] = 1 },
            ["results"] = new Dictionary<string, object?>
            {
                ["channels"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["alternatives"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["transcript"] = Text,
                                ["confidence"] = 1.0,
                                ["words"] = dgWords,
                            },
                        },
                    },
                },
                ["utterances"] = Utterances,
            },
        };
    }

    public static TranscriptResult FromContent(string jobId, byte[] content, string outputType, string downloadUrl)
    {
        if (outputType != "json")
        {
            string text;
            try { text = System.Text.Encoding.UTF8.GetString(content); }
            catch { text = ""; }
            return new TranscriptResult
            {
                JobId = jobId,
                Content = content,
                DownloadUrl = downloadUrl,
                OutputType = outputType,
                Text = text,
            };
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var words = new List<Word>();
            if (doc.RootElement.TryGetProperty("words", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var text = el.TryGetProperty("word", out var w) ? w.GetString() ?? "" :
                               el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    words.Add(new Word
                    {
                        Text = text,
                        Start = el.TryGetProperty("start", out var s) && s.TryGetDouble(out var sd) ? sd : null,
                        End = el.TryGetProperty("end", out var e) && e.TryGetDouble(out var ed) ? ed : null,
                        Speaker = el.TryGetProperty("speaker", out var sp) ? sp.ToString() : null,
                        Language = el.TryGetProperty("language", out var lg) ? lg.GetString() : null,
                    });
                }
            }

            var languages = new List<LanguageSegment>();
            if (doc.RootElement.TryGetProperty("languages", out var langArr))
            {
                foreach (var el in langArr.EnumerateArray())
                {
                    languages.Add(new LanguageSegment
                    {
                        Start = el.TryGetProperty("start", out var s) && s.TryGetDouble(out var sd) ? sd : 0.0,
                        End = el.TryGetProperty("end", out var e) && e.TryGetDouble(out var ed) ? ed : 0.0,
                        Language = el.TryGetProperty("language", out var lg) ? lg.GetString() ?? "" : "",
                    });
                }
            }

            doc.RootElement.TryGetProperty("diarization", out var diarization);
            return new TranscriptResult
            {
                JobId = jobId,
                Content = content,
                DownloadUrl = downloadUrl,
                OutputType = outputType,
                Words = words,
                Utterances = BuildUtterances(words, diarization),
                Languages = languages,
                RawJson = System.Text.Encoding.UTF8.GetString(content),
                Text = JoinWords(words),
            };
        }
        catch
        {
            return new TranscriptResult
            {
                JobId = jobId,
                Content = content,
                DownloadUrl = downloadUrl,
                OutputType = outputType,
            };
        }
    }

    /// <summary>
    /// Prefers the server's diarization segments, which separate turns the
    /// speaker labels alone cannot (the same speaker talking twice). Falls back
    /// to grouping consecutive words by speaker.
    /// </summary>
    private static List<Utterance> BuildUtterances(List<Word> words, System.Text.Json.JsonElement diarization)
    {
        if (diarization.ValueKind != System.Text.Json.JsonValueKind.Array) return BuildUtterances(words);

        var segments = new List<Utterance>();
        foreach (var el in diarization.EnumerateArray())
        {
            if (!el.TryGetProperty("start", out var s) || !s.TryGetDouble(out var start)) continue;
            if (!el.TryGetProperty("end", out var e) || !e.TryGetDouble(out var end)) continue;

            var segWords = WordsWithin(words, start, end);
            segments.Add(new Utterance
            {
                Text = JoinWords(segWords),
                Speaker = el.TryGetProperty("speaker", out var sp) ? sp.ToString() : null,
                Start = start,
                End = end,
                Words = segWords,
            });
        }
        return segments.Count > 0 ? segments : BuildUtterances(words);
    }

    /// <summary>
    /// Words a segment covers, falling back to a midpoint test for words that
    /// straddle the boundary.
    /// </summary>
    private static List<Word> WordsWithin(List<Word> words, double start, double end)
    {
        const double eps = 1e-3;
        var inside = words
            .Where(w => w.Start is not null && w.End is not null && w.Start >= start - eps && w.End <= end + eps)
            .ToList();
        if (inside.Count > 0) return inside;

        return words
            .Where(w => w.Start is not null && w.End is not null &&
                        (w.Start + w.End) / 2 >= start && (w.Start + w.End) / 2 <= end)
            .ToList();
    }

    /// <summary>Joins words with spaces, attaching trailing punctuation.</summary>
    private static string JoinWords(IReadOnlyList<Word> words)
    {
        var parts = new List<string>();
        foreach (var w in words)
        {
            if (string.IsNullOrEmpty(w.Text)) continue;
            if (parts.Count > 0 && ".,!?;:%)]}'\"".Contains(w.Text[0]))
                parts[^1] += w.Text;
            else
                parts.Add(w.Text);
        }
        return string.Join(" ", parts);
    }

    private static List<Utterance> BuildUtterances(List<Word> words)
    {
        if (words.Count == 0) return new();
        if (words.All(w => w.Speaker is null))
        {
            return new List<Utterance>
            {
                new()
                {
                    Text = JoinWords(words),
                    Start = words[0].Start,
                    End = words[^1].End,
                    Words = words,
                },
            };
        }

        var outList = new List<Utterance>();
        var cur = new List<Word> { words[0] };
        for (var i = 1; i < words.Count; i++)
        {
            if (words[i].Speaker == cur[0].Speaker) cur.Add(words[i]);
            else
            {
                outList.Add(FromGroup(cur));
                cur = new List<Word> { words[i] };
            }
        }
        outList.Add(FromGroup(cur));
        return outList;
    }

    private static Utterance FromGroup(List<Word> group) => new()
    {
        Text = JoinWords(group),
        Speaker = group[0].Speaker,
        Start = group[0].Start,
        End = group[^1].End,
        Words = group,
    };
}
