# Speech Revolutions — C# SDK

Official C# client for the Speech Revolutions STT API. Async-first (`net8.0`),
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

### From a URL (Deepgram-style)

```csharp
var result = await client.TranscribeUrlAsync("https://example.com/audio.mp3");
// or just pass the URL to TranscribeAsync — http(s):// paths are detected:
var same = await client.TranscribeAsync("https://example.com/audio.mp3");
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
| `Tier` | `ProcessingTier` | `Standard` | `Standard \| Economy` |
| `CustomVocabulary` | `IReadOnlyList<string>?` | `null` | domain terms to bias toward |
| `Progress` | `bool` | `false` | render live console bars (see below) |
| `OnUploadProgress` | `Action<ProgressEvent>?` | `null` | upload-progress callback (see below) |

```csharp
var result = await client.TranscribeAsync("a.mp3", new TranscribeOptions
{
    OutputType = OutputType.Srt,
    CustomVocabulary = new[] { "Kubernetes", "Anthropic" },
});
```

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
var result = await client.TranscribeAsync("meeting.mp3", new TranscribeOptions
{
    Progress = true,
});
```

```
Uploading:   100% [##############################] 6.0MB/6.0MB
Transcribing: 100% [##############################]
```

### Callbacks

Read `event.Percent` (a clamped `double?` in 0–100, `null` until it can be
computed) to drive your own UI or API.

```csharp
var result = await client.TranscribeAsync(
    "meeting.mp3",
    new TranscribeOptions
    {
        // upload progress — Step == "upload"
        OnUploadProgress = e => Console.WriteLine($"upload {e.Percent:0}%"),
    },
    // transcription progress
    onProgress: e => Console.WriteLine($"{e.Percent:0}% {e.Step}"));
```

Each `ProgressEvent` carries `Completed`, `Total`, `Step`, `ElapsedSeconds`, and
the computed `Percent`.

## Webhooks & retrieving results later

`SubmitAsync` uploads and enqueues a job and returns its id **without waiting** —
ideal for batch/background work. Collect the result later via a webhook
(`CallbackUrl`, a signed POST — verify `X-SR-Signature: sha256=…` against the raw
body) or by polling (`dotnet run -- retrieve`):

```csharp
var jobId = await client.SubmitAsync("meeting.mp3");   // returns immediately, no waiting
// ...or notify a webhook instead of polling:
await client.TranscribeAsync(path, new TranscribeOptions { CallbackUrl = "https://you.example.com/hook" });

var st = await client.GetJobStatusAsync(jobId);      // st.Status: processing|completed|failed
if (st.IsCompleted)
    result = await client.GetTranscriptAsync(jobId); // downloads + parses
var page = await client.ListJobsAsync(limit: 50);    // page.Jobs, page.NextBefore
```

## Result shape

Default `OutputType` is `Json`, which the SDK parses into a transcript-first
`TranscriptResult`:

| Member | Like |
|--------|------|
| `result.Text` | AssemblyAI / ElevenLabs full transcript |
| `result.Transcript` | Deepgram-style alias |
| `result.Words` | word + `Start` / `End` / `Speaker` |
| `result.Utterances` | AssemblyAI-style speaker turns |
| `result.ToDeepgram()` | Deepgram-shaped dictionary |
| `result.ToDict()` | normalized dictionary |
| `result.Content` | raw response bytes |
| `await result.SaveAsync(path)` | write to disk |

`SaveAsync("output")` writes `output.<OutputType>` (extension inferred when the
path has none) and returns the final path. The client never writes files on its
own.

```csharp
var outPath = await result.SaveAsync("output"); // -> output.json
```

## Timeouts and retries

Every method takes a `CancellationToken`; cancelling one surfaces as
`OperationCanceledException`, as .NET callers expect.

JSON API requests that fail to connect or return 429/500/502/503/504 are retried
with exponential backoff, honoring `Retry-After`. Uploads and the progress
stream have their own retry loops.

```csharp
var client = new SpeechRevolutionsClient(
    timeout: TimeSpan.FromMinutes(10),      // whole-job wait (SSE + polling)
    maxRetries: 3,                          // extra attempts per API request
    retryBackoff: TimeSpan.FromMilliseconds(500),
    httpClient: myHttpClient);               // proxies, handlers, tracing
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
```
