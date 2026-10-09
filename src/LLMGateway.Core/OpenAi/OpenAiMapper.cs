using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Core.OpenAi;

/// <summary>Converts between the OpenAI wire format and the gateway's neutral model.</summary>
public static class OpenAiMapper
{
    public static ChatRequest ToChatRequest(OpenAiChatRequest dto, string? accountHeader = null, GatewayOptions? options = null, ILogger? logger = null)
    {
        if (dto.Messages is not { Count: > 0 }) throw GatewayException.Invalid("Поле 'messages' обязательно.");
        var sampling = options?.UnsupportedSamplingParameters ?? UnsupportedSamplingParameterPolicy.Ignore;
        NoteIgnoredSampling(sampling, dto.Temperature, dto.TopP, dto.PresencePenalty, dto.FrequencyPenalty, dto.Seed, logger);
        ValidateCommonOptions(dto.N, dto.Temperature, dto.TopP, dto.PresencePenalty, dto.FrequencyPenalty, dto.Seed, sampling);
        if (dto.MaxTokens is not null && dto.MaxCompletionTokens is not null)
            throw GatewayException.Invalid("Нельзя одновременно указывать max_tokens и max_completion_tokens.");
        if (dto.Logprobs == true) throw Logprobs();
        RejectTokenLimit(dto.MaxCompletionTokens ?? dto.MaxTokens);
        var request = new ChatRequest
        {
            Model = string.IsNullOrWhiteSpace(dto.Model) ? "auto" : dto.Model.Trim(),
            AccountId = FirstNonEmpty(dto.AccountId, accountHeader),
            ReasoningEffort = dto.ReasoningEffort,
            MaxOutputTokens = dto.MaxCompletionTokens ?? dto.MaxTokens,
            Stop = ParseStop(dto.Stop),
            ResponseFormat = ParseResponseFormat(dto.ResponseFormat),
            ToolChoice = ParseToolChoice(dto.ToolChoice),
            ToolChoiceIsFunction = dto.ToolChoice is { ValueKind: JsonValueKind.Object }
        };
        foreach (var message in dto.Messages) request.Messages.Add(ToMessage(message));

        if (dto.Tools is { Count: > 0 })
        {
            request.Tools = [];
            foreach (var tool in dto.Tools)
            {
                if (!string.Equals(tool.Type ?? "function", "function", StringComparison.OrdinalIgnoreCase))
                    throw new GatewayException(GatewayErrorKind.Unsupported, $"Тип инструмента '{tool.Type}' не поддерживается.");
                if (string.IsNullOrWhiteSpace(tool.Function?.Name)) throw GatewayException.Invalid("tools[].function.name обязателен.");
                request.Tools.Add(new ToolDefinition(tool.Function.Name, tool.Function.Description,
                    tool.Function.Parameters is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } parameters ? parameters.GetRawText() : null));
            }
        }
        return request;
    }

    public static ChatRequest ToChatRequest(OpenAiCompletionRequest dto, string? accountHeader = null, GatewayOptions? options = null, ILogger? logger = null)
    {
        var sampling = options?.UnsupportedSamplingParameters ?? UnsupportedSamplingParameterPolicy.Ignore;
        NoteIgnoredSampling(sampling, dto.Temperature, dto.TopP, dto.PresencePenalty, dto.FrequencyPenalty, dto.Seed, logger);
        ValidateCommonOptions(dto.N, dto.Temperature, dto.TopP, dto.PresencePenalty, dto.FrequencyPenalty, dto.Seed, sampling);
        if (dto.Suffix is not null)
            throw new GatewayException(GatewayErrorKind.Unsupported, "Параметр suffix не поддерживается нативными клиентами.");
        if (dto.Logprobs == true) throw Logprobs();
        RejectTokenLimit(dto.MaxTokens);
        var prompt = dto.Prompt switch
        {
            { ValueKind: JsonValueKind.String } value => value.GetString() ?? string.Empty,
            { ValueKind: JsonValueKind.Array } array when array.GetArrayLength() == 1 && array[0].ValueKind == JsonValueKind.String => array[0].GetString() ?? string.Empty,
            _ => throw GatewayException.Invalid("Поле 'prompt' должно быть строкой.")
        };
        if (string.IsNullOrWhiteSpace(prompt)) throw GatewayException.Invalid("Поле 'prompt' пустое.");
        return new ChatRequest
        {
            Model = string.IsNullOrWhiteSpace(dto.Model) ? "auto" : dto.Model.Trim(),
            AccountId = FirstNonEmpty(dto.AccountId, accountHeader),
            Messages = [ChatMessage.User(prompt)],
            Stop = ParseStop(dto.Stop),
            MaxOutputTokens = dto.MaxTokens
        };
    }

    public static OpenAiChatCompletion ToCompletion(ChatResult result) => new()
    {
        Id = result.Id,
        Created = result.Created,
        Model = result.Model,
        Choices =
        [
            new OpenAiChoice
            {
                Index = 0,
                Message = new OpenAiResponseMessage
                {
                    Content = result.Content,
                    ToolCalls = result.ToolCalls.Count == 0 ? null : result.ToolCalls.Select(c => ToWire(c, null)).ToList()
                },
                FinishReason = result.FinishReason
            }
        ],
        Usage = ToUsage(result.Usage),
        XGateway = Info(result)
    };

    public static OpenAiCompletion ToTextCompletion(ChatResult result) => new()
    {
        Id = "cmpl-" + result.Id["chatcmpl-".Length..],
        Created = result.Created,
        Model = result.Model,
        Choices = [new OpenAiCompletionChoice { Index = 0, Text = result.Content ?? string.Empty, FinishReason = result.FinishReason }],
        Usage = ToUsage(result.Usage),
        XGateway = Info(result)
    };

    public static OpenAiChatChunk Chunk(string id, long created, string model, OpenAiDelta delta, string? finishReason = null) => new()
    {
        Id = id,
        Created = created,
        Model = model,
        Choices = [new OpenAiChoice { Index = 0, Delta = delta, FinishReason = finishReason }]
    };

    public static List<OpenAiToolCall> ToStreamToolCalls(IReadOnlyList<ToolCall> calls) =>
        calls.Select((c, i) => ToWire(c, i)).ToList();

    public static OpenAiUsage ToUsage(TokenUsage usage) => new()
    {
        PromptTokens = usage.PromptTokens,
        CompletionTokens = usage.CompletionTokens,
        TotalTokens = usage.TotalTokens,
        PromptTokensDetails = new OpenAiPromptTokensDetails { CachedTokens = usage.CachedPromptTokens },
        CompletionTokensDetails = new OpenAiCompletionTokensDetails { ReasoningTokens = usage.ReasoningTokens },
        Estimated = usage.Estimated ? true : null
    };

    public static TokenUsage FromUsage(OpenAiUsage? usage) => usage is null
        ? new TokenUsage(0, 0, 0, Estimated: true)
        : new TokenUsage(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens,
            usage.PromptTokensDetails?.CachedTokens ?? 0, usage.CompletionTokensDetails?.ReasoningTokens ?? 0, usage.Estimated == true);

    public static OpenAiGatewayInfo Info(ChatResult result) => new()
    {
        Provider = result.Provider,
        AccountId = result.AccountId,
        NativeModel = result.NativeModel
    };

    public static OpenAiModel ToModel(GatewayModel model) => new()
    {
        Id = model.Id,
        Created = 0,
        OwnedBy = model.Provider.ToString().ToLowerInvariant(),
        Provider = model.Provider,
        AccountId = model.AccountId,
        NativeModel = model.NativeModel,
        DisplayName = model.DisplayName,
        IsDefault = model.IsDefault
    };

    public static GatewayModel FromModel(OpenAiModel model) =>
        new(model.Id, model.NativeModel, model.DisplayName ?? model.NativeModel, model.Provider, model.AccountId, model.IsDefault);

    public static (int Status, string Type, string? Code) Describe(GatewayErrorKind kind) => kind switch
    {
        GatewayErrorKind.InvalidRequest => (400, "invalid_request_error", null),
        GatewayErrorKind.Unsupported => (400, "invalid_request_error", "unsupported"),
        GatewayErrorKind.Unauthorized => (401, "authentication_error", "invalid_api_key"),
        GatewayErrorKind.AuthenticationRequired => (401, "authentication_error", "native_login_required"),
        GatewayErrorKind.NotFound => (404, "invalid_request_error", "not_found"),
        GatewayErrorKind.ModelNotFound => (404, "invalid_request_error", "model_not_found"),
        GatewayErrorKind.RateLimited => (429, "rate_limit_error", "rate_limit_exceeded"),
        GatewayErrorKind.ProviderUnavailable => (503, "api_error", "provider_unavailable"),
        GatewayErrorKind.Timeout => (504, "api_error", "timeout"),
        _ => (502, "api_error", "upstream_error")
    };

    public static GatewayErrorKind KindFromWire(int status, string? code) => code switch
    {
        "unsupported" => GatewayErrorKind.Unsupported,
        "invalid_api_key" => GatewayErrorKind.Unauthorized,
        "native_login_required" => GatewayErrorKind.AuthenticationRequired,
        "not_found" => GatewayErrorKind.NotFound,
        "model_not_found" => GatewayErrorKind.ModelNotFound,
        "rate_limit_exceeded" => GatewayErrorKind.RateLimited,
        "provider_unavailable" => GatewayErrorKind.ProviderUnavailable,
        "timeout" => GatewayErrorKind.Timeout,
        _ => status switch
        {
            400 or 422 => GatewayErrorKind.InvalidRequest,
            401 or 403 => GatewayErrorKind.Unauthorized,
            404 => GatewayErrorKind.NotFound,
            429 => GatewayErrorKind.RateLimited,
            503 => GatewayErrorKind.ProviderUnavailable,
            504 => GatewayErrorKind.Timeout,
            _ => GatewayErrorKind.Upstream
        }
    };

    public static OpenAiErrorResponse Error(GatewayException exception)
    {
        var (_, type, code) = Describe(exception.Kind);
        return new OpenAiErrorResponse { Error = new OpenAiError { Message = exception.Message, Type = type, Code = code, RetryAt = exception.RetryAt } };
    }

    private static OpenAiToolCall ToWire(ToolCall call, int? index) => new()
    {
        Index = index,
        Id = call.Id,
        Type = "function",
        Function = new OpenAiFunctionCall { Name = call.Name, Arguments = call.ArgumentsJson }
    };

    private static ChatMessage ToMessage(OpenAiMessage message)
    {
        var role = message.Role?.Trim().ToLowerInvariant() switch
        {
            "system" => ChatRole.System,
            "developer" => ChatRole.Developer,
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "tool" or "function" => ChatRole.Tool,
            var other => throw GatewayException.Invalid($"Неизвестная роль сообщения '{other}'.")
        };
        List<ToolCall>? toolCalls = message.ToolCalls is { Count: > 0 }
            ? message.ToolCalls.Where(c => c.Function?.Name is { Length: > 0 }).Select(c => new ToolCall(c.Id ?? "call_" + Guid.NewGuid().ToString("N")[..24], c.Function!.Name!, c.Function.Arguments ?? "{}")).ToList()
            : null;
        return new ChatMessage(role, ContentText(message.Content))
        {
            Name = message.Name,
            ToolCallId = message.ToolCallId,
            ToolCalls = toolCalls
        };
    }

    /// <summary>Accepts a string, null, or an array of content parts; only text parts are supported.</summary>
    public static string ContentText(JsonElement? content)
    {
        if (content is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return string.Empty;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind != JsonValueKind.Array) throw GatewayException.Invalid("content должен быть строкой или массивом частей.");
        var builder = new StringBuilder();
        foreach (var part in value.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                Append(builder, part.GetString());
                continue;
            }
            if (part.ValueKind != JsonValueKind.Object)
                throw GatewayException.Invalid("Часть content должна быть строкой или объектом.");
            if (!part.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                throw GatewayException.Invalid("content.type должен быть строкой.");
            var type = typeElement.GetString();
            switch (type)
            {
                case "text" or "input_text" or "output_text":
                    Append(builder, ReadText(part, "text"));
                    break;
                case "refusal":
                    Append(builder, ReadText(part, "refusal"));
                    break;
                case "image_url" or "input_image" or "input_audio" or "file" or "input_file":
                    throw new GatewayException(GatewayErrorKind.Unsupported, $"Части содержимого типа '{type}' не поддерживаются: нативные CLI принимают только текст.");
                default:
                    throw new GatewayException(GatewayErrorKind.Unsupported, $"Неизвестный тип части содержимого '{type}'.");
            }
        }
        return builder.ToString();

        static string? ReadText(JsonElement part, string name)
        {
            if (!part.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String)
                throw GatewayException.Invalid("Текст части content должен быть строкой.");
            return text.GetString();
        }

        static void Append(StringBuilder builder, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(text);
        }
    }

    private static List<string>? ParseStop(JsonElement? stop)
    {
        if (stop is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() is { Length: > 0 } s ? [s] : null;
        if (value.ValueKind != JsonValueKind.Array) throw GatewayException.Invalid("stop должен быть строкой или массивом строк.");
        var items = value.EnumerateArray().ToList();
        if (items.Any(v => v.ValueKind != JsonValueKind.String))
            throw GatewayException.Invalid("stop должен содержать только строки.");
        var list = items.Select(v => v.GetString()!).Where(s => s.Length > 0).ToList();
        if (list.Count > 4) throw GatewayException.Invalid("Допустимо не более 4 stop-последовательностей.");
        return list.Count == 0 ? null : list;
    }

    private static string? ParseToolChoice(JsonElement? choice)
    {
        if (choice is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("function", out var function)
            && function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
            return name.GetString();
        throw GatewayException.Invalid("Неверный формат tool_choice.");
    }

    private static ResponseFormat? ParseResponseFormat(OpenAiResponseFormat? format) => format?.Type switch
    {
        null or "text" => null,
        "json_object" => new ResponseFormat(ResponseFormatKind.JsonObject),
        "json_schema" => ParseSchema(format.JsonSchema),
        var other => throw GatewayException.Invalid($"response_format.type '{other}' не поддерживается.")
    };

    private static ResponseFormat ParseSchema(OpenAiJsonSchema? schema)
    {
        if (schema is null || string.IsNullOrWhiteSpace(schema.Name) || schema.Schema is not { ValueKind: JsonValueKind.Object } value)
            throw GatewayException.Invalid("response_format.json_schema требует name и объект schema.");
        return new ResponseFormat(ResponseFormatKind.JsonSchema, schema.Name, value.GetRawText());
    }

    private static GatewayException MultipleChoices() =>
        new(GatewayErrorKind.Unsupported, "Параметр n > 1 не поддерживается нативными клиентами.");

    private static GatewayException Logprobs() =>
        new(GatewayErrorKind.Unsupported, "logprobs не поддерживаются: нативные клиенты не возвращают вероятности токенов.");

    private static void NoteIgnoredSampling(UnsupportedSamplingParameterPolicy policy, double? temperature, double? topP,
        double? presencePenalty, double? frequencyPenalty, int? seed, ILogger? logger)
    {
        if (policy == UnsupportedSamplingParameterPolicy.Reject) return;
        if (temperature is null && topP is null && presencePenalty is null && frequencyPenalty is null && seed is null) return;
        logger?.LogInformation("Ignoring unsupported sampling parameters temperature, top_p, presence_penalty, frequency_penalty and seed.");
    }

    private static void ValidateCommonOptions(int? n, double? temperature, double? topP,
        double? presencePenalty, double? frequencyPenalty, int? seed, UnsupportedSamplingParameterPolicy sampling)
    {
        if (n is <= 0) throw GatewayException.Invalid("n должен быть не меньше 1.");
        if (n is > 1) throw MultipleChoices();
        if (sampling == UnsupportedSamplingParameterPolicy.Reject
            && (temperature is not null || topP is not null || presencePenalty is not null
                || frequencyPenalty is not null || seed is not null))
            throw new GatewayException(GatewayErrorKind.Unsupported,
                "temperature, top_p, presence_penalty, frequency_penalty и seed не поддерживаются нативными клиентами.");
    }

    private static void RejectTokenLimit(int? max)
    {
        if (max is < 1) throw GatewayException.Invalid("max_tokens должен быть не меньше 1.");
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
