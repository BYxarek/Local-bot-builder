using NativeBot.Core;

namespace NativeBot.Telegram;

public sealed record TelegramRequirement(
    string Action,
    IReadOnlySet<string> ChatTypes,
    IReadOnlySet<string> Rights,
    bool RequiresPrivacyDisabled = false);

public static class TelegramPolicy
{
    public static TimeSpan MinimumSendInterval => TelegramLimits.MinimumSendInterval;

    public static IReadOnlyDictionary<string, TelegramRequirement> Requirements { get; } =
        new Dictionary<string, TelegramRequirement>(StringComparer.OrdinalIgnoreCase)
        {
            ["sendText"] = new("Отправить сообщение", new HashSet<string> { "private", "group", "supergroup", "channel" }, new HashSet<string>()),
            ["sendAsset"] = new("Отправить вложение", new HashSet<string> { "private", "group", "supergroup", "channel" }, new HashSet<string>()),
            ["deleteMessage"] = new("Удалить сообщение", new HashSet<string> { "group", "supergroup", "channel" }, new HashSet<string> { "can_delete_messages" }),
            ["groupText"] = new("Читать сообщения группы", new HashSet<string> { "group", "supergroup" }, new HashSet<string>(), true)
        };

    public static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempt, 8))));
}

public static class TelegramLimits
{
    public const int MaxTextLength = 4096;
    public const int MaxCaptionLength = 1024;
    public const int MaxCallbackDataBytes = 64;
    public const int MinMediaGroupItems = 2;
    public const int MaxMediaGroupItems = 10;
    public const long MaxDocumentBytes = 50 * 1024 * 1024;
    public static TimeSpan MinimumSendInterval { get; } = TimeSpan.FromMilliseconds(35);
}

public sealed record TelegramFailure(TelegramFailureKind Kind, string UserMessage, bool Retry, TimeSpan? RetryAfter = null);

public static class TelegramErrorClassifier
{
    public static TelegramFailure Classify(Exception exception) => exception switch
    {
        HttpRequestException => new(TelegramFailureKind.Offline, "Нет подключения к Telegram.", true),
        TaskCanceledException => new(TelegramFailureKind.Temporary, "Telegram не ответил вовремя.", true),
        TelegramException { ErrorCode: 429 } error => new(TelegramFailureKind.RateLimited, "Telegram временно ограничил скорость отправки.", true, error.RetryAfter),
        TelegramException { ErrorCode: >= 500 } => new(TelegramFailureKind.Temporary, "Telegram временно недоступен.", true),
        TelegramException { ErrorCode: 401 } => new(TelegramFailureKind.Unauthorized, "Токен бота недействителен.", false),
        TelegramException { ErrorCode: 403 } => new(TelegramFailureKind.Forbidden, "Боту не хватает прав или пользователь заблокировал бота.", false),
        TelegramException { ErrorCode: 400, Description: var text } when text.Contains("chat not found", StringComparison.OrdinalIgnoreCase) => new(TelegramFailureKind.UnavailableChat, "Чат недоступен.", false),
        TelegramException { ErrorCode: 400 } => new(TelegramFailureKind.InvalidRequest, "Параметры действия не приняты Telegram.", false),
        _ => new(TelegramFailureKind.Unknown, "Не удалось выполнить действие.", false)
    };
}
