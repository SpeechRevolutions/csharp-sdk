# Speech Revolutions — C# SDK

Official C# client for the [Speech Revolutions](https://www.speechrevolutions.com) speech-to-text API. Async-first (`net8.0`),
in the style of the Deepgram / ElevenLabs .NET clients.

## Install

```bash
dotnet add package SpeechRevolutions
```

Or reference the project directly:

```xml
<ProjectReference Include="path/to/SpeechRevolutions/SpeechRevolutions.csproj" />
```

## Quick start

```csharp
using SpeechRevolutions;

using var client = new SpeechRevolutionsClient(); // reads SPEECHREVOLUTIONS_API_KEY
var result = await client.TranscribeAsync("meeting.mp3", new TranscribeOptions
{
    SpeakerLabels = true, // or Diarize = true
});

Console.WriteLine(result.Text);
foreach (var u in result.Utterances)
    Console.WriteLine($"Speaker {u.Speaker}: {u.Text}");
```

That is a complete `Program.cs` for `dotnet new console`. The snippets below
continue it: each one needs only `client` and `using SpeechRevolutions;`.

### From a URL (Deepgram-style)

```csharp
var audioUrl = "https://docs.speechrevolutions.com/samples/diamond-necklace.mp3";
var fromUrl = await client.TranscribeUrlAsync(audioUrl);
// or just pass the URL to TranscribeAsync — http(s):// paths are detected:
var same = await client.TranscribeAsync(audioUrl);
Console.WriteLine(fromUrl.Text == same.Text ? fromUrl.Text : $"{fromUrl.Text}\n{same.Text}");
```

The platform fetches the URL itself — the audio never passes through your
process. `TranscribeFileAsync` is the same for a local path, and
`TranscribeAsync` also accepts a `byte[]` overload for in-memory audio.

## Options

Pass a `TranscribeOptions`. `Diarize` is a Deepgram-compatible alias for
`SpeakerLabels` (when set, it wins).

| Option | Type | Default | Notes |
|--------|------|---------|-------|
| `OutputType` | `OutputType` | `Json` | `Txt \| Json \| Srt \| Vtt \| Docx \| Pdf` |
| `WordTimestamps` | `bool` | `true` | per-word start/end times |
| `SpeakerLabels` | `bool` | `true` | label who spoke each segment |
| `Diarize` | `bool?` | `null` | alias for `SpeakerLabels` |
| `Nltk` | `bool` | `true` | restore punctuation & capitalization |
| `Tier` | `ProcessingTier` | `Standard` | `Standard` — the only tier currently available |
| `CustomVocabulary` | `IReadOnlyList<string>?` | `null` | domain terms to bias toward |
| `Language` | `string?` | `null` | ISO 639-1 code (e.g. `"en"`, `"ru"`); `null` = auto-detect |
| `Progress` | `bool` | `false` | render live console bars (see below) |
| `OnUploadProgress` | `Action<ProgressEvent>?` | `null` | upload-progress callback (see below) |

```csharp
var captions = await client.TranscribeAsync("meeting.mp3", new TranscribeOptions
{
    OutputType = OutputType.Srt,
    CustomVocabulary = new[] { "Kubernetes", "Anthropic" },
    Language = "en",
});
Console.WriteLine(captions.Text); // the SRT document
```

`Language` skips automatic language detection and transcribes the whole file in
that language. Pinning the wrong language makes the model translate into it
(Russian audio pinned to `"en"` comes back as an English translation), so leave
it `null` unless you know the language; an unsupported code returns HTTP 422.

## Live progress

Unlike AssemblyAI / Deepgram (which give no percentage for pre-recorded audio),
you get real-time progress for **both** the file upload and the transcription —
as a console bar, a callback, or both. They compose: the bars render *and* your
callbacks still fire for every event.

### Console bars

Set `Progress = true`. An `Uploading` byte bar renders first, then a
`Transcribing` bar. Both are single-line, updated in place, and written to
`stderr` (so they never pollute piped `stdout`).

```csharp
var withBars = await client.TranscribeAsync("meeting.mp3", new TranscribeOptions
{
    Progress = true,
});
Console.WriteLine($"{withBars.Text.Length} characters");
```

```
Uploading:   100% [##############################] 6.0MB/6.0MB
Transcribing: 100% [##############################]
```

### Callbacks

Read `event.Percent` (a clamped `double?` in 0–100, `null` until it can be
computed) to drive your own UI or API.

```csharp
var tracked = await client.TranscribeAsync(
    "meeting.mp3",
    new TranscribeOptions
    {
        // upload progress — Step == "upload"
        OnUploadProgress = e => Console.WriteLine($"upload {e.Percent:0}%"),
    },
    // transcription progress, e.g. 50% chunk:0, then 100% completed
    onProgress: e => Console.WriteLine($"{e.Percent:0}% {e.Step}"));
Console.WriteLine(tracked.Text);
```

Each `ProgressEvent` carries `Completed`, `Total`, `Step`, `ElapsedSeconds`, and
the computed `Percent`. A successful transcription always ends with a 100% event
(`Step == "completed"`), even for a short file the service finishes without
streaming any progress.

