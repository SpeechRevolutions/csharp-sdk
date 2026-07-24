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
    /// <summary>Either a URL string or a <see cref="PresignedPost"/>.</summary>
    public required object UploadUrl { get; init; }
    public required string DownloadUrl { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public int ExpiresIn { get; init; }
}

public sealed class PresignedPost
{
    public required string Url { get; init; }
    public Dictionary<string, string>? Fields { get; init; }
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
                return string.Join(" ", Words.Select(w => w.Text));
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

            var utterances = BuildUtterances(words);
            return new TranscriptResult
            {
                JobId = jobId,
                Content = content,
                DownloadUrl = downloadUrl,
                OutputType = outputType,
                Words = words,
                Utterances = utterances,
                Languages = languages,
                RawJson = System.Text.Encoding.UTF8.GetString(content),
                Text = string.Join(" ", words.Select(w => w.Text)),
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

    private static List<Utterance> BuildUtterances(List<Word> words)
    {
        if (words.Count == 0) return new();
        if (words.All(w => w.Speaker is null))
        {
            return new List<Utterance>
            {
                new()
                {
                    Text = string.Join(" ", words.Select(w => w.Text)),
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
        Text = string.Join(" ", group.Select(w => w.Text)),
        Speaker = group[0].Speaker,
        Start = group[0].Start,
        End = group[^1].End,
        Words = group,
    };
}
