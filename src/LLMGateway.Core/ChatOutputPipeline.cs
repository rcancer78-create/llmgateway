using System.Text;
using System.Text.Json;

namespace LLMGateway.Core;

/// <summary>
/// Applies stop sequences, the output budget tail, tool-call extraction and JSON checks
/// after a native client has finished producing text.
/// </summary>
public static class ChatOutputPipeline
{
    public readonly record struct Result(string Content, IReadOnlyList<ToolCall> Calls, bool Truncated, bool Stopped);

    public static Result Complete(
        string streamed,
        bool truncated,
        string? finalText,
        StopSequenceFilter liveStop,
        ChatRequest request,
        bool useTools,
        bool jsonMode)
    {
        var builder = new StringBuilder(streamed);
        var stopped = liveStop.Stopped;
        if (!useTools && !truncated)
        {
            var tail = liveStop.Flush();
            var piece = OutputBudget.Fit(tail, builder.Length, request.MaxOutputTokens, out var exhausted);
            truncated |= exhausted;
            if (piece.Length > 0) builder.Append(piece);
        }
        if (!truncated && builder.Length == 0 && !string.IsNullOrEmpty(finalText))
        {
            var final = OutputBudget.Fit(liveStop.Push(finalText) + liveStop.Flush(), builder.Length, request.MaxOutputTokens, out var exhausted);
            truncated |= exhausted;
            stopped |= liveStop.Stopped;
            if (final.Length > 0) builder.Append(final);
        }

        var content = builder.ToString();
        IReadOnlyList<ToolCall> calls = [];
        if (!truncated && useTools)
        {
            (content, calls) = ToolCalling.Parse(content, request.Tools!);
            var textStop = new StopSequenceFilter(request.Stop);
            content = textStop.Push(content) + textStop.Flush();
            stopped = textStop.Stopped;
            ToolCalling.ValidateResult(request, calls);
        }
        if (!truncated && jsonMode && calls.Count == 0)
        {
            content = PromptBuilder.StripJsonFences(content);
            if (request.ResponseFormat?.Kind == ResponseFormatKind.JsonObject)
                EnsureJsonObject(content);
        }
        return new Result(content, calls, truncated, stopped);
    }

    private static void EnsureJsonObject(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент вернул ответ, не являющийся JSON-объектом.");
        }
        catch (JsonException ex)
        {
            throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент вернул некорректный JSON-объект.", ex);
        }
    }
}
