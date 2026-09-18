using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SpeechRevolutions;

/// <summary>
/// Client for the Speech Revolutions speech-to-text API.
/// </summary>
public sealed class SttClient : IDisposable
{
    private const string DefaultBaseUrl = "https://api.speechrevolutions.com";
    private static readonly TimeSpan UploadProgressInterval = TimeSpan.FromSeconds(10);
    private const int UploadMaxAttempts = 4;
    private static readonly TimeSpan UploadBaseDelay = TimeSpan.FromSeconds(1);
    private const int SseMaxReconnects = 10;

    /// <summary>
    /// How many times to retry a stream endpoint that answered with a NON-2xx
    /// status, as opposed to one whose connection dropped.
    /// <para>
    /// The two look the same to the reconnect loop and are not the same thing.
    /// A drop is transient. A non-2xx is a refusal: a proxy or load balancer
    /// that does not pass text/event-stream answers every attempt identically,
    /// forever, so the full ladder just burns 30s before falling back to
    /// polling — on every job.
    /// </para>
    /// </summary>
    private const int SseMaxStatusRefusals = 2;
    private static readonly TimeSpan SseReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private const int DefaultMaxRetries = 3;
    private static readonly TimeSpan DefaultRetryBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryBackoffMax = TimeSpan.FromSeconds(30);
    private static readonly int[] RetryStatusCodes = { 429, 500, 502, 503, 504 };
    private static readonly string[] RequestIdHeaders = { "x-request-id", "x-amzn-requestid", "cf-ray" };

    /// <summary>
    /// Endpoints that CREATE a job, and so are not safe to blindly retry.
    /// <para>
    /// A job is created the moment the server handles one of these; the response
    /// carrying the job id back is what can be lost. Retrying after the request
    /// may have arrived creates a SECOND job for the same audio - two
    /// transcripts, two charges - and the caller never learns about the orphan.
    /// The API has no idempotency key, so the only safe rule is to retry these
    /// solely when the request provably never reached the server.
    /// </para>
    /// <para>
    /// Every other endpoint either reads, or acts on a job id the caller already
    /// holds, and stays fully retryable.
    /// </para>
    /// </summary>
    private static readonly string[] JobCreatingPaths =
    {
        "/api/v1/upload",
        "/api/v1/upload/multipart/create",
    };

