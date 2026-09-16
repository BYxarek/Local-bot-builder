using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using NativeBot.Core;
using NativeBot.Storage;
using NativeBot.Telegram;

var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NativeBot");
var databasePath = Option("--db") ?? Path.Combine(dataDirectory, "nativebot.db");
var instanceName = Option("--instance") ?? "NativeBot.Agent";
using var singleInstance = new Mutex(true, $"Local\\{instanceName}", out var created);
if (!created) return;

var database = new AppDatabase(databasePath);
await database.InitializeAsync();
await database.RecoverRuntimeStateAsync();
await database.AddLogAsync(null, "Событие", "Bot Agent запущен. Очереди и расписания восстановлены.");
var listenUrl = Option("--listen") ?? await database.GetSettingAsync("WebhookListenUrl") ?? "http://localhost:8443";
using var shutdown = new CancellationTokenSource();
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
var agent = new AgentHost(database, httpClient, shutdown, instanceName);
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();
builder.WebHost.UseUrls(listenUrl);
var web = builder.Build();
web.MapGet("/health", () => TypedResults.Ok(new { status = "ok" }));
web.MapPost("/webhook/{botId:guid}", async Task<Results<Ok, NotFound, UnauthorizedHttpResult, BadRequest<string>, StatusCodeHttpResult>>
    (Guid botId, HttpRequest request, CancellationToken cancellationToken) => await agent.ReceiveWebhookAsync(botId, request, cancellationToken));

Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
await web.StartAsync(shutdown.Token);
try { await agent.RunAsync(shutdown.Token); }
finally
{
    await web.StopAsync();
    await database.AddLogAsync(null, "Событие", "Bot Agent остановлен.");
}

string? Option(string name) => args.SkipWhile(x => x != name).Skip(1).FirstOrDefault();

internal sealed record ActiveBot(TelegramClient Client, string? Secret, BotUpdateMode UpdateMode);

