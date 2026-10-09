using System.Text;

namespace LLMGateway.Core;

/// <summary>
/// Native clients take a single prompt, so an OpenAI conversation is rendered as a transcript.
/// Old turns are dropped first when the client has a prompt length limit (command-line clients).
/// </summary>
public static class PromptBuilder
{
    private const string OmittedNotice = "[Earlier messages were omitted to fit the native client's prompt limit.]";
    private const string ConversationHeader = "# Conversation\n";

    public static string Build(ChatRequest request, int maxCharacters)
    {
        var instructions = new List<string>();
        instructions.AddRange(request.Messages
            .Where(m => m.Role is ChatRole.System or ChatRole.Developer && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content.Trim()));
        var toolInstructions = ToolCalling.BuildInstructions(request);
        if (toolInstructions is not null) instructions.Add(toolInstructions);
        var formatInstructions = BuildFormatInstructions(request.ResponseFormat);
        if (formatInstructions is not null) instructions.Add(formatInstructions);

        var turns = request.Messages.Where(m => m.Role is not (ChatRole.System or ChatRole.Developer)).ToList();
        if (turns.Count == 0) throw GatewayException.Invalid("Нужно хотя бы одно сообщение user.");

        if (instructions.Count == 0 && turns.Count == 1 && turns[0].Role == ChatRole.User && IsSafeLeading(turns[0].Content))
        {
            var single = turns[0].Content;
            if (single.Length > maxCharacters) throw TooLong(maxCharacters);
            return single;
        }

        var header = instructions.Count == 0 ? string.Empty
            : "# Instructions\n" + string.Join("\n\n", instructions) + "\n\n";
        const string footer = "\n\nReply as the assistant to the last message of the conversation.";
        var rendered = turns.Select(RenderTurn).ToList();

        var omitted = false;
        while (Length(header, rendered, footer, omitted) > maxCharacters && turns.Count > 1)
        {
            turns.RemoveAt(0);
            while (turns.Count > 0 && turns[0].Role == ChatRole.Tool) turns.RemoveAt(0);
            omitted = true;
            if (turns.Count == 0) throw TooLong(maxCharacters);
            rendered = turns.Select(RenderTurn).ToList();
        }
        if (Length(header, rendered, footer, omitted) > maxCharacters) throw TooLong(maxCharacters);

        var builder = new StringBuilder(header);
        builder.Append(ConversationHeader);
        if (omitted) builder.Append(OmittedNotice).Append("\n\n");
        builder.AppendJoin("\n\n", rendered);
        builder.Append(footer);
        return builder.ToString();
    }

    private static int Length(string header, List<string> turns, string footer, bool omitted) =>
        header.Length + ConversationHeader.Length + (omitted ? OmittedNotice.Length + 2 : 0) + turns.Sum(t => t.Length + 2) + footer.Length;

    private static string RenderTurn(ChatMessage message)
    {
        var builder = new StringBuilder();
        switch (message.Role)
        {
            case ChatRole.User:
                builder.Append("## User").Append(message.Name is { Length: > 0 } ? $" ({message.Name})" : string.Empty).Append('\n').Append(message.Content);
                break;
            case ChatRole.Assistant:
                builder.Append("## Assistant\n").Append(message.Content);
                if (message.ToolCalls is { Count: > 0 } calls) builder.Append(message.Content.Length > 0 ? "\n" : string.Empty).Append(ToolCalling.Render(calls));
                break;
            case ChatRole.Tool:
                builder.Append("## Tool result").Append(message.ToolCallId is { Length: > 0 } id ? $" (tool_call_id={id})" : string.Empty)
                    .Append(message.Name is { Length: > 0 } name ? $" [{name}]" : string.Empty).Append('\n').Append(message.Content);
                break;
        }
        return builder.ToString();
    }

    private static string? BuildFormatInstructions(ResponseFormat? format) => format?.Kind switch
    {
        ResponseFormatKind.JsonObject => "Respond with one valid JSON object only. Do not use markdown code fences or any text outside the JSON.",
        ResponseFormatKind.JsonSchema => "Respond with one valid JSON value only, matching this JSON Schema"
            + (format.SchemaName is { Length: > 0 } name ? $" ({name})" : string.Empty) + ":\n" + format.SchemaJson
            + "\nDo not use markdown code fences or any text outside the JSON.",
        _ => null
    };

    /// <summary>Command-line clients would treat a leading '-' as an option.</summary>
    private static bool IsSafeLeading(string text) => text.Length > 0 && text.TrimStart().Length > 0 && !text.TrimStart().StartsWith('-');

    private static GatewayException TooLong(int max) =>
        GatewayException.Invalid($"Сообщение длиннее лимита нативного клиента ({max} символов). Сократите запрос.");

    public static string StripJsonFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLine = trimmed.IndexOf('\n');
        var lastLine = trimmed.LastIndexOf('\n');
        return firstLine > 0 && lastLine > firstLine && trimmed[(lastLine + 1)..].Trim() == "```"
            ? trimmed[(firstLine + 1)..lastLine].Trim() : trimmed;
    }
}
