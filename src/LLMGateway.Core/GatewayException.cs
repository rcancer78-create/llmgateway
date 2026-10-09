namespace LLMGateway.Core;

public enum GatewayErrorKind
{
    InvalidRequest,
    NotFound,
    ModelNotFound,
    Unauthorized,
    AuthenticationRequired,
    RateLimited,
    ProviderUnavailable,
    Unsupported,
    Timeout,
    Upstream
}

public sealed class GatewayException : Exception
{
    public GatewayException(GatewayErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public GatewayErrorKind Kind { get; }
    /// <summary>Original JSON-RPC error code when supplied by a native protocol response.</summary>
    public int? JsonRpcErrorCode { get; init; }

    /// <summary>Provider supplied reset moment when the error is a rate limit.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    public static GatewayException Invalid(string message) => new(GatewayErrorKind.InvalidRequest, message);
}
