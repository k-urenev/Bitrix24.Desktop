namespace Bitrix24.Desktop.Core.Rest;

public sealed class BitrixException : Exception
{
    public BitrixException(string message, string? errorCode, bool isRateLimit, bool isAuthFailure, TimeSpan? retryAfter, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        IsRateLimit = isRateLimit;
        IsAuthFailure = isAuthFailure;
        IsAccessDenied = string.Equals(errorCode, "insufficient_scope", StringComparison.Ordinal);
        RetryAfter = retryAfter;
    }

    public string? ErrorCode { get; }
    public bool IsRateLimit { get; }
    public bool IsAuthFailure { get; }
    public bool IsAccessDenied { get; }
    public TimeSpan? RetryAfter { get; }
}
