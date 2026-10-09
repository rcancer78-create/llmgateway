# LLMGateway: техническая справка

Модуль на C#/.NET 8 для LLMWorkGUI. Он даёт единый доступ к локально установленным нативным клиентам **Codex, Cursor, Antigravity, Grok** и готовый адаптер для **Claude Code**. Возможности:

- несколько аккаунтов (Codex и Claude изолированно, Antigravity через профили с API-ключом), явный выбор аккаунта;
- текущие квоты/лимиты и время до сброса там, где нативный клиент их публикует (Codex, Grok);
- OpenAI-совместимый текстовый API (`/v1/models`, `/v1/chat/completions` со стримингом и tools, `/v1/completions`) с расширениями для аккаунтов и квот; неподдерживаемые нативными CLI поверхности отвечают ошибкой в формате OpenAI;
- два режима работы: вызов библиотеки (`ILlmGateway`) и HTTP OpenAI API (тот же `ILlmGateway` через `OpenAiGatewayClient`);
- тестовое WPF-приложение-чат, в котором проверяются все функции и настройки.

[← README](../README.md). Команды выполняются из корня репозитория.

## Структура

| Проект | Назначение |
|---|---|
| `src/LLMGateway.Core` | Контракты (`ILlmGateway`, `IProviderAdapter`, `IAccountStore`), маршрутизация моделей, сборка промпта, эмуляция tool calling, stop-последовательности, кэш квот/моделей, хранилище аккаунтов, DTO и маппер OpenAI, HTTP-клиент `OpenAiGatewayClient`. Без зависимостей от ASP.NET. |
| `src/LLMGateway.Native` | Адаптеры нативных клиентов, поиск исполняемых файлов, безопасный запуск процессов, JSON-RPC по stdio, регистрация в DI (`AddLlmGateway`) и фабрика `NativeGateway.Create`. |
| `src/LLMGateway.Server` | OpenAI-совместимые эндпоинты (`MapLlmGateway`) и `EmbeddedGatewayServer` для встраивания HTTP API в любой процесс. |
| `src/LLMGateway.Api` | Самостоятельный HTTP-хост (`dotnet run`). |
| `src/LLMGateway.ChatApp` | WPF-чат: режимы «Библиотека» и «OpenAI API», встроенный сервер, аккаунты, модели, квоты, журнал. |
| `tests/LLMGateway.Tests` | Тесты: парсеры на реальных образцах вывода CLI, маршрутизация, промпт, tools, хранилище, маппер, сквозные HTTP-тесты с фейковым адаптером. |

## Запуск

```powershell
dotnet build LLMGateway.sln
dotnet test tests/LLMGateway.Tests
dotnet run --project src/LLMGateway.ChatApp          # тестовый чат
dotnet run --project src/LLMGateway.Api              # HTTP API на http://127.0.0.1:5157/v1
```

В чате по умолчанию выбран режим «Библиотека». Чтобы проверить HTTP: нажмите «Запустить» (встроенный сервер, по умолчанию `127.0.0.1:5158`), выберите «OpenAI API», затем «Подключить». Чат также работает с любым сторонним OpenAI-совместимым сервером; расширения `/v1/gateway/*` в этом случае просто недоступны.

## Нативные клиенты

Все вызовы идут только через публичные CLI и RPC-интерфейсы самих клиентов. Процессы запускаются напрямую через `ProcessStartInfo.ArgumentList`, без shell (npm-шимы `.cmd`/`.ps1` разворачиваются в прямой вызов `node.exe`). Агенты работают в режиме «только чтение» в пустом каталоге `%LOCALAPPDATA%\LLMGateway\workspace`.

