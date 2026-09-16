using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace NativeBot.Telegram;

public sealed record TelegramBotInfo(long Id, string FirstName, string Username, bool? CanReadAllGroupMessages = null,
    bool? SupportsInlineQueries = null, bool? SupportsGuestQueries = null, bool? CanManageBots = null);
public sealed record TelegramWebhookInfo(string Url, int PendingUpdateCount);
public sealed record TelegramUser(long Id, bool IsBot, string FirstName, string? LastName, string? Username, string? LanguageCode);
public sealed record TelegramChat(long Id, string Type);
public sealed record TelegramMessage(
    long MessageId,
    TelegramUser? From,
    TelegramChat Chat,
    string? Text,
    long? MessageThreadId = null,
    string? BusinessConnectionId = null,
    TelegramUser? GuestBotCallerUser = null,
    TelegramChat? GuestBotCallerChat = null,
    long? GuestQueryId = null,
    TelegramUser? ReceiverUser = null,
    string? EphemeralMessageId = null);

public sealed class TelegramUpdate
{
    public long UpdateId { get; init; }
    public TelegramMessage? Message { get; init; }
    public TelegramMessage? EditedMessage { get; init; }
    public TelegramMessage? ChannelPost { get; init; }
    public TelegramMessage? EditedChannelPost { get; init; }
    public TelegramMessage? BusinessMessage { get; init; }
    public TelegramMessage? EditedBusinessMessage { get; init; }
    public TelegramMessage? GuestMessage { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Data { get; init; } = [];

    public TelegramMessage? MessageFor(string updateType) => updateType switch
    {
        "message" => Message,
        "edited_message" => EditedMessage,
        "channel_post" => ChannelPost,
        "edited_channel_post" => EditedChannelPost,
        "business_message" => BusinessMessage,
        "edited_business_message" => EditedBusinessMessage,
        "guest_message" => GuestMessage,
        _ => null
    };
}

internal sealed record TelegramResponseParameters(int? RetryAfter, long? MigrateToChatId);
internal sealed record TelegramEnvelope<T>(bool Ok, T? Result, string? Description, int? ErrorCode, TelegramResponseParameters? Parameters);

public sealed class TelegramClient(HttpClient httpClient, string token)
{
    private readonly Uri _baseUri = new($"https://api.telegram.org/bot{ValidateToken(token)}/");
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<TelegramBotInfo> GetMeAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<TelegramBotInfo>("getMe", cancellationToken);

    public async Task<TelegramWebhookInfo> GetWebhookInfoAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<TelegramWebhookInfo>("getWebhookInfo", cancellationToken);

    public async Task SetWebhookAsync(Uri url, string secret, IEnumerable<string>? allowedUpdates = null, CancellationToken cancellationToken = default)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Webhook должен использовать публичный HTTPS-адрес.", nameof(url));
        if (secret.Length is < 1 or > 256 || secret.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '_' and not '-'))
            throw new ArgumentException("Секрет webhook содержит недопустимые символы.", nameof(secret));
        await SendAsync<bool>("setWebhook", new
        {
            url = url.AbsoluteUri,
            secret_token = secret,
            allowed_updates = allowedUpdates?.Distinct(StringComparer.Ordinal).ToArray(),
            drop_pending_updates = false
        }, cancellationToken);
    }

    public async Task DeleteWebhookAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<bool>("deleteWebhook", new { drop_pending_updates = false }, cancellationToken);

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long? offset = null,
        int timeoutSeconds = 30,
        IEnumerable<string>? allowedUpdates = null,
        CancellationToken cancellationToken = default)
    {
        if (timeoutSeconds is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        var parameters = new JsonObject { ["timeout"] = timeoutSeconds };
        if (offset is { } value) parameters["offset"] = value;
        if (allowedUpdates is not null) parameters["allowed_updates"] = new JsonArray(allowedUpdates.Distinct(StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        return await SendAsync<TelegramUpdate[]>("getUpdates", parameters, cancellationToken);
    }

    public async Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default)
    {
        if (text.Length is 0 or > TelegramLimits.MaxTextLength) throw new ArgumentOutOfRangeException(nameof(text), $"Сообщение должно содержать от 1 до {TelegramLimits.MaxTextLength} символов.");
        await SendAsync<JsonElement>("sendMessage", new { chat_id = chatId, text }, cancellationToken);
    }

    public Task<JsonElement> ExecuteAsync(TelegramFeature feature, JsonObject parameters, CancellationToken cancellationToken = default)
    {
        if (feature.Kind != TelegramFeatureKind.Action || feature.Method is null)
            throw new ArgumentException("Выбрана не Telegram-команда.", nameof(feature));
        return SendAsync<JsonElement>(feature.Method, parameters, cancellationToken);
    }

    private Task<T> GetAsync<T>(string method, CancellationToken cancellationToken) => SendAsync<T>(method, null, cancellationToken);

    private async Task<T> SendAsync<T>(string method, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(_baseUri, method));
        if (body is not null) request.Content = JsonContent.Create(body, options: _json);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<T>>(_json, cancellationToken)
            ?? throw new TelegramException("Telegram вернул пустой ответ.", (int)response.StatusCode);
        if (!response.IsSuccessStatusCode || !envelope.Ok || envelope.Result is null)
            throw new TelegramException(FriendlyMessage(response.StatusCode, envelope.Description), envelope.ErrorCode ?? (int)response.StatusCode,
                envelope.Description, envelope.Parameters?.RetryAfter is { } seconds ? TimeSpan.FromSeconds(seconds) : null, envelope.Parameters?.MigrateToChatId);
        return envelope.Result;
    }

    private static string ValidateToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d+:[A-Za-z0-9_-]{20,}$"))
            throw new ArgumentException("Токен BotFather имеет неверный формат.", nameof(value));
        return value;
    }

    private static string FriendlyMessage(HttpStatusCode status, string? description) => status switch
    {
        HttpStatusCode.Unauthorized => "Telegram отклонил токен бота.",
        HttpStatusCode.TooManyRequests => "Telegram временно ограничил скорость запросов.",
        _ when description?.Contains("chat not found", StringComparison.OrdinalIgnoreCase) == true => "Не удалось найти указанный чат.",
        _ => "Не удалось выполнить запрос к Telegram."
    };
}

public static class TelegramUpdateReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static async Task<TelegramUpdate> ReadAsync(Stream body, CancellationToken cancellationToken = default)
    {
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var update = document.RootElement.Deserialize<TelegramUpdate>(Json)
            ?? throw new JsonException("Telegram Update не содержит данных.");
        foreach (var property in document.RootElement.EnumerateObject())
            update.Data[property.Name] = property.Value.Clone();
        return update;
    }

    public static string Serialize(TelegramUpdate update) => JsonSerializer.Serialize(update, Json);
}

public static class TelegramWebhookSecurity
{
    public static bool IsValid(string? provided, string expected)
    {
        if (provided is null) return false;
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

public sealed class TelegramException(string message, int errorCode, string? description = null, TimeSpan? retryAfter = null, long? migrateToChatId = null) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
    public string Description { get; } = description ?? message;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public long? MigrateToChatId { get; } = migrateToChatId;
}
