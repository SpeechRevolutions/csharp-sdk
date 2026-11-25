using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SpeechRevolutions;
using Xunit;

namespace SpeechRevolutions.Tests;

/// <summary>The streaming session against a local fake server: normal flow, resume after a drop,
/// failover replay and fatal errors.</summary>
public class StreamingTests
{
    private const int Ms = 32; // bytes per millisecond of PCM16 mono 16 kHz

    private static byte[] Pcm(int ms, short v = 1)
    {
        var b = new byte[ms * Ms];
        for (var i = 0; i < b.Length; i += 2) BitConverter.TryWriteBytes(b.AsSpan(i), v);
        return b;
    }

    private sealed class FakeConn
    {
        public required string Url;
        public required WebSocket Ws;
        public readonly List<JsonElement> Got = new();
        public readonly SemaphoreSlim Signal = new(0, int.MaxValue);

        public async Task Send(object m) =>
            await Ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(m), WebSocketMessageType.Text, true, CancellationToken.None);

        public byte[] Audio(int from = 0)
        {
            lock (Got)
            {
                return Got.Skip(from)
                    .Where(m => m.GetProperty("type").GetString() == "input_audio_buffer.append")
                    .SelectMany(m => Convert.FromBase64String(m.GetProperty("audio").GetString()!))
                    .ToArray();
            }
        }

