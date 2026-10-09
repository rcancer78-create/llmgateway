using System.Buffers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLMGateway.Core.OpenAi;

namespace LLMGateway.Core.Client;

/// <summary>
/// <see cref="ILlmGateway"/> over the gateway's OpenAI-compatible HTTP API. Chat uses only standard
/// OpenAI endpoints; accounts and quotas use the <c>/v1/gateway/*</c> extensions.
/// </summary>
public sealed class OpenAiGatewayClient : ILlmGateway, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string? _apiKey;
    private string? _accountStoreWarning;
    private static readonly JsonSerializerOptions Json = GatewayJson.Options;

    public string? AccountStoreWarning => _accountStoreWarning;

    public OpenAiGatewayClient(Uri baseAddress, string? apiKey = null, TimeSpan? timeout = null)
        : this(new HttpClient { BaseAddress = baseAddress, Timeout = timeout ?? TimeSpan.FromMinutes(15) }, apiKey, ownsClient: true)
    {
    }

    public OpenAiGatewayClient(HttpClient http, string? apiKey = null, bool ownsClient = false)
    {
        _http = http;
        _ownsClient = ownsClient;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync<OpenAiList<ProviderInfo>>("v1/gateway/providers", cancellationToken).ConfigureAwait(false)).Data;

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var list = await GetAsync<OpenAiList<AccountInfo>>("v1/gateway/accounts", cancellationToken).ConfigureAwait(false);
        _accountStoreWarning = list.Warning;
        return list.Data;
    }

    public Task<AccountInfo> AddAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) =>
        SendAsync<AccountInfo>(HttpMethod.Post, "v1/gateway/accounts", AccountWriteBody(profile), cancellationToken);

    public Task<AccountInfo> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) =>
        SendAsync<AccountInfo>(HttpMethod.Put, $"v1/gateway/accounts/{Uri.EscapeDataString(profile.Id)}", AccountWriteBody(profile), cancellationToken);

    public async Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        using var operation = CreateBufferedOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        using var message = CreateMessage(HttpMethod.Delete, $"v1/gateway/accounts/{Uri.EscapeDataString(accountId)}");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<AccountInfo> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
        SendAsync<AccountInfo>(HttpMethod.Post, $"v1/gateway/accounts/{Uri.EscapeDataString(accountId)}/select", null, cancellationToken);

    public Task<AccountInfo> CheckAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
        SendAsync<AccountInfo>(HttpMethod.Post, $"v1/gateway/accounts/{Uri.EscapeDataString(accountId)}/check", null, cancellationToken);

    public async Task StartNativeLoginAsync(string accountId, CancellationToken cancellationToken = default)
    {
        using var operation = CreateBufferedOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        using var message = CreateMessage(HttpMethod.Post, $"v1/gateway/accounts/{Uri.EscapeDataString(accountId)}/login");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GatewayModel>> GetModelsAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
    {
        var query = Query(refresh, accountId);
        var list = await GetAsync<OpenAiList<OpenAiModel>>("v1/models" + query, cancellationToken).ConfigureAwait(false);
        return list.Data.Select(OpenAiMapper.FromModel).ToArray();
    }

    public async Task<IReadOnlyList<QuotaSnapshot>> GetQuotasAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default) =>
        (await GetAsync<OpenAiList<QuotaSnapshot>>("v1/gateway/quotas" + Query(refresh, accountId), cancellationToken).ConfigureAwait(false)).Data;

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var body = ToWire(request, stream: false);
        var completion = await SendAsync<OpenAiChatCompletion>(HttpMethod.Post, "v1/chat/completions", body, cancellationToken).ConfigureAwait(false);
        var choice = completion.Choices?.FirstOrDefault() ?? throw new GatewayException(GatewayErrorKind.Upstream, "Ответ без choices.");
        var message = choice.Message;
        if (string.IsNullOrWhiteSpace(choice.FinishReason) || message is null
            || message.Content is null && message.Refusal is null && message.ToolCalls is not { Count: > 0 })
            throw new GatewayException(GatewayErrorKind.Upstream, "Нативный HTTP-ответ не содержит подтверждённого завершения и сообщения.");
        if (choice.FinishReason == "tool_calls" && message.ToolCalls is not { Count: > 0 })
            throw new GatewayException(GatewayErrorKind.Upstream, "Gateway completion is missing tool calls.");
        var calls = new List<ToolCall>();
        foreach (var call in message.ToolCalls ?? [])
        {
            if (call is null || string.IsNullOrWhiteSpace(call.Id)
                || string.IsNullOrWhiteSpace(call.Function?.Name) || call.Function.Arguments is null)
                throw new GatewayException(GatewayErrorKind.Upstream, "Нативный HTTP-ответ содержит неполный вызов инструмента.");
            calls.Add(new ToolCall(call.Id, call.Function.Name, call.Function.Arguments));
        }
        return new ChatResult(
            completion.Id,
            completion.Created,
            completion.Model,
            completion.XGateway?.NativeModel ?? string.Empty,
            completion.XGateway?.Provider ?? ProviderKind.Unknown,
            completion.XGateway?.AccountId ?? string.Empty,
            VisibleAssistantText(message.Content, message.Refusal),
            calls,
            choice.FinishReason,
            OpenAiMapper.FromUsage(completion.Usage));
    }

    public async IAsyncEnumerable<ChatUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = ToWire(request, stream: true);
        using var message = CreateMessage(HttpMethod.Post, "v1/chat/completions");
        message.Content = JsonContent.Create(body, options: Json);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await SendStreamHandshakeAsync(message, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments, bool ArgumentsSeen)>();
        string? id = null, model = null, finish = null;
        long created = 0;
        OpenAiGatewayInfo? info = null;
        OpenAiUsage? usage = null;
        var started = false;
        var done = false;

        await foreach (var eventData in ReadSseEventsAsync(reader, cancellationToken).ConfigureAwait(false))
        {
            var data = eventData.Trim();
            if (data == "[DONE]") { done = true; break; }
            using var document = ParseSseDocument(data);
            if (document.RootElement.TryGetProperty("error", out _))
            {
                throw ReadSseError(document.RootElement);
            }
            var chunk = ReadSseChunk(document.RootElement);
            id ??= chunk.Id;
            model ??= chunk.Model;
            if (created == 0) created = chunk.Created;
            info = chunk.XGateway ?? info;
            usage = chunk.Usage ?? usage;
            if (!started)
            {
                started = true;
                yield return new ChatUpdate(ChatUpdateKind.Started, AccountId: info?.AccountId, Provider: info?.Provider, Model: chunk.Model);
            }
            foreach (var choice in chunk.Choices)
            {
                finish = choice.FinishReason ?? finish;
                var delta = choice.Delta;
                if (delta is null) continue;
                if (!string.IsNullOrEmpty(delta.ReasoningContent)) yield return new ChatUpdate(ChatUpdateKind.ReasoningDelta, delta.ReasoningContent);
                if (!string.IsNullOrEmpty(delta.Content))
                {
                    content.Append(delta.Content);
                    yield return new ChatUpdate(ChatUpdateKind.TextDelta, delta.Content);
                }
                foreach (var call in delta.ToolCalls ?? [])
                {
                    if (call is null || call.Index is not { } index || index < 0) throw MalformedSse();
                    if (!calls.ContainsKey(index) && calls.Count >= MaximumStreamToolCalls)
                        throw new GatewayException(GatewayErrorKind.Upstream, "Поток шлюза превысил лимит вызовов инструментов (128).");
                    if (!calls.TryGetValue(index, out var entry)) entry = (call.Id ?? string.Empty, call.Function?.Name ?? string.Empty, new StringBuilder(), false);
                    if (!string.IsNullOrEmpty(call.Id)) entry.Id = call.Id;
                    if (!string.IsNullOrEmpty(call.Function?.Name)) entry.Name = call.Function.Name;
                    var arguments = call.Function?.Arguments;
                    if (entry.Arguments.Length > MaximumToolArgumentCharacters - (arguments?.Length ?? 0))
                        throw new GatewayException(GatewayErrorKind.Upstream, "Поток шлюза превысил лимит аргументов инструмента (1 Mi UTF-16 символов).");
                    entry.Arguments.Append(arguments);
                    entry.ArgumentsSeen |= arguments is not null;
                    calls[index] = entry;
                }
            }
        }

        if (!done || string.IsNullOrWhiteSpace(finish))
            throw new GatewayException(GatewayErrorKind.Upstream, "Поток ответа оборван до подтверждения завершения.");
        if (finish == "tool_calls" && calls.Count == 0
            || calls.Values.Any(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name) || !c.ArgumentsSeen)
            || calls.Values.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != calls.Count)
            throw MalformedSse();

        var result = new ChatResult(
            id ?? string.Empty,
            created,
            model ?? request.Model,
            info?.NativeModel ?? string.Empty,
            info?.Provider ?? ProviderKind.Unknown,
            info?.AccountId ?? string.Empty,
            content.Length == 0 ? null : content.ToString(),
            calls.Values.Select(c => new ToolCall(c.Id, c.Name, c.Arguments.ToString())).ToArray(),
            finish,
            OpenAiMapper.FromUsage(usage));
        yield return new ChatUpdate(ChatUpdateKind.Completed, Result: result, AccountId: result.AccountId, Provider: result.Provider, Model: result.Model);
    }

    public static OpenAiChatRequest ToWire(ChatRequest request, bool stream) => request.ExecutionContext is not null
        ? throw new GatewayException(GatewayErrorKind.Unsupported, "Exact in-process execution bindings cannot be sent over HTTP.")
        : new()
    {
        Model = request.Model,
        AccountId = request.AccountId,
        Stream = stream,
        StreamOptions = stream ? new OpenAiStreamOptions { IncludeUsage = true } : null,
        ReasoningEffort = request.ReasoningEffort,
        MaxCompletionTokens = request.MaxOutputTokens,
        Stop = request.Stop is { Count: > 0 } stop ? JsonSerializer.SerializeToElement(stop) : null,
        Messages = request.Messages.Select(m => new OpenAiMessage
        {
            Role = m.Role.ToString().ToLowerInvariant(),
            Content = JsonSerializer.SerializeToElement(m.Content),
            Name = m.Name,
            ToolCallId = m.ToolCallId,
            ToolCalls = m.ToolCalls?.Select(c => new OpenAiToolCall { Id = c.Id, Function = new OpenAiFunctionCall { Name = c.Name, Arguments = c.ArgumentsJson } }).ToList()
        }).ToList(),
        Tools = request.Tools?.Select(t => new OpenAiTool
        {
            Function = new OpenAiFunctionDefinition
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = string.IsNullOrWhiteSpace(t.ParametersJson) ? null : CloneJson(t.ParametersJson)
            }
        }).ToList(),
        ToolChoice = request.ToolChoiceIsFunction && request.ToolChoice is { } functionName
            ? JsonSerializer.SerializeToElement(new { type = "function", function = new { name = functionName } })
            : request.ToolChoice switch
        {
            null => null,
            "auto" or "none" or "required" => JsonSerializer.SerializeToElement(request.ToolChoice),
            var name => JsonSerializer.SerializeToElement(new { type = "function", function = new { name } })
        },
        ResponseFormat = request.ResponseFormat?.Kind switch
        {
            ResponseFormatKind.JsonObject => new OpenAiResponseFormat { Type = "json_object" },
            ResponseFormatKind.JsonSchema => new OpenAiResponseFormat
            {
                Type = "json_schema",
                JsonSchema = new OpenAiJsonSchema
                {
                    Name = request.ResponseFormat.SchemaName ?? "response",
                    Schema = CloneJson(request.ResponseFormat.SchemaJson ?? "{}")
                }
            },
            _ => null
        }
    };

    private static JsonElement CloneJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Query(bool refresh, string? accountId)
    {
        var parts = new List<string>();
        if (refresh) parts.Add("refresh=true");
        if (!string.IsNullOrWhiteSpace(accountId)) parts.Add("account_id=" + Uri.EscapeDataString(accountId));
        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var operation = CreateBufferedOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        using var message = CreateMessage(HttpMethod.Get, path);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var operation = CreateBufferedOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        using var message = CreateMessage(method, path);
        if (body is not null) message.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false))!;
    }

    private HttpRequestMessage CreateMessage(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, path);
        if (_apiKey is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return message;
    }

    private CancellationTokenSource CreateBufferedOperationCancellation(CancellationToken callerToken)
    {
        // HeadersRead prevents eager error buffering; preserve the former whole-operation
        // HttpClient deadline through actual success/error body consumption and disposal.
        var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        var timeout = _http.Timeout;
        if (timeout != Timeout.InfiniteTimeSpan) operation.CancelAfter(timeout);
        return operation;
    }

    private static GatewayAccountRequest AccountWriteBody(AccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Environment is null || profile.ExtraArguments is null)
            throw GatewayException.Invalid("Профиль содержит некорректные коллекции настроек.");
        var frozen = profile.Clone();
        if (frozen.Environment.Any(pair => AccountEnvironment.IsSecret(pair.Key, pair.Value)))
            throw GatewayException.Invalid("Профиль не должен передавать значения секретов; укажите имя переменной окружения с ключом.");
        return GatewayAccountRequest.From(frozen);
    }

    // Limits allocation while reading, including a single unterminated line or event.
    private const int MaxSseEventCharacters = 4 * 1024 * 1024;
    private const int MaximumStreamToolCalls = 128;
    private const int MaximumToolArgumentCharacters = 1024 * 1024;
    private static async IAsyncEnumerable<string> ReadSseEventsAsync(StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var previousCr = false;
        var hasData = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    if (character == '\n' && previousCr) { previousCr = false; continue; }
                    previousCr = character == '\r';
                    if (line.Length == 0)
                    {
                        if (hasData) { yield return data.ToString(); data.Clear(); hasData = false; }
                    }
                    else if (line.Length >= 5 && line.ToString(0, 5) == "data:")
                    {
                        var start = line.Length > 5 && line[5] == ' ' ? 6 : 5;
                        var added = line.Length - start + (hasData ? 1 : 0);
                        if (data.Length > MaxSseEventCharacters - added) throw OversizedSse();
                        if (hasData) data.Append('\n');
                        data.Append(line, start, line.Length - start);
                        hasData = true;
                    }
                    line.Clear();
                }
                else
                {
                    previousCr = false;
                    if (line.Length >= MaxSseEventCharacters) throw OversizedSse();
                    line.Append(character);
                }
            }
        }
        // An event without its blank separator is incomplete and cannot prove success.
    }

    private static GatewayException OversizedSse() => new(GatewayErrorKind.Upstream,
        "Поток шлюза превысил лимит строки или события SSE (4 Mi UTF-16 символов).");

    private async Task<HttpResponseMessage> SendStreamHandshakeAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        // The finite HTTP deadline covers headers and an unsuccessful response body.
        // Dispose it before publishing a successful SSE body, which retains caller ownership.
        using var operation = CreateBufferedOperationCancellation(cancellationToken);
        var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, operation.Token).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, operation.Token).ConfigureAwait(false);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static JsonDocument ParseSseDocument(string data)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(data); }
        catch (JsonException) { throw MalformedSse(); }
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw MalformedSse();
    }

    private static GatewayException ReadSseError(JsonElement root)
    {
        OpenAiError? error;
        try { error = root.Deserialize<OpenAiErrorResponse>(Json)?.Error; }
        catch (JsonException) { return MalformedSse(); }
        if (error is null) return MalformedSse();
        return new GatewayException(OpenAiMapper.KindFromWire(502, error.Code),
            error.Message is { Length: > 0 } message ? message : "Поток шлюза вернул ошибку без сообщения.")
        { RetryAt = error.RetryAt };
    }

    private static OpenAiChatChunk ReadSseChunk(JsonElement root)
    {
        OpenAiChatChunk? chunk;
        try { chunk = root.Deserialize<OpenAiChatChunk>(Json); }
        catch (JsonException) { throw MalformedSse(); }
        if (chunk is null || chunk.Choices is null || chunk.Choices.Any(choice => choice is null))
            throw MalformedSse();
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("refusal", out var refusal))
                {
                    if (refusal.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw MalformedSse();
                    if (chunk.Choices[index].Delta is { } parsed)
                        parsed.Content = VisibleAssistantText(parsed.Content, refusal.GetString());
                }
                index++;
            }
        }
        return chunk;
    }

    // A refusal is assistant-visible text in the neutral contract. Do not invent whitespace
    // between fields or stream fragments, and keep ordinary content unchanged when absent.
    private static string? VisibleAssistantText(string? content, string? refusal) =>
        string.IsNullOrEmpty(refusal) ? content : string.IsNullOrEmpty(content) ? refusal : content + refusal;

    private static GatewayException MalformedSse() => new(GatewayErrorKind.Upstream,
        "Поток шлюза содержит некорректные данные SSE.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await ReadBoundedErrorBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        OpenAiError? error = null;
        try { error = JsonSerializer.Deserialize<OpenAiErrorResponse>(text, Json)?.Error; }
        catch (JsonException) { }
        var status = (int)response.StatusCode;
        var path = response.RequestMessage?.RequestUri?.AbsolutePath;
        throw new GatewayException(OpenAiMapper.KindFromWire(status, error?.Code),
            error?.Message is { Length: > 0 } message ? message : $"HTTP {status} {path}: {NativeErrorClassifier.Trim(text, 300)}".TrimEnd(' ', ':'))
        { RetryAt = error?.RetryAt };
    }

    private const int MaximumErrorBodyBytes = 4 * 1024 * 1024;
    private static async Task<string> ReadBoundedErrorBodyAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaximumErrorBodyBytes) throw OversizedErrorBody();
        var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using var body = new MemoryStream(8192);
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (true)
                {
                    // Read at most one byte beyond the inclusive budget, before retaining it.
                    var room = (int)Math.Min(buffer.Length, MaximumErrorBodyBytes - body.Length + 1);
                    var count = await source.ReadAsync(buffer.AsMemory(0, room), token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (body.Length + count > MaximumErrorBodyBytes) throw OversizedErrorBody();
                    body.Write(buffer, 0, count);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }

            Encoding encoding = Encoding.UTF8;
            if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
            {
                try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
                catch (ArgumentException exception)
                {
                    throw new GatewayException(GatewayErrorKind.Upstream, "Неизвестная кодировка ответа шлюза.", exception);
                }
            }
            body.Position = 0;
            using var reader = new StreamReader(body, encoding, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }
    }

    private static GatewayException OversizedErrorBody() => new(GatewayErrorKind.Upstream,
        "Ответ ошибки шлюза превысил допустимый размер (4 MiB).");

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
