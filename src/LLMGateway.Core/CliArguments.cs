namespace LLMGateway.Core;

/// <summary>
/// Parses profile CLI arguments and refuses flags that turn off a client's own permission checks.
/// Adapters already choose a read-only or plan/ask mode; a later extra argument must not undo that.
/// </summary>
public static class CliArguments
{
    private static readonly HashSet<string> Banned = new(StringComparer.OrdinalIgnoreCase)
    {
        "--dangerously-skip-permissions",
        "--dangerously-bypass-approvals-and-sandbox",
        "--dangerously-bypass-hook-trust",
        "--yolo",
        "--force",
        "--full-auto",
        "--approve-mcps", "--trust",
        "-f", "--config", "-c", "--profile", "-p", "--settings", "--settings-json",
        "--allowedTools", "--allowed-tools", "--allow", "--add-dir",
        "--mcp-config", "--plugin-dir", "--append-system-prompt-file", "--tools", "--strict-mcp-config"
    };

    private static readonly HashSet<string> CredentialOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--api-key", "--apiKey", "-k", "--token", "--auth-token", "--access-token",
        "--refresh-token", "--authorization", "--password", "--client-secret", "--secret"
    };

    public static IReadOnlyList<string> Parse(string text)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                var end = i;
                while (end < text.Length && text[end] == '\\') end++;
                var count = end - i;
                if (end < text.Length && text[end] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1) current.Append('"');
                    else quoted = !quoted;
                    i = end;
                }
                else
                {
                    current.Append('\\', count);
                    i = end - 1;
                }
                started = true;
            }
            else if (c == '"') { quoted = !quoted; started = true; }
            else if (!quoted && char.IsWhiteSpace(c))
            {
                if (!started) continue;
                args.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else { current.Append(c); started = true; }
        }
        if (quoted) throw GatewayException.Invalid("В дополнительных аргументах не закрыта кавычка.");
        if (started) args.Add(current.ToString());
        return args;
    }

    public static string Format(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0) return string.Empty;
        return string.Join(' ', args.Select(Quote));
    }

    public static void RejectUnsafe(IReadOnlyList<string>? args)
    {
        if (args is null) return;
        for (var i = 0; i < args.Count; i++)
        {
            var (name, inline) = Split(args[i]);
            if (CredentialOptions.Contains(name)
                || AccountEnvironment.IsSecret(string.Empty, args[i])
                || inline is not null && AccountEnvironment.IsSecret(string.Empty, inline))
                throw GatewayException.Invalid("Секреты в дополнительных CLI-аргументах не принимаются; используйте имя переменной окружения для профиля.");
            if (Banned.Contains(name) || name.StartsWith("--dangerously-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("--allow-dangerously-", StringComparison.OrdinalIgnoreCase))
                throw GatewayException.Invalid($"Аргумент '{name}' отключает проверки клиента и не принимается.");
            if (!TakesPolicyValue(name)) continue;
            var value = inline;
            if (value is null && i + 1 < args.Count && !args[i + 1].StartsWith('-')) value = args[++i];
            if (value is null || !IsSafePolicyValue(name, value))
                throw GatewayException.Invalid($"Аргумент '{name}' не задаёт подтверждённый безопасный режим клиента.");
        }
    }

    private static bool TakesPolicyValue(string name) =>
        name.Equals("--sandbox", StringComparison.OrdinalIgnoreCase)
        || name.Equals("-s", StringComparison.OrdinalIgnoreCase)
        || name.Equals("--mode", StringComparison.OrdinalIgnoreCase)
        || name.Equals("--permission-mode", StringComparison.OrdinalIgnoreCase)
        || name.Equals("--approval-mode", StringComparison.OrdinalIgnoreCase)
        || name.Equals("--ask-for-approval", StringComparison.OrdinalIgnoreCase)
        || name.Equals("-a", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafePolicyValue(string name, string value) => name.ToLowerInvariant() switch
    {
        "--sandbox" or "-s" => value.Equals("read-only", StringComparison.OrdinalIgnoreCase),
        "--mode" => value.Equals("ask", StringComparison.OrdinalIgnoreCase) || value.Equals("plan", StringComparison.OrdinalIgnoreCase),
        "--permission-mode" => value.Equals("plan", StringComparison.OrdinalIgnoreCase),
        _ => value.Equals("on-request", StringComparison.OrdinalIgnoreCase) || value.Equals("untrusted", StringComparison.OrdinalIgnoreCase)
    };

    private static (string Name, string? Inline) Split(string token)
    {
        // CLI parsers accept both -s=value and compact -svalue/-cvalue forms.
        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            if (token.Length > 2 && token[0] == '-' && "scapfk".Contains(char.ToLowerInvariant(token[1])))
                return (token[..2], token[2] == '=' ? token[3..] : token[2..]);
            return (token, null);
        }
        var eq = token.IndexOf('=');
        return eq > 0 ? (token[..eq], token[(eq + 1)..]) : (token, null);
    }

    private static string Quote(string arg)
    {
        if (arg.Length != 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"')) return arg;
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        result.Append('"');
        return result.ToString();
    }
}
