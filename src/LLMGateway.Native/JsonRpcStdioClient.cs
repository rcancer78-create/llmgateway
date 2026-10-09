using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMGateway.Core;

namespace LLMGateway.Native;

/// <summary>Newline-delimited JSON-RPC 2.0 over a native client's stdio (codex app-server, ACP agents).</summary>
public sealed class JsonRpcStdioClient : IAsyncDisposable
{
    private readonly NativeProcess _process;
    private readonly bool _codexAppServer;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly CancellationToken _stopToken;
    private readonly TaskCompletionSource _writesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeWrites;
    private bool _disposed;
    private Task? _disposal;
    private int _nextId;

    private JsonRpcStdioClient(NativeProcess process, bool codexAppServer = false)
    {
        _process = process;
        _codexAppServer = codexAppServer;
        _stopToken = _stop.Token;
        _reader = ReadLoopAsync();
    }

    public static JsonRpcStdioClient Start(NativeLaunch launch) => new(NativeProcess.Start(launch, keepInputOpen: true));
    internal static JsonRpcStdioClient StartCodexAppServer(NativeLaunch launch) =>
        new(NativeProcess.Start(launch, keepInputOpen: true), codexAppServer: true);

    public async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters is null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters)
            }, deadline.Token).ConfigureAwait(false);
            if (_reader.IsCompleted && !completion.Task.IsCompleted) throw Closed();
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new GatewayException(GatewayErrorKind.Timeout, $"Нативный клиент не ответил на '{method}' за {timeout.TotalSeconds:0} с. {NativeErrorClassifier.Trim(_process.StandardError, 300)}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters is null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters)
        }, cancellationToken);

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (_codexAppServer) message.Remove("jsonrpc");
        lock (_lifetimeGate)
        {
            if (_disposed) throw Closed();
            _activeWrites++;
        }
        var acquired = false;
        try
        {
            using var writeStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopToken);
            await _writeGate.WaitAsync(writeStop.Token).ConfigureAwait(false);
            acquired = true;
            await _process.Input.WriteAsync((message.ToJsonString() + "\n").AsMemory(), writeStop.Token).ConfigureAwait(false);
            await _process.Input.FlushAsync(writeStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw Closed();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException || ex is InvalidOperationException && _stopToken.IsCancellationRequested)
        {
            throw new GatewayException(GatewayErrorKind.Upstream, $"Нативный клиент закрыл канал: {NativeErrorClassifier.Trim(_process.StandardError, 300)}", ex);
        }
        finally
        {
            if (acquired) _writeGate.Release();
            lock (_lifetimeGate)
                if (--_activeWrites == 0 && _disposed) _writesDrained.TrySetResult();
        }
    }

    private async Task ReadLoopAsync()
    {
        var failure = new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент завершился.");
        try
        {
            await foreach (var line in _process.ReadLinesAsync(_stopToken).ConfigureAwait(false))
            {
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException)
                {
                    throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент вернул некорректный JSON-RPC JSON.");
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idElement)) continue;
                    var hasVersion = root.TryGetProperty("jsonrpc", out var version);
                    if ((!hasVersion && !_codexAppServer) || hasVersion
                        && (version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"))
                        throw new GatewayException(GatewayErrorKind.Upstream, "Клиент вернул несовместимую версию JSON-RPC.");
                    if (root.TryGetProperty("method", out var method))
                    {
                        if (method.ValueKind != JsonValueKind.String)
                            throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент вернул некорректный метод JSON-RPC.");
                        // Requests from the agent (permissions, fs access) are declined: the gateway offers no client capabilities.
                        await WriteAsync(new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = JsonNode.Parse(idElement.GetRawText()),
                            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Method '{method.GetString()}' is not supported by LLMGateway." }
                        }, _stopToken).ConfigureAwait(false);
                        continue;
                    }
                    if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out var id) || !_pending.TryGetValue(id, out var completion)) continue;
                    if (root.TryGetProperty("result", out _) == root.TryGetProperty("error", out _))
                        throw new GatewayException(GatewayErrorKind.Upstream, "Ответ JSON-RPC должен содержать ровно одно поле result или error.");
                    if (root.TryGetProperty("error", out var error))
                    {
                        if (error.ValueKind != JsonValueKind.Object
                            || !error.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number)
                            || !error.TryGetProperty("message", out var text) || text.ValueKind != JsonValueKind.String)
                            throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент вернул некорректную ошибку JSON-RPC.");
                        var message = text.GetString();
                        completion.TrySetException(new GatewayException(NativeErrorClassifier.Classify(message), message ?? "JSON-RPC error")
                        { JsonRpcErrorCode = number });
                    }
                    else
                    {
                        if (!root.TryGetProperty("result", out var result))
                            throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент вернул ответ JSON-RPC без результата.");
                        completion.TrySetResult(result.Clone());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or GatewayException
            or InvalidOperationException or JsonException)
        {
            failure = ex is GatewayException classified ? classified
                : new GatewayException(GatewayErrorKind.Upstream, "Канал нативного клиента завершился или нарушил протокол JSON-RPC.", ex);
        }
        finally
        {
            lock (_lifetimeGate)
                if (!_disposed) _stop.Cancel();
            foreach (var pending in _pending.Values) pending.TrySetException(failure);
        }
    }

    private static GatewayException Closed() => new(GatewayErrorKind.Upstream, "Канал нативного клиента закрыт.");

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposal is not null) return new ValueTask(_disposal);
            _disposed = true;
            if (_activeWrites == 0) _writesDrained.TrySetResult();
            return new ValueTask(_disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        try
        {
            try
            {
                // Termination closes the child's end of a blocked pipe. Join every canceled
                // RPC writer before disposing its shared StreamWriter.
                await _process.StopAsync().ConfigureAwait(false);
                await _writesDrained.Task.ConfigureAwait(false);
            }
            finally { await _process.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            await _writesDrained.Task.ConfigureAwait(false);
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            finally { _stop.Dispose(); _writeGate.Dispose(); }
        }
    }
}
