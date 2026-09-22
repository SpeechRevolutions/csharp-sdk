// Examples for the Speech Revolutions C# SDK. Pick one with the first arg:
//   dotnet run                # transcribe (default) — options + live progress bars, saves the file
//   dotnet run -- retrieve    # submit() without waiting, then poll/get a job by id + list
//   dotnet run -- progress    # live progress as one 0–100 number (web-app pattern)
// The API key is read from SPEECHREVOLUTIONS_API_KEY.

using SpeechRevolutions;

var mode = args.Length > 0 ? args[0] : "transcribe";
switch (mode)
{
    case "retrieve":
        await Examples.RetrieveAsync();
        break;
    case "progress":
        await Examples.ProgressAsync();
        break;
    default:
        await Examples.TranscribeAsync();
        break;
}

return 0;

static class Examples
{
    // --- transcribe: every option explicit, live bars, save to disk ---------
    public static async Task TranscribeAsync()
    {
        using var client = new SpeechRevolutionsClient(); // reads SPEECHREVOLUTIONS_API_KEY

        var options = new TranscribeOptions
        {
            OutputType = OutputType.Json,   // txt | json | srt | vtt | docx | pdf
            WordTimestamps = true,          // per-word start/end times
            SpeakerLabels = true,           // who spoke each segment (alias: Diarize)
            Nltk = true,                    // restore punctuation & capitalization
            Tier = ProcessingTier.Standard, // standard | economy
            CustomVocabulary = null,        // domain terms to bias toward, or null
            CallbackUrl = null,             // webhook URL for completion/failure, or null
            OnUploadProgress = null,        // Action<ProgressEvent> for upload %, or null
            Progress = true,                // render live upload + transcription bars (stderr)
        };

        var result = await client.TranscribeAsync("audio.mp3", options, onProgress: null);
        var outPath = await result.SaveAsync("output"); // writes output.<OutputType>
        Console.WriteLine($"Saved transcript to {outPath}");
    }

    // --- retrieve: list / get-by-id / fetch transcript ----------------------
    public static async Task RetrieveAsync()
    {
        using var client = new SpeechRevolutionsClient();

        // 1. Fire-and-forget: SubmitAsync returns a job id immediately, no waiting.
        var jobId = await client.SubmitAsync("audio.mp3");
        Console.WriteLine($"submitted job {jobId}");

        // (Bonus) list your most-recent jobs (newest first), cursor-paginated.
        var page = await client.ListJobsAsync(limit: 10);
        Console.WriteLine($"{page.Jobs.Count} recent job(s); nextBefore={page.NextBefore}");

        // 2. Collect later: poll status, then fetch the transcript by id.
        Console.WriteLine($"Fetching transcript for {jobId} ...");
        while (true)
        {
            var status = await client.GetJobStatusAsync(jobId);
            Console.WriteLine($"  status: {status.Status}");
            if (status.IsCompleted)
            {
                var result = await client.GetTranscriptAsync(jobId); // downloads + parses
                Console.WriteLine(result.Text[..Math.Min(500, result.Text.Length)]);
                return;
            }
            if (status.IsFailed)
            {
                Console.Error.WriteLine($"job failed at {status.FailedStage}: {status.Reason}");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    // --- progress: two callbacks -> one 0–100 number for your UI ------------
    public static async Task ProgressAsync()
    {
        using var client = new SpeechRevolutionsClient();

        // Weight the phases into a single bar (upload is usually quick).
        const double uploadWeight = 0.15, transcribeWeight = 0.85;
        double overall = 0;
        var gate = new object();
        void Set(string phase, double value)
        {
            lock (gate)
            {
                overall = Math.Max(overall, value); // never go backwards
                Console.WriteLine($"  [{phase,10}] {overall,5:0.0}%");
            }
        }

        var options = new TranscribeOptions
        {
            // Upload-phase callback (Step == "upload").
            OnUploadProgress = e => Set("upload", (e.Percent ?? 0) * uploadWeight),
        };

        // Transcription-phase callback is the onProgress argument.
        var result = await client.TranscribeAsync(
            "audio.mp3",
            options,
            onProgress: e => Set("transcribe", uploadWeight * 100 + (e.Percent ?? 0) * transcribeWeight));

        Set("done", 100);
        Console.WriteLine($"\nDone — {result.Text.Length} chars");
        // In a web app you'd store `overall` per job (keyed by id) and serve it
        // from an endpoint your frontend polls, instead of writing to the console.
    }
}
