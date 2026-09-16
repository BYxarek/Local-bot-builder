using System.Net;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NativeBot.Core;
using NativeBot.Storage;
using NativeBot.Telegram;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Подстановка и типы переменных", TestVariables),
    ("Выполнение сценария", TestFlowEngine),
    ("Валидация сценария", TestValidator),
    ("SQLite, версии и дедупликация", TestStorage),
    ("Telegram API parsing", TestTelegram),
    ("Telegram Bot API 10.3 возможности", TestTelegramFeatures),
    ("Отдельный Bot.Agent и IPC", TestAgentProcess)
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception exception) { failed++; Console.WriteLine($"FAIL  {name}: {exception.Message}"); }
}
Console.WriteLine($"\n{tests.Length - failed}/{tests.Length} проверок пройдено.");
return failed;

static Task TestVariables()
{
    var context = new VariableContext([new("user.order", VariableScope.User, VariableType.Number)]);
    context["user.order"] = 42;
    Equal("Заказ 42", context.Interpolate("Заказ {user.order}"));
    Equal("{user.missing}", context.Interpolate("{user.missing}"));
    Throws<InvalidOperationException>(() => context["system.now"] = DateTimeOffset.UtcNow);
    Throws<ArgumentException>(() => context["user.order"] = "не число");
    return Task.CompletedTask;
}

static async Task TestFlowEngine()
{
    var first = Guid.NewGuid();
    var end = Guid.NewGuid();
    var flow = new FlowDefinition(Guid.NewGuid(), "Тест", first,
    [
        new(first, "Действие", NodeKind.Action, "set", new Dictionary<NodeOutcome, Guid> { [NodeOutcome.Success] = end }),
        new(end, "Конец", NodeKind.End, "end")
    ]);
    var handlers = new Dictionary<string, BlockHandler>
    {
        ["set"] = (_, variables, _) => { variables["flow.done"] = true; return Task.FromResult(BlockResult.Success); }
    };
    var variables = new VariableContext();
    Equal(NodeOutcome.Success, await new FlowEngine(handlers).ExecuteAsync(flow, variables));
    Equal(true, variables["flow.done"]);
}

static Task TestValidator()
{
    var start = Guid.NewGuid();
    var missing = Guid.NewGuid();
    var flow = new FlowDefinition(Guid.NewGuid(), "Ошибочный", start,
    [
        new(start, "Сообщение", NodeKind.Action, "sendText",
            new Dictionary<NodeOutcome, Guid> { [NodeOutcome.Success] = missing },
            new JsonObject { ["text"] = new string('x', 4097) + " {user.unknown}" })
    ]);
    var issues = new FlowValidator().Validate(flow).Concat(new TelegramFlowValidator().Validate(flow)).ToArray();
    True(issues.Any(x => x.Code == "transition.missing"));
    True(issues.Any(x => x.Code == "telegram.text_limit"));
    True(issues.Any(x => x.Code == "variable.unknown"));
    var privacyNode = flow.Nodes[0] with { Parameters = new JsonObject { ["requiresGroupMessages"] = true, ["privacyModeDisabled"] = false } };
    True(new FlowValidator().Validate(flow with { Nodes = [privacyNode] }).Any(x => x.Code == "privacy_mode"));
    return Task.CompletedTask;
}

