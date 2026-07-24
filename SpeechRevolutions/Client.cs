using System.Net.Http.Headers;
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
    private static readonly TimeSpan SseReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public SttClient(string? apiKey = null, string? baseUrl = null, TimeSpan? timeout = null, HttpClient? httpClient = null)
    {
        apiKey ??= Environment.GetEnvironmentVariable("SPEECHREVOLUTIONS_API_KEY")
                   ?? Environment.GetEnvironmentVariable("STT_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AuthenticationException(
                "apiKey is required (pass apiKey or set SPEECHREVOLUTIONS_API_KEY / STT_API_KEY)");

        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _timeout = timeout ?? TimeSpan.FromSeconds(600);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
    }

    // High-level

    public async Task<TranscriptResult> TranscribeAsync(
        string audioPath,
        TranscribeOptions? options = null,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (audioPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            audioPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var resp = await _http.GetAsync(audioPath, cancellationToken);
            var data = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
                throw new ApiException($"Failed to download audio URL (HTTP {(int)resp.StatusCode})", (int)resp.StatusCode);
            return await TranscribeAsync(data, options, onProgress, cancellationToken);
        }

        var fileData = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        return await TranscribeAsync(fileData, options, onProgress, cancellationToken);
    }

    public Task<TranscriptResult> TranscribeUrlAsync(
        string url,
        TranscribeOptions? options = null,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
        => TranscribeAsync(url, options, onProgress, cancellationToken);

    public async Task<TranscriptResult> TranscribeAsync(
        byte[] audio,
        TranscribeOptions? options = null,
        Action<ProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (audio is null || audio.Length == 0)
            throw new ArgumentException("Audio is empty", nameof(audio));

        options ??= new TranscribeOptions();
        var outputType = options.OutputType.ToApiValue();

        var job = await CreateUploadJobAsync(audio.Length, options, cancellationToken);

        // Upload progress: a byte "Uploading" bar (when Progress is on) and/or
        // the OnUploadProgress callback. Both compose.
        var uploadPrinter = options.Progress
            ? new ProgressPrinter(options.OnUploadProgress, "Uploading", bytesMode: true)
            : null;
        Action<ProgressEvent>? uploadCb = uploadPrinter is not null
            ? uploadPrinter.Report
            : options.OnUploadProgress;
        try
        {
            await UploadAudioAsync(job.UploadUrl, audio, job.JobId, onProgress: uploadCb, cancellationToken: cancellationToken);
        }
        finally
        {
            uploadPrinter?.Close();
        }

        await CompleteUploadAsync(job.JobId, cancellationToken);

        // Transcription progress: a "Transcribing" bar (when Progress is on)
        // and/or the onProgress callback.
        var printer = options.Progress
            ? new ProgressPrinter(onProgress, "Transcribing", bytesMode: false)
            : null;
        Action<ProgressEvent>? progressCb = printer is not null ? printer.Report : onProgress;
        byte[] content;
        string downloadUrl;
        try
        {
            (content, downloadUrl) = await WaitForResultAsync(job.JobId, job.DownloadUrl, progressCb, cancellationToken);
        }
        finally
        {
            printer?.Close();
        }

        return TranscriptResult.FromContent(job.JobId, content, outputType, downloadUrl);
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
        if (audioPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            audioPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var resp = await _http.GetAsync(audioPath, cancellationToken);
            var data = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
                throw new ApiException($"Failed to download audio URL (HTTP {(int)resp.StatusCode})", (int)resp.StatusCode);
            return await SubmitAsync(data, options, cancellationToken);
        }

        var fileData = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        return await SubmitAsync(fileData, options, cancellationToken);
    }

    /// <summary>Submit raw audio bytes without waiting; returns the job id.</summary>
    public async Task<string> SubmitAsync(
        byte[] audio,
        TranscribeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (audio is null || audio.Length == 0)
            throw new ArgumentException("Audio is empty", nameof(audio));

        options ??= new TranscribeOptions();
        var job = await CreateUploadJobAsync(audio.Length, options, cancellationToken);

        var uploadPrinter = options.Progress
            ? new ProgressPrinter(options.OnUploadProgress, "Uploading", bytesMode: true)
            : null;
        Action<ProgressEvent>? uploadCb = uploadPrinter is not null
            ? uploadPrinter.Report
            : options.OnUploadProgress;
        try
        {
            await UploadAudioAsync(job.UploadUrl, audio, job.JobId, onProgress: uploadCb, cancellationToken: cancellationToken);
        }
        finally
        {
            uploadPrinter?.Close();
        }

        await CompleteUploadAsync(job.JobId, cancellationToken);
        return job.JobId;
    }

    // Upload flow

    public async Task<UploadJob> CreateUploadJobAsync(
        int fileSize,
        TranscribeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TranscribeOptions();
        var payload = new Dictionary<string, object?>
        {
            ["file_size"] = fileSize,
            ["output_type"] = options.OutputType.ToApiValue(),
            ["word_timestamps"] = options.WordTimestamps,
            ["speaker_labels"] = options.EffectiveSpeakerLabels,
            ["nltk"] = options.Nltk,
            ["tier"] = options.Tier.ToApiValue(),
        };
        if (options.CustomVocabulary is { Count: > 0 })
            payload["custom_vocabulary"] = options.CustomVocabulary;
        if (!string.IsNullOrEmpty(options.CallbackUrl))
            payload["callback_url"] = options.CallbackUrl;

        using var doc = await ApiRequestAsync(HttpMethod.Post, "/api/v1/upload", payload, cancellationToken);
        var root = doc.RootElement;

        object uploadUrl;
        if (root.GetProperty("upload_url").ValueKind == JsonValueKind.String)
        {
            uploadUrl = root.GetProperty("upload_url").GetString()!;
        }
        else
        {
            var u = root.GetProperty("upload_url");
            var fields = new Dictionary<string, string>();
            if (u.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in fieldsEl.EnumerateObject())
                    fields[p.Name] = p.Value.ToString();
            }
            uploadUrl = new PresignedPost
            {
                Url = u.GetProperty("url").GetString()!,
                Fields = fields,
            };
        }

        return new UploadJob
        {
            JobId = root.GetProperty("job_id").ToString(),
            UploadUrl = uploadUrl,
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
        object uploadUrl,
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
                    await PutOrPostUploadAsync(uploadUrl, data, contentType, onProgress, cancellationToken);
                    return;
                }
                catch (Exception ex) when (attempt < UploadMaxAttempts)
                {
                    last = ex;
                    await Task.Delay(UploadBaseDelay * (1 << (attempt - 1)), cancellationToken);
                }
                catch (Exception ex)
                {
                    last = ex;
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

    private async Task<JsonDocument> ApiRequestAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        req.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new ApiException($"Cannot connect to {_baseUrl}: {ex.Message}");
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(cancellationToken);
            RaiseForStatus((int)resp.StatusCode, text);
            if (string.IsNullOrWhiteSpace(text))
                return JsonDocument.Parse("{}");
            return JsonDocument.Parse(text);
        }
    }

    private static void RaiseForStatus(int status, string body)
    {
        if (status is 200 or 204) return;
        var truncated = Truncate(body, 300);
        throw status switch
        {
            401 => new AuthenticationException(),
            404 => new JobNotFoundException(),
            429 => new RateLimitException(),
            _ => new ApiException($"Unexpected response (HTTP {status})", status, truncated),
        };
    }

    private async Task PutOrPostUploadAsync(
        object uploadUrl, byte[] data, string contentType, Action<ProgressEvent>? onProgress, CancellationToken ct)
    {
        // Adapt the ProgressEvent callback to a (sent, total) byte callback.
        // Upload events share the ProgressEvent shape with Step == "upload".
        Action<int, int>? byteCb = onProgress is null
            ? null
            : (sent, total) => onProgress!(new ProgressEvent { Completed = sent, Total = total, Step = "upload" });

        HttpResponseMessage resp;
        if (uploadUrl is PresignedPost post)
        {
            using var form = new MultipartFormDataContent();
            if (post.Fields is not null)
            {
                foreach (var (k, v) in post.Fields)
                    form.Add(new StringContent(v), k);
            }
            // A fresh content per call so a retry restarts progress from 0.
            var fileContent = new ProgressByteArrayContent(data, contentType, byteCb);
            form.Add(fileContent, "file", "audio");
            resp = await _http.PostAsync(post.Url, form, ct);
        }
        else if (uploadUrl is string url)
        {
            using var content = new ProgressByteArrayContent(data, contentType, byteCb);
            resp = await _http.PutAsync(url, content, ct);
        }
        else
        {
            throw new UploadException($"Unsupported upload_url type: {uploadUrl.GetType().Name}");
        }

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
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is not null)
            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch
        {
            return ("reconnect", null, lastEventId);
        }

        using (resp)
        {
            if ((int)resp.StatusCode == 401) throw new AuthenticationException();
            if ((int)resp.StatusCode == 429) throw new RateLimitException();
            if ((int)resp.StatusCode != 200) return ("reconnect", null, lastEventId);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var current = new Dictionary<string, string>();
            while (true)
            {
                if (DateTime.UtcNow - start >= timeout)
                    return ("timeout", null, lastEventId);

                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch { return ("reconnect", null, lastEventId); }

                if (line is null)
                    return ("reconnect", null, lastEventId);

                if (line.Length == 0)
                {
                    if (!current.TryGetValue("data", out var dataRaw))
                    {
                        current.Clear();
                        continue;
                    }

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
                current[field] = value;
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
