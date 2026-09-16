using System.Text.Json;
using System.Text.Json.Nodes;
using NativeBot.Core;

namespace NativeBot.Telegram;

public enum TelegramFeatureKind { Event, Action }

public sealed record TelegramFeature(
    string Id,
    string Name,
    TelegramFeatureKind Kind,
    string? UpdateType = null,
    string? Method = null,
    IReadOnlySet<string>? ChatTypes = null,
    IReadOnlySet<string>? Rights = null,
    string? MessageContentType = null);

public static class TelegramFeatures
{
    public const string BotApiVersion = "10.3";
    public const int MaxBotToBotDepth = 8;

    private static readonly string[] UpdateTypes =
    [
        "message", "edited_message", "channel_post", "edited_channel_post", "business_connection",
        "business_message", "edited_business_message", "deleted_business_messages", "guest_message",
        "message_reaction", "message_reaction_count", "inline_query", "chosen_inline_result", "callback_query",
        "shipping_query", "pre_checkout_query", "purchased_paid_media", "poll", "poll_answer", "my_chat_member",
        "chat_member", "chat_join_request", "chat_boost", "removed_chat_boost", "managed_bot", "subscription",
        "stopped_message_generation"
    ];

    public static IReadOnlyDictionary<string, TelegramFeature> All { get; } = Build();

