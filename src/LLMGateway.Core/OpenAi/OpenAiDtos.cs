using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMGateway.Core.OpenAi;

public sealed class OpenAiChatRequest
{
    public string? Model { get; set; }
    public List<OpenAiMessage>? Messages { get; set; }
    public bool? Stream { get; set; }
    public OpenAiStreamOptions? StreamOptions { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? N { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public JsonElement? Stop { get; set; }
    public List<OpenAiTool>? Tools { get; set; }
    public JsonElement? ToolChoice { get; set; }
    public bool? ParallelToolCalls { get; set; }
    public OpenAiResponseFormat? ResponseFormat { get; set; }
    public string? ReasoningEffort { get; set; }
    public double? PresencePenalty { get; set; }
    public double? FrequencyPenalty { get; set; }
    public bool? Logprobs { get; set; }
    public int? Seed { get; set; }
    public string? User { get; set; }

    /// <summary>LLMGateway extension: explicit native account.</summary>
    public string? AccountId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class OpenAiStreamOptions
{
    public bool? IncludeUsage { get; set; }
}

public sealed class OpenAiMessage
{
    public string Role { get; set; } = "user";
    public JsonElement? Content { get; set; }
    public string? Name { get; set; }
    public List<OpenAiToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class OpenAiToolCall
{
    public int? Index { get; set; }
    public string? Id { get; set; }
    public string? Type { get; set; } = "function";
    public OpenAiFunctionCall? Function { get; set; }
}

public sealed class OpenAiFunctionCall
{
    public string? Name { get; set; }
    public string? Arguments { get; set; }
}

public sealed class OpenAiTool
{
    public string? Type { get; set; } = "function";
    public OpenAiFunctionDefinition? Function { get; set; }
}

public sealed class OpenAiFunctionDefinition
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public JsonElement? Parameters { get; set; }
    public bool? Strict { get; set; }
}

public sealed class OpenAiResponseFormat
{
    public string? Type { get; set; }
    public OpenAiJsonSchema? JsonSchema { get; set; }
}

public sealed class OpenAiJsonSchema
{
    public string? Name { get; set; }
    public JsonElement? Schema { get; set; }
    public bool? Strict { get; set; }
}

public sealed class OpenAiChatCompletion
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = "chat.completion";
    public long Created { get; set; }
    public string Model { get; set; } = string.Empty;
    public List<OpenAiChoice> Choices { get; set; } = [];
    public OpenAiUsage? Usage { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SystemFingerprint { get; set; }

    /// <summary>LLMGateway extension: which native account answered.</summary>
    public OpenAiGatewayInfo? XGateway { get; set; }
}

public sealed class OpenAiChoice
{
    public int Index { get; set; }
    public OpenAiResponseMessage? Message { get; set; }
    public OpenAiDelta? Delta { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? FinishReason { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Logprobs { get; set; }
}

public sealed class OpenAiResponseMessage
{
    public string Role { get; set; } = "assistant";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Content { get; set; }

    public List<OpenAiToolCall>? ToolCalls { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Refusal { get; set; }
}

public sealed class OpenAiDelta
{
    public string? Role { get; set; }
    public string? Content { get; set; }
    public string? ReasoningContent { get; set; }
    public List<OpenAiToolCall>? ToolCalls { get; set; }
}

public sealed class OpenAiUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public OpenAiPromptTokensDetails? PromptTokensDetails { get; set; }
    public OpenAiCompletionTokensDetails? CompletionTokensDetails { get; set; }

    /// <summary>LLMGateway extension: true when the native client did not report usage.</summary>
    public bool? Estimated { get; set; }
}

public sealed class OpenAiPromptTokensDetails
{
    public int CachedTokens { get; set; }
}

public sealed class OpenAiCompletionTokensDetails
{
    public int ReasoningTokens { get; set; }
}

public sealed class OpenAiGatewayInfo
{
    public ProviderKind? Provider { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string? NativeModel { get; set; }
}

public sealed class OpenAiChatChunk
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = "chat.completion.chunk";
    public long Created { get; set; }
    public string Model { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SystemFingerprint { get; set; }

    public List<OpenAiChoice> Choices { get; set; } = [];
    public OpenAiUsage? Usage { get; set; }
    public OpenAiGatewayInfo? XGateway { get; set; }
}

public sealed class OpenAiModel
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = "model";
    public long Created { get; set; }
    public string OwnedBy { get; set; } = string.Empty;
    public ProviderKind Provider { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string NativeModel { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class OpenAiList<T>
{
    public string Object { get; set; } = "list";
    public List<T> Data { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Warning { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReadOnly { get; set; }
}

public sealed class OpenAiErrorResponse
{
    public OpenAiError Error { get; set; } = new();
}

public sealed class OpenAiError
{
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = "invalid_request_error";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Param { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Code { get; set; }

    /// <summary>LLMGateway extension for rate limits.</summary>
    public DateTimeOffset? RetryAt { get; set; }
}

public sealed class OpenAiCompletionRequest
{
    public string? Model { get; set; }
    public JsonElement? Prompt { get; set; }
    public bool? Stream { get; set; }
    public OpenAiStreamOptions? StreamOptions { get; set; }
    public JsonElement? Stop { get; set; }
    public int? MaxTokens { get; set; }
    public int? N { get; set; }
    public bool? Logprobs { get; set; }
    public string? Suffix { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public double? PresencePenalty { get; set; }
    public double? FrequencyPenalty { get; set; }
    public int? Seed { get; set; }
    public string? AccountId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class OpenAiCompletion
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = "text_completion";
    public long Created { get; set; }
    public string Model { get; set; } = string.Empty;
    public List<OpenAiCompletionChoice> Choices { get; set; } = [];
    public OpenAiUsage? Usage { get; set; }
    public OpenAiGatewayInfo? XGateway { get; set; }
}

public sealed class OpenAiCompletionChoice
{
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? FinishReason { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Logprobs { get; set; }
}

/// <summary>Body of POST /v1/gateway/accounts and PUT /v1/gateway/accounts/{id}.</summary>
public sealed class GatewayAccountRequest
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public ProviderKind Provider { get; set; }
    public string? Executable { get; set; }
    public string? ConfigDirectory { get; set; }
    public AccountAuthMode AuthMode { get; set; }
    public string? ApiKeyVariable { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? DefaultModel { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public List<string>? ExtraArguments { get; set; }
    public bool? Enabled { get; set; }

    public AccountProfile ToProfile(string? idOverride = null) => new()
    {
        Id = (idOverride ?? Id ?? string.Empty).Trim(),
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? (idOverride ?? Id ?? string.Empty).Trim() : DisplayName.Trim(),
        Provider = Provider,
        Executable = Blank(Executable),
        ConfigDirectory = Blank(ConfigDirectory),
        AuthMode = AuthMode,
        ApiKeyVariable = Blank(ApiKeyVariable),
        WorkingDirectory = Blank(WorkingDirectory),
        DefaultModel = Blank(DefaultModel),
        Environment = new Dictionary<string, string>(Environment ?? [], StringComparer.OrdinalIgnoreCase),
        ExtraArguments = ExtraArguments ?? [],
        Enabled = Enabled ?? true
    };

    public static GatewayAccountRequest From(AccountProfile profile) => new()
    {
        Id = profile.Id,
        DisplayName = profile.DisplayName,
        Provider = profile.Provider,
        Executable = profile.Executable,
        ConfigDirectory = profile.ConfigDirectory,
        AuthMode = profile.AuthMode,
        ApiKeyVariable = profile.ApiKeyVariable,
        WorkingDirectory = profile.WorkingDirectory,
        DefaultModel = profile.DefaultModel,
        Environment = profile.Environment,
        ExtraArguments = profile.ExtraArguments,
        Enabled = profile.Enabled
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