static async Task TestStorage()
{
    var testDirectory = Path.Combine(Path.GetTempPath(), "NativeBot.Tests", Guid.NewGuid().ToString("N"));
    var databasePath = Path.Combine(testDirectory, "test.db");
    try
    {
        var database = new AppDatabase(databasePath);
        await database.InitializeAsync();
        var bot = new Bot(Guid.NewGuid(), "Тест", "@test_bot", AutoStart: true);
        const string token = "123456789:abcdefghijklmnopqrstuvwxyz_ABCDE";
        await database.AddBotAsync(bot, token);
        Equal(token, await database.GetBotTokenAsync(bot.Id));
        await database.SetBotUpdateModeAsync(bot.Id, BotUpdateMode.LongPolling);
        Equal(BotUpdateMode.LongPolling, (await database.GetBotsAsync()).Single().UpdateMode);
        var webhookSecret = await database.GetOrCreateWebhookSecretAsync(bot.Id);
        Equal(webhookSecret, await database.GetOrCreateWebhookSecretAsync(bot.Id));
        True(webhookSecret.Length >= 32);
        Equal(1, (await database.GetBotsAsync()).Count);

        var now = DateTimeOffset.UtcNow;
        await database.UpsertUserAsync(new(bot.Id, long.MaxValue - 1, "user", "Имя", null, "ru", now, now));
        Equal(long.MaxValue - 1, (await database.GetUsersAsync(bot.Id)).Single().TelegramId);
        await database.AddUserEventAsync(bot.Id, long.MaxValue - 1, "Первое взаимодействие", "Пользователь написал боту");
        Equal(1, (await database.GetUserCardAsync(bot.Id, long.MaxValue - 1)).History.Count);
        await database.AddTagAsync(bot.Id, long.MaxValue - 1, "VIP");
        True(await database.HasTagAsync(bot.Id, long.MaxValue - 1, "VIP"));
        await database.SetUserFieldAsync(bot.Id, long.MaxValue - 1, "Город", "city", VariableType.Text, "Москва");
        Equal("Москва", (await database.GetUserCardAsync(bot.Id, long.MaxValue - 1)).Fields["Город"]);
        var segment = new Segment(Guid.NewGuid(), bot.Id, "Русские", new(Language: "ru"));
        await database.SaveSegmentAsync(segment);
        Equal(1, (await database.GetSegmentsAsync(bot.Id)).Count);
        await database.SetVariableAsync(VariableScope.Secret, bot.Id.ToString(), "api_key", VariableType.Text, "секрет", true);
        Equal("секрет", await database.GetVariableAsync<string>(VariableScope.Secret, bot.Id.ToString(), "api_key"));
        True(await database.TryMarkUpdateProcessedAsync(bot.Id, 100));
        True(!await database.TryMarkUpdateProcessedAsync(bot.Id, 100));
        True(await database.EnqueueWebhookUpdateAsync(bot.Id, 101, "{\"update_id\":101}"));
        True(!await database.EnqueueWebhookUpdateAsync(bot.Id, 101, "{\"update_id\":101}"));
        Equal(1, (await database.GetPendingWebhookUpdatesAsync()).Count);
        await database.CompleteWebhookUpdateAsync(bot.Id, 101);
        True(!await database.EnqueueWebhookUpdateAsync(bot.Id, 101, "{\"update_id\":101}"));

        var end = Guid.NewGuid();
        var flow = new FlowDefinition(Guid.NewGuid(), "Сценарий", end, [new(end, "Конец", NodeKind.End, "end")]);
        var version1 = await database.PublishAsync(bot.Id, flow);
        var version2 = await database.PublishAsync(bot.Id, flow with { Name = "Новый черновик" });
        Equal(1, version1.Version);
        Equal(2, version2.Version);
        Equal("Новый черновик", (await database.GetDraftsAsync(bot.Id)).Single().Name);
        var wait = new WaitingState(Guid.NewGuid(), bot.Id, long.MaxValue - 1, 42, null, "message", version2.Id, end, now.AddHours(1));
        await database.AddWaitingStateAsync(wait);
        Equal(wait.Id, (await database.GetActiveWaitingStatesAsync(bot.Id)).Single().Id);
        var scheduled = new ScheduledAction(Guid.NewGuid(), bot.Id, flow.Id, version2.Id, long.MaxValue - 1, 42, end, now.AddSeconds(-1));
        await database.AddScheduledActionAsync(scheduled);
        Equal(scheduled.Id, (await database.GetDueScheduledActionsAsync()).Single().Id);
        var broadcast = new Broadcast(Guid.NewGuid(), bot.Id, "Тест", null, "Привет", now, BroadcastStatus.Running, 1);
        await database.AddBroadcastAsync(broadcast);
        var outbox = new OutboxAction(Guid.NewGuid(), bot.Id, 42, "text", "Привет", OutboxStatus.Pending, now, DeduplicationKey: $"broadcast:{broadcast.Id}:42");
        True(await database.EnqueueOutboxAsync(outbox));
        True(!await database.EnqueueOutboxAsync(outbox with { Id = Guid.NewGuid() }));
        Equal(outbox.Id, (await database.GetDueOutboxAsync()).Single().Id);
        await database.UpdateOutboxAsync(outbox.Id, OutboxStatus.Sending, now, 0);
        await database.RecoverRuntimeStateAsync();
        Equal(OutboxStatus.Retry, (await database.GetDueOutboxAsync()).Single().Status);
        await database.RecordBroadcastResultAsync(broadcast.Id, true);
        Equal(BroadcastStatus.Completed, (await database.GetBroadcastsAsync(bot.Id)).Single().Status);

        var execution = new Execution(Guid.NewGuid(), bot.Id, flow.Id, version2.Id, long.MaxValue - 1, 42, 101, end, ExecutionStatus.Running, now);
        await database.AddExecutionAsync(execution);
        await database.UpdateExecutionAsync(execution.Id, ExecutionStatus.Completed, end, "Success");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Status FROM Executions WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", execution.Id.ToString());
            Equal((long)ExecutionStatus.Completed, (long)(await command.ExecuteScalarAsync())!);
        }

        var projectPath = Path.Combine(testDirectory, "project.nativebot");
        await database.ExportProjectAsync(bot.Id, projectPath);
        var exported = await File.ReadAllTextAsync(projectPath);
        True(exported.Contains("formatVersion", StringComparison.Ordinal));
        True(!exported.Contains(token, StringComparison.Ordinal));
        True(!exported.Contains("api_key", StringComparison.Ordinal));
        var importedBot = new Bot(Guid.NewGuid(), "Импорт", "@import_bot");
        await database.AddBotAsync(importedBot, "987654321:abcdefghijklmnopqrstuvwxyz_ABCDE");
        await database.ImportProjectAsync(importedBot.Id, projectPath);
        True((await database.GetDraftsAsync(importedBot.Id)).Count > 0);
        Equal(BotUpdateMode.LongPolling, (await database.GetBotsAsync()).Single(x => x.Id == importedBot.Id).UpdateMode);
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, true);
    }
}

