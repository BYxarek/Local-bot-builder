using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NativeBot.Core;

namespace NativeBot.Storage;

public sealed record PendingWebhookUpdate(Guid BotId, long UpdateId, string Payload, int Attempts);

public sealed partial class AppDatabase(string path)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(path),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS Bots (
                Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Username TEXT NOT NULL,
                Status INTEGER NOT NULL, AutoStart INTEGER NOT NULL DEFAULT 0,
                LastEventAt TEXT NULL, CreatedAt TEXT NOT NULL, UpdateMode INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS BotSecrets (
                BotId TEXT PRIMARY KEY REFERENCES Bots(Id) ON DELETE CASCADE,
                Token BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BotWebhookSecrets (
                BotId TEXT PRIMARY KEY REFERENCES Bots(Id) ON DELETE CASCADE,
                Secret BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Flows (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL, DraftJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS FlowVersions (
                Id TEXT PRIMARY KEY, FlowId TEXT NOT NULL REFERENCES Flows(Id) ON DELETE CASCADE,
                Version INTEGER NOT NULL, Status INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL, PublishedAt TEXT NULL, DefinitionJson TEXT NOT NULL,
                UNIQUE(FlowId, Version)
            );
            CREATE TABLE IF NOT EXISTS Users (
                BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                TelegramId INTEGER NOT NULL, Username TEXT NULL, FirstName TEXT NULL,
                LastName TEXT NULL, Language TEXT NULL, FirstSeenAt TEXT NOT NULL,
                LastSeenAt TEXT NOT NULL, Status TEXT NOT NULL,
                PRIMARY KEY(BotId, TelegramId)
            );
            CREATE TABLE IF NOT EXISTS Chats (
                BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                TelegramId INTEGER NOT NULL, Type TEXT NOT NULL, Title TEXT NULL, Status TEXT NOT NULL DEFAULT 'Активен',
                PRIMARY KEY(BotId, TelegramId)
            );
            CREATE TABLE IF NOT EXISTS UserVariables (
                BotId TEXT NOT NULL, TelegramId INTEGER NOT NULL, Name TEXT NOT NULL,
                ValueJson TEXT NULL, IsSecret INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(BotId, TelegramId, Name),
                FOREIGN KEY(BotId, TelegramId) REFERENCES Users(BotId, TelegramId) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS Variables (
                Scope INTEGER NOT NULL, OwnerId TEXT NOT NULL, Name TEXT NOT NULL,
                Type INTEGER NOT NULL, IsSecret INTEGER NOT NULL, Value BLOB NOT NULL,
                PRIMARY KEY(Scope, OwnerId, Name)
            );
            CREATE TABLE IF NOT EXISTS Tags (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL, UNIQUE(BotId, Name)
            );
            CREATE TABLE IF NOT EXISTS UserTags (
                BotId TEXT NOT NULL, TelegramId INTEGER NOT NULL, TagId TEXT NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY(BotId, TelegramId, TagId),
                FOREIGN KEY(BotId, TelegramId) REFERENCES Users(BotId, TelegramId) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS CustomFields (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL, InternalKey TEXT NOT NULL DEFAULT '', Type INTEGER NOT NULL,
                UNIQUE(BotId, Name), UNIQUE(BotId, InternalKey)
            );
            CREATE TABLE IF NOT EXISTS UserValues (
                BotId TEXT NOT NULL, TelegramId INTEGER NOT NULL, FieldId TEXT NOT NULL REFERENCES CustomFields(Id) ON DELETE CASCADE,
                Value TEXT NULL, PRIMARY KEY(BotId, TelegramId, FieldId),
                FOREIGN KEY(BotId, TelegramId) REFERENCES Users(BotId, TelegramId) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS WaitingStates (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL, TelegramId INTEGER NOT NULL,
                ChatId INTEGER NOT NULL DEFAULT 0, ThreadId INTEGER NULL, ExpectedEvent TEXT NOT NULL DEFAULT 'message',
                FlowVersionId TEXT NOT NULL DEFAULT '', ResumeNodeId TEXT NOT NULL DEFAULT '', ExpiresAt TEXT NULL,
                FlowName TEXT NOT NULL DEFAULT '', NodeName TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL,
                FOREIGN KEY(BotId, TelegramId) REFERENCES Users(BotId, TelegramId) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS UserEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, BotId TEXT NOT NULL, TelegramId INTEGER NOT NULL,
                Kind TEXT NOT NULL, Description TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                FOREIGN KEY(BotId, TelegramId) REFERENCES Users(BotId, TelegramId) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS ProcessedUpdates (
                BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                UpdateId INTEGER NOT NULL, ProcessedAt TEXT NOT NULL,
                PRIMARY KEY(BotId, UpdateId)
            );
            CREATE TABLE IF NOT EXISTS WebhookUpdates (
                BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                UpdateId INTEGER NOT NULL, Payload TEXT NOT NULL, Attempts INTEGER NOT NULL DEFAULT 0,
                NextAttemptAt TEXT NOT NULL, ReceivedAt TEXT NOT NULL,
                PRIMARY KEY(BotId, UpdateId)
            );
            CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Logs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, BotId TEXT NULL,
                Level TEXT NOT NULL, Message TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                UpdateId INTEGER NULL, ExecutionId TEXT NULL, NodeId TEXT NULL, Method TEXT NULL,
                DurationMs INTEGER NULL, ResponseCode INTEGER NULL, TechnicalMessage TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS Segments (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL, FilterJson TEXT NOT NULL, UNIQUE(BotId, Name)
            );
            CREATE TABLE IF NOT EXISTS Assets (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL, ContentType TEXT NOT NULL, Size INTEGER NOT NULL,
                Hash TEXT NOT NULL, Path TEXT NOT NULL, UNIQUE(BotId, Hash)
            );
            CREATE TABLE IF NOT EXISTS TelegramFiles (
                AssetId TEXT NOT NULL REFERENCES Assets(Id) ON DELETE CASCADE,
                BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE, FileId TEXT NOT NULL,
                PRIMARY KEY(AssetId, BotId)
            );
            CREATE TABLE IF NOT EXISTS Schedules (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                FlowId TEXT NOT NULL, FlowVersionId TEXT NOT NULL, UserId INTEGER NOT NULL,
                ChatId INTEGER NOT NULL, ResumeNodeId TEXT NOT NULL, DueAt TEXT NOT NULL, Status INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Executions (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                FlowId TEXT NOT NULL, FlowVersionId TEXT NOT NULL, UserId INTEGER NOT NULL, ChatId INTEGER NOT NULL,
                OriginUpdateId INTEGER NOT NULL, CurrentNodeId TEXT NOT NULL, Status INTEGER NOT NULL,
                StartedAt TEXT NOT NULL, FinishedAt TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS ExecutionEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, ExecutionId TEXT NOT NULL REFERENCES Executions(Id) ON DELETE CASCADE,
                NodeId TEXT NULL, Kind TEXT NOT NULL, Message TEXT NULL, CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Broadcasts (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL, SegmentId TEXT NULL, Text TEXT NOT NULL, StartsAt TEXT NULL,
                Status INTEGER NOT NULL, Total INTEGER NOT NULL DEFAULT 0, Sent INTEGER NOT NULL DEFAULT 0,
                Failed INTEGER NOT NULL DEFAULT 0, Skipped INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS Outbox (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                ChatId INTEGER NOT NULL, Kind TEXT NOT NULL, Payload TEXT NOT NULL, Status INTEGER NOT NULL,
                DueAt TEXT NOT NULL, Attempts INTEGER NOT NULL DEFAULT 0, DeduplicationKey TEXT NULL UNIQUE,
                LastError TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS DeadLetters (
                Id TEXT PRIMARY KEY, BotId TEXT NOT NULL REFERENCES Bots(Id) ON DELETE CASCADE,
                UserId INTEGER NULL, ChatId INTEGER NULL, FlowId TEXT NULL, FlowVersionId TEXT NULL,
                NodeId TEXT NULL, Event TEXT NOT NULL, Reason TEXT NOT NULL, Attempts INTEGER NOT NULL,
                TechnicalData TEXT NULL, CreatedAt TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "CustomFields", "InternalKey", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "Logs", "UpdateId", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Logs", "ExecutionId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Logs", "NodeId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Logs", "Method", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Logs", "DurationMs", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Logs", "ResponseCode", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Logs", "TechnicalMessage", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "WaitingStates", "ChatId", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "WaitingStates", "ThreadId", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(connection, "WaitingStates", "ExpectedEvent", "TEXT NOT NULL DEFAULT 'message'", cancellationToken);
        await EnsureColumnAsync(connection, "WaitingStates", "FlowVersionId", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "WaitingStates", "ResumeNodeId", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "WaitingStates", "ExpiresAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "Bots", "UpdateMode", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
    }

    public async Task RecoverRuntimeStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "UPDATE Outbox SET Status=$retry WHERE Status=$sending; UPDATE Schedules SET Status=$pending WHERE Status=$running;", cancellationToken,
            ("$retry", (int)OutboxStatus.Retry), ("$sending", (int)OutboxStatus.Sending), ("$pending", (int)ScheduledActionStatus.Pending), ("$running", (int)ScheduledActionStatus.Running));
    }

    public async Task AddBotAsync(Bot bot, string token, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, """
            INSERT INTO Bots(Id, Name, Username, Status, AutoStart, LastEventAt, CreatedAt, UpdateMode)
            VALUES($id, $name, $username, $status, $autoStart, $lastEventAt, $createdAt, $updateMode);
            """, cancellationToken,
            ("$id", bot.Id), ("$name", bot.Name), ("$username", bot.Username),
            ("$status", (int)bot.Status), ("$autoStart", bot.AutoStart),
            ("$lastEventAt", bot.LastEventAt), ("$createdAt", DateTimeOffset.UtcNow), ("$updateMode", (int)bot.UpdateMode));
        await ExecuteAsync(connection, "INSERT INTO BotSecrets(BotId, Token) VALUES($id, $token);", cancellationToken,
            ("$id", bot.Id), ("$token", SecretProtector.Protect(token)));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Bot>> GetBotsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.Id, b.Name, b.Username, b.Status, b.AutoStart, b.LastEventAt, COUNT(u.TelegramId), b.UpdateMode
            FROM Bots b LEFT JOIN Users u ON u.BotId = b.Id GROUP BY b.Id ORDER BY b.Name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Bot>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                (BotStatus)reader.GetInt32(3), reader.GetBoolean(4), reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)), (BotUpdateMode)reader.GetInt32(7)));
        return result;
    }

    public async Task<string> GetBotTokenAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Token FROM BotSecrets WHERE BotId = $id;";
        command.Parameters.AddWithValue("$id", botId.ToString());
        return SecretProtector.Unprotect((byte[])(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Токен бота не найден.")));
    }

    public async Task<string> GetOrCreateWebhookSecretAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Secret FROM BotWebhookSecrets WHERE BotId=$id;";
        command.Parameters.AddWithValue("$id", botId.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken) is byte[] existing) return SecretProtector.Unprotect(existing);

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await ExecuteAsync(connection, "INSERT OR IGNORE INTO BotWebhookSecrets(BotId, Secret) VALUES($id, $secret);", cancellationToken,
            ("$id", botId), ("$secret", SecretProtector.Protect(secret)));
        return SecretProtector.Unprotect((byte[])(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Не удалось создать секрет webhook.")));
    }

    public Task SetBotStatusAsync(Guid botId, BotStatus status, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE Bots SET Status = $status WHERE Id = $id;", cancellationToken, ("$status", (int)status), ("$id", botId));

    public Task TouchBotAsync(Guid botId, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE Bots SET LastEventAt = $at WHERE Id = $id;", cancellationToken, ("$at", at), ("$id", botId));

    public Task SetBotAutoStartAsync(Guid botId, bool value, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE Bots SET AutoStart = $value WHERE Id = $id;", cancellationToken, ("$value", value), ("$id", botId));

    public Task SetBotUpdateModeAsync(Guid botId, BotUpdateMode value, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE Bots SET UpdateMode = $value WHERE Id = $id;", cancellationToken, ("$value", (int)value), ("$id", botId));

    public Task SetUserStatusByChatAsync(Guid botId, long chatId, string status, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE Users SET Status=$status WHERE BotId=$botId AND TelegramId=$chatId; UPDATE Chats SET Status=$status WHERE BotId=$botId AND TelegramId=$chatId;",
            cancellationToken, ("$status", status), ("$botId", botId), ("$chatId", chatId));

    public async Task AddExecutionAsync(Execution execution, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO Executions(Id,BotId,FlowId,FlowVersionId,UserId,ChatId,OriginUpdateId,CurrentNodeId,Status,StartedAt,FinishedAt) VALUES($id,$botId,$flowId,$versionId,$userId,$chatId,$updateId,$nodeId,$status,$startedAt,$finishedAt);", cancellationToken,
            ("$id", execution.Id), ("$botId", execution.BotId), ("$flowId", execution.FlowId), ("$versionId", execution.FlowVersionId),
            ("$userId", execution.UserId), ("$chatId", execution.ChatId), ("$updateId", execution.OriginUpdateId),
            ("$nodeId", execution.CurrentNodeId), ("$status", (int)execution.Status), ("$startedAt", execution.StartedAt), ("$finishedAt", execution.FinishedAt));
    }

    public Task UpdateExecutionAsync(Guid id, ExecutionStatus status, Guid currentNodeId, string? message = null, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE Executions SET Status=$status,CurrentNodeId=$nodeId,FinishedAt=CASE WHEN $finished=1 THEN $now ELSE FinishedAt END WHERE Id=$id; INSERT INTO ExecutionEvents(ExecutionId,NodeId,Kind,Message,CreatedAt) VALUES($id,$nodeId,$kind,$message,$now);",
            cancellationToken, ("$status", (int)status), ("$nodeId", currentNodeId), ("$finished", status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.Cancelled),
            ("$now", DateTimeOffset.UtcNow), ("$id", id), ("$kind", status.ToString()), ("$message", message));

    public async Task UpsertUserAsync(BotUser user, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            INSERT INTO Users(BotId, TelegramId, Username, FirstName, LastName, Language, FirstSeenAt, LastSeenAt, Status)
            VALUES($botId, $telegramId, $username, $firstName, $lastName, $language, $firstSeenAt, $lastSeenAt, $status)
            ON CONFLICT(BotId, TelegramId) DO UPDATE SET
              Username=excluded.Username, FirstName=excluded.FirstName, LastName=excluded.LastName,
              Language=excluded.Language, LastSeenAt=excluded.LastSeenAt, Status=excluded.Status;
            """, cancellationToken,
            ("$botId", user.BotId), ("$telegramId", user.TelegramId), ("$username", user.Username),
            ("$firstName", user.FirstName), ("$lastName", user.LastName), ("$language", user.Language),
            ("$firstSeenAt", user.FirstSeenAt), ("$lastSeenAt", user.LastSeenAt), ("$status", user.Status));
    }

    public Task UpsertChatAsync(Guid botId, long telegramId, string type, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("INSERT INTO Chats(BotId,TelegramId,Type) VALUES($botId,$chatId,$type) ON CONFLICT(BotId,TelegramId) DO UPDATE SET Type=excluded.Type;",
            cancellationToken, ("$botId", botId), ("$chatId", telegramId), ("$type", type));

    public async Task<IReadOnlyList<BotUser>> GetUsersAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TelegramId, Username, FirstName, LastName, Language, FirstSeenAt, LastSeenAt, Status FROM Users WHERE BotId=$botId ORDER BY LastSeenAt DESC;";
        command.Parameters.AddWithValue("$botId", botId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BotUser>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(botId, reader.GetInt64(0), NullableString(reader, 1), NullableString(reader, 2), NullableString(reader, 3), NullableString(reader, 4),
                DateTimeOffset.Parse(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6)), reader.GetString(7)));
        return result;
    }

    public async Task<IReadOnlyList<BotUser>> FindUsersAsync(Guid botId, UserFilter filter, CancellationToken cancellationToken = default)
    {
        var users = await GetUsersAsync(botId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(filter.Search))
            users = users.Where(x => new[] { x.TelegramId.ToString(), x.Username, x.FirstName, x.LastName }
                .Any(value => value?.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        if (!string.IsNullOrWhiteSpace(filter.Language)) users = users.Where(x => x.Language == filter.Language).ToArray();
        if (!string.IsNullOrWhiteSpace(filter.Status)) users = users.Where(x => x.Status == filter.Status).ToArray();
        if (filter.FirstSeenAfter is { } firstAfter) users = users.Where(x => x.FirstSeenAt >= firstAfter).ToArray();
        if (filter.FirstSeenBefore is { } firstBefore) users = users.Where(x => x.FirstSeenAt <= firstBefore).ToArray();
        if (filter.ActiveAfter is { } activeAfter) users = users.Where(x => x.LastSeenAt >= activeAfter).ToArray();
        if (filter.ActiveBefore is { } activeBefore) users = users.Where(x => x.LastSeenAt <= activeBefore).ToArray();
        if (filter.TagIds is { Count: > 0 })
        {
            await using var connection = await OpenAsync(cancellationToken);
            var ids = new HashSet<long>();
            foreach (var tagId in filter.TagIds)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT TelegramId FROM UserTags WHERE BotId=$botId AND TagId=$tagId;";
                command.Parameters.AddWithValue("$botId", botId.ToString());
                command.Parameters.AddWithValue("$tagId", tagId.ToString());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var current = new HashSet<long>();
                while (await reader.ReadAsync(cancellationToken)) current.Add(reader.GetInt64(0));
                if (ids.Count == 0) ids.UnionWith(current); else ids.IntersectWith(current);
            }
            users = users.Where(x => ids.Contains(x.TelegramId)).ToArray();
        }
        if (filter.FieldEquals is { Count: > 0 })
        {
            // ponytail: последовательное чтение подходит локальной базе; заменить одним SQL JOIN при десятках тысяч пользователей.
            var matched = new List<BotUser>();
            foreach (var user in users)
            {
                var fields = (await GetUserCardAsync(botId, user.TelegramId, cancellationToken)).Fields;
                if (filter.FieldEquals.All(pair => fields.TryGetValue(pair.Key, out var value) && value == pair.Value)) matched.Add(user);
            }
            users = matched;
        }
        return users;
    }

    public async Task<Guid> AddTagAsync(Guid botId, long telegramId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await OpenAsync(cancellationToken);
        var id = Guid.NewGuid();
        await ExecuteAsync(connection, "INSERT OR IGNORE INTO Tags(Id, BotId, Name) VALUES($id,$botId,$name);", cancellationToken,
            ("$id", id), ("$botId", botId), ("$name", name.Trim()));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Tags WHERE BotId=$botId AND Name=$name;";
        command.Parameters.AddWithValue("$botId", botId.ToString());
        command.Parameters.AddWithValue("$name", name.Trim());
        id = Guid.Parse((string)(await command.ExecuteScalarAsync(cancellationToken))!);
        await ExecuteAsync(connection, "INSERT OR IGNORE INTO UserTags(BotId,TelegramId,TagId) VALUES($botId,$userId,$tagId);", cancellationToken,
            ("$botId", botId), ("$userId", telegramId), ("$tagId", id));
        return id;
    }

    public Task RemoveTagAsync(Guid botId, long telegramId, Guid tagId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("DELETE FROM UserTags WHERE BotId=$botId AND TelegramId=$userId AND TagId=$tagId;", cancellationToken,
            ("$botId", botId), ("$userId", telegramId), ("$tagId", tagId));

    public async Task<IReadOnlyList<BotTag>> GetTagsAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name FROM Tags WHERE BotId=$botId ORDER BY Name;";
        command.Parameters.AddWithValue("$botId", botId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BotTag>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), botId, reader.GetString(1)));
        return result;
    }

    public async Task<IReadOnlyList<CustomField>> GetCustomFieldsAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name,InternalKey,Type FROM CustomFields WHERE BotId=$botId ORDER BY Name;";
        command.Parameters.AddWithValue("$botId", botId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CustomField>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), botId, reader.GetString(1), reader.GetString(2), (VariableType)reader.GetInt32(3)));
        return result;
    }

    public Task RemoveTagAsync(Guid botId, long telegramId, string name, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("DELETE FROM UserTags WHERE BotId=$botId AND TelegramId=$userId AND TagId IN (SELECT Id FROM Tags WHERE BotId=$botId AND Name=$name);", cancellationToken,
            ("$botId", botId), ("$userId", telegramId), ("$name", name));

    public async Task<bool> HasTagAsync(Guid botId, long telegramId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = UserCommand(connection, "SELECT COUNT(*) FROM UserTags ut JOIN Tags t ON t.Id=ut.TagId WHERE ut.BotId=$botId AND ut.TelegramId=$userId AND t.Name=$name;", botId, telegramId);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    public async Task<CustomField> SetUserFieldAsync(Guid botId, long telegramId, string name, string key, VariableType type, string? value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '_')) throw new ArgumentException("Ключ поля может содержать латинские буквы, цифры и подчёркивание.", nameof(key));
        await using var connection = await OpenAsync(cancellationToken);
        var id = Guid.NewGuid();
        await ExecuteAsync(connection, "INSERT OR IGNORE INTO CustomFields(Id,BotId,Name,InternalKey,Type) VALUES($id,$botId,$name,$key,$type);", cancellationToken,
            ("$id", id), ("$botId", botId), ("$name", name), ("$key", key), ("$type", (int)type));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name,InternalKey,Type FROM CustomFields WHERE BotId=$botId AND InternalKey=$key;";
        command.Parameters.AddWithValue("$botId", botId.ToString()); command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken);
        var field = new CustomField(Guid.Parse(reader.GetString(0)), botId, reader.GetString(1), reader.GetString(2), (VariableType)reader.GetInt32(3));
        await reader.DisposeAsync();
        await ExecuteAsync(connection, "INSERT INTO UserValues(BotId,TelegramId,FieldId,Value) VALUES($botId,$userId,$fieldId,$value) ON CONFLICT(BotId,TelegramId,FieldId) DO UPDATE SET Value=excluded.Value;", cancellationToken,
            ("$botId", botId), ("$userId", telegramId), ("$fieldId", field.Id), ("$value", value));
        return field;
    }

    public async Task SaveSegmentAsync(Segment segment, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO Segments(Id,BotId,Name,FilterJson) VALUES($id,$botId,$name,$json) ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,FilterJson=excluded.FilterJson;", cancellationToken,
            ("$id", segment.Id), ("$botId", segment.BotId), ("$name", segment.Name), ("$json", JsonSerializer.Serialize(segment.Filter)));
    }

    public async Task<IReadOnlyList<Segment>> GetSegmentsAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name,FilterJson FROM Segments WHERE BotId=$botId ORDER BY Name;"; command.Parameters.AddWithValue("$botId", botId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<Segment>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), botId, reader.GetString(1), JsonSerializer.Deserialize<UserFilter>(reader.GetString(2)) ?? new()));
        return result;
    }

    public async Task AddAssetAsync(Asset asset, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO Assets(Id,BotId,Name,ContentType,Size,Hash,Path) VALUES($id,$botId,$name,$type,$size,$hash,$path);", cancellationToken,
            ("$id", asset.Id), ("$botId", asset.BotId), ("$name", asset.Name), ("$type", asset.ContentType), ("$size", asset.Size), ("$hash", asset.Hash), ("$path", asset.Path));
    }

    public Task SetTelegramFileIdAsync(Guid assetId, Guid botId, string fileId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("INSERT INTO TelegramFiles(AssetId,BotId,FileId) VALUES($assetId,$botId,$fileId) ON CONFLICT(AssetId,BotId) DO UPDATE SET FileId=excluded.FileId;", cancellationToken,
            ("$assetId", assetId), ("$botId", botId), ("$fileId", fileId));

    public async Task AddWaitingStateAsync(WaitingState state, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO WaitingStates(Id,BotId,TelegramId,ChatId,ThreadId,ExpectedEvent,FlowVersionId,ResumeNodeId,ExpiresAt,FlowName,NodeName,Status) VALUES($id,$botId,$userId,$chatId,$threadId,$event,$flow,$node,$expires,$flow,$node,$status);", cancellationToken,
            ("$id", state.Id), ("$botId", state.BotId), ("$userId", state.UserId), ("$chatId", state.ChatId), ("$threadId", state.ThreadId),
            ("$event", state.ExpectedEvent), ("$flow", state.FlowVersionId), ("$node", state.ResumeNodeId), ("$expires", state.ExpiresAt), ("$status", state.Status.ToString()));
    }

    public async Task<IReadOnlyList<WaitingState>> GetActiveWaitingStatesAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,TelegramId,ChatId,ThreadId,ExpectedEvent,FlowVersionId,ResumeNodeId,ExpiresAt FROM WaitingStates WHERE BotId=$botId AND Status='Active' AND (ExpiresAt IS NULL OR ExpiresAt>$now);";
        command.Parameters.AddWithValue("$botId", botId.ToString()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<WaitingState>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), botId, reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4), Guid.Parse(reader.GetString(5)), Guid.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))));
        return result;
    }

    public Task CompleteWaitingStateAsync(Guid id, WaitingStateStatus status = WaitingStateStatus.Completed, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE WaitingStates SET Status=$status WHERE Id=$id;", cancellationToken, ("$status", status.ToString()), ("$id", id));

    public async Task AddScheduledActionAsync(ScheduledAction action, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO Schedules(Id,BotId,FlowId,FlowVersionId,UserId,ChatId,ResumeNodeId,DueAt,Status) VALUES($id,$botId,$flowId,$versionId,$userId,$chatId,$nodeId,$dueAt,$status);", cancellationToken,
            ("$id", action.Id), ("$botId", action.BotId), ("$flowId", action.FlowId), ("$versionId", action.FlowVersionId), ("$userId", action.UserId), ("$chatId", action.ChatId), ("$nodeId", action.ResumeNodeId), ("$dueAt", action.DueAt), ("$status", (int)action.Status));
    }

    public async Task<IReadOnlyList<ScheduledAction>> GetDueScheduledActionsAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,BotId,FlowId,FlowVersionId,UserId,ChatId,ResumeNodeId,DueAt,Status FROM Schedules WHERE Status=$status AND DueAt<=$now ORDER BY DueAt LIMIT $limit;";
        command.Parameters.AddWithValue("$status", (int)ScheduledActionStatus.Pending); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<ScheduledAction>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), reader.GetInt64(4), reader.GetInt64(5), Guid.Parse(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7)), (ScheduledActionStatus)reader.GetInt32(8)));
        return result;
    }

    public Task UpdateScheduledActionAsync(Guid id, ScheduledActionStatus status, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE Schedules SET Status=$status WHERE Id=$id;", cancellationToken, ("$status", (int)status), ("$id", id));

    public Task MigrateChatAsync(Guid botId, long oldChatId, long newChatId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE Outbox SET ChatId=$new WHERE BotId=$botId AND ChatId=$old; UPDATE Schedules SET ChatId=$new WHERE BotId=$botId AND ChatId=$old; UPDATE WaitingStates SET ChatId=$new WHERE BotId=$botId AND ChatId=$old;", cancellationToken,
            ("$new", newChatId), ("$botId", botId), ("$old", oldChatId));

    public async Task AddBroadcastAsync(Broadcast broadcast, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO Broadcasts(Id,BotId,Name,SegmentId,Text,StartsAt,Status,Total,Sent,Failed,Skipped) VALUES($id,$botId,$name,$segmentId,$text,$startsAt,$status,$total,$sent,$failed,$skipped);", cancellationToken,
            ("$id", broadcast.Id), ("$botId", broadcast.BotId), ("$name", broadcast.Name), ("$segmentId", broadcast.SegmentId), ("$text", broadcast.Text), ("$startsAt", broadcast.StartsAt), ("$status", (int)broadcast.Status), ("$total", broadcast.Total), ("$sent", broadcast.Sent), ("$failed", broadcast.Failed), ("$skipped", broadcast.Skipped));
    }

    public async Task<IReadOnlyList<Broadcast>> GetBroadcastsAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name,SegmentId,Text,StartsAt,Status,Total,Sent,Failed,Skipped FROM Broadcasts WHERE BotId=$botId ORDER BY COALESCE(StartsAt,'') DESC;"; command.Parameters.AddWithValue("$botId", botId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<Broadcast>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), botId, reader.GetString(1), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)), (BroadcastStatus)reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9)));
        return result;
    }

    public Task RecordBroadcastResultAsync(Guid id, bool sent, CancellationToken cancellationToken = default) =>
        WithConnectionAsync(sent
            ? "UPDATE Broadcasts SET Sent=Sent+1,Status=CASE WHEN Sent+Failed+Skipped+1>=Total THEN $completed ELSE Status END WHERE Id=$id;"
            : "UPDATE Broadcasts SET Failed=Failed+1,Status=CASE WHEN Sent+Failed+Skipped+1>=Total THEN $completed ELSE Status END WHERE Id=$id;",
            cancellationToken, ("$completed", (int)BroadcastStatus.Completed), ("$id", id));

    public async Task<bool> EnqueueOutboxAsync(OutboxAction action, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ExecuteAsync(connection, "INSERT OR IGNORE INTO Outbox(Id,BotId,ChatId,Kind,Payload,Status,DueAt,Attempts,DeduplicationKey) VALUES($id,$botId,$chatId,$kind,$payload,$status,$dueAt,$attempts,$dedup);", cancellationToken,
            ("$id", action.Id), ("$botId", action.BotId), ("$chatId", action.ChatId), ("$kind", action.Kind), ("$payload", action.Payload), ("$status", (int)action.Status), ("$dueAt", action.DueAt), ("$attempts", action.Attempts), ("$dedup", action.DeduplicationKey)) == 1;
    }

    public async Task<IReadOnlyList<OutboxAction>> GetDueOutboxAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,BotId,ChatId,Kind,Payload,Status,DueAt,Attempts,DeduplicationKey FROM Outbox WHERE Status IN ($pending,$scheduled,$retry) AND DueAt<=$now ORDER BY DueAt, rowid LIMIT $limit;";
        command.Parameters.AddWithValue("$pending", (int)OutboxStatus.Pending); command.Parameters.AddWithValue("$scheduled", (int)OutboxStatus.Scheduled); command.Parameters.AddWithValue("$retry", (int)OutboxStatus.Retry); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<OutboxAction>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), (OutboxStatus)reader.GetInt32(5), DateTimeOffset.Parse(reader.GetString(6)), reader.GetInt32(7), NullableString(reader, 8)));
        return result;
    }

    public Task UpdateOutboxAsync(Guid id, OutboxStatus status, DateTimeOffset dueAt, int attempts, string? error = null, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("UPDATE Outbox SET Status=$status,DueAt=$dueAt,Attempts=$attempts,LastError=$error WHERE Id=$id;", cancellationToken,
            ("$status", (int)status), ("$dueAt", dueAt), ("$attempts", attempts), ("$error", error), ("$id", id));

    public async Task AddDeadLetterAsync(DeadLetter item, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO DeadLetters(Id,BotId,UserId,ChatId,FlowId,FlowVersionId,NodeId,Event,Reason,Attempts,TechnicalData,CreatedAt) VALUES($id,$botId,$userId,$chatId,$flowId,$versionId,$nodeId,$event,$reason,$attempts,$data,$at);", cancellationToken,
            ("$id", item.Id), ("$botId", item.BotId), ("$userId", item.UserId), ("$chatId", item.ChatId), ("$flowId", item.FlowId), ("$versionId", item.FlowVersionId), ("$nodeId", item.NodeId), ("$event", item.Event), ("$reason", item.Reason), ("$attempts", item.Attempts), ("$data", item.TechnicalData), ("$at", item.CreatedAt));
    }

    public async Task AddLogAsync(Guid? botId, string level, string message, string? technicalMessage = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO Logs(BotId,Level,Message,CreatedAt,TechnicalMessage) VALUES($botId,$level,$message,$at,$technical);", cancellationToken,
            ("$botId", botId), ("$level", level), ("$message", message), ("$at", DateTimeOffset.UtcNow), ("$technical", technicalMessage));
    }

    public async Task<IReadOnlyList<AppLog>> GetLogsAsync(Guid botId, bool technical, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,BotId,Level,Message,CreatedAt,UpdateId,ExecutionId,NodeId,Method,DurationMs,ResponseCode,TechnicalMessage FROM Logs WHERE BotId=$botId OR BotId IS NULL ORDER BY Id DESC LIMIT 500;"; command.Parameters.AddWithValue("$botId", botId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<AppLog>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)), technical ? NullableString(reader, 8) : null, technical && !reader.IsDBNull(9) ? reader.GetInt32(9) : null, technical && !reader.IsDBNull(10) ? reader.GetInt32(10) : null, technical ? NullableString(reader, 11) : null));
        return result;
    }

    public async Task AddUserEventAsync(Guid botId, long telegramId, string kind, string description, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO UserEvents(BotId, TelegramId, Kind, Description, CreatedAt) VALUES($botId, $userId, $kind, $description, $at);",
            cancellationToken, ("$botId", botId), ("$userId", telegramId), ("$kind", kind), ("$description", description), ("$at", DateTimeOffset.UtcNow));
    }

    public async Task AddUserEventIfMissingAsync(Guid botId, long telegramId, string kind, string description, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            INSERT INTO UserEvents(BotId, TelegramId, Kind, Description, CreatedAt)
            SELECT $botId, $userId, $kind, $description, $at
            WHERE NOT EXISTS (SELECT 1 FROM UserEvents WHERE BotId=$botId AND TelegramId=$userId AND Kind=$kind);
            """, cancellationToken, ("$botId", botId), ("$userId", telegramId), ("$kind", kind), ("$description", description), ("$at", DateTimeOffset.UtcNow));
    }

    public async Task AddUserEventIfKnownAsync(Guid botId, long telegramId, string kind, string description, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            INSERT INTO UserEvents(BotId, TelegramId, Kind, Description, CreatedAt)
            SELECT $botId, $userId, $kind, $description, $at
            WHERE EXISTS (SELECT 1 FROM Users WHERE BotId=$botId AND TelegramId=$userId);
            """, cancellationToken, ("$botId", botId), ("$userId", telegramId), ("$kind", kind), ("$description", description), ("$at", DateTimeOffset.UtcNow));
    }

    public async Task<UserCard> GetUserCardAsync(Guid botId, long telegramId, CancellationToken cancellationToken = default)
    {
        var user = (await GetUsersAsync(botId, cancellationToken)).Single(x => x.TelegramId == telegramId);
        await using var connection = await OpenAsync(cancellationToken);
        var tags = await ReadStringsAsync(connection, "SELECT t.Name FROM UserTags ut JOIN Tags t ON t.Id=ut.TagId WHERE ut.BotId=$botId AND ut.TelegramId=$userId ORDER BY t.Name;", botId, telegramId, cancellationToken);
        var waits = await ReadStringsAsync(connection, "SELECT FlowName || ' — ' || NodeName FROM WaitingStates WHERE BotId=$botId AND TelegramId=$userId AND Status='Active';", botId, telegramId, cancellationToken);
        var fields = new Dictionary<string, string?>();
        await using (var command = UserCommand(connection, "SELECT f.Name, v.Value FROM UserValues v JOIN CustomFields f ON f.Id=v.FieldId WHERE v.BotId=$botId AND v.TelegramId=$userId;", botId, telegramId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) fields[reader.GetString(0)] = NullableString(reader, 1);
        var history = new List<UserEvent>();
        await using (var command = UserCommand(connection, "SELECT CreatedAt, Kind, Description FROM UserEvents WHERE BotId=$botId AND TelegramId=$userId ORDER BY CreatedAt DESC LIMIT 200;", botId, telegramId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) history.Add(new(DateTimeOffset.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        return new(user, tags, fields, waits, waits.FirstOrDefault()?.Split(" — ")[0], history);
    }

    public async Task SetVariableAsync<T>(VariableScope scope, string ownerId, string name, VariableType type, T value, bool isSecret = false, CancellationToken cancellationToken = default)
    {
        if (scope is VariableScope.System or VariableScope.Event) throw new ArgumentException("Эта область не сохраняется.", nameof(scope));
        var secret = isSecret || scope == VariableScope.Secret;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (secret) bytes = SecretProtector.Protect(System.Text.Encoding.UTF8.GetString(bytes));
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            INSERT INTO Variables(Scope, OwnerId, Name, Type, IsSecret, Value) VALUES($scope, $owner, $name, $type, $secret, $value)
            ON CONFLICT(Scope, OwnerId, Name) DO UPDATE SET Type=excluded.Type, IsSecret=excluded.IsSecret, Value=excluded.Value;
            """, cancellationToken, ("$scope", (int)scope), ("$owner", ownerId), ("$name", name), ("$type", (int)type), ("$secret", secret), ("$value", bytes));
    }

    public async Task<T?> GetVariableAsync<T>(VariableScope scope, string ownerId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsSecret, Value FROM Variables WHERE Scope=$scope AND OwnerId=$owner AND Name=$name;";
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return default;
        var bytes = (byte[])reader[1];
        if (reader.GetBoolean(0)) bytes = System.Text.Encoding.UTF8.GetBytes(SecretProtector.Unprotect(bytes));
        return JsonSerializer.Deserialize<T>(bytes);
    }

    public async Task<bool> TryMarkUpdateProcessedAsync(Guid botId, long updateId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ExecuteAsync(connection, "INSERT OR IGNORE INTO ProcessedUpdates(BotId, UpdateId, ProcessedAt) VALUES($botId, $updateId, $at);", cancellationToken,
            ("$botId", botId), ("$updateId", updateId), ("$at", DateTimeOffset.UtcNow)) == 1;
    }

    public Task ForgetProcessedUpdateAsync(Guid botId, long updateId, CancellationToken cancellationToken = default) =>
        WithConnectionAsync("DELETE FROM ProcessedUpdates WHERE BotId=$botId AND UpdateId=$updateId;", cancellationToken,
            ("$botId", botId), ("$updateId", updateId));

    public async Task<bool> EnqueueWebhookUpdateAsync(Guid botId, long updateId, string payload, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ExecuteAsync(connection, """
            INSERT OR IGNORE INTO WebhookUpdates(BotId, UpdateId, Payload, NextAttemptAt, ReceivedAt)
            SELECT $botId, $updateId, $payload, $at, $at
            WHERE NOT EXISTS (SELECT 1 FROM ProcessedUpdates WHERE BotId=$botId AND UpdateId=$updateId);
            """, cancellationToken, ("$botId", botId), ("$updateId", updateId), ("$payload", payload), ("$at", DateTimeOffset.UtcNow)) == 1;
    }

    public async Task<IReadOnlyList<PendingWebhookUpdate>> GetPendingWebhookUpdatesAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT BotId, UpdateId, Payload, Attempts FROM WebhookUpdates WHERE NextAttemptAt <= $now ORDER BY ReceivedAt LIMIT $limit;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PendingWebhookUpdate>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3)));
        return result;
    }

    public async Task CompleteWebhookUpdateAsync(Guid botId, long updateId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var processed = connection.CreateCommand();
        processed.Transaction = (SqliteTransaction)transaction;
        processed.CommandText = "INSERT OR IGNORE INTO ProcessedUpdates(BotId, UpdateId, ProcessedAt) VALUES($botId, $updateId, $at);";
        processed.Parameters.AddWithValue("$botId", botId.ToString());
        processed.Parameters.AddWithValue("$updateId", updateId);
        processed.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await processed.ExecuteNonQueryAsync(cancellationToken);
        await using var remove = connection.CreateCommand();
        remove.Transaction = (SqliteTransaction)transaction;
        remove.CommandText = "DELETE FROM WebhookUpdates WHERE BotId=$botId AND UpdateId=$updateId;";
        remove.Parameters.AddWithValue("$botId", botId.ToString());
        remove.Parameters.AddWithValue("$updateId", updateId);
        await remove.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task RetryWebhookUpdateAsync(Guid botId, long updateId, int attempts, CancellationToken cancellationToken = default)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 8))));
        return WithConnectionAsync("UPDATE WebhookUpdates SET Attempts=$attempts, NextAttemptAt=$next WHERE BotId=$botId AND UpdateId=$updateId;",
            cancellationToken, ("$attempts", attempts), ("$next", DateTimeOffset.UtcNow + delay), ("$botId", botId), ("$updateId", updateId));
    }

    public async Task SaveDraftAsync(Guid botId, FlowDefinition definition, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, """
            INSERT INTO Flows(Id, BotId, Name, DraftJson) VALUES($id, $botId, $name, $json)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, DraftJson=excluded.DraftJson;
            """, cancellationToken, ("$id", definition.Id), ("$botId", botId), ("$name", definition.Name), ("$json", JsonSerializer.Serialize(definition)));
    }

    public async Task<IReadOnlyList<FlowDefinition>> GetDraftsAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DraftJson FROM Flows WHERE BotId=$botId ORDER BY Name;";
        command.Parameters.AddWithValue("$botId", botId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FlowDefinition>();
        while (await reader.ReadAsync(cancellationToken))
            if (JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0)) is { } definition) result.Add(definition);
        return result;
    }

    public async Task<FlowVersion> PublishAsync(Guid botId, FlowDefinition definition, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, """
            INSERT INTO Flows(Id, BotId, Name, DraftJson) VALUES($id, $botId, $name, $json)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, DraftJson=excluded.DraftJson;
            """, cancellationToken, ("$id", definition.Id), ("$botId", botId), ("$name", definition.Name), ("$json", JsonSerializer.Serialize(definition)));
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT COALESCE(MAX(Version), 0) + 1 FROM FlowVersions WHERE FlowId=$id;";
        versionCommand.Parameters.AddWithValue("$id", definition.Id.ToString());
        var versionNumber = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
        var version = new FlowVersion(Guid.NewGuid(), definition.Id, versionNumber, FlowVersionStatus.Published, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, definition);
        await ExecuteAsync(connection, """
            INSERT INTO FlowVersions(Id, FlowId, Version, Status, CreatedAt, PublishedAt, DefinitionJson)
            VALUES($id, $flowId, $version, $status, $createdAt, $publishedAt, $json);
            """, cancellationToken, ("$id", version.Id), ("$flowId", version.FlowId), ("$version", version.Version),
            ("$status", (int)version.Status), ("$createdAt", version.CreatedAt), ("$publishedAt", version.PublishedAt), ("$json", JsonSerializer.Serialize(definition)));
        await transaction.CommitAsync(cancellationToken);
        return version;
    }

    public async Task<IReadOnlyList<FlowVersion>> GetPublishedFlowsAsync(Guid botId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.Id, v.FlowId, v.Version, v.Status, v.CreatedAt, v.PublishedAt, v.DefinitionJson
            FROM FlowVersions v
            JOIN Flows f ON f.Id = v.FlowId
            WHERE f.BotId = $botId AND v.Status = $status
              AND v.Version = (SELECT MAX(v2.Version) FROM FlowVersions v2 WHERE v2.FlowId = v.FlowId)
            ORDER BY f.Name;
            """;
        command.Parameters.AddWithValue("$botId", botId.ToString());
        command.Parameters.AddWithValue("$status", (int)FlowVersionStatus.Published);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FlowVersion>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var definition = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(6))
                ?? throw new InvalidDataException("Не удалось прочитать опубликованный сценарий.");
            result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2),
                (FlowVersionStatus)reader.GetInt32(3), DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)), definition));
        }
        return result;
    }

    public async Task<FlowVersion?> GetFlowVersionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,FlowId,Version,Status,CreatedAt,PublishedAt,DefinitionJson FROM FlowVersions WHERE Id=$id;"; command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) return null;
        var definition = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(6)) ?? throw new InvalidDataException("Не удалось прочитать сценарий.");
        return new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2), (FlowVersionStatus)reader.GetInt32(3), DateTimeOffset.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)), definition);
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "INSERT INTO Settings(Key, Value) VALUES($key, $value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;", cancellationToken,
            ("$key", key), ("$value", value));
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task WithConnectionAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] values)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, sql, cancellationToken, values);
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, DbValue(value));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand(); check.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync();
        await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", cancellationToken);
    }

    private static object DbValue(object? value) => value switch
    {
        null => DBNull.Value,
        Guid id => id.ToString(),
        DateTimeOffset date => date.ToString("O"),
        bool boolean => boolean ? 1 : 0,
        _ => value
    };

    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static SqliteCommand UserCommand(SqliteConnection connection, string sql, Guid botId, long telegramId)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$botId", botId.ToString());
        command.Parameters.AddWithValue("$userId", telegramId);
        return command;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql, Guid botId, long telegramId, CancellationToken cancellationToken)
    {
        await using var command = UserCommand(connection, sql, botId, telegramId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }
}
