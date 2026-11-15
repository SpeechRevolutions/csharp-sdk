using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeechRevolutions;

/// <summary>Options for a realtime session (<see cref="SpeechRevolutionsClient.StreamAsync"/>).</summary>
public sealed class StreamOptions
{
    /// <summary>ISO code to pin the language; null detects it.</summary>
    public string? Language { get; init; }
    /// <summary>Terms to bias recognition toward (names, products, jargon).</summary>
    public IReadOnlyList<string>? Vocabulary { get; init; }
    /// <summary>Your id for the person speaking: their automatically learned vocabulary is added (opted-in accounts).</summary>
    public string? EndUserId { get; init; }
    /// <summary>End-of-turn probability threshold (Smart Turn).</summary>
    public double? TurnThreshold { get; init; }
    /// <summary>Silence after which a turn always ends.</summary>
    public int? MaxSilenceMs { get; init; }

    /// <summary>The session.update message these options become.</summary>
    public JsonObject ToSessionUpdate()
    {
        var u = new JsonObject { ["type"] = "session.update" };
        if (!string.IsNullOrEmpty(Language) && Language != "auto") u["language"] = Language;
        if (Vocabulary is { Count: > 0 }) u["vocabulary"] = new JsonArray(Vocabulary.Select(v => (JsonNode)v!).ToArray());
        if (!string.IsNullOrEmpty(EndUserId)) u["end_user_id"] = EndUserId;
        var turn = new JsonObject();
        if (TurnThreshold is { } t) turn["threshold"] = t;
        if (MaxSilenceMs is { } m) turn["max_silence_ms"] = m;
        if (turn.Count > 0) u["turn_detection"] = turn;
        return u;
    }
}

/// <summary>One server event. Type: partial, final, revision, speech_started, turn_ended, language, done, error.</summary>
public sealed record StreamEvent(string Type, string Text, int? Index, double? Start, double? End, int? Stable,
    string? Language, JsonObject Raw)
{
    private static readonly Dictionary<string, string> Types = new()
    {
        ["transcription.partial"] = "partial", ["final"] = "final", ["final.revised"] = "revision",
        ["speech.started"] = "speech_started", ["turn.ended"] = "turn_ended", ["language.detected"] = "language",
        ["transcription.done"] = "done", ["error"] = "error", ["session.created"] = "session",
    };

    /// <summary>A server message, normalised.</summary>
    public static StreamEvent From(JsonObject m)
    {
        var raw = m["type"]?.GetValue<string>() ?? "";
        var type = Types.TryGetValue(raw, out var t) ? t : raw;
        string? S(string k) => m[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        double? D(string k) => m[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        int? I(string k) => m[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        var text = S("text") ?? (type == "error" ? S("error") ?? S("message") ?? "" : "");
        return new StreamEvent(type, text, I("index"), D("start") ?? D("audio_s"), D("end"), I("stable"), S("language"), m);
    }
}

/// <summary>
/// A live transcription session. Send PCM16 mono 16 kHz with <see cref="SendAudioAsync"/>, call
/// <see cref="FinishAsync"/> when there is no more audio, and read <see cref="ReadEventsAsync"/> until "done".
/// </summary>
public sealed class StreamSession : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>The job id the stream is recorded (and billed) under.</summary>
    public string? JobId { get; private set; }

    internal StreamSession(ClientWebSocket ws) => _ws = ws;

    internal static async Task<StreamSession> OpenAsync(string baseUrl, string apiKey, string userAgent,
        StreamOptions options, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-API-Key", apiKey);
        ws.Options.SetRequestHeader("User-Agent", userAgent); // the edge 403s a handshake without one
        var url = new Uri(System.Text.RegularExpressions.Regex.Replace(baseUrl, "^http", "ws") + "/api/v1/stream");
        await ws.ConnectAsync(url, ct).ConfigureAwait(false);
        var s = new StreamSession(ws);
        await s.SendAsync(options.ToSessionUpdate(), ct).ConfigureAwait(false);
        await s.SendAsync(new JsonObject { ["type"] = "input_audio_buffer.commit" }, ct).ConfigureAwait(false);
        return s;
    }

    /// <summary>Send PCM16 mono 16 kHz audio (any chunk size).</summary>
    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default) =>
        pcm16.Length == 0 ? Task.CompletedTask : SendAsync(new JsonObject
        {
            ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(pcm16.Span),
        }, ct);

    /// <summary>Finalise the current utterance now (push-to-talk release).</summary>
    public Task EndTurnAsync(CancellationToken ct = default) => SendAsync(new JsonObject { ["type"] = "turn.end" }, ct);

    /// <summary>No more audio: the server sends the remaining finals, then "done".</summary>
    public Task FinishAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "input_audio_buffer.commit", ["final"] = true }, ct);

    /// <summary>Server events until "done", "error" or the connection closes.</summary>
    public async IAsyncEnumerable<StreamEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var buf = new byte[64 * 1024];
        using var msg = new MemoryStream();
        while (_ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            msg.SetLength(0);
            do
            {
                try { r = await _ws.ReceiveAsync(buf, ct).ConfigureAwait(false); }
                catch (WebSocketException) { yield break; }
                if (r.MessageType == WebSocketMessageType.Close) yield break;
                msg.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            JsonObject? m;
            try { m = JsonNode.Parse(msg.ToArray()) as JsonObject; }
            catch (JsonException) { continue; }
            if (m is null) continue;
            var e = StreamEvent.From(m);
            if (e.Type == "session") { JobId = m["job_id"]?.GetValue<string>(); continue; }
            yield return e;
            if (e.Type is "done" or "error") yield break;
        }
    }

    private async Task SendAsync(JsonObject m, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(m.ToJsonString());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
            catch (WebSocketException) { }
        }
        _ws.Dispose();
        _sendLock.Dispose();
    }
}
