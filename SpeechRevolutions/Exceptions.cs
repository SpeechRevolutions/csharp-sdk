namespace SpeechRevolutions;

public class SttException : Exception
{
    public SttException(string message) : base(message) { }
    public SttException(string message, Exception inner) : base(message, inner) { }
}

public class AuthenticationException : SttException
{
    public AuthenticationException(string message = "Unauthorized — check your API key") : base(message) { }
}

public class RateLimitException : SttException
{
    public RateLimitException(string message = "Rate limit exceeded — try again shortly") : base(message) { }
}

public class JobNotFoundException : SttException
{
    public JobNotFoundException(string message = "Job not found or upload session expired") : base(message) { }
}

public class JobFailedException : SttException
{
    public string? Step { get; }
    public string? Reason { get; }

    public JobFailedException(string message, string? step = null, string? reason = null) : base(message)
    {
        Step = step;
        Reason = reason;
    }
}

public class UploadException : SttException
{
    public UploadException(string message) : base(message) { }
}

public class JobTimeoutException : SttException
{
    public JobTimeoutException(string message) : base(message) { }
}

public class ApiException : SttException
{
    public int? StatusCode { get; }
    public string? Body { get; }

    public ApiException(string message, int? statusCode = null, string? body = null) : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }
}