    public static IReadOnlyList<string> AllowedUpdates(IEnumerable<FlowDefinition> flows)
    {
        var updates = flows.SelectMany(x => x.Nodes)
            .Where(x => x.Kind == NodeKind.Event)
            .Select(x => All.GetValueOrDefault(x.Handler)?.UpdateType ?? LegacyUpdateType(x.Handler))
            .Where(x => x is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return updates.Length == 0 ? ["message"] : updates;
    }

    public static bool IsKnownUpdate(string value) => UpdateTypes.Contains(value, StringComparer.Ordinal);

    private static Dictionary<string, TelegramFeature> Build()
    {
        var features = new Dictionary<string, TelegramFeature>(StringComparer.OrdinalIgnoreCase);
        foreach (var update in UpdateTypes)
            features[$"telegram.event.{update}"] = new($"telegram.event.{update}", EventName(update), TelegramFeatureKind.Event, update);

        AddMessageEvent(features, "text", "Получен текст");
        AddMessageEvent(features, "photo", "Получено фото");
        AddMessageEvent(features, "video", "Получено видео");
        AddMessageEvent(features, "animation", "Получена анимация");
        AddMessageEvent(features, "audio", "Получено аудио");
        AddMessageEvent(features, "document", "Получен документ");
        AddMessageEvent(features, "voice", "Получено голосовое сообщение");
        AddMessageEvent(features, "video_note", "Получено видеосообщение");
        AddMessageEvent(features, "location", "Получена геопозиция");
        AddMessageEvent(features, "venue", "Получено место");
        AddMessageEvent(features, "contact", "Получен контакт");
        AddMessageEvent(features, "dice", "Получен результат кубика");
        AddMessageEvent(features, "poll", "Получен опрос");
        AddMessageEvent(features, "sticker", "Получен стикер");
        AddMessageEvent(features, "game", "Получена игра");
        AddMessageEvent(features, "web_app_data", "Получены данные Mini App");
        AddMessageEvent(features, "successful_payment", "Оплата подтверждена");
        AddMessageEvent(features, "refunded_payment", "Платёж возвращён");
        AddMessageEvent(features, "paid_media", "Получен платный контент");
        AddMessageEvent(features, "rich_message", "Получен Rich Message");
        AddMessageEvent(features, "community_chat_added", "Чат добавлен в Community");
        AddMessageEvent(features, "community_chat_removed", "Чат удалён из Community");
        AddMessageEvent(features, "community_chat_joined", "Чат присоединился из Community");
        AddMessageEvent(features, "managed_bot_created", "Создан управляемый бот");

        AddAction(features, "sendText", "Отправить текст", "sendMessage", AllChats());
        AddAction(features, "sendPhoto", "Отправить фото", "sendPhoto", AllChats());
        AddAction(features, "sendVideo", "Отправить видео", "sendVideo", AllChats());
        AddAction(features, "sendAnimation", "Отправить анимацию", "sendAnimation", AllChats());
        AddAction(features, "sendAudio", "Отправить аудио", "sendAudio", AllChats());
        AddAction(features, "sendDocument", "Отправить документ", "sendDocument", AllChats());
        AddAction(features, "sendVoice", "Отправить голосовое сообщение", "sendVoice", AllChats());
        AddAction(features, "sendVideoNote", "Отправить видеосообщение", "sendVideoNote", AllChats());
        AddAction(features, "sendLocation", "Отправить геопозицию", "sendLocation", AllChats());
        AddAction(features, "sendVenue", "Отправить место", "sendVenue", AllChats());
        AddAction(features, "sendContact", "Отправить контакт", "sendContact", AllChats());
        AddAction(features, "sendDice", "Бросить кубик", "sendDice", AllChats());
        AddAction(features, "sendPoll", "Создать опрос", "sendPoll", AllChats());
        AddAction(features, "sendMediaGroup", "Отправить альбом", "sendMediaGroup", AllChats());
        AddAction(features, "sendSticker", "Отправить стикер", "sendSticker", AllChats());
        AddAction(features, "sendGame", "Отправить игру", "sendGame", Set("private", "group", "supergroup"));
        AddAction(features, "sendRichMessage", "Отправить Rich Message", "sendRichMessage", AllChats());
        AddAction(features, "sendMessageDraft", "Показать потоковый ответ", "sendMessageDraft", Set("private"));
        AddAction(features, "sendRichMessageDraft", "Показать потоковый Rich Message", "sendRichMessageDraft", Set("private"));
        AddAction(features, "editMessage", "Изменить сообщение", "editMessageText", AllChats());
        AddAction(features, "deleteMessage", "Удалить сообщение", "deleteMessage", AllChats());
        AddAction(features, "copyMessage", "Скопировать сообщение", "copyMessage", AllChats());
        AddAction(features, "forwardMessage", "Переслать сообщение", "forwardMessage", AllChats());
        AddAction(features, "pinMessage", "Закрепить сообщение", "pinChatMessage", Set("group", "supergroup", "channel"), Set("can_pin_messages"));
        AddAction(features, "setReaction", "Поставить реакцию", "setMessageReaction", AllChats());
        AddAction(features, "answerInlineQuery", "Ответить на inline-запрос", "answerInlineQuery");
        AddAction(features, "answerWebAppQuery", "Принять данные Mini App", "answerWebAppQuery");
        AddAction(features, "answerGuestQuery", "Ответить в Guest Mode", "answerGuestQuery");
        AddAction(features, "editEphemeralMessage", "Изменить временное сообщение", "editEphemeralMessageText");
        AddAction(features, "deleteEphemeralMessage", "Удалить временное сообщение", "deleteEphemeralMessage");
        AddAction(features, "getManagedBotToken", "Получить токен управляемого бота", "getManagedBotToken");
        AddAction(features, "replaceManagedBotToken", "Заменить токен управляемого бота", "replaceManagedBotToken");
        AddAction(features, "sendInvoice", "Создать счёт", "sendInvoice", AllChats());
        AddAction(features, "sendPaidMedia", "Отправить платный контент", "sendPaidMedia", Set("channel"));
        AddAction(features, "answerPreCheckoutQuery", "Подтвердить проверку платежа", "answerPreCheckoutQuery");
        AddAction(features, "editUserStarSubscription", "Изменить подписку Stars", "editUserStarSubscription");
        AddAction(features, "createChatSubscriptionInviteLink", "Создать ссылку подписки", "createChatSubscriptionInviteLink", Set("channel"), Set("can_invite_users"));
        AddAction(features, "editChatSubscriptionInviteLink", "Изменить ссылку подписки", "editChatSubscriptionInviteLink", Set("channel"), Set("can_invite_users"));
        AddAction(features, "refundStarPayment", "Вернуть Stars", "refundStarPayment");
        AddAction(features, "getStarTransactions", "Получить операции Stars", "getStarTransactions");
        AddAction(features, "getMyStarBalance", "Получить баланс Stars", "getMyStarBalance");
        AddAction(features, "sendBotMessage", "Отправить сообщение другому боту", "sendMessage", Set("private", "group", "supergroup"));
        return features;
    }

    private static void AddMessageEvent(Dictionary<string, TelegramFeature> target, string contentType, string name) =>
        target[$"telegram.event.message.{contentType}"] = new($"telegram.event.message.{contentType}", name, TelegramFeatureKind.Event,
            "message", MessageContentType: contentType);

    private static void AddAction(Dictionary<string, TelegramFeature> target, string id, string name, string method,
        IReadOnlySet<string>? chats = null, IReadOnlySet<string>? rights = null) =>
        target[id] = new(id, name, TelegramFeatureKind.Action, Method: method, ChatTypes: chats, Rights: rights);

    private static IReadOnlySet<string> AllChats() => Set("private", "group", "supergroup", "channel");
    private static IReadOnlySet<string> Set(params string[] values) => values.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string? LegacyUpdateType(string handler) => handler switch
    {
        "start" or "text" or "groupText" => "message",
        _ => null
    };

    private static string EventName(string update) => update switch
    {
        "message" => "Получено сообщение",
        "edited_message" => "Сообщение изменено",
        "channel_post" => "Опубликован пост канала",
        "edited_channel_post" => "Пост канала изменён",
        "business_connection" => "Изменилось Business-подключение",
        "business_message" => "Получено Business-сообщение",
        "edited_business_message" => "Business-сообщение изменено",
        "deleted_business_messages" => "Business-сообщение удалено",
        "guest_message" => "Получено гостевое сообщение",
        "inline_query" => "Получен inline-запрос",
        "chosen_inline_result" => "Выбран inline-результат",
        "callback_query" => "Пользователь нажал кнопку",
        "pre_checkout_query" => "Требуется проверить платёж",
        "purchased_paid_media" => "Куплен платный контент",
        "managed_bot" => "Изменился управляемый бот",
        "subscription" => "Изменилась подписка",
        "stopped_message_generation" => "Пользователь остановил генерацию",
        _ => update.Replace('_', ' ')
    };
}

public sealed record TelegramEvent(string Type, TelegramMessage? Message, IReadOnlyDictionary<string, object?> Variables, JsonElement Data,
    string? MessageContentType = null);

public static class TelegramEventMapper
{
    public static TelegramEvent Map(TelegramUpdate update)
    {
        var pair = update.Data.FirstOrDefault(x => x.Key != "update_id");
        if (string.IsNullOrEmpty(pair.Key) || !TelegramFeatures.IsKnownUpdate(pair.Key))
            throw new JsonException("Telegram Update не содержит поддерживаемого события.");

        var message = update.MessageFor(pair.Key);
        var contentType = message is null ? null : MessageContentType(pair.Value);
        var variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["event.update.id"] = update.UpdateId,
            ["event.update.type"] = pair.Key,
            ["event.message.text"] = message?.Text,
            ["event.message.id"] = message?.MessageId,
            ["event.message.content_type"] = contentType,
            ["event.chat.id"] = message?.Chat.Id,
            ["event.chat.type"] = message?.Chat.Type,
            ["event.business.connection_id"] = message?.BusinessConnectionId,
            ["event.guest.query_id"] = message?.GuestQueryId,
            ["event.guest.caller_user_id"] = message?.GuestBotCallerUser?.Id,
            ["event.guest.caller_chat_id"] = message?.GuestBotCallerChat?.Id,
            ["event.start.payload"] = StartPayload(message?.Text),
            ["event.context.is_business"] = message?.BusinessConnectionId is not null,
            ["event.context.is_guest"] = pair.Key == "guest_message",
            ["event.context.is_ephemeral"] = message?.EphemeralMessageId is not null,
            ["event.context.is_community"] = contentType?.StartsWith("community_", StringComparison.Ordinal) == true,
            ["event.payment.status"] = pair.Key == "pre_checkout_query" ? "pre_checkout" : contentType switch
            {
                "successful_payment" => "successful",
                "refunded_payment" => "refunded",
                _ => null
            }
        };
        if (contentType == "web_app_data" && pair.Value.TryGetProperty("web_app_data", out var webApp) && webApp.TryGetProperty("data", out var data))
            variables["event.mini_app.data"] = data.GetString();
        return new(pair.Key, message, variables, pair.Value, contentType);
    }

