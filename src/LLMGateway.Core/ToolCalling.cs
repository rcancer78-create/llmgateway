using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMGateway.Core;

/// <summary>
/// Prompt-level emulation of OpenAI function calling. Native agent CLIs do not accept caller-defined
/// tools, so the model is asked to answer with a tagged JSON block that is converted to <c>tool_calls</c>.
/// </summary>
public static class ToolCalling
{
    public const string OpenTag = "<tool_calls>";
    public const string CloseTag = "</tool_calls>";

    public static bool IsEnabled(ChatRequest request) =>
        request.Tools is { Count: > 0 } && (request.ToolChoiceIsFunction || !string.Equals(request.ToolChoice, "none", StringComparison.OrdinalIgnoreCase));

    public static string? BuildInstructions(ChatRequest request)
    {
        var requested = request.ToolChoice;
        if (request.ToolChoiceIsFunction && (string.IsNullOrWhiteSpace(requested)
            || request.Tools is not { Count: > 0 }
            || !request.Tools.Any(tool => tool.Name.Equals(requested, StringComparison.Ordinal))))
            throw GatewayException.Invalid("tool_choice требует объявленную функцию в tools.");
        if (!string.IsNullOrWhiteSpace(requested)
            && !string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requested, "none", StringComparison.OrdinalIgnoreCase)
            && (request.Tools is not { Count: > 0 }
                || !string.Equals(requested, "required", StringComparison.OrdinalIgnoreCase)
                    && !request.Tools.Any(tool => tool.Name.Equals(requested, StringComparison.Ordinal))))
            throw GatewayException.Invalid("tool_choice требует объявленную функцию в tools.");
        if (!IsEnabled(request)) return null;
        var builder = new StringBuilder();
        builder.AppendLine("You can call the following caller-defined functions. They are executed by the caller, not by you, and are separate from any built-in tools you may have.");
        foreach (var tool in request.Tools!)
        {
            builder.Append("- ").Append(tool.Name);
            if (!string.IsNullOrWhiteSpace(tool.Description)) builder.Append(": ").Append(tool.Description!.Trim());
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(tool.ParametersJson)) builder.Append("  parameters JSON Schema: ").AppendLine(tool.ParametersJson);
        }
        builder.AppendLine("To call functions, reply with nothing but this block (one or more calls):");
        builder.Append(OpenTag).Append("[{\"name\":\"function_name\",\"arguments\":{...}}]").AppendLine(CloseTag);
        var choice = request.ToolChoice;
        if (!request.ToolChoiceIsFunction && string.Equals(choice, "required", StringComparison.OrdinalIgnoreCase))
            builder.Append("You must call at least one function.");
        else if (!string.IsNullOrWhiteSpace(choice) && (request.ToolChoiceIsFunction || !string.Equals(choice, "auto", StringComparison.OrdinalIgnoreCase)))
            builder.Append("You must call the function '").Append(choice).Append("'.");
        else
            builder.Append("If no function is needed, answer normally without the block. Results of earlier calls appear as 'Tool result' messages.");
        return builder.ToString();
    }

    public static void ValidateResult(ChatRequest request, IReadOnlyList<ToolCall> calls)
    {
        var choice = request.ToolChoice;
        if (request.ToolChoiceIsFunction)
        {
            if (calls.Count == 0 || calls.Any(call => !call.Name.Equals(choice, StringComparison.Ordinal)))
                throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент не выполнил обязательный tool_choice.");
            return;
        }
        if (string.IsNullOrWhiteSpace(choice)
            || string.Equals(choice, "auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(choice, "none", StringComparison.OrdinalIgnoreCase)) return;
        if (calls.Count == 0 || !string.Equals(choice, "required", StringComparison.OrdinalIgnoreCase)
            && calls.Any(call => !call.Name.Equals(choice, StringComparison.Ordinal)))
            throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент не выполнил обязательный tool_choice.");
    }

    public static string Render(IEnumerable<ToolCall> calls)
    {
        var array = new JsonArray();
        foreach (var call in calls)
        {
            JsonNode? arguments;
            try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson); }
            catch (JsonException) { arguments = JsonValue.Create(call.ArgumentsJson); }
            array.Add(new JsonObject { ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = arguments });
        }
        return OpenTag + array.ToJsonString() + CloseTag;
    }

    /// <summary>Extracts tool calls; returns the remaining text (may be empty) and the parsed calls.</summary>
    public static (string Text, IReadOnlyList<ToolCall> Calls) Parse(string output, IReadOnlyCollection<ToolDefinition> tools)
    {
        var remaining = output;
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        while (remaining.IndexOf(OpenTag, StringComparison.Ordinal) is var start && start >= 0)
        {
            var end = remaining.IndexOf(CloseTag, start + OpenTag.Length, StringComparison.Ordinal);
            if (end < 0) throw InvalidCalls();
            text.Append(remaining[..start]);
            var block = remaining[start..(end + CloseTag.Length)];
            var parsed = ParseBlock(block, tools);
            if (parsed.Calls.Count == 0) throw InvalidCalls();
            calls.AddRange(parsed.Calls);
            remaining = remaining[(end + CloseTag.Length)..];
        }
        text.Append(remaining);
        return (calls.Count == 0 ? output : text.ToString().Trim(), calls);
    }

    private static GatewayException InvalidCalls() => new(GatewayErrorKind.Upstream, "Нативный клиент вернул некорректный или неизвестный вызов функции.");

    private static (string Text, IReadOnlyList<ToolCall> Calls) ParseBlock(string output, IReadOnlyCollection<ToolDefinition> tools)
    {
        var start = output.IndexOf(OpenTag, StringComparison.Ordinal);
        if (start < 0) return (output, []);
        var end = output.IndexOf(CloseTag, start, StringComparison.Ordinal);
        var json = end < 0 ? output[(start + OpenTag.Length)..] : output[(start + OpenTag.Length)..end];
        json = PromptBuilder.StripJsonFences(json);
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return (output, []); }

        var items = node switch
        {
            JsonArray array => array.ToList(),
            JsonObject obj => [obj],
            _ => []
        };
        var known = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var calls = new List<ToolCall>();
        foreach (var value in items)
        {
            if (value is not JsonObject item) throw InvalidCalls();
            var name = item["name"]?.GetValueKind() == JsonValueKind.String ? item["name"]!.GetValue<string>() : null;
            if (name is null || !known.Contains(name)) throw InvalidCalls();
            var arguments = item["arguments"] switch
            {
                null => "{}",
                JsonValue argument when argument.GetValueKind() == JsonValueKind.String => argument.GetValue<string>(),
                var other => other.ToJsonString()
            };
            calls.Add(new ToolCall("call_" + Guid.NewGuid().ToString("N")[..24], name, arguments));
        }
        if (calls.Count == 0) return (output, []);
        var before = output[..start];
        var after = end < 0 ? string.Empty : output[(end + CloseTag.Length)..];
        return ((before + after).Trim(), calls);
    }
}

/// <summary>Applies OpenAI <c>stop</c> sequences to streamed text without leaking a partial stop marker.</summary>
public sealed class StopSequenceFilter
{
    private readonly string[] _stops;
    private readonly int _holdBack;
    private readonly StringBuilder _pending = new();

    public StopSequenceFilter(IEnumerable<string>? stops)
    {
        _stops = stops?.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray() ?? [];
        _holdBack = _stops.Length == 0 ? 0 : _stops.Max(s => s.Length) - 1;
    }

    public bool Stopped { get; private set; }

    /// <summary>Returns text that is safe to emit now.</summary>
    public string Push(string delta)
    {
        if (Stopped) return string.Empty;
        if (_stops.Length == 0) return delta;
        _pending.Append(delta);
        var text = _pending.ToString();
        var hit = _stops.Select(s => text.IndexOf(s, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (hit >= 0)
        {
            Stopped = true;
            _pending.Clear();
            return text[..hit];
        }
        var safe = Math.Max(0, text.Length - _holdBack);
        _pending.Remove(0, safe);
        return text[..safe];
    }

    public string Flush()
    {
        var rest = Stopped ? string.Empty : _pending.ToString();
        _pending.Clear();
        return rest;
    }
}
