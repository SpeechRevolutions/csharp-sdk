using System.Net;
using System.Net.Http.Headers;

namespace SpeechRevolutions;

// Renders ProgressEvents as a single in-place bar on stderr and forwards each
// event to an optional user callback. There's no tqdm in .NET, so this is a
// small renderer: label + percentage + [####----], redrawn with a carriage
// return. Redraws are throttled since a byte upload fires many events, but the
// final 100% frame always gets drawn.
//
// The per-chunk step name is deliberately left off the transcription bar —
// chunks finish out of order and it made the label jump around. Callers who
// want it can read ProgressEvent.Step from their own callback.
internal sealed class ProgressPrinter
{
    private const int Width = 30;
    private static readonly TimeSpan MinRedrawInterval = TimeSpan.FromMilliseconds(80);

    private readonly Action<ProgressEvent>? _forward;
    private readonly string _label;
    private readonly bool _bytesMode;

    private DateTime? _lastDraw;
    private double? _lastPct;
    private int _lastTotal;
    private bool _drewAnything;
    private bool _closed;

    public ProgressPrinter(Action<ProgressEvent>? forward, string label, bool bytesMode)
    {
        _forward = forward;
        _label = label;
        _bytesMode = bytesMode;
    }

    public void Report(ProgressEvent e)
    {
        try
        {
            Render(e);
        }
        finally
        {
            _forward?.Invoke(e);
        }
    }

    private void Render(ProgressEvent e)
    {
        var pct = e.Percent;
        if (pct is null)
            return;

        var completed = e.Completed ?? 0;
        var total = e.Total ?? 0;
        _lastTotal = total;

        // Always draw the final frame; otherwise throttle (uploads emit many events).
        var complete = total > 0 && e.Completed is not null && e.Completed.Value >= total;
        var now = DateTime.UtcNow;
        var throttleOk = _lastDraw is null || (now - _lastDraw.Value) >= MinRedrawInterval;
        if (!complete && !throttleOk)
            return;

        Draw(pct.Value, completed, total, newline: complete);
        _lastDraw = now;
        _lastPct = pct;
    }

    private void Draw(double pct, int completed, int total, bool newline)
    {
        var filled = (int)(Width * pct / 100.0);
        filled = Math.Clamp(filled, 0, Width);
        var bar = new string('#', filled) + new string('-', Width - filled);

        var line = _bytesMode && total > 0
            ? $"\r{_label}: {pct,3:0}% [{bar}] {FormatBytes(completed)}/{FormatBytes(total)}"
            : $"\r{_label}: {pct,3:0}% [{bar}]";

        Console.Error.Write(line);
        if (newline)
            Console.Error.Write('\n');
        Console.Error.Flush();
        _drewAnything = true;
    }

    // Transcription completes via a "completed" SSE event, not a 100% progress
    // event, so the final frame may never have been drawn — draw it now.
    // Safe to call more than once.
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        if (!_drewAnything)
            return;
        if (_lastPct is >= 100.0)
            return;
        Draw(100.0, _lastTotal, _lastTotal, newline: true);
    }

    private static string FormatBytes(long n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = n;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{(long)v}{units[i]}" : $"{v:0.0}{units[i]}";
    }
}

// Streams an in-memory buffer in chunks, reporting byte-level progress after
// each one. Content-Length is set explicitly (see TryComputeLength) so
// HttpClient sends a fixed-length body instead of chunked transfer encoding —
// presigned S3 PUTs reject chunked requests.
internal sealed class ProgressByteArrayContent : HttpContent
{
    private const int ChunkSize = 64 * 1024;

    private readonly byte[] _data;

    // Receives (bytesSent, totalBytes).
    private readonly Action<int, int>? _onProgress;

    public ProgressByteArrayContent(byte[] data, string contentType, Action<int, int>? onProgress)
    {
        _data = data;
        _onProgress = onProgress;
        Headers.ContentType = new MediaTypeHeaderValue(contentType);
        // Explicit length keeps the request fixed-length (not chunked).
        Headers.ContentLength = data.Length;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var total = _data.Length;
        var sent = 0;
        while (sent < total)
        {
            var len = Math.Min(ChunkSize, total - sent);
            await stream.WriteAsync(_data.AsMemory(sent, len), cancellationToken);
            sent += len;
            _onProgress?.Invoke(sent, total);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _data.Length;
        return true;
    }
}
