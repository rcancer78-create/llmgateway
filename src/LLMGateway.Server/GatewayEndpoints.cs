using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.OpenAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LLMGateway.Server;

public sealed class GatewayServerOptions
{
    /// <summary>Bearer key for /v1. Without a key or validator the API refuses dispatch and management.</summary>
    public string? ApiKey { get; set; }
    public bool RequireApiKey { get; set; }
    public bool LoopbackOnly { get; set; }

    /// <summary>Browser and webview origins that may call the API. Empty rejects every Origin and Sec-Fetch header.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; set; } = [];
    public bool ExposeManagement { get; set; } = true;
    public bool SanitizeErrors { get; set; } = true;
    /// <summary>Disable when the facade has only configured routing, without response-origin identity.</summary>
    public bool ExposeRoutingMetadata { get; set; } = true;
    public long MaxRequestBodyBytes { get; set; } = 1024 * 1024;
    public Func<string, CancellationToken, Task<bool>>? ApiKeyValidator { get; set; }

    /// <summary>Interval of SSE keep-alive comments while a native client is thinking.</summary>
    public int KeepAliveSeconds { get; set; } = 15;
}

public static class GatewayEndpoints
{
    private const string AccountHeader = "X-LLM-Account";
    private static readonly JsonSerializerOptions Json = GatewayJson.Options;

