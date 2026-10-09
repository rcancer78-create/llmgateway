namespace LLMGateway.Core;

/// <summary>
/// Applies <c>max_tokens</c> / <c>max_completion_tokens</c> after the native client has produced text.
/// Clients have no hard stop parameter, so the gateway keeps about four characters per token — the same
/// ratio as <see cref="TokenUsage.Estimate"/> — and reports <c>finish_reason=length</c>.
/// </summary>
public static class OutputBudget
{
    public const int CharsPerToken = 4;

    public static string Fit(string text, int alreadyChars, int? maxTokens, out bool exhausted)
    {
        if (maxTokens is not > 0)
        {
            exhausted = false;
            return text;
        }
        var allowed = (int)Math.Clamp((long)maxTokens.Value * CharsPerToken - alreadyChars, 0, int.MaxValue);
        if (text.Length <= allowed)
        {
            exhausted = false;
            return text;
        }
        exhausted = true;
        if (allowed > 0 && char.IsHighSurrogate(text[allowed - 1]) && char.IsLowSurrogate(text[allowed])) allowed--;
        return text[..allowed];
    }
}