| Клиент | Чат | Аккаунт и статус | Квоты | Модели | Несколько аккаунтов |
|---|---|---|---|---|---|
| Codex (`codex`) | `exec --json --ephemeral --sandbox read-only`, промпт через stdin | `app-server` → `account/read` (стабильное рукопожатие) | `account/rateLimits/read` только при чтении квот (`experimentalApi`): окна, `resetsAt`, кредиты. Если метод исчез, квота помечается неподдерживаемой | `app-server` → `model/list` (стабильное рукопожатие) | Да, у каждого аккаунта свой `CODEX_HOME`. Профили `~/.codex*` находятся автоматически |
| Cursor (`cursor-agent`) | `--print --mode ask --output-format stream-json --stream-partial-output`, промпт через stdin | `status --format json`, `about --format json` | Вне интерактивного `/usage` не публикуются, показывается тариф | `--list-models` | Один вход на машину; `CURSOR_API_KEY` через профиль с API-ключом |
| Antigravity (`agy`) | `--input-format stream-json --output-format stream-json --mode plan`, промпт одной JSON-строкой в stdin | `agy models` | Не публикуются; исчерпание лимита фиксируется по ответам | `agy models` | Один Google-вход на машину. Дополнительные аккаунты — отдельные домашние каталоги с `GEMINI_API_KEY` (общий `~/.gemini` не меняется) |
| Grok (`grok`) | `--prompt-file … --output-format streaming-json --permission-mode plan --max-turns 1` | `grok models` | `grok agent stdio` (ACP): сначала `_x.ai/billing`, затем `x.ai/billing`. Если сборка метод убрала, квота помечается неподдерживаемой | `grok models` | Один вход на машину |
| Claude Code (`claude`) | `-p --output-format stream-json --include-partial-messages --max-turns 1`, промпт через stdin | `--version` | Не публикуются | Алиасы `sonnet`, `opus`, `haiku` | Да, через `CLAUDE_CONFIG_DIR`. Адаптер готов; доступность зависит от локальной установки |

Если CLI не установлен, провайдер помечается `not_installed` и не попадает в `/v1/models`.

Опрос моделей и квот ограничен четырьмя одновременными операциями на экземпляр шлюза, в том числе для пересекающихся HTTP-запросов. Ошибка обнаружения моделей записывается в журнал. Для совместимости каталог при этом использует прежний кэш, заданную `DefaultModel` или маршрут `auto`: HTTP 200 и наличие такого маршрута не подтверждают доступность модели, вход или готовность аккаунта. Проверяйте `/v1/gateway/accounts/{id}/check` и результат самого запроса. При поиске отсутствующей модели без префикса шлюз обновляет каталог, но не чаще чем раз в `CatalogRefreshCooldownSeconds` (по умолчанию 30 секунд); параллельные запросы ждут одно общее обновление. Ожидание свободного слота аккаунта ограничено `SlotWaitSeconds` (по умолчанию 60 секунд) и отвечает 503 с `Retry-After`.

## Правила пользования

