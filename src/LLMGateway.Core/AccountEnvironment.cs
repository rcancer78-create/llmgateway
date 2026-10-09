using System.Text.RegularExpressions;

namespace LLMGateway.Core;

/// <summary>
/// Profile environment values are settings, never credentials.
/// API keys are passed by variable name (<see cref="AccountProfile.ApiKeyVariable"/>) and read from the host process.
/// </summary>
public static partial class AccountEnvironment
{
    /// <summary>Provider credentials a native CLI reads from the inherited process environment.</summary>
    public static readonly string[] ProviderCredentialVariables =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "GOOGLE_API_KEY",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "AZURE_OPENAI_API_KEY",
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "CODEX_API_KEY",
        "OPENAI_API_KEY",
        "GEMINI_API_KEY",
        "CURSOR_API_KEY",
        "GROK_API_KEY",
        "XAI_API_KEY"
    ];

    /// <summary>
    /// Nulls known provider credentials and configured key-variable names so a NativeLogin child
    /// cannot pick up a host key or another profile's variable. <paramref name="keepVariable"/> stays.
    /// </summary>
    public static void RemoveForeignCredentials(IDictionary<string, string?> environment, string? keepVariable, IEnumerable<string>? configuredNames)
    {
        foreach (var name in ProviderCredentialVariables.Concat(configuredNames ?? []))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (keepVariable is not null && name.Equals(keepVariable, StringComparison.OrdinalIgnoreCase)) continue;
            environment[name] = null;
        }
    }

    public static bool IsSecret(string name, string? value)
    {
        // These documented Claude settings are token counts, not authentication values.
        // Exempt only their names; the value checks below still refuse credentials.
        var publicTokenBudget = name.Equals("MAX_THINKING_TOKENS", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CLAUDE_CODE_MAX_OUTPUT_TOKENS", StringComparison.OrdinalIgnoreCase)
            || name.Equals("MAX_MCP_OUTPUT_TOKENS", StringComparison.OrdinalIgnoreCase);
        if (!publicTokenBudget && (SecretName().IsMatch(name) || ConcatenatedSecretName().IsMatch(name))) return true;
        if (string.IsNullOrEmpty(value)) return false;
        var candidate = value.Trim();
        return candidate.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
            || PrivateKeyValue().IsMatch(candidate)
            || JwtValue().IsMatch(candidate)
            || candidate.StartsWith("sk-", StringComparison.Ordinal)
            || candidate.StartsWith("ghp_", StringComparison.Ordinal)
            || candidate.StartsWith("gho_", StringComparison.Ordinal)
            || candidate.StartsWith("ghu_", StringComparison.Ordinal)
            || candidate.StartsWith("ghs_", StringComparison.Ordinal)
            || candidate.StartsWith("ghr_", StringComparison.Ordinal)
            || candidate.StartsWith("github_pat_", StringComparison.Ordinal)
            || candidate.StartsWith("xai-", StringComparison.Ordinal)
            || candidate.StartsWith("AIza", StringComparison.Ordinal)
            || (Uri.TryCreate(candidate.TrimEnd(), UriKind.Absolute, out var uri)
                && !string.IsNullOrEmpty(uri.UserInfo));
    }

    public static bool RemoveSecrets(AccountProfile profile)
    {
        var secret = profile.Environment.Keys.Where(key => IsSecret(key, profile.Environment[key])).ToArray();
        foreach (var key in secret) profile.Environment.Remove(key);
        return secret.Length > 0;
    }

    public static Dictionary<string, string> WithoutSecrets(IReadOnlyDictionary<string, string> environment)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment)
            if (!IsSecret(key, value)) copy[key] = value;
        return copy;
    }

    [GeneratedRegex(@"(?i)(^|[^A-Za-z0-9])(api[-_]?key|access[-_]?key|secret|password|passwd|credentials?|tokens?|private[-_]?key)([^A-Za-z0-9]|$)")]
    private static partial Regex SecretName();

    // Recognize concrete credential suffixes without treating tokenBudget/maxTokens as secrets.
    [GeneratedRegex(@"(?i)(api[-_]?key|access[-_]?key|access[-_]?token|oauth[-_]?token|auth[-_]?token|refresh[-_]?token|client[-_]?secret|private[-_]?key|password|passwd)$|^(github|gh|gitlab|gl|openai|anthropic|claude(code)?|gemini|google|grok|xai|cursor|aws|azure|huggingface|hf)token$")]
    private static partial Regex ConcatenatedSecretName();

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$")]
    private static partial Regex JwtValue();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9]+[ -])*PRIVATE KEY(?: BLOCK)?-----")]
    private static partial Regex PrivateKeyValue();
}