## Webhooks & retrieving results later

`SubmitAsync` uploads and enqueues a job and returns its id **without waiting** —
ideal for batch/background work. Collect the result later via a webhook
(`CallbackUrl`, a signed POST — verify `X-SR-Signature: sha256=…` against the raw
body) or by polling (`dotnet run -- retrieve`):

```csharp
var jobId = await client.SubmitAsync("meeting.mp3"); // returns immediately, no waiting
// ...or have the finished job POSTed to your webhook instead of polling:
// var jobId = await client.SubmitAsync("meeting.mp3",
//     new TranscribeOptions { CallbackUrl = "https://you.example.com/hook" });

var st = await client.GetJobStatusAsync(jobId); // st.Status: processing|completed|failed
if (st.IsCompleted)
{
    var later = await client.GetTranscriptAsync(jobId); // downloads + parses
    Console.WriteLine(later.Text);
}
var page = await client.ListJobsAsync(limit: 50); // page.Jobs, page.NextBefore
Console.WriteLine($"{page.Jobs.Count} recent jobs");
```

The signature is HMAC-SHA256 over the raw request body, keyed with **your account's own
webhook signing secret** — find it under API Keys in the
[console](https://console.speechrevolutions.com) and put it in
`SPEECHREVOLUTIONS_WEBHOOK_SECRET`. Compare it with a constant-time function, and verify
against the bytes you received rather than a re-serialised copy. A complete receiver
(`Program.cs` of a `dotnet new web` project):

```csharp
using System.Security.Cryptography;
using System.Text;

var app = WebApplication.CreateBuilder(args).Build();
var secret = Encoding.UTF8.GetBytes(
    Environment.GetEnvironmentVariable("SPEECHREVOLUTIONS_WEBHOOK_SECRET")!);

app.MapPost("/webhooks/stt", async (HttpRequest request) =>
{
    using var ms = new MemoryStream();
    await request.Body.CopyToAsync(ms); // the exact bytes received
    var raw = ms.ToArray();

    var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(secret, raw)).ToLowerInvariant();
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(request.Headers["X-SR-Signature"].ToString())))
        return Results.Unauthorized();

    Console.WriteLine(Encoding.UTF8.GetString(raw)); // {"job_id": ..., "status": "completed", "download_url": ...}
    return Results.Ok();
});

app.Run();
```

Worked receivers for Python, Node, Go and C# are in the
[webhooks guide](https://docs.speechrevolutions.com/guides/webhooks).

## Result shape

Default `OutputType` is `Json`, which the SDK parses into a transcript-first
`TranscriptResult`:

| Member | Like |
|--------|------|
| `result.Text` | AssemblyAI / ElevenLabs full transcript |
| `result.Transcript` | Deepgram-style alias |
| `result.Words` | word + `Start` / `End` / `Speaker` |
| `result.Utterances` | AssemblyAI-style speaker turns |
| `result.ToDeepgram()` | Deepgram-shaped dictionary (0-based integer speakers) |
| `result.ToDict()` | normalized dictionary |
| `result.Content` | raw response bytes |
| `await result.SaveAsync(path)` | write to disk |

`SaveAsync("output")` writes `output.<OutputType>` (extension inferred when the
path has none) and returns the final path. The client never writes files on its
own.

```csharp
var outPath = await result.SaveAsync("output"); // -> output.json
Console.WriteLine($"saved to {outPath}");
```

## Timeouts and retries

Every method takes a `CancellationToken`; cancelling one surfaces as
`OperationCanceledException`, as .NET callers expect.

JSON API requests that fail to connect or return 429/500/502/503/504 are retried
with exponential backoff, honoring `Retry-After`. Uploads and the progress
stream have their own retry loops.

```csharp
using var tuned = new SpeechRevolutionsClient(
    timeout: TimeSpan.FromMinutes(10),      // whole-job wait (SSE + polling)
    maxRetries: 3,                          // extra attempts per API request
    retryBackoff: TimeSpan.FromMilliseconds(500),
    httpClient: new HttpClient());          // yours: proxies, handlers, tracing

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var bounded = await tuned.TranscribeAsync("meeting.mp3", cancellationToken: cts.Token);
Console.WriteLine(bounded.Text);
```

Errors carry `StatusCode`, `RequestId` and `Body` where the server supplied
them; `RateLimitException.RetryAfter` holds the server's hint in seconds.

## Auth

```bash
export SPEECHREVOLUTIONS_API_KEY=stt_...
```

Or pass it directly: `new SpeechRevolutionsClient(apiKey: "stt_...")`.

## Errors

All errors derive from `SpeechRevolutionsException`: `AuthenticationException`,
`RateLimitException`, `JobNotFoundException`, `JobFailedException`
(`Step` / `Reason`), `UploadException`, `JobTimeoutException`, and `ApiException`
(`StatusCode` / `Body`).

## Links

- [Speech Revolutions](https://www.speechrevolutions.com) — the speech-to-text API this library talks to
- [Documentation](https://docs.speechrevolutions.com) — API reference, guides and quickstarts
