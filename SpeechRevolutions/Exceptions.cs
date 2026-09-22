namespace SpeechRevolutions;

public class SpeechRevolutionsException : Exception
{
    /// <summary>HTTP status that produced the error, when there was one.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Correlates the failure with server-side logs.</summary>
    public string? RequestId { get; init; }

    /// <summary>Truncated response body, when there was one.</summary>
    public string? Body { get; init; }

    public SpeechRevolutionsException(string message) : base(message) { }
    public SpeechRevolutionsException(string message, Exception inner) : base(message, inner) { }
}

public class AuthenticationException : SpeechRevolutionsException
{
    public AuthenticationException(string message = "Unauthorized — check your API key") : base(message) { }
}

public class RateLimitException : SpeechRevolutionsException
{
    /// <summary>The server's Retry-After hint in seconds, when it sent one.</summary>
    public double? RetryAfter { get; init; }

    public RateLimitException(string message = "Rate limit exceeded — try again shortly") : base(message) { }
}

public class JobNotFoundException : SpeechRevolutionsException
{
    public JobNotFoundException(string message = "Job not found or upload session expired") : base(message) { }
}

public class JobFailedException : SpeechRevolutionsException
{
    public string? Step { get; }
    public string? Reason { get; }

    public JobFailedException(string message, string? step = null, string? reason = null) : base(message)
    {
        Step = step;
        Reason = reason;
    }
}

public class UploadException : SpeechRevolutionsException
{
    public UploadException(string message) : base(message) { }
}

public class JobTimeoutException : SpeechRevolutionsException
{
    public JobTimeoutException(string message) : base(message) { }
}

public class ApiException : SpeechRevolutionsException
{
    public ApiException(string message, int? statusCode = null, string? body = null) : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }
}