- Шлюз не читает, не копирует и не переносит файлы с учётными данными. Проверяется только существование маркеров вроде `auth.json`, чтобы найти профили. Вход и обновление токенов выполняет сам нативный клиент: кнопка «Войти» открывает его штатный вход в отдельной консоли.
- Шлюз не обращается к приватным HTTP-эндпоинтам провайдеров. Квоты читаются методами, которые клиент предоставляет сам (Codex app-server, ACP-расширение Grok, которое использует собственная команда `/usage`).
- Аккаунты не ротируются автоматически и лимиты не обходятся. Аккаунт выбирается явно (`select`, `account_id`, `X-LLM-Account` или id модели). При исчерпании лимита возвращается `429` с `Retry-After`, аккаунт помечается `rate_limited`.
- Флаги, отключающие проверки разрешений (`--dangerously-skip-permissions`, `--yolo`, `--force`, опасные значения `--sandbox` и `--permission-mode`), не используются и отклоняются, даже если их записать в дополнительные аргументы профиля. Агентам даётся режим только для чтения или режим ask/plan.
- API-ключи не хранятся в `accounts.json`. В профиле указывается только **имя** переменной окружения; её значение передаётся процессу клиента в документированной переменной (`CODEX_API_KEY`, `GEMINI_API_KEY`, `CURSOR_API_KEY`, `ANTHROPIC_API_KEY`). Поле `environment` принимает только настройки без секретов: имена и значения, похожие на ключ, отклоняются при записи и удаляются при загрузке файла.
- Профиль с `NativeLogin` не наследует ключи хоста (`ANTHROPIC_API_KEY`, `CODEX_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`, `CURSOR_API_KEY` и имена `api_key_variable` других профилей). Процессу остаётся только переменная, которую профиль задаёт явно в режиме API-ключа.
- Запуски Codex передают пустой `mcp_servers`, Claude — `--strict-mcp-config` и `--tools ""`, Cursor не получает `--trust`. Так конфигурация MCP пользователя не подключается к запросу шлюза. Флаги `--mcp-config`, `--plugin-dir` и `--append-system-prompt-file` в дополнительных аргументах отклоняются.
- Ключ управления API — это ключ к машине шлюза. Через `PUT /v1/gateway/accounts` можно задать `executable` и переменные окружения вроде `PATH`, `COMSPEC` или `NODE_OPTIONS`. Для loopback с ключом это сознательно разрешено: владелец ключа может запустить код на хосте. Не публикуйте порт и ключ в сеть.

## OpenAI-совместимый API

`LLMGateway:Server:ApiKey` обязателен для всех маршрутов `/v1`, включая loopback. Нужен заголовок `Authorization: Bearer <key>` или `x-api-key`; пустой настроенный ключ запрещает доступ к `/v1` даже с loopback. `GET /health` ключ не требует и только сообщает, что процесс отвечает. Запросы с `Origin` или `Sec-Fetch-*` отклоняются, пока origin нет в `GatewayServerOptions.AllowedOrigins` (по умолчанию список пуст). Недопустимый Host также проверяется. Встроенный сервер ChatApp создаёт случайный ключ для текущего запуска и передаёт его своему HTTP-клиенту; ключ не записывается в журнал. Для отдельного API задайте ключ через конфигурацию хоста, не через аргументы командной строки. Ошибки возвращаются в формате OpenAI (`error.type`, `error.code`) с корректными HTTP-статусами: 400, 401, 404, 429 с `Retry-After`, 502, 503, 504.

`temperature`, `top_p`, `presence_penalty`, `frequency_penalty` и `seed` нативные клиенты не применяют. По умолчанию (`UnsupportedSamplingParameters = Ignore`) шлюз их игнорирует и пишет об этом в журнал, чтобы Cline, Roo Code и Continue не получали 400. Режим `Reject` сохраняет прежний строгий ответ.

| Метод | Путь | Описание |
|---|---|---|
| GET | `/v1/models?refresh=&account_id=` | Модели всех включённых установленных аккаунтов. Id имеет вид `provider/account/model`; дополнительные поля `provider`, `account_id`, `native_model`, `display_name`, `is_default` |
| GET | `/v1/models/{id}` | Одна модель |
| POST | `/v1/chat/completions` | `messages` (строка или массив text-частей; роли system, developer, user, assistant, tool), `stream` + `stream_options.include_usage`, `tools`/`tool_choice`, `response_format` (`json_object`), `stop`, `reasoning_effort`, `max_completion_tokens` |
| POST | `/v1/completions` | Устаревший text completion |
| GET | `/v1/gateway/providers` | Установленные клиенты, путь, возможности (квоты, мультиаккаунт, лимит промпта) |
| GET/POST | `/v1/gateway/accounts` | Список и добавление профилей |
| PUT/DELETE | `/v1/gateway/accounts/{id}` | Изменение и удаление профиля (файлы клиента не трогаются) |
| POST | `/v1/gateway/accounts/{id}/select` | Сделать аккаунт активным для своего провайдера |
| POST | `/v1/gateway/accounts/{id}/check` | Проверить вход через нативный клиент (email, тариф, версия) |
| POST | `/v1/gateway/accounts/{id}/login` | Открыть штатный вход клиента (только с loopback) |
| GET | `/v1/gateway/quotas?refresh=&account_id=` | Квоты: `buckets[]` с `used_percent`, `remaining_percent`, `used`/`limit`/`unit`, `window_minutes`, `resets_at`, `reset_in`; также `plan`, `availability`, `is_stale` |