    public static WebApplication MapLlmGateway(this WebApplication app, GatewayServerOptions? options = null)
    {
        options ??= app.Services.GetService<GatewayServerOptions>() ?? new GatewayServerOptions();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LLMGateway.Server");
        app.Use((context, next) => GuardAsync(context, next, options, logger));

        app.MapGet("/health", () => Results.Json(new { status = "ok", service = "llmgateway" }, Json));

        var v1 = app.MapGroup("/v1");
        v1.MapGet("/models", async (bool? refresh, string? account_id, ILlmGateway gateway, CancellationToken ct) =>
        {
            var models = await gateway.GetModelsAsync(refresh == true, account_id, ct);
            return Results.Json(new OpenAiList<OpenAiModel> { Data = models.Select(OpenAiMapper.ToModel).ToList() }, Json);
        });
        v1.MapGet("/models/{**id}", async (string id, ILlmGateway gateway, CancellationToken ct) =>
        {
            var model = (await gateway.GetModelsAsync(false, null, ct)).FirstOrDefault(m => m.Id.Equals(id, StringComparison.Ordinal))
                ?? throw new GatewayException(GatewayErrorKind.ModelNotFound, $"Модель '{id}' не найдена.");
            return Results.Json(OpenAiMapper.ToModel(model), Json);
        });
        v1.MapPost("/chat/completions", ChatCompletionsAsync);
        v1.MapPost("/completions", CompletionsAsync);
        v1.MapPost("/embeddings", (HttpContext _) => throw UnsupportedSurface("embeddings"));
        v1.MapPost("/images/generations", (HttpContext _) => throw UnsupportedSurface("images"));
        v1.MapPost("/audio/transcriptions", (HttpContext _) => throw UnsupportedSurface("audio/transcriptions"));
        v1.MapPost("/audio/speech", (HttpContext _) => throw UnsupportedSurface("audio/speech"));

        if (options.ExposeManagement)
        {
            var gatewayGroup = v1.MapGroup("/gateway");
            gatewayGroup.MapGet("/providers", async (ILlmGateway gateway, CancellationToken ct) =>
                Results.Json(new OpenAiList<ProviderInfo> { Data = [.. await gateway.GetProvidersAsync(ct)] }, Json));
            gatewayGroup.MapGet("/accounts", async (ILlmGateway gateway, CancellationToken ct) =>
            {
                var warning = gateway.AccountStoreWarning;
                return Results.Json(new OpenAiList<AccountInfo>
                {
                    Data = [.. await gateway.GetAccountsAsync(ct)],
                    Warning = warning,
                    ReadOnly = warning is null ? null : true
                }, Json);
            });
            gatewayGroup.MapPost("/accounts", async (HttpContext context, ILlmGateway gateway, CancellationToken ct) =>
            {
                var body = await ReadAsync<GatewayAccountRequest>(context, ct);
                return Results.Json(await gateway.AddAccountAsync(body.ToProfile(), ct), Json, statusCode: StatusCodes.Status201Created);
            });
            gatewayGroup.MapPut("/accounts/{id}", async (string id, HttpContext context, ILlmGateway gateway, CancellationToken ct) =>
            {
                var body = await ReadAsync<GatewayAccountRequest>(context, ct);
                return Results.Json(await gateway.UpdateAccountAsync(body.ToProfile(id), ct), Json);
            });
            gatewayGroup.MapDelete("/accounts/{id}", async (string id, ILlmGateway gateway, CancellationToken ct) =>
            {
                await gateway.RemoveAccountAsync(id, ct);
                return Results.Json(new { id, deleted = true }, Json);
            });
            gatewayGroup.MapPost("/accounts/{id}/select", async (string id, ILlmGateway gateway, CancellationToken ct) =>
                Results.Json(await gateway.SelectAccountAsync(id, ct), Json));
            gatewayGroup.MapPost("/accounts/{id}/check", async (string id, ILlmGateway gateway, CancellationToken ct) =>
                Results.Json(await gateway.CheckAccountAsync(id, ct), Json));
            gatewayGroup.MapPost("/accounts/{id}/login", async (string id, HttpContext context, ILlmGateway gateway, CancellationToken ct) =>
            {
                if (!IsLoopback(context))
                    throw new GatewayException(GatewayErrorKind.Unauthorized, "Интерактивный вход открывается на машине шлюза и доступен только с loopback.");
                await gateway.StartNativeLoginAsync(id, ct);
                return Results.Json(new { id, started = true }, Json);
            });
            gatewayGroup.MapGet("/quotas", async (bool? refresh, string? account_id, ILlmGateway gateway, CancellationToken ct) =>
                Results.Json(new OpenAiList<QuotaSnapshot> { Data = [.. await gateway.GetQuotasAsync(refresh == true, account_id, ct)] }, Json));
        }

        return app;

        static GatewayException UnsupportedSurface(string operation) =>
            new(GatewayErrorKind.Unsupported, $"Операция '{operation}' не поддерживается: локальные клиенты принимают только текстовый чат.");

        async Task ChatCompletionsAsync(HttpContext context, ILlmGateway gateway)
        {
            var dto = await ReadAsync<OpenAiChatRequest>(context, context.RequestAborted);
            dto.AccountId ??= dto.Extra is not null && dto.Extra.TryGetValue("accountId", out var camel) && camel.ValueKind == JsonValueKind.String ? camel.GetString() : null;
            var request = OpenAiMapper.ToChatRequest(dto, context.Request.Headers[AccountHeader].FirstOrDefault(), SamplingOptions(context), logger);
            if (dto.Stream == true)
            {
                await StreamChatAsync(context, gateway, request, dto.StreamOptions?.IncludeUsage == true, options, chat: true);
                return;
            }
            var result = await gateway.CompleteAsync(request, context.RequestAborted);
            if (options.ExposeRoutingMetadata) context.Response.Headers[AccountHeader] = result.AccountId;
            var completion = OpenAiMapper.ToCompletion(result);
            if (!options.ExposeRoutingMetadata) completion.XGateway = null;
            await context.Response.WriteAsJsonAsync(completion, Json, context.RequestAborted);
        }

        async Task CompletionsAsync(HttpContext context, ILlmGateway gateway)
        {
            var dto = await ReadAsync<OpenAiCompletionRequest>(context, context.RequestAborted);
            var request = OpenAiMapper.ToChatRequest(dto, context.Request.Headers[AccountHeader].FirstOrDefault(), SamplingOptions(context), logger);
            if (dto.Stream == true)
            {
                await StreamChatAsync(context, gateway, request, dto.StreamOptions?.IncludeUsage == true, options, chat: false);
                return;
            }
            var result = await gateway.CompleteAsync(request, context.RequestAborted);
            if (options.ExposeRoutingMetadata) context.Response.Headers[AccountHeader] = result.AccountId;
            var completion = OpenAiMapper.ToTextCompletion(result);
            if (!options.ExposeRoutingMetadata) completion.XGateway = null;
            await context.Response.WriteAsJsonAsync(completion, Json, context.RequestAborted);
        }
    }