    private static string? MessageContentType(JsonElement message)
    {
        string[] fields =
        [
            "text", "photo", "video", "animation", "audio", "document", "voice", "video_note", "location", "venue",
            "contact", "dice", "poll", "sticker", "game", "rich_message", "web_app_data", "successful_payment",
            "refunded_payment", "community_chat_added", "community_chat_removed", "community_chat_joined", "managed_bot_created"
        ];
        return fields.FirstOrDefault(x => message.TryGetProperty(x, out _));
    }

    private static string? StartPayload(string? text)
    {
        if (text is null || !text.StartsWith("/start", StringComparison.OrdinalIgnoreCase)) return null;
        var space = text.IndexOf(' ');
        return space < 0 ? "" : text[(space + 1)..].Trim();
    }
}

public sealed record BotToBotContext(Guid ExecutionId, Guid OriginBotId, int Depth, string DeduplicationKey, TimeSpan Timeout)
{
    public BotToBotContext Next(Guid executionId, string deduplicationKey)
    {
        if (Depth >= TelegramFeatures.MaxBotToBotDepth) throw new InvalidOperationException("Достигнута максимальная глубина bot-to-bot цепочки.");
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        if (Timeout <= TimeSpan.Zero) throw new InvalidOperationException("Таймаут bot-to-bot должен быть положительным.");
        return this with { ExecutionId = executionId, Depth = Depth + 1, DeduplicationKey = deduplicationKey };
    }
}