`response_format: json_object` буферизует ответ и проверяет, что он является корректным JSON-объектом. Если ответ обрезан по `max_tokens`, проверка не выполняется и `finish_reason` равен `length`, как в OpenAI. `json_schema` не поддерживается: корректно оформленный запрос получает код `unsupported` до вызова нативного клиента, повреждённое описание схемы — `invalid_request`. Возможности провайдера явно сообщают `supports_json_schema: false`. Stop-последовательность в начале ответа даёт пустой `content` и `finish_reason=stop`. При включённых tools stop применяется к тексту после извлечения блока `<tool_calls>`.

Аккаунт выбирается так (по убыванию приоритета): поле `account_id` в теле, заголовок `X-LLM-Account`, часть id модели (`codex/codex-work/gpt-5.5`), активный аккаунт провайдера (`codex/gpt-5.5`, `codex`), поиск модели без префикса по кэшу или `auto` (провайдер по умолчанию). В ответе есть поле `x_gateway` с `provider`, `account_id` и `native_model`.

```json
POST /v1/chat/completions
{
  "model": "codex/codex-work/gpt-5.5",
  "stream": true,
  "messages": [{"role": "user", "content": [{"type": "text", "text": "Привет"}]}]
}
```

Стриминг настоящий: дельты от CLI уходят клиенту сразу, пока соединение простаивает, отправляются комментарии `: keep-alive`. Ошибки маршрутизации возвращаются до начала потока с обычным HTTP-статусом; ошибки во время потока приходят отдельным SSE-чанком `error`, после которого поток завершается без `[DONE]`. `[DONE]` подтверждает успешное завершение.

Нативные CLI не принимают пользовательские функции, поэтому tool calling эмулируется: описания функций добавляются в промпт, ответ в блоке `<tool_calls>[…]</tool_calls>` превращается в стандартные `tool_calls`, а `finish_reason` становится `tool_calls`. Обрабатываются все полные блоки вызовов; неизвестное имя или повреждённый блок отклоняет ответ целиком. `tool_choice=required` и выбор конкретной функции проверяются: отсутствие нужного вызова возвращает `upstream_error`, а выбор необъявленной функции — `invalid_request`. Изображения, аудио, файлы, `logprobs` и `n > 1` отклоняются с кодом `unsupported`. Отдельные маршруты `/v1/embeddings`, `/v1/images/generations` и `/v1/audio/*` отвечают той же ошибкой: локальные клиенты их не умеют. `max_tokens` и `max_completion_tokens` обрезают уже полученный текст (около 4 кодовых единиц UTF-16 на токен) и ставят `finish_reason=length`; сам CLI остановить на границе токена нельзя.

### Ограничения и жизненный цикл