    private static async Task StreamChatAsync(HttpContext context, ILlmGateway gateway, ChatRequest request, bool includeUsage, GatewayServerOptions options, bool chat)
    {
        using var streamStop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var cancellation = streamStop.Token;
        await using var updates = gateway.StreamAsync(request, cancellation).GetAsyncEnumerator(cancellation);
        // Routing errors surface on the first step, before any SSE byte is sent, so they keep a proper HTTP status.
        if (!await updates.MoveNextAsync()) throw new GatewayException(GatewayErrorKind.Upstream, "Пустой поток ответа.");
        var started = updates.Current;
        if (started.Kind != ChatUpdateKind.Started)
            throw new GatewayException(GatewayErrorKind.Upstream, "Поток не содержит начального события.");
        var id = (chat ? "chatcmpl-" : "cmpl-") + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = started.Model ?? request.Model;
        var info = new OpenAiGatewayInfo { Provider = started.Provider ?? ProviderKind.Unknown, AccountId = started.AccountId ?? string.Empty };

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        if (options.ExposeRoutingMetadata) response.Headers[AccountHeader] = info.AccountId;
        await response.StartAsync(cancellation);

        if (chat)
        {
            var first = OpenAiMapper.Chunk(id, created, model, new OpenAiDelta { Role = "assistant", Content = string.Empty });
            if (options.ExposeRoutingMetadata) first.XGateway = info;
            await SendAsync(response, first, cancellation);
        }

        try
        {
            var completed = false;
            var keepAlive = TimeSpan.FromSeconds(Math.Max(5, options.KeepAliveSeconds));
            while (true)
            {
                var next = updates.MoveNextAsync().AsTask();
                using var delayStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                try
                {
                    while (await Task.WhenAny(next, Task.Delay(keepAlive, delayStop.Token)) != next)
                    {
                        await response.WriteAsync(": keep-alive\n\n", cancellation);
                        await response.Body.FlushAsync(cancellation);
                    }
                }
                catch
                {
                    // Finish the outstanding MoveNext before disposing the iterator.
                    streamStop.Cancel();
                    try { await next; } catch { }
                    throw;
                }
                finally { delayStop.Cancel(); }
                if (!await next) break;
                var update = updates.Current;
                if (completed || update.Kind == ChatUpdateKind.Started)
                    throw new GatewayException(GatewayErrorKind.Upstream, "Нарушена последовательность событий ответа.");
                switch (update.Kind)
                {
                    case ChatUpdateKind.TextDelta:
                        await SendAsync(response, chat
                            ? OpenAiMapper.Chunk(id, created, model, new OpenAiDelta { Content = update.Text })
                            : TextChunk(id, created, model, update.Text ?? string.Empty, null), cancellation);
                        break;
                    case ChatUpdateKind.ReasoningDelta when chat:
                        await SendAsync(response, OpenAiMapper.Chunk(id, created, model, new OpenAiDelta { ReasoningContent = update.Text }), cancellation);
                        break;
                    case ChatUpdateKind.Completed:
                        completed = true;
                        var result = update.Result!;
                        if (chat && result.ToolCalls.Count > 0)
                            await SendAsync(response, OpenAiMapper.Chunk(id, created, model, new OpenAiDelta { ToolCalls = OpenAiMapper.ToStreamToolCalls(result.ToolCalls) }), cancellation);
                        info.NativeModel = result.NativeModel;
                        if (chat)
                        {
                            var last = OpenAiMapper.Chunk(id, created, model, new OpenAiDelta(), result.FinishReason);
                            if (options.ExposeRoutingMetadata) last.XGateway = info;
                            await SendAsync(response, last, cancellation);
                        }
                        else
                        {
                            await SendAsync(response, TextChunk(id, created, model, string.Empty, result.FinishReason), cancellation);
                        }
                        if (includeUsage)
                            await SendAsync(response, new OpenAiChatChunk { Id = id, Created = created, Model = model, Usage = OpenAiMapper.ToUsage(result.Usage), Object = chat ? "chat.completion.chunk" : "text_completion" }, cancellation);
                        break;
                }
            }
            if (!completed) throw new GatewayException(GatewayErrorKind.Upstream, "Поток ответа оборван до подтверждения завершения.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
        catch (GatewayException ex)
        {
            await SendAsync(response, OpenAiMapper.Error(PublicError(ex, options)), cancellation);
            return;
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LLMGateway.Server");
            logger.LogError("Gateway stream failed ({ExceptionType})", ex.GetType().Name);
            await SendAsync(response, OpenAiMapper.Error(PublicError(
                new GatewayException(GatewayErrorKind.Upstream, "Не удалось завершить поток ответа."), options)), cancellation);
            return;
        }
        await response.WriteAsync("data: [DONE]\n\n", cancellation);
        await response.Body.FlushAsync(cancellation);
    }

    private static object TextChunk(string id, long created, string model, string text, string? finish) => new OpenAiCompletion
    {
        Id = id,
        Created = created,
        Model = model,
        Choices = [new OpenAiCompletionChoice { Index = 0, Text = text, FinishReason = finish }]
    };

    private static async Task SendAsync(HttpResponse response, object payload, CancellationToken cancellation)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), Json);
        await response.WriteAsync("data: " + json + "\n\n", Encoding.UTF8, cancellation);
        await response.Body.FlushAsync(cancellation);
    }

    private static async Task<T> ReadAsync<T>(HttpContext context, CancellationToken cancellation) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json, cancellation)
                ?? throw GatewayException.Invalid("Пустое тело запроса.");
        }
        catch (JsonException ex)
        {
            throw GatewayException.Invalid("Некорректный JSON: " + ex.Message);
        }
    }

    private static async Task GuardAsync(HttpContext context, RequestDelegate next, GatewayServerOptions options, ILogger logger)
    {
        // Browsers require an explicit allowed origin. Loopback alone is not
        // authentication; also reject rebinding Host values on local connections.
        var browserRequest = context.Request.Headers.ContainsKey("Origin")
            || context.Request.Headers.Keys.Any(name => name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase));
        if ((browserRequest && !IsAllowedOrigin(context, options))
            || (options.LoopbackOnly && !IsLoopback(context))
            || (IsLoopback(context) && !IsLocalHost(context.Request.Host.Host)))
        {
            await WriteErrorAsync(context, new GatewayException(GatewayErrorKind.Unauthorized, "Локальный HTTP-доступ запрещён."));
            return;
        }
        if (browserRequest)
        {
            const string methods = "GET, POST, PUT, DELETE";
            const string headers = "authorization, content-type, x-api-key, x-llm-account";
            var preflight = HttpMethods.IsOptions(context.Request.Method)
                && context.Request.Headers.ContainsKey("Access-Control-Request-Method");
            if (preflight)
            {
                var requestedMethod = context.Request.Headers["Access-Control-Request-Method"].ToString();
                var requestedHeaders = context.Request.Headers["Access-Control-Request-Headers"].ToString();
                if (!methods.Split(", ").Contains(requestedMethod, StringComparer.Ordinal)
                    || requestedHeaders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(header => !headers.Split(", ").Contains(header, StringComparer.OrdinalIgnoreCase)))
                {
                    await WriteErrorAsync(context, new GatewayException(GatewayErrorKind.Unauthorized, "Параметры CORS-запроса запрещены."));
                    return;
                }
            }
            context.Response.Headers["Access-Control-Allow-Origin"] = context.Request.Headers.Origin;
            context.Response.Headers.Append("Vary", "Origin");
            if (preflight)
            {
                context.Response.Headers["Access-Control-Allow-Methods"] = methods;
                context.Response.Headers["Access-Control-Allow-Headers"] = headers;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
        }
        if (options.RequireApiKey || context.Request.Path.StartsWithSegments("/v1"))
        {
            if (options.RequireApiKey || options.ApiKeyValidator is not null || !string.IsNullOrEmpty(options.ApiKey))
            {
                var header = context.Request.Headers.Authorization.ToString();
                var provided = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : context.Request.Headers["x-api-key"].ToString();
                var accepted = false;
                try
                {
                    accepted = provided.Length is > 0 and <= 4096 && (options.ApiKeyValidator is { } validate
                        ? await validate(provided, context.RequestAborted)
                        : !string.IsNullOrEmpty(options.ApiKey) && FixedEquals(provided, options.ApiKey));
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogWarning("Gateway authentication unavailable ({ExceptionType})", ex.GetType().Name);
                    await WriteErrorAsync(context, new GatewayException(GatewayErrorKind.ProviderUnavailable,
                        "Проверка ключа доступа временно недоступна."));
                    return;
                }
                if (!accepted)
                {
                    await WriteErrorAsync(context, new GatewayException(GatewayErrorKind.Unauthorized, "Неверный API-ключ."));
                    return;
                }
            }
            else
            {
                await WriteErrorAsync(context, new GatewayException(GatewayErrorKind.Unauthorized, "Для HTTP API требуется настроенный ключ доступа."));
                return;
            }
        }
        if (context.Request.ContentLength is long length && length > options.MaxRequestBodyBytes)
        {
            await WriteErrorAsync(context, GatewayException.Invalid("Тело запроса превышает допустимый размер."));
            return;
        }
        var requestBody = context.Request.Body;
        // Count consumed bytes as well as Content-Length: custom hosts can accept
        // chunked bodies without installing EmbeddedGatewayServer's Kestrel cap.
        using var boundedBody = new BoundedRequestBody(requestBody, options.MaxRequestBodyBytes);
        context.Request.Body = boundedBody;
        try
        {
            await next(context);
        }
        catch (GatewayException ex) when (!context.Response.HasStarted)
        {
            logger.LogInformation("Gateway request failed ({Kind})", ex.Kind);
            await WriteErrorAsync(context, PublicError(ex, options));
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            await WriteErrorAsync(context, GatewayException.Invalid(options.SanitizeErrors ? "Некорректный HTTP-запрос." : ex.Message));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            logger.LogError("Gateway request failed ({ExceptionType})", ex.GetType().Name);
            await WriteErrorAsync(context, new GatewayException(GatewayErrorKind.Upstream,
                options.SanitizeErrors ? "Не удалось выполнить запрос. Проверьте локальный журнал приложения." : ex.Message));
        }
        finally { context.Request.Body = requestBody; }
    }

    /// <summary>Bounds reads without buffering the body or taking ownership of the host stream.</summary>
    private sealed class BoundedRequestBody(Stream inner, long limit) : Stream
    {
        private long _remaining = limit;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..ReadSize(buffer.Length)]);
            Account(read);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..ReadSize(buffer.Length)], cancellationToken).ConfigureAwait(false);
            Account(read);
            return read;
        }
        private int ReadSize(int requested)
        {
            if (_remaining < 0) throw TooLarge();
            // Read at most one byte beyond the limit to distinguish exact-size EOF
            // from an oversized body. This also avoids overflow for long.MaxValue.
            return _remaining >= requested ? requested : (int)_remaining + 1;
        }
        private void Account(int read)
        {
            if (read > _remaining) throw TooLarge();
            _remaining -= read;
        }
        private static GatewayException TooLarge() => GatewayException.Invalid("Тело запроса превышает допустимый размер.");
    }

    private static async Task WriteErrorAsync(HttpContext context, GatewayException exception)
    {
        var (status, _, _) = OpenAiMapper.Describe(exception.Kind);
        context.Response.StatusCode = status;
        if (exception.RetryAt is { } retry)
            context.Response.Headers.RetryAfter = Math.Max(1, (int)(retry - DateTimeOffset.UtcNow).TotalSeconds).ToString();
        await context.Response.WriteAsJsonAsync(OpenAiMapper.Error(exception), Json);
    }

    private static GatewayOptions SamplingOptions(HttpContext context) =>
        context.RequestServices.GetService<GatewayOptions>() ?? new GatewayOptions();

    private static bool IsAllowedOrigin(HttpContext context, GatewayServerOptions options)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return origin.Length > 0 && options.AllowedOrigins.Any(allowed =>
            string.Equals(allowed.Trim(), origin, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    private static bool IsLocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    private static GatewayException PublicError(GatewayException exception, GatewayServerOptions options) =>
        !options.SanitizeErrors || IsClientSafe(exception.Kind)
            ? exception
            : new GatewayException(exception.Kind, "Запрос отклонён. Проверьте доступность маршрута и локальный журнал приложения.") { RetryAt = exception.RetryAt };

    /// <summary>Validation and routing messages are written by the gateway. Upstream text can contain local paths.</summary>
    private static bool IsClientSafe(GatewayErrorKind kind) => kind is
        GatewayErrorKind.InvalidRequest
        or GatewayErrorKind.Unsupported
        or GatewayErrorKind.Unauthorized
        or GatewayErrorKind.NotFound
        or GatewayErrorKind.ModelNotFound
        or GatewayErrorKind.ProviderUnavailable;

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
