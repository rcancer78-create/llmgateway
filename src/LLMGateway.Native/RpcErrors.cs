namespace LLMGateway.Native;

internal static class RpcErrors
{
    /// <summary>The client build does not expose this JSON-RPC method (removed, renamed, or still experimental).</summary>
    public static bool IsMissingMethod(Exception exception)
    {
        if (exception is LLMGateway.Core.GatewayException { JsonRpcErrorCode: { } code }) return code == -32601;
        var message = exception.Message;
        return System.Text.RegularExpressions.Regex.IsMatch(message,
                @"\bmethod(?:\s+['""][^'""\r\n]+['""])?\s+not found\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                | System.Text.RegularExpressions.RegexOptions.NonBacktracking)
            || message.Contains("unknown method", StringComparison.OrdinalIgnoreCase)
            || message.Contains("requires experimentalApi", StringComparison.OrdinalIgnoreCase)
            || message.Contains("-32601", StringComparison.Ordinal);
    }
}