- Оценка `usage` без отчёта CLI и лимит возвращаемого текста используют приближение 4 кодовые единицы UTF-16 на токен, а не токенизатор модели. Для кириллицы и других языков ошибка может быть значительной; это не ограничение стоимости или реально сгенерированных токенов. Reasoning-дельты не входят в этот текстовый бюджет и учитываются в usage только при наличии отчёта клиента.
- SSE заканчивается `[DONE]` только после подтверждённого завершения. Ошибка в ходе ответа отправляется отдельным JSON error-фреймом без `[DONE]`; неожиданные исключения не раскрывают свои сообщения клиенту. `OpenAiGatewayClient` отклоняет поток без `[DONE]` или `finish_reason`. Настроенный HTTP-таймаут ограничивает получение заголовков и чтение тела неуспешного HTTP-ответа; после успешных заголовков чтение SSE управляется токеном отмены вызывающего кода. Некорректные SSE root/error приводят к типизированной ошибке Upstream без сырого повреждённого содержимого.
- `Dispose` шлюза запрещает новые чат-запросы, а принятые запросы и ожидающие слота завершаются без уничтожения используемого семафора. После удаления аккаунта его слот освобождается, когда завершатся владельцы и ожидающие. Настройки параллелизма задаются до создания шлюза; для изменения уже созданных слотов пересоздайте экземпляр.
- Короткие команды статуса, моделей и версии отклоняют stdout больше 4 Mi кодовых единиц UTF-16. Это лимит буферизации вывода, не токенов ответа.
- Поддерживаемая среда этого решения — Windows и .NET 8; WPF и поиск Windows CLI-обёрток не являются кроссплатформенными. Core можно использовать отдельно.
- Проверка loopback основана на адресе непосредственного соединения и не доверяет `X-Forwarded-For`. При доступе через reverse proxy обязательно настройте API-ключ и ограничение доступа на прокси.
- История передаётся CLI как один текстовый транскрипт. Маркеры ролей и инструкции внутри пользовательского текста не образуют защищённой границы доверия. Режимы разрешений CLI остаются отдельной защитой.
- `JsonAccountStore.Load` выполняет синхронную первоначальную загрузку, миграцию и, если включено `DiscoverProfiles`, поиск профилей. Вызывайте его при запуске, а не для каждого запроса.

## Аккаунты

Профили хранятся в `%LOCALAPPDATA%\LLMGateway\accounts.json` (формат v2 `{version, accounts}`). Старый формат-массив мигрирует с сохранением id, запись атомарная. Повреждённый файл не перезаписывается: рядом сохраняется копия `.corrupt-<время>` (если копирование не удалось, это только попадает в журнал), хранилище открывается в памяти только для чтения, а предупреждение видно в интерфейсе чата и в `GET /v1/gateway/accounts` (`warning`, `read_only`). У каждого провайдера всегда есть `<provider>-default` и ровно один активный аккаунт, когда файл читается успешно. Лимит без времени сброса держится 15 минут, а успешный запрос, принятый уже после фиксации лимита, снимает его.

```json
{ "id": "codex-work", "display_name": "Codex Work", "provider": "codex",
  "config_directory": "C:\\Users\\me\\.codex_work" }
{ "id": "agy-team", "display_name": "Gemini team key", "provider": "antigravity",
  "auth_mode": "api_key_from_environment", "api_key_variable": "AGY_TEAM_KEY" }
```

Эти же поля принимает `POST /v1/gateway/accounts`.

Для Codex и Claude новый профиль получает собственный каталог конфигурации; «Войти» запускает штатный вход в этом каталоге. Профиль Antigravity с API-ключом получает свой домашний каталог (`ConfigDirectory` или `%LOCALAPPDATA%\LLMGateway\antigravity\<id>`). В нём шлюз записывает `modelProvider=gemini`, как требует документация agy, и выставляет `USERPROFILE`/`HOME` только этому процессу. Общий `~/.gemini` и системное хранилище ключей не используются для второго аккаунта: у CLI по-прежнему один Google-вход на машину.

## Интеграция в LLMWorkGUI

Примеры ниже используют случайный ключ из переменной окружения; сначала создайте его по инструкции в README. Не используйте общий пример ключа для нескольких установок.

LLMWorkGUI — это WPF на net8.0-windows с Microsoft.Extensions DI 8.0. Модуль использует те же версии пакетов.