static async Task TestTelegram()
{
    var handler = new StubHandler("""{"ok":true,"result":{"id":1,"first_name":"Помощник","username":"helper_bot"}}""");
    using var http = new HttpClient(handler);
    var info = await new TelegramClient(http, "123456789:abcdefghijklmnopqrstuvwxyz_ABCDE").GetMeAsync();
    Equal("helper_bot", info.Username);
    True(handler.LastRequest?.AbsolutePath.EndsWith("/getMe", StringComparison.Ordinal) == true);
    handler.Json = """{"ok":true,"result":true}""";
    await new TelegramClient(http, "123456789:abcdefghijklmnopqrstuvwxyz_ABCDE")
        .SetWebhookAsync(new Uri("https://example.com/webhook/id"), "valid_secret", ["message", "callback_query"]);
    True(handler.LastBody?.Contains("valid_secret", StringComparison.Ordinal) == true);
    True(handler.LastBody?.Contains("callback_query", StringComparison.Ordinal) == true);
    handler.Json = """{"ok":true,"result":[{"update_id":88,"message":{"message_id":6,"chat":{"id":9,"type":"private"},"text":"poll"}}]}""";
    var updates = await new TelegramClient(http, "123456789:abcdefghijklmnopqrstuvwxyz_ABCDE")
        .GetUpdatesAsync(80, 30, ["message"]);
    Equal(88L, updates.Single().UpdateId);
    True(handler.LastRequest?.AbsolutePath.EndsWith("/getUpdates", StringComparison.Ordinal) == true);
    True(handler.LastBody?.Contains("\"offset\":80", StringComparison.Ordinal) == true);
    await using var pollingBody = new MemoryStream(Encoding.UTF8.GetBytes(TelegramUpdateReader.Serialize(updates.Single())));
    Equal(88L, (await TelegramUpdateReader.ReadAsync(pollingBody)).UpdateId);
    True(TelegramWebhookSecurity.IsValid("valid_secret", "valid_secret"));
    True(!TelegramWebhookSecurity.IsValid("wrong", "valid_secret"));
    var rateLimit = TelegramErrorClassifier.Classify(new TelegramException("limit", 429, retryAfter: TimeSpan.FromSeconds(3)));
    True(rateLimit.Retry);
    Equal(TimeSpan.FromSeconds(3), rateLimit.RetryAfter);
    await using var updateBody = new MemoryStream(Encoding.UTF8.GetBytes("""{"update_id":77,"message":{"message_id":5,"chat":{"id":9,"type":"private"},"text":"/start"}}"""));
    Equal(77L, (await TelegramUpdateReader.ReadAsync(updateBody)).UpdateId);
}