internal sealed class AgentHost(AppDatabase database, HttpClient httpClient, CancellationTokenSource shutdown, string pipeName)
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runners = [];
    private readonly ConcurrentDictionary<Guid, byte> _manuallyStarted = [];
    private readonly ConcurrentDictionary<Guid, ActiveBot> _activeBots = [];
    private bool _paused;
    private DateTimeOffset _nextSendAt;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var commandTask = ListenForCommandsAsync(cancellationToken);
        var queueTask = ProcessQueueAsync(cancellationToken);
        var outboxTask = ProcessOutboxAsync(cancellationToken);
        var scheduleTask = ProcessSchedulesAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bots = await database.GetBotsAsync(cancellationToken);
                var wanted = _paused ? [] : bots.Where(x => x.AutoStart || _manuallyStarted.ContainsKey(x.Id)).Select(x => x.Id).ToHashSet();
                foreach (var bot in bots.Where(x => wanted.Contains(x.Id) && !_runners.ContainsKey(x.Id))) Start(bot, cancellationToken);
                foreach (var id in _runners.Keys.Where(id => !wanted.Contains(id)).ToArray())
                {
                    _runners[id].Cancel();
                    await database.SetBotStatusAsync(id, _paused ? BotStatus.Paused : BotStatus.Stopped, cancellationToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            foreach (var runner in _runners.Values) runner.Cancel();
            try { await commandTask; } catch (OperationCanceledException) { }
            try { await queueTask; } catch (OperationCanceledException) { }
            try { await outboxTask; } catch (OperationCanceledException) { }
            try { await scheduleTask; } catch (OperationCanceledException) { }
        }
    }

    public async Task<Results<Ok, NotFound, UnauthorizedHttpResult, BadRequest<string>, StatusCodeHttpResult>> ReceiveWebhookAsync(
        Guid botId, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!_activeBots.TryGetValue(botId, out var bot) || bot.UpdateMode != BotUpdateMode.Webhook || bot.Secret is null) return TypedResults.NotFound();
        if (!TelegramWebhookSecurity.IsValid(request.Headers["X-Telegram-Bot-Api-Secret-Token"].FirstOrDefault(), bot.Secret))
            return TypedResults.Unauthorized();
        if (request.ContentLength is > 1_048_576) return TypedResults.BadRequest("Telegram Update превышает допустимый размер.");
        string payload;
        TelegramUpdate update;
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            payload = await reader.ReadToEndAsync(cancellationToken);
            await using var body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            update = await TelegramUpdateReader.ReadAsync(body, cancellationToken);
        }
        catch (System.Text.Json.JsonException exception) { return TypedResults.BadRequest(exception.Message); }
        try
        {
            await database.EnqueueWebhookUpdateAsync(botId, update.UpdateId, payload, cancellationToken);
            return TypedResults.Ok();
        }
        catch
        {
            return TypedResults.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pending = await database.GetPendingWebhookUpdatesAsync(cancellationToken: cancellationToken);
            if (pending.Count == 0) { await Task.Delay(100, cancellationToken); continue; }
            foreach (var item in pending)
            {
                if (!_activeBots.TryGetValue(item.BotId, out var bot)) continue;
                try
                {
                    await using var body = new MemoryStream(Encoding.UTF8.GetBytes(item.Payload));
                    var update = await TelegramUpdateReader.ReadAsync(body, cancellationToken);
                    await ProcessUpdateAsync(item.BotId, bot.Client, update, cancellationToken);
                    await database.CompleteWebhookUpdateAsync(item.BotId, item.UpdateId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    var attempts = item.Attempts + 1;
                    await database.AddLogAsync(item.BotId, "Ошибка", "Не удалось обработать событие. Бот продолжает работу.", exception.Message, cancellationToken);
                    if (attempts < 10) await database.RetryWebhookUpdateAsync(item.BotId, item.UpdateId, attempts, cancellationToken);
                    else
                    {
                        await database.AddDeadLetterAsync(new(Guid.NewGuid(), item.BotId, null, null, null, null, null, $"Update {item.UpdateId}", "Не обработано после допустимых попыток.", attempts, exception.ToString(), DateTimeOffset.UtcNow), cancellationToken);
                        await database.CompleteWebhookUpdateAsync(item.BotId, item.UpdateId, cancellationToken);
                    }
                }
            }
        }
    }

    private async Task ProcessOutboxAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var items = await database.GetDueOutboxAsync(cancellationToken: cancellationToken);
            if (items.Count == 0) { await Task.Delay(100, cancellationToken); continue; }
            foreach (var item in items)
            {
                if (!_activeBots.TryGetValue(item.BotId, out var bot)) continue;
                try
                {
                    await database.UpdateOutboxAsync(item.Id, OutboxStatus.Sending, item.DueAt, item.Attempts, cancellationToken: cancellationToken);
                    var delay = _nextSendAt - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                    if (item.Kind == "text") await bot.Client.SendTextAsync(item.ChatId, item.Payload, cancellationToken);
                    else throw new InvalidOperationException($"Неизвестное исходящее действие: {item.Kind}.");
                    _nextSendAt = DateTimeOffset.UtcNow + TelegramPolicy.MinimumSendInterval;
                    await database.UpdateOutboxAsync(item.Id, OutboxStatus.Sent, DateTimeOffset.UtcNow, item.Attempts, cancellationToken: cancellationToken);
                    if (BroadcastId(item) is { } broadcastId) await database.RecordBroadcastResultAsync(broadcastId, true, cancellationToken);
                    await database.AddLogAsync(item.BotId, "Событие", "Отправлено сообщение", cancellationToken: cancellationToken);
                    await database.AddUserEventIfKnownAsync(item.BotId, item.ChatId, "Отправлено сообщение",
                        item.Payload[..Math.Min(item.Payload.Length, 200)], cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    if (exception is TelegramException { MigrateToChatId: { } newChatId })
                    {
                        await database.MigrateChatAsync(item.BotId, item.ChatId, newChatId, cancellationToken);
                        await database.UpdateOutboxAsync(item.Id, OutboxStatus.Retry, DateTimeOffset.UtcNow, item.Attempts + 1, "Чат преобразован в супергруппу.", cancellationToken);
                        continue;
                    }
                    var failure = TelegramErrorClassifier.Classify(exception);
                    var attempts = item.Attempts + 1;
                    if (failure.Retry && attempts < 10)
                    {
                        var delay = failure.RetryAfter ?? TelegramPolicy.RetryDelay(attempts);
                        await database.UpdateOutboxAsync(item.Id, OutboxStatus.Retry, DateTimeOffset.UtcNow + delay, attempts, failure.UserMessage, cancellationToken);
                    }
                    else
                    {
                        await database.UpdateOutboxAsync(item.Id, OutboxStatus.Failed, DateTimeOffset.UtcNow, attempts, failure.UserMessage, cancellationToken);
                        await database.AddDeadLetterAsync(new(Guid.NewGuid(), item.BotId, null, item.ChatId, null, null, null, item.Kind, failure.UserMessage, attempts, exception.ToString(), DateTimeOffset.UtcNow), cancellationToken);
                        if (BroadcastId(item) is { } broadcastId) await database.RecordBroadcastResultAsync(broadcastId, false, cancellationToken);
                        if (failure.Kind is TelegramFailureKind.Forbidden or TelegramFailureKind.UnavailableChat)
                            await database.SetUserStatusByChatAsync(item.BotId, item.ChatId,
                                failure.Kind == TelegramFailureKind.Forbidden ? "Бот заблокирован" : "Чат недоступен", cancellationToken);
                    }
                    await database.AddLogAsync(item.BotId, "Ошибка", failure.UserMessage + " Бот продолжает работу.", exception.Message, cancellationToken);
                }
            }
        }
        static Guid? BroadcastId(OutboxAction item) => item.DeduplicationKey?.Split(':') is ["broadcast", var id, ..] && Guid.TryParse(id, out var value) ? value : null;
    }

    private async Task ProcessSchedulesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var items = await database.GetDueScheduledActionsAsync(cancellationToken: cancellationToken);
            if (items.Count == 0) { await Task.Delay(250, cancellationToken); continue; }
            foreach (var item in items)
            {
                if (!_activeBots.ContainsKey(item.BotId)) continue;
                try
                {
                    var version = await database.GetFlowVersionAsync(item.FlowVersionId, cancellationToken);
                    if (version is null) throw new InvalidOperationException("Версия сценария не найдена.");
                    var message = new TelegramMessage(0, new(item.UserId, false, "Пользователь", null, null, null), new(item.ChatId, "private"), null);
                    var scheduledEvent = new TelegramEvent("scheduled", message,
                        new Dictionary<string, object?> { ["event.message.text"] = null, ["event.message.id"] = 0L, ["event.chat.id"] = message.Chat.Id }, default);
                    await ExecuteFlowAsync(item.BotId, version, version.Definition with { EntryNodeId = item.ResumeNodeId }, scheduledEvent, cancellationToken);
                    await database.UpdateScheduledActionAsync(item.Id, ScheduledActionStatus.Completed, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    await database.UpdateScheduledActionAsync(item.Id, ScheduledActionStatus.Failed, cancellationToken);
                    await database.AddDeadLetterAsync(new(Guid.NewGuid(), item.BotId, item.UserId, item.ChatId, item.FlowId, item.FlowVersionId, item.ResumeNodeId, "Отложенное действие", "Не удалось продолжить сценарий.", 1, exception.ToString(), DateTimeOffset.UtcNow), cancellationToken);
                }
            }
        }
    }

    private void Start(Bot bot, CancellationToken cancellationToken)
    {
        var runnerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_runners.TryAdd(bot.Id, runnerCancellation)) { runnerCancellation.Dispose(); return; }
        _ = RunBotAsync(bot, runnerCancellation.Token).ContinueWith(completed =>
        {
            runnerCancellation.Dispose();
            _runners.TryRemove(bot.Id, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunBotAsync(Bot bot, CancellationToken cancellationToken)
    {
        TelegramClient? client = null;
        var webhookRegistered = false;
        try
        {
            await database.SetBotStatusAsync(bot.Id, BotStatus.Starting, cancellationToken);
            client = new TelegramClient(httpClient, await database.GetBotTokenAsync(bot.Id, cancellationToken));
            await client.GetMeAsync(cancellationToken);
            var publishedFlows = await database.GetPublishedFlowsAsync(bot.Id, cancellationToken);
            var allowedUpdates = TelegramFeatures.AllowedUpdates(publishedFlows.Select(x => x.Definition));
            if (bot.UpdateMode == BotUpdateMode.Webhook)
            {
                var publicUrl = await database.GetSettingAsync("WebhookPublicUrl", cancellationToken);
                if (!Uri.TryCreate(publicUrl?.TrimEnd('/') + $"/webhook/{bot.Id}", UriKind.Absolute, out var webhookUrl) || webhookUrl.Scheme != Uri.UriSchemeHttps)
                {
                    await database.SetBotStatusAsync(bot.Id, BotStatus.NeedsAttention, cancellationToken);
                    await database.AddLogAsync(bot.Id, "Ошибка", "Для webhook укажите публичный HTTPS-адрес.", cancellationToken: cancellationToken);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return;
                }
                var secret = await database.GetOrCreateWebhookSecretAsync(bot.Id, cancellationToken);
                await client.SetWebhookAsync(webhookUrl, secret, allowedUpdates, cancellationToken);
                webhookRegistered = true;
                await database.AddLogAsync(bot.Id, "Событие", "Webhook Telegram обновлён.", cancellationToken: cancellationToken);
                _activeBots[bot.Id] = new(client, secret, bot.UpdateMode);
                await database.SetBotStatusAsync(bot.Id, BotStatus.Running, cancellationToken);
                await database.AddLogAsync(bot.Id, "Событие", "Бот запущен через webhook.", cancellationToken: cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else
            {
                await client.DeleteWebhookAsync(cancellationToken);
                _activeBots[bot.Id] = new(client, null, bot.UpdateMode);
                await database.SetBotStatusAsync(bot.Id, BotStatus.Running, cancellationToken);
                await database.AddLogAsync(bot.Id, "Событие", "Бот запущен через long polling.", cancellationToken: cancellationToken);
                long? offset = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var updates = await client.GetUpdatesAsync(offset, allowedUpdates: allowedUpdates, cancellationToken: cancellationToken);
                    foreach (var update in updates)
                    {
                        await database.EnqueueWebhookUpdateAsync(bot.Id, update.UpdateId, TelegramUpdateReader.Serialize(update), cancellationToken);
                        offset = update.UpdateId + 1;
                    }
                }
            }
        }
        catch (TelegramException exception) when (exception.ErrorCode == 401)
        {
            await database.SetBotStatusAsync(bot.Id, BotStatus.AuthorizationError, CancellationToken.None);
            await database.AddLogAsync(bot.Id, "Ошибка", "Telegram отклонил токен бота.", exception.Message, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await database.SetBotStatusAsync(bot.Id, BotStatus.Offline, CancellationToken.None);
            await database.AddLogAsync(bot.Id, "Ошибка", "Соединение с Telegram потеряно. Bot Agent повторит подключение.", exception.Message, CancellationToken.None);
        }
        finally
        {
            _activeBots.TryRemove(bot.Id, out _);
            if (client is not null && webhookRegistered)
                try { await client.DeleteWebhookAsync(CancellationToken.None); } catch (TelegramException) { }
            await database.AddLogAsync(bot.Id, "Событие", "Бот остановлен.", cancellationToken: CancellationToken.None);
        }
    }

    private async Task ProcessUpdateAsync(Guid botId, TelegramClient client, TelegramUpdate update, CancellationToken cancellationToken)
    {
        var telegramEvent = TelegramEventMapper.Map(update);
        var message = telegramEvent.Message;
        if (message is null)
        {
            await database.AddLogAsync(botId, "Событие", $"Получено событие Telegram: {telegramEvent.Type}", cancellationToken: cancellationToken);
            return;
        }
        if (message?.From is not { IsBot: false } user) return;
        var now = DateTimeOffset.UtcNow;
        await database.UpsertUserAsync(new(botId, user.Id, user.Username, user.FirstName, user.LastName, user.LanguageCode, now, now), cancellationToken);
        await database.UpsertChatAsync(botId, message.Chat.Id, message.Chat.Type, cancellationToken);
        await database.AddUserEventIfMissingAsync(botId, user.Id, "Первое взаимодействие", "Пользователь впервые обратился к боту", cancellationToken);
        var userEventKind = telegramEvent.MessageContentType is "successful_payment" or "refunded_payment" ? "Платёжное событие" : "Получено сообщение";
        await database.AddUserEventAsync(botId, user.Id, userEventKind, message.Text is null ? "Получено вложение или служебное событие" : message.Text[..Math.Min(message.Text.Length, 200)], cancellationToken);
        await database.TouchBotAsync(botId, now, cancellationToken);
        await ExecuteMatchingFlowsAsync(botId, client, telegramEvent, cancellationToken);
    }

    private async Task ExecuteMatchingFlowsAsync(Guid botId, TelegramClient client, TelegramEvent telegramEvent, CancellationToken cancellationToken)
    {
        _ = client;
        var message = telegramEvent.Message!;
        var versions = await database.GetPublishedFlowsAsync(botId, cancellationToken);
        var waits = (await database.GetActiveWaitingStatesAsync(botId, cancellationToken)).Where(x => x.UserId == message.From?.Id && x.ChatId == message.Chat.Id && x.ExpectedEvent == "message").ToArray();
        foreach (var wait in waits)
        {
            if (await database.GetFlowVersionAsync(wait.FlowVersionId, cancellationToken) is not { } version) continue;
            await ExecuteFlowAsync(botId, version, version.Definition with { EntryNodeId = wait.ResumeNodeId }, telegramEvent, cancellationToken);
            await database.CompleteWaitingStateAsync(wait.Id, cancellationToken: cancellationToken);
        }
        if (waits.Length > 0) return;
        foreach (var version in versions)
        {
            var flow = version.Definition;
            var entry = flow.Nodes.FirstOrDefault(x => x.Id == flow.EntryNodeId);
            if (entry is null || !Matches(entry.Handler, telegramEvent, message)) continue;
            await ExecuteFlowAsync(botId, version, flow, telegramEvent, cancellationToken);
        }

        static bool Matches(string handler, TelegramEvent telegramEvent, TelegramMessage message) => handler switch
        {
            "start" => message.Text?.StartsWith("/start", StringComparison.OrdinalIgnoreCase) == true,
            "text" => telegramEvent.Type == "message" && message.Text is not null,
            "groupText" => telegramEvent.Type == "message" && message.Text is not null && message.Chat.Type is "group" or "supergroup",
            _ => TelegramFeatures.All.GetValueOrDefault(handler) is { } feature && feature.UpdateType == telegramEvent.Type &&
                (feature.MessageContentType is null || feature.MessageContentType == telegramEvent.MessageContentType)
        };
    }

    private async Task ExecuteFlowAsync(Guid botId, FlowVersion version, FlowDefinition flow, TelegramEvent telegramEvent, CancellationToken cancellationToken)
    {
            var message = telegramEvent.Message ?? throw new InvalidOperationException("Событие не содержит контекст сообщения.");
            var executionId = Guid.NewGuid();
            var originUpdateId = Convert.ToInt64(telegramEvent.Variables.GetValueOrDefault("event.update.id") ?? 0L);
            await database.AddExecutionAsync(new(executionId, botId, flow.Id, version.Id, message.From?.Id ?? 0, message.Chat.Id,
                originUpdateId, flow.EntryNodeId, ExecutionStatus.Running, DateTimeOffset.UtcNow), cancellationToken);
            if (message.From is { IsBot: false } flowUser)
                await database.AddUserEventAsync(botId, flowUser.Id, "Запуск сценария", flow.Name, cancellationToken);
            var variables = new VariableContext(flow.Variables);
            ExecutionStatus? suspendedStatus = null;
            foreach (var (name, value) in telegramEvent.Variables) variables.SetRuntime(name, value);
            variables["user.first_name"] = message.From?.FirstName;
            variables["user.username"] = message.From?.Username;
            var handlers = new Dictionary<string, BlockHandler>(StringComparer.OrdinalIgnoreCase)
            {
                ["start"] = Success,
                ["text"] = Success,
                ["groupText"] = Success,
                ["else"] = Success,
                ["transition"] = Success,
                ["sendText"] = async (node, context, token) =>
                {
                    var text = context.Interpolate(node.Parameters?["text"]?.GetValue<string>() ?? "");
                    await database.EnqueueOutboxAsync(new(Guid.NewGuid(), botId, message.Chat.Id, "text", text, OutboxStatus.Pending, DateTimeOffset.UtcNow,
                        DeduplicationKey: $"{version.Id}:{updateIdentity(message)}:{node.Id}"), token);
                    return BlockResult.Success;
                },
                ["addTag"] = async (node, _, token) =>
                {
                    if (node.Parameters?["text"]?.GetValue<string>() is { Length: > 0 } tag)
                    {
                        await database.AddTagAsync(botId, message.From!.Id, tag, token);
                        await database.AddUserEventAsync(botId, message.From.Id, "Изменён тег", $"Добавлен тег «{tag}»", token);
                    }
                    return BlockResult.Success;
                },
                ["wait"] = async (node, _, token) =>
                {
                    if (node.Next?.GetValueOrDefault(NodeOutcome.Success) is not { } resume) return new(NodeOutcome.Error, "Не указан блок продолжения.");
                    var seconds = node.Parameters?["timeoutSeconds"]?.GetValue<int>() ?? 3600;
                    await database.AddWaitingStateAsync(new(Guid.NewGuid(), botId, message.From!.Id, message.Chat.Id, null, "message", version.Id, resume, DateTimeOffset.UtcNow.AddSeconds(seconds)), token);
                    suspendedStatus = ExecutionStatus.Waiting;
                    return new(NodeOutcome.Timeout);
                },
                ["delay"] = async (node, _, token) =>
                {
                    if (node.Next?.GetValueOrDefault(NodeOutcome.Success) is not { } resume) return new(NodeOutcome.Error, "Не указан блок продолжения.");
                    var seconds = node.Parameters?["timeoutSeconds"]?.GetValue<int>() ?? 1;
                    await database.AddScheduledActionAsync(new(Guid.NewGuid(), botId, flow.Id, version.Id, message.From!.Id, message.Chat.Id, resume, DateTimeOffset.UtcNow.AddSeconds(seconds)), token);
                    suspendedStatus = ExecutionStatus.Scheduled;
                    return new(NodeOutcome.Timeout);
                },
                ["setVariable"] = (node, context, _) =>
                {
                    if (node.Parameters?["variable"]?.GetValue<string>() is { } name) context[name] = node.Parameters["value"]?.GetValue<string>();
                    return Task.FromResult(BlockResult.Success);
                },
                ["if"] = (node, context, _) =>
                {
                    var name = node.Parameters?["variable"]?.GetValue<string>();
                    var expected = node.Parameters?["equals"]?.GetValue<string>();
                    return Task.FromResult(new BlockResult(Equals(context[name ?? ""], expected) ? NodeOutcome.Success : NodeOutcome.Alternative));
                }
            };
            try
            {
                var outcome = await new FlowEngine(handlers).ExecuteAsync(flow, variables, cancellationToken);
                var status = outcome == NodeOutcome.Timeout ? suspendedStatus ?? ExecutionStatus.Waiting :
                    outcome == NodeOutcome.Success ? ExecutionStatus.Completed : ExecutionStatus.Failed;
                await database.UpdateExecutionAsync(executionId, status, flow.EntryNodeId, outcome.ToString(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await database.UpdateExecutionAsync(executionId, ExecutionStatus.Cancelled, flow.EntryNodeId, "Выполнение остановлено.", CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                await database.UpdateExecutionAsync(executionId, ExecutionStatus.Failed, flow.EntryNodeId, exception.Message, CancellationToken.None);
                if (message.From is { IsBot: false } failedUser)
                    await database.AddUserEventAsync(botId, failedUser.Id, "Ошибка", $"Сценарий «{flow.Name}»: {exception.Message}", CancellationToken.None);
                throw;
            }
        static string updateIdentity(TelegramMessage value) => $"{value.Chat.Id}:{value.MessageId}";
        static Task<BlockResult> Success(FlowNode _, VariableContext __, CancellationToken ___) => Task.FromResult(BlockResult.Success);
    }

    private async Task ListenForCommandsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cancellationToken);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var command = await reader.ReadLineAsync(cancellationToken) ?? "";
            var parts = command.Split(':', 2);
            switch (parts[0])
            {
                case "start" when parts.Length == 2 && Guid.TryParse(parts[1], out var startId): _manuallyStarted[startId] = 0; await writer.WriteLineAsync("ok"); break;
                case "stop" when parts.Length == 2 && Guid.TryParse(parts[1], out var stopId):
                    _manuallyStarted.TryRemove(stopId, out _);
                    if (_runners.TryGetValue(stopId, out var runner)) runner.Cancel();
                    await database.SetBotStatusAsync(stopId, BotStatus.Stopped, cancellationToken);
                    await writer.WriteLineAsync("ok");
                    break;
                case "reload": foreach (var activeRunner in _runners.Values) activeRunner.Cancel(); await writer.WriteLineAsync("ok"); break;
                case "pause": _paused = true; await writer.WriteLineAsync("ok"); break;
                case "resume": _paused = false; await writer.WriteLineAsync("ok"); break;
                case "exit": await writer.WriteLineAsync("ok"); shutdown.Cancel(); break;
                default: await writer.WriteLineAsync("unknown"); break;
            }
        }
    }
}
