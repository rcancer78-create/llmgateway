using System.IO;
using LLMGateway.Core;

namespace LLMGateway.ChatApp;

internal static class GatewayUiDiagnostics
{
    // Native/HTTP exception messages may contain credentials, arguments and private paths.
    public static string Describe(Exception error) => error switch
    {
        GatewayException { Kind: GatewayErrorKind.AuthenticationRequired or GatewayErrorKind.Unauthorized } =>
            "Требуется авторизация. Проверьте выбранный аккаунт и ключ подключения.",
        GatewayException { Kind: GatewayErrorKind.RateLimited } => "Достигнут лимит аккаунта. Проверьте время сброса квоты.",
        GatewayException { Kind: GatewayErrorKind.InvalidRequest } => "Проверьте параметры запроса и настройки профиля.",
        GatewayException { Kind: GatewayErrorKind.ModelNotFound or GatewayErrorKind.NotFound } => "Выбранная модель или аккаунт недоступны. Обновите каталог.",
        GatewayException { Kind: GatewayErrorKind.Unsupported } => "Операция не поддерживается выбранным клиентом.",
        GatewayException { Kind: GatewayErrorKind.Timeout } or TimeoutException => "Время ожидания истекло.",
        OperationCanceledException => "Операция отменена.",
        _ => "Операция не выполнена. Проверьте подключение и настройки клиента."
    };

    // Callers pass fixed lifecycle event codes only, never exception/provider payloads.
    public static void Record(string eventCode)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLMGateway", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "app_lifecycle.log"), $"{DateTimeOffset.UtcNow:O} {eventCode}{Environment.NewLine}");
        }
        catch (Exception) { /* Diagnostic storage must not break startup, shutdown or error handling. */ }
    }
}
