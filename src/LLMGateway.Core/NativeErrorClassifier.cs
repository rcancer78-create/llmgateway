using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMGateway.Core;

public static partial class NativeErrorClassifier
{
    public static GatewayErrorKind Classify(string? message) => Classify(null, message);

    /// <summary>
    /// Prefers an HTTP status or error code on the structured object. The message is only a fallback,
    /// and incidental phrases such as "retrying after 429" are not treated as an account limit.
    /// </summary>
    public static GatewayErrorKind Classify(JsonElement? error, string? message)
    {
        if (error is { ValueKind: JsonValueKind.Object } obj)
        {
            int? status = ReadStatus(obj);
            var code = FirstCode(obj);
            if (status is 429 || IsRateCode(code)) return GatewayErrorKind.RateLimited;
            if (status is 401 or 403 || IsAuthCode(code)) return GatewayErrorKind.AuthenticationRequired;
            if (status is not null) return GatewayErrorKind.Upstream;
        }
        if (string.IsNullOrWhiteSpace(message)) return GatewayErrorKind.Upstream;
        if (RateLimitPattern().IsMatch(message)) return GatewayErrorKind.RateLimited;
        if (AuthPattern().IsMatch(message)) return GatewayErrorKind.AuthenticationRequired;
        return GatewayErrorKind.Upstream;
    }

    private static int? ReadStatus(JsonElement obj)
    {
        foreach (var name in new[] { "status", "status_code", "http_status" })
        {
            if (obj.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value)) return value;
        }
        if (obj.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var numeric))
            return numeric;
        return null;
    }

    private static string? FirstCode(JsonElement obj)
    {
        foreach (var name in new[] { "code", "type", "error_type", "reason", "status", "status_code", "http_status" })
        {
            if (obj.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String)
                return node.GetString();
        }
        return null;
    }

    private static bool IsRateCode(string? code) => code is not null && RateCodePattern().IsMatch(code);

    private static bool IsAuthCode(string? code) => code is not null && AuthCodePattern().IsMatch(code);

    /// <summary>Best-effort reset moment from texts like "try again in 2h 5m" or "resets at 2026-10-03T10:00:00Z".</summary>
    public static DateTimeOffset? ParseRetryAt(string? message, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var absolute = AbsolutePattern().Match(message);
        if (absolute.Success && DateTimeOffset.TryParse(absolute.Groups["date"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date;
        var relative = RelativePattern().Match(message);
        if (!relative.Success) return null;
        try
        {
            var span = TimeSpan.Zero;
            foreach (Match part in DurationPartPattern().Matches(relative.Groups["span"].Value))
            {
                if (!double.TryParse(part.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || !double.IsFinite(value)) return null;
                span += part.Groups["u"].Value.ToLowerInvariant()[0] switch
                {
                    'd' => TimeSpan.FromDays(value),
                    'h' => TimeSpan.FromHours(value),
                    's' => TimeSpan.FromSeconds(value),
                    _ => TimeSpan.FromMinutes(value)
                };
            }
            return span > TimeSpan.Zero ? now + span : null;
        }
        // Native error text can contain durations outside TimeSpan or timestamp ranges.
        // This best-effort hint must not replace the original provider failure with a parser error.
        catch (OverflowException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static string Trim(string? message, int max = 800)
    {
        var text = (message ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    [GeneratedRegex(@"rate.?limit|usage limit|too many requests|limit reached|out of credits|resource.?exhausted", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitPattern();

    [GeneratedRegex(@"^(rate[-_ ]?limit|too[-_ ]many[-_ ]requests|resource[-_ ]exhausted|usage[-_ ]limit|quota[-_ ]exceeded|insufficient[-_ ]quota)", RegexOptions.IgnoreCase)]
    private static partial Regex RateCodePattern();

    [GeneratedRegex(@"^(unauthori[sz]ed|unauthenticated|invalid[-_ ]api[-_ ]key|authentication[-_ ](failed|required)|missing[-_ ]api[-_ ]key)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthCodePattern();

    [GeneratedRegex(@"not (logged|signed) in|log ?in required|please (log|sign) ?in|unauthori[sz]ed|unauthenticated|authentication (failed|required)|\b401\b|invalid api key|missing api key|no credentials|run [`'""]?\S+ login", RegexOptions.IgnoreCase)]
    private static partial Regex AuthPattern();

    [GeneratedRegex(@"(?:reset|available|try again)\w*\s+(?:at|on)\s+(?<date>\d{4}-\d{2}-\d{2}[T ][\d:.]+(?:Z|[+-]\d{2}:?\d{2})?)", RegexOptions.IgnoreCase)]
    private static partial Regex AbsolutePattern();

    [GeneratedRegex(@"(?:try again|retry|resets?|available)\s+in\s+(?<span>(?:\d+(?:\.\d+)?\s*(?:days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b[\s,]*(?:and\s+)?)+)", RegexOptions.IgnoreCase)]
    private static partial Regex RelativePattern();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<u>days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPartPattern();
}
