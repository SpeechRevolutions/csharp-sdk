using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SpeechRevolutions;

// Real-time streaming transcription over WebSocket (wss://.../api/v1/stream).
//
// A LiveSession, per the streaming protocol:
// - buffers every chunk until the server acknowledges it (audio.committed), at most ~30 s;
// - resumes a dropped connection: reconnects with ?job_id= for up to 60 s (at once on close code
//   1012, "going away for a deploy"), then sends rewind and replays the buffer;
// - replays on failover (resend) the same way, paced at up to 3x real time.
//
// Audio is PCM16 little-endian, mono, 16 kHz. Streaming.LoadPcm16 converts any file (ffmpeg for
// anything that isn't already a 16 kHz mono 16-bit WAV).

/// <summary>A live session ended with an error. <see cref="Code"/> is the server's error code, if any.</summary>
public sealed class StreamException : SpeechRevolutionsException
{
    public string? Code { get; }
    public StreamException(string message, string? code = null) : base(message) { Code = code; }
}

/// <summary>
/// One server event. <see cref="Type"/> is the protocol name: <c>session.created</c>,
/// <c>session.resumed</c>, <c>transcription.partial</c> (the open utterance so far; replaces the
/// previous partial of that <see cref="Index"/>; <c>Text[..Stable]</c> is settled), <c>final</c>,
/// <c>final.revised</c>, <c>speech.started</c>, <c>turn.ended</c>, <c>language.detected</c>,
/// <c>language.unsupported</c>, <c>audio.committed</c>, <c>transcription.done</c>,
/// <c>voice_focus</c> (<see cref="Active"/>: the stream is locked to the voiceprint), <c>warning</c>, <c>error</c>.
/// A redacted final whose PII pass fell back to patterns only carries <see cref="PiiStatus"/> <c>"patterns_only"</c>.
/// </summary>
public sealed class StreamEvent
{
    public required string Type { get; init; }
    public int? Index { get; init; }
    public string? Text { get; init; }
    public double? Start { get; init; }
    public double? End { get; init; }
    public int? Stable { get; init; }
    public double? AudioS { get; init; }
    public string? Language { get; init; }
    public double? Probability { get; init; }
    public bool? Forced { get; init; }
    public string? JobId { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    /// <summary>On a <c>voice_focus</c> event: whether the stream is locked to the voiceprint (only then billed).</summary>
    public bool? Active { get; init; }
    /// <summary>On a redacted final: <c>"patterns_only"</c> when the PII pass could only use pattern matching.</summary>
    public string? PiiStatus { get; init; }
    /// <summary>The event's JSON as received.</summary>
    public required string RawJson { get; init; }

    internal JsonElement Raw { get; init; }

    internal static StreamEvent? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement.Clone();
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double? D(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            int? I(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : null;
            bool? B(string k) => r.TryGetProperty(k, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : null;
            var err = S("error");
            return new StreamEvent
            {
                Type = S("type") ?? "",
                Index = I("index"),
                Text = S("text"),
                Start = D("start"),
                End = D("end"),
                Stable = I("stable"),
                AudioS = D("audio_s"),
                Language = S("language"),
                Probability = D("probability"),
                Forced = B("forced"),
                JobId = S("job_id"),
                Active = B("active"),
                PiiStatus = S("pii_status"),
                Code = S("code") ?? (err is not null && !err.Contains(' ') ? err : null),
                Message = S("message") ?? err,
                RawJson = json,
                Raw = r,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One utterance's latest text.</summary>
public sealed record StreamFinal(int Index, string Text, double? Start, double? End);

/// <summary>What a finished session produced.</summary>
public sealed class StreamResult
{
    public string? JobId { get; init; }
    /// <summary>All finals joined, as the server sent it in <c>transcription.done</c>.</summary>
    public string Text { get; init; } = "";
    /// <summary>Each utterance's latest text (<c>final.revised</c> applied), in order.</summary>
    public IReadOnlyList<StreamFinal> Finals { get; init; } = Array.Empty<StreamFinal>();
    /// <summary>False when the session ended before <c>transcription.done</c>.</summary>
    public bool Complete { get; init; }
    /// <summary>With <see cref="StreamOptions.CaptureVoiceprint"/>: the sealed voiceprint to pass as
    /// <see cref="StreamOptions.Voiceprint"/> later. You keep it; the server keeps none after the session.</summary>
    public string? Voiceprint { get; init; }
    /// <summary>The captured voiceprint's quality; capture again until <see cref="VoiceprintQuality.Ok"/>.</summary>
    public VoiceprintQuality? VoiceprintQuality { get; init; }
}

/// <summary>How usable a captured voiceprint is (<c>voiceprint_quality</c> in <c>transcription.done</c>).</summary>
public sealed class VoiceprintQuality
{
    public double? Score { get; init; }
    public bool Ok { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    /// <summary>The object's JSON as received.</summary>
    public string RawJson { get; init; } = "{}";

    internal static VoiceprintQuality? From(JsonElement done)
    {
        if (done.ValueKind != JsonValueKind.Object || !done.TryGetProperty("voiceprint_quality", out var q)
            || q.ValueKind != JsonValueKind.Object)
            return null;
        return new VoiceprintQuality
        {
            Score = q.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null,
            Ok = q.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
            Reasons = q.TryGetProperty("reasons", out var rs) && rs.ValueKind == JsonValueKind.Array
                ? rs.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList()
                : new List<string>(),
            RawJson = q.GetRawText(),
        };
    }
}

/// <summary>Tunes when a turn ends.</summary>
public sealed class TurnDetection
{
    /// <summary>The end-of-turn model's threshold, 0.05–0.99.</summary>
    public double? Threshold { get; set; }
    /// <summary>End a turn after this much silence regardless, 300–5000 ms.</summary>
    public int? MaxSilenceMs { get; set; }
}

/// <summary>Live session settings. The defaults detect the language.</summary>
public sealed class StreamOptions
{
    /// <summary>The spoken language ("en", "ja"…). Null detects it automatically.</summary>
    public string? Language { get; set; }
    /// <summary>Narrow automatic detection to these languages.</summary>
    public IReadOnlyList<string>? Languages { get; set; }
    /// <summary>Names and terms to recognise (included in the streaming price).</summary>
    public IReadOnlyList<string>? Vocabulary { get; set; }
    public TurnDetection? TurnDetection { get; set; }
    /// <summary>
    /// PII redaction (+$0.12 per session hour): finals are redacted before they reach you. Partials are not
    /// sent while it is on, unless <see cref="IncludePartials"/> is true, in which case they arrive unredacted.
    /// </summary>
    public bool RedactPii { get; set; }
    /// <summary>The PII types to redact (<see cref="PiiPolicies.All"/>). Sent only with <see cref="RedactPii"/>.</summary>
    public IReadOnlyList<string>? RedactPiiPolicies { get; set; }
    /// <summary><c>"entity_name"</c> or <c>"hash"</c>. Sent only with <see cref="RedactPii"/>.</summary>
    public string? RedactPiiSub { get; set; }
    /// <summary>Send partials (unredacted) even with <see cref="RedactPii"/>. Null leaves the server default.</summary>
    public bool? IncludePartials { get; set; }
    /// <summary>
    /// Voice focus (+$0.10 per session hour, billed only while a <c>voice_focus</c> event says active): lock
    /// the stream to this enrolled voice (a <see cref="StreamResult.Voiceprint"/> from an earlier session).
    /// </summary>
    public string? Voiceprint { get; set; }
    /// <summary>Enrol a voice: the result carries <see cref="StreamResult.Voiceprint"/> and its quality.</summary>
    public bool CaptureVoiceprint { get; set; }
    /// <summary>With <see cref="Voiceprint"/>: <c>"prefer"</c> or <c>"strict"</c>.</summary>
    public string? SpeakerPolicy { get; set; }
    /// <summary>Called for every server event, from the session's reader task. Don't block in it.</summary>
    public Action<StreamEvent>? OnEvent { get; set; }
    /// <summary>Other <c>session.update</c> fields, sent as given.</summary>
    public IDictionary<string, object?>? Extra { get; set; }

    internal Dictionary<string, object?> SessionUpdate()
    {
        var m = new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["audio_format"] = new Dictionary<string, object?> { ["encoding"] = "pcm_s16le", ["sample_rate"] = Streaming.SampleRate, ["channels"] = 1 },
        };
        if (!string.IsNullOrEmpty(Language) && Language != "auto") m["language"] = Language;
        if (Languages is { Count: > 0 }) m["languages"] = Languages;
        if (Vocabulary is { Count: > 0 }) m["vocabulary"] = Vocabulary;
        if (TurnDetection is not null)
        {
            var td = new Dictionary<string, object?>();
            if (TurnDetection.Threshold is not null) td["threshold"] = TurnDetection.Threshold;
            if (TurnDetection.MaxSilenceMs is not null) td["max_silence_ms"] = TurnDetection.MaxSilenceMs;
            m["turn_detection"] = td;
        }
        if (RedactPii)
        {
            m["redact_pii"] = true;
            if (RedactPiiPolicies is { Count: > 0 }) m["redact_pii_policies"] = RedactPiiPolicies.ToList();
            if (!string.IsNullOrEmpty(RedactPiiSub)) m["redact_pii_sub"] = RedactPiiSub;
        }
        if (IncludePartials is not null) m["include_partials"] = IncludePartials.Value;
        if (!string.IsNullOrEmpty(Voiceprint)) m["voiceprint"] = Voiceprint;
        if (CaptureVoiceprint) m["capture_voiceprint"] = true;
        if (!string.IsNullOrEmpty(SpeakerPolicy)) m["speaker_policy"] = SpeakerPolicy;
        if (Extra is not null)
            foreach (var kv in Extra) m[kv.Key] = kv.Value;
        return m;
    }
}

/// <summary>
/// A real-time transcription session. Create one with <see cref="SpeechRevolutionsClient.StreamAsync"/>;
/// send audio with <see cref="SendAudio"/> as it is produced, read events from <see cref="Events"/>
/// or <see cref="StreamOptions.OnEvent"/>, and call <see cref="FinishAsync"/> at the end of the audio.
/// </summary>
public sealed class LiveSession : IAsyncDisposable
{
    private const int BytesPerMs = Streaming.SampleRate * 2 / 1000; // 32
    private const int ChunkMs = 80;
    private const int ReplaySpeed = 3;
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> FatalErrors = new()
    {
        "invalid_api_key", "insufficient_balance", "session_expired", "no_capacity", "idle_timeout",
        "max_duration", "use_batch", "frame_too_large", "bad_audio_format",
    };

    private readonly string _url;
    private readonly string _apiKey;
    private readonly Dictionary<string, object?> _update;
    private readonly Action<StreamEvent>? _onEvent;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<StreamEvent> _events = Channel.CreateUnbounded<StreamEvent>();
    private readonly List<(int Start, byte[] Data)> _buffer = new();
    private readonly Dictionary<int, StreamFinal> _finals = new();

    private ClientWebSocket? _ws;
    private int _gen;
    private bool _live; // false between a drop and session.resumed: audio waits in the buffer
    private int _audioMs, _nextMs, _replayUntil, _committedMs;
    private bool _finishing, _finalSent;
    private StreamEvent? _done;
    private Exception? _fatal;
    private Task? _sender;

    /// <summary>The session's job id: the billing id, and the resume handle.</summary>
    public string? JobId { get; private set; }

    /// <summary>Every event, in order. Completes when the session ends.</summary>
    public ChannelReader<StreamEvent> Events => _events.Reader;

    internal LiveSession(string url, string apiKey, StreamOptions options)
    {
        _url = url;
        _apiKey = apiKey;
        _update = options.SessionUpdate();
        _onEvent = options.OnEvent;
    }

    internal async Task ConnectAsync(CancellationToken ct)
    {
        await OpenAsync(resume: false, ct).ConfigureAwait(false);
        _sender = Task.Run(SendLoopAsync);
    }

    /// <summary>Queue PCM16 mono 16 kHz audio. Send it at the pace it is produced.</summary>
    public void SendAudio(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0) throw new ArgumentException("PCM16 audio must be an even number of bytes", nameof(pcm));
        lock (_lock)
        {
            if (_fatal is not null) throw _fatal;
            if (_finishing) throw new StreamException("SendAudio after FinishAsync");
            if (pcm.Length == 0) return;
            _buffer.Add((_audioMs, pcm.ToArray()));
            _audioMs += pcm.Length / BytesPerMs;
        }
        _wake.Release();
    }

    /// <summary>End the current turn now (push-to-talk release).</summary>
    public Task EndTurnAsync() => SendJsonAsync(new Dictionary<string, object?> { ["type"] = "turn.end" });

    /// <summary>
    /// End the audio, wait for <c>transcription.done</c>, and return the result. Waits at most until
    /// <paramref name="ct"/> is cancelled, or two minutes when it can't be; the result then has
    /// <see cref="StreamResult.Complete"/> false.
    /// </summary>
    public async Task<StreamResult> FinishAsync(CancellationToken ct = default)
    {
        lock (_lock) _finishing = true;
        _wake.Release();
        using var timeout = ct.CanBeCanceled ? null : new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout?.Token ?? ct;
        var cancelled = new TaskCompletionSource();
        await using (token.Register(() => cancelled.TrySetResult()))
        {
            await Task.WhenAny(_closed.Task, cancelled.Task).ConfigureAwait(false);
        }
        if (!_closed.Task.IsCompleted)
        {
            await DisposeAsync().ConfigureAwait(false);
            return Result(false);
        }
        await TeardownAsync().ConfigureAwait(false);
        lock (_lock)
        {
            if (_fatal is not null && _done is null) throw _fatal;
        }
        return Result(_done is not null);
    }

    /// <summary>Drop the connection without waiting for the transcript.</summary>
    public async ValueTask DisposeAsync()
    {
        MarkClosed();
        await TeardownAsync().ConfigureAwait(false);
    }

    /// <summary>Cut the connection without a close handshake (tests).</summary>
    internal void SimulateDrop() => _ws?.Abort();

    // ---------------------------------------------------------------- connection

    private async Task OpenAsync(bool resume, CancellationToken ct)
    {
        var url = resume && JobId is not null ? $"{_url}?job_id={Uri.EscapeDataString(JobId)}" : _url;
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-API-Key", _apiKey);
        ws.Options.SetRequestHeader("User-Agent", SpeechRevolutionsClient.UserAgent);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        ws.Options.CollectHttpResponseDetails = true;
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await ws.ConnectAsync(new Uri(url), connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            var status = (int)ws.HttpStatusCode;
            ws.Dispose();
            throw status switch
            {
                401 or 403 => new StreamException($"refused (HTTP {status}): check the API key", "invalid_api_key"),
                429 => new StreamException("refused (HTTP 429): too many sessions or connects", "rate_limited"),
                0 => new StreamException($"connection failed: {e.Message}"),
                _ => new StreamException($"refused (HTTP {status})"),
            };
        }
        int gen;
        lock (_lock)
        {
            gen = ++_gen;
            _ws = ws;
        }
        if (!resume)
        {
            await SendJsonAsync(_update).ConfigureAwait(false);
            await SendJsonAsync(new Dictionary<string, object?> { ["type"] = "input_audio_buffer.commit" }).ConfigureAwait(false); // start of audio
        }
        // Read until session.created / session.resumed.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            string? text;
            try
            {
                text = await ReceiveTextAsync(ws, readCts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                ws.Abort();
                throw new StreamException($"closed before the session started: {e.Message}");
            }
            if (text is null)
            {
                throw new StreamException($"closed before the session started ({ws.CloseStatus})");
            }
            var ev = StreamEvent.Parse(text);
            if (ev is null) continue;
            if (ev.Type == "error")
            {
                await CloseQuietlyAsync(ws).ConfigureAwait(false);
                throw ErrorOf(ev);
            }
            await HandleAsync(ev).ConfigureAwait(false);
            if (ev.Type is "session.created" or "session.resumed") break;
        }
        _ = Task.Run(() => ReadLoopAsync(ws, gen));
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, int gen)
    {
        while (true)
        {
            string? text;
            try
            {
                text = await ReceiveTextAsync(ws, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                text = null;
            }
            if (text is null)
            {
                bool current, closed;
                lock (_lock)
                {
                    current = gen == _gen;
                    closed = _closed.Task.IsCompleted;
                    if (current) _live = false;
                }
                if (!current || closed) return;
                await ResumeAsync(ws.CloseStatus == (WebSocketCloseStatus)1012).ConfigureAwait(false);
                return;
            }
            var ev = StreamEvent.Parse(text);
            if (ev is null) continue;
            await HandleAsync(ev).ConfigureAwait(false);
            if (ev.Type == "transcription.done")
            {
                MarkClosed();
                await CloseQuietlyAsync(ws).ConfigureAwait(false);
                return;
            }
            if (ev.Type == "error")
            {
                bool terminal;
                lock (_lock)
                {
                    terminal = FatalErrors.Contains(ev.Code ?? "") || JobId is null;
                    if (terminal) _fatal = ErrorOf(ev);
                }
                if (terminal)
                {
                    MarkClosed();
                    return;
                }
            }
        }
    }

    private async Task ResumeAsync(bool immediately)
    {
        var deadline = Stopwatch.StartNew();
        var delay = immediately ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500);
        while (!_closed.Task.IsCompleted)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
            try
            {
                await OpenAsync(resume: true, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (StreamException e) when (e.Code is not null && FatalErrors.Contains(e.Code))
            {
                lock (_lock) _fatal = e;
                break;
            }
            catch
            {
                // retry
            }
            if (deadline.Elapsed > ResumeWindow)
            {
                lock (_lock) _fatal = new StreamException("connection lost and could not be resumed within 60 s");
                break;
            }
            delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds * 2, 500, 5000));
        }
        MarkClosed();
    }

    private static StreamException ErrorOf(StreamEvent ev)
    {
        var msg = ev.Message ?? ev.Code ?? "stream error";
        return new StreamException(ev.Code == "invalid_api_key" ? $"invalid API key: {msg}" : msg, ev.Code);
    }

    // ---------------------------------------------------------------- events

    private async Task HandleAsync(StreamEvent ev)
    {
        switch (ev.Type)
        {
            case "session.created":
                lock (_lock)
                {
                    JobId = ev.JobId;
                    _live = true;
                }
                break;
            case "session.resumed":
            case "resend":
            {
                int from;
                lock (_lock) from = _committedMs;
                if (ev.Raw.TryGetProperty("resume_from_ms", out var rf) && rf.TryGetDouble(out var rfv)) from = (int)rfv;
                else if (ev.Raw.TryGetProperty("from_ms", out var fm) && fm.TryGetDouble(out var fmv)) from = (int)fmv;
                await SendJsonAsync(new Dictionary<string, object?> { ["type"] = "input_audio_buffer.rewind", ["from_ms"] = from }).ConfigureAwait(false);
                lock (_lock)
                {
                    _replayUntil = Math.Max(_nextMs, _replayUntil);
                    _nextMs = from;
                    _finalSent = false; // the commit is re-sent once the replay is through
                    _live = true;
                }
                break;
            }
            case "audio.committed":
                lock (_lock)
                {
                    if (ev.Raw.TryGetProperty("through_ms", out var t) && t.TryGetDouble(out var tv)) _committedMs = (int)tv;
                    _buffer.RemoveAll(c => c.Start + c.Data.Length / BytesPerMs <= _committedMs);
                }
                break;
            case "final" when ev.Index is not null:
                lock (_lock) _finals[ev.Index.Value] = new StreamFinal(ev.Index.Value, ev.Text ?? "", ev.Start, ev.End);
                break;
            case "final.revised" when ev.Index is not null:
                lock (_lock)
                {
                    _finals.TryGetValue(ev.Index.Value, out var f);
                    _finals[ev.Index.Value] = new StreamFinal(ev.Index.Value, ev.Text ?? "", f?.Start, f?.End);
                }
                break;
            case "transcription.done":
                lock (_lock) _done = ev;
                break;
        }
        _wake.Release();
        if (ev.Type == "resend") return; // internal: the replay is the answer
        _events.Writer.TryWrite(ev);
        _onEvent?.Invoke(ev);
    }

    // ---------------------------------------------------------------- sending

    private async Task SendJsonAsync(Dictionary<string, object?> m)
    {
        var ws = _ws;
        if (ws is null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(m);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // the reader notices the drop and resumes; the buffer still holds the audio
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendLoopAsync()
    {
        while (!_closed.Task.IsCompleted)
        {
            (int Start, byte[] Data)? chunk = null;
            bool sendFinal = false, replay = false;
            lock (_lock)
            {
                if (_live) chunk = ChunkAt(_nextMs);
                if (chunk is { } c)
                {
                    replay = c.Start < _replayUntil;
                    _nextMs = c.Start + c.Data.Length / BytesPerMs;
                }
                else if (_live && _finishing && !_finalSent && _nextMs >= _audioMs)
                {
                    sendFinal = true;
                    _finalSent = true;
                }
            }
            if (chunk is { } ch)
            {
                await SendJsonAsync(new Dictionary<string, object?>
                {
                    ["type"] = "input_audio_buffer.append",
                    ["audio"] = Convert.ToBase64String(ch.Data),
                }).ConfigureAwait(false);
                if (replay) await Task.Delay(ch.Data.Length / BytesPerMs / ReplaySpeed).ConfigureAwait(false);
            }
            else if (sendFinal)
            {
                await SendJsonAsync(new Dictionary<string, object?> { ["type"] = "input_audio_buffer.commit", ["final"] = true }).ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAny(_wake.WaitAsync(), _closed.Task).ConfigureAwait(false);
            }
        }
    }

    private (int Start, byte[] Data)? ChunkAt(int ms)
    {
        foreach (var (start, data) in _buffer)
        {
            var end = start + data.Length / BytesPerMs;
            if (end <= ms) continue;
            var off = Math.Max(0, ms - start) * BytesPerMs;
            var len = Math.Min(ChunkMs * BytesPerMs, data.Length - off);
            if (len <= 0) return null;
            return (start + off / BytesPerMs, data.AsSpan(off, len).ToArray());
        }
        return null;
    }

    // ---------------------------------------------------------------- end

    private void MarkClosed()
    {
        if (_closed.TrySetResult()) _events.Writer.TryComplete();
    }

    private async Task TeardownAsync()
    {
        ClientWebSocket? ws;
        lock (_lock)
        {
            _gen++; // late frames from this connection are ignored
            ws = _ws;
        }
        if (ws is not null) await CloseQuietlyAsync(ws).ConfigureAwait(false);
        if (_sender is not null) await Task.WhenAny(_sender, Task.Delay(1000)).ConfigureAwait(false);
    }

    /// <summary>Send a close frame without waiting for the server's reply (which a broken server never sends).</summary>
    private static async Task CloseQuietlyAsync(ClientWebSocket ws)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private StreamResult Result(bool complete)
    {
        lock (_lock)
        {
            var finals = _finals.Values.OrderBy(f => f.Index).ToList();
            var text = _done?.Text ?? string.Join(" ", finals.Select(f => f.Text).Where(t => t.Length > 0));
            string? voiceprint = null;
            VoiceprintQuality? quality = null;
            if (_done is not null && _done.Raw.ValueKind == JsonValueKind.Object)
            {
                if (_done.Raw.TryGetProperty("voiceprint", out var vp) && vp.ValueKind == JsonValueKind.String)
                    voiceprint = vp.GetString();
                quality = VoiceprintQuality.From(_done.Raw);
            }
            return new StreamResult
            {
                JobId = JobId, Text = text, Finals = finals, Complete = complete,
                Voiceprint = voiceprint, VoiceprintQuality = quality,
            };
        }
    }
}

/// <summary>Streaming helpers.</summary>
public static class Streaming
{
    public const int SampleRate = 16000;

    /// <summary>
    /// How far ahead of real time <see cref="StreamPcmAsync"/> sends audio. The server accepts audio
    /// at most ~10 s ahead of the clock and refuses a faster sender with <c>use_batch</c>.
    /// </summary>
    public static readonly TimeSpan MaxLead = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Send recorded PCM through a session as live audio, then finish. Audio goes out in real time,
    /// up to <see cref="MaxLead"/> ahead of the clock, so the first seconds go at once.
    /// </summary>
    public static async Task<StreamResult> StreamPcmAsync(LiveSession session, byte[] pcm, CancellationToken ct = default)
    {
        const int step = 80 * SampleRate * 2 / 1000;
        var clock = Stopwatch.StartNew();
        for (int i = 0, n = 0; i < pcm.Length; i += step, n++)
        {
            session.SendAudio(pcm.AsSpan(i, Math.Min(step, pcm.Length - i)));
            var due = TimeSpan.FromMilliseconds((n + 1) * 80) - MaxLead;
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        }
        return await session.FinishAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Any audio file as PCM16 mono 16 kHz: read directly if it already is that WAV, else via ffmpeg.
    /// </summary>
    public static async Task<byte[]> LoadPcm16Async(string path, CancellationToken ct = default)
    {
        var data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (WavPcm16Mono16k(data) is { } pcm) return pcm;
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-v", "error", "-i", path, "-f", "s16le", "-ac", "1", "-ar", "16000", "-" }) psi.ArgumentList.Add(a);
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new StreamException("could not start ffmpeg");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new StreamException("ffmpeg is needed to convert this file to PCM16 mono 16 kHz (or pass such a WAV)");
        }
        using (p)
        {
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            var err = p.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(copy, err).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0 || ms.Length == 0)
                throw new StreamException($"ffmpeg could not decode {path}: {(await err)[..Math.Min(300, (await err).Length)]}");
            return ms.ToArray();
        }
    }

    private static byte[]? WavPcm16Mono16k(byte[] b)
    {
        if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return null;
        var ok = false;
        for (var o = 12; o + 8 <= b.Length;)
        {
            var id = Encoding.ASCII.GetString(b, o, 4);
            var size = (int)BitConverter.ToUInt32(b, o + 4);
            if (id == "fmt " && o + 24 <= b.Length)
            {
                ok = BitConverter.ToUInt16(b, o + 8) == 1 && BitConverter.ToUInt16(b, o + 10) == 1
                     && BitConverter.ToUInt32(b, o + 12) == SampleRate && BitConverter.ToUInt16(b, o + 22) == 16;
            }
            else if (id == "data" && ok)
            {
                return b.AsSpan(o + 8, Math.Min(size, b.Length - o - 8)).ToArray();
            }
            o += 8 + size + size % 2;
        }
        return null;
    }
}