        public async Task WaitFor(Func<bool> cond)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                lock (Got) if (cond()) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException("condition never held");
                await Signal.WaitAsync(200);
            }
        }

        public bool HasFinalCommit()
        {
            lock (Got)
                return Got.Any(m => m.GetProperty("type").GetString() == "input_audio_buffer.commit"
                                    && m.TryGetProperty("final", out var f) && f.ValueKind == JsonValueKind.True);
        }
    }

    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public readonly List<FakeConn> Conns = new();
        public string BaseUrl { get; }

        public FakeServer(Func<int, FakeConn, Task> script)
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        var wsCtx = await ctx.AcceptWebSocketAsync(null);
                        var conn = new FakeConn { Url = ctx.Request.RawUrl!, Ws = wsCtx.WebSocket };
                        int n;
                        lock (Conns) { Conns.Add(conn); n = Conns.Count; }
                        _ = Task.Run(async () =>
                        {
                            var buf = new byte[1 << 20];
                            while (conn.Ws.State == WebSocketState.Open)
                            {
                                using var ms = new MemoryStream();
                                WebSocketReceiveResult r;
                                try
                                {
                                    do
                                    {
                                        r = await conn.Ws.ReceiveAsync(buf, CancellationToken.None);
                                        ms.Write(buf, 0, r.Count);
                                    } while (!r.EndOfMessage);
                                }
                                catch { break; }
                                if (r.MessageType == WebSocketMessageType.Close) break;
                                var el = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
                                lock (conn.Got) conn.Got.Add(el);
                                conn.Signal.Release();
                            }
                        });
                        await script(n, conn);
                        await Task.Delay(200);
                    });
                }
            });
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        public void Dispose() => _listener.Stop();
    }

    private static async Task FinishNormally(FakeConn c)
    {
        await c.WaitFor(c.HasFinalCommit);
        await c.Send(new { type = "final", index = 0, text = "hello world", start = 0, end = 1 });
        await c.Send(new { type = "transcription.done", text = "hello world" });
    }

    private static SpeechRevolutionsClient Client(string baseUrl) => new("k", baseUrl);

    [Fact]
    public async Task NormalSession()
    {
        using var srv = new FakeServer(async (n, c) =>
        {
            await c.Send(new { type = "session.created", job_id = "job-1" });
            await FinishNormally(c);
        });
        var session = await Client(srv.BaseUrl).StreamAsync(new StreamOptions
        {
            Language = "en", Vocabulary = new[] { "Zephyr" }, TurnDetection = new TurnDetection { Threshold = 0.5 },
        });
        session.SendAudio(Pcm(400));
        var result = await session.FinishAsync(new CancellationTokenSource(5000).Token);
        var c = srv.Conns[0];
        Assert.Equal("/api/v1/stream", c.Url);
        var upd = c.Got[0];
        Assert.Equal("session.update", upd.GetProperty("type").GetString());
        Assert.Equal("en", upd.GetProperty("language").GetString());
        Assert.Equal(0.5, upd.GetProperty("turn_detection").GetProperty("threshold").GetDouble());
        Assert.Equal("input_audio_buffer.commit", c.Got[1].GetProperty("type").GetString());
        Assert.False(c.Got[1].TryGetProperty("final", out _));
        Assert.Equal(Pcm(400), c.Audio());
        Assert.True(result.Complete);
        Assert.Equal("hello world", result.Text);
        Assert.Equal("job-1", result.JobId);
        Assert.Null(result.Voiceprint);
        Assert.Null(result.VoiceprintQuality);
    }

    [Fact]
    public async Task CapturedVoiceprintComesBackInTheResult()
    {
        var events = new List<StreamEvent>();
        using var srv = new FakeServer(async (n, c) =>
        {
            await c.Send(new { type = "session.created", job_id = "job-v" });
            await c.Send(new { type = "voice_focus", active = true });
            await c.WaitFor(c.HasFinalCommit);
            await c.Send(new { type = "final", index = 0, text = "hello", start = 0, end = 1 });
            await c.Send(new
            {
                type = "transcription.done", text = "hello", voiceprint = "vp1.sealed",
                voiceprint_quality = new { score = 0.82, ok = true, reasons = Array.Empty<string>() },
            });
        });
        var session = await Client(srv.BaseUrl).StreamAsync(new StreamOptions
        {
            CaptureVoiceprint = true, RedactPii = true,
            OnEvent = e => { lock (events) events.Add(e); },
        });
        session.SendAudio(Pcm(400));
        var result = await session.FinishAsync(new CancellationTokenSource(5000).Token);
        var upd = srv.Conns[0].Got[0];
        Assert.True(upd.GetProperty("capture_voiceprint").GetBoolean());
        Assert.True(upd.GetProperty("redact_pii").GetBoolean());
        Assert.Equal("vp1.sealed", result.Voiceprint);
        Assert.NotNull(result.VoiceprintQuality);
        Assert.True(result.VoiceprintQuality!.Ok);
        Assert.Equal(0.82, result.VoiceprintQuality.Score);
        Assert.Empty(result.VoiceprintQuality.Reasons);
        lock (events) Assert.Contains(events, e => e.Type == "voice_focus" && e.Active == true);
    }

    [Fact]
    public async Task ResumeReplaysUnacknowledgedAudio()
    {
        var dropped = new TaskCompletionSource();
        using var srv = new FakeServer(async (n, c) =>
        {
            if (n == 1)
            {
                await c.Send(new { type = "session.created", job_id = "job-r" });
                await c.WaitFor(() => c.Audio().Length >= 600 * Ms);
                await c.Send(new { type = "audio.committed", through_ms = 200 });
                await Task.Delay(50);
                dropped.SetResult();
                await c.Ws.CloseAsync((WebSocketCloseStatus)1012, "deploy", CancellationToken.None);
                return;
            }
            await c.Send(new { type = "session.resumed", job_id = "job-r", resume_from_ms = 200 });
            await FinishNormally(c);
        });
        var session = await Client(srv.BaseUrl).StreamAsync(new StreamOptions { Language = "en" });
        session.SendAudio(Pcm(600, 7));
        await dropped.Task;
        await Task.Delay(300);
        session.SendAudio(Pcm(200, 9));
        var result = await session.FinishAsync(new CancellationTokenSource(10000).Token);
        var c2 = srv.Conns[1];
        Assert.Equal("/api/v1/stream?job_id=job-r", c2.Url);
        Assert.Equal("input_audio_buffer.rewind", c2.Got[0].GetProperty("type").GetString());
        Assert.Equal(200, c2.Got[0].GetProperty("from_ms").GetInt32());
        Assert.DoesNotContain(c2.Got, m => m.GetProperty("type").GetString() == "session.update");
        Assert.Equal(Pcm(400, 7).Concat(Pcm(200, 9)).ToArray(), c2.Audio());
        Assert.True(result.Complete);
        Assert.Equal("job-r", result.JobId);
    }

    [Fact]
    public async Task FailoverResendReplaysOnTheSameConnection()
    {
        using var srv = new FakeServer(async (n, c) =>
        {
            await c.Send(new { type = "session.created", job_id = "j" });
            await c.WaitFor(() => c.Audio().Length >= 480 * Ms);
            await c.Send(new { type = "audio.committed", through_ms = 160 });
            await c.Send(new { type = "resend", from_ms = 160 });
            await FinishNormally(c);
        });
        var session = await Client(srv.BaseUrl).StreamAsync(new StreamOptions { Language = "en" });
        session.SendAudio(Pcm(480, 3));
        await Task.Delay(300);
        await session.FinishAsync(new CancellationTokenSource(10000).Token);
        var c = srv.Conns[0];
        var rw = c.Got.FindIndex(m => m.GetProperty("type").GetString() == "input_audio_buffer.rewind");
        Assert.True(rw > 0);
        Assert.Equal(160, c.Got[rw].GetProperty("from_ms").GetInt32());
        Assert.Equal(Pcm(320, 3), c.Audio(rw));
    }

    [Fact]
    public async Task FatalErrorAtConnect()
    {
        using var srv = new FakeServer(async (n, c) =>
            await c.Send(new { type = "error", code = "insufficient_balance", message = "top up" }));
        var e = await Assert.ThrowsAsync<StreamException>(() => Client(srv.BaseUrl).StreamAsync(new StreamOptions { Language = "en" }));
        Assert.Equal("insufficient_balance", e.Code);
    }

    [Fact]
    public async Task FatalErrorMidSession()
    {
        using var srv = new FakeServer(async (n, c) =>
        {
            await c.Send(new { type = "session.created", job_id = "j" });
            await c.WaitFor(() => c.Audio().Length > 0);
            await c.Send(new { type = "error", code = "use_batch", message = "sent too fast" });
        });
        var session = await Client(srv.BaseUrl).StreamAsync(new StreamOptions { Language = "en" });
        session.SendAudio(Pcm(100));
        var e = await Assert.ThrowsAsync<StreamException>(() => session.FinishAsync(new CancellationTokenSource(5000).Token));
        Assert.Equal("use_batch", e.Code);
    }

    [Fact]
    public async Task RevisedFinalsAndEventsInOrder()
    {
        using var srv = new FakeServer(async (n, c) =>
        {
            await c.Send(new { type = "session.created", job_id = "j" });
            await c.WaitFor(c.HasFinalCommit);
            await c.Send(new { type = "final", index = 0, text = "helo", start = 0, end = 1 });
            await c.Send(new { type = "final.revised", index = 0, text = "hello" });
            await c.Send(new { type = "transcription.done", text = "hello" });
        });
        var seen = new List<string>();
        var session = await Client(srv.BaseUrl).StreamAsync(new StreamOptions { Language = "en", OnEvent = e => { lock (seen) seen.Add(e.Type); } });
        var result = await session.FinishAsync(new CancellationTokenSource(5000).Token);
        Assert.Equal("hello", result.Finals[0].Text);
        Assert.Equal(0, result.Finals[0].Start);
        Assert.Equal(new[] { "session.created", "final", "final.revised", "transcription.done" }, seen);
        var fromChannel = new List<string>();
        await foreach (var e in session.Events.ReadAllAsync()) fromChannel.Add(e.Type);
        Assert.Equal(seen, fromChannel);
    }

    [Fact]
    public async Task OddLengthPcmIsRefused()
    {
        using var srv = new FakeServer(async (n, c) => await c.Send(new { type = "session.created", job_id = "j" }));
        var session = await Client(srv.BaseUrl).StreamAsync(new StreamOptions { Language = "en" });
        Assert.Throws<ArgumentException>(() => session.SendAudio(new byte[3]));
        await session.DisposeAsync();
    }
}