```csharp
// Режим библиотеки
services.AddLlmGateway(o => o.DefaultProvider = ProviderKind.Codex);
var gateway = provider.GetRequiredService<ILlmGateway>();
await foreach (var update in gateway.StreamAsync(new ChatRequest { Model = "codex", Messages = [ChatMessage.User("Привет")] }))
    if (update.Kind == ChatUpdateKind.TextDelta) Console.Write(update.Text);

var quotas = await gateway.GetQuotasAsync(refresh: true);

// Тот же ILlmGateway поверх HTTP (отдельный процесс или удалённый хост)
ILlmGateway remote = new OpenAiGatewayClient(new Uri("http://127.0.0.1:5157/"), apiKey: Environment.GetEnvironmentVariable("LLMGateway__Server__ApiKey"));

// OpenAI API внутри процесса GUI для внешних инструментов (Cline, Roo Code, скрипты)
await using var server = await EmbeddedGatewayServer.StartAsync(
    gateway, "http://127.0.0.1:5157", new GatewayServerOptions { ApiKey = Environment.GetEnvironmentVariable("LLMGateway__Server__ApiKey") });
```

Бэкенды LLMWorkGUI (`Backends.Abstractions`) можно реализовать как тонкие обёртки над `ILlmGateway`: модели берутся из `GetModelsAsync`, ответы из `StreamAsync`, а квоты из `GetQuotasAsync` (обратный отсчёт считается по `QuotaBucket.ResetIn` на момент чтения).

## Исследование

Перед реализацией изучены готовые решения. Их подходы использованы как ориентир; код не копировался и не встраивался.

- [Cline](https://github.com/cline/cline) и [Roo Code](https://github.com/RooCodeInc/Roo-Code): провайдеры CLI-агентов (Claude Code, Codex), разделение настроек провайдера и транспорта, передача истории диалога одним промптом, требования OpenAI-совместимых клиентов к SSE и `tool_calls`.
- [Codex app-server](https://developers.openai.com/codex/app-server): `account/read` и `model/list` идут по стабильному рукопожатию. `account/rateLimits/read` включается только на чтении квот (`experimentalApi`), и если сборка метод убрала, квота помечается неподдерживаемой. Изоляция — `CODEX_HOME`.
- [Cursor CLI: headless](https://cursor.com/docs/cli/headless), [формат вывода](https://cursor.com/docs/cli/reference/output-format), [параметры](https://cursor.com/docs/cli/reference/parameters) и [разбор `--stream-partial-output`](https://forum.cursor.com/t/stream-partial-output-assistant-events-have-multiple-undocumented-forms-how-should-consumers-parse-them/156289): дельты помечены `timestamp_ms`, копии с `model_call_id` пропускаются.
- Grok Build: `_x.ai/billing` через `grok agent stdio`. Эту же функцию использует собственная команда `/usage`. Разбор ответа сверен с [hel `grok_usage.rs`](https://docs.rs/crate/hel-core/latest/source/src/grok_usage.rs) и [waku `usage.rs`](https://github.com/egoist/waku/blob/6d433e87/src/usage.rs).
- Мультиаккаунт Antigravity: [agy-switcher](https://github.com/tranquocthong/agy-switcher), [agys](https://github.com/first087/agy-switch), [agm](https://github.com/shyim/agm), [agy-cli-manager](https://github.com/zcop/agy-cli-manager), [acm](https://github.com/hiri-dev/acm). Все они подменяют OAuth-токены в хранилище ключей или файлах `~/.gemini`, а многие автоматически ротируют аккаунты при исчерпании квоты. Этот подход **намеренно не повторён**: он требует работы с чужими учётными данными и обходит лимиты. Вместо него используются профили с документированным `GEMINI_API_KEY` и штатный перевход.
- [CodexSwitch](https://github.com/qiyadeng/CodexSwitch) и [Quota](https://github.com/pinkpixel-dev/quota): именованные профили, отображение «последний известный снимок квоты» и время сброса.
- [openai-dotnet](https://github.com/openai/openai-dotnet): форма Chat Completions и SSE, по ней сверена совместимость.

Опциональная проверка реальных клиентов (не часть публикационной офлайн-проверки) описана в [docs/REAL_PROVIDER_TESTS.md](REAL_PROVIDER_TESTS.md).