    /// <summary>
    /// Resolves the API host: an explicit value, then the environment, then
    /// production.
    /// <para>
    /// Symmetric with the API key — if a caller can supply a key from the
    /// environment, they can point it at an environment too. Needed for
    /// staging, for an egress proxy or gateway, and for running any published
    /// example against something that is not production.
    /// </para>
    /// </summary>
    private static string ResolveBaseUrl(string? baseUrl)
    {
        baseUrl ??= Environment.GetEnvironmentVariable("SPEECHREVOLUTIONS_BASE_URL");
        baseUrl ??= Environment.GetEnvironmentVariable("STT_BASE_URL");
        return (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
    }

    private static bool CreatesJob(string path)
    {
        var clean = path.Split('?')[0].TrimEnd('/');
        return Array.IndexOf(JobCreatingPaths, clean) >= 0;
    }

    /// <summary>
    /// True when the exception proves the request never got to the server, so
    /// retrying it cannot duplicate work. Anything later (a reset mid-flight, a
    /// response-read timeout) is ambiguous and must not be retried for a create.
    /// </summary>
    private static bool NeverReachedServer(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException se)
            {
                return se.SocketErrorCode is SocketError.ConnectionRefused
                    or SocketError.HostNotFound
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.TryAgain;
            }
        }
        return false;
    }

    /// <summary>
    /// The API host this client will talk to, after resolving an explicit
    /// value, then SPEECHREVOLUTIONS_BASE_URL / STT_BASE_URL, then production.
    /// Exposed for parity with the Go and JavaScript clients, and so callers
    /// can confirm which environment they are pointed at.
    /// </summary>
    public string BaseUrl { get; } = DefaultBaseUrl;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    /// <summary>
    /// Sent on every request. HttpClient sends no User-Agent of its own, and the
    /// edge rejects a request without one with a bare 403 — so without this the
    /// SDK cannot reach production at all, while still passing every test that
    /// points at a local mock.
    /// </summary>
    internal const string UserAgent = "speechrevolutions-csharp/0.2.0";

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly bool _multipart;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryBackoff;

    /// <summary>Internal signal that a multipart upload should fall back to single-shot.</summary>
    private sealed class MultipartUnavailableException : Exception { }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="multipart">
    /// Prefer S3 multipart uploads and fall back to a single presigned PUT if the
    /// server has multipart disabled or a multipart upload fails mid-flight. Default true.
    /// </param>
    /// <param name="maxRetries">
    /// Extra attempts for a JSON API request that fails to connect or returns
    /// 429/5xx. Uploads and the progress stream have their own retry loops.
    /// </param>
    /// <param name="retryBackoff">First retry delay; doubles per attempt, capped at 30s.</param>
    public SttClient(
        string? apiKey = null,
        string? baseUrl = null,
        TimeSpan? timeout = null,
        HttpClient? httpClient = null,
        bool multipart = true,
        int maxRetries = DefaultMaxRetries,
        TimeSpan? retryBackoff = null)
    {
        apiKey ??= Environment.GetEnvironmentVariable("SPEECHREVOLUTIONS_API_KEY")
                   ?? Environment.GetEnvironmentVariable("STT_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AuthenticationException(
                "apiKey is required (pass apiKey or set SPEECHREVOLUTIONS_API_KEY / STT_API_KEY)");

        _apiKey = apiKey;
        _baseUrl = ResolveBaseUrl(baseUrl);
        BaseUrl = _baseUrl;
        _timeout = timeout ?? TimeSpan.FromSeconds(600);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _multipart = multipart;
        _maxRetries = Math.Max(0, maxRetries);
        _retryBackoff = retryBackoff ?? DefaultRetryBackoff;

        // Covers the presigned upload and download calls too, which go out on the
        // shared client rather than through ApiRequestAsync. Only set when the
        // caller has not chosen their own, since a supplied HttpClient is theirs.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    // High-level

    /// <summary>
    /// Transcribe a local file path or an http(s) URL. A URL is handed to the
    /// platform to fetch, so nothing is uploaded from here.
    /// </summary>
    public async Task<TranscriptResult> TranscribeAsync(
        string audioPath,
        TranscribeOptions? options = null,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsUrl(audioPath))
            return await TranscribeUrlAsync(audioPath, options, onProgress, cancellationToken);

        var fileData = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        return await TranscribeAsync(fileData, options, onProgress, cancellationToken);
    }

    /// <summary>
    /// Transcribe audio the platform fetches from a public http(s) URL, then wait
    /// for the result.
    /// </summary>
    public async Task<TranscriptResult> TranscribeUrlAsync(
        string url,
        TranscribeOptions? options = null,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TranscribeOptions();
        var (jobId, jobDownloadUrl) = await SubmitUrlAsync(url, options, cancellationToken);
        return await AwaitTranscriptAsync(jobId, jobDownloadUrl, options, onProgress, cancellationToken);
    }

    /// <summary>Alias for <see cref="TranscribeAsync(string, TranscribeOptions?, Action{ProgressEvent}?, CancellationToken)"/> with a local path.</summary>
    public Task<TranscriptResult> TranscribeFileAsync(
        string path,
        TranscribeOptions? options = null,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
        => TranscribeAsync(path, options, onProgress, cancellationToken);

    public async Task<TranscriptResult> TranscribeAsync(
        byte[] audio,
        TranscribeOptions? options = null,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (audio is null || audio.Length == 0)
            throw new ArgumentException("Audio is empty", nameof(audio));

        options ??= new TranscribeOptions();

        // Upload progress: a byte "Uploading" bar (when Progress is on) and/or
        // the OnUploadProgress callback. Both compose.
        var uploadPrinter = options.Progress
            ? new ProgressPrinter(options.OnUploadProgress, "Uploading", bytesMode: true)
            : null;
        Action<ProgressEvent>? uploadCb = uploadPrinter is not null
            ? uploadPrinter.Report
            : options.OnUploadProgress;
        string jobId;
        string jobDownloadUrl;
        try
        {
            (jobId, jobDownloadUrl) = await IngestUploadAsync(audio, options, uploadCb, cancellationToken);
        }
        finally
        {
            uploadPrinter?.Close();
        }

        return await AwaitTranscriptAsync(jobId, jobDownloadUrl, options, onProgress, cancellationToken);
    }

    /// <summary>Waits out the transcription phase and parses the result.</summary>
    private async Task<TranscriptResult> AwaitTranscriptAsync(
        string jobId,
        string jobDownloadUrl,
        TranscribeOptions options,
        Action<ProgressEvent>? onProgress,
        CancellationToken cancellationToken)
    {
        var printer = options.Progress
            ? new ProgressPrinter(onProgress, "Transcribing", bytesMode: false)
            : null;
        Action<ProgressEvent>? progressCb = printer is not null ? printer.Report : onProgress;
        byte[] content;
        string downloadUrl;
        try
        {
            (content, downloadUrl) = await WaitForResultAsync(jobId, jobDownloadUrl, progressCb, cancellationToken);
        }
        finally
        {
            printer?.Close();
        }

        return TranscriptResult.FromContent(jobId, content, options.OutputType.ToApiValue(), downloadUrl);
    }

    /// <summary>
    /// Upload and enqueue a job, returning its <c>job_id</c> WITHOUT waiting for
    /// the result. Collect it later via a webhook (<see cref="TranscribeOptions.CallbackUrl"/>)
    /// or by polling <see cref="GetJobStatusAsync"/> / <see cref="GetTranscriptAsync"/>.
    /// Ideal for batch workloads. <paramref name="audioPath"/> may be a local path
    /// or an http(s) URL.
    /// </summary>
    public async Task<string> SubmitAsync(
        string audioPath,
        TranscribeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (IsUrl(audioPath))
        {
            var (jobId, _) = await SubmitUrlAsync(audioPath, options ?? new TranscribeOptions(), cancellationToken);
            return jobId;
        }

        var fileData = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        return await SubmitAsync(fileData, options, cancellationToken);
    }

    /// <summary>
    /// Registers a job the platform fetches itself, returning (jobId, downloadUrl).
    /// No bytes leave this process.
    /// </summary>
    private async Task<(string JobId, string DownloadUrl)> SubmitUrlAsync(
        string audioUrl, TranscribeOptions options, CancellationToken ct)
    {
        var payload = BuildUploadPayload(null, options);
        payload["audio_url"] = audioUrl;

        using var doc = await ApiRequestAsync(HttpMethod.Post, "/api/v1/upload", payload, ct);
        var root = doc.RootElement;
        return (root.GetProperty("job_id").ToString(), root.GetProperty("download_url").GetString()!);
    }

    private static bool IsUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Submit raw audio bytes without waiting; returns the job id.</summary>
    public async Task<string> SubmitAsync(
        byte[] audio,
        TranscribeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (audio is null || audio.Length == 0)
            throw new ArgumentException("Audio is empty", nameof(audio));

        options ??= new TranscribeOptions();

        var uploadPrinter = options.Progress
            ? new ProgressPrinter(options.OnUploadProgress, "Uploading", bytesMode: true)
            : null;
        Action<ProgressEvent>? uploadCb = uploadPrinter is not null
            ? uploadPrinter.Report
            : options.OnUploadProgress;
        try
        {
            var (jobId, _) = await IngestUploadAsync(audio, options, uploadCb, cancellationToken);
            return jobId;
        }
        finally
        {
            uploadPrinter?.Close();
        }
    }

    // Upload flow

    /// <summary>JSON body shared by the single-shot and multipart create endpoints.</summary>
    private static Dictionary<string, object?> BuildUploadPayload(int? fileSize, TranscribeOptions options)
    {
        var payload = new Dictionary<string, object?>
        {
            ["output_type"] = options.OutputType.ToApiValue(),
            ["word_timestamps"] = options.WordTimestamps,
            ["speaker_labels"] = options.EffectiveSpeakerLabels,
            ["nltk"] = options.Nltk,
            ["tier"] = options.Tier.ToApiValue(),
        };
        if (fileSize is not null)
            payload["file_size"] = fileSize.Value;
        if (options.CustomVocabulary is { Count: > 0 })
            payload["custom_vocabulary"] = options.CustomVocabulary;
        if (!string.IsNullOrEmpty(options.CallbackUrl))
            payload["callback_url"] = options.CallbackUrl;
        return payload;
    }

    /// <summary>
    /// Get audio into the platform and return (jobId, downloadUrl). Prefers a
    /// multipart upload (when enabled) and falls back to a single presigned PUT if
    /// the server has multipart disabled or a multipart upload fails mid-flight.
    /// </summary>
    private async Task<(string JobId, string DownloadUrl)> IngestUploadAsync(
        byte[] data, TranscribeOptions options, Action<ProgressEvent>? uploadCb, CancellationToken ct)
    {
        if (_multipart)
        {
            try
            {
                return await UploadMultipartAsync(data, options, uploadCb, ct);
            }
            catch (MultipartUnavailableException)
            {
                // multipart unavailable — fall through to the single-shot path
            }
        }
        var job = await CreateUploadJobAsync(data.Length, options, ct);
        await UploadAudioAsync(job.UploadUrl, data, job.JobId, onProgress: uploadCb, cancellationToken: ct);
        await CompleteUploadAsync(job.JobId, ct);
        return (job.JobId, job.DownloadUrl);
    }

    /// <summary>
    /// S3 multipart flow: create -> PUT each part -> complete. Throws
    /// <see cref="MultipartUnavailableException"/> when the server has multipart
    /// disabled (404) or a mid-flight failure means we should retry via single-shot.
    /// </summary>
    private async Task<(string JobId, string DownloadUrl)> UploadMultipartAsync(
        byte[] data, TranscribeOptions options, Action<ProgressEvent>? uploadCb, CancellationToken ct)
    {
        JsonElement created;
        try
        {
            using var doc = await ApiRequestAsync(
                HttpMethod.Post, "/api/v1/upload/multipart/create", BuildUploadPayload(data.Length, options), ct);
            created = doc.RootElement.Clone();
        }
        catch (JobNotFoundException) // route returns 404 when multipart is disabled
        {
            throw new MultipartUnavailableException();
        }

        var jobId = created.GetProperty("job_id").ToString();
        var partSize = created.GetProperty("part_size").GetInt32();
        var completed = new List<Dictionary<string, object?>>();
        var total = data.Length;
        var uploaded = 0;
        try
        {
            foreach (var part in created.GetProperty("parts").EnumerateArray())
            {
                var number = part.GetProperty("part_number").GetInt32();
                var url = part.GetProperty("url").GetString()!;
                var start = (number - 1) * partSize;
                var length = Math.Min(partSize, total - start);
                var etag = await PutPartAsync(url, data, start, length, ct);
                completed.Add(new Dictionary<string, object?> { ["part_number"] = number, ["etag"] = etag });
                uploaded += length;
                uploadCb?.Invoke(new ProgressEvent { Completed = uploaded, Total = total, Step = "upload" });
            }
            using var _ = await ApiRequestAsync(
                HttpMethod.Post, "/api/v1/upload/multipart/complete",
                new { job_id = jobId, parts = completed }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await AbortMultipartAsync(jobId);
            throw;
        }
        catch (Exception)
        {
            // Roll back the partial upload, then fall back to a single-shot PUT.
            await AbortMultipartAsync(jobId);
            throw new MultipartUnavailableException();
        }

        return (jobId, created.GetProperty("download_url").GetString()!);
    }

    /// <summary>
    /// Best-effort discard of an in-progress multipart upload. Runs uncancelled
    /// so a cancelled caller still cleans up.
    /// </summary>
    private async Task AbortMultipartAsync(string jobId)
    {
        try
        {
            using var _ = await ApiRequestAsync(
                HttpMethod.Post, "/api/v1/upload/multipart/abort", new { job_id = jobId }, CancellationToken.None);
        }
        catch { /* best effort */ }
    }

    /// <summary>PUT one part to its presigned URL and return the S3 ETag.</summary>
    private async Task<string> PutPartAsync(string url, byte[] data, int offset, int length, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(data, offset, length),
        };
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode is not (System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.NoContent))
            throw new UploadException($"part upload failed (HTTP {(int)resp.StatusCode})");
        var etag = resp.Headers.ETag?.ToString();
        if (string.IsNullOrEmpty(etag) && resp.Headers.TryGetValues("ETag", out var vals))
            etag = System.Linq.Enumerable.FirstOrDefault(vals);
        if (string.IsNullOrEmpty(etag))
            throw new UploadException("part upload response missing ETag header");
        return etag;
    }

    public async Task<UploadJob> CreateUploadJobAsync(
        int fileSize,
        TranscribeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TranscribeOptions();
        var payload = BuildUploadPayload(fileSize, options);

        using var doc = await ApiRequestAsync(HttpMethod.Post, "/api/v1/upload", payload, cancellationToken);
        var root = doc.RootElement;

        return new UploadJob
        {
            JobId = root.GetProperty("job_id").ToString(),
            UploadUrl = root.GetProperty("upload_url").GetString()!,
            DownloadUrl = root.GetProperty("download_url").GetString()!,
            ContentType = root.TryGetProperty("content_type", out var ct) ? ct.GetString() ?? "application/octet-stream" : "application/octet-stream",
            ExpiresIn = root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 0,
        };
    }

    public async Task TouchUploadProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var _ = await ApiRequestAsync(HttpMethod.Post, "/api/v1/upload/progress", new { job_id = jobId }, cancellationToken);
    }

    public async Task UploadAudioAsync(
        string uploadUrl,
        byte[] data,
        string? jobId = null,
        string contentType = "application/octet-stream",
        Action<ProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeat = null;
        if (!string.IsNullOrEmpty(jobId))
        {
            heartbeat = RunUploadHeartbeatAsync(jobId!, cts.Token);
        }

        Exception? last = null;
        try
        {
            for (var attempt = 1; attempt <= UploadMaxAttempts; attempt++)
            {
                try
                {
                    await PutUploadAsync(uploadUrl, data, contentType, onProgress, cancellationToken);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < UploadMaxAttempts)
                        await Task.Delay(UploadBaseDelay * (1 << (attempt - 1)), cancellationToken);
                }
            }
        }
        finally
        {
            cts.Cancel();
            if (heartbeat is not null)
            {
                try { await heartbeat; } catch { /* ignore */ }
            }
        }

        throw new UploadException($"Upload failed after {UploadMaxAttempts} attempts: {last?.Message}");
    }

    public async Task CompleteUploadAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var _ = await ApiRequestAsync(HttpMethod.Post, "/api/v1/upload/complete", new { job_id = jobId }, cancellationToken);
    }

    // Progress / result

    public async Task<(byte[] Content, string DownloadUrl)> WaitForResultAsync(
        string jobId,
        string downloadUrl,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var sseUrl = await WaitSseAsync(jobId, downloadUrl, onProgress, _timeout, cancellationToken);
        if (sseUrl is null)
        {
            var content = await WaitPollAsync(jobId, downloadUrl, _timeout, cancellationToken);
            return (content, downloadUrl);
        }

        var bytes = await DownloadResultAsync(sseUrl, cancellationToken);
        return (bytes, sseUrl);
    }

    public async Task<byte[]> DownloadResultAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        using var resp = await _http.GetAsync(downloadUrl, cancellationToken);
        var body = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(body);
            throw new ApiException($"Download failed (HTTP {(int)resp.StatusCode})", (int)resp.StatusCode, Truncate(text, 300));
        }
        return body;
    }

    // Job management

    public async Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var _ = await ApiRequestAsync(HttpMethod.Post, "/api/v1/jobs/cancel", new { job_id = jobId }, cancellationToken);
    }

    public async Task<IReadOnlyList<bool>> CheckFailedAsync(IEnumerable<string> jobIds, CancellationToken cancellationToken = default)
    {
        using var doc = await ApiRequestAsync(
            HttpMethod.Post,
            "/api/v1/jobs/check-failed",
            new { job_ids = jobIds.Select(id => id.ToString()).ToArray() },
            cancellationToken);

        var list = new List<bool>();
        if (doc.RootElement.TryGetProperty("failed_jobs", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
                list.Add(el.GetBoolean());
        }
        return list;
    }

    // Retrieval (get by id / list)

    /// <summary>Fetch a job's status (and a fresh download URL once complete).</summary>
    public async Task<JobStatus> GetJobStatusAsync(
        string jobId, CancellationToken cancellationToken = default)
    {
        using var doc = await ApiRequestAsync(
            HttpMethod.Get, $"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", null, cancellationToken);
        var root = doc.RootElement;
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
        return new JobStatus
        {
            JobId = Str(root, "job_id") ?? jobId,
            Status = Str(root, "status") ?? "",
            DownloadUrl = Str(root, "download_url"),
            FailedStage = Str(root, "failed_stage"),
            Reason = Str(root, "reason"),
        };
    }

    /// <summary>
    /// Fetch and parse a completed job's transcript by id. Throws
    /// <see cref="JobFailedException"/> if it failed, or <see cref="ApiException"/>
    /// if it is still processing.
    /// </summary>
    public async Task<TranscriptResult> GetTranscriptAsync(
        string jobId,
        OutputType outputType = OutputType.Json,
        CancellationToken cancellationToken = default)
    {
        var status = await GetJobStatusAsync(jobId, cancellationToken);
        if (status.IsFailed)
            throw new JobFailedException($"Job {jobId} failed", status.FailedStage, status.Reason);
        if (!status.IsCompleted || string.IsNullOrEmpty(status.DownloadUrl))
            throw new ApiException($"Job {jobId} is not complete (status={status.Status})");
        var content = await DownloadResultAsync(status.DownloadUrl, cancellationToken);
        return TranscriptResult.FromContent(jobId, content, outputType.ToApiValue(), status.DownloadUrl);
    }

    /// <summary>List the caller's most-recent jobs (newest first), cursor-paginated.</summary>
    public async Task<JobList> ListJobsAsync(
        int limit = 50, string? before = null, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) limit = 50;
        var path = $"/api/v1/jobs?limit={limit}";
        if (!string.IsNullOrEmpty(before))
            path += $"&before={Uri.EscapeDataString(before)}";
        using var doc = await ApiRequestAsync(HttpMethod.Get, path, null, cancellationToken);
        var root = doc.RootElement;
        var jobs = new List<JobSummary>();
        if (root.TryGetProperty("jobs", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                jobs.Add(new JobSummary
                {
                    JobId = el.TryGetProperty("job_id", out var ji) ? ji.GetString() ?? "" : "",
                    CreatedAt = el.TryGetProperty("created_at", out var ca) ? ca.GetString() ?? "" : "",
                });
            }
        }
        string? nextBefore =
            root.TryGetProperty("next_before", out var nb) && nb.ValueKind != JsonValueKind.Null
                ? nb.GetString()
                : null;
        return new JobList { Jobs = jobs, NextBefore = nextBefore };
    }

    // Internals

    private async Task RunUploadHeartbeatAsync(string jobId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(UploadProgressInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TouchUploadProgressAsync(jobId, ct); }
                catch { /* non-fatal */ }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
    }

    /// <summary>
    /// Sends a JSON request, retrying transient failures and 429/5xx responses
    /// with exponential backoff (honoring Retry-After).
    /// </summary>
    private async Task<JsonDocument> ApiRequestAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body);
        // Job-creating calls retry only when the request provably never landed;
        // anything else would risk a duplicate job and a duplicate charge.
        var creating = CreatesJob(path);

        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
            req.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (json is not null)
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if ((!creating || NeverReachedServer(ex)) && attempt <= _maxRetries)
                {
                    await Task.Delay(RetryDelay(attempt, null), cancellationToken);
                    continue;
                }
                throw new ApiException($"Cannot connect to {_baseUrl}: {ex.Message}");
            }

            using (resp)
            {
                var status = (int)resp.StatusCode;
                // For a create, only 429 is safe to retry: the server refused it
                // outright, so no job exists. A 5xx may have created one first.
                if (Array.IndexOf(RetryStatusCodes, status) >= 0
                    && attempt <= _maxRetries
                    && (!creating || status == 429))
                {
                    await Task.Delay(RetryDelay(attempt, RetryAfterSeconds(resp)), cancellationToken);
                    continue;
                }

                var text = await resp.Content.ReadAsStringAsync(cancellationToken);
                RaiseForStatus(resp, text);
                if (string.IsNullOrWhiteSpace(text))
                    return JsonDocument.Parse("{}");
                return JsonDocument.Parse(text);
            }
        }
    }

    private TimeSpan RetryDelay(int attempt, double? retryAfterSeconds)
    {
        if (retryAfterSeconds is not null)
            return TimeSpan.FromSeconds(Math.Min(retryAfterSeconds.Value, RetryBackoffMax.TotalSeconds));
        var ms = _retryBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(ms, RetryBackoffMax.TotalMilliseconds));
    }

    /// <summary>Retry-After in delta-seconds form; the HTTP-date form is not honored.</summary>
    private static double? RetryAfterSeconds(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Retry-After", out var values)) return null;
        var raw = values.FirstOrDefault();
        return double.TryParse(raw, out var secs) && secs >= 0 ? secs : null;
    }

    private static string? RequestIdOf(HttpResponseMessage resp)
    {
        foreach (var name in RequestIdHeaders)
        {
            if (resp.Headers.TryGetValues(name, out var values))
            {
                var v = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        return null;
    }

    private static void RaiseForStatus(HttpResponseMessage resp, string body)
    {
        var status = (int)resp.StatusCode;
        if (status is 200 or 204) return;

        var truncated = Truncate(body, 300);
        var requestId = RequestIdOf(resp);
        throw status switch
        {
            401 => new AuthenticationException { StatusCode = 401, RequestId = requestId, Body = truncated },
            404 => new JobNotFoundException { StatusCode = 404, RequestId = requestId, Body = truncated },
            429 => new RateLimitException
            {
                StatusCode = 429,
                RequestId = requestId,
                Body = truncated,
                RetryAfter = RetryAfterSeconds(resp),
            },
            _ => new ApiException($"Unexpected response (HTTP {status})", status, truncated) { RequestId = requestId },
        };
    }

    private async Task PutUploadAsync(
        string uploadUrl, byte[] data, string contentType, Action<ProgressEvent>? onProgress, CancellationToken ct)
    {
        // Adapt the ProgressEvent callback to a (sent, total) byte callback.
        // Upload events share the ProgressEvent shape with Step == "upload".
        Action<int, int>? byteCb = onProgress is null
            ? null
            : (sent, total) => onProgress!(new ProgressEvent { Completed = sent, Total = total, Step = "upload" });

        // A fresh content per call so a retry restarts progress from 0.
        using var content = new ProgressByteArrayContent(data, contentType, byteCb);
        HttpResponseMessage resp = await _http.PutAsync(uploadUrl, content, ct);

        using (resp)
        {
            if ((int)resp.StatusCode is not (200 or 204))
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                throw new UploadException($"HTTP {(int)resp.StatusCode}: {Truncate(text, 200)}");
            }
        }
    }

    private async Task<string?> WaitSseAsync(
        string jobId,
        string fallbackDownloadUrl,
        Action<ProgressEvent>? onProgress,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        string? lastEventId = null;
        var reconnects = 0;
        var refusals = 0;

        while (true)
        {
            if (DateTime.UtcNow - start >= timeout)
                throw new JobTimeoutException($"Timed out after {timeout.TotalSeconds}s waiting for job {jobId}");
            if (reconnects > SseMaxReconnects)
                return null;
            if (reconnects > 0)
                await Task.Delay(SseReconnectDelay, ct);

            var (outcome, downloadUrl, newId) = await SseAttemptAsync(
                jobId, start, timeout, lastEventId, fallbackDownloadUrl, onProgress, ct);
            lastEventId = newId ?? lastEventId;

            switch (outcome)
            {
                case "done":
                    return downloadUrl ?? fallbackDownloadUrl;
                case "timeout":
                    throw new JobTimeoutException($"Timed out after {timeout.TotalSeconds}s waiting for job {jobId}");
                case "refused":
                    refusals++;
                    // The endpoint will not stream. Fall back to polling now
                    // rather than burning the full reconnect ladder.
                    if (refusals >= SseMaxStatusRefusals) return null;
                    reconnects++;
                    break;
                case "reconnect":
                    reconnects++;
                    break;
            }
        }
    }

    private async Task<(string Outcome, string? DownloadUrl, string? LastEventId)> SseAttemptAsync(
        string jobId,
        DateTime start,
        TimeSpan timeout,
        string? lastEventId,
        string fallbackDownloadUrl,
        Action<ProgressEvent>? onProgress,
        CancellationToken ct)
    {
        if (DateTime.UtcNow - start >= timeout)
            return ("timeout", null, lastEventId);

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v1/jobs/{jobId}/stream");
        req.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is not null)
            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ("reconnect", null, lastEventId);
        }

        using (resp)
        {
            if ((int)resp.StatusCode == 401) throw new AuthenticationException();
            if ((int)resp.StatusCode == 429) throw new RateLimitException();
            // A status, not a dropped connection: the endpoint answered and said
            // no. Budgeted separately — see SseMaxStatusRefusals.
            if ((int)resp.StatusCode != 200) return ("refused", null, lastEventId);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var current = new Dictionary<string, string>();
            var dataLines = new List<string>();
            while (true)
            {
                if (DateTime.UtcNow - start >= timeout)
                    return ("timeout", null, lastEventId);

                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { return ("reconnect", null, lastEventId); }

                if (line is null)
                    return ("reconnect", null, lastEventId);

                if (line.Length == 0)
                {
                    if (dataLines.Count == 0)
                    {
                        current.Clear();
                        continue;
                    }
                    var dataRaw = string.Join("\n", dataLines);
                    dataLines.Clear();

                    var eventType = current.GetValueOrDefault("event", "message");
                    if (current.TryGetValue("id", out var id))
                        lastEventId = id;

                    Dictionary<string, JsonElement>? data = null;
                    try
                    {
                        using var parsed = JsonDocument.Parse(dataRaw);
                        data = parsed.RootElement.EnumerateObject()
                            .ToDictionary(p => p.Name, p => p.Value.Clone());
                    }
                    catch
                    {
                    }

                    var elapsed = (DateTime.UtcNow - start).TotalSeconds;
                    if (eventType == "progress")
                    {
                        onProgress?.Invoke(new ProgressEvent
                        {
                            Completed = GetInt(data, "completed"),
                            Total = GetInt(data, "total"),
                            Step = GetString(data, "step"),
                            ElapsedSeconds = elapsed,
                        });
                    }
                    else if (eventType == "completed")
                    {
                        var dl = GetString(data, "download_url") ?? fallbackDownloadUrl;
                        return ("done", dl, lastEventId);
                    }
                    else if (eventType == "failed")
                    {
                        var step = GetString(data, "step") ?? "unknown";
                        var reason = GetString(data, "reason") ?? "unknown";
                        throw new JobFailedException($"Job failed at step={step}: {reason}", step, reason);
                    }

                    current.Clear();
                    continue;
                }

                if (line.StartsWith(':'))
                    continue;

                var colon = line.IndexOf(':');
                string field, value;
                if (colon < 0)
                {
                    field = line;
                    value = "";
                }
                else
                {
                    field = line[..colon];
                    value = line[(colon + 1)..];
                    if (value.StartsWith(' ')) value = value[1..];
                }

                if (field == "data") dataLines.Add(value);
                else current[field] = value;
            }
        }
    }

    private async Task<byte[]> WaitPollAsync(string jobId, string downloadUrl, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var maxAttempts = Math.Max(1, (int)(timeout / PollInterval));

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (DateTime.UtcNow - start >= timeout) break;

            try
            {
                var failed = await CheckFailedAsync(new[] { jobId }, ct);
                if (failed.Count > 0 && failed[0])
                    throw new JobFailedException($"Job {jobId} has failed");
            }
            catch (AuthenticationException) { throw; }
            catch (JobFailedException) { throw; }
            catch (SttException) { /* ignore transient */ }

            try
            {
                using var resp = await _http.GetAsync(downloadUrl, ct);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* ignore probe errors */ }

            if (attempt < maxAttempts)
                await Task.Delay(PollInterval, ct);
        }

        throw new JobTimeoutException($"Job {jobId} did not complete within {timeout.TotalSeconds}s");
    }

    private static int? GetInt(Dictionary<string, JsonElement>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => int.TryParse(el.GetString(), out var i) ? i : null,
            _ => null,
        };
    }

    private static string? GetString(Dictionary<string, JsonElement>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
