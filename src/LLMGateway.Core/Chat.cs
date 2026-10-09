namespace LLMGateway.Core;

public enum ChatRole
{
    System,
    Developer,
    User,
    Assistant,
    Tool
}

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ToolDefinition(string Name, string? Description, string? ParametersJson);

public enum ResponseFormatKind
{
    Text,
    JsonObject,
    JsonSchema
}

public sealed record ResponseFormat(ResponseFormatKind Kind, string? SchemaName = null, string? SchemaJson = null);

public sealed class ChatMessage
{
    public ChatMessage()
    {
    }

    public ChatMessage(ChatRole role, string content)
    {
        Role = role;
        Content = content;
    }

    public ChatRole Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Name { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }

    public static ChatMessage System(string content) => new(ChatRole.System, content);
    public static ChatMessage User(string content) => new(ChatRole.User, content);
    public static ChatMessage Assistant(string content) => new(ChatRole.Assistant, content);
}

public sealed class ChatRequest
{
    /// <summary>In-process execution binding. Never accepted from the HTTP JSON surface.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public GatewayExecutionContext? ExecutionContext { get; set; }

    /// <summary>
    /// Model id. Accepted forms: <c>provider/account/model</c>, <c>provider/model</c>, <c>provider</c>,
    /// a bare native model name or <c>auto</c>.
    /// </summary>
    public string Model { get; set; } = "auto";

    /// <summary>Explicit account; overrides the account part of <see cref="Model"/>.</summary>
    public string? AccountId { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];
    public List<ToolDefinition>? Tools { get; set; }

    /// <summary><c>auto</c>, <c>none</c>, <c>required</c> or a function name.</summary>
    public string? ToolChoice { get; set; }
    /// <summary>Distinguishes an explicit function name from the built-in tool choice modes.</summary>
    public bool ToolChoiceIsFunction { get; set; }

    public ResponseFormat? ResponseFormat { get; set; }
    public List<string>? Stop { get; set; }

    /// <summary>low / medium / high (provider specific values are passed through).</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Caps the returned text (about 4 characters per token) and sets finish_reason to length.</summary>
    public int? MaxOutputTokens { get; set; }
}

/// <summary>An exact account/model binding and an existing project directory; no default routing.</summary>
public sealed record GatewayExecutionContext(ProviderKind Provider, string AccountId, string NativeModel, string WorkingDirectory);

public sealed record TokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int CachedPromptTokens = 0,
    int ReasoningTokens = 0,
    bool Estimated = false)
{
    public static TokenUsage Estimate(string prompt, string completion)
    {
        var input = Math.Max(1, prompt.Length / 4);
        var output = Math.Max(1, completion.Length / 4);
        return new TokenUsage(input, output, input + output, Estimated: true);
    }
}

public sealed record ChatResult(
    string Id,
    long Created,
    string Model,
    string NativeModel,
    ProviderKind Provider,
    string AccountId,
    string? Content,
    IReadOnlyList<ToolCall> ToolCalls,
    string FinishReason,
    TokenUsage Usage);

public enum ChatUpdateKind
{
    /// <summary>Routing resolved; <see cref="ChatUpdate.AccountId"/>, provider and model are set.</summary>
    Started,
    TextDelta,
    ReasoningDelta,
    Completed
}

public sealed record ChatUpdate(
    ChatUpdateKind Kind,
    string? Text = null,
    ChatResult? Result = null,
    string? AccountId = null,
    ProviderKind? Provider = null,
    string? Model = null);

public sealed record GatewayModel(
    string Id,
    string NativeModel,
    string DisplayName,
    ProviderKind Provider,
    string AccountId,
    bool IsDefault);

public sealed record NativeModel(string Id, string DisplayName, bool IsDefault = false);