public sealed class TelegramFlowValidator
{
    public IReadOnlyList<ValidationIssue> Validate(FlowDefinition flow)
    {
        var issues = new List<ValidationIssue>();
        foreach (var node in flow.Nodes)
        {
            if (!TelegramFeatures.All.TryGetValue(node.Handler, out var feature))
            {
                if (node.Handler.StartsWith("telegram.", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(node.Id, "telegram.feature.unknown", "Эта Telegram-функция не поддерживается текущим адаптером."));
                continue;
            }

            var parameters = node.Parameters;
            if (parameters?["text"]?.GetValue<string>() is { Length: > TelegramLimits.MaxTextLength })
                issues.Add(new(node.Id, "telegram.text_limit", $"Текст превышает лимит Telegram: {TelegramLimits.MaxTextLength} символов."));
            if (parameters?["caption"]?.GetValue<string>() is { Length: > TelegramLimits.MaxCaptionLength })
                issues.Add(new(node.Id, "telegram.caption_limit", $"Подпись превышает лимит Telegram: {TelegramLimits.MaxCaptionLength} символа."));
            if (parameters?["callbackData"]?.GetValue<string>() is { } callback &&
                System.Text.Encoding.UTF8.GetByteCount(callback) > TelegramLimits.MaxCallbackDataBytes)
                issues.Add(new(node.Id, "telegram.callback_limit", $"Данные кнопки превышают лимит Telegram: {TelegramLimits.MaxCallbackDataBytes} байта."));
            if (parameters?["media"] is JsonArray media && media.Count is < TelegramLimits.MinMediaGroupItems or > TelegramLimits.MaxMediaGroupItems)
                issues.Add(new(node.Id, "telegram.media_group_limit", $"Альбом должен содержать от {TelegramLimits.MinMediaGroupItems} до {TelegramLimits.MaxMediaGroupItems} элементов."));
            if (parameters?["fileSize"]?.GetValue<long>() is > TelegramLimits.MaxDocumentBytes)
                issues.Add(new(node.Id, "telegram.file_limit", $"Файл превышает лимит Telegram: {TelegramLimits.MaxDocumentBytes} байт."));
            if (parameters?["chatType"]?.GetValue<string>() is { } chat && feature.ChatTypes is { } chats && !chats.Contains(chat))
                issues.Add(new(node.Id, "telegram.chat.incompatible", "Действие недоступно для выбранного типа чата."));
            if (feature.Rights is { Count: > 0 } rights && parameters?["grantedRights"] is JsonArray granted)
            {
                var actual = granted.Select(x => x?.GetValue<string>()).Where(x => x is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var right in rights.Where(x => !actual.Contains(x)))
                    issues.Add(new(node.Id, "telegram.permission.missing", $"У бота нет права: {right}."));
            }
            if (node.Handler is "sendRichMessage" or "sendRichMessageDraft" && parameters?["richMessage"] is not JsonObject)
                issues.Add(new(node.Id, "telegram.rich_message.invalid", "Rich Message должен храниться как структурированный объект."));
            if (parameters?["ephemeral"]?.GetValue<bool>() == true && parameters["receiverUserId"] is null)
                issues.Add(new(node.Id, "telegram.ephemeral.receiver", "Для временного сообщения выберите получателя."));
            if (node.Handler == "sendInvoice" && parameters?["digital"]?.GetValue<bool>() == true && parameters["currency"]?.GetValue<string>() != "XTR")
                issues.Add(new(node.Id, "telegram.stars.currency", "Цифровые товары внутри Telegram оплачиваются в Stars (XTR)."));
            if (node.Handler == "sendBotMessage")
            {
                string[] required = ["executionId", "originBotId", "deduplicationKey", "timeoutSeconds"];
                foreach (var name in required.Where(name => parameters?[name] is null))
                    issues.Add(new(node.Id, "telegram.bot_to_bot.missing", $"Для bot-to-bot требуется параметр {name}."));
                if (parameters?["depth"]?.GetValue<int>() is < 0 or >= TelegramFeatures.MaxBotToBotDepth)
                    issues.Add(new(node.Id, "telegram.bot_to_bot.depth", "Превышена максимальная глубина bot-to-bot цепочки."));
            }
        }
        return issues;
    }
}