static async Task TestTelegramFeatures()
{
    var eventNode = Guid.NewGuid();
    var flow = new FlowDefinition(Guid.NewGuid(), "Inline", eventNode,
        [new(eventNode, "Inline", NodeKind.Event, "telegram.event.inline_query")]);
    True(TelegramFeatures.AllowedUpdates([flow]).SequenceEqual(["inline_query"]));
    Equal("10.3", TelegramFeatures.BotApiVersion);

    await using var startBody = new MemoryStream(Encoding.UTF8.GetBytes(
        """{"update_id":78,"message":{"message_id":6,"from":{"id":7,"is_bot":false,"first_name":"Иван"},"chat":{"id":9,"type":"private"},"text":"/start campaign-42"}}"""));
    var startEvent = TelegramEventMapper.Map(await TelegramUpdateReader.ReadAsync(startBody));
    Equal("message", startEvent.Type);
    Equal("campaign-42", startEvent.Variables["event.start.payload"]);
    Equal("text", startEvent.MessageContentType);

    await using var businessBody = new MemoryStream(Encoding.UTF8.GetBytes(
        """{"update_id":79,"business_message":{"message_id":7,"from":{"id":8,"is_bot":false,"first_name":"Анна"},"chat":{"id":10,"type":"private"},"business_connection_id":"bc-1","text":"Заказ"}}"""));
    var businessEvent = TelegramEventMapper.Map(await TelegramUpdateReader.ReadAsync(businessBody));
    Equal("business_message", businessEvent.Type);
    Equal("bc-1", businessEvent.Variables["event.business.connection_id"]);

    await using var paymentBody = new MemoryStream(Encoding.UTF8.GetBytes(
        """{"update_id":80,"message":{"message_id":8,"from":{"id":9,"is_bot":false,"first_name":"Пётр"},"chat":{"id":11,"type":"private"},"successful_payment":{"currency":"XTR","total_amount":10}}}"""));
    var paymentEvent = TelegramEventMapper.Map(await TelegramUpdateReader.ReadAsync(paymentBody));
    Equal("successful_payment", paymentEvent.MessageContentType);
    Equal("successful", paymentEvent.Variables["event.payment.status"]);

    var richNode = new FlowNode(Guid.NewGuid(), "Rich", NodeKind.Action, "sendRichMessage", Parameters: new JsonObject());
    True(new TelegramFlowValidator().Validate(flow with { EntryNodeId = richNode.Id, Nodes = [richNode] })
        .Any(x => x.Code == "telegram.rich_message.invalid"));

    var context = new BotToBotContext(Guid.NewGuid(), Guid.NewGuid(), TelegramFeatures.MaxBotToBotDepth, "dedup", TimeSpan.FromSeconds(5));
    Throws<InvalidOperationException>(() => context.Next(Guid.NewGuid(), "next"));
}

static async Task TestAgentProcess()
{
    var testDirectory = Path.Combine(Path.GetTempPath(), "NativeBot.AgentTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testDirectory);
    var instance = "NativeBot.Agent.Test." + Guid.NewGuid().ToString("N");
    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var httpsPort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();
    var executable = Path.GetFullPath("src/NativeBot.Agent/bin/Debug/net10.0/NativeBot.Agent.exe");
    using var process = Process.Start(new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--db", Path.Combine(testDirectory, "agent.db"), "--instance", instance, "--listen", $"https://localhost:{httpsPort}" }
    }) ?? throw new Exception("Не удалось запустить Bot.Agent.");
    var stage = "подключение IPC";
    try
    {
        await using var pipe = new NamedPipeClientStream(".", instance, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(timeout.Token);
        stage = "проверка HTTPS health";
        using var testHttp = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });
        Equal(HttpStatusCode.OK, (await testHttp.GetAsync($"https://localhost:{httpsPort}/health", timeout.Token)).StatusCode);
        stage = "команда завершения";
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync("exit");
        Equal("ok", await reader.ReadLineAsync(timeout.Token));
        stage = "ожидание завершения процесса";
        await process.WaitForExitAsync(timeout.Token);
        Equal(0, process.ExitCode);
    }
    catch (OperationCanceledException exception) { throw new Exception($"Таймаут: {stage}.", exception); }
    finally
    {
        if (!process.HasExited) process.Kill(true);
        SqliteConnection.ClearAllPools();
        Directory.Delete(testDirectory, true);
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Ожидалось: {expected}; получено: {actual}");
}

static void True(bool value)
{
    if (!value) throw new Exception("Условие не выполнено.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Ожидалось исключение {typeof(T).Name}.");
}

sealed class StubHandler(string json) : HttpMessageHandler
{
    public Uri? LastRequest { get; private set; }
    public string? LastBody { get; private set; }
    public string Json { get; set; } = json;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request.RequestUri;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Json, Encoding.UTF8, "application/json")
        };
    }
}
